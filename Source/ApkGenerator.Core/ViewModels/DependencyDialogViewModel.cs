namespace ApkGenerator.Core.ViewModels;

/// <summary>One entry in the footer ("Java 25.0.1", "adb not found"); only missing ones are drawn in red.</summary>
public sealed record ToolStatusItem(string Text, bool Missing, bool ShowSeparator);

/// <summary>Backs the "some tools are missing" window shown at launch.</summary>
public sealed class DependencyDialogViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _isChecking;
    private string _message = "";

    public DependencyDialogViewModel(MainViewModel main)
    {
        _main = main;
        Reload();
    }

    public List<DependencyStatus> Missing { get; private set; } = new();
    public bool AnyMissing => Missing.Count > 0;
    public bool IsChecking { get => _isChecking; private set { if (Set(ref _isChecking, value)) Raise(nameof(CanRetry)); } }
    public bool CanRetry => !IsChecking;

    /// <summary>"Checked again - still missing: adb" after a retry that didn't find everything.</summary>
    public string Message { get => _message; private set => Set(ref _message, value); }

    private void Reload()
    {
        Missing = _main.MissingDependencies.ToList();
        Raise(nameof(Missing));
        Raise(nameof(AnyMissing));
    }

    public async Task RetryAsync()
    {
        IsChecking = true;
        Message = "Checking again…";
        try
        {
            await _main.RecheckToolsAsync();
            Reload();
            Message = AnyMissing ? "Checked again - still not found: " + string.Join(", ", Missing.Select(m => m.ShortName)) : "";
        }
        finally
        {
            IsChecking = false;
        }
    }

    public void OpenLink(string url) => PlatformShell.OpenUrl(url);
}
