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
            try
            {
                var key = ParseKey(hexKey);

                await using var inFile = new FileStream(
                    isoFile.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

                await using var outFile = new FileStream(
                    isoFile.FilePath + ".dec",
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None);

                long fileSize = inFile.Length;
                if (fileSize % SectorSize != 0)
                    throw new Exception($"ERROR: File size {fileSize} is not a multiple of sector size ({SectorSize}).");

                long totalSectors = fileSize / SectorSize;

                Debug.WriteLine($"[DECRYPT] File: {Path.GetFileName(isoFile.FilePath)}");
                Debug.WriteLine($"[DECRYPT] File size: {fileSize:N0} bytes ({totalSectors} sectors)");

                // --- Read header once to build the regions (like Rust's extract_regions) ---
                var header = new byte[4096];
                ReadFully(inFile, header, 0, header.Length);

                var regions = ExtractRegions(header);
                Debug.WriteLine($"[DECRYPT] Regions (from header): {regions.Count}");

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
                    Debug.WriteLine("[DECRYPT] Decryption finished.");
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[DECRYPT] Decryption cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DECRYPT] ERROR: {ex.Message}");
                Debug.WriteLine($"[DECRYPT] Stack trace: {ex.StackTrace}");
                throw new Exception($"Decryption failed: {ex.Message}", ex);
            }
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
        /// Equivalent to Rust's extract_regions(): reads num_normal_regions and builds
        /// (num_normal_regions * 2) - 1 regions from the 4096-byte header.
        /// </summary>
        private static List<Region> ExtractRegions(byte[] header)
        {
            uint numNormalRegions = CharArrBEToUInt(header, 0);
            int regionsCount = (int)(numNormalRegions * 2 - 1);

            var regions = new List<Region>(regionsCount);

            for (int i = 0; i < regionsCount; i++)
            {
                int regionOffset = 4 + (i * 8);

                uint start = CharArrBEToUInt(header, regionOffset);
                uint end = CharArrBEToUInt(header, regionOffset + 4);

                regions.Add(new Region
                {
                    Start = start,
                    End = end
                });
            }

            return regions;
        }

        /// <summary>
        /// Processes a buffer containing 'sectorCount' sectors starting at 'baseSector'.
        /// For each sector, mirrors Rust's:
        ///   if is_encrypted(regions, sector_index, sector_data) { decrypt_sector(...) }
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

                if (!IsSectorEncrypted(regions, sectorIndex, data, offset))
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
        /// Mirrors Rust's is_encrypted():
        ///   - If sector is all zeros -> not encrypted
        ///   - If sectorIndex is inside any Region [start, end) -> encrypted
        /// </summary>
        private static bool IsSectorEncrypted(
            List<Region> regions,
            long sectorIndex,
            byte[] data,
            int offset)
        {
            // 1) Skip all-zero sectors (fast path).
            bool allZero = true;
            int end = offset + SectorSize;
            for (int i = offset; i < end; i++)
            {
                if (data[i] != 0)
                {
                    allZero = false;
                    break;
                }
            }

            if (allZero)
                return false;

            // 2) Check if the sector lies inside any [start, end) region
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
