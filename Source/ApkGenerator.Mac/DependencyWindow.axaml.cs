using Avalonia.Controls;
using Avalonia.Interactivity;
using ApkGenerator.Core.ViewModels;

namespace ApkGenerator.Mac;

public partial class DependencyWindow : Window
{
    private readonly DependencyDialogViewModel _vm;

    public DependencyWindow(MainViewModel main)
    {
        _vm = new DependencyDialogViewModel(main);
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
    }

    private void Link_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }) _vm.OpenLink(url);
    }

    /// <summary>Looks again; closes by itself once everything has been found.</summary>
    private async void Retry_Click(object? sender, RoutedEventArgs e)
    {
        await _vm.RetryAsync();
        if (!_vm.AnyMissing) Close(true);
    }

    private void Continue_Click(object? sender, RoutedEventArgs e) => Close(false);
}
