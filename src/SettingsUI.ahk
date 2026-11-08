#Requires AutoHotkey v2.0
#SingleInstance Off
; ★ 关闭「未赋值变量」警告 —— 该警告是【模态弹窗】，会把校验/常驻进程卡死。
;   参数名以官方文档为准：VarUnset（不是 UnsetLocal，曾写错直接启动失败）。
#Warn VarUnset, Off

; ★ 依赖：本文件用到 ConfParser（菜单项页只读浏览）。
;   主程序里 RunEver.ahk 已 #Include 过，重复 include 由 AHK 自动去重；
;   独立测试脚本加载本文件时靠这行拿到类定义。
#Include ConfParser.ahk
; ============================================================
; RunEver 设置管理 UI
;
; 结构参考 RunAny（AHK v1 的 Gui,66:Add 写法）—— 抄的是信息架构，
; 代码全部按 AHK v2 对象式重写：
;   · Tab 分页 + GroupBox 分组
;   · 改一项存一项（无「保存」按钮，即时落盘）
;   · 插件页 = ListView + 勾选列
;   · -Redraw → 批量填充 → +Redraw（防闪烁）
;   · 不可用项置灰 + 状态列说明原因
;
; 四个 Tab：
;   常规   总开关 / minLen / 缓存 / 显示窗口默认
;   插件   ★ 主体：启用 + 显示窗口两个开关，写回插件文件元数据
;   菜单项 只读浏览 menus.conf（编辑写回留下一轮）
;   关于   路径 / 打开目录 / 重载
;
; ★ 写回纪律：
;   · 元数据行原地替换（保留行尾注释）
;   · 原子写（.tmp → FileMove 覆盖）
;   · 写完调 AutoMatcher.Refresh() 重建索引 + 登记文件
; ============================================================

class SettingsUI {

    ; ---- 构造：拿 root + matcher 引用 ----
    __New(root, matcher) {
        this.Root    := root
        this.AM      := matcher
        this.ConfAuto := root "\config\auto.conf"
        this.ConfMenu := root "\config\menus.conf"
        this.GuiObj  := ""
        this.LV      := ""
        this.Items   := []          ; ListView 行 → 插件（或非法插件）映射
        this.TabCtrl := ""
    }

    ; ============================================================
    ; 打开（已开则激活，不重复建）
    ; ============================================================
    Show() {
        if (this.GuiObj != "" && WinExist("ahk_id " this.GuiObj.Hwnd)) {
            this.GuiObj.Show()
            WinActivate("ahk_id " this.GuiObj.Hwnd)
            return
        }
        this.Build()
        this.GuiObj.Show()
    }

    ; ============================================================
    ; 构建窗口
    ; ============================================================
    Build() {
        g := Gui("+Resize +MinSize760x560", "RunEver 设置")
        g.SetFont("s9", "Microsoft YaHei")
        g.OnEvent("Close", (*) => this.OnClose())
        g.OnEvent("Escape", (*) => this.OnClose())
        this.GuiObj := g

        ; ---- Tab 控件 ----
        tc := g.Add("Tab3", "x10 y10 w740 h520", ["常规", "插件", "菜单项", "关于"])
        this.TabCtrl := tc

        ; 注意：Tab 页控件必须用 tc.Add(...) 的形式（AHK v2 的 Tab3 契约），
        ; 但 v2 里 Tab3 的子控件是普通 Add 后自动归属当前页 —— 用 tc.UseTab
        ; 切换页面。这里明确切换，避免控件挂错页。
        tc.UseTab(1)
        this.BuildTabGeneral(tc)
        tc.UseTab(2)
        this.BuildTabPlugins(tc)
        tc.UseTab(3)
        this.BuildTabMenus(tc)
        tc.UseTab(4)
        this.BuildTabAbout(tc)
        tc.UseTab()

        return g
    }

    ; ------------------------------------------------------------
    ; Tab 1 常规
    ; ------------------------------------------------------------
    BuildTabGeneral(tc) {
        g := this.GuiObj
        Y := "y+15 x30"

        g.Add("GroupBox", "x20 y40 w700 h140", "自动执行（选中文本 → 匹配 → 直接执行）")

        ; 总开关
        vEn := this.AM.Enabled
        this.CbEnabled := g.Add("Checkbox", "x40 y70 vEn " (vEn ? "Checked" : ""), "启用自动执行")
        this.CbEnabled.OnEvent("Click", (*) => this.SaveGeneral())

        ; minLen
        g.Add("Text", "x40 y+18", "最短文本长度")
        this.EdMinLen := g.Add("Edit", "x+10 yp-3 w60 Number", this.AM.MinLen)
        g.Add("Text", "x+8 yp+3", "字符（低于此长度不触发，防单字误触）")
        this.EdMinLen.OnEvent("Change", (*) => this.SaveGeneral())

        ; 缓存
        g.Add("Text", "x40 y+18", "元数据缓存")
        this.EdCache := g.Add("Edit", "x+10 yp-3 w70 Number", this.AM.CacheSec)
        g.Add("Text", "x+8 yp+3", "秒（0 = 每次重扫）")
        this.EdCache.OnEvent("Change", (*) => this.SaveGeneral())

        ; 显示窗口默认
        g.Add("GroupBox", "x20 y195 w700 h110", "新建插件的默认行为")
        vShow := this.AM.ShowByDefault
        this.CbShowDef := g.Add("Checkbox", "x40 y225 vSD " (vShow ? "Checked" : "")
            , "默认显示执行窗口（命令行工具需要看到输出）")
        this.CbShowDef.OnEvent("Click", (*) => this.SaveGeneral())
        g.Add("Text", "x40 y+12 cGray"
            , "不勾 = 默认隐藏（推荐）。单个插件要显示 → 在「插件」页勾它的「显示窗口」")
        this.CbWait := g.Add("Checkbox", "x40 y+14 vWT " (this.AM.WaitWhenShown ? "Checked" : "")
            , "显示窗口时等待命令结束（窗口留存，可看进度）")
        this.CbWait.OnEvent("Click", (*) => this.SaveGeneral())

        g.Add("Text", "x40 y+20 cGray"
            , "※ 改一项存一项，即时生效，无需重启。")
    }

    ; ------------------------------------------------------------
    ; Tab 2 插件（★ 主体）
    ; ------------------------------------------------------------
    BuildTabPlugins(tc) {
        g := this.GuiObj

        g.Add("Text", "x30 y40", "勾选 = 启用；「显示窗口」= 该插件执行时保留 cmd 窗口")

        ; ListView：勾选列 + 显示窗口 + 只读列
        lv := g.Add("ListView", "x30 y65 w700 h350 -Multi Grid Checked vLV"
            , ["插件名", "显示窗口", "匹配式", "产出", "形态", "文件", "状态"])
        lv.OnEvent("ItemCheck", (lvCtl, item, checked) => this.OnCheck(lvCtl, item, checked))
        lv.OnEvent("DoubleClick", (lvCtl, item) => this.OnDblClick(lvCtl, item))
        this.LV := lv

        ; 按钮行
        g.Add("Button", "x30 y+12 w110", "刷新（重扫）").OnEvent("Click", (*) => this.ReloadPlugins())
        g.Add("Button", "x+10 w110", "打开插件目录").OnEvent("Click"
            , (*) => Run(this.Root "\config\functions"))
        g.Add("Button", "x+10 w130", "打开登记文件").OnEvent("Click"
            , (*) => Run(this.AM.GetRegistryPath()))
        g.Add("Text", "x+15 yp+5 cGray", "双击插件行 = 用编辑器打开该插件")

        ; 非法插件提示区
        this.LblBad := g.Add("Text", "x30 y+14 cRed w700", "")

        this.FillPlugins()
    }

    ; ---- 填充插件列表 ----
    FillPlugins() {
        lv := this.LV
        ; ★ -Redraw / +Redraw 防闪烁（抄 RunAny 的做法）
        try lv.Opt("-Redraw")
        lv.Delete()
        this.Items := []

        for p in this.AM.GetPlugins() {
            ; 形态判定
            vForm := (p["tpl"] != "") ? "模板" : "逻辑"
            ; 显示窗口三态
            vShowCol := (p["show"] < 0)
                ? (this.AM.ShowByDefault ? "继承(显)" : "继承(隐)")
                : (p["show"] ? "✔ 显示" : "隐藏")
            vStatus := p["enabled"] ? "" : "已停用"
            vMatch  := StrLen(p["match"]) > 34 ? SubStr(p["match"], 1, 34) "…" : p["match"]

            lv.Add(p["enabled"] ? "Check" : "", p["name"], vShowCol, vMatch
                , p["out"], vForm, p["file"], vStatus)
            this.Items.Push(Map("kind", "plugin", "file", p["file"], "name", p["name"]))
        }

        ; 非法插件（元数据有问题的）也列出来，置灰 + 标原因
        for b in this.AM.BadPlugins {
            lv.Add("", "⚠ " b["file"], "", "", "", "", b["file"], b["reason"])
            this.Items.Push(Map("kind", "bad", "file", b["file"], "name", b["file"]))
        }

        ; 列宽
        lv.ModifyCol(1, 180)
        lv.ModifyCol(2, 90)
        lv.ModifyCol(3, 240)
        lv.ModifyCol(4, 70)
        lv.ModifyCol(5, 55)
        lv.ModifyCol(6, 130)
        lv.ModifyCol(7, 130)

        ; 置灰非法项
        for i, it in this.Items {
            if (it["kind"] = "bad")
                lv.Modify(i, "Select")   ; 视觉提示（v2 ListView 无 Color 方法，用状态列表达）
        }
        lv.Modify(0)                     ; 清除选择

        try lv.Opt("+Redraw")

        vBad := this.AM.BadPlugins.Length
        this.LblBad.Value := vBad > 0
            ? "⚠ 有 " vBad " 个插件元数据非法（见列表底部，已跳过加载）"
            : ""
    }

    ; ---- 勾选事件：启用 / 显示窗口 ----
    OnCheck(lv, item, checked) {
        if (item < 1 || item > this.Items.Length)
            return
        it := this.Items[item]
        if (it["kind"] != "plugin")
            return

        ; ★ 关键：ListView 的「Check」列只有一列（第 0 列，即主项）。
        ;   AHK v2 ListView 原生只支持"行勾选"，不支持"单元格勾选"。
        ;   所以「启用」用行勾选表达；「显示窗口」用双击切换（见 OnDblClick）。
        vOk := this.AM.SetPluginEnabled(it["file"], checked)
        if (vOk) {
            RunEverLog("设置UI: " it["file"] " 启用=" (checked ? "1" : "0"))
        } else {
            MsgBox("写回失败：" it["file"] "`n请检查文件权限。", "RunEver 设置", "Icon!")
        }
    }

    ; ---- 双击：切换「显示窗口」；bad 项则打开文件 ----
    OnDblClick(lv, item) {
        if (item < 1 || item > this.Items.Length)
            return
        it := this.Items[item]

        if (it["kind"] = "bad") {
            Run(this.Root "\config\functions\" it["file"])
            return
        }

        p := this.AM.GetPlugin(it["file"])
        if (p = "")
            return
        ; 三态轮转：继承 → 显示 → 隐藏 → 继承
        vCur := p["show"]
        vNew := (vCur < 0) ? 1 : (vCur = 1 ? 0 : -1)
        vTxt := (vNew < 0) ? "继承" : (vNew ? "显示" : "隐藏")

        if (vNew = -1) {
            ; 继承无法用 true/false 表达 → 直接把元数据行删掉
            this.RemoveMetaLine(p["path"], "Show")
            p["show"] := -1
            RunEverLog("设置UI: " it["file"] " 显示窗口=继承（已移除元数据行）")
        } else {
            if (!this.AM.SetPluginShow(it["file"], vNew))
                MsgBox("写回失败：" it["file"], "RunEver 设置", "Icon!")
            else
                RunEverLog("设置UI: " it["file"] " 显示窗口=" vTxt)
        }

        this.ReloadPlugins()
    }

    ; ---- 删除元数据行（用于「恢复继承」）----
    RemoveMetaLine(path, key) {
        try {
            if !FileExist(path)
                return false
            vText := FileRead(path, "UTF-8")
            vEol := InStr(vText, "`r`n") ? "`r`n" : "`n"
            vKeep := []
            for vLine in StrSplit(vText, "`n", "`r") {
                if RegExMatch(vLine, "i)^\s*global\s+RVE_FN_" key "\s*:=")
                    continue
                vKeep.Push(vLine)
            }
            vOut := ""
            for i, vLine in vKeep {
                vOut .= vLine
                if (i < vKeep.Length)
                    vOut .= vEol
            }
            vTmp := path ".tmp"
            try FileDelete(vTmp)
            FileAppend(vOut, vTmp, "UTF-8")
            FileMove(vTmp, path, 1)
            return true
        } catch {
            return false
        }
    }

    ; ---- 重扫插件并刷新列表 ----
    ReloadPlugins() {
        try {
            vN := this.AM.Refresh()
            this.FillPlugins()
            RunEverLog("设置UI: 重扫插件 " vN " 个")
        } catch as e {
            MsgBox("重扫失败：`n" e.Message, "RunEver 设置", "Icon!")
        }
    }

    ; ------------------------------------------------------------
    ; Tab 3 菜单项（只读）
    ; ------------------------------------------------------------
    BuildTabMenus(tc) {
        g := this.GuiObj
        g.Add("Text", "x30 y40", "menus.conf 只读浏览（图形化编辑留下一轮）")
        lv := g.Add("ListView", "x30 y65 w700 h350 -Multi Grid"
            , ["分组", "条目", "类型", "目标", "行"])
        this.LvMenu := lv

        ; 用 ConfParser 解析出真实结构（而不是纯文本读），保证与运行期一致
        try {
            vConf := this.Root "\config\menus.conf"
            if FileExist(vConf) {
                ; ★ __New 内部已自动 Parse()，不要再调一次
                cp := ConfParser(vConf)
                try lv.Opt("-Redraw")
                for grp in cp.Groups {
                    for it in grp["items"] {
                        vType := it["type"]
                        if (it.Has("flag") && it["flag"] != "")
                            vType .= " " it["flag"]
                        lv.Add("", grp["name"], it["title"], vType
                            , it["target"], it["line"])
                    }
                }
                try lv.Opt("+Redraw")
            }
        } catch as e {
            g.Add("Text", "x30 y+10 cRed", "解析 menus.conf 失败：" e.Message)
        }

        lv.ModifyCol(1, 110)
        lv.ModifyCol(2, 170)
        lv.ModifyCol(3, 90)
        lv.ModifyCol(4, 250)
        lv.ModifyCol(5, 50)

        g.Add("Text", "x30 y+12 cGray"
            , "※ 类型列显示为「action show」表示该条目会显示执行窗口")
        g.Add("Button", "x30 y+12 w130", "用编辑器打开").OnEvent("Click"
            , (*) => Run(this.ConfMenu))
    }

    ; ------------------------------------------------------------
    ; Tab 4 关于
    ; ------------------------------------------------------------
    BuildTabAbout(tc) {
        g := this.GuiObj
        g.Add("Text", "x30 y40 w700", "RunEver 设置管理")
        g.Add("Text", "x30 y+14 cGray", "根目录")
        g.Add("Edit", "x30 y+4 w660 ReadOnly", this.Root)

        g.Add("Text", "x30 y+14 cGray", "插件目录")
        g.Add("Edit", "x30 y+4 w660 ReadOnly", this.Root "\config\functions")

        g.Add("Text", "x30 y+14 cGray", "启动登记")
        g.Add("Edit", "x30 y+4 w660 ReadOnly", this.AM.GetRegistryPath())

        g.Add("Text", "x30 y+14 cGray", "自动执行配置")
        g.Add("Edit", "x30 y+4 w660 ReadOnly", this.ConfAuto)

        g.Add("Button", "x30 y+18 w130", "打开配置目录").OnEvent("Click"
            , (*) => Run(this.Root "\config"))
        g.Add("Button", "x+10 w130", "打开日志").OnEvent("Click"
            , (*) => Run(this.Root "\state\runever.log"))
        g.Add("Button", "x+10 w130", "重载全部配置").OnEvent("Click", (*) => this.ReloadPlugins())

        g.Add("Text", "x30 y+20 cGray"
            , "插件数：" this.AM.GetPlugins().Length " 个（含停用）")
    }

    ; ============================================================
    ; 保存常规页 → auto.conf
    ; ============================================================
    SaveGeneral() {
        vEn    := this.CbEnabled.Value ? 1 : 0
        vMin   := this.EdMinLen.Value
        vCache := this.EdCache.Value
        vShow  := this.CbShowDef.Value ? 1 : 0
        vWait  := this.CbWait.Value ? 1 : 0

        ; 输入兜底
        if (vMin = "" || vMin < 0)
            vMin := 0
        if (vCache = "" || vCache < 0)
            vCache := 0

        vOk := this.WriteAutoConf(vEn, vMin, vCache, vShow, vWait)
        if (!vOk) {
            MsgBox("写入 auto.conf 失败，请检查文件权限。", "RunEver 设置", "Icon!")
            return
        }
        ; 内存同步（不重启即生效）
        this.AM.Enabled        := (vEn = 1)
        this.AM.MinLen         := vMin
        this.AM.CacheSec       := vCache
        this.AM.ShowByDefault  := vShow
        this.AM.WaitWhenShown  := vWait

        RunEverLog("设置UI: 常规已保存 enabled=" vEn " minLen=" vMin
            . " cache=" vCache " showByDefault=" vShow " waitWhenShown=" vWait)
    }

    ; ---- 写 auto.conf（保留注释骨架，只改值行）----
    WriteAutoConf(vEn, vMin, vCache, vShow, vWait) {
        try {
            vPath := this.ConfAuto
            if !FileExist(vPath)
                return false
            vText := FileRead(vPath, "UTF-8")
            vEol  := InStr(vText, "`r`n") ? "`r`n" : "`n"

            vMap := Map("enabled", vEn, "minlen", vMin, "cacheseconds", vCache
                , "showbydefault", vShow, "waitwhenshown", vWait)
            vSeen := Map()

            vOut := ""
            vLines := StrSplit(vText, "`n", "`r")
            for i, vLine in vLines {
                vT := Trim(vLine, " `t")
                ; 注释行 / 空行原样保留
                if (vT = "" || SubStr(vT, 1, 1) = "#" || SubStr(vT, 1, 1) = ";") {
                    vOut .= vLine
                } else {
                    vPos := InStr(vT, "=")
                    if (vPos = 0) {
                        vOut .= vLine
                    } else {
                        vKey := StrLower(Trim(SubStr(vT, 1, vPos - 1), " `t"))
                        if (vMap.Has(vKey)) {
                            vOut .= vKey "=" vMap[vKey]
                            vSeen[vKey] := true
                        } else {
                            vOut .= vLine
                        }
                    }
                }
                if (i < vLines.Length)
                    vOut .= vEol
            }
            ; 缺失的键补到尾部
            for k, v in vMap {
                if (!vSeen.Has(k)) {
                    if (SubStr(vOut, -StrLen(vEol)) != vEol)
                        vOut .= vEol
                    vOut .= k "=" v
                }
            }

            vTmp := vPath ".tmp"
            try FileDelete(vTmp)
            FileAppend(vOut, vTmp, "UTF-8")
            FileMove(vTmp, vPath, 1)
            return true
        } catch {
            return false
        }
    }

    ; ============================================================
    OnClose() {
        this.GuiObj.Hide()
    }
}

; ---- 日志转发（主程序的 Log 不可见时也不崩）----
RunEverLog(s) {
    try {
        global RVE_LOG
        if (RVE_LOG != "")
            FileAppend(FormatTime(A_Now, "HH:mm:ss") " " s "`n", RVE_LOG, "UTF-8")
    } catch {
    }
}
