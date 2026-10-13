using System.Windows;
using ApkGenerator.App.ViewModels;
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
        Loaded += (_, _) => HostBox.Focus();
        Closed += (_, _) => _vm.CancelTest();
    }

    private async void Test_Click(object sender, RoutedEventArgs e) => await _vm.TestAsync();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Save()) DialogResult = true;
    }
}
