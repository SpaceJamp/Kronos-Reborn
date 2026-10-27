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

            // A file for the record, and memory for the diagnostics page.
            var logFilePath = Path.Combine(paths.Root, "logs", "kronosreborn.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
            Log.Instance.AddSink(new FileLogSink(logFilePath));
            var recentLog = new MemoryLogSink(5000);
            Log.Instance.AddSink(recentLog);

            Log.Instance.Information("Kronos Reborn starting", category: "App.Startup");
            Log.Instance.Information($"Data directory: {paths.Root}", category: "App.Startup");
            Log.Instance.Information($"Build: {BuildInfo.InformationalVersion}", category: "App.Startup");

            var persistence = new PersistenceService(paths);
            var loadResult = persistence.Load();
            var catalog = new RuntimeCatalog(paths.CatalogPath);

            var window = new MainWindow(persistence);
            window.DataContext = new MainViewModel(
                catalog, persistence, loadResult.Settings, paths, window, recentLog,
                catalog.LoadProblem is { } bad ? [.. loadResult.Problems, bad] : loadResult.Problems);

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
