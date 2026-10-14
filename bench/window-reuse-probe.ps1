# 判断 SHOpenFolderAndSelectItems 是否复用了"已经打开的目标文件夹窗口"。
#
# 为什么记录 HWND 而不是窗口数：
#   用户报的症状是"目标目录已经打开着，粘贴后又新开一个窗口"。
#   那种情况下窗口总数前后一样（旧窗口还在，新的多一个），数个数会得出"正常"的假结论。
#
# 只读且自我收尾：只开自己的测试文件夹，只关自己开的窗口，不碰用户原有窗口。

param(
    [string]$FolderA = 'D:\Coding\MyRobocLab\t\parent',
    [string]$FolderB = 'D:\Coding\MyRobocLab\t\parent\child',
    [int]$DelayMs    = 0,
    [switch]$NoCall,
    [switch]$FocusTarget
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class W
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
}
'@

$Shell = New-Object -ComObject Shell.Application

function Get-Windows {
    $out = @()
    $ws = $Shell.Windows()

    for ($i = 0; $i -lt $ws.Count; $i++) {
        $w = $null
        try { $w = $ws.Item($i) } catch { continue }
        if ($null -eq $w) { continue }

        try { $hwnd = [int64]$w.HWND } catch { continue }

        $loc = ''
        try { $loc = [string]$w.LocationURL } catch { }

        $path = Normalize-Path $loc

        $out += [pscustomobject]@{ HWND = $hwnd; Path = $path }
    }
    return $out
}

function Show-Windows {
    param([string]$Label, $Windows)
    Write-Host ("  {0,-14} {1} 个窗口" -f $Label, $Windows.Count)
    foreach ($w in $Windows) {
        Write-Host ("      HWND={0,-12} {1}" -f $w.HWND, $w.Path)
    }
}

function Normalize-Path {
    <#
      统一成"反斜杠、无尾斜杠、已反转义"的形式。
      不能用行内表达式比较：`$_.Path -eq $FolderB -replace '/', '\'`
      实际被解析成 `($_.Path -eq $FolderB) -replace ...`，比较先执行，于是恒为假
      —— 上一版因此把列表里第一个窗口当成了目标窗口（HWND 报成 723390）。
    #>
    param([string]$P)

    if ([string]::IsNullOrWhiteSpace($P)) { return '' }

    $t = [System.Uri]::UnescapeDataString($P)
    $t = $t -replace '^file:///', ''
    $t = $t -replace '/', '\'

    return $t.TrimEnd('\')
}

function Get-FileUrl { param([string]$P) return ([System.Uri]$P).AbsoluteUri }

function Find-RootedWindow {
    <#
      按"显示哪个文件夹"找窗口。用 COM 对象身份去重：Shell.Application.Windows()
      在 Windows 11 上会把同一个窗口按标签页重复列出，所以一台窗口可能出现多次。
    #>
    param([string]$Url)

    $ws = $Shell.Windows()
    $seen = @{}
    $found = @()

    for ($i = 0; $i -lt $ws.Count; $i++) {
        $w = $null
        try { $w = $ws.Item($i) } catch { continue }
        if ($null -eq $w) { continue }

        $loc = ''
        try { $loc = [string]$w.LocationURL } catch { }

        [Console]::Error.WriteLine("    [debug] i=$i loc=[$loc] url=[$Url] eq=$($loc -eq $Url)")

        if ($loc -ne $Url) { continue }

        try { $hwnd = [int64]$w.HWND } catch { continue }

        $key = "$hwnd|$loc"
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true

        $found += [pscustomobject]@{ HWND = $hwnd; Url = $loc }
    }

    return $found
}

function Select-InFolder {
    param([string]$Folder, [string[]]$Items)

    $folderPidl = [IntPtr]::Zero
    $sfgao = 0

    if ([W]::SHParseDisplayName($Folder, [IntPtr]::Zero, [ref]$folderPidl, 0, [ref]$sfgao) -ne 0) {
        throw "SHParseDisplayName 解析文件夹失败：$Folder"
    }

    $childPidl = New-Object IntPtr[] $Items.Count
    $n = 0

    try {
        foreach ($item in $Items) {
            $child = [IntPtr]::Zero
            if ([W]::SHParseDisplayName($item, [IntPtr]::Zero, [ref]$child, 0, [ref]$sfgao) -eq 0) {
                $childPidl[$n] = $child
                $n++
            }
        }

        if ($n -eq 0) { throw '一个子项 PIDL 都没解析出来' }

        $hr = [W]::SHOpenFolderAndSelectItems($folderPidl, [uint32]$n, $childPidl, 0)
        return $hr
    }
    finally {
        for ($k = 0; $k -lt $n; $k++) { [W]::CoTaskMemFree($childPidl[$k]) }
        [W]::CoTaskMemFree($folderPidl)
    }
}

# ---- 前置检查 -------------------------------------------------------------

foreach ($p in @($FolderA, $FolderB)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "缺少目录：$p" }
}

$urlB = Get-FileUrl $FolderB

$items = @(Get-ChildItem -LiteralPath $FolderB -Force | Select-Object -First 3 | ForEach-Object { $_.FullName })

if ($items.Count -eq 0) { throw "$FolderB 是空的，先放几个文件进去" }

Write-Host "文件夹 A（源）：$FolderA"
Write-Host "文件夹 B（目标，已存在的窗口）：$FolderB"
Write-Host "要选中的项：$($items.Count) 个  ->  $(($items | ForEach-Object { Split-Path $_ -Leaf }) -join ', ')"
Write-Host "延迟：$DelayMs 毫秒    调用 API：$(-not $NoCall)    抢占前台：$([bool]$FocusTarget)"
Write-Host ''

# ---- 基线 -----------------------------------------------------------------

$baseline = Get-Windows
Show-Windows -Label '基线' -Windows $baseline
$baselineIds = @($baseline | ForEach-Object { $_.HWND })

# ---- 打开目标文件夹窗口（模拟"用户已经打开了目标目录"）--------------------

Start-Process -FilePath 'explorer.exe' -ArgumentList $FolderB | Out-Null
Start-Sleep -Milliseconds 2500

$opened = Get-Windows
Show-Windows -Label '打开目标后' -Windows $opened

$targetWindow = Find-RootedWindow -Url $urlB

if ($targetWindow.Count -eq 0) {
    Write-Host '  ⚠ 没找到显示目标文件夹的窗口，实验无效'
    exit 3
}

$targetHwnd = [int64]$targetWindow[0].HWND
$mine = @($opened | Where-Object { $baselineIds -notcontains $_.HWND } | ForEach-Object { $_.HWND })

Write-Host ''
Write-Host "  目标窗口 HWND = $targetHwnd"

if ($FocusTarget) {
    [W]::SetForegroundWindow([IntPtr]$targetHwnd) | Out-Null
    Start-Sleep -Milliseconds 400
    Write-Host '  已把目标窗口抢到前台'
}

# ---- 延迟（这是要验证的变量：HDD 上粘贴要 146 秒）------------------------

if ($DelayMs -gt 0) {
    Write-Host "  等待 $DelayMs 毫秒（模拟 HDD 上复制耗时）…"
    Start-Sleep -Milliseconds $DelayMs
}

$beforeCall = Get-Windows
Show-Windows -Label '调用前' -Windows $beforeCall

# ---- 调用 -----------------------------------------------------------------

$hr = $null

if (-not $NoCall) {
    $fgBefore = [W]::GetForegroundWindow()
    Write-Host ("  调用 SHOpenFolderAndSelectItems… 调用前前台 HWND = {0}" -f [int64]$fgBefore)
    $hr = Select-InFolder -Folder $FolderB -Items $items
    Write-Host ("  返回 HRESULT = 0x{0:X8}" -f $hr)
} else {
    Write-Host '  （NoCall：不调用 API，只观察窗口自身是否变化）'
}

Start-Sleep -Milliseconds 2500

# ---- 结果 -----------------------------------------------------------------

$after = Get-Windows
Show-Windows -Label '调用后' -Windows $after

$afterIds = @($after | ForEach-Object { $_.HWND })
$newWindows = @($after | Where-Object { $beforeCall.HWND -notcontains $_.HWND })

# 目标窗口是否还在：既看调用前那份列表，也看调用后。
# 上面显示"目标窗口还在吗"用的就是这里的结果，所以它必须真的测的是目标窗口。
$targetStillThere = $afterIds -contains $targetHwnd

# 复用失败的可观测形态：出现了一个新的、显示同一文件夹的窗口。
# 用 COM 对象身份去重，避免同一窗口因标签页被重复列出而误报。
$targetNow = Find-RootedWindow -Url $urlB
$targetDuplicates = @($targetNow | Where-Object { $_.HWND -ne $targetHwnd })

Write-Host ''
Write-Host '  ── 判据 ──'
Write-Host ("  目标窗口 HWND           : {0}" -f $targetHwnd)

if ($targetStillThere) {
    Write-Host '  目标窗口是否还在        : 是'
} else {
    $targetStillThere = $baselineIds -contains $targetHwnd
    Write-Host ("  目标窗口是否还在        : 否（它进了基线列表吗：{0}）" -f $targetStillThere)
}

Write-Host ("  显示目标文件夹的窗口数  : {0}  (去重后)" -f $targetNow.Count)
Write-Host ("  除目标窗口外的同目录窗口: {0}" -f $targetDuplicates.Count)
Write-Host ("  调用后新增的窗口        : {0}" -f $newWindows.Count)

foreach ($w in $newWindows) {
    Write-Host ("      + HWND={0,-12} {1}" -f $w.HWND, $w.Path)
}

if ($newWindows.Count -gt 0) {
    Write-Host ''
    Write-Host '  ★ 结论：新开了窗口（这就是要复现的症状）'
} else {
    Write-Host ''
    Write-Host '  ✓ 结论：没有新开窗口（本次复用正常）'
}

# ---- 收尾：只关我自己开的窗口 ---------------------------------------------

Write-Host ''
Write-Host '  收尾：关闭本次实验打开的窗口…'

foreach ($id in $mine) {
    [W]::PostMessage([IntPtr]$id, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}

Start-Sleep -Milliseconds 900

$final = Get-Windows
Show-Windows -Label '收尾后' -Windows $final

$leftover = @($final | Where-Object { $mine -contains $_.HWND })

if ($leftover.Count -gt 0) {
    Write-Host ("  ⚠ 还有 {0} 个窗口没关掉：{1}" -f $leftover.Count, (($leftover | ForEach-Object { $_.HWND }) -join ', '))
    foreach ($id in $leftover.HWND) {
        [W]::PostMessage([IntPtr]$id, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    }
} else {
    Write-Host '  ✓ 已全部关闭'
}
