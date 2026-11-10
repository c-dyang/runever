#Requires AutoHotkey v2.0
#SingleInstance Off

; ★★ 关闭「未赋值局部变量」警告（2026-10-03 实测定案，别删）
;   原因：该警告是【模态弹窗】，在常驻程序里会直接卡死主程序
;   （实测：ReadMeta 里一处形参笔误 vPath/path ⇒ 弹框 ⇒ RunEver 假死，
;     用户只看到「按了热键没反应」）。
;   RunEver 是 7×24 常驻程序，不能因一行笔误弹框；
;   笔误风险由测试脚本（state\amtest.ahk）+ 自检断言兜住。
;   覆盖范围仅本文件 —— 插件脚本仍保留警告，便于插件作者发现笔误。
;
;   ★ 参数名以官方文档为准（实测踩坑，别凭记忆写）：
;     正确 = VarUnset ；曾写 UnsetLocal ⇒ 报 "Parameter #1 invalid" 直接启动失败
;     文档：https://www.autohotkey.com/docs/v2/lib/_Warn.htm
;   警告类型全集：VarUnset / LocalSameAsGlobal / Unreachable / All
;   投递方式：MsgBox（默认，会弹框卡死）/ StdOut / OutputDebug / Off
#Warn VarUnset, Off

; ============================================================
; RunEver 自动执行模块（匹配块 + 功能块加载器）
;
; 职责：
;   ① 扫描 config\functions\*.ahk，读【元数据】建索引（不做 IO 反复扫盘）
;   ② 用选中文本逐个匹配 → 命中唯一则产出目标
;   ③ 产出交给主程序派发（复用 RunEverExec 任务文件契约）
;   ④ ★ 启动登记：把插件清单落盘 state\plugins.json，供后期设置 UI 读取
;
; 设计依据（2026-10-03 定案）：
;   · 双形态插件：纯模板型（只写元数据）/ 带逻辑型（元数据 + Run(text)）
;   · 元数据只读扫描，匹配器【不执行】插件脚本（安全 + 快）
;   · 主显示启动模型完全不动（menus.conf / MenuNative / Matcher 零改动）
;
; 元数据规范（写在 .ahk 头部，AHK v2 语法）：
;   global RVE_FN_Name  := "顺丰快递"        ; 必需 · 显示名
;   global RVE_FN_Match := "^SF\d{10,}$"     ; 必需 · 匹配式（正则 或 SF* 前缀糖）
;   global RVE_FN_Whole := true              ; 可选 · 整段匹配（默认 true）
;   global RVE_FN_Prio  := 100               ; 可选 · 优先级（大者先，默认 100）
;   global RVE_FN_Out   := "url"             ; 必需 · 产出形态，必须是执行器的
;                                            ;   动作类型之一（见下方 OutTypes）
;   global RVE_FN_Tpl   := "https://...{0}"  ; 形态A必需 · 模板，{0}=选中文本
;   global RVE_FN_Icon  := "chrome.exe"      ; 可选 · 图标源
;   global RVE_FN_Menu  := false             ; 可选 · L3：true = 作为菜单项追加进核心菜单
;                                            ;   （always 段末尾；且不自动派发）
;   global RVE_FN_Arg   := "text"            ; 可选 · 传参：text/file/clip
;   global RVE_FN_Show  := true              ; 可选 · 显示执行窗口（命令行工具用）
;                                            ;   不写 = 继承 auto.conf 的 showByDefault（默认隐藏）
;                                             ;   ★ 看进度/输出的命令（choco/ffmpeg/git）必须写 true
;   global RVE_FN_Enabled := true            ; 可选 · 启用开关（设置 UI 勾选用）
;
; ★ 三层权限（2026-11-10 定案，声明即定档，核心不仲裁）：
;   L1 接管  Out="intercept"：命中后核心不建菜单/不派发/不追加，插件自己弹菜单；
;            Run(text) 返回非空 = 接管成功；空 / 异常 / 超时 ⇒ 回落核心菜单
;   L2 派发  Out ∈ run/open/url/action/send/keys：命中后直接派发、跳过弹菜单
;   L3 菜单  Menu=true：不自动派发，作为菜单项追加进 always 段末尾
;   ★ 互斥：intercept + Menu 冲突 ⇒ intercept 优先（Menu 忽略）
;
; 用法：
;   am := AutoMatcher(RVE_ROOT)
;   am.Load()                        ; 扫描 + 建索引 + 启动登记
;   r  := am.Match(text)             ; → Map("hit", bool, "plugin", ..., "out","", "target","")
;   am.Dump()                        ; 调试输出
; ============================================================

class AutoMatcher {

    ; ★★ 合法的产出形态 = 执行器 RunEverExec.DoAction 支持的动作类型
    ;    （2026-10-03 实测定案）
    ;      run    启动程序（首 token 走路径缓存）
    ;      open   打开文件/文件夹
    ;      url    打开网址
    ;      action 走 cmd /c 执行命令   ← ★ 不是 "cmd"！
    ;      send   发送文本
    ;      keys   发按键
    ;      ★ 2026-11-10 新增 intercept = L1 接管：插件自己弹菜单，核心跳过
    ;        （它不是执行器动作类型，只由 RunEver.TryAutoExec 消费）
    ;    ★ 曾把 cmd 型插件写成 out="cmd"，执行器报「未知动作类型: cmd」
    ;      ⇒ 产出为空 → 用户看到失败小菜单。这里加白名单，加载期就拦住。
    static OutTypes := ["run", "open", "url", "action", "send", "keys", "intercept"]

    __New(root) {
        this.Root        := root
        this.Dir         := root "\config\functions"
        this.ConfPath    := root "\config\auto.conf"
        this.StateDir    := root "\state"
        this.RegistryPath := root "\state\plugins.json"   ; ★ 启动登记文件
        this.Plugins     := []      ; 插件数组（按 Prio 降序）
        this.ByFile      := Map()   ; 文件名 → 插件（查重用）
        this.BadPlugins  := []      ; ★ 元数据非法的插件（供 UI/日志暴露）
        this.Enabled     := true
        this.MinLen      := 4
        this.CacheSec    := 300
        ; ★ 显示执行窗口的全局默认（插件未声明 RVE_FN_Show 时生效）
        ;   0 = 隐藏（默认，与存量行为一致）  1 = 显示
        this.ShowByDefault := 0
        this.WaitWhenShown := 1
        this.LastScan    := 0
        this.Loaded      := false
    }

    ; ============================================================
    ; 加载：读配置 → 扫插件 → 建索引 → 启动登记
    ; ============================================================
    Load(forceRescan := false) {
        this.ReadConf()

        ; 缓存判定：首次 / 强制 / 过期 → 重扫
        vNow := A_TickCount
        vNeedScan := forceRescan || !this.Loaded
            || (this.CacheSec > 0 && (vNow - this.LastScan) > this.CacheSec * 1000)

        if (vNeedScan)
            this.Scan()
        return this.Plugins.Length
    }

    ; ---- 读 auto.conf ----
    ReadConf() {
        this.Enabled  := true
        this.MinLen   := 4
        this.CacheSec := 300
        this.ShowByDefault := 0
        this.WaitWhenShown := 1
        if !FileExist(this.ConfPath)
            return
        try {
            vText := FileRead(this.ConfPath, "UTF-8")
            for vLine in StrSplit(vText, "`n") {
                vLine := Trim(vLine, " `t`r")
                if (vLine = "" || SubStr(vLine, 1, 1) = "#" || SubStr(vLine, 1, 1) = ";")
                    continue
                vPos := InStr(vLine, "=")
                if (vPos = 0)
                    continue
                vKey := StrLower(Trim(SubStr(vLine, 1, vPos - 1), " `t"))
                vVal := Trim(SubStr(vLine, vPos + 1), " `t")
                switch vKey {
                    case "enabled":        this.Enabled  := (vVal != "0")
                    case "minlen":         this.MinLen   := Integer(vVal)
                    case "cacheseconds":   this.CacheSec := Integer(vVal)
                    case "showbydefault":  this.ShowByDefault := (vVal = "0" || StrLower(vVal) = "false") ? 0 : 1
                    case "waitwhenshown":  this.WaitWhenShown := (vVal = "0" || StrLower(vVal) = "false") ? 0 : 1
                }
            }
        } catch {
            ; 配置读失败 → 用默认值（不阻断启动）
        }
    }

    ; ============================================================
    ; 扫描插件目录，读元数据建索引
    ;   ★ 只读头部元数据行，不执行脚本 → 安全且快
    ; ============================================================
    Scan() {
        this.Plugins := []
        this.ByFile  := Map()
        this.BadPlugins := []
        this.LastScan := A_TickCount
        this.Loaded  := true

        if !DirExist(this.Dir)
            return 0

        Loop Files, this.Dir "\*.ahk", "F" {
            vPath := A_LoopFileFullPath
            vMeta := this.ReadMeta(vPath)
            if (vMeta = "")
                continue                       ; 元数据不完整 → 跳过（不是错误）
            vMeta["file"] := A_LoopFileName
            vMeta["path"] := vPath
            this.Plugins.Push(vMeta)
            this.ByFile[A_LoopFileName] := vMeta
        }

        ; 按优先级降序（大者先）；同优先级按文件名升序（稳定可预期）
        this.SortPlugins()

        ; ★ 启动登记
        this.WriteRegistry()

        return this.Plugins.Length
    }

    ; ---- 排序：Prio 降序 → 文件名升序 ----
    SortPlugins() {
        vArr := this.Plugins
        vN := vArr.Length
        if (vN <= 1)
            return
        ; 简单插入排序（插件数量级小，不引复杂算法）
        Loop vN - 1 {
            vI := A_Index + 1
            vKey := vArr[vI]
            vJ := vI - 1
            while (vJ >= 1 && this.Less(vKey, vArr[vJ])) {
                vArr[vJ + 1] := vArr[vJ]
                vJ -= 1
            }
            vArr[vJ + 1] := vKey
        }
    }

    ; a 是否应排在 b 前
    Less(a, b) {
        if (a["prio"] != b["prio"])
            return a["prio"] > b["prio"]       ; 优先级大者先
        return StrCompare(a["file"], b["file"]) < 0   ; 同优先级按文件名
    }

    ; ============================================================
    ; 读单个插件的元数据
    ;   返回 Map，缺必需字段返回 ""（跳过该插件）
    ;
    ; ★★ 两个必须避开的坑（2026-10-03 实测）：
    ;   ① 不能用 `Loop Read, path` —— 它按 ANSI 读，UTF-8 中文变乱码，
    ;      且解析异常会被 catch 吞掉导致插件「神秘消失」。
    ;      正解：FileRead(path, "UTF-8") 读全文，再 StrSplit 切行。
    ;   ② 值捕获必须用【贪婪】(.+)：配 \s*$ 的非贪婪 (.+?) 只捕到首字符。
    ; ============================================================
    ReadMeta(path) {
        vName := "", vMatch := "", vTpl := "", vOut := ""
        vWhole := true, vPrio := 100, vIcon := "", vMenu := false, vArg := "text"
        ; ★ Show 用 -1 表示"未声明"（区别于显式 false）→ 由 auto.conf 的
        ;   showByDefault 决定。三态：-1 继承 / 0 隐藏 / 1 显示。
        vShow := -1
        vEnabled := true

        ; 读全文（UTF-8），只解析头部 80 行
        vText := ""
        try {
            vText := FileRead(path, "UTF-8")
        } catch {
            return ""
        }

        vLineNo := 0
        for vLine in StrSplit(vText, "`n", "`r") {
            vLineNo += 1
            if (vLineNo > 80)
                break

            ; 匹配  global RVE_FN_Xxx := "值"  或 := 值
            ;   \s* 已能吃掉行首缩进；贪婪 (.+) 保证值完整
            if !RegExMatch(vLine
                , "i)^\s*global\s+RVE_FN_(\w+)\s*:=\s*(.+)$", &m)
                continue
            vKey := StrLower(m[1])
            vRaw := m[2]

            ; ★★ 关键：剥掉行内注释 —— 贪婪 (.+) 会把 `; 说明文字` 一起吃进来
            ;   实测翻车：`:= false          ; 允许包含（...）` 使 whole 变成 true，
            ;   进而给已有 ^ 的正则再加锚 ⇒ ^^xxx$ ⇒ 永不匹配。
            ;   规则：在【引号外】遇到 ; 或 # 即视为注释起点。
            vRaw := this.StripInlineComment(vRaw)
            vRaw := this.Unquote(vRaw)

            switch vKey {
                case "name":  vName  := vRaw
                case "match": vMatch := vRaw
                case "tpl":   vTpl   := vRaw
                case "out":   vOut   := StrLower(vRaw)
                case "icon":  vIcon  := vRaw
                case "arg":   vArg   := StrLower(vRaw)
                case "whole": vWhole := (StrLower(vRaw) != "false" && vRaw != "0")
                case "menu":  vMenu  := (StrLower(vRaw) = "true" || vRaw = "1")
                case "show":
                    ; 三态：显式 true/1 → 显示；显式 false/0 → 隐藏
                    vShow := (StrLower(vRaw) = "true" || vRaw = "1") ? 1 : 0
                case "enabled":
                    vEnabled := !(StrLower(vRaw) = "false" || vRaw = "0")
                case "prio":
                    vPrio := (IsInteger(vRaw) ? Integer(vRaw) : 100)
            }
        }

        ; 必需字段校验
        if (vName = "" || vMatch = "" || vOut = "")
            return ""

        ; ★ 产出形态白名单校验（加载期就拦住，别等执行器报错）
        vOutOk := false
        for vT in AutoMatcher.OutTypes {
            if (vOut = vT) {
                vOutOk := true
                break
            }
        }
        if (!vOutOk) {
            ; 记进 BadPlugins，便于 UI / 日志暴露问题（不静默丢弃）
            ;   ★ 用形参 path（不是 vPath —— 曾写错触发 #Warn 弹窗卡死）
            this.BadPlugins.Push(Map("file", path
                , "reason", "未知产出形态 out=" vOut "（合法值: "
                    . this.JoinArr(AutoMatcher.OutTypes) "）"))
            return ""
        }

        ; 形态 A 必须有 Tpl（形态 B 由脚本 Run() 产出）
        ;   这里不强制：无 Tpl 时视为形态 B，运行时若脚本缺 Run() 会失败 → 兜底
        return Map(
            "name",    vName,
            "match",   vMatch,
            "whole",   vWhole,
            "prio",    vPrio,
            "out",     vOut,
            "tpl",     vTpl,
            "icon",    vIcon,
            "menu",    vMenu,
            "arg",     vArg,
            "show",    vShow,       ; -1 继承 / 0 隐藏 / 1 显示
            "enabled", vEnabled     ; 插件启用开关
        )
    }

    ; ---- 剥掉行内注释（引号外的 ; 或 # 起）----
    ;   例：  "值"  ; 说明      →  "值"
    ;         false  ; 说明    →  false
    ;   ★ 引号内的 # / ; 不视为注释（如正则里的 "#" 或 URL 的 ;）
    StripInlineComment(s) {
        vOut := ""
        vInQuote := false
        Loop Parse s {
            vC := A_LoopField
            if (vC = Chr(34)) {
                vInQuote := !vInQuote
                vOut .= vC
                continue
            }
            if (!vInQuote && (vC = ";" || vC = "#"))
                break
            vOut .= vC
        }
        return Trim(vOut, " `t`r")
    }

    ; 去引号（支持 "xxx" 和 'xxx'）
    Unquote(s) {
        s := Trim(s, " `t")
        if (StrLen(s) >= 2) {
            vFirst := SubStr(s, 1, 1)
            vLast  := SubStr(s, -1)
            if ((vFirst = Chr(34) && vLast = Chr(34))
                || (vFirst = "'" && vLast = "'"))
                return SubStr(s, 2, StrLen(s) - 2)
        }
        return s
    }

    ; ============================================================
    ; ★ 启动登记（供后期设置 UI 读取）
    ;   落盘 state\plugins.json：插件清单 + 元数据全量
    ;   ★ 这是 UI 接口的数据源，格式要稳定
    ; ============================================================
    WriteRegistry() {
        try {
            if !DirExist(this.StateDir)
                DirCreate(this.StateDir)

            ; ★ AHK v2 没有单引号字符串（那是 v1 习惯）—— 统一用双引号
            ;   JSON 里的引号用 Chr(34) 拼，避免转义地狱
            Q := Chr(34)
            vS := "{" "`n"
            vS .= "  " Q "generatedAt" Q ": " Q FormatTime(A_Now, "yyyy-MM-dd HH:mm:ss") Q "," "`n"
            vS .= "  " Q "pluginDir" Q ": " Q this.EscJson(this.Dir) Q "," "`n"
            vS .= "  " Q "enabled" Q ": " (this.Enabled ? "true" : "false") "," "`n"
            vS .= "  " Q "minLen" Q ": " this.MinLen "," "`n"
            vS .= "  " Q "cacheSeconds" Q ": " this.CacheSec "," "`n"
            vS .= "  " Q "count" Q ": " this.Plugins.Length "," "`n"
            vS .= "  " Q "plugins" Q ": [" "`n"

            vFirst := true
            for p in this.Plugins {
                if (!vFirst)
                    vS .= "," "`n"
                vFirst := false
                vS .= "    {"
                vS .= Q "file"  Q ": " Q this.EscJson(p["file"])  Q ", "
                vS .= Q "name"  Q ": " Q this.EscJson(p["name"])  Q ", "
                vS .= Q "match" Q ": " Q this.EscJson(p["match"]) Q ", "
                vS .= Q "whole" Q ": " (p["whole"] ? "true" : "false") ", "
                vS .= Q "prio"  Q ": " p["prio"] ", "
                vS .= Q "out"   Q ": " Q this.EscJson(p["out"])   Q ", "
                vS .= Q "tpl"   Q ": " Q this.EscJson(p["tpl"])   Q ", "
                vS .= Q "icon"  Q ": " Q this.EscJson(p["icon"])  Q ", "
                vS .= Q "menu"  Q ": " (p["menu"] ? "true" : "false") ", "
                vS .= Q "arg"   Q ": " Q this.EscJson(p["arg"])   Q ", "
                ; ★ show 三态落盘：-1 继承 / 0 隐藏 / 1 显示（UI 要区分"继承"）
                vS .= Q "show"  Q ": " p["show"] ", "
                vS .= Q "enabled" Q ": " (p["enabled"] ? "true" : "false") ", "
                vS .= Q "path"  Q ": " Q this.EscJson(p["path"]) Q
                vS .= "}"
            }
            vS .= "`n  ]," "`n"
            ; ★ 非法插件清单（供 UI 暴露问题，不静默）
            vS .= "  " Q "badPlugins" Q ": [" "`n"
            vBFirst := true
            for b in this.BadPlugins {
                if (!vBFirst)
                    vS .= "," "`n"
                vBFirst := false
                vS .= "    {" Q "file" Q ": " Q this.EscJson(b["file"]) Q ", "
                    . Q "reason" Q ": " Q this.EscJson(b["reason"]) Q "}"
            }
            vS .= "`n  ]" "`n" "}" "`n"

            ; ★ 覆盖写（重建登记）
            try FileDelete(this.RegistryPath)
            FileAppend(vS, this.RegistryPath, "UTF-8")
            return true
        } catch {
            return false
        }
    }

    ; ============================================================
    ; 匹配：选中文本 → 命中的插件（唯一）
    ;   返回 Map("hit", bool, "plugin", Map|"", "out", "", "target", "")
    ; ============================================================
    Match(text) {
        vNone := Map("hit", false, "plugin", "", "out", "", "target", "", "show", 0)

        if (!this.Enabled)
            return vNone
        vText := Trim(text, " `t`r`n")
        if (StrLen(vText) < this.MinLen)
            return vNone

        for p in this.Plugins {
            ; ★ 停用的插件直接跳过（UI 里勾掉的）
            if (!p["enabled"])
                continue
            if !this.MatchOne(p, vText)
                continue

            ; 命中 → 产出目标
            vTarget := ""
            if (p["tpl"] != "")
                vTarget := this.Fill(p["tpl"], vText)     ; 形态 A：模板代入
            ; 形态 B：目标由插件脚本产出（主程序不执行脚本，此处只标记）
            ;   实际产出在 Dispatch 阶段交给执行器（见 AutoMatcher.BuildTask）

            ; ★ 显示执行窗口：插件三态（-1 继承 / 0 隐藏 / 1 显示）
            vShow := (p["show"] >= 0) ? p["show"] : this.ShowByDefault

            return Map("hit", true, "plugin", p, "out", p["out"]
                     , "target", vTarget, "show", vShow)
        }
        return vNone
    }

    ; ---- 单条匹配 ----
    MatchOne(p, text) {
        vPat := this.Compile(p["match"], p["whole"])
        if (vPat = "")
            return false
        try {
            ; ★ 统一用 RegExMatch —— 不区分两种模式：
            ;   whole=true  时 Compile 已补锚（整段约束）
            ;   whole=false 时原样正则（可否出现在任意位置）
            ;   曾对 whole=false 走 InStr 分支，导致正则完全失效
            return RegExMatch(text, vPat) ? true : false
        } catch {
            return false
        }
    }

    ; ---- 匹配式编译：正则 或 前缀糖 ----
    ; ★★ 关键：不要无脑加 ^ $ —— 原式可能已自带锚点，
    ;    双重锚（^^xxx$$）会导致永不匹配（2026-10-03 实测翻车）。
    Compile(pat, whole) {
        pat := Trim(pat, " `t")
        if (pat = "")
            return ""

        ; 前缀糖：以 * 结尾 ⇒ ^前缀
        if (SubStr(pat, -1) = "*" && !RegExMatch(pat, "[\^$\[\]\(\)\|\\]")) {
            vPrefix := SubStr(pat, 1, StrLen(pat) - 1)
            return "^" . this.RegEsc(vPrefix) . (whole ? ".*$" : "")
        }

        ; 已是正则
        if (!whole)
            return pat                       ; 包含匹配：原样

        ; 整段匹配：只在缺锚点时补
        vOut := pat
        if (SubStr(vOut, 1, 1) != "^")
            vOut := "^" . vOut
        if (SubStr(vOut, -1) != "$")
            vOut := vOut . "$"
        return vOut
    }

    ; 正则转义（前缀糖用）
    RegEsc(s) {
        static vSpecial := "\\.^$|?*+()[]{}"
        vOut := ""
        Loop Parse s {
            vC := A_LoopField
            vOut .= (InStr(vSpecial, vC) ? "\" . vC : vC)
        }
        return vOut
    }

    ; ---- 模板代入：{0} = 选中文本 ----
    Fill(tpl, text) {
        vOut := StrReplace(tpl, "{0}", text)
        return vOut
    }

    ; ---- 数组转逗号串（错误提示用）----
    JoinArr(a) {
        vS := ""
        for v in a
            vS .= (vS = "" ? "" : "/") v
        return vS
    }

    ; ---- JSON 字符串转义 ----
    EscJson(s) {
        s := StrReplace(s, "\", "\\")
        s := StrReplace(s, Chr(34), "\" Chr(34))
        s := StrReplace(s, "`r", "\r")
        s := StrReplace(s, "`n", "\n")
        s := StrReplace(s, "`t", "\t")
        return s
    }

    ; ============================================================
    ; ★ 设置 UI 接口（预留，本轮不实现 UI）
    ;   后期设置页直接调这些方法，不用改本类
    ; ============================================================

    ; 取插件清单（UI 列表用）
    GetPlugins() {
        return this.Plugins
    }

    ; 取单个插件
    GetPlugin(fileName) {
        return this.ByFile.Has(fileName) ? this.ByFile[fileName] : ""
    }

    ; 启停某插件（UI 开关用）—— 改内存 + 写回插件文件元数据
    ;   写回 RVE_FN_Enabled := true/false（原地替换该行，保留其余内容与注释）
    SetPluginEnabled(fileName, enabled) {
        if !this.ByFile.Has(fileName)
            return false
        this.ByFile[fileName]["enabled"] := !!enabled
        return this.WriteMetaFlag(this.ByFile[fileName]["path"]
            , "Enabled", enabled ? "true" : "false")
    }

    ; 设置「显示执行窗口」（UI 开关用）—— 写回 RVE_FN_Show
    ;   val: -1 继承 / 0 隐藏 / 1 显示
    SetPluginShow(fileName, val) {
        if !this.ByFile.Has(fileName)
            return false
        this.ByFile[fileName]["show"] := val
        ; -1 = 继承 → 写 false 不够表达"继承"，故写回时用 0/1 明确落值
        vStr := (val = 1) ? "true" : "false"
        return this.WriteMetaFlag(this.ByFile[fileName]["path"], "Show", vStr)
    }

    ; ---- 元数据行原地替换（UI 写回的唯一落盘口径）----
    ;   规则：
    ;     ① 命中 `global RVE_FN_<Key> := ...` → 整行替换，保留行尾注释
    ;     ② 未命中 → 追加一行到元数据块尾（第一个非元数据行之前）
    ;   写法：先写 .tmp 再 FileMove 覆盖（原子写，防半写损坏）
    WriteMetaFlag(path, key, valStr) {
        try {
            if !FileExist(path)
                return false
            vText := FileRead(path, "UTF-8")
            vEol  := InStr(vText, "`r`n") ? "`r`n" : "`n"
            vLines := StrSplit(vText, "`n", "`r")

            vPat  := "i)^\s*global\s+RVE_FN_" key "\s*:="
            vNew  := 'global RVE_FN_' key ' := ' valStr
            vHit  := false
            vIns  := 0        ; 最后一个元数据行的行号（1-based）；0 = 一个都没有
            for i, vLine in vLines {
                isTarget := RegExMatch(vLine, vPat)
                isMeta   := isTarget
                    || RegExMatch(vLine, "i)^\s*global\s+RVE_FN_\w+\s*:=")
                if (isTarget) {
                    ; 保留行尾注释：取该行里第一个引号外的 ; 
                    vCm := this.TailComment(vLine)
                    ; ★ StrSplit 返回的是 1-based 数组，for 的 i 也是 1-based
                    ;   —— 曾写 vLines[i-1] 越界，替换写到了前一行、
                    ;   目标行纹丝不动（实测表现为"新旧两行并存"）
                    vLines[i] := vNew . vCm
                    vHit := true
                }
                ; ★ 必须在两个分支【之外】更新 vIns —— 曾写在 else-if 里，
                ;   导致"命中替换的行"不被计入元数据块尾，追加时插错位置
                ;   （实测：新行插到已存在的同名行之前，产生重复定义）
                if (isMeta)
                    vIns := i
            }
            if (!vHit) {
                if (vIns = 0) {
                    ; 文件里一个元数据行都没有 → 插到 #Requires 行之后
                    vIns := 1
                    for i, vLine in vLines {
                        if RegExMatch(vLine, "i)^\s*#(Requires|SingleInstance|Include)")
                            vIns := i
                    }
                }
                ; ★ InsertAt 是"插到该索引之前"，所以要用 vIns+1（最后一个元数据行的下一行）
                vLines.InsertAt(vIns + 1, vNew)
            }

            vOut := ""
            for i, vLine in vLines {
                vOut .= vLine
                if (i < vLines.Length)
                    vOut .= vEol
            }
            ; ---- 原子写 ----
            vTmp := path ".tmp"
            try FileDelete(vTmp)
            FileAppend(vOut, vTmp, "UTF-8")
            FileMove(vTmp, path, 1)
            return true
        } catch {
            return false
        }
    }

    ; 取行尾注释（引号外的第一个 ; 起，含前导空格）
    TailComment(line) {
        vInQ := false
        Loop Parse line {
            c := A_LoopField
            if (c = '"')
                vInQ := !vInQ
            else if (!vInQ && c = ";")
                return SubStr(line, A_Index - 1)
        }
        return ""
    }

    ; 强制重扫（UI「刷新插件」按钮用）
    Refresh() {
        return this.Load(true)
    }

    ; 登记文件路径（UI 读它做展示）
    GetRegistryPath() {
        return this.RegistryPath
    }

    ; ============================================================
    ; ★ L3 菜单追加（2026-11-10 定案）：RVE_FN_Menu := true 的插件
    ;
    ; 位置：always 段末尾（插在最后一个 always 组之后；不进 dynamic/process 槽位，
    ;   不碰选中类槽位互斥）。没有 always 组 ⇒ 追加到列表末尾。
    ; 条目：标题 = 插件名；无快捷键；动作 = Out；目标 = Tpl（{0} = 弹出时选中文本，无则空）。
    ; 点击：与核心条目走同一条 OnItemClick（任务文件 → 执行器）。
    ; ★ 单独成组：组间分隔线让插件产出与核心条目可分；组名不进菜单（BuildMenu 平铺）。
    ; ★ 互斥：intercept 优先 —— Out="intercept" 且 Menu=true 的插件不进菜单。
    ; 返回追加的条目数（0 = 没有菜单插件）。
    ; ============================================================
    AppendMenuItems(groups, text) {
        if (!IsObject(groups))
            return 0

        ; 插件清单可能尚未加载（无选中文本时 TryAutoExec 提前返回）——补一次
        try this.Load(false)

        vText := Trim(text, " `t`r`n")
        vItems := []
        for p in this.Plugins {
            if (!p["enabled"] || !p["menu"])
                continue
            if (p["out"] = "intercept")          ; ★ 互斥：intercept 优先，Menu 忽略
                continue
            vTarget := (p["tpl"] != "") ? StrReplace(p["tpl"], "{0}", vText) : ""
            vItems.Push(Map(
                "title",  p["name"],
                "hotkey", "",
                "type",   p["out"],
                "target", vTarget,
                "level",  1
            ))
        }
        if (vItems.Length = 0)
            return 0

        vGrp := Map("name", "插件", "type", "always", "kw", [], "items", vItems, "line", 0)

        vIdx := 0
        for i, g in groups {
            if (g["type"] = "always")
                vIdx := i
        }
        if (vIdx > 0)
            groups.InsertAt(vIdx + 1, vGrp)
        else
            groups.Push(vGrp)

        return vItems.Length
    }

    ; ---- 调试输出（对照 AHK `Dump`）—— 设置页/自检用 ----
    Dump() {
        vS := "自动执行: enabled=" (this.Enabled ? "1" : "0")
            . " minLen=" this.MinLen " cache=" this.CacheSec "s"
            . " showByDefault=" this.ShowByDefault
            . " 插件数=" this.Plugins.Length "`n"
        for p in this.Plugins {
            vS .= "  [" p["prio"] "] " p["name"]
                . "  match=" p["match"] " whole=" (p["whole"] ? "1" : "0")
                . " out=" p["out"]
                . (p["tpl"] != "" ? " tpl=" p["tpl"] : " (形态B)")
                . " show=" (p["show"] < 0 ? "继承" : (p["show"] ? "显示" : "隐藏"))
                . (p["enabled"] ? "" : " ★已停用")
                . "`n"
        }
        return vS
    }
}
