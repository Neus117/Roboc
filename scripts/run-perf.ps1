<#
    Roboc 性能对照测试

    目标
      复制/粘贴：持平或略优于 robocopy
      删除      ：优于 PowerShell Remove-Item

    为什么必须一次跑完
      这台机器的卷会因反复小文件读写而劣化，只有重启能恢复。历史记录里同一个
      操作在同一时刻因卷不同差了 2000 倍。因此：

        · 交错执行，不批量      —— 批量会让后跑的选手系统性吃亏
        · 每轮轮转顺序          —— 消除"总是第一个跑"的优势
        · 每轮前后测卷健康      —— 首尾差异 >20% 则整轮作废
        · 控制总时长            —— 避免热降频叠加

    用法
      powershell -ExecutionPolicy Bypass -File scripts\run-perf.ps1 -Root <仓库根>

    结果
      scripts\results\perf-<时间戳>.txt
#>

param(
    [Parameter(Mandatory = $true)][string]$Root,
    [string]$Work = 'D:\roboc-perf',
    [int]$Rounds = 5,
    [int]$SmallCount = 20000,
    [int]$SmallSize = 4096,
    [int]$RoboThreads = 32,
    [switch]$SkipCorpus,
    [switch]$SkipDelete,

    # Named -Only, not -Corpora. PowerShell variable names are case-insensitive, so a
    # parameter called $Corpora silently replaced the $corpora array of corpus
    # definitions below -- every $c.Key became null and no corpus ever matched, which
    # looked exactly like "the corpus was not generated".
    [string[]]$Only = @('A', 'B', 'C', 'D'),
    [switch]$Quick
)

$ErrorActionPreference = 'Continue'

$exe     = Join-Path $Root 'artifacts\portable\Roboc.exe'
$gen     = Join-Path $Root 'tools\Roboc.CorpusGen\bin\Release\net10.0-windows\Roboc.CorpusGen.exe'
$results = Join-Path $Root 'scripts\results'
$stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$report  = Join-Path $results "perf-$stamp.txt"

if ($Quick) { $Rounds = 2; $SmallCount = 5000 }

New-Item -ItemType Directory -Path $results -Force | Out-Null

# ------------------------------------------------------------------ 记录

$script:lines = New-Object System.Collections.Generic.List[string]

function Say {
    param([string]$Text = '')
    $script:lines.Add($Text)
    Write-Host $Text
}

function SayBoth {
    param([string]$Text)
    Say $Text
    Add-Content -LiteralPath $report -Value $Text -Encoding UTF8
}

# ------------------------------------------------------------------ 测量

function Measure-VolumeProbe {
    param([string]$Dir, [int]$Count = 300)
    try {
        $probe = Join-Path $Dir ('vp-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        New-Item -ItemType Directory -Path $probe -Force | Out-Null
        $buf = New-Object byte[] 4096
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        for ($i = 0; $i -lt $Count; $i++) {
            [System.IO.File]::WriteAllBytes((Join-Path $probe "f$i.bin"), $buf)
        }
        $sw.Stop()
        $t = $sw.Elapsed.TotalSeconds
        cmd /c rd /s /q "$probe" 2>$null | Out-Null
        return [math]::Round($Count / [Math]::Max(0.001, $t), 0)
    } catch { return 0 }
}

function Get-Median {
    param([double[]]$Values)
    if ($Values.Count -eq 0) { return 0 }
    $sorted = $Values | Sort-Object
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

# 跑一个外部命令并计时。返回 @(秒, 退出码)
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
    # 整个对照测试就挂在那里，而且没有输出说明它在等什么。
    # 顺序本身不是危害 —— .NET 的 Process 在第一次读时会为两个流各起后台读取
    # 线程，实测 20 KB 输出也不死锁。真正的问题是【没有超时】。
    $stdoutTask = $p.StandardOutput.ReadToEndAsync()
    $stderrTask = $p.StandardError.ReadToEndAsync()

    if (-not $p.WaitForExit(600000)) {
        $p.Kill()
        $p.WaitForExit(10000)
        throw "被测程序超过 600 秒未结束，已终止：$($psi.FileName) $($psi.Arguments)"
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $sw.Stop()

    # The output is returned, not discarded. Throwing it away made an earlier run of
    # this script report "生成耗时 0.2 秒" for twenty thousand files and then blame a
    # missing corpus -- the generator had failed instantly with "Access is denied" and
    # nothing anywhere said so.
    return @($sw.Elapsed.TotalSeconds, $p.ExitCode, ($stdout + $stderr))
}

# ------------------------------------------------------------------ 选手

# 每个选手：名字 + 生成命令行的函数（接收 源 目标）
# The two Roboc entries exist to separate cost from overhead.
#
# Roboc writes a resume journal per completed file and, for large files, forces the
# bytes to disk before declaring success. Robocopy does neither. Comparing only the
# default against robocopy therefore measures two different promises, not two
# implementations of one -- a large-file result of 6233 MB/s for robocopy is above the
# drive's physical write speed, which is the cache answering, not the disk.
#
# Running the pair makes the split visible: default versus stripped says what the
# features cost, stripped versus robocopy says whether the rest can match.
$copiers = @(
    @{ Name = 'Roboc';          Make = { param($s, $d) "copy --to `"$d`" --quiet `"$s`"" }; File = $exe },
    @{ Name = 'Roboc 裸';       Make = { param($s, $d) "copy --to `"$d`" --quiet --no-resume --no-flush `"$s`"" }; File = $exe },
    @{ Name = 'Robocopy';       Make = { param($s, $d) "`"$s`" `"$d`" /E /NFL /NDL /NJH /NJS /NP" }; File = 'robocopy' },
    @{ Name = "Robocopy/MT:$RoboThreads"; Make = { param($s, $d) "`"$s`" `"$d`" /E /MT:$RoboThreads /NFL /NDL /NJH /NJS /NP" }; File = 'robocopy' },
    @{ Name = 'Robocopy/MT:1';  Make = { param($s, $d) "`"$s`" `"$d`" /E /MT:1 /NFL /NDL /NJH /NJS /NP" }; File = 'robocopy' },
    @{ Name = 'xcopy';          Make = { param($s, $d) "`"$s`" `"$d`" /E /I /Q /Y" }; File = 'xcopy' }
)

$deleters = @(
    @{ Name = 'Roboc delete';   Make = { param($p) "delete --direct --yes `"$p`"" }; File = $exe },
    @{ Name = 'PS Remove-Item'; Make = { param($p) "-NoProfile -Command `"Remove-Item -LiteralPath '$p' -Recurse -Force -ErrorAction SilentlyContinue`"" }; File = 'powershell' },
    @{ Name = 'cmd rd';         Make = { param($p) "/c rd /s /q `"$p`"" }; File = 'cmd' }
)

# ------------------------------------------------------------------ 语料

$corpora = @(
    @{ Key = 'A'; Name = '海量小文件'; Args = "--small-count $SmallCount --small-size $SmallSize --large-count 0" },
    @{ Key = 'B'; Name = '少量大文件'; Args = "--small-count 0 --large-count 2 --large-size 1024" },
    @{ Key = 'C'; Name = '混合';       Args = "--small-count $([int]($SmallCount/2)) --small-size $SmallSize --large-count 1 --large-size 2048" },
    @{ Key = 'D'; Name = '元数据密集'; Args = "--small-count 1000 --small-size $SmallSize --large-count 0 --hardlink-groups 500 --ads 500" }
)

# ================================================================== 开始

SayBoth "================================================================"
SayBoth "Roboc 性能对照测试"
SayBoth "开始时间 : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
SayBoth "语料目录 : $Work"
SayBoth "轮数     : $Rounds"
SayBoth "小文件   : $SmallCount x $SmallSize B"
SayBoth "================================================================"
SayBoth ""

foreach ($req in @($exe, $gen)) {
    if (-not (Test-Path $req)) { SayBoth "缺少: $req"; exit 1 }
}

if (-not (Test-Path $Work)) { New-Item -ItemType Directory -Path $Work -Force | Out-Null }

$probeStart = Measure-VolumeProbe $Work
SayBoth "起始卷健康度: $probeStart 文件/秒   （健康参照约 3900）"
SayBoth ""

# ---------------------------------------------------------------- 造语料

if (-not $SkipCorpus) {
    SayBoth "--- 生成语料 ---"

    foreach ($c in $corpora) {
        if ($Only -notcontains $c.Key) { continue }

        $dir = Join-Path $Work "corpus-$($c.Key)"
        SayBoth "  [$($c.Key)] $($c.Name)"

        $genArgs = "create --root `"$dir`" --clean $($c.Args)"
        $t = Invoke-Timed -File $gen -Arguments $genArgs

        if ($t[1] -ne 0 -or -not (Test-Path $dir)) {
            SayBoth "      生成失败（退出码 $($t[1])）："
            foreach ($line in ($t[2] -split "`r?`n")) { if ($line.Trim()) { SayBoth "        $line" } }
            SayBoth ""
            SayBoth "语料无法生成，测试中止。"
            $script:lines | Set-Content -LiteralPath $report -Encoding UTF8
            exit 1
        }

        SayBoth "      生成耗时 $([math]::Round($t[0],1)) 秒"
    }

    SayBoth ""
}

# ---------------------------------------------------------------- 复制测试

$copyResults = @{}

foreach ($c in $corpora) {
    if ($Only -notcontains $c.Key) { continue }

    $src = Join-Path $Work "corpus-$($c.Key)"

    if (-not (Test-Path $src)) { SayBoth "跳过 $($c.Key)：语料不存在"; continue }

    $bytes = (Get-ChildItem $src -Recurse -File -Force -EA SilentlyContinue | Measure-Object Length -Sum).Sum
    $files = @(Get-ChildItem $src -Recurse -File -Force -EA SilentlyContinue).Count
    $mb = [math]::Round($bytes / 1MB, 1)

    SayBoth "================================================================"
    SayBoth "[$($c.Key)] $($c.Name) —— $files 个文件，$mb MB，$Rounds 轮"
    SayBoth "================================================================"
    SayBoth ""

    $samples = @{}
    foreach ($p in $copiers) { $samples[$p.Name] = New-Object System.Collections.Generic.List[double] }

    $usedRounds = 0
    $droppedRounds = 0

    for ($round = 1; $round -le $Rounds; $round++) {
        $before = Measure-VolumeProbe $Work

        # This round's timings are held here until the volume is checked afterwards.
        #
        # They used to go straight into $samples, so a round flagged as polluted was
        # flagged and then counted anyway -- the median was taken over a mix of clean
        # and contaminated runs, and the warning next to it changed nothing. A round
        # that fails the drift check now contributes nothing at all.
        $roundSamples = @{}
        foreach ($p in $copiers) { $roundSamples[$p.Name] = New-Object System.Collections.Generic.List[double] }

        # 轮转：每轮把顺序左移一位
        $shift = ($round - 1) % $copiers.Count
        $order = @($copiers[$shift..($copiers.Count - 1)]) + @($copiers[0..($shift - 1)])

        SayBoth "  第 $round 轮   （卷健康 $before）"

        foreach ($p in $order) {
            $dest = Join-Path $Work "dst-$($c.Key)-$($p.Name -replace '[^\w]','')"

            if (Test-Path $dest) { cmd /c rd /s /q "$dest" 2>$null | Out-Null }

            $cmdArgs = & $p.Make $src $dest
            $r = Invoke-Timed -File $p.File -Arguments $cmdArgs
            $secs = $r[0]
            $code = $r[1]

            # 预热轮不计入
            if ($round -eq 1) {
                SayBoth ("    {0,-18} {1,7:F2}s   （预热，不计）" -f $p.Name, $secs)
            } else {
                $roundSamples[$p.Name].Add($secs)
                SayBoth ("    {0,-18} {1,7:F2}s   {2,8:F0} 文件/秒   {3,7:F0} MB/秒" -f `
                    $p.Name, $secs, ($files / [math]::Max(0.01, $secs)), ($mb / [math]::Max(0.01, $secs)))
            }

            if (Test-Path $dest) { cmd /c rd /s /q "$dest" 2>$null | Out-Null }
        }

        $after = Measure-VolumeProbe $Work
        $drift = if ($before -gt 0) { [math]::Abs($after - $before) / $before * 100 } else { 0 }

        if ($round -eq 1) {
            SayBoth ("    卷健康后: $after   漂移 {0:F0}%   （预热轮）" -f $drift)
        } elseif ($drift -gt 20) {
            $droppedRounds++
            SayBoth ("    卷健康后: $after   漂移 {0:F0}%   ⚠ 超出 20% —— 本轮作废，不计入中位数" -f $drift)
        } else {
            $usedRounds++
            foreach ($name in $roundSamples.Keys) {
                foreach ($v in $roundSamples[$name]) { $samples[$name].Add($v) }
            }
            SayBoth ("    卷健康后: $after   漂移 {0:F0}%   ✓ 计入" -f $drift)
        }

        SayBoth ""
    }

    SayBoth "  可用轮数 $usedRounds / $Rounds" + $(if ($droppedRounds -gt 0) { "   （丢弃 $droppedRounds 轮）" } else { '' })
    SayBoth ""

    if ($usedRounds -lt 2) {
        SayBoth "  ⚠ 可用轮数不足 2，中位数没有统计意义。本语料的数据仅作参考。"
        SayBoth ""
    }

    $copyResults[$c.Key] = @{ Files = $files; Mb = $mb; Samples = $samples; Name = $c.Name; Used = $usedRounds }
}

# ---------------------------------------------------------------- 删除测试

SayBoth "================================================================"
SayBoth "删除测试"
SayBoth "================================================================"
SayBoth ""

$delResults = @{}
foreach ($p in $deleters) { $delResults[$p.Name] = New-Object System.Collections.Generic.List[double] }

$delRounds = [math]::Max(2, [int]($Rounds / 2))
$srcA = Join-Path $Work 'corpus-A'

if ($SkipDelete) {
    SayBoth "  已跳过（-SkipDelete）"
} elseif (Test-Path $srcA) {
    for ($round = 1; $round -le $delRounds; $round++) {
        $shift = ($round - 1) % $deleters.Count
        $order = @($deleters[$shift..($deleters.Count - 1)]) + @($deleters[0..($shift - 1)])

        SayBoth "  第 $round 轮"

        foreach ($p in $order) {
            # 每次删除前重建一份，保证起点相同
            $target = Join-Path $Work "del-$($p.Name -replace '[^\w]','')"
            if (Test-Path $target) { cmd /c rd /s /q "$target" 2>$null | Out-Null }

            $robocopyOut = & robocopy $srcA $target /E /NFL /NDL /NJH /NJS /NP /MT:$RoboThreads 2>&1
            $null = $robocopyOut

            $n = @(Get-ChildItem $target -Recurse -File -Force -EA SilentlyContinue).Count

            $cmdArgs = & $p.Make $target
            $r = Invoke-Timed -File $p.File -Arguments $cmdArgs

            $left = if (Test-Path $target) { @(Get-ChildItem $target -Recurse -File -Force -EA SilentlyContinue).Count } else { 0 }

            if ($round -eq 1) {
                SayBoth ("    {0,-18} {1,7:F2}s   （预热，不计）  剩余 $left" -f $p.Name, $r[0])
            } else {
                $delResults[$p.Name].Add($r[0])
                $ok = if ($left -eq 0) { '✓' } else { "✗ 剩 $left" }
                SayBoth ("    {0,-18} {1,7:F2}s   {2,8:F0} 文件/秒   $ok" -f `
                    $p.Name, $r[0], ($n / [math]::Max(0.01, $r[0])))
            }

            if (Test-Path $target) { cmd /c rd /s /q "$target" 2>$null | Out-Null }
        }
        SayBoth ""
    }
} else {
    SayBoth "  跳过：语料 A 不存在"
}

# ---------------------------------------------------------------- 汇总

$probeEnd = Measure-VolumeProbe $Work

SayBoth "================================================================"
SayBoth "汇总"
SayBoth "================================================================"
SayBoth ""
SayBoth "卷健康度: 起始 $probeStart  →  结束 $probeEnd"
SayBoth ""

SayBoth "复制（中位数，秒）"

foreach ($key in @('A','B','C','D')) {
    if (-not $copyResults.ContainsKey($key)) { continue }
    $r = $copyResults[$key]

    $roboc = Get-Median $r.Samples['Roboc'].ToArray()
    $bare = Get-Median $r.Samples['Roboc 裸'].ToArray()
    $roboMt = Get-Median $r.Samples["Robocopy/MT:$RoboThreads"].ToArray()
    $xcopy = Get-Median $r.Samples['xcopy'].ToArray()

    SayBoth ("  [{0}] {1}   （可用轮数 $($r.Used)）" -f $key, $r.Name)
    SayBoth ("      {0,-16} {1,8:F2}s" -f 'Roboc 默认', $roboc)
    SayBoth ("      {0,-16} {1,8:F2}s" -f 'Roboc 裸', $bare)
    SayBoth ("      {0,-16} {1,8:F2}s" -f "Robocopy/MT:$RoboThreads", $roboMt)
    SayBoth ("      {0,-16} {1,8:F2}s" -f 'xcopy', $xcopy)

    if ($bare -gt 0 -and $roboc -gt 0) {
        SayBoth ("      功能开销（默认 vs 裸）     : {0:F2}s → {1:F2}s   功能占 {2:F0}%" -f `
            $bare, $roboc, (($roboc - $bare) / $roboc * 100))
    }

    if ($roboMt -gt 0 -and $bare -gt 0) {
        $pct = ($roboMt - $bare) / $roboMt * 100
        SayBoth ("      同条件（裸 vs Robocopy/MT） : " + $(if ($pct -ge 0) { "快 {0:F0}%  ✓ 达标" -f $pct } else { "慢 {0:F0}%" -f [math]::Abs($pct) }))
    }
}

SayBoth ""
SayBoth "删除（中位数，秒）"

foreach ($p in $deleters) {
    $m = Get-Median $delResults[$p.Name].ToArray()
    SayBoth ("  {0,-20} {1,8:F2}" -f $p.Name, $m)
}

$psDel = Get-Median $delResults['PS Remove-Item'].ToArray()
$roboDel = Get-Median $delResults['Roboc delete'].ToArray()

if ($psDel -gt 0 -and $roboDel -gt 0) {
    $pct = ($psDel - $roboDel) / $psDel * 100
    $verdict = if ($pct -ge 0) { "快 {0:F0}%  ✓ 达标" -f $pct } else { "慢 {0:F0}%" -f [math]::Abs($pct) }
    SayBoth ""
    SayBoth "  相对 PowerShell Remove-Item —— $verdict"
}

SayBoth ""
SayBoth "================================================================"
SayBoth "结束时间 : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
SayBoth "报告     : $report"
SayBoth "================================================================"

$script:lines | Set-Content -LiteralPath $report -Encoding UTF8
Write-Host ""
Write-Host "报告已写入 $report" -ForegroundColor Green
