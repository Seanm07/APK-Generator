using Avalonia.Controls;
using Avalonia.Interactivity;
using ApkGenerator.Core;
using ApkGenerator.Core.ViewModels;

namespace ApkGenerator.Mac;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsStore store)
    {
        _vm = new SettingsViewModel(store);
        InitializeComponent();
        DataContext = _vm;
        // SizeToContent windows are measured after they've been "centered", so re-centre over the owner once sized.
        Opened += (_, _) =>
        {
            if (Owner is not Window o) return;
            var scale = RenderScaling;
            Position = new Avalonia.PixelPoint(
                o.Position.X + (int)((o.Bounds.Width - Bounds.Width) * scale / 2),
                Math.Max(0, o.Position.Y + (int)((o.Bounds.Height - Bounds.Height) * scale / 2)));
        };
        Opened += (_, _) => HostBox.Focus();
        Closed += (_, _) => _vm.CancelTest();
    }

    private async void Test_Click(object? sender, RoutedEventArgs e) => await _vm.TestAsync();

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.Save()) Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
