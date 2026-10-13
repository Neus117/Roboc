<#
    接线检查 —— 找出"声明了却无人读取"的配置项与参数。

    为什么需要它
      本项目已经出现过六次同一类缺陷：一个选项、参数或开关声明了某种行为，
      代码却从不实现它。它们全都编译通过、测试通过、运行时也不报错：

        CopyAlternateDataStreams = true   声明复制数据流，无人读取
        VerifyChecksum                    声明校验内容，无人读取
        --verify                          接受参数，什么都不做
        UseUnbufferedIo = true            声明无缓冲 I/O，无人读取
        --no-flush                        管不到检查点 flush
        --checkpoint-interval             未注册进 TakesValue

      每一次都是靠人眼审查发现的，而审查之所以能看到，是因为正好有人在找。
      这个脚本把"正好有人"换成"每次都查"。

    判定方式
      属性若只出现在声明行与赋值处，从未以成员访问（.名称）的形式出现，
      就是无人读取。成员访问是读取的唯一形态，赋值是写入。

    用法
      powershell -ExecutionPolicy Bypass -File scripts\check-wiring.ps1 -Root <仓库根>

    退出码
      0 = 全部接线正常
      1 = 发现未接线的项
#>

param([Parameter(Mandatory = $true)][string]$Root)

$ErrorActionPreference = 'Stop'

$srcDir = Join-Path $Root 'src'
if (-not (Test-Path $srcDir)) { Write-Host "找不到 $srcDir"; exit 2 }

$files = Get-ChildItem $srcDir -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' }

# 全文索引，避免每个属性都重读一遍磁盘
$corpus = @{}
foreach ($f in $files) { $corpus[$f.FullName] = [System.IO.File]::ReadAllText($f.FullName) }

$problems = New-Object System.Collections.Generic.List[string]
$checked = 0

# ---------------------------------------------------------------- 配置属性

Write-Host "== 配置属性（*Options.cs 上的公开属性）=="
Write-Host ""

# Everything below reads the corpus directly rather than through a helper.
#
# A helper taking the name and looking the corpus up in script scope returned zero for
# every property -- including ones known to be read -- so all twenty-six came back as
# unwired. A checker that reports everything is indistinguishable from one that reports
# nothing, and it would have been believed.

$corpusList = @($corpus.Values)
$optionsFiles = @($files | Where-Object { $_.Name -like '*Options.cs' })

foreach ($optFile in $optionsFiles) {
    $text = $corpus[$optFile.FullName]

    # public <type> <Name> { get; init; }   -- also matches the form with a default
    foreach ($prop in [regex]::Matches($text, 'public\s+[\w<>\[\]?]+\s+(\w+)\s*\{\s*get;')) {
        $propName = $prop.Groups[1].Value
        $checked++

        # A member access is a read; a declaration or an assignment is not.
        $pattern = '\.' + [regex]::Escape($propName) + '\b'
        $reads = 0

        foreach ($body in $corpusList) {
            $reads += ([regex]::Matches($body, $pattern)).Count
        }

        if ($reads -eq 0) {
            $problems.Add("$($optFile.Name): 属性 '$propName' 从未被读取")
            Write-Host "  [未接线] $propName" -ForegroundColor Red
            Write-Host "           $($optFile.Name) —— 声明了行为，代码里没有任何地方读它"
        }
    }
}

Write-Host "  已检查 $checked 个属性"
Write-Host ""

# ---------------------------------------------------------------- 命令行参数

Write-Host "== 命令行参数（args.Flag / args.Option / args.IntOption）=="
Write-Host ""

# 每个参数应有的两件事：被读取，且（若带值）注册进 TakesValue
$declared = @{}
foreach ($entry in $corpus.GetEnumerator()) {
    foreach ($a in [regex]::Matches($entry.Value, 'args\.(Flag|Option|IntOption|DoubleOption)\("(--[\w-]+)"')) {
        $argKind = $a.Groups[1].Value
        $argFlag = $a.Groups[2].Value
        if (-not $declared.ContainsKey($argFlag)) {
            $declared[$argFlag] = @{ Kind = $argKind; File = [System.IO.Path]::GetFileName($entry.Key) }
        }
    }
}

$argsFile = $files | Where-Object { $_.Name -eq 'Args.cs' } | Select-Object -First 1
$takesValue = ''
if ($argsFile) { $takesValue = $corpus[$argsFile.FullName] }

$unregistered = 0
foreach ($flag in ($declared.Keys | Sort-Object)) {
    $info = $declared[$flag]

    # 带值的参数必须出现在 TakesValue 里，否则它的值会被当成位置参数
    if ($info.Kind -ne 'Flag') {
        if ($takesValue -notmatch [regex]::Escape($flag)) {
            $problems.Add("$flag 未注册进 Args.TakesValue（它的值会被当成路径）")
            Write-Host "  [未注册] $flag" -ForegroundColor Red
            Write-Host "           在 $($info.File) 里读取，但 Args.cs 的 TakesValue 没有它"
            Write-Host "           后果：'roboc: source not found: 32' —— 错误信息不会提到这个参数"
            $unregistered++
        }
    }
}

Write-Host "  已检查 $($declared.Count) 个参数" + $(if ($unregistered -gt 0) { "，其中 $unregistered 个未注册" } else { "，全部已注册" })
Write-Host ""

# ---------------------------------------------------------------- 结论

if ($problems.Count -eq 0) {
    Write-Host "接线检查通过：没有声明了却无人读取的项。" -ForegroundColor Green
    exit 0
}

Write-Host "==================================================" -ForegroundColor Yellow
Write-Host "发现 $($problems.Count) 处接线问题：" -ForegroundColor Yellow
Write-Host ""
foreach ($p in $problems) { Write-Host "  · $p" -ForegroundColor Yellow }
Write-Host ""
Write-Host "每一项都意味着代码承诺了一件它没有做的事。" -ForegroundColor Yellow
Write-Host "要么实现它，要么删除它 —— 但不要留着。" -ForegroundColor Yellow
exit 1
