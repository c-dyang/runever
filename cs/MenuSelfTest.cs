using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using RunEverCs.Models;
using RunEverCs.Services;
using RunEverCs.Native;

namespace RunEverCs
{
    /// <summary>
    /// 菜单渲染自检（无窗口）：验证同名项共存、层级、图标。
    /// 用法：RunEverCs.exe --selftest-menu &lt;menus.conf 或根目录&gt; &lt;out.txt&gt;
    ///
    /// ★ v8 最小改动（C2，为让 AC-4/AC-10 判据有效）：原来只把
    ///   `MenuBuilder.SelfTest` 的**字符串**追加进报告、从不检查内容 ⇒ 报告里出现
    ///   `FAIL` / `有失败` 时 exit 码仍恒为 0（**假绿**）。现在补两层判据：
    ///     ① 返回码：报告含 `FAIL` 或 `有失败` ⇒ rc = 1；
    ///     ② 必需断言文本：`OK 全局唯一` / `OK 无冲突` / `OK 同名项共存` 三者必须出现。
    ///   除本改动外，本文件其余逻辑与断言文本**零改动**（自检打印内容保持可比对）。
    /// </summary>
    public static class MenuSelfTest
    {
        /// <summary>AC-4 的必需断言文本（缺失即判失败）。</summary>
        private static readonly string[] RequiredOkTokens = {
            "OK 全局唯一", "OK 无冲突", "OK 同名项共存"
        };

        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;
            string conf = args[1];
            string outFile = args[2];

            // ★ B4a-2：参数口径放宽 —— 既接受 `<root>\config\menus.conf`，也接受根目录
            //   （`--selftest-menu . out.txt` 与 `--selftest-menueditor <root> <out>` 口径一致）。
            if (Directory.Exists(conf))
                conf = System.IO.Path.Combine(conf, "config", "menus.conf");

            var sb = new StringBuilder();
            int rc = 0;
            try
            {
                var p = new ConfParser(conf);
                p.Parse();
                sb.AppendLine("=== C# 菜单渲染自检 ===");
                sb.AppendLine("分组数 = " + p.Groups.Count);
                // ★ L2/F3：解析层结构规范化的可观测数字（正常配置 =0）
                sb.AppendLine(p.NormalizeReport);

                // 用 always 组做渲染样本（含 group 标题 → 验证子菜单）
                var sample = new List<MenuGroup>();
                foreach (var g in p.Groups)
                {
                    if (g.Type == GroupType.Always) sample.Add(g);
                }
                if (sample.Count == 0) sample.AddRange(p.Groups);

                sb.AppendLine("样本分组数 = " + sample.Count);

                // ★ 图标解析（对应 AHK ResolveIconPath）
                string cachePath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(conf), "..", "cache", "paths.txt");
                var cache = new PathCache(cachePath);
                sb.AppendLine("路径缓存条目数 = " + cache.Count);
                var ir = new IconResolver(cache);
                ir.ResolveAll(sample);

                int withIcon = 0, totalLeaf = 0;
                foreach (var g in sample)
                    foreach (var it in g.Items)
                        if (!it.IsGroupHeader)
                        {
                            totalLeaf++;
                            if (!string.IsNullOrEmpty(it.IconSource)) withIcon++;
                        }
                sb.AppendLine("图标源命中 = " + withIcon + " / " + totalLeaf
                    + "（未命中走兜底图标）");

                sb.AppendLine(MenuBuilder.SelfTest(sample));

                // ★ 2026-11-08：菜单里「空格 = 回车」（WH_MSGFILTER 改写）的判定核心。
                //   纯函数级断言 —— 真机按键靠系统菜单循环喂进来，这里只证明"改写规则对了"。
                var m1 = new Win32.MSG();
                m1.wParam = (IntPtr)Win32.VK_SPACE;
                bool c1 = NativeMenu.TryConvertSpace(Win32.MSGF_MENU, (int)Win32.WM_KEYDOWN, ref m1);
                sb.AppendLine((c1 && m1.wParam.ToInt32() == Win32.VK_RETURN ? "[OK]   " : "[FAIL] ")
                    + "空格→回车：菜单消息 + 按键按下 + 空格 ⇒ 改写成回车（实得 wParam=0x"
                    + m1.wParam.ToInt32().ToString("X") + "）");

                var m2 = new Win32.MSG();
                m2.wParam = (IntPtr)'a';
                bool c2 = NativeMenu.TryConvertSpace(Win32.MSGF_MENU, (int)Win32.WM_KEYDOWN, ref m2);
                sb.AppendLine((!c2 && m2.wParam.ToInt32() == 'a' ? "[OK]   " : "[FAIL] ")
                    + "普通键不动：字母 a 原样放行（菜单助记不受影响）");

                var m3 = new Win32.MSG();
                m3.wParam = (IntPtr)Win32.VK_SPACE;
                bool c3 = NativeMenu.TryConvertSpace(0, (int)Win32.WM_KEYDOWN, ref m3);
                sb.AppendLine((!c3 ? "[OK]   " : "[FAIL] ")
                    + "非菜单消息不动：nCode 不是 MSGF_MENU 时空格原样放行（不干扰对话框/文本框）");

                var m4 = new Win32.MSG();
                m4.wParam = (IntPtr)Win32.VK_SPACE;
                bool c4 = NativeMenu.TryConvertSpace(Win32.MSGF_MENU, 0x0101, ref m4);   // WM_KEYUP
                sb.AppendLine((!c4 ? "[OK]   " : "[FAIL] ")
                    + "抬起事件不动：只改写按下（WM_KEYDOWN），避免重复触发");

                // ============================================================
                // ★ B4a-2（P0 体验）：菜单全空 ⇒ 禁用占位项
                //   ① 空配置：MenuBuilder.ItemCount 必须为 0（保留"跳过空分组"的既有语义）
                //   ② 占位项：真的进得去、文本对、状态带 MF_GRAYED（看得见但点不动）
                // ============================================================
                var emptyBuild = MenuBuilder.Build(new List<MenuGroup>());
                sb.AppendLine((emptyBuild.ItemCount == 0 ? "[OK]   " : "[FAIL] ")
                    + "空配置：可点条目=0（MenuBuilder 跳过空/无条目分组，既有语义不变）");

                emptyBuild.Menu.AddItemDisabled("（没有可显示的条目）");
                uint st = Win32.GetMenuState(emptyBuild.Menu.Handle, 0, Win32.MF_BYPOSITION);
                bool grayed = (st & Win32.MF_GRAYED) != 0;
                string phText = emptyBuild.Menu.GetItemText(0);
                bool phOk = emptyBuild.Menu.Count() == 1
                    && phText == "（没有可显示的条目）" && grayed;
                sb.AppendLine((phOk ? "[OK]   " : "[FAIL] ")
                    + "空菜单禁用占位项：项数=" + emptyBuild.Menu.Count()
                    + " 文本=[" + phText + "]"
                    + " 状态=0x" + st.ToString("X") + "（MF_GRAYED=" + grayed + "）");
                emptyBuild.Menu.Dispose();

                // ============================================================
                // ★ L2/F3：解析层容错 —— 有子项的非 group 父条目，解析后升格为 group
                //   夹具写进临时目录（不碰真实配置），断言：
                //     ① 升格后 IsGroupHeader == true，Title/Hotkey/Level/行序不变
                //     ② 子树完整（14 个子项还在、子项 Level 仍为 1）
                //     ③ 无子项的普通条目**不**被误升格（否则就是 F1 要消灭的空子菜单）
                //     ④ 原动作目标非空 ⇒ 明细里记一条"丢弃目标"，不静默吞
                //     ⑤ 统计口径：结构自动修正=3（PCTools / 清理工具 / 老工具）
                // ============================================================
                string f3Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "runever-f3-" + Guid.NewGuid().ToString("N"));
                string f3Conf = System.IO.Path.Combine(f3Dir, "config", "menus.conf");
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f3Conf));

                    var f3 = new StringBuilder();
                    f3.AppendLine("! 通用菜单  fallback");
                    f3.AppendLine("PCTools  _p  | run");
                    for (int i = 1; i <= 14; i++)
                        f3.AppendLine("  子项" + i + "  _x  | run  child" + i + ".exe");
                    f3.AppendLine("清理工具  _q  | run");
                    f3.AppendLine("  CleanWeChat  _c  | run  Clean WeChat X.exe");
                    f3.AppendLine("  磁盘空间分析器  _d  | run  SpaceSniffer.exe");
                    f3.AppendLine("老工具  _o  | run  old-tool.exe");
                    f3.AppendLine("  老工具子项  _z  | run  old-child.exe");
                    f3.AppendLine("普通条目  _n  | run  notepad.exe");
                    f3.AppendLine("兄弟叶  _s  | run  sib.exe");
                    f3.AppendLine("兄弟父  _b  | run");
                    f3.AppendLine("  兄弟子  _x  | run  sib-child.exe");
                    File.WriteAllText(f3Conf, f3.ToString(), new UTF8Encoding(false));

                    var f3p = new ConfParser(f3Conf);
                    f3p.Parse();
                    var items = (f3p.Groups.Count > 0) ? f3p.Groups[0].Items : new List<MenuItem>();

                    MenuItem pct = null, clean = null, oldTool = null, plain = null, sibLeaf = null, sibParent = null;
                    foreach (var it in items)
                    {
                        if (it.Title == "PCTools") pct = it;
                        else if (it.Title == "清理工具") clean = it;
                        else if (it.Title == "老工具") oldTool = it;
                        else if (it.Title == "普通条目") plain = it;
                        else if (it.Title == "兄弟叶") sibLeaf = it;
                        else if (it.Title == "兄弟父") sibParent = it;
                    }

                    int pctIdx = (pct == null) ? -1 : items.IndexOf(pct);
                    int pctChildren = 0;
                    if (pctIdx >= 0)
                        for (int i = pctIdx + 1; i < items.Count && items[i].Level > pct.Level; i++)
                            pctChildren++;

                    int cleanIdx = (clean == null) ? -1 : items.IndexOf(clean);
                    int cleanChildren = 0;
                    if (cleanIdx >= 0)
                        for (int i = cleanIdx + 1; i < items.Count && items[i].Level > clean.Level; i++)
                            cleanChildren++;

                    bool f3ok1 = pct != null && pct.IsGroupHeader && pct.Level == 0
                              && pct.Target == "" && pct.Hotkey == "p";
                    sb.AppendLine((f3ok1 ? "[OK]   " : "[FAIL] ")
                        + "F3① 非 group 父条目升格: PCTools Type="
                        + (pct == null ? "<null>" : pct.Type.ToString())
                        + " IsGroupHeader=" + (pct != null && pct.IsGroupHeader)
                        + " Level=" + (pct == null ? -1 : pct.Level)
                        + " 目标=[" + (pct == null ? "" : pct.Target) + "]");

                    bool f3ok2 = pctChildren == 14 && cleanChildren == 2 && items.Count == 24;

                    // 子项 Level 必须原样（1），行序必须与配置一致（只升格 Type，不动结构）
                    bool childLevelsOk = true;
                    if (pctIdx >= 0)
                        for (int i = pctIdx + 1; i < items.Count && items[i].Level > pct.Level; i++)
                            if (items[i].Level != 1) childLevelsOk = false;

                    var expectTitles = new List<string>();
                    expectTitles.Add("PCTools");
                    for (int i = 1; i <= 14; i++) expectTitles.Add("子项" + i);
                    expectTitles.Add("清理工具");
                    expectTitles.Add("CleanWeChat");
                    expectTitles.Add("磁盘空间分析器");
                    expectTitles.Add("老工具");
                    expectTitles.Add("老工具子项");
                    expectTitles.Add("普通条目");
                    expectTitles.Add("兄弟叶");
                    expectTitles.Add("兄弟父");
                    expectTitles.Add("兄弟子");

                    bool orderOk = (expectTitles.Count == items.Count);
                    if (orderOk)
                        for (int i = 0; i < items.Count; i++)
                            if (items[i].Title != expectTitles[i]) { orderOk = false; break; }

                    f3ok2 = f3ok2 && childLevelsOk && orderOk;
                    sb.AppendLine((f3ok2 ? "[OK]   " : "[FAIL] ")
                        + "F3② 子树完整/行序不变: 条目总数=" + items.Count + "（期望 24）"
                        + " PCTools 子项=" + pctChildren + "（期望 14）"
                        + " 清理工具 子项=" + cleanChildren + "（期望 2）"
                        + " 子项Level不变=" + childLevelsOk + " 行序一致=" + orderOk);

                    bool f3ok3 = plain != null && !plain.IsGroupHeader
                              && plain.Type == ActionType.Run && plain.Target == "notepad.exe"
                              && sibLeaf != null && !sibLeaf.IsGroupHeader
                              && sibParent != null && sibParent.IsGroupHeader;
                    sb.AppendLine((f3ok3 ? "[OK]   " : "[FAIL] ")
                        + "F3③ 无子项普通条目不误升格: 普通条目 Type="
                        + (plain == null ? "<null>" : plain.Type.ToString())
                        + " 目标=[" + (plain == null ? "" : plain.Target) + "]"
                        + " | 兄弟叶(后随更深条目)仍为普通=" + (sibLeaf != null && !sibLeaf.IsGroupHeader)
                        + " 兄弟父已升格=" + (sibParent != null && sibParent.IsGroupHeader));

                    bool f3ok4 = oldTool != null && oldTool.IsGroupHeader && oldTool.Target == "";
                    bool c5 = false;
                    foreach (var n in f3p.AutoFixNotes)
                        if (n.IndexOf("丢弃动作目标「old-tool.exe」", StringComparison.Ordinal) >= 0) c5 = true;
                    sb.AppendLine((f3ok4 && c5 ? "[OK]   " : "[FAIL] ")
                        + "F3④ 非空目标丢弃有留痕: 老工具升格=" + f3ok4
                        + " 明细含「丢弃动作目标」=" + c5
                        + (f3p.AutoFixNotes.Count > 0 ? " 例=" + f3p.AutoFixNotes[0] : ""));

                    bool f3ok5 = f3p.AutoFixCount == 4 && f3p.NormalizeReport == "结构自动修正=4";
                    sb.AppendLine((f3ok5 ? "[OK]   " : "[FAIL] ")
                        + "F3⑤ 规范化计数: " + f3p.NormalizeReport + "（期望 4）");

                    f3p = null;   // 释放引用，便于删除临时目录
                }
                finally
                {
                    try { if (Directory.Exists(f3Dir)) Directory.Delete(f3Dir, true); }
                    catch { }
                }

                // ============================================================
                // ★ 2026-11-09（U-B）：`open sub <目录>` 渲染期展开
                //   夹具全在临时目录（不碰真实配置），断言：
                //     ① 修饰位识别（open 置位 / 其它动作忽略且不吃掉目标）
                //     ② auto.conf 的 5 个键经 AutoMatcher 读进 AutoConf
                //     ③ 子菜单真的建了（docs = 2 文件 + 1 子目录）
                //     ④ 空目录 / 不存在目录 ⇒ 子项=0 且退回普通 open
                //     ⑤ 30 项目录 + limit=20 ⇒ 子项=20
                //     ⑥ folder-first / name 两种排序
                //     ⑦ 构建前后 menus.conf 字节与 SHA256 不变（绝不写回）
                //     ⑧ 保存往返后配置里仍只有 `open sub` 一行，菜单不含目录内容
                //     ⑨ 总开关关闭 ⇒ 一条子菜单都不建（默认口径）
                //     ⑩ 第二遍构建走 mtime 缓存（不重扫目录）
                // ============================================================
                RunSubmenuCases(sb);
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex.Message + "\n" + ex.StackTrace);
                rc = 1;
            }

            // ★★ C2：内容判据（原来完全没有 —— 报告里有 FAIL 也返回 0 = 假绿）
            string report = sb.ToString();
            if (report.IndexOf("FAIL", StringComparison.Ordinal) >= 0
                || report.IndexOf("有失败", StringComparison.Ordinal) >= 0)
            {
                sb.AppendLine("内容判据: 报告含 FAIL/有失败 ⇒ 返回码 1");
                rc = 1;
            }
            foreach (var token in RequiredOkTokens)
            {
                if (report.IndexOf(token, StringComparison.Ordinal) < 0)
                {
                    sb.AppendLine("FAIL 内容判据: 缺少必需断言文本 [" + token + "]");
                    rc = 1;
                }
            }
            if (rc == 0) sb.AppendLine("内容判据: 必需断言文本齐备（全局唯一 / 无冲突 / 同名项共存）");

            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
            return rc;
        }

        // ============================================================
        // ★ 2026-11-09（U-B）：open sub 夹具
        // ============================================================

        private static void RunSubmenuCases(StringBuilder sb)
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "runever-ub-" + Guid.NewGuid().ToString("N"));
            string confDir = System.IO.Path.Combine(root, "config");
            string confPath = System.IO.Path.Combine(confDir, "menus.conf");
            string autoPath = System.IO.Path.Combine(confDir, "auto.conf");

            string dataDir = System.IO.Path.Combine(root, "data");
            string docsDir = System.IO.Path.Combine(dataDir, "文档 docs");   // 带空格 + 中文
            string emptyDir = System.IO.Path.Combine(dataDir, "empty");
            string manyDir = System.IO.Path.Combine(dataDir, "many");
            string orderDir = System.IO.Path.Combine(dataDir, "order");
            string missingDir = System.IO.Path.Combine(dataDir, "no-such-dir");

            var utf8 = new UTF8Encoding(false);
            try
            {
                Directory.CreateDirectory(confDir);
                Directory.CreateDirectory(docsDir);
                Directory.CreateDirectory(System.IO.Path.Combine(docsDir, "sub"));
                File.WriteAllText(System.IO.Path.Combine(docsDir, "sub", "inner.txt"), "i", utf8);
                File.WriteAllText(System.IO.Path.Combine(docsDir, "a.txt"), "a", utf8);
                File.WriteAllText(System.IO.Path.Combine(docsDir, "b.txt"), "b", utf8);

                Directory.CreateDirectory(emptyDir);

                Directory.CreateDirectory(manyDir);
                for (int i = 1; i <= 30; i++)
                    File.WriteAllText(System.IO.Path.Combine(manyDir, "f" + i.ToString("00") + ".txt"), "x", utf8);

                Directory.CreateDirectory(orderDir);
                Directory.CreateDirectory(System.IO.Path.Combine(orderDir, "adir"));
                Directory.CreateDirectory(System.IO.Path.Combine(orderDir, "zdir"));
                File.WriteAllText(System.IO.Path.Combine(orderDir, "a.txt"), "a", utf8);
                File.WriteAllText(System.IO.Path.Combine(orderDir, "b.txt"), "b", utf8);

                var conf = new StringBuilder();
                conf.AppendLine("! 子菜单  always");
                conf.AppendLine("  普通目录  _D  | open sub  \"" + docsDir + "\"");
                conf.AppendLine("  空目录  _E  | open sub  \"" + emptyDir + "\"");
                conf.AppendLine("  大目录  _M  | open sub  \"" + manyDir + "\"");
                conf.AppendLine("  排序目录  _O  | open sub  \"" + orderDir + "\"");
                conf.AppendLine("  普通打开  _P  | open  \"" + docsDir + "\"");
                conf.AppendLine("  run上写sub  _R  | run sub  notepad.exe");
                conf.AppendLine("  无此目录  _X  | open sub  \"" + missingDir + "\"");
                File.WriteAllText(confPath, conf.ToString(), utf8);

                // ---- ① 修饰位识别 ----
                var p = new ConfParser(confPath);
                p.Parse();
                var items = (p.Groups.Count > 0) ? p.Groups[0].Items : new List<MenuItem>();

                MenuItem normal = null, emptyIt = null, manyIt = null, orderIt = null,
                         plain = null, runSub = null, missingIt = null;
                foreach (var it in items)
                {
                    switch (it.Title)
                    {
                        case "普通目录": normal = it; break;
                        case "空目录": emptyIt = it; break;
                        case "大目录": manyIt = it; break;
                        case "排序目录": orderIt = it; break;
                        case "普通打开": plain = it; break;
                        case "run上写sub": runSub = it; break;
                        case "无此目录": missingIt = it; break;
                    }
                }

                bool s1 = normal != null && normal.Sub && normal.Type == ActionType.Open
                       && plain != null && !plain.Sub && plain.Type == ActionType.Open
                       && runSub != null && !runSub.Sub && runSub.Type == ActionType.Run
                       && runSub.Target == "notepad.exe"
                       && missingIt != null && missingIt.Sub;
                sb.AppendLine((s1 ? "[OK]   " : "[FAIL] ")
                    + "U-B① sub 修饰位识别: 普通目录 Sub=" + (normal != null && normal.Sub)
                    + " Type=" + (normal == null ? "?" : normal.Type.ToString())
                    + " | 普通打开 Sub=" + (plain != null && plain.Sub)
                    + " | run 上的 sub 已忽略且目标完好: Sub=" + (runSub != null && runSub.Sub)
                    + " 目标=[" + (runSub == null ? "" : runSub.Target) + "]"
                    + " | 不存在目录也保留 Sub 标记=" + (missingIt != null && missingIt.Sub));

                // ---- ② auto.conf 五个键（经 AutoMatcher 解析，不在渲染层重读）----
                File.WriteAllText(autoPath,
                    "submenuEnabled=1" + Environment.NewLine
                    + "submenuLimit=20" + Environment.NewLine
                    + "submenuOrder=folder-first" + Environment.NewLine
                    + "submenuRecursive=0" + Environment.NewLine
                    + "submenuDepth=2" + Environment.NewLine, utf8);

                var am = new AutoMatcher(root);
                am.ReadConf();
                var live = am.Conf;
                bool s2 = live.SubmenuEnabled == 1 && live.SubmenuLimit == 20
                       && live.SubmenuOrder == "folder-first"
                       && live.SubmenuRecursive == 0 && live.SubmenuDepth == 2;
                sb.AppendLine((s2 ? "[OK]   " : "[FAIL] ")
                    + "U-B② auto.conf 五键: enabled=" + live.SubmenuEnabled
                    + " limit=" + live.SubmenuLimit + " order=" + live.SubmenuOrder
                    + " recursive=" + live.SubmenuRecursive + " depth=" + live.SubmenuDepth
                    + "（期望 1/20/folder-first/0/2）");

                var groups = new List<MenuGroup> { p.Groups[0] };
                byte[] before = File.ReadAllBytes(confPath);
                string hashBefore = Sha256Hex(before);

                // ---- ③ 子菜单真的建了 ----
                var r = MenuBuilder.Build(groups, live);
                try
                {
                    int idxDocs = FindTopIndex(r.Menu, "普通目录");
                    var docsChildren = SubTexts(r.Menu, idxDocs);
                    bool s3 = idxDocs >= 0 && docsChildren.Count == 3
                           && MenuBuilder.LastSubmenus >= 1
                           && MenuBuilder.LastSubmenuItems >= 3;
                    sb.AppendLine((s3 ? "[OK]   " : "[FAIL] ")
                        + "U-B③ 子菜单创建: 普通目录 popup=" + (idxDocs >= 0)
                        + " 子项=" + docsChildren.Count + "（期望 3：a.txt/b.txt/sub）"
                        + " 文本=[" + string.Join(" | ", docsChildren.ToArray()) + "]"
                        + " 建菜单数=" + MenuBuilder.LastSubmenus);

                    // ---- ④ 空目录 / 不存在目录 ⇒ 子项 0 + 普通 open ----
                    int idxEmpty = FindTopIndex(r.Menu, "空目录");
                    int idxMissing = FindTopIndex(r.Menu, "无此目录");
                    var emptyChildren = SubTexts(r.Menu, idxEmpty);
                    var missingChildren = SubTexts(r.Menu, idxMissing);
                    bool emptyLeaf = idxEmpty >= 0 && emptyChildren.Count == 0
                                  && r.IdMap.ContainsKey(r.Menu.GetItemIdAt(idxEmpty));
                    bool missingLeaf = idxMissing >= 0 && missingChildren.Count == 0
                                  && r.IdMap.ContainsKey(r.Menu.GetItemIdAt(idxMissing));
                    bool s4 = emptyLeaf && missingLeaf && MenuBuilder.LastSubmenuFallbacks == 2;
                    sb.AppendLine((s4 ? "[OK]   " : "[FAIL] ")
                        + "U-B④ 空/失效兜底: 空目录子项=" + emptyChildren.Count
                        + " 退回普通open=" + emptyLeaf
                        + " | 不存在目录子项=" + missingChildren.Count
                        + " 退回普通open=" + missingLeaf
                        + " | 退化计数=" + MenuBuilder.LastSubmenuFallbacks + "（期望 2）");

                    // ---- ⑤ 上限截断 ----
                    int idxMany = FindTopIndex(r.Menu, "大目录");
                    int manyCount = SubTexts(r.Menu, idxMany).Count;
                    bool s5 = manyCount == 20;
                    sb.AppendLine((s5 ? "[OK]   " : "[FAIL] ")
                        + "U-B⑤ 上限截断: 30 项目录 + submenuLimit=20 ⇒ 子项=" + manyCount + "（期望 20）");

                    // ---- ⑥ folder-first 排序 ----
                    int idxOrder = FindTopIndex(r.Menu, "排序目录");
                    var orderFirst = SubTexts(r.Menu, idxOrder);
                    bool s6 = orderFirst.Count == 4
                           && orderFirst[0] == "adir" && orderFirst[1] == "zdir"
                           && orderFirst[2] == "a.txt" && orderFirst[3] == "b.txt";
                    sb.AppendLine((s6 ? "[OK]   " : "[FAIL] ")
                        + "U-B⑥ 排序 folder-first: [" + string.Join(" | ", orderFirst.ToArray())
                        + "]（期望 adir | zdir | a.txt | b.txt）");
                }
                finally
                {
                    r.Menu.Dispose();
                }

                // ---- ⑥b name 排序 ----
                live.SubmenuOrder = "name";
                var rn = MenuBuilder.Build(groups, live);
                try
                {
                    int idxOrder = FindTopIndex(rn.Menu, "排序目录");
                    var byName = SubTexts(rn.Menu, idxOrder);
                    bool s6b = byName.Count == 4
                            && byName[0] == "a.txt" && byName[1] == "adir"
                            && byName[2] == "b.txt" && byName[3] == "zdir";
                    sb.AppendLine((s6b ? "[OK]   " : "[FAIL] ")
                        + "U-B⑦ 排序 name: [" + string.Join(" | ", byName.ToArray())
                        + "]（期望 a.txt | adir | b.txt | zdir）");
                    live.SubmenuOrder = "folder-first";
                }
                finally
                {
                    rn.Menu.Dispose();
                }

                // ---- ⑧ mtime 缓存：第二遍构建不重扫目录 ----
                var rw = MenuBuilder.Build(groups, live);   // 预热缓存
                rw.Menu.Dispose();
                var rc2 = MenuBuilder.Build(groups, live);
                rc2.Menu.Dispose();
                bool s8 = MenuBuilder.LastDirCacheHits >= 3;
                sb.AppendLine((s8 ? "[OK]   " : "[FAIL] ")
                    + "U-B⑧ mtime 缓存: 第二遍构建命中缓存目录=" + MenuBuilder.LastDirCacheHits
                    + "（期望 >=3：docs/many/order 都走缓存）");

                // ---- ⑨ 总开关关闭 ⇒ 一条子菜单都不建 ----
                var off = new AutoConf { SubmenuEnabled = 0 };
                var roff = MenuBuilder.Build(groups, off);
                try
                {
                    int idxDocs = FindTopIndex(roff.Menu, "普通目录");
                    bool offLeaf = idxDocs >= 0 && SubTexts(roff.Menu, idxDocs).Count == 0
                                && roff.IdMap.ContainsKey(roff.Menu.GetItemIdAt(idxDocs));
                    bool s9 = offLeaf && MenuBuilder.LastSubmenus == 0;
                    sb.AppendLine((s9 ? "[OK]   " : "[FAIL] ")
                        + "U-B⑨ 总开关默认关: submenuEnabled=0 ⇒ 子菜单数="
                        + MenuBuilder.LastSubmenus + " 普通目录退回普通 open=" + offLeaf);
                }
                finally
                {
                    roff.Menu.Dispose();
                }

                // ---- ⑨b 递归：recursive=1 + depth=2 时子目录再展开一层；depth=1 时仍是叶 ----
                var rec2 = new AutoConf
                {
                    SubmenuEnabled = 1,
                    SubmenuLimit = 20,
                    SubmenuOrder = "folder-first",
                    SubmenuRecursive = 1,
                    SubmenuDepth = 2
                };
                var rr2 = MenuBuilder.Build(groups, rec2);
                bool deepOk, shallowOk = false;
                try
                {
                    IntPtr docsHandle = rr2.Menu.GetSubMenuHandle(FindTopIndex(rr2.Menu, "普通目录"));
                    var docsMenu = NativeMenu.FromHandle(docsHandle);
                    int idxSub = FindTopIndex(docsMenu, "sub");
                    bool deep = idxSub >= 0 && SubTexts(docsMenu, idxSub).Count == 1;

                    var rec1 = new AutoConf
                    {
                        SubmenuEnabled = 1,
                        SubmenuLimit = 20,
                        SubmenuOrder = "folder-first",
                        SubmenuRecursive = 1,
                        SubmenuDepth = 1
                    };
                    var rr1 = MenuBuilder.Build(groups, rec1);
                    try
                    {
                        IntPtr h1 = rr1.Menu.GetSubMenuHandle(FindTopIndex(rr1.Menu, "普通目录"));
                        var m1 = NativeMenu.FromHandle(h1);
                        int s1Idx = FindTopIndex(m1, "sub");
                        shallowOk = s1Idx >= 0 && SubTexts(m1, s1Idx).Count == 0;
                    }
                    finally { rr1.Menu.Dispose(); }

                    deepOk = deep;
                }
                finally
                {
                    rr2.Menu.Dispose();
                }
                sb.AppendLine((deepOk && shallowOk ? "[OK]   " : "[FAIL] ")
                    + "U-B⑨b 递归: recursive=1 depth=2 ⇒ 子目录 sub 再展开一层=" + deepOk
                    + "（内含 1 项）| depth=1 ⇒ 子目录保持叶=" + shallowOk);

                // ---- ⑩ 不写回配置（字节 + SHA256）----
                byte[] after = File.ReadAllBytes(confPath);
                string hashAfter = Sha256Hex(after);
                string confText = File.ReadAllText(confPath, Encoding.UTF8);
                bool s10 = before.Length == after.Length && hashBefore == hashAfter
                        && confText.IndexOf("a.txt", StringComparison.Ordinal) < 0
                        && confText.IndexOf("f01.txt", StringComparison.Ordinal) < 0;
                sb.AppendLine((s10 ? "[OK]   " : "[FAIL] ")
                    + "U-B⑩ 不写回配置: 构建前 " + before.Length + "B/" + hashBefore.Substring(0, 16)
                    + " 构建后 " + after.Length + "B/" + hashAfter.Substring(0, 16)
                    + " 含目录内容=" + (confText.IndexOf("a.txt", StringComparison.Ordinal) >= 0));

                // ---- ⑪ 保存往返：仍只写 `open sub` 一行，菜单不含目录内容 ----
                string serialized = MenuConfWriter.Serialize(p.Groups);
                string rtPath = System.IO.Path.Combine(confDir, "menus.roundtrip.conf");
                File.WriteAllText(rtPath, serialized, utf8);

                var p2 = new ConfParser(rtPath);
                p2.Parse();
                var rrt = MenuBuilder.Build(new List<MenuGroup> { p2.Groups[0] }, live);
                int rtChildren;
                try
                {
                    rtChildren = SubTexts(rrt.Menu, FindTopIndex(rrt.Menu, "普通目录")).Count;
                }
                finally
                {
                    rrt.Menu.Dispose();
                }

                MenuItem rtNormal = null;
                foreach (var it in p2.Groups[0].Items)
                    if (it.Title == "普通目录") rtNormal = it;

                bool s11 = serialized.IndexOf("open sub", StringComparison.Ordinal) >= 0
                        && rtNormal != null && rtNormal.Sub
                        && rtChildren == 3
                        && serialized.IndexOf("a.txt", StringComparison.Ordinal) < 0
                        && serialized.IndexOf("f01.txt", StringComparison.Ordinal) < 0;
                sb.AppendLine((s11 ? "[OK]   " : "[FAIL] ")
                    + "U-B⑪ 保存往返: 序列化含 `open sub`="
                    + (serialized.IndexOf("open sub", StringComparison.Ordinal) >= 0)
                    + " 重新解析 Sub=" + (rtNormal != null && rtNormal.Sub)
                    + " 子项=" + rtChildren + "（期望 3）"
                    + " 序列化含目录内容="
                    + (serialized.IndexOf("a.txt", StringComparison.Ordinal) >= 0));

                am = null;
                p = null;
                p2 = null;
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }

        private static int FindTopIndex(NativeMenu menu, string title)
        {
            if (menu == null) return -1;
            int n = menu.Count();
            for (int i = 0; i < n; i++)
                if (menu.GetItemText(i).IndexOf(title, StringComparison.Ordinal) >= 0) return i;
            return -1;
        }

        private static List<string> SubTexts(NativeMenu menu, int index)
        {
            var list = new List<string>();
            if (menu == null || index < 0) return list;
            IntPtr sh = menu.GetSubMenuHandle(index);
            if (sh == IntPtr.Zero) return list;

            var sub = NativeMenu.FromHandle(sh);   // 只读包装，不拥有句柄
            int n = sub.Count();
            for (int i = 0; i < n; i++) list.Add(sub.GetItemText(i));
            return list;
        }

        private static string Sha256Hex(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }
    }
}
