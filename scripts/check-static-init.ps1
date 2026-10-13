# 静态初始化检查
#
# 找出"用 Lang.Pick 初始化 static readonly 字段"的地方。
#
# 为什么这是一个必然的错误，而不是风格问题：
#
#   static readonly 字段只在【首次访问该类型】时求值一次，而那个时机由运行时决定，
#   不由你决定。Lang 是在 Main 的第一行才被设置的，所以任何在此之前被碰到的类型，
#   都会把标签冻结在"启动时的旧语言"上，之后改设置、重装菜单，写回的还是旧标签。
#
#   这个错误在本项目已经犯过两次：
#     RobocSettings.Catalogue       设置窗口文案冻结
#     ContextMenuInstaller.Verbs    右键菜单标签冻结（表现为"改语言没反应"）
#
#   两次都是编译通过、运行无异常、只是不生效——最难查的一类。
#
# 正确写法是表达式属性（`private static Verb[] Verbs => [...]`），每次访问重建。

param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$problems = @()

$files = Get-ChildItem (Join-Path $Root 'src'), (Join-Path $Root 'tools') -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' }

foreach ($file in $files) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)

    for ($i = 0; $i -lt $lines.Count; $i++) {
        # Comments are skipped, and that is not tidiness.
        #
        # The first version flagged the explanatory comment above the very field it had
        # just been told to stop flagging -- because that comment says the words "static
        # readonly" while explaining why they would be wrong here. A checker that reports
        # its own documentation is a checker nobody reads, which is the same as no checker.
        if ($lines[$i] -match '^\s*//') { continue }
        if ($lines[$i] -match '^\s*/\*') { continue }
        if ($lines[$i] -match '^\s*\*') { continue }

        $code = $lines[$i] -replace '//.*$', ''

        if ($code -notmatch 'static\s+readonly') { continue }

        # Scan forward for the initialiser's terminating semicolon, taking comments out as
        # it goes. Covers `= Lang.Pick(...)`, a collection expression opened with `= [`,
        # and `= new[] { ... }`.
        $initializer = $code
        $depth = ([regex]::Matches($code, '\[')).Count - ([regex]::Matches($code, '\]')).Count
        $isMultiLine = $code -notmatch ';\s*$'

        if ($isMultiLine) {
            for ($k = $i + 1; $k -lt [Math]::Min($i + 40, $lines.Count); $k++) {
                $candidate = $lines[$k] -replace '//.*$', ''

                if ($candidate -match '^\s*(\*|/\*)') { continue }

                $initializer += "`n" + $candidate
                $depth += ([regex]::Matches($candidate, '\[')).Count
                $depth -= ([regex]::Matches($candidate, '\]')).Count

                if ($depth -le 0 -and $candidate -match ';') { break }
            }
        }

        if ($initializer -match 'Lang\.Pick') {
            $relative = $file.FullName.Replace("$Root\", '')
            $problems += "$relative`:$($i + 1)  $($lines[$i].Trim())"
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host "静态初始化顺序问题：用 Lang.Pick 初始化了 static readonly 字段。" -ForegroundColor Red
    Write-Host ""
    Write-Host "这些标签会在类型首次被访问时冻结，而不是在语言确定之后。改为表达式属性："
    Write-Host "    private static Verb[] Verbs => [ ... ];"
    Write-Host ""
    $problems | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "静态初始化检查通过：没有用 Lang.Pick 初始化 static readonly 字段。"
exit 0
