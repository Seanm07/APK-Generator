using ApkGenerator.Core;

namespace ApkGenerator.Core.ViewModels;

/// <summary>Backs the small Settings window (currently the FTP upload details).</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private CancellationTokenSource? _testCts;

    private string _host;
    private string _port;
    private string _user;
    private string _password;
    private string _remoteFolder;
    private bool _useTls;

    private string _testMessage = "";
    private bool _testIsError;
    private bool _testIsOk;
    private bool _isTesting;
    private string _validationError = "";

    public SettingsViewModel(SettingsStore store)
    {
        _store = store;
        var f = store.GetFtp();
        _host = f.Host;
        _port = f.Port.ToString();
        _user = f.User;
        _password = f.Password;
        _remoteFolder = f.RemoteFolder;
        _useTls = f.UseTls;
    }

    public string Host { get => _host; set => Set(ref _host, value); }
    public string Port { get => _port; set => Set(ref _port, value); }
    public string User { get => _user; set => Set(ref _user, value); }
    public string Password { get => _password; set => Set(ref _password, value); }
    public string RemoteFolder { get => _remoteFolder; set => Set(ref _remoteFolder, value); }
    public bool UseTls { get => _useTls; set => Set(ref _useTls, value); }

    public string TestMessage { get => _testMessage; private set => Set(ref _testMessage, value); }
    public bool TestIsError { get => _testIsError; private set => Set(ref _testIsError, value); }
    public bool TestIsOk { get => _testIsOk; private set => Set(ref _testIsOk, value); }
    public bool IsTesting { get => _isTesting; private set { if (Set(ref _isTesting, value)) Raise(nameof(CanTest)); } }
    public bool CanTest => !IsTesting;
    public string ValidationError { get => _validationError; private set => Set(ref _validationError, value); }

    protected override void OnChanged(string? name)
    {
        // Any edit makes the last test result stale, and abandons a test that is still running
        // (otherwise its late result would be shown against the newly edited details).
        if (name is nameof(Host) or nameof(Port) or nameof(User) or nameof(Password) or nameof(RemoteFolder) or nameof(UseTls))
        {
            _testCts?.Cancel();
            _testCts = null;
            IsTesting = false;
            TestMessage = "";
            TestIsError = TestIsOk = false;
            ValidationError = "";
        }
    }

    /// <summary>
    /// Turns what was typed into settings. Forgives "ftp://host/folder" and "host:2121" in the server box.
    /// Returns null (and sets <see cref="ValidationError"/>) if the port isn't usable.
    /// </summary>
    private FtpSettings? Normalize()
    {
        var host = Host.Trim();
        var folder = RemoteFolder.Trim();
        var portText = Port.Trim();

        var schemeEnd = host.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0) host = host[(schemeEnd + 3)..];
        var slash = host.IndexOf('/');
        if (slash >= 0)
        {
            if (folder.Length == 0) folder = host[slash..];
            host = host[..slash];
        }
        var colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out _))
        {
            portText = host[(colon + 1)..];
            host = host[..colon];
        }

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            ValidationError = "The port must be a number between 1 and 65535 (FTP is normally 21).";
            return null;
        }

        return new FtpSettings(host, port, User.Trim(), Password, folder, UseTls);
    }

    /// <summary>Validates and stores the details. A blank server clears the FTP setup (the button disappears).</summary>
    public bool Save()
    {
        var s = Normalize();
        if (s is null) return false;
        _store.SaveFtp(s);
        return true;
    }

    public async Task TestAsync()
    {
        var s = Normalize();
        if (s is null) return;
        if (!s.IsConfigured)
        {
            TestMessage = "Enter the server name first.";
            TestIsError = true;
            return;
        }

        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();
        IsTesting = true;
        TestMessage = "Connecting…";
        TestIsError = TestIsOk = false;
        try
        {
            var result = await Task.Run(() => FtpUploader.TestAsync(s, cts.Token));
            if (cts.IsCancellationRequested) return;

            if (result.Success)
            {
                TestMessage = result.Message;
                TestIsOk = true;
            }
            else if (result.Error != null)
            {
                TestMessage = result.Error.Title + (result.Error.Hint != null ? " – " + result.Error.Hint : "");
                TestIsError = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_testCts, cts)) IsTesting = false;   // not if the details were edited and a newer state took over
        }
    }

    public void CancelTest() => _testCts?.Cancel();
}
