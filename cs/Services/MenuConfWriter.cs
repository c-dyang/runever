using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// 一条结构校验问题（C1：保存前的闸）。
    /// 文案口径（用户看的就是这句）：「第 N 行 / 条目「X」：原因」——
    /// 必须带**行号 + 条目名 + 原因**，否则用户改不动（见 HANDOFF-L6-C1 §四）。
    /// N = **按当前内存模型序列化后**该条目会落在 menus.conf 的第几行，
    /// 不是"上次读盘时的行号"（用户刚插了一行，旧行号早就不对了）。
    /// </summary>
    public sealed class MenuStructureIssue
    {
        /// <summary>机器可判的类别（自检断言用，不给人看）。</summary>
        public string Code = "";

        /// <summary>给人看的原因（不含行号/条目名，那两段由 <see cref="Where"/> 拼）。</summary>
        public string Reason = "";

        /// <summary>分组下标（0-based；-1 = 不知道该属于哪组）。</summary>
        public int GroupIndex = -1;

        public string GroupName = "";

        /// <summary>组内条目下标（0-based；-1 = 分组级问题）。</summary>
        public int ItemIndex = -1;

        /// <summary>序列化后的行号（1-based；0 = 算不出来）。</summary>
        public int LineNo;

        /// <summary>出问题的条目（双击跳转用；分组级问题为 null）。</summary>
        public MenuItem Item;

        public string Where
        {
            get
            {
                string at = (LineNo > 0) ? ("第 " + LineNo + " 行") : "行号未知";
                string who;
                if (Item != null)
                {
                    string t = (Item.Title ?? "").Trim();
                    who = "条目「" + (t.Length == 0 ? "(无标题)" : t) + "」";
                }
                else
                {
                    who = "分组「" + ((GroupName ?? "").Trim().Length == 0 ? "(未命名)" : GroupName) + "」";
                }
                return at + " / " + who + "：" + Reason;
            }
        }

        public override string ToString() { return Where; }
    }

    /// <summary>
    /// menus.conf **写盘前的结构校验器**（C1）。
    ///
    /// 为什么要有它：编辑器原来是"内存里怎么改就怎么存"，跳级 / 父条目后没紧跟子条目 /
    /// 同一条目重复 这类结构错误能一路写进磁盘，读回来还挺"自洽"（假绿）。
    ///
    /// ★ 口径纪律：这里是**唯一**一套结构不变式，自检（MenuEditorSelfTest）与本类共用，
    ///   不许另写一套"宽松版"给 UI 用。运行期的同源依据：
    ///     · <see cref="MenuBuilder"/> 用 `while (j &lt; n &amp;&amp; src[j].Level &gt; it.Level) j++`
    ///       按"紧随其后"扫子条目 ⇒ 只认逐级加深、只认连续子树；
    ///     · `ConfParser.NormalizeStructure`（F3）按同一套层级栈判定父子。
    ///
    /// 校验项（任一命中 ⇒ 不许写盘）：
    ///   ① 不跳级：`Level ≤ 前一条.Level + 1`
    ///   ② 父条目必须紧跟 ≥1 个子条目（否则是空壳子菜单，渲染时被整棵剪掉 ⇒ 存了等于没存）
    ///   ③ 非 group 条目不得有子项（F3 会在解析层升格；走到这里说明绕过了 F3，只做拒绝兜底）
    ///   ④ 组标题不得带动作目标 / hide-show 标记
    ///   ⑤ 动作类型必须是 <see cref="ActionType"/> 的 7 个之一；分组类型同理
    ///   ⑥ 同一菜单（同一分组）内条目 ID 唯一
    ///   ⑦ 分组名不许为空（空名序列化出来 `!   always` 会被解析器读成"分组名=always"⇒ 写坏文件）
    ///
    /// ★ 关于"ID 唯一"的口径（判断权，已在 RESULT.md 说明）：
    ///   menus.conf **没有显式 ID 字段**，运行期命令 ID 由 MenuBuilder 按序分配；
    ///   `MenuSelfTest` 的「ID 唯一断言」断的是"每个可点条目恰好对应一个 ID、无重复"。
    ///   在配置层能让条目**身份**重复的唯一形态，就是同一菜单里出现两条
    ///   标题 / 快捷键 / 动作 / 目标 / flag **全同**的条目 —— 它们在菜单里完全无法区分，
    ///   也就是历史"重名 ⇒ 点错项 / 撞 ID"事故的配置层症状。故此处以"条目身份键"判重。
    ///   （标题相同但快捷键不同的两条**仍然合法** —— 「同名项共存」是产品的既有语义。）
    /// </summary>
    public static class MenuStructureValidator
    {
        // ---- 问题码（自检据此断言，不要随手改名）----
        public const string LevelJump = "LEVEL_JUMP";
        public const string LevelNegative = "LEVEL_NEGATIVE";
        public const string ParentNoChild = "PARENT_NO_CHILD";
        public const string LeafWithChild = "LEAF_WITH_CHILD";
        public const string GroupWithTarget = "GROUP_WITH_TARGET";
        public const string BadActionType = "BAD_ACTION_TYPE";
        public const string BadGroupType = "BAD_GROUP_TYPE";
        public const string DuplicateId = "DUPLICATE_ID";
        public const string EmptyGroupName = "EMPTY_GROUP_NAME";
        public const string NullItem = "NULL_ITEM";

        /// <summary>
        /// 全量校验。返回空列表 = 可以写盘。
        /// 逐条给可读原因（带行号 + 条目名），不短路 —— 用户一次能看到全部问题。
        /// </summary>
        public static List<MenuStructureIssue> Validate(List<MenuGroup> groups)
        {
            var issues = new List<MenuStructureIssue>();
            if (groups == null) return issues;

            // 行号基准 = 序列化头部的行数（Serialize 每次都会写同一段注释头）
            int cursor = MenuConfWriter.HeaderLineCount;

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                int groupLine = cursor + 1;
                int itemBase = cursor + 1;    // 条目 i 的行号 = itemBase + i + 1（分组行之后）

                if (g == null)
                {
                    issues.Add(new MenuStructureIssue
                    {
                        Code = BadGroupType,
                        Reason = "分组对象为空（内存模型里有个 null 分组）",
                        GroupIndex = gi,
                        LineNo = groupLine
                    });
                    cursor += 2;
                    continue;
                }

                string gname = (g.Name ?? "").Trim();
                if (gname.Length == 0)
                {
                    issues.Add(new MenuStructureIssue
                    {
                        Code = EmptyGroupName,
                        Reason = "分组名是空的 —— 写出去会变成「!   " + MenuGroup.TypeToString(g.Type)
                            + "」，解析时第一个词（" + MenuGroup.TypeToString(g.Type)
                            + "）会被当成分组名、类型变空 ⇒ 文件直接读不回来",
                        GroupIndex = gi,
                        GroupName = g.Name,
                        LineNo = groupLine
                    });
                }
                else if (gname.IndexOf(' ') >= 0 || gname.IndexOf('\t') >= 0)
                {
                    // 分组名里有空白 ⇒ 解析器按空白切分 ⇒ 第二个词会被当成"类型"
                    issues.Add(new MenuStructureIssue
                    {
                        Code = EmptyGroupName,
                        Reason = "分组名里有空格/制表符 —— 解析器按空白切分，第 1 个词才是分组名，"
                            + "后面的会被当成类型或关键词（写出去就改不回来）",
                        GroupIndex = gi,
                        GroupName = g.Name,
                        LineNo = groupLine
                    });
                }

                if (!Enum.IsDefined(typeof(GroupType), g.Type))
                {
                    issues.Add(new MenuStructureIssue
                    {
                        Code = BadGroupType,
                        Reason = "分组类型 " + (int)g.Type + " 不是合法值（只能 folder/ext/proc/text/always/fallback）",
                        GroupIndex = gi,
                        GroupName = g.Name,
                        LineNo = groupLine
                    });
                }

                var items = g.Items ?? new List<MenuItem>();
                var idSeen = new Dictionary<string, MenuItem>(StringComparer.Ordinal);

                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    int lineNo = itemBase + i + 1;

                    if (it == null)
                    {
                        issues.Add(new MenuStructureIssue
                        {
                            Code = NullItem,
                            Reason = "条目对象为空（内存模型里有个 null 条目）",
                            GroupIndex = gi,
                            GroupName = g.Name,
                            ItemIndex = i,
                            LineNo = lineNo
                        });
                        continue;
                    }

                    // ① 层级：非负 + 不跳级
                    if (it.Level < 0)
                    {
                        Add(issues, LevelNegative, it, gi, g.Name, i, lineNo,
                            "层级是 " + it.Level + "（不能为负）");
                    }
                    else if (i > 0 && items[i - 1] != null && it.Level > items[i - 1].Level + 1)
                    {
                        Add(issues, LevelJump, it, gi, g.Name, i, lineNo,
                            "跳级：上一条是 lv" + items[i - 1].Level + "、本条是 lv" + it.Level
                            + "（每层只能 +1）—— 读回来时父子关系会错位");
                    }

                    // ② / ③ 父子关系（"紧随其后"口径，与 MenuBuilder / F3 同源）
                    int childCount = 0;
                    for (int j = i + 1; j < items.Count; j++)
                    {
                        if (items[j] == null) continue;
                        if (items[j].Level <= it.Level) break;
                        childCount++;
                    }

                    if (it.Type == ActionType.Group)
                    {
                        if (childCount == 0)
                        {
                            Add(issues, ParentNoChild, it, gi, g.Name, i, lineNo,
                                "父条目后面没有紧跟任何子条目（空子菜单：菜单渲染时会被整棵剪掉，"
                                + "存进去等于没存）");
                        }
                    }
                    else if (childCount > 0)
                    {
                        Add(issues, LeafWithChild, it, gi, g.Name, i, lineNo,
                            "不是 group 却有 " + childCount + " 个子条目 —— 请把「动作类型」改成 group"
                            + "（分组标题），或把这些子条目移走");
                    }

                    // ④ 组标题不得带动作目标 / flag
                    if (it.Type == ActionType.Group)
                    {
                        string tgt = (it.Target ?? "").Trim();
                        if (tgt.Length > 0)
                        {
                            Add(issues, GroupWithTarget, it, gi, g.Name, i, lineNo,
                                "是分组标题（group）却带着动作目标「" + tgt
                                + "」—— group 没有动作，写出去读回来目标会被成解析成动作");
                        }
                        else if (it.Flag != ActionFlag.None)
                        {
                            Add(issues, GroupWithTarget, it, gi, g.Name, i, lineNo,
                                "是分组标题（group）却带着 hide/show 标记（group 没有执行窗口）");
                        }
                    }

                    // ⑤ 动作类型必须是 7 个合法值之一
                    if (!Enum.IsDefined(typeof(ActionType), it.Type))
                    {
                        Add(issues, BadActionType, it, gi, g.Name, i, lineNo,
                            "动作类型 " + (int)it.Type + " 不是合法值"
                            + "（只能 group/run/open/url/action/send/keys/---）");
                    }

                    // ⑤b 分割线不允许带标题/目标/快捷键/flag/sub —— 写出去会被丢掉，
                    //     在这里先报出来，别让用户以为改了却没落盘。
                    if (it.IsSeparator)
                    {
                        var extra = new List<string>();
                        if (!string.IsNullOrEmpty((it.Title ?? "").Trim())) extra.Add("标题「" + it.Title.Trim() + "」");
                        if (!string.IsNullOrEmpty((it.Target ?? "").Trim())) extra.Add("目标");
                        if (!string.IsNullOrEmpty((it.Hotkey ?? "").Trim())) extra.Add("快捷键");
                        if (it.Flag != ActionFlag.None) extra.Add("hide/show");
                        if (it.Sub) extra.Add("sub");
                        if (extra.Count > 0)
                        {
                            Add(issues, BadActionType, it, gi, g.Name, i, lineNo,
                                "是分割线（---）却带着 " + string.Join(" / ", extra.ToArray())
                                + " —— 保存时这些字段会被丢弃，只留一行 ---");
                        }
                    }

                    // ⑥ 同一菜单内条目 ID 唯一（身份键 = 标题+快捷键+动作+flag+目标）
                    //    ★ 分割线不参与判重：同一菜单里可以有多条分割线，它们的身份键天然相同。
                    if (!it.IsSeparator)
                    {
                        string key = IdentityKey(it);
                        MenuItem first;
                        if (idSeen.TryGetValue(key, out first))
                        {
                            string reason = "与同一菜单里第 " + (items.IndexOf(first) + 1)
                                + " 条「" + (first.Title ?? "").Trim() + "」完全重复（标题/快捷键/动作/目标都一样）"
                                + " ⇒ 菜单里两条无法区分，等于同一条目占了两个 ID";
                            Add(issues, DuplicateId, it, gi, g.Name, i, lineNo, reason);
                        }
                        else
                        {
                            idSeen[key] = it;
                        }
                    }
                }

                cursor = itemBase + items.Count;   // 最后一条条目行
                cursor += 1;                        // 组间空行
            }

            return issues;
        }

        /// <summary>条目身份键（同菜单内判重用）。空快捷键与"无快捷键"视为同一个。</summary>
        private static string IdentityKey(MenuItem it)
        {
            return (it.Title ?? "").Trim()
                + "\u0001" + ((it.Hotkey ?? "").Trim().ToLowerInvariant())
                + "\u0001" + (int)it.Type
                + "\u0001" + (int)it.Flag
                + "\u0001" + (it.Target ?? "").Trim();
        }

        private static void Add(List<MenuStructureIssue> issues, string code, MenuItem it,
            int gi, string gname, int idx, int lineNo, string reason)
        {
            issues.Add(new MenuStructureIssue
            {
                Code = code,
                Reason = reason,
                GroupIndex = gi,
                GroupName = gname,
                ItemIndex = idx,
                LineNo = lineNo,
                Item = it
            });
        }

        /// <summary>把问题列表压成一行摘要（状态行用；完整列表在弹窗里）。</summary>
        public static string Summarize(List<MenuStructureIssue> issues, int max)
        {
            if (issues == null || issues.Count == 0) return "";
            var sb = new StringBuilder();
            int n = Math.Min(max, issues.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append("\n");
                sb.Append(issues[i].Where);
            }
            if (issues.Count > n) sb.Append("\n…另有 " + (issues.Count - n) + " 处");
            return sb.ToString();
        }
    }

    /// <summary>
    /// menus.conf 写回器 —— 序列化 + **写盘前结构校验** + 备份 + 原子写 + 回读校验。
    ///
    /// ★ 安全纪律（吸取 AHK 版越界损坏配置的教训）：
    ///   ① 写前备份 menus.conf.bak.&lt;时间戳&gt;（保留最近 10 份）
    ///   ② ★ C1：**校验通过之前一个字节都不写**（不备份、不建 tmp、不 rename）
    ///   ③ 原子写：写 `*.tmp` → **回读 tmp 解析 + 结构与内存一致 + 再走一遍同一条写法逐字节一致** → rename 覆盖
    ///   ④ 回读不通过 ⇒ 删 tmp、原文件一字节不动、返回可读原因
    ///   失败语义是**全有或全无**：绝不"尽力写一半"。
    ///
    /// ★ 窄写回（2026-11-17 / 卡 B）：保存**不再整份重写**。
    ///   读入原文的原始行数组（含每行确切文本 + 行尾风格 + BOM 状态），与内存模型做行级对齐：
    ///     · 内容没变的条目行 / 分组行 —— **原字节照抄**（连手写的怪缩进/怪空格都不动）
    ///     · 内容变了的条目行 —— 只重写这一行（格式照抄 <see cref="FormatItem"/>）
    ///     · 新增条目 —— 插在上一条之后（本组最后一条之后 = 其父项子树末尾）
    ///     · 删除条目 —— 只删它自己那一行（子树按模型已展开 ⇒ 每行各删各的）
    ///     · 注释 / 空行 / 认不出的行 —— 原样保留，不猜、不合并、不删
    ///   分组数变化（新增/删除分组）等"行级对齐做不到"的情形，退回整份重写（老行为）。
    ///   窄写回产物还要过 <see cref="VerifyTmp"/>：**把产物当原文再窄写一次必须逐字节相同**，
    ///   且所有非条目行与原文逐字节相同；任一条不成立 ⇒ 退回整份重写再验（宁丢注释，不能丢保存）。
    /// </summary>
    public class MenuConfWriter
    {
        private readonly string _confPath;
        private const int MaxBackups = 10;

        public MenuConfWriter(string confPath)
        {
            _confPath = confPath;
        }

        /// <summary>序列化头部（注释块）—— 抽出来是为了让校验器能算"条目会落在第几行"。</summary>
        private static void AppendHeader(StringBuilder sb)
        {
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# RunEver 配置（由设置界面保存）");
            sb.AppendLine("# 编码: UTF-8 (无 BOM)");
            sb.AppendLine("#");
            sb.AppendLine("# 语法:");
            sb.AppendLine("#   ! <分组名>  <类型>  [关键词...]        分组（folder/ext/proc/text/always/fallback）");
            sb.AppendLine("#     <标题>  [_快捷键]  | <动作>  <目标>   条目（缩进 2 空格 = 层级）");
            sb.AppendLine("#   #                                      注释");
            sb.AppendLine("#");
            sb.AppendLine("# 动作类型:");
            sb.AppendLine("#   run    启动程序（目标 = 命令行，首个 token 走路径缓存解析）");
            sb.AppendLine("#   open   打开文件/文件夹");
            sb.AppendLine("#   url    打开网址");
            sb.AppendLine("#   action 调外部程序（cmd 管道）；可选 hide/show 控制执行窗口");
            sb.AppendLine("#   send   发送文本");
            sb.AppendLine("#   keys   发快捷键");
            sb.AppendLine("#   group  纯分组标题");
            sb.AppendLine("# ============================================================");
            sb.AppendLine();
        }

        /// <summary>序列化头部的行数（= 第一个分组行之前已经占掉的行数）。</summary>
        public static int HeaderLineCount
        {
            get
            {
                var sb = new StringBuilder();
                AppendHeader(sb);
                int n = 0;
                foreach (char c in sb.ToString()) if (c == '\n') n++;
                return n;
            }
        }

        /// <summary>序列化成分组列表 → 菜单语法文本。</summary>
        public static string Serialize(List<MenuGroup> groups)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);

            if (groups != null)
            {
                foreach (var g in groups)
                {
                    if (g == null) continue;
                    sb.AppendLine(FormatGroup(g));

                    if (g.Items != null)
                        foreach (var it in g.Items)
                        {
                            if (it == null) continue;
                            sb.AppendLine(FormatItem(it));
                        }

                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        /// <summary>单分组 → 一行文本（`! 名称  类型  [关键词...]`）。
        /// ★ 唯一实现：整份重写（<see cref="Serialize"/>）与窄写回（分组行真的改了才重写）
        ///   共用这一份，免得两条路径的格式漂移。</summary>
        public static string FormatGroup(MenuGroup g)
        {
            if (g == null) return "";
            string kw = (g.Keywords != null && g.Keywords.Count > 0)
                ? "  " + string.Join(" ", g.Keywords) : "";
            return "! " + g.Name + "  " + MenuGroup.TypeToString(g.Type) + kw;
        }

        /// <summary>单条目 → 一行文本（缩进按 Level）。</summary>
        public static string FormatItem(MenuItem it)
        {
            var sb = new StringBuilder();

            // ★★ 缩进必须是 Level*2 + 1 —— 解析器按 leading/2 反算层级。
            //    实测原始 menus.conf：Level=1 的条目缩进 = 3 空格（3/2 = 1）。
            //    曾写成 Level*2+2（=4，4/2=2）⇒ 往返后所有条目 Level 都 +1，
            //    属于会静默损坏配置的严重 bug（由往返自检抓出）。
            sb.Append(new string(' ', Math.Max(0, it.Level) * 2 + 1));

            // 分割线：整行只有 `---`（往返逐字节幂等要求：不能带标题/动作/尾随空格）
            if (it.Type == ActionType.Separator)
            {
                sb.Append("---");
                return sb.ToString();
            }

            sb.Append(it.Title);
            if (!string.IsNullOrEmpty(it.Hotkey))
                sb.Append("  _" + it.Hotkey);

            if (it.Type != ActionType.Group || !string.IsNullOrEmpty(it.Target))
            {
                sb.Append("  | ").Append(TypeToString(it.Type));

                if (it.Type == ActionType.Action && it.Flag != ActionFlag.None)
                {
                    sb.Append(' ').Append(it.Flag == ActionFlag.Show ? "show" : "hide");
                }

                // ★ 2026-11-09（U-B）：`open sub` 的修饰位必须原样回写 ——
                //   否则编辑器保存一次就把「展开」悄悄变回普通 open。
                //   ⚠ 只回写这一个词，**目录内容永不进配置**（子项只存在于渲染期）。
                if (it.Type == ActionType.Open && it.Sub)
                    sb.Append(" sub");

                // ★ 2026-11-17b（卡 E）：`off`（临时禁用）也必须原样回写 ——
                //   否则"编辑器保存一次就把禁用悄悄恢复成可点"（用户以为关了、其实能点）。
                //   写成**修饰位形态**（`| run off  "x.exe"`）而不是行尾：所有动作类型都能
                //   原样解析回来（行尾形态对 send/keys 的目标是内容、解析层不吃）。
                if (it.Off && it.Type != ActionType.Separator)
                    sb.Append(" off");

                if (!string.IsNullOrEmpty(it.Target))
                    sb.Append("  ").Append(it.Target);
            }

            return sb.ToString();
        }

        private static string TypeToString(ActionType t)
        {
            switch (t)
            {
                case ActionType.Run: return "run";
                case ActionType.Open: return "open";
                case ActionType.Url: return "url";
                case ActionType.Action: return "action";
                case ActionType.Send: return "send";
                case ActionType.Keys: return "keys";
                default: return "group";
            }
        }

        // ============================================================
        // ★ 窄写回（卡 B）：只重写变化了的条目行，其余行逐字节照抄
        // ============================================================

        /// <summary>窄写回的动作计数（写盘说明里给人看的那一句）。</summary>
        public sealed class NarrowWriteStats
        {
            public int GroupLinesRewritten;
            public int ItemsRewritten;
            public int ItemsInserted;
            public int ItemsDeleted;
            public int LinesKept;          // 原字节照抄的行数（注释/空行/未变的条目行）

            public override string ToString()
            {
                return "改 " + ItemsRewritten + " 行 / 新增 " + ItemsInserted
                    + " / 删除 " + ItemsDeleted + "；分组行改 " + GroupLinesRewritten
                    + "；其余 " + LinesKept + " 行原样保留（注释/空行未动）";
            }
        }

        /// <summary>一行原文：<see cref="Text"/> 不含行尾，<see cref="Eol"/> = "\r\n" / "\n" / "\r" / ""。</summary>
        private sealed class RawLine
        {
            public string Text;
            public string Eol;
            public RawLine(string text, string eol) { Text = text; Eol = eol; }
        }

        /// <summary>
        /// 窄写回：以 <paramref name="originalText"/> 为底，只改**确实变了**的行。
        /// 返回 null = 本次不适用（调用方退回整份重写），条件只有两个：
        ///   · 原文为空 / 拿不到老模型（调用方没读过盘）；
        ///   · 分组**数目**变了（增删分组要搬整块、还要决定被删分组里的注释往哪放，
        ///     与"非条目行条数/顺序都不许变"的不变式冲突 ⇒ 不猜，整份重写）。
        ///     分组**改名/改类型/改关键词**可以窄写（只重写那一行分组行）。
        /// </summary>
        /// <param name="oldGroups">磁盘原文的解析结果（行号来源 —— 分类口径与解析器**同一份**，不另写一套）。</param>
        public static string BuildNarrowText(string originalText, List<MenuGroup> oldGroups,
            List<MenuGroup> groups, out NarrowWriteStats stats)
        {
            stats = new NarrowWriteStats();
            if (string.IsNullOrEmpty(originalText) || oldGroups == null || groups == null) return null;

            var lines = SplitRaw(originalText);
            if (lines.Count == 0) return null;

            string defaultEol = "\r\n";
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Eol.Length > 0) { defaultEol = lines[i].Eol; break; }

            // 分组必须一一对应（数目一样），否则退回整份重写
            if (oldGroups.Count != groups.Count || oldGroups.Count == 0) return null;

            var outp = new List<RawLine>(lines.Count + 16);

            // ---- 逐组：分组行 + 本组块（块内逐条目行对齐）----
            var gline = new int[groups.Count];
            for (int i = 0; i < oldGroups.Count; i++)
            {
                var og = oldGroups[i];
                if (og == null) return null;
                int ln = og.Line - 1;
                if (ln < 0 || ln >= lines.Count) return null;
                string t = lines[ln].Text.Trim(' ', '\t');
                if (t.Length == 0 || t[0] != '!') return null;      // 行号对不上 ⇒ 不猜，退回整份重写
                if (i > 0 && ln <= gline[i - 1]) return null;
                gline[i] = ln;
            }

            // 第一个分组之前的内容（文件头注释块等）原样照抄
            for (int i = 0; i < gline[0]; i++) { outp.Add(lines[i]); stats.LinesKept++; }

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                var og = oldGroups[gi];
                if (g == null || og == null) return null;

                int gidx = gline[gi];
                int blockEnd = (gi + 1 < groups.Count) ? gline[gi + 1] : lines.Count;

                // ---- 分组行：语义没变就照抄原字节（手写的怪空格也不动）----
                if (SameGroup(og, g))
                {
                    outp.Add(lines[gidx]); stats.LinesKept++;
                }
                else
                {
                    outp.Add(new RawLine(FormatGroup(g), EolOr(lines[gidx], defaultEol)));
                    stats.GroupLinesRewritten++;
                }

                // ---- 本组条目行的行号 = 解析器给的行号（权威，不另写一套分类）----
                var oldItems = og.Items ?? new List<MenuItem>();
                var newItems = g.Items ?? new List<MenuItem>();
                var oldLine = new int[oldItems.Count];
                for (int k = 0; k < oldItems.Count; k++)
                {
                    if (oldItems[k] == null) return null;
                    int l2 = oldItems[k].Line - 1;
                    if (l2 <= gidx || l2 >= blockEnd) return null;          // 行号越界 / 串到别组
                    if (k > 0 && l2 <= oldLine[k - 1]) return null;         // 行号必须严格递增
                    oldLine[k] = l2;
                }

                int[] match = AlignItems(oldItems, newItems);   // match[k] = 新下标；-1 = 已删除
                var matchedNew = new bool[newItems.Count];
                for (int k = 0; k < match.Length; k++)
                    if (match[k] >= 0 && match[k] < newItems.Count) matchedNew[match[k]] = true;

                int pos = gidx + 1;
                int nj = 0;
                for (int k = 0; k < oldItems.Count; k++)
                {
                    // 条目行之间的注释 / 空行：原样照抄
                    while (pos < oldLine[k]) { outp.Add(lines[pos]); stats.LinesKept++; pos++; }
                    if (pos != oldLine[k]) return null;         // 行号自相矛盾 ⇒ 不猜，退回整份重写

                    int target = match[k];
                    if (target < 0)
                    {
                        pos++;                                  // ★ 删除：只丢它自己这一行
                        stats.ItemsDeleted++;
                        continue;
                    }

                    // 新增条目插在上一条之后（= 其父项子树末尾）
                    while (nj < target)
                    {
                        if (!matchedNew[nj] && newItems[nj] != null)
                        {
                            outp.Add(new RawLine(FormatItem(newItems[nj]), EolOfLast(outp, defaultEol)));
                            stats.ItemsInserted++;
                        }
                        nj++;
                    }

                    var ni = newItems[target];
                    if (ni == null) return null;
                    if (SameItem(oldItems[k], ni))
                    {
                        outp.Add(lines[pos]);                   // 内容没变：原字节照抄
                        stats.LinesKept++;
                    }
                    else
                    {
                        outp.Add(new RawLine(FormatItem(ni), EolOr(lines[pos], defaultEol)));
                        stats.ItemsRewritten++;
                    }
                    pos++;
                    nj = target + 1;
                }

                // 本组尾部的新增条目：放在"最后一条之后、组间空行之前"，
                // 否则新条目会掉到下一组的视觉块里（语义没错，但看着像写错了）。
                while (nj < newItems.Count)
                {
                    if (!matchedNew[nj] && newItems[nj] != null)
                    {
                        outp.Add(new RawLine(FormatItem(newItems[nj]), EolOfLast(outp, defaultEol)));
                        stats.ItemsInserted++;
                    }
                    nj++;
                }

                // 本组块剩余行（组间空行 / 组尾注释）原样照抄
                while (pos < blockEnd) { outp.Add(lines[pos]); stats.LinesKept++; pos++; }
            }

            return JoinRaw(outp, defaultEol);
        }

        /// <summary>
        /// 条目行级对齐（同一分组内，按顺序单调匹配）。
        /// 判据是"逐字段完全相同"（标题/快捷键/动作/flag/sub/目标/层级）：
        ///   · 对得上 ⇒ 同一行（内容没变 ⇒ 原字节保留）
        ///   · 对不上但有窗口内的错位 ⇒ 判定为插入 / 删除
        ///   · 都对不上 ⇒ 判定为"就地修改"，仍占这一行（★ 只重写这一行）
        /// 返回 match[k] = 新条目下标，-1 = 该老条目已删除。
        /// 复杂度 O(n * Window)：条目数上万（现场 24960 项）也不能退化成 O(n²)。
        /// </summary>
        private static int[] AlignItems(List<MenuItem> oldItems, List<MenuItem> newItems)
        {
            int m = oldItems.Count, n = newItems.Count;
            var match = new int[m];
            for (int k = 0; k < m; k++) match[k] = -1;

            const int Window = 32;
            int j = 0;
            for (int k = 0; k < m; k++)
            {
                if (j >= n) break;                                  // 后面的老条目全被删了

                if (SameItem(oldItems[k], newItems[j])) { match[k] = j; j++; continue; }

                int ins = -1;      // 新条目多出来的（插入）
                for (int d = 1; d <= Window && j + d < n; d++)
                    if (SameItem(oldItems[k], newItems[j + d])) { ins = d; break; }

                int del = -1;      // 老条目多出来的（删除）
                for (int d = 1; d <= Window && k + d < m; d++)
                    if (SameItem(oldItems[k + d], newItems[j])) { del = d; break; }

                if (del > 0 && (ins < 0 || del <= ins)) continue;   // 本条删除，下一轮再对上
                if (ins > 0) { j += ins; match[k] = j; j++; continue; }
                match[k] = j; j++;                                  // 就地修改
            }
            return match;
        }

        private static bool SameItem(MenuItem a, MenuItem b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.Title ?? "", b.Title ?? "", StringComparison.Ordinal)
                && string.Equals(a.Hotkey ?? "", b.Hotkey ?? "", StringComparison.Ordinal)
                && string.Equals(a.Target ?? "", b.Target ?? "", StringComparison.Ordinal)
                && a.Type == b.Type && a.Flag == b.Flag && a.Sub == b.Sub && a.Level == b.Level
                // ★ 卡 E：`off` 参与"内容是否没变"的判定 —— 用户把一条从禁用改回可用，
                //   必须真的重写这一行；不比较就会照抄原字节 ⇒ 改了却没落盘（假保存）。
                && a.Off == b.Off;
        }

        private static bool SameGroup(MenuGroup a, MenuGroup b)
        {
            if (a == null || b == null) return false;
            if (!string.Equals(a.Name ?? "", b.Name ?? "", StringComparison.Ordinal)) return false;
            if (a.Type != b.Type) return false;
            var ka = a.Keywords ?? new List<string>();
            var kb = b.Keywords ?? new List<string>();
            if (ka.Count != kb.Count) return false;
            for (int i = 0; i < ka.Count; i++)
                if (!string.Equals(ka[i] ?? "", kb[i] ?? "", StringComparison.Ordinal)) return false;
            return true;
        }

        private static string EolOr(RawLine line, string fallback)
        {
            return (line != null && line.Eol.Length > 0) ? line.Eol : fallback;
        }

        private static string EolOfLast(List<RawLine> outp, string fallback)
        {
            if (outp.Count == 0) return fallback;
            string e = outp[outp.Count - 1].Eol;
            return e.Length > 0 ? e : fallback;
        }

        /// <summary>按行拆原文，**保留每行确切文本与行尾风格**（CRLF / LF / CR / 末行无行尾）。</summary>
        private static List<RawLine> SplitRaw(string s)
        {
            var list = new List<RawLine>();
            int i = 0;
            while (i < s.Length)
            {
                int start = i;
                while (i < s.Length && s[i] != '\r' && s[i] != '\n') i++;
                string text = s.Substring(start, i - start);
                string eol = "";
                if (i < s.Length)
                {
                    if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n') { eol = "\r\n"; i += 2; }
                    else { eol = s[i] == '\r' ? "\r" : "\n"; i++; }
                }
                list.Add(new RawLine(text, eol));
            }
            return list;
        }

        /// <summary>拼回文本。中间行没有行尾的（原文末行无换行，后面又插了新行）补默认行尾。</summary>
        private static string JoinRaw(List<RawLine> lines, string defaultEol)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                string eol = lines[i].Eol;
                if (eol.Length == 0 && i < lines.Count - 1) eol = defaultEol;
                sb.Append(lines[i].Text).Append(eol);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 非条目行 = 原文里既不是分组行、也不是任何条目行的行（注释 / 空行 / 认不出的行）。
        /// 窄写回承诺这些行**逐字节不变**；<see cref="VerifyTmp"/> 与自检都按这个口径比对。
        /// </summary>
        public static List<string> OpaqueLines(string text, List<MenuGroup> parsedGroups)
        {
            var result = new List<string>();
            if (text == null) return result;
            var lines = SplitRaw(text);
            var skip = new HashSet<int>();
            if (parsedGroups != null)
            {
                foreach (var g in parsedGroups)
                {
                    if (g == null) continue;
                    skip.Add(g.Line - 1);
                    if (g.Items == null) continue;
                    foreach (var it in g.Items)
                        if (it != null) skip.Add(it.Line - 1);
                }
            }
            for (int i = 0; i < lines.Count; i++)
                if (!skip.Contains(i)) result.Add(lines[i].Text + "\u0000" + lines[i].Eol);
            return result;
        }

        /// <summary>
        /// 写盘前的结构校验（对外暴露给 UI / 自检；与 <see cref="Save"/> 里那把闸同一份实现）。
        /// </summary>
        public static List<MenuStructureIssue> Validate(List<MenuGroup> groups)
        {
            return MenuStructureValidator.Validate(groups);
        }

        public bool Save(List<MenuGroup> groups, out string message)
        {
            List<MenuStructureIssue> ignored;
            return Save(groups, out message, out ignored);
        }

        /// <summary>
        /// 保存：**结构校验 → 空写硬闸 → 备份 → 写 tmp → 回读校验 tmp → rename 覆盖**。
        /// 返回 (成功, 说明)；结构不合法时 <paramref name="issues"/> 带逐条原因（弹窗用）。
        ///
        /// ★ B4a-1（P0 数据安全）：空写硬闸兜住所有调用方（设置页 / 测试缝 / 将来的 CLI）。
        ///   原因：`MenuEditorPage.Reload()` 解析失败时集合为空，而旧逻辑照样写盘
        ///   （`0 组 0 条`）且回读校验 `0==0` 判"成功" ⇒ 一键清空 menus.conf。
        ///   规则：
        ///     · 待写集合为空 / 0 条目 + 原文件**存在且分组数 &gt; 0** ⇒ **拒绝写入**
        ///     · 待写集合为空 + 原文件存在但**读不出结构**（已损坏）⇒ 同样拒绝
        ///     · 原文件本来就不存在 / 本来就是 0 分组 ⇒ 允许（首次创建、保存空白初始态）
        ///
        /// ★ C1：结构校验放在**任何写动作之前**（备份/ tmp / rename 全在它后面）。
        ///   顺序一旦反了（先写 tmp 再校验再 rename），"用户点保存"那一刻就已经动过磁盘，
        ///   等于没闸。
        ///
        /// ★ 卡 B：待写文本先由 <see cref="BuildNarrowText"/> 窄写回产出（行级对齐），
        ///   做不到才用 <see cref="Serialize"/> 整份重写；窄写回产物必须同时过
        ///   "结构与模型一致 / 再窄写一次逐字节相同 / 非条目行逐字节相同"三关，
        ///   任一关不过就**退回整份重写再验**（能保存优先于保注释）。
        /// </summary>
        public bool Save(List<MenuGroup> groups, out string message, out List<MenuStructureIssue> issues)
        {
            message = "";
            issues = new List<MenuStructureIssue>();
            string backupPath = null;
            string tmp = _confPath + ".tmp";
            bool replaced = false;

            try
            {
                // ---- 待写内容的规模（空判定用）----
                int newGroups = (groups == null) ? 0 : groups.Count;
                int newItems = 0;
                if (groups != null)
                    foreach (var g in groups)
                        if (g != null && g.Items != null) newItems += g.Items.Count;

                // ---- 磁盘现状（硬闸用）----
                int diskGroupsBefore = 0, diskItemsBefore = 0;
                bool diskParsed = true;      // 原文件不存在 ⇒ 视为"没有可保护的内容"
                ConfParser diskParser = null; // ★ 窄写回的行号来源（与硬闸共用同一次解析）
                if (File.Exists(_confPath) && new FileInfo(_confPath).Length > 0)
                {
                    try
                    {
                        var old = new ConfParser(_confPath);
                        old.Parse();
                        diskParser = old;
                        diskGroupsBefore = old.Groups.Count;
                        foreach (var g in old.Groups) diskItemsBefore += g.Items.Count;
                    }
                    catch
                    {
                        diskParsed = false;   // 原文件坏到解析不了 ⇒ 绝不能用空集合覆盖它
                    }
                }

                // ---- ★ B4a-1 兜底硬闸 ----
                if (newGroups == 0 || newItems == 0)
                {
                    if (!diskParsed)
                    {
                        message = "拒绝写入：这次要保存的配置是空的（分组 " + newGroups
                            + "，条目 " + newItems + "），而磁盘上的 menus.conf 存在但读不出结构"
                            + "（可能已损坏）。为免覆盖掉可恢复的原文件，已取消保存，原文件未改动。";
                        return false;
                    }
                    if (diskGroupsBefore > 0)
                    {
                        message = "拒绝写入：这次要保存的配置是空的（分组 " + newGroups
                            + "，条目 " + newItems + "），而磁盘上的 menus.conf 有 "
                            + diskGroupsBefore + " 个分组 / " + diskItemsBefore + " 个条目。"
                            + "多半是配置没读进来 —— 已取消保存，原文件未改动。"
                            + "（确实要清空请用文本编辑器手工删，或先备份再改）";
                        return false;
                    }
                }

                // ---- ★ C1：写盘前的结构校验（这一步之前磁盘一个字节都没动）----
                issues = MenuStructureValidator.Validate(groups);
                if (issues.Count > 0)
                {
                    message = "结构校验没通过（" + issues.Count + " 处），没有写盘，menus.conf 原样未动：\n"
                        + MenuStructureValidator.Summarize(issues, 5);
                    return false;
                }

                // ---- ★ 待写内容：优先**窄写回**（只改变化的行），做不到才整份重写 ----
                string originalText;
                bool originalHasBom;
                originalText = ReadOriginalText(out originalHasBom);

                NarrowWriteStats narrow = null;
                string text = null;
                if (originalText != null && diskParser != null)
                    text = BuildNarrowText(originalText, diskParser.Groups, groups, out narrow);
                bool narrowUsed = text != null;
                if (text == null) text = Serialize(groups);
                string narrowNote = "";

                // ---- ① 备份（只有"真要写"才做；被拒的保存不留垃圾备份）----
                if (File.Exists(_confPath))
                {
                    string dir = Path.GetDirectoryName(_confPath);
                    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    backupPath = Path.Combine(dir, "menus.conf.bak." + stamp);
                    try
                    {
                        File.Copy(_confPath, backupPath, true);
                        CleanupBackups(dir);
                    }
                    catch { backupPath = null; }
                }

                // ---- ② 写 tmp（BOM 状态：窄写回原样保留；整份重写沿用"UTF-8 无 BOM"）----
                WriteTmp(tmp, text, narrowUsed && originalHasBom);

                // ---- ③ ★ 回读校验 tmp（通过才允许 rename）----
                string why;
                bool verified = VerifyTmp(tmp, groups, text,
                    narrowUsed ? originalText : null, narrowUsed ? diskParser.Groups : null, out why);

                // ★ 窄写回自校验没过 ⇒ 退回整份重写再验一次。
                //   口径：**"能保存"优先于"保留注释"** —— 宁可这一次丢注释，也不能让用户存不进去。
                if (!verified && narrowUsed)
                {
                    text = Serialize(groups);
                    narrowUsed = false;
                    narrow = null;
                    WriteTmp(tmp, text, false);
                    verified = VerifyTmp(tmp, groups, text, null, null, out why);
                    if (verified) narrowNote = "\n（窄写回自校验未通过，本次已退回整份重写）";
                }

                if (!verified)
                {
                    TryDelete(tmp);
                    message = "已取消保存（menus.conf 原样未动）：" + why;
                    return false;
                }

                // ---- ④ 原子替换 ----
                if (File.Exists(_confPath))
                {
                    try
                    {
                        File.Replace(tmp, _confPath, null);   // 同一目录 ⇒ NTFS 上是原子替换
                    }
                    catch (Exception)
                    {
                        // 个别文件系统不支持 Replace ⇒ 退化成"删 + 移"（窗口极短，且有备份兜底）
                        File.Delete(_confPath);
                        File.Move(tmp, _confPath);
                    }
                    replaced = true;
                }
                else
                {
                    File.Move(tmp, _confPath);
                    replaced = true;
                }

                TryDelete(tmp);

                message = "已保存（分组 " + groups.Count + "，条目 " + newItems + "）"
                    + (narrowUsed && narrow != null ? "\n窄写回：" + narrow.ToString() : "")
                    + narrowNote
                    + (backupPath != null ? "\n备份：" + Path.GetFileName(backupPath) : "");
                return true;
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                if (replaced) Rollback(backupPath);
                message = "保存失败：" + ex.Message
                    + (replaced
                        ? (string.IsNullOrEmpty(backupPath) ? "（已尝试回滚）" : "（已回滚）")
                        : "（原文件未改动）");
                return false;
            }
        }

        /// <summary>读原文（原样字节 → 文本）。拿不到 / 不是 UTF-8 系 ⇒ null（本次不窄写）。
        /// <paramref name="hasBom"/> = 原文带 UTF-8 BOM（窄写回要把它原样写回去）。</summary>
        private string ReadOriginalText(out bool hasBom)
        {
            hasBom = false;
            try
            {
                if (!File.Exists(_confPath)) return null;
                byte[] raw = File.ReadAllBytes(_confPath);
                if (raw.Length == 0) return null;

                // UTF-16 / UTF-32 的 BOM：解析器本身按 UTF-8 读，这类文件不窄写（别在半懂的状态下改写它）
                if (raw.Length >= 2)
                {
                    bool le16 = raw[0] == 0xFF && raw[1] == 0xFE;
                    bool be16 = raw[0] == 0xFE && raw[1] == 0xFF;
                    if (le16 || be16) return null;
                }

                hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
                return hasBom
                    ? Encoding.UTF8.GetString(raw, 3, raw.Length - 3)
                    : Encoding.UTF8.GetString(raw);
            }
            catch { return null; }
        }

        private static void WriteTmp(string tmp, string text, bool withBom)
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            File.WriteAllText(tmp, text, new UTF8Encoding(withBom));
        }

        /// <summary>
        /// 回读 <paramref name="tmp"/> 校验四件事，任一不成立都不许覆盖原文件：
        ///   ① 能解析（解析器不抛异常）；
        ///   ② 解析结果与内存模型**逐条一致**（分组数 / 条目数 / 标题 / 层级 / 类型 / 目标 / 快捷键）；
        ///   ③ 幂等：**把写出的内容当原文再窄写一次，必须逐字节相同**（整份重写的产物则退回
        ///      "解析后再序列化逐字节相同"这同一条判据）—— 破了就会"存一次变一次"；
        ///   ④ 窄写回时：**所有非条目行与原文逐字节相同**（注释 / 空行一个都不许动）。
        /// 另外对**落盘内容**再跑一遍结构校验（保证磁盘上那份自身合法，而不只是内存那份合法）。
        /// </summary>
        /// <param name="originalText">窄写回前的原文（整份重写传 null ⇒ 跳过 ④）。</param>
        /// <param name="oldParsedGroups">原文的解析结果（行号来源；整份重写传 null）。</param>
        private static bool VerifyTmp(string tmp, List<MenuGroup> groups, string text,
            string originalText, List<MenuGroup> oldParsedGroups, out string why)
        {
            why = "";
            ConfParser check;
            try
            {
                check = new ConfParser(tmp);
                check.Parse();
            }
            catch (Exception ex)
            {
                why = "写出的临时文件解析不回来（" + ex.Message + "）—— 已删除临时文件，原文件未改动";
                return false;
            }

            if (groups != null && check.Groups.Count != groups.Count)
            {
                why = "回读分组数 " + check.Groups.Count + " ≠ 期望 " + groups.Count
                    + " —— 已删除临时文件，原文件未改动";
                return false;
            }

            int wantItems = 0;
            foreach (var g in groups) if (g != null && g.Items != null) wantItems += g.Items.Count;
            int gotItems = 0;
            foreach (var g in check.Groups) gotItems += g.Items.Count;
            if (wantItems != gotItems)
            {
                why = "回读条目数 " + gotItems + " ≠ 期望 " + wantItems
                    + " —— 已删除临时文件，原文件未改动";
                return false;
            }

            // 逐条比对（标题/层级是第一现场；类型/目标/快捷键变了同样算"写坏了"）
            for (int gi = 0; gi < groups.Count && gi < check.Groups.Count; gi++)
            {
                var a = groups[gi];
                var b = check.Groups[gi];
                if (a == null) continue;
                if ((a.Name ?? "") != (b.Name ?? ""))
                {
                    why = "回读第 " + (gi + 1) + " 个分组名对不上（存「" + a.Name + "」/ 盘「" + b.Name + "」）"
                        + " —— 已删除临时文件，原文件未改动";
                    return false;
                }
                var ai = a.Items ?? new List<MenuItem>();
                for (int k = 0; k < ai.Count && k < b.Items.Count; k++)
                {
                    var x = ai[k];
                    var y = b.Items[k];
                    if (x == null) continue;
                    if ((x.Title ?? "") != (y.Title ?? "") || x.Level != y.Level
                        || x.Type != y.Type || (x.Target ?? "") != (y.Target ?? "")
                        || (x.Hotkey ?? "") != (y.Hotkey ?? "") || x.Flag != y.Flag
                        // ★ 卡 E：`off` 也要核 —— 写丢了它等于"临时关闭"悄悄失效
                        || x.Off != y.Off)
                    {
                        why = "回读不一致：分组「" + a.Name + "」第 " + (k + 1) + " 条（存「"
                            + x.Title + " lv" + x.Level + " " + x.Type + "」，盘「"
                            + y.Title + " lv" + y.Level + " " + y.Type + "」）"
                            + " —— 已删除临时文件，原文件未改动";
                        return false;
                    }
                }
            }

            // ③ 幂等：把写出的内容当原文**再走一遍同一条写法**，必须逐字节相同
            //     · 窄写回 ⇒ 再窄写一次（老模型 = 刚回读出来的模型 ⇒ 应当一个字都不改）
            //     · 整份重写 ⇒ 解析后再序列化（老判据原样保留）
            NarrowWriteStats againStats;
            string again = (originalText != null)
                ? BuildNarrowText(text, check.Groups, check.Groups, out againStats)
                : Serialize(check.Groups);
            if (again == null) again = Serialize(check.Groups);
            if (!string.Equals(again, text, StringComparison.Ordinal))
            {
                why = "回读后再走一遍同一条写法与写入内容不一致（存 " + text.Length + "B / 回读再写 "
                    + again.Length + "B）—— 存一次变一次，已删除临时文件，原文件未改动";
                return false;
            }

            // ③b 窄写回：非条目行（注释 / 空行 / 认不出的行）必须与原文**逐字节相同**
            if (originalText != null)
            {
                var before = OpaqueLines(originalText, oldParsedGroups);
                var after = OpaqueLines(text, check.Groups);
                int diff = -1;
                for (int i = 0; i < Math.Min(before.Count, after.Count); i++)
                    if (!string.Equals(before[i], after[i], StringComparison.Ordinal)) { diff = i; break; }
                if (diff < 0 && before.Count != after.Count) diff = Math.Min(before.Count, after.Count);
                if (diff >= 0)
                {
                    string a1 = diff < before.Count ? before[diff].Replace("\u0000", "⟪EOL⟫") : "<无>";
                    string b1 = diff < after.Count ? after[diff].Replace("\u0000", "⟪EOL⟫") : "<无>";
                    why = "窄写回动了非条目行（第 " + (diff + 1) + " 行非条目行：原「" + a1 + "」/ 新「" + b1
                        + "」，原文 " + before.Count + " 行 / 新文 " + after.Count + " 行）"
                        + " —— 已删除临时文件，原文件未改动";
                    return false;
                }
            }

            // ④ 落盘内容自身的结构校验
            var diskIssues = MenuStructureValidator.Validate(check.Groups);
            if (diskIssues.Count > 0)
            {
                why = "写出内容自身结构不合法（" + diskIssues[0].Where + (diskIssues.Count > 1 ? " 等 " + diskIssues.Count + " 处" : "") + "）"
                    + " —— 已删除临时文件，原文件未改动";
                return false;
            }

            return true;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private void Rollback(string backupPath)
        {
            if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath)) return;
            try
            {
                File.Copy(backupPath, _confPath, true);
            }
            catch { }
        }

        /// <summary>保留最近 N 份备份。</summary>
        private static void CleanupBackups(string dir)
        {
            try
            {
                var files = new List<string>(Directory.GetFiles(dir, "menus.conf.bak.*"));
                if (files.Count <= MaxBackups) return;

                files.Sort(StringComparer.OrdinalIgnoreCase);
                int remove = files.Count - MaxBackups;
                for (int i = 0; i < remove; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }
    }
}
