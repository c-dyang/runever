using System;
using System.Collections.Generic;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// 缺件过滤 —— 「本机解析不到的条目自动隐藏」（用户 2026-10-03 需求 + 两态开关）。
    ///
    /// 场景：menus.conf 可能是在**另一台机器**上配好的（装的东西不一样），
    ///   搬过来后那些"本机没装"的条目点了报错/没反应。
    ///
    /// 判定口径（用户定案：**run + open 两类都判**）：
    ///   · run  —— 目标首个 token（程序名）在【路径缓存 → PATH → App Paths】都解析不到 ⇒ 判为缺件
    ///   · open —— 目标（去引号后）文件/目录都不存在 ⇒ 判为缺件
    ///   · url / action / send / keys / 子菜单标题 —— **不判**（无本地依赖，误杀代价大）
    ///   · 目标里含 `%`（`%file%/%text%/%clip%/%win%/%proc%/%s` 等运行时占位符）—— **不判**
    ///     （运行前无法确定，宁可留着）
    ///
    /// ★ 刻意**不调 es/EV 搜索**：热键路径的纪律是零多余 IO。
    ///   全量解析（含搜索）交给「刷新路径缓存」与「全盘自检」两个显式入口，
    ///   它们把结果落进 `cache\paths.txt`，这里只查缓存 + 系统原生解析（毫秒级）。
    ///
    /// ★ 非破坏性：返回新的分组/条目列表，不改动传入对象（调用方可安全复用原配置）。
    /// ★ 统计口径：每个条目只计一次（"检查/隐藏/跳过"三者互斥）。
    /// </summary>
    public static class MissingFilter
    {
        public class FilterResult
        {
            /// <summary>参与存在性判定的**叶条目**数（run/open 且目标无占位符）。</summary>
            public int Checked;

            /// <summary>未参与判定的**叶条目**数（含占位符目标 / url·action·send·keys）。</summary>
            public int Skipped;

            /// <summary>被丢弃的条目数 = 判定为缺件的叶条目 + 因"子项全缺"被丢弃的子菜单标题。</summary>
            public int Hidden;

            /// <summary>
            /// 口径不变式（自检断言用）：
            ///   Checked + Skipped == 全部叶条目（每个条目只在二者之一里计一次）
            ///   Hidden ⊆ Checked（叶条目）+ 空子菜单标题
            ///   子菜单标题不参与 Checked/Skipped（它没有可判定的目标）。
            /// </summary>
            public List<string> HiddenTitles = new List<string>();

            public override string ToString()
            {
                return "检查=" + Checked + " 隐藏=" + Hidden + " 跳过=" + Skipped;
            }
        }

        /// <summary>过滤产物：新分组列表 + 统计（调用方两者都要）。</summary>
        public class FilterOutput
        {
            public List<MenuGroup> Groups;
            public FilterResult Stats;

            public FilterOutput()
            {
                Groups = new List<MenuGroup>();
                Stats = new FilterResult();
            }
        }

        private enum Decision
        {
            Keep,       // 保留
            Drop,       // 隐藏（本机缺失）
            Skip        // 不参与判定
        }

        /// <summary>
        /// 过滤分组列表。enabled=false 时 Stats 全零、Groups 原样返回**同一批对象**
        /// （零开销、零行为变化）。
        /// </summary>
        public static FilterOutput Apply(List<MenuGroup> groups, PathCache cache,
            bool enabled, Action<string> log)
        {
            var output = new FilterOutput();

            if (groups == null) return output;
            if (!enabled)                  // 关 = 完全不处理（用户定案的两态）
            {
                output.Groups = groups;
                return output;
            }

            var res = output.Stats;
            var outGroups = new List<MenuGroup>();

            foreach (var g in groups)
            {
                var ng = new MenuGroup
                {
                    Name = g.Name,
                    Type = g.Type,
                    Keywords = g.Keywords,
                    Line = g.Line,
                    Items = new List<MenuItem>()
                };

                var src = g.Items;
                for (int i = 0; i < src.Count; i++)
                {
                    var it = src[i];

                    // ---- 子菜单标题：子项全被隐藏 ⇒ 标题也不显示（免得留个点了没反应的空壳）----
                    if (it.IsGroupHeader)
                    {
                        int j = i + 1;
                        while (j < src.Count && src[j].Level > it.Level) j++;

                        bool hasChildren = (j > i + 1);
                        bool anyKeep = false;
                        for (int k = i + 1; k < j; k++)
                        {
                            if (Peek(src[k], cache) != Decision.Drop) { anyKeep = true; break; }
                        }

                        if (hasChildren && !anyKeep)
                        {
                            res.Hidden++;
                            res.HiddenTitles.Add(it.Title + "（空子菜单）");
                            if (log != null)
                                log("缺件隐藏: [" + g.Name + "] " + it.Title + "（子项全部本机缺失）");
                            continue;
                        }

                        ng.Items.Add(it.Clone());
                        continue;
                    }

                    // ---- 普通条目：判定 + 一次计数 ----
                    var d = Peek(it, cache);

                    if (d == Decision.Skip)
                    {
                        res.Skipped++;
                        ng.Items.Add(it.Clone());
                        continue;
                    }

                    res.Checked++;
                    if (d == Decision.Keep)
                    {
                        ng.Items.Add(it.Clone());
                    }
                    else
                    {
                        res.Hidden++;
                        res.HiddenTitles.Add(it.Title);
                        if (log != null)
                            log("缺件隐藏: [" + g.Name + "] " + it.Title + "  → " + it.Target);
                    }
                }

                outGroups.Add(ng);
            }

            if (log != null && res.Hidden > 0)
                log("缺件过滤: " + res.ToString());

            output.Groups = outGroups;
            return output;
        }

        /// <summary>
        /// 纯判定（不写统计）—— 子菜单预判与主循环都用它，保证计数只发生一次。
        ///
        /// ★ C3（薄封装口径）：本方法**自己不再读盘**。逐条的"盘上事实"判定统一转发给
        ///   解析层的唯一 IO 源 `PathResolvePolicy.JudgeEntry`（路径缓存 / 自带路径 /
        ///   PATH + App Paths / %ENV% 展开都在那边）；本文件只保留**结构处理**
        ///   （子菜单预判、统计口径、非破坏性克隆）——即"数据注入 / 转发"。
        /// </summary>
        private static Decision Peek(MenuItem it, PathCache cache)
        {
            var j = PathResolvePolicy.JudgeEntry(it, cache);
            switch (j)
            {
                case EntryJudgement.Keep: return Decision.Keep;
                case EntryJudgement.Drop: return Decision.Drop;
                default: return Decision.Skip;
            }
        }

        /// <summary>取命令行的首个 token（转发 —— 单一源在解析层 `PathResolvePolicy`）。</summary>
        public static string FirstToken(string target)
        {
            return PathResolvePolicy.FirstToken(target);
        }

        /// <summary>去引号（转发 —— 单一源在解析层）。</summary>
        public static string StripQuotes(string s)
        {
            return PathResolvePolicy.StripQuotes(s);
        }

        /// <summary>目标里含运行时占位符（`%`）⇒ 判不了，不隐藏（转发）。</summary>
        public static bool NeedsRuntimeValue(string s)
        {
            return PathResolvePolicy.NeedsRuntimeValue(s);
        }
    }
}
