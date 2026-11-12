using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using KronosReborn.Data.Logging;
using KronosReborn.Data.Updates;

namespace KronosReborn.UI;

/// <summary>
/// Whether a newer release is out, and getting it: the installer for an installed copy, or the
/// release page for a portable one.
/// </summary>
public sealed class UpdatesViewModel : INotifyPropertyChanged
{
    private readonly Func<CancellationToken, Task<UpdateCheckResult>> _check;
    private readonly Func<AvailableUpdate, IProgress<double>, CancellationToken, Task<string>> _download;
    private readonly IFilePicker? _window;
    private readonly Func<bool> _canClose;
    private readonly bool _isInstalled;

    private AvailableUpdate? _update;
    private bool _isBusy;
    private double _progress;
    private string _statusText = string.Empty;

    public UpdatesViewModel(
        Func<CancellationToken, Task<UpdateCheckResult>> check,
        Func<AvailableUpdate, IProgress<double>, CancellationToken, Task<string>> download,
        IFilePicker? window,
        Func<bool> canClose,
        bool isInstalled)
    {
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _window = window;
        _canClose = canClose ?? throw new ArgumentNullException(nameof(canClose));
        _isInstalled = isInstalled;

        CheckCommand = new RelayCommand(async _ => await CheckAsync(automatic: false), _ => !IsBusy);
        InstallCommand = new RelayCommand(async _ => await InstallAsync(), _ => CanInstall && !IsBusy && _window is not null);
        OpenPageCommand = new RelayCommand(
            async _ =>
            {
                if (_update is not null && _window is not null)
                {
                    await _window.OpenUrlAsync(_update.PageUrl);
                }
            },
            _ => HasUpdate && _window is not null);
    }

    /// <summary>Whether this copy was installed by the installer, which leaves an uninstaller beside it.</summary>
    public static bool IsInstalledCopy(string appFolder) => File.Exists(Path.Combine(appFolder, "unins000.exe"));

    /// <summary>
    /// The installer's command line for an update: silent, relaunching the app after, and in the same
    /// mode as this copy was installed.
    /// </summary>
    /// <remarks>
    /// The mode is passed rather than left to Inno to remember, because a copy in Program Files
    /// upgraded per-user would leave the old one in place and be offered the update forever.
    /// </remarks>
    public static string InstallerArguments(string appFolder, string programFilesFolder)
    {
        var forEveryone = !string.IsNullOrEmpty(programFilesFolder)
            && Path.GetFullPath(appFolder).StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFilesFolder)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);

        return $"/SILENT /SUPPRESSMSGBOXES /NORESTART {(forEveryone ? "/ALLUSERS" : "/CURRENTUSER")} /update=1";
    }

    public ICommand CheckCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand OpenPageCommand { get; }

    /// <summary>The newer release, or null.</summary>
    public AvailableUpdate? Update
    {
        get => _update;
        private set
        {
            _update = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanOnlyOpenPage));
            RaiseCommandStates();
        }
    }

    public bool HasUpdate => _update is not null;

    /// <summary>Whether the update can be installed from here: an installed copy and a checkable installer.</summary>
    public bool CanInstall => _isInstalled && _update?.CanInstall == true;

    /// <summary>Whether there is an update that can only be fetched from its page.</summary>
    public bool CanOnlyOpenPage => HasUpdate && !CanInstall;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            RaiseCommandStates();
        }
    }

    /// <summary>Download progress, 0 to 100.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            _progress = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Asks whether a newer release is out.</summary>
    /// <param name="automatic">
    /// The check made at startup. It says nothing when it cannot reach GitHub: being offline is not
    /// something to be told about every launch.
    /// </param>
    public async Task CheckAsync(bool automatic)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (!automatic)
            {
                StatusText = "Checking for updates…";
            }

            var result = await _check(CancellationToken.None);
            Update = result.Update;

            StatusText = result switch
            {
                { Update: { } u } => $"Build {u.Version} is available.",
                { Error: { } e } when !automatic => $"Could not check for updates: {e}",
                { Error: { } } => string.Empty,
                _ when !automatic => "You have the newest build.",
                _ => string.Empty,
            };

            if (result.Error is { } error)
            {
                Log.Instance.Warning($"Update check failed: {error}", category: "Updates");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Downloads and checks the installer, then hands over to it and closes.</summary>
    public async Task InstallAsync()
    {
        if (!CanInstall || IsBusy || _window is null || _update is not { } update)
        {
            return;
        }

        // A swap closed part way through loses its undo record; the window says so itself.
        if (!_canClose())
        {
            return;
        }

        IsBusy = true;
        Progress = 0;
        string? installer = null;
        try
        {
            StatusText = "Downloading the update…";
            installer = await _download(update, new Progress<double>(p => Progress = p), CancellationToken.None);
        }
        catch (InvalidDataException ex)
        {
            StatusText = $"The update failed a security check, so it was deleted and not run: {ex.Message}";
            Log.Instance.Error(ex, "Update failed a security check", category: "Updates");
        }
        // Disposed and cancelled: the window was closed under the download.
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            StatusText = $"Could not download the update: {ex.Message}";
            Log.Instance.Error(ex, "Update download failed", category: "Updates");
        }
        finally
        {
            IsBusy = false;
        }

        if (installer is null)
        {
            return;
        }

        // Asked again: Apply stays usable during the download, and the installer closes the app by
        // force, so a swap started meanwhile would lose its undo record.
        if (!_canClose())
        {
            StatusText = "The update is downloaded. Install it once the swap or undo has finished.";
            return;
        }

        try
        {
            Log.Instance.Information($"Installing update {update.Version}", category: "Updates");
            _window.RunInstallerAndExit(installer);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // Antivirus often quarantines an unsigned download just written to the temp folder.
            StatusText = $"Could not start the installer: {ex.Message}";
            Log.Instance.Error(ex, "Update installer could not start", category: "Updates");
        }
    }

    private void RaiseCommandStates()
    {
        ((RelayCommand)CheckCommand).RaiseCanExecuteChanged();
        ((RelayCommand)InstallCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenPageCommand).RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
