#Requires AutoHotkey v2.0
; ============================================================
; RunEver 判定器
; 依据采集到的上下文，从配置中挑出要显示的分组
;
; 合成规则（用户定案 2026-10-02）：
;   显示 = always组 + 命中组（按配置顺序） + fallback组
;   fallback 仅在 folder/ext/proc/text 全未命中时追加
;   ★ 全部堆叠，不去重
; ============================================================

class Matcher {

    __New(parser) {
        this.Parser := parser
    }

    ; ---- 判定，返回要显示的分组数组 ----
    ; ctx: Collector 实例
    ; 返回: { groups: [...], stats: Map }
    Match(ctx) {
        selectGrps := []        ; ★ 选中类动态：folder / ext / text（与 fallback 同槽位）
        procGrps := []          ; ★ 进程类动态：proc
        alwaysGrps := []        ; 固定项目
        fallbackGrps := []      ; 选中类未命中时的补位

        exts := ctx.GetExtensions()
        hasFolder := ctx.HasFolder()
        hasText := (Trim(ctx.Text) != "")
        proc := StrLower(ctx.Win["proc"])

        for g in this.Parser.Groups {
            t := g["type"]
            switch t {
                case "always":
                    alwaysGrps.Push(g)
                case "fallback":
                    fallbackGrps.Push(g)
                case "folder":
                    if (hasFolder)
                        selectGrps.Push(g)
                case "text":
                    if (hasText)
                        selectGrps.Push(g)
                case "ext":
                    if this.MatchExt(g["kw"], exts)
                        selectGrps.Push(g)
                case "proc":
                    if this.MatchProc(g["kw"], proc)
                        procGrps.Push(g)
            }
        }

        ; 合成（★ 2026-10-03 用户定案：三层顺序）
        ;   ① 选中类动态（text / folder / ext）  —— 与 fallback 同槽位，互斥
        ;   ② fallback  —— 选中类全未命中时的同槽位替代内容
        ;   ③ 进程类动态（proc）
        ;   ④ 固定项目（always）
        ;   同槽位语义：Ctrl+C 的结果要么是文件、要么是文本、要么什么都没选，
        ;   所以「选中类命中」与 fallback 天然互斥，二者只出一个。
        result := []

        ; ① 选中类动态（text/folder/ext）
        for g in selectGrps
            result.Push(g)

        ; ② fallback：仅当选中类一个都没命中时补位
        if (selectGrps.Length = 0) {
            for g in fallbackGrps
                result.Push(g)
        }

        ; ③ 进程类动态
        for g in procGrps
            result.Push(g)

        ; ④ 固定项目
        for g in alwaysGrps
            result.Push(g)

        stats := Map(
            "always",   alwaysGrps.Length,
            "dynamic",  selectGrps.Length + procGrps.Length,
            "fallback", (selectGrps.Length = 0 ? fallbackGrps.Length : 0)
        )
        return Map("groups", result, "stats", stats)
    }

    ; ---- 扩展名匹配（任一命中）----
    MatchExt(kwList, exts) {
        if (kwList.Length = 0 || exts.Length = 0)
            return false
        for k in kwList {
            for e in exts {
                if (k = e)
                    return true
            }
        }
        return false
    }

    ; ---- 进程名匹配（任一命中）----
    MatchProc(kwList, proc) {
        if (kwList.Length = 0 || proc = "")
            return false
        ; proc 形如 "chrome.exe"，关键词可能写 "chrome"（不含 .exe）
        procNoExt := proc
        if (SubStr(proc, -4) = ".exe")
            procNoExt := SubStr(proc, 1, StrLen(proc) - 4)

        for k in kwList {
            if (k = proc || k = procNoExt)
                return true
        }
        return false
    }
}
