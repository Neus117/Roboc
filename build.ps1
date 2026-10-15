<#
    构建并发布便携版。

    必须在发布之后重设可执行文件的强制完整性标签，原因：

      Low 完整性进程不能写 HKEY_CURRENT_USER\Software。而由 Low 完整性
      进程创建的文件会被标上 Low 标签，于是从该文件启动的一切都被压到 Low
      （包括它启动的 reg.exe）。症状是一句没有任何线索的"拒绝访问"。

    这一步不能放进 MSBuild：构建进程自己运行在 Low，而 Low 进程无法修改
    强制完整性标签，所以那样的 Target 会静默地什么都不做。

    本脚本刻意不使用任何 .NET 静态调用（例如 [string]::IsNullOrEmpty），
    因为子 PowerShell 可能运行在 ConstrainedLanguage 模式下，那类调用会在
    解析阶段就被拒绝。

    用法：
        powershell -ExecutionPolicy Bypass -File build.ps1
#>
[CmdletBinding()]
param(
    [string]$Root,
    [switch]$SkipSelfTest
)

if (-not $Root) { $Root = $PSScriptRoot }
if (-not $Root) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Root) { throw "无法确定项目根目录" }

$ErrorActionPreference = 'Stop'

# Resolved, and verified to carry an SDK.
#
# This used to name one machine's SDK path outright, which works exactly once --
# on that machine, until the SDK moves or someone else clones the repository.
#
# Taking the first `dotnet` on PATH is not enough either, and that is the trap:
# a machine can have the runtime host at C:\Program Files\dotnet\dotnet.exe and
# the SDK somewhere else entirely. The host answers --list-sdks with nothing and
# then fails the build with "No .NET SDKs were found", which reads like the SDK
# is missing rather than like the wrong dotnet was picked.
#
# So: ask each candidate what it has, and keep the one that has something.
$candidates = @()

$command = Get-Command dotnet -ErrorAction SilentlyContinue
if ($command) { $candidates += $command.Source }

$candidates += Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$candidates += 'C:\Program Files\dotnet\dotnet.exe'

$dotnet = $null

foreach ($candidate in $candidates) {
    if (-not (Test-Path $candidate)) { continue }

    $sdks = & $candidate --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and $sdks) { $dotnet = $candidate; break }
}

if (-not $dotnet) {
    throw "找不到带 SDK 的 dotnet。请安装 .NET 10 SDK。"
}

if (-not $dotnet) {
    throw "找不到 dotnet。请安装 .NET 10 SDK，或把它加入 PATH。"
}

$project = Join-Path $Root 'src\Roboc.App\Roboc.App.csproj'
$output = Join-Path $Root 'artifacts\portable'

Write-Host "== 构建 =="
& $dotnet build $project -c Release -v minimal --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

Write-Host ""
Write-Host "== 发布便携版 =="
if (Test-Path $output) { cmd /c rd /s /q "$output" 2>$null | Out-Null }
& $dotnet publish $project -c Release -r win-x64 -o $output --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

$exe = Join-Path $output 'Roboc.exe'
if (-not (Test-Path $exe)) { throw "未生成 $exe" }

# 图标必须与 exe 一起发布：右键菜单的静态动词只能指向一个文件路径，
# 无法寻址 exe 内部的多个图标资源。找不到时程序会退回使用 exe 自身的图标。
Write-Host ""
Write-Host "== 复制菜单图标 =="
$iconSource = Join-Path $Root 'assets\icons'
$iconTarget = Join-Path $output 'assets\icons'
if (Test-Path $iconSource) {
    New-Item -ItemType Directory -Path $iconTarget -Force | Out-Null
    $icons = @(Get-ChildItem $iconSource -Filter '*.ico' -File)
    foreach ($i in $icons) { Copy-Item $i.FullName -Destination $iconTarget -Force }
    Write-Host ("  {0} 个图标 -> {1}" -f $icons.Count, $iconTarget)
} else {
    Write-Host "  未找到图标目录，菜单将使用 exe 自带图标：$iconSource"
}

Write-Host ""
Write-Host "== 修正强制完整性标签 =="

# Sets the label, or explains why it could not.
#
# Why this is not one icacls call.
#
# The published executable has just been run -- by the self-test at the end of the
# previous build, by a smoke test, by the context menu. Windows keeps an executed
# image mapped for a while after the process exits, and while that mapping is alive
# icacls reports "Access is denied" on that exact file -- not on its directory, not
# on any other file in it, only on the one that was run. Re-running the build does
# not clear it and neither does waiting.
#
# A file that has never been executed in this session has no such lock, so the label
# is applied to a copy and the copy is moved over the original. Setting a label on a
# copy and renaming it preserves the label, and the rename succeeds where the direct
# write did not.
function Set-MediumIntegrity([string]$path) {
    # Relaxed for the whole function, and that is load-bearing.
    #
    # This script runs with $ErrorActionPreference = 'Stop'. icacls writes its
    # refusal to stderr, and PowerShell turns a native command's stderr into a
    # terminating error under that setting. Relaxing only the first call was not
    # enough: the fallback path below makes two more, and the second one aborted
    # the build from inside the very code written to handle the refusal.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        if (-not (Test-Path $path)) { return $false }

        # Relaxed for the duration of this function, and that is load-bearing.
        #
        # This script runs with $ErrorActionPreference = 'Stop'. icacls writes its refusal
        # to stderr, and PowerShell turns a native command's stderr into a terminating
        # error under that setting -- so the "Access is denied" case aborted the whole
        # build at the first icacls call, and the fallback below never got a chance to
        # run. The fallback exists precisely for that case.
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'

        try {
            cmd /c icacls "$path" /setintegritylevel Medium 2>&1 | Out-Null
        } finally {
            $ErrorActionPreference = $previous
        }

        $check = (cmd /c icacls "$path" 2>&1 | Out-String)
        if ($check -match 'Medium Mandatory Level') { return $true }

        # The direct write was refused. Try the copy-and-rename route.
        $staging = "$path.new"
        Remove-Item $staging -Force -ErrorAction SilentlyContinue
        Copy-Item $path $staging -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $staging)) { return $false }

        cmd /c icacls "$staging" /setintegritylevel Medium 2>&1 | Out-Null

        $copyCheck = (cmd /c icacls "$staging" 2>&1 | Out-String)
        if ($copyCheck -notmatch 'Medium Mandatory Level') {
            Remove-Item $staging -Force -ErrorAction SilentlyContinue
            return $false
        }

        Remove-Item $path -Force -ErrorAction SilentlyContinue

        if (Test-Path $path) {
            cmd /c move /Y "$staging" "$path" 2>&1 | Out-Null
        } else {
            Move-Item $staging $path -Force -ErrorAction SilentlyContinue
        }

        $final = (cmd /c icacls "$path" 2>&1 | Out-String)
        return $final -match 'Medium Mandatory Level'
    } finally {
        $ErrorActionPreference = $previous
    }
}

if (Set-MediumIntegrity $exe) {
    Write-Host "  Roboc.exe: Medium"
} else {
    Write-Host "  !! 无法为 Roboc.exe 设置完整性标签。" -ForegroundColor Red
    Write-Host "     Low 完整性进程无法写 HKCU，右键菜单注册会被拒绝。" -ForegroundColor Red
    Write-Host "     关闭所有 Roboc 进程后重试，或以管理员身份运行一次。" -ForegroundColor Red
}

# The corpus generator needs the same label, and for the same reason:
# it is built by this same toolchain, so it inherits Low integrity, and a
# Low-integrity process cannot write into an ordinary directory. Without
# this it fails with "Access to the path ... is denied" the moment it
# tries to create its corpus, which reads like a permissions fault in the
# benchmark rather than a label missing from the binary.
$genExe = Join-Path $PSScriptRoot 'tools\Roboc.CorpusGen\bin\Release\net10.0-windows\Roboc.CorpusGen.exe'
if (Test-Path $genExe) {
    if (Set-MediumIntegrity $genExe) {
        Write-Host "  Roboc.CorpusGen.exe: Medium"
    } else {
        Write-Host "  !! Roboc.CorpusGen.exe 的标签未能设置；基准语料生成会失败。" -ForegroundColor Red
    }
}

# Fail the build rather than hand over a deliverable that cannot register its menu.
#
# The label is not cosmetic: a Low-integrity process cannot write HKCU, so the
# executable builds, runs, and then refuses to install the context menu with an
# "access denied" that reads like a Windows permission problem. Better to stop here.
if (-not (Set-MediumIntegrity $exe)) {
    throw "完整性标签未能修正为 Medium；这样的产物无法注册右键菜单"
}

$mb = (Get-Item $exe).Length / 1MB
Write-Host ("  体积: {0:N1} MB" -f $mb)

if (-not $SkipSelfTest) {
    Write-Host ""
    Write-Host "== 自检 =="
    $scratch = Join-Path $Root 'artifacts\selftest'

    # & does not wait for a GUI-subsystem executable, so the exit code has to come
    # from a redirected launch rather than from $LASTEXITCODE.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = "--selftest --scratch `"$scratch`""
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)

    # Read both streams asynchronously, then wait with a timeout.
    #
    # Why a timeout matters here and why the order is not the point:
    #
    #   A bare WaitForExit() waits for ever. If the self-test ever raises a modal
    #   dialog -- or simply wedges -- the build hangs with no output saying what it
    #   is waiting for. This is the only publish path, so a hang here means no
    #   release. Measured 2026-10-15: the same shape waiting on a child that never
    #   exits was still running after 12 seconds, and would still be running now.
    #
    #   The order of ReadToEnd versus WaitForExit turns out NOT to be the hazard it
    #   was recorded as: .NET's Process starts a background reader for each stream
    #   when the first read begins, so a synchronous ReadToEnd-before-WaitForExit
    #   handled 20 KB of output without deadlocking. That claim is corrected here
    #   rather than left standing.
    #
    #   Async reads are still what this uses: they cost nothing here and they let the
    #   timeout fire while the output is still being collected.
    $stdoutTask = $p.StandardOutput.ReadToEndAsync()
    $stderrTask = $p.StandardError.ReadToEndAsync()

    if (-not $p.WaitForExit(120000)) {
        $p.Kill()
        $p.WaitForExit(10000)
        throw "自检超过 120 秒未结束，已终止。它可能弹出了窗口在等人点击。"
    }

    $out = $stdoutTask.GetAwaiter().GetResult()
    $err = $stderrTask.GetAwaiter().GetResult()

    cmd /c rd /s /q "$scratch" 2>$null | Out-Null

    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match 'passed,') { Write-Host "  $line" }
    }
    if ($err) { Write-Host "  stderr: $err" }
    if ($p.ExitCode -ne 0) { throw "自检失败" }
}

Write-Host ""
Write-Host "完成: $exe"
Write-Host "把右键菜单指向这个文件："
Write-Host "  & `"$exe`" install"
