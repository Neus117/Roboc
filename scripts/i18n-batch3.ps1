# 第三批 i18n：Program.FirstRun / SettingsWindow

$files = @(
    'src\Roboc.App\Program.cs',
    'src\Roboc.App\SettingsWindow.cs'
)

$map = [ordered]@{

# --- Program.FirstRun -------------------------------------------------------
'可以改用命令行使用：\r\n' = 'You can still use it from the command line:\r\n'
'　roboc copy --to <目标> <来源>\r\n' = '  roboc copy --to <destination> <source>\r\n'
'　roboc --help　查看全部命令' = '  roboc --help   lists every command'
'Roboc — 注册失败' = 'Roboc — Registration failed'
'✅Roboc 已经可以使用。\r\n\r\n' = '✅Roboc is ready to use.\r\n\r\n'
'在资源管理器中选中文件或文件夹，右键即可看到 Roboc 菜单。' = 'Select a file or folder in Explorer and right-click to find the Roboc menu.'
'✅已为你注册右键菜单：\r\n\r\n' = '✅The context menu has been registered:\r\n\r\n'
'　复制　粘贴　删除　回收　回收站　设置\r\n\r\n' = '  Copy   Paste   Delete   Recycle   Recycle bin   Settings\r\n\r\n'
'在资源管理器中选中文件或文件夹，右键即可看到它们。\r\n\r\n' = 'Select a file or folder in Explorer and right-click to find them.\r\n\r\n'
'「设置」里可以开关各项功能——每一项都写明了它的收益与代价，\r\n' = '"Settings" turns individual features on and off, and states what each one'
'「卸载」也在那里。\r\n\r\n' = 'costs and what it buys. "Remove" lives there too.\r\n\r\n'
'这个程序不需要安装。把整个文件夹放在哪里都行，\r\n' = 'There is nothing to install. Put the folder wherever you like;'
'不想用了执行一次「卸载」，再删掉文件夹即可。' = 'when you are done, run "Remove" once and delete the folder.'
'程序位置：\r\n　{folder}' = 'Location:\r\n  {folder}'
'Roboc 已就绪' = 'Roboc is ready'

# --- SettingsWindow ---------------------------------------------------------
'Roboc 设置' = 'Roboc Settings'
'⚠️设置无法保存：\r\n\r\n{error}\r\n\r\n' = '⚠️Settings could not be saved:\r\n\r\n{error}\r\n\r\n'
'程序仍按原有设置运行。' = 'The program will carry on with the settings it already had.'
'Roboc — 保存失败' = 'Roboc — Save failed'
'默认：开' = 'default: on'
'默认：关' = 'default: off'
'卸载' = 'Remove'
'取消' = 'Cancel'
'确定' = 'OK'
}

$root = Split-Path -Parent $PSScriptRoot
$total = 0

foreach ($relative in $files) {
    $path = Join-Path $root $relative
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
}

Write-Host "  合计: $total"
