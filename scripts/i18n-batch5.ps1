# 第五批：ProgressWindow.cs（进度窗，21 处）
#
# 这个文件被漏掉了：它出现在待办清单里，却没有出现在第四批的文件数组中。
# 结果是进度窗的标题栏是英文、正文是中文。

$files = @(
    'src\Roboc.App\ProgressWindow.cs',
    'src\Roboc.App\MessageDialog.cs'
)

$map = [ordered]@{

# --- 按钮与标签 -------------------------------------------------------------
'暂停' = 'Pause'
'继续' = 'Resume'
'取消' = 'Cancel'
'已暂停' = 'Paused'
'正在停止…' = 'Stopping...'
'正在准备…' = 'Preparing...'

# --- 停止确认 ---------------------------------------------------------------
'⚠️确定要停止当前操作吗？\r\n\r\n已经完成的项目会保留，尚未处理的会被放弃。' = '⚠️Stop this operation?\r\n\r\nItems already finished are kept; anything not yet started is abandoned.'
'Roboc — 停止操作' = 'Roboc — Stop'

# --- 状态行（无障碍文本）----------------------------------------------------
'名称: {_phase}' = 'Name: {_phase}'
'剩余项目: {items:N0} ({FormatBytes(bytes)})' = 'Items left: {items:N0} ({FormatBytes(bytes)})'
'速度: {FormatBytes(speed)}/秒' = 'Speed: {FormatBytes(speed)}/s'

# --- 画面上的四行 -----------------------------------------------------------
'已完成 {percent}%' = '{percent}% done'
'剩余项目 {items:N0} ({FormatBytes(bytes)})' = '{items:N0} left ({FormatBytes(bytes)})'
'速度 {FormatBytes(speed)}/秒' = '{FormatBytes(speed)}/s'

# 时间：中文说"大约 5 秒"，英文说"about 5 seconds"，单复数也不同。
'剩余时间 大约 {seconds / 60} 分 {seconds % 60} 秒' = 'about {seconds / 60} min {seconds % 60} s left'
'剩余时间 大约 {seconds} 秒' = 'about {seconds} s left'
'剩余时间: 大约 {seconds / 60} 分 {seconds % 60} 秒' = 'Time left: about {seconds / 60} min {seconds % 60} s'
'剩余时间: 大约 {seconds} 秒' = 'Time left: about {seconds} s'

# --- 暂停按钮的字段初值（会被下面两处覆盖，但初值也要对）--------------------
'_pauseLabel = "暂停";' = '_pauseLabel = Lang.Pick("暂停", "Pause");'
'_pauseLabel = "继续";' = '_pauseLabel = Lang.Pick("继续", "Resume");'
}

$root = Split-Path -Parent $PSScriptRoot
$total = 0

foreach ($relative in $files) {
    $path = Join-Path $root $relative
    if (-not (Test-Path $path)) { continue }

    $text = [System.IO.File]::ReadAllText($path, (New-Object System.Text.UTF8Encoding($false)))
    $applied = 0

    foreach ($zh in $map.Keys) {
        $en = $map[$zh]

        foreach ($prefix in @('$"', '"')) {
            $from = $prefix + $zh + '"'
            if ($text.Contains($from) -and -not $text.Contains('Lang.Pick(' + $from)) {
                $to = 'Lang.Pick(' + $prefix + $zh + '", ' + $prefix + $en + '")'
                $text = $text.Replace($from, $to)
                $applied++
                break
            }
        }
    }

    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  $([System.IO.Path]::GetFileName($path)): $applied 处"
    $total += $applied
}

Write-Host "  合计: $total"

# 剩余
Write-Host ""
Write-Host "  剩余："
foreach ($relative in $files) {
    $path = Join-Path $root $relative
    if (-not (Test-Path $path)) { continue }
    $lines = [System.IO.File]::ReadAllLines($path)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].TrimStart() -match '^//|^\*|^/\*') { continue }
        if ($lines[$i] -match 'Lang\.Pick') { continue }
        foreach ($m in [regex]::Matches($lines[$i], '"((?:[^"\\]|\\.)*)"')) {
            if ($m.Groups[1].Value -match '[\u4e00-\u9fff]') {
                Write-Host "    $([System.IO.Path]::GetFileName($path)):$($i+1)  $($lines[$i].Trim())"
            }
        }
    }
}
