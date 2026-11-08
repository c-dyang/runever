#Requires AutoHotkey v2.0
#SingleInstance Off
#NoTrayIcon
; 配置解析器测试
#Include ConfParser.ahk

global g_log := A_ScriptDir "\..\state\test-parser.log"

Log(s) {
    global g_log
    FileAppend(s "`n", g_log, "UTF-8")
}

try FileDelete(g_log)
Log("=== parser test ===")

confPath := A_ScriptDir "\..\config\menus.conf"
Log("conf = " confPath)
Log("exist = " FileExist(confPath))

try {
    p := ConfParser(confPath)
    Log("--- 解析成功 ---")
    Log(p.Dump())

    Log("--- 收集到的程序名 ---")
    progs := p.CollectPrograms()
    n := 0
    for k, v in progs {
        n++
        Log("  " n ": " k)
    }
    Log("程序名总数: " n)

} catch as e {
    Log("FAIL: " e.Message)
    Log("  what = " e.What)
    Log("  line = " e.Line)
    Log("  file = " e.File)
}

Log("=== done ===")
ExitApp(0)
