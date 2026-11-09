using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// menus.conf 解析器 —— 与 AHK 版 ConfParser.ahk 语义逐条对齐。
    ///
    /// 语法：
    ///   # 或 ; 开头         → 注释
    ///   ! 名称  类型  [关键词...]  → 分组（folder/ext/proc/text/always/fallback）
    ///     标题  [_快捷键]  | 动作  [hide|show]  目标
    ///     缩进 2 空格 = 层级 +1
    ///
    /// 动作类型：run / open / url / action / send / keys / group
    ///   action 后面的白名单词 hide/show 被吃成 Flag，其余归 Target。
    ///   ★ 2026-11-09（U-B）：紧随类型（或 hide/show 之后）的 `sub` 是第三个修饰位取值，
    ///     仅对 open 生效 ⇒ 渲染期展开成子菜单（见 MenuBuilder）。行语法不变。
    /// </summary>
    public class ConfParser
    {
        public List<MenuGroup> Groups { get; private set; } = new List<MenuGroup>();

        private readonly string _path;

        // ============================================================
        // ★ L2/F3：解析后的结构规范化统计（解析层唯一归属）
        // ============================================================
        /// <summary>
        /// 本次解析里被**就地升格**的父条目数（原本 Type != Group、却有更深 Level 的子项）。
        /// 正常配置为 0。
        /// </summary>
        public int AutoFixCount { get; private set; }

        /// <summary>本次升格的明细（行号 / 标题 / 是否丢弃了动作目标）；无升格时为空。</summary>
        public List<string> AutoFixNotes { get; private set; } = new List<string>();

        /// <summary>本次解析的规范化汇总行，恒有值：<c>结构自动修正=N</c>（供日志 / 自检同源展示）。</summary>
        public string NormalizeReport { get; private set; } = "结构自动修正=0";

        /// <summary>最近一次解析的升格数（跨实例探针：日志 / 诊断 / 自检共用同一数字）。</summary>
        public static int LastAutoFixCount;

        /// <summary>
        /// 可选日志出口（调用方注入时优先走它，例如挂到托盘主日志）。
        /// 未注入 ⇒ 尝试追加到已存在的 <c>&lt;root&gt;\state\runever-cs.log</c>（不主动创建，见 EmitLog）。
        /// </summary>
        public static Action<string> LogSink;

        public ConfParser(string path)
        {
            _path = path;
        }

        /// <summary>解析；失败抛异常（调用方决定是否回落）。</summary>
        public void Parse()
        {
            Groups = new List<MenuGroup>();
            AutoFixCount = 0;
            AutoFixNotes = new List<string>();
            NormalizeReport = "结构自动修正=0";
            if (!File.Exists(_path))
                throw new FileNotFoundException("配置文件不存在: " + _path);

            string text = File.ReadAllText(_path, Encoding.UTF8);
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            string[] lines = text.Split('\n');
            int cur = -1;   // 当前分组索引（-1 = 还没有分组）

            for (int i = 0; i < lines.Length; i++)
            {
                string rawLine = lines[i];
                int lineNo = i + 1;
                string trimmed = rawLine.Trim(' ', '\t');

                // 空行 / 注释
                if (trimmed.Length == 0) continue;
                char c0 = trimmed[0];
                if (c0 == '#' || c0 == ';') continue;

                // 分组行
                if (c0 == '!')
                {
                    cur = ParseGroupLine(trimmed.Substring(1), lineNo);
                    continue;
                }

                // 条目行（无归属分组则忽略）
                if (cur < 0) continue;
                ParseItemLine(rawLine, cur, lineNo);
            }

            // ★ L2/F3：解析完成后的**唯一一次**结构规范化（在判定层之前）。
            //   语义：只升格、不改行序、不改 Level、不删子项；无子项的普通条目不碰。
            NormalizeStructure();
        }

        // ============================================================
        // ★ L2/F3：结构规范化 —— 有子项的非 group 父条目 ⇒ 升格为子菜单标题
        // ============================================================

        /// <summary>
        /// 判断父子用**与 MenuBuilder / SubtreePruner 同款的层级栈**：
        /// 对每个条目，弹出所有 Level &gt;= 当前 Level 的栈顶，新的栈顶就是它的父条目
        /// （跳级配置也能正确挂到"最近的更浅层"上，不会因为下一条更深就乱认父）。
        ///
        /// 只改内存结构，不写盘、不动配置文件：
        ///   · 被判定为父、且 Type != Group ⇒ Type = Group
        ///   · 丢弃动作目标（group 没有动作）与 action 的 hide/show 标记
        ///   · 保留 Title / Hotkey / IconSource / Level / Line / 行序
        ///   · 原目标非空 ⇒ 记一条明细（"丢了一个目标"），不静默吞
        /// </summary>
        private void NormalizeStructure()
        {
            AutoFixCount = 0;
            AutoFixNotes = new List<string>();

            foreach (var g in Groups)
            {
                if (g == null || g.Items == null || g.Items.Count < 2) continue;

                // ① 先按层级栈收集"有子项"的父条目（判定期间不改任何对象）
                var parents = new List<MenuItem>();
                var seen = new HashSet<MenuItem>();
                var stack = new List<MenuItem>();

                foreach (var it in g.Items)
                {
                    if (it == null) continue;

                    while (stack.Count > 0 && stack[stack.Count - 1].Level >= it.Level)
                        stack.RemoveAt(stack.Count - 1);

                    if (stack.Count > 0)
                    {
                        var parent = stack[stack.Count - 1];
                        if (seen.Add(parent)) parents.Add(parent);
                    }

                    stack.Add(it);
                }

                // ② 就地升格（保持原有顺序）
                foreach (var p in parents)
                {
                    if (p.IsGroupHeader) continue;    // 已经是 group：只保留，不计数

                    string lostTarget = (p.Target ?? "").Trim();

                    p.Type = ActionType.Group;
                    p.Target = "";
                    p.Flag = ActionFlag.None;
                    p.Sub = false;      // ★ U-B：升格成 group 就没有 open 动作了，展开位随之失效

                    AutoFixCount++;

                    string note = "结构自动修正: 第 " + p.Line + " 行「" + p.Title
                        + "」非 group 但有子项 ⇒ 升格为 group（Level=" + p.Level + " 不变）";
                    if (lostTarget.Length > 0)
                        note += "，丢弃动作目标「" + lostTarget + "」";
                    AutoFixNotes.Add(note);
                }
            }

            NormalizeReport = "结构自动修正=" + AutoFixCount;
            LastAutoFixCount = AutoFixCount;

            // ③ 可观测：明细逐条 + 汇总一行（恒输出，含 =0，便于对照）
            foreach (var note in AutoFixNotes) EmitLog(note);
            EmitLog(NormalizeReport);
        }

        /// <summary>
        /// 规范化日志出口：注入了 <see cref="LogSink"/> 就走调用方；否则**追加到已存在**的
        /// <c>&lt;root&gt;\state\runever-cs.log</c>（与托盘 / CLI 同一份日志）。
        /// 不主动创建日志文件 —— 只读引用（如 pc 复现配置）与自检临时目录零副作用；
        /// 任何写失败都不影响解析（与 ConfigSync.AppendAppLog 同口径）。
        /// </summary>
        private void EmitLog(string line)
        {
            if (LogSink != null)
            {
                try { LogSink(line); } catch { }
                return;
            }

            try
            {
                string confDir = Path.GetDirectoryName(_path);
                if (string.IsNullOrEmpty(confDir)) return;
                if (!string.Equals(Path.GetFileName(confDir), "config", StringComparison.OrdinalIgnoreCase))
                    return;

                string root = Path.GetDirectoryName(confDir);
                if (string.IsNullOrEmpty(root)) return;

                string log = Path.Combine(root, "state", "runever-cs.log");
                if (!File.Exists(log)) return;    // 不创建：没有日志就没有输出
                if (!AppLogEnabled(root)) return; // 尊重 auto.conf 的 log/loglevel=0

                File.AppendAllText(log,
                    DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>
        /// 读 auto.conf 的日志开关（与 RunEverApp.ReadLogSettings 同口径：默认开，后出现的键覆盖前者）。
        /// 只用于决定"要不要往已存在的 runever-cs.log 追加一行"，不改变任何其它行为。
        /// </summary>
        private static bool AppLogEnabled(string root)
        {
            try
            {
                string conf = Path.Combine(root, "config", "auto.conf");
                if (!File.Exists(conf)) return true;

                bool on = true;
                foreach (var raw in File.ReadAllLines(conf, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "log" || k == "loglevel")
                    {
                        int n;
                        if (int.TryParse(v, out n)) on = n > 0;
                        else on = (v == "true" || v == "1");
                    }
                }
                return on;
            }
            catch { return true; }
        }

        // ---- 分组行: "名称  类型  关键词..." ----
        private int ParseGroupLine(string s, int lineNo)
        {
            s = s.Trim(' ', '\t');
            if (s.Length == 0)
                throw new FormatException("第 " + lineNo + " 行: 分组名为空");

            List<string> parts = SplitWhitespace(s);
            string name = parts.Count >= 1 ? parts[0] : "";
            string typeStr = parts.Count >= 2 ? parts[1].ToLowerInvariant() : "";

            GroupType gt;
            if (!MenuGroup.TryParseType(typeStr, out gt))
                throw new FormatException("第 " + lineNo + " 行: 未知分组类型 '" + typeStr
                    + "'（可用: folder/ext/proc/text/always/fallback）");

            var g = new MenuGroup { Name = name, Type = gt, Line = lineNo };
            for (int k = 2; k < parts.Count; k++)
                g.Keywords.Add(parts[k].ToLowerInvariant());

            Groups.Add(g);
            return Groups.Count - 1;
        }

        // ---- 条目行 ----
        private void ParseItemLine(string rawLine, int groupIdx, int lineNo)
        {
            // 缩进层级 = 前导空白数 / 2（与 AHK 一致）
            int leading = 0;
            foreach (char c in rawLine)
            {
                if (c == ' ' || c == '\t') leading++;
                else break;
            }
            int level = leading / 2;

            string content = rawLine.Trim(' ', '\t');

            // 拆分 标题部分 / 动作部分
            string titlePart;
            string actionPart;
            int barPos = content.IndexOf('|');
            if (barPos < 0)
            {
                // 无 | → 纯标题（视作 group 类型）
                titlePart = content;
                actionPart = "";
            }
            else
            {
                titlePart = content.Substring(0, barPos).Trim(' ', '\t');
                actionPart = content.Substring(barPos + 1).Trim(' ', '\t');
            }

            // 标题里的快捷键: "标题 _X"（末尾 _ + 单字符）
            string title = titlePart;
            string hotkey = "";
            var m = System.Text.RegularExpressions.Regex.Match(titlePart, @"^(.*?)\s+_(\S)$");
            if (m.Success)
            {
                title = m.Groups[1].Value.Trim(' ', '\t');
                hotkey = m.Groups[2].Value;
            }

            // 动作部分
            ActionType type = ActionType.Group;
            ActionFlag flag = ActionFlag.None;
            bool sub = false;
            string target = "";

            if (actionPart.Length > 0)
            {
                List<string> ap = SplitWhitespace(actionPart);
                if (ap.Count >= 1)
                {
                    ActionType at;
                    if (TryParseActionType(ap[0], out at)) type = at;
                    else type = ActionType.Group;   // 未知类型按 AHK 行为保留原文，执行期报错
                }

                int idx = 1;   // 0-based：下一个待取 token 的下标
                if (ap.Count >= 2)
                {
                    string w = ap[1].ToLowerInvariant();
                    if (w == "hide" || w == "show")
                    {
                        flag = (w == "hide") ? ActionFlag.Hide : ActionFlag.Show;
                        idx = 2;
                    }
                }

                // ★ 2026-11-09（U-B）：第三个修饰位取值 `sub`。
                //   · 只认**修饰位**上的 sub（类型之后 / hide|show 之后），目标里出现 sub 不动；
                //   · 只对 open 置位；其它动作类型按"普通修饰位、忽略"吃掉，并留一行日志；
                //   · hide/show 的解析一个字没动（上面那段原样保留）。
                if (ap.Count >= idx + 1
                    && string.Equals(ap[idx], "sub", StringComparison.OrdinalIgnoreCase))
                {
                    idx++;
                    if (type == ActionType.Open)
                    {
                        sub = true;
                    }
                    else
                    {
                        EmitLog("第 " + lineNo + " 行「" + title + "」: 修饰位 sub 只对 open 生效，已忽略（动作="
                            + type.ToString().ToLowerInvariant() + "）");
                    }
                }

                if (ap.Count >= idx + 1)
                {
                    // 剩余部分作为目标（保留原样，含空格）
                    int start = actionPart.IndexOf(ap[idx], StringComparison.Ordinal);
                    if (start >= 0) target = actionPart.Substring(start);
                }
                else if (ap.Count == idx)
                {
                    // 只有类型+flag，无目标
                    target = "";
                }
            }

            var item = new MenuItem
            {
                Title = title,
                Hotkey = hotkey,
                Type = type,
                Flag = flag,
                Sub = sub,
                Target = target,
                Level = level,
                Line = lineNo
            };
            Groups[groupIdx].Items.Add(item);
        }

        private static bool TryParseActionType(string s, out ActionType t)
        {
            t = ActionType.Group;
            if (string.IsNullOrEmpty(s)) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "run": t = ActionType.Run; return true;
                case "open": t = ActionType.Open; return true;
                case "url": t = ActionType.Url; return true;
                case "action": t = ActionType.Action; return true;
                case "send": t = ActionType.Send; return true;
                case "keys": t = ActionType.Keys; return true;
                case "group": t = ActionType.Group; return true;
                default: return false;
            }
        }

        /// <summary>
        /// 收集配置里所有 run 类型条目引用的程序名（供路径缓存构建用）。
        /// 对照 AHK ConfParser.ahk:214-238。
        ///
        /// 规则（逐条对齐 AHK）：
        ///   - 只取 type=run 的条目
        ///   - 目标为空 ⇒ 跳过
        ///   - 取目标的首个空白分隔 token（引号内不拆，见 SplitWhitespace）
        ///   - 首 token 去掉包裹引号
        ///   - 含 \ 或 / 的（已经是路径）⇒ 跳过 —— 它们不需要「程序名→全路径」解析
        /// 返回：小写名集合（用 HashSet 天然去重，等价 AHK 的 Map 去重）
        /// </summary>
        public HashSet<string> CollectPrograms()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in Groups)
            {
                foreach (var it in g.Items)
                {
                    if (it.Type != ActionType.Run) continue;
                    string tgt = it.Target ?? "";
                    if (tgt.Length == 0) continue;

                    var parts = SplitWhitespace(tgt);
                    if (parts.Count < 1) continue;

                    string prog = parts[0].Trim('"');
                    if (prog.Length == 0) continue;

                    // 只处理无路径的（不含 \ 或 /）—— 已是路径的条目无需解析
                    if (prog.IndexOf('\\') >= 0 || prog.IndexOf('/') >= 0) continue;

                    names.Add(prog.ToLowerInvariant());
                }
            }

            return names;
        }

        /// <summary>按空白拆分（保留引号内内容不拆）—— 与 AHK SplitWS 一致。</summary>
        public static List<string> SplitWhitespace(string s)
        {
            var outList = new List<string>();
            var buf = new StringBuilder();
            bool inQuote = false;

            foreach (char c in s)
            {
                if (c == '"')
                {
                    inQuote = !inQuote;
                    buf.Append(c);
                    continue;
                }
                if (!inQuote && (c == ' ' || c == '\t'))
                {
                    if (buf.Length > 0)
                    {
                        outList.Add(buf.ToString());
                        buf.Length = 0;
                    }
                    continue;
                }
                buf.Append(c);
            }
            if (buf.Length > 0)
                outList.Add(buf.ToString());

            return outList;
        }
    }
}
