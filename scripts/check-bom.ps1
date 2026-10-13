# BOM 检查
#
# 所有 .ps1 必须带 UTF-8 BOM。
#
# 为什么这不是洁癖：
#
#   PowerShell 5.1 对【没有 BOM】的 .ps1 按系统 ANSI 代码页解码。在中文 Windows 上
#   就是 GBK，于是文件里的中文变成乱码，进而引发一串【看起来像语法错误】的报错：
#
#     Unexpected token '鏃犳硶灏嗘湳璇瘑鍒负' in expression or statement
#     Missing closing '}' in statement block
#
#   报错指向的行号和内容都是乱码，读起来像代码写坏了，实际是编码丢了。
#
#   这个坑在本项目已经踩过四次，其中三次是编辑工具写回时静默剥掉了 BOM。
#   人眼看不见 BOM，所以只能靠检查。
#
# .editorconfig 里的 charset = utf-8-bom 只在编辑器写入时生效，
# 对脚本、工具链、以及任何直接写文件的程序都无效。这就是需要这个检查的原因。

param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$missing = @()

$scripts = Get-ChildItem $Root -Recurse -Filter '*.ps1' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\|\\artifacts\\|\\.git\\' }

foreach ($script in $scripts) {
    $bytes = [System.IO.File]::ReadAllBytes($script.FullName)

    $hasBom = $bytes.Length -ge 3 -and
              $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF

    if (-not $hasBom) {
        $missing += $script.FullName.Replace("$Root\", '')
    }
}

if ($missing.Count -gt 0) {
    Write-Host "以下 .ps1 缺少 UTF-8 BOM：" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    Write-Host "PowerShell 5.1 会把它们按 GBK 解码，中文变成乱码并报出一串假语法错误。"
    Write-Host "修复：以 UTF-8 with BOM 重新保存。"
    exit 1
}

Write-Host "BOM 检查通过：$($scripts.Count) 个 .ps1 全部带 BOM。"
exit 0
