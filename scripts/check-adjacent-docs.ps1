# 检出"一个文档块之后没等到成员，却又开始了下一块"。
#
# 判据（刻意保守）：
#   从一行 /// 开始，向下收集连续的 /// 行；
#   若这一段里出现两次【行首】的 /// <summary>，即两段文档叠在一起，报一处。
#
# 为什么必须要求"行首"：
#   正文里提到 "<summary> element" 会被当成块起始。本项目注释里这类说明很常见，
#   第一版就是这么误报的。
#
# 已知取舍（2026-10-15 定的，别当成 bug 去"修"）：
#   行内形态 /// <summary>一句话。</summary> 抓不到 —— 它行首是 <summary>，
#   但两行行内块不会被算成"同一段里两个起始"。真实历史里 8 处全是独占一行形态，
#   行内只有 1 处（SettingsWindow.HitLanguage）。
#
#   试过四种更激进的判据，都以"同时误报与漏报"告终，其中一个版本还把
#   SettingsWindow.cs 的正确写法（类文档紧接枚举文档，两个块各自闭合）报了出
#   来。结论：这一段能做对的机械判据只有这一种，宁漏勿误报 ——
#   **一个会误报的门禁会被人关掉，那比没有更糟。**
#
#   根因：这 8 处的共同点是【语义】的 —— "这段注释描述的不是它下面那个成员"，
#   而机械判据只看结构。要更准就得比较内容与成员名，那是另一个量级的工作。
param([string] $Root = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'

$files = @(Get-ChildItem $Root -Recurse -Include *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|artifacts|\.git)\\' })

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
