#Requires AutoHotkey v2.0
#SingleInstance Off
#Warn VarUnset, Off
; ============================================================
; RunEver 热键触发器（AHK 侧，唯一职责）
;
; ★ 分工（用户 2026-10-03 定案）：
;   热键 → 留 AHK（单键 ` 用 AHK 可靠；C# 需低级键盘钩子，有杀软风险）
;   其余 → 全在 C#（菜单渲染 / 判定 / 执行 / 设置 UI）
;
; 工作方式：
;   按下热键 → 起 RunEverCs.exe --show-menu --root <root> → 立即返回（不等）
;   C# 自己完成采集→匹配→弹菜单→执行→退出。
;
;   ★ 不等待：与 AHK 版"进程生命周期消灭阻塞"架构一致 ——
;     主程序常驻只负责热键，任何卡死都在 C# 子进程里，不影响热键响应。
; ============================================================

; ---- 定位 root：本脚本恒在 <root>\src\ 下 ----
global g_root := ""
global g_csExe := ""

DetectRoot()
RegisterHotkey()

DetectRoot() {
    global
    vDir := A_ScriptDir

    ; ① 同级上找 config\menus.conf（开发态：<root>\src\）
    SplitPath(vDir, , &vParent)
    if (FileExist(vParent "\config\menus.conf")) {
        g_root := vParent
    } else if (FileExist(vDir "\config\menus.conf")) {
        g_root := vDir
    } else {
        ; ② 发行态：exe 同目录
        if (FileExist(A_ScriptDir "\config\menus.conf"))
            g_root := A_ScriptDir
        else
            g_root := vParent
    }

    ; C# 主程序位置：优先 dist 编译版，其次开发态 bin
    for vCand in [g_root "\dist\RunEverCs.exe"
                , g_root "\cs\bin\Release\RunEverCs.exe"] {
        if (FileExist(vCand)) {
            g_csExe := vCand
            break
        }
    }
}

RegisterHotkey() {
    global
    ; ★ 单键反引号 —— 这就是用 AHK 而非 C# 做热键的原因
    try {
        Hotkey("``", OnHotkey)
    } catch as e {
        MsgBox("热键注册失败：`n" e.Message, "RunEver", "Icon!")
        ExitApp(1)
    }

    ; 托盘图标（右键菜单：打开设置 / 重载 / 退出）
    A_IconTip := "RunEver 热键触发器"
    SetupTray()

    Log("热键注册 OK: ``")
    Log("root = " g_root)
    Log("C# exe = " (g_csExe = "" ? "(未找到)" : g_csExe))
}

SetupTray() {
    global
    tm := A_TrayMenu
    tm.Delete()
    tm.Add("打开设置…", (*) => RunCs("--settings"))
    tm.Add()
    tm.Add("打开 C# 日志", (*) => Run(g_root "\state\runever-cs.log"))
    tm.Add("打开日志", (*) => Run(A_ScriptDir "\..\state\hotkey.log"))
    tm.Add()
    tm.Add("退出", (*) => ExitApp())
    tm.Default := "打开设置…"
}

; ---- 热键回调 ----
OnHotkey(*) {
    global
    if (g_csExe = "") {
        TrayTip("未找到 RunEverCs.exe", "RunEver", 3)
        return
    }
    ; ★ 不等待：立即返回，热键保持响应
    try {
        Run('"' g_csExe '" --show-menu --root "' g_root '"')
        Log("已触发 C# 主程序")
    } catch as e {
        Log("触发失败: " e.Message)
    }
}

; ---- 起 C# 并等待（设置窗口用）----
RunCs(vArg) {
    global
    if (g_csExe = "") {
        MsgBox("未找到 RunEverCs.exe`n`n期望位置：`n" g_root "\dist\RunEverCs.exe", "RunEver", "Icon!")
        return
    }
    try {
        Run('"' g_csExe '" ' vArg ' --root "' g_root '"')
    } catch as e {
        Log("启动失败: " e.Message)
    }
}

Log(s) {
    global
    try {
        vDir := g_root "\state"
        if !DirExist(vDir)
            DirCreate(vDir)
        FileAppend(FormatTime(A_Now, "HH:mm:ss") " [" ProcessExist() "] " s "`n"
            , vDir "\hotkey.log", "UTF-8")
    } catch {
    }
}
