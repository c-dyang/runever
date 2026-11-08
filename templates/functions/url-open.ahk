#Requires AutoHotkey v2.0
; ============================================================
; RunEver 插件 · 打开网址（形态 A：纯模板，零代码）
;
; 选中 http/https 网址 → 直接打开
; 依据：Quicker 动作定义 L6484 / L11643
; ============================================================

; ===== 元数据 =====
global RVE_FN_Name  := "打开网址"
global RVE_FN_Match := "^https?://\S+$"
global RVE_FN_Whole := true
global RVE_FN_Prio  := 90
global RVE_FN_Out   := "url"
global RVE_FN_Tpl   := "{0}"
global RVE_FN_Icon  := "chrome.exe"
