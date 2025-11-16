using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DecryptStation3.Models;
using System.Diagnostics;

namespace DecryptStation3.Services
{
    public class DecryptionService
    {
        private const int BufferSizeSec = 4096;
        private const int BufferSize = BufferSizeSec * 2048;
        private uint _globalLBA = 0;
        private readonly int _threadCount = Math.Max(1, Environment.ProcessorCount - 1);

        public event EventHandler<double>? ProgressChanged;

        public async Task DecryptFileAsync(IsoFile isoFile, string hexKey, CancellationToken cancellationToken = default)
        {
            try
            {
                _globalLBA = 0;

                var key = new byte[16];
                var potKey = hexKey;
                if (potKey.Length == 34)
                    potKey = potKey.Substring(2);
                if (potKey.Length != 32)
                    throw new Exception("ERROR: Key must be 32 hex characters in length");

                potKey = potKey.ToUpper();
                for (int i = 0; i < potKey.Length; i += 2)
                {
                    key[i / 2] = Convert.ToByte(potKey.Substring(i, 2), 16);
                }

                await using var inFile = new FileStream(isoFile.FilePath, FileMode.Open, FileAccess.Read);
                await using var outFile = new FileStream(isoFile.FilePath + ".dec", FileMode.Create);

                var fileSize = inFile.Length;
                var totalSectors = fileSize / 2048;
                Debug.WriteLine($"[DECRYPT] File: {Path.GetFileName(isoFile.FilePath)}");
                Debug.WriteLine($"[DECRYPT] File size: {fileSize:N0} bytes ({totalSectors} sectors)");

                var sec0sec1 = new byte[4096];
                ReadFully(inFile, sec0sec1, 0, 4096);

                var numNormalRegions = CharArrBEToUInt(sec0sec1);
                var regions = (numNormalRegions * 2) - 1;
                Debug.WriteLine($"[DECRYPT] Normal regions: {numNormalRegions}, Total regions: {regions}");

                var inBuffer = new byte[BufferSize];

                await Task.Run(() =>
                {
                    var first = true;
                    var plain = true;
                    uint totalSectorsProcessed = 0;

                    for (uint i = 0; i < regions; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Read region end sector from correct offset: 4 + (i*8) + 4 = 8 + (i*8)
                        // Each region entry is 8 bytes (4 bytes start + 4 bytes end)
                        // The end sector is EXCLUSIVE (not included in the region)
                        var regionEndSector = CharArrBEToUInt(sec0sec1, 8 + ((int)i * 8));

                        // Calculate sector count: end is exclusive, so no +1 needed
                        uint numSectors = regionEndSector - _globalLBA;
                        uint numFullBlocks = numSectors / BufferSizeSec;
                        uint partialBlockSize = numSectors % BufferSizeSec;
                        uint numBlocks = numFullBlocks + (partialBlockSize == 0 ? 0u : 1u);

                        Debug.WriteLine($"[DECRYPT] Region {i}: {(plain ? "PLAIN" : "ENCRYPTED")}, " +
                            $"Start={_globalLBA}, End={regionEndSector}, Sectors={numSectors}, " +
                            $"Blocks={numBlocks} (full={numFullBlocks}, partial={partialBlockSize})");

                        if (plain)
                        {
                            for (uint currBlock = 0; currBlock < numBlocks; currBlock++)
                            {
                                uint currBlockSize = (currBlock == numFullBlocks) ? partialBlockSize : BufferSizeSec;
                                int bytesToRead = (int)(currBlockSize * 2048);

                                if (first)
                                {
                                    if (currBlock == 0)
                                    {
                                        // Header (first 4096 bytes / 2 sectors) was already read
                                        int headerSize = Math.Min(4096, bytesToRead);
                                        Array.Copy(sec0sec1, 0, inBuffer, 0, headerSize);

                                        int remainingBytes = bytesToRead - headerSize;
                                        if (remainingBytes > 0)
                                        {
                                            ReadFully(inFile, inBuffer, headerSize, remainingBytes);
                                        }
                                        Debug.WriteLine($"[DECRYPT] First block: copied {headerSize} bytes from header, read {remainingBytes} bytes from file");
                                    }
                                    else
                                    {
                                        ReadFully(inFile, inBuffer, 0, bytesToRead);
                                    }
                                    first = false;
                                }
                                else
                                {
                                    ReadFully(inFile, inBuffer, 0, bytesToRead);
                                }

                                outFile.Write(inBuffer, 0, bytesToRead);
                                _globalLBA += currBlockSize;
                            }
                        }
                        else
                        {
                            for (uint currBlock = 0; currBlock < numBlocks; currBlock++)
                            {
                                uint currBlockSize = (currBlock == numFullBlocks) ? partialBlockSize : BufferSizeSec;
                                int bytesToRead = (int)(currBlockSize * 2048);
                                ReadFully(inFile, inBuffer, 0, bytesToRead);
                                ProcessData(inBuffer, (int)currBlockSize, key);
                                outFile.Write(inBuffer, 0, bytesToRead);
                            }
                        }

                        totalSectorsProcessed += numSectors;
                        Debug.WriteLine($"[DECRYPT] Region {i} complete. Total sectors processed: {totalSectorsProcessed}, LBA now: {_globalLBA}");

                        plain = !plain;
                        ProgressChanged?.Invoke(this, (double)(i + 1) / regions);
                    }

                    Debug.WriteLine($"[DECRYPT] All regions processed. Total sectors: {totalSectorsProcessed}, Expected: {totalSectors}");
                    if (totalSectorsProcessed != totalSectors)
                    {
                        Debug.WriteLine($"[DECRYPT] WARNING: Sector count mismatch! Processed {totalSectorsProcessed} but file has {totalSectors} sectors");
                    }

                    outFile.Flush();
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DECRYPT] ERROR: {ex.Message}");
                Debug.WriteLine($"[DECRYPT] Stack trace: {ex.StackTrace}");
                throw new Exception($"Decryption failed: {ex.Message}", ex);
            }
        }

        private void ProcessData(byte[] data, int sectorCount, byte[] key)
        {
            const int sectorSize = 2048;

            Parallel.For(0, sectorCount, new ParallelOptions { MaxDegreeOfParallelism = _threadCount }, k =>
            {
                var iv = new byte[16];
                ResetIV(iv, _globalLBA + (uint)k);

                using var aes = Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;

                using var transform = aes.CreateDecryptor();
                int offset = k * sectorSize;
                transform.TransformBlock(data, offset, sectorSize, data, offset);
            });

            _globalLBA += (uint)sectorCount;
        }

        private static void ResetIV(byte[] iv, uint sectorNumber)
        {
            Array.Clear(iv, 0, 12);
            iv[12] = (byte)((sectorNumber & 0xFF000000) >> 24);
            iv[13] = (byte)((sectorNumber & 0x00FF0000) >> 16);
            iv[14] = (byte)((sectorNumber & 0x0000FF00) >> 8);
            iv[15] = (byte)(sectorNumber & 0x000000FF);
        }

        private static uint CharArrBEToUInt(byte[] arr, int offset = 0)
        {
            return (uint)(arr[offset + 3] + (arr[offset + 2] << 8) +
                         (arr[offset + 1] << 16) + (arr[offset] << 24));
        }

        private static void ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            if (count == 0) return; // Nothing to read

            if (count < 0)
            {
                throw new IOException($"Invalid read count: {count} bytes (offset={offset})");
            }

            var totalRead = 0;
            while (totalRead < count)
            {
                var read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                {
                    var pos = stream.Position;
                    var len = stream.Length;
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