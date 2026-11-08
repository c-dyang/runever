using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Services;
using RunEverCs.Views;

// ★ 消歧：WPF 也有 System.Windows.Controls.MenuItem，与我们的模型同名
using MItem = RunEverCs.Models.MenuItem;

namespace RunEverCs
{
    /// <summary>
    /// 菜单编辑页（v9 树形中栏）**无人值守自检**。
    /// 用法：RunEverCs.exe --selftest-menueditor &lt;root&gt; &lt;out.txt&gt;
    ///
    /// 覆盖：
    ///   ① 分组数 / 树深度与 `Level` 的对应（含层级基准 baseLevel）
    ///   ② 结构操作（新增 / 删除 / 同层上下移 / ⇥缩进 / ⇤反缩进 / 拖拽三落点）
    ///      每次操作后都断言：🔴 不变式成立（父项后紧跟其全部子项 + 不跳级）、
    ///      **条目集合不丢不重**（按引用多重集比对）
    ///   ③ 保存往返：保存→解析 与内存模型逐条一致；连存两次**字节一致**（幂等）
    ///
    /// ⚠️ 全程用 `state\ui-test-tree\` 下的**副本**配置，绝不动真实 `config\menus.conf`；
    ///    全部操作走 `MenuEditorPage` 的测试缝（与产品路径同一份实现），不弹任何窗口。
    /// </summary>
    public static class MenuEditorSelfTest
    {
        private static readonly List<string> Lines = new List<string>();
        private static int _fail;

        private static void Chk(string name, bool ok, string detail)
        {
            if (!ok) _fail++;
            Lines.Add((ok ? "PASS  " : "FAIL  ") + name
                + (string.IsNullOrEmpty(detail) ? "" : "  |  " + detail));
        }

        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;
            string root = args[1];
            string outFile = args[2];

            string srcConf = Path.Combine(root, "config", "menus.conf");
            string tmpRoot = Path.Combine(root, "state", "ui-test-tree");
            string tmpConf = Path.Combine(tmpRoot, "config", "menus.conf");

            try
            {
                if (!File.Exists(srcConf))
                    throw new FileNotFoundException("配置不存在: " + srcConf);

                // ---- 干净沙箱：拷真实配置的副本 ----
                Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
                Directory.CreateDirectory(Path.Combine(tmpRoot, "cache"));
                File.Copy(srcConf, tmpConf, true);
                string cacheSrc = Path.Combine(root, "cache", "paths.txt");
                if (File.Exists(cacheSrc))
                    File.Copy(cacheSrc, Path.Combine(tmpRoot, "cache", "paths.txt"), true);

                var baseParser = new ConfParser(srcConf);
                baseParser.Parse();

                var page = new MenuEditorPage(tmpRoot);

                // ============================================================
                // ① 分组数 + 树深度 ↔ Level
                // ============================================================
                Chk("① 分组数一致", page.T_GroupCount() == baseParser.Groups.Count,
                    "页=" + page.T_GroupCount() + " 配置=" + baseParser.Groups.Count);

                int depthBad = 0, baseBad = 0, groupsChecked = 0;
                for (int gi = 0; gi < page.T_GroupCount(); gi++)
                {
                    page.T_SelectGroup(gi);
                    var g = page.T_Group();
                    if (g == null) continue;
                    groupsChecked++;

                    int baseLv = int.MaxValue;
                    foreach (var it in g.Items) if (it.Level < baseLv) baseLv = it.Level;
                    if (g.Items.Count == 0) baseLv = 0;
                    if (baseLv != page.T_BaseLevel()) baseBad++;

                    foreach (var pair in Walk(page.T_Roots(), 0))
                        if (pair.Key.Item.Level != baseLv + pair.Value) depthBad++;
                }
                Chk("② 树深度 ↔ Level（含基准）", depthBad == 0 && baseBad == 0,
                    "组=" + groupsChecked + " 深度不符=" + depthBad + " 基准不符=" + baseBad);

                // ============================================================
                // ③ 保存往返（未改动）：解析回来与内存一致 + 连存两次字节一致
                // ============================================================
                string saveMsg = page.T_SaveConf();
                Chk("③ 保存成功（未改动）", saveMsg.StartsWith("OK"), saveMsg);
                Chk("③b 保存结果与内存模型一致", ModelEqualsFile(page, tmpConf, out string why1), why1);
                byte[] first = File.ReadAllBytes(tmpConf);
                page.T_SaveConf();
                byte[] second = File.ReadAllBytes(tmpConf);
                Chk("③c 连存两次字节一致（幂等）", BytesEqual(first, second),
                    "第一次=" + first.Length + "B 第二次=" + second.Length + "B");

                // 下面各场景都在"有树形层级"的分组上做：选第一个含子项的分组
                int target = -1;
                for (int gi = 0; gi < page.T_GroupCount(); gi++)
                {
                    page.T_SelectGroup(gi);
                    var g = page.T_Group();
                    if (g != null && HasChild(g.Items)) { target = gi; break; }
                }
                if (target < 0)
                {
                    Chk("④ 找到含子项的分组（做结构操作）", false, "配置里没有带层级的样例");
                }
                else
                {
                    page.T_SelectGroup(target);
                    var g = page.T_Group();
                    var orig = new List<MItem>(g.Items);

                    // ④ ⇥ 缩进被拒（同级第一条没有可挂靠的上一条）
                    page.T_Select(orig[0]);
                    page.T_Indent(1);
                    Chk("④ ⇥ 同级第一条被拒绝", SameSet(orig, g.Items) && page.T_Status().IndexOf("拒绝") >= 0,
                        page.T_Status());
                    Chk("④b 拒绝后不变式仍成立 + 集合不丢", WellFormed(page) && SameSet(orig, g.Items), "");

                    // ⑤ 拖拽 = 成为子项（把第 2 个根节点拖到第 1 个上）
                    var roots = page.T_Roots();
                    if (roots.Count >= 2)
                    {
                        var a = roots[0]; var b = roots[1];
                        page.T_Drop(b, a, MenuEditorPage.DropWhere.Child);
                        // 判据（别写成"紧邻父项"—— 子项是**整体挂进父项子树**，可排在父项其它子项之后）：
                        //   层级 +1、父指针挂上、落在父项子树范围内、不变式成立、集合不丢
                        int ia = g.Items.IndexOf(a.Item), ib = g.Items.IndexOf(b.Item);
                        Chk("⑤ 拖拽→成为子项：层级 +1 且落在父项子树内",
                            b.Item.Level == a.Item.Level + 1
                            && ReferenceEquals(page.T_Find(b.Item).Parent, page.T_Find(a.Item))
                            && ib > ia && ib <= LastOf(g.Items, ia)
                            && WellFormed(page) && SameSet(orig, g.Items),
                            "父 lv" + a.Item.Level + " 子 lv" + b.Item.Level + " a=" + ia + " b=" + ib);

                        Chk("⑤b 子项挂对了父节点", ReferenceEquals(page.T_Find(b.Item).Parent, page.T_Find(a.Item)), "");

                        // ⑥ 拖回最外层末尾（子树整体跟随 ⇒ 判"它是最后一个根节点"，不是"最后一个扁平条目"）
                        page.T_Drop(page.T_Find(b.Item), null, MenuEditorPage.DropWhere.End);
                        var rr = page.T_Roots();
                        Chk("⑥ 拖回根层末尾：Level 回到基准 + 成为最后一个根节点",
                            b.Item.Level == page.T_BaseLevel()
                            && rr.Count > 0 && ReferenceEquals(rr[rr.Count - 1].Item, b.Item)
                            && WellFormed(page) && SameSet(orig, g.Items),
                            "lv" + b.Item.Level + " 根节点数=" + rr.Count);

                        // ⑦ 同层插到目标之前（判据：b 的**整棵子树**正好落在 a 之前）
                        page.T_Drop(page.T_Find(b.Item), page.T_Find(a.Item), MenuEditorPage.DropWhere.Before);
                        ia = g.Items.IndexOf(a.Item); ib = g.Items.IndexOf(b.Item);
                        Chk("⑦ 拖拽→同层插到之前（子树整体在前）",
                            LastOf(g.Items, ib) + 1 == ia
                            && b.Item.Level == a.Item.Level
                            && WellFormed(page) && SameSet(orig, g.Items),
                            "a=" + ia + " b=" + ib + " b子树末=" + LastOf(g.Items, ib));

                        // ⑧ 同层插到目标之后
                        page.T_Drop(page.T_Find(b.Item), page.T_Find(a.Item), MenuEditorPage.DropWhere.After);
                        ia = g.Items.IndexOf(a.Item); ib = g.Items.IndexOf(b.Item);
                        Chk("⑧ 拖拽→同层插到之后（落在目标子树之后）",
                            ib == LastOf(g.Items, ia) + 1 && WellFormed(page) && SameSet(orig, g.Items),
                            "a=" + ia + " b=" + ib);

                        // ⑨ 上下移（同层）
                        page.T_Select(b.Item);
                        int before = g.Items.IndexOf(b.Item);
                        page.T_Move(-1);
                        int after = g.Items.IndexOf(b.Item);
                        Chk("⑨ 同层上移生效", after != before && WellFormed(page) && SameSet(orig, g.Items),
                            before + " → " + after);

                        // ⑩ 拖进自己的子树 → 拒绝
                        var childCandidate = page.T_Find(a.Item).Children.Count > 0
                            ? page.T_Find(a.Item).Children[0] : null;
                        if (childCandidate != null)
                        {
                            page.T_Drop(page.T_Find(a.Item), childCandidate, MenuEditorPage.DropWhere.Child);
                            Chk("⑩ 拖进自己子树被拒绝", page.T_Status().IndexOf("拒绝") >= 0
                                && WellFormed(page) && SameSet(orig, g.Items), page.T_Status());
                        }
                        else
                        {
                            Chk("⑩ 拖进自己子树被拒绝", false, "★未执行：目标节点没有子项（样例不足，不算通过）");
                        }
                    }
                    else
                    {
                        Chk("⑤–⑩ 拖拽场景", false, "该分组根节点不足 2 个");
                    }

                    // ⑪ 新增（组头下 ⇒ 成为子项）→ 删除 → 集合还原
                    var groupNode = FindFirstGroupNode(page.T_Roots());
                    if (groupNode != null)
                    {
                        page.T_Select(groupNode.Item);
                        int n0 = g.Items.Count;
                        page.T_AddItem();
                        bool addedAsChild = g.Items.Count == n0 + 1
                            && page.T_Selected() != null
                            && page.T_Selected().Level == groupNode.Item.Level + 1;
                        Chk("⑪ 组头下新增 ⇒ 成为其子项", addedAsChild && WellFormed(page),
                            "新增后条数=" + g.Items.Count);

                        page.T_Delete();
                        Chk("⑪b 删除后集合还原", g.Items.Count == n0 && SameSet(orig, g.Items) && WellFormed(page),
                            "删除后条数=" + g.Items.Count);
                    }
                    else
                    {
                        page.T_Select(g.Items[0]);
                        int n0 = g.Items.Count;
                        page.T_AddItem();
                        page.T_Delete();
                        Chk("⑪ 新增+删除（根层）集合还原", g.Items.Count == n0 && SameSet(orig, g.Items) && WellFormed(page), "");
                    }

                    // ⑭ 外部拖文件入树（P0-1/P0-3 的回归网）。
                    //    `DropFiles` 只按后缀识别类型 ⇒ 路径**不必真实存在**；无窗口，走测试缝
                    //    `T_DropFiles`（与产品路径同一个实现）。全程只碰副本配置。
                    //    ⚠️ ④ 挑的分组可能根层全是组头 ⇒ 单独找一个「根层有普通条目」的分组。
                    int dropGi = -1;
                    for (int gi = 0; gi < page.T_GroupCount(); gi++)
                    {
                        page.T_SelectGroup(gi);
                        if (FindRootPlainNode(page.T_Roots()) != null) { dropGi = gi; break; }
                    }
                    if (dropGi >= 0)
                    {
                        var dg = page.T_Group();
                        var droot = FindRootPlainNode(page.T_Roots());
                        var before = new List<MItem>(dg.Items);
                        page.T_Select(droot.Item);
                        page.T_DropFiles(new[] { "zz-probe-root.exe" }, page.T_Find(droot.Item));
                        var added = Diff(before, dg.Items);
                        Chk("⑭ 拖到根层条目 ⇒ 新条目进入 g.Items（P0-1 回归网）",
                            added.Count == 1 && dg.Items.Contains(added[0]) && WellFormed(page),
                            "组「" + dg.Name + "」新增=" + added.Count + " 条数=" + dg.Items.Count
                            + " 状态=" + page.T_Status());
                        Chk("⑭b 新条目同时出现在视图（CurrentRoots 里能按 MItem 找到）",
                            added.Count == 1 && page.T_Find(added[0]) != null, "");
                        Chk("⑭c 拖入后选中新插入的最后一条（P0-3）",
                            added.Count == 1 && ReferenceEquals(page.T_Selected(), added[added.Count - 1]),
                            added.Count == 1 ? "选中=" + page.T_Selected().Title : "新增数=" + added.Count);

                        // 落盘验证（AC-1：保存 → 重开仍在）：保存后重新解析，新条目必须还在文件里
                        page.T_SaveConf();
                        bool onDisk = false;
                        var disk = new ConfParser(tmpConf);
                        disk.Parse();
                        foreach (var dg2 in disk.Groups)
                            foreach (var d2 in dg2.Items)
                                if (d2.Title == "zz-probe-root") onDisk = true;
                        Chk("⑭e 拖入项已落到副本 menus.conf（保存→重开仍在）", onDisk, tmpConf);

                        if (added.Count == 1) { page.T_Select(added[0]); page.T_Delete(); }
                        Chk("⑭d 清理拖入项后集合还原", SameSet(before, dg.Items) && WellFormed(page),
                            "条数=" + dg.Items.Count);

                        page.T_SelectGroup(target);   // 回到 ④ 的分组，供 ⑮ 使用
                    }
                    else
                    {
                        Chk("⑭ 拖到根层条目", false, "★未执行：没有根层普通条目的分组");
                    }

                    // ⑮ 拖到组头 ⇒ 成为其子项且模型立即一致（P0-2）
                    var headNode = FindFirstGroupNode(page.T_Roots());
                    if (headNode != null)
                    {
                        var headItem = headNode.Item;
                        var before2 = new List<MItem>(g.Items);
                        page.T_DropFiles(new[] { "zz-probe-child.lnk" }, page.T_Find(headItem));
                        var added2 = Diff(before2, g.Items);
                        var node2 = added2.Count == 1 ? page.T_Find(added2[0]) : null;
                        Chk("⑮ 拖到组头 ⇒ 新条目成为其子项（P0-2）",
                            node2 != null && node2.Parent != null
                            && ReferenceEquals(node2.Parent.Item, headItem)
                            && added2[0].Level == headItem.Level + 1
                            && WellFormed(page),
                            "层级=" + (added2.Count == 1 ? added2[0].Level.ToString() : "?")
                            + " 状态=" + page.T_Status());
                        Chk("⑮b 拖到组头后仍选中新条目",
                            added2.Count == 1 && ReferenceEquals(page.T_Selected(), added2[added2.Count - 1]), "");

                        if (added2.Count == 1) { page.T_Select(added2[0]); page.T_Delete(); }
                    }
                    else
                    {
                        Chk("⑮ 拖到组头", false, "★未执行：该分组没有组头节点");
                    }

                    // ⑯ 定位式搜索：纯模型匹配（标题 / 快捷键 / 目标，大小写不敏感）
                    var anyItem = g.Items.Count > 0 ? g.Items[0] : null;
                    if (anyItem != null)
                    {
                        var hitTitle = page.T_Search(anyItem.Title.ToUpperInvariant());
                        var hitTarget = string.IsNullOrEmpty(anyItem.Target) ? null : page.T_Search(anyItem.Target);
                        Chk("⑯ T_Search 大小写不敏感命中标题",
                            hitTitle.Contains(anyItem), "命中=" + hitTitle.Count);
                        Chk("⑯b T_Search 命中目标（若有）",
                            hitTarget == null || hitTarget.Contains(anyItem), "");
                        Chk("⑯c T_Search 空串 = 无命中", page.T_Search("").Count == 0, "");
                        Chk("⑯d T_Search 无关键 = 无命中",
                            page.T_Search("zzz-绝不可能命中的关键词-zzz").Count == 0, "");
                    }

                    // ⑫ 结构操作后再保存：解析回来仍与内存一致
                    string msg2 = page.T_SaveConf();
                    Chk("⑫ 结构操作后保存成功", msg2.StartsWith("OK"), msg2);
                    Chk("⑫b 保存文件与内存模型一致", ModelEqualsFile(page, tmpConf, out string why2), why2);
                }

                // ⑬ 真实配置未被触碰（沙箱纪律）
                var srcBytes = File.ReadAllBytes(srcConf);
                var srcAgain = File.ReadAllBytes(srcConf);
                Chk("⑬ 真实 menus.conf 未被写（沙箱隔离）", BytesEqual(srcBytes, srcAgain),
                    "src=" + srcConf);
            }
            catch (Exception ex)
            {
                Chk("异常", false, ex.GetType().Name + ": " + ex.Message);
                Lines.Add(ex.StackTrace);
            }

            Lines.Add("");
            Lines.Add("说明：结构操作全部走 MenuEditorPage 测试缝（与产品路径同一份实现）；");
            Lines.Add("      沙箱 = state\\ui-test-tree\\config\\menus.conf（副本），不动真实配置。");
            Lines.Add("结果: " + (_fail == 0 ? "全部通过 ✓" : _fail + " 项失败 ✗"));

            try { File.WriteAllText(outFile, string.Join(Environment.NewLine, Lines.ToArray()), new UTF8Encoding(false)); }
            catch { }

            return _fail == 0 ? 0 : 1;
        }

        // ============================================================
        // 辅助
        // ============================================================

        private static IEnumerable<KeyValuePair<MenuEditorPage.NodeRow, int>> Walk(
            IEnumerable<MenuEditorPage.NodeRow> nodes, int depth)
        {
            foreach (var n in nodes)
            {
                yield return new KeyValuePair<MenuEditorPage.NodeRow, int>(n, depth);
                foreach (var kv in Walk(n.Children, depth + 1)) yield return kv;
            }
        }

        private static MenuEditorPage.NodeRow FindFirstGroupNode(IEnumerable<MenuEditorPage.NodeRow> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Item.Type == ActionType.Group) return n;
                var r = FindFirstGroupNode(n.Children);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>根层的**普通条目**（非组头）——「拖到条目上」这一分支的落点。</summary>
        private static MenuEditorPage.NodeRow FindRootPlainNode(IEnumerable<MenuEditorPage.NodeRow> nodes)
        {
            foreach (var n in nodes)
                if (n.Parent == null && n.Item.Type != ActionType.Group) return n;
            return null;
        }

        /// <summary>after 里**新增**的引用（before 里没有的）。拖入类断言靠它认出新建的 MItem。</summary>
        private static List<MItem> Diff(List<MItem> before, List<MItem> after)
        {
            var seen = new HashSet<MItem>(before);
            var added = new List<MItem>();
            foreach (var x in after) if (!seen.Contains(x)) added.Add(x);
            return added;
        }

        /// <summary>该分组是否**真有层级**（存在比最外层更深的条目）。</summary>
        private static bool HasChild(List<MItem> items)
        {
            if (items.Count == 0) return false;
            int min = int.MaxValue;
            foreach (var it in items) if (it.Level < min) min = it.Level;
            foreach (var it in items) if (it.Level > min) return true;
            return false;
        }

        /// <summary>同层后继判定用：某下标处子树覆盖到哪。</summary>
        private static int LastOf(List<MItem> items, int i)
        {
            int lv = items[i].Level;
            int j = i + 1;
            while (j < items.Count && items[j].Level > lv) j++;
            return j - 1;
        }

        private static bool WellFormed(MenuEditorPage page)
        {
            string why;
            return page.T_WellFormed(out why);
        }

        /// <summary>引用多重集比对（条目不丢、不重、不换对象）。</summary>
        private static bool SameSet(List<MItem> a, List<MItem> b)
        {
            if (a.Count != b.Count) return false;
            var d = new Dictionary<MItem, int>();
            foreach (var x in a) d[x] = (d.ContainsKey(x) ? d[x] : 0) + 1;
            foreach (var x in b)
            {
                if (!d.ContainsKey(x) || d[x] == 0) return false;
                d[x]--;
            }
            return true;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>内存模型（分组/条目/Level）与磁盘文件解析结果逐条比对。</summary>
        private static bool ModelEqualsFile(MenuEditorPage page, string conf, out string why)
        {
            why = "";
            try
            {
                var p = new ConfParser(conf);
                p.Parse();
                var disk = p.Groups;
                var mem = page.T_Groups();
                if (disk.Count != mem.Count) { why = "分组数 盘=" + disk.Count + " 存=" + mem.Count; return false; }

                for (int i = 0; i < mem.Count; i++)
                {
                    var a = mem[i]; var b = disk[i];
                    if (a.Name != b.Name) { why = "第 " + (i + 1) + " 组名不同：盘[" + b.Name + "] 存[" + a.Name + "]"; return false; }
                    if (a.Items.Count != b.Items.Count)
                    {
                        why = "组「" + a.Name + "」条目数 盘=" + b.Items.Count + " 存=" + a.Items.Count;
                        return false;
                    }
                    for (int k = 0; k < a.Items.Count; k++)
                    {
                        if (a.Items[k].Title != b.Items[k].Title || a.Items[k].Level != b.Items[k].Level)
                        {
                            why = "组「" + a.Name + "」第 " + (k + 1) + " 条不同：盘["
                                + b.Items[k].Title + " lv" + b.Items[k].Level + "] 存["
                                + a.Items[k].Title + " lv" + a.Items[k].Level + "]";
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                why = "解析异常：" + ex.Message;
                return false;
            }
        }
    }
}
