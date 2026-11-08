using System;
using System.Collections.Generic;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// 判定引擎 —— 架构契约 §5 的 R1–R9 逐条落地。
    ///
    /// ★ 硬边界（契约 §2）：**纯函数、无状态、零 IO**。
    ///   本文件不许出现路径存在性判定、注册表、外部进程、缓存读取 ——
    ///   需要"盘上事实"的一律由 `MatchContext` 以值注入（见 MatchContext 注释）。
    ///
    /// 决策顺序（不可交换，见任务表 §2.2）：
    ///   1) R1 有选中文本 → text 组（无 text 组 ⇒ 动态槽位为空 ⇒ 兜底）
    ///   2) R5 `FileCount>0 && FolderCount>0` → 兜底（**短路**，不再算扩展名命中）
    ///   3) R2/R3 仅文件：扩展名命中组数 h==1 → 该 ext 组；h>=2 → 兜底；h==0 → 兜底
    ///   4) R4 仅文件夹 → folder 组（无 folder 组 ⇒ 兜底）
    ///   5) R6 全空 → 兜底
    ///   6) R7 进程命中 → 该 proc 组（多命中 ⇒ 配置顺序第一个 + Violation）
    ///   7) R8 always 恒显示；R9 顺序 = 动态 → 进程 → 固定，堆叠不去重
    ///
    /// 不变量：
    ///   · I-1 动态槽位互斥 —— 上面 1)–5) 只会命中其一（要么一个 ext 组，要么兜底）。
    ///   · I-2 兜底只看**动态槽位**是否为空，与进程槽位无关。
    ///   · I-3/I-4 预设单例 / proc 跨组唯一 —— 违规只记 `Violation` 数据位，取配置顺序第一个。
    /// </summary>
    public static class MatchEngine
    {
        /// <summary>判定入口（默认多命中消解 = 配置顺序第一个）。</summary>
        public static MenuDecision Decide(MatchContext ctx, IList<MenuGroup> all)
        {
            return Decide(ctx, all, new ConfigOrderResolver());
        }

        /// <summary>判定入口（可注入多命中消解策略 —— 段 2 的 I-4 挂点）。</summary>
        public static MenuDecision Decide(MatchContext ctx, IList<MenuGroup> all, IMultiHitResolver multiHit)
        {
            var d = new MenuDecision();
            if (ctx == null) ctx = new MatchContext();
            if (multiHit == null) multiHit = new ConfigOrderResolver();

            var list = new List<MenuGroup>();
            if (all != null)
                foreach (var g in all)
                    if (g != null) list.Add(g);

            // ---- 分桶（六类）----
            var textG = new List<MenuGroup>();
            var folderG = new List<MenuGroup>();
            var fallbackG = new List<MenuGroup>();
            var alwaysG = new List<MenuGroup>();
            var extG = new List<MenuGroup>();
            var procG = new List<MenuGroup>();

            foreach (var g in list)
            {
                switch (g.Type)
                {
                    case GroupType.Text: textG.Add(g); break;
                    case GroupType.Folder: folderG.Add(g); break;
                    case GroupType.Fallback: fallbackG.Add(g); break;
                    case GroupType.Always: alwaysG.Add(g); break;
                    case GroupType.Ext: extG.Add(g); break;
                    case GroupType.Proc: procG.Add(g); break;
                }
            }

            // ---- I-3：预设单例违规（只记录，不改变选择口径）----
            AddSingletonViolation(d, "text", textG);
            AddSingletonViolation(d, "folder", folderG);
            AddSingletonViolation(d, "fallback", fallbackG);
            AddSingletonViolation(d, "always", alwaysG);

            // ---- 动态槽位（I-1 互斥）----
            var dyn = new List<MenuGroup>();
            if (ctx.HasText)
            {
                // R1（文本优先：文本与其他形态并存时仍只出一个动态组）
                var pick = multiHit.Choose("text", textG);
                d.Rule = "R1";
                if (pick != null) dyn.Add(pick);
            }
            else if (ctx.MixedFilesAndFolders)
            {
                // R5 短路：不再算扩展名命中（任务表 §2.2 反例用例）
                d.Rule = "R5";
            }
            else if (ctx.FileCount > 0)
            {
                var hits = MatchExtGroups(extG, ctx.ExtensionsLower);
                if (hits.Count == 1)
                {
                    d.Rule = "R2";
                    dyn.Add(hits[0]);
                }
                else if (hits.Count >= 2)
                {
                    d.Rule = "R3";                    // 多命中 ⇒ 兜底（不再乱合并）
                }
                else
                {
                    d.Rule = "R2-0命中";              // 契约只授权"恰 1 个"进 ext
                }
            }
            else if (ctx.FolderCount > 0)
            {
                d.Rule = "R4";
                var pick = multiHit.Choose("folder", folderG);
                if (pick != null) dyn.Add(pick);
            }
            else
            {
                d.Rule = "R6";
            }

            // ---- I-2：兜底只看动态槽位是否为空（与进程槽位无关）----
            d.Fallback = fallbackG;
            if (dyn.Count == 0)
            {
                d.FallbackShown = true;
                var pick = multiHit.Choose("fallback", fallbackG);
                if (pick != null) dyn.Add(pick);
            }
            d.Dynamic = FilterMissing(ctx, list, dyn);

            // ---- R7 进程槽位 ----
            var phits = MatchProcGroups(procG, ctx.ProcessNameLower);
            var procSel = new List<MenuGroup>();
            if (phits.Count > 0)
            {
                var pick = multiHit.Choose("proc", phits);
                if (pick != null) procSel.Add(pick);
                if (phits.Count > 1)
                {
                    d.Violations.Add(new Violation
                    {
                        Kind = ViolationKind.DuplicateProcName,
                        Detail = "进程名 [" + ctx.ProcessNameLower + "] 命中 " + phits.Count
                                 + " 个 proc 组，取配置顺序第一个：" + (pick != null ? pick.Name : ""),
                        Line = pick != null ? pick.Line : 0
                    });
                }
            }
            d.Process = FilterMissing(ctx, list, procSel);

            // ---- R8 固定槽位恒显示 ----
            d.Always = FilterMissing(ctx, list, alwaysG);

            return d;
        }

        /// <summary>扩展名命中组（去重后比对；关键词大小写不敏感、可带前导点）。</summary>
        public static List<MenuGroup> MatchExtGroups(IList<MenuGroup> extGroups, IList<string> extsLower)
        {
            var hits = new List<MenuGroup>();
            if (extGroups == null || extGroups.Count == 0) return hits;
            if (extsLower == null || extsLower.Count == 0) return hits;

            // 选中项扩展名去重（判定不看数量，只看"命中了几个组"）
            var uniq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in extsLower)
            {
                string n = NormalizeKeyword(e);
                if (n.Length > 0) uniq.Add(n);
            }
            if (uniq.Count == 0) return hits;

            foreach (var g in extGroups)
            {
                if (g == null) continue;
                bool hit = false;
                if (g.Keywords != null)
                {
                    foreach (var k in g.Keywords)
                    {
                        if (uniq.Contains(NormalizeKeyword(k))) { hit = true; break; }
                    }
                }
                if (hit) hits.Add(g);
            }
            return hits;
        }

        /// <summary>进程命中组（关键词可写 "chrome" 或 "chrome.exe"）。</summary>
        public static List<MenuGroup> MatchProcGroups(IList<MenuGroup> procGroups, string procLower)
        {
            var hits = new List<MenuGroup>();
            if (procGroups == null || procGroups.Count == 0) return hits;
            if (string.IsNullOrEmpty(procLower)) return hits;

            string noExt = procLower;
            if (noExt.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                noExt = noExt.Substring(0, noExt.Length - 4);

            foreach (var g in procGroups)
            {
                if (g == null) continue;
                bool hit = false;
                if (g.Keywords != null)
                {
                    foreach (var raw in g.Keywords)
                    {
                        string k = (raw ?? "").Trim().ToLowerInvariant();
                        if (k.Length == 0) continue;
                        if (k == procLower || k == noExt) { hit = true; break; }
                    }
                }
                if (hit) hits.Add(g);
            }
            return hits;
        }

        /// <summary>关键词归一化：去前后空白、去前导点、小写。</summary>
        public static string NormalizeKeyword(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string v = s.Trim().ToLowerInvariant();
            return v.TrimStart('.');
        }

        // ============================================================
        // 缺件注入位的**纯**应用（不查盘：只看 ctx.MissingItemIndex）
        // ============================================================
        /// <summary>
        /// 按 `ctx.MissingItemIndex` 丢弃缺件条目，并剪掉"子项全缺"的子菜单标题。
        /// ★ 不做任何 IO —— 下标由解析层事先算好注入。
        /// </summary>
        public static List<MenuGroup> FilterMissing(MatchContext ctx, IList<MenuGroup> all, List<MenuGroup> groups)
        {
            var outList = new List<MenuGroup>();
            if (groups == null) return outList;
            if (ctx == null || ctx.MissingItemIndex == null || ctx.MissingItemIndex.Count == 0)
            {
                outList.AddRange(groups);
                return outList;
            }

            // 展平下标（与 PathResolvePolicy.InjectEntryResolved 同一口径：分组序 → 组内条目序）
            var index = new Dictionary<MenuItem, int>();
            int k = 0;
            if (all != null)
            {
                foreach (var g in all)
                {
                    if (g == null || g.Items == null) continue;
                    foreach (var it in g.Items) index[it] = k++;
                }
            }

            foreach (var g in groups)
            {
                if (g == null) continue;
                var ng = new MenuGroup
                {
                    Name = g.Name,
                    Type = g.Type,
                    Keywords = g.Keywords,
                    Line = g.Line,
                    Items = new List<MenuItem>()
                };

                var src = g.Items ?? new List<MenuItem>();
                for (int i = 0; i < src.Count; i++)
                {
                    var it = src[i];

                    if (it.IsGroupHeader)
                    {
                        int j = i + 1;
                        while (j < src.Count && src[j].Level > it.Level) j++;
                        bool hasChildren = (j > i + 1);
                        bool anyKeep = false;
                        for (int m = i + 1; m < j; m++)
                        {
                            if (!Dropped(ctx, index, src[m])) { anyKeep = true; break; }
                        }
                        if (hasChildren && !anyKeep) continue;      // 空子菜单标题不显示
                        ng.Items.Add(it);
                        continue;
                    }

                    if (Dropped(ctx, index, it)) continue;
                    ng.Items.Add(it);
                }

                if (ng.Items.Count > 0) outList.Add(ng);
            }
            return outList;
        }

        private static bool Dropped(MatchContext ctx, Dictionary<MenuItem, int> index, MenuItem it)
        {
            if (it == null) return true;
            int i;
            if (!index.TryGetValue(it, out i)) return false;    // 不在原列表里 ⇒ 不误杀
            return ctx.IsEntryMissing(i);
        }

        private static void AddSingletonViolation(MenuDecision d, string slot, List<MenuGroup> groups)
        {
            if (groups == null || groups.Count <= 1) return;
            var names = new List<string>();
            foreach (var g in groups) names.Add(g.Name);
            d.Violations.Add(new Violation
            {
                Kind = ViolationKind.DuplicateSingleton,
                Detail = "预设槽位 [" + slot + "] 有 " + groups.Count + " 个组（应单例）："
                         + string.Join(",", names.ToArray()),
                Line = groups[0].Line
            });
        }
    }
}
