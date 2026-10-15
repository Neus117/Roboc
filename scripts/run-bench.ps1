<#
.SYNOPSIS
    Self-contained benchmark: Roboc publish strategies vs robocopy,
    interleaved, with byte-for-byte verification of every result.

.DESCRIPTION
    Run this rather than writing a new ad-hoc measurement script. Three separate
    ad-hoc scripts in this project each contained a bug that produced confident
    nonsense:
      * $Args is a PowerShell automatic variable and silently fails to bind when
        used as a parameter name, so the tool received no arguments and "copied"
        20,000 files in 0.04s.
      * a stray \r defeated a line filter, hiding the phase breakdown.
      * ToString("D3") on a double threw, leaving the corpus empty, so both tools
        copied nothing and produced a meaningless comparison.

    Design points that matter:

    * Rounds are interleaved -- every contestant runs once per round -- so drift
      in storage performance lands on all of them equally. Batching by contestant
      would attribute the drift to whoever happened to run last.

    * A volume probe runs before each round, using the operation that actually
      degrades a drive here: many small file creations. A volume in a bad state
      measured 1 MB/s sequential writes once, and every number taken in that
      window was meaningless. The probe makes that visible instead of silent.

    * Every output tree is verified byte-for-byte against the corpus manifest, so
      a contestant that is fast but wrong cannot hide behind a timing number.

    * Cleanup happens once at the end. Deleting between rounds is itself a
      small-file workload, and doing it 27 times in one session was enough to
      push this machine's system volume from 900 MB/s down to 1 MB/s.

.NOTES
    PowerShell 5.1 compatible on purpose: this is a tool the user runs, and it
    must not depend on a pwsh 7 install.
#>
[CmdletBinding()]
param(
    [string]$Root,
    [string]$Scratch,
    [int]$SmallCount = 20000,
    [int]$SmallSize = 4096,
    [int]$LargeSizeMb = 256,
    [int]$Reps = 3,
    [string]$Label = 'run',
    [switch]$ReuseCorpus,
    [int]$RoboThreads = 32,
    [string]$TsWorkers = '0',
    [switch]$ComparePublish
)

$ErrorActionPreference = 'Continue'

if ([string]::IsNullOrEmpty($Root)) { $Root = Split-Path -Parent $PSScriptRoot }
if ([string]::IsNullOrEmpty($Scratch)) { $Scratch = Join-Path $Root 'scripts\scratch' }

$exe = Join-Path $Root 'src\Roboc.App\bin\Release\net10.0-windows\Roboc.exe'
$gen = Join-Path $Root 'tools\Roboc.CorpusGen\bin\Release\net10.0-windows\Roboc.CorpusGen.exe'
$corpus = Join-Path $Scratch 'corpus'
$resultsDir = Join-Path $Root 'scripts\results'

foreach ($p in @($exe, $gen)) {
    if (-not (Test-Path $p)) { throw "missing: $p" }
}
New-Item -ItemType Directory -Path $Scratch, $resultsDir -Force | Out-Null


# --------------------------------------------------------------------- helpers

function Invoke-Timed {
    param([string]$File, [string]$Arguments)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $File
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)

    # 异步读 + 超时。裸 WaitForExit() 会一直等：若被测程序弹窗或卡住，
    # 整个基准就挂在那里，而且没有输出说明它在等什么。
    # 顺序（先 ReadToEnd 再 WaitForExit）本身不是危害 —— .NET 的 Process 在
    # 第一次读时会为两个流各起后台读取线程，实测 20 KB 输出也不死锁。
    # 真正的问题是【没有超时】。
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()

    if (-not $p.WaitForExit(600000)) {
        $p.Kill()
        $p.WaitForExit(10000)
        throw "被测程序超过 600 秒未结束，已终止：$($psi.FileName) $($psi.Arguments)"
    }

    $out = $outTask.GetAwaiter().GetResult()
    $err = $errTask.GetAwaiter().GetResult()
    $sw.Stop()

    return [pscustomobject]@{ Code = $p.ExitCode; Sec = $sw.Elapsed.TotalSeconds; Out = $out; Err = $err }
}

function Get-Median {
    param([double[]]$Values)
    $s = @($Values | Sort-Object)
    if ($s.Count -eq 0) { return 0 }
    return $s[[int](($s.Count - 1) / 2)]
}

function Get-Spread {
    param([double[]]$Values)
    $s = @($Values | Sort-Object)
    if ($s.Count -lt 2) { return 1.0 }
    return ($s[$s.Count - 1] / [Math]::Max(0.0001, $s[0]))
}

function Format-Bytes {
    param([double]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:F2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:F1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:F1} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

# Many small creations is the operation that degrades a volume here, so that is
# what the probe measures. A healthy NVMe on this machine did ~3900 files/s; a
# degraded one did ~420.
function Measure-VolumeProbe {
    param([string]$Dir, [int]$Count = 200)
    try {
        $probeDir = Join-Path $Dir ('volprobe-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        New-Item -ItemType Directory -Path $probeDir -Force | Out-Null
        $buf = New-Object byte[] 4096
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        for ($i = 0; $i -lt $Count; $i++) {
            [System.IO.File]::WriteAllBytes((Join-Path $probeDir ("f$i.bin")), $buf)
        }
        $sw.Stop()
        $t = $sw.Elapsed.TotalSeconds
        cmd /c rd /s /q "$probeDir" 2>$null | Out-Null
        return ($Count / [Math]::Max(0.001, $t))
    } catch {
        return 0
    }
}

# ------------------------------------------------------- scratch writability

# The tools resolve their own paths; PowerShell creating a directory proves
# nothing about whether they can. Probe with the actual binary. The DSH file
# sandbox confines a spawned process to the session workspace, and a scratch on
# another volume then fails with a bare access-denied that is easy to misread as
# a disk problem.
$writeCheck = Join-Path $Scratch ('writecheck-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
$wc = Invoke-Timed $gen ('create --root "' + $writeCheck + '" --small-count 4 --large-count 0 --clean')
if ($wc.Code -ne 0) {
    cmd /c rd /s /q "$writeCheck" 2>$null | Out-Null
    throw ("the tools cannot write to scratch '" + $Scratch + "': " + (($wc.Err -replace "\r?\n", ' ').Trim()) +
           "  -- this is usually the file sandbox confining spawned processes to the session workspace")
}
cmd /c rd /s /q "$writeCheck" 2>$null | Out-Null
Write-Host "scratch writable by the tools: $Scratch"

# ---------------------------------------------------------------------- corpus

$corpusFiles = 0
$corpusBytes = 0L

if (-not $ReuseCorpus -or -not (Test-Path (Join-Path $corpus 'corpus.manifest'))) {
    Write-Host "generating corpus ($SmallCount x $SmallSize B, 1 x $LargeSizeMb MB) on $Scratch ..."
    $g = Invoke-Timed $gen ('create --root "' + $corpus + '" --small-count ' + $SmallCount +
        ' --small-size ' + $SmallSize + ' --large-count 1 --large-size ' + $LargeSizeMb + ' --clean')
    if ($g.Code -ne 0) {
        throw ("corpus generation failed: " + (($g.Err -replace "\r?\n", ' ').Trim()))
    }
    Write-Host ("  generated in {0:F1}s" -f $g.Sec)
}

foreach ($f in [System.IO.Directory]::EnumerateFiles($corpus, '*', [System.IO.SearchOption]::AllDirectories)) {
    $corpusFiles++
    $corpusBytes += (New-Object System.IO.FileInfo $f).Length
}

if ($corpusFiles -eq 0) { throw "corpus is empty; refusing to measure nothing" }

# ------------------------------------------------------------------ contestants

# robocopy copies the CONTENTS of the source into the destination; Roboc
# nests the source's own name, as Explorer does. Each contestant therefore has its
# own result root, counted and verified at that root.
# Roboc nests the source directory's own name under the destination, so the
# result root depends on where the corpus lives. Assuming a fixed name made every
# Roboc result count zero files.
$nestName = Split-Path $corpus -Leaf

$contestants = New-Object System.Collections.ArrayList
$null = $contestants.Add([pscustomobject]@{
    Name = 'robocopy'; Kind = 'robo'; ResultRoot = ''
    Extra = ('/E /MT:' + $RoboThreads + ' /NFL /NDL /NJH /NJS /NP /R:0 /W:0')
})
$null = $contestants.Add([pscustomobject]@{
    Name = 'TS-split'; Kind = 'ts'; ResultRoot = $nestName; Extra = ''
})

if ($ComparePublish) {
    $null = $contestants.Add([pscustomobject]@{ Name = 'TS-atomic'; Kind = 'ts'; ResultRoot = $nestName; Extra = '--always-atomic' })
    $null = $contestants.Add([pscustomobject]@{ Name = 'TS-direct'; Kind = 'ts'; ResultRoot = $nestName; Extra = '--always-direct' })
}

$report = New-Object System.Collections.ArrayList
function Say($s) { Write-Host $s; $null = $report.Add($s) }

Say ""
Say "================================================================"
Say " Roboc benchmark   label=$Label"
Say " $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Say "================================================================"
Say (" corpus  : {0:N0} files, {1}" -f $corpusFiles, (Format-Bytes $corpusBytes))
Say (" scratch : {0}" -f $Scratch)
Say (" rounds  : {0}" -f $Reps)
Say ""

# -------------------------------------------------------------------- measure

$times = @{}
$dests = @{}
$counts = @{}
$probes = @()
foreach ($c in $contestants) { $times[$c.Name] = @() }

for ($r = 1; $r -le $Reps; $r++) {
    $probe = Measure-VolumeProbe $Scratch
    $probes += $probe
    Say ("  --- round {0}   volume probe {1:F0} files/s ---" -f $r, $probe)

    foreach ($c in $contestants) {
        $dest = Join-Path $Scratch ('r' + $r + '-' + $c.Name)
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        $dests[$c.Name] = $dest

        if ($c.Kind -eq 'robo') {
            $t = Invoke-Timed 'robocopy.exe' ('"' + $corpus + '" "' + $dest + '" ' + $c.Extra)
        } else {
            $t = Invoke-Timed $exe ('copy --to "' + $dest + '" --quiet --workers ' + $TsWorkers +
                ' ' + $c.Extra + ' "' + $corpus + '"')
        }

        $times[$c.Name] += $t.Sec
        $root = $dest
        if ($c.ResultRoot -ne '') { $root = Join-Path $dest $c.ResultRoot }
        $n = @(Get-ChildItem $root -Recurse -Force -File -ErrorAction SilentlyContinue).Count
        $counts[$c.Name] = $n

        Say ("      {0,-11} {1,7:F2}s  {2,8:F0} files/s  {3} files{4}" -f `
            $c.Name, $t.Sec, ($corpusFiles / [Math]::Max(0.001, $t.Sec)), $n,
            $(if ($n -ne $corpusFiles) { '   *** FILE COUNT MISMATCH ***' } else { '' }))
    }
}

# --------------------------------------------------------------- verification

Say ""
Say "--- byte-for-byte verification of the last round's output ---"
foreach ($c in $contestants) {
    $root = $dests[$c.Name]
    if ($c.ResultRoot -ne '') { $root = Join-Path $root $c.ResultRoot }

    $v = Invoke-Timed $gen ('verify --root "' + $root + '"')
    $verdict = (($v.Out -split "\r?\n") | Where-Object { $_ -match 'corpus intact|FAILED' } | Select-Object -First 1)
    if (-not $verdict) { $verdict = "(no verdict; exit $($v.Code))" }
    Say ("  {0,-11} {1}" -f $c.Name, $verdict.Trim())
}

$temps = 0
foreach ($c in $contestants) {
    if ($c.Kind -eq 'ts') {
        $temps += @(Get-ChildItem $dests[$c.Name] -Recurse -Force -Filter '*roboc-tmp' -ErrorAction SilentlyContinue).Count
    }
}
Say ("  leftover temp files: {0}" -f $temps)

# ---------------------------------------------------------------------- result

Say ""
Say "--- median of $Reps ---"
foreach ($c in $contestants) {
    $med = Get-Median $times[$c.Name]
    Say ("  {0,-11} {1,7:F2}s  {2,8:F0} files/s   spread {3:F2}x" -f `
        $c.Name, $med, ($corpusFiles / [Math]::Max(0.001, $med)), (Get-Spread $times[$c.Name]))
}

$probeMin = ($probes | Measure-Object -Minimum).Minimum
$probeMax = ($probes | Measure-Object -Maximum).Maximum
$drift = $probeMax / [Math]::Max(1, $probeMin)

Say ""
Say ("  volume probe across rounds: min {0:F0}  max {1:F0} files/s  (drift {2:F1}x)" -f $probeMin, $probeMax, $drift)

if ($drift -gt 3) {
    Say ""
    Say "  *** UNRELIABLE ***"
    Say ("  The volume's own throughput varied {0:F1}x during this run, so the timings" -f $drift)
    Say "  above cannot be compared against each other or against another run."
    Say "  Reboot, let the volume idle, then run again on a quiet machine."
} elseif ($probeMin -lt 3000) {
    Say ""
    Say "  *** SUSPECT ***"
    Say ("  The volume managed only {0:F0} files/s on the probe; a healthy NVMe here" -f $probeMin)
    Say "  does roughly 3900, and about 2000 means it is running at half speed. Ratios"
    Say "  between contestants measured in the same round are still usable; absolute"
    Say "  numbers are not. Reboot before trusting any small difference."
    Say ""
    Say "  This threshold used to be 1500, which waved through a half-speed volume as"
    Say "  'comparable'. A guard belongs near the healthy floor, not near collapse."
} else {
    Say "  volume behaved consistently; timings are comparable."
}

Say ""
Say "  NOTE: absolute numbers are only meaningful compared against another run"
Say "        of this same script on the same hardware."

$outPath = Join-Path $resultsDir ("bench-{0}.txt" -f $Label)
$report -join "`r`n" | Set-Content -LiteralPath $outPath -Encoding UTF8
Write-Host ""
Write-Host "written: $outPath"

# One cleanup, after the numbers are taken. Deleting between rounds would itself
# be a small-file workload and would degrade the volume mid-measurement.
Write-Host "cleaning scratch ..."
cmd /c rd /s /q "$Scratch" 2>$null | Out-Null
Write-Host "done."
