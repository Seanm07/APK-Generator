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
