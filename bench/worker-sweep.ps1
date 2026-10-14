<#
    worker 数对照测试 —— 验证 HDD 上是否存在寻道争用。

    为什么需要这个脚本，而不是手打三行命令：
    本程序是 GUI 子系统 exe，PowerShell 的 & 不等待它，
    所以 foreach 里连续三次调用会同时启动，三份复制在同一个
    机械盘上抢磁头。2026-10-14 就这样测过一次，三组数据全部作废。

    脚本强制做了三件手打容易漏的事：
      1. 用 Start-Process -Wait 等每一组真正结束
      2. 每组之前清空目标目录
      3. 每组之后校验条数，不符就中止

    用法（普通 PowerShell 即可）：
      powershell -ExecutionPolicy Bypass -File bench\worker-sweep.ps1 `
          -Source "D:\某目录" -Target "H:\Temp\bench" -Counts 4,8,16
#>

param(
    [Parameter(Mandatory = $true)][string] $Source,
    [Parameter(Mandatory = $true)][string] $Target,
    # A string, not [int[]], and that is not a style choice.
    #
    # powershell -File cannot pass an array: "-Counts 4,8" arrives as the single value 48,
    # because the whole command line is text before PowerShell parses it. The parameter was
    # [int[]] and the sweep silently ran one round with 48 workers. Splitting a string here
    # is what works from the command line, which is the only way this is ever invoked.
    [string] $Counts = '4,8,16',
    [string] $Exe = ''
)

$ErrorActionPreference = 'Stop'

$workerCounts = @(
    $Counts -split '[,;\s]+' |
        Where-Object { $_ -match '^\d+$' } |
        ForEach-Object { [int]$_ }
)

if ($workerCounts.Count -eq 0) { throw "Counts 里没有有效的数字：$Counts" }

# Resolved in the body, not as a parameter default.
#
# $PSScriptRoot came back empty when used as the default value for $Exe, so the path was
# built as "\..\artifacts\portable\Roboc.exe" and the script refused to start. Parameter
# defaults are evaluated during binding and this one does not survive that; in the body it
# is simply a variable that is already set.
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $PSScriptRoot '..\artifacts\portable\Roboc.exe'
}

if (-not (Test-Path $Exe)) { throw "找不到 Roboc.exe：$Exe" }
if (-not (Test-Path $Source)) { throw "找不到源目录：$Source" }

$Exe = (Resolve-Path $Exe).Path

# 源和目标不能互相包含，否则这一次测的就不是纯复制
$srcFull = (Resolve-Path $Source).Path.TrimEnd('\')
if ($Target.TrimEnd('\').StartsWith($srcFull, 'OrdinalIgnoreCase')) {
    throw "目标目录在源目录里面，会把刚写的东西再复制一遍"
}

Write-Host "源      $srcFull"
Write-Host "目标    $Target"
Write-Host "轮次    $($workerCounts -join ', ')"
Write-Host ""

$results = @()

foreach ($w in $workerCounts) {
    Write-Host "═══ workers = $w ═══" -ForegroundColor Cyan

    # 每组都从空目标开始。
    # 不清的话，第二组起走的是"替换"路径而不是"新建"，
    # 两者的 create/rename 开销完全不同，数据不可比。
    if (Test-Path $Target) {
        Write-Host "  清空目标…"
        Remove-Item $Target -Recurse -Force -EA SilentlyContinue
    }
    New-Item -ItemType Directory -Path $Target -Force | Out-Null

    $log = Join-Path $env:TEMP "roboc-sweep-$w.txt"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    # Waited on explicitly, and not with Start-Process.
    #
    # Two separate traps here, both of which produced a silently wrong measurement:
    #
    # 1. Roboc.exe is a GUI-subsystem exe, so PowerShell's & does not wait for it. A loop
    #    over worker counts therefore starts every copy at once and they fight over the
    #    same disk. Measured exactly that way once: three concurrent copies, worker counts
    #    reported in reverse order, and every number meaningless.
    #
    # 2. Start-Process fails outright on this machine with "Item has already been added.
    #    Key in dictionary: 'NO_PROXY'". Windows environment variables are case-insensitive
    #    and PowerShell 5.1 builds its copy of them in a case-sensitive dictionary, so an
    #    environment holding both NO_PROXY and no_proxy -- which miniconda and most proxy
    #    tools set -- breaks it. Process.Start does not go through that path.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    # Arguments, not ArgumentList.
    #
    # ArgumentList is .NET 5+ and is null under Windows PowerShell 5.1, which runs on .NET
    # Framework -- the call threw "You cannot call a method on a null-valued expression".
    # This means quoting by hand, and the quotes are not decoration: the target path is a
    # user-supplied directory and may contain spaces.
    $quoted = @('copy', '--to', $Target, '--workers', $w, '--timing', $srcFull) |
        ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }

    $psi.Arguments = $quoted -join ' '

    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()

    $sw.Stop()
    [System.IO.File]::WriteAllText($log, $stdout + "`r`n--- stderr ---`r`n" + $stderr,
        (New-Object System.Text.UTF8Encoding($false)))
    $text = $stdout

    $elapsed = if ($text -match 'elapsed\s+([\d.]+)s') { [double]$Matches[1] } else { $sw.Elapsed.TotalSeconds }
    $files   = if ($text -match 'copied\s+([\d,]+)') { [int]($Matches[1] -replace ',', '') } else { -1 }
    $failed  = if ($text -match 'failed\s+(\d+)') { [int]$Matches[1] } else { -1 }
    $state   = if ($text -match 'state\s+(\w+)') { $Matches[1] } else { '?' }

    # 每组之间取各阶段耗时
    $phases = @{}
    foreach ($name in @('create dest', 'close', 'transfer', 'set metadata', 'rename', 'flush')) {
        if ($text -match [regex]::Escape($name) + '\s+([\d.]+) ms') { $phases[$name] = [double]$Matches[1] }
    }
    $perFile = if ($text -match 'per file\s+([\d.]+) ms') { [double]$Matches[1] } else { 0 }

    $results += [PSCustomObject]@{
        Workers  = $w
        State    = $state
        Seconds  = [math]::Round($elapsed, 1)
        Copied   = $files
        Failed   = $failed
        PerFile  = [math]::Round($perFile, 1)
        CreateMs = if ($phases['create dest']) { [math]::Round($phases['create dest'] / 1000) } else { 0 }
        CloseMs  = if ($phases['close']) { [math]::Round($phases['close'] / 1000) } else { 0 }
        TransferMs = if ($phases['transfer']) { [math]::Round($phases['transfer'] / 1000) } else { 0 }
    }

    Write-Host ("  完成  {0}s  copied={1} failed={2}  per-file={3}ms" -f [math]::Round($elapsed,1), $files, $failed, [math]::Round($perFile,1))

    if ($failed -ne 0 -or $state -notmatch 'Completed$') {
        Write-Host "  ⚠️ 本轮有失败项，数据可能不可比" -ForegroundColor Yellow
    }

    # 完整报告留一份，供细看
    $keep = Join-Path $env:TEMP "roboc-sweep-$w-report.txt"
    Copy-Item $log $keep -Force
    Write-Host "  完整报告  $keep"
    Write-Host ""
}

Write-Host "═══ 汇总 ═══" -ForegroundColor Cyan
$results | Format-Table -AutoSize

Write-Host @"

怎么读这张表：

  如果 create dest 随 workers 增加而显著变大  → 证实寻道争用
      （create dest 就是一次 CreateFile，机械盘上要寻道）
  如果 Seconds 在某个值最低                    → 那就是这台机器 + 这块盘的最优值
  如果三组 Seconds 都差不多                    → worker 数不是瓶颈，去别处找

注意：
  · 每一轮都会写满目标盘，卷会劣化
  · 三组跑完如果数字明显不合理（比如 workers 少的反而慢得离谱），
    先重启再测，不要在同一劣化状态下继续加组
  · 源目录不要选正在运行的程序自己的目录，那些文件锁着
"@
