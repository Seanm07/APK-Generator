using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ApkGenerator.Core;

public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    /// <summary>stdout and stderr interleaved in the order the lines arrived.</summary>
    public string Combined { get; init; } = "";
    public bool Cancelled { get; init; }
    public bool TimedOut { get; init; }
    public string? StartError { get; init; }

    public bool Success => StartError is null && !Cancelled && !TimedOut && ExitCode == 0;
}

public static class ProcessRunner
{
    /// <summary>
    /// Runs a tool without a window, capturing output. Never throws for tool failures – inspect the result.
    /// Safe for adb: it returns when the process exits even if a spawned daemon keeps the pipes open.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        CancellationToken ct = default,
        Action<string>? onLine = null,
        TimeSpan? timeout = null,
        IDictionary<string, string>? env = null,
        string? workDir = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // closed straight away so tools that prompt get EOF instead of hanging
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir ?? "",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        var allSb = new StringBuilder();
        var gate = new object();
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outEof = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errEof = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, _) => exited.TrySetResult(true);
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outEof.TrySetResult(true); return; }
            lock (gate) { outSb.AppendLine(e.Data); allSb.AppendLine(e.Data); }
            onLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errEof.TrySetResult(true); return; }
            lock (gate) { errSb.AppendLine(e.Data); allSb.AppendLine(e.Data); }
            onLine?.Invoke(e.Data);
        };

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult { ExitCode = -1, StartError = ex.Message };
        }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { p.StandardInput.Close(); } catch { }

        using var reg = ct.Register(() => TryKill(p));
        var timedOut = false;
        if (timeout is { } t)
        {
            var winner = await Task.WhenAny(exited.Task, Task.Delay(t)).ConfigureAwait(false);
            if (winner != exited.Task)
            {
                timedOut = true;
                TryKill(p);
                await exited.Task.ConfigureAwait(false);
            }
        }
        else
        {
            await exited.Task.ConfigureAwait(false);
        }

        // Let buffered output drain, but don't wait forever if a daemon inherited the pipes.
        await Task.WhenAny(Task.WhenAll(outEof.Task, errEof.Task), Task.Delay(1500)).ConfigureAwait(false);

        int code;
        try { code = p.ExitCode; } catch { code = -1; }

        string so, se, all;
        lock (gate) { so = outSb.ToString(); se = errSb.ToString(); all = allSb.ToString(); }
        return new ProcessResult
        {
            ExitCode = code,
            StdOut = so,
            StdErr = se,
            Combined = all,
            Cancelled = ct.IsCancellationRequested,
            TimedOut = timedOut,
        };
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}

public static class CommandLine
{
    /// <summary>Formats a command for display, hiding any passwords.</summary>
    public static string Format(string exe, IEnumerable<string> args)
    {
        var sb = new StringBuilder(Quote(Path.GetFileName(exe)));
        foreach (var a in args) sb.Append(' ').Append(Quote(Mask(a)));
        return sb.ToString();
    }

    private static string Mask(string arg) =>
        Regex.Replace(arg, @"(?i)(--(?:ks|key)-pass=pass:).*", "$1********");

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
