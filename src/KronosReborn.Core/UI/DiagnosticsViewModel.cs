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
    private readonly IFilePicker? _filePicker;
    private readonly Func<Task>? _rescan;

    private DiagnosticsReport? _report;
    private bool _isRunning;
    private string _statusText = "Not run yet.";

    public DiagnosticsViewModel(
        DataPaths paths,
        RuntimeCatalog catalog,
        PersistenceService persistence,
        Func<IEnumerable<ScanProblem>> problems,
        IFilePicker? filePicker,
        Func<Task>? rescan = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _problems = problems ?? throw new ArgumentNullException(nameof(problems));
        _filePicker = filePicker;
        _rescan = rescan;

        RunCommand = new RelayCommand(async _ => await RunAsync(rescan: true), _ => !IsRunning);
        CopyCommand = new RelayCommand(async _ => await CopyAsync(), _ => _report is not null && _filePicker is not null);
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
    /// <param name="rescan">
    /// Whether to scan the games again first, as Run again does. The checks alone take milliseconds
    /// and read only the app's own data, so a rerun without it looked as if nothing had happened.
    /// </param>
    public async Task RunAsync(bool rescan = false)
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;

        try
        {
            if (rescan && _rescan is not null)
            {
                StatusText = "Scanning your games…";
                await _rescan();
            }

            StatusText = "Checking…";
            _report = await Task.Run(Build);
            StatusText = $"{_report.Summary} Checked at {DateTime.Now:T}.";

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
            DiagnosticsRunner.ReadLog(_paths.LogsDirectory),
            [
                new DiagnosticFact("App version", BuildInfo.InformationalVersion),
                new DiagnosticFact("Commit", BuildInfo.GitCommitShort ?? "unknown"),
            ]);

    private async Task CopyAsync()
    {
        if (_report is null || _filePicker is null)
        {
            return;
        }

        await _filePicker.SetClipboardTextAsync(_report.ToText());
        StatusText = "Report copied to the clipboard.";
    }

    /// <summary>Re-runs the checks so the export is current, then writes the zip where the user chose.</summary>
    private async Task ExportAsync()
    {
        var target = _filePicker is null ? null : await _filePicker.AskWhereToSaveDiagnosticsAsync(
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
