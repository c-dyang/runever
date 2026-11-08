#Requires AutoHotkey v2.0
; ============================================================
; RunEver 渲染层 · Win32 原生菜单封装
;
; 为什么不用 AHK 的 Menu 对象：
;   Menu.Add(名, 回调) 把「唯一键」和「显示文本」合并成同一个字符串，
;   同菜单内重名 = 静默替换（实测：Rename#2 / Delete#2 抛错 ⇒ 只剩一个），
;   同名的另一组内容会整块消失。
;   Win32 的 AppendMenuW(hMenu, uFlags, uIDNewItem, lpNewItem) 把
;   「命令 ID」（整数）与「显示文本」分成两个参数 ⇒ 同名项天然共存。
;   实测：两条同名项 GetMenuItemCount = 2，ID = 101/102，文本读回完全相同。
;
; 依赖：只用 user32.dll / gdi32.dll / shell32.dll，零外部库、零 DLL 文件。
;
; 用法：
;   m := NativeMenu()
;   m.AddItem("打开 PowerShell")               ; 叶条目（返回 cmdId）
;   m.AddSeparator()
;   sub := NativeMenu()
;   sub.AddItem("计算器")
;   m.AddSubMenu("工具", sub)                   ; 子菜单
;   m.SetIconById(id, "C:\...\app.exe")         ; 图标（可选）
;   selId := m.Show()                           ; 返回被点项 ID，0 = 未选
;   m.Destroy()
;
; 生命周期：
;   Show() 之后一律调 Destroy()。Destroy 只销毁菜单本身；
;   图标 DIB 由全局缓存持有（用户定案：程序图标走缓存路径拿），
;   Show() 之后一律调 Destroy()。Destroy 只销毁菜单本身；
;   图标 DIB 由全局缓存持有，进程退出时由系统回收。
; ============================================================

; ---- Win32 常量 ----
global NM_MF_STRING      := 0x0000
global NM_MF_POPUP       := 0x0010
global NM_MF_SEPARATOR   := 0x0800
global NM_MF_GRAYED      := 0x0001
global NM_MF_DISABLED    := 0x0002

global NM_TPM_LEFTALIGN  := 0x0000
global NM_TPM_RIGHTALIGN := 0x0008
global NM_TPM_TOPALIGN   := 0x0000
global NM_TPM_BOTTOMALIGN:= 0x0020
global NM_TPM_RETURNCMD  := 0x0100
global NM_TPM_NONOTIFY   := 0x0080

global NM_MIIM_BITMAP    := 0x0080
global NM_MIIM_ID        := 0x0002
global NM_MIIM_FTYPE     := 0x0100
global NM_MFT_BITMAP     := 0x0004
global NM_MIIM_SUBMENU   := 0x0004

global NM_ID_FIRST       := 1              ; 叶条目 ID 从 1 起（0 保留给「未选」）

class NativeMenu {

    ; ---------- 构造 ----------
    __New() {
        this.Handle        := DllCall("CreatePopupMenu", "Ptr")
        if (this.Handle = 0)
            throw Error("CreatePopupMenu 失败")
        this.NextId        := NM_ID_FIRST
        this.OwnedBitmaps  := []           ; 需要 DeleteObject 的 HBITMAP
        this.IconByCmd     := Map()        ; cmdId -> HBITMAP（便于查证）
        this.TitleById     := Map()        ; cmdId -> 显示文本（回传后防串）
        this.SubMenus      := []           ; 子 NativeMenu 引用（防被 GC）
        this.Destroyed     := false
    }

    ; ---------- 加叶条目 ----------
    ; 返回分配的 cmdId（>0）。text 里可含 Tab：Win32 会把 Tab 后的文字右对齐。
    AddItem(text, opts := "") {
        vId := this.NextId
        this.NextId += 1

        vFlags := NM_MF_STRING
        if (opts != "" && InStr(opts, "gray"))
            vFlags |= NM_MF_GRAYED

        ok := DllCall("AppendMenuW", "Ptr", this.Handle, "UInt", vFlags
            , "UPtr", vId, "Str", text, "Int")
        if (ok = 0)
            throw Error("AppendMenuW 失败: " text)

        this.TitleById[vId] := text
        return vId
    }

    ; ---------- 加不可点的标题（group 类型用）----------
    ; 用 MF_STRING + MF_GRAYED + MF_DISABLED，和 menu.Disable 视觉一致。
    AddTitle(text) {
        vFlags := NM_MF_STRING | NM_MF_GRAYED | NM_MF_DISABLED
        ok := DllCall("AppendMenuW", "Ptr", this.Handle, "UInt", vFlags
            , "UPtr", 0, "Str", text, "Int")
        if (ok = 0)
            throw Error("AppendMenuW(标题) 失败: " text)
    }

    ; ---------- 分隔线 ----------
    AddSeparator() {
        DllCall("AppendMenuW", "Ptr", this.Handle, "UInt", NM_MF_SEPARATOR
            , "UPtr", 0, "Ptr", 0, "Int")
    }

    ; ---------- 挂子菜单 ----------
    ; 注意：MF_POPUP 项的「ID 位」存的是 HMENU 而不是命令 ID，
    ;       所以子菜单本身没有 cmdId（GetMenuItemID 对它返回 -1）。
    AddSubMenu(text, subMenu) {
        ; 把子菜单的 ID 空间接到父菜单，保证整棵树 ID 全局唯一
        subMenu.NextId := Max(subMenu.NextId, this.NextId)

        ok := DllCall("AppendMenuW", "Ptr", this.Handle, "UInt", NM_MF_POPUP
            , "UPtr", subMenu.Handle, "Str", text, "Int")
        if (ok = 0)
            throw Error("AppendMenuW(子菜单) 失败: " text)

        for k, v in subMenu.TitleById
            this.TitleById[k] := v
        this.SubMenus.Push(subMenu)
    }

    ; ---------- 取项数（守门断言用）----------
    GetCount() {
        return DllCall("GetMenuItemCount", "Ptr", this.Handle, "Int")
    }

    ; ---------- 取某项 ID（断言用；popup 返回 -1）----------
    GetIdAt(pos) {
        return DllCall("GetMenuItemID", "Ptr", this.Handle, "Int", pos, "Int")
    }

    ; ---------- 取某项的子菜单句柄（0 = 不是子菜单）----------
    ; MENUITEMINFO (x64) 布局：
    ;   0 cbSize | 4 fMask | 8 fType | 12 fState | 16 wID
    ;   24 hSubMenu | 32 hbmpChecked | 40 hbmpUnchecked
    ;   48 dwItemData | 56 dwTypeData | 64 cch | 72 hbmpItem
    GetSubMenuHandle(pos) {
        mii := Buffer(80, 0)
        NumPut("UInt", 80, mii, 0)                       ; cbSize
        NumPut("UInt", NM_MIIM_SUBMENU, mii, 4)          ; fMask = MIIM_SUBMENU
        DllCall("GetMenuItemInfoW", "Ptr", this.Handle, "UInt", pos
            , "Int", 1, "Ptr", mii)
        return NumGet(mii, 24, "Ptr")                    ; hSubMenu
    }

    ; ---------- 取某项的显示文本 ----------
    GetItemText(pos) {
        buf := Buffer(1024, 0)
        DllCall("GetMenuStringW", "Ptr", this.Handle, "UInt", pos
            , "Ptr", buf, "Int", 512, "UInt", 0x0400)    ; MF_BYPOSITION
        return StrGet(buf, "UTF-16")
    }

    ; ---------- 统计子菜单数量（层级断言用）----------
    CountSubMenus() {
        n := 0
        Loop this.GetCount() {
            if (this.GetSubMenuHandle(A_Index - 1) != 0)
                n++
        }
        return n
    }

    ; ---------- 图标：给已存在的 cmdId 挂 HBITMAP ----------
    ; src 支持：exe/dll/ico 全路径（"文件" 或 "文件, 资源号"）
    ; 返回 true/false。失败静默（图标是装饰，不该拖垮菜单）。
    ;
    ; ★★ 三个必须遵守的坑（2026-10-03 实测/查证，改前先读）：
    ;
    ; ① MENUITEMINFO 结构体偏移（x64，sizeof = 80）：
    ;      0 cbSize | 4 fMask | 8 fType | 12 fState | 16 wID
    ;      24 hSubMenu | 32 hbmpChecked | 40 hbmpUnchecked
    ;      48 dwItemData | 56 dwTypeData | 64 cch | 72 hbmpItem
    ;    ★ hbmpItem 在 **72**（早先写 40，那是 hbmpUnchecked ⇒ 图标永远不显示）
    ;
    ; ② ★ fMask **只能给 MIIM_BITMAP**，不要带 MIIM_FTYPE！
    ;    实测（state\p17.ahk，四种组合穷举）：
    ;      fMask = MIIM_BITMAP|MIIM_FTYPE         → 返回 0，err=87
    ;      fMask = MIIM_BITMAP|MIIM_FTYPE|MIIM_ID → 同样 0/87
    ;      fMask = MIIM_BITMAP                    → 返回 1 ✅
    ;    官方文档亦证：MIIM_BITMAP 就是「Retrieves or sets the hbmpItem member」。
    ;
    ; ③ 菜单要的是 **32bpp 预乘 alpha 的 DIB**（见 IconToDib 注释）。
    SetIconById(cmdId, src) {
        vHbm := this.GetIconBitmap(src)
        if (vHbm = 0)
            return false

        mii := Buffer(80, 0)
        NumPut("UInt", 80, mii, 0)                        ; cbSize
        NumPut("UInt", NM_MIIM_BITMAP, mii, 4)            ; fMask（★不要加 FTYPE）
        NumPut("Ptr",  vHbm,       mii, 72)               ; hbmpItem ★ 偏移 72

        ok := DllCall("SetMenuItemInfoW", "Ptr", this.Handle
            , "UInt", cmdId, "Int", 0, "Ptr", mii, "Int")
        return ok ? true : false
    }

    ; ---------- 图标位图缓存（按源路径去重）----------
    ; ★ 用户定案（2026-10-02 [10:01]）：程序图标走缓存路径拿。
    ;   同一个 exe 在配置里可能被多条引用 ⇒ 公共缓存。
    ;   实测 state\p38.ahk：同图标重复 10 次，GDI 句柄稳定不涨。
    GetIconBitmap(src) {
        global g_nmIconCache
        if (!IsSet(g_nmIconCache))
            g_nmIconCache := Map()

        ; ★ 空源 = 明确要兜底图标（无程序可解析时走这条）
        if (Trim(src) = "")
            return this.MakeFallbackIcon()

        vKey := StrLower(src)
        if (g_nmIconCache.Has(vKey))
            return g_nmIconCache[vKey]

        vHbm := this.ExtractHBitmap(src)
        if (vHbm = 0)
            vHbm := this.MakeFallbackIcon()        ; ★ 兜底图标

        if (vHbm != 0)
            g_nmIconCache[vKey] := vHbm
        return vHbm
    }

    ; ---------- 从文件抽图标 → DIB ----------
    ExtractHBitmap(src) {
        vFile := src
        vIdx  := 0
        if (RegExMatch(src, "^(.+?),\s*(-?\d+)$", &m)) {
            vFile := Trim(m[1], " `t" . Chr(34))
            vIdx  := Integer(m[2])
        }

        ; ★★ ExtractIconExW 必须写库名前缀 "shell32\"！
        ;   实测（state\p13.ahk / p14.ahk）：
        ;     DllCall("ExtractIconExW", ...)          → "Call to nonexistent function"
        ;     DllCall("shell32\ExtractIconExW", ...)  → n=1 hS=6620249  ✅
        ;   这是「自检说命中 9 条、菜单上却零图标」的**真正根因**。
        bufLarge := Buffer(8, 0)
        bufSmall := Buffer(8, 0)
        n := DllCall("shell32\ExtractIconExW", "Str", vFile, "Int", vIdx
            , "Ptr", bufLarge, "Ptr", bufSmall, "UInt", 1, "UInt")
        hLarge := NumGet(bufLarge, 0, "Ptr")
        hSmall := NumGet(bufSmall, 0, "Ptr")

        hIcon := 0
        if (n > 0 && hSmall != 0)
            hIcon := hSmall
        else if (n > 0 && hLarge != 0)
            hIcon := hLarge

        ; ★ 用不上的那个图标句柄必须销毁，否则泄漏 HICON
        if (hIcon != hLarge && hLarge != 0)
            DllCall("DestroyIcon", "Ptr", hLarge)
        if (hIcon != hSmall && hSmall != 0)
            DllCall("DestroyIcon", "Ptr", hSmall)

        ; 主源失败 → SHGetFileInfo 兜底拿系统图标
        if (hIcon = 0) {
            bufInfo := Buffer(1024, 0)
            if (DllCall("shell32\SHGetFileInfoW", "Str", vFile, "UInt", 0
                , "Ptr", bufInfo, "UInt", 1024
                , "UInt", 0x00000100 | 0x00000001, "UPtr"))     ; SHGFI_ICON|SHGFI_SMALLICON
                hIcon := NumGet(bufInfo, 0, "Ptr")
        }
        if (hIcon = 0)
            return 0

        vHbm := this.IconToDib(hIcon)
        DllCall("DestroyIcon", "Ptr", hIcon)
        return vHbm
    }

    ; ---------- HICON → DIB（菜单唯一认的位图形态）----------
    ;
    ; ★★ 路线选择（2026-10-03 实测定案，别换）：
    ;
    ;  ❌ 弃用 GetDIBits 像素级合成：
    ;     实测（state\p39.ahk）不同图标累积到第 3~4 条时必卡死，
    ;     且卡在 curl.exe（经 SHGetFileInfoW 拿到的系统图标）——
    ;     GetDIBits 对这类位图在 AHK DllCall 环境下会阻塞。
    ;     联网查证也表明菜单位图要求「32bpp 预乘 alpha」，手工合成极易踩坑。
    ;
    ;  ✅ 采用 DrawIconEx 铺图（state\p40.ahk 实测）：
    ;     4 个图标（含 curl）全部 0ms 完成、无阻塞。
    ;     原理：建 32bpp DIB → SelectObject 进内存 DC → DrawIconEx(DI_NORMAL)
    ;           —— 图标自带的遮罩与 alpha 由系统处理，不用手工读两张位图。
    ;     配合 32bpp DIB，产出的就是菜单要的预乘 alpha 位图。
    IconToDib(hIcon) {
        ; 图标尺寸：优先取系统菜单图标尺寸，回退 16x16
        w := DllCall("GetSystemMetrics", "Int", 49, "Int")      ; SM_CXSMICON
        h := w
        if (w <= 0 || w > 256) {
            w := 16
            h := 16
        }

        hdcScreen := DllCall("GetDC", "Ptr", 0, "Ptr")
        hDib := 0
        try {
            ; ---- 建 32bpp DIB（负高度 = 自上而下）----
            bi := Buffer(40, 0)
            NumPut("UInt", 40, bi, 0)          ; biSize
            NumPut("Int",  w,  bi, 4)          ; biWidth
            NumPut("Int", -h,  bi, 8)          ; biHeight（负 = top-down）
            NumPut("UShort", 1, bi, 12)        ; biPlanes
            NumPut("UShort", 32, bi, 14)       ; biBitCount
            NumPut("UInt", 0, bi, 16)          ; BI_RGB

            ppv := Buffer(8, 0)
            hDib := DllCall("CreateDIBSection", "Ptr", hdcScreen, "Ptr", bi
                , "UInt", 0, "Ptr", ppv, "Ptr", 0, "UInt", 0, "Ptr")
            if (hDib = 0)
                return 0

            ; ---- 用 DrawIconEx 把 HICON 铺到 DIB 上 ----
            hdcMem := DllCall("CreateCompatibleDC", "Ptr", hdcScreen, "Ptr")
            old := DllCall("SelectObject", "Ptr", hdcMem, "Ptr", hDib, "Ptr")
            ; DI_NORMAL(0x3) = DI_IMAGE | DI_MASK —— 带遮罩的正常绘制
            DllCall("DrawIconEx", "Ptr", hdcMem, "Int", 0, "Int", 0
                , "Ptr", hIcon, "Int", w, "Int", h, "UInt", 0, "Ptr", 0, "UInt", 3)
            DllCall("SelectObject", "Ptr", hdcMem, "Ptr", old)
            DllCall("DeleteDC", "Ptr", hdcMem)
        } finally {
            if (hdcScreen != 0)
                DllCall("ReleaseDC", "Ptr", 0, "Ptr", hdcScreen)
        }
        return hDib
    }

    ; ---------- ★ 兜底图标 ----------
    ; 用户定案（2026-10-02）：配置里只有程序名，解析不到路径时不能空着。
    ; 用 shell32.dll 里的通用「应用程序」图标（资源号 2）。
    MakeFallbackIcon() {
        global g_nmFallbackIcon
        if (IsSet(g_nmFallbackIcon) && g_nmFallbackIcon != 0)
            return g_nmFallbackIcon

        bufLarge := Buffer(8, 0)
        bufSmall := Buffer(8, 0)
        ; ★ 同 ExtractHBitmap：必须带 shell32\ 前缀
        DllCall("shell32\ExtractIconExW", "Str", "shell32.dll", "Int", 2
            , "Ptr", bufLarge, "Ptr", bufSmall, "UInt", 1, "UInt")
        hSmall := NumGet(bufSmall, 0, "Ptr")
        hLarge := NumGet(bufLarge, 0, "Ptr")
        hIcon := (hSmall != 0) ? hSmall : hLarge

        ; ★ 不用的句柄要销毁（同 ExtractHBitmap，防 GDI 泄漏）
        if (hIcon != hLarge && hLarge != 0)
            DllCall("DestroyIcon", "Ptr", hLarge)
        if (hIcon != hSmall && hSmall != 0)
            DllCall("DestroyIcon", "Ptr", hSmall)
        if (hIcon = 0)
            return 0

        vHbm := this.IconToDib(hIcon)
        DllCall("DestroyIcon", "Ptr", hIcon)
        g_nmFallbackIcon := vHbm
        return vHbm
    }

    ; ---------- 显示并取回被点项 ID ----------
    ; 返回 cmdId（>0）；0 = 用户未选/取消。
    ; ★ TPM_RETURNCMD：直接拿 ID，省掉 WM_COMMAND 消息循环。
    ;
    ; ★★ 属主窗口规则（2026-10-03 实测定案，别改）：
    ;   TrackPopupMenuEx 的 hwnd 参数**不接受别的进程的窗口**。
    ;   原来写 `WinExist("A")` 取前台窗口 —— AHK 脚本自身常无窗口，
    ;   该调用返回 0 / 或返回别的进程的 hwnd ⇒ 传进去直接失败
    ;   （实测 GetLastError = 1400 ERROR_INVALID_WINDOW_HANDLE，
    ;   菜单 0ms 秒退、什么都不显示）。
    ;   正解：**自建一个本进程的隐藏属主窗口**，用完销毁。
    Show(hwndOwner := 0) {
        global g_nmOwnerHwnd

        vOwn := 0
        if (hwndOwner != 0) {
            vOwn := hwndOwner
        } else {
            if (!IsSet(g_nmOwnerHwnd) || g_nmOwnerHwnd = 0
                || !DllCall("IsWindow", "Ptr", g_nmOwnerHwnd))
                g_nmOwnerHwnd := this.EnsureOwnerWindow()
            vOwn := g_nmOwnerHwnd
        }
        if (vOwn = 0)
            vOwn := 0      ; 兜底：至少不崩

        pt := Buffer(8, 0)
        DllCall("GetCursorPos", "Ptr", pt)
        x := NumGet(pt, 0, "Int")
        y := NumGet(pt, 4, "Int")

        vFlags := NM_TPM_LEFTALIGN | NM_TPM_TOPALIGN | NM_TPM_RETURNCMD

        ; ★ SetForegroundWindow 是前置条件：不设前台，菜单不会自动消失
        try DllCall("SetForegroundWindow", "Ptr", vOwn)

        vRet := DllCall("TrackPopupMenuEx", "Ptr", this.Handle, "UInt", vFlags
            , "Int", x, "Int", y, "Ptr", vOwn, "Ptr", 0, "Int")

        return vRet
    }

    ; ---------- 创建隐藏属主窗口（本进程内，供 TrackPopupMenuEx 用）----------
    EnsureOwnerWindow() {
        global g_nmOwnerObj, g_nmOwnerHwnd
        try {
            g_nmOwnerObj := Gui("-Caption +ToolWindow +E0x08000000")   ; WS_EX_NOACTIVATE
            g_nmOwnerObj.Show("x-32000 y-32000 w1 h1")                  ; 屏幕外 1x1
            g_nmOwnerHwnd := g_nmOwnerObj.Hwnd
            return g_nmOwnerHwnd
        } catch {
            return 0
        }
    }

    ; ---------- 销毁 ----------
    Destroy() {
        if (this.Destroyed)
            return
        this.Destroyed := true

        ; 根菜单销毁会连带销毁子菜单（它们被 AppendMenu 挂上去了）
        if (this.Handle != 0)
            DllCall("DestroyMenu", "Ptr", this.Handle)

        ; ★ 图标 DIB 由全局缓存持有，进程生命周期内不逐菜单释放
        ;   （用户定案：程序图标走缓存路径拿）。ExitApp 后由系统回收。
        this.OwnedBitmaps := []
        this.IconByCmd    := Map()
    }

    ; ---------- 调试：导出项数 + 各 ID/文本（供断言与排障）----------
    Dump() {
        s := "项数=" this.GetCount() "`n"
        Loop this.GetCount() {
            vPos := A_Index - 1
            vId  := this.GetIdAt(vPos)
            s .= "  [" vPos "] id=" vId "`n"
        }
        return s
    }
}
