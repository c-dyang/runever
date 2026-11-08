#Requires AutoHotkey v2.0
#SingleInstance Off
#NoTrayIcon
; 参数传递排查
global g_log := A_ScriptDir "\..\state\test-args.log"
try FileDelete(g_log)
FileAppend("A_Args.Length = " A_Args.Length "`n", g_log, "UTF-8")
i := 0
for a in A_Args {
    i++
    FileAppend("  [" i "] = " a "`n", g_log, "UTF-8")
}
FileAppend("A_LineFile = " A_LineFile "`n", g_log, "UTF-8")
FileAppend("A_ScriptDir = " A_ScriptDir "`n", g_log, "UTF-8")
FileAppend("cmdline = " DllCall("GetCommandLine", "Str") "`n", g_log, "UTF-8")
ExitApp(0)
