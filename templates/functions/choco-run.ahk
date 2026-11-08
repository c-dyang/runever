#Requires AutoHotkey v2.0
; ============================================================
; RunEver 插件 · 执行 Chocolatey 命令（形态 B：带逻辑）
;
; 选中 choco install/upgrade/uninstall 命令 → 直接执行
; 依据：Quicker 动作定义 L15100（^choco (install|Upgrade|Uninstall).*）
;
; ★ 安全：动词白名单 —— 只放行 install/upgrade/uninstall，
;   防止选中任意 choco 子命令（如 uninstall --all）被误执行。
; ============================================================

; ===== 元数据 =====
;   ★ RVE_FN_Out 必须用【执行器的动作类型】之一：
;     run / open / url / action / send / keys
;     "action" = 走 cmd /c 执行命令（不是 "cmd"！曾写 cmd 导致
;     执行器报「未知动作类型: cmd」）
global RVE_FN_Name  := "执行 Chocolatey 命令"
global RVE_FN_Match := "^choco\s+(install|upgrade|uninstall)\b"
global RVE_FN_Whole := false          ; 允许包含（文本可能带前后空白）
global RVE_FN_Prio  := 200            ; 优先级高于纯 URL 类
global RVE_FN_Out   := "action"
global RVE_FN_Icon  := "SHELL32.dll,71"
; ★ 显示执行窗口：choco 装包耗时长且有进度/报错，必须让用户看见
;   （不写此行则继承 auto.conf 的 showByDefault=0 → 隐藏，输出全丢）
global RVE_FN_Show  := true

; ===== 脚本体：接收选中文本，返回目标字符串（空 = 放弃）=====
;   契约：Run(text) → 目标；返回空串 ⇒ 回落弹菜单
Run(text) {
    vCmd := Trim(text, " `t`r`n")

    ; ★ 动词白名单校验（元数据 Match 只是粗筛，这里是硬闸门）
    if !RegExMatch(vCmd, "i)^choco\s+(install|upgrade|uninstall)\b")
        return ""

    ; 拒绝危险参数（防止清空所有包、指定任意源等）
    if RegExMatch(vCmd, "i)\s(--all|-y\s+--all|--force)\s*$")
        return ""

    ; 单行命令才放行（多行可能夹带其他命令）
    if (InStr(vCmd, "`n") || InStr(vCmd, "`r"))
        return ""

    return vCmd
}
