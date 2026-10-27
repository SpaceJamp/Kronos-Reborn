using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KronosReborn.Data.Logging;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;
using KronosReborn.UI;

namespace KronosReborn;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = DataPaths.ForCurrentUser();
            paths.EnsureCreated();

            Directory.CreateDirectory(paths.LogsDirectory);
            Log.Instance.AddSink(new FileLogSink(paths.LogFilePath));

            // Logged before the program goes down, so the diagnostics page can show it next time.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    Log.Instance.Error(ex, $"Crashed: {ex.Message}", category: "Crash");
                }
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Instance.Error(e.Exception.GetBaseException(), $"A background task failed: {e.Exception.GetBaseException().Message}", category: "Crash");
                e.SetObserved();
            };

            Log.Instance.Information("Kronos Reborn starting", category: "App.Startup");
            Log.Instance.Information($"Data directory: {paths.Root}", category: "App.Startup");
            Log.Instance.Information($"Build: {BuildInfo.InformationalVersion}", category: "App.Startup");

            var persistence = new PersistenceService(paths);
            var loadResult = persistence.Load();
            var catalog = new RuntimeCatalog(paths.CatalogPath);

            var window = new MainWindow(persistence);
            window.DataContext = new MainViewModel(
                catalog, persistence, loadResult.Settings, paths, window,
                catalog.LoadProblem is { } bad ? [.. loadResult.Problems, bad] : loadResult.Problems);

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
