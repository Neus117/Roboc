<#
    磁盘满行为测试 —— 需要一个小的虚拟磁盘，避免写满真实硬盘。

    为什么需要提权：创建和挂载 VHD 需要管理员权限。

    做法：造一个 256 MB 的 VHD，格式化为 NTFS，挂成盘符，
          往里复制超过容量的数据，观察 Roboc 的行为，然后卸载并删除。
          整个过程不碰你的任何真实卷。

    用法（右键"以管理员身份运行 PowerShell"，然后）：
        powershell -ExecutionPolicy Bypass -File "D:\Coding\DeepSeek Harness\workspace\Roboc\scripts\disk-full-test.ps1"

    结果写入 scripts\disk-full-result.txt，跑完告诉我，我来读。
#>

$ErrorActionPreference = 'Stop'

$root   = "D:\Coding\DeepSeek Harness\workspace\Roboc"
$exe    = Join-Path $root "artifacts\portable\Roboc.exe"
$work   = Join-Path $root "scripts\diskfull"
$vhd    = Join-Path $work "roboc-test.vhd"
$source = Join-Path $work "source"
$result = Join-Path $root "scripts\disk-full-result.txt"
$letter = 'R'

function Write-Result {
    param([string]$Line)
    Add-Content -LiteralPath $result -Value $Line -Encoding UTF8
    Write-Host $Line
}

# ---------------------------------------------------------------- 前置检查

if (-not ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "需要管理员权限。请以管理员身份运行 PowerShell 后重试。" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $exe)) { Write-Host "未找到 $exe" -ForegroundColor Red; exit 1 }

if (Test-Path "${letter}:") {
    Write-Host "盘符 ${letter}: 已被占用。请修改脚本里的 `$letter。" -ForegroundColor Red
    exit 1
}

Remove-Item -LiteralPath $result -EA SilentlyContinue
if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force -EA SilentlyContinue }
New-Item -ItemType Directory -Path $work, $source -Force | Out-Null

Write-Result "=== 磁盘满行为测试 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="
Write-Result ""

$attached = $false

try {
    # ------------------------------------------------------------ 造 VHD

    Write-Result "[1] 创建 256 MB 动态 VHD"

    $dp = @"
create vdisk file="$vhd" maximum=256 type=expandable
select vdisk file="$vhd"
attach vdisk
create partition primary
format fs=ntfs quick label=ROBOCTEST
assign letter=$letter
"@

    $dpFile = Join-Path $work "attach.txt"
    Set-Content -LiteralPath $dpFile -Value $dp -Encoding ASCII

    $attachOut = & diskpart /s $dpFile 2>&1 | Out-String
    Write-Result $attachOut
    $attached = $true

    Start-Sleep -Seconds 3

    if (-not (Test-Path "${letter}:")) {
        Write-Result "挂载失败，盘符 ${letter}: 不存在。中止。"
        exit 1
    }

    $free = (Get-PSDrive -Name $letter).Free
    Write-Result ("    挂载成功，${letter}: 可用 {0:N0} 字节" -f $free)
    Write-Result ""

    # ------------------------------------------------------------ 造源数据

    Write-Result "[2] 造源数据（一个 300 MB 文件 + 200 个小文件）"

    $bigFile = Join-Path $source "big.bin"
    $fs = [System.IO.File]::Create($bigFile)
    $fs.SetLength(300MB)
    $fs.Close()

    foreach ($i in 1..200) {
        [System.IO.File]::WriteAllText((Join-Path $source "small$i.txt"), ("x" * 2000))
    }

    $srcBytes = (Get-ChildItem $source -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Result ("    源共 {0:N0} 字节，目标可用 {1:N0} 字节 —— 必然写满" -f $srcBytes, $free)
    Write-Result ""

    # ------------------------------------------------------------ 跑复制

    Write-Result "[3] 执行复制（预期：磁盘满，干净中止）"
    Write-Result ""

    $dest = "${letter}:\dest"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.Arguments = "copy --to `"$dest`" `"$source`""
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = [System.Diagnostics.Process]::Start($psi)
    $out = $proc.StandardOutput.ReadToEnd()
    $err = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    $sw.Stop()

    Write-Result ("    退出码 : {0}" -f $proc.ExitCode)
    Write-Result ("    耗时   : {0:F2} 秒" -f $sw.Elapsed.TotalSeconds)
    Write-Result ""
    Write-Result "    --- stdout ---"
    foreach ($line in ($out -split "`r?`n")) { if ($line.Trim()) { Write-Result "    $line" } }
    Write-Result "    --- stderr ---"
    foreach ($line in ($err -split "`r?`n")) { if ($line.Trim()) { Write-Result "    $line" } }
    Write-Result ""

    # ------------------------------------------------------------ 检查残留

    Write-Result "[4] 检查目标中的残留暂存文件"

    $temps = @(Get-ChildItem $dest -Recurse -Force -File -EA SilentlyContinue |
               Where-Object { $_.Name -like '*roboc-tmp*' -or $_.Name -like '*.tsj' })
    $all = @(Get-ChildItem $dest -Recurse -Force -File -EA SilentlyContinue)

    Write-Result ("    目标文件总数     : {0}" -f $all.Count)
    Write-Result ("    残留暂存文件     : {0}" -f $temps.Count)

    foreach ($t in $temps | Select-Object -First 10) {
        Write-Result ("      {0}  ({1:N0} 字节)" -f $t.Name, $t.Length)
    }
    Write-Result ""

    # 程序是否在输出里提到了这些残留？
    $mentioned = ($out + $err) -match '残留'
    Write-Result ("    输出中提到了残留 : {0}" -f $(if ($mentioned) { '是' } else { '否' }))
    Write-Result ""

    # 磁盘是否仍可写（确认没有把卷弄坏）
    Write-Result "[5] 确认卷仍然健康"

    try {
        $probe = "${letter}:\health.txt"
        [System.IO.File]::WriteAllText($probe, "ok")
        Remove-Item $probe -Force
        Write-Result "    卷仍可正常读写"
    } catch {
        Write-Result ("    卷读写异常: {0}" -f $_.Exception.Message)
    }

    Write-Result ""
    Write-Result "[6] 第二次运行：确认重复执行不会累积残留"

    $psi2 = New-Object System.Diagnostics.ProcessStartInfo
    $psi2.FileName = $exe
    $psi2.UseShellExecute = $false
    $psi2.CreateNoWindow = $true
    $psi2.Arguments = "copy --to `"$dest`" `"$source`""
    $psi2.RedirectStandardOutput = $true
    $psi2.RedirectStandardError = $true
    $p2 = [System.Diagnostics.Process]::Start($psi2)
    $out2 = $p2.StandardOutput.ReadToEnd()
    $err2 = $p2.StandardError.ReadToEnd()
    $p2.WaitForExit()

    $temps2 = @(Get-ChildItem $dest -Recurse -Force -File -EA SilentlyContinue |
                Where-Object { $_.Name -like '*roboc-tmp*' })
    Write-Result ("    第二次退出码     : {0}" -f $p2.ExitCode)
    Write-Result ("    残留暂存文件     : {0}  （第一次是 {1}）" -f $temps2.Count, $temps.Count)
    Write-Result ""
    Write-Result "    --- 第二次 stdout ---"
    foreach ($line in ($out2 -split "`r?`n")) { if ($line.Trim()) { Write-Result "    $line" } }
    Write-Result "    --- 第二次 stderr ---"
    foreach ($line in ($err2 -split "`r?`n")) { if ($line.Trim()) { Write-Result "    $line" } }

    Write-Result ""
    Write-Result "=== 测试完成 ==="
}
catch {
    Write-Result ""
    Write-Result ("测试过程中出错: {0}" -f $_.Exception.Message)
    Write-Result $_.ScriptStackTrace
}
finally {
    # ------------------------------------------------------------ 清理

    Write-Host ""
    Write-Host "清理中..." -ForegroundColor Cyan

    if ($attached) {
        try {
            $dpFile = Join-Path $work "detach.txt"
            Set-Content -LiteralPath $dpFile -Value "select vdisk file=`"$vhd`"`ndetach vdisk" -Encoding ASCII
            & diskpart /s $dpFile 2>&1 | Out-Null
        } catch { }
    }

    Start-Sleep -Seconds 2

    try { Remove-Item -LiteralPath $work -Recurse -Force -EA SilentlyContinue } catch { }

    $stillThere = Test-Path "${letter}:"
    Add-Content -LiteralPath $result -Value "" -Encoding UTF8
    Add-Content -LiteralPath $result -Value ("清理: 盘符 ${letter}: " + $(if ($stillThere) { "仍存在（请手工检查）" } else { "已移除" })) -Encoding UTF8
    Add-Content -LiteralPath $result -Value ("清理: 工作目录 " + $(if (Test-Path $work) { "仍存在" } else { "已删除" })) -Encoding UTF8

    Write-Host ""
    Write-Host "结果已写入: $result" -ForegroundColor Green
    Write-Host "盘符 ${letter}: " -NoNewline
    Write-Host $(if ($stillThere) { "仍存在，请手工卸载" } else { "已移除" }) -ForegroundColor $(if ($stillThere) { 'Yellow' } else { 'Green' })
}
