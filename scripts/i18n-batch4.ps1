# 第四批（最后一批）i18n：引擎与剩余 UI
#
# 有两类字符串刻意不翻译，理由见下方 NOT_TRANSLATED。

$files = @(
    'src\Roboc.Core\Fs\RecycleBin.cs',
    'src\Roboc.Core\Copy\CopyEngine.cs',
    'src\Roboc.Core\Shell\SendToMenu.cs',
    'src\Roboc.App\Cli\CopyCommand.cs',
    'src\Roboc.App\Cli\DeleteCommand.cs',
    'src\Roboc.App\MessageDialog.cs',
    'src\Roboc.Core\Copy\Verifier.cs',
    'src\Roboc.Core\Interop\Kernel32.cs',
    'src\Roboc.Core\Delete\DeleteEngine.cs',
    'src\Roboc.Core\Shell\ClipboardFiles.cs'
)

# NOT_TRANSLATED —— 这两类必须保持原样：
#
#   RecycleBin 的标记文件名（Roboc.回收信息.txt / .Roboc回收信息.txt）
#     它们是回收站的识别依据：取回时要靠名字找到标记文件并读回原路径。
#     名字若跟着语言变，中文环境下回收的项目在英文环境下就认不出来——
#     而这些名字不是给人看的界面文字，是给程序看的键。
#
#   BinIndex 的文件头（# Roboc 回收索引 …）
#     同上：这是索引文件的格式标识，改语言等于改格式。
#
# 若将来要改，正确做法是换成语言中立的固定名（如 Roboc.info.txt），
# 并保留对旧名的读取——而不是让它随语言浮动。
$notTranslated = @(
    'Roboc.回收信息.txt',
    '.Roboc回收信息.txt',
    '# Roboc 回收索引　卷\t名称\t原路径\t回收时间'
)

$map = [ordered]@{

# --- RecycleBin：标记文件的内容（给人读的，要翻译）-------------------------
'此项目由 Roboc 回收。\r\n' = 'Recycled by Roboc.\r\n'
'原位置：' = 'Original location: '
'回收时间：' = 'Recycled at:      '
'取回方法：在资源管理器中选中此项目，右键 Roboc → 回收。\r\n' = 'To restore: select this item in Explorer and choose Roboc -> Recycle.\r\n'
'也可以直接把这个项目移回上面的原位置。\r\n' = 'You can also move it back to the original location shown above.\r\n'
'删除此文件会导致回收站无法判断该项目应还原到哪里。\r\n' = 'Deleting this file leaves the bin unable to tell where the item belongs.\r\n'
'此目录由 Roboc 回收。\r\n' = 'Recycled by Roboc.\r\n'
'还原方法：把此目录移回上面的原位置即可。\r\n' = 'To restore: move this folder back to the original location shown above.\r\n'
'此文件是识别标记，删除它会导致回收站无法识别此目录。\r\n' = 'This file is the marker. Deleting it leaves the bin unable to recognise the folder.\r\n'

# --- CopyEngine -------------------------------------------------------------
'磁盘空间不足，任务已中止。已完成的项目保留，尚未处理的项目未开始。' = 'Out of disk space; the job was stopped. Finished items are kept, unstarted ones were not begun.'
'硬链接映射已达上限 {LinkMapLimit:N0} 条，其后的多名称文件按独立副本复制。' = 'The hard-link map reached its limit of {LinkMapLimit:N0}; files with several names after that point were copied as independent copies.'
'目标占用会大于源，内容不受影响。' = 'The destination will use more space than the source. The contents are unaffected.'
'未发现残留的暂存文件。' = 'No leftover staging files were found.'
'发现 {found.Count} 个残留的暂存文件，共 {Describe(total)}（未删除，供你确认后再处理）：' = 'Found {found.Count} leftover staging files, {Describe(total)} in total. They have not been deleted, so you can look before deciding:'
' …另有 {found.Count - 20} 个未列出' = ' ...{found.Count - 20} more not listed'
'这些是上一次传输留下的临时文件。确认无用后删除它们可释放 {Describe(total)}，然后重新执行复制。' = 'These are temporary files left by an earlier transfer. Once you are satisfied they are of no use, deleting them frees {Describe(total)}; then run the copy again.'
'枚举残留暂存文件时出错：{ex.Message}' = 'Error while scanning for leftover staging files: {ex.Message}'
'复制后校验失败：{Verifier.Describe(offset)}（目标文件已保留，未删除）' = 'Verification failed after copying: {Verifier.Describe(offset)} (the destination file was kept, not deleted)'
'未复制的数据流：{string.Join(' = 'Streams not copied: {string.Join('

# --- SendToMenu -------------------------------------------------------------
'彻底删除' = 'Delete permanently'
'无法访问发送到目录：{ex.Message}' = 'Cannot reach the Send To folder: {ex.Message}'
'无法创建 {name}：{error}；备用方式也失败：{fallbackError}' = 'Cannot create {name}: {error}; the fallback failed too: {fallbackError}'
'已在“发送到”菜单中创建 {written} 个入口' = 'Created {written} entries in the Send To menu'
'移除时出错：{ex.Message}' = 'Error while removing: {ex.Message}'
'已从“发送到”菜单移除 {removed} 个入口' = 'Removed {removed} entries from the Send To menu'
'通知外壳失败：{ex.Message}' = 'Could not notify the shell: {ex.Message}'

# --- CopyCommand ------------------------------------------------------------
'正在复制' = 'Copying'
'正在准备…' = 'Preparing...'
'{snapshot.FilesTotal:N0} 个项目' = '{snapshot.FilesTotal:N0} items'
'所选项目' = 'the selection'
'正在将 {count}从 {from} {verb}到 {Path.GetFileName(destination.TrimEnd(' = 'Copying {count} from {from} to {Path.GetFileName(destination.TrimEnd('
'⚠️复制 {result.FilesCopied:N0} 项成功，{result.FilesFailed:N0} 项失败。' = '⚠️{result.FilesCopied:N0} copied, {result.FilesFailed:N0} failed.'
'…另有 {result.Errors.Count - 8} 条错误未显示' = '...{result.Errors.Count - 8} more errors not shown'
'（未记录具体错误，请用 --show-journal 查看日志位置）' = '(no specific errors were recorded; use --show-journal to find the log)'

# --- DeleteCommand ----------------------------------------------------------
'正在处理' = 'Working'
'正在删除 {snapshot.FilesTotal:N0} 个项目' = 'Deleting {snapshot.FilesTotal:N0} items'
'正在删除' = 'Deleting'
'没有收到任何项目。' = 'No items were received.'
'为防止误操作，删除需要 --yes 参数确认。' = 'Deletion needs --yes to confirm, so that it cannot happen by accident.'
'文件不会进入任何回收站' = 'Files do not go to any recycle bin'
'操作已取消。' = 'Cancelled.'
'操作失败：{ex.Message}' = 'Failed: {ex.Message}'

# --- MessageDialog ----------------------------------------------------------
'是' = 'Yes'
'否' = 'No'

# --- Verifier ---------------------------------------------------------------
'内容不一致' = 'contents differ'
'内容在第 {offset:N0} 字节处开始不同' = 'contents first differ at byte {offset:N0}'

# --- Kernel32 ---------------------------------------------------------------
'无法打开进程令牌' = 'cannot open the process token'
'无法查询完整性级别' = 'cannot query the integrity level'
'完整性级别 SID 无效' = 'the integrity level SID is not valid'
'Low (4096) -- 无法写 HKCU\\Software' = 'Low (4096) -- cannot write to HKCU\\Software'
'未知 ({level})' = 'unknown ({level})'

# --- DeleteEngine -----------------------------------------------------------
'指定的项目都不存在，可能已经被删除或移动过了。' = 'None of the given items exist; they may already have been deleted or moved.'

# --- ClipboardFiles ---------------------------------------------------------
'剪贴板正被其他程序占用，暂时无法写入。请稍后重试。' = 'Another program is holding the clipboard. Try again in a moment.'
}

$root = Split-Path -Parent $PSScriptRoot
$total = 0

foreach ($relative in $files) {
    $path = Join-Path $root $relative
    if (-not (Test-Path $path)) { continue }

    $text = [System.IO.File]::ReadAllText($path, (New-Object System.Text.UTF8Encoding($false)))
    $applied = 0

    foreach ($zh in $map.Keys) {
        if ($notTranslated -contains $zh) { continue }

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
    if ($applied -gt 0) { Write-Host "  $([System.IO.Path]::GetFileName($path)): $applied 处" }
    $total += $applied
}

Write-Host "  合计: $total"

# 未包裹的剩余中文（排除刻意保留的）
Write-Host ""
Write-Host "  剩余（应只有刻意保留的）："
foreach ($relative in $files) {
    $path = Join-Path $root $relative
    if (-not (Test-Path $path)) { continue }
    $lines = [System.IO.File]::ReadAllLines($path)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].TrimStart() -match '^//|^\*|^/\*') { continue }
        if ($lines[$i] -match 'Lang\.Pick') { continue }
        foreach ($m in [regex]::Matches($lines[$i], '"((?:[^"\\]|\\.)*)"')) {
            $s = $m.Groups[1].Value
            if ($s -match '[\u4e00-\u9fff]') {
                Write-Host "    $([System.IO.Path]::GetFileName($path)):$($i+1)  $s"
            }
        }
    }
}
