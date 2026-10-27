<h2 align="center">Kronos Reborn</h2>
<p align="center">Change the DLSS, FSR and XeSS version a game runs, without waiting for the game to update.</p>

> [!WARNING]
> **Early version (0.1.0).** It works, but builds are unsigned and no release has been published yet.
> Windows only for now; Linux is planned.

## What it does

Games ship with a fixed version of NVIDIA DLSS, AMD FSR or Intel XeSS, as DLL files in the game's
folder. Newer versions of those DLLs often look better or run faster. Kronos Reborn finds your games,
shows which versions they carry, and swaps in the version you choose. It keeps a backup of every file
it replaces, so any change can be undone.

## Using it

1. **Refresh** finds your games and the runtime DLLs inside each one.
2. Click a game. Its pane shows each runtime it carries, with the version it runs now.
3. Pick a version, or press **⬆ Newest** to choose the newest version you hold for every game.
4. Check the preview, then press **Apply**.
5. Changed your mind? **Undo** puts the old files back. **Redo** reapplies.

To get versions you don't have yet, open **Download** and press **Get newest for my games**, or pick
any runtime and release.

## Where games are found

| Source | How |
| --- | --- |
| Steam | Every library folder, with covers from Steam's own artwork cache |
| GOG, Ubisoft Connect | Their install records in the registry |
| Epic | The launcher's manifest for each game |
| Battle.net, EA app | Windows' list of installed apps, by publisher |
| Xbox app | Each drive's `.GamingRoot` file |
| **+ Add Folder** | A folder where every subfolder is a game, such as `C:\Games` for repacks or DRM-free installs. New games in it appear on the next refresh. |
| **+ Add Game** | One game, by choosing its folder |

Folders and hand-added games are listed on the **Settings** page and can be removed there. Removing
one never touches its files.

Covers come from Steam's artwork cache. A game with none there, such as a repack or an Epic game, is
looked up by name in Steam's store and its cover downloaded once. That sends the game's name to
Steam; delete the `posters` folder to have covers looked up again.

## Runtimes it handles

| Runtime | File |
| --- | --- |
| DLSS, Frame Generation, Ray Reconstruction | `nvngx_dlss.dll`, `nvngx_dlssg.dll`, `nvngx_dlssd.dll` |
| FSR 3.1 (DirectX 12, Vulkan) | `amd_fidelityfx_dx12.dll`, `amd_fidelityfx_vk.dll` |
| FSR upscaler and frame generation (FidelityFX SDK 2) | `amd_fidelityfx_upscaler_dx12.dll`, `amd_fidelityfx_framegeneration_dx12.dll` |
| XeSS, XeSS Frame Generation, XeSS DX11, XeLL | `libxess.dll`, `libxess_fg.dll`, `libxess_dx11.dll`, `libxell.dll` |

DLLs are found up to ten folders deep, so Unreal Engine games, which keep DLSS eight levels down, are
found too. If a game has several copies of a DLL, all of them are replaced.

## Safety

- **Backups and undo.** Every replaced file is copied first. Undo checks the game still holds what
  was put there before restoring, so it never overwrites a change something else made.
- **Signatures.** A downloaded or imported file is added as trusted only if it is validly signed by
  NVIDIA, AMD or Intel, with revocation checked when the network allows. Anything else needs your
  explicit OK, and is flagged with a warning before it goes into a game.
- **Downloads** come only from the vendors' own GitHub repositories: `NVIDIA/DLSS`,
  `GPUOpen-LibrariesAndSDKs/FidelityFX-SDK` and `intel/xess`.
- **Downgrades** and mismatched DLSS parts are pointed out in the preview before anything changes.
- **Diagnostics** checks the collection, backups and undo history, and can export a zip of the logs
  and data for a bug report.

## Where your data is kept

Everything lives in `%LOCALAPPDATA%\KronosReborn`, as plain JSON you can read:

| | |
| --- | --- |
| `settings.json` | Your settings |
| `catalog.json` | The versions you hold |
| `history\` | Undo history, one file per game |
| `backups\` | Copies of every file a swap replaced |
| `runtimes\` | Imported and downloaded DLLs |
| `posters\` | Covers fetched from Steam's store |
| `manual-games.json`, `game-folders.json` | Games and folders you added |
| `logs\` | The log |

A file that can't be read is set aside as `*.unreadable-*` rather than overwritten, so it can still
be recovered.

## Requirements

Windows 10 (build 19041) or newer, 64-bit. The portable build carries its own .NET, so nothing else
needs installing.

## Building

Needs the .NET 10 SDK.

```powershell
dotnet build .\KronosReborn.sln -c Release
```

Run the tests with `dotnet run`, not `dotnet test` (which reports zero tests with this SDK and xUnit):

```powershell
dotnet run --project .\tests\KronosReborn.Tests -c Release
```

Package with the scripts in `package\`. The installer needs Inno Setup
(`winget install JRSoftware.InnoSetup`):

```powershell
.\build.cmd     # Output\KronosReborn-<version>-portable.zip
.\package.cmd   # Output\KronosReborn-<version>-installer.exe
```

CI restores with the committed `packages.lock.json` in locked mode, checks formatting, builds with
warnings as errors, runs the tests and packages both. After changing a package reference, regenerate
the lock files with `dotnet restore .\KronosReborn.sln --force-evaluate` and commit them.

## Project layout

```
src/KronosReborn/          the window (Avalonia)
src/KronosReborn.Core/     everything else: discovery, swapping, undo, downloads, view models
tests/KronosReborn.Tests/  tests
package/                   build and installer scripts
docs/spec.md               the specification; requirements are cited by number in the code
```

## Licence

Apache-2.0. See [LICENSE](LICENSE). Kronos Reborn is written from scratch and contains no code from
Kronos or DLSS Swapper.

DLSS, FSR, FidelityFX and XeSS are trademarks of their owners. This project is not affiliated with
NVIDIA, AMD or Intel.
