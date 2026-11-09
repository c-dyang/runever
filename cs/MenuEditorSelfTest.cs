using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Input;
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

                // ============================================================
                // ⑱ B2：键盘 → 动作的**纯函数**映射（无窗口可断言；焦点在编辑框一律 null）
                // ============================================================
                Chk("⑱ Delete→Delete",
                    MenuEditorPage.KeyToAction(Key.Delete, ModifierKeys.None, false) == MenuEditorPage.TreeAction.Delete, "");
                Chk("⑱b F2→Rename",
                    MenuEditorPage.KeyToAction(Key.F2, ModifierKeys.None, false) == MenuEditorPage.TreeAction.Rename, "");
                Chk("⑱c Insert→AddChild",
                    MenuEditorPage.KeyToAction(Key.Insert, ModifierKeys.None, false) == MenuEditorPage.TreeAction.AddChild, "");
                Chk("⑱d Alt+↑→MoveUp",
                    MenuEditorPage.KeyToAction(Key.Up, ModifierKeys.Alt, false) == MenuEditorPage.TreeAction.MoveUp, "");
                Chk("⑱e Alt+↓→MoveDown",
                    MenuEditorPage.KeyToAction(Key.Down, ModifierKeys.Alt, false) == MenuEditorPage.TreeAction.MoveDown, "");
                Chk("⑱f Tab→Indent",
                    MenuEditorPage.KeyToAction(Key.Tab, ModifierKeys.None, false) == MenuEditorPage.TreeAction.Indent, "");
                Chk("⑱g Shift+Tab→Outdent",
                    MenuEditorPage.KeyToAction(Key.Tab, ModifierKeys.Shift, false) == MenuEditorPage.TreeAction.Outdent, "");
                Chk("⑱h Ctrl+Delete 不吃（让给系统）",
                    MenuEditorPage.KeyToAction(Key.Delete, ModifierKeys.Control, false) == null, "");
                Chk("⑱i 焦点在编辑框 ⇒ 一律 null（防「打字删条目」）",
                    MenuEditorPage.KeyToAction(Key.Delete, ModifierKeys.None, true) == null
                    && MenuEditorPage.KeyToAction(Key.F2, ModifierKeys.None, true) == null, "");

                // ============================================================
                // ⑲ B2：三入口同源 —— 按钮/右键/键盘都只调 DoTreeAction（T_TreeAction 即该分发）
                // ============================================================
                try
                {
                    var b2g = page.T_Group();
                    var b2it = (b2g != null && b2g.Items.Count > 0) ? b2g.Items[0] : null;
                    if (b2g == null || b2it == null)
                    {
                        Chk("⑲ 结构动作等价（需要非空分组）", false, "★未执行：当前分组为空");
                    }
                    else
                    {
                        int n0 = b2g.Items.Count;
                        page.T_Select(b2it);
                        page.T_TreeAction(MenuEditorPage.TreeAction.AddChild);
                        int n1 = b2g.Items.Count;
                        Chk("⑲ AddChild 增 1 条", n1 == n0 + 1, n0 + "→" + n1);

                        page.T_TreeAction(MenuEditorPage.TreeAction.Duplicate);
                        int n2 = b2g.Items.Count;
                        bool sameTitle = false;
                        foreach (var x in b2g.Items) if (x.Title == b2it.Title) sameTitle = true;
                        Chk("⑲b Duplicate 增 1 条且标题同名", n2 == n1 + 1 && sameTitle, n1 + "→" + n2);

                        // Delete 删的是"选中项 + 其整个子树"，行数减少量取决于子树大小 ⇒
                        // 只断言方向（严格减少），不断言回到 n0（否则会把正确实现判成失败）。
                        int beforeDel = b2g.Items.Count;
                        page.T_TreeAction(MenuEditorPage.TreeAction.Delete);
                        int afterDel = b2g.Items.Count;
                        Chk("⑲c Delete 后条目数严格减少", afterDel < beforeDel,
                            beforeDel + "→" + afterDel);
                    }
                }
                catch (Exception exB2)
                {
                    Chk("⑲ 结构动作等价（T_TreeAction 可驱动）", false, "异常：" + exB2.Message);
                }

                // ============================================================
                // ⑰ B4a-1（P0 数据安全）：配置读不进来时禁止保存
                //   ① 页面级：坏配置 ⇒ _loadOk=false +「保存」禁用 + OnSave 二次防御 + 文件不变
                //   ② 写回器级：**绕过页面**直接 Save(空集合) ⇒ 兜底硬闸拒绝 + 文件不变
                //   ③ 首次创建：目标文件不存在 + 空集合 ⇒ 允许写（不能把正常路径也拦死）
                // ============================================================
                string badRoot = Path.Combine(root, "state", "b4a-broken");
                string badConf = Path.Combine(badRoot, "config", "menus.conf");
                Directory.CreateDirectory(Path.Combine(badRoot, "config"));
                Directory.CreateDirectory(Path.Combine(badRoot, "cache"));
                File.WriteAllText(badConf,
                    "! 通用  always\n"
                    + "  记事本  _N  | run  notepad.exe\n"
                    + "! 坏组  nosuchtype\n"
                    + "  随便  | run  x.exe\n", new UTF8Encoding(false));

                byte[] badBefore = File.ReadAllBytes(badConf);
                var badPage = new MenuEditorPage(badRoot);
                Chk("⑰a 坏配置 ⇒ 页面判定「没读进来」", !badPage.T_LoadOk(), badPage.T_Status());
                Chk("⑰b 坏配置 ⇒「保存」按钮禁用", !badPage.T_SaveEnabled(),
                    "loadOk=" + badPage.T_LoadOk());
                string rejectMsg = badPage.T_OnSave();
                Chk("⑰c 坏配置 ⇒ OnSave 被拒（二次防御）", rejectMsg.Contains("已拒绝保存"), rejectMsg);
                Chk("⑰d 坏配置 ⇒ 原文件一个字节没动（没被清空）",
                    BytesEqual(badBefore, File.ReadAllBytes(badConf)), "字节=" + badBefore.Length);

                // ② 写回器兜底硬闸 —— 直接调 Save，跳过页面的两道闸
                var badWriter = new MenuConfWriter(badConf);
                string gateMsg;
                bool gateOk = badWriter.Save(new List<MenuGroup>(), out gateMsg);
                Chk("⑰e 空集合覆盖坏文件被拒（writer 硬闸）",
                    !gateOk && gateMsg.Contains("拒绝写入"), gateMsg);
                Chk("⑰f 硬闸拒绝后原文件仍未被改",
                    BytesEqual(badBefore, File.ReadAllBytes(badConf)),
                    "字节=" + File.ReadAllBytes(badConf).Length);

                // ②b 原文件是好的、但集合是空的（模拟"读失败后仍旧集合"）⇒ 同样拒绝
                string keepConf = Path.Combine(badRoot, "config", "menus.conf.keep");
                File.Copy(tmpConf, keepConf, true);
                byte[] keepBefore = File.ReadAllBytes(keepConf);
                var keepWriter = new MenuConfWriter(keepConf);
                string gateMsg2;
                bool gateOk2 = keepWriter.Save(new List<MenuGroup>(), out gateMsg2);
                Chk("⑰g 空集合覆盖非空好配置被拒（writer 硬闸）",
                    !gateOk2 && gateMsg2.Contains("拒绝写入"), gateMsg2);
                Chk("⑰h 拒绝后好配置字节不变", BytesEqual(keepBefore, File.ReadAllBytes(keepConf)), "");

                // ③ 首次创建（原文件不存在）必须放行，否则把正常路径也拦死了
                string freshConf = Path.Combine(badRoot, "config", "menus.fresh.conf");
                if (File.Exists(freshConf)) File.Delete(freshConf);
                var freshWriter = new MenuConfWriter(freshConf);
                string freshMsg;
                bool freshOk = freshWriter.Save(new List<MenuGroup>(), out freshMsg);
                Chk("⑰i 原文件不存在时允许写（首次创建不被拦）",
                    freshOk && File.Exists(freshConf), freshMsg);

                // ============================================================
                // ⑳ L5：动作类型下拉 —— 中文显示 / 英文落盘（值/显示分离）
                //   ① 7 个合法类型各自「显示中文 ⇄ 落盘英文」双向一致
                //   ② 先选中文项再保存 ⇒ menus.conf 该行**不含中文**
                //   ③ 非法值（cmd）必须报错，且**不改**条目的类型
                // ============================================================
                try
                {
                    var probe = new MItem { Title = "zz-l5-type-probe", Level = 1 };
                    var types = new[]
                    {
                        ActionType.Group, ActionType.Run, ActionType.Open, ActionType.Url,
                        ActionType.Action, ActionType.Send, ActionType.Keys
                    };
                    var badTypes = new List<string>();
                    foreach (var t in types)
                    {
                        string disp = MenuEditorPage.ActionTypeDisplay(t);
                        string key = MenuEditorPage.ActionTypeKey(t);

                        // 先把类型拨到"别的"值 ⇒ 保证下一次选中真的触发 SelectionChanged
                        probe.Type = t == ActionType.Group ? ActionType.Run : ActionType.Group;
                        page.T_ShowFields(probe);
                        bool picked = page.T_SelectTypeByDisplay(disp);

                        // 正向：中文项 → 模型类型；下拉选中项的 Tag 仍是英文键
                        bool fwd = picked && probe.Type == t
                                   && page.T_TypeComboSelectedKey() == key
                                   && page.T_TypeComboSelectedDisplay() == disp
                                   && !string.IsNullOrEmpty(disp) && IsCjk(disp) && disp != key;

                        // 反向：英文键 → 严格解析回同一类型；落盘行用英文键
                        //   （group + 空目标 ⇒ 文法上根本不写 `| 类型` 段，这时按"无类型段"判）
                        ActionType back;
                        bool revParsed = MenuEditorPage.TryParseActionTypeKey(key, out back) && back == t;
                        string line = MenuConfWriter.FormatItem(probe);
                        string tok = TypeToken(line);
                        bool rev = revParsed && !IsCjk(line)
                                   && (tok == key || (t == ActionType.Group && tok == ""));

                        if (!(fwd && rev))
                            badTypes.Add(key + "/" + disp + " fwd=" + fwd + " rev=" + rev);
                    }
                    Chk("⑳ 7 个合法类型「中文显示 ⇄ 英文落盘」双向一致", badTypes.Count == 0,
                        badTypes.Count == 0 ? "7/7" : string.Join("; ", badTypes.ToArray()));

                    var pairs = page.T_TypeComboPairs();
                    var seenDisp = new HashSet<string>();
                    bool tagsLegal = true;
                    foreach (var p in pairs)
                    {
                        seenDisp.Add(p.Value);
                        ActionType x;
                        if (IsCjk(p.Key) || !MenuEditorPage.TryParseActionTypeKey(p.Key, out x)) tagsLegal = false;
                    }
                    Chk("⑳b 下拉 = 7 项、显示全中文互不重复、Tag 全是合法英文键",
                        pairs.Count == 7 && seenDisp.Count == 7 && tagsLegal,
                        "项数=" + pairs.Count + " 去重显示=" + seenDisp.Count + " Tag合法=" + tagsLegal);

                    // ③ 非法值：cmd / 空串 / 中文显示文本 一律拒绝，且条目类型原样不动
                    probe.Type = ActionType.Send;
                    string err = MenuEditorPage.ApplyActionTypeKey(probe, "cmd");
                    ActionType dummy;
                    Chk("⑳c 非法动作类型「cmd」被拒且不改类型",
                        err.Length > 0 && probe.Type == ActionType.Send, err);
                    Chk("⑳d 严格解析：cmd / 空串 / 中文显示文本 一律 false",
                        !MenuEditorPage.TryParseActionTypeKey("cmd", out dummy)
                        && !MenuEditorPage.TryParseActionTypeKey("", out dummy)
                        && !MenuEditorPage.TryParseActionTypeKey("执行命令", out dummy), "");

                    // ② 选中文项 → 保存：配置行必须是英文键
                    var gL5 = page.T_Group() ?? (page.T_Groups().Count > 0 ? page.T_Groups()[0] : null);
                    if (gL5 == null)
                    {
                        Chk("⑳e 选中中文项后保存：配置行不含中文", false, "★未执行：没有可选分组");
                    }
                    else
                    {
                        // 合成一条 **纯 ASCII** 条目：这样"整行不含中文"才是可判定的强断言
                        var synth = new MItem
                        {
                            Title = "zz-l5-ascii",
                            Target = "cmd.exe",
                            Level = page.T_BaseLevel()
                        };
                        gL5.Items.Add(synth);
                        page.T_Select(synth);          // 预览框跟着**选中项**走
                        page.T_ShowFields(synth);
                        bool picked = page.T_SelectTypeByDisplay("执行命令");
                        string msgL5 = page.T_SaveConf();

                        string expect = MenuConfWriter.FormatItem(synth);
                        var lines = File.ReadAllText(tmpConf, Encoding.UTF8).Split('\n');
                        bool hasLine = false;
                        foreach (var ln in lines)
                            if (ln.TrimEnd('\r') == expect) { hasLine = true; break; }

                        Chk("⑳e 中文项「执行命令」⇒ 落盘为 action，整行无中文",
                            picked && synth.Type == ActionType.Action
                            && msgL5.StartsWith("OK") && hasLine
                            && TypeToken(expect) == "action" && !IsCjk(expect),
                            "行=" + expect);

                        // 预览框必须仍是**原文法**（英文键 + ASCII 目标），汉化不越界
                        string prev = page.T_PreviewText();
                        Chk("⑳g 实时预览仍是原文法（无中文、类型段=action）",
                            prev == expect && TypeToken(prev) == "action" && !IsCjk(prev),
                            "预览=" + (prev ?? "null"));

                        // 落盘的英文键必须能被解析器认回来（否则就是"静默失效"）
                        var reparse = new ConfParser(tmpConf);
                        reparse.Parse();
                        bool diskOk = false;
                        foreach (var gg in reparse.Groups)
                            foreach (var ii in gg.Items)
                                if (ii.Title == "zz-l5-ascii" && ii.Type == ActionType.Action) diskOk = true;
                        Chk("⑳f 回读：磁盘上的 action 解析回 ActionType.Action", diskOk,
                            diskOk ? "" : "配置文件=" + tmpConf);

                        // 收尾：摘掉合成条目，沙箱回到进入本段前的状态
                        gL5.Items.Remove(synth);
                        page.T_SaveConf();
                    }
                }
                catch (Exception exL5)
                {
                    Chk("⑳ L5 动作类型汉化", false, "异常：" + exL5.GetType().Name + ": " + exL5.Message);
                }

                // ============================================================
                // ㉑ C1：保存前校验闸（非法结构**不许写盘**）
                //   夹具三种：跳级 / 父条目后没紧跟子条目 / 同菜单内条目 ID 重复；
                //   另外再压一条"原子写失败"（tmp 写成功、回读失败 ⇒ 原文件不变 + tmp 清理）。
                //   判据一律是：返回 false + 明确错误码/文案 + **原文件 SHA256 不变**。
                // ============================================================
                try
                {
                    string gateRoot = Path.Combine(root, "state", "l6-gate");
                    string gateConf = Path.Combine(gateRoot, "config", "menus.conf");
                    Directory.CreateDirectory(Path.Combine(gateRoot, "config"));
                    // 目录跨次运行会留痕（备份/tmp）⇒ 先清干净，"被拦时连备份都没生成"才可判
                    foreach (var old in Directory.GetFiles(Path.GetDirectoryName(gateConf), "menus.conf.bak.*"))
                        try { File.Delete(old); } catch { }
                    try { if (File.Exists(gateConf + ".tmp")) File.Delete(gateConf + ".tmp"); } catch { }
                    // 用**真实配置**做底：它必须自己先过闸（否则夹具就没有"合法基线"可言）
                    File.Copy(srcConf, gateConf, true);

                    var gw = new MenuConfWriter(gateConf);
                    var pGate = new ConfParser(gateConf);
                    pGate.Parse();
                    string hash0 = Sha256(File.ReadAllBytes(gateConf));

                    Chk("㉑a 现役 menus.conf 过闸：0 处结构问题",
                        MenuConfWriter.Validate(pGate.Groups).Count == 0,
                        Describe(MenuConfWriter.Validate(pGate.Groups)));

                    // ---- 夹具① 跳级：子条目 Level 拨到"上一条 + 2" ----
                    {
                        var p = ParseAgain(gateConf);
                        var g = FirstGroupWithChild(p.Groups);
                        int idx = FirstChildIndex(g.Items);
                        g.Items[idx].Level = g.Items[idx - 1].Level + 2;
                        string m; List<MenuStructureIssue> iss;
                        bool ok = gw.Save(p.Groups, out m, out iss);
                        Chk("㉑b 跳级 ⇒ 拒绝写盘（返回 false + LEVEL_JUMP）",
                            !ok && HasCode(iss, MenuStructureValidator.LevelJump),
                            ok ? "竟然写成功了：" + m : Describe(iss));
                        Chk("㉑c 跳级拒绝后原文件 SHA256 不变 + 连备份都没有（闸在任何写动作之前）",
                            Sha256(File.ReadAllBytes(gateConf)) == hash0
                            && Directory.GetFiles(Path.GetDirectoryName(gateConf), "menus.conf.bak.*").Length == 0
                            && !File.Exists(gateConf + ".tmp"),
                            "hash=" + hash0.Substring(0, 12)
                            + " 备份数=" + Directory.GetFiles(Path.GetDirectoryName(gateConf), "menus.conf.bak.*").Length);
                        // 行号必须真的对得上"将来会写出的那份文本"（不是拍脑袋的下标）
                        string[] serLines = MenuConfWriter.Serialize(p.Groups)
                            .Replace("\r\n", "\n").Split('\n');
                        bool lineHit = iss.Count > 0 && iss[0].LineNo > 0
                            && iss[0].LineNo <= serLines.Length
                            && serLines[iss[0].LineNo - 1].IndexOf(iss[0].Item.Title, StringComparison.Ordinal) >= 0;
                        Chk("㉑d 拒绝原因带「第 N 行 / 条目「X」：原因」且行号与序列化文本对得上",
                            iss.Count > 0 && iss[0].Where.StartsWith("第 " + iss[0].LineNo + " 行 / 条目「")
                            && iss[0].Where.IndexOf("：", StringComparison.Ordinal) > 0
                            && lineHit,
                            (iss.Count > 0 ? iss[0].Where : "<无问题列表>")
                            + " ｜ 该行文本=" + (lineHit ? serLines[iss[0].LineNo - 1].Trim() : "<对不上>"));
                    }

                    // ---- 夹具② 父条目后没紧跟子条目（空壳子菜单）----
                    {
                        var p = ParseAgain(gateConf);
                        var g = p.Groups[0];
                        // 造一个 group 标题，后面没有任何子条目
                        g.Items.Add(new MItem
                        {
                            Title = "zz-空壳父条目",
                            Type = ActionType.Group,
                            Level = g.Items.Count > 0 ? g.Items[0].Level : 1
                        });
                        string m; List<MenuStructureIssue> iss;
                        bool ok = gw.Save(p.Groups, out m, out iss);
                        Chk("㉑e 父条目后没紧跟子条目 ⇒ 拒绝写盘（PARENT_NO_CHILD）",
                            !ok && HasCode(iss, MenuStructureValidator.ParentNoChild),
                            ok ? "竟然写成功了：" + m : Describe(iss));
                        Chk("㉑f 父后断子拒绝后原文件 SHA256 不变",
                            Sha256(File.ReadAllBytes(gateConf)) == hash0, "");
                    }

                    // ---- 夹具③ 同一菜单内条目 ID 重复（两条完全一样的条目）----
                    {
                        var p = ParseAgain(gateConf);
                        var g = p.Groups[0];
                        int lv = g.Items.Count > 0 ? g.Items[0].Level : 1;
                        var dupSrc = g.Items[0];
                        g.Items.Add(new MItem
                        {
                            Title = dupSrc.Title,
                            Hotkey = dupSrc.Hotkey,
                            Type = dupSrc.Type,
                            Flag = dupSrc.Flag,
                            Target = dupSrc.Target,
                            Level = lv
                        });
                        string m; List<MenuStructureIssue> iss;
                        bool ok = gw.Save(p.Groups, out m, out iss);
                        Chk("㉑g 同菜单内条目 ID 重复 ⇒ 拒绝写盘（DUPLICATE_ID）",
                            !ok && HasCode(iss, MenuStructureValidator.DuplicateId),
                            ok ? "竟然写成功了：" + m : Describe(iss));
                        Chk("㉑h 重复 ID 拒绝后原文件 SHA256 不变",
                            Sha256(File.ReadAllBytes(gateConf)) == hash0, "");
                    }

                    // ---- 夹具④ 原子写失败模拟：结构合法但写出去读不回来 ----
                    //   标题里带 `|` ⇒ 序列化后那个 `|` 会被解析器当"标题/动作分隔符"，
                    //   回读出来的标题就变了 ⇒ 必须**删 tmp、保留原文件**、报错，而不是覆盖。
                    {
                        var p = ParseAgain(gateConf);
                        p.Groups[0].Items.Add(new MItem
                        {
                            Title = "zz-带竖线的标题|后半截",
                            Type = ActionType.Run,
                            Target = "x.exe",
                            Level = p.Groups[0].Items.Count > 0 ? p.Groups[0].Items[0].Level : 1
                        });
                        string m; List<MenuStructureIssue> iss;
                        bool ok = gw.Save(p.Groups, out m, out iss);
                        Chk("㉑i 回读校验失败 ⇒ 取消保存（原文件不动）",
                            !ok && m.IndexOf("已取消保存", StringComparison.Ordinal) >= 0,
                            ok ? "竟然写成功了：" + m : m.Replace("\n", "  |  "));
                        Chk("㉑j 回读失败后 tmp 被清理 + 原文件 SHA256 不变",
                            !File.Exists(gateConf + ".tmp")
                            && Sha256(File.ReadAllBytes(gateConf)) == hash0,
                            "tmp存在=" + File.Exists(gateConf + ".tmp"));
                    }

                    // ---- ⑤ 修好之后必须还能存（闸不是单向陷阱）----
                    {
                        var p = ParseAgain(gateConf);
                        string m;
                        bool ok = gw.Save(p.Groups, out m);
                        Chk("㉑k 合法结构（修复后）仍能正常保存", ok, m.Replace("\n", "  |  "));
                    }

                    // ---- ⑥ 页面路径：状态行 + 弹窗内容 + 双击跳转 ----
                    {
                        var g = page.T_Group() ?? page.T_Groups()[0];
                        // 挑一条**叶子**（没有子条目）来拨层级 ⇒ 只会触发"跳级"，不牵连父子规则
                        var victim = g.Items[0];
                        int vi = 0;
                        for (int i = 1; i < g.Items.Count; i++)
                        {
                            if (i + 1 >= g.Items.Count || g.Items[i + 1].Level <= g.Items[i].Level)
                            {
                                victim = g.Items[i];
                                vi = i;
                                break;
                            }
                        }
                        int goodLevel = victim.Level;
                        victim.Level = (vi > 0 ? g.Items[vi - 1].Level : goodLevel) + 3;    // 跳级
                        byte[] beforeBytes = File.ReadAllBytes(tmpConf);

                        string st = page.T_OnSave();
                        Chk("㉑l 页面保存被结构校验拦下（状态行含「结构校验没通过」）",
                            st.IndexOf("结构校验没通过", StringComparison.Ordinal) >= 0,
                            st);
                        Chk("㉑m 页面被拦后 menus.conf 一字节没动",
                            BytesEqual(beforeBytes, File.ReadAllBytes(tmpConf)), "");

                        var issues = page.T_Validate();
                        string probe = MenuEditorPage.T_DialogProbe(issues);
                        bool rowsOk = issues.Count > 0;
                        foreach (var x in issues)
                            if (x.Where.IndexOf("行 / 条目「", StringComparison.Ordinal) < 0) rowsOk = false;
                        Chk("㉑n 验证窗逐条列「第 N 行 / 条目 X：原因」",
                            rowsOk && probe.IndexOf("行 / 条目「", StringComparison.Ordinal) >= 0,
                            probe);
                        Chk("㉑o 验证窗按钮 = 继续编辑 / 放弃修改",
                            probe.IndexOf("按钮=[继续编辑/放弃修改]", StringComparison.Ordinal) >= 0,
                            probe);

                        // 双击（同一个方法）⇒ 选中该条目
                        MenuStructureIssue jumpTarget = null;
                        foreach (var x in issues) if (x.Item != null) { jumpTarget = x; break; }
                        Chk("㉑p 双击某行 ⇒ 跳到该条目（选中它）",
                            jumpTarget != null && page.T_JumpToIssue(jumpTarget),
                            jumpTarget == null ? "问题列表里没有可跳转的条目" : jumpTarget.Where);

                        // 还原 ⇒ 保存必须恢复成功
                        victim.Level = goodLevel;
                        string okMsg = page.T_SaveConf();
                        Chk("㉑q 还原后保存成功（闸只拦非法结构）+ 成功路径不留 .tmp",
                            okMsg.StartsWith("OK") && !File.Exists(tmpConf + ".tmp"), okMsg);
                    }
                }
                catch (Exception exC1)
                {
                    Chk("㉑ C1 保存前校验闸", false, "异常：" + exC1.GetType().Name + ": " + exC1.Message);
                }

                // ============================================================
                // ㉒ C2：条目跨分组移动 / 复制
                //   独立沙箱 state\l7-crossgroup（不动上面那台 page 的状态）。
                //   ① 带 3 层子树的条目搬到另一分组：子树完整 + Level 重算 + 源分组彻底消失
                //   ② 复制到分组：新条目 ID 唯一 + 内容一致 + 原条目不变
                //   ③ 多选 N 条一起搬（走右键菜单 → 选目标 → 搬）：两分组条目总数守恒
                //   ④ 保存往返仍逐字节幂等
                //   ⑤ 反向：搬完会让父条目变空壳 ⇒ C1 闸拒绝 + 整体回滚（不写盘）
                // ============================================================
                try
                {
                    string l7Root = Path.Combine(root, "state", "l7-crossgroup");
                    string l7Conf = Path.Combine(l7Root, "config", "menus.conf");
                    Directory.CreateDirectory(Path.Combine(l7Root, "config"));
                    Directory.CreateDirectory(Path.Combine(l7Root, "cache"));
                    // 目录跨次运行会留痕（备份 / tmp）⇒ 先清干净，沙箱每轮从同一基线开始
                    foreach (var old in Directory.GetFiles(Path.GetDirectoryName(l7Conf), "menus.conf.bak.*"))
                        try { File.Delete(old); } catch { }
                    try { if (File.Exists(l7Conf + ".tmp")) File.Delete(l7Conf + ".tmp"); } catch { }
                    File.Copy(srcConf, l7Conf, true);

                    var p2 = new MenuEditorPage(l7Root);

                    int giTool = GroupIndexByName(p2, "通用菜单");
                    int giDef = GroupIndexByName(p2, "默认菜单");
                    int giText = GroupIndexByName(p2, "文本");
                    int giZip = GroupIndexByName(p2, "压缩包");

                    if (giTool < 0 || giDef < 0 || giText < 0 || giZip < 0)
                    {
                        Chk("㉒a 跨分组场景所需分组齐全（通用菜单/默认菜单/文本/压缩包）", false,
                            "下标=" + giTool + "," + giDef + "," + giText + "," + giZip);
                    }
                    else
                    {
                        // ---- 右键菜单：两项齐全，且紧跟在「复制条目」之后 ----
                        var hs = p2.T_MenuHeaders();
                        int ixDup = hs.IndexOf("复制条目");
                        int ixMove = hs.IndexOf("移动到分组…");
                        int ixCopy = hs.IndexOf("复制到分组…");
                        Chk("㉒a 右键菜单含「移动到分组…」「复制到分组…」（相邻，排在「复制条目」之后）",
                            ixDup >= 0 && ixMove == ixDup + 1 && ixCopy == ixMove + 1,
                            string.Join(" / ", hs.ToArray()));

                        // ---- 目标选择窗：候选 = 除当前分组外的全部分组，按钮 = 移动/复制 + 取消 ----
                        p2.T_SelectGroup(giTool);
                        var cands = p2.T_PickerGroupNames();
                        Chk("㉒b 目标选择窗排除当前分组、含其余全部分组",
                            cands.Count == p2.T_Groups().Count - 1
                            && !cands.Contains("通用菜单") && cands.Contains("默认菜单"),
                            "候选=" + cands.Count + "/" + (p2.T_Groups().Count - 1));
                        string probeMove = p2.T_PickerProbe(false);
                        string probeCopy = p2.T_PickerProbe(true);
                        Chk("㉒c 选择窗文案/按钮对得上（移动=整棵子树，复制=新条目）",
                            probeMove.IndexOf("移动到分组", StringComparison.Ordinal) >= 0
                            && probeMove.IndexOf("按钮=[移动/取消]", StringComparison.Ordinal) >= 0
                            && probeCopy.IndexOf("复制到分组", StringComparison.Ordinal) >= 0
                            && probeCopy.IndexOf("按钮=[复制/取消]", StringComparison.Ordinal) >= 0
                            && probeCopy.IndexOf("新条目", StringComparison.Ordinal) >= 0,
                            probeMove);

                        // ---- 造一棵 3 层子树：工具(lv1) → PowerShell(lv2, 改成 group) → zz-l7-l3-leaf(lv3) ----
                        var toolNode = FindNodeByTitleDeep(p2.T_Roots(), "工具");
                        var psNode = toolNode == null ? null : FindNodeByTitleDeep(toolNode.Children, "PowerShell");
                        MItem leaf = null;
                        if (toolNode == null || psNode == null)
                        {
                            Chk("㉒d 构造 3 层子树（样例缺「工具 / PowerShell」）", false, "样例不足，不算通过");
                        }
                        else
                        {
                            // 中间层必须真是 group（否则"非 group 有子项"会被 C1 拒，构造不出来）
                            psNode.Item.Type = ActionType.Group;
                            psNode.Item.Target = "";
                            psNode.Item.Flag = ActionFlag.None;
                            p2.T_Select(psNode.Item);
                            p2.T_AddItem();
                            leaf = p2.T_Selected();
                            leaf.Title = "zz-l7-l3-leaf";

                            Chk("㉒d 3 层子树就位（lv1/lv2/lv3）",
                                leaf != null
                                && toolNode.Item.Level + 2 == leaf.Level
                                && psNode.Item.Level == toolNode.Item.Level + 1,
                                "工具 lv" + toolNode.Item.Level + " / PowerShell lv" + psNode.Item.Level
                                + " / 叶子 lv" + (leaf == null ? "?" : leaf.Level.ToString()));
                        }

                        if (toolNode != null && psNode != null && leaf != null)
                        {
                            int srcBase = p2.T_GroupBaseLevel(giTool);
                            int dstBase = p2.T_GroupBaseLevel(giDef);

                            // ============ ⑤ 反向：搬走唯一子项 ⇒ 父条目变空壳 ⇒ C1 闸拒绝 ============
                            var srcSnap = new List<MItem>(p2.T_Groups()[giTool].Items);
                            int dstCount0 = p2.T_GroupItemCount(giDef);
                            p2.T_Select(leaf);
                            string rj = p2.T_MoveSelectionToGroup(giDef, false);
                            Chk("㉒e 会把父条目搬成空壳 ⇒ 被 C1 闸拒绝（PARENT_NO_CHILD 文案）",
                                rj.StartsWith("REJECT", StringComparison.Ordinal)
                                && rj.IndexOf("空子菜单", StringComparison.Ordinal) >= 0,
                                rj);
                            Chk("㉒f 拒绝后两个分组整体回滚（引用集合 / Level / 目标分组条目数全不变）",
                                SameLevels(srcSnap, p2.T_Groups()[giTool].Items)
                                && p2.T_GroupItemCount(giDef) == dstCount0
                                && leaf.Level == toolNode.Item.Level + 2
                                && p2.T_Find(leaf) != null
                                && MenuConfWriter.Validate(p2.T_Groups()).Count == 0,
                                "源=" + p2.T_GroupItemCount(giTool) + " 目标=" + p2.T_GroupItemCount(giDef));

                            // ============ ① 整棵 3 层子树移动到另一分组 ============
                            // 「工具」下不止 PowerShell（还有 Node.js / curl / ffmpeg）⇒
                            // 用 DFS 块当期望值，别硬编码条数（否则样例一变就假绿/假红）。
                            var toolFresh = p2.T_Find(toolNode.Item);
                            var subItems = new List<MItem>();
                            var subDepths = new List<int>();
                            FlatBlock(toolFresh, 0, subItems, subDepths);
                            int subCount = subItems.Count;

                            int srcCount0 = p2.T_GroupItemCount(giTool);
                            int dstCount1 = p2.T_GroupItemCount(giDef);
                            p2.T_Select(toolNode.Item);
                            string mv = p2.T_MoveSelectionToGroup(giDef, false);

                            var srcNow = p2.T_Groups()[giTool].Items;
                            var dstNow = p2.T_Groups()[giDef].Items;

                            Chk("㉒g 移动整棵子树返回 OK", mv.StartsWith("OK", StringComparison.Ordinal), mv);
                            Chk("㉒h 源分组少了整棵子树、目标分组多了整棵子树（同一数量）",
                                subCount >= 3
                                && srcNow.Count == srcCount0 - subCount
                                && dstNow.Count == dstCount1 + subCount,
                                "子树=" + subCount + " 条 / 源 " + srcCount0 + "→" + srcNow.Count
                                + " / 目标 " + dstCount1 + "→" + dstNow.Count);
                            bool anyLeft = false;
                            foreach (var x in subItems) if (srcNow.Contains(x)) anyLeft = true;
                            Chk("㉒i 源分组里该子树彻底消失（父 / 中间层 / 叶子 / 其余子项都不在）",
                                !anyLeft, "源分组还残留 " + srcNow.Count + " 条");

                            string wfDef;
                            bool wellDef = p2.T_WellFormedGroup(giDef, out wfDef);
                            bool tailOk = dstNow.Count >= subCount;
                            for (int k = 0; k < subCount && tailOk; k++)
                            {
                                var got = dstNow[dstNow.Count - subCount + k];
                                if (!ReferenceEquals(got, subItems[k]) || got.Level != dstBase + subDepths[k])
                                    tailOk = false;
                            }
                            Chk("㉒j 目标分组里子树完整（DFS 顺序不变）+ Level 重算（相对深度 + 目标基准）",
                                tailOk && wellDef,
                                "目标基准 lv" + dstBase + " ⇒ 工具 lv" + toolNode.Item.Level
                                + " / PowerShell lv" + psNode.Item.Level + " / 叶子 lv" + leaf.Level
                                + "（子树 " + subCount + " 条）" + wfDef);

                            // 目标分组的树里父子链必须真的挂上（不只是 Level 数字对）
                            p2.T_SelectGroup(giDef);
                            var t2 = p2.T_Find(toolNode.Item);
                            var n2 = p2.T_Find(psNode.Item);
                            var l2 = p2.T_Find(leaf);
                            Chk("㉒k 目标分组的树里父子链完整（工具 → PowerShell → 叶子）",
                                t2 != null && n2 != null && l2 != null
                                && ReferenceEquals(n2.Parent, t2) && ReferenceEquals(l2.Parent, n2),
                                t2 == null || n2 == null || l2 == null ? "有节点没进视图" : "");

                            // 落盘验证：保存后重新解析，源分组没有「工具」、目标分组是完整三层
                            string m1 = p2.T_SaveConf();
                            var disk = new ConfParser(l7Conf);
                            disk.Parse();
                            MenuGroup dSrc = null, dDst = null;
                            foreach (var g in disk.Groups)
                            {
                                if (g.Name == "通用菜单") dSrc = g;
                                if (g.Name == "默认菜单") dDst = g;
                            }
                            bool gone = dSrc != null && !ContainsTitle(dSrc.Items, "工具");
                            bool there = dDst != null && HasTitlesInOrder(dDst.Items,
                                new[] { "工具", "PowerShell", "zz-l7-l3-leaf" },
                                new[] { dstBase, dstBase + 1, dstBase + 2 });
                            Chk("㉒l 落盘：源分组没有「工具」、目标分组有完整三层（Level 已重算）",
                                m1.StartsWith("OK", StringComparison.Ordinal) && gone && there,
                                m1 + " | 源有工具=" + !gone + " 目标三层=" + there);

                            // ============ ② 复制到分组：新对象 + 内容一致 + 原条目不变 ============
                            p2.T_SelectGroup(giDef);
                            var copySnap = new List<MItem>(p2.T_Groups()[giDef].Items);
                            int textCount0 = p2.T_GroupItemCount(giText);

                            p2.T_Select(toolNode.Item);
                            string cp = p2.T_MoveSelectionToGroup(giText, true);
                            var textNow = p2.T_Groups()[giText].Items;

                            bool noShared = true;
                            foreach (var x in textNow) if (subItems.Contains(x)) noShared = false;

                            bool sameContent = textNow.Count >= subCount;
                            for (int k = 0; k < subCount && sameContent; k++)
                            {
                                var got = textNow[textNow.Count - subCount + k];
                                var exp = subItems[k];
                                if (got.Title != exp.Title || got.Hotkey != exp.Hotkey
                                    || got.Type != exp.Type || got.Flag != exp.Flag
                                    || (got.Target ?? "") != (exp.Target ?? ""))
                                    sameContent = false;
                            }

                            Chk("㉒m 复制 OK 且目标分组多了整棵子树（" + subCount + " 条）",
                                cp.StartsWith("OK", StringComparison.Ordinal)
                                && textNow.Count == textCount0 + subCount, cp);
                            Chk("㉒n 复制件是**新对象**（不与原条目共用引用）且内容逐条一致",
                                noShared && sameContent
                                && textNow[textNow.Count - subCount].Title == "工具"
                                && textNow[textNow.Count - subCount + 1].Title == "PowerShell"
                                && textNow[textNow.Count - subCount + 2].Title == "zz-l7-l3-leaf",
                                "共用引用=" + !noShared + " 内容一致=" + sameContent);
                            Chk("㉒o 复制后目标分组内**身份键全唯一**（ID 唯一断言）",
                                AllIdentityUnique(textNow),
                                "条目数=" + textNow.Count);
                            Chk("㉒p 复制后源分组原条目一条没动（引用 / 顺序 / Level 全同）",
                                SameLevels(copySnap, p2.T_Groups()[giDef].Items),
                                "源 " + copySnap.Count + "→" + p2.T_Groups()[giDef].Items.Count);

                            // ============ ③ 多选 2 条 → 右键「移动到分组…」→ 一起搬 ============
                            p2.T_SelectGroup(giText);
                            var txt = p2.T_Groups()[giText].Items;
                            var ma = txt[0];          // 记事本
                            var mb = txt[1];          // VS Code
                            int textCount1 = txt.Count;
                            int zipCount0 = p2.T_GroupItemCount(giZip);
                            int totalBefore = TotalItems(p2.T_Groups());

                            p2.T_ClickNode(ma, ModifierKeys.None);
                            p2.T_ClickNode(mb, ModifierKeys.Control);
                            Chk("㉒q Ctrl 多选命中 2 条",
                                p2.T_MarkedCount() == 2 && p2.T_Marked().Contains(ma)
                                && p2.T_Marked().Contains(mb),
                                "marked=" + p2.T_MarkedCount());

                            p2.T_SetGroupPicker(delegate(bool copy) { return giZip; });
                            bool clicked = p2.T_ClickMenu("移动到分组…");
                            p2.T_SetGroupPicker(null);      // 还原真实弹窗（后续没人再用替身）

                            Chk("㉒r 右键「移动到分组…」→ 2 条一起搬（走菜单 → 选目标 → 搬同一条链路）",
                                clicked
                                && p2.T_GroupItemCount(giZip) == zipCount0 + 2
                                && txt.Count == textCount1 - 2
                                && TotalItems(p2.T_Groups()) == totalBefore,
                                "点击=" + clicked + " 压缩包 " + zipCount0 + "→" + p2.T_GroupItemCount(giZip)
                                + " 文本 " + textCount1 + "→" + txt.Count
                                + " 总数 " + totalBefore + "→" + TotalItems(p2.T_Groups()));

                            // Shift 连选（同一个 HandleNodeClick）：锚点 → 目标整段
                            p2.T_SelectGroup(giDef);
                            var droots = p2.T_Roots();
                            if (droots.Count >= 3)
                            {
                                p2.T_ClickNode(droots[0].Item, ModifierKeys.None);
                                p2.T_ClickNode(droots[2].Item, ModifierKeys.Shift);
                                Chk("㉒s Shift 连选 = 锚点到目标区间（3 条）",
                                    p2.T_MarkedCount() == 3, "marked=" + p2.T_MarkedCount());
                            }
                            else
                            {
                                Chk("㉒s Shift 连选 = 锚点到目标区间（3 条）", false,
                                    "★未执行：根节点不足 3 个（样例不足，不算通过）");
                            }
                            if (droots.Count > 0) p2.T_Select(droots[0].Item);

                            // ============ ④ 保存往返仍逐字节幂等 ============
                            string m2 = p2.T_SaveConf();
                            byte[] b1 = File.ReadAllBytes(l7Conf);
                            p2.T_SaveConf();
                            byte[] b2 = File.ReadAllBytes(l7Conf);
                            Chk("㉒t 保存成功 + 连存两次逐字节一致（幂等）",
                                m2.StartsWith("OK", StringComparison.Ordinal) && BytesEqual(b1, b2),
                                m2 + " | " + b1.Length + "B / " + b2.Length + "B");
                            Chk("㉒u 保存后磁盘与内存模型逐条一致",
                                ModelEqualsFile(p2, l7Conf, out string whyL7), whyL7);

                            // 真实配置全程未被触碰（C2 的沙箱纪律）
                            Chk("㉒v 真实 menus.conf 未被 C2 用例写（沙箱隔离）",
                                BytesEqual(File.ReadAllBytes(srcConf), File.ReadAllBytes(srcConf)), "");
                        }
                    }
                }
                catch (Exception exC2)
                {
                    Chk("㉒ C2 跨分组移动/复制", false, "异常：" + exC2.GetType().Name + ": " + exC2.Message);
                }
            }
            catch (Exception ex)
            {
                Chk("异常", false, ex.GetType().Name + ": " + ex.Message);
                Lines.Add(ex.StackTrace);
            }

            Lines.Add("");
            Lines.Add("说明：结构操作全部走 MenuEditorPage 测试缝（与产品路径同一份实现）；");
            Lines.Add("      沙箱 = state\\ui-test-tree\\config\\menus.conf（副本）+ state\\b4a-broken\\（故意写坏的配置），不动真实配置。");
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

        /// <summary>SHA256（十六进制大写）—— 判"原文件一字节没动"用。</summary>
        private static string Sha256(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte x in h) sb.Append(x.ToString("X2"));
                return sb.ToString();
            }
        }

        /// <summary>重新从磁盘解析一份**独立模型**（夹具要改结构，不能污染页面那份）。</summary>
        private static ConfParser ParseAgain(string conf)
        {
            var p = new ConfParser(conf);
            p.Parse();
            return p;
        }

        /// <summary>第一个**真有层级**的分组（不跳级夹具需要一个"上一条比它浅"的位置）。</summary>
        private static MenuGroup FirstGroupWithChild(List<MenuGroup> groups)
        {
            foreach (var g in groups)
                if (g != null && HasChild(g.Items)) return g;
            return (groups != null && groups.Count > 0) ? groups[0] : null;
        }

        /// <summary>第一条"比前一条更深"的条目下标（= 第一个子条目）。找不到 ⇒ 1（安全兜底）。</summary>
        private static int FirstChildIndex(List<MItem> items)
        {
            for (int i = 1; i < items.Count; i++)
                if (items[i].Level > items[i - 1].Level) return i;
            return items.Count > 1 ? 1 : 0;
        }

        private static bool HasCode(List<MenuStructureIssue> issues, string code)
        {
            if (issues == null) return false;
            foreach (var x in issues) if (x.Code == code) return true;
            return false;
        }

        private static string Describe(List<MenuStructureIssue> issues)
        {
            if (issues == null || issues.Count == 0) return "0 处";
            var parts = new List<string>();
            foreach (var x in issues) parts.Add("[" + x.Code + "] " + x.Where);
            return string.Join("  ‖  ", parts.ToArray());
        }

        /// <summary>该串里是否含中日韩统一表意文字（用于断言"落盘不含中文"）。</summary>
        private static bool IsCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (c >= '\u4E00' && c <= '\u9FFF') return true;
            return false;
        }

        // ============================================================
        // ★ C2 辅助：跨分组移动 / 复制用例
        // ============================================================

        /// <summary>按分组名找 `_groups` 下标（找不到 ⇒ -1）。</summary>
        private static int GroupIndexByName(MenuEditorPage page, string name)
        {
            var gs = page.T_Groups();
            for (int i = 0; i < gs.Count; i++)
                if (gs[i] != null && (gs[i].Name ?? "").Trim() == name) return i;
            return -1;
        }

        /// <summary>在树里按标题深度优先找节点（标题完全相等）。</summary>
        private static MenuEditorPage.NodeRow FindNodeByTitleDeep(
            IEnumerable<MenuEditorPage.NodeRow> nodes, string title)
        {
            foreach (var n in nodes)
            {
                if (n.Item != null && n.Item.Title == title) return n;
                var r = FindNodeByTitleDeep(n.Children, title);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>把一棵子树压成 DFS 顺序的（条目, 相对深度）—— 与产品侧搬动块同口径。</summary>
        private static void FlatBlock(MenuEditorPage.NodeRow n, int depth,
            List<MItem> items, List<int> depths)
        {
            if (n == null) return;
            items.Add(n.Item);
            depths.Add(depth);
            foreach (var c in n.Children) FlatBlock(c, depth + 1, items, depths);
        }

        /// <summary>扁平条目里是否有该标题。</summary>
        private static bool ContainsTitle(List<MItem> items, string title)
        {
            foreach (var it in items) if (it != null && it.Title == title) return true;
            return false;
        }

        /// <summary>
        /// 扁平条目里是否**按顺序**出现这几条标题，且 Level 与期望一一对应
        /// （判"搬迁后子树完整 + Level 重算"用）。
        /// </summary>
        private static bool HasTitlesInOrder(List<MItem> items, string[] titles, int[] levels)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null || items[i].Title != titles[0]) continue;
                if (i + titles.Length > items.Count) continue;
                bool ok = true;
                for (int k = 0; k < titles.Length; k++)
                {
                    if (items[i + k].Title != titles[k] || items[i + k].Level != levels[k]) { ok = false; break; }
                }
                if (ok) return true;
            }
            return false;
        }

        /// <summary>两条扁平列表**逐位**同引用 + 同 Level（判"操作没动源分组"用）。</summary>
        private static bool SameLevels(List<MItem> a, List<MItem> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!ReferenceEquals(a[i], b[i]) || a[i].Level != b[i].Level) return false;
            return true;
        }

        /// <summary>全分组条目总数（判"两分组搬运守恒"用）。</summary>
        private static int TotalItems(List<MenuGroup> groups)
        {
            int n = 0;
            foreach (var g in groups)
                if (g != null && g.Items != null) n += g.Items.Count;
            return n;
        }

        /// <summary>同一菜单内「条目身份键」是否全唯一（= MenuSelfTest 的 ID 唯一口径）。</summary>
        private static bool AllIdentityUnique(List<MItem> items)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var it in items)
                if (it != null && !seen.Add(IdentityOf(it))) return false;
            return true;
        }

        /// <summary>
        /// 条目身份键（与 `MenuStructureValidator.IdentityKey` **同口径**）：
        /// 标题 + 快捷键(小写) + 动作类型 + flag + 目标。
        /// </summary>
        private static string IdentityOf(MItem it)
        {
            return (it.Title ?? "").Trim()
                + "\u0001" + ((it.Hotkey ?? "").Trim().ToLowerInvariant())
                + "\u0001" + (int)it.Type
                + "\u0001" + (int)it.Flag
                + "\u0001" + (it.Target ?? "").Trim();
        }

        /// <summary>一行菜单文法里「动作类型」那一段（`|` 之后第一个 token）；无 `|` ⇒ ""。</summary>
        private static string TypeToken(string line)
        {
            if (string.IsNullOrEmpty(line)) return "";
            int bar = line.IndexOf('|');
            if (bar < 0) return "";
            var rest = line.Substring(bar + 1).Trim();
            int sp = rest.IndexOf(' ');
            return sp < 0 ? rest : rest.Substring(0, sp);
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
