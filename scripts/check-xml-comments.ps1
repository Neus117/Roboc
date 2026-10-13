# XML 注释检查
#
# 找出 XML 注释里的双连字符。
#
# 为什么需要这个检查，而不是靠小心：
#
#   XML 规范禁止注释中出现 "--"，也不允许注释以 "-" 结尾。违反时 MSBuild 报的是
#
#     error MSB4025: 未能加载项目文件。An XML comment cannot contain '--'
#
#   这个报错本身很清楚，问题在于【它出现的时机】：项目加载失败意味着【什么都没构建】，
#   于是构建输出的警告数和错误数都是 0。
#
#   于是"0 警告"既可能是"构建干净"，也可能是"构建根本没跑"。
#   本项目已经因此误判过一次：把一次项目加载失败读成了"裁剪警告已抑制成功"。
#
#   一天之内这个错误犯了四次，四次都是同一个原因：中文写作里的破折号习惯，
#   在英文里落成了 `--`。
#
#   靠记性没用 —— 靠检查。

param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$problems = @()

$files = Get-ChildItem $Root -Recurse -Include '*.props','*.csproj','*.targets','*.slnx','*.xml','*.config' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\|\\artifacts\\|\\.git\\' }

foreach ($file in $files) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    $inComment = $false

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        if ($line -match '<!--') { $inComment = $true }

        if ($inComment) {
            # 去掉定界符本身，再看剩下的部分是否含双连字符。
            $inner = $line -replace '<!--', '' -replace '-->', ''

            if ($inner -match '--') {
                $relative = $file.FullName.Replace("$Root\", '')
                $problems += "$relative`:$($i + 1)  $($line.Trim())"
            }
        }

        if ($line -match '-->') { $inComment = $false }
    }
}

if ($problems.Count -gt 0) {
    Write-Host "XML 注释里出现了双连字符，项目将无法加载：" -ForegroundColor Red
    Write-Host ""
    $problems | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    Write-Host "XML 规范禁止注释中出现 '--'，也不允许以 '-' 结尾。改成单个连字符或逗号。"
    Write-Host ""
    Write-Host "注意为什么这条要自动检查：项目加载失败时【什么都没构建】，"
    Write-Host "所以构建输出里的警告数是 0 —— 和'构建干净'长得一模一样。"
    exit 1
}

Write-Host "XML 注释检查通过：$($files.Count) 个文件，无双连字符。"
exit 0
