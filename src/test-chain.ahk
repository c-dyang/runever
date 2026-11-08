#Requires AutoHotkey v2.0
#SingleInstance Off
; ============================================================
; 端到端链路测试：模拟 OnItemClick（不依赖热键）
;   采集 → 判定 → 构建菜单 → 挑一个条目 → 写任务 → 起执行器
; 用法: AutoHotkey64.exe test-chain.ahk [目标标题子串]
; ============================================================

#Include ConfParser.ahk
#Include PathCache.ahk
#Include Collector.ahk
#Include Matcher.ahk

global T_DIR := A_ScriptDir "\.."
global T_LOG := T_DIR "\state\chain-test.log"

ChainMain()

ChainMain() {
    global
    FileAppend("=== chain test @ " FormatTime(A_Now, "HH:mm:ss") "`n", T_LOG, "UTF-8")

    ; --- 配置 ---
    vParser := ConfParser(T_DIR "\config\menus.conf")
    FileAppend("分组数=" vParser.Groups.Length "`n", T_LOG, "UTF-8")

    ; --- 缓存 ---
    vCache := PathCache(T_DIR "\cache\paths.txt")
    FileAppend("缓存条目=" vCache.Map.Count "`n", T_LOG, "UTF-8")

    ; --- 采集 ---
    vCtx := Collector()
    vCtx.Collect()
    FileAppend("采集: text=" StrLen(vCtx.Text) "B files=" vCtx.Files.Length
        . " proc=[" vCtx.Win["proc"] "]`n", T_LOG, "UTF-8")

    ; --- 判定 ---
    vMatcher := Matcher(vParser)
    vR := vMatcher.Match(vCtx)
    vGroups := vR["groups"]
    vSt := vR["stats"]
    FileAppend("判定: 命中分组=" vGroups.Length " always=" vSt["always"]
        . " dynamic=" vSt["dynamic"] " fallback=" vSt["fallback"] "`n", T_LOG, "UTF-8")

    ; --- 枚举所有条目 ---
    vAll := []
    for g in vGroups {
        for it in g["items"] {
            vAll.Push(it)
            FileAppend("  [" g["name"] "] " it["title"] " | " it["type"] " " it["target"] "`n", T_LOG, "UTF-8")
        }
    }
    FileAppend("条目总数=" vAll.Length "`n", T_LOG, "UTF-8")

    if (vAll.Length = 0) {
        FileAppend("!! 无条目，链路无法继续`n", T_LOG, "UTF-8")
        ExitApp(10)
    }

    ; --- 挑目标（默认第一个 run 条目）---
    vWant := (A_Args.Length >= 1) ? A_Args[1] : "notepad"
    vPick := ""
    for it in vAll {
        if (InStr(StrLower(it["title"]), StrLower(vWant)) || InStr(StrLower(it["target"]), StrLower(vWant))) {
            vPick := it
            break
        }
    }
    if (vPick = "") {
        FileAppend("!! 未找到匹配 '" vWant "' 的条目`n", T_LOG, "UTF-8")
        ExitApp(11)
    }

    FileAppend("选中: " vPick["title"] " | " vPick["type"] " " vPick["target"] "`n", T_LOG, "UTF-8")

    ; --- 写任务文件（复刻 OnItemClick）---
    vTask := Map(
        "id",       A_TickCount "_" Random(1000, 9999),
        "type",     vPick["type"],
        "target",   vPick["target"],
        "title",    vPick["title"],
        "text",     vCtx.Text,
        "files",    vCtx.Files,
        "proc",     vCtx.Win["proc"],
        "winTitle", vCtx.Win["title"],
        "winClass", vCtx.Win["class"],
        "ts",       A_Now
    )
    vTaskFile := T_DIR "\state\tasks\" vTask["id"] ".task"
    WriteTaskFile(vTaskFile, vTask)
    FileAppend("任务文件已写: " vTaskFile "`n", T_LOG, "UTF-8")

    ; --- 起执行器 ---
    vExecPath := A_ScriptDir "\RunEverExec.ahk"
    vCmd := '"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe" "' vExecPath '" "' vTaskFile '"'
    FileAppend("启动执行器: " vCmd "`n", T_LOG, "UTF-8")
    Run(vCmd)

    FileAppend("=== chain test 完成`n", T_LOG, "UTF-8")
    ExitApp(0)
}

; ---- 复刻主程序的 WriteTaskFile ----
WriteTaskFile(path, task) {
    s := "{`n"
    s .= '  "id": ' JsonStr(task["id"]) ",`n"
    s .= '  "type": ' JsonStr(task["type"]) ",`n"
    s .= '  "target": ' JsonStr(task["target"]) ",`n"
    s .= '  "title": ' JsonStr(task["title"]) ",`n"
    s .= '  "text": ' JsonStr(task["text"]) ",`n"
    s .= '  "files": ' JsonArr(task["files"]) ",`n"
    s .= '  "proc": ' JsonStr(task["proc"]) ",`n"
    s .= '  "winTitle": ' JsonStr(task["winTitle"]) ",`n"
    s .= '  "winClass": ' JsonStr(task["winClass"]) ",`n"
    s .= '  "ts": ' JsonStr(task["ts"]) "`n"
    s .= "}`n"
    try FileDelete(path)
    FileAppend(s, path, "UTF-8")
}

JsonStr(v) {
    if (v = "" || v = 0)
        return '""'
    v := StrReplace(v, "\", "\\")
    v := StrReplace(v, '"', '\"')
    v := StrReplace(v, "`r", "\r")
    v := StrReplace(v, "`n", "\n")
    v := StrReplace(v, "`t", "\t")
    return '"' v '"'
}

JsonArr(a) {
    s := "["
    for i, v in a {
        if (i > 1)
            s .= ", "
        s .= JsonStr(v)
    }
    return s "]"
}
