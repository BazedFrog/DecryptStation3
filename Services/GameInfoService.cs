using Microsoft.UI.Xaml;
using DecryptStation3.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DecryptStation3.Services;

public class GameInfoService
{
    private const string JsonFileName = "game_keys.json";

    private readonly string _jsonPath;
    private readonly SetupService _setupService;
    private Dictionary<string, GameInfo>? _gameInfoCache;

    public GameInfoService()
    {
        _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, JsonFileName);
        _setupService = new SetupService();
    }

    public bool IsInitialized { get; private set; }

    public async Task<bool> InitializeAsync(XamlRoot? xamlRoot = null, IntPtr? windowHandle = null)
    {
        if (IsInitialized) return true;

        try
        {
            if (!File.Exists(_jsonPath))
            {
                if (xamlRoot == null || !windowHandle.HasValue)
                {
                    throw new FileNotFoundException($"Game database file not found: {_jsonPath}");
                }

                if (!await _setupService.ShowSetupDialogAsync(xamlRoot, windowHandle.Value))
                {
                    return false;
                }
            }

            await LoadGameDatabaseAsync();
            return true;
        }
        catch (Exception ex)
        {
            IsInitialized = false;
            throw new Exception($"Error loading game database: {ex.Message}", ex);
        }
    }

    public GameInfo? FindGameByHash(string hash)
    {
        if (!IsInitialized)
        {
            throw new InvalidOperationException("Game database not initialized. Call InitializeAsync first.");
        }

        return _gameInfoCache?.TryGetValue(hash.ToLowerInvariant(), out var gameInfo) == true
            ? gameInfo
            : null;
    }

    public int GetTotalGamesCount() => _gameInfoCache?.Count ?? 0;

    private async Task LoadGameDatabaseAsync()
    {
        var jsonContent = await File.ReadAllTextAsync(_jsonPath);

        var gameList = JsonSerializer.Deserialize<List<GameInfo>>(
            jsonContent,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        ) ?? throw new JsonException("Failed to parse JSON file");

        _gameInfoCache = new Dictionary<string, GameInfo>(
            gameList.Count,
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var game in gameList)
        {
            if (!string.IsNullOrEmpty(game.Sha1))
            {
                _gameInfoCache[game.Sha1] = game;
            }
        }

        IsInitialized = true;
    }
}