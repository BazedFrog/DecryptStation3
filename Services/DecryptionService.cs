using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DecryptStation3.Models;

namespace DecryptStation3.Services
{
    public class DecryptionService
    {
        private const int SectorSize = 2048;
        private const int BufferSizeSec = 4096;              // sectors per chunk
        private const int BufferSize = BufferSizeSec * SectorSize;

        private readonly int _threadCount = Math.Max(1, Environment.ProcessorCount - 1);

        public event EventHandler<double>? ProgressChanged;

        private sealed class Region
        {
            public ulong Start;
            public ulong End;
        }

        public async Task DecryptFileAsync(IsoFile isoFile, string hexKey, CancellationToken cancellationToken = default)
        {
            var outputPath = isoFile.FilePath + ".dec";

            // Decrypt into a temporary file and only move it into place once every sector
            // has been written. The output is pre-sized to the full image, so a failed or
            // interrupted run would otherwise leave a .dec that looks complete but isn't.
            var tempPath = outputPath + ".part";

            try
            {
                var key = ParseKey(hexKey);

                await DecryptToFileAsync(isoFile.FilePath, tempPath, key, cancellationToken);

                File.Move(tempPath, outputPath, overwrite: true);
                Debug.WriteLine("[DECRYPT] Decryption finished.");
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(tempPath);
                Debug.WriteLine("[DECRYPT] Decryption cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                TryDeleteFile(tempPath);
                Debug.WriteLine($"[DECRYPT] ERROR: {ex.Message}");
                Debug.WriteLine($"[DECRYPT] Stack trace: {ex.StackTrace}");
                throw new Exception($"Decryption failed: {ex.Message}", ex);
            }
        }

        private async Task DecryptToFileAsync(string inputPath, string outputPath, byte[] key, CancellationToken cancellationToken)
        {
            await using var inFile = new FileStream(
                inputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            await using var outFile = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None);

            long fileSize = inFile.Length;
            if (fileSize % SectorSize != 0)
                throw new Exception($"ERROR: File size {fileSize} is not a multiple of sector size ({SectorSize}).");

            long totalSectors = fileSize / SectorSize;

            Debug.WriteLine($"[DECRYPT] File: {Path.GetFileName(inputPath)}");
            Debug.WriteLine($"[DECRYPT] File size: {fileSize:N0} bytes ({totalSectors} sectors)");

            // --- Sector 0 holds the region table ---
            var header = new byte[SectorSize];
            ReadFully(inFile, header, 0, header.Length);

            var regions = ExtractRegions(header);
            Debug.WriteLine($"[DECRYPT] Encrypted regions: {regions.Count}");
            foreach (var region in regions)
                Debug.WriteLine($"[DECRYPT]   sectors {region.Start}-{region.End - 1}");

            // For actual processing, start again from the beginning,
            // just like Rust opens a second handle at offset 0.
            inFile.Seek(0, SeekOrigin.Begin);
            outFile.SetLength(fileSize);

            await Task.Run(() =>
            {
                var buffer = new byte[BufferSize];
                long currentSector = 0;

                while (currentSector < totalSectors)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int sectorsThisChunk = (int)Math.Min((long)BufferSizeSec, totalSectors - currentSector);
                    int bytesToRead = sectorsThisChunk * SectorSize;

                    ReadFully(inFile, buffer, 0, bytesToRead);

                    ProcessChunk(buffer, sectorsThisChunk, key, regions, currentSector);

                    outFile.Write(buffer, 0, bytesToRead);

                    currentSector += sectorsThisChunk;
                    ProgressChanged?.Invoke(this, (double)currentSector / totalSectors);
                }

                outFile.Flush();
            }, cancellationToken);
        }

        // --- Core logic mirrored from Rust ---

        private static byte[] ParseKey(string hexKey)
        {
            var potKey = hexKey.Trim();

            // Allow optional "0x" prefix (32 hex chars or "0x" + 32).
            if (potKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                potKey = potKey.Substring(2);

            if (potKey.Length != 32)
                throw new Exception("ERROR: Key must be 32 hex characters in length");

            var key = new byte[16];
            for (int i = 0; i < potKey.Length; i += 2)
            {
                key[i / 2] = Convert.ToByte(potKey.Substring(i, 2), 16);
            }

            return key;
        }

        /// <summary>
        /// Reads the region table from sector 0 and returns the encrypted regions as
        /// [Start, End) sector ranges. The table (big-endian u32 values) is:
        ///   0x00  number of unencrypted regions (n)
        ///   0x04  unused (0)
        ///   0x08  n pairs of (first sector, last sector) of each unencrypted region
        /// The last sector is inclusive, so the encrypted regions are the gaps
        /// between consecutive unencrypted regions.
        /// </summary>
        private static List<Region> ExtractRegions(byte[] header)
        {
            uint numPlainRegions = CharArrBEToUInt(header, 0);
            if (numPlainRegions == 0 || numPlainRegions > (SectorSize - 8) / 8)
                throw new Exception($"ERROR: Invalid region table ({numPlainRegions} unencrypted regions).");

            var regions = new List<Region>((int)numPlainRegions - 1);
            ulong previousEnd = 0;

            for (int i = 0; i < numPlainRegions; i++)
            {
                int regionOffset = 8 + (i * 8);

                ulong start = CharArrBEToUInt(header, regionOffset);
                ulong end = (ulong)CharArrBEToUInt(header, regionOffset + 4) + 1;

                if (start >= end || (i > 0 && start < previousEnd))
                    throw new Exception("ERROR: Invalid region table (regions are reversed or overlapping).");

                if (i > 0 && start > previousEnd)
                {
                    regions.Add(new Region
                    {
                        Start = previousEnd,
                        End = start
                    });
                }

                previousEnd = end;
            }

            return regions;
        }

        /// <summary>
        /// Processes a buffer containing 'sectorCount' sectors starting at 'baseSector',
        /// decrypting every sector that lies inside an encrypted region.
        /// </summary>
        private void ProcessChunk(
            byte[] data,
            int sectorCount,
            byte[] key,
            List<Region> regions,
            long baseSector)
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = _threadCount
            };

            Parallel.For(0, sectorCount, options, k =>
            {
                long sectorIndex = baseSector + k;
                int offset = k * SectorSize;

                if (!IsSectorEncrypted(regions, sectorIndex))
                    return;

                // AES-128 CBC, IV derived from sector index (last 4 bytes big-endian),
                // exactly like Rust's generate_iv + manual CBC.
                var iv = new byte[16];
                ResetIV(iv, (uint)sectorIndex);

                using var aes = Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;

                using var transform = aes.CreateDecryptor();
                transform.TransformBlock(data, offset, SectorSize, data, offset);
            });
        }

        /// <summary>
        /// A sector is encrypted when it lies inside any Region [start, end).
        /// Like PS3Dec (and the console itself), every sector in an encrypted region is
        /// decrypted, including all-zero ones, so the output is byte-identical to PS3Dec
        /// and re-encrypting it reproduces the original image exactly.
        /// </summary>
        private static bool IsSectorEncrypted(List<Region> regions, long sectorIndex)
        {
            foreach (var region in regions)
            {
                if (sectorIndex >= (long)region.Start && sectorIndex < (long)region.End)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Equivalent to Rust's generate_iv(sector): only last 4 bytes hold the sector index (big-endian).
        /// </summary>
        private static void ResetIV(byte[] iv, uint sectorNumber)
        {
            Array.Clear(iv, 0, iv.Length);
            iv[12] = (byte)((sectorNumber & 0xFF000000) >> 24);
            iv[13] = (byte)((sectorNumber & 0x00FF0000) >> 16);
            iv[14] = (byte)((sectorNumber & 0x0000FF00) >> 8);
            iv[15] = (byte)(sectorNumber & 0x000000FF);
        }

        private static uint CharArrBEToUInt(byte[] arr, int offset = 0)
        {
            return (uint)(arr[offset + 3]
                        + (arr[offset + 2] << 8)
                        + (arr[offset + 1] << 16)
                        + (arr[offset] << 24));
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DECRYPT] Could not delete {path}: {ex.Message}");
            }
        }

        private static void ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            if (count == 0) return;

            if (count < 0)
                throw new IOException($"Invalid read count: {count} bytes (offset={offset})");

            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                {
                    long pos = stream.Position;
                    long len = stream.Length;
                    throw new IOException(
                        $"Failed to read from file: requested {count} bytes at offset {offset}, " +
                        $"but only read {totalRead}/{count} bytes. " +
                        $"Stream position: {pos}, Stream length: {len}, " +
                        $"Trying to read {count - totalRead} more bytes");
                }

                totalRead += read;
            }
        }
    }
}
