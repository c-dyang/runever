#Requires AutoHotkey v2.0
#SingleInstance Off
; ============================================================
; RunEver 执行器
; 接收一个任务文件路径 → 执行 → 退出
;
; 设计（用户定案 2026-10-02）：
;   ★ 单向：不回推结果给主程序
;   ★ 允许并发：多个执行器可同时存在
;   ★ 失败时弹交互式小菜单（用户点"继续"才重试）
;   ★ 卡死就卡死这一路；下一轮主程序启动会清理
; ============================================================

; ---- 编译版参数形态：第一个参数即脚本参数 ----
if (A_Args.Length < 1) {
    ; 无参数 = 被误调用，直接退出（不弹窗，避免卡住主程序）
    ExitApp(2)
}

; ---- 根目录：从任务文件位置反推，不依赖 exe 放在哪 ----
;   任务文件恒在 <root>\state\tasks\xxx.task，故向上 3 级即 root。
;   ❌ 旧写法 A_ScriptDir "\.." 的坑：exe 不在 bin\ 下时日志/缓存路径全废。
global g_taskFile := A_Args[1]
global g_root     := DeriveRoot(g_taskFile)
global g_log      := g_root "\state\exec.log"
global g_task     := ""
global g_retry    := 0

ExecMain()

ExecMain() {
    global
    try DirCreate(g_root "\state")
    Log("--- 执行器启动 PID=" ProcessExist() " task=" g_taskFile " ---")

    if !FileExist(g_taskFile) {
        Log("任务文件不存在: " g_taskFile)
        ExitApp(3)
    }

    ; 读任务
    try {
        vRaw := FileRead(g_taskFile, "UTF-8")
        g_task := ParseTask(vRaw)
    } catch as e {
        Log("任务解析失败: " e.Message)
        ExitApp(4)
    }

    Log("任务: type=" g_task["type"] " target=" g_task["target"] " title=" g_task["title"])

    ; 执行（带重试菜单）
    RunWithRetry()

    ; 清理任务文件
    try FileDelete(g_taskFile)

    Log("--- 执行器结束 ---")
    ExitApp(0)
}

; ---- 带重试的执行 ----
RunWithRetry() {
    global
    loop {
        vOk := false
        errMsg := ""
        try {
            vOk := DoAction(g_task)
        } catch as e {
            vOk := false
            errMsg := e.Message
        }

        if (vOk) {
            Log("执行成功")
            return
        }

        ; ---- 失败 → 交互式小菜单 ----
        Log("执行失败: " errMsg)
        vChoice := ShowFailMenu(errMsg)
        if (vChoice != "retry") {
            Log("用户选择不重试，退出")
            return
        }
        g_retry++
        Log("用户选择重试（第 " g_retry " 次）")
        if (g_retry > 5) {
            Log("重试超过 5 次，放弃")
            return
        }
    }
}

; ---- 失败小菜单 ----
ShowFailMenu(errMsg) {
    global
    global g_menuChoice := ""

    vShort := (errMsg = "") ? "(未知错误)" : errMsg
    m := Menu()
    m.Add("重试", MenuPickRetry)
    m.Add("打开任务文件", MenuPickOpenTask)
    m.Add("打开日志", MenuPickOpenLog)
    m.Add()
    m.Add(SubStr(vShort, 1, 60), NoOp)

    m.Show()
    return g_menuChoice
}

NoOp(*) {
}

MenuPickRetry(*) {
    global g_menuChoice
    g_menuChoice := "retry"
}
MenuPickOpenTask(*) {
    global
    Run(g_taskFile)
}
MenuPickOpenLog(*) {
    global
    Run(g_log)
}

; ============================================================
; 动作分发
; ★ 注意：`type` 是 AHK 内置函数名（Type()），不能用作变量名
;   → 全部改用 vType
; ============================================================
DoAction(t) {
    global
    vType := t["type"]
    vTarget := ReplaceVars(t["target"], t)

    Log("DoAction type=" vType " resolvedTarget=[" vTarget "]")

    switch vType {
        case "run":
            return DoRun(vTarget, t)
        case "open":
            return DoOpen(vTarget)
        case "url":
            return DoUrl(vTarget)
        case "action":
            return DoCmd(vTarget, t)
        case "send":
            return DoSend(vTarget)
        case "keys":
            return DoKeys(vTarget)
        default:
            Log("未知动作类型: " vType)
            return false
    }
}

; ---- run: 启动程序（首个 token 解析全路径）----
DoRun(target, t) {
    global
    vParts := SplitWS(target)
    if (vParts.Length < 1)
        return false

    vProg := Trim(vParts[1], '"')
    vRest := ""
    if (vParts.Length >= 2) {
        vPos := InStr(target, vParts[2])
        vRest := SubStr(target, vPos)
    }

    ; ---- 解析全路径 ----
    ; 三级：含路径 → 直用；否则查主程序缓存；缓存未命中 → 交原生解析
    vFull := ""
    vQuoted := true
    if (InStr(vProg, "\") || InStr(vProg, "/")) {
        vFull := vProg
    } else {
        vFull := LookupPath(vProg)
        if (vFull = "") {
            ; ★ 缓存未命中（新增程序未重建缓存 / 缓存过期）
            ;   AHK 的 Run 原生就会查 PATH + App Paths 注册表，
            ;   直接把裸名交给它即可，不必在这里重造解析逻辑。
            ;   ★ 不能加引号：引号会干扰 AHK 的原生搜索
            Log("缓存未命中，交原生解析: " vProg)
            vQuoted := false
        }
    }

    if (vFull != "" && !FileExist(vFull)) {
        Log("程序不存在: " vFull)
        return false
    }

    vCmd := vQuoted ? ('"' vFull '"') : vProg
    if (vRest != "")
        vCmd .= " " vRest

    Log("Run: " vCmd)
    try {
        Run(vCmd)
    } catch as e {
        Log("Run 失败: " e.Message)
        return false
    }
    return true
}

; ---- open: 打开文件/文件夹 ----
DoOpen(target) {
    global
    if (target = "")
        return false
    ; 多个文件用引号分隔
    paths := SplitWS(target)
    for p in paths {
        p := Trim(p, '"')
        if (p = "")
            continue
        if !FileExist(p) {
            Log("路径不存在: " p)
            return false
        }
        Run('"' p '"')
    }
    return true
}

; ---- url: 打开网址 ----
DoUrl(target) {
    global
    if (target = "")
        return false
    Log("URL: " target)
    Run(target)
    return true
}

; ---- action: cmd 管道（★ 一等公民）----
;   显示/隐藏定夺（2026-10-03 用户定案，两级开关）：
;     ① 条目 flag（menus.conf 写 `| action show xxx`）—— 最具体，优先
;     ② 插件 show（自动执行插件的 RVE_FN_Show）—— 批量策略
;     ③ 都缺省 → 隐藏（存量行为，零迁移）
;   ★ 显示时必须 RunWait：否则执行器秒退 → cmd 窗口被连带关闭或成孤儿。
;     等待只阻塞【执行器自己】这个独立进程，不违反主程序零阻塞约束。
DoCmd(target, t := "") {
    global
    if (target = "")
        return false

    ; ---- 定夺显示 / 隐藏 ----
    vFlag := (t != "" && t.Has("flag")) ? t["flag"] : ""
    vShow := false
    if (vFlag = "show")
        vShow := true
    else if (vFlag = "hide")
        vShow := false
    else if (t != "" && t.Has("show"))
        vShow := (t["show"] ? true : false)      ; 插件带来的 0/1 或 true/false
    ; flag=""/缺失 且无 show 键 → vShow 保持 false（隐藏）

    vOpt := vShow ? "" : "Hide"
    Log("Cmd: " target "  show=" (vShow ? 1 : 0) " flag=[" vFlag "]")
    try {
        if (vShow)
            RunWait(A_ComSpec " /c " target, , vOpt)
        else
            Run(A_ComSpec " /c " target, , vOpt)
    } catch as e {
        Log("Cmd 失败: " e.Message)
        return false
    }
    return true
}

; ---- send: 发送文本 ----
DoSend(target) {
    global
    try {
        SendText(target)
        return true
    } catch as e {
        Log("SendText 失败: " e.Message)
        return false
    }
}

; ---- keys: 发快捷键 ----
DoKeys(target) {
    global
    try {
        Send(target)
        return true
    } catch as e {
        Log("Send 失败: " e.Message)
        return false
    }
}

; ============================================================
; 占位符替换
; ============================================================
ReplaceVars(s, t) {
    if (s = "")
        return ""
    ; %s% = 搜索词（此处用文本或剪贴板），%text% 选中文本，%file% 选中文件，
    ; %clip% 剪贴板，%win% 窗口标题，%proc% 进程名
    s := StrReplace(s, "%text%", t["text"])
    s := StrReplace(s, "%file%", t["files"].Length >= 1 ? t["files"][1] : "")
    s := StrReplace(s, "%clip%", A_Clipboard)
    s := StrReplace(s, "%win%", t["winTitle"])
    s := StrReplace(s, "%proc%", t["proc"])
    ; %s = 搜索词：优先文本，其次剪贴板
    sVal := t["text"]
    if (sVal = "")
        sVal := A_Clipboard
    s := StrReplace(s, "%s", sVal)
    return s
}

; ============================================================
; 路径查找（读主程序的缓存文件）
; ============================================================
LookupPath(progName) {
    global
    cacheFile := g_root "\cache\paths.txt"
    if !FileExist(cacheFile)
        return ""
    vKey := StrLower(progName)
    try {
        txt := FileRead(cacheFile, "UTF-8")
        for vLine in StrSplit(txt, "`n") {
            vLine := Trim(vLine, " `t`r")
            if (vLine = "" || SubStr(vLine, 1, 1) = "#")
                continue
            vPos := InStr(vLine, "=")
            if (vPos < 2)
                continue
            k := Trim(SubStr(vLine, 1, vPos - 1), " `t")
            if (StrLower(k) = vKey) {
                v := Trim(SubStr(vLine, vPos + 1), " `t")
                if FileExist(v)
                    return v
            }
        }
    } catch {
    }
    return ""
}

; ============================================================
; 任务文件解析（极简 JSON 提取，不引外部库）
; ============================================================
ParseTask(raw) {
    t := Map(
        "id",       GetJsonStr(raw, "id"),
        "type",     GetJsonStr(raw, "type"),
        "target",   GetJsonStr(raw, "target"),
        "title",    GetJsonStr(raw, "title"),
        "text",     GetJsonStr(raw, "text"),
        "proc",     GetJsonStr(raw, "proc"),
        "winTitle", GetJsonStr(raw, "winTitle"),
        "winClass", GetJsonStr(raw, "winClass"),
        "ts",       GetJsonStr(raw, "ts"),
        "flag",     GetJsonStr(raw, "flag"),      ; 条目级："" | "hide" | "show"
        "files",    GetJsonArr(raw, "files")
    )
    ; ★ show 只在任务文件里【显式存在】时才放进 Map —— 用 Has("show") 区分
    ;   "未声明"（老任务文件 / 菜单普通条目）与"显式 false"。DoCmd 依赖这个区分。
    if RegExMatch(raw, '"show"\s*:\s*(true|false|0|1)', &mS) {
        vS := StrLower(mS[1])
        t["show"] := (vS = "true" || vS = "1")
    }
    return t
}

GetJsonStr(raw, key) {
    pat := '"' key '"\s*:\s*"((?:[^"\\]|\\.)*)"'
    if RegExMatch(raw, pat, &m) {
        v := m[1]
        v := StrReplace(v, "\r", "`r")
        v := StrReplace(v, "\n", "`n")
        v := StrReplace(v, "\t", "`t")
        v := StrReplace(v, '\"', '"')
        v := StrReplace(v, "\\", "\")
        return v
    }
    return ""
}

GetJsonArr(raw, key) {
    out := []
    pat := '"' key '"\s*:\s*\[(.*?)\]'
    if RegExMatch(raw, pat, &m) {
        inner := m[1]
        if (Trim(inner) = "")
            return out
        for piece in StrSplit(inner, ",") {
            piece := Trim(piece, " `t")
            piece := Trim(piece, '"')
            if (piece != "")
                out.Push(piece)
        }
    }
    return out
}

SplitWS(s) {
    out := []
    buf := ""
    inQ := false
    Loop Parse s {
        c := A_LoopField
        if (c = '"') {
            inQ := !inQ
            buf .= c
            continue
        }
        if (!inQ && (c = " " || c = "`t")) {
            if (buf != "") {
                out.Push(buf)
                buf := ""
            }
            continue
        }
        buf .= c
    }
    if (buf != "")
        out.Push(buf)
    return out
}

; ============================================================
; 根目录反推
; ============================================================
DeriveRoot(taskFile) {
    ; taskFile = <root>\state\tasks\xxx.task → 上 3 级 = <root>
    ; 任务文件位置才是权威的根：主程序可能用 --root 把 config/cache/state
    ; 指到别处，此时 exe 所在目录并不是根。
    vD1 := "", vD2 := "", vD3 := ""
    SplitPath(taskFile, , &vD1)
    SplitPath(vD1, , &vD2)
    SplitPath(vD2, , &vD3)
    if (vD3 != "" && (FileExist(vD3 "\config\menus.conf") || FileExist(vD3 "\cache\paths.txt")))
        return vD3
    ; 回退：与主程序同目录（自包含发行布局）
    return A_ScriptDir
}

; ============================================================
; 日志
; ============================================================
Log(s) {
    global
    try FileAppend(FormatTime(A_Now, "HH:mm:ss") " [" ProcessExist() "] " s "`n", g_log, "UTF-8")
}
