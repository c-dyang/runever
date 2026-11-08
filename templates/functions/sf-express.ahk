#Requires AutoHotkey v2.0
; ============================================================
; RunEver 插件 · 顺丰快递查询（形态 A：纯模板，零代码）
;
; 选中运单号（SF 开头）→ 直接打开顺丰官网查询页
; 依据：Quicker 动作定义 L13349/L13387
; ============================================================

; ===== 元数据（匹配器只读这几行）=====
global RVE_FN_Name  := "顺丰快递查询"
global RVE_FN_Match := "^SF\d{10,}$"
global RVE_FN_Whole := true
global RVE_FN_Prio  := 100
global RVE_FN_Out   := "url"
global RVE_FN_Tpl   := "https://www.sf-express.com/chn/sc/waybill/waybill-detail/{0}"
global RVE_FN_Icon  := "chrome.exe"

; ===== 形态 A：无脚本体 —— 匹配器按 Tpl 生成目标，交给执行器 =====
