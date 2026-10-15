# Roboc Developer Guide

[中文](开发者文档.md) · **English**

**For maintainers.** Users want the [User Guide](User%20Guide.md).

This document is the only developer reference. Material that used to live in `FINDINGS.md`,
`POSTMORTEM.md`, `STATUS.md`, `状态说明.md`, `视觉规范.md`, `弹窗清单.md` and
`性能测试方案.md` has all been folded into it.

---

# 1. Project overview

**A portable single-file executable** built into the Explorer context menu, doing copy,
paste, delete and recycle by performance-first means.

| | |
|---|---|
| Platform | Windows 11, .NET 10 |
| Language | C#, hand-written Win32 P/Invoke, **no MSVC, no NativeAOT, no COM shell extension** |
| Artefact | `artifacts/portable/Roboc.exe`, **a single 12.5 MB file** |
| Code | 57 `.cs` files, 20,388 lines (Core 11,382 / App 9,006), excluding `bin`, `obj` and `tools\Roboc.CorpusGen` |
| Self-test | **25 checks**, `--selftest` |
| Test machine | i7-12800HX, 31.7 GB RAM, 2560×1600 at 175% (DPI 168) |

**There is no MSVC on the development machine.** That rules out NativeAOT, a native DLL and a
COM shell extension, so every native capability has to be reached through P/Invoke.

---

# 2. Building and publishing

**The only sanctioned route:**

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

The sequence is: build → publish → copy icons → **`icacls /setintegritylevel Medium`** →
`--selftest`.

## The mandatory step: the integrity label

**Processes started by some toolchains run at Low integrity**, and a Low-integrity process
**cannot write to `HKCU\Software`**. The published executable therefore inherits Low and every
registry write is refused.

`build.ps1` handles it automatically, **but three things are worth knowing**:

| Target | Why it needs the label |
|---|---|
| `Roboc.exe` | It writes the menu keys into `HKCU` |
| **`Roboc.CorpusGen.exe`** | **A Low-integrity process cannot write into an ordinary directory either** — without the label it fails with `Access to the path ... is denied`, which **reads like a directory permission problem when the real cause is that the program's own integrity level is too low** |

**Manual recovery**, if publishing was skipped:

```powershell
icacls <exe> /setintegritylevel Medium
```

**Two ways this fails in practice, both handled by `Set-MediumIntegrity` in `build.ps1`:**

1. **An image lock.** Windows keeps an executable mapped for a while after it has been run,
   and while that mapping is alive `icacls` reports `Access is denied` on that exact file —
   not on its directory, not on any other file in it. A copy that has never been executed has
   no such lock, so the label goes on a copy and the copy is renamed over the original.
2. **A missing `WRITE_OWNER`.** Setting an integrity label needs it, and `M` (Modify) does
   not include it. Directories inherited from elsewhere may not grant it; `icacls <dir>
   /grant "<user>:(OI)(CI)F" /T` fixes it.

**The function relaxes `$ErrorActionPreference` for its whole body**, and that is
load-bearing: the script runs with `Stop`, and PowerShell turns a native command's stderr
into a terminating error under that setting — so the first refusal aborted the build from
inside the very code written to handle refusals.

## `.ps1` files must carry a UTF-8 BOM

**PowerShell 5.1 decodes a `.ps1` without a BOM using the system ANSI code page**, which turns
Chinese into mojibake and produces a string of errors that look like syntax faults
("missing closing `}`", "Try statement is missing its Catch").

**This has been hit four times, three of them when an editing tool silently stripped the BOM
on write.** Checked automatically by `scripts/check-bom.ps1`.

`.editorconfig`'s `charset = utf-8-bom` only binds editors. It does nothing for scripts,
toolchains, or any program that writes files directly — which is why the check exists.

## Publishing can fail silently

If `build.ps1` cannot delete the output directory because something holds it, it reports
`Access is denied` and **carries on to print "完成"** — **with the paths still pointing at the
old artefact.**

**Kill stray processes before shipping:**

```powershell
Get-Process Roboc -EA SilentlyContinue | Stop-Process -Force
```

---

# 3. Code structure

```
Roboc/
├── README.md                 English front page
├── README.zh-CN.md           Chinese front page
├── LICENSE                   Apache-2.0
├── NOTICE                    Trademark and third-party notes
├── Directory.Build.props     Shared build properties
├── Roboc.slnx                .NET 10 solution format (XML)
├── build.ps1                 The only sanctioned build entry point
├── .editorconfig             ★ .ps1 needs a BOM — the rule came from real failures
├── .gitattributes            Line-ending policy, binary markers
├── .github/workflows/        CI: build, publish, self-test, three checks
├── assets/icons/             Menu icons (copied into the package at build time, see below)
├── docs/                     User Guide · Developer Guide · Chinese originals · images
├── scripts/                  Benchmarks and checks (results/ is gitignored)
│   ├── check-wiring.ps1      Wiring check (section 7, rules 2 and 3)
│   ├── run-bench.ps1         Benchmark run
│   ├── run-perf.ps1          Head-to-head performance run
│   └── disk-full-test.ps1    Disk-full behaviour (needs elevation)
├── src/Roboc.Core/           10,373 lines — no UI dependency
│   ├── Copy/                 CopyEngine 1132 · FileCopier 681 · OverlappedPipeline · Verifier
│   ├── Delete/               DeleteEngine · FastDeleter
│   ├── Fs/                   RecycleBin 993 · AlternateStreams · ReparsePoints · DirectoryOps · PathUtil
│   ├── Shell/                ContextMenuInstaller 805 · BinIndex · SendToMenu
│   ├── Journal/              JobJournal 532
│   ├── Interop/              Kernel32 · Ntdll · NativeConst · NativeStruct
│   ├── Model/                CopyOptions · RobocSettings · Lang · JobTypes
│   └── Diagnostics/          SelfTest 732 · NativeError · Watchdog
├── src/Roboc.App/            6,410 lines — CLI and UI
│   ├── ProgressWindow 1303 · MessageDialog 820 · SettingsWindow · ProgressTheme · RoundedBox · AppIcon
│   └── Cli/                  MaintenanceCommands 1188 · ShellCommand 531 · CopyCommand
│                             UninstallCommand · DeleteCommand · Args · PendingSelection
│                             ExplorerSelection · ConflictScan
└── tools/Roboc.CorpusGen/    Benchmark corpus generator (the usual place for a helper tool)
```

## The two icon mechanisms

**They must not be confused, because the constraints differ:**

| | Application icon | Menu icons |
|---|---|---|
| **Location** | `assets/icons/Roboc.ico`, **embedded in the executable** | `assets/icons/*.ico`, **external files** |
| **Used for** | How the exe looks in Explorer, **and the window title bar** | How each of the six menu entries looks |
| **Resolved by** | `AppIcon.Handle` (`LoadIconW` reading its own resources) | `ContextMenuInstaller.IconFor` (path construction) |
| **Why** | A window class can hold a handle, so it uses the one already in the binary | **A static registry verb can only name a file** — it cannot address one icon inside an exe |

**Both the cascade and the flat layout must go through `IconFor`.** The cascade version once
hard-coded every entry's icon to `quotedExe + ",0"` — so six entries showed the same program
icon while six purpose-drawn ones sat unused beside them. **The flat writer had always done it
correctly; only the layout that ships by default was wrong.**

**To change the application icon**: copy the one you want from `assets/icons/` over
`assets/icons/Roboc.ico` and rebuild.

## A few key designs

**The selection is read over COM.** Explorer's `MultiSelectModel=Player` switches to passing
only the clicked item once more than about fifteen are selected. **The only reliable route is
to read `SelectedItems()` from the foreground Explorer window's `Shell.Application`**, and to
match the window **by the parent folder of the clicked item** (`ShowsFolder`) — **never by
focus**, because a Windows 11 context menu is a XAML popup whose owner chain does not lead
back to the folder window.

`AppliesTo` is a dead end — four expressions were tried and every one of them hid the entry
outright.

**De-duplication across processes.** One operation spawns several processes, one per selected
item. The first to take the lock runs and **the rest exit silently**. But the lock is released
when a process exits, so a late arrival **processes the same batch again** — hence the
"already handled" fingerprint (a SHA-256 of the sorted paths plus a timestamp, valid for
fifteen seconds).

---

# 4. Design decisions and why

**This section records the "why not"s.** Every entry stands for something that was measured,
or for something that broke once.

## Deletion

| Decision | Reason |
|---|---|
| **Three guards on `delete`** | `GuardDriveRelative`, `GuardAgainstVolumeRoot` and `GuardAgainstSelf` — drive-relative paths, volume roots, the program's own folder (section 6) |
| **`--yes` required** | Deleting is irreversible; an argument that deletes without confirmation is an argument that deletes by accident |
| **`--yes` is the only route to emptying the bin** | It used to accept `--gui-errors` in its place, on the grounds that "the menu path shows a confirmation dialog" — **but the menu never passes `--empty`, so that dialog is unreachable and the exemption was a back door to emptying the bin with no confirmation at all** |
| **Recycling is the default**; `--direct` is what makes a delete permanent | |
| **Recycle refuses a duplicate** | Two copies of the same original path make it impossible to know which to restore |
| **An item already in the bin cannot be recycled again** | The program explains why and offers to take it back instead — a static verb's label cannot change with the selection, and `AppliesTo` does not work |

## Copying

| Decision | Reason |
|---|---|
| **Atomic publication on by default**: stage, then rename through `FileRenameInfoEx` (POSIX rename semantics) | The destination path is never observed half-written |
| **Below `LargeFileThreshold` (2 MB) with no destination in the way, write straight to the target** | Staging and renaming are pure overhead on the fast path |
| **Large files go through overlapped double buffering** | |
| **Block cloning when possible** | Server-side copy on the same volume, never entering user mode |
| **Hard links preserved** | Otherwise a set of links becomes independent copies: the contents are right, the space multiplies, and later edits stop propagating |
| **`UseUnbufferedIo` has been deleted** | It defaulted to `true` and nothing read it. Unbuffered I/O requires the offset, the length and the buffer address to be sector-aligned, and **measurement showed large-file copies already at 1034 MB/s against a volume that sustains 967 MB/s — already at the device's limit** — so bypassing a cache that never waits cannot help |
| **The checkpoint interval stays at 32 MB** | Raising it to 256 MB **produced measurements that contradicted each other** (32 MB: 1.02 s, 256 MB: 1.18 s, 1 GB: 4.11 s, and `--no-resume` came out slower than leaving it on), because cache eviction dominated a sequential measurement. Reverted |

## Recycling

| Decision | Reason |
|---|---|
| **One bin per volume**, `<volume>:\Roboc_bin\` | A move within a volume is a rename; across volumes it would be a copy. It also keeps clear of the system Recycle Bin |
| **A flat layout**: `<bin>\<leaf name>` beside a `.txt` sidecar of the same name | |
| **The original path has three fallbacks**: sidecar, then marker, then mirrored arithmetic | |
| **Marker files are not translated** | They are identifiers, not interface text — see the note in `RecycleBin` |
| **The original-path line is read in both languages** | The marker is written in whichever language was in force and read back later, possibly after the user has switched |

## Visual

| Decision | Reason |
|---|---|
| **Every dimension declared in points and logical pixels** | A fixed `-12` pixel height is only **57%** of the system UI at 175% |
| **`GetDpiForSystem()` before the window exists** | `GetDpiForWindow(0)` **returns 96 unconditionally** before then |
| **No Mica background** | It assumes the window does not paint its own background; on a self-drawn window it only smears the composited edges |
| **No `WS_THICKFRAME`** | A resizable border misaligns the composited edge with the drawn content |
| **All dialogs are self-drawn**, never `MessageBoxW` | `MessageBox`'s font, colours and corners **cannot be set at all**, which is why two dialogs used to look like two programs |
| **The title bar is painted one step darker than the card** | DWM draws it with Mica, which tints from the wallpaper; against a self-drawn neutral grey card that is two colours in one window (see section 5E) |
| **Rounded corners are drawn with GDI+ paths** | A GDI region is a set of whole pixels with no notion of a curve, so the corners came out visibly stepped |

### The application icon in dark mode (measured, decided, unchanged)

**Measured 2026-10-13, Windows 11 build 26300, dark taskbar `#202020` (luminance 32).**

`Roboc.ico` is composed of:

```
52% opaque area (the rest transparent)
  100% of it dark pixels
    0% midtones
    0% light pixels
```

**Maximum luminance difference against a dark taskbar:**

| Icon | Difference | Verdict |
|---|---|---|
| **Roboc (application)** | **32** | **barely visible** |
| copy / paste | 45 | weak |
| delete | 63 | clear |
| recycle_bin / settings | 82 | clear |
| recycle | 105 | clear |

**This is the inevitable result of a black mark on a fully transparent background**, and the
application icon is the one that must appear on the **taskbar, Alt-Tab and the notification
area**, where the background is chosen by the user's theme rather than by this program.

**The author has confirmed it is legible on a real dark-theme machine and has chosen to keep
the design.** A script's contrast estimate differs from what the eye sees, and the choice is
part of the intent.

**Recorded so it is not "rediscovered".** Finding it again does not mean changing it again —
unless the author changes their mind, or a user reports not being able to see it.

**If it is ever changed**, the cheapest fix is to give the mark a solid background in the menu
icons' blue, which solves both the visibility and the "application icon does not match the
other six" problem in one edit.

## Arguments and configuration

| Decision | Reason |
|---|---|
| **Accepting an argument and doing nothing with it is worse than refusing it** | Refusing costs a second. **A false assurance can cost the data it was supposed to protect** |
| **`--verify` is implemented**, off by default | It costs 54%, and most copies are not worth that. Windows' own copy does **no verification at all** (`CopyFileEx` has no such option), which is what makes this a capability rather than a duplicate |
| **`--ads` is implemented**, off by default | Probing for streams costs **one `FindFirstStreamW` per file** — 19 µs measured, against 16 µs for reading a timestamp — about **3.8-7.4%** of throughput |
| **Settings live beside the executable** | So "delete the folder" is literally true |
| **Both directions exist for every switch** | A feature enabled in settings must be switchable off for one run |
| **The language is a string, not a boolean** | A third language is a new value rather than a new field |

---

## Why it cannot read portable devices (phones, cameras — MTP)

**Conclusion: it does not read them. This is not a defect waiting to be fixed; it is a decision
that follows from the architecture.**

### A device item is not a file

A phone connected over MTP **has no drive letter** — it is not a file system but an item in the
shell namespace. Its `Path` property is a string, so it travels all the way to the file APIs
looking like a path:

```
::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\\?\usb#vid_22d9&pid_2765&mi_00#7&18246318&1&0000#{6ac27878-...}\SID-{10001,,493075931136}\{6AD0215E-...}\{6AD02111-...}
```

**Measured 2026-10-15** (device: OnePlus Ace 5 Pro):

| Check | Result |
|---|---|
| What the clipboard holds after copying from the device | **`CF_HDROP` only** — no `CFSTR_SHELLIDLIST` |
| `SHParseDisplayName(that string)` | **`0x80070057 (E_INVALIDARG)`** |
| Path spellings tried | as-is / without the `::{20D04FE0}` prefix / with `shell:::` / device instance alone / with the SID segment / joined with `#` — **all six unresolvable** |
| Control: `C:\Windows\notepad.exe` | `S_OK` |
| Control: `::{20D04FE0-...}` (the This PC CLSID) | `S_OK` |
| `GetFileAttributesW(that string)` | `0xFFFFFFFF` — **this is where the program used to die** |

**The shell can navigate *into* the device** (`NameSpace` returns the device item, `IsFolder` is
true), **but a device item's `Path` is one-way** — no prefix turns it back into a PIDL.

### Why it is not done

**1. The only workable route has nothing to do with the existing engine.** The documented way to
read device content is the **WPD API** (`IPortableDevice` → `IPortableDeviceContent` →
`IPortableDeviceResources::GetStream` → `IStream` → a sequential `StreamCopy`). It is
**streaming**:

| What this project is good at | On MTP |
|---|---|
| Multi-threaded worker pool | ✗ the stream is serial |
| Overlapped I/O (`OVERLAPPED`) | ✗ |
| Block cloning / hard-link reconstruction | ✗ meaningless |
| The resume journal | ✗ would have to be written from scratch |

**Even if built, it would be a separate code path that gets none of the optimisations this tool
exists for.**

**2. It does not serve the reason this tool exists.** Roboc solves "Explorer is slow with very
large numbers of small files"; its advantage comes from cutting **per-file overhead**. MTP's
bottleneck is the device, not per-file overhead. **On that route the premise — faster than
Explorer — does not hold.**

**3. There is already a better answer.** Copy the device's contents to a local disk with Explorer
first, then run this tool on the local copy. That is the case it is built for.

### What was done instead: say it plainly, do not fail silently

Three behaviours used to mislead the user:

| Verb | Before | Now |
|---|---|---|
| Paste | `source not found:` followed by a device identifier — English, internal | A plain explanation plus "copy it to a local disk with Explorer first" |
| Recycle | **"the drive cannot hold a separate Roboc recycle bin"** — this program's limitation reported as a fault of the user's device | The same plain explanation |
| Delete | **silently skipped**, then "none of the given items exist" — implying the files had already been deleted | The same plain explanation, no longer silent |

**The test is `PathUtil.LooksLikeFileSystemPath`**: only a path with a drive letter or a UNC
prefix is a file-system path. It does not test existence and is not a general parse of the shell
grammar — it answers only the question the caller actually has: **could a Win32 file API mean
anything by this string?**

**Note that it accepts UNC**: a network share works. That is why the wording says "the file
system" and not "the **local** file system", and why the limitation is stated as "MTP and any
other storage" rather than "MTP and **local** storage" — either narrower phrasing would tell
everyone with a mapped drive or a NAS that their setup is unsupported, which is false.

**The wording lives in one place**: `Roboc.Core.Fs.NonFileSystemNotice`. It has **two forms**,
deliberately:

| Form | Used by | Why |
|---|---|---|
| `Text` | the dialogs on the copy and delete paths | there is room to explain |
| `Reason` | the failure lists on the recycle and restore paths | ten such lines can appear in one dialog, and the full explanation would bury the list it belongs to |

The earlier version was written out in five places in four different phrasings; that is gone.
Changing a sentence is now one edit.

**This is a design principle**: where a tool cannot do something, saying so beats doing half of
it — and **reporting your own limitation as the other side's fault is the worst version of all.**

---

# 5. Defects that have been fixed

**These are grouped by the shape of the mistake, because the shape is what recurs.** Note that
most of them belong to the same family.

## A. "The code is right but the screen is blank" (4)

Rendering that fails silently, leaving something invisible rather than wrong.

| Defect | Symptom |
|---|---|
| The gridlines were drawn before the fill, and the opaque fill covered them | Something that should have been visible was not |
| `DT_CALCRECT` was used to draw instead of to measure | A button with no text |
| No `WS_CLIPCHILDREN`, so the parent repainted over its child buttons every frame | A handle with no button |
| `Marshal.PtrToStructure<T>` used on a class | The exception crossed a P/Invoke callback boundary and **the process exited silently, with exit code 0** |

**Only a screenshot finds this family.** Reading the code does not.

## B. "The promise does not match the implementation" (7)

**Accepted arguments that were never implemented, and claims in the output that were not true
of the code that printed them.**

| Claim | Reality |
|---|---|
| `CopyAlternateDataStreams = true` | Nothing reads it |
| `VerifyChecksum` | Nothing reads it |
| `--verify` | Accepted the argument and did nothing |
| `UseUnbufferedIo = true` | Nothing reads it (since deleted) |
| `--no-flush` | Cannot reach the checkpoint flush |
| `--checkpoint-interval` | Not registered in `TakesValue` |
| **`--export-reg`** | **Not registered in `TakesValue`, and still is not** |

**The first six were found by reading the code by eye; the seventh was found by
`scripts/check-wiring.ps1`.**

## C. "The failure never reached anyone who needed it" (4)

Errors caught and discarded, or written somewhere nobody looks.

| Where | Symptom |
|---|---|
| `Walk` enumerated a file as if it were a directory | Something that should have been counted was not |
| `OnItemWritten` only fired for files | Something that should have been selected was not |
| `Invoke-Timed` discarded stderr | Something that should have reported a failure said "finished in 0.2 seconds" |
| The `$Corpora` parameter shadowed the `$corpora` array | Something that should have run the test said "the corpus does not exist" |

**The cause of the fourth**: PowerShell variable names are case-insensitive, so the parameter
declaration erased the corpus definition and every `$c.Key` was `null`.

## D. A single severe defect

**`FSCTL_SET_REPARSE_POINT` with the wrong access bits**:

```csharp
// wrong:  FILE_WRITE_ACCESS -> 0x0009C0A4
// right:  FILE_ANY_ACCESS   -> 0x000900A4
```

**The access bits are part of an IOCTL's identity, not a permission check.** With the wrong
bits the kernel treats it as **a request that was never defined** and returns
`ERROR_INVALID_FUNCTION` — which literally reads "the function is incorrect", sounds like the
filesystem refusing, and actually means "there is no such control code".

**This defect had been present since the project began, and had been written off as "this
machine does not support it".**

## E. "Only half of it was changed" (5)

**When one problem has several paths or several layers, fixing one of them feels like fixing
it.**

| Defect | What was changed | What was missed |
|---|---|---|
| **The Mica material** | The window background's Mica was removed | **The title bar is drawn by DWM and kept its Mica** — so the bar followed the wallpaper while the body stayed neutral grey |
| **Menu icons** | `WriteFlat` called `IconFor` correctly | **`WriteCascade` — the default layout — hard-coded `quotedExe + ",0"`**, so six entries showed one icon |
| **Settings persistence** | Both `Load()` and `Save()` were written | **`Save` still used the reflection overload**, which must throw in a trimmed build |
| **The progress window and a dialog together** | The progress window was disposed, so it looked handled | **Both verbs disposed it at the end of the block, and the dialog on the failure path is shown *inside* that block** — so the modal dialog appeared first. A modal dialog runs its own message loop, so 取消 and 关闭 were painted but dead: the progress window could only be dismissed through the dialog. A `using` declaration is disposed at the end of its scope; a `finally` runs before the `catch` that follows it, which is the order actually needed. **Reported by the user on 2026-10-15 with a screenshot** |
| **The "reveal" after a paste** | `SHOpenFolderAndSelectItems` called on its own did reuse the window | **Called by the program, it does not** — an already-open destination still gets a new window beside it. The criterion used was "the API works on its own", not "what the user sees" (section 17) |

**The Mica case is the clearest.** The comment written when Mica was first removed says:

> *Mica is designed for windows that do not paint their own background.*

**That is correct, and it did not prompt the next question**: does a title bar count as the
window's background? **It does, and DWM draws it, and the program cannot reach it.** It took a
user reporting a dark blue title bar to find out.

**All three were found by the same action: verifying after the change instead of assuming the
change covered everything.**

## F. "The compiler said so all along" (3)

**Three defects were reported on every build and were lost in the warning count.**

| Warning | What it said | Consequence |
|---|---|---|
| `CS0169` × 3 | `ProgressWindow`'s `_detail`, `_headlineControl`, `_percentControl` **are never used** | Debris from the move to self-drawn text |
| `CA2014` | `JobJournal` has a **`stackalloc` inside a loop** | Nine bytes of header per record, never released until the method returned: **a million records asked for nine megabytes of a one-megabyte stack** |
| `IL2026` / `IL3050` | `RobocSettings.Save` used **reflection-based serialization, which trimming disables** | **Compiled clean, and threw the first time a settings file existed** |

**They were found together, during one pass to clear warnings** — until then the build had
said "3 warnings" and nobody had read it.

**`scripts/check-wiring.ps1` later turned this class into an automated check**, but its scope
is configuration and arguments, not compiler warnings. **The rule is that the build must be
silent, not that the warning count has not changed** (section 7, rule 8).

## H. "The method exists and nobody ever called it" (1)

**`RecycleBin.WriteMarker` had been there since the first commit and was never called once in
the project's whole life.**

**The consequence**: when a **directory** was recycled, the directory was moved and the index
recorded it, **but the marker file that lets the bin recognise it was never written**. `Walk`
therefore treated it as an ordinary container and recursed into it, looking each file inside up
in the index — **and finding none of them**.

**What the user saw: a "recycled successfully" message, and then a bin that said it was empty
— with a complete folder sitting inside it, 1015 MB, every file present.**

**Why it was hard to find**:

| Layer | State |
|---|---|
| The recycle operation | Reported success |
| The file system | The directory really was in the bin |
| The index | The record was complete |
| **The listing** | **Said there was nothing** |

**No layer reported an error.** The only symptom the user could see was "the bin is empty",
**and that is also a legitimate message describing a normal state.**

**The root cause was not only a missing call**; there was a design inconsistency as well:

- `OriginalPathOf` says plainly that **the index is the authoritative source** and the marker
  is only a fallback for old data
- `Walk`, deciding what a directory was, **consulted only the marker and never the index**

**Two methods disagreed about what was authoritative, each was self-consistent, and together
they were wrong.**

**The fix, and the defences**:

1. `Walk` now **asks `OriginalPathOf` first** for a directory (which consults the index), and
   only recurses when that fails
2. `TryPut` now calls `WriteMarker` — **not only a fallback for the program but a note for the
   person**: the marker records the original location, the time of recycling and how to
   restore it
3. **A twenty-fourth self-test item**: recycle a directory, then assert that it **can be
   listed**, and that it **carries a marker**

**One gap is still open**: `scripts/check-wiring.ps1` can find properties and arguments that
are **declared and never read**, but **it cannot find a private method that is defined and
never called**. Only a compiler analyser (`IDE0051`) can, and that does not run at build time
by default.

## G. "The comment says the opposite of the code" (1)

```csharp
DrawTextW(dc, text, text.Length, ref rect,
    0x00000010 | 0x00000800 | 0x00000020); // WORDBREAK | NOPREFIX | SINGLELINE off
//                                          ^ 0x20 is DT_SINGLELINE
```

**The comment said "off" and the code had it on.** The result was the settings window's three
explanation paragraphs: no wrapping (text ran off the right edge), and the three paragraphs
marching down in 160-pixel steps (the second below the panel, the third off the window).

**This is worse than no comment at all** — it teaches a reviewer to skip the line.

**The same repair exposed the other half of the pairing**: `DT_CALCRECT` belongs only in the
measuring call. **Measuring and drawing with flags that do not match is the root of this whole
family of defects.**

---

# 6. Post-mortem: `delete D:` removed an entire working tree

**This is the most serious accident in the project's history.**

## What happened

An early version ran `delete D:` — without the backslash. Windows reads that as *the current
directory on drive D*, which was the project root. It was deleted.

## Root cause

**A drive-relative path is a semantic trap in Windows**: `D:` is not `D:\`, it is "the current
directory on drive D". **The input was ambiguous and the program resolved the ambiguity
silently** — it took a legal but semantically unexpected input literally, and nothing in the
argument parser had any reason to object.

## The three guards now in place

| Guard | Catches |
|---|---|
| `GuardDriveRelative` | `D:` and anything shaped like it |
| `GuardAgainstVolumeRoot` | An entire volume |
| `GuardAgainstSelf` | The program's own folder and every ancestor |

**Any one of them refuses the command outright** rather than asking for confirmation.

**On top of that**, deleting requires `--yes` and one forced confirmation.

## The principle that came out of it

**A destructive operation must never run on uncertain input.** Not "must warn first" — must
decline. Better to refuse and ask the user to be more specific than to guess. A prompt can be
dismissed by reflex; a refusal cannot.

**This principle later saved the recycle bin** — refusing a second item with the same name, and
refusing to recycle something that is already in the bin, are both extensions of it.

---

# 7. Maintenance rules

**This section is a set of hard conventions. Breaking one does not fail the build.**

## Rule 1: an empty `catch` is allowed only for enumeration, cleanup, or a read with a fallback

```csharp
catch (IOException) { }
catch (UnauthorizedAccessException) { }
```

**Every empty catch must be one of those three. Anything else is a swallowed failure.**

**How to decide**:

| Allowed | Not allowed |
|---|---|
| Skipping an unreadable item during enumeration | A failed move, delete or write being swallowed |
| A cleanup whose failure is harmless (a leftover file stays behind) | A failure with consequences for data integrity |
| A read that has a fallback below it (sidecar, marker, arithmetic) | The only channel that records something |

**There are 78 such sites at present, each one reviewed, and all of them in the allowed
categories.** But **a new one must be classified before it is written** — copy that catch onto
a delete path and it compiles, passes its tests, and silently loses data.

## Rule 2: a new argument with a value must be registered in `Args.TakesValue`

Otherwise the parser treats the value as a positional path. `--checkpoint-interval` was
missing from it once, and the failure was `roboc: source not found: 32` — **naming neither the
argument nor the omission.**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\check-wiring.ps1 -Root .
```

**Exit code 0 means it passed. Worth running before every release.**

## Rule 3: a new `CopyOptions` property must have a reader

The same checker covers it. **A property that is only ever assigned makes a reader believe some
behaviour is implemented** — see section 4.

## Rule 4: `.ps1` must carry a UTF-8 BOM

See section 2. Checked by `scripts/check-bom.ps1`.

## Rule 5: kill stray processes before publishing, and confirm the integrity label

See section 2.

## Rule 6: destructive operations must go through the guards

See section 6.

## Rule 7: make the problem observable before changing code

**Four separate "the failure never arrived" defects in this project make the case**: before
changing what the code does, make sure the failure will be visible.

## Rule 8: the build must be silent

**Not "the warning count has not changed" — silent.**

The three defects in section 5F sat in the build output for a long time under the words "3
warnings". **An unchanging number carries no information.**

```powershell
dotnet build Roboc.slnx -c Release -warnaserror
dotnet publish src/Roboc.App/Roboc.App.csproj -c Release -r win-x64 -warnaserror
```

**Both, not just the build.** Trimming warnings are produced by ILLink during *publish*, so
gating only the build leaves that whole class with a back door.

**A new warning is handled in the same change**: either fixed, or suppressed **in the csproj
with the reason written beside it** — not left in the log.

## Rule 9: verify after the change, do not assume

**One problem usually has several paths or several layers.** Fixing one is not fixing all of
them: the window background's Mica was removed while **DWM kept painting the title bar with
it**; the flat layout's icons were fixed while **the cascade layout that ships by default is a
different piece of code**; the settings reader was written while **the save still used an
overload that trimming disables**. See section 5E.

The concrete form: changed the default layout, test the default layout; changed part of a
window, look at the whole window; changed both ends of a setting, test both ends.

**And read the exit code.** Three times in one session a failure was read as a success because
only part of the output was examined — a checker that reported everything, a script that had
failed to parse, and a build that had failed to load. **The last one reported "0 warnings"
because it had not run at all.**

---

# 8. Visual specification

**The single reference for every interface element. Each number is declared once, here, and
must not appear a second time in the code.**

## The underlying principle

**Everything is declared in points and logical pixels, and scaled by DPI at draw time.** Fixed
pixel values do not survive a change of scale factor.

```csharp
// font: points -> pixels
int height = -(int)Math.Round(points * dpi / 72.0);

// layout: declared in logical pixels at 96 DPI
private static int Scaled(int pixels, int dpi) => pixels * dpi / 96;
```

**`GetDpiForSystem()` must be used before the window exists**, and `AdjustWindowRectExForDpi`
must account for the border and the title bar.

## Vertical rhythm (96 DPI logical pixels)

```
 20  headline (10 pt)                   20 .. 33
 46  percentage (19 pt)                 46 .. 71
 86  current file name (9 pt)           86 .. 98
108  bar (8 px)                        108 .. 116
134  rate graph (92 px, fixed)         134 .. 226
250  three detail lines (26 px apart)  250 .. 306
-----------------------------------------------
client height 320, window 510 × 352
```

**Two iron rules**:

1. **The graph's height is fixed; it is never "the remaining space"** — an elastic height
   absorbs the rounding differences between the text bands
2. **Changing any one band means recomputing the whole rhythm** — a card was once shrunk
   without re-spacing, and a file name grew into the progress bar

## Font hierarchy

| Element | Size | At 175% |
|---|---|---|
| Progress headline | **10 pt** | 23 px — **the baseline** |
| Dialog body, dialog buttons, progress buttons | **10 pt** | 23 px |
| Progress percentage | 19 pt | 44 px |
| **Progress detail lines** | **9 pt** | 21 px — **the only exception** (a secondary reading) |

`CreateFontW` takes a **negative pixel height** derived from points:
`height = -(points * dpi / 72)`.

## Colours

The palette is read from the system theme and accent colour at startup. Dark and light are
separate palettes, not one palette with an inversion applied.

| Purpose | Source |
|---|---|
| Light and dark theme | `HKCU\...\Themes\Personalize\AppsUseLightTheme` |
| Accent colour | `HKCU\Software\Microsoft\Windows\DWM\AccentColor` (**ABGR**, needs `& 0x00FFFFFF`) |

## The gridline tuning record

| Version | Horizontal | Vertical | `GraphGrid` |
|---|---|---|---|
| v1 | 3 | — | `Lighten(rgb, 0.42)` — covered by the fill, invisible |
| v2 | 3 | — | `0.42` — visible once the order was fixed, too strong a contrast |
| **v3** | **3** | **9** | **`0.52`** — current |

**For reference**: the graph fill is `Lighten(rgb, 0.72)`. **A larger number is lighter**, so a
contrast that is too strong is reduced by moving up. **Later adjustments in steps of 0.05.**

## Window appearance

- Rounded corners: `DWMWA_WINDOW_CORNER_PREFERENCE` (33)
- Dark title bar: `DWMWA_USE_IMMERSIVE_DARK_MODE` (20, falling back to 19)
- **Caption colour**: `DWMWA_CAPTION_COLOR` (35), one step darker than the card
- **No Mica**

```csharp
private const uint WS_CLIPCHILDREN = 0x02000000;   // not optional
private const uint WindowStyle = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_CLIPCHILDREN;

DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));          // rounded corners

// the title bar is drawn by DWM, so this must be set before the window is shown
if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
    DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));       // the old Windows 10 number
```

**Without `WS_CLIPCHILDREN` the parent's painting does not avoid its children** — and the
progress window repaints its whole client area ten times a second, so **a button is created,
positioned correctly, and then painted over on every frame.**

## The rate graph

**Two stages of smoothing**: a moving average (window 3) to remove instantaneous spikes, then
a Catmull-Rom spline converted to cubic Bezier curves to remove the corners.

**120 samples at 100 ms each — twelve seconds of history.**

Histogram samples in a ring buffer, not instantaneous values — instantaneous rates jitter too
much to draw as a line.

Tuning record: three horizontal and nine vertical gridlines; gridlines at
`Lighten(rgb, 0.52)`; the fill at `Lighten(rgb, 0.72)`.

**Gridlines must be drawn after the opaque fill.** Drawn before, they are invisible — which is
exactly what happened.

**The fill and the stroke must share one path**:

```csharp
BeginPath(dc);
MoveToEx(dc, points[0].X, points[0].Y, 0);
PolyBezierTo(dc, curves, curves.Length);
LineTo(dc, x + width, y + height);
LineTo(dc, x, y + height);
EndPath(dc);
StrokeAndFillPath(dc);      // the brush fills and the pen strokes
```

**The window size must be settled before the layout**: `_dpi = ScaleFor(_window)` comes before
`Layout()`.

## GDI discipline

**Every brush, pen, region and bitmap must be `DeleteObject`d.** A process has a finite number
of GDI objects, and leaking three per repaint at five repaints a second exhausts the quota
within minutes — after which every drawing call fails silently and the card goes blank rather
than wrong, which is far harder to diagnose.

**Rounded shapes go through `RoundedBox`**, which converts the HDC to a `Graphics`, draws, and
disposes before returning. A `Graphics` holds drawing state on the device context while it is
alive and the surrounding GDI calls know nothing about it, so the two APIs are never in use at
the same moment.

**Font objects are cached per instance and never released**: acceptable, because the process
reclaims them at exit, **but if one process ever displayed several dialogs at once this would
become a real leak.**

---

# 9. Dialog inventory

**Fourteen dialogs, all self-drawn.** Shared properties: body and buttons at 10 pt, DWM
rounded corners, a themed title bar, centred text, a warning sign on error dialogs and a
recycle mark on the bin dialogs.

## Confirmation

| # | Dialog | Trigger | Buttons |
|---|---|---|---|
| 1 | Restore confirmation | Right-click → recycle, with the selection inside the bin | Yes / No |
| 2 | Paste conflict | Pasting into a folder with a name clash **that is a real conflict** | Yes / No / Cancel |
| 3 | Permanent delete confirmation | Right-click → delete | Yes / No |
| 4 | Empty-the-bin confirmation | `reclaim --empty` (**unreachable from the menu**) | Yes / No |
| 5 | Stop confirmation | Cancelling or closing the progress window | Yes / No |

**Number 4 is dead code** — the menu command is `reclaim --gui-errors`, which does not contain
`--empty`, so that branch is never entered.

**One confirmation sits outside this list**: removing the program, `Roboc — Confirm removal`
(`UninstallCommand.cs`).

## Report

| # | Dialog | Trigger |
|---|---|---|
| 6 | The bin is empty | The bin was clicked and holds no user files |
| 7 | Recycle result | **Something failed** after recycling (a clean run shows nothing) |
| 8 | Restore failed | Something failed after restoring |
| 9 | Copy/paste failed | Something failed after the operation (a clean run shows nothing) |
| 10 | Permanent delete failed | The delete engine wrote anything at all to stderr |

**One report sits outside this list**: `Roboc is ready`, shown once the context menu has been
registered (`Program.cs`).

## Error

No selection received (two versions), clipboard write failed, clipboard is empty. A failed
context-menu registration is reported the same way, from `Program.cs`.

## Layout: keeping columns aligned under centring

Dialog bodies are centred as a block **while each line inside it is left-aligned**. Centring
every line individually would put the labels of a two-column table at different indents and
destroy the alignment that makes the numbers readable.

**Under per-line centring (`DT_CENTER`) every line has a different width, so every line has a
different left edge, and columns cannot align at all.** The answer is to **make every line's
total width identical**:

```csharp
// display width: a CJK or full-width character is 2, ASCII is 1
private static int DisplayWidth(string text);

// the label is padded to 12 units and the value right-aligned within 10 = a constant 22
private static string StatRow(string label, string value);
```

**Computed at run time, not written as literal spaces** — values grow (`4 items` becomes
`10000 items`), and literal spaces are misaligned the first time a four-digit number appears.

---

# 10. Performance data and measurement discipline

## Measurement discipline: this machine's storage is degraded by its own load

**Sustained small-file churn degrades the volume, and only a reboot restores it.**

The record — **the same moment, the same operation, only the volume differing**:

| | D: (Disk 0) | E: (Disk 1) |
|---|---|---|
| 300 × 4 KB file creations | 504 files/s | 1999 files/s |
| 16 MB sequential write + fsync | **1 MB/s** | 289 MB/s |

**A difference of two thousand times.**

**Therefore:**

1. **Only `scripts/run-perf.ps1` is used for comparisons.** Ad-hoc measurement is not
   comparable.
2. **Interleave the runs, rotate the order, and measure the volume's health before and after
   every round.**
3. **A round whose drift exceeds 20% is discarded whole**, and the number of usable rounds is
   reported — annotating it without excluding it still lets contaminated data into the median.
4. **Rebooting before each measurement session is recommended.**

**Health reference: about 3900 files per second.** One session measured 2953 falling to 1335.

**The full comparison across corpora needs a separate session for each corpus, with a reboot
between them.**

## Results

| Goal | Conclusion | Evidence |
|---|---|---|
| **Copy level with robocopy, or slightly ahead** | **Level at its best, and markedly more consistent** | Roboc's spread across rounds is 7%; Robocopy `/MT:32` is 114% |
| **Delete better than PowerShell `rm`** | **Yes — two to three times faster on a healthy volume** | Three rounds agreed, and it never lost |
| The large-file gap | **It does not exist** | See below |

**The measurements behind those conclusions:**

| Measurement | Result |
|---|---|
| Volume sustained write (with flush) | **967 MB/s** |
| Raw write, no flush | 4109 MB/s |
| Roboc, large files | **831–1034 MB/s** (86–107% of physical) |
| Robocopy `/MT:32`, large files | 11,100 MB/s — **eleven times the volume's physical limit** |
| Corpus A, Roboc | ~2.7 s, **7% spread** |
| Corpus A, robocopy `/MT:32` | 2.88–6.16 s, **114% spread** |
| Delete, Roboc | 1.88 / 7.77 / 15.46 s |
| Delete, `Remove-Item` | 8.28 / 22.83 / 19.92 s |
| `--verify` cost | **+54%** (911 → 591 MB/s) |
| `--ads` cost | **+3.8% to 7.4%** (19 µs per file against a 16 µs baseline) |

**Conclusion: level with robocopy at its best on many small files, and far more consistent;
two to three times faster than PowerShell at deleting.**

### The large files' "3.8x gap" is a measurement artefact

| Measurement | Value | Nature |
|---|---|---|
| The volume's true sustained write (`Flush(true)`) | **967 MB/s** | the physical limit |
| Pure write with no flush (.NET) | 4109 MB/s | absorbed by the cache |
| **Robocopy `/MT:32`** | **11,100 MB/s** | **a cache number; the data had not reached the disk** |
| **Roboc** | **831–1034 MB/s** | **really on disk = 86–107% of the limit** |

**Robocopy reports 11.1 GB/s, eleven times what the volume can physically do.**

**The mechanism**: `OverlappedPipeline.cs:198` flushes at every resume checkpoint — **which log
correctness requires** (the log must not claim bytes that a power cut could lose).

**`--no-flush` does not reach it** (it only turns off the flush at the end of the file). **This
is written into the code comment.**

**A controlled measurement proves the checkpoint interval is not a lever**:

```
32MB / 128MB / 512MB / no-resume  ->  median 0.93-0.99 s, ranges completely overlapping
```

**Large-file copying is bound by the disk. That is the end of it.**

## Measurement method is easier to get wrong than the conclusion

**Recorded because it happened repeatedly:**

- **The same 1 GB interval variant measured 4.11 s uncontrolled and 0.99 s controlled**, with
  the source warmed into cache before every run. A parameter's effect measured sequentially is
  dominated by **cache eviction**, and produced results that contradicted each other.
  **The later number describes the state of the cache, not the parameter under test.**
- Timings were taken without discarding stderr, so a failed generation was reported as
  "generated in 0.2 seconds".
- A PowerShell parameter named `$Corpora` silently overwrote a `$corpora` array, because
  **PowerShell variable names are case-insensitive**.

---

# 11. Performance test plan

`scripts/run-perf.ps1`. The corpora and the contestants:

| Corpus | Shape |
|---|---|
| A | Many small files: 20,000 × 4 KB |
| B | A few large files: 2 × 1 GB |
| C | Mixed |
| D | Metadata-heavy (hard links + ADS) |

Contestants: `Roboc`, `Roboc bare` (`--no-resume --no-flush`), `Robocopy` (default, `/MT:32`,
`/MT:1`) and `xcopy`.

**`Roboc bare` exists to separate the cost of the features from the cost of the
implementation.**

Deletion: `Roboc delete`, `PS Remove-Item` and `cmd rd`, **each one rebuilding the same starting
point.**

---

# 12. Known gaps

| Gap | Status |
|---|---|
| **ADS copying** | `--ads` is implemented, off by default |
| **`--verify`** | Implemented (byte-for-byte comparison), off by default |
| **Hard-link reconstruction** | Implemented, on by default; the mapping is capped at one million entries |
| **Reparse points** | Implemented and verified |
| **Portable devices (MTP — phones, cameras)** | **Not supported, and not planned** — see section 4. A device item is represented by the shell namespace and cannot be opened by the file APIs; the only workable route is the WPD streaming API, which gets none of this tool's optimisations. **It now explains this in plain language instead of printing an internal path, and no longer claims the drive is at fault** |
| **Dialog 4 is unreachable** | Undecided: delete it, add a menu entry, or keep it |
| A comment stating the empty-catch convention | **Not done** — the rule is in section 7 |
| `Rounds=2` in Quick mode is not statistically meaningful | Known; full mode uses 5 |

## Licence: Apache-2.0 (decided)

**Copyright holder: Neus117.** See `LICENSE` and `NOTICE`.

**Why Apache-2.0 rather than MIT**: section 6 **explicitly grants no trademark rights**. A
permissive licence lets anyone use, modify or even resell this code, and **the only boundary
left is the name** — "Roboc" identifies the original work and its official distributions, so
a fork may say it is based on Roboc but may not call itself Roboc. **Code can be copied;
provenance cannot.**

**On "open source cannot stop someone else selling it"**: every OSI-approved licence permits
commercial sale; that is part of the definition. **What protects a commercial interest is a
trademark, not a licence.**

**If the licence is ever changed**: Apache-2.0 is one-way compatible — it can be relicensed as
GPL-3.0, never the reverse. **A licence already granted cannot be withdrawn.**

## Next priorities

### Exposed by this round of firefighting, and still open

| Priority | Item | Why |
|---|---|---|
| **P1** | **Add a check for "a private method nobody calls"** | A whole-project scan this round turned up two (`MeasureOne` and `mergedIntoRestore`), and `scripts/check-wiring.ps1` **cannot find that class at all**. `WriteMarker` hid for a long time in the same shape. The compiler has `IDE0051`, but it does not run at build time by default |
| **P1** | **`build.ps1` calls `ReadToEnd()` before `WaitForExit()`** | Lines 230-232: `$p.StandardOutput.ReadToEnd()` runs before `$p.WaitForExit()`. `--selftest` raises no dialog, so this has never surfaced; the moment it does (or the step is replaced by one that can), **the build hangs for good** — `ReadToEnd` waits for the pipe's write end to close, a modal window never closes it, and every wait and cleanup after that line is dead code. This is the exact shape rule 14 exists for. The fix is to wait first and read afterwards, or read asynchronously |
| ~~**P1**~~ **Paid off** | ~~Sync the English developer guide~~ | **Completed 2026-10-14, evening**: English 610 → 1361 lines, 17 sections matching the Chinese section for section, 90 headings with an identical level signature. **One line worth keeping about how the debt arose**: the Chinese gained four sections while writing this firefighting record and the English did not follow — and *a document out of sync is worse than no document* (文档不同步比没有文档更糟), because the reader cannot tell which one is complete. **Syncing is maintenance, not a one-off task**: every section the Chinese gains, the English has to follow |
| **P2** | `TryCollectFromMenu`'s coverage of explicit paths | `--gui-errors` lets the shell's answer overwrite a path passed on the command line. **The menu path is correct**, but anyone who later wants `--gui-errors` from the command line will be caught by it. **Not changing it now** — that logic exists for "a large selection sends only one path", and touching it is riskier than leaving it |
| **P3** | If a stray marker appears in the bin's root again | This round fixed `Walk`'s `atRoot` test, **so a marker in the root is now correctly treated as an ordinary file item**. But the files left over from before are still on users' machines and have to be handled by hand |

### Carried over from the previous round

| Priority | Item | Note |
|---|---|---|
| P3 | What to do about dialog 4 | |
| P3 | A full performance run with a separate session per corpus | Clean numbers for B, C and D are needed. **The corpora have moved to `D:\bench\roboc-perf`** |
| P3 | A comment for each of the 78 empty catches | Low value; the rule already covers them |
| P3 | Screenshots in the README | |
| P3 | The `YesNo` default-button question | Dialogs currently default to "Yes". **If a report ever arrives of a destructive action taken by reflex, this is the first place to look** |

### Publishing and distribution (untouched)

| Priority | Item |
|---|---|
| **P1** | A winget manifest — the audience fits, and it does not force signing |
| **P1** | README support the author + `.github/FUNDING.yml` |
| P2 | An installer (Inno Setup) |
| P3 | A code-signing policy section in the README |

**Day-to-day to-dos, channel evaluations, the donation plan and promotional copy all live outside
the repository, in `D:\Coding\DeepSeek 开发经验\运营\`.** That material is commercial and
promotional in nature and does not go into a public repository.

**This path was "improved" once, wrongly, and the episode is worth recording.** On 2026-10-14 I
moved those two documents into the repository at `docs/运营/`, on the grounds that keeping them
outside gave them no version history and that they would be deleted along with the workspace.
**The reasoning was sound but it did not override the original constraint** — that material does
not belong in a public repository. The commit in question had not been pushed, **so it could still
be withdrawn cleanly**; had it been pushed, the only remedy would have been rewriting published
history.

**Do not compromise with an ignored directory inside the repository either.** `git status` would
then be permanently dirty, and a single `git add -A` would sweep it in. **Hiding is not excluding;
only keeping it outside the repository actually excludes it.** `.gitignore` carries an entry for
the same name as a backstop, so that nobody — including me — recreates that directory here.

---

# 13. One working principle

**Make the problem observable before changing the code.**

This project has had four "the failure never arrived" defects and seven "the promise does not
match the implementation" defects, and **not one of them was found by reading the code** — they
were found by screenshots, by diagnostic logging, and by a tool written specifically to hunt
that class of problem.

**What is on the screen, what is in the log, and what the tool prints are the only things that
get through this kind of defect.**

---

# 14. Test hygiene

**Test artefacts go in `artifacts\scratch\`, never in the root of a drive.**

## Why this has a section of its own

On 2026-10-14 the user found more than a dozen test directories in the root of `D:\`. The test
cases had created every one of them — `D:\roboc-restore-test`, `D:\roboc-marker-test`,
`D:\roboc-open-verify` and the like — **harmless one at a time, and together a heap of rubbish
growing in someone else's drive root.**

**The user's words: 「你这样很不规范。」** ("This is very unprofessional.") That is correct.

## Three rules

**1. Build in a fixed place**

```
artifacts\scratch\        <- every temporary directory and file
%TEMP%\                   <- only when artifacts is not writable
```

**Never** `D:\<something>-test`. The drive root belongs to the user, not to the build system.

**And never create a new top-level directory inside the project as a scratch area.** On
2026-10-14 I created `D:\Coding\MyRobocLab` to hold a few probe scripts. The name carried the
project's name, so it looked legitimate — which makes it *more* dangerous than `D:\xxx-test`:
**it will not be recognised as rubbish.** It was folded into the repository's `bench\` that same
day, and the directory itself deleted.

**2. Delete on the spot, not "at the end of the round"**

**The user asked for this explicitly on 2026-10-14**: 「以后每次生成临时测试文件并完成相关目标后及时删除」 — every
time temporary test files are created, delete them promptly once the goal is met.

**Why "clean up at the end" is not enough**: a round of work contains many moments at which "the
goal is met", and the end is always further away. By then, **what was created has to come from
memory** — and memory misses things (see rule 3). That day I created at least seven `H:\roboc-*`
test directories; every one of them was useless the moment its experiment finished, and every one
of them survived until the final scan.

**The practice**: the experiment finishes, the conclusion is in hand, delete it. Scripts should be
written that way too — `bench\window-reuse-probe.ps1` closes the windows it opened **whatever the
result**, and `worker-sweep.ps1` empties its target after every round.

**3. Cleanup must scan and confirm; memory is not enough**

This is the rule that was actually broken. Cleanup ran twice that day, reported "deleted" both
times, and missed something both times — **because the directories were listed from memory
rather than found by scanning the disk.**

**On the final sweep of that day, scanning turned up two more that I had "already finished"**:
`artifacts\scratch\g-src` / `g-dst` (leftovers from an earlier guard test), and two documents that
should never have been inside the repository at all. **Every "I thought that was done" was
overturned by a scan.**

```powershell
# wrong: list them from memory
Remove-Item 'D:\roboc-recycle-test' -Recurse -Force

# right: scan, then confirm one by one
Get-ChildItem 'D:\' -Force | Where-Object { $_.Name -like 'roboc-*' } |
    ForEach-Object { "  $($_.FullName)  $($_.LastWriteTime)" }
# look at the list, confirm every entry is yours, then delete
```

**Memory misses things. A scan does not.**

## Deciding whether something is yours

**Never delete anything whose origin you cannot explain.** The scan that day produced three
kinds:

| Item | Verdict |
|---|---|
| `roboc-*-test`, `%TEMP%\roboc-trim*` | **Mine, delete** |
| `Roboc-backup-20261013-115159` | **A deliberate safety net**, with a verification record. **Keep it, and ask the user** |
| `roboc-perf` (a 4.2 GB corpus) | **Mine, but expensive to rebuild** (the volume degrades, and only a reboot restores it). **Move it, do not delete it** |

**The last one became a move to `D:\bench\roboc-perf`** — the drive root was clean, the corpus
survived, and the cost was zero.

## An earlier lesson of the same kind

The same day: after a code change, `restore --gui-errors` was run directly to test the automatic
open. It passed, and "fixed" was reported.

**But the menu never takes that path.** The menu calls `recycle --gui-errors`, which calls
`restore --quiet` internally — `fromMenu` is false, and that code never runs.

**A test must go through the real entry point, not the most convenient one.** This is the same
fault as "cleanup must scan, not remember": **using what you assume instead of what is there.**

---

# 15. Firefighting record: six layers of one recycle-bin defect

**2026-10-14. The user reported 「坏了坏了重大bug」 ("something is badly broken"). Six rounds
followed; every round believed it was fixed, and every round had a next layer.**

This section is not a log of events. It is here because **five of the six layers are the same
fault.**

## The six layers

| Layer | Symptom | Root cause | How it was found |
|---|---|---|---|
| 1 | After recycling, the bin said it was empty | `WriteMarker` had never been called since the first commit | **Reading the code** |
| 2 | Restoring did not open the original location | The internal call went through `--quiet`, so `fromMenu` was false | **Reading the code** (wrong) |
| 3 | The same | The `explorer.exe` spawn was taken over by the shell and dropped | **Reading the code plus one unrelated test** (still wrong) |
| 4 | The same | **The restore cleared the index, so looking the original path up afterwards had to fail** | **A diagnostic log** |
| 5 | A conflict silently kept a copy, and the user was not told | `--called-internally` suppressed the menu path as well | **A diagnostic log** |
| 6 | `Roboc.回收信息.txt` appeared in the bin's root | **A marker file was recycled as an ordinary file, so `Walk` treated the entire bin as one item** | **A full lifecycle log** |

## Five of the layers are the same fault

**For layers 2 to 6, the first two rounds ended in a conclusion drawn from reading the code, and
both were wrong. Layers 4, 5 and 6 were each located in one pass once logs were used.**

**This is exactly what rule 7 says ("make the problem observable first"), and it was violated
three times.**

**How, specifically:**

- Layer 2: **`restore --gui-errors` was run directly**, it passed, and "fixed" was reported.
  **The menu never takes that path.**
- Layer 3: after switching to `ShellExecute`, the window count went from five to six and
  "fixed" was reported. **That test proved the API works, not that Roboc calls it.**
- Before layer 6: three explanations of "why the marker is in the root", all three of them
  guesses.

**The lesson, in one sentence:**

> **Before reporting "fixed", one run has to go the way the user actually goes.**
> **A proxy metric — a live process, a changed window count, an API that works on its own — is
> not verification.**

## One finding that deserves its own place

**The root cause of layer 6 had a hidden severity:**

```
one stray file -> HasMarker is true for Walk(binRoot)
               -> "the whole bin" is treated as one recycled directory
               -> the user clicks restore, and the whole bin is moved away
```

**The text shown in the list was identical before and after the fix**
(`236 B  D:\Temp\0\Roboc.回收信息.txt`) — **only what the item actually was differed.** This
kind of defect reports nothing at all.

**The fix**: `Walk` gained an `atRoot` test — **the bin's root is a container, and is never one
of its own contents.**

## The layer-1 leftover found along the way

**The same firefight found that `WriteMarker` existed and had never been called** — and
`scripts/check-wiring.ps1` can find "a property or argument that is declared and never read",
**but not "a private method that is defined and never called".**

**The whole-project scan at the end of that round turned up two more of the same kind:
`MeasureOne` (never called) and `mergedIntoRestore` (assigned, never read).**

**That gap in the checks is still open.** The compiler has `IDE0051`, but it does not run at
build time by default. **It deserves a round of its own.**

---

# 16. The method this firefight left behind

**What is reusable from those six rounds, ordered by how much it can be trusted.**

## One: diagnostic logs beat reading the code

**Layers 4, 5 and 6 were each located in one pass with a log; layers 2 and 3 were two wrong
guesses from reading the code.**

**A log must record what was decided, not what happened.** The line from layer 4 was:

```
originals=[(original location unknown)]
```

**It states the wrong answer outright.** If it had said only "restore started" and "restore
finished", nothing would have been visible.

**The practice**: at a branch, record **the value the decision was made on**, not a progress
marker.

## Two: verification must go through the real path

**All three "fixed" reports were false, because the test took the convenient path:**

| What was tested | What the user does | Difference |
|---|---|---|
| `restore --gui-errors` | menu → `recycle --gui-errors` → internal `restore --quiet` | `fromMenu` is the opposite |
| `[ShellSelect]::Go(...)` called on its own | the call site inside Roboc | proving the API works is not proving the program calls it |
| three paths passed explicitly | three items selected in the shell | `TryCollectFromMenu` overwrites them with the shell's answer |

**A proxy metric is not verification**: a live process, a changed window count, an API that
works on its own — **none of these is guaranteed to hold on the real path.**

## Three: look at every item a scan turns up

**This round's automated scan reported 36 i18n gaps and 9 orphan methods, and checking them one
by one showed every one to be a false positive:**

| What the scanner said | The truth |
|---|---|
| 36 "bare Chinese strings" | Multi-line `Lang.Pick(Chinese, English)` calls, with the Chinese on the first line |
| 4 "English side missing" | `"OK"` and `"No"` are two letters long, and the threshold was three |
| 7 "orphan methods" | P/Invoke declarations, method groups passed as arguments, delegate assignments |
| 3 "TODO"s | The substring inside `RtlNtStatusToDosError` — the letters of "ToDo" sit in the middle of the identifier |
| 1 `readonly` "method" | The regular expression had read a field declaration as a method |

**There were only two real problems** (`MeasureOne` and `mergedIntoRestore`).

**A scanner's value is narrowing the field to something that can be checked by hand, not
handing over a conclusion.**

## Four: ask before a destructive operation

**Two changes in that batch involved deletion:**

- `ReplaceWith` deletes the destination before moving in (when the user chooses "replace")
- Merging directories moves the contents of a folder the user already had

**Both got the same thing right: the decision was handed to the user rather than made for
them.** "Keep the original" means **keep it in the bin**, not delete it — **because what the
user chose was "keep", not "discard".**

## Five: something shared must actually be shared

**`OriginalsOf` serves both the dialog and the restore**, and its comment says:

> Shared by the confirmation that names them and the restore that performs them, so **the
> dialog cannot promise one path while the move goes to another.**

**This round broke it once**: the dialog resolved the original paths before the restore, and the
restore **resolved them again** — the second pass had to fail, because the index had been
cleared. **Sharing one variable would have prevented it.**

## Six: look for the capability the project already has

**`Shell32.SelectInFolder` already wrapped `SHOpenFolderAndSelectItems` (native multi-select),
and the paste path had been using it all along.**

**Instead, an `explorer.exe /select` version was written for the restore path, and it can select
only one item at a time.**

> **The capability was already in the project; the mistake was not looking.**

**Search before writing something new — especially around P/Invoke, where wheels are easy to
reinvent.**

---

# 17. "A new window after a paste": an investigation that followed a dead end to a conclusion

**This section and section 15 are two instances of the same family of problem.** The difference
is that this time the way out was not a log but **checking before changing** — and it produced a
reusable decision: **when to stop tuning parameters and go and find out whether the path itself
exists.**

## Symptom

**The destination folder's window was already open, and pasting into it produced another window
on the same folder.** The user has to close it by hand. Reproducible every time.

**This symptom cannot be judged by counting windows.** The old window is still there and one
more has appeared, **so the total is the same in the "normal" and the "broken" case**. The only
trustworthy criterion is whether the set of window **identities** (HWNDs) changed across the
call. `bench/window-reuse-probe.ps1` is written that way.

## The dead end, and why it is dead

**The approach**: instead of `SHOpenFolderAndSelectItems`, find the window showing the
destination yourself and set the selection in it.

**Step one**: `IServiceProvider::QueryService(SID_STopLevelBrowser, IID_IShellBrowser)` to get
`IShellBrowser`, then `QueryActiveShellView` to get `IShellView`.

**The result**:

| Call style | Return value |
|---|---|
| Hand-written vtable dispatch (no managed marshalling) | `E_NOINTERFACE` |
| Managed COM interop | `S_OK`, plus an 8-byte **unaligned** pointer, `0xE072C` |

**Calling `Marshal.Release` on `0xE072C` crashed three times.**

**The conclusion at the time was "my declaration is wrong".** That conclusion is false, and a
very tempting one: two call styles give different answers, so one of them must be misdeclared.

**The real answer only comes out of the documentation:**

1. `QueryService` is declared **`[local]`** in `servprov.h` — it is not an oleautomation method,
   and can only be forwarded through an in-process proxy
2. `IShellBrowser` itself contains `[local]` methods (`SendControlMsg`, `SetToolbarItems`),
   `IShellView` contains `[local] AddPropertySheetPages`, and `IFolderView` is entirely
   `[unique][object]`, not dual
3. **An interface with `[local]` members cannot be marshalled in full at all**, so by design it
   is in-process only
4. Microsoft's own stated precondition for `IShellBrowser` is: **"Objects that have access to
   the site chain of the browser"**

**So the path does not exist for an external process. The declaration was not wrong; there is no
road.** That `E_NOINTERFACE` was the truth, and `S_OK` with a garbage pointer was the illusion.

**One earlier misjudgement corrected along the way**: `Shell.Application`'s `Windows()` returns
an **`IShellWindows` collection, not an `IWebBrowser2`**. The collection's `IServiceProvider`
does answer `S_OK` — and that is exactly what made the dead end look promising. `LocationURL`,
`HWND` and `Document` are properties of the **elements**.

**Why the managed side answered `S_OK` with garbage**: when a `[ComImport]` interface is missing
`[PreserveSig]`, the CLR moves the return value into an **extra out parameter**, the whole
argument list shifts with it, and `out nint` reads a stack slot that was never written. **It
presents as success, which is more dangerous than a crash.**

## A reusable decision

> **If the same place keeps coming back after several rounds, that is not "nearly there", it is
> "the model is wrong". Stop, and make "why does this keep recurring" the problem itself.**

**The recurring shape here was**: the same API, with one report saying it reuses the window and
another saying it does not, and both experiments reproducible.

**The right move was to ask whether the API ever promised to reuse the window, instead of
continuing to tune parameters.** The answer:

- The **entire** window-related text in MSDN for `SHOpenFolderAndSelectItems` is one sentence:
  *"Opens a Windows Explorer window with specified items in a particular folder selected."*
- It **never promises to reuse a window that is already open**, and it documents **no condition
  under which it would open a new one instead**
- `dwFlags` defines exactly two values, `OFASI_EDIT` and `OFASI_OPENDESKTOP`
  (`ShlObj_core.h`)
- `SHOpenFolderAndSelectItemsEx` **does not exist** (one declaration of
  `SHOpenFolderAndSelectItems` in the whole 10.0.26100 header tree, and none of the `Ex` form)

**A behaviour that was never promised is not worth tuning for.** That is the stopping point.

## The fix

**Stop relying on that API's reuse behaviour and set the selection yourself**, along the path
already known to work:

```
Shell.Application -> Windows() -> element -> Document -> IShellFolderViewDual::SelectItem
```

**Only when no window is showing the destination does it fall back to the original API** — and
in that case opening a window is the desired behaviour, so the fallback is not a compromise.

**Two traps found by measurement**:

| Trap | Consequence |
|---|---|
| Matching the folder by `LocationURL` | The address-bar string turns a **space** into `%20` while leaving **Chinese characters** alone, so a naive comparison fails on every folder name that contains a space |
| Passing `SVSI_DESELECTOTHERS` (0x4) with every item | Its meaning is **"deselect everything else"**, not "begin a new selection". Three items left only the last one selected, **which looks like success from outside** |

**The second trap can only be found by testing with more than one item.** With a single item the
test passes while the bug is present.

## Three things this section should leave behind

**One: a proxy metric is not verification** (代理指标不算验证). An API that works when called on
its own, an unchanged window count, a process that reported no error — any of these can hold on
the path you tested and fail on the path the user takes. **The acceptance criterion is the path
the user takes.**

**Two: distinguish "I wrote it wrong" from "this path does not exist".** The first means
changing code; the second means changing direction. **The criterion is the documented
preconditions**, not a success rate you arrived at by trying. A success rate arrived at by
trying gets masked by "sometimes it works" for a long time.

**Three: the capability may already be in the project.** Section 5E and section 16's sixth point
have both recorded the same thing, and this round proved it once more: `ExplorerSelection` was
already enumerating windows through `Shell.Application`, already able to match a folder, and
already serving the "more than fifteen items selected" problem on the production path. **The
only thing missing was the step that sets a selection in a window that is already open.**
