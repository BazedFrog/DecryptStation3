using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DecryptStation3.Models;
using DecryptStation3.Services;
using DecryptStation3.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml.Markup;

namespace DecryptStation3;

public static class DialogHelper
{
    public static async Task ShowDialogAsync(Window window, string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            XamlRoot = window.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }
}

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        this.InitializeComponent();

        // Create services
        var gameInfoService = new GameInfoService();
        var hashService = new HashCalculationService();
        var decryptionService = new DecryptionService();
        var fileProcessingService = new FileProcessingService(hashService, decryptionService, gameInfoService);

        // Create and initialize ViewModel
        _viewModel = new MainViewModel(fileProcessingService, gameInfoService);

        // Initialize the ViewModel with the dispatcher
        if (DispatcherQueue != null)
        {
            _viewModel.Initialize(DispatcherQueue);
        }

        // Ensure the XAML root is ready before setting DataContext
        if (Content != null)
        {
            RootGrid.DataContext = _viewModel;
        }

        SetupWindow();
    }

    public MainWindow(MainViewModel viewModel)
    {
        this.InitializeComponent();
        _viewModel = viewModel;

        if (DispatcherQueue != null)
        {
            _viewModel.Initialize(DispatcherQueue);
        }

        if (Content != null)
        {
            RootGrid.DataContext = _viewModel;
        }

        SetupWindow();
    }

    private void SetupWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Set backdrop only if supported
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }

        btnAddFiles.Click += BtnAddFiles_Click;
        btnSelectAll.Click += BtnSelectAll_Click;
        btnProcessSelected.Click += BtnProcessSelected_Click;
        btnClearCompleted.Click += BtnClearCompleted_Click;

        gridFiles.ContainerContentChanging += ListView_ContainerContentChanging;
        gridFiles.SelectionChanged += ListView_SelectionChanged;

        Activated += MainWindow_Activated;

        SetWindowSizeAndPosition();
    }

    private void SetWindowSizeAndPosition()
    {
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);

        var width = (int)(displayArea.WorkArea.Width * 0.75);
        var height = (int)(displayArea.WorkArea.Height * 0.75);
        var centerX = (displayArea.WorkArea.Width - width) / 2;
        var centerY = (displayArea.WorkArea.Height - height) / 2;

        appWindow.MoveAndResize(new RectInt32(
            centerX + displayArea.WorkArea.X,
            centerY + displayArea.WorkArea.Y,
            width,
            height));
    }

    private void ListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is ListViewItem item && args.Item is IsoFile fileItem)
        {
            item.IsSelected = fileItem.IsSelected;
        }
    }

    private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (IsoFile item in e.AddedItems)
        {
            item.IsSelected = true;
        }

        foreach (IsoFile item in e.RemovedItems)
        {
            item.IsSelected = false;
        }

        // Update the ViewModel's HasSelectedFiles property
        _viewModel.HasSelectedFiles = gridFiles.SelectedItems.Count > 0;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // Only handle first activation
        Activated -= MainWindow_Activated;

        // Ensure DataContext is set after XAML is fully loaded
        if (_viewModel != null && RootGrid != null && RootGrid.DataContext == null)
        {
            RootGrid.DataContext = _viewModel;
        }

        // Continue with initialization
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await Task.Delay(500); // Give UI time to settle
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            while (true)
            {
                try
                {
                    if (await _viewModel.InitializeAsync(Content.XamlRoot, hwnd))
                    {
                        return;
                    }
                    App.Current.Exit();
                    return;
                }
                catch (Exception ex)
                {
                    var result = await new ContentDialog
                    {
                        Title = "Initialization Error",
                        Content = $"Failed to initialize: {ex.Message}\n\nWould you like to try again?",
                        PrimaryButtonText = "Try Again",
                        CloseButtonText = "Exit",
                        XamlRoot = Content.XamlRoot
                    }.ShowAsync();

                    if (result != ContentDialogResult.Primary)
                    {
                        App.Current.Exit();
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowDialogAsync(this, "Fatal Error", $"A critical error occurred: {ex.Message}");
            App.Current.Exit();
        }
    }

    private async void BtnAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var filePicker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        WinRT.Interop.InitializeWithWindow.Initialize(filePicker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        filePicker.FileTypeFilter.Add(".iso");

        var files = await filePicker.PickMultipleFilesAsync();
        if (files?.Count > 0)
        {
            _viewModel.AddFiles(files.Select(f => f.Path).ToArray());
        }
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        gridFiles.SelectAll();
    }

    private async void BtnProcessSelected_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = gridFiles.SelectedItems?.Cast<IsoFile>().ToArray();
        if (selectedFiles?.Length == 0)
        {
            await DialogHelper.ShowDialogAsync(this, "No Files Selected", "Please select files to process");
            return;
        }

        try
        {
            await _viewModel.ProcessSelectedAsync(selectedFiles!);
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowDialogAsync(this, "Processing Error",
                $"An error occurred while processing files: {ex.Message}");
        }
    }

    private void BtnClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearCompleted();
    }
}