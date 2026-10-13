using System.Diagnostics;

namespace ApkGenerator.Core;

/// <summary>Things that differ per operating system: showing a file in Explorer/Finder, and a clean-ish "is this macOS" check.</summary>
public static class PlatformShell
{
    /// <summary>Opens the file manager with <paramref name="path"/> selected (or just opens the folder if it's a folder / missing).</summary>
    public static void Reveal(string path)
    {
        try
        {
            var isFile = File.Exists(path);
            if (OperatingSystem.IsWindows())
            {
                Process.Start("explorer.exe", isFile ? $"/select,\"{path}\"" : $"\"{path}\"");
            }
            else if (OperatingSystem.IsMacOS())
            {
                Start("open", isFile ? new[] { "-R", path } : new[] { path });        // -R = reveal in Finder
            }
            else
            {
                Start("xdg-open", new[] { isFile ? Path.GetDirectoryName(path)! : path });
            }
        }
        catch
        {
            // Nothing sensible to do if the file manager can't start.
        }
    }

    private static void Start(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
