using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using KronosReborn.Data.Downloads;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.UI;

/// <summary>One runtime that can be downloaded, as the picker lists it.</summary>
public sealed record DownloadKind(RuntimeKind Kind, string Label, string Repository);

/// <summary>
/// View model for the download panel: the newest of everything your games use in one click, or any
/// release of any runtime.
/// </summary>
public sealed class DownloadsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly VendorDownloader _downloader;
    private readonly Func<IEnumerable<RuntimeKind>> _kindsInGames;
    private CancellationTokenSource _cts = new();

    private DownloadKind? _selectedKind;
    private string? _selectedRelease;
    private bool _isBusy;
    private double _progress;
    private string _statusText = string.Empty;

    public DownloadsViewModel(VendorDownloader downloader, Func<IEnumerable<RuntimeKind>> kindsInGames)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _kindsInGames = kindsInGames ?? throw new ArgumentNullException(nameof(kindsInGames));

        Kinds = [.. VendorDownloader.Sources
            .Select(s => new DownloadKind(s.Key, RuntimeKindNames.Display(s.Key), s.Value.Repository))
            .OrderBy(k => k.Label)];

        GetNewestForGamesCommand = new RelayCommand(async _ => await GetNewestForGamesAsync(), _ => !IsBusy);
        DownloadSelectedCommand = new RelayCommand(
            async _ => await DownloadSelectedAsync(), _ => !IsBusy && SelectedKind is not null);
        StopCommand = new RelayCommand(_ => Stop(), _ => IsBusy);
    }

    /// <summary>Raised after a download adds a version, so the pickers can offer it.</summary>
    public event EventHandler? CollectionChanged;

    public IReadOnlyList<DownloadKind> Kinds { get; }

    /// <summary>Releases of the selected kind's repository, newest first, with "Newest" on top.</summary>
    public ObservableCollection<string> Releases { get; } = [];

    public ICommand GetNewestForGamesCommand { get; }
    public ICommand DownloadSelectedCommand { get; }
    public ICommand StopCommand { get; }

    /// <summary>What "Newest" means in the release picker: the newest release that has the file.</summary>
    public const string NewestRelease = "Newest";

    public DownloadKind? SelectedKind
    {
        get => _selectedKind;
        set
        {
            _selectedKind = value;
            OnPropertyChanged(nameof(SelectedKind));
            RaiseCommandStates();
            _ = LoadReleasesAsync();
        }
    }

    public string? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            _selectedRelease = value;
            OnPropertyChanged(nameof(SelectedRelease));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged(nameof(IsBusy));
            RaiseCommandStates();
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            _progress = value;
            OnPropertyChanged(nameof(Progress));
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

    /// <summary>Downloads the newest version of every runtime any game carries.</summary>
    private async Task GetNewestForGamesAsync()
    {
        var kinds = _kindsInGames().Distinct().Where(VendorDownloader.Sources.ContainsKey).ToList();
        if (kinds.Count == 0)
        {
            StatusText = "None of your games carry a runtime yet. Refresh the game list first.";
            return;
        }

        var lines = await RunAsync(async token =>
        {
            var results = new List<string>();
            foreach (var kind in kinds)
            {
                token.ThrowIfCancellationRequested();
                StatusText = $"Getting the newest {RuntimeKindNames.Display(kind)} ({results.Count + 1} of {kinds.Count})…";
                results.Add((await _downloader.DownloadNewestAsync(kind, NewProgress(), token)).Message);
            }

            return results;
        });

        if (lines is not null)
        {
            StatusText = string.Join(" ", lines);
        }
    }

    private async Task DownloadSelectedAsync()
    {
        if (SelectedKind is not { } kind)
        {
            return;
        }

        var release = SelectedRelease;
        var lines = await RunAsync(async token =>
        {
            StatusText = $"Downloading {kind.Label}…";
            var outcome = release is null or NewestRelease
                ? await _downloader.DownloadNewestAsync(kind.Kind, NewProgress(), token)
                : await _downloader.DownloadAsync(kind.Kind, release, NewProgress(), token);
            return new List<string> { outcome.Message };
        });

        if (lines is not null)
        {
            StatusText = lines[0];
        }
    }

    private async Task LoadReleasesAsync()
    {
        Releases.Clear();
        Releases.Add(NewestRelease);
        SelectedRelease = NewestRelease;

        if (SelectedKind is not { } kind)
        {
            return;
        }

        try
        {
            foreach (var release in await _downloader.ListReleasesAsync(kind.Kind))
            {
                // The kind may have changed while the list was loading.
                if (SelectedKind == kind)
                {
                    Releases.Add(release);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            StatusText = $"Could not list releases of {kind.Repository}: {ex.Message}. \"Newest\" still works once you are online.";
        }
    }

    /// <summary>Runs one download job: busy while it runs, refreshed pickers after, stoppable.</summary>
    private async Task<List<string>?> RunAsync(Func<CancellationToken, Task<List<string>>> job)
    {
        if (IsBusy)
        {
            return null;
        }

        IsBusy = true;
        Progress = 0;

        try
        {
            return await job(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download stopped.";
            return null;
        }
        catch (HttpRequestException ex)
        {
            StatusText = $"Could not reach GitHub: {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            CollectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Progress<double> NewProgress() => new(p => Progress = p);

    private void Stop()
    {
        // A fresh source for the next job; the stopped one stays cancelled.
        var stopped = _cts;
        _cts = new CancellationTokenSource();
        stopped.Cancel();
        stopped.Dispose();
    }

    private void RaiseCommandStates()
    {
        ((RelayCommand)GetNewestForGamesCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DownloadSelectedCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
    }

    public void Dispose() => _cts.Dispose();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
