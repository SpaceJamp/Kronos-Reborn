# Update System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kronos Reborn tells the user when a newer release is out on GitHub and, for an installed copy, downloads and runs the new installer in one click.

**Architecture:** A core `UpdateChecker` reads `releases/latest` from the GitHub API, compares its tag with the running version, and downloads the installer asset, checking it against the SHA-256 digest GitHub reports. An `UpdatesViewModel` holds the state and commands, and the window supplies the two things only it can do: opening a URL, and starting the installer then closing. The Inno Setup script closes and relaunches the app around a silent upgrade.

**Tech Stack:** .NET 10, C#, Avalonia 11.3.22, xUnit v3, Inno Setup 6, GitHub REST API (unauthenticated).

**Spec:** None exists for updates; `docs/spec.md` does not cover them. The decisions below are this plan's spec.

## Decisions

- Source of truth: `GET https://api.github.com/repos/SpaceJamp/Kronos-Reborn/releases/latest`. It already skips drafts and pre-releases. Fields used: `tag_name`, `html_url`, and `assets[].name`, `assets[].browser_download_url`, `assets[].digest`.
- Running version: `BuildInfo.InformationalVersion` up to the first `+` (for example `0.1.0+abc1234` gives `0.1.0`). Tag version: `tag_name` without a leading `v`. Newer means `VersionNumber.IsOlder(current, tag)`.
- Installer asset: the one whose name ends with `-installer.exe` (ordinal, ignore case), as `package.cmd` names it.
- Integrity: the asset's `digest` must be present and of the form `sha256:<64 hex>`. The downloaded file's SHA-256 must equal it. Otherwise the file is deleted and nothing runs.
- Download: to `%TEMP%\KronosReborn-update\<asset name>`, written as `<name>.partial`, renamed only after the digest matches.
- Installed or portable: installed when `unins000.exe` sits next to the running exe (`AppContext.BaseDirectory`). Portable copies are only offered the release page, never the installer.
- Running the installer: `<path> /SILENT /SUPPRESSMSGBOXES /NORESTART /update=1`. The window then shuts down. Inno remembers the previous per-user or per-machine choice (`UsePreviousPrivileges` defaults to yes). A machine-wide install still shows the Windows UAC prompt, which is expected and not suppressed.
- When: once on startup after the first refresh, if the setting is on, plus a **Check for updates** button in Settings. No background timer.
- Setting: `Settings.CheckForUpdates`, default `true`.
- Not doing: skipping a version, update channels, delta updates, automatic installs without a click, code signing.

## Global Constraints

- No new package references. HTTP via the existing `VendorDownloader.CreateClient()` (`User-Agent: KronosReborn`).
- Warnings are errors; `dotnet format KronosReborn.sln --verify-no-changes` must pass; restore stays `--locked-mode`.
- Tests run with `dotnet run --project .\tests\KronosReborn.Tests -c Release`, not `dotnet test`.
- Core code lives in `src/KronosReborn.Core` (net10.0, no Avalonia). Window-only work goes through `IFilePicker`.
- Network calls are made only to `api.github.com` and the asset's `browser_download_url` (GitHub then redirects to its download host).
- Nothing personal is sent: no query parameters, no headers beyond the User-Agent.
- User-facing text is plain sentences, as elsewhere in the app.

## Review Focus

1. **No network, GitHub down, or rate limited (HTTP 403/429).** The startup check says nothing and the app carries on. The manual check says "Could not check for updates: …". Test in Task 1 (`CheckReportsFailureWithoutThrowingOnHttpError`) and Task 2 (`AnAutomaticCheckThatFailsStaysQuiet`).
2. **A truncated or tampered download.** The digest doesn't match, the file is deleted and not run, and the status says why. Test in Task 1 (`ADownloadWhoseDigestDiffersIsDeletedAndRefused`).
3. **A release with no installer asset, or an asset with no digest.** The update is still reported, but only the release page is offered. Test in Task 1 (`AReleaseWithoutAUsableInstallerOffersOnlyThePage`).
4. **Install clicked while a swap or undo is running.** Refused with the existing "still running" message, because quitting mid-swap loses undo records. Test in Task 2 (`InstallIsRefusedWhileTheWindowMayNotClose`).
5. **A portable copy.** The install command is never offered; **Open release page** is. Test in Task 2 (`APortableCopyIsOnlyOfferedThePage`).

---

### Task 1: Check GitHub and download the installer

**Files:**
- Create: `src/KronosReborn.Core/Data/Updates/UpdateChecker.cs`
- Test: `tests/KronosReborn.Tests/UpdateCheckerTests.cs`

**Interfaces:**
- Consumes: `VersionNumber.IsOlder(string? a, string? b)` from `Data/Runtimes/VersionNumber.cs`.
- Produces:
  - `public sealed record AvailableUpdate(string Version, Uri PageUrl, Uri? InstallerUrl, string? InstallerName, string? InstallerSha256)` with `public bool CanInstall => InstallerUrl is not null && InstallerSha256 is not null;`
  - `public sealed record UpdateCheckResult(AvailableUpdate? Update, string? Error)` (`Update` is null and `Error` is null when up to date).
  - `public sealed class UpdateChecker(HttpClient http, string currentVersion)`, with:
    - `public const string LatestReleaseUrl = "https://api.github.com/repos/SpaceJamp/Kronos-Reborn/releases/latest";`
    - `public static string CurrentVersion()`: `BuildInfo.InformationalVersion` up to `+`.
    - `public Task<UpdateCheckResult> CheckAsync(CancellationToken token)`
    - `public Task<string> DownloadAsync(AvailableUpdate update, string folder, IProgress<double>? progress, CancellationToken token)`: returns the final path. Throws `InvalidOperationException` when `!update.CanInstall`, and `InvalidDataException` on a digest mismatch, after deleting the partial file.

- [ ] **Step 1: Write the failing tests**

Use a fake `HttpMessageHandler` like `FakeGitHub` in `VendorDownloaderTests.cs`: a URL-to-response table, with 404 otherwise. Build the release JSON with `System.Text.Json` from anonymous objects using GitHub's field names. Tests:

```csharp
[Fact] public async Task ANewerTagIsReportedWithItsInstaller()
// tag "v0.2.0", current "0.1.0", asset "KronosReborn-0.2.0-installer.exe" with digest "sha256:" + 64 hex
// Assert: result.Error null; result.Update!.Version == "0.2.0"; CanInstall true;
//         InstallerSha256 == the 64 hex (lower case, no "sha256:" prefix); PageUrl == html_url

[Theory] public async Task TheSameOrAnOlderTagIsNoUpdate(string tag)
// InlineData("v0.1.0"), ("v0.0.9"), ("0.1.0") with current "0.1.0": Update null, Error null

[Theory] public async Task AReleaseWithoutAUsableInstallerOffersOnlyThePage(string case_)
// cases: no assets; installer asset with digest null; digest "md5:abc"
// Assert: Update not null, CanInstall false, PageUrl set

[Theory] public async Task CheckReportsFailureWithoutThrowingOnHttpError(string case_)
// cases: 403, 500, and the handler throwing HttpRequestException; also a body that is not JSON
// Assert: Update null; Error not null and non-empty

[Fact] public async Task ADownloadMatchingItsDigestIsKept()
// asset bytes "installer" with their real SHA-256; DownloadAsync into a TemporaryDirectory
// Assert: File.ReadAllText(path) == "installer"; no "*.partial" left in the folder

[Fact] public async Task ADownloadWhoseDigestDiffersIsDeletedAndRefused()
// digest of other bytes; Assert.ThrowsAsync<InvalidDataException>; folder has no files left

[Fact] public void TheRunningVersionDropsBuildMetadata()
// UpdateChecker.CurrentVersion() does not contain '+'
```

- [ ] **Step 2: Run the tests and see them fail**

Run: `dotnet build KronosReborn.sln -c Release`
Expected: build fails, with `UpdateChecker` not found.

- [ ] **Step 3: Implement `UpdateChecker` in `src/KronosReborn.Core/Data/Updates/UpdateChecker.cs`**

Parse with `JsonDocument`. Catch `HttpRequestException`, `TaskCanceledException` (but not when `token` was cancelled), `JsonException` and `KeyNotFoundException` into `Error`. Use a non-success status as `Error = $"GitHub answered {(int)status}"`. Hash with `SHA256.HashDataAsync` on the finished `.partial` file, and compare with `Convert.ToHexStringLower`. Report progress from `Content-Length` when present.

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet build KronosReborn.sln -c Release && dotnet run --project .\tests\KronosReborn.Tests -c Release --no-build`
Expected: all pass, including the new `UpdateCheckerTests`.

- [ ] **Step 5: Commit**

```bash
git add src/KronosReborn.Core/Data/Updates/UpdateChecker.cs tests/KronosReborn.Tests/UpdateCheckerTests.cs
git commit -m "Check GitHub for a newer release and download its installer, verified by digest"
```

---

### Task 2: Update state, setting and commands

**Files:**
- Create: `src/KronosReborn.Core/UI/UpdatesViewModel.cs`
- Modify: `src/KronosReborn.Core/Data/Persistence/Settings.cs` (add `bool CheckForUpdates = true` as the last record parameter, plus `bool? checkForUpdates = null` in `With`)
- Modify: `src/KronosReborn.Core/UI/IFilePicker.cs` (two members below)
- Modify: `src/KronosReborn.Core/UI/ViewModels.cs` (construct and expose `Updates`; add the `CheckForUpdates` setting property in the same pattern as `AllowUntrustedRuntimes`)
- Modify: `src/KronosReborn/MainWindow.axaml.cs` (stubs for the two new `IFilePicker` members, replaced in Task 3)
- Test: `tests/KronosReborn.Tests/UpdatesViewModelTests.cs`

**Interfaces:**
- Consumes: `UpdateChecker`, `AvailableUpdate` and `UpdateCheckResult` from Task 1; `MainViewModel.CanClose()`.
- Produces:
  - In `IFilePicker`: `Task OpenUrlAsync(Uri url);` and `void RunInstallerAndExit(string installerPath);`
  - `public sealed class UpdatesViewModel : INotifyPropertyChanged`, constructed as `UpdatesViewModel(Func<CancellationToken, Task<UpdateCheckResult>> check, Func<AvailableUpdate, IProgress<double>, CancellationToken, Task<string>> download, IFilePicker? window, Func<bool> canClose, bool isInstalled)`, with:
    - `Task CheckAsync(bool automatic)`
    - `ICommand CheckCommand` (manual), `ICommand InstallCommand`, `ICommand OpenPageCommand`
    - `AvailableUpdate? Update`, `bool HasUpdate`, `bool CanInstall` (`isInstalled && Update?.CanInstall == true`), `bool IsBusy`, `double Progress`, `string StatusText`
    - `public static bool IsInstalledCopy(string appFolder) => File.Exists(Path.Combine(appFolder, "unins000.exe"));`
  - `MainViewModel.Updates` (`UpdatesViewModel`) and `MainViewModel.CheckForUpdates` (`bool`, saved to settings).

- [ ] **Step 1: Write the failing tests**

Construct the view model with lambdas and a small fake `IFilePicker` that records `OpenUrlAsync` and `RunInstallerAndExit` calls (other members throw `NotSupportedException`). Tests:

```csharp
[Fact] public async Task AManualCheckSaysWhenUpToDate()
// check returns (null, null); await CheckAsync(automatic: false)
// Assert: HasUpdate false; StatusText == "You have the newest version."

[Fact] public async Task AnUpdateIsShownWithItsVersion()
// check returns Update "0.2.0"; Assert: HasUpdate; StatusText contains "0.2.0"

[Fact] public async Task AnAutomaticCheckThatFailsStaysQuiet()
// check returns (null, "GitHub answered 403"); CheckAsync(automatic: true)
// Assert: StatusText == "" ; then CheckAsync(automatic: false) → StatusText starts with "Could not check for updates"

[Fact] public async Task InstallDownloadsThenRunsTheInstaller()
// isInstalled true, update CanInstall; download returns @"C:\t\setup.exe"; InstallCommand.Execute(null), await completion
// Assert: fake.RanInstaller == @"C:\t\setup.exe"

[Fact] public async Task InstallIsRefusedWhileTheWindowMayNotClose()
// canClose returns false; Assert: InstallCommand.CanExecute(null) true but executing leaves RanInstaller null
// and the download lambda was never called

[Fact] public async Task ABadDownloadIsReportedAndNothingRuns()
// download throws InvalidDataException("digest"); Assert: RanInstaller null; StatusText contains "did not match"

[Fact] public void APortableCopyIsOnlyOfferedThePage()
// isInstalled false, update CanInstall true; Assert: CanInstall false; InstallCommand.CanExecute(null) false;
// OpenPageCommand.Execute → fake.OpenedUrl == update.PageUrl

[Fact] public void UpdateChecksAreOnByDefaultAndSaved()
// Settings.Default.CheckForUpdates true; round-trip through PersistenceService as in QualityOfLifeTests
```

- [ ] **Step 2: Run the tests and see them fail**

Run: `dotnet build KronosReborn.sln -c Release`
Expected: build fails, with `UpdatesViewModel` not found.

- [ ] **Step 3: Implement**

`InstallCommand` checks `canClose()` first, and the download only after that. Status texts are exactly:
- up to date: `"You have the newest version."`
- update: `$"Version {v} is available."`
- failed check: `$"Could not check for updates: {error}"`
- bad digest: `"The download did not match what GitHub published, so it was deleted and not run."`
- downloading: `"Downloading the update…"`

In `MainViewModel`, construct it with `new UpdateChecker(_http, UpdateChecker.CurrentVersion())`, a download folder of `Path.Combine(Path.GetTempPath(), "KronosReborn-update")`, `canClose: CanClose`, and `isInstalled: UpdatesViewModel.IsInstalledCopy(AppContext.BaseDirectory)`. Implement the two new `IFilePicker` members in `MainWindow` in Task 3; until then the build needs them, so Step 3 adds stubs that throw `NotImplementedException` in `MainWindow.axaml.cs`.

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet build KronosReborn.sln -c Release && dotnet run --project .\tests\KronosReborn.Tests -c Release --no-build`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/KronosReborn.Core tests/KronosReborn.Tests/UpdatesViewModelTests.cs src/KronosReborn/MainWindow.axaml.cs
git commit -m "Add update state, the update-check setting and install commands"
```

---

### Task 3: Show updates in the window

**Files:**
- Modify: `src/KronosReborn/MainWindow.axaml.cs` (replace the Task 2 stubs; run the startup check)
- Modify: `src/KronosReborn/MainWindow.axaml` (status bar button; Settings section)

**Interfaces:**
- Consumes: `MainViewModel.Updates` and `MainViewModel.CheckForUpdates` from Task 2.
- Produces: nothing used later.

- [ ] **Step 1: Implement the window members**

- `OpenUrlAsync(Uri url)`: `await Launcher.LaunchUriAsync(url)`.
- `RunInstallerAndExit(string path)`: `Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /update=1") { UseShellExecute = true })`, then `Close()`. The existing `Closing` handler saves settings and disposes as usual.
- In the `Opened` handler, after `await vm.RefreshAsync()`: `if (vm.CheckForUpdates) await vm.Updates.CheckAsync(automatic: true);`

- [ ] **Step 2: Add the UI**

- Status bar: a button between the status text and **Undo last apply**, with `Content="{Binding Updates.Update.Version, StringFormat='Update to {0}'}"`, `IsVisible="{Binding Updates.HasUpdate}"` and `Command="{Binding Updates.InstallCommand}"`. When `Updates.CanInstall` is false it binds `Updates.OpenPageCommand` instead: use two buttons with opposite `IsVisible`. The second reads `"Get {0}"`.
- Settings, after **Hidden games**: a heading `Updates`; a CheckBox `Check for updates when Kronos Reborn starts` bound to `CheckForUpdates`; a button `Check now` (`Updates.CheckCommand`); a `TextBlock` for `Updates.StatusText`; and a `ProgressBar` (`Maximum="100"`, `Value="{Binding Updates.Progress, Mode=OneWay}"`, `IsVisible="{Binding Updates.IsBusy}"`).

- [ ] **Step 3: Build and run the tests**

Run: `dotnet build KronosReborn.sln -c Release && dotnet run --project .\tests\KronosReborn.Tests -c Release --no-build`
Expected: Build succeeded, and all tests pass. Compiled bindings catch misspelt property paths at build time.

- [ ] **Step 4: Check by hand**

Run the app built with `/p:Version=0.0.9`: `dotnet run --project .\src\KronosReborn -c Release -p:Version=0.0.9`.
Expected: within seconds of the first refresh, the status bar shows **Get 0.1.0** (a `dotnet run` copy has no `unins000.exe`, so it counts as portable), and clicking it opens the v0.1.0 release page. Settings → **Check now** says "Version 0.1.0 is available."

- [ ] **Step 5: Commit**

```bash
git add src/KronosReborn/MainWindow.axaml src/KronosReborn/MainWindow.axaml.cs
git commit -m "Show available updates in the status bar and Settings"
```

---

### Task 4: Installer upgrade path, and docs

**Files:**
- Modify: `package/Installer.iss`
- Modify: `README.md` (the Safety list, and the privacy sentence near covers)

**Interfaces:**
- Consumes: the `/update=1` parameter passed in Task 3.
- Produces: nothing used later.

- [ ] **Step 1: Close and relaunch around a silent upgrade**

In `[Setup]`: `CloseApplications=force` and `RestartApplications=no`. Replace the `; No [Code] section yet.` comment block's second bullet. That bullet is now handled by `CloseApplications`; keep the first bullet about the data directory. Add:

```ini
[Run]
Filename: "{app}\{#AppExeName}"; Flags: nowait runasoriginaluser; Check: IsUpdate

[Code]
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:update|0}') = '1';
end;
```

The existing `[Run]` line keeps `skipifsilent`, so a normal install is unchanged.

- [ ] **Step 2: Document it**

- README Safety list: `- **Updates** are checked against GitHub's latest release when the app starts (turn this off in Settings). An installed copy downloads the new installer, checks it against the SHA-256 GitHub publishes, and runs it; a portable copy opens the release page.`
- README covers paragraph: after "That sends the game's name to Steam;", add a sentence saying the update check contacts `api.github.com` and sends nothing about you or your games.

- [ ] **Step 3: Build the installer**

Run: `cd package && cmd /c build.cmd && cmd /c package.cmd`
Expected: `Output\KronosReborn-0.1.0-installer.exe` is built with no Inno errors.

- [ ] **Step 4: Check the upgrade by hand**

1. Install the v0.1.0 release installer, for the current user only.
2. Build an installer with `-p:Version=0.0.9`, install it over the top, and open the app.
3. Expected: the status bar shows **Update to 0.1.0**. Clicking it downloads, closes the app, upgrades silently, and reopens the app.
4. Expected: Settings and history survive the upgrade.

- [ ] **Step 5: Commit**

```bash
git add package/Installer.iss README.md
git commit -m "Relaunch after a silent upgrade, and document the update check"
```
