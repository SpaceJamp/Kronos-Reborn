<p align="center">
  <h2 align="center">Kronos Reborn</h2>
  <p align="center">A tool for managing the graphics runtime DLLs inside your game installs.</p>
</p>

> [!WARNING]
> **Under construction. The program finds games and can swap runtimes, but parts of the interface
> are not built yet.**
>
> Steam discovery, runtime version detection, the runtime collection, persistence, swapping and
> undoing all work and are covered by tests. Settings has no interface, only launch and undo use it
> so far, and only Steam is supported. Builds are unsigned and no binaries are published yet.

## What this is

Kronos Reborn places graphics runtime DLLs — `nvngx_dlss.dll`, `nvngx_dlssg.dll`,
`amd_fidelityfx_dx12.dll` and similar — into game installations, so the DLSS, FSR or XeSS version a
game runs can be changed without the game itself updating.

It is a from-scratch implementation. There is no code carried over from any previous version of this
program or from any other project.

## Architectures

**x64 only.** ARM64 was supported and has been taken out again for now, so this is the whole build
matrix rather than the x64 half of it.

It is a small piece of scaffolding rather than a rewrite. `KronosRid` still decides the architecture in
one place, so putting ARM64 back is a value in `Directory.Build.props`, an entry in
`RuntimeIdentifiers`, a CI step, and a regenerated lock file — with no code untangled to make room
for it.

What ARM64 would have meant is worth recording, because it is the reason it was cheap to support and
it is still true if it returns. **The DLLs this program places are x64 binaries, on ARM64 machines
too.** Windows on ARM runs x64 applications under emulation, so games on such a machine are x64 games
and the DLLs in their folders are x64. There is no need for an ARM64 build of a runtime, and none
exists. The practical difference between the architectures is therefore the hardware rather than the
payload — DLSS would not apply on ARM64 at all, since there is no ARM64 DLSS and no NVIDIA GPU on
Windows on ARM, while FSR and XeSS would.

## Status

| | |
| --- | --- |
| Version | `0.1.0` |
| Framework | .NET 10, WPF |
| Architectures | x64 |
| Libraries | Steam only |
| Tests | 177, including architecture guards that fail on whole classes of mistake |

### What works

| Area | State |
| --- | --- |
| Steam library discovery, manifests, deduplication | Working |
| Runtime version detection, in nested folders as well as the root | Working |
| Runtime collection: discovery from games, import, signature checking, removal | Working |
| Persistence: settings, per-game history, atomic writes, corrupt-file recovery | Working |
| Swapping, including runtimes made of several DLLs, with preview and rollback | Working |
| Undoing a swap, repeatably | Working |
| Settings interface | Not built — values load and persist, but nothing edits them |
| Undo through the interface | Launches and undo the newest change for the selected game |
| GOG, Epic, Battle.net and the other libraries | Not started |
| Batch preview and apply from the interface | Engine and tests exist; the selection flow is partial |

The specification is in [`docs/spec.md`](docs/spec.md) and is the authority on what the program is
meant to do. Requirements are cited by number in comments next to the code that satisfies them, and
a test fails if code cites a requirement the specification does not define.

## Requirements

| | |
| --- | --- |
| **OS** | Windows 10 (build 19041) or newer, x64 |
| **CPU** | 64-bit x64 |
| **To build** | .NET 10 SDK |
| **To package** | Inno Setup 6 |

**No 32-bit support.** A 32-bit Windows cannot run a 64-bit program, and the DLLs involved are 64-bit
binaries. A 32-bit Windows installation is the only case where this is fixable, by installing 64-bit
Windows.

## Building

```powershell
dotnet build .\KronosReborn.sln -c Release
```

`KronosRid` is declared once in `Directory.Build.props` and both the runtime identifier and the
platform target are derived from it, so a build cannot produce a binary whose architecture disagrees
with the runtime it was published for.

The committed `packages.lock.json` must cover every architecture listed in `RuntimeIdentifiers`, or
CI fails to restore with `NU1004` rather than quietly re-resolving to something unreviewed. Adding or
removing an entry there therefore means regenerating the lock and committing it in the same change:

```powershell
dotnet restore .\KronosReborn.sln --force-evaluate
```

## Testing

```powershell
dotnet run --project .\tests\KronosReborn.Tests -c Release
```

**Not `dotnet test`.** That reports "Zero tests ran" and exits 5 on this SDK. The tests themselves are
fine — they all pass when the test executable runs — but `dotnet test` reaches xUnit's in-process
console runner rather than its Microsoft.Testing.Platform runner, and the two do not share a protocol
here. The reasons this was not simply fixed are recorded in `KronosReborn.Tests.csproj`; revisit when
the SDK or xUnit ships a combination that works.

### Tests that read the source

`ArchitectureTests` reads the repository's own files rather than calling into it. These are not
style checks: each one exists because the mistake it guards was made and not caught.

- **Every runtime kind is a unit with files, and every kind is covered by the swap tests.** A runtime
  made of several DLLs had one of them overwritten and the other left behind. Any test that happened
  to pick a single-DLL runtime passed while that was true.
- **Only `DataPaths` builds the program's data directory, and no test constructs a service without
  one.** The data directory used to be a static reading the real user profile, so running the suite
  wrote history files into the developer's own account and read them back on the next run.
- **The swap engine is reachable from the view model, and every command it exposes is bound.** The
  swap, undo and batch engines were written, tested and never called: the program could list games
  but could not change anything, and no engine test would have shown that.
- **Every requirement cited in code exists in the specification.**

## Checks that run on every build

| Check | What it prevents |
| --- | --- |
| Warnings are errors, with analysis at the recommended level | A nullable dereference or an obsolete API reaching a release |
| `dotnet format --verify-no-changes` | Formatting drift hiding real diffs |
| Tests run against temporary directories only | Tests depending on, or damaging, machine state |
| Architecture guards above | Whole classes of defect passing unnoticed |

## Packaging

```powershell
winget install JRSoftware.InnoSetup
```

From `package\`:

```powershell
.\build.cmd     # portable zip
.\package.cmd   # installer
```

Produces `Output\KronosReborn-<version>-portable.zip` and
`Output\KronosReborn-<version>-installer.exe`. The name comes from `config.cmd` and nowhere else;
each script prints the path it produced and, under CI, also writes it to `GITHUB_OUTPUT` so the
workflow uploads exactly those files rather than re-deriving the name from a pattern.

Builds are **unsigned**, so Windows SmartScreen will warn. The installer asks whether to install for
the current user only or for everyone; the install location, registry hive and Start Menu entry all
follow that answer.

## Publishing a release

The in-app update check, once it exists, reads the release *title*, which must begin with `v` followed
by the version — `v0.1.0`. It then looks for exactly one attached asset whose name ends with
`-installer.exe`; two matches disable updating rather than guessing.

## Layout

```
src/KronosReborn/          the application
tests/KronosReborn.Tests/  unit tests
package/                   build and packaging scripts, Inno Setup script
.github/workflows/         CI
Directory.Build.props      settings shared by every project
```

## Design decisions worth knowing

These are recorded in the files themselves, with reasoning, because each is a decision that looks
arbitrary until you know why:

- **`Directory.Build.props` injects build metadata from a target, not a bare `ItemGroup`.** A bare
  `ItemGroup` is evaluated at project load, before the SDK assigns `SourceRevisionId`, so the commit
  is written as an empty string while the informational version still shows it.
- **Warnings are errors.** A build that reports warnings still succeeds, so a possible null
  dereference gets committed and is discovered at run time. There were seventeen of these. NuGet
  advisory codes stay warnings, because an unfixable transitive advisory must not be able to stop
  every build.
- **Runtime DLLs are found by searching a few levels down, not by a list of folder names.** Games
  disagree about where they keep these: The Witcher 3 uses `bin\x64_dx12`, Injustice uses
  `Binaries\Retail`, Portal with RTX uses `bin\.trex`. A fixed list of the usual names missed all of
  them, so the program reported no runtime at all in most of a typical library. One search is shared
  by the scanner and the swap planner, so what is detected is what a swap would replace.
- **Two spellings of one folder are one folder.** A default Steam install is recorded in the registry
  as `c:/program files (x86)/steam` and guessed as `C:\Program Files (x86)\Steam`. Comparing those as
  different made the library be scanned three times and every game appear three times over.
- **A runtime kind is a unit that a swap handles whole.** DLSS is one DLL; FSR is two that only work
  as a matched pair. Writing one of them leaves the game with a runtime that never existed in that
  shape, so a swap of any kind is all of its files or none.
- **A history entry records the state the program left behind, not just where the original went.**
  Undoing has to know whether the game still holds what was written into it, which means storing a
  hash of the file after the swap. Comparing the live file against the backup instead cannot work:
  after a successful swap those two necessarily differ, so it rejects every undo.
- **Backups live in the program's data directory, not beside the file they protect.** A file named
  `nvngx_dlss.dll.kronos_bak_...` inside a Steam install is at the mercy of anything that verifies
  game files, shows all files in a launcher, or deletes unknown DLLs.
- **Every build metadata value is nullable.** An unset timestamp converts to the Unix epoch, which
  formats as 1 January 1970 and reads as a real build date rather than as missing data.
- **NuGet auditing covers the whole graph, transitive packages included,** and audit findings stay
  warnings. The default mode only inspects direct references, which is where most advisories slip
  past; but making them errors would let an unfixable transitive advisory block every build.
- **`packages.lock.json` is committed** and CI restores in locked mode.
- **The test project does not use WPF,** so tests cannot accidentally depend on a running
  `Application`.

## Licence

Apache-2.0. See [LICENSE](LICENSE).

`DLSS`, `FSR`, `FidelityFX` and `XeSS` are trademarks of their respective owners. This project is not
affiliated with NVIDIA.
