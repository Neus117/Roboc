# 检出"一对相邻 XML 文档注释"：同一个成员头上压着两个块。
#
# 判据（务必与脚本保持一致）：
#   - 从一行 /// 开始，向上连续收集 /// 行
#   - 若这一段里出现两次起首的 <summary>（行首就是 <summary>，不是行中间出现），
#     且紧随其后的是成员声明，则这是一处错位
#
# 为什么不用"包含 <summary>"：多行文本里可能出现这个词，会误报。
param([string] $Root = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'

$files = @(Get-ChildItem $Root -Recurse -Include *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })

$total = 0

foreach ($file in $files) {
    $lines = Get-Content $file.FullName -Encoding UTF8
    $i = 0

    while ($i -lt $lines.Count) {
        if ($lines[$i] -notmatch '^\s*///') { $i++; continue }

        # 向下收集这一段连续的 /// 行
        $start = $i
        $end = $i
        while ($end + 1 -lt $lines.Count -and $lines[$end + 1] -match '^\s*///') { $end++ }

        $block = $lines[$start..$end]
        $starts = @($block | Where-Object { $_ -match '^\s*///\s*<summary>\s*$' }).Count

        if ($starts -ge 2) {
            # 这段注释属于紧随其后的那个成员
            $member = if ($end + 1 -lt $lines.Count) { $lines[$end + 1].Trim() } else { '(文件末尾)' }
            $rel = $file.FullName.Replace($Root, '').TrimStart('\')

            Write-Host ("  {0}:{1}" -f $rel, ($start + 1))
            Write-Host ("      第一块属于别处，应删除；成员是: {0}" -f $member.Substring(0, [Math]::Min(72, $member.Length)))
            $total++
        }

        $i = $end + 1
    }
}

Write-Host ''
if ($total -eq 0) {
    Write-Host '  ✓ 没有相邻的重复文档块'
    exit 0
}

Write-Host ("  ★ 共 {0} 处" -f $total)
exit 1
