# 第二批 i18n：UninstallCommand / ShellCommand / ContextMenuInstaller
#
# 写入时必须带 BOM，否则 PowerShell 5.1 按 GBK 读中文，满屏假语法错误。

$files = @(
    'src\Roboc.App\Cli\UninstallCommand.cs',
    'src\Roboc.App\Cli\ShellCommand.cs',
    'src\Roboc.Core\Shell\ContextMenuInstaller.cs'
)

$map = [ordered]@{

# --- UninstallCommand -------------------------------------------------------
'设置已保存。' = 'Settings saved.'
'⚠️即将移除 Roboc：' = '⚠️About to remove Roboc:'
'　✅ 右键菜单与发送到菜单' = '  ✅ Context menu and Send To entry'
'　✅ 程序数据与设置' = '  ✅ Program data and settings'
'　✅ 临时痕迹' = '  ✅ Temporary traces'
'⚠️不会动你的回收站——里面是文件，不是程序数据：' = '⚠️Your recycle bins will not be touched -- they hold files, not program data:'
'　要取回就打开它，不要了就自行删除。' = '  Open them to get things back, or delete them yourself if you do not want them.'
'移除后，再删除程序文件夹即可彻底清除：' = 'After that, deleting the program folder removes everything:'
'Roboc — 确认卸载' = 'Roboc — Confirm removal'
'右键菜单（文件、文件夹、文件夹背景）' = 'Context menu (files, folders, folder background)'
'发送到菜单' = 'Send To entry'
'移除 Shell 集成：{ex.Message}' = 'removing shell integration: {ex.Message}'
'临时数据  {local}' = 'temporary data  {local}'
'程序数据  {name}' = 'program data    {name}'
'⚠️已清除以下内容：' = '⚠️Removed:'
'⚠️以下内容未能清除：' = '⚠️Could not be removed:'
'⚠️回收站未动——里面是你的文件：' = '⚠️Recycle bins untouched -- they hold your files:'
'现在删除这个文件夹即可彻底清除：' = 'Delete this folder now to remove everything:'
'Roboc — 卸载' = 'Roboc — Removed'
'Roboc 已从系统中移除。' = 'Roboc has been removed from the system.'
'回收站未动——里面是你要么取回、要么自行删除的文件：' = 'Recycle bins untouched -- they hold files for you to recover or delete yourself:'

# --- ShellCommand -----------------------------------------------------------
'⚠️没有收到任何选中的项目。\r\n\r\n' = '⚠️No items were received.\r\n\r\n'
'如果这条是从右键菜单调用的，说明选中项没有传达到子菜单。' = 'If this came from the context menu, the selection did not reach the submenu.'
'⚠️没有收到任何选中的项目。' = '⚠️No items were received.'
'⚠️写入剪贴板失败：{failure}' = '⚠️Could not write to the clipboard: {failure}'
'roboc: 无法更新粘贴菜单项的状态：{syncFailure}' = 'roboc: cannot update the paste menu entry: {syncFailure}'
'已复制 {paths.Count} 项到剪贴板' = 'Copied {paths.Count} items to the clipboard'
'⚠️剪贴板里没有文件。\r\n\r\n请先对要复制的项目使用 Roboc → 复制。' = '⚠️There are no files on the clipboard.\r\n\r\nUse Roboc → Copy on what you want to copy first.'
'入口位置：{SendToMenu.SendToDirectory}' = 'Installed at: {SendToMenu.SendToDirectory}'
'右键任意文件 → 发送到 → 选择 Roboc 复制 / 回收 / 彻底删除。' = 'Right-click any file → Send to → pick Roboc Copy / Recycle / Delete.'
'多选时全部选中项会一次传给程序。' = 'With several items selected, all of them are passed in one go.'
'提示：--machine 写入 HKLM，需要管理员权限；当前进程未提权，可能失败。' = 'Note: --machine writes to HKLM and needs administrator rights. This process is not elevated, so it may fail.'
'使用平级菜单布局（非级联）' = 'Using the flat menu layout (not cascaded)'
'roboc: 无法写入 {path}：{ex.Message}' = 'roboc: cannot write {path}: {ex.Message}'
'已导出注册表文件：{path}' = 'Registry file written: {path}'
'双击导入即可（regedit 是 Windows 受信任程序，不受针对本程序的限制）。' = 'Double-click it to import. (regedit is a trusted Windows program and is not subject to the restrictions placed on this one.)'
'导入前可以先用记事本打开查看内容。' = 'You can open it in Notepad first to see what it contains.'
'导入后如未立即生效，重启资源管理器或注销重登。' = 'If it does not take effect immediately, restart Explorer or sign out and back in.'
'{files:N0} 个文件' = '{files:N0} files'
'{folders:N0} 个文件夹' = '{folders:N0} folders'
'  0 项' = '  0 items'
'发送到菜单 : {(SendToMenu.IsInstalled ? ' = 'Send To    : {(SendToMenu.IsInstalled ? '

# --- ContextMenuInstaller ---------------------------------------------------
# 菜单项名称在安装时写入注册表，所以语言在安装那一刻固定。
'复制' = 'Copy'
'粘贴' = 'Paste'
'删除' = 'Delete'
'回收' = 'Recycle'
'回收站' = 'Recycle bin'
'设置' = 'Settings'
'注册表写入被拒绝：{ex.Message}' = 'registry write denied: {ex.Message}'
'已注册 {written} 个右键菜单项（HKCU，无需管理员）' = 'Registered {written} context-menu entries (HKCU, no administrator needed)'
'未找到已安装的菜单项' = 'no installed menu entries were found'
'无法启动 reg.exe' = 'could not start reg.exe'
')}] 已移除 {removed} 个右键菜单项' = ')}] Removed {removed} context-menu entries'
}

$root = Split-Path -Parent $PSScriptRoot
$total = 0

foreach ($relative in $files) {
    $path = Join-Path $root $relative
    if (-not (Test-Path $path)) { Write-Host "  跳过（不存在）: $relative"; continue }

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
    Write-Host "  $([System.IO.Path]::GetFileName($path)): 替换 $applied 处"
    $total += $applied

    # 剩余未包裹的中文
    $lines = [System.IO.File]::ReadAllLines($path)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].TrimStart() -match '^//|^\*|^/\*') { continue }
        if ($lines[$i] -match 'Lang\.Pick') { continue }
        foreach ($m in [regex]::Matches($lines[$i], '"((?:[^"\\]|\\.)*)"')) {
            if ($m.Groups[1].Value -match '[\u4e00-\u9fff]') {
                Write-Host "    遗留 :$($i+1)  $($lines[$i].Trim())"
            }
        }
    }
}

Write-Host "  合计替换: $total"
