#Requires AutoHotkey v2.0
; ============================================================
; RunEver 插件 · 打开本地路径（形态 B：带逻辑）
;
; 选中本地路径（含盘符）→ 打开它
; 依据：Quicker 动作定义 L14298（^[A-Za-z]:\\...）
;
; ★ 用形态 B 而非纯模板的原因：要校验路径真实存在，
;   不存在则放弃（回落弹菜单），避免弹出「找不到文件」系统错误框。
; ============================================================

; ===== 元数据 =====
global RVE_FN_Name  := "打开本地路径"
global RVE_FN_Match := "^[A-Za-z]:\\(?:[^\\\r\n]+\\)*[^\\\r\n]*$"
global RVE_FN_Whole := true
global RVE_FN_Prio  := 120
global RVE_FN_Out   := "open"
global RVE_FN_Icon  := "explorer.exe"

; ===== 脚本体 =====
Run(text) {
    vPath := Trim(text, " `t`r`n" . Chr(34))

    ; 路径必须存在，否则放弃（回落弹菜单）
    if !FileExist(vPath)
        return ""

    return vPath
}
