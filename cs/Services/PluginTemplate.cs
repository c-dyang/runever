using System;
using System.IO;
using System.Text;

namespace RunEverCs.Services
{
    /// <summary>
    /// 插件模板生成器 —— 「填表 → 生成可用插件」。
    ///
    /// 两种形态（与 AutoMatcher 双形态设计对齐）：
    ///   A. 纯模板型：只写元数据，零代码（RVE_FN_Tpl 代入 {0}）
    ///   B. 带逻辑型：元数据 + Run(text) 函数（脚本自己决定产出）
    ///
    /// 四类常用模板（用户 2026-10-03 需求）：
    ///   1. 匹配文本 → 执行命令（choco/git/自定义 CLI）
    ///   2. 匹配文本 → 打开 URL（快递查询/搜索）
    ///   3. 匹配文本 → 打开路径
    ///   4. 空壳（形态 B，自己写逻辑）
    /// </summary>
    public class PluginTemplate
    {
        /// <summary>模板类型。</summary>
        public enum Kind
        {
            CmdRun,     // 执行命令
            OpenUrl,    // 打开 URL
            OpenPath,   // 打开路径
            Custom      // 空壳（形态 B）
        }

        /// <summary>生成参数。</summary>
        public class Spec
        {
            public Kind Kind = Kind.CmdRun;
            public string Name = "";          // 显示名（必需）
            public string Match = "";         // 匹配式（必需）
            public bool Whole = true;         // 整段匹配
            public int Prio = 100;
            public string Icon = "";

            // CmdRun 用
            public string Command = "";
            public string WorkDir = "";
            public bool ShowWindow;           // true = 显示 cmd 窗口

            // OpenUrl 用
            public string UrlTemplate = "";

            // OpenPath 用
            public string PathTemplate = "{0}";

            public string FileName = "";      // 输出文件名（不含扩展名）
        }

        /// <summary>生成 .ahk 源码文本。</summary>
        public static string Generate(Spec s)
        {
            switch (s.Kind)
            {
                case Kind.OpenUrl: return GenOpenUrl(s);
                case Kind.OpenPath: return GenOpenPath(s);
                case Kind.Custom: return GenCustom(s);
                default: return GenCmdRun(s);
            }
        }

        /// <summary>写到插件目录；返回写入的完整路径（已存在则不覆盖）。</summary>
        public static string Write(string pluginDir, Spec s, out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrWhiteSpace(s.Name)) { error = "插件名不能为空"; return ""; }
                if (string.IsNullOrWhiteSpace(s.Match)) { error = "匹配式不能为空"; return ""; }

                if (!Directory.Exists(pluginDir))
                    Directory.CreateDirectory(pluginDir);

                string baseName = string.IsNullOrWhiteSpace(s.FileName)
                    ? SanitizeFileName(s.Name) : SanitizeFileName(s.FileName);
                if (string.IsNullOrEmpty(baseName)) baseName = "plugin-" + DateTime.Now.ToString("HHmmss");

                string path = Path.Combine(pluginDir, baseName + ".ahk");
                if (File.Exists(path))
                {
                    error = "文件已存在：" + Path.GetFileName(path) + "（请换个名字）";
                    return "";
                }

                string code = Generate(s);

                // ---- ★ 落盘前语法校验（防止生成不可用的插件）----
                //   实测教训：模板里硬编码了不存在的变量名 ⇒ AHK 弹出模态警告框
                //   "This local variable appears to never be assigned a value"，
                //   插件完全不可用。加这道闸门后，此类错误在校验阶段就被挡下。
                string syntaxErr = ValidateAhkSyntax(code);
                if (!string.IsNullOrEmpty(syntaxErr))
                {
                    error = "生成的脚本语法有误（未写入）：\n" + syntaxErr;
                    return "";
                }

                // 原子写
                string tmp = path + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
                File.WriteAllText(tmp, code, new UTF8Encoding(false));
                File.Move(tmp, path);

                return path;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return "";
            }
        }

        /// <summary>
        /// 用 AutoHotkey 运行时做语法校验（/validate）。
        /// 返回空串 = 通过；非空 = 错误信息。
        /// 运行时不存在的环境（如未装 AHK）跳过校验，不阻塞生成。
        /// </summary>
        public static string ValidateAhkSyntax(string code)
        {
            string ahk = FindAhkRuntime();
            if (string.IsNullOrEmpty(ahk)) return "";   // 无运行时 → 跳过

            string tmpFile = null;
            try
            {
                tmpFile = Path.Combine(Path.GetTempPath(),
                    "rve_validate_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".ahk");
                File.WriteAllText(tmpFile, code, new UTF8Encoding(false));

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ahk,
                    Arguments = "/ErrorStdOut /validate \"" + tmpFile + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    string errOut = proc.StandardError.ReadToEnd();
                    string stdOut = proc.StandardOutput.ReadToEnd();
                    if (!proc.WaitForExit(10000))
                    {
                        try { proc.Kill(); } catch { }
                        return "";
                    }
                    if (proc.ExitCode != 0)
                    {
                        string msg = (errOut + " " + stdOut).Trim();
                        return string.IsNullOrEmpty(msg)
                            ? "AHK 校验失败，exit=" + proc.ExitCode : msg;
                    }
                    return "";
                }
            }
            catch
            {
                return "";   // 校验环节自身异常不阻塞生成
            }
            finally
            {
                try { if (tmpFile != null && File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }
            }
        }

        /// <summary>找 AutoHotkey v2 运行时。</summary>
        private static string FindAhkRuntime()
        {
            string[] candidates = {
                @"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe",
                @"C:\Program Files\AutoHotkey\v2\AutoHotkey32.exe",
                @"C:\Program Files\AutoHotkey\AutoHotkey64.exe"
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            // 环境变量覆盖（便携部署）
            string env = Environment.GetEnvironmentVariable("RUNEVER_AHK");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            return "";
        }

        // ============================================================
        // 模板 1：匹配文本 → 执行命令
        // ============================================================
        private static string GenCmdRun(Spec s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#Requires AutoHotkey v2.0");
            sb.AppendLine("; ============================================================");
            sb.AppendLine("; RunEver 插件 · " + s.Name);
            sb.AppendLine(";");
            sb.AppendLine("; 由设置界面生成（模板：匹配文本 → 执行命令）");
            sb.AppendLine("; 选中匹配的文本 → 直接执行命令（不走菜单）");
            sb.AppendLine("; ============================================================");
            sb.AppendLine();
            sb.AppendLine("; ===== 元数据 =====");
            sb.AppendLine("global RVE_FN_Name  := " + Q(s.Name));
            sb.AppendLine("global RVE_FN_Match := " + Q(s.Match));
            sb.AppendLine("global RVE_FN_Whole := " + B(s.Whole));
            sb.AppendLine("global RVE_FN_Prio  := " + s.Prio);
            sb.AppendLine("global RVE_FN_Out   := \"action\"");
            if (!string.IsNullOrWhiteSpace(s.Icon))
                sb.AppendLine("global RVE_FN_Icon  := " + Q(s.Icon));
            sb.AppendLine("; 是否显示命令窗口（命令行工具看输出设 true）");
            sb.AppendLine("global RVE_FN_Show  := " + B(s.ShowWindow));
            sb.AppendLine();
            sb.AppendLine("; ===== 脚本体 =====");
            sb.AppendLine("; 契约：Run(text) 返回要执行的命令；返回空串 = 放弃（回落到菜单）");
            sb.AppendLine("Run(text) {");
            sb.AppendLine("    vCmd := Trim(text, \" `t`r`n\")");
            sb.AppendLine("    if (vCmd = \"\")");
            sb.AppendLine("        return \"\"");
            sb.AppendLine();
            sb.AppendLine("    ; ★ 安全校验：这里可加白名单/危险参数拦截");
            sb.AppendLine("    ;   例：if !RegExMatch(vCmd, \"i)^choco\\s+(install|upgrade)\\b\")");
            sb.AppendLine("    ;           return \"\"");
            sb.AppendLine();
            string cmd = string.IsNullOrWhiteSpace(s.Command) ? "{0}" : s.Command;
            if (!string.IsNullOrWhiteSpace(s.WorkDir))
            {
                sb.AppendLine("    ; 工作目录：" + s.WorkDir);
                sb.AppendLine("    ; 如需在工作目录执行，可改用 Run 的 WorkingDir 参数");
            }
            // ★ BuildCommand 返回的是 AHK 表达式（如 `"choco install " vText`），
            //   不能再套引号。
            sb.AppendLine("    return " + BuildCommand(cmd));
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ============================================================
        // 模板 2：匹配文本 → 打开 URL
        // ============================================================
        private static string GenOpenUrl(Spec s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#Requires AutoHotkey v2.0");
            sb.AppendLine("; ============================================================");
            sb.AppendLine("; RunEver 插件 · " + s.Name);
            sb.AppendLine(";");
            sb.AppendLine("; 由设置界面生成（模板：匹配文本 → 打开网址）");
            sb.AppendLine("; 形态 A：纯模板，零代码；{0} = 选中的文本");
            sb.AppendLine("; ============================================================");
            sb.AppendLine();
            sb.AppendLine("; ===== 元数据 =====");
            sb.AppendLine("global RVE_FN_Name  := " + Q(s.Name));
            sb.AppendLine("global RVE_FN_Match := " + Q(s.Match));
            sb.AppendLine("global RVE_FN_Whole := " + B(s.Whole));
            sb.AppendLine("global RVE_FN_Prio  := " + s.Prio);
            sb.AppendLine("global RVE_FN_Out   := \"url\"");
            string tpl = string.IsNullOrWhiteSpace(s.UrlTemplate) ? "{0}" : s.UrlTemplate;
            sb.AppendLine("global RVE_FN_Tpl   := " + Q(tpl));
            if (!string.IsNullOrWhiteSpace(s.Icon))
                sb.AppendLine("global RVE_FN_Icon  := " + Q(s.Icon));
            else
                sb.AppendLine("global RVE_FN_Icon  := \"chrome.exe\"");
            return sb.ToString();
        }

        // ============================================================
        // 模板 3：匹配文本 → 打开路径
        // ============================================================
        private static string GenOpenPath(Spec s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#Requires AutoHotkey v2.0");
            sb.AppendLine("; ============================================================");
            sb.AppendLine("; RunEver 插件 · " + s.Name);
            sb.AppendLine(";");
            sb.AppendLine("; 由设置界面生成（模板：匹配文本 → 打开路径）");
            sb.AppendLine("; ============================================================");
            sb.AppendLine();
            sb.AppendLine("; ===== 元数据 =====");
            sb.AppendLine("global RVE_FN_Name  := " + Q(s.Name));
            sb.AppendLine("global RVE_FN_Match := " + Q(s.Match));
            sb.AppendLine("global RVE_FN_Whole := " + B(s.Whole));
            sb.AppendLine("global RVE_FN_Prio  := " + s.Prio);
            sb.AppendLine("global RVE_FN_Out   := \"open\"");
            if (!string.IsNullOrWhiteSpace(s.Icon))
                sb.AppendLine("global RVE_FN_Icon  := " + Q(s.Icon));
            else
                sb.AppendLine("global RVE_FN_Icon  := \"explorer.exe\"");
            sb.AppendLine();
            sb.AppendLine("; ===== 脚本体（形态 B：先校验路径存在）=====");
            sb.AppendLine("Run(text) {");
            sb.AppendLine("    vPath := Trim(text, \" `t`r`n\")");
            sb.AppendLine("    if (vPath = \"\" || !FileExist(vPath))");
            sb.AppendLine("        return \"\"");
            sb.AppendLine("    return vPath");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ============================================================
        // 模板 4：空壳（自己写逻辑）
        // ============================================================
        private static string GenCustom(Spec s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#Requires AutoHotkey v2.0");
            sb.AppendLine("; ============================================================");
            sb.AppendLine("; RunEver 插件 · " + s.Name);
            sb.AppendLine(";");
            sb.AppendLine("; 由设置界面生成（空壳模板，形态 B：带逻辑）");
            sb.AppendLine("; 在 Run(text) 里自行决定产出；返回空串 = 放弃（回落菜单）");
            sb.AppendLine("; ============================================================");
            sb.AppendLine();
            sb.AppendLine("; ===== 元数据 =====");
            sb.AppendLine("global RVE_FN_Name  := " + Q(s.Name));
            sb.AppendLine("global RVE_FN_Match := " + Q(s.Match));
            sb.AppendLine("global RVE_FN_Whole := " + B(s.Whole));
            sb.AppendLine("global RVE_FN_Prio  := " + s.Prio);
            sb.AppendLine("global RVE_FN_Out   := \"action\"   ; run/open/url/action/send/keys");
            if (!string.IsNullOrWhiteSpace(s.Icon))
                sb.AppendLine("global RVE_FN_Icon  := " + Q(s.Icon));
            sb.AppendLine("global RVE_FN_Show  := false      ; true = 显示命令窗口");
            sb.AppendLine();
            sb.AppendLine("; ===== 脚本体 =====");
            sb.AppendLine("Run(text) {");
            sb.AppendLine("    vText := Trim(text, \" `t`r`n\")");
            sb.AppendLine("    if (vText = \"\")");
            sb.AppendLine("        return \"\"");
            sb.AppendLine();
            sb.AppendLine("    ; TODO: 在这里写你的逻辑");
            sb.AppendLine("    return \"\"");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ============================================================

        /// <summary>
        /// 把命令模板里的 {0} 转成 AHK 表达式。
        ///
        /// ★ 关键：{0} 是【本生成器】的占位符，不是 AHK 语法。
        ///   命令里出现 {0} 的位置要替换成 Run() 的形参名 RunArg。
        ///
        /// ★★ 形参名必须与生成的 Run(text) 签名一致（都用 text）。
        ///     曾硬编码成 vText ⇒ 生成的插件里 vText 从未赋值 ⇒
        ///     AHK 直接报 "This local variable appears to never be assigned a value"
        ///     并弹模态警告框（截图实证），插件完全不可用。
        /// </summary>
        private static string BuildCommand(string template)
        {
            if (string.IsNullOrEmpty(template)) return "\"\"";
            if (!template.Contains("{0}"))
                return "\"" + EscapeAhk(template) + "\"";

            // 按 {0} 切分，奇数段是变量位
            var parts = template.Split(new[] { "{0}" }, StringSplitOptions.None);
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(" text ");
                if (parts[i].Length > 0) sb.Append("\"").Append(EscapeAhk(parts[i])).Append("\"");
            }
            return sb.ToString().Trim();
        }

        /// <summary>转义 AHK 双引号字符串里的特殊字符。</summary>
        private static string EscapeAhk(string s)
        {
            if (s == null) return "";
            return s.Replace("`", "``").Replace("\"", "\"\"");
        }

        private static string Q(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        }

        private static string B(bool b) { return b ? "true" : "false"; }

        /// <summary>文件名净化：去掉非法字符。</summary>
        public static string SanitizeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('-');
            }
            return sb.ToString();
        }
    }
}
