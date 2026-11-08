#Requires AutoHotkey v2.0
#SingleInstance Off
#Warn VarUnset, Off
; ★ 常驻模式下不得在托盘留图标：AHK 默认会给脚本一个托盘图标，
;   本项目是主程序的子进程配件，不该在通知区域占一格（也不该让人误点）。
#NoTrayIcon
; ============================================================
; RunEver 采集器（独立进程，供 C# 调用）
;
; 职责：采集三件事 —— 选中文本 / 选中文件 / 前台窗口信息
;
; ★ 输出方式（两种，按调用形态选）：
;   ① 一次性：写 JSON 到文件（由调用方传路径进来）。
;      实测教训：AHK **无终端且 stdout 未重定向**时 stdout 不可用
;      （exit=0 但输出为空）⇒ 一次性模式固定用「输出文件」通信。
;   ② 常驻（2026-11-07）：stdin/stdout 行协议，父进程**显式重定向**了管道，
;      此时 FileOpen("*") 正常可用（已实测：单次往返 ~3ms）。
;
; 调用形态：
;   AutoHotkey64.exe CaptureRunner.ahk <out.json>          一次性（写文件）
;   编译版：        CaptureRunner.exe <out.json>
;   常驻：          CaptureRunner.exe --serve               读 stdin 一行 → 写 stdout 一行
;
; 设计约束（沿用目标卡）：
;   · 采集必须「有界」：任一项失败/超时都不影响其他项
;   · 全部失败也返回空上下文（菜单仍可显示 always/fallback）
;
; ★ 编码口径（契约 §1-D12）：取文件列表经 PowerShell 中转时，**PS 自己以
;   UTF-8 无 BOM 写文件**，绝不用 `cmd >` 重定向（无控制台时重定向按 OEM
;   代码页 936 落盘，AHK 却按 UTF-8 读 ⇒ 非 ASCII 路径必乱码）。
;   输出路径以**参数**传给 PS，不嵌进脚本文本（避免二次编码）。
; ============================================================

if (A_Args.Length < 1) {
    ; 无参数 = 被误调用，直接退出（不弹窗，避免卡住调用方）
    ExitApp(2)
}

; ★ 常驻模式（2026-11-07）：--serve
;   主程序起一次子进程，之后长期按「一行请求 → 一行 JSON」往返。
;   实测：单次往返 ~3ms（一次性模式 ~396ms，开销几乎全是解释器启动 + 进程创建）。
;   退出条件：stdin 读到 EOF ⇒ ExitApp(0)。
;   主程序正常退出或崩溃时，OS 都会关闭管道 ⇒ 本进程随之退出（"死了一起死"）。
global g_serve := (A_Args[1] = "--serve")

; 常驻模式的 g_outFile 只是一个占位（WarnReplacement 要拿它反推日志目录），
; 不会真的写这个文件。
global g_outFile := g_serve ? (A_ScriptDir "\state\capture-serve.json") : A_Args[1]

; ★ 测试缝（仅为自检存在，见契约 §1-D12 的自动化验证要求）：
;   `CaptureRunner.exe <out.json> [--clipfiles-from <listfile>]`
;   给定时跳过去剪贴板动作，直接把该 UTF-8 文件当文件列表读入。
;   ★ 生产调用点（CaptureClient.cs:101/106）恒**只传 1 个参数** ⇒ 本分支
;     永不触发，默认流程逐行为不变（零行为变化，见交付说明）。
global g_clipFilesFrom := ""
Loop A_Args.Length {
    if (A_Args[A_Index] = "--clipfiles-from" && A_Index < A_Args.Length)
        g_clipFilesFrom := A_Args[A_Index + 1]
}

if (g_serve) {
    ServeMain()
    ExitApp(0)
}

CaptureMain()

CaptureMain() {
    global
    vJson := Capture()
    try {
        ; ★ 原子写：先写 .tmp 再改名，避免调用方读到半截
        vTmp := g_outFile ".tmp"
        try FileDelete(vTmp)
        FileAppend(vJson, vTmp, "UTF-8")
        try FileDelete(g_outFile)
        FileMove(vTmp, g_outFile)
    } catch as e {
        ExitApp(3)
    }
    ExitApp(0)
}

; ---- 常驻模式主循环：stdin 一行请求 → stdout 一行 JSON ----
;   · 只认 "cap"（采集一次）和 "quit"（正常退出）；空行忽略
;   · stdin EOF ⇒ 返回，由调用方 ExitApp(0)
;   · ★ 任何一次采集失败都**不能**让循环退出：返回空上下文，菜单仍可显示
ServeMain() {
    global g_outFile
    fin := "", fout := ""
    try {
        fin := FileOpen("*", "r", "UTF-8")
        fout := FileOpen("*", "w", "UTF-8")
    }
    if (!IsObject(fin) || !IsObject(fout))
        ExitApp(4)

    loop {
        vReq := ""
        try vReq := fin.ReadLine()
        catch
            break
        if (vReq = "") {
            if (fin.AtEOF)
                break
            continue
        }
        if (vReq = "quit")
            break

        vJson := ""
        try vJson := Capture()
        catch
            vJson := ""
        if (vJson = "")
            vJson := BuildJson("", [], "", "", "", "")

        ; 压成单行（行协议：一条记录一行；Js() 已把真实换行转义成 \r \n）
        vOne := StrReplace(StrReplace(vJson, "`r", ""), "`n", "")
        try fout.WriteLine(vOne)
        catch
            break
        ; ★ 强制把写缓冲推出去，否则父进程会一直阻塞在读那一行上
        try fout.Read(0)
    }

    try fin.Close()
    try fout.Close()
}

; ---- 采集全部 ----
Capture() {
    vText := ""
    vFiles := []
    vTitle := "", vClass := "", vProc := ""

    ; ① 前台窗口（最稳，先取）
    try {
        hwnd := WinExist("A")
        if (hwnd) {
            vTitle := WinGetTitle("ahk_id " hwnd)
            vClass := WinGetClass("ahk_id " hwnd)
            vProc  := WinGetProcessName("ahk_id " hwnd)
        }
    } catch {
    }

    ; ② 剪贴板原有内容（供 %clip%）
    vClip := ""
    try {
        if (ClipWait(0.05, 1))
            vClip := A_Clipboard
    } catch {
    }

    ; ③ 选中文本 / 文件
    if (g_clipFilesFrom != "") {
        ; ★ 测试缝：直接读列表文件（生产路径不触发，见文件头说明）
        vFiles := ReadListFile(g_clipFilesFrom)
        return BuildJson(vText, vFiles, vTitle, vClass, vProc, vClip)
    }

    vSavedAll := ""
    try vSavedAll := ClipboardAll()

    try {
        A_Clipboard := ""
        SendInput("^c")

        vClipped := false
        try {
            vClipped := ClipWait(0.15)
        } catch {
            vClipped := false
        }

        if (vClipped) {
            ; CF_HDROP = 15 → 说明复制的是文件
            vIsFile := DllCall("IsClipboardFormatAvailable", "UInt", 15)
            if (vIsFile) {
                vFiles := GetClipboardFiles()
            } else {
                try vText := A_Clipboard
            }
        }
    } catch {
    }

    ; 还原剪贴板
    if (vSavedAll != "")
        try A_Clipboard := vSavedAll

    return BuildJson(vText, vFiles, vTitle, vClass, vProc, vClip)
}

; ---- 取剪贴板中的文件列表 ----
;   ① 快路径：DllCall 直接解 CF_HDROP（~1ms）
;   ② 兜底：起 PowerShell 读（~300ms，历史实现，保留以防 DllCall 取不到）
;
;   为什么加①：常驻模式下实测单次采集 311~360ms，几乎**全部**耗在
;   RunWait 起 PowerShell 那一下（进程创建 ~300ms），与采集逻辑无关。
;   常驻本身已把进程启动降到 18ms，剩下的大头就是这里。
GetClipboardFiles() {
    out := GetClipboardFilesFast()
    if (out.Length > 0)
        return out
    return GetClipboardFilesPs()
}

; ---- 快路径：直接解 CF_HDROP（不做代码页转换，路径原样 UTF-16）----
GetClipboardFilesFast() {
    out := []
    if (!DllCall("IsClipboardFormatAvailable", "UInt", 15))
        return out
    if (!DllCall("OpenClipboard", "Ptr", 0))
        return out
    try {
        hDrop := DllCall("GetClipboardData", "UInt", 15, "Ptr")
        if (!hDrop)
            return out
        n := DllCall("shell32\DragQueryFileW", "Ptr", hDrop, "UInt", 0xFFFFFFFF, "Ptr", 0, "UInt", 0)
        if (n < 1)
            return out
        loop n {
            idx := A_Index - 1
            len := DllCall("shell32\DragQueryFileW", "Ptr", hDrop, "UInt", idx, "Ptr", 0, "UInt", 0)
            if (len < 1)
                continue
            cap := len + 1
            buf := Buffer(cap * 2, 0)
            DllCall("shell32\DragQueryFileW", "Ptr", hDrop, "UInt", idx, "Ptr", buf.Ptr, "UInt", cap)
            v := StrGet(buf, cap, "UTF-16")
            v := Trim(v, " `t`r`n")
            if (v != "")
                out.Push(v)
        }
    } finally {
        DllCall("CloseClipboard")
    }
    return out
}

; ---- 兜底路径：PowerShell 取（历史实现）
;   用 PowerShell 取（稳妥，避免在这里解析 CF_HDROP 原始结构）
;   ★ 编码口径（契约 §1-D12）：
;     ① 生成的 .ps1 用 `param([string]$out)` 收输出路径 —— 路径不进脚本正文；
;     ② 脚本体用 [IO.File]::WriteAllLines + UTF8Encoding($false) 直写 UTF-8 无 BOM；
;     ③ **删除 `cmd >` 重定向**（重定向字符集由控制台决定，无控制台时 = GBK）；
;     ④ 脚本以 "UTF-8-RAW" 落盘（内容纯 ASCII ⇒ 逐字节 ≤0x7F，无 BOM 歧义）。
GetClipboardFilesPs() {
    out := []
    psFile := A_Temp "\rve_cap_" A_TickCount ".ps1"
    outFile := A_Temp "\rve_capout_" A_TickCount ".txt"
    try FileDelete(psFile)
    try FileDelete(outFile)

    ps := "param([string]$out)`r`n"
        . "Add-Type -AssemblyName System.Windows.Forms`r`n"
        . "$a = @()`r`n"
        . "foreach ($f in [System.Windows.Forms.Clipboard]::GetFileDropList()) { $a += [string]$f }`r`n"
        . "[IO.File]::WriteAllLines($out, [string[]]$a, (New-Object System.Text.UTF8Encoding($false)))`r`n"

    try {
        FileAppend(ps, psFile, "UTF-8-RAW")
        RunWait(A_ComSpec " /c powershell -NoProfile -ExecutionPolicy Bypass -File "
            . Q(psFile) " " . Q(outFile), , "Hide")
        if FileExist(outFile) {
            vTxt := ""
            try vTxt := FileRead(outFile, "UTF-8")
            out := FinishFileList(vTxt, "clipboard")
        }
    } catch {
    }
    try FileDelete(psFile)
    try FileDelete(outFile)
    return out
}

; ---- 测试缝：直接读列表文件（仅 --clipfiles-from 时调用）----
ReadListFile(path) {
    vTxt := ""
    try vTxt := FileRead(path, "UTF-8")
    catch {
        return []
    }
    return FinishFileList(vTxt, "clipfiles-from")
}

; ---- 列表文本 → 数组（PS 输出与列表文件共用同一段解析）----
FinishFileList(vTxt, vTag) {
    WarnReplacement(vTxt, vTag)
    out := []
    for vLine in StrSplit(vTxt, "`n") {
        vLine := Trim(vLine, " `t`r")
        if (vLine != "")
            out.Push(vLine)
    }
    return out
}

; ---- U+FFFD 告警（契约 §1-D12：读到替换字符 ⇒ 落日志，不静默、不抛异常）----
;   日志路径由输出文件反推：g_outFile 恒为 <root>\state\cap_*.json ⇒ 同级 capture.log
WarnReplacement(vTxt, vTag) {
    global g_outFile
    if (!InStr(vTxt, Chr(0xFFFD)))
        return
    try {
        ; ★ SplitPath 第 2 参是「文件名」，目录在第 3 参 ⇒ 必须 `SplitPath(p, , &dir)`
        SplitPath(g_outFile, , &vDir)
        vSample := StrReplace(StrReplace(SubStr(vTxt, 1, 80), "`r", " "), "`n", " ")
        FileAppend(FormatTime(A_Now, "yyyy-MM-dd HH:mm:ss")
            . " [WARN] U+FFFD 来源=" vTag " 样本=[" vSample "]`r`n",
            vDir "\capture.log", "UTF-8")
    } catch {
    }
}

; ---- 组装 JSON（手拼，不引外部库）----
BuildJson(text, files, title, cls, proc, clip) {
    s := "{`n"
    s .= "  " Q("text") ": " Js(text) ",`n"
    s .= "  " Q("files") ": " Ja(files) ",`n"
    s .= "  " Q("winTitle") ": " Js(title) ",`n"
    s .= "  " Q("winClass") ": " Js(cls) ",`n"
    s .= "  " Q("proc") ": " Js(proc) ",`n"
    s .= "  " Q("clipboard") ": " Js(clip) ",`n"
    s .= "  " Q("isFileCtx") ": " (files.Length > 0 ? "true" : "false") ",`n"
    s .= "  " Q("hasFolder") ": " (HasFolder(files) ? "true" : "false") ",`n"
    s .= "  " Q("ts") ": " Js(FormatTime(A_Now, "yyyy-MM-dd HH:mm:ss")) "`n"
    s .= "}`n"
    return s
}

HasFolder(files) {
    for f in files {
        try {
            if InStr(FileExist(f), "D")
                return true
        }
    }
    return false
}

Q(s) => Chr(34) s Chr(34)

Js(v) {
    if (v = "")
        return Chr(34) Chr(34)
    v := StrReplace(v, "\", "\\")
    v := StrReplace(v, Chr(34), "\" Chr(34))
    v := StrReplace(v, "`r", "\r")
    v := StrReplace(v, "`n", "\n")
    v := StrReplace(v, "`t", "\t")
    return Chr(34) v Chr(34)
}

Ja(a) {
    s := "["
    for i, v in a {
        if (i > 1)
            s .= ", "
        s .= Js(v)
    }
    return s "]"
}
