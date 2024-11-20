using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using DecryptStation3.Models;
using DecryptStation3.Services;
using System.Collections.Generic;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace DecryptStation3.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly FileProcessingService _fileProcessingService;
    private readonly GameInfoService _gameInfoService;
    private DispatcherQueue? _dispatcherQueue;
    private bool _isProcessing;
    private string _statusMessage = string.Empty;
    private bool _hasSelectedFiles;

    public MainViewModel(FileProcessingService fileProcessingService, GameInfoService gameInfoService)
    {
        _fileProcessingService = fileProcessingService;
        _gameInfoService = gameInfoService;
        Files = new ObservableCollection<IsoFile>();
        Files.CollectionChanged += Files_CollectionChanged;
    }

    private void Files_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Handle new items
        if (e.NewItems != null)
        {
            foreach (IsoFile file in e.NewItems)
            {
                file.PropertyChanged += File_PropertyChanged;
            }
        }

        // Handle removed items
        if (e.OldItems != null)
        {
            foreach (IsoFile file in e.OldItems)
            {
                file.PropertyChanged -= File_PropertyChanged;
            }
        }

        NotifyPropertyChanged(nameof(HasFiles));
        NotifyPropertyChanged(nameof(HasCompletedFiles));
        NotifyPropertyChanged(nameof(CanProcessSelected));
    }

    private void File_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsoFile.Status))
        {
            NotifyPropertyChanged(nameof(HasCompletedFiles));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<IsoFile> Files { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasSelectedFiles
    {
        get => _hasSelectedFiles;
        set
        {
            if (SetProperty(ref _hasSelectedFiles, value))
            {
                NotifyPropertyChanged(nameof(CanProcessSelected));
            }
        }
    }

    public bool IsProcessing
    {
        get => !_isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, !value))
            {
                NotifyPropertyChanged(nameof(CanProcessSelected));
            }
        }
    }

    public bool HasFiles => Files.Count > 0;

    public bool HasCompletedFiles => Files.Any(f =>
        f.Status is ProcessingStatus.Completed or ProcessingStatus.Error);

    public bool CanProcessSelected => HasSelectedFiles && !_isProcessing;

    public void Initialize(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public async Task<bool> InitializeAsync(XamlRoot xamlRoot, IntPtr windowHandle)
    {
        try
        {
            StatusMessage = "Loading key database...";
            if (!await _gameInfoService.InitializeAsync(xamlRoot, windowHandle))
            {
                return false;
            }

            StatusMessage = $"Loaded {_gameInfoService.GetTotalGamesCount()} keys";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading game database: {ex.Message}";
            throw;
        }
    }

    public void AddFiles(string[] filePaths)
    {
        if (_dispatcherQueue == null) return;

        var newFiles = filePaths
            .Where(path => !Files.Any(f => f.FilePath == path))
            .Select(path => new IsoFile(path, _dispatcherQueue))
            .ToList();

        if (newFiles.Count == 0) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            foreach (var file in newFiles)
            {
                Files.Add(file);
            }
        });
    }

    public async Task ProcessSelectedAsync(IsoFile[] selectedFiles)
    {
        if (_isProcessing || selectedFiles.Length == 0) return;

        _isProcessing = true;
        IsProcessing = false;
        StatusMessage = "Processing files...";

        try
        {
            await ProcessFilesAsync(selectedFiles);
        }
        finally
        {
            _isProcessing = false;
            IsProcessing = true;
            StatusMessage = "Processing complete";
        }
    }

    public void ClearCompleted()
    {
        if (_dispatcherQueue == null) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            var completedFiles = Files
                .Where(f => f.Status is ProcessingStatus.Completed or ProcessingStatus.Error)
                .ToList();

            foreach (var file in completedFiles)
            {
                Files.Remove(file);
            }
        });
    }

    private async Task ProcessFilesAsync(IsoFile[] files)
    {
        foreach (var file in files)
        {
            try
            {
                await _fileProcessingService.ProcessFileAsync(file);
            }
            catch (Exception ex)
            {
                UpdateFileError(file, ex.Message);
            }
        }
    }

    private void UpdateFileError(IsoFile file, string message)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            file.Status = ProcessingStatus.Error;
            file.StatusMessage = $"Error: {message}";
        });
    }

    private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
    {
        if (_dispatcherQueue?.HasThreadAccess == true)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return;
        }

        _dispatcherQueue?.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        });
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        NotifyPropertyChanged(propertyName);
        return true;
    }
}