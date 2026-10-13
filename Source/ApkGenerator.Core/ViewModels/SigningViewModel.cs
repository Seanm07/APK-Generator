using System.Collections.ObjectModel;
using System.Security.Cryptography;
using ApkGenerator.Core;

namespace ApkGenerator.Core.ViewModels;

public sealed record KeystoreFile(string Name, string Path)
{
    public override string ToString() => Name;
}

public sealed class SigningViewModel : ObservableObject
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly Func<ToolSet> _tools;

    private bool _enabled;
    private KeystoreFile? _selectedKeystore;
    private string _storePassword = "";
    private string _alias = "";
    private string _aliasPassword = "";
    private string _aliasStatus = "";
    private bool _aliasStatusIsError;
    private bool _aliasStatusIsOk;
    private string _notice = "";

    private bool _loading;                 // true while we fill fields programmatically
    private bool _programmaticAliasPw;
    private bool _aliasPwAutoFilled;
    private CancellationTokenSource? _aliasCts;

    public SigningViewModel(AppPaths paths, SettingsStore settings, Func<ToolSet> tools)
    {
        _paths = paths;
        _settings = settings;
        _tools = tools;

        ReloadKeystores();
        _enabled = settings.Data.SignApk;
        var remembered = settings.Data.SelectedKeystore;
        if (remembered != null)
            SelectedKeystore = Keystores.FirstOrDefault(k => string.Equals(k.Name, remembered, StringComparison.OrdinalIgnoreCase));
    }

    public ObservableCollection<KeystoreFile> Keystores { get; } = new();
    public ObservableCollection<string> Aliases { get; } = new();

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public KeystoreFile? SelectedKeystore { get => _selectedKeystore; set => Set(ref _selectedKeystore, value); }
    public string StorePassword { get => _storePassword; set => Set(ref _storePassword, value); }
    public string Alias { get => _alias; set => Set(ref _alias, value); }
    public string AliasPassword { get => _aliasPassword; set => Set(ref _aliasPassword, value); }
    public string AliasStatus { get => _aliasStatus; private set => Set(ref _aliasStatus, value); }
    public bool AliasStatusIsError { get => _aliasStatusIsError; private set => Set(ref _aliasStatusIsError, value); }
    public bool AliasStatusIsOk { get => _aliasStatusIsOk; private set => Set(ref _aliasStatusIsOk, value); }
    /// <summary>Message about the last browse/copy, e.g. "Copied into the Keystores folder".</summary>
    public string Notice { get => _notice; private set => Set(ref _notice, value); }

    public string KeystoresFolder => _paths.KeystoresDir;

    /// <summary>
    /// Without "Sign APK" bundletool uses the Android debug key; warn if that file doesn't exist,
    /// because the resulting APK would be unsigned and un-installable.
    /// </summary>
    public bool ShowDebugKeyWarning => !Enabled && !File.Exists(BuildService.DebugKeystorePath) && _tools().KeytoolPath is null;

    protected override void OnChanged(string? name)
    {
        switch (name)
        {
            case nameof(Enabled):
                _settings.Data.SignApk = Enabled;
                _settings.Save();
                Raise(nameof(ShowDebugKeyWarning));
                if (Enabled) ReloadKeystores();
                break;

            case nameof(SelectedKeystore):
                OnKeystoreSelected();
                break;

            case nameof(StorePassword):
                if (!_loading) _ = RefreshAliasesAsync(delayMs: 500);
                break;

            case nameof(Alias):
                if (!_loading) OnAliasEdited();
                break;

            case nameof(AliasPassword):
                if (!_programmaticAliasPw) _aliasPwAutoFilled = false;
                break;
        }
    }

    // ------------------------------------------------------------------ keystore list

    public void ReloadKeystores()
    {
        var keep = SelectedKeystore?.Name;
        string[] files;
        try
        {
            Directory.CreateDirectory(_paths.KeystoresDir);
            files = Directory.GetFiles(_paths.KeystoresDir).Where(KeystoreReader.LooksLikeKeystore)
                .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            files = Array.Empty<string>();
        }

        var wanted = files.Select(f => new KeystoreFile(System.IO.Path.GetFileName(f), f)).ToList();
        if (wanted.Select(w => w.Name).SequenceEqual(Keystores.Select(k => k.Name))) return;   // nothing changed

        _loading = true;
        try
        {
            Keystores.Clear();
            foreach (var w in wanted) Keystores.Add(w);
            _selectedKeystore = null;                 // the ComboBox cleared it; re-pick quietly below
            Raise(nameof(SelectedKeystore));
        }
        finally
        {
            _loading = false;
        }
        if (keep != null)
        {
            var again = Keystores.FirstOrDefault(k => string.Equals(k.Name, keep, StringComparison.OrdinalIgnoreCase));
            if (again != null)
            {
                _loading = true;
                _selectedKeystore = again;
                Raise(nameof(SelectedKeystore));
                _loading = false;
            }
        }
    }

    /// <summary>Called when a keystore was chosen with Browse. Copies it into the Keystores folder if it isn't already there.</summary>
    public OperationError? AddKeystore(string sourcePath)
    {
        try
        {
            Directory.CreateDirectory(_paths.KeystoresDir);
            var sourceDir = System.IO.Path.GetFullPath(System.IO.Path.GetDirectoryName(sourcePath)!).TrimEnd('\\', '/');
            var folder = System.IO.Path.GetFullPath(_paths.KeystoresDir).TrimEnd('\\', '/');
            var fileName = System.IO.Path.GetFileName(sourcePath);
            string notice;

            if (string.Equals(sourceDir, folder, StringComparison.OrdinalIgnoreCase))
            {
                notice = "";
            }
            else
            {
                var target = System.IO.Path.Combine(folder, fileName);
                if (File.Exists(target) && SameContent(sourcePath, target))
                {
                    notice = $"{fileName} is already in the Keystores folder.";
                }
                else
                {
                    if (File.Exists(target))
                    {
                        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
                        var ext = System.IO.Path.GetExtension(fileName);
                        for (var n = 2; File.Exists(target); n++)
                        {
                            fileName = $"{stem} ({n}){ext}";
                            target = System.IO.Path.Combine(folder, fileName);
                        }
                    }
                    File.Copy(sourcePath, target);
                    notice = $"Copied into the Keystores folder as {fileName}.";
                }
            }

            Keystores.Clear();                       // force ReloadKeystores to rebuild
            ReloadKeystores();
            SelectedKeystore = Keystores.FirstOrDefault(k => string.Equals(k.Name, fileName, StringComparison.OrdinalIgnoreCase));
            Notice = notice;
            return null;
        }
        catch (Exception ex)
        {
            return ErrorInterpreter.Custom("Couldn't add the keystore", "Check that the Keystores folder is writable.", ex.Message);
        }
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sha = SHA256.Create();
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        return sha.ComputeHash(sa).AsSpan().SequenceEqual(sha.ComputeHash(sb));
    }

    // ------------------------------------------------------------------ remembered details

    private void OnKeystoreSelected()
    {
        if (_loading) return;
        Notice = "";
        _aliasCts?.Cancel();

        _loading = true;
        try
        {
            Aliases.Clear();
            var ks = SelectedKeystore;
            if (ks is null)
            {
                StorePassword = "";
                Alias = "";
                SetAliasPassword("");
                SetAliasStatus("", false, false);
                return;
            }

            var saved = _settings.GetKeystoreDetails(ks.Name);
            StorePassword = saved?.StorePassword ?? "";
            Alias = saved?.Alias ?? "";
            SetAliasPassword(saved?.AliasPassword ?? "");
            _settings.Data.SelectedKeystore = ks.Name;
            _settings.Save();
        }
        finally
        {
            _loading = false;
        }
        _ = RefreshAliasesAsync(delayMs: 0);
    }

    /// <summary>The alias text changed (picked or typed): switch to that alias's remembered password.</summary>
    private void OnAliasEdited()
    {
        var ks = SelectedKeystore;
        if (ks is null) return;
        var saved = _settings.GetAliasPassword(ks.Name, Alias);
        if (saved != null) SetAliasPassword(saved);
        else if (_aliasPwAutoFilled) SetAliasPassword("");
    }

    private void SetAliasPassword(string value)
    {
        _programmaticAliasPw = true;
        try { AliasPassword = value; }
        finally { _programmaticAliasPw = false; }
        _aliasPwAutoFilled = value.Length > 0;
    }

    /// <summary>Remembers what's in the boxes as the details last used with this keystore.</summary>
    public void SaveDetails()
    {
        if (SelectedKeystore is null) return;
        _settings.SaveKeystoreDetails(SelectedKeystore.Name, StorePassword, Alias.Trim(), AliasPassword);
    }

    // ------------------------------------------------------------------ alias list

    private void SetAliasStatus(string text, bool isError, bool isOk)
    {
        AliasStatus = text;
        AliasStatusIsError = isError;
        AliasStatusIsOk = isOk;
    }

    private async Task RefreshAliasesAsync(int delayMs)
    {
        _aliasCts?.Cancel();
        var cts = new CancellationTokenSource();
        _aliasCts = cts;
        var ks = SelectedKeystore;
        if (ks is null) return;

        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cts.Token);
            SetAliasStatus("Reading aliases…", false, false);

            var password = StorePassword;
            var tools = _tools();
            var result = await Task.Run(() => KeystoreReader.ListAliasesAsync(ks.Path, password.Length > 0 ? password : null, tools, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            _loading = true;
            try
            {
                var current = Alias;
                Aliases.Clear();
                foreach (var a in result.Aliases) Aliases.Add(a);
                if (!string.IsNullOrEmpty(current)) Alias = current;      // the ComboBox may have cleared its text
            }
            finally
            {
                _loading = false;
            }

            if (result.Error != null)
                SetAliasStatus(result.Error, true, false);
            else if (result.NeedsPassword)
                SetAliasStatus("Enter the keystore password to list its aliases (or type the alias in).", false, false);
            else
            {
                var n = result.Aliases.Count;
                var text = n == 0 ? "No private keys found in this keystore."
                         : n == 1 ? "1 alias found." : $"{n} aliases found.";
                if (result.PasswordVerified == true) text = "Password correct · " + text;
                SetAliasStatus(text, n == 0, result.PasswordVerified == true);

                if (string.IsNullOrWhiteSpace(Alias) && n == 1)
                {
                    Alias = result.Aliases[0];          // single alias: pick it (also fills its remembered password)
                }
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer request
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) SetAliasStatus("Couldn't read the keystore: " + ex.Message, true, false);
        }
    }

    // ------------------------------------------------------------------ for the build

    /// <summary>Null config + null error means "not signing with a keystore".</summary>
    public (SigningConfig? config, OperationError? error) GetConfig()
    {
        if (!Enabled) return (null, null);

        if (SelectedKeystore is null)
            return (null, ErrorInterpreter.Custom("Choose a keystore to sign with",
                "Pick one from the list, or press Browse. Untick \"Sign APK\" to use the Android debug key instead."));
        if (!File.Exists(SelectedKeystore.Path))
            return (null, ErrorInterpreter.Custom("That keystore file no longer exists", "Pick another keystore, or press Browse."));
        if (string.IsNullOrEmpty(StorePassword))
            return (null, ErrorInterpreter.Custom("Enter the keystore password"));
        if (string.IsNullOrWhiteSpace(Alias))
            return (null, ErrorInterpreter.Custom("Choose or type the key alias"));

        var keyPassword = AliasPassword.Length > 0 ? AliasPassword : StorePassword;
        return (new SigningConfig(SelectedKeystore.Path, StorePassword, Alias.Trim(), keyPassword), null);
    }
}
