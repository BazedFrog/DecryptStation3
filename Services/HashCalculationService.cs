using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DecryptStation3.Services;

public class HashCalculationService
{
    private const int ChunkSize = 64 * 1024 * 1024; // 64MB chunks
    private const int FileBufferSize = 4096;

    public event EventHandler<double>? ProgressChanged;

    public async Task<string> CalculateHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous
            );

            using var sha1 = SHA1.Create();
            var totalBytes = fileStream.Length;
            long bytesProcessed = 0;

            while (bytesProcessed < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bytesToRead = (int)Math.Min(ChunkSize, totalBytes - bytesProcessed);
                var bytesRead = await fileStream.ReadAsync(
                    new Memory<byte>(buffer, 0, bytesToRead),
                    cancellationToken
                ).ConfigureAwait(false);

                if (bytesRead == 0) break;

                sha1.TransformBlock(buffer, 0, bytesRead, null, 0);
                bytesProcessed += bytesRead;

                OnProgressChanged(bytesProcessed, totalBytes);
            }

            sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return FormatHash(sha1.Hash!);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnProgressChanged(long bytesProcessed, long totalBytes)
    {
        var handler = ProgressChanged;
        if (handler != null)
        {
            var progress = (double)bytesProcessed / totalBytes;
            handler(this, progress);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FormatHash(byte[] hash)
    {
        return string.Create(hash.Length * 2, hash, (chars, bytes) =>
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                chars[i * 2] = GetHexChar(b >> 4);
                chars[i * 2 + 1] = GetHexChar(b & 0xF);
            }
        }).ToLowerInvariant();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char GetHexChar(int value) => (char)(value < 10 ? '0' + value : 'A' + (value - 10));
}