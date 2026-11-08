#Requires AutoHotkey v2.0
#SingleInstance Off
#NoTrayIcon
; 路径缓存测试
#Include ConfParser.ahk
#Include PathCache.ahk

global g_log := A_ScriptDir "\..\state\test-pathcache.log"

Log(s) {
    global g_log
    FileAppend(s "`n", g_log, "UTF-8")
}

try FileDelete(g_log)
Log("=== pathcache test @ " FormatTime(A_Now, "HH:mm:ss") " ===")

confPath  := A_ScriptDir "\..\config\menus.conf"
cacheFile := A_ScriptDir "\..\cache\paths.txt"

; 1. 解析配置
p := ConfParser(confPath)
progs := p.CollectPrograms()
Log("配置程序名数: " progs.Count)

; 2. 建缓存（全量）
t1 := A_TickCount
pc := PathCache(cacheFile)
r := pc.Build(progs, true)
t2 := A_TickCount
Log("--- 全量构建完成，耗时 " (t2 - t1) " ms ---")
Log("  解析成功: " r["resolved"])
Log("  失败:     " r["failed"])
Log("  跳过:     " r["skipped"])

if (r["failed"] > 0) {
    Log("  失败清单:")
    for f in r["failedList"]
        Log("    - " f)
}

Log("--- 缓存内容 ---")
Log(pc.Dump())

; 3. 增量测试（应全部跳过）
t3 := A_TickCount
r2 := pc.Build(progs, false)
t4 := A_TickCount
Log("--- 增量构建（应全跳过），耗时 " (t4 - t3) " ms ---")
Log("  解析: " r2["resolved"] " 失败: " r2["failed"] " 跳过: " r2["skipped"])

; 4. 单点查询测试
Log("--- 单点查询 ---")
for name in ["chrome.exe", "7zfm.exe", "pwsh.exe", "notepad.exe", "7z.exe", "calc.exe", "taskmgr.exe", "everything.exe"] {
    Log("  " name " -> " pc.Get(name))
}

; 5. 解析来源标注（验收用：证明走的是哪一级）
Log("--- 解析来源（tier）---")
for name in ["chrome.exe", "mspaint.exe", "notepad.exe", "calc.exe", "taskmgr.exe", "explorer.exe", "curl.exe", "ffmpeg.exe", "git.exe", "node.exe", "pwsh.exe", "7z.exe", "7zfm.exe", "everything.exe", "autohotkey64.exe", "msedge.exe"] {
    r := pc.ResolveOneEx(name)
    Log("  " name "`t[" r["tier"] "]`t" r["path"])
}

Log("=== done ===")
ExitApp(0)