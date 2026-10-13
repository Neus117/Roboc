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
| Code | 51 files, ~17,000 lines (Core 10,373 / App 6,410) |
| Self-test | **23 checks**, `--selftest` |
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
├── assets/icons/             Menu icons (copied into the package at build time)
├── docs/                     User Guide · Developer Guide · Chinese originals · images
├── scripts/                  Benchmarks and checks (results/ is gitignored)
├── src/Roboc.Core/           No UI dependency
│   ├── Copy/                 CopyEngine · FileCopier · OverlappedPipeline · Verifier
│   ├── Delete/               DeleteEngine · FastDeleter
│   ├── Fs/                   RecycleBin · AlternateStreams · ReparsePoints · DirectoryOps · PathUtil
│   ├── Shell/                ContextMenuInstaller · BinIndex · SendToMenu
│   ├── Journal/              JobJournal
│   ├── Interop/              Kernel32 · Ntdll · NativeConst · NativeStruct
│   ├── Model/                CopyOptions · RobocSettings · Lang · JobTypes
│   └── Diagnostics/          SelfTest · NativeError · Watchdog
├── src/Roboc.App/            CLI and UI
│   ├── ProgressWindow · MessageDialog · SettingsWindow · ProgressTheme · RoundedBox · AppIcon
│   └── Cli/                  MaintenanceCommands · ShellCommand · CopyCommand · DeleteCommand
│                             UninstallCommand · Args · PendingSelection · ExplorerSelection
└── tools/Roboc.CorpusGen/    Benchmark corpus generator
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

## Deletion

| Decision | Reason |
|---|---|
| **Three guards on `delete`** | Drive-relative paths, volume roots, the program's own folder — see section 6 |
| **`--yes` required** | Deleting is irreversible; an argument that deletes without confirmation is an argument that deletes by accident |
| **Recycle refuses a duplicate** | Two copies of the same original path make it impossible to know which to restore |

## Copying

| Decision | Reason |
|---|---|
| **Atomic publication on by default** | The destination path is never observed half-written |
| **Block cloning when possible** | Server-side copy on the same volume, never entering user mode |
| **Hard links preserved** | Otherwise a set of links becomes independent copies: the contents are right, the space multiplies, and later edits stop propagating |
| **`--verify` off by default** | It costs 54%, and most copies are not worth that |
| **`--ads` off by default** | Detecting streams costs 19 µs per file, and most files have none |

## Recycling

| Decision | Reason |
|---|---|
| **One bin per volume** | A move within a volume is a rename; across volumes it would be a copy |
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
| **Settings live beside the executable** | So "delete the folder" is literally true |
| **Both directions exist for every switch** | A feature enabled in settings must be switchable off for one run |
| **The language is a string, not a boolean** | A third language is a new value rather than a new field |
| **Rejecting beats accepting-and-ignoring** | See section 13 |

---

# 5. Defects that have been fixed

**These are grouped by the shape of the mistake, because the shape is what recurs.**

## A. "The code is right but the screen is blank" (4)

Rendering that fails silently, leaving something invisible rather than wrong.

## B. "The promise does not match the implementation" (7)

Eight, counting the one found while writing this: accepted arguments that were never
implemented, and claims in output that were not true of the code that printed them.

## C. "The failure never reached anyone who needed it" (4)

Errors caught and discarded, or written somewhere nobody looks.

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

## E. "Only half of it was changed" (3)

**When one problem has several paths or several layers, fixing one of them feels like fixing
it.**

| Defect | What was changed | What was missed |
|---|---|---|
| **The Mica material** | The window background's Mica was removed | **The title bar is drawn by DWM and kept its Mica** — so the bar followed the wallpaper while the body stayed neutral grey |
| **Menu icons** | `WriteFlat` called `IconFor` correctly | **`WriteCascade` — the default layout — hard-coded `quotedExe + ",0"`**, so six entries showed one icon |
| **Settings persistence** | Both `Load()` and `Save()` were written | **`Save` still used the reflection overload**, which must throw in a trimmed build |

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

## What happened

An early version ran `delete D:` — without the backslash. Windows reads that as *the current
directory on drive D*, which was the project root. It was deleted.

## Root cause

**The input was ambiguous and the program resolved the ambiguity silently.** `D:` is a legal
path, so nothing in the argument parser had any reason to object.

## The three guards now in place

| Guard | Catches |
|---|---|
| Drive-relative paths | `D:` and anything shaped like it |
| Volume roots | An entire volume |
| The program's own folder | Its folder and every ancestor |

**Any one of them refuses the command outright** rather than asking for confirmation.

## The principle that came out of it

**A destructive operation must never run on uncertain input.** Not "must warn first" — must
decline. A prompt can be dismissed by reflex; a refusal cannot.

---

# 7. Maintenance rules

## Rule 1: an empty `catch` is allowed only for enumeration, cleanup, or a read with a fallback

Every empty catch must be one of those three. Anything else is a swallowed failure. There are
roughly 78 such sites, each reviewed against that list.

## Rule 2: a new argument with a value must be registered in `Args.TakesValue`

Otherwise the parser treats the value as a positional path. `--checkpoint-interval` was
missing from it once, and the failure was `roboc: source not found: 32`.

## Rule 3: a new `CopyOptions` property must have a reader

Declared and never read means an argument that is accepted and ignored — see section 13.

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

**One problem usually has several paths or several layers.** See section 5E.

The concrete form: changed the default layout, test the default layout; changed part of a
window, look at the whole window; changed both ends of a setting, test both ends.

**And read the exit code.** Three times in one session a failure was read as a success because
only part of the output was examined — a checker that reported everything, a script that had
failed to parse, and a build that had failed to load. **The last one reported "0 warnings"
because it had not run at all.**

---

# 8. Visual specification

## The underlying principle

**Everything is declared in points and logical pixels, and scaled by DPI at draw time.** Fixed
pixel values do not survive a change of scale factor.

## Vertical rhythm (96 DPI logical pixels)

```
20   headline
46   percentage
86   current item
108  bar (8 px tall)
134  graph (92 px, fixed)
250 / 276 / 302   detail lines
320  client height
```

## Font hierarchy

| Element | Size |
|---|---|
| Progress headline | 10 pt |
| Dialog body | 10 pt |
| All buttons | 10 pt |
| Progress detail lines | **9 pt (the only exception)** |
| Percentage | 19 pt |

`CreateFontW` takes a **negative pixel height** derived from points: `height = -(points * dpi / 72)`.

## Colours

The palette is read from the system theme and accent colour at startup. Dark and light are
separate palettes, not one palette with an inversion applied.

## Window appearance

- Rounded corners: `DWMWA_WINDOW_CORNER_PREFERENCE` (33)
- Dark title bar: `DWMWA_USE_IMMERSIVE_DARK_MODE` (20, falling back to 19)
- **Caption colour**: `DWMWA_CAPTION_COLOR` (35), one step darker than the card
- **No Mica**

## The rate graph

Histogram samples in a ring buffer, not instantaneous values — instantaneous rates jitter too
much to draw as a line.

Tuning record: three horizontal and nine vertical gridlines; gridlines at
`Lighten(rgb, 0.52)`; the fill at `Lighten(rgb, 0.72)`.

**Gridlines must be drawn after the opaque fill.** Drawn before, they are invisible — which is
exactly what happened.

## GDI discipline

**Every brush, pen and region is deleted.** A process has a finite number of GDI objects, and
leaking three per repaint at five repaints a second exhausts the quota within minutes — after
which every drawing call fails silently and the card goes blank rather than wrong, which is far
harder to diagnose.

**Rounded shapes go through `RoundedBox`**, which converts the HDC to a `Graphics`, draws, and
disposes before returning. A `Graphics` holds drawing state on the device context while it is
alive and the surrounding GDI calls know nothing about it, so the two APIs are never in use at
the same moment.

---

# 9. Dialog inventory

**Confirmation** — restore, stop an operation, delete permanently, paste over existing names,
remove the program.

**Report** — ready, recycle finished, restore finished, permanent deletion failed, integrity
warning.

**Error** — no selection received, clipboard write failed, registration failed.

## Layout: keeping columns aligned under centring

Dialog bodies are centred as a block **while each line inside it is left-aligned**. Centring
every line individually would put the labels of a two-column table at different indents and
destroy the alignment that makes the numbers readable.

---

# 10. Performance data and measurement discipline

## Measurement discipline: this machine's storage is degraded by its own load

**Sustained small-file churn degrades the volume, and only a reboot restores it.** Healthy is
around 3900 files per second; one session measured 2953 falling to 1335.

**Consequences:**

- Only `scripts/run-perf.ps1` is used for comparisons. Ad-hoc measurement is not comparable.
- A run whose drift exceeds 20% is **excluded** and the number of usable rounds is reported.
- The full comparison across corpora needs **separate sessions with a reboot between them.**

## Results

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

## Measurement method is easier to get wrong than the conclusion

**Recorded because it happened repeatedly:**

- Timings were taken without discarding stderr, so a failed generation was reported as
  "generated in 0.2 seconds".
- A parameter's effect was measured sequentially, where **cache eviction dominated** and
  produced results that contradicted each other. Warming the source into cache before each run
  made the effect vanish entirely.
- A PowerShell parameter named `$Corpora` silently overwrote a `$corpora` array, because
  **PowerShell variable names are case-insensitive**.

---

# 11. Permissions

## Apache-2.0 (decided)

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

- English documentation for both guides (in progress)
- Screenshots in the README
- The `YesNo` default-button question: dialogs currently default to "Yes". **If a report ever
  arrives of a destructive action taken by reflex, this is the first place to look.**

---

# 12. One working principle

**Accepting an argument and doing nothing with it is worse than refusing it.**

Refusing costs a second. **A false assurance can cost the data it was supposed to protect.**
