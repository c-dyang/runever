# RunEver selftest batch runner（AC-B8：全量回归 —— 套数以本文件 Add-Case 清单为准，不写死数字）
# Usage:  pwsh -File run-selftests.ps1 -OutDir v8b
#         pwsh -File run-selftests.ps1 -OutDir v8b -Exe "cs\bin\Release\RunEverCs.exe"
#
# ★ 2026-10-04（v8b）修正两处（原脚本已不可用于 AC-B8）：
#   ① **补齐全部套**：缺 capture / webdav / 总自检；
#   ② **取退出码改 `Start-Process -Wait -PassThru`**：把取码方式显式化，不依赖宿主对子进程的等待行为。
#      （更正 2026-10-04：曾据"`& $exe` 对 WinExe 不等待 ⇒ 读到陈旧退出码"改写 —— 该因果已被实测**证伪**：
#        pwsh 7.6.6 与 Windows PowerShell 5.1 用 `&` 调用均取到真实退出码。此前自检结果未被污染。）
#   （该脚本是测试工具，不在冻结清单内。）
param(
    [string]$OutDir = "v8b",
    [string]$Exe = ""
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if ([string]::IsNullOrEmpty($Exe)) { $Exe = Join-Path $root "dist\RunEverCs.exe" }
if (-not (Test-Path $Exe)) { throw "exe not found: $Exe" }
if ($OutDir -eq "baseline") { throw "禁止写入 state\baseline（只读基线，AC-B8 比对源）" }
$out = Join-Path $root ("state\" + $OutDir)
New-Item -ItemType Directory -Force -Path $out | Out-Null
$conf = Join-Path $root "config\menus.conf"

$cases = New-Object System.Collections.ArrayList
function Add-Case($n, $a) { [void]$cases.Add(@{ n = $n; a = $a }) }

# --- 全量套（官方口径，delivery-v8-seg1.md:119）---
Add-Case "bootstrap"  @("--selftest-bootstrap", $root)
Add-Case "menu"       @("--selftest-menu", $conf)      # 收 conf，不收 root
Add-Case "writer"     @("--selftest-writer", $conf)    # 收 conf，不收 root
Add-Case "template"   @("--selftest-template", $root)
Add-Case "pluginedit" @("--selftest-pluginedit", $root)
Add-Case "pathcache"  @("--selftest-pathcache", $root)
Add-Case "automatch"  @("--selftest-automatch", $root)
Add-Case "selfcheck"  @("--selftest-selfcheck", $root)
Add-Case "hide"       @("--selftest-hide", $root)
Add-Case "matcher"    @("--selftest-matcher", $root)
Add-Case "resolve"    @("--selftest-resolve", $root)
Add-Case "exec"       @("--selftest-exec", $root)
Add-Case "capture"    @("--selftest-capture", $root)   # 两参单 out 文件（root + out）
Add-Case "webdav"     @("--selftest-webdav", $root)
Add-Case "confwrite"  @("--selftest-confwrite", $root) # ★ 段1.6 新增：B1 写盘留痕 / B2 开关即时生效
Add-Case "hotreload"  @("--selftest-hotreload", $root) # ★ 段1.7 新增：配置级热加载（menus.conf 换代 / auto.conf 全键 / I-5 开销）
Add-Case "folderdlg"  @("--selftest-folderdlg", $root)  # ★ v9 新增：系统默认文件夹选择窗口（IFileOpenDialog）互操作
Add-Case "menueditor" @("--selftest-menueditor", $root) # ★ v9 新增：树形条目编辑（结构操作 + 不变式 + 保存往返；沙箱副本）
Add-Case "arch"       @("--selftest-arch", $root)      # ★ 拆-0 新增：分层引用方向（白名单 = 已知违规，按文件+类型）
Add-Case "iconurl"    @("--selftest-iconurl", $root)   # ★ U-C 新增：图标源支持网址（favicon 缓存 + 协议白名单 + 不阻塞）
Add-Case "selftest"   @("--selftest", $root)           # 总自检：报告固定落 state\selftest-cs.txt

$hash = (Get-FileHash $Exe -Algorithm SHA256).Hash
$lines = New-Object System.Collections.ArrayList
[void]$lines.Add("exe          = " + $Exe)
[void]$lines.Add("exe SHA256   = " + $hash)
[void]$lines.Add("out dir      = " + $out)
[void]$lines.Add("取码方式     = Start-Process -Wait -PassThru（显式取码，不依赖宿主等待行为）")
[void]$lines.Add("")

foreach ($c in $cases) {
    $f = Join-Path $out ("selftest-" + $c.n + ".txt")
    $argv = @($c.a) + $f
    $p = Start-Process -FilePath $Exe -ArgumentList $argv -Wait -PassThru
    $code = $p.ExitCode
    # 总自检忽略额外参数，报告固定写 state\selftest-cs.txt ⇒ 拷到本目录统一归档
    if ($c.n -eq "selftest") {
        $src = Join-Path $root "state\selftest-cs.txt"
        if (Test-Path $src) { Copy-Item $src $f -Force }
    }
    $tail = ""
    if (Test-Path $f) {
        $txt = Get-Content -Raw -Path $f -ErrorAction SilentlyContinue
        if ($txt) {
            $nonEmpty = ($txt -split "`r?`n") | Where-Object { $_.Trim() -ne "" }
            if ($nonEmpty.Count -gt 0) { $tail = $nonEmpty[$nonEmpty.Count - 1].Trim() }
        }
    }
    if ($tail.Length -gt 110) { $tail = $tail.Substring(0, 110) + "..." }
    $line = "{0,-11} exit={1}  tail={2}" -f $c.n, $code, $tail
    [void]$lines.Add($line)
    Write-Output $line
}
$lines | Set-Content -Path (Join-Path $out "summary.txt") -Encoding UTF8
Write-Output ("--- summary -> " + (Join-Path $out "summary.txt"))
