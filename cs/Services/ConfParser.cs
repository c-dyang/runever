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
    /// </summary>
    public class ConfParser
    {
        public List<MenuGroup> Groups { get; private set; } = new List<MenuGroup>();

        private readonly string _path;

        public ConfParser(string path)
        {
            _path = path;
        }

        /// <summary>解析；失败抛异常（调用方决定是否回落）。</summary>
        public void Parse()
        {
            Groups = new List<MenuGroup>();
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
