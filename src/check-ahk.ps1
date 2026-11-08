# ============================================================
# AHK v2 语法/加载检查器
# ============================================================
# 用途：对 .ahk 文件做「语法 + 加载期」检查，不执行脚本主体。
#   ★ 核心手段：AutoHotkey64.exe /ErrorStdOut /validate
#     - /validate  只做语法与加载检查，不进主流程
#     - /ErrorStdOut 把错误写到 stderr（★ 关键：否则弹模态对话框卡死无人值守流程）
#   ★ 必须用 cmd 重定向捕获 stderr：PowerShell 管道拿不到 AHK 的 stderr
#
# 检查什么（这些都是 AHK 加载期就会炸、且 try/catch 抓不住的）：
#   1. 语法错误
#   2. 标识符与内置函数/类撞名（例：Format := 1 / Menu := 1）
#   3. ★ global 作用域下的撞名（比局部更严格，见 README 说明）
#   4. #Include 目标缺失、类/函数重复定义
#
# 用法：
#   .\check-ahk.ps1 <文件或目录> [更多文件...]
#   .\check-ahk.ps1 -Dir <目录> -Recurse
# 退出码：0 = 全部通过；1 = 有文件不通过
# ============================================================
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Paths,
    [string]$Dir,
    [switch]$Recurse,
    [switch]$Quiet
)

$AHK = "C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe"
if (-not (Test-Path $AHK)) { Write-Error "AHK 未找到: $AHK"; exit 1 }

# ---- 收集目标文件 ----
$files = @()
if ($Dir) {
    $files += Get-ChildItem -LiteralPath $Dir -Filter *.ahk -File -Recurse:$Recurse
}
foreach ($p in $Paths) {
    if (Test-Path -LiteralPath $p -PathType Container) {
        $files += Get-ChildItem -LiteralPath $p -Filter *.ahk -File -Recurse:$Recurse
    } elseif (Test-Path -LiteralPath $p) {
        $files += Get-Item -LiteralPath $p
    } else {
        Write-Warning "路径不存在: $p"
    }
}
$files = $files | Sort-Object FullName -Unique

if ($files.Count -eq 0) { Write-Warning "没有找到 .ahk 文件"; exit 0 }

$tmp = Join-Path $env:TEMP "ahkcheck_$PID"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$errFile = Join-Path $tmp "err.txt"

$fail = 0
$pass = 0
$results = @()

foreach ($f in $files) {
    if (Test-Path $errFile) { Remove-Item $errFile -Force }
    # ★ cmd 重定向：把 AHK stderr 落到文件；/ErrorStdOut 阻止弹窗
    cmd /c "`"$AHK`" /ErrorStdOut /validate `"$($f.FullName)`" > nul 2> `"$errFile`""
    $code = $LASTEXITCODE

    $msg = ""
    if ($code -ne 0 -and (Test-Path $errFile)) {
        $msg = (Get-Content $errFile -Encoding UTF8 -Raw)
        if ($msg) { $msg = $msg.Trim() }
    }

    if ($code -eq 0) {
        $pass++
        if (-not $Quiet) { Write-Host ("  OK    " + $f.Name) -ForegroundColor Green }
    } else {
        $fail++
        Write-Host ("  FAIL  " + $f.Name + "   (exit=$code)") -ForegroundColor Red
        if ($msg) {
            foreach ($ln in ($msg -split "`r?`n")) {
                if ($ln.Trim() -ne "") { Write-Host ("        " + $ln.Trim()) -ForegroundColor Yellow }
            }
        }
        $results += [PSCustomObject]@{ File = $f.FullName; Exit = $code; Message = $msg }
    }
}

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("检查完成：通过 $pass / 失败 $fail / 共 $($files.Count)") -ForegroundColor Cyan

if ($fail -gt 0) {
    Write-Host ""
    Write-Host "失败详情：" -ForegroundColor Red
    $results | Format-List
    exit 1
}
exit 0
