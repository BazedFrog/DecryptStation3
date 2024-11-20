using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DecryptStation3.Models;
using System.Collections.Generic;
using System.IO;
using System;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DecryptStation3.Services;

public class SetupService
{
    private const string JsonFileName = "game_keys.json";
    private const string TempDirName = "PS3DecryptTemp";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _jsonPath;
    private readonly string _tempPath;

    public SetupService()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _jsonPath = Path.Combine(baseDir, JsonFileName);
        _tempPath = Path.Combine(Path.GetTempPath(), TempDirName);
    }

    public async Task<bool> ShowSetupDialogAsync(XamlRoot xamlRoot, IntPtr windowHandle)
    {
        if (File.Exists(_jsonPath)) return true;

        while (true)
        {
            var choice = await ShowSetupChoiceDialogAsync(xamlRoot);

            var success = choice switch
            {
                ContentDialogResult.Primary => await ImportExistingJsonAsync(xamlRoot, windowHandle),
                ContentDialogResult.Secondary => await CreateDatabaseFromZipsAsync(xamlRoot, windowHandle),
                _ => false
            };

            if (!success && choice == ContentDialogResult.None)
            {
                return false; // User clicked Exit
            }

            if (success) return true;
        }
    }

    private static async Task<ContentDialogResult> ShowSetupChoiceDialogAsync(XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            Title = "First Time Setup",
            Content = "The required game database is missing. How would you like to create it?",
            PrimaryButtonText = "Use Existing JSON",
            SecondaryButtonText = "Create from ZIP files",
            CloseButtonText = "Exit",
            XamlRoot = xamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync();
    }

    private async Task<bool> ImportExistingJsonAsync(XamlRoot xamlRoot, IntPtr windowHandle)
    {
        try
        {
            var jsonFile = await PickFileAsync(windowHandle, ".json");
            if (jsonFile == null) return false;

            var jsonContent = await File.ReadAllTextAsync(jsonFile.Path);
            var gameList = JsonSerializer.Deserialize<List<GameInfo>>(jsonContent, JsonOptions);

            if (!ValidateGameList(gameList, xamlRoot)) return false;

            await File.WriteAllTextAsync(_jsonPath, jsonContent);
            await ShowSuccessDialog(xamlRoot, gameList!.Count);
            return true;
        }
        catch (Exception ex)
        {
            await ShowErrorDialog(xamlRoot, "Import Error", ex.Message);
            return false;
        }
    }

    private async Task<bool> CreateDatabaseFromZipsAsync(XamlRoot xamlRoot, IntPtr windowHandle)
    {
        try
        {
            // Get input files
            var (keysFile, datFile) = await GetInputFilesAsync(xamlRoot, windowHandle);
            if (keysFile == null || datFile == null) return false;

            // Show progress and process files
            var progressDialog = new ContentDialog
            {
                Title = "Creating Database",
                Content = new ProgressRing { IsIndeterminate = true },
                XamlRoot = xamlRoot
            };

            var progressTask = progressDialog.ShowAsync();

            try
            {
                await PrepareAndExtractFiles(keysFile.Path, datFile.Path);
                var gamesData = await ProcessExtractedFilesAsync();
                await SaveDatabaseAsync(gamesData);

                progressDialog.Hide();
                await ShowSuccessDialog(xamlRoot, gamesData.Count);
                return true;
            }
            finally
            {
                try { Directory.Delete(_tempPath, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialog(xamlRoot, "Setup Error", ex.Message);
            return false;
        }
    }

    private async Task<(StorageFile? keysFile, StorageFile? datFile)> GetInputFilesAsync(
        XamlRoot xamlRoot, IntPtr windowHandle)
    {
        var keysFile = await ShowFilePickerDialog(
            xamlRoot, windowHandle,
            "Select Keys File",
            "Please select the PS3 Disc Keys zip file\n(starts with 'Sony - PlayStation 3 - Disc Keys')"
        );

        if (keysFile == null) return (null, null);

        var datFile = await ShowFilePickerDialog(
            xamlRoot, windowHandle,
            "Select DAT File",
            "Please select the PS3 Datfile zip file\n(starts with 'Sony - PlayStation 3 - Datfile')"
        );

        return (keysFile, datFile);
    }

    private async Task PrepareAndExtractFiles(string keysPath, string datPath)
    {
        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, true);
        }
        Directory.CreateDirectory(_tempPath);

        await Task.Run(() =>
        {
            ZipFile.ExtractToDirectory(keysPath, _tempPath);
            ZipFile.ExtractToDirectory(datPath, _tempPath);
        });
    }

    private async Task<List<GameInfo>> ProcessExtractedFilesAsync()
    {
        var keyFiles = Directory.GetFiles(_tempPath, "*.key", SearchOption.AllDirectories);
        var datFiles = Directory.GetFiles(_tempPath, "*.dat", SearchOption.AllDirectories);

        if (!keyFiles.Any() || !datFiles.Any())
        {
            throw new Exception("Required files not found in zip archives");
        }

        return await Task.Run(() => ProcessFiles(datFiles[0], keyFiles));
    }

    private async Task SaveDatabaseAsync(List<GameInfo> games)
    {
        var jsonString = JsonSerializer.Serialize(games, JsonOptions);
        await File.WriteAllTextAsync(_jsonPath, jsonString);
    }

    private List<GameInfo> ProcessFiles(string datFile, string[] keyFiles)
    {
        var gamesFromDat = ParseDatFile(datFile);
        var games = new List<GameInfo>();

        foreach (var keyFile in keyFiles)
        {
            var gameName = Path.GetFileNameWithoutExtension(keyFile);
            var matchingGame = gamesFromDat.Keys.FirstOrDefault(k =>
                k.Equals(gameName, StringComparison.OrdinalIgnoreCase));

            if (matchingGame != null)
            {
                var hexKey = BitConverter.ToString(File.ReadAllBytes(keyFile))
                    .Replace("-", string.Empty);

                games.Add(new GameInfo
                {
                    GameName = matchingGame,
                    Sha1 = gamesFromDat[matchingGame],
                    HexKey = hexKey
                });
            }
        }

        return games;
    }

    private static Dictionary<string, string> ParseDatFile(string datFile)
    {
        try
        {
            var xml = XDocument.Load(datFile);
            return xml.Descendants("game")
                     .Where(g => g.Element("rom")?.Attribute("sha1") != null)
                     .ToDictionary(
                         g => g.Attribute("name")?.Value ?? string.Empty,
                         g => g.Element("rom")?.Attribute("sha1")?.Value ?? string.Empty,
                         StringComparer.OrdinalIgnoreCase
                     );
        }
        catch
        {
            return FallbackParseDatFile(datFile);
        }
    }

    private static Dictionary<string, string> FallbackParseDatFile(string datFile)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var gamePattern = new Regex(@"<game name=""([^""]+)""");
        var sha1Pattern = new Regex(@"sha1=""([^""]+)""");

        foreach (var line in File.ReadAllLines(datFile))
        {
            var gameMatch = gamePattern.Match(line);
            var sha1Match = sha1Pattern.Match(line);

            if (gameMatch.Success && sha1Match.Success)
            {
                result[gameMatch.Groups[1].Value] = sha1Match.Groups[1].Value;
            }
        }

        return result;
    }

    private static async Task<StorageFile?> ShowFilePickerDialog(
        XamlRoot xamlRoot, IntPtr windowHandle, string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "Select File",
            CloseButtonText = "Cancel",
            XamlRoot = xamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            FileTypeFilter = { ".zip" }
        };

        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        return await picker.PickSingleFileAsync();
    }

    private static async Task<StorageFile?> PickFileAsync(IntPtr windowHandle, string extension)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            FileTypeFilter = { extension }
        };

        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        return await picker.PickSingleFileAsync();
    }

    private static bool ValidateGameList(List<GameInfo>? gameList, XamlRoot xamlRoot)
    {
        if (gameList == null || !gameList.Any())
        {
            ShowErrorDialog(xamlRoot, "Invalid JSON File", "The selected file doesn't contain valid game data.");
            return false;
        }

        var invalidEntries = gameList.Where(g =>
            string.IsNullOrEmpty(g.GameName) ||
            string.IsNullOrEmpty(g.Sha1) ||
            string.IsNullOrEmpty(g.HexKey)).ToList();

        if (invalidEntries.Any())
        {
            ShowErrorDialog(xamlRoot, "Invalid JSON Content",
                $"Found {invalidEntries.Count} invalid entries. The JSON file must contain GameName, Sha1, and HexKey for all entries.");
            return false;
        }

        return true;
    }

    private static Task ShowErrorDialog(XamlRoot xamlRoot, string title, string message) =>
        new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = xamlRoot
        }.ShowAsync().AsTask();

    private static Task ShowSuccessDialog(XamlRoot xamlRoot, int gameCount) =>
        new ContentDialog
        {
            Title = "Setup Complete",
            Content = $"Successfully created database with {gameCount} games",
            CloseButtonText = "OK",
            XamlRoot = xamlRoot
        }.ShowAsync().AsTask();
}