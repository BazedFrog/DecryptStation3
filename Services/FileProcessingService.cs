using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils.Iso9660;
using DecryptStation3.Models;

namespace DecryptStation3.Services;

public class FileProcessingService
{
    private readonly DecryptionService _decryptionService;
    private readonly GameInfoService _gameInfoService;
    private readonly HashCalculationService _hashCalculationService;

    public FileProcessingService(
        HashCalculationService hashCalculationService,
        DecryptionService decryptionService,
        GameInfoService gameInfoService)
    {
        _hashCalculationService = hashCalculationService;
        _decryptionService = decryptionService;
        _gameInfoService = gameInfoService;
    }

    public async Task ProcessFileAsync(IsoFile isoFile, CancellationToken cancellationToken = default)
    {
        var progressHandler = new EventHandler<double>((_, p) =>
            isoFile.UpdateOnUIThread(file => file.Progress = p * 100));

        try
        {
            await CalculateHashAsync(isoFile, progressHandler, cancellationToken);

            if (!await FindGameInfoAsync(isoFile)) return;

            await DecryptFileAsync(isoFile, progressHandler, cancellationToken);

            await ExtractContentsAsync(isoFile, cancellationToken);

            isoFile.UpdateOnUIThread(file =>
            {
                file.Status = ProcessingStatus.Completed;
                file.StatusMessage = "Processing complete";
                file.Progress = 100;
            });
        }
        catch (OperationCanceledException)
        {
            UpdateFileError(isoFile, "Operation cancelled");
            throw;
        }
        catch (Exception ex)
        {
            UpdateFileError(isoFile, $"Error: {ex.Message}");
            throw;
        }
        finally
        {
            _hashCalculationService.ProgressChanged -= progressHandler;
            _decryptionService.ProgressChanged -= progressHandler;
        }
    }

    private async Task CalculateHashAsync(IsoFile isoFile, EventHandler<double> progressHandler,
        CancellationToken cancellationToken)
    {
        isoFile.UpdateOnUIThread(file =>
        {
            file.Status = ProcessingStatus.CalculatingHash;
            file.Progress = 0;
        });

        _hashCalculationService.ProgressChanged += progressHandler;
        var hash = await _hashCalculationService.CalculateHashAsync(isoFile.FilePath, cancellationToken);
        _hashCalculationService.ProgressChanged -= progressHandler;

        isoFile.UpdateOnUIThread(file =>
        {
            file.Hash = hash;
            file.Status = ProcessingStatus.HashCalculated;
        });
    }

    private async Task<bool> FindGameInfoAsync(IsoFile isoFile)
    {
        var gameInfo = _gameInfoService.FindGameByHash(isoFile.Hash);
        isoFile.UpdateOnUIThread(file => file.GameInfo = gameInfo);

        if (gameInfo != null) return true;

        UpdateFileError(isoFile, "No matching game found");
        return false;
    }

    private async Task DecryptFileAsync(IsoFile isoFile, EventHandler<double> progressHandler,
        CancellationToken cancellationToken)
    {
        isoFile.UpdateOnUIThread(file =>
        {
            file.Status = ProcessingStatus.Decrypting;
            file.Progress = 0;
        });

        _decryptionService.ProgressChanged += progressHandler;
        await _decryptionService.DecryptFileAsync(isoFile, isoFile.GameInfo!.HexKey, cancellationToken);
        _decryptionService.ProgressChanged -= progressHandler;
    }

    private async Task ExtractContentsAsync(IsoFile isoFile, CancellationToken cancellationToken)
    {
        isoFile.UpdateOnUIThread(file =>
        {
            file.Status = ProcessingStatus.Extracting;
            file.Progress = 0;
        });

        var decryptedFile = isoFile.FilePath + ".dec";
        var folderName = GetSafeFileName(isoFile.GameInfo!.GameName);
        var extractPath = Path.Combine(Path.GetDirectoryName(isoFile.FilePath) ?? ".", folderName);

        await Task.Run(() =>
        {
            Directory.CreateDirectory(extractPath);
            ExtractIsoContents(isoFile, decryptedFile, extractPath, cancellationToken);
        }, cancellationToken);
    }

    private static string GetSafeFileName(string fileName) =>
        string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));

    private static void UpdateFileError(IsoFile isoFile, string message)
    {
        isoFile.UpdateOnUIThread(file =>
        {
            file.Status = ProcessingStatus.Error;
            file.StatusMessage = message;
        });
    }

    private void ExtractIsoContents(IsoFile isoFile, string decryptedFile, string extractPath,
        CancellationToken cancellationToken)
    {
        using var isoStream = File.Open(decryptedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cd = new CDReader(isoStream, true, true);

        var files = cd.GetFiles("", "*.*", SearchOption.AllDirectories).ToList();
        var fileCount = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractSingleFile(cd, file, extractPath);

            fileCount++;
            var progress = ((double)fileCount / files.Count) * 100;
            isoFile.UpdateOnUIThread(f => f.Progress = progress);
        }
    }

    private static void ExtractSingleFile(CDReader cd, string file, string extractPath)
    {
        var safePath = file.TrimStart('\\', '/').Replace(':', '_');
        var destinationPath = Path.Combine(extractPath, safePath);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var sourceStream = cd.OpenFile(file, FileMode.Open);
        using var destinationStream = File.Create(destinationPath);
        sourceStream.CopyTo(destinationStream);
    }
}