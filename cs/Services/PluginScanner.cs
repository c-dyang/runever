using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RunEverCs.Services
{
    /// <summary>
    /// 插件元数据 —— 对应 AHK AutoMatcher 的插件条目。
    /// </summary>
    public class PluginInfo
    {
        public string File = "";
        public string Path = "";
        public string Name = "";
        public string Match = "";
        public bool Whole = true;
        public int Prio = 100;
        public string Out = "";
        public string Tpl = "";
        public string Icon = "";
        public bool Menu;
        public string Arg = "text";

        /// <summary>显示执行窗口：-1 继承 / 0 隐藏 / 1 显示。</summary>
        public int Show = -1;

        public bool Enabled = true;

        /// <summary>元数据非法时的原因（非空表示加载失败）。</summary>
        public string BadReason = "";

        /// <summary>
        /// 本次解析**实际出现过**的键（小写，如 "name" / "show"）。
        /// 用途：「配置块」按块覆盖时只改写出现的键，未出现的键保持原样（不误删）。
        /// </summary>
        public List<string> PresentKeys = new List<string>();

        public bool IsFormB { get { return string.IsNullOrEmpty(Tpl); } }

        public string ShowText
        {
            get
            {
                if (Show < 0) return "继承";
                return Show == 1 ? "显示" : "隐藏";
            }
        }
    }

    /// <summary>
    /// 插件扫描器 —— 对应 AHK AutoMatcher.ReadMeta / Scan。
    ///
    /// 元数据规范（写在 .ahk 头部）：
    ///   global RVE_FN_Name  := "顺丰快递"
    ///   global RVE_FN_Match := "^SF\d{10,}$"
    ///   global RVE_FN_Whole := true
    ///   global RVE_FN_Prio  := 100
    ///   global RVE_FN_Out   := "url"
    ///   global RVE_FN_Tpl   := "https://...{0}"
    ///   global RVE_FN_Icon  := "chrome.exe"
    ///   global RVE_FN_Menu  := false   ; L3：作为菜单项追加进核心菜单（always 段末尾）
    ///   global RVE_FN_Show  := true
    ///   global RVE_FN_Enabled := true
    ///
    /// ★ 三层权限（2026-11-10 定案，声明即定档，核心不仲裁）：
    ///   L1 接管  out="intercept" 命中后核心不建菜单/不派发/不追加，插件自己弹菜单；
    ///            插件 Run(text) 返回非空 = 接管成功；空/异常/超时 ⇒ 回落核心菜单。
    ///   L2 派发  out ∈ run/open/url/action/send/keys 命中后直接派发、跳过弹菜单。
    ///   L3 菜单  menu=true ⇒ 不自动派发，作为菜单项追加进 always 段末尾。
    ///   ★ 互斥：intercept + menu 冲突 ⇒ intercept 优先（menu 忽略）。
    ///
    /// ★ 解析只有一份：`ParseLines`（同一个正则 + 同一个字段 switch）同时服务
    ///   ① 扫描插件文件（`ReadMeta`，只看头部 80 行）
    ///   ② 「配置块」面板的粘贴解析（`ParseMetaText`）
    ///   禁止另写第二套正则 —— 否则同一份文本会「扫文件能读、粘贴读不出」。
    /// </summary>
    public class PluginScanner
    {
        /// <summary>
        /// 合法产出形态。`intercept` = L1 接管（插件自己弹菜单，核心跳过），
        /// 其余六种 = L2 派发（交由 Executor 执行）。
        /// ★ 顺序即错误提示里的列举顺序（不要随意调换）。
        /// </summary>
        public static readonly string[] OutTypes = { "run", "open", "url", "action", "send", "keys", "intercept" };

        /// <summary>文件扫描只看头部这么多行（脚本体里的文本不算元数据）。</summary>
        public const int FileMetaScanLines = 80;

        /// <summary>规范键名（导出/写回用的顺序与大小写）。解析本身不区分大小写。</summary>
        public static readonly string[] MetaKeys =
        {
            "Name", "Match", "Whole", "Prio", "Out", "Tpl", "Icon", "Arg", "Menu", "Show", "Enabled"
        };

        /// <summary>
        /// ★ 唯一元数据行正则（文件扫描与粘贴解析共用这一份）。
        ///   只认行首的 `global RVE_FN_X := 值`（注释行以 ; 开头，不会命中）。
        /// </summary>
        private static readonly Regex MetaLineRe = new Regex(
            @"^\s*global\s+RVE_FN_(\w+)\s*:=\s*(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public List<PluginInfo> Plugins = new List<PluginInfo>();
        public List<PluginInfo> BadPlugins = new List<PluginInfo>();

        private readonly string _dir;

        public PluginScanner(string root)
        {
            _dir = Path.Combine(root, "config", "functions");
        }

        public string Dir { get { return _dir; } }

        /// <summary>扫描目录，按优先级降序（同优先级按文件名升序）。</summary>
        public void Scan()
        {
            Plugins.Clear();
            BadPlugins.Clear();

            if (!Directory.Exists(_dir)) return;

            var files = new List<string>(Directory.GetFiles(_dir, "*.ahk"));
            files.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var f in files)
            {
                var p = ReadMeta(f);
                if (p == null) continue;

                if (!string.IsNullOrEmpty(p.BadReason))
                    BadPlugins.Add(p);
                else
                    Plugins.Add(p);
            }

            // 稳定排序：Prio 降序 → 文件名升序
            Plugins.Sort((a, b) =>
            {
                int c = b.Prio.CompareTo(a.Prio);
                if (c != 0) return c;
                return string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>读单个插件文件的元数据（只解析头部 80 行）。</summary>
        public PluginInfo ReadMeta(string path)
        {
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch
            {
                return null;
            }

            var info = new PluginInfo
            {
                File = Path.GetFileName(path),
                Path = path
            };

            ParseLines(text, FileMetaScanLines, info);

            if (string.IsNullOrEmpty(info.Name) || string.IsNullOrEmpty(info.Match)
                || string.IsNullOrEmpty(info.Out))
            {
                // 不是插件文件（缺必需元数据）→ 静默跳过，不算错误
                return null;
            }

            info.BadReason = JoinBad(CheckOutType(info.Out), CheckCombination(info));
            return info;
        }

        /// <summary>把多条错误拼成 BadReason（空项自动省略）。</summary>
        private static string JoinBad(params string[] parts)
        {
            var errs = new List<string>();
            foreach (var s in parts)
                if (!string.IsNullOrEmpty(s)) errs.Add(s);
            return string.Join("；", errs.ToArray());
        }

        /// <summary>
        /// 解析一段「配置块」文本 → PluginInfo（**永不抛、永不返回 null**）。
        ///
        /// 与 `ReadMeta` 共用同一个 `MetaLineRe` 正则和同一个字段赋值 switch：
        ///   · 可选键缺省 ⇒ 默认值（Whole=true / Prio=100 / Show=-1 继承 /
        ///     Enabled=true / Arg="text" / Menu=false）
        ///   · 必需键缺失、或 RVE_FN_Out 非法（含历史炸点 "cmd"）⇒ 写进 BadReason，
        ///     由调用方决定展示 / 拒绝落盘
        ///
        /// 与文件扫描的唯一差别：不限行数（粘贴进来的块可能带一大段头部注释）。
        /// </summary>
        public static PluginInfo ParseMetaText(string text)
        {
            var info = new PluginInfo();
            ParseLines(text ?? "", 0, info);

            var errs = new List<string>();
            if (string.IsNullOrEmpty(info.Name)) errs.Add("缺少必需键 RVE_FN_Name");
            if (string.IsNullOrEmpty(info.Match)) errs.Add("缺少必需键 RVE_FN_Match");
            if (string.IsNullOrEmpty(info.Out)) errs.Add("缺少必需键 RVE_FN_Out");
            else
            {
                string outErr = CheckOutType(info.Out);
                if (outErr.Length > 0) errs.Add(outErr);
            }

            // ★ 组合约束（危险输出带 {0} / Menu 无 Tpl）—— 粘贴导入同样要挡
            string combErr = CheckCombination(info);
            if (combErr.Length > 0) errs.Add(combErr);

            info.BadReason = string.Join("；", errs.ToArray());
            return info;
        }

        /// <summary>
        /// 生成规范配置块（`ParseMetaText` 的逆运算）——「配置块」面板导出与预览共用。
        ///
        /// 只输出**有确定值**的键：Tpl/Icon 空 ⇒ 省略；Show&lt;0（继承）⇒ 省略；
        /// Arg 是默认 "text" ⇒ 省略；Menu=false / Enabled=true ⇒ 省略。
        /// 因此 `Format(Parse(Format(x)))` 与 `Format(x)` 逐字符相同（自检 ⑩ 断言）。
        /// </summary>
        public static string FormatMetaText(PluginInfo p)
        {
            if (p == null) p = new PluginInfo();

            var lines = new List<string>();
            lines.Add(MetaLine("Name", MetaValueText(p, "Name")));
            lines.Add(MetaLine("Match", MetaValueText(p, "Match")));
            lines.Add(MetaLine("Whole", MetaValueText(p, "Whole")));
            lines.Add(MetaLine("Prio", MetaValueText(p, "Prio")));
            lines.Add(MetaLine("Out", MetaValueText(p, "Out")));

            if (!string.IsNullOrEmpty(p.Tpl))
                lines.Add(MetaLine("Tpl", MetaValueText(p, "Tpl")));
            if (!string.IsNullOrEmpty(p.Icon))
                lines.Add(MetaLine("Icon", MetaValueText(p, "Icon")));
            if (!string.Equals((p.Arg ?? "text"), "text", StringComparison.OrdinalIgnoreCase))
                lines.Add(MetaLine("Arg", MetaValueText(p, "Arg")));
            if (p.Menu)
                lines.Add(MetaLine("Menu", MetaValueText(p, "Menu")));
            if (p.Show >= 0)
                lines.Add(MetaLine("Show", MetaValueText(p, "Show")));
            if (!p.Enabled)
                lines.Add(MetaLine("Enabled", MetaValueText(p, "Enabled")));

            return string.Join("\r\n", lines.ToArray());
        }

        /// <summary>某个键在配置块里的右值文本（字符串带双引号，布尔/数字裸写）。</summary>
        public static string MetaValueText(PluginInfo p, string key)
        {
            if (p == null) return "";
            switch ((key ?? "").ToLowerInvariant())
            {
                case "name": return Q(p.Name);
                case "match": return Q(p.Match);
                case "whole": return Bool(p.Whole);
                case "prio": return p.Prio.ToString();
                case "out": return Q(p.Out);
                case "tpl": return Q(p.Tpl);
                case "icon": return Q(p.Icon);
                case "arg": return Q(p.Arg);
                case "menu": return Bool(p.Menu);
                case "show": return p.Show == 1 ? "true" : "false";
                case "enabled": return Bool(p.Enabled);
                default: return null;
            }
        }

        /// <summary>小写键名 → 规范键名（Name/Match/…）；未知键返回 null。</summary>
        public static string CanonicalKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (var k in MetaKeys)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return k;
            return null;
        }

        /// <summary>
        /// 数一段文本里某个键出现了几次（多段配置块诊断用）。
        /// ★ 走同一个 `MetaLineRe`，不另写正则。
        /// </summary>
        public static int CountKeyOccurrences(string text, string key)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key)) return 0;

            int n = 0;
            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            foreach (var line in lines)
            {
                var m = MetaLineRe.Match(line);
                if (m.Success && string.Equals(m.Groups[1].Value, key, StringComparison.OrdinalIgnoreCase))
                    n++;
            }
            return n;
        }

        /// <summary>
        /// 产出类型合法性判定 —— 合法返回空串，否则返回给用户看的错误。
        /// ★ "cmd" 必须单独报错（历史炸点）：执行器只认 action；
        ///   插件里写 `RVE_FN_Out := "cmd"` ⇒ 运行期「未知动作类型: cmd」。
        /// </summary>
        /// <summary>
        /// 组合约束校验（2026-11-11 加入）—— **单个键合法不代表组合合法**。
        ///
        /// ① 危险输出（action / send / keys）的 Tpl 不允许含 {0}：
        ///    `Executor.DoCmd` 对 target **不做任何转义或白名单**，直接 `cmd /c`；
        ///    形态 A 没有 Run() 这道闸门，若 Tpl 里带 {0}，选中文本会原样进命令行 ⇒ 命令注入。
        ///    （Tpl 不含 {0} 的固定命令是安全的，不禁 —— 别把合法用法一起打死。）
        ///
        /// ② Menu=true 但没有 Tpl：`RunEverApp` 构建菜单项时
        ///    `Target = string.IsNullOrEmpty(p.Tpl) ? "" : p.Tpl.Replace("{0}", text)`
        ///    ⇒ 生成一个**点了没反应的死项**。形态 B 要动态目标请用 `Out="intercept"` 接管。
        /// </summary>
        public static string CheckCombination(PluginInfo p)
        {
            if (p == null) return "";

            string outType = (p.Out ?? "").Trim().ToLowerInvariant();
            bool dangerous = outType == "action" || outType == "send" || outType == "keys";

            if (dangerous && !string.IsNullOrEmpty(p.Tpl)
                && p.Tpl.IndexOf("{0}", StringComparison.Ordinal) >= 0)
            {
                return "危险输出（" + outType + "）的 Tpl 不能含 {0}："
                     + "选中文本会被原样拼进命令行（命令注入）。"
                     + "请改用形态 B —— 去掉 Tpl，在 Run(text) 里做白名单校验后再返回目标";
            }

            // ★ 例外：intercept 是形态 B（无 Tpl）却常被顺手写上 Menu ——
            //   代码语义是「intercept 优先，Menu 被忽略」，属**合法**声明，不拒载。
            if (p.Menu && string.IsNullOrEmpty(p.Tpl) && outType != "intercept")
            {
                return "RVE_FN_Menu := true 必须有 Tpl（菜单项的目标由 Tpl 展开）；"
                     + "形态 B 要动态目标请用 Out=\"intercept\" 接管";
            }

            return "";
        }

        public static string CheckOutType(string outType)
        {
            string v = (outType ?? "").Trim().ToLowerInvariant();
            string legal = "（合法值: " + string.Join("/", OutTypes) + "）";

            if (v.Length == 0)
                return "缺少产出类型 RVE_FN_Out" + legal;

            foreach (var t in OutTypes)
                if (v == t) return "";

            if (v == "cmd")
                return "RVE_FN_Out 不能是 \"cmd\" —— 要执行命令请写 \"action\""
                    + "（曾写 cmd 导致执行器报「未知动作类型: cmd」）";

            return "未知产出形态 out=" + v + legal;
        }

        // ============================================================
        // 唯一解析实现（ReadMeta 与 ParseMetaText 都走这里）
        //   maxLines <= 0 ⇒ 不限行数
        // ============================================================
        private static void ParseLines(string text, int maxLines, PluginInfo info)
        {
            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int limit = (maxLines > 0 && maxLines < lines.Length) ? maxLines : lines.Length;

            for (int i = 0; i < limit; i++)
            {
                var m = MetaLineRe.Match(lines[i]);
                if (!m.Success) continue;

                string key = m.Groups[1].Value.ToLowerInvariant();
                string raw = StripInlineComment(m.Groups[2].Value).Trim();
                raw = Unquote(raw);

                if (!info.PresentKeys.Contains(key))
                    info.PresentKeys.Add(key);

                switch (key)
                {
                    case "name": info.Name = raw; break;
                    case "match": info.Match = raw; break;
                    case "tpl": info.Tpl = raw; break;
                    case "out": info.Out = raw.ToLowerInvariant(); break;
                    case "icon": info.Icon = raw; break;
                    case "arg": info.Arg = raw.ToLowerInvariant(); break;
                    case "whole": info.Whole = !(raw.ToLowerInvariant() == "false" || raw == "0"); break;
                    case "menu": info.Menu = (raw.ToLowerInvariant() == "true" || raw == "1"); break;
                    case "prio":
                        int pr;
                        info.Prio = int.TryParse(raw, out pr) ? pr : 100;
                        break;
                    case "show":
                        info.Show = (raw.ToLowerInvariant() == "true" || raw == "1") ? 1 : 0;
                        break;
                    case "enabled":
                        info.Enabled = !(raw.ToLowerInvariant() == "false" || raw == "0");
                        break;
                }
            }
        }

        /// <summary>剥掉引号外的行内注释（; 或 #）。</summary>
        public static string StripInlineComment(string s)
        {
            bool inQuote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') inQuote = !inQuote;
                else if (!inQuote && (c == ';' || c == '#'))
                    return s.Substring(0, i);
            }
            return s;
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2);
            return s;
        }

        // ---- 生成侧的小工具 ----
        private static string MetaLine(string key, string value)
        {
            return "global RVE_FN_" + key.PadRight(7) + " := " + value;
        }

        private static string Q(string s)
        {
            return "\"" + (s ?? "") + "\"";
        }

        private static string Bool(bool b) { return b ? "true" : "false"; }
    }
}
