using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using KronosReborn.Data.Downloads;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.UI;

/// <summary>
/// View model for downloading DLSS runtimes from NVIDIA's NGX CDN.
/// </summary>
public sealed class NgxDownloadViewModel : System.ComponentModel.INotifyPropertyChanged, IDisposable
{
    private readonly NgxDownloader _downloader;
    private readonly RuntimeCatalog _catalog;
    private CancellationTokenSource _cts = new();

    public NgxDownloadViewModel(NgxDownloader downloader, RuntimeCatalog catalog)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        ScanCommand = new RelayCommand(async _ => await ScanAsync(), _ => !IsScanning && !IsDownloading);
        DownloadCommand = new RelayCommand(async parameter =>
        {
            if (parameter is NgxVersionEntry entry)
            {
                await DownloadAsync(entry);
            }
        }, _ => !IsScanning && !IsDownloading);
    }

    /// <summary>Versions discovered on NVIDIA's CDN.</summary>
    public ObservableCollection<NgxVersionEntry> Versions { get; } = new();

    /// <summary>Whether a scan is in progress.</summary>
    public bool IsScanning { get; private set; }

    /// <summary>Whether a download is in progress.</summary>
    public bool IsDownloading { get; private set; }

    /// <summary>Download progress percentage (0-100).</summary>
    public double Progress { get; private set; }

    /// <summary>Status message shown to the user.</summary>
    public string StatusText { get; private set; } = "Ready";

    /// <summary>Errors encountered during scan or download.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Scan NVIDIA's CDN for available DLSS versions.</summary>
    public ICommand ScanCommand { get; }

    /// <summary>Download a selected DLSS version from NVIDIA's CDN.</summary>
    public ICommand DownloadCommand { get; }

    private readonly List<string> _errors = new();

    /// <summary>
    /// Scans NVIDIA's CDN for available DLSS versions.
    /// </summary>
    public async Task ScanAsync()
    {
        if (IsScanning || IsDownloading)
        {
            return;
        }

        IsScanning = true;
        StatusText = "Scanning NVIDIA CDN for DLSS versions...";
        _errors.Clear();
        Versions.Clear();
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Versions));
        ((RelayCommand)ScanCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DownloadCommand).RaiseCanExecuteChanged();

        try
        {
            var list = await NgxDownloader.ListVersionsAsync(_cts.Token);

            foreach (var entry in list.Versions.OrderByDescending(v => v.VersionPacked))
            {
                Versions.Add(entry);
            }

            StatusText = Versions.Count == 0
                ? "No DLSS versions found on NVIDIA CDN."
                : $"Found {Versions.Count} DLSS version(s) on NVIDIA CDN.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
            _errors.Add(ex.Message);
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(IsScanning));
            OnPropertyChanged(nameof(StatusText));
            ((RelayCommand)ScanCommand).RaiseCanExecuteChanged();
            ((RelayCommand)DownloadCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Downloads and verifies a selected runtime version from NVIDIA's CDN.
    /// </summary>
    public async Task DownloadAsync(NgxVersionEntry entry)
    {
        if (IsDownloading || IsScanning)
        {
            return;
        }

        IsDownloading = true;
        StatusText = $"Downloading {entry.AssetType} version {entry.VersionPacked}...";
        Progress = 0;
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Progress));
        ((RelayCommand)ScanCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DownloadCommand).RaiseCanExecuteChanged();

        try
        {
            var path = await _downloader.DownloadAndVerifyAsync(
                entry,
                entry.VersionPacked.ToString(CultureInfo.InvariantCulture),
                new Progress<double>(p => { Progress = p; OnPropertyChanged(nameof(Progress)); }),
                _cts.Token);

            if (path is null)
            {
                StatusText = $"Download or signature verification failed for {entry.AssetType} {entry.VersionPacked}.";
                _errors.Add($"Download failed: {entry.AssetType} {entry.VersionPacked}");
                return;
            }

            // Read the version from the downloaded file and add it to the catalog.
            if (VersionNumber.TryRead(path, out var version, out var _, out var readError) && version is not null)
            {
                var kind = RuntimeKindFromAsset(entry.AssetType);
                var trust = RuntimeTrust.Trusted; // Authenticode was already verified in DownloadAndVerifyAsync

                var alreadyHeld = _catalog.TryGet(
                    CatalogId.For(kind, version, RuntimeSource.Imported), out var existing)
                    && existing?.Trust == RuntimeTrust.Trusted;

                var entryAdded = _catalog.AddImport(
                    kind,
                    version,
                    Path.GetDirectoryName(path)!,
                    $"NVIDIA CDN ({entry.DownloadUrl})",
                    trust);

                StatusText = entryAdded is null
                    ? $"{RuntimeKindNames.Display(kind)} {version} could not be added to the collection."
                    : alreadyHeld
                        ? $"{RuntimeKindNames.Display(kind)} {version} is already in the collection."
                        : $"Installed {RuntimeKindNames.Display(kind)} {version} into the collection.";
            }
            else
            {
                StatusText = $"Downloaded {entry.AssetType} {entry.VersionPacked} but could not read its version ({readError}).";
                _errors.Add($"Version read failed: {entry.AssetType} {entry.VersionPacked}: {readError}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Download failed: {ex.Message}";
            _errors.Add(ex.Message);
        }
        finally
        {
            IsDownloading = false;
            Progress = 0;
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(Progress));
            ((RelayCommand)ScanCommand).RaiseCanExecuteChanged();
            ((RelayCommand)DownloadCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>Cancels any in-progress scan or download.</summary>
    public void Cancel()
    {
        // A fresh source for the next operation: reusing the cancelled one would cancel every scan
        // and download after the first stop, and disposing it without replacing it made them throw.
        var cancelled = _cts;
        _cts = new CancellationTokenSource();
        cancelled.Cancel();
        cancelled.Dispose();
    }

    public void Dispose()
    {
        _cts.Dispose();
    }

    private static RuntimeKind RuntimeKindFromAsset(string asset) => asset switch
    {
        "dlss" => RuntimeKind.Dlss,
        "dlssg" => RuntimeKind.DlssG,
        "dlssd" => RuntimeKind.DlssR,
        _ => throw new ArgumentOutOfRangeException(nameof(asset), asset, null)
    };

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string? name = null) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
