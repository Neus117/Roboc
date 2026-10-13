# 把 MaintenanceCommands.cs 的中文字面量包成 Lang.Pick(中文, English)
#
# 键是字符串的原始内容（含转义与占位符），值是英文译文。
# 占位符 {..} 必须原样保留，否则插值会失效。

$map = [ordered]@{

# --- 取回确认 ---------------------------------------------------------------
'♻️选中的 {inBin.Count:N0} 项在 Roboc 回收站里。' = '♻️{inBin.Count:N0} of the selected items are in the Roboc recycle bin.'
'回收站中的项目无法再次回收，可以取回它们原来的位置：' = 'Items already in the bin cannot be recycled again. They can be put back where they came from:'
'是：取回原位置' = 'Yes: put them back'
'否：什么都不做' = 'No: do nothing'
'另有 {discardCount:N0} 项不在回收站中，将在取回之后照常回收。' = '{discardCount:N0} more are not in the bin and will be recycled as usual afterwards.'
'Roboc — 取回确认' = 'Roboc — Confirm restore'

# --- 无选中项 ---------------------------------------------------------------
'⚠️没有收到任何选中的项目。\r\n\r\n' = '⚠️No items were received.\r\n\r\n'
'如果这条是从右键菜单调用的，说明选中项没有传达到子菜单。' = 'If this was invoked from the context menu, the selection did not reach the submenu.'
'⚠️没有收到任何选中的项目。' = '⚠️No items were received.'

# --- 回收 -------------------------------------------------------------------
'已回收 {path}' = 'Recycled {path}'
'roboc: 无法回收 {path}：{DescribeBinError(error)}' = 'roboc: cannot recycle {path}: {DescribeBinError(error)}'
'回收完成：成功 {moved} 项，失败 {failed} 项' = 'Recycle finished: {moved} succeeded, {failed} failed'
'⚠️已回收 {moved} 项，{failures.Count} 项失败：\r\n\r\n' = '⚠️Recycled {moved}, {failures.Count} failed:\r\n\r\n'
'\r\n\r\n（另有 {failures.Count - 10} 项未显示）' = '\r\n\r\n({failures.Count - 10} more not shown)'
'Roboc 回收' = 'Roboc — Recycle'

# --- 取回 -------------------------------------------------------------------
'{path}\r\n  该项目不在 Roboc 回收站中，无法取回。' = '{path}\r\n  This item is not in the Roboc recycle bin, so it cannot be restored.'
'{path}\r\n  无法从回收站的存放位置推算出原路径。' = '{path}\r\n  The original location cannot be worked out from where it is stored.'
'  原位置已经有同名项目了。请先处理掉它，否则取回会覆盖掉它。' = '  Something with that name is already in the original location. Deal with it first, or restoring will overwrite it.'
'{path}\r\n  无法建立原目录：{ex.Message}' = '{path}\r\n  Cannot create the original folder: {ex.Message}'
'{path}\r\n  移动失败：{new System.ComponentModel.Win32Exception(moveError).Message}' = '{path}\r\n  Move failed: {new System.ComponentModel.Win32Exception(moveError).Message}'
'⚠️已取回 {restored} 项，{failures.Count} 项失败：' = '⚠️Restored {restored}, {failures.Count} failed:'
'取回完成：成功 {restored} 项，失败 {failures.Count} 项' = 'Restore finished: {restored} succeeded, {failures.Count} failed'

# --- 回收站列表 -------------------------------------------------------------
'♻️回收站是空的，没有可回收的内容。' = '♻️The recycle bin is empty.'
'Roboc 回收站' = 'Roboc — Recycle bin'
'♻️回收空间是空的，没有可回收的内容。' = '♻️The recycle bin is empty.'
'回收站中共有 {items.Count} 项，占用 {FormatBytes(totalBytes)}：' = '{items.Count} items in the recycle bin, using {FormatBytes(totalBytes)}:'
'存储位置：{string.Join(' = 'Stored at: {string.Join('
'加 --empty 参数可清空并释放空间。' = 'Pass --empty to empty it and reclaim the space.'

# --- 清空回收站 -------------------------------------------------------------
'roboc: 清空回收站不可恢复，需要 --yes 确认' = 'roboc: emptying the recycle bin cannot be undone; pass --yes to confirm'
'roboc: 无法删除 {PathUtil.ToDisplay(item.StoredPath)}：{DescribeDeleteError(deleteError)}' = 'roboc: cannot delete {PathUtil.ToDisplay(item.StoredPath)}: {DescribeDeleteError(deleteError)}'
'已释放 {FormatBytes(totalBytes)}，删除 {removed} 项' = 'Freed {FormatBytes(totalBytes)}, deleted {removed} items'
'，{failed} 项失败' = ', {failed} failed'

# --- 删除失败 ---------------------------------------------------------------
'⚠️彻底删除失败：\r\n\r\n{text}' = '⚠️Permanent delete failed:\r\n\r\n{text}'

# --- 诊断 -------------------------------------------------------------------
'Roboc 环境诊断' = 'Roboc environment report'
'程序位置   : {Environment.ProcessPath}' = 'Program    : {Environment.ProcessPath}'
'64 位进程  : {Environment.Is64BitProcess}' = '64-bit     : {Environment.Is64BitProcess}'
'完整性级别 : {Kernel32.DescribeIntegrityLevel()}' = 'Integrity  : {Kernel32.DescribeIntegrityLevel()}'
'输出编码   : {Console.OutputEncoding.CodePage} ({Console.OutputEncoding.WebName})' = 'Output enc : {Console.OutputEncoding.CodePage} ({Console.OutputEncoding.WebName})'
'系统 ANSI CP: {Kernel32.GetACP()} / OEM CP: {Kernel32.GetOEMCP()}' = 'System CPs : ANSI {Kernel32.GetACP()} / OEM {Kernel32.GetOEMCP()}'
'控制台输出 CP: {Kernel32.GetConsoleOutputCP()}' = 'Console CP : {Kernel32.GetConsoleOutputCP()}'
'有控制台   : {Kernel32.GetConsoleMode(Kernel32.GetStdHandle(Kernel32.STD_OUTPUT_HANDLE), out _) != 0}' = 'Console    : {Kernel32.GetConsoleMode(Kernel32.GetStdHandle(Kernel32.STD_OUTPUT_HANDLE), out _) != 0}'
'日志文件   : {ConsoleHost.LogPath ?? ' = 'Log file   : {ConsoleHost.LogPath ?? '
'HKCU\\Software 下的普通键' = 'an ordinary key under HKCU\\Software'
'HKCU\\Software\\Classes 根下' = 'the root of HKCU\\Software\\Classes'
'文件右键菜单区（键名含字面星号）' = 'the file context-menu area (key name contains a literal asterisk)'
'文件夹右键菜单区' = 'the folder context-menu area'
'本程序实际要写的位置' = 'where this program actually writes'
'注册表写入测试：' = 'Registry write test:'
'失败：CreateSubKey 返回了 null' = 'failed: CreateSubKey returned null'
'可写' = 'writable'
'被拒绝：{ex.Message}' = 'denied: {ex.Message}'
'结论：{usable}/{probes.Length} 个位置可写。' = 'Result: {usable} of {probes.Length} locations are writable.'
'  所有位置都被拒绝，说明整个 HKCU 写入对本程序被拦截。' = '  Every location was denied, so HKCU writes are being blocked for this program entirely.'
'  普通用户对自己的 HKCU 有完全控制权，这不是 Windows 权限的结果。' = '  An ordinary user has full control over their own HKCU, so this is not a Windows permission.'
'  请检查安全软件（如 360、火绒、电脑管家）是否拦截了本程序写注册表。' = '  Check whether security software is blocking this program from writing to the registry.'
'  只有部分位置被拒绝。若被拒的都是右键菜单相关的键，' = '  Only some locations were denied. If the denials are all context-menu related,'
'  则是安全软件针对 shell 扩展注册位置的定向拦截。' = '  it is security software targeting shell-extension registration specifically.'
'  所有位置都可写，右键菜单应当可以正常安装。' = '  Every location is writable, so the context menu should install normally.'

# --- 删除错误 ---------------------------------------------------------------
'未提供错误码' = 'no error code given'
'找不到该项目（可能已被删除）' = 'not found (it may already be deleted)'
'路径不存在' = 'the path does not exist'
'拒绝访问：文件可能是只读的，或正被其他程序占用' = 'access denied: the file may be read-only, or in use by another program'
'文件正被其他程序占用' = 'the file is in use by another program'
'目录不是空的：其中有无法删除的项目' = 'the folder is not empty: something inside it could not be deleted'
'错误码 {error}' = 'error {error}'

# --- 彻底删除确认 -----------------------------------------------------------
'⚠️即将彻底删除 {paths.Count:N0} 项：' = '⚠️About to permanently delete {paths.Count:N0} items:'
'⚠️即将彻底删除 {paths.Count:N0} 项（其中部分内容仍在统计）：' = '⚠️About to permanently delete {paths.Count:N0} items (still counting some of them):'
'包含文件' = 'Files'
'{files:N0} 个' = '{files:N0}'
'{folders:N0} 个' = '{folders:N0}'
'包含文件夹' = 'Folders'
'共占空间' = 'Total size'
'⚠️此操作无法撤销。' = '⚠️This cannot be undone.'
'确定要继续吗？' = 'Continue?'
'Roboc — 不可恢复的操作' = 'Roboc — Irreversible'

# --- 粘贴冲突 ---------------------------------------------------------------
'⚠️目标位置已有 {scan.Conflicts:N0} 个同名项目：' = '⚠️The destination already has {scan.Conflicts:N0} items with the same name:'
'⚠️目标位置至少有 {scan.Conflicts:N0} 个同名项目（仍在统计中）：' = '⚠️The destination has at least {scan.Conflicts:N0} items with the same name (still counting):'
'内容相同' = 'Identical'
'{scan.Identical:N0} 个文件' = '{scan.Identical:N0} files'
'{scan.Different:N0} 个文件' = '{scan.Different:N0} files'
'内容不同' = 'Different'
'类型不同' = 'Wrong type'
'{scan.TypeMismatch:N0} 个' = '{scan.TypeMismatch:N0}'
'涉及大小' = 'Size involved'
'目标位置没有同名项目，将全部作为新增内容粘贴。' = 'Nothing at the destination shares these names; everything will be pasted as new.'
'是：全部替换为，剪贴板中的版本' = 'Yes: replace all with the clipboard versions'
'否：保留原有项目，粘贴新增内容' = 'No: keep what is there and paste only what is new'
'将新增文件' = 'New files'
'{scan.NewFiles:N0} 个' = '{scan.NewFiles:N0}'
'{scan.NewFolders:N0} 个' = '{scan.NewFolders:N0}'
'将新增文件夹' = 'New folders'
'{scan.MergedFolders:N0} 个同名文件夹' = '{scan.MergedFolders:N0} folders will be merged'
'将合并' = 'Merged'
'取消：什么都不做' = 'Cancel: do nothing'
'Roboc — 目标已存在同名项目' = 'Roboc — Name already exists at the destination'

# --- 回收失败原因 -----------------------------------------------------------
'该位置所在的驱动器不支持独立的 Roboc 回收站' = 'the drive cannot hold a separate Roboc recycle bin'
'该位置的路径无法映射到回收站中（例如网络路径）' = 'the path cannot be mapped into the bin (a network path, for instance)'
'该项目已经在回收站里了，不能重复回收' = 'this item is already in the bin and cannot be recycled twice'
'回收站中这个位置已经有一份了。请先把回收站里的那一份处理掉，' = 'The bin already holds something from that location. Deal with that copy first,'
'否则无法判断该恢复哪一份。' = 'or there is no way to tell which one to restore.'
'拒绝访问：无法在所在驱动器建立回收站' = 'access denied: the recycle bin could not be created on that drive'
}

$path = Join-Path $PSScriptRoot '..\src\Roboc.App\Cli\MaintenanceCommands.cs'
$text = [System.IO.File]::ReadAllText($path, (New-Object System.Text.UTF8Encoding($false)))
$applied = 0
$missed = @()

foreach ($zh in $map.Keys) {
    $en = $map[$zh]

    # 插值字符串与普通字符串各试一次
    foreach ($prefix in @('$"', '"')) {
        $from = $prefix + $zh + '"'
        if ($text.Contains($from)) {
            $to = 'Lang.Pick(' + $prefix + $zh + '", ' + $prefix + $en + '")'
            $text = $text.Replace($from, $to)
            $applied++
            break
        }
    }
}

foreach ($zh in $map.Keys) {
    if ($text.Contains('"' + $zh + '"') -or $text.Contains('$"' + $zh + '"')) { $missed += $zh }
}

[System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "  已替换: $applied / $($map.Count)"
if ($missed.Count -gt 0) {
    Write-Host "  未匹配到:"
    $missed | ForEach-Object { Write-Host "    $_" }
}

# 剩余未处理的中文字面量
$lines = [System.IO.File]::ReadAllLines($path)
$left = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i].TrimStart() -match '^//|^\*|^/\*') { continue }
    foreach ($m in [regex]::Matches($lines[$i], '"((?:[^"\\]|\\.)*)"')) {
        if ($m.Groups[1].Value -match '[\u4e00-\u9fff]') { $left++ }
    }
}
Write-Host "  剩余含中文的字符串字面量: $left"
