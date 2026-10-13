using System.Windows;
using ApkGenerator.App.ViewModels;
using ApkGenerator.Core;

namespace ApkGenerator.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if DEBUG
        // Lets the dark theme be checked without changing the Windows setting (compiled out of release builds).
        if (Environment.GetEnvironmentVariable("APKGENERATOR_THEME") == "dark") ThemeMode = ThemeMode.Dark;
#endif
        UseWindowsAccentColor();

        var paths = AppPaths.Detect();
        var settings = SettingsStore.Load(paths);
        var vm = new MainViewModel(paths, settings);
        var window = new MainWindow(vm);
        MainWindow = window;

        // Anything unexpected goes into the in-app error panel instead of crashing the program.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            vm.ShowError(OperationError_FromException(args.Exception), "Something went wrong");
        };

        window.Show();

        // Dragging a file onto the exe (like the old batch file) or "Open with" passes its path here.
        var file = e.Args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (file != null) window.OpenOnStartup(file);
    }

    private static OperationError OperationError_FromException(Exception ex) =>
        ErrorInterpreter.Custom("An unexpected error occurred", "This is a bug in the program – use Copy details to report it.", ex.ToString());

    /// <summary>The badge, spinners and drop highlight follow the accent colour chosen in Windows settings.</summary>
    private void UseWindowsAccentColor()
    {
        var c = System.Windows.SystemParameters.WindowGlassColor;
        Resources["AccentBrush"] = Freeze(new System.Windows.Media.SolidColorBrush(c));
        Resources["AccentTintBrush"] = Freeze(new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x26, c.R, c.G, c.B)));

        static System.Windows.Media.Brush Freeze(System.Windows.Media.SolidColorBrush b)
        {
            b.Freeze();
            return b;
        }
    }
}
