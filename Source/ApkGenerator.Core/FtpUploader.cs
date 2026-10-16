using System.Net.Sockets;
using System.Text.RegularExpressions;
using FluentFTP;

namespace ApkGenerator.Core;

/// <summary>FTP details as entered in the settings window. A blank server means "FTP not set up".</summary>
public sealed record FtpSettings(string Host, int Port, string User, string Password, string RemoteFolder, bool UseTls)
{
    public static FtpSettings Empty { get; } = new("", 21, "", "", "", false);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    /// <summary>
    /// The link copied by "Copy URL": the "ftp://host" part is replaced with "https://", so an FTP path like
    /// "/cdn.example.com/builds" becomes "https://cdn.example.com/builds/game.apk".
    /// </summary>
    public string WebUrl(string fileName)
    {
        var segments = RemoteFolder.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(fileName).Select(Uri.EscapeDataString);
        return "https://" + string.Join('/', segments);
    }

    /// <summary>"ftp://host:2121/builds/android/game.apk"</summary>
    public string Describe(string? fileName = null)
    {
        var port = Port == 21 ? "" : ":" + Port;
        var folder = RemoteFolder.Trim();
        var path = folder.Length == 0 ? "" : (folder.StartsWith('/') ? folder : "/" + folder).TrimEnd('/');
        return $"ftp://{Host.Trim()}{port}{path}" + (fileName != null ? "/" + fileName : "");
    }
}

public sealed class FtpResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string Message { get; init; } = "";
    public OperationError? Error { get; init; }
}

public static class FtpUploader
{
    /// <summary>Connects and checks the folder, without uploading anything.</summary>
    public static async Task<FtpResult> TestAsync(FtpSettings s, CancellationToken ct)
    {
        try
        {
            using var client = Create(s);
            await client.Connect(ct).ConfigureAwait(false);

            var folder = s.RemoteFolder.Trim();
            if (folder.Length == 0 || folder == "/")
                return new FtpResult { Success = true, Message = "Connected. Files will be uploaded to the server's top folder." };

            return await client.DirectoryExists(folder, ct).ConfigureAwait(false)
                ? new FtpResult { Success = true, Message = $"Connected. The folder {folder} exists." }
                : new FtpResult { Success = true, Message = $"Connected. The folder {folder} doesn't exist yet – it will be created on the first upload." };
        }
        catch (OperationCanceledException)
        {
            return new FtpResult { Cancelled = true };
        }
        catch (Exception ex)
        {
            return new FtpResult { Error = Interpret(ex, s) };
        }
    }

    /// <summary>Uploads one file into the configured folder (creating the folder if needed, replacing a file with the same name).</summary>
    public static async Task<FtpResult> UploadAsync(
        FtpSettings s, string localPath, string remoteFileName, Action<double, long>? progress, CancellationToken ct)
    {
        using var client = Create(s);
        string? partial = null;
        try
        {
            await client.Connect(ct).ConfigureAwait(false);

            var remotePath = RemotePath(s.RemoteFolder, remoteFileName);
            // Upload under a temporary name and rename when complete, so nobody can pick up a half-uploaded APK.
            partial = remotePath + ".uploading";
            var reporter = new Progress<FtpProgress>(p => progress?.Invoke(p.Progress, p.TransferredBytes));

            var status = await client.UploadFile(localPath, partial, FtpRemoteExists.Overwrite,
                createRemoteDir: true, FtpVerify.None, reporter, ct).ConfigureAwait(false);

            if (status == FtpStatus.Failed)
            {
                await DiscardPartialAsync(client, partial).ConfigureAwait(false);
                return new FtpResult { Error = ErrorInterpreter.Custom("The FTP upload failed",
                    "The server accepted the connection but the file wasn't stored. Check you have permission to write to the folder.") };
            }

            await client.MoveFile(partial, remotePath, FtpRemoteExists.Overwrite, ct).ConfigureAwait(false);
            partial = null;
            return new FtpResult { Success = true, Message = s.Describe(remoteFileName) };
        }
        catch (OperationCanceledException)
        {
            await DiscardPartialAsync(client, partial).ConfigureAwait(false);
            return new FtpResult { Cancelled = true };
        }
        catch (Exception ex)
        {
            await DiscardPartialAsync(client, partial).ConfigureAwait(false);
            return new FtpResult { Error = Interpret(ex, s) };
        }
    }

    /// <summary>Best effort: remove the temporary file left by an interrupted upload.</summary>
    private static async Task DiscardPartialAsync(AsyncFtpClient client, string? partial)
    {
        if (partial is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.DeleteFile(partial, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // The connection may be gone or the file never created; nothing more to do.
        }
    }

    private static AsyncFtpClient Create(FtpSettings s)
    {
        var anonymous = string.IsNullOrWhiteSpace(s.User);
        var client = new AsyncFtpClient(s.Host.Trim(), anonymous ? "anonymous" : s.User, anonymous ? "anonymous@" : s.Password, s.Port);
        client.Config.EncryptionMode = !s.UseTls ? FtpEncryptionMode.None : s.Port == 990 ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit;
        client.Config.ConnectTimeout = 15_000;
        client.Config.ReadTimeout = 30_000;
        client.Config.DataConnectionConnectTimeout = 15_000;
        client.Config.DataConnectionReadTimeout = 60_000;
        return client;
    }

    /// <summary>A relative folder stays relative to the login folder; a leading "/" is absolute.</summary>
    internal static string RemotePath(string folder, string fileName)
    {
        folder = folder.Trim();
        if (folder.Length == 0) return fileName;
        return folder.TrimEnd('/') + "/" + fileName;
    }

    // ------------------------------------------------------------------ errors

    private static OperationError Interpret(Exception ex, FtpSettings s)
    {
        var chain = new List<Exception>();
        for (var e = ex; e != null; e = e.InnerException) chain.Add(e);
        var names = chain.Select(e => e.GetType().Name).ToList();
        var text = string.Join(" | ", chain.Select(e => e.Message).Distinct());
        var technical = string.Join(Environment.NewLine, chain.Select(e => $"{e.GetType().Name}: {e.Message}").Distinct());

        OperationError Err(string title, string? hint = null) => ErrorInterpreter.Custom(title, hint, technical);

        if (names.Any(n => n.Contains("Authentication") && n.StartsWith("Ftp")) ||
            Regex.IsMatch(text, @"\b530\b|Login (incorrect|authentication failed)|not logged in", RegexOptions.IgnoreCase))
            return Err("The FTP server rejected the username or password",
                "Check the username and password in Settings (the cog button). Leave the username blank for anonymous access.");

        if (names.Any(n => n.Contains("SecurityNotAvailable")) ||
            Regex.IsMatch(text, @"does not support|AUTH (TLS|SSL)|encryption", RegexOptions.IgnoreCase) && s.UseTls)
            return Err("The FTP server doesn't support TLS",
                "Untick \"Use TLS (FTPS)\" in Settings, or use a server that supports it.");

        if (names.Any(n => n.Contains("Certificate")) || names.Contains("AuthenticationException") ||
            Regex.IsMatch(text, @"certificate|SSL|TLS", RegexOptions.IgnoreCase) && s.UseTls)
            return Err("The FTP server's TLS certificate couldn't be verified",
                "The server's certificate isn't trusted by this PC. Use a server with a valid certificate, or untick \"Use TLS (FTPS)\".");

        var socket = chain.OfType<SocketException>().FirstOrDefault();
        if (socket != null)
        {
            return socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                ? Err($"Couldn't find the FTP server \"{s.Host.Trim()}\"", "Check the server name in Settings.")
                : Err("Couldn't connect to the FTP server",
                    $"Check the server name and port ({s.Port}) in Settings, and that the server is reachable from this PC (VPN / firewall).");
        }

        if (chain.Any(e => e is TimeoutException) || names.Any(n => n.Contains("Timeout")) ||
            Regex.IsMatch(text, @"timed? ?out", RegexOptions.IgnoreCase))
            return Err("The FTP server didn't respond in time",
                "Check the server name and port in Settings. If it connects but stalls, the server may need a different connection mode.");

        // FluentFTP hides a DNS failure (unknown server name) behind a plain "Failed to connect to host".
        if (Regex.IsMatch(text, @"Failed to connect|connect to host|No such host|host.{0,20}not (found|known)", RegexOptions.IgnoreCase))
            return Err($"Couldn't connect to the FTP server \"{s.Host.Trim()}\"",
                "Check the server name and port in Settings, and that the server is reachable from this PC (VPN / firewall).");

        if (Regex.IsMatch(text, @"\b55[0-9]\b|permission denied|access denied|not allowed|No such file", RegexOptions.IgnoreCase))
            return Err("The FTP server refused to store the file",
                $"Check that the folder path ({(string.IsNullOrWhiteSpace(s.RemoteFolder) ? "the top folder" : s.RemoteFolder.Trim())}) is right and that this account may write to it.");

        if (chain.Any(e => e is IOException) && Regex.IsMatch(text, @"forcibly closed|connection.*(reset|closed|aborted)", RegexOptions.IgnoreCase))
            return Err("The connection to the FTP server was lost", "Try again. If it keeps happening the server may be dropping large uploads.");

        return Err("The FTP upload failed", null);
    }
}
