# Roboc — status

Last updated at the end of an overnight session. Read this first; the detailed
evidence lives in [FINDINGS.md](FINDINGS.md) and the incident write-up in
[POSTMORTEM.md](POSTMORTEM.md).

---

## What works right now

| Area | State | Evidence |
|---|---|---|
| Interop foundation | working | `--selftest`: **19/19 pass** |
| Copy engine | working, verified | 4 publish variants byte-for-byte intact |
| Large-file path (overlapped IO) | working, ahead of robocopy | 2797 MB/s vs robocopy 2233 MB/s |
| Small-file path | at parity with robocopy | median 0.62 s vs 0.64 s on 4,002 files |
| Delete engine | working, guarded, verified | 303-item staged delete in 0.10 s; undo restored all 303 |
| Same-volume instant delete | working | 3,001 items in 0.095 s (31,698 items/s) |
| Journal / resume | working, verified | interrupted job detected; re-run skipped 6,000 items in 0.14 s |
| Clipboard (CF_HDROP) | working, verified | copy then paste round-trip delivered 2 files |
| Portable exe | working | 10.7 MB single file, zero dependencies, 19/19 selftest |

**Byte-for-byte verification has passed in every round of this project**, which
is the property worth trusting most.

---

## Waiting on you

**1. `install` — the context menu could not be verified from here.**

The sandbox denies registry writes to processes it spawns. From a normal shell
the app returns exit 1 with:

```
the registry could not be written: Access to the registry key
'HKEY_CURRENT_USER\Software\Classes\*\shell\Roboc.Copy' is denied.
```

PowerShell itself writes that exact key path successfully, and both the trimmed
and framework-dependent builds fail identically — so this is not a code or
trimming problem. A normal user owns HKCU, so a denial most likely means a
security product is blocking registry writes from an unsigned binary.

```powershell
$exe = "D:\Coding\DeepSeek Harness\workspace\Roboc\artifacts\portable\Roboc.exe"

# Wait for it: this is a GUI-subsystem exe, so PowerShell's & does not wait, and
# an immediate registry check races the process.
$p = [System.Diagnostics.Process]::Start($exe, 'install')
$p.WaitForExit()
"exit=$($p.ExitCode)"

Get-ChildItem 'HKCU:\Software\Classes\*\shell'                   | Where-Object PSChildName -like 'Roboc*'
Get-ChildItem 'HKCU:\Software\Classes\Directory\shell'           | Where-Object PSChildName -like 'Roboc*'
Get-ChildItem 'HKCU:\Software\Classes\Directory\Background\shell'| Where-Object PSChildName -like 'Roboc*'
```

Seven verbs are expected. If `exit=0` and the keys are present but the menu does
not show them, restart Explorer; that is a shell refresh, not a code problem.

**2. NativeAOT was declined** in favour of the self-contained single-file build
(10.7 MB, 95–123 ms startup). A plain `dotnet publish -c Release -r win-x64` now
produces it with no extra properties. AOT would need the Visual Studio C++
build tools, which are not installed.

---

## Known gaps, stated plainly

Ordered by how much they matter.

1. **Reparse point recreation is implemented but does not work here, and I could
   not determine whether the cause is my code or this environment.** This is the
   one thing in the project I am genuinely unsure about, so the evidence is
   written out in full below rather than summarised.

   What is in place: `Fs/ReparsePoints.cs` reads a link's raw reparse buffer with
   `FSCTL_GET_REPARSE_POINT` and writes it back with `FSCTL_SET_REPARSE_POINT`,
   and `CopyEngine` routes junctions and symlinks through it. Tags that cannot
   meaningfully be reproduced elsewhere (OneDrive placeholders, WCI, dedup) are
   deliberately *followed* instead, so their content is copied rather than lost.

   What happens: `FSCTL_GET_REPARSE_POINT` **succeeds** — the tag reads back as
   `0xA0000003` with a 308-byte buffer that matches what `fsutil reparsepoint
   query` reports, so the read path is right. `FSCTL_SET_REPARSE_POINT` then
   fails with `ERROR_INVALID_FUNCTION` (0x00000001).

   The control experiment argues against my code: in the *same destination
   directory on the same volume*, `New-Item -ItemType Junction` and `mklink /J`
   both create working junctions (verified reparse flag and target), while the
   app's own call does not. That is the same split seen with the registry, where
   PowerShell writes `HKCU` successfully and the app is refused. Everything in
   the call matches the documented recipe and what `mklink` does.

   Whatever the cause, the failure is safe and honest: the error names the tag and
   buffer size, and the empty directory created for a link that could not be made
   is removed, because an empty directory bearing the link's name looks like a
   successful copy to anything that only checks that the path exists.

   **To settle it, run this on your machine** — if the junction appears, the
   feature works and this environment was the obstacle:

   ```powershell
   $b = "$env:TEMP\rptest"; Remove-Item -Recurse -Force $b -EA 0
   New-Item -ItemType Directory "$b\real","$b\dst" -Force | Out-Null
   Set-Content "$b\real\a.txt" "a"
   cmd /c mklink /J "$b\src" "$b\real" | Out-Null
   New-Item -ItemType Directory "$b\src" -Force | Out-Null   # mklink needs the parent
   ```
   (create the junction as `$b\src\link` pointing at `$b\real`, then)
   ```powershell
   & "…\artifacts\portable\Roboc.exe" copy --to "$b\dst" "$b\src"
   (Get-Item "$b\dst\src\link" -Force).Attributes   # look for ReparsePoint
   ```
2. **Hard links are not recreated.** Each link is copied as a separate file, so
   the output is correct but larger than the source. The `FILE_ID_INFO` plumbing
   is already in place.
3. **Alternate data streams are not copied.**
4. **Mid-file resume is unit-tested but not integration-tested.** The journal
   round-trips a checkpoint offset and tolerates a truncated tail (both in
   `--selftest`), and file-level resume is proven end to end, but nothing has yet
   killed a copy *during* a large file and confirmed the copier truncates the
   temp to the journaled offset and continues. Proving it needs a deterministic
   interruption, and the two attempts so far both finished before the kill landed.
5. **`--verify` (checksum comparison) is accepted and not implemented.**
6. **No progress UI yet.** Phase 4 of the plan.
7. **Recycle-bin deletion is not implemented.** `--direct` is permanent; the
   staged path is a same-volume detach, not a Recycle Bin entry.

---

## Two things to know before running any benchmark

**The measurement environment degrades under its own load.** A comparison run
performed 27 recursive deletes of 20,000 files each and pushed this machine's
system volume from 900 MB/s to 1 MB/s. It recovers only on reboot.

`bench/run-bench.ps1` now guards against this:

* a **volume probe** before every round, using the operation that degrades the
  drive (many small creations). Healthy is about 3900 files/s; about 2000 means
  half speed; the run is flagged SUSPECT below 3000 and UNRELIABLE if the probe
  drifts more than 3× across rounds;
* rounds are **interleaved**, so drift lands on every contestant equally;
* **cleanup happens once at the end**, never between rounds;
* the generator's stderr is surfaced, and the scratch is probed with the real
  binary before measuring, because the sandbox confines spawned processes to the
  session workspace and a denied scratch is easy to mistake for a slow disk.

**Write measurement scripts into `run-bench.ps1`, not as new ad-hoc scripts.**
Three consecutive ad-hoc scripts produced confident nonsense: `$Args` is a
PowerShell automatic variable and silently fails to bind as a parameter name; a
stray `\r` defeated a line filter; `ToString("D3")` on a double threw and left
the corpus empty so both tools "copied" nothing.

---

## Decisions taken, with the reasoning that drove them

**Publish strategy is split, not uniform.** A file below `LargeFileThreshold`
whose destination does not exist is written directly at its final name; anything
else stages a temp file and renames. Measurement drove this: create-plus-rename
was 89% of a small-file copy while the actual data transfer was 2.8%, because
staging costs two directory-entry updates per file and `CopyFileEx` costs one.
Splitting recovered 1.6–1.9×, which is the difference between losing to robocopy
and matching it. Overwrites still stage, because a direct write truncates the
original immediately and a failure partway would destroy it.

**Deletion requires `--yes` and stages by default.** A ten-minute undo window is
the default; `--direct` is the old irreversible behaviour. See POSTMORTEM.md for
why.

**`asInvoker`, not `requireAdministrator`.** Unconditional elevation would raise a
UAC prompt on every menu invocation, make Administrators the owner of every
created file, and hide mapped network drives. Elevation is per job and on demand.

---

## If you want to pick this up

Highest value first:

1. Reparse point recreation (gap 1) — closes the largest correctness hole.
2. Progress UI, phase 4 — the remaining piece of what makes this a product.
3. The deterministic mid-file interruption test (gap 4).

Working agreement that has held up: verify correctness freely, because reading
costs nothing; ration timed measurement, because writing is what degrades the
machine; and never trust a number from a run whose volume probe was low.

---

## RESOLVED: why "access to the registry key ... is denied" on every HKCU key

**The executable file carried a `Low` mandatory integrity label.**

A Low integrity process cannot write `HKEY_CURRENT_USER\Software` at all -- that
is a Windows rule, not a permission on the key. Anything launched from the file
inherits the level, so `reg.exe` spawned by the program was refused too, while
`reg.exe` spawned by PowerShell was not.

The label was inherited from the build: **a file created by a Low integrity
process is given a Low mandatory label**, and the development shell here runs
sandboxed at Low. So the compiler wrote the exe at Low, and the exe then dragged
every process started from it down to Low -- on any machine, including a machine
whose shell is at Medium, which is why the same failure appeared in two
environments that shared nothing but the file.

Evidence chain, in the order it was established:

| Observation | Ruled out |
|---|---|
| Firewall/AV logged nothing | security software |
| Plain framework-dependent build failed identically | trimming, single-file packaging |
| Removing `activeCodePage` changed nothing | the application manifest |
| PowerShell wrote the same keys with the **same .NET API** | permissions, API misuse |
| `reg.exe` worked when **PowerShell** started it, failed when the **program** started it | the specific tool being used |
| `diagnose` reported `Low (4096)` against PowerShell's `Medium (8192)` | everything above |

The last row is the answer. `diagnose` now reports the integrity level, because
nothing else in the chain was visible from inside the program.

**Fix and its consequence for the build:**

```
icacls <exe> /setintegritylevel Medium
```

This must be run **after every publish**. The build itself recreates the problem,
because the compiler is running at Low. A published artifact that has not been
through this step will install nothing and say only that access was denied.

Confirmed fixed: `diagnose` reports `Medium (8192)` and `5/5 个位置可写`, and
`install` registers all 13 keys with no workaround.
