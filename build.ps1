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
$before = (cmd /c icacls "$exe" 2>&1 | Select-String 'Mandatory' | Select-Object -First 1)
Write-Host ("  之前: " + ($before -replace '\s+', ' '))
cmd /c icacls "$exe" /setintegritylevel Medium 2>&1 | Out-Null

# The corpus generator needs the same label, and for the same reason:
# it is built by this same toolchain, so it inherits Low integrity, and a
# Low-integrity process cannot write into an ordinary directory. Without
# this it fails with "Access to the path ... is denied" the moment it
# tries to create its corpus, which reads like a permissions fault in the
# benchmark rather than a label missing from the binary.
$genExe = Join-Path $PSScriptRoot 'tools\Roboc.CorpusGen\bin\Release\net10.0-windows\Roboc.CorpusGen.exe'
if (Test-Path $genExe) {
    icacls $genExe /setintegritylevel Medium | Out-Null
}
$after = (cmd /c icacls "$exe" 2>&1 | Select-String 'Mandatory' | Select-Object -First 1)
Write-Host ("  之后: " + ($after -replace '\s+', ' '))

if ($after -notmatch 'Medium') {
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
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
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
