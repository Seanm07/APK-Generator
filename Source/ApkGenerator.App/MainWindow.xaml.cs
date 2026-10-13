using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApkGenerator.App.ViewModels;
using Microsoft.Win32;

namespace ApkGenerator.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

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
        // Never open taller/wider than the visible desktop (e.g. on a laptop screen).
        var work = SystemParameters.WorkArea;
        Height = Math.Min(Height, work.Height - 10);
        Width = Math.Min(Width, work.Width - 10);

        Loaded += async (_, _) => await vm.InitializeAsync();
    }

    /// <summary>A path passed on the command line (drag onto the exe, or "Open with").</summary>
    public void OpenOnStartup(string path)
    {
        if (IsLoaded) _ = _vm.LoadFileAsync(path);
        else Loaded += async (_, _) => await _vm.LoadFileAsync(path);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _vm.Settings.Data.WindowWidth = Width;
            _vm.Settings.Data.WindowHeight = Height;
            _vm.Settings.Save();
        }
        _vm.Shutdown();
    }

    private async void Window_Activated(object? sender, EventArgs e)
    {
        // Pick up devices that were plugged in while another window had focus.
        if (!_vm.IsBusy) await _vm.Devices.RefreshAsync(force: false);
    }

    // ------------------------------------------------------------------ drag & drop

    private static string[]? DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is { Length: > 0 })
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        // Moving between child controls also raises DragLeave; only hide when the pointer really left the window.
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
            DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        var files = DroppedFiles(e);
        if (files is not { Length: > 0 }) return;

        // If several files are dropped, take the first .aab/.apk.
        var pick = files.FirstOrDefault(f => f.EndsWith(".aab", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) ?? files[0];
        await _vm.LoadFileAsync(pick);
    }

    // ------------------------------------------------------------------ file / keystore pickers

    private async void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open an Android App Bundle or APK",
            Filter = "Android packages (*.aab;*.apk)|*.aab;*.apk|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true) await _vm.LoadFileAsync(dlg.FileName);
    }

    private void BrowseKeystore_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a keystore",
            Filter = "Keystores (*.keystore;*.jks;*.ks;*.p12;*.pfx)|*.keystore;*.jks;*.ks;*.p12;*.pfx|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(_vm.Signing.KeystoresFolder) ? _vm.Signing.KeystoresFolder : "",
        };
        if (dlg.ShowDialog(this) != true) return;

        var error = _vm.Signing.AddKeystore(dlg.FileName);
        if (error != null) _vm.ShowError(error, "Couldn't add the keystore");
    }

    private void KeystoreCombo_DropDownOpened(object? sender, EventArgs e) => _vm.Signing.ReloadKeystores();

    // ------------------------------------------------------------------ actions

    private async void Install_Click(object sender, RoutedEventArgs e) => await _vm.InstallAsync();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanSave) return;
        var (folder, name) = _vm.SuggestedSave();
        var dlg = new SaveFileDialog
        {
            Title = "Save APK",
            Filter = "Android package (*.apk)|*.apk",
            DefaultExt = ".apk",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = folder,
            FileName = name,
        };
        if (dlg.ShowDialog(this) == true) await _vm.SaveApkAsync(dlg.FileName);
    }

    private async void SaveFtp_Click(object sender, RoutedEventArgs e) => await _vm.SaveApkToFtpAsync();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_vm.Settings) { Owner = this };
        dialog.ShowDialog();
        _vm.RefreshFtpState();      // the FTP button appears or disappears depending on what was saved
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _vm.Cancel();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _vm.OpenOutputFolder();
    private void DismissError_Click(object sender, RoutedEventArgs e) => _vm.DismissError();
    private void StoreSizeDetails_Click(object sender, RoutedEventArgs e) => _vm.ShowStoreSizeError();
    private void ClearLog_Click(object sender, RoutedEventArgs e) => _vm.ClearLog();

    private void CopyError_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Error is null) return;
        try { Clipboard.SetText(_vm.Error.ToString()); }
        catch { /* the clipboard can be locked by another app – nothing useful to do */ }
    }

    // ------------------------------------------------------------------ devices

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await _vm.Devices.RefreshAsync();
    private async void Connect_Click(object sender, RoutedEventArgs e) => await _vm.Devices.ConnectAsync();

    private async void IpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await _vm.Devices.ConnectAsync();
    }

    // ------------------------------------------------------------------ log

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
