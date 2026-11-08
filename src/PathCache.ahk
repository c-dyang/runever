#Requires AutoHotkey v2.0
; ============================================================
; RunEver 路径缓存模块
; 解析"程序名 → 全路径"，落盘缓存供菜单图标 + 执行器使用
;
; ★ 三级解析（2026-10-02 定案）—— 按 Windows 自身的语义分层
;   ① App Paths 注册表：最"有意注册"的规范入口（Start→Run 用的就是它）
;   ② PATH 语义（原生 AHK，不调子进程）：
;        System32 → Windows 目录 → %PATH% 各目录按序
;        ★ System32 先于 PATH 是 Windows 的真实解析顺序，
;          天然解决「SysWOW64 32位抢答」与「内部 libexec 抢答」
;   ③ es.exe 搜索 + 加权：仅兜底不在 PATH 的应用（7zFM/Everything/mspaint/chrome）
;
; ★ 为什么不能只用 es 的「修改时间倒序」：
;   该启发式会把刚解压/刚生成的临时副本当成正式路径。实测踩坑：
;     7z.exe         → session TempSave\autoextract\...\7z.exe（临时副本）
;     everything.exe → %TEMP%\runany-src\Everything\Everything.exe（解包副本）
;     autohotkey64   → WorkBuddy\claw-tools\...（项目内副本）
;     calc/taskmgr   → SysWOW64（32位，应为 System32）
;   → 前三个 ① ② 都查不到时靠 ③ 的加权修；后两个由 ② 直接修（根本不用加权）
;
; EV 语法（2026-10-02 官方文档 + 实测确认）:
;   es.exe -n <N> -sort date-modified-descending -export-txt <file> wfn:<name>
;   - wfn:  = Match Whole Filename（精确匹配整个文件名）
;   - -export-txt = 原生写文件（cmd 重定向会丢编码 → 0 字节）
;   - ★ 查询串不能加引号：AHK RunWait 传引号会让 es 收到字面量引号 → 不匹配
; ============================================================

class PathCache {

    ; 默认 es.exe 路径（EXP 记录的可靠路径）
    static EsPath := "C:\Users\" . A_UserName . "\.workbuddy\binaries\es.exe"

    ; ---- 路径优先级规则（2026-10-02 实测重定）----
    ; ★ 核心教训：es 的 date-modified-descending 是错的启发式！
    ;   "最新修改"恰恰会选中刚解压出来的临时副本 / 会话工作副本，
    ;   而不是正规安装位。实测踩坑：
    ;     7z.exe        → session TempSave\autoextract\...\7z.exe（临时）
    ;     everything.exe→ %TEMP%\runany-src\Everything\Everything.exe（解包）
    ;     autohotkey64  → WorkBuddy\claw-tools\...\AutoHotkey64.exe（项目内副本）
    ;     calc/taskmgr  → SysWOW64（32位，应为 System32）
    ;   正解 = 显式加权：正规安装位加分、临时/副本位重罚。

    ; 硬排除：这些位置的文件永远不作为结果（系统备份/缓存）
    static ExcludeFrag := ["\WinSxS\", "\servicing\", "\Prefetch\", "\SystemResources\"]

    ; 高权前缀（命中即 +100）：正规安装位
    static PreferPrefix := [
        "C:\Program Files\",
        "C:\Program Files (x86)\",
        "C:\Windows\System32\",
        "C:\Windows\"
    ]

    ; 降权片段（命中即 -100）：临时/会话工作副本，绝不该当正式路径
    ; ★ 只针对「明确错误的位置」。不要往里塞「某个工具恰好也有同名 exe」的目录
    ;   —— 那类问题归第 ② 级 PATH 语义解决，塞进这里就是过拟合（实测教训）。
    static PenalizeFrag := [
        "\AppData\Local\Temp\",
        "\Windows\Temp\",
        "\TempSave\",
        "\.dsh\sessions\",
        "\SysWOW64\",
        "\chocolatey\tools\",          ; choco 内部解包工具，非用户的 7z
        "\chocolatey\lib\"             ; choco 包载荷（版本化目录）
    ]

    __New(cacheFile) {
        this.File := cacheFile
        this.Map := Map()          ; 小写程序名 -> 全路径
        this.Load()
    }

    ; ---- 读缓存 ----
    Load() {
        this.Map := Map()
        if !FileExist(this.File)
            return
        try {
            vText := FileRead(this.File, "UTF-8")
        } catch {
            return
        }
        vText := StrReplace(vText, "`r`n", "`n")
        for vLine in StrSplit(vText, "`n") {
            vLine := Trim(vLine, " `t")
            if (vLine = "" || SubStr(vLine, 1, 1) = "#")
                continue
            vPos := InStr(vLine, "=")
            if (vPos < 2)
                continue
            k := Trim(SubStr(vLine, 1, vPos - 1), " `t")
            v := Trim(SubStr(vLine, vPos + 1), " `t")
            if (k = "" || v = "")          ; ★ 跳过空值，不 break（EXP 血泪）
                continue
            this.Map[StrLower(k)] := v
        }
    }

    ; ---- 写缓存（原子：临时文件 + 替换）----
    Save() {
        tmp := this.File ".tmp"
        try FileDelete(tmp)               ; EXP: FileDelete 对不存在文件抛错
        s := "# RunEver 路径缓存 —— 机器生成，勿手改`n"
        s .= "# 格式: 程序名小写=全路径`n"
        keys := []
        for k, v in this.Map
            keys.Push(k)
        ; 排序输出，便于人工查看/diff
        keys := this.SortArr(keys)
        for k in keys {
            s .= k "=" this.Map[k] "`n"
        }
        FileAppend(s, tmp, "UTF-8")
        ; 替换
        try FileDelete(this.File)
        FileMove(tmp, this.File)
    }

    SortArr(a) {
        ; ★ AHK v2 的 `>` 对字符串会走数值比较，非数字字符串抛
        ;   "Expected a Number but got a String" → 字符串比较必须用 StrCompare()
        ; 简单冒泡（条目量小，几十个）
        n := a.Length
        Loop n - 1 {
            i := A_Index
            Loop n - i {
                j := A_Index
                if (StrCompare(a[j], a[j + 1]) > 0) {
                    t := a[j]
                    a[j] := a[j + 1]
                    a[j + 1] := t
                }
            }
        }
        return a
    }

    ; ---- 取全路径（无则返回空）----
    Get(progName) {
        k := StrLower(progName)
        if this.Map.Has(k) {
            p := this.Map[k]
            ; 校验文件仍存在（否则视为失效）
            if FileExist(p)
                return p
            ; 失效 → 移除，走重新解析
            this.Map.Delete(k)
            return ""
        }
        return ""
    }

    ; ---- 路径权重（越大越优先）----
    ; 规则：高权前缀 +100（按列表序位微调，靠前更优先）
    ;       降权片段 -100
    ;       同权重时保持 es 的时间倒序（稳定选择）
    PathWeight(p) {
        w := 0
        ; 高权前缀：列表越靠前，加分越多（System32 > Program Files）
        n := PathCache.PreferPrefix.Length
        for i, frag in PathCache.PreferPrefix {
            if InStr(p, frag) {
                w += 100 + (n - i)
                break                     ; 只取第一个命中的前缀
            }
        }
        ; 降权片段
        for frag in PathCache.PenalizeFrag {
            if InStr(p, frag) {
                w -= 100
                break
            }
        }
        return w
    }

    ; ---- 单个程序名解析（调 es.exe）----
    ; ★ 实测（2026-10-02）：AHK RunWait 里给参数加引号会导致 es.exe 收到带引号
    ;   字面量 → 查询不匹配 → 导出文件 0 字节。正解 = 不加引号。
    ;   前提：es.exe 路径、输出路径、查询串均不含空格（当前满足）。
    ;   若将来路径含空格，需改用 RunWait 的引号转义规则重测。
    ; ============================================================
    ; 三级解析入口
    ; ============================================================
    ; 返回路径字符串（解析不到 → ""）
    ResolveOne(progName) {
        vR := this.ResolveOneEx(progName)
        return vR["path"]
    }

    ; 带来源标注（供验收/排障）：Map("path" -> 路径, "tier" -> "apppaths|path|everything|none")
    ResolveOneEx(progName) {
        ; ① App Paths 注册表
        vP := this.ResolveViaAppPaths(progName)
        if (vP != "")
            return Map("path", vP, "tier", "apppaths")

        ; ② PATH 语义
        vP := this.ResolveViaPath(progName)
        if (vP != "")
            return Map("path", vP, "tier", "path")

        ; ③ es 搜索 + 加权兜底
        vP := this.ResolveViaEverything(progName)
        if (vP != "")
            return Map("path", vP, "tier", "everything")

        return Map("path", "", "tier", "none")
    }

    ; ---- ① App Paths 注册表 ----
    ; Windows 的 Start→Run 对话框解析程序名就用这个键。
    ; 值：默认值 = exe 全路径；另有 Path 值 = 工作目录（本模块不需要）。
    ; ★ 64 位 AHK 默认读 64 位视图，32 位应用注册在 WOW6432Node，需各查一遍。
    ResolveViaAppPaths(progName) {
        vSub := "Microsoft\Windows\CurrentVersion\App Paths\" progName
        vRoots := [
            "HKCU\SOFTWARE\" vSub,
            "HKLM\SOFTWARE\" vSub,
            "HKLM\SOFTWARE\WOW6432Node\" vSub
        ]
        for vKey in vRoots {
            try {
                vVal := RegRead(vKey)
                if (vVal != "" && FileExist(vVal))
                    return vVal
            } catch {
                ; 键不存在 → RegRead 抛错，正常情况，继续
            }
        }
        return ""
    }

    ; ---- ② PATH 语义（原生实现，不调子进程）----
    ; 复刻 Windows 的解析顺序：System32 → Windows 目录 → %PATH% 各目录按序
    ; ★ System32 必须先于 PATH：这是 Windows 自身顺序，也是修正
    ;   「SysWOW64 抢答」的关键（System32 是 64 位正主）。
    ResolveViaPath(progName) {
        ; 系统目录（★ 顺序不可调换）
        for vDir in [A_WinDir "\System32", A_WinDir] {
            vFull := vDir "\" progName
            if FileExist(vFull)
                return vFull
        }

        ; %PATH% 各目录，按声明顺序
        vPath := ""
        try vPath := EnvGet("PATH")
        if (vPath = "")
            return ""

        for vDir in StrSplit(vPath, ";") {
            vDir := Trim(vDir, " `t`"")
            if (vDir = "")
                continue
            ; 未展开的变量条目（如 %SOMEVAR%\bin）跳过，避免误判
            if InStr(vDir, "%")
                continue
            vFull := RTrim(vDir, "\") "\" progName
            if FileExist(vFull)
                return vFull
        }
        return ""
    }

    ; ---- ③ es 搜索 + 加权兜底 ----
    ; 只处理「不在 PATH、也不在 App Paths」的应用（7zFM / Everything / mspaint / chrome 等）
    ResolveViaEverything(progName) {
        es := PathCache.EsPath
        if !FileExist(es)
            throw Error("es.exe 不存在: " es)

        outFile := A_Temp "\rv_es_" A_TickCount ".txt"
        try FileDelete(outFile)

        cmd := es " -n 30 -sort date-modified-descending -export-txt " outFile " wfn:" progName
        RunWait(cmd, , "Hide")

        ; 收集全部候选（保持 es 的时间倒序），过滤硬排除项
        cands := []
        if FileExist(outFile) {
            try {
                vTxt := FileRead(outFile, "UTF-8")
                for vLine in StrSplit(vTxt, "`n") {
                    vLine := Trim(vLine, " `t`r")
                    if (vLine = "")
                        continue
                    if this.IsExcluded(vLine)
                        continue
                    cands.Push(vLine)
                }
            } catch {
            }
            try FileDelete(outFile)
        }

        if (cands.Length = 0)
            return ""

        ; ★ 加权择优：只在权重「更优」时替换，同权重保持原时间倒序（稳定）
        best := cands[1]
        bestW := this.PathWeight(best)
        for i, c in cands {
            if (i = 1)
                continue
            w := this.PathWeight(c)
            if (w > bestW) {
                best := c
                bestW := w
            }
        }
        return best
    }

    ; ---- 判断是否在排除路径 ----
    IsExcluded(p) {
        for frag in PathCache.ExcludeFrag {
            if InStr(p, frag)
                return true
        }
        return false
    }

    ; ---- 批量构建/更新缓存 ----
    ; progs: Map(小写名 -> true)
    ; force: true = 全部重查；false = 增量（只补缺失）
    ; 返回: Map("resolved" -> n, "failed" -> n, "skipped" -> n, "failedList" -> [])
    Build(progs, force := false) {
        resolved := 0
        failed := 0
        skipped := 0
        failedList := []

        for name, _ in progs {
            if (!force) {
                existing := this.Get(name)
                if (existing != "") {
                    skipped++
                    continue
                }
            }
            p := ""
            try {
                p := this.ResolveOne(name)
            } catch as e {
                p := ""
            }
            if (p != "") {
                this.Map[name] := p
                resolved++
            } else {
                failed++
                failedList.Push(name)
            }
        }
        this.Save()
        return Map(
            "resolved", resolved,
            "failed", failed,
            "skipped", skipped,
            "failedList", failedList
        )
    }

    ; ---- 调试输出 ----
    Dump() {
        s := "缓存条目数: " this.Map.Count "`n"
        for k, v in this.Map
            s .= "  " k " = " v "`n"
        return s
    }
}
