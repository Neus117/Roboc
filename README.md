<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/icons/Roboc-dark.png">
    <img src="assets/icons/Roboc.png" width="128" height="128" alt="Roboc">
  </picture>
  &nbsp;&nbsp;
  <img src="assets/icons/paste.png" width="128" height="128" alt="Paste">
  &nbsp;&nbsp;
  <img src="assets/icons/delete.png" width="128" height="128" alt="Delete">
  &nbsp;&nbsp;
  <img src="assets/icons/recycle.png" width="128" height="128" alt="Recycle">
</p>
<h1 align="center">Roboc</h1>

<p align="center">
  <a href="README.zh-CN.md">中文</a> · <strong>English</strong>
</p>

<p align="center">
  <strong>A portable copy and delete tool for Windows</strong><br>
  built into the Explorer context menu
</p>

**Made for very large numbers of small files.** Explorer stalls on the progress bar when a
copy runs into tens of thousands of files; Roboc does the same work with multiple threads,
block cloning and atomic publication, and runs at close to the limit of the disk.

**Portable and self-contained.** Unpack it and it works; double-click once and the menu is
registered; when you are done, remove it from the settings window and delete the folder.
**It leaves nothing behind.**

---

## Getting started

```
1. Unpack anywhere
2. Double-click Roboc.exe
        ↓
   The context menu is registered and "Roboc is ready" appears
```

**Nothing to install, no administrator rights needed.**

Select a file or folder in Explorer afterwards and right-click:

| Context menu | Copy progress | Settings |
|---|---|---|
| ![Context menu](docs/images/context-menu.png) | ![Progress](docs/images/progress.png) | ![Settings](docs/images/settings.png) |

<p align="center">
  <em>Six entries · rate graph and time remaining · every switch states what it costs</em>
</p>

| Entry | What it does |
|---|---|
| **Roboc → Copy** | Puts the selection on the Roboc clipboard |
| **Roboc → Paste** | Pastes into the current folder (appears only after a copy) |
| **Roboc → Delete** | Deletes permanently, bypassing every recycle bin |
| **Roboc → Recycle** | Moves into `<volume>:\Roboc_bin\` |
| **Roboc → Recycle bin** | Opens the bin folder |
| **Roboc → Settings** | Turns features on and off; **Remove lives here too** |

---

## Features

### On by default

| Feature | What it means |
|---|---|
| **Multi-threaded copying** | 4 to 16 threads, decided from the core count |
| **Block cloning** | Server-side copy on the same NTFS or ReFS volume, effectively instant |
| **Hard links preserved** | Several names for one file stay linked at the destination, not copied out |
| **Reparse points rebuilt** | Symbolic links and junctions are recreated as links |
| **Resumable** | Run the same copy again and it continues where it stopped |
| **Atomic publication** | The destination path is **never seen half-written** |
| **Progress window** | Rate graph, time remaining, pause and cancel |

### Optional, and off by default

| Switch | What it costs | When to turn it on |
|---|---|---|
| **Keep data streams (`--ads`)** | 3.8% to 7.4% of throughput | Backups and moving downloaded files, or the "downloaded from the internet" mark disappears silently |
| **Verify after copying (`--verify`)** | 54% longer | External disks, USB drives, network locations, or when hardware is suspect |

**Both are in the settings window, each stating what it buys and what it costs.**

---

## Performance

Measured (601 files, 764 MB, median of three runs):

| Operation | Result |
|---|---|
| **Very many small files** | Level with the best `robocopy /MT:32` time, and **substantially more consistent** (7% spread against 114%) |
| **Delete** | **2 to 3 times faster** than PowerShell `Remove-Item`, about twice `cmd rd` |

**Why large files are slower than robocopy.** Because it waits for the data to actually
reach the disk; robocopy returns as soon as the write is cached. Sustained write speed on
this machine measures **967 MB/s**, and Roboc reaches **831 to 1034 MB/s** (86% to 107% of
the physical limit) while robocopy reports **11,100 MB/s** — eleven times what the volume
can physically do.

**That wait is what makes resuming correct:** the journal must not claim to hold bytes that
a power loss could take away.

---

## Documentation

| Document | For |
|---|---|
| [User Guide](docs/User%20Guide.md) | **Users** — every feature and switch, what each costs, common questions |
| [Developer Guide](docs/Developer%20Guide.md) | **Maintainers** — architecture, design decisions, maintenance rules, visual specification, measurements |
| [使用说明](docs/使用说明.md) · [开发者文档](docs/开发者文档.md) | The same two documents in Chinese |

---

## Building from source

**Requires** the .NET 10 SDK and Windows 11.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

The result is in `artifacts/portable/`, and the script runs `--selftest` (23 checks)
afterwards.

**The build sets an integrity label on the executable**, and that is not optional: a process
started by some toolchains runs at Low integrity, and a Low-integrity process cannot write
to `HKCU` at all, so every registry write would be refused without it.

---

## Repository layout

```
src/Roboc.App/           CLI, progress window, dialogs, settings window
src/Roboc.Core/          Copy engine, delete engine, recycle bin, shell integration, interop
tools/Roboc.CorpusGen/   Benchmark corpus generator
assets/icons/            Menu icons (copied into the package at build time)
scripts/                 Benchmarks and checks
docs/                    Documentation
```

---

## Known limitations

| Item | Status |
|---|---|
| Encrypted files (EFS) | Not specifically supported |
| Sparse files | Not specifically supported |
| Hard link count | Beyond one million, files are copied out and the fact is reported |

---

## Design principles

**Accepting an argument and doing nothing with it is worse than refusing it.**

Refusing costs a second. **A false assurance can cost the data it was supposed to protect.**
So the things this program can do but does not do by default (`--ads`, `--verify`) state
their cost plainly, and the things it cannot do are refused outright.

**Destructive operations never run on uncertain input.** Deletion has three guards (drive
relative paths, volume roots, the program's own folder), and **they came from a real
accident.**

---

## Licence and trademark

**Code**: [Apache License 2.0](LICENSE) — free to use, modify and redistribute, **including
commercially**.

**Name**: "Roboc" identifies the original work and its official distributions. **Section 6
of the licence grants no trademark rights**, so:

- ✅ You may do anything with this code, including selling it, and say that it is based on Roboc
- ❌ You may not present a modified or redistributed build as **official Roboc**, or use the
  name in a way that suggests the original author endorses it

**Official channels**: the only official source for this program is the
[GitHub repository](https://github.com/Neus117/Roboc).

**Please obtain it only from the channels above.** The licence permits anyone to use, modify
and even resell this code, so **any program with the same or a different name from any other
source was not published by this author**. Third-party distributions get no support, and the
author cannot answer for their safety, completeness or behaviour.

> If you are unsure whether the copy you have is official, build it from the source in this
> repository.

See [NOTICE](NOTICE) for details.
