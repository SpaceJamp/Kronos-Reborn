<p align="center">
  <h2 align="center">Kronos Reborn</h2>
  <p align="center">A tool for managing the graphics runtime DLLs inside your game installs.</p>
</p>

> [!WARNING]
> **Under construction. There is no working application yet.**
>
> This repository currently contains scaffolding only: a buildable WPF project, a test harness, CI and
> an installer script. Nothing scans for games or swaps a DLL. Nothing here is ready to run, and no
> binaries are published.

## What this is

Kronos Reborn places graphics runtime DLLs — `nvngx_dlss.dll`, `nvngx_dlssg.dll`,
`amd_fidelityfx_dx12.dll` and similar — into game installations, so the DLSS, FSR or XeSS version a
game runs can be changed without the game itself updating.

It is a from-scratch implementation. There is no code carried over from any previous version of this
program or from any other project.

## Architectures

The program is built for **x64** and **ARM64**, chosen at build time. Both are produced from one
source tree.

An important detail about what ARM64 support actually means here, because it is not what it might
look like. **The DLLs this program places are x64 binaries, on ARM64 machines too.** Windows on ARM
runs x64 applications under emulation, so games on such a machine are x64 games and the DLLs in their
folders are x64. There is no need for an ARM64 build of a runtime, and none exists.

The practical difference between the two architectures is therefore the hardware, not the payload:

| | x64 | ARM64 |
| --- | --- | --- |
| **The program itself** | native x64 | native ARM64 |
| **Game DLLs it manages** | x64 | x64, via emulation |
| **DLSS** | applies | **not applicable** — no ARM64 DLSS, and no NVIDIA GPU on Windows on ARM |
| **FSR, XeSS** | applies | applies — neither is tied to a GPU vendor |

So ARM64 support is real and worth having, but the runtimes worth offering there are FSR and XeSS
rather than DLSS.

## Status

| | |
| --- | --- |
| Version | `0.1.0`, scaffolding only |
| Framework | .NET 10, WPF |
| Architectures | x64 and ARM64 |
| Features | None yet |
| Tests | 14, covering build metadata, version consistency and specification structure |

## Requirements

| | |
| --- | --- |
| **OS** | Windows 10 (build 19041) or newer, x64 or ARM64 |
| **CPU** | 64-bit, either x64 or ARM64 |
| **To build** | .NET 10 SDK |
| **To package** | Inno Setup 6 |

**No 32-bit support.** A 32-bit Windows cannot run a 64-bit program, and the DLLs involved are 64-bit
binaries. A 32-bit Windows installation is the only case where this is fixable, by installing 64-bit
Windows.

## Building

```powershell
dotnet build .\KronosReborn.sln -c Release                        # x64, the default
dotnet build .\KronosReborn.sln -c Release -p:KronosRid=win-arm64  # ARM64
```

`KronosRid` is declared once in `Directory.Build.props` and both the runtime identifier and the
platform target are derived from it, so a build cannot produce a binary whose architecture disagrees
with the runtime it was published for.

The committed `packages.lock.json` covers **both** architectures. That is what lets CI restore in
locked mode whichever it happens to be building: the lock records which runtimes it covers and
refuses a restore whose set does not match, so a single-architecture lock would fail the other
architecture with `NU1004` rather than quietly re-resolving to something unreviewed.

## Testing

```powershell
dotnet run --project .\tests\KronosReborn.Tests -c Release
```

**Not `dotnet test`.** That reports "Zero tests ran" and exits 5 on this SDK. The tests themselves are
fine — the same fourteen pass when the test executable runs — but `dotnet test` reaches xUnit's
in-process console runner rather than its Microsoft.Testing.Platform runner, and the two do not share a
protocol here. The reasons this was not simply fixed are recorded in `KronosReborn.Tests.csproj`;
revisit when the SDK or xUnit ships a combination that works.

## Packaging

```powershell
winget install JRSoftware.InnoSetup
```

From `package\`:

```powershell
.\build.cmd
```

Produces `Output\Kronos-<version>-portable.zip` and `Output\Kronos-<version>-installer.exe`.

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
