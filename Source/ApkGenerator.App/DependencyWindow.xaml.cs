using System.Windows;
using System.Windows.Controls;
using ApkGenerator.Core.ViewModels;

namespace ApkGenerator.App;

public partial class DependencyWindow : Window
{
    private readonly DependencyDialogViewModel _vm;

    public DependencyWindow(MainViewModel main)
    {
        _vm = new DependencyDialogViewModel(main);
        InitializeComponent();
        DataContext = _vm;
        // SizeToContent windows are measured after WPF has "centered" them, so they end up off to one side - centre again once sized.
        Loaded += (_, _) =>
        {
            if (Owner is null) return;
            Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
            Top = Math.Max(0, Owner.Top + (Owner.ActualHeight - ActualHeight) / 2);
        };
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string url) _vm.OpenLink(url);
    }

    /// <summary>Looks again; closes by itself once everything has been found.</summary>
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        await _vm.RetryAsync();
        if (!_vm.AnyMissing) DialogResult = true;
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
