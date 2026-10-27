using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using KronosReborn.Data.Diagnostics;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Logging;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.UI;

/// <summary>
/// View model for the diagnostics panel: runs the checks, and copies or exports the result.
/// </summary>
public sealed class DiagnosticsViewModel : INotifyPropertyChanged
{
    private readonly DataPaths _paths;
    private readonly RuntimeCatalog _catalog;
    private readonly PersistenceService _persistence;
    private readonly Func<IEnumerable<ScanProblem>> _problems;
    private readonly MemoryLogSink? _recentLog;
    private readonly IFilePicker? _filePicker;

    private DiagnosticsReport? _report;
    private bool _isRunning;
    private string _statusText = "Not run yet.";

    public DiagnosticsViewModel(
        DataPaths paths,
        RuntimeCatalog catalog,
        PersistenceService persistence,
        Func<IEnumerable<ScanProblem>> problems,
        MemoryLogSink? recentLog,
        IFilePicker? filePicker)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _problems = problems ?? throw new ArgumentNullException(nameof(problems));
        _recentLog = recentLog;
        _filePicker = filePicker;

        RunCommand = new RelayCommand(async _ => await RunAsync(), _ => !IsRunning);
        CopyCommand = new RelayCommand(_ => Copy(), _ => _report is not null);
        ExportCommand = new RelayCommand(async _ => await ExportAsync(), _ => !IsRunning && _filePicker is not null);
    }

    public ICommand RunCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand ExportCommand { get; }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            _isRunning = value;
            OnPropertyChanged(nameof(IsRunning));
            RaiseCommandStates();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public IReadOnlyList<DiagnosticFact> Environment => _report?.Environment ?? [];
    public IReadOnlyList<DiagnosticCheck> Checks => _report?.Checks ?? [];
    public IReadOnlyList<LogEntry> RecentErrors => _report?.RecentErrors ?? [];
    public bool HasRecentErrors => RecentErrors.Count > 0;

    /// <summary>Runs every check off the UI thread, since it reads every file the collection holds.</summary>
    public async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        StatusText = "Checking…";

        try
        {
            _report = await Task.Run(Build);
            StatusText = _report.Summary;

            OnPropertyChanged(nameof(Environment));
            OnPropertyChanged(nameof(Checks));
            OnPropertyChanged(nameof(RecentErrors));
            OnPropertyChanged(nameof(HasRecentErrors));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Diagnostics could not finish: {ex.Message}";
            Log.Instance.Error(ex, "Diagnostics run failed", category: "Diagnostics");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private DiagnosticsReport Build() =>
        DiagnosticsRunner.Run(
            _paths,
            _catalog,
            _persistence.Histories.Values,
            _problems(),
            _recentLog?.Entries ?? [],
            [
                new DiagnosticFact("App version", BuildInfo.InformationalVersion),
                new DiagnosticFact("Commit", BuildInfo.GitCommitShort ?? "unknown"),
            ]);

    private void Copy()
    {
        if (_report is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_report.ToText());
            StatusText = "Report copied to the clipboard.";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // Another program holding the clipboard is common and harmless; say so and move on.
            StatusText = $"The clipboard is busy ({ex.Message}). Try again.";
        }
    }

    /// <summary>Re-runs the checks so the export is current, then writes the zip where the user chose.</summary>
    private async Task ExportAsync()
    {
        var target = _filePicker?.AskWhereToSaveDiagnostics(
            $"kronos-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        if (target is null)
        {
            return;
        }

        await RunAsync();
        if (_report is not { } report)
        {
            return;
        }

        IsRunning = true;
        try
        {
            Log.Instance.Flush();
            await Task.Run(() => DiagnosticsRunner.Export(report, _paths, target));
            StatusText = $"Saved diagnostics to {target}.";
            Log.Instance.Information($"Exported diagnostics to {target}", category: "Diagnostics");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not save diagnostics: {ex.Message}";
            Log.Instance.Error(ex, "Diagnostics export failed", category: "Diagnostics");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void RaiseCommandStates()
    {
        ((RelayCommand)RunCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CopyCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ExportCommand).RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
