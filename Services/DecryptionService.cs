using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DecryptStation3.Models;

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

                var sec0sec1 = new byte[4096];
                ReadFully(inFile, sec0sec1, 0, 4096);

                var regions = (CharArrBEToUInt(sec0sec1) * 2) - 1;
                var inBuffer = new byte[BufferSize];

                await Task.Run(() =>
                {
                    var first = true;
                    var plain = true;

                    for (uint i = 0; i < regions; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Read region end sector from correct offset: 4 + (i*8) + 4 = 8 + (i*8)
                        // Each region entry is 8 bytes (4 bytes start + 4 bytes end)
                        var regionLastSector = CharArrBEToUInt(sec0sec1, 8 + ((int)i * 8));
                        regionLastSector -= plain ? 0u : 1u;

                        uint numSectors = regionLastSector - _globalLBA + 1;
                        uint numFullBlocks = numSectors / BufferSizeSec;
                        uint partialBlockSize = numSectors % BufferSizeSec;
                        uint numBlocks = numFullBlocks + (partialBlockSize == 0 ? 0u : 1u);

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
                                        Array.Copy(sec0sec1, 0, inBuffer, 0, 4096);
                                        ReadFully(inFile, inBuffer, 4096, bytesToRead - 4096);
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

                        plain = !plain;
                        ProgressChanged?.Invoke(this, (double)(i + 1) / regions);
                    }

                    outFile.Flush();
                }, cancellationToken);
            }
            catch (Exception ex)
            {
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
            var totalRead = 0;
            while (totalRead < count)
            {
                var read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0) throw new IOException("Failed to read from file");
                totalRead += read;
            }
        }
    }
}