# Roboc — engineering findings

Facts established by measurement. Each entry records what was believed, what was
observed, and what changed. The incident that destroyed the workspace is written
up separately in [POSTMORTEM.md](POSTMORTEM.md).

---

## 1. Interop foundation

`Roboc.exe --selftest` exercises every primitive the engines depend on
against a real scratch directory. **17/17 passing.**

| Probe | Proves |
|---|---|
| native struct layouts | hand-written structs match the ABI byte-for-byte |
| path conversion | `\\?\` / `\\?\UNC\` / `\??\` round-trip; trailing-dot and reserved-name detection |
| volume-root detection | `\\?\D:\` keeps its separator (see the latent bug below) |
| drive-relative refusal | `D:` and `D:sub` rejected before resolution |
| volume-root refusal | `C:\`, `D:\`, `E:\` rejected by name |
| self-containment refusal | targets containing the running program or the cwd rejected |
| confirmation refusal | deletion without `--yes` rejected |
| benign-target check | an ordinary deep path is **not** refused |
| volume info | NTFS confirmed, sector size 512, **block refcounting unavailable** |
| enumeration | `FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH` counts and sizes correct |
| file create/write/read | 300 KB written and read back identical |
| FILE_BASIC/STANDARD/ID | timestamps, size, link count, 128-bit id all correct |
| read-only delete in one call | `FILE_DISPOSITION_INFO_EX` removes a read-only file where `DeleteFileW` refuses |
| tree delete | nested plus read-only tree removed |
| NTSTATUS mapping | NTSTATUS → Win32 mapping and retry/elevation classification |

The guard rows exist because of the incident, and the benign-target row exists
because a guard that refuses real work is its own failure mode.

**Environment fact:** this volume reports `FILE_SUPPORTS_BLOCK_REFCOUNTING = false`
— NTFS, not ReFS. ReFS block clone cannot be validated here and the fallback is
what runs.

---

## 2. A real bug: one second of dead time per job

**Symptom.** A 256 MB copy took 1.18–1.57 s with wild variance, while the engine's
own phase timers accounted for 276 ms.

**Cause.** `Watchdog` polled with `Thread.Sleep(1000)` and only then checked its
stop flag, so `Dispose()` waited out the remainder of a one-second sleep at exit.
The progress sampler added up to another 100 ms.

**Fix.** Wait on a `ManualResetEventSlim` instead of sleeping. Also cap worker count
at the number of pending items so a one-file job no longer creates two dozen
threads and pins two dozen megabytes of buffer.

| | before | after |
|---|---|---|
| 256 MB copy, rep 1/2/3 | 1.18 / 1.49 / 1.57 s | **0.46 / 0.46 / 0.47 s** |
| robocopy, same file | — | 0.19 / 0.19 / 0.18 s |

The absolute win matters less than the collapse in variance.

---

## 3. A wrong model, caught before it drove decisions

**Believed.** A raw sequential-write sweep showed an apparently *constant* ~0.7 ms
cost per write regardless of size (64 KB → 62 MB/s, 256 KB → 318 MB/s,
1 MB → 1530 MB/s). That looked like a fixed per-syscall tax from a filter driver,
implying the goal was purely "fewer syscalls".

**Reality.** Re-running the identical sweep gave 256 KB → 850 MB/s, a 2.7×
difference for the same code. The "constant cost" was **noise** — SSD SLC-cache
exhaustion from repeated 256 MB writes plus background activity. Per-operation cost
is not constant; larger operations amortise better, which is ordinary behaviour.

**Consequence.** The optimisation strategy built on that artifact was discarded.
Corrected strategy: buffers large enough to amortise, and overlap reads with writes
rather than chasing syscall counts. The phase timers exist because of this.

---

## 4. The measurement environment cannot support fine-grained conclusions

| Observation | Run A | Run B |
|---|---|---|
| raw 256 KB sequential write | 318 MB/s | 850 MB/s |
| raw 1 MB sequential write | 1530 MB/s | 1352 MB/s |
| robocopy /MT:32, 30 k small files | 5269 files/s | 1147 files/s |
| corpus verify (read 373 MB) | 2.4 s | **92.0 s** |

The verify number is the clearest: **2.4 s → 92.0 s for identical work.** Storage
degrades over a benchmarking session and recovers between sessions.

Ruled out by direct test: the sandbox does **not** filter by path. Writing inside
the workspace, outside it on the same volume, and on a second volume gave 1352,
1411 and 1146 MB/s — all within noise.

**Implication.** Absolute performance and fine relative performance must be
validated on the target machine, outside this environment, ideally with the
NativeAOT build. Benchmarks here catch order-of-magnitude regressions and
correctness, not percentages.

---

## 5. Single large file: the overlapped pipeline

The synchronous read-then-write loop serialises reads and writes, so throughput is
bounded by their *sum*. Buffer sweep on that path:

| buffer | throughput |
|---|---|
| 256 KB | 362 MB/s |
| 1 MB | 559 MB/s |
| 2 MB | 645 MB/s |
| 4 MB | 641 MB/s |
| 8 MB | 569 MB/s |

It plateaus around 645 MB/s no matter the buffer size, because the buffer size does
not change the fact that each read finishes before its write starts.

`OverlappedPipeline` issues `depth - 1` reads ahead and keeps one write in flight.
Reads are issued `depth - 1` ahead and **not** `depth` ahead on purpose: at `depth`
the next read lands in the buffer currently being written, and the device would
still be reading it. That bug does not fail loudly, it corrupts data, so it is
called out in the code as well.

| 256 MB single file, best of 4 | time | throughput |
|---|---|---|
| Roboc, synchronous | 0.40 s | 645 MB/s |
| **Roboc, overlapped** | **0.092 s** | **2797 MB/s** |
| robocopy | 0.115 s | 2233 MB/s |

4.3× on this path, 1.25× ahead of robocopy, with run-to-run spread down to
0.092–0.119 s. Defaults: `OverlappedDepth = 3`, `LargeChunkSize = 2 MiB`. Buffers
are allocated lazily, only for files at or above `LargeFileThreshold`.

---

## 6. Unresolved: filename corruption in the publish step

**Symptom.** Files appeared at the destination under names with three CJK
characters appended — `zero.bin䍪` — with correct size and content, while the copy
reported success. The system locale is Chinese and the only CJK text in the process
comes from `FormatMessage`.

**Evidence.**

| Observation | Detail |
|---|---|
| Appended sequence | the *same* three characters across independent processes |
| Name prefix | always correct; only a suffix was added |
| Affected files | once the three largest, once a zero-byte file — no size pattern |
| Worker count | single worker never reproduced; multiple workers did |
| Our buffer | round-trip comparison of the constructed buffer always passed |
| 20 reps × 16 boundary sizes | 0 problems, 0 stray names, 0 guard trips |
| Full corpus, 30,001 files | byte-for-byte intact |

A constant suffix across processes is what reading past the end of a buffer until an
incidental NUL looks like, not random stack garbage.

**A control experiment was run and produced useful data:** three independent rename
mechanisms, none of them Roboc code — `File.Move`, `Move-Item`, `cmd move` —
each 4000 renames: **0 corrupted in all three.** That weakens the "the sandbox
corrupts renames" hypothesis and points back at the rename buffer.

**Status: contained, not diagnosed.** What is in place:

1. The buffer is a CLR `byte[]` (guaranteed zero-initialised), which also fills the
   4-byte padding hole at offset 4..8 between `Flags` and `RootDirectory` that no
   field write reaches.
2. A NUL terminator sits just past the name, so any scan past `FileName` stops on
   our NUL instead of walking into an adjacent frame.
3. `dwBufferSize` stays at the documented minimum.
4. The buffer is read back and compared before the call.
5. **The file is confirmed to exist at the intended path after the rename**, so a
   residual misbehaviour surfaces as a reported per-file failure rather than a
   silently misnamed file.

---

## 7. Delete: the same-volume detach

A directory move into a sibling staging directory is a single directory-entry
update. No file data is touched, so the item count is irrelevant.

| Workload | Result |
|---|---|
| 303 items (nested + read-only) | **0.102 s wall**, 0.01 s engine-internal |
| 3001 items | **0.095 s wall**, 31,698 items/s |
| undo after a staged delete | **303/303 items restored**, staging directory removed |
| `--direct` (unlink in place) | works; 201 items in 0.37 s, 541 items/s |
| single read-only file, direct | works |
| empty directory | works |

The staged path is roughly 300× faster than the direct path on the same tree, which
is the expected consequence of replacing N unlinks with one rename.

**Design correction found by testing:** the undo record was first written to
`%LOCALAPPDATA%`. A locked-down profile denies that write, so staging succeeded
while the record was silently lost and undo restored nothing. The record now lives
**inside the staging directory**, whose parent is writable by construction.

---

## 8. Deliberate trade-offs

**Atomic publish is kept even though it costs a syscall.** Every file goes to a
hidden sibling temp name and is published with `FILE_RENAME_FLAG_POSIX_SEMANTICS`.
That is one extra operation per file. It is kept because it is the only guarantee
that the destination path is never observed in a partial state, and because the
per-syscall tax that would have justified dropping it does not exist (§3).

**Flushing is on by default for large files.** `--no-flush` exists for like-for-like
comparison against tools that never flush. Measurement showed the flush costs almost
nothing here (1.18 s vs 1.21 s), so durability is close to free on this hardware;
that may not hold elsewhere.

**`asInvoker`, not `requireAdministrator`.** Unconditional elevation would raise a
UAC prompt on every context-menu invocation, make Administrators the owner of every
created file, and hide mapped network drives from the process. Elevation is per-job
and on demand instead.

**Deletion requires `--yes`, and stages by default.** See POSTMORTEM.md.

---

## 9. Status against the plan

| Phase | State |
|---|---|
| 0 — toolchain, skeleton | done (.NET SDK 10.0.401, user-local, no admin) |
| — interop layer | done, 17/17 self-test |
| — benchmark corpus + baselines | corpus generator restored; comparator is manifest-backed |
| 1 — enumeration + copy engine | **restored and verified**; overlapped large-file path ahead of robocopy |
| 2 — delete engine | **restored, guarded, verified**; POSIX delete + same-volume detach + undo window |
| 3 — journal / resume | interface only (`IJobJournal`); no persistence yet |
| 4 — UI | not started |
| 5 — context menu, elevation | not started |
| 6 — hardening | watchdog done; reparse points/ADS/ACL outstanding |
| 7 — NativeAOT packaging | not started; startup 131 ms framework-dependent, expected ~30–50 ms AOT |

### Known gaps, stated plainly

- Reparse points are **detected and reported as skipped**, not recreated. A junction
  or symlink in the source makes the copied tree incomplete, loudly.
- Hard links are **not recreated**; each link is copied as a separate file. The
  `FILE_ID_INFO` plumbing is in place.
- Alternate data streams are **not copied**.
- The journal does not persist. The temp file and atomic rename already provide
  crash *safety*, but not continuation.
- `--verify` (checksum comparison) is accepted and not implemented.
- Recycle-bin deletion is not implemented; `--direct` is permanent and the staged
  path is a same-volume detach, not a Recycle Bin entry.
- A staging directory whose window expired while no process was running is reclaimed
  on the next delete in that same parent directory, or by `undo`; there is no
  background service to catch it otherwise.
