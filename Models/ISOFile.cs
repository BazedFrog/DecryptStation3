using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;

namespace DecryptStation3.Models;

public enum ProcessingStatus
{
    Pending,
    CalculatingHash,
    HashCalculated,
    Decrypting,
    Extracting,
    Completed,
    Error
}

public sealed class IsoFile : INotifyPropertyChanged
{
    private static readonly string[] StatusMessages = {
        "Ready to process",           // Pending
        "Calculating hash...",        // CalculatingHash
        "Hash calculation complete",  // HashCalculated
        "Decrypting file...",         // Decrypting
        "Extracting contents...",     // Extracting
        "Processing complete",        // Completed
        string.Empty                  // Error - uses custom message
    };

    private readonly string _fileName = string.Empty;
    private readonly DispatcherQueue? _dispatcherQueue;
    private GameInfo? _gameInfo;
    private string _hash = string.Empty;
    private bool _isSelected;
    private double _progress;
    private ProcessingStatus _status = ProcessingStatus.Pending;
    private string _statusMessage = StatusMessages[0];

    public IsoFile(string filePath, DispatcherQueue dispatcherQueue)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        _dispatcherQueue = dispatcherQueue;
        Status = ProcessingStatus.Pending;
    }

    public string FilePath { get; }

    public string FileName
    {
        get => _fileName;
        private init => SetField(ref _fileName, value);
    }

    public string Hash
    {
        get => _hash;
        set => SetField(ref _hash, value);
    }

    public GameInfo? GameInfo
    {
        get => _gameInfo;
        set => SetField(ref _gameInfo, value);
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) > double.Epsilon)
            {
                SetField(ref _progress, value);
            }
        }
    }

    public ProcessingStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;

            _status = value;
            NotifyPropertyChanged();

            if (_status != ProcessingStatus.Error)
            {
                StatusMessage = StatusMessages[(int)value];
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateOnUIThread(Action<IsoFile> action)
    {
        if (_dispatcherQueue?.HasThreadAccess == true)
        {
            action(this);
            return;
        }

        _dispatcherQueue?.TryEnqueue(() => action(this));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        NotifyPropertyChanged(propertyName);
        return true;
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
}