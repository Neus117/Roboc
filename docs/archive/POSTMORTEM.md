# Postmortem: `delete D:` removed the entire workspace

**Date:** the session in which the delete engine was first exercised end to end.
**Severity:** unrecoverable data loss.
**Status:** root cause fixed and covered by a regression test.

## What happened

While testing the delete engine's guards, I ran:

```
Roboc.exe delete --quiet "D:"
```

The intent was to confirm that the "refuse a volume root" guard fired. Instead the
command deleted the contents of `D:\Coding\DeepSeek Harness\workspace`, which
included the entire Roboc source tree and two files that predated this
project (`composer-enter-swap/`, `build_umi_doc.py`).

## Root cause

`D:` is not "the D: drive" to Windows. It is a **drive-relative** path meaning
"the current directory on D:", and `Path.GetFullPath("D:")` resolves it against
whatever per-drive current directory the process inherited. In this case it
resolved to the workspace.

The guard existed but ran on the **resolved** path:

```csharp
string extended = PathUtil.TrimTrailingSeparator(PathUtil.ToExtended(raw));
GuardAgainstVolumeRoot(extended, raw);   // too late: `D:` is now a normal directory
```

By the time it ran, the value was an ordinary directory path, indistinguishable
from any other, so nothing was refused. The engine then did exactly what it was
built to do: a fast, thorough, irreversible delete.

Two aggravating factors:

1. **The permanent-delete path bypasses the Recycle Bin entirely** (POSIX
   semantics), so there was no recovery route.
2. **A second bug hid the first.** My test passed `"D:\"` on the command line,
   where the trailing backslash escapes the closing quote and the argument
   arrives as `D:"`. The output said "path does not exist", which read as a
   wording problem rather than "the guard never ran". I moved on instead of
   investigating why a guard was reporting the wrong thing.

## What the guards are now

| Guard | Rejects | Runs on |
|---|---|---|
| `GuardDriveRelative` | `D:`, `D:sub` | the **raw** argument, before resolution |
| `GuardAgainstVolumeRoot` | `C:\`, `\\?\UNC\srv\share\` | the resolved extended path |
| `GuardAgainstSelf` | any target containing the running program's directory or the current working directory | the resolved extended path |
| Confirmation | everything, without `--yes` | before any path work |

Ordering is the fix: the drive-relative check must see the un-resolved text,
because resolution is what destroys the information needed to reject it.

A latent bug was found in the same pass: `TrimTrailingSeparator` turned `\\?\D:\`
into `\\?\D:`, which is not a valid path, so `GetFileAttributes` failed on a
volume root and any guard keyed on "does this path exist" passed for the wrong
reason. That is fixed too.

## Test gap that let it through

The delete engine had no test that ran **dangerous inputs through the real entry
point**. It had been exercised only on ordinary directories I had just created,
which is precisely the case where nothing goes wrong.

`SelfTest` now contains a guard section that calls `DeleteEngine.Run` with each
dangerous input and asserts on the refusal message, plus a case asserting that a
benign deep path is **not** refused -- over-zealous guards that block real work
are their own failure mode.

## What should have happened instead

1. Destructive verbs should have required an explicit affirmation from the start.
   `--yes` was added afterwards; it should have been there before the first run.
2. A guard whose purpose is to refuse something should have been tested with the
   input it exists to refuse, not with the inputs that are supposed to succeed.
3. When a guard produces a confusing message, that is a signal the guard is not
   doing what it looks like it is doing. "Path does not exist" from a path that
   plainly exists was a loud hint that I read as a cosmetic issue.
4. Testing a delete tool against live data is itself the hazard. The scratch
   directory for destructive tests must be disposable, and it must be created
   before the tool is pointed at anything.

## Design changes that came out of it

- **Undo window by default.** A delete now detaches the target to a same-volume
  staging directory (a single directory-entry update, so item count is
  irrelevant) and keeps it for a configurable window. `roboc undo <dir>`
  brings it back. `--direct` restores the old irreversible behaviour.
- **The undo record lives inside the staging directory.** It was first written to
  `%LOCALAPPDATA%`, which a locked-down profile denies; the staging then
  succeeded while the record was silently lost, and undo restored nothing. The
  staging directory's parent is writable by construction.
- **Confirmation default.** Without `--yes` the command refuses; from a terminal
  it asks the caller to type `DELETE` after printing the target list.

## Unresolved

- `D:\Coding\DeepSeek Harness\workspace\composer-enter-swap\` and
  `build_umi_doc.py` were not authored by this project and could not be rebuilt.
- No shadow copies or restore points were accessible from the session to recover
  them.
