using System;
using System.IO;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// 「文件名 / 参数」拆分与回拼 —— **唯一口径源**（契约 §6、§7-Q7）。
    ///
    /// 编辑器把 `Target` 拆成两框只是**视图**：磁盘上仍是单串，
    /// 拆分口径必须与运行期完全一致，否则会出现"编辑框显示正常、保存后行为变了"。
    ///
    /// ★ 切分**只允许**调用 <see cref="ConfParser.SplitWhitespace"/> 拿 token 边界；
    ///   **禁止**在本类里另写一套引号解析。
    /// ★ `args` 的起点用与 `Executor.DoRun` 相同的 index 口径。
    ///
    /// ★★ 2026-10-05 口径修订（用户定案）——**支持"未加引号的含空格路径"**：
    ///   旧口径只认引号，`C:\Program Files\Listary\Listary.exe` 会被切成
    ///   `file="C:\Program"` + `args="Files\Listary\Listary.exe"`，
    ///   且**运行期同口径** ⇒ 真的报 `run-missing`（点了没反应）。
    ///   新口径四段式：
    ///     ① 首个 token 由引号包裹 ⇒ 路径 = 去引号内容（**前后引号剥离**）
    ///     ② 未加引号 + 多 token ⇒ **最长"存在"路径前缀**（文件或目录）
    ///     ③ 仍不行 ⇒ **最早的可执行扩展名 token 边界**
    ///     ④ 都不行 ⇒ 旧口径（首 token），不猜
    ///   回拼侧：路径**含空白即套引号**（Windows 命令行要求），已带引号则原样。
    /// </summary>
    public static class TargetSplit
    {
        /// <summary>③ 段用：可执行/脚本类扩展名（按"最早边界"取值）。</summary>
        private static readonly string[] kExecExt =
        {
            ".exe", ".com", ".bat", ".cmd", ".ps1", ".lnk", ".vbs", ".js", ".ahk", ".py"
        };

        /// <summary>
        /// 需要「文件名 / 参数」两框的动作类型（契约 §6）。
        /// `url`（含 %s 模板）/ `send`（待发正文）/ `keys`（按键序列）的目标语义
        /// **不是**"程序 + 参数" ⇒ 保持单框，不拆。
        /// </summary>
        public static bool AppliesTo(ActionType type)
        {
            return type == ActionType.Run || type == ActionType.Action || type == ActionType.Open;
        }

        /// <summary>按类型字符串判定（`Executor.Task.Type` 是字符串的场景）。</summary>
        public static bool AppliesTo(string type)
        {
            if (type == null) return false;
            string s = type.Trim();
            return string.Equals(s, "run", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "action", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "open", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>剥离路径前后包裹的双引号（只剥"首尾成对"的那一层，内容不动）。</summary>
        public static string Unquote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2);
            return s;
        }

        /// <summary>
        /// 拆分：`file` = 程序/目标路径（**已剥引号**），`args` = 其余部分（原样，含内部多空格）。
        /// 无第二个 token ⇒ `args = ""`。
        /// </summary>
        public static void Split(string target, out string file, out string args)
        {
            string raw = target ?? "";
            file = raw;
            args = "";

            var parts = ConfParser.SplitWhitespace(raw);
            if (parts.Count == 0) return;

            // ---- ① 引号口径（主路径）：首 token 被引号包裹 ⇒ 去引号 + 其后即为参数 ----
            string t0 = parts[0];
            if (t0.Length >= 2 && t0[0] == '"' && t0[t0.Length - 1] == '"')
            {
                int at = raw.IndexOf(t0, StringComparison.Ordinal);
                file = t0.Substring(1, t0.Length - 2);
                args = raw.Substring(at + t0.Length).TrimStart(' ', '\t');
                return;
            }

            if (parts.Count == 1)
            {
                // 单 token ⇒ 无参数（顺带把首尾空白归一掉）
                file = Unquote(t0);
                return;
            }

            // ---- ② 未加引号：最长"存在"路径前缀 ----
            int cut = LongestExistingPrefix(raw);
            // ---- ③ 仍不行：最早的可执行扩展名边界 ----
            if (cut <= 0) cut = EarliestExecBoundary(raw);
            if (cut > 0)
            {
                file = Unquote(raw.Substring(0, cut).TrimEnd(' ', '\t'));
                args = raw.Substring(cut).TrimStart(' ', '\t');
                return;
            }

            // ---- ④ 兜底：旧口径（首 token），不猜 ----
            int pos = raw.IndexOf(parts[1], StringComparison.Ordinal);
            if (pos < 0)
            {
                file = Unquote(t0);
                return;
            }
            file = Unquote(raw.Substring(0, pos).TrimEnd(' ', '\t'));
            args = raw.Substring(pos);
        }

        /// <summary>
        /// 回拼：`file` **含空白即套引号**（否则写回磁盘的命令行在 cmd 里就是错的）；
        /// 已带引号则原样；`args` 为空且 file 无需引号 ⇒ 逐字节返回 `file`。
        /// </summary>
        public static string Join(string file, string args)
        {
            string f = file ?? "";
            string a = args ?? "";
            if (f.Length == 0) return a;               // 文件名空 ⇒ 结果 = 参数（不阻断保存）
            string q = Quote(f);
            return a.Length == 0 ? q : q + " " + a;
        }

        /// <summary>含空白（空格/制表）且未带引号 ⇒ 套一层引号。</summary>
        private static string Quote(string f)
        {
            if (f.Length >= 2 && f[0] == '"' && f[f.Length - 1] == '"') return f;
            if (f.IndexOf(' ') >= 0 || f.IndexOf('\t') >= 0) return "\"" + f + "\"";
            return f;
        }

        /// <summary>
        /// 从**最长候选**开始逐级退到上一个空白边界，返回第一个"真实存在"的前缀长度。
        /// ★ 约束（2026-10-05 实测踩到）：候选**必须止于空白或串尾**才算数 ——
        ///   `C:\Program Files` 本身是存在的目录，但它被 `\...` 续接 ⇒ 只是前缀，不是目标
        ///   （否则"程序没装"的路径会被错切到 `C:\Program Files`）。
        /// ★ 要求候选里含路径分隔符（避免把 `notepad.exe` 这种裸名当路径去探盘）。
        /// 异常一律返回 0 = 放弃该策略（后面还有 ③ 段可执行扩展名边界兜底）。
        /// </summary>
        private static int LongestExistingPrefix(string raw)
        {
            string s = raw.TrimEnd(' ', '\t');
            int end = s.Length;
            while (end > 0)
            {
                bool atBoundary = (end == s.Length) || s[end] == ' ' || s[end] == '\t';
                string cand = s.Substring(0, end);
                if (atBoundary && cand.Length > 1 && (cand.IndexOf('\\') >= 0 || cand.IndexOf('/') >= 0))
                {
                    try
                    {
                        if (File.Exists(cand) || Directory.Exists(cand)) return end;
                    }
                    catch
                    {
                        return 0;          // 非法字符 / 网络异常 ⇒ 放弃
                    }
                }
                int sp = s.LastIndexOfAny(new[] { ' ', '\t' }, end - 1);
                if (sp < 0) break;
                end = sp;
            }
            return 0;
        }

        /// <summary>
        /// 返回**最早**的「可执行扩展名 + 其后为空白或串尾」边界（= 该位置恰为路径末尾）。
        /// 取最早而非最晚：避免被参数里的 `.exe`（如 `--run other.exe`）带偏。
        /// </summary>
        private static int EarliestExecBoundary(string raw)
        {
            int best = -1;
            foreach (string ext in kExecExt)
            {
                int i = raw.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                while (i >= 0)
                {
                    int after = i + ext.Length;
                    if (after == raw.Length || raw[after] == ' ' || raw[after] == '\t')
                    {
                        if (best < 0 || after < best) best = after;
                        break;
                    }
                    i = raw.IndexOf(ext, i + 1, StringComparison.OrdinalIgnoreCase);
                }
            }
            return best;
        }

        /// <summary>
        /// 「参数为空」判定（契约 §6 自动追加规则的前置条件）。
        /// ★ 2026-10-05：**必须走同一套 Split** —— 旧的 `SplitWhitespace(raw).Count &lt;= 1`
        ///   在新口径下会把 `C:\Program Files\x.exe`（无参数）误判为"有参数"。
        /// </summary>
        public static bool ArgsEmpty(string target)
        {
            string f, a;
            Split(target, out f, out a);
            return a.Length == 0;
        }
    }
}
