#Requires AutoHotkey v2.0
; ============================================================
; RunEver 采集模块
; 采集三件事：选中文本、选中文件、前台窗口信息
;
; 设计约束（目标卡）：
;   - 采集必须是"有界"的，任何一项失败/超时都不影响其他项
;   - 全部失败也要能返回空上下文（菜单仍可显示 always/fallback）
; ============================================================

class Collector {

    __New() {
        this.Text    := ""
        this.Files   := []
        this.Win     := Map("title", "", "class", "", "proc", "")
        this.IsFileCtx := false
    }

    ; ---- 采集全部 ----
    Collect() {
        this.Text := ""
        this.Files := []
        this.Win := Map("title", "", "class", "", "proc", "")
        this.IsFileCtx := false

        this.CaptureWindow()
        this.CaptureSelection()
        return this
    }

    ; ---- 前台窗口（最稳）----
    CaptureWindow() {
        try {
            hwnd := WinExist("A")
            if (!hwnd)
                return
            vTitle := WinGetTitle("ahk_id " hwnd)
            vClass := WinGetClass("ahk_id " hwnd)
            vProc  := WinGetProcessName("ahk_id " hwnd)
            this.Win := Map(
                "title", vTitle,
                "class", vClass,
                "proc",  vProc
            )
        } catch {
            ; 保持空值
        }
    }

    ; ---- 选中文本 / 文件 ----
    ; 策略（参考 RunAny Get_Zz 的稳妥做法）：
    ;   1. 先看剪贴板里是否已有文件（CF_HDROP）
    ;   2. 模拟 Ctrl+C，带超时（ClipWait）
    ;   3. 判断取得的是文件还是文本，还原剪贴板
    CaptureSelection() {
        ; 保存原剪贴板
        vSavedAll := ""
        try vSavedAll := ClipboardAll()

        try {
            A_Clipboard := ""
            SendInput("^c")

            ; 有界等待（0.15s，超时即放弃）
            vClipped := false
            try {
                vClipped := ClipWait(0.15)
            } catch {
                vClipped := false
            }

            if (!vClipped) {
                ; 恢复剪贴板
                if (vSavedAll != "")
                    try A_Clipboard := vSavedAll
                return
            }

            ; 判断是否文件（CF_HDROP = 15）
            vIsFile := DllCall("IsClipboardFormatAvailable", "UInt", 15)
            if (vIsFile) {
                this.IsFileCtx := true
                ; 取文件列表
                try {
                    vFiles := this.GetClipboardFiles()
                    this.Files := vFiles
                } catch {
                    this.Files := []
                }
            } else {
                try this.Text := A_Clipboard
            }
        } catch {
        }

        ; 还原剪贴板
        if (vSavedAll != "")
            try A_Clipboard := vSavedAll
    }

    ; ---- 取剪贴板中的文件列表 ----
    ;   用 PowerShell 取（稳妥，避免解析 CF_HDROP 原始结构）
    ;   ★ 编码口径（契约 §1-D12）与 CaptureRunner.ahk 同源同修：
    ;     ① 生成的 .ps1 用 `param([string]$out)` 收输出路径 —— 路径不进脚本正文；
    ;     ② 脚本体用 [IO.File]::WriteAllLines + UTF8Encoding($false) 直写 UTF-8 无 BOM；
    ;     ③ 删除 `cmd >` 重定向（无控制台时按 OEM 代码页 936 落盘 ⇒ 中文必乱码）；
    ;     ④ 脚本以 "UTF-8-RAW" 落盘（内容纯 ASCII ⇒ 逐字节 ≤0x7F，无 BOM 歧义）。
    GetClipboardFiles() {
        out := []

        ps := "param([string]$out)`r`n"
            . "Add-Type -AssemblyName System.Windows.Forms`r`n"
            . "$a = @()`r`n"
            . "foreach ($f in [System.Windows.Forms.Clipboard]::GetFileDropList()) { $a += [string]$f }`r`n"
            . "[IO.File]::WriteAllLines($out, [string[]]$a, (New-Object System.Text.UTF8Encoding($false)))`r`n"
        psFile := A_Temp "\rv_clipfiles_" A_TickCount ".ps1"
        try FileDelete(psFile)
        outFile := A_Temp "\rv_clipout_" A_TickCount ".txt"
        try FileDelete(outFile)

        try {
            FileAppend(ps, psFile, "UTF-8-RAW")
            RunWait(A_ComSpec " /c powershell -NoProfile -ExecutionPolicy Bypass -File "
                . '"' psFile '" "' outFile '"', , "Hide")
            if FileExist(outFile) {
                vTxt := FileRead(outFile, "UTF-8")
                ; ★ U+FFFD 告警（读到替换字符 ⇒ 落日志，不静默）
                if InStr(vTxt, Chr(0xFFFD)) {
                    try FileAppend(FormatTime(A_Now, "yyyy-MM-dd HH:mm:ss")
                        . " [WARN] U+FFFD 来源=collector 样本=["
                        . StrReplace(StrReplace(SubStr(vTxt, 1, 80), "`r", " "), "`n", " ") "]`r`n",
                        A_ScriptDir "\..\state\collector.log", "UTF-8")
                }
                for vLine in StrSplit(vTxt, "`n") {
                    vLine := Trim(vLine, " `t`r")
                    if (vLine != "")
                        out.Push(vLine)
                }
            }
        } catch {
        }
        try FileDelete(psFile)
        try FileDelete(outFile)
        return out
    }

    ; ---- 取扩展名集合（小写，不含点）----
    GetExtensions() {
        exts := []
        for f in this.Files {
            SplitPath(f, , , &vExt)
            if (vExt != "")
                exts.Push(StrLower(vExt))
        }
        return exts
    }

    ; ---- 是否有选中的文件夹 ----
    HasFolder() {
        for f in this.Files {
            if InStr(FileExist(f), "D")
                return true
        }
        return false
    }

    ; ---- 调试输出 ----
    Dump() {
        s := "文本: [" this.Text "]`n"
        s .= "文件数: " this.Files.Length "`n"
        for f in this.Files
            s .= "  " f "`n"
        s .= "窗口标题: " this.Win["title"] "`n"
        s .= "窗口类:   " this.Win["class"] "`n"
        s .= "进程名:   " this.Win["proc"] "`n"
        s .= "扩展名:   " this.JoinArr(this.GetExtensions()) "`n"
        s .= "含文件夹: " (this.HasFolder() ? "是" : "否") "`n"
        return s
    }

    JoinArr(a) {
        s := ""
        for v in a
            s .= (s = "" ? "" : ",") v
        return s
    }
}
