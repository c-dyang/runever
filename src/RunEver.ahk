#Requires AutoHotkey v2.0
#SingleInstance Force
; ============================================================
; RunEver 主程序
; 热键 → 采集 → 判定 → 渲染菜单 → 点击 → 甩给执行器
;
; 架构约束（目标卡）：
;   ★ 热键路径零外部 IO（只读已加载的配置 + 路径缓存）
;   ★ 一切外部操作甩给执行器
; ============================================================

#Include ConfParser.ahk
#Include PathCache.ahk
#Include Collector.ahk
#Include Matcher.ahk
#Include MenuNative.ahk
#Include AutoMatcher.ahk
#Include SettingsUI.ahk

; ============================================================
; 命令行参数 + 根目录（★ 必须在常量之前；auto-execute 段止于首个函数定义，
;   所以这段不能包成函数，包了就不会执行）
;
; 根目录规则：root = exe 所在目录。发行物是一套自包含目录，
; 拷到哪台机器都能直接跑，不再依赖 bin\ 的父目录：
;   RunEver\RunEver.exe
;   RunEver\RunEverExec.exe
;   RunEver\config\menus.conf     ← 首次启动自动生成
;   RunEver\cache\paths.txt       ← 首次启动自动构建
;   RunEver\state\
;
;   --root <dir>                     指定别的根（开发态跑 src\ 源码时用）
;   --selftest [--click <标题子串>]   自检后退出
;
; ❌ 曾经的写法 root = A_ScriptDir "\.."：exe 一旦不在 bin\ 下，
;    state 就指向不存在的目录 → LogInit 里裸 FileAppend 抛
;    "(3) 系统找不到指定的路径" 模态框 → 卡死。
; ============================================================
global RVE_ROOT     := A_ScriptDir
global RVE_SELFTEST := false
global RVE_CLICK    := ""

vI := 1
while (vI <= A_Args.Length) {
    vA := A_Args[vI]
    if (vA = "--root" && vI < A_Args.Length) {
        RVE_ROOT := RTrim(A_Args[vI + 1], "\/")
        vI += 2
    } else if (vA = "--selftest") {
        RVE_SELFTEST := true
        vI += 1
    } else if (vA = "--click" && vI < A_Args.Length) {
        RVE_CLICK := A_Args[vI + 1]
        vI += 2
    } else {
        vI += 1
    }
}

; ============================================================
; 常量区
; ============================================================
global RVE_CONF_DIR   := RVE_ROOT "\config"
global RVE_CACHE_DIR  := RVE_ROOT "\cache"
global RVE_CONF       := RVE_CONF_DIR "\menus.conf"
global RVE_CONF_AUTO  := RVE_CONF_DIR "\auto.conf"          ; ★ 自动执行配置
global RVE_FN_DIR     := RVE_CONF_DIR "\functions"          ; ★ 插件目录
global RVE_CACHE      := RVE_CACHE_DIR "\paths.txt"
global RVE_STATE      := RVE_ROOT "\state"
global RVE_TASKS      := RVE_STATE "\tasks"
global RVE_EXEC       := A_ScriptDir "\RunEverExec.exe"
global RVE_LOG        := RVE_STATE "\runever.log"
global RVE_HOTKEY     := "``"                 ; 默认反引号

; 运行时全局
global g_parser       := ""
global g_cache        := ""
global g_matcher      := ""
global g_lastCtx      := ""
global g_bootstrapped := false                ; 本轮是否新生成过默认配置
global g_iconReal     := 0                    ; 图标计数：真程序图标
global g_iconFallback := 0                    ; 图标计数：兜底图标
global g_autoMatcher  := ""                   ; ★ 自动执行匹配器（匹配块）
global g_settings     := ""                   ; ★ 设置管理 UI（复用同一实例）

; ============================================================
; 自检模式（★ 编译版验活专用）
;   背景：/ErrorStdOut 对「编译版」无效，编译也不做语法校验，
;         所以每次重编后必须真正跑一次才能验活。
;   跑法：RunEver.exe --selftest
;   行为：完整走一遍 配置→缓存→采集→判定→菜单渲染，然后退出；
;         不注册热键、不建托盘、不触发缓存构建（避免副作用）。
;   可选：RunEver.exe --selftest --click <标题子串>
;         额外走一遍 OnItemClick（写任务文件 + 起执行器），
;         把「最后一环」也闭合，无需真人点菜单。
; ============================================================
if (RVE_SELFTEST) {
    SelfTest(RVE_CLICK)
    ExitApp(0)
}

Main()

Main() {
    global

    ; --- 目录 / 配置准备（★ 必须早于任何日志写入）---
    EnsureLayout()
    if !FileExist(RVE_CONF) {
        MsgBox("RunEver 无法生成配置文件，请检查目录是否可写：`n`n" RVE_CONF
            , "RunEver", "Icon!")
        ExitApp(1)
    }
    if !DirExist(RVE_STATE) {
        MsgBox("RunEver 无法创建 state 目录，请检查目录是否可写：`n`n" RVE_STATE
            , "RunEver", "Icon!")
        ExitApp(1)
    }

    LogInit()
    Log("=== RunEver start @ " FormatTime(A_Now, "yyyy-MM-dd HH:mm:ss") " ===")
    Log("A_ScriptDir = " A_ScriptDir)
    Log("root = " RVE_ROOT)
    Log("conf = " RVE_CONF)
    Log("cache = " RVE_CACHE)
    if (g_bootstrapped)
        Log("首次启动：已生成默认配置模板")

    ; --- 加载配置 ---
    try {
        g_parser := ConfParser(RVE_CONF)
        Log("配置加载 OK，分组数 = " g_parser.Groups.Length)
    } catch as e {
        Log("配置加载失败: " e.Message)
        MsgBox("RunEver 配置加载失败：`n" e.Message, "RunEver", "Icon!")
        ExitApp(1)
    }

    ; --- 加载路径缓存（只读，不阻塞）---
    try {
        g_cache := PathCache(RVE_CACHE)
        Log("路径缓存加载 OK，条目数 = " g_cache.Map.Count)
    } catch as e {
        Log("路径缓存加载失败: " e.Message)
        g_cache := ""
    }

    g_matcher := Matcher(g_parser)

    ; ★ 自动执行匹配器（匹配块）：扫描插件 + 启动登记
    ;   失败不影响主流程（自动执行是增强，不是必需）
    try {
        g_autoMatcher := AutoMatcher(RVE_ROOT)
        vN := g_autoMatcher.Load()
        Log("自动执行加载 OK，插件数 = " vN
            . " (enabled=" (g_autoMatcher.Enabled ? "1" : "0")
            . " minLen=" g_autoMatcher.MinLen ")")
        if (vN > 0)
            Log("  插件登记 → " g_autoMatcher.GetRegistryPath())
    } catch as e {
        Log("自动执行加载失败（不影响主流程）: " e.Message)
        g_autoMatcher := ""
    }

    ; --- 托盘 ---
    SetupTray()

    ; --- 注册热键 ---
    try {
        Hotkey(RVE_HOTKEY, ShowMenu)
        Log("热键注册 OK: " RVE_HOTKEY)
    } catch as e {
        Log("热键注册失败: " e.Message)
    }

    ; --- 启动后清理假死执行器（轻量，不阻塞）---
    CleanZombies()

    ; --- 首次运行：缓存为空则后台建缓存 ---
    if (g_cache = "" || g_cache.Map.Count = 0) {
        Log("缓存为空，触发首次构建")
        TriggerCacheBuild()
    } else {
        Log("缓存已存在，跳过首次构建")
    }

    Log("主循环就绪")
}

; ============================================================
; 自检（--selftest）：把关键路径全部走一遍，结果写日志
; ============================================================
SelfTest(clickTarget := "") {
    global
    EnsureLayout()
    LogInit()
    Log("=== SELFTEST @ " FormatTime(A_Now, "yyyy-MM-dd HH:mm:ss") " ===")
    Log("A_ScriptDir = " A_ScriptDir)
    Log("root = " RVE_ROOT)
    Log("conf = " RVE_CONF "  (存在=" (FileExist(RVE_CONF) ? "是" : "否")
        . " 本轮自举=" (g_bootstrapped ? "是" : "否") ")")
    Log("cache = " RVE_CACHE "  (存在=" (FileExist(RVE_CACHE) ? "是" : "否") ")")

    vFail := 0

    ; ---- 1. 配置 ----
    try {
        g_parser := ConfParser(RVE_CONF)
        Log("  [OK]   配置解析  分组=" g_parser.Groups.Length)
    } catch as e {
        Log("  [FAIL] 配置解析: " e.Message)
        vFail++
    }

    ; ---- 2. 路径缓存 ----
    try {
        g_cache := PathCache(RVE_CACHE)
        Log("  [OK]   路径缓存  条目=" g_cache.Map.Count)
    } catch as e {
        Log("  [FAIL] 路径缓存: " e.Message)
        g_cache := ""
        vFail++
    }

    if (vFail > 0) {
        Log("=== SELFTEST 中止（前置失败）===")
        return
    }

    ; ---- 3. 采集 ----
    vCtx := ""
    try {
        vCtx := Collector()
        vCtx.Collect()
        Log("  [OK]   采集      text=" StrLen(vCtx.Text) "B files=" vCtx.Files.Length
            . " proc=[" vCtx.Win["proc"] "]")
    } catch as e {
        Log("  [FAIL] 采集: " e.Message)
        vFail++
    }

    ; ---- 4. 判定 ----
    vGroups := []
    try {
        g_matcher := Matcher(g_parser)
        vR := g_matcher.Match(vCtx)
        vGroups := vR["groups"]
        vSt := vR["stats"]
        Log("  [OK]   判定      命中分组=" vGroups.Length
            . " always=" vSt["always"] " dynamic=" vSt["dynamic"] " fallback=" vSt["fallback"])

        ; ★ 顺序断言（2026-10-03 用户定案）：选中动态/fallback → 进程 → 固定
        vSeq := ""
        for g in vGroups
            vSeq .= (vSeq = "" ? "" : " > ") g["type"]
        Log("  [..]   顺序断言  实际序列: " vSeq)

        ; 判据：序列中不得出现 always 早于任何动态类型的情况
        vBad := false
        vSeenAlways := false
        for g in vGroups {
            t := g["type"]
            if (t = "always")
                vSeenAlways := true
            else if (vSeenAlways)
                vBad := true
        }
        if (vBad) {
            Log("  [FAIL] 顺序断言  固定项目(always)出现在动态项之前")
            vFail++
        } else {
            Log("  [OK]   顺序断言  固定项目在最后 ✓")
        }
    } catch as e {
        Log("  [FAIL] 判定: " e.Message)
        vFail++
    }

    ; ---- 5. 图标解析（逐条目）----
    vIconHit := 0
    vIconMiss := 0
    try {
        for g in vGroups {
            for it in g["items"] {
                p := ResolveIconPath(it)
                if (p != "")
                    vIconHit++
                else
                    vIconMiss++
            }
        }
        Log("  [OK]   图标解析  命中=" vIconHit " 未命中=" vIconMiss)
    } catch as e {
        Log("  [FAIL] 图标解析: " e.Message)
        vFail++
    }

    ; ---- 6. 菜单渲染（★ 真正构造 Win32 菜单，不 Show）----
    ; 这一步会跑 AddItemToMenu / 子菜单嵌套 / SetIconById / 标题拼接，
    ; 任何一处出错都会在这里抛出来。
    try {
        vR := BuildMenu(vGroups, vCtx)
        m := vR["menu"]
        vTop := m.GetCount()
        vIds := vR["idmap"].Count
        Log("  [OK]   菜单渲染  顶层分组=" vGroups.Length
            . " 顶层项数=" vTop " 可点条目=" vIds)
        if (vTop <= 0) {
            Log("  [FAIL] 菜单渲染  顶层项数为 0")
            vFail++
        }
        m.Destroy()
    } catch as e {
        Log("  [FAIL] 菜单渲染: " e.Message "  (line " e.Line ") extra=" (e.HasProp("Extra") ? e.Extra : ""))
        vFail++
    }

    ; ---- 6b. ★ 本轮核心断言：同名项在同一个菜单里共存 ----
    ;   背景：AHK v2 的 Menu.Add 同菜单内重名 = 静默替换（另一组内容整块消失）。
    ;   这里直接构造一个含两条同名项 + 两条同名子菜单的菜单，数项数。
    ;   ★ 项数构成（探针 4 实测 state\p4.ahk）：
    ;     2 同名叶条目 + 1 分隔线 + 2 同名子菜单 = 5
    ;     pos0.id=1 / pos1.id=2（不同 ⇒ 共存）；子菜单位 GetIdAt = -1
    try {
        vT := NativeMenu()
        vT.AddItem("打开 PowerShell")
        vT.AddItem("打开 PowerShell")
        vT.AddSeparator()
        vA := NativeMenu()
        vA.AddItem("用浏览器打开")
        vT.AddSubMenu("图片", vA)
        vB := NativeMenu()
        vB.AddItem("用浏览器打开")
        vT.AddSubMenu("图片", vB)

        vCnt := vT.GetCount()          ; 预期 5
        vId0 := vT.GetIdAt(0)
        vId1 := vT.GetIdAt(1)
        vId3 := vT.GetIdAt(3)          ; 子菜单位 = -1
        Log("  [..]   重名断言  项数=" vCnt " (预期5) | 项0.id=" vId0
            . " 项1.id=" vId1 " (预期不同) | 项3.id=" vId3 " (子菜单位=-1)")

        vOk := (vCnt = 5) && (vId0 > 0) && (vId1 > 0) && (vId0 != vId1) && (vId3 = -1)
        if (vOk)
            Log("  [OK]   重名断言  同名项共存 ✓ 命令ID与显示文本分离")
        else {
            Log("  [FAIL] 重名断言  未达预期")
            vFail++
        }
        vT.Destroy()
    } catch as e {
        Log("  [FAIL] 重名断言: " e.Message)
        vFail++
    }

    ; ---- 6c. ★ 平铺断言：真实配置里第一层出现重名条目 ----
    ;   配置有两处「打开 PowerShell」（浏览器组 / 终端组）。
    ;   平铺后二者同处根菜单 —— 用 GetMenuStringW 逐项读回文本，数同名。
    try {
        vR2 := BuildMenu(vGroups, vCtx)
        vM2 := vR2["menu"]
        vNames := Map()          ; 文本 -> 出现次数
        Loop vM2.GetCount() {
            vP := A_Index - 1
            buf := Buffer(512, 0)
            DllCall("GetMenuStringW", "Ptr", vM2.Handle, "UInt", vP
                , "Ptr", buf, "Int", 256, "UInt", 0x0400)
            vTxt := StrGet(buf, "UTF-16")
            if (vTxt = "")
                continue
            vNames[vTxt] := (vNames.Has(vTxt) ? vNames[vTxt] : 0) + 1
        }
        vDup := ""
        vDupCnt := 0
        for k, c in vNames {
            if (c > 1) {
                vDup .= "[" k "×" c "] "
                vDupCnt++
            }
        }
        Log("  [..]   平铺断言  根菜单项数=" vM2.GetCount() " 唯一文本=" vNames.Count
            . " 重名组=" vDupCnt (vDup != "" ? "  " vDup : ""))
        ; ★ 层级断言：通用菜单组有「工具/系统/常用」三个 level-1 标题，
        ;   它们必须变成真子菜单（而不是被摊平成叶条目）
        vSubCnt := vM2.CountSubMenus()
        Log("  [..]   层级断言  根菜单子菜单数=" vSubCnt " (always 组有3个标题，预期>=3)")
        if (vSubCnt >= 3)
            Log("  [OK]   层级断言  组内缩进建出真子菜单 ✓")
        else {
            Log("  [FAIL] 层级断言  子菜单数不足，层级被摊平")
            vFail++
        }
        if (vDupCnt > 0)
            Log("  [OK]   平铺断言  根菜单内同名项共存 ✓（同一层，AHK 原生会吃掉）")
        else
            Log("  [..]   平铺断言  本轮上下文中无同名条目（非失败，取决于前台进程）")
        vM2.Destroy()
    } catch as e {
        Log("  [FAIL] 平铺断言: " e.Message)
        vFail++
    }

    ; ---- 6d. ★ 自动执行（匹配块）断言 ----
    try {
        vAM := AutoMatcher(RVE_ROOT)
        vN := vAM.Load(true)
        Log("  [..]   自动执行  插件数=" vN
            . " enabled=" (vAM.Enabled ? "1" : "0") " minLen=" vAM.MinLen)

        ; 挑几个代表性用例
        vCases := [
            ["SF1234567890",       "顺丰快递查询"],
            ["https://a.com",      "打开网址"],
            ["choco install 7zip", "执行 Chocolatey 命令"],
            ["普通文字啊啊啊",       ""]
        ]
        vCaseFail := 0
        for c in vCases {
            vR := vAM.Match(c[1])
            vGot := vR["hit"] ? vR["plugin"]["name"] : ""
            if (vGot != c[2]) {
                Log("  [FAIL] 自动执行  用例 [" c[1] "] 期望=" c[2] " 实得=" vGot)
                vCaseFail++
            }
        }
        if (vCaseFail = 0)
            Log("  [OK]   自动执行  " vCases.Length " 个用例全通过 ✓")
        else
            vFail += vCaseFail

        ; 启动登记文件
        if FileExist(vAM.GetRegistryPath())
            Log("  [OK]   启动登记  " vAM.GetRegistryPath())
        else {
            Log("  [FAIL] 启动登记  文件未生成")
            vFail++
        }
    } catch as e {
        Log("  [FAIL] 自动执行: " e.Message)
        vFail++
    }

    ; ---- 7. 执行器存在性 ----
    if FileExist(RVE_EXEC)
        Log("  [OK]   执行器    " RVE_EXEC)
    else if FileExist(A_ScriptDir "\RunEverExec.ahk")
        Log("  [WARN] 执行器    无 exe，回退源码 " A_ScriptDir "\RunEverExec.ahk")
    else
        Log("  [FAIL] 执行器    未找到: " RVE_EXEC)

    ; ---- 8. 点击链路模拟（可选）----
    ;   走真正的 OnItemClick：写任务文件 → 起执行器（不等待）
    if (clickTarget != "") {
        vPick := ""
        vWant := StrLower(clickTarget)
        for g in vGroups {
            for it in g["items"] {
                if it["type"] = "group"
                    continue
                if (InStr(StrLower(it["title"]), vWant) || InStr(StrLower(it["target"]), vWant)) {
                    vPick := it
                    break 2
                }
            }
        }
        if (vPick = "") {
            Log("  [FAIL] 点击模拟  未找到条目: " clickTarget)
            vFail++
        } else {
            Log("  [..]   点击模拟  " vPick["title"] " | " vPick["type"] " " vPick["target"])
            try {
                OnItemClick(vPick, vCtx)
                Log("  [OK]   点击模拟  已写任务文件并启动执行器")
            } catch as e {
                Log("  [FAIL] 点击模拟: " e.Message)
                vFail++
            }
        }
    }

    if (vFail = 0)
        Log("=== SELFTEST 通过 ===")
    else
        Log("=== SELFTEST 失败 " vFail " 项 ===")
}

; ============================================================
; 托盘
; ============================================================
SetupTray() {
    global
    A_IconTip := "RunEver"
    tm := A_TrayMenu
    tm.Delete()
    tm.Add("显示菜单", (*) => ShowMenu())
    tm.Add()
    tm.Add("设置…", (*) => OpenSettings())               ; ★ 设置管理 UI
    tm.Add()
    tm.Add("刷新路径缓存", (*) => TriggerCacheBuild())
    tm.Add("刷新插件", (*) => RefreshPlugins())          ; ★ 自动执行：重扫插件
    tm.Add("打开配置", (*) => Run(RVE_CONF))
    tm.Add("打开自动执行配置", (*) => Run(RVE_CONF_AUTO)) ; ★ 自动执行配置
    tm.Add("打开插件目录", (*) => Run(RVE_FN_DIR))        ; ★ 插件目录
    tm.Add("打开插件登记", (*) => Run(g_autoMatcher != ""
        ? g_autoMatcher.GetRegistryPath() : RVE_STATE "\plugins.json"))   ; ★ UI 数据源
    tm.Add("打开日志", (*) => Run(RVE_LOG))
    tm.Add()
    tm.Add("退出", (*) => ExitApp())
    tm.Default := "显示菜单"
}

; ---- 设置管理 UI（托盘「设置…」入口）----
;   ★ 复用同一个实例：再次打开只激活窗口，不重建
;   ★ 主程序不可用时给出明确提示（不静默失败）
OpenSettings() {
    global
    if (g_autoMatcher = "") {
        MsgBox("自动执行模块未加载，设置界面不可用。`n`n请检查 "
            . RVE_FN_DIR " 目录与 auto.conf。", "RunEver", "Icon!")
        return
    }
    try {
        if (g_settings = "") {
            g_settings := SettingsUI(RVE_ROOT, g_autoMatcher)
            Log("设置UI: 已创建实例")
        }
        g_settings.Show()
    } catch as e {
        Log("设置UI 打开失败: " e.Message)
        MsgBox("设置界面打开失败：`n" e.Message, "RunEver", "Icon!")
    }
}

; ---- 刷新插件（托盘菜单用；也是未来设置 UI 的入口）----
RefreshPlugins() {
    global
    if (g_autoMatcher = "") {
        MsgBox("自动执行未启用（插件加载失败）", "RunEver", "Icon!")
        return
    }
    try {
        vN := g_autoMatcher.Refresh()
        Log("手动刷新插件：插件数 = " vN)
        MsgBox("已重扫插件目录：`n`n共 " vN " 个插件`n" g_autoMatcher.GetRegistryPath()
            , "RunEver", "Iconi")
    } catch as e {
        Log("刷新插件失败: " e.Message)
        MsgBox("刷新插件失败：`n" e.Message, "RunEver", "Icon!")
    }
}

; ============================================================
; 热键处理：采集 → ★自动执行 → 判定 → 渲染
; ============================================================
ShowMenu(*) {
    global
    t1 := A_TickCount

    ; 1. 采集
    ctx := Collector()
    ctx.Collect()
    g_lastCtx := ctx

    ; ★ 1.5 自动执行（匹配块）：选中文本命中插件 → 直接执行，跳过弹菜单
    ;   设计（2026-10-03 用户定案）：
    ;     · 命中 → 派发后直接返回（不弹菜单）
    ;     · 未命中 / 执行失败 → 继续走下面的正常链路（零回归）
    if (g_autoMatcher != "") {
        try {
            if (TryAutoExec(ctx)) {
                Log("自动执行命中 → 已派发，跳过弹菜单")
                return
            }
        } catch as e {
            Log("自动执行异常（回落弹菜单）: " e.Message)
        }
    }

    ; 2. 判定
    r := g_matcher.Match(ctx)
    groups := r["groups"]
    st := r["stats"]

    t2 := A_TickCount
    Log("采集+判定 耗时 " (t2 - t1) " ms | always=" st["always"]
        . " dynamic=" st["dynamic"] " fallback=" st["fallback"]
        . " | proc=" ctx.Win["proc"] " text=" (StrLen(ctx.Text)) "B files=" ctx.Files.Length)

    ; 3. 渲染（Win32 原生菜单）
    t3 := A_TickCount
    vR := BuildMenu(groups, ctx)
    m := vR["menu"]
    t3b := A_TickCount
    Log("  菜单构建 耗时 " (t3b - t3) " ms | 项数=" vR["count"]
        . " 图标(真=" vR["iconHit"] " 兜底=" vR["iconFallback"] ")")

    ; 4. 显示 —— TPM_RETURNCMD 直接拿被点项 ID
    ;   ★ 注意：这里的耗时含「你盯着菜单看」的等待时间，不是渲染耗时。
    ;     真正的渲染耗时看上一行「菜单构建」。
    selId := m.Show()
    t4 := A_TickCount
    Log("  菜单交互 耗时 " (t4 - t3b) " ms（含用户等待）| 选中ID=" selId)

    ; 5. 派发 —— id → item 映射表查回条目本体（★ 零字符串剥离）
    if (selId > 0) {
        item := vR["idmap"][selId]
        if (item) {
            OnItemClick(item, ctx)
        } else {
            Log("警告: 选中 ID " selId " 无对应条目")
        }
    }

    ; 6. 回收 GDI（★ 不回收会泄漏 HBITMAP）
    m.Destroy()
}

; ============================================================
; ★ 自动执行（匹配块）· 2026-10-03 定案
;
; 流程：选中文本 → 匹配插件 → 产出目标 → 复用执行器派发
; 返回：true = 已派发（调用方应跳过弹菜单）；false = 未命中/放弃（走正常链路）
;
; ★ 失败兜底三层（用户定案）：
;   ① 无规则命中 → 返回 false → 弹菜单
;   ② 脚本返回空串 → 返回 false → 弹菜单
;   ③ 派发失败 → 返回 false → 弹菜单
; ============================================================
TryAutoExec(ctx) {
    global

    vText := Trim(ctx.Text, " `t`r`n")
    if (vText = "")
        return false

    ; ---- 匹配 ----
    r := g_autoMatcher.Match(vText)
    if (!r["hit"])
        return false

    p := r["plugin"]
    vOut := r["out"]
    vTarget := r["target"]

    ; ---- 形态 B：无 Tpl ⇒ 由插件脚本产出目标 ----
    if (vTarget = "") {
        vTarget := CallPluginRun(p["path"], vText)
        if (vTarget = "") {
            Log("  自动执行: 插件 " p["name"] " 未产出目标 → 回落弹菜单")
            return false
        }
    }

    Log("  自动执行命中: " p["name"] " (prio=" p["prio"] ") out=" vOut
        . " target=" (StrLen(vTarget) > 60 ? SubStr(vTarget, 1, 60) "..." : vTarget))

    ; ---- 派发：复用 OnItemClick 的任务文件契约 ----
    ;   ★ show 是插件级开关（RVE_FN_Show / auto.conf 的 showByDefault）
    ;     0/1 → 任务文件写 true/false；执行器 DoCmd 据此决定 Run / RunWait
    item := Map(
        "title",  p["name"],
        "type",   vOut,
        "target", vTarget,
        "show",   (r["show"] ? true : false)
    )
    try {
        OnItemClick(item, ctx)
        return true
    } catch as e {
        Log("  自动执行派发失败（回落弹菜单）: " e.Message)
        return false
    }
}

; ---- 调插件脚本的 Run(text) 产出目标（形态 B）----
;   ★ 实现方式：起独立进程执行插件脚本的 Run()，通过临时文件回传结果。
;     不用 #Include（那是加载期行为，热键路径不能重载脚本）。
;
;   契约：
;     AutoMatcher 生成一个临时驱动脚本 → 它 #Include 插件 →
;     调 Run(text) → 结果写入输出文件 → 主程序读回
;
;   返回：目标字符串；失败返回 ""（调用方回落弹菜单）
CallPluginRun(pluginPath, text) {
    global

    vTmpDir := RVE_STATE "\autotmp"
    try DirCreate(vTmpDir)

    vStamp   := A_TickCount "_" Random(10000, 99999)
    vInFile  := vTmpDir "\in_"  vStamp ".txt"
    vOutFile := vTmpDir "\out_" vStamp ".txt"
    vDrvFile := vTmpDir "\drv_" vStamp ".ahk"

    ; 写输入（选中文本，独立文件免转义）
    try {
        FileAppend(text, vInFile, "UTF-8")
    } catch {
        return ""
    }

    ; 生成驱动脚本
    ;   ★ 注意：#Include 的路径要带双引号包裹（防空格路径断裂）
    ;   ★ 不能用 IsSet(Run) 判函数存在（v2 里对函数无效）——
    ;     改用 try 包住调用，缺函数时抛错被吃掉 ⇒ 结果为空 ⇒ 回落弹菜单
    Q := Chr(34)
    vDrv := ""
        . "#Requires AutoHotkey v2.0" "`n"
        . "#SingleInstance Off" "`n"
        . "#NoTrayIcon" "`n"
        . "vIn  := A_Args[1]" "`n"
        . "vOut := A_Args[2]" "`n"
        . "vTxt := " Q Q "`n"
        . "try vTxt := FileRead(vIn, " Q "UTF-8" Q ")" "`n"
        . "vResult := " Q Q "`n"
        . "try {" "`n"
        . "  #Include " Q pluginPath Q "`n"
        . "  vResult := Run(vTxt)" "`n"
        . "} catch {" "`n"
        . "  vResult := " Q Q "`n"
        . "}" "`n"
        . "try {" "`n"
        . "  if FileExist(vOut)" "`n"
        . "    FileDelete(vOut)" "`n"
        . "  FileAppend(vResult, vOut, " Q "UTF-8" Q ")" "`n"
        . "} catch {" "`n"
        . "}" "`n"
        . "ExitApp(0)" "`n"

    try {
        FileAppend(vDrv, vDrvFile, "UTF-8")
    } catch {
        CleanAutoTmp(vInFile, vOutFile, vDrvFile)
        return ""
    }

    ; 执行驱动脚本（带超时保护）
    vAhk := A_AhkPath
    if (vAhk = "")
        vAhk := "C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe"

    vResult := ""
    try {
        vPid2 := 0
        Run('"' vAhk '" "' vDrvFile '" "' vInFile '" "' vOutFile '"', , , &vPid2)
        ; 有界等待：最多 3 秒（脚本简单，通常 <300ms）
        vWait := 0
        while (vWait < 3000) {
            if (FileExist(vOutFile))
                break
            Sleep 30
            vWait += 30
        }
        if (FileExist(vOutFile))
            vResult := Trim(FileRead(vOutFile, "UTF-8"), " `t`r`n")
    } catch {
        vResult := ""
    }

    CleanAutoTmp(vInFile, vOutFile, vDrvFile)
    return vResult
}

CleanAutoTmp(a, b, c) {
    try FileDelete(a)
    try FileDelete(b)
    try FileDelete(c)
}

; ============================================================
; 构建菜单（Win32 原生 · 第一层平铺 · 2026-10-03 用户定案）
;
; 平铺规则（用户 2026-10-03 定案）：
;   ★ 不显示分组名 —— 命中的动态组 + always 组的条目直接铺在第一层
;   ★ 组与组之间插分隔线
;   ★ 组内按缩进（item["level"]）建真子菜单
;
; 为什么平铺（本轮的真实动机）：
;   配置里两条「打开 PowerShell」（浏览器组 / 终端组）、两条「用浏览器打开」
;   （PDF 组 / 图片组）——平铺后它们同处一个根菜单。
;   AHK 的 Menu.Add 下这会是静默替换（整组消失）；
;   Win32 的 AppendMenuW 把 ID 与文本分成两个参数 ⇒ 同名项共存。
;
; 外部契约：返回 Map("menu", "idmap", "count", "iconHit")
; ============================================================
BuildMenu(groups, ctx) {
    global
    m := NativeMenu()
    idmap := Map()          ; cmdId -> item（★ 派发靠它，不靠剥字符串）
    vIconReal := 0          ; 真图标（程序路径解析成功）
    vIconFallback := 0      ; 兜底图标

    if (groups.Length = 0) {
        vId := m.AddItem("(无可用条目)")
        idmap[vId] := 0
        return Map("menu", m, "idmap", idmap, "count", m.GetCount()
            , "iconHit", 0, "iconFallback", 0)
    }

    vFirst := true
    for g in groups {
        items := g["items"]
        if (items.Length = 0)
            continue

        ; ★ 组与组之间插分隔线（第一个组之前不插）
        if (!vFirst)
            m.AddSeparator()
        vFirst := false

        ; ★ 组内：按缩进层级建真子菜单
        ;   语义：item["level"] = **挂载深度**（不是入栈深度）
        ;     level 1 → 直接挂根菜单（第一层平铺）
        ;     level 2 → 挂到最近的那个 level 1 子菜单里
        ;   所以栈深度 = level，条件是 stack.Length > level 才收栈。
        ;   ★ 曾写 `> lv-1`：AddSubMenu 后栈深 2，被这条又弹回 1，
        ;     level 2 的条目全挂到了根菜单上 ⇒「工具/系统/常用」下面全空。
        stack := [m]

        for it in items {
            lv := it["level"]
            if (lv < 1)
                lv := 1

            ; 收栈：让栈深 = lv（栈底恒为根菜单）
            while (stack.Length > lv)
                stack.Pop()
            ; 补栈：配置跳级时用当前栈顶补齐，避免无处可挂
            while (stack.Length < lv)
                stack.Push(stack[stack.Length])

            if (it["type"] = "group") {
                ; 分组标题 → 建真子菜单，挂到当前栈顶，然后入栈
                sub := NativeMenu()
                vTitle := it["title"]
                if (it["hotkey"] != "")
                    vTitle .= "`t[" it["hotkey"] "]"
                stack[stack.Length].AddSubMenu(vTitle, sub)
                stack.Push(sub)
            } else {
                ; 叶条目 → 加到当前栈顶
                vId := AddItemToMenu(stack[stack.Length], it, ctx)
                if (vId > 0)
                    idmap[vId] := it
                ; 图标计数由 AddItemToMenu 写入全局（避开引用出参的坑）
                vIconReal     += g_iconReal
                vIconFallback += g_iconFallback
                g_iconReal := 0
                g_iconFallback := 0
            }
        }
    }

    return Map("menu", m, "idmap", idmap, "count", m.GetCount()
        , "iconHit", vIconReal, "iconFallback", vIconFallback)
}

; ---- 把单个条目加到菜单（含子菜单嵌套）----
; 返回本条目分配的 cmdId（不可点项返回 0）
; 图标计数写入全局 g_iconReal / g_iconFallback（调用方读后清零）
AddItemToMenu(menu, item, ctx) {
    global

    title := item["title"]
    if (item["hotkey"] != "")
        title .= "`t[" item["hotkey"] "]"     ; ★ Tab：Win32 把 Tab 后文字右对齐

    ; group 类型 = 纯标题，不可点击，不给图标
    if (item["type"] = "group") {
        menu.AddTitle(title)
        return 0
    }

    vId := menu.AddItem(title)

    ; ★ 图标：先按缓存路径取程序图标；取不到则挂兜底图标
    ;   （用户定案 2026-10-02：配置里只有程序名 ⇒ 解析不到时不能空着）
    iconPath := ResolveIconPath(item)
    if (iconPath != "" && menu.SetIconById(vId, iconPath))
        g_iconReal += 1
    else if (menu.SetIconById(vId, ""))       ; 空源 ⇒ 内部走 MakeFallbackIcon
        g_iconFallback += 1
    return vId
}

; ---- 点击处理：甩给执行器 ----
OnItemClick(item, ctx) {
    global
    Log("点击: " item["title"] " | " item["type"] " " item["target"])

    ; 组装任务
    task := Map(
        "id",       A_TickCount "_" Random(1000, 9999),
        "type",     item["type"],
        "target",   item["target"],
        "title",    item["title"],
        "text",     ctx.Text,
        "files",    ctx.Files,
        "proc",     ctx.Win["proc"],
        "winTitle", ctx.Win["title"],
        "winClass", ctx.Win["class"],
        "ts",       A_Now
    )
    ; ★ 可选键：仅在来源显式提供时才写入任务文件
    ;   flag  = 条目级（menus.conf 的 `| action show xxx`）
    ;   show  = 插件级（自动执行插件的 RVE_FN_Show）
    ;   两者缺省则不写 ⇒ 执行器按"隐藏"处理（存量行为）
    if (item.Has("flag") && item["flag"] != "")
        task["flag"] := item["flag"]
    if (item.Has("show"))
        task["show"] := item["show"]

    ; 写任务文件
    try DirCreate(RVE_TASKS)
    taskFile := RVE_TASKS "\" task["id"] ".task"
    WriteTaskFile(taskFile, task)

    ; 启动执行器（★ 不等待、不回推）
    ; 优先用编译好的 exe；开发态（src 目录无 exe）回退到同目录 .ahk 源码
    vExecCmd := ""
    if FileExist(RVE_EXEC) {
        vExecCmd := '"' RVE_EXEC '" "' taskFile '"'
    } else {
        vExecAhk := A_ScriptDir "\RunEverExec.ahk"
        if FileExist(vExecAhk)
            vExecCmd := '"' A_AhkPath '" "' vExecAhk '" "' taskFile '"'
    }

    if (vExecCmd = "") {
        Log("执行器不存在: " RVE_EXEC)
        MsgBox("执行器不存在：`n" RVE_EXEC, "RunEver", "Icon!")
        return
    }

    try {
        Run(vExecCmd)
        Log("执行器已启动: " taskFile)
    } catch as e {
        Log("执行器启动失败: " e.Message)
    }
}

; ---- 任务文件写入（极简 JSON，避免依赖 JSON 库）----
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
    ; ★ 可选键：只有调用方显式设定才写（缺键 = 存量语义 = 隐藏）
    if (task.Has("flag"))
        s .= '  "flag": ' JsonStr(task["flag"]) ",`n"
    if (task.Has("show"))
        s .= '  "show": ' (task["show"] ? "true" : "false") ",`n"
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

; ---- 解析条目的图标路径 ----
ResolveIconPath(item) {
    global
    ; group 类型无图标
    if (item["type"] = "group")
        return ""

    ; run 类型：取程序全路径
    if (item["type"] = "run") {
        tgt := item["target"]
        parts := StrSplit(tgt, " ")
        if (parts.Length < 1)
            return ""
        prog := Trim(parts[1], '"')
        ; 若已含路径，直接用
        if (InStr(prog, "\") || InStr(prog, "/")) {
            return FileExist(prog) ? prog : ""
        }
        ; 查缓存
        if (g_cache != "") {
            p := g_cache.Get(prog)
            if (p != "")
                return p
        }
        return ""
    }
    return ""
}

; ============================================================
; 路径缓存构建（后台，不阻塞主线程太久）
; ============================================================
TriggerCacheBuild() {
    global
    Log("开始构建路径缓存...")
    progs := g_parser.CollectPrograms()
    Log("待解析程序名数: " progs.Count)

    t1 := A_TickCount
    try {
        r := g_cache.Build(progs, true)
        t2 := A_TickCount
        Log("缓存构建完成，耗时 " (t2 - t1) " ms | 成功=" r["resolved"]
            . " 失败=" r["failed"] " 跳过=" r["skipped"])
        if (r["failed"] > 0) {
            s := ""
            for f in r["failedList"]
                s .= f " "
            Log("  失败清单: " s)
        }
    } catch as e {
        Log("缓存构建失败: " e.Message)
    }
}

; ============================================================
; 假死执行器清理（★ 不阻塞：只看文件时间戳与进程存活）
; ============================================================
CleanZombies() {
    global
    try {
        if !DirExist(RVE_TASKS)
            return
        n := 0
        Loop Files, RVE_TASKS "\*.task" {
            ; 任务文件超过 5 分钟未被处理 → 视为残留
            age := DateDiff(A_Now, A_LoopFileTimeModified, "Minutes")
            if (age > 5) {
                try FileDelete(A_LoopFileFullPath)
                n++
            }
        }
        ; 清理孤立的执行器进程（启动超过 10 分钟视为假死）
        try {
            vNow := A_Now
            for p in ComObjGet("winmgmts:").ExecQuery(
                "SELECT ProcessId, CreationDate FROM Win32_Process WHERE Name='RunEverExec.exe'") {
                vPid := p.ProcessId
                vBorn := SubStr(p.CreationDate, 1, 14)   ; YYYYMMDDHHMISS
                vAgeMin := DateDiff(vNow, vBorn, "Minutes")
                if (vAgeMin > 10) {
                    try {
                        ProcessClose(vPid)
                        Log("  终止假死执行器 PID=" vPid " 已运行 " vAgeMin " 分钟")
                    }
                } else {
                    Log("  执行器存活中 PID=" vPid " 已运行 " vAgeMin " 分钟")
                }
            }
        } catch {
        }
        if (n > 0)
            Log("清理残留任务文件: " n " 个")
    } catch as e {
        Log("僵尸清理异常: " e.Message)
    }
}

; ============================================================
; 日志
; ============================================================
; 目录准备（幂等）：建目录 + 首次启动自举配置模板
;   必须在任何日志 / 配置写入之前调用。
EnsureLayout() {
    global
    ; ★ 含插件目录 RVE_FN_DIR：首次启动就要存在，否则用户不知道往哪放插件
    for d in [RVE_ROOT, RVE_CONF_DIR, RVE_CACHE_DIR, RVE_STATE, RVE_TASKS, RVE_FN_DIR]
        try DirCreate(d)
    ; 首次启动自举：配置缺失就把「编译进 exe 的模板」释放出来。
    ; ★ 用 FileInstall 而不是把模板写成代码里的字符串常量：
    ;   (a) 模板与 config\menus.conf 逐字节一致，不经手转义；
    ;   (b) 配置里有 %clip% / %file% / "%clip%" —— 在 AHK 字符串字面量里
    ;       %name% 会被当变量引用解析，"" 连续引号也会把字面量拆断
    ;       （实测报 "Missing space or operator before this"）。
    ;   Source 是编译期字面量（相对 src\ 解析），Dest 可以是变量。
    if (!FileExist(RVE_CONF) && DirExist(RVE_CONF_DIR)) {
        try {
            FileInstall("..\config\menus.conf", RVE_CONF, 1)
            g_bootstrapped := FileExist(RVE_CONF) ? true : false
        }
    }
    ; ★ 自动执行配置自举（同机制）
    if (!FileExist(RVE_CONF_AUTO) && DirExist(RVE_CONF_DIR)) {
        try FileInstall("..\config\auto.conf", RVE_CONF_AUTO, 1)
    }
}

LogInit() {
    global
    EnsureLayout()
    try FileDelete(RVE_LOG)
    try FileAppend("=== RunEver log ===" "`n", RVE_LOG, "UTF-8")
}

Log(s) {
    global
    try FileAppend(FormatTime(A_Now, "HH:mm:ss") " " s "`n", RVE_LOG, "UTF-8")
}
