using System.Collections.ObjectModel;

namespace ApkGenerator.Core.ViewModels;

/// <summary>One task in the bottom section (an install, a save, an upload…). Several can run side by side.</summary>
public sealed class JobViewModel : ObservableObject
{
    private StatusKind _kind = StatusKind.Working;
    private string _message = "";
    private OperationError? _error;
    private string? _resultPath;
    private string? _webUrl;
    private string _copyLabel = "Copy URL";

    public JobViewModel(string title, bool isInstall)
    {
        Title = title;
        IsInstall = isInstall;
    }

    public string Title { get; }
    public bool IsInstall { get; }
    public ObservableCollection<DeviceResultRow> Rows { get; } = new();
    internal CancellationTokenSource Cts { get; } = new();

    public StatusKind Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value)) return;
            Raise(nameof(IsRunning));
            Raise(nameof(IsFinished));
        }
    }

    public string Message { get => _message; set => Set(ref _message, value); }

    public OperationError? Error
    {
        get => _error;
        set { if (Set(ref _error, value)) Raise(nameof(HasError)); }
    }

    public string? ResultPath
    {
        get => _resultPath;
        set { if (Set(ref _resultPath, value)) { Raise(nameof(ShowInFolderVisible)); } }
    }

    /// <summary>Set after an FTP upload: the link "Copy URL" puts on the clipboard.</summary>
    public string? WebUrl
    {
        get => _webUrl;
        set { if (Set(ref _webUrl, value)) { Raise(nameof(HasUploadUrl)); Raise(nameof(ShowInFolderVisible)); } }
    }

    public string CopyLabel { get => _copyLabel; set => Set(ref _copyLabel, value); }

    public bool IsRunning => Kind == StatusKind.Working;
    public bool IsFinished => !IsRunning;
    public bool HasError => Error != null;
    public bool HasUploadUrl => WebUrl != null;
    /// <summary>"Show in folder" is for saved files; after an FTP upload it gives way to "Copy URL".</summary>
    public bool ShowInFolderVisible => ResultPath != null && !HasUploadUrl;
}
