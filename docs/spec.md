# Kronos Reborn — Specification

## About this document

This describes **what the program must do**, not how it is built. It is written from requirements.

That is deliberate. No part of any previous implementation of this program was consulted while
writing it, and nothing here should be read as a description of one. Where a previous version of this
program is mentioned at all, it is as a product that once existed under a different name, not as a
source of design.

If a requirement here cannot be tested, it should be rewritten until it can be. Every numbered item
below is intended to be directly verifiable, and most map onto a test.

Terms are defined in [Concepts](#concepts) and used consistently throughout.

---

## Purpose

Games ship with a particular version of a graphics runtime — DLSS, FSR, XeSS — baked into the game
folder. The game then depends on that exact version, and there is often no way to update it other than
waiting for the developer.

This program changes it. It finds games installed on the machine, reports which runtime version each
one is currently running, offers the other versions, and swaps them in — after backing up what was
there so the change can be undone.

It is a maintenance tool for someone who owns the machine it runs on. It is not a distribution
system, not a launcher, and not a game manager.

---

## Concepts

| Term | Meaning |
| --- | --- |
| **Runtime** | A graphics runtime shipped as one or more DLLs a game loads. DLSS, FSR and XeSS are the targets. |
| **Runtime version** | One version of a runtime, identified by its version number. For example "DLSS 3.5". |
| **Library** | A source of games on this machine: Steam, GOG, Epic, Battle.net, Ubisoft Connect, Xbox, or manually added. Also called a store or platform. |
| **Game** | One installed game, identified by where it is installed and which library reported it. |
| **Swap** | Replacing one or more of a game's runtime DLLs with those from a different runtime version. |
| **Backup** | A saved copy of a file a swap overwrote, kept so the swap can be undone. |
| **Import** | Adding a runtime version obtained from outside the program into the local collection. |

---

## Scope

### In scope

- Detecting games from installed libraries
- Reporting the runtime version each game currently uses
- Swapping a game's runtime version
- Undoing swaps
- Warning before a swap that is likely to break a game
- Applying a change across many games at once, after showing what it would do

### Out of scope

- Adding runtime support to a game that does not already support it. This program changes which
  version a game runs; it cannot make an unsupported game work.
- Launching games, managing save data, or installing games
- Modifying anything outside the game's own installation folder
- Any server this program depends on. Everything it needs is either on the machine or the user
  provides it.

---

## Requirements

### Discovery

- **R1.** The program detects installed libraries and reports which are present.
- **R2.** For each enabled library, it enumerates the games that library reports.
- **R3.** A game that appears in more than one library is presented once, with the other library
  recorded rather than discarded.
- **R4.** A library that is not installed, or whose configuration cannot be read, is reported as such
  rather than causing a failure.
- **R5.** A library configuration that cannot be parsed must not discard games already discovered
  from other libraries.
- **R6.** Games can be added manually by the user, specifying a name and an installation folder.
- **R7.** Games that vanish from disk are reported as missing rather than silently retained forever.
- **R8.** Discovery is safe to interrupt. A cancelled or interrupted discovery leaves the program in a
  usable state and does not discard previously known games.

### Reporting

- **R9.** For each game, the program reports the runtime version it currently has installed.
- **R10.** A version that cannot be read is reported as unknown. It is never guessed at.
- **R11.** The user can see when a game was last changed by this program.
- **R12.** The list can be grouped by library or shown as one list, and the choice persists.

### The runtime collection

- **R13.** The program holds a collection of runtime versions it can install into games.
- **R14.** The collection is populated by discovering versions already present on this machine.
- **R15.** The user can import a runtime version from a file obtained elsewhere.
- **R16.** An imported runtime is checked for a valid digital signature before being accepted, unless
  the user has explicitly enabled accepting untrusted ones.
- **R17.** The source of a runtime version is recorded, so the user can tell where it came from.
- **R18.** Removing a runtime version from the collection does not remove it from any game.
- **R48.** A runtime version found inside an installed game is added to the collection without the
  user asking, and is marked as discovered rather than imported, because its trustworthiness comes
  from where it was found rather than from having been checked.
- **R49.** Games that were installed before the program was ever run are still usable as a source of
  runtime versions, with no prior discovery by the user required.

### Swapping

- **R19.** A swap replaces a game's runtime DLLs with those of a chosen runtime version.
- **R20.** A swap is refused if the game is currently being modified by another operation.
- **R21.** Before overwriting anything, a backup of every affected file is made. A swap that cannot
  back up everything it needs does not begin.
- **R22.** If a swap fails part way through, the game is restored to its state before the swap began.
- **R23.** A swap never leaves a game in a state the program cannot describe and reverse.
- **R24.** The program does not write to a game folder except the runtime files themselves.

### Undoing

- **R25.** Every swap can be undone, restoring exactly what was there before it.
- **R26.** Undoing can be repeated as many times as swaps have been made. Each is separately
  reversible.
- **R27.** A backup made by a previous version of the program is adopted rather than discarded, so
  upgrading does not remove the ability to undo.
- **R28.** If a game's files were changed by something other than this program, the undo history for
  that game is cleared, because no entry in it describes a state the game can return to.
- **R29.** A game whose original files are missing is reported as unrecoverable rather than being
  presented as restorable.

### Warnings

- **R30.** Before swapping in a runtime version **older** than the game currently has, the program
  warns that features depending on the newer version may stop working.
- **R31.** The warning asks rather than blocks. Downgrading deliberately is a legitimate reason to
  be choosing that version.
- **R32.** A warning is not shown when the version cannot be read, because it would be a guess.
- **R33.** Before swapping in a runtime version from an untrusted source, the program warns.
- **R34.** A game the user has flagged as a repack carries a visible marker, because a repack's own
  launcher may ignore swapped files.

### Many games at once

- **R35.** The user can select any set of games and ask what would happen if a change were applied to
  all of them.
- **R36.** That preview is shown before anything is changed, and lists every game with the change it
  would receive.
- **R37.** Changes that would make a game worse — a downgrade, or an untrusted source — are listed
  separately in the preview and are not applied automatically.
- **R38.** A preview that finds nothing to do says so, rather than appearing to succeed silently.
- **R39.** Applying a change to many games continues past a failure. Games that succeeded stay
  changed, and the summary accounts for every game.
- **R40.** Applying a change to many games can be cancelled, and cancelling stops work that has not
  started.

### Runtimes and architectures

**Only x64 is built at present.** ARM64 was supported and has been taken out again for now. That is a
change of scope, not a correction: the ARM64 build worked, and putting it back is a build
configuration change rather than new code.

The observation that made ARM64 cheap is worth keeping, because it is still true. The runtime DLLs
this program places are **x64 binaries**, and that would remain true on an ARM64 machine: Windows on
ARM runs x64 applications under emulation, so games there are x64 games and the DLLs in their folders
are x64. Nothing would ever have needed an ARM64 build of a runtime.

If it does return, the practical difference between the architectures would not be the payload, it
would be the hardware: **DLSS would not apply on ARM64**, because there is no ARM64 DLSS and no NVIDIA
GPU on Windows on ARM, while **FSR and XeSS would**, since neither is tied to a GPU vendor and both
work on any DirectX 12 GPU.

- **R45.** The program does not offer a runtime version that cannot apply to the machine it is running
  on, and says why rather than listing it and letting it fail later.
- **R46.** A game whose runtime DLLs are not x64 is reported as unsupported rather than offered for a
  swap it cannot perform.
- **R47.** The set of runtime versions offered is decided by what the machine can actually run — its
  architecture, its GPU and its API support — rather than by what happens to be available, and the
  program states which versions were excluded and on what grounds. Architecture is one input to that
  decision and currently the only one that varies between builds, since only one is produced.

### Settings

- **R41.** Which libraries are enabled is configurable and persists.
- **R42.** Accepting untrusted runtime versions is off by default.
- **R43.** The program's own data directory is user-specific. A per-user installation and a
  machine-wide installation write to the same place for a given user.
- **R44.** Changing settings takes effect without restarting the program.

---

### Persistence

- **R50.** Data is stored as plain files, in a form a person can read and correct without running
  the program.
- **R51.** A corrupt or unreadable data file does not stop the program starting. It is reported, and
  the rest of the data is used.
- **R52.** Writing data is atomic: an interrupted write leaves the previous version of the file
  intact rather than a truncated one.
- **R53.** History is retained per game up to a limit, and the limit is changeable by the user.
- **R54.** Discarding history beyond the limit discards the oldest entries first.
- **R55.** The program does not read data written by any previous version of this program.

## Constraints

- **C1.** Windows on x64. 32-bit Windows is not supported, because it cannot run a 64-bit program and
  the DLLs involved are 64-bit binaries. ARM64 was in scope and has been deferred for now; it is not
  excluded on technical grounds, and nothing in the program would need to change to support it.
- **C2.** No elevation required. The program runs as the current user. It writes only to the user's
  own data directory and to game installations, which are already writable by that user.
- **C3.** No required server. The program works with no network connection and with no service
  operated by anyone else. Where a runtime version comes from is recorded per version, so a dependency
  on someone else's availability is visible rather than hidden.
- **C4.** Verifiable builds. Every build records its version, the commit it came from, when it was
  made, and the architecture it was built for, so a bug report can be tied to a specific binary.
- **C5.** The test suite runs in CI. Nothing ships that CI has not built and tested.
- **C6.** The program is built and published for x64 from one source tree, with the architecture chosen
  at build time rather than hard-coded, so adding another is a configuration change. The committed
  dependency lock covers exactly the architectures built; it is regenerated in the same change that
  alters that set, because a mismatched lock fails the build on purpose.
- **C7.** The runtime DLLs placed into games are x64 binaries. This is not a limitation of the program
  but a property of what it places, and it is the reason an ARM64 build would manage x64 payloads
  rather than needing ARM64 ones.

---

### Error reporting

- **R56.** Anything that fails tells the user what failed, and the program keeps running. An
  unrecoverable error is reported and the program stops deliberately, rather than disappearing.
- **R57.** An error says what was being attempted, and what the underlying failure was. "Could not read
  Steam libraries" is not enough; the user cannot act on it.
- **R58.** An error says which path, library or game it concerns, so the user can tell a problem with
  one game from a problem with the whole library.
- **R59.** An error distinguishes "this could not be read" from "this is empty". A library whose
  configuration is unreadable is a different problem from a library containing no games, and reporting
  them the same way tells the user something false.
- **R60.** An error names a next action where there is an obvious one, such as the file or setting
  involved, rather than leaving the user to work it out.
- **R61.** Detailed diagnostics are available on demand without interrupting the user. A summary is
  always shown; the full detail, including the underlying exception, is one step away.
- **R62.** Diagnostics identify the build, the architecture and the session, so a report can be tied to
  a specific binary. Version alone is not enough once more than one build exists.
- **R63.** A failure that affects several items at once reports every item affected, not only the
  first. Truncating a failure list loses the information the user needed it for.
- **R64.** Errors are never swallowed silently, including in background work. Work that fails while the
  user is looking at something else still reaches them.
- **R65.** Distinct runtime kinds are separate choices, and choosing one installs only that one. DLSS,
  DLSS Frame Generation and DLSS Ray Reconstruction are three files with three names, offered as
  three separate pickers, and a choice made for one is never applied to another without the user
  making that choice too.
- **R66.** Where the vendor supports several runtimes only at matching versions, a choice that would
  leave them out of step says so in the preview. NVIDIA supports the three DLSS kinds only at matching
  versions, and this is reported rather than enforced: the user chooses what goes in their library,
  and there are reasons to pair them differently that this program cannot know.
- **R67.** A part is never added to a game that did not ship with it. Adding Frame Generation to a
  game that never had it is adding runtime support to that game, which is out of scope, and would
  leave the title running a feature its developer never enabled.
- **R68.** A change to several runtimes in one game either fully applies or fully rolls back. If one
  fails, the others already applied to that game are undone and reported as failed with the reason.
  This is safety principle 3 applied within a game, and it does not extend across games: one game
  failing does not stop the others.
- **R69.** Where several kinds share a name, an option says which kind it is. DLSS, DLSS Frame
  Generation and DLSS Ray Reconstruction routinely hold the same version number, so a picker listing
  bare versions would show identical entries that differ only in the part they would install.
- **R70.** Every runtime kind has its own version picker in the game's own row, so a change can be
  made to any number of games and then seen as a whole before anything is written. Kinds that share a
  version number are what makes this necessary rather than merely tidy: one shared column, or one
  picker for the selected game, cannot say which of them a version belongs to.
- **R71.** Choosing a version in one cell affects that cell only. The pending changes are the cells
  whose chosen version differs from the one installed, and a version equal to the installed one is not
  among them — including where the two are written differently, as NVIDIA writes 310,2,1,0 and
  310.9.1 for the same build.
- **R72.** A runtime a game cannot take is not offered at all, and its cell is empty rather than
  holding a control that looks usable and does nothing. A game carrying no runtimes offers nothing.
- **R73.** A version is chosen in exactly one place: a panel naming the game and its install path, with
  a picker per runtime beneath that runtime's name. A list row is the wrong place for a control that
  changes something: it makes every row look editable and puts two controls on one value.
- **R74.** What the collection holds is viewable on its own, newest first by version number rather than
  as text. Each version says which runtime it is for, whether its signature was accepted, where it came
  from, and which games already run it — a version several games already run is one known to work here.
- **R75.** Games can be shown as poster cards carrying the versions they run, and either view can be
  filtered by name. Both views are built once and one is hidden, rather than one being rebuilt on each
  toggle, because rebuilding throws away column widths the user has set.
- **R76.** The choice of list or grid persists, as grouping does. A preference that resets every launch
  is a preference that has to be set every launch.

## Safety principles

A tool that overwrites files in somebody's game install is one bad operation away from making a game
unplayable. These are the rules that follow from taking that seriously.

1. **Never lose the original.** A file is never overwritten before a copy of it exists. This is not
   negotiable and takes precedence over every other requirement.
2. **Always reversible.** If an operation cannot be undone, it does not happen.
3. **Fail by doing nothing.** A failure leaves the machine as it was. A partial change is worse than
   no change, because it leaves the user unsure what state they are in.
4. **Say what is known, not what is assumed.** An unreadable version is unknown, never a guess.
5. **Warn before, not after.** The user is told before a consequential change, while they can still
   choose otherwise.
6. **Never act silently on something surprising.** Anything unexpected is reported, even if reporting
   it is inconvenient.

---

## Interface

The program is a single window. It does not open multiple windows to show unrelated things.

- A list of games, showing each game's current runtime version, its library, and any marker such as
  being a repack. The list can be a grid of tiles or a compact list.
- Selecting a game shows the runtime versions available for it and offers to swap.
- The selection for many-games work is made on the main list, not in a separate mode.
- Settings and information are reachable without leaving the main list.
- Long operations report progress and can be cancelled.
- The program does not use a modal dialog to ask a question that could equally be asked inline, and
  never shows two dialogs at once.

---

## Decisions

Questions that were open, and what was decided. Recorded with the reasoning, because each one
constrains the others and none should be revisited without knowing why.

### Where runtime versions come from

**Both: discovered from games already installed, and imported by the user.**

A version found in a game on this machine is known to exist and needs no verification, because it is
already on the disk and already in use. An imported version broadens the collection beyond whatever
happens to be installed, and is verified before being accepted.

Discovery alone would leave the program unable to offer a version nobody happens to have. Import
alone would mean trusting files of unknown provenance for every version it offers, with no ground
truth to compare against.

### How data is persisted

**Plain files, not a database.**

The data is a list of games, a list of runtime versions, and a short history. Nothing here needs
querying, joining or indexing, so a database would be machinery without a job. Plain files can be
read, corrected and recovered by hand, which matters when the same directory holds the backups standing
between a user and an unrecoverable game.

Reversible: if history retention grows into something that needs querying, the history can move to a
database without anything else changing.

### How much history is kept

**A bounded number of recent changes per game.**

Unlimited history combined with plain files means a data directory that grows forever and gets slower
to load. The bound is presented in the interface rather than hard-coded, so the user can raise it.

### Whether the previous version's data is read

**No. The program starts empty.**

It uses its own data directory, and reading the old one would mean maintaining a format that is no
longer maintained, in exchange for convenience to a small number of existing users. If that turns out
to matter, an import can be added as a separate one-way operation that is not part of normal startup.

### Cover images

**Shown, and read from what Steam has already downloaded.**

Steam keeps the artwork it displays for every app it knows about, in its own cache on the local
machine. The poster for an installed game is therefore already here, and showing it costs a file
lookup rather than a download.

The two objections that deferred this are both answered rather than dismissed:

- *"Fetching them means network access and a cache to maintain."* Nothing is fetched. There is no
  cache to build, nothing to expire, and no request that can fail because a server is down or the
  machine is offline. This continues to satisfy C3 and the exclusion of servers from scope, because
  no server is contacted.
- *"Plenty of games have no artwork at all."* True, and harmless: such a game is listed with its
  initial in place of a poster, exactly as it would be listed without one. Artwork is never a reason
  for a game to be missing.

Two layouts exist — named files directly under a folder named for the app, and named files inside a
subfolder named for the image hash — and both are still produced, so both are read. Neither layout is
a choice the user made or can be asked about.

### Launching games

**Out of scope.**

Convenient, and also a natural place to be wrong: the program would have to decide which runtime a
game actually loaded, which is a harder question than which runtime files are on disk. That belongs in
a version that already reports versions correctly.

---

## Open questions

None outstanding. New questions are added here as they arise, and moved into Decisions once settled.

---
## Change log

| Version | Change |
| --- | --- |
| 0.1.0 | Initial specification. |
| 0.1.0 | Settled the six open questions into Decisions, and added the requirements that follow from them: R48 and R49 for discovery of runtime versions, R50 to R55 for persistence. |
| 0.1.0 | Added R56 to R64 for error reporting, so that failures name what failed, why, where, and what to do about it. |
| 0.1.0 | Deferred ARM64, leaving x64 as the only architecture. Restated C1, C6, C7 and R47 rather than deleting them, so the reasoning survives and putting it back is a configuration change. |
| 0.1.0 | Recorded what undoing a swap actually requires: a history entry must hold the state the program left behind, not only where the original was saved, so that R28 can distinguish "still as we left it" from "changed by something else". |
| 0.1.0 | Clarified R21 and R24: a runtime kind is installed whole, and backups are held outside the game folder, so nothing but the runtime files themselves is written into a game installation. |
| 0.1.0 | Clarified R27: an undo is itself reversible, so history carries forward the state needed to redo. |
| 0.1.0 | Noted R33 against R42: the setting decides whether an untrusted file may enter the collection, and does not decide whether the user is warned when one is installed. Accepting a file is not evidence that it is genuine. |
| 0.1.0 | Built cover images, which the Decisions section above had deferred, after establishing that Steam's own artwork cache makes them available locally with no network access and no cache to maintain. |
| 0.1.0 | Added R65 to R69. DLSS, DLSS Frame Generation and DLSS Ray Reconstruction are three separate things the user chooses between separately, so choosing one installs one. The picker gives each its own picker rather than one flat list of bare version numbers, because the three routinely hold the same version and a line reading only "310.2.1" does not say which of them it would install. NVIDIA supports them only at matching versions, which the preview now reports rather than acts on — it was briefly implemented as installing them together, which took the choice away, and that has been reversed. A runtime the game does not carry is never offered; a game whose several runtimes cannot all be applied is left as it was. |
| 0.1.0 | Added R73 to R76, after reading the DLSS Swapper source rather than working from its description. Three things came out of it that this program had no equivalent of: a poster grid carrying each game's versions, a search box, and a version library — a screen answering "what can I install, where did it come from, and does it work anywhere", which nothing previously could answer. The pickers moved from the list rows into a panel for the selected game, because a control in every row makes the list look editable and puts two controls on one value. That reverses the R70 arrangement of putting them in the list; R70's underlying requirement, that each runtime be chosen separately, is unchanged and is now stated per picker rather than per column. |
| 0.1.0 | Reduced the game list's fixed column widths and the size of the poster cell so the whole row fits the window at its minimum width. Adding posters had pushed the runtime columns off the right edge, where the version being compared was the one that could no longer be seen. Guarded by two checks over the window markup, since a column that is off screen is still present and still bound and nothing else fails. |
