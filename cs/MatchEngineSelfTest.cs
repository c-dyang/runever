using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 判定层自检（AC-1 判据）—— 纯内存用例逐条断言 R1–R9 + I-1/I-2 + 缺件注入位 + 同名项共存。
    ///
    /// 用法：RunEverCs.exe --selftest-matcher &lt;root&gt; &lt;out.txt&gt;
    ///
    /// ★ 判定层零 IO 的判据：本自检的**判定用例全都不创建任何文件**（只有第 ⑧ 组
    ///   "选中项分类"用例按设计创建临时夹具 —— 那是分类器/解析层的 IO，不是判定层的）。
    /// ★ 每个断言输出 `OK/FAIL` 行，末尾 `PASS=n FAIL=m`，`exit = FAIL==0 ? 0 : 1`。
    /// ★ AC-4：同组同名 + **跨组同名**条目必须 ID 不同、都点得对 —— 派发断言走
    ///   `IdMap[ID] → MenuItem 引用 → Target`，**不接受"按标题回查"**。
    /// </summary>
    public static class MatchEngineSelfTest
    {
        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;
            string root = args[1];
            string outFile = args[2];

            var sb = new StringBuilder();
            int pass = 0, fail = 0;

            Action<bool, string, string> chk = null;
            chk = (cond, label, extra) =>
            {
                if (cond) { pass++; sb.AppendLine("  OK   " + label); }
                else { fail++; sb.AppendLine("  FAIL " + label + (extra != "" ? "   <<< " + extra : "")); }
            };
            Action<bool, string> ok = (cond, label) => chk(cond, label, "");

            try
            {
                sb.AppendLine("=== 判定层自检（R1–R9 + I-1/I-2 + 缺件注入 + 同名共存）===");
                sb.AppendLine("root = " + root);
                sb.AppendLine();

                var cfg = StandardConfig();

                // ---------------- R1 ----------------
                var d1 = MatchEngine.Decide(Ctx(text: "hello", files: 0, folders: 0), cfg);
                ok(d1.DynamicCount == 1 && d1.Dynamic[0].Name == "文本" && d1.Rule == "R1",
                    "R1 有选中文本 ⇒ text 组（rule=" + d1.Rule + "）");

                // ---------------- R2 ----------------
                var d2 = MatchEngine.Decide(Ctx(exts: new[] { "pdf" }, files: 1), cfg);
                ok(d2.DynamicCount == 1 && d2.Dynamic[0].Name == "PDF" && d2.Rule == "R2",
                    "R2 扩展名恰好命中 1 个 ext 组 ⇒ 该组（rule=" + d2.Rule + "）");

                // ---------------- U-A 一键直达（仅扩展名槽位；L1 结论 + 消费点开关） ----------------
                sb.AppendLine();
                sb.AppendLine("--- U-A 一键直达（扩展名槽位唯一可执行条目）---");

                // ① 唯一可执行条目（run）⇒ 给出直达目标
                var osCfg = ExtOnlyConfig(new[] { It("用 Word 打开", ActionType.Run, "winword.exe", 1) });
                var os1 = MatchEngine.Decide(Ctx(exts: new[] { "docx" }, files: 1), osCfg);
                ok(os1.OneShot != null && os1.OneShot.Title == "用 Word 打开"
                    && os1.OneShotReason.IndexOf("唯一", StringComparison.Ordinal) >= 0,
                    "U-A 扩展名槽位剪枝后恰 1 个可执行条目 ⇒ OneShot 指向该条（" + os1.OneShotReason + "）");

                // ② 两个可执行条目 ⇒ 不直达（让用户选）
                var os2 = MatchEngine.Decide(Ctx(exts: new[] { "docx" }, files: 1), ExtOnlyConfig(new[]
                {
                    It("用 Word 打开", ActionType.Run, "winword.exe", 1),
                    It("用写字板打开", ActionType.Run, "write.exe", 1)
                }));
                ok(os2.OneShot == null && os2.OneShotReason.IndexOf("=2", StringComparison.Ordinal) >= 0,
                    "U-A 两个可执行条目 ⇒ 不直达（" + os2.OneShotReason + "）");

                // ③ action / send / keys 一律禁直达（用户 2026-11-09 明文口径）
                foreach (var t in new[] { ActionType.Action, ActionType.Send, ActionType.Keys })
                {
                    var dt = MatchEngine.Decide(Ctx(exts: new[] { "docx" }, files: 1),
                        ExtOnlyConfig(new[] { It("副作用动作", t, "x", 1) }));
                    ok(dt.OneShot == null && dt.OneShotReason.IndexOf("类型禁止", StringComparison.Ordinal) >= 0,
                        "U-A " + t.ToString().ToLowerInvariant() + " 命中 ⇒ 强制退化弹菜单（" + dt.OneShotReason + "）");
                }

                // ④ 计数按**剪枝之后**：两条里缺件隐藏一条 ⇒ 实际 1 条 ⇒ 才直达
                var osPruneCtx = Ctx(exts: new[] { "docx" }, files: 1);
                osPruneCtx.MissingItemIndex = new HashSet<int> { 1 };
                var osPrune = MatchEngine.Decide(osPruneCtx, ExtOnlyConfig(new[]
                {
                    It("保留项", ActionType.Run, "winword.exe", 1),
                    It("缺件项", ActionType.Run, "not-exist-xyz.exe", 1)
                }));
                ok(osPrune.OneShot != null && osPrune.OneShot.Title == "保留项",
                    "U-A 计数在剪枝之后：缺件隐藏一条 ⇒ 恰 1 条 ⇒ 直达（原始条目数=2 不能算 2）");

                // ⑤ always / fallback / 文本槽位都不参与
                var osFb = MatchEngine.Decide(Ctx(exts: new[] { "xyz" }, files: 1), cfg);
                ok(osFb.OneShot == null && osFb.Dynamic[0].Type == GroupType.Fallback,
                    "U-A 兜底段不参与直达（扩展名 0 命中 ⇒ 弹菜单）");
                var osText = MatchEngine.Decide(Ctx(text: "t"), cfg);
                ok(osText.OneShot == null, "U-A 文本槽位不参与直达");
                var osAlwaysCfg = new List<MenuGroup> { G("固定", GroupType.Always) };
                osAlwaysCfg[0].Items.Add(It("设置", ActionType.Run, "RunEverCs.exe --settings", 1));
                var osAlways = MatchEngine.Decide(new MatchContext(), osAlwaysCfg);
                ok(osAlways.AlwaysCount == 1 && osAlways.OneShot == null,
                    "U-A always 段不参与直达（哪怕整段只有 1 个可执行条目）");

                // ⑥ 开关：0 ⇒ 退化弹菜单；1 ⇒ 采用 L1 结论
                ok(os1.OneShotFor(new AutoConf { OneShot = 0 }) == null,
                    "U-A oneShot=0 ⇒ OneShotFor=null（退化弹菜单）");
                ok(ReferenceEquals(os1.OneShotFor(new AutoConf { OneShot = 1 }), os1.OneShot),
                    "U-A oneShot=1 ⇒ OneShotFor 返回 L1 结论");

                // ⑦ 只加数据位：既有出口 Flat(order) 与默认序逐项一致
                ok(Names(os1.Flat(MenuOrderSpec.Parse(""))) == Names(os1.Flat()),
                    "U-A Flat(order) 既有行为不变（菜单段序唯一出口）");

                // ---------------- R3 ----------------
                var d3 = MatchEngine.Decide(Ctx(exts: new[] { "pdf", "txt" }, files: 1), cfg);
                ok(d3.FallbackShown && d3.DynamicCount == 1 && d3.Dynamic[0].Name == "兜底"
                    && d3.Rule == "R3",
                    "R3 扩展名命中 ≥2 个 ext 组 ⇒ 兜底（rule=" + d3.Rule + "）");

                // ---- 反例①（最易漏）：混合选择 + 扩展名恰命中 1 个 ⇒ 必须兜底，不是 ext ----
                var dm = MatchEngine.Decide(Ctx(exts: new[] { "pdf" }, files: 1, folders: 1), cfg);
                ok(dm.Rule == "R5" && dm.DynamicCount == 1 && dm.Dynamic[0].Name == "兜底"
                    && dm.Dynamic[0].Type == GroupType.Fallback,
                    "反例① R5 短路：1 文件 + 1 文件夹且扩展名恰命中 1 个 ext 组 ⇒ 兜底（非 ext）");

                // ---- 反例②：扩展名命中 0 个 ⇒ 兜底 ----
                var d0 = MatchEngine.Decide(Ctx(exts: new[] { "xyz" }, files: 1), cfg);
                ok(d0.FallbackShown && d0.Dynamic[0].Name == "兜底",
                    "反例② 扩展名命中 0 个 ext 组 ⇒ 兜底（rule=" + d0.Rule + "）");

                // ---------------- R4 ----------------
                var d4 = MatchEngine.Decide(Ctx(folders: 1), cfg);
                ok(d4.DynamicCount == 1 && d4.Dynamic[0].Name == "文件夹" && d4.Rule == "R4",
                    "R4 只选中文件夹 ⇒ folder 组（rule=" + d4.Rule + "）");

                // ---------------- R5 ----------------
                var d5 = MatchEngine.Decide(Ctx(files: 2, folders: 1), cfg);
                ok(d5.Rule == "R5" && d5.Dynamic[0].Name == "兜底",
                    "R5 文件 + 文件夹同时选中 ⇒ 兜底");

                // ---------------- R6 ----------------
                var d6 = MatchEngine.Decide(new MatchContext(), cfg);
                ok(d6.Rule == "R6" && d6.DynamicCount == 1 && d6.Dynamic[0].Name == "兜底",
                    "R6 无选中 ⇒ 兜底");

                // ---------------- R7 ----------------
                var d7 = MatchEngine.Decide(Ctx(proc: "chrome.exe"), cfg);
                ok(d7.ProcessCount == 1 && d7.Process[0].Name == "浏览器",
                    "R7 前台进程命中 proc 组（chrome.exe）");

                var d7b = MatchEngine.Decide(Ctx(proc: "CHROME.EXE"), cfg);
                ok(d7b.ProcessCount == 1 && d7b.Process[0].Name == "浏览器",
                    "R7 proc 值大小写不敏感（关键词写 chrome.exe）");

                // 关键词不带扩展名（配置写 "chrome"）也要命中 —— proc 值带不带 .exe 都算
                var cfgNoExt = new List<MenuGroup>(cfg);
                cfgNoExt.Add(G("火狐", GroupType.Proc, "firefox"));
                var d7d = MatchEngine.Decide(Ctx(proc: "FIREFOX.EXE"), cfgNoExt);
                ok(d7d.ProcessCount == 1 && d7d.Process[0].Name == "火狐",
                    "R7 关键词不带 .exe 也命中（配置 firefox / 进程 firefox.exe）");

                // ---- 反例③：两个 proc 组含同进程名 ⇒ 取配置顺序第一个 + Violation ----
                var dupProc = new List<MenuGroup>(cfg);
                dupProc.Add(G("浏览器2", GroupType.Proc, "chrome.exe"));
                var d7c = MatchEngine.Decide(Ctx(proc: "chrome.exe"), dupProc);
                bool hasProcViolation = false;
                foreach (var v in d7c.Violations)
                    if (v.Kind == ViolationKind.DuplicateProcName) hasProcViolation = true;
                ok(d7c.ProcessCount == 1 && d7c.Process[0].Name == "浏览器" && hasProcViolation,
                    "反例③ 两个 proc 组同进程名 ⇒ 取配置顺序第一个 + 记录 Violation");

                // ---------------- R8 ----------------
                var d8 = MatchEngine.Decide(new MatchContext(), cfg);
                ok(d8.AlwaysCount == 1 && d8.Always[0].Name == "固定",
                    "R8 always 组恒显示（无任何动态命中时仍在）");

                // ---------------- R9 顺序 ----------------
                var d9 = MatchEngine.Decide(Ctx(text: "hi", proc: "chrome.exe"), cfg);
                var flat9 = d9.Flat();
                ok(flat9.Count == 3
                    && flat9[0].Type == GroupType.Text
                    && flat9[1].Type == GroupType.Proc
                    && flat9[2].Type == GroupType.Always,
                    "R9 顺序固定：动态 → 进程 → 固定（堆叠不去重），实际=" + Names(flat9));

                // ---------------- R9b 顺序可配置（auto.conf 的 menuOrder，**三段**） ----------------
                // 动态/进程/固定各自可换位；旧调用 Flat() 不受影响（= 默认序）。
                var ordRev = new[] { MenuOrderSpec.Always, MenuOrderSpec.Process, MenuOrderSpec.Dynamic };
                var flat9b = d9.Flat(ordRev);
                ok(flat9b.Count == 3
                    && flat9b[0].Type == GroupType.Always
                    && flat9b[1].Type == GroupType.Proc
                    && flat9b[2].Type == GroupType.Text,
                    "R9b 顺序可配置：固定 → 进程 → 动态，实际=" + Names(flat9b));

                // 兜底与动态**同格**（I-1）：命中兜底时它就是 dynamic 段那一个组，不单列一格
                var d9c = MatchEngine.Decide(Ctx(proc: "chrome.exe"), cfg);   // 兜底 + 进程
                var flatFb = d9c.Flat(new[] { MenuOrderSpec.Dynamic, MenuOrderSpec.Process, MenuOrderSpec.Always });
                ok(d9c.FallbackShown && flatFb.Count == 3
                    && flatFb[0].Type == GroupType.Fallback
                    && flatFb[1].Type == GroupType.Proc
                    && flatFb[2].Type == GroupType.Always,
                    "R9b 兜底占 dynamic 那一格（不单列），实际=" + Names(flatFb));

                // 兜底随 dynamic 段一起移动（排到末段 ⇒ 整体落底）
                var flatFb2 = d9c.Flat(new[] { MenuOrderSpec.Process, MenuOrderSpec.Always, MenuOrderSpec.Dynamic });
                ok(flatFb2.Count == 3
                    && flatFb2[0].Type == GroupType.Proc
                    && flatFb2[1].Type == GroupType.Always
                    && flatFb2[2].Type == GroupType.Fallback,
                    "R9b 兜底随 dynamic 段移动（动态排末 ⇒ 兜底落底），实际=" + Names(flatFb2));

                // 未配置 ⇒ 与旧行为逐项一致；写错 / 旧四段词 ⇒ 归一成三段（fallback 并入 dynamic）
                ok(Names(d9.Flat(MenuOrderSpec.Parse(""))) == Names(d9.Flat()),
                    "R9b 未配置 ⇒ 默认序（与旧行为逐项一致）");
                var ordBad = MenuOrderSpec.Parse("always, 乱写, always, dynamic, fallback");
                ok(ordBad.Length == 3
                    && ordBad[0] == MenuOrderSpec.Always && ordBad[1] == MenuOrderSpec.Dynamic && ordBad[2] == MenuOrderSpec.Process,
                    "R9b Parse 容错：重复/未知词剔除、fallback 并入 dynamic、缺段补末尾，实际=" + string.Join(",", ordBad));

                // ---------------- I-1 动态槽位互斥 ----------------
                bool i1ok = true;
                foreach (var ctx in new[]
                {
                    Ctx(text: "t"), Ctx(files: 1, folders: 1), Ctx(exts: new[] { "pdf" }, files: 1),
                    Ctx(folders: 1), new MatchContext()
                })
                {
                    var dd = MatchEngine.Decide(ctx, cfg);
                    if (dd.DynamicCount > 1) i1ok = false;
                }
                ok(i1ok, "I-1 动态槽位互斥：任一形态下动态组数 ≤ 1（ext 组与兜底不并存）");

                // ---------------- I-2 兜底只看动态槽位 ----------------
                var i2 = MatchEngine.Decide(Ctx(proc: "chrome.exe"), cfg);
                ok(i2.FallbackShown && i2.Dynamic[0].Name == "兜底" && i2.ProcessCount == 1,
                    "I-2 兜底判定只看动态槽位：进程命中不与兜底互斥（两者并存）");

                // ---------------- I-3 预设单例违规（数据位） ----------------
                var dupText = new List<MenuGroup>(cfg);
                dupText.Add(G("文本2", GroupType.Text));
                var i3 = MatchEngine.Decide(Ctx(text: "hi"), dupText);
                bool hasSingleton = false;
                foreach (var v in i3.Violations)
                    if (v.Kind == ViolationKind.DuplicateSingleton) hasSingleton = true;
                ok(hasSingleton && i3.Dynamic[0].Name == "文本",
                    "I-3/I-4 预设槽位多组 ⇒ 取配置顺序第一个 + 记录 DuplicateSingleton（文案段 2 做）");

                // ---------------- 缺件注入位（判定层零 IO） ----------------
                var missCfg = MissingConfig();
                var mctx = Ctx(text: "t");
                // 兜底组里两条：下标 1（第 2 条）判缺件
                mctx.MissingItemIndex = new HashSet<int>();
                mctx.MissingItemIndex.Add(1);
                var dmiss = MatchEngine.Decide(mctx, missCfg);
                ok(dmiss.DynamicCount == 1 && dmiss.Dynamic[0].Items.Count == 1
                    && dmiss.Dynamic[0].Items[0].Title == "保留项",
                    "缺件注入位：被判缺件的条目从菜单里消失（判定层不查盘，只读下标）");

                // 子菜单标题剪枝：子项全缺 ⇒ 标题也不显示
                var hdrCtx = Ctx(text: "t");
                hdrCtx.MissingItemIndex = new HashSet<int>();
                hdrCtx.MissingItemIndex.Add(1);   // 子项
                var dhdr = MatchEngine.Decide(hdrCtx, HeaderConfig());
                ok(dhdr.DynamicCount == 0,
                    "缺件注入位：子项全缺 ⇒ 子菜单标题一并剪掉（整组无可见内容 ⇒ 不显示，不留点了没反应的空壳）");

                // ---------------- AC-4 同名项共存 ----------------
                sb.AppendLine();
                sb.AppendLine("--- AC-4 同名项共存（ID 与显示文本分离）---");
                var gA = new MenuGroup { Name = "终端组", Type = GroupType.Text };
                // ★ Level=1：与真实配置一致（menus.conf 的条目缩进 1 级 = 顶级叶条目；
                //   MenuBuilder 的层级栈按 Level 弹出，Level=0 的叶条目会弹空栈 ⇒ 越界）
                gA.Items.Add(It("打开 PowerShell", ActionType.Run, "pwsh.exe", 1));
                gA.Items.Add(It("打开 PowerShell", ActionType.Run, "powershell.exe", 1));
                var gB = new MenuGroup { Name = "浏览器组", Type = GroupType.Folder };
                gB.Items.Add(It("打开 PowerShell", ActionType.Run, "wt.exe", 1));
                gB.Items.Add(It("打开 PowerShell", ActionType.Run, "cmd.exe", 1));
                var built = MenuBuilder.Build(new List<MenuGroup> { gA, gB });
                try
                {
                    // ① ID 全局唯一
                    var idSet = new HashSet<int>();
                    int dup = 0;
                    foreach (var kv in built.IdMap) if (!idSet.Add(kv.Key)) dup++;
                    ok(dup == 0 && built.IdMap.Count == built.ItemCount && built.IdMap.Count == 4,
                        "同组 + 跨组同名：命令 ID 全局唯一（idmap=" + built.IdMap.Count
                        + " 可点条目=" + built.ItemCount + " 重复=" + dup + "）");

                    // ② 点击分发按**原始引用**命中正确条目（禁止按标题回查）
                    var dispatched = new List<string>();
                    foreach (var kv in built.IdMap)
                    {
                        MenuItem hit = kv.Value;          // ← 这里是 ID → 条目引用，不是按标题查
                        dispatched.Add(hit.Title + "|" + hit.Target);
                    }
                    var targets = new HashSet<string>();
                    foreach (var s in dispatched) targets.Add(s.Substring(s.IndexOf('|') + 1));
                    bool allSameTitle = true;
                    foreach (var s in dispatched)
                        if (s.Substring(0, s.IndexOf('|')) != "打开 PowerShell") allSameTitle = false;
                    ok(dispatched.Count == 4 && allSameTitle
                        && targets.Contains("pwsh.exe") && targets.Contains("powershell.exe")
                        && targets.Contains("wt.exe") && targets.Contains("cmd.exe"),
                        "跨组同名的 4 条都点得对（ID→引用→Target，四个目标各不相同）");
                }
                finally
                {
                    built.Menu.Dispose();
                }

                // ---------------- 选中项分类（R5 的数据前置，IO 在分类器） ----------------
                sb.AppendLine();
                sb.AppendLine("--- 选中项分类（SelectionClassifier，IO 允许层）---");
                string tmp = Path.Combine(Path.GetTempPath(), "rve_match_selftest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    Directory.CreateDirectory(tmp);
                    string f1 = Path.Combine(tmp, "a.pdf");
                    File.WriteAllText(f1, "x", new UTF8Encoding(false));

                    int fc, dc;
                    var kindMixed = SelectionClassifier.Classify(new List<string> { f1, tmp }, out fc, out dc);
                    ok(kindMixed == SelectionKind.Mixed && fc == 1 && dc == 1,
                        "分类器：1 文件 + 1 文件夹 ⇒ Mixed（fileCount=1 folderCount=1）");

                    int fc2, dc2;
                    var kindFile = SelectionClassifier.Classify(new List<string> { f1 }, out fc2, out dc2);
                    ok(kindFile == SelectionKind.File && fc2 == 1 && dc2 == 0, "分类器：仅文件 ⇒ File");

                    int fc3, dc3;
                    var kindFolder = SelectionClassifier.Classify(new List<string> { tmp }, out fc3, out dc3);
                    ok(kindFolder == SelectionKind.Folder && fc3 == 0 && dc3 == 1, "分类器：仅文件夹 ⇒ Folder");

                    var exts = SelectionClassifier.ExtensionsLower(new List<string> { f1, tmp });
                    ok(exts.Count == 1 && exts[0] == "pdf",
                        "分类器：扩展名只从文件取（文件夹不贡献），实际=" + exts.Count);

                    // 经分类器 → MatchContext → 判定（端到端一条真路径用例）
                    var realCtx = SelectionClassifier.Build("", new List<string> { f1, tmp }, "", "", "");
                    var dReal = MatchEngine.Decide(realCtx, cfg);
                    ok(dReal.Rule == "R5" && dReal.Dynamic[0].Name == "兜底",
                        "端到端：真路径混合选择 ⇒ 兜底（R5）");
                }
                finally
                {
                    try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("  FAIL 异常: " + ex.Message + "\n" + ex.StackTrace);
            }

            sb.AppendLine();
            sb.AppendLine("结果：PASS=" + pass + " FAIL=" + fail);
            sb.AppendLine(fail == 0 ? "全部通过" : "有失败");
            try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false)); } catch { }
            return fail == 0 ? 0 : 1;
        }

        // ============================================================
        // 夹具
        // ============================================================
        /// <summary>标准配置：四预设 + 两个 ext 组 + 两个 proc 组。</summary>
        private static List<MenuGroup> StandardConfig()
        {
            var r = new List<MenuGroup>();
            var text = G("文本", GroupType.Text);
            text.Items.Add(It("翻译选中文本", ActionType.Url, "https://x/%s"));
            r.Add(text);

            var folder = G("文件夹", GroupType.Folder);
            folder.Items.Add(It("在终端打开", ActionType.Run, "wt.exe"));
            r.Add(folder);

            r.Add(G("PDF", GroupType.Ext, "pdf"));
            r.Add(G("TXT", GroupType.Ext, "txt"));

            var fb = G("兜底", GroupType.Fallback);
            fb.Items.Add(It("复制路径", ActionType.Keys, "^c"));
            r.Add(fb);

            r.Add(G("浏览器", GroupType.Proc, "chrome.exe"));
            r.Add(G("编辑器", GroupType.Proc, "code.exe"));

            var al = G("固定", GroupType.Always);
            al.Items.Add(It("设置", ActionType.Run, "RunEverCs.exe --settings"));
            r.Add(al);
            return r;
        }

        /// <summary>兜底组含两条（第 2 条用于缺件注入断言）。</summary>
        private static List<MenuGroup> MissingConfig()
        {
            var fb = G("兜底", GroupType.Fallback);
            fb.Items.Add(It("保留项", ActionType.Keys, "^c"));
            fb.Items.Add(It("缺件项", ActionType.Run, "not-exist-xyz.exe"));
            return new List<MenuGroup> { fb };
        }

        /// <summary>兜底组含一个子菜单标题 + 一个子项（用于剪枝断言）。</summary>
        private static List<MenuGroup> HeaderConfig()
        {
            var fb = G("兜底", GroupType.Fallback);
            fb.Items.Add(It("子菜单", ActionType.Group, "", 0));
            fb.Items.Add(It("子项", ActionType.Run, "not-exist-xyz.exe", 1));
            return new List<MenuGroup> { fb };
        }

        /// <summary>只有一个 ext 组（扩展名 docx）的配置 —— U-A 一键直达用例专用。</summary>
        private static List<MenuGroup> ExtOnlyConfig(IEnumerable<MenuItem> items)
        {
            var ext = G("DOCX", GroupType.Ext, "docx");
            foreach (var it in items) ext.Items.Add(it);
            return new List<MenuGroup> { ext };
        }

        private static MenuGroup G(string name, GroupType t, params string[] kw)
        {
            var g = new MenuGroup { Name = name, Type = t };
            foreach (var k in kw) g.Keywords.Add(k);
            return g;
        }

        private static MenuItem It(string title, ActionType t, string target, int level = 0)
        {
            return new MenuItem { Title = title, Type = t, Target = target, Level = level };
        }

        /// <summary>构造判定上下文（只给值，不给路径 —— 判定层不碰盘）。</summary>
        private static MatchContext Ctx(string text = "", int files = 0, int folders = 0,
            string[] exts = null, string proc = "")
        {
            var c = new MatchContext
            {
                Text = text ?? "",
                FileCount = files,
                FolderCount = folders,
                ProcessName = proc ?? "",
                Kind = SelectionClassifier.Shape(files, folders)
            };
            if (exts != null)
                foreach (var e in exts) c.ExtensionsLower.Add(e);
            return c;
        }

        private static string Names(List<MenuGroup> gs)
        {
            var l = new List<string>();
            foreach (var g in gs) l.Add(g.Name);
            return string.Join("→", l.ToArray());
        }
    }
}
