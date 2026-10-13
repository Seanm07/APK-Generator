using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ApkGenerator.Core;
using ApkGenerator.Core.ViewModels;

namespace ApkGenerator.Mac;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = AppPaths.Detect();
            var settings = SettingsStore.Load(paths);
            var vm = new MainViewModel(paths, settings);
            var window = new MainWindow(vm);
            desktop.MainWindow = window;

            // Anything unexpected goes into the in-app error panel instead of crashing the program.
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                vm.ShowError(ErrorInterpreter.Custom("An unexpected error occurred",
                    "This is a bug in the program – use Copy details to report it.", args.Exception.ToString()), "Something went wrong");
            };

            // A path passed on the command line (macOS: `open -a "APK Generator" file.aab --args`) ...
            var file = desktop.Args?.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            if (file != null) window.OpenOnStartup(file);

            // ... or a file opened from Finder ("Open With") / dropped on the Dock icon.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime lifetime)
            {
                lifetime.Activated += (_, e) =>
                {
                    if (e is FileActivatedEventArgs fa &&
                        fa.Files.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrEmpty(p)) is { } path)
                        Dispatcher.UIThread.Post(() => window.OpenOnStartup(path));
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
