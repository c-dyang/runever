using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 缺件过滤自检（功能 A：本机解析不到的条目自动隐藏）。
    ///
    /// 用法：RunEverCs.exe --selftest-hide &lt;root&gt; &lt;out.txt&gt;
    ///
    /// 覆盖：开关两态 / run 四路判定（缓存命中·缓存陈旧·PATH 命中·全解析不到） /
    ///       open 三路 / 占位符跳过 / 非判定类型全保留 / 空子菜单标题处理 /
    ///       统计口径一次计数 / 非破坏性
    /// </summary>
    public static class MissingFilterSelfTest
    {
        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;
            string root = args[1];
            string outFile = args[2];

            var sb = new StringBuilder();
            int pass = 0, fail = 0;

            void chk(bool c, string label, string extra = "")
            {
                if (c) { pass++; sb.AppendLine("  OK   " + label); }
                else { fail++; sb.AppendLine("  FAIL " + label + (extra != "" ? "   <<< " + extra : "")); }
            }

            string dir = Path.Combine(root, "state", "hide-selftest");
            try
            {
                sb.AppendLine("=== 缺件过滤自检（功能 A）===");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);

                // ---- 夹具：一个真存在的假程序、一个"缓存里有但文件已删"的程序、一个真目录 ----
                string fakeExe = Path.Combine(dir, "fake.exe");
                File.WriteAllText(fakeExe, "x", new UTF8Encoding(false));
                string goneExe = Path.Combine(dir, "gone.exe");        // 故意不创建
                string existDir = Path.Combine(dir, "existdir");
                Directory.CreateDirectory(existDir);

                string cacheFile = Path.Combine(dir, "paths.txt");
                File.WriteAllLines(cacheFile, new[]
                {
                    "fake.exe=" + fakeExe,
                    "stale.exe=" + goneExe
                }, new UTF8Encoding(false));

                var cache = new PathCache(cacheFile);
                chk(cache.Count == 2, "夹具: 路径缓存载入 2 条", "实际 " + cache.Count);

                // ---- ① 开关关：零变化且返回同一批对象 ----
                var g0 = MakeGroups(dir, existDir);
                int n0 = CountItems(g0);
                var r0 = MissingFilter.Apply(g0, cache, false, null);
                chk(ReferenceEquals(r0.Groups, g0), "① 开关关 ⇒ 原样返回同一批对象（零开销）");
                chk(CountItems(r0.Groups) == n0, "① 开关关 ⇒ 条目数不变", n0 + " → " + CountItems(r0.Groups));
                chk(r0.Stats.Hidden == 0 && r0.Stats.Checked == 0, "① 开关关 ⇒ 统计全零");

                // ---- ② 开关开 ----
                var g = MakeGroups(dir, existDir);
                var log = new List<string>();
                var fo = MissingFilter.Apply(g, cache, true, s => log.Add(s));
                var res = fo.Stats;
                var titles = AllTitles(fo.Groups);

                // has(t) = 该条目仍在输出里（= 保留）；!has(t) = 被隐藏
                bool has(string t) { return titles.Contains(t); }

                chk(has("RUN-缓存命中存在"), "② run: 缓存命中且文件在 ⇒ 保留");
                chk(!has("RUN-缓存陈旧"), "② run: 缓存指向已删文件 ⇒ 隐藏（不吃陈旧缓存）");
                chk(has("RUN-PATH命中"), "② run: 缓存未命中但 PATH 有(cmd.exe) ⇒ 保留");
                chk(!has("RUN-全解析不到"), "② run: 三级都解析不到 ⇒ 隐藏");
                chk(has("RUN-带路径存在"), "② run: 目标自带路径且存在 ⇒ 保留");
                chk(!has("RUN-带路径不存在"), "② run: 目标自带路径但不存在 ⇒ 隐藏");
                chk(has("RUN-占位符"), "② run: 目标含 %text% ⇒ 跳过不判（保留）");

                chk(has("OPEN-目录存在"), "② open: 目录存在 ⇒ 保留");
                chk(!has("OPEN-不存在"), "② open: 路径不存在 ⇒ 隐藏");
                chk(has("OPEN-占位符"), "② open: 目标含 %file% ⇒ 跳过不判（保留）");

                chk(has("URL-条目"), "② url ⇒ 不判（保留）");
                chk(has("ACTION-条目"), "② action ⇒ 不判（保留）");
                chk(has("SEND-条目"), "② send ⇒ 不判（保留）");
                chk(has("KEYS-条目"), "② keys ⇒ 不判（保留）");

                // ---- ③ 子菜单：子项全隐藏 ⇒ 标题也隐藏；有可见子项 ⇒ 保留 ----
                chk(!has("HDR-全缺失"), "③ 子菜单标题: 子项全部缺失 ⇒ 标题也隐藏");
                chk(!has("子项缺失"), "③ 子菜单: 缺失的子项本身也不显示");
                chk(has("HDR-部分可见"), "③ 子菜单标题: 有可见子项 ⇒ 标题保留");
                chk(has("子项存在"), "③ 子菜单: 可见子项保留");

                // ---- ⑦ F1 剪枝四形态夹具（走判定层：InjectEntryResolved → MatchEngine.Decide）----
                //   (a) 子项全缺的 group 标题   (b) 子项全缺的**普通父条目**
                //   (c) 三层嵌套只剩空壳        (d) 无子项的空壳 group
                var pruneG = MakePruneFixture();
                var pruneList = new List<MenuGroup> { pruneG };
                var pctx = new MatchContext();
                PathResolvePolicy.InjectEntryResolved(pctx, pruneList, cache, true, null);
                var pdec = MatchEngine.Decide(pctx, pruneList);
                var pTitles = AllTitles(pdec.Flat());

                chk(!pTitles.Contains("A-全缺组"), "⑦(a) 子项全缺的 group 标题 ⇒ 剪掉");
                chk(!pTitles.Contains("B-普通父"), "⑦(b) 普通父条目（子项全缺）⇒ 剪掉（不留空壳）");
                chk(!pTitles.Contains("C-一层") && !pTitles.Contains("C-二层"),
                    "⑦(c) 三层嵌套只剩空壳 ⇒ 整棵剪掉");
                chk(!pTitles.Contains("D-空壳组"), "⑦(d) 无子项的空壳 group ⇒ 剪掉");
                chk(pTitles.Contains("E-可见"), "⑦ 有可见内容的条目不受影响（不误杀）");

                int shellNodes = CountShellNodes(pdec.Flat());
                chk(shellNodes == 0, "⑦ 空壳节点数 = 0（输出中 items=0 的节点数）",
                    "实际=" + shellNodes + " 输出=" + string.Join(",", pTitles.ToArray()));
                chk(SubtreePruner.LastPrunedTotal == 4, "⑦ 剪空子树 = 4（四形态各算一棵）",
                    "实际=" + SubtreePruner.LastPrunedTotal);

                // ---- ⑧ F1-2 渲染护栏：只有空壳子菜单的分组 ⇒ 一个子菜单都不建 ----
                var shellG = new MenuGroup { Name = "护栏夹具", Type = GroupType.Always };
                shellG.Items.Add(It("护栏-空壳", ActionType.Group, "", 1));
                var shellBuilt = MenuBuilder.Build(new List<MenuGroup> { shellG });
                try
                {
                    chk(shellBuilt.Menu.CountSubMenus() == 0,
                        "⑧ 渲染护栏: 空壳子菜单不 AddSubMenu（CountSubMenus = 0）",
                        "实际子菜单数=" + shellBuilt.Menu.CountSubMenus());
                    chk(MenuBuilder.LastPrunedSubtrees == 1,
                        "⑧ 渲染护栏计数: 剪空子树 = 1", "实际=" + MenuBuilder.LastPrunedSubtrees);
                }
                finally { shellBuilt.Menu.Dispose(); }

                // ---- ④ 统计口径：叶条目只计一次（标题不参与"检查/跳过"）----
                int leafTotal = 0;
                foreach (var it in g[0].Items) if (!it.IsGroupHeader) leafTotal++;
                int judged = res.Checked + res.Skipped;
                chk(judged == leafTotal,
                    "④ 统计口径: 检查+跳过 = 全部叶条目（每个只计一次）",
                    "检查=" + res.Checked + " 跳过=" + res.Skipped + " 合计=" + judged + " 叶条目=" + leafTotal);
                chk(res.Hidden > 0, "④ 确实发生了隐藏", "隐藏=" + res.Hidden);

                // ---- ⑤ 非破坏性：原对象没被改动 ----
                chk(CountItems(g0) == n0, "⑤ 非破坏性: 原分组条目数未被改动");

                // ★ 主 agent 验收补充：空目标父条目不得被连带剪掉
                chk(has("父项空目标"), "⑦(e) 空目标的普通父条目（子项存在）⇒ 父项保留");
                chk(has("空目标父条目下的可见子项"), "⑦(e) 空目标父条目下的可见子项 ⇒ 不被误剪");
                // ---- ⑥ 日志可观测 ----
                bool logged = false;
                foreach (var l in log) if (l.Contains("缺件隐藏:")) { logged = true; break; }
                chk(logged, "⑥ 隐藏动作进日志（可排障）");

                sb.AppendLine();
                sb.AppendLine("  统计: " + res.ToString());
                sb.AppendLine("  隐藏清单: " + string.Join(" | ", res.HiddenTitles.ToArray()));
                sb.AppendLine();
                sb.AppendLine("结果: " + (fail == 0 ? "全部通过 ✓" : "失败 " + fail + " 项 ✗")
                    + "   (通过 " + pass + " / 失败 " + fail + ")");
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("  FAIL 异常: " + ex.Message + "\n" + ex.StackTrace);
            }

            try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false)); } catch { }
            return fail == 0 ? 0 : 1;
        }

        /// <summary>造一份覆盖各种判定分支的分组夹具。</summary>
        private static List<MenuGroup> MakeGroups(string dir, string existDir)
        {
            var g = new MenuGroup { Name = "测试组", Type = GroupType.Always, Line = 1 };

            g.Items.Add(It("RUN-缓存命中存在", ActionType.Run, "fake.exe"));
            g.Items.Add(It("RUN-缓存陈旧", ActionType.Run, "stale.exe"));
            g.Items.Add(It("RUN-PATH命中", ActionType.Run, "cmd.exe /c echo hi"));
            g.Items.Add(It("RUN-全解析不到", ActionType.Run, "no-such-prog-xyz.exe --flag"));
            g.Items.Add(It("RUN-带路径存在", ActionType.Run, "\"" + Path.Combine(dir, "fake.exe") + "\""));
            g.Items.Add(It("RUN-带路径不存在", ActionType.Run, Path.Combine(dir, "nope.exe")));
            g.Items.Add(It("RUN-占位符", ActionType.Run, "%text%"));

            g.Items.Add(It("OPEN-目录存在", ActionType.Open, existDir));
            g.Items.Add(It("OPEN-不存在", ActionType.Open, Path.Combine(dir, "no-such-file.txt")));
            g.Items.Add(It("OPEN-占位符", ActionType.Open, "%file%"));

            g.Items.Add(It("URL-条目", ActionType.Url, "https://example.com"));
            g.Items.Add(It("ACTION-条目", ActionType.Action, "echo hello"));
            g.Items.Add(It("SEND-条目", ActionType.Send, "hello"));
            g.Items.Add(It("KEYS-条目", ActionType.Keys, "^c"));

            // 子菜单 1：子项全缺失 ⇒ 标题应被隐藏
            g.Items.Add(It("HDR-全缺失", ActionType.Group, "", 0));
            g.Items.Add(It("子项缺失", ActionType.Run, "no-such-prog-abc.exe", 1));

            // 子菜单 2：有可见子项 ⇒ 标题保留
            g.Items.Add(It("HDR-部分可见", ActionType.Group, "", 0));
            g.Items.Add(It("子项存在", ActionType.Run, "fake.exe", 1));

            // ★ 主 agent 验收补充（防误剪）：非 group 的普通父条目、目标为空、子项存在
            //   ⇒ 父项与子项都必须保留（空目标判不了 ⇒ Keep，不许连带剪子树）
            g.Items.Add(It("父项空目标", ActionType.Run, "", 0));
            g.Items.Add(It("空目标父条目下的可见子项", ActionType.Run, "fake.exe", 1));

            return new List<MenuGroup> { g };
        }

        private static MenuItem It(string title, ActionType t, string target, int level = 0)
        {
            return new MenuItem { Title = title, Type = t, Target = target, Level = level };
        }

        /// <summary>
        /// F1 剪枝四形态夹具（一个组内四棵空子树 + 一条可见条目）：
        ///   A 子项全缺的 group 标题 / B 子项全缺的普通父条目 /
        ///   C 三层嵌套只剩空壳 / D 无子项的空壳 group / E 可见对照组。
        /// </summary>
        private static MenuGroup MakePruneFixture()
        {
            var g = new MenuGroup { Name = "剪枝夹具", Type = GroupType.Always, Line = 900 };

            // (a) group 标题 + 子项全缺
            g.Items.Add(It("A-全缺组", ActionType.Group, "", 1));
            g.Items.Add(It("A-子缺", ActionType.Run, "no-such-prog-a.exe", 2));

            // (b) 普通父条目（自身不判缺件）+ 子项全缺
            g.Items.Add(It("B-普通父", ActionType.Keys, "^c", 1));
            g.Items.Add(It("B-子缺", ActionType.Run, "no-such-prog-b.exe", 2));

            // (c) 三层嵌套，最内层缺 ⇒ 整链只剩空壳
            g.Items.Add(It("C-一层", ActionType.Group, "", 1));
            g.Items.Add(It("C-二层", ActionType.Group, "", 2));
            g.Items.Add(It("C-三层缺", ActionType.Run, "no-such-prog-c.exe", 3));

            // (d) 无子项的空壳 group
            g.Items.Add(It("D-空壳组", ActionType.Group, "", 1));

            // 对照组：可见条目必须留着
            g.Items.Add(It("E-可见", ActionType.Keys, "^v", 1));

            return g;
        }

        /// <summary>统计输出里"items=0 的节点"数（空壳 = 组标题无子项，或容器子树内无可见叶条目）。</summary>
        private static int CountShellNodes(List<MenuGroup> groups)
        {
            int bad = 0;
            foreach (var g in groups)
            {
                var items = g.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    int j = i + 1;
                    int visible = 0;
                    while (j < items.Count && items[j].Level > items[i].Level)
                    {
                        if (!items[j].IsGroupHeader) visible++;
                        j++;
                    }
                    bool hasChildren = (j > i + 1);
                    if (hasChildren && visible == 0) bad++;
                    else if (!hasChildren && items[i].IsGroupHeader) bad++;
                }
            }
            return bad;
        }

        private static int CountItems(List<MenuGroup> groups)
        {
            int n = 0;
            foreach (var g in groups) n += g.Items.Count;
            return n;
        }

        private static List<string> AllTitles(List<MenuGroup> groups)
        {
            var l = new List<string>();
            foreach (var g in groups)
                foreach (var it in g.Items) l.Add(it.Title);
            return l;
        }
    }
}
