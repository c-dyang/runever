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
                            + "（只能 group/run/open/url/action/send/keys）");
                    }

                    // ⑥ 同一菜单内条目 ID 唯一（身份键 = 标题+快捷键+动作+flag+目标）
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
    ///   ③ 原子写：写 `*.tmp` → **回读 tmp 解析 + 结构与内存一致 + 再序列化逐字节一致** → rename 覆盖
    ///   ④ 回读不通过 ⇒ 删 tmp、原文件一字节不动、返回可读原因
    ///   失败语义是**全有或全无**：绝不"尽力写一半"。
    ///
    /// ⚠️ 已知代价：注释会丢失（分组/条目的自由文本行无法保留原始装饰注释）。
    ///    如需保留注释请用「窄写回」模式（只改条目行），本轮未实现。
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
                    string kw = (g.Keywords != null && g.Keywords.Count > 0)
                        ? "  " + string.Join(" ", g.Keywords) : "";
                    sb.AppendLine("! " + g.Name + "  " + MenuGroup.TypeToString(g.Type) + kw);

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

        /// <summary>单条目 → 一行文本（缩进按 Level）。</summary>
        public static string FormatItem(MenuItem it)
        {
            var sb = new StringBuilder();

            // ★★ 缩进必须是 Level*2 + 1 —— 解析器按 leading/2 反算层级。
            //    实测原始 menus.conf：Level=1 的条目缩进 = 3 空格（3/2 = 1）。
            //    曾写成 Level*2+2（=4，4/2=2）⇒ 往返后所有条目 Level 都 +1，
            //    属于会静默损坏配置的严重 bug（由往返自检抓出）。
            sb.Append(new string(' ', Math.Max(0, it.Level) * 2 + 1));

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
                if (File.Exists(_confPath) && new FileInfo(_confPath).Length > 0)
                {
                    try
                    {
                        var old = new ConfParser(_confPath);
                        old.Parse();
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

                string text = Serialize(groups);

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

                // ---- ② 写 tmp ----
                if (File.Exists(tmp)) File.Delete(tmp);
                File.WriteAllText(tmp, text, new UTF8Encoding(false));

                // ---- ③ ★ 回读校验 tmp（通过才允许 rename）----
                string why;
                if (!VerifyTmp(tmp, groups, text, out why))
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

        /// <summary>
        /// 回读 <paramref name="tmp"/> 校验三件事，任一不成立都不许覆盖原文件：
        ///   ① 能解析（解析器不抛异常）；
        ///   ② 解析结果与内存模型**逐条一致**（分组数 / 条目数 / 标题 / 层级 / 类型 / 目标 / 快捷键）；
        ///   ③ 解析后再序列化与写入内容**逐字节一致**（否则"存一次变一次"，幂等性破了）。
        /// 另外对**落盘内容**再跑一遍结构校验（保证磁盘上那份自身合法，而不只是内存那份合法）。
        /// </summary>
        private static bool VerifyTmp(string tmp, List<MenuGroup> groups, string text, out string why)
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
                        || (x.Hotkey ?? "") != (y.Hotkey ?? "") || x.Flag != y.Flag)
                    {
                        why = "回读不一致：分组「" + a.Name + "」第 " + (k + 1) + " 条（存「"
                            + x.Title + " lv" + x.Level + " " + x.Type + "」，盘「"
                            + y.Title + " lv" + y.Level + " " + y.Type + "」）"
                            + " —— 已删除临时文件，原文件未改动";
                        return false;
                    }
                }
            }

            // ③ 逐字节幂等：解析后再序列化必须与刚写出的内容完全一样
            string again = Serialize(check.Groups);
            if (!string.Equals(again, text, StringComparison.Ordinal))
            {
                why = "回读后再序列化与写入内容不一致（存 " + text.Length + "B / 回读再写 "
                    + again.Length + "B）—— 存一次变一次，已删除临时文件，原文件未改动";
                return false;
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
