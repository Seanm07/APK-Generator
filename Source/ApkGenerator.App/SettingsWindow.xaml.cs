using System.Windows;
using ApkGenerator.Core.ViewModels;
using ApkGenerator.Core;

namespace ApkGenerator.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsStore store)
    {
        _vm = new SettingsViewModel(store);
        InitializeComponent();
        DataContext = _vm;
        // SizeToContent windows are measured after WPF has "centered" them, so they end up off to one side - centre again once sized.
        Loaded += (_, _) =>
        {
            if (Owner is null) return;
            Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
            Top = Math.Max(0, Owner.Top + (Owner.ActualHeight - ActualHeight) / 2);
        };
        Loaded += (_, _) => HostBox.Focus();
        Closed += (_, _) => _vm.CancelTest();
    }

    private async void Test_Click(object sender, RoutedEventArgs e) => await _vm.TestAsync();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Save()) DialogResult = true;
    }
}
