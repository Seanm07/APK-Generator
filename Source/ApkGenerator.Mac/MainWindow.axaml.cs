#pragma warning disable CS0618   // DragEventArgs.Data: the replacement API isn't in every 11.x release
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ApkGenerator.Core.ViewModels;

namespace ApkGenerator.Mac;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    // Window.Opened can fire before OpenOnStartup is called, or after; remember a pending file either way.
    private string? _pendingFile;
    private bool _opened;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        var s = vm.Settings.Data;
        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Opened += async (_, _) =>
        {
            _opened = true;
            // Keep the window on screen if it was saved on a bigger display.
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                var work = screen.WorkingArea;
                var scale = screen.Scaling;
                if (Height * scale > work.Height) Height = Math.Max(MinHeight, work.Height / scale - 20);
                if (Width * scale > work.Width) Width = Math.Max(MinWidth, work.Width / scale - 20);
            }
            if (_pendingFile != null) { var f = _pendingFile; _pendingFile = null; await _vm.LoadFileAsync(f); }
            await vm.InitializeAsync();
        };

        Closing += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
            {
                _vm.Settings.Data.WindowWidth = Width;
                _vm.Settings.Data.WindowHeight = Height;
                _vm.Settings.Save();
            }
            _vm.Shutdown();
        };

        // Pick up devices that were plugged in while another window had focus.
        Activated += async (_, _) => { if (!_vm.IsBusy) await _vm.Devices.RefreshAsync(force: false); };

        LogBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        };
    }

    /// <summary>A path passed on the command line, or a file opened from Finder / dropped on the Dock icon.</summary>
    public void OpenOnStartup(string path)
    {
        if (_opened) _ = _vm.LoadFileAsync(path);
        else _pendingFile = path;
    }

    // ------------------------------------------------------------------ drag & drop

    private static IEnumerable<string> DroppedPaths(DragEventArgs e) =>
        (e.Data.GetFiles() ?? Array.Empty<IStorageItem>())
            .Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
            DropOverlay.IsVisible = true;
        }
        else e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => DropOverlay.IsVisible = false;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        e.Handled = true;
        var files = DroppedPaths(e).ToList();
        if (files.Count == 0) return;

        // If several files are dropped, take the first .aab/.apk.
        var pick = files.FirstOrDefault(f => f.EndsWith(".aab", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) ?? files[0];
        await _vm.LoadFileAsync(pick);
    }

    // ------------------------------------------------------------------ pickers

    private async void BrowseFile_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open an Android App Bundle or APK",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Android packages") { Patterns = new[] { "*.aab", "*.apk" } } },
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) await _vm.LoadFileAsync(path);
    }

    private async void BrowseKeystore_Click(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_vm.Signing.KeystoresFolder);
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a keystore",
            AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_vm.Signing.KeystoresFolder),
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Keystores") { Patterns = new[] { "*.keystore", "*.jks", "*.ks", "*.p12", "*.pfx" } },
                FilePickerFileTypes.All,
            },
        });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;

        var error = _vm.Signing.AddKeystore(path);
        if (error != null) _vm.ShowError(error, "Couldn't add the keystore");
    }

    private void KeystoreCombo_DropDownOpened(object? sender, EventArgs e) => _vm.Signing.ReloadKeystores();

    // ------------------------------------------------------------------ actions

    private async void Install_Click(object? sender, RoutedEventArgs e) => await _vm.InstallAsync();

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vm.CanSave) return;
        var (folder, name) = _vm.SuggestedSave();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save APK",
            SuggestedFileName = name,
            DefaultExtension = "apk",
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(folder),
            FileTypeChoices = new[] { new FilePickerFileType("Android package") { Patterns = new[] { "*.apk" } } },
        });
        if (file?.TryGetLocalPath() is { } path) await _vm.SaveApkAsync(path);
    }

    private async void SaveFtp_Click(object? sender, RoutedEventArgs e) => await _vm.SaveApkToFtpAsync();

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        await new SettingsWindow(_vm.Settings).ShowDialog(this);
        _vm.RefreshFtpState();      // the FTP button appears or disappears depending on what was saved
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _vm.Cancel();
    private void OpenFolder_Click(object? sender, RoutedEventArgs e) => _vm.OpenOutputFolder();
    private void DismissError_Click(object? sender, RoutedEventArgs e) => _vm.DismissError();
    private void StoreSizeDetails_Click(object? sender, RoutedEventArgs e) => _vm.ShowStoreSizeError();
    private void ClearLog_Click(object? sender, RoutedEventArgs e) => _vm.ClearLog();

    private async void CopyError_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.Error is null) return;
        try
        {
            if (GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(_vm.Error.ToString());
        }
        catch { /* the clipboard can be locked by another app – nothing useful to do */ }
    }

    // ------------------------------------------------------------------ devices

    private async void RefreshDevices_Click(object? sender, RoutedEventArgs e) => await _vm.Devices.RefreshAsync();
    private async void Connect_Click(object? sender, RoutedEventArgs e) => await _vm.Devices.ConnectAsync();

    private async void IpBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await _vm.Devices.ConnectAsync();
    }
}
