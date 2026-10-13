# Roboc User Guide

[中文](使用说明.md) · **English**

**This document covers every feature and every switch.** Technical detail and maintenance
notes are in the [Developer Guide](Developer%20Guide.md).

---

# 1. The context menu

Select a file or folder in Explorer, right-click, and look under **Roboc**.

| Entry | What it does | When it appears |
|---|---|---|
| **Roboc → Copy** | Puts the selection on the Roboc clipboard | Always |
| **Roboc → Paste** | Pastes into the current folder | **Only after a copy has been made** |
| **Roboc → Delete** | **Deletes permanently**, bypassing every recycle bin | Needs a selection |
| **Roboc → Recycle** | Moves into the Roboc recycle bin | Needs a selection |
| **Roboc → Recycle bin** | Opens the bin folder | Always |
| **Roboc → Settings** | Opens the settings window (removal is in there) | Always |

**When the selection is already inside the bin, Recycle offers to restore instead.** An item
already in the bin cannot be recycled a second time, and putting it back is the only thing
left that means anything.

---

# 2. Installing and removing

## Installing: unpack and double-click

```
1. Unpack anywhere
2. Double-click Roboc.exe
        ↓
   The context menu is registered and "Roboc is ready" appears
```

**No command line, no administrator rights.**

Double-clicking again only reports that it is already usable; it does not register twice.

## Removing: two steps

```
1. Right-click any file → Roboc → Settings → click Remove
2. Confirm, then delete the program folder as instructed
```

**The window states plainly what it will do:**

- ✅ **What it clears**: the context menu (18 registry entries), the Send To entries, program
  data, temporary traces
- ⚠️ **What it leaves alone**: **your recycle bins** — they hold files for you to recover or
  delete yourself
- 📁 **Which folder to delete afterwards**

**After removal the folder holds nothing but `Roboc.exe` and `assets\`, so deleting the
folder is the whole of it.**

**That is what "portable" means here**: settings live beside the executable, nothing is
written to the registry except the keys the context menu requires, and nothing is left in
AppData.

---

# 3. The settings window

**Right-click → Roboc → Settings.** One switch per optional feature, **each stating what it
buys, what it costs and what it defaults to.**

| Switch | Default | In one line |
|---|---|---|
| **Keep hard links** | **on** | Several names for one file stay linked at the destination, using no extra space |
| **Resume interrupted copies** | **on** | Run the same copy again and it continues |
| **Keep data streams (ADS)** | **off** | Preserves the "downloaded from the internet" mark; costs 3.8% to 7.4% of throughput |
| **Verify contents after copying** | **off** | Byte-for-byte comparison, catches silent write errors; costs 54% more time |
| **Publish atomically** | **on** | The destination path is never seen half-written |

**Select any switch and the lower half of the window explains it.** The default is marked on
every row — **"what does this do" and "what was it before I touched it" are different
questions, and only the second one lets you undo an experiment.**

**Settings are stored in `<program folder>\Roboc.settings.json`**, and disappear with the
folder.

**The command line can still override them for a single run**: `--ads` / `--no-ads`,
`--verify` / `--no-verify`, `--hardlinks` / `--no-hardlinks`. **Both directions exist** — a
feature enabled in settings must be switchable off for one run.

---

# 4. What is on without asking

**None of this needs any action; it works from the moment it is unpacked.**

| Capability | Detail |
|---|---|
| **Multi-threaded copying** | 4 to 16 threads by default, decided from the core count |
| **Block cloning** | Effectively instant on the same NTFS or ReFS volume (server-side, never entering user mode) |
| **Hard links preserved** | Several names for one file **remain linked** at the destination |
| **Reparse points rebuilt** | Symbolic links and junctions are recreated as links |
| **Resumable** | Journals live in `.roboc\jobs\` inside the program folder |
| **Atomic publication** | The destination path is **never seen half-written** |
| **Progress window** | Anything over about two seconds shows progress, a rate graph and time remaining, and can be paused or cancelled |

---

# 5. Every switch

| Switch | Default | Effect | Cost |
|---|---|---|---|
| `--ads` | **off** | Keeps alternate data streams | 3.8% to 7.4% of throughput |
| `--verify` | **off** | Byte-for-byte check after copying | 54% more time |
| `--checkpoint-interval <MB>` | 32 | How often resume state is recorded | No measured benefit, see below |
| `--workers <n>` | automatic | Thread count | — |
| `--buffer <bytes>` | 1 MB | Write buffer per operation | — |
| `--overwrite <mode>` | `skip` | What to do about existing names | — |
| `--no-resume` | — | Turns resuming off | — |
| `--no-flush` | — | Does not force large files to disk | **Does not affect checkpoint flushing** |
| `--always-direct` | — | Always writes directly, no temporary file | Loses atomicity |
| `--timing` | — | Prints per-stage timings | — |

---

## `--ads`: keep alternate data streams (**off by default**)

```powershell
roboc copy --to "D:\destination" --ads "E:\source"
```

**Alternate data streams (ADS)** are extra named data NTFS lets a file carry. The common one
is **`Zone.Identifier`**, the "mark of the web" that **almost every file from a browser, a
mail attachment or an extracted archive carries**. Windows uses it to decide whether to warn
that a file came from another computer, and whether to open Office documents in Protected
View.

**Not copied by default, because:**

- **Detecting streams costs one extra system call per file**, measured at **19 µs per file**
  (for comparison, reading a file's timestamp costs about 16 µs), which works out at roughly
  **3.8% to 7.4%** of throughput across very many small files
- **The great majority of files have no streams at all**, and every one of them pays for the
  detection
- Many people **specifically want** the mark gone once a file is on their own disk

**When to turn it on:**

| Situation | Advice |
|---|---|
| Backups and archives | **On** — streams are part of the data |
| Moving downloaded files | **On** — otherwise a security prompt disappears silently |
| Everyday tidying, many small files | **Off** |
| Destination is FAT32, exFAT or a network share | **It will do nothing** — those filesystems cannot carry streams |

**With it on**: streams are copied one at a time, and a failure **does not abort the job** or
count as a file failure, but **is listed in the results**.

**With it off**: no detection, no copying, no reporting — deliberately, so that the detection
cost is never paid.

---

## `--verify`: check the contents after copying (**off by default**)

```powershell
roboc copy --to "D:\destination" --verify "E:\source"
```

**When the copy finishes, every destination file is read back and compared with its source,
byte for byte.**

### What it guards against

**Not an interrupted copy** — atomic publication already covers that.

**It guards against writing that silently goes wrong.** Every layer below has its own
protection, and **not one of them checks whether the bytes that were requested to be written
are the bytes that ended up stored**:

| Protection | Covers | Does not catch |
|---|---|---|
| SMB checksums | The network transfer | Host memory, storage controller |
| USB / SATA CRC | The cable | Driver faults, a wrong LBA |
| NVMe ECC | The flash cell | Host memory, firmware faults |
| TCP checksums | The network packets | The application's buffers |

**Flipped bits in memory, driver faults and controllers writing the wrong sector all produce
a file of the right size, with the right timestamp, that opens perfectly — and holds the
wrong contents.**

**Windows' own copy does not check this.** The `CopyFileEx` that Explorer uses **has no
verification option at all**, and neither does `robocopy`.

### What it costs (measured)

601 files, 764 MB, median of three runs each:

```
without --verify :  0.84 s     911 MB/s
with --verify    :  1.29 s     591 MB/s
                    54% more
```

**The reason is direct: verification has to read the source and the destination.**

### When to turn it on

| Situation | Advice |
|---|---|
| Copying to an **external disk or USB drive** | **On** — the medium and the cable are the weak points |
| Copying to a **network location** | **On** |
| **Hardware is suspect** | **On** — this is the only way to find out |
| Backing up **anything irreplaceable** | **On** |
| Between NVMe drives in the same machine | Depends how much the data matters |
| Everyday tidying | **Off** |

### What happens when it fails

- **The job continues** — the remaining files are still copied
- **The destination file is not deleted** — the problem is reported so you can decide.
  **Deleting automatically could destroy the only remaining candidate copy**
- The report names **which file** and **which byte the difference starts at**

---

## `--checkpoint-interval`: how often resume state is recorded (**rarely worth changing**)

```powershell
roboc copy --to "D:\destination" --checkpoint-interval 128 "E:\source"
```

**In megabytes, 32 by default.**

Every time resuming records "N bytes have been written", **those N bytes must first reach the
disk** — otherwise a power loss would leave a journal claiming to hold data that was never
persisted, and a resume would skip straight over it. **So this value sets how far the writer
may run ahead of the disk.**

**The default was once raised to 256 and then put back**, because the measurements at the
time contradicted each other (32 MB: 1.02 s, 256 MB: 1.18 s, 1 GB: 4.11 s, and turning
resuming off was slower still) — sequential runs were being dominated by cache eviction.

**Later, controlled measurement — warming the source into cache before every run — showed the
parameter is not a lever at all:**

```
32 MB / 128 MB / 512 MB / resuming off  →  median 0.93–0.99 s, ranges entirely overlapping
```

**So there is no reason to change it, unless you measure something different on your own
machine.**

---

# 6. The recycle bin

## Where it is

```
<volume>:\Roboc_bin\
```

**One bin per volume**, independent of the Windows one, and the two do not interfere.

## Getting things back

Select the item in the bin folder → right-click → **Roboc → Recycle**, and it offers to
restore the original location.

**The original location comes from a record.** If something with that name is already there,
the restore is refused — **it will not overwrite**.

## How it differs from the Windows recycle bin

| | Roboc bin | Windows bin |
|---|---|---|
| Location | `<volume>:\Roboc_bin\` | `$Recycle.Bin` |
| Emptying | Delete the contents in Explorer | Right-click, empty |
| Same name | **Refuses a second copy** | Renames automatically |

---

# 7. Irreversible actions

**`Roboc → Delete` and emptying the bin cannot be undone**, and the program asks first.
**Read the numbers before confirming.**

Emptying the bin from the command line **requires** `--yes`:

```powershell
roboc reclaim --empty --yes
```

Without it the command is refused — **there is no other route to emptying the bin**.

---

# 8. Protections on deletion

The `delete` command has three guards, and tripping any one of them **refuses the command
outright**:

| Guard | Catches |
|---|---|
| Drive-relative paths | Anything shaped like `D:`, which Windows reads as "the current directory on D", not `D:\` |
| Volume roots | An entire volume |
| The program's own folder | Its folder and every ancestor of it |

**These three came from a real accident** — in an earlier life, `delete D:` removed an entire
working tree.

---

# 9. Common questions

**The Paste entry is greyed out.**

→ Use **Roboc → Copy** on what you want to copy first. Paste only appears once the clipboard
holds something.

**Recycling says "the bin already holds something from that location".**

→ The same original path is already in the bin, and the program refuses a second copy —
otherwise there would be no way to tell which one to restore. Deal with the existing one
first.

**The "downloaded from the internet" mark is gone after copying.**

→ That is the default. Add `--ads` to keep it; see section 5.

**The progress window did not appear.**

→ Operations finishing in under about two seconds do not show it. It is there for long jobs.

**Can an operation be cancelled part way?**

→ Yes, with Cancel on the progress window, which asks for confirmation. Finished items are
kept; anything not yet started is abandoned.

**Can an interrupted copy be resumed?**

→ Yes. The journal is in `.roboc\jobs\` inside the program folder, and running the same copy
again continues from where it stopped.

**What happens to hard links and data streams when copying to a USB drive?**

→ **Hard links**: FAT32 and exFAT do not support them, so they become independent copies
(same contents, more space). **Data streams**: also unsupported, and **not copied by
default** either. With `--ads`, a destination that cannot hold them is reported.

**Why is copying large files much slower than robocopy?**

→ **Because Roboc waits for the data to actually reach the disk, while robocopy returns as
soon as the write is cached.**

Sustained write speed on this volume measures **967 MB/s**; Roboc reaches **831 to
1034 MB/s** (86% to 107% of the physical limit) while robocopy reports **11,100 MB/s** —
eleven times what the volume can physically do. That number describes the cache, not the
disk.

**The wait is what makes resuming correct:** a journal must not claim to hold bytes that a
power loss could take away.

**`--verify` passed, but I am still uneasy.**

→ Verification reads **what the destination file holds now**. A bit that flips afterwards
flips after the check — **no tool can prevent that**.

---

# 10. Limitations and known gaps

| Item | Status |
|---|---|
| **Data stream copying** | Supported, `--ads`, **off by default** |
| **Content verification** | Supported, `--verify`, **off by default** |
| Hard links preserved | Supported, on by default; **beyond one million, files are copied out and the fact reported** |
| Reparse points | Supported, on by default |
| Encrypted files (EFS) | **Not specifically supported** |
| Sparse files | **Not specifically supported** |

---

# 11. Design principles

**Accepting an argument and doing nothing with it is worse than refusing it.** Refusing costs
a second; a false assurance can cost the data it was supposed to protect.

So: the things it **can** do but does not do by default — `--ads`, `--verify` — state their
cost and when they are worth it; and the things it **cannot** do are refused outright with a
reason, rather than accepted in silence.
