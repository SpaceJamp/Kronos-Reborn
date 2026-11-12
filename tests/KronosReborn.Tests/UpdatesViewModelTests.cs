using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Updates;
using KronosReborn.UI;
using Xunit;

namespace KronosReborn.Tests;

public class UpdatesViewModelTests
{
    private static readonly AvailableUpdate Installable = new(
        "5", new Uri("https://github.com/x/y/releases/tag/v5"),
        new Uri("https://github.com/x/y/releases/download/v5/setup.exe"), "setup.exe", new string('a', 64),
        new Uri("https://github.com/x/y/releases/download/v5/setup.exe.sig"));

    [Fact]
    public async Task AManualCheckSaysWhenUpToDate()
    {
        var vm = Vm(new UpdateCheckResult(null, null));

        await vm.CheckAsync(automatic: false);

        Assert.False(vm.HasUpdate);
        Assert.Equal("You have the newest build.", vm.StatusText);
    }

    [Fact]
    public async Task AnUpdateIsShownWithItsVersion()
    {
        var vm = Vm(new UpdateCheckResult(Installable, null));

        await vm.CheckAsync(automatic: true);

        Assert.True(vm.HasUpdate);
        Assert.Equal("Build 5 is available.", vm.StatusText);
    }

    [Fact]
    public async Task AnAutomaticCheckThatFailsStaysQuiet()
    {
        var vm = Vm(new UpdateCheckResult(null, "GitHub answered 403"));

        await vm.CheckAsync(automatic: true);
        Assert.Equal(string.Empty, vm.StatusText);

        await vm.CheckAsync(automatic: false);
        Assert.StartsWith("Could not check for updates", vm.StatusText);
    }

    [Fact]
    public async Task InstallDownloadsThenRunsTheInstaller()
    {
        var window = new FakeWindow();
        var vm = Vm(new UpdateCheckResult(Installable, null), window, download: (_, _, _) => Task.FromResult(@"C:\t\setup.exe"));
        await vm.CheckAsync(automatic: true);

        Assert.True(vm.InstallCommand.CanExecute(null));
        await vm.InstallAsync();

        Assert.Equal(@"C:\t\setup.exe", window.RanInstaller);
    }

    [Fact]
    public async Task InstallIsRefusedWhileTheWindowMayNotClose()
    {
        var window = new FakeWindow();
        var downloads = 0;
        var vm = Vm(new UpdateCheckResult(Installable, null), window,
            download: (_, _, _) => { downloads++; return Task.FromResult("x"); }, canClose: () => false);
        await vm.CheckAsync(automatic: true);

        // Quitting part way through a swap loses its undo record, so the update waits.
        await vm.InstallAsync();

        Assert.Equal(0, downloads);
        Assert.Null(window.RanInstaller);
    }

    [Fact]
    public async Task InstallIsRefusedIfASwapStartsDuringTheDownload()
    {
        var window = new FakeWindow();
        var swapRunning = false;
        var vm = Vm(new UpdateCheckResult(Installable, null), window,
            download: (_, _, _) => { swapRunning = true; return Task.FromResult("setup.exe"); },
            canClose: () => !swapRunning);
        await vm.CheckAsync(automatic: true);

        // Apply stays usable while the download runs; the installer would then kill the swap.
        await vm.InstallAsync();

        Assert.Null(window.RanInstaller);
        Assert.Contains("swap", vm.StatusText);
    }

    [Fact]
    public async Task AnInstallerThatWillNotStartIsReported()
    {
        var window = new FakeWindow { StartFails = true };
        var vm = Vm(new UpdateCheckResult(Installable, null), window);
        await vm.CheckAsync(automatic: true);

        await vm.InstallAsync();

        Assert.StartsWith("Could not start the installer", vm.StatusText);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Kronos Reborn\", "/ALLUSERS")]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\Kronos Reborn\", "/CURRENTUSER")]
    public void TheInstallerIsToldHowThisCopyWasInstalled(string appFolder, string mode)
    {
        var arguments = UpdatesViewModel.InstallerArguments(appFolder, @"C:\Program Files");

        Assert.Contains(mode, arguments);
        Assert.Contains("/update=1", arguments);
        Assert.Contains("/SILENT", arguments);
    }

    [Fact]
    public async Task ABadDownloadIsReportedAndNothingRuns()
    {
        var window = new FakeWindow();
        var vm = Vm(new UpdateCheckResult(Installable, null), window,
            download: (_, _, _) => throw new InvalidDataException("digest"));
        await vm.CheckAsync(automatic: true);

        await vm.InstallAsync();

        Assert.Null(window.RanInstaller);
        Assert.Equal("The update failed a security check, so it was deleted and not run: digest", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData("disposed")]
    [InlineData("cancelled")]
    public async Task ClosingTheWindowMidDownloadIsNotACrash(string how)
    {
        // Closing the window disposes the HTTP client under a running download.
        var window = new FakeWindow();
        var vm = Vm(new UpdateCheckResult(Installable, null), window,
            download: (_, _, _) => how == "disposed"
                ? throw new ObjectDisposedException("HttpClient")
                : throw new OperationCanceledException());
        await vm.CheckAsync(automatic: true);

        await vm.InstallAsync();

        Assert.Null(window.RanInstaller);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task APortableCopyIsOnlyOfferedThePage()
    {
        var window = new FakeWindow();
        var vm = Vm(new UpdateCheckResult(Installable, null), window, isInstalled: false);
        await vm.CheckAsync(automatic: true);

        Assert.False(vm.CanInstall);
        Assert.False(vm.InstallCommand.CanExecute(null));

        vm.OpenPageCommand.Execute(null);
        Assert.Equal(Installable.PageUrl, window.OpenedUrl);
    }

    [Fact]
    public void UpdateChecksAreOnByDefaultAndSaved()
    {
        Assert.True(Settings.Default.CheckForUpdates);

        using var temp = new TemporaryDirectory();
        var paths = new DataPaths(temp.Root);
        using (var persistence = new PersistenceService(paths))
        {
            persistence.SaveSettings(Settings.Default.With(checkForUpdates: false));
        }

        using var reloaded = new PersistenceService(paths);
        Assert.False(reloaded.Load().Settings.CheckForUpdates);
    }

    [Fact]
    public void ACopyWithAnUninstallerIsAnInstalledCopy()
    {
        using var temp = new TemporaryDirectory();
        Assert.False(UpdatesViewModel.IsInstalledCopy(temp.Root));

        File.WriteAllText(Path.Combine(temp.Root, "unins000.exe"), "");
        Assert.True(UpdatesViewModel.IsInstalledCopy(temp.Root));
    }

    private static UpdatesViewModel Vm(
        UpdateCheckResult result,
        FakeWindow? window = null,
        Func<AvailableUpdate, IProgress<double>, CancellationToken, Task<string>>? download = null,
        Func<bool>? canClose = null,
        bool isInstalled = true) =>
        new(_ => Task.FromResult(result),
            download ?? ((_, _, _) => Task.FromResult("setup.exe")),
            window ?? new FakeWindow(),
            canClose ?? (() => true),
            isInstalled);

    private sealed class FakeWindow : IFilePicker
    {
        public string? RanInstaller { get; private set; }
        public Uri? OpenedUrl { get; private set; }

        public Task OpenUrlAsync(Uri url)
        {
            OpenedUrl = url;
            return Task.CompletedTask;
        }

        public bool StartFails { get; init; }

        public void RunInstallerAndExit(string installerPath)
        {
            if (StartFails)
            {
                // What Windows reports when antivirus quarantines an unsigned download.
                throw new System.ComponentModel.Win32Exception(225);
            }

            RanInstaller = installerPath;
        }

        public Task<string?> AskForRuntimeFileAsync() => throw new NotSupportedException();
        public Task<string?> AskWhereToSaveDiagnosticsAsync(string suggestedName) => throw new NotSupportedException();
        public Task<string?> AskForGameFolderAsync(string title) => throw new NotSupportedException();
        public Task SetClipboardTextAsync(string text) => throw new NotSupportedException();
        public Task OpenFolderAsync(string path) => throw new NotSupportedException();
    }
}
