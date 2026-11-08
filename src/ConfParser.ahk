#Requires AutoHotkey v2.0
; ============================================================
; RunEver 配置解析模块
; 从 menus.conf 解析出分组 + 条目
;
; 格式:
;   # 注释
;   ! <分组名>  <类型>  [关键词...]
;     <标题>  [_快捷键]  | <动作>  <目标>  [参数...]
;
; 类型: folder / ext / proc / text / always / fallback
; ============================================================

class ConfParser {

    ; 解析结果
    Groups := []          ; 分组数组，每项是 Map

    ; ---- 构造 ----
    __New(path) {
        this.Path := path
        this.Groups := []
        this.Parse()
    }

    ; ---- 主解析 ----
    Parse() {
        if !FileExist(this.Path)
            throw Error("配置文件不存在: " this.Path)

        text := FileRead(this.Path, "UTF-8")
        text := StrReplace(text, "`r`n", "`n")
        text := StrReplace(text, "`r", "`n")

        cur := 0              ; 当前分组索引（0 = 还没有分组）
        lineNo := 0

        for line in StrSplit(text, "`n") {
            lineNo++

            ; --- 空行 ---
            if (Trim(line) = "")
                continue

            trimmed := Trim(line, " `t")

            ; --- 注释（整行 # 或 ;）---
            if (SubStr(trimmed, 1, 1) = "#" || SubStr(trimmed, 1, 1) = ";")
                continue

            ; --- 分组行（! 开头）---
            if (SubStr(trimmed, 1, 1) = "!") {
                cur := this.ParseGroupLine(SubStr(trimmed, 2), lineNo)
                continue
            }

            ; --- 条目行 ---
            if (cur = 0)
                continue            ; 无归属分组，忽略

            this.ParseItemLine(line, cur, lineNo)
        }
    }

    ; ---- 解析分组行: "名称  类型  关键词..." ----
    ParseGroupLine(s, lineNo) {
        s := Trim(s, " `t")
        if (s = "")
            throw Error("第 " lineNo " 行: 分组名为空")

        ; 分隔符：空白（空格或制表符）
        parts := this.SplitWS(s)

        vName := parts.Length >= 1 ? parts[1] : ""
        vType := parts.Length >= 2 ? StrLower(parts[2]) : ""
        kw   := []
        if (parts.Length >= 3) {
            Loop parts.Length - 2 {
                kw.Push(StrLower(parts[A_Index + 2]))
            }
        }

        ; 类型校验
        valid := ["folder", "ext", "proc", "text", "always", "fallback"]
        found := false
        for v in valid {
            if (vType = v) {
                found := true
                break
            }
        }
        if !found
            throw Error("第 " lineNo " 行: 未知分组类型 '" vType "'（可用: folder/ext/proc/text/always/fallback）")

        g := Map(
            "name",   vName,
            "type",   vType,
            "kw",     kw,
            "items",  [],
            "line",   lineNo
        )
        this.Groups.Push(g)
        return this.Groups.Length
    }

    ; ---- 解析条目行 ----
    ParseItemLine(rawLine, groupIdx, lineNo) {
        ; 缩进层级 = 前导空格数 / 2
        leading := 0
        Loop Parse rawLine {
            c := A_LoopField
            if (c = " " || c = "`t")
                leading++
            else
                break
        }
        level := leading // 2
        if (level < 1)
            level := 1

        content := Trim(rawLine, " `t")
        if (content = "")
            return

        ; --- 拆 标题部分 | 动作部分 ---
        pos := InStr(content, "|")
        if (pos = 0) {
            ; 无 | → 纯标题（视作 group 类型）
            titlePart := content
            actionPart := ""
        } else {
            titlePart  := Trim(SubStr(content, 1, pos - 1), " `t")
            actionPart := Trim(SubStr(content, pos + 1), " `t")
        }

        ; --- 解析标题部分的快捷键: "标题 _X" ---
        vTitle  := titlePart
        hotkey := ""
        ; 匹配末尾 " _X"（_ 后单个非空字符）
        if (RegExMatch(titlePart, "^(.*?)\s+_(\S)$", &m)) {
            vTitle  := Trim(m[1], " `t")
            hotkey := m[2]
        }

        ; --- 解析动作部分 ---
        ; ★ 词法（用户 2026-10-03 定案）：
        ;     action  xxx        → 隐藏执行窗口（默认，裸写）
        ;     action hide  xxx   → 隐藏（显式，等价裸写）
        ;     action show  xxx   → 显示执行窗口（看进度/输出）
        ;   首 token = 动作类型；紧随其后的白名单词 hide/show 被吃掉当 flag；
        ;   其余全部归 target（保留原样，含空格）。
        ;   白名单之外的第二 token（如历史的 "action cmd xxx" 里的 cmd）照旧归
        ;   target —— 存量配置行为零变化。
        actionType := "group"
        actionTarget := ""
        actionFlag := ""
        if (actionPart != "") {
            ap := this.SplitWS(actionPart)
            if (ap.Length >= 1)
                actionType := StrLower(ap[1])
            vIdx := 2
            if (ap.Length >= 2) {
                vW := StrLower(ap[2])
                if (vW = "hide" || vW = "show") {
                    actionFlag := vW
                    vIdx := 3
                }
            }
            if (ap.Length >= vIdx) {
                ; 剩余部分作为目标（保留原样，含空格）
                vStart := InStr(actionPart, ap[vIdx])
                actionTarget := SubStr(actionPart, vStart)
            }
        }

        item := Map(
            "title",   vTitle,
            "hotkey",  hotkey,
            "type",    actionType,
            "flag",    actionFlag,   ; "" | "hide" | "show"（仅 action 有意义）
            "target",  actionTarget,
            "level",   level,
            "line",    lineNo
        )
        this.Groups[groupIdx]["items"].Push(item)
    }

    ; ---- 按空白拆分（保留引号内内容不拆）----
    SplitWS(s) {
        out := []
        buf := ""
        inQuote := false
        Loop Parse s {
            c := A_LoopField
            if (c = '"') {
                inQuote := !inQuote
                buf .= c
                continue
            }
            if (!inQuote && (c = " " || c = "`t")) {
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

    ; ---- 收集配置里所有 run 类型的程序名（供 EV 解析）----
    CollectPrograms() {
        names := Map()      ; 小写名 -> true（去重）
        for g in this.Groups {
            for it in g["items"] {
                if (it["type"] != "run")
                    continue
                tgt := it["target"]
                if (tgt = "")
                    continue
                ; 取目标的首个 token 作为程序名
                parts := this.SplitWS(tgt)
                if (parts.Length < 1)
                    continue
                prog := Trim(parts[1], '"')
                if (prog = "")
                    continue
                ; 只处理无路径的（不含 \ 或 /）
                if (InStr(prog, "\") || InStr(prog, "/"))
                    continue
                names[StrLower(prog)] := true
            }
        }
        return names
    }

    ; ---- 调试输出 ----
    Dump() {
        s := "分组数: " this.Groups.Length "`n"
        for g in this.Groups {
            s .= "`n! " g["name"] "  [" g["type"] "]  kw=" this.JoinArr(g["kw"])
            s .= "   (行 " g["line"] ")`n"
            for it in g["items"] {
                indent := ""
                Loop it["level"] - 1
                    indent .= "  "
                s .= "  " indent it["title"]
                if (it["hotkey"] != "")
                    s .= "  _" it["hotkey"]
                s .= "  | " it["type"] "  " it["target"] "`n"
            }
        }
        return s
    }

    JoinArr(a) {
        s := ""
        for v in a
            s .= (s = "" ? "" : ",") v
        return s
    }
}
