using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Native;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 卡 E（2026-11-17b）自检：配置层两条修饰位。
    ///
    /// 用法：RunEverCs.exe --selftest-conf-flags &lt;root&gt; &lt;out.txt&gt;
    ///   ★ 与其它自检同口径：**禁带 --root**；全部夹具落在临时 root 下（不碰真实配置），
    ///     跑完自清（`%TEMP%\runever-cf-*` 残量 = 0）。
    ///
    /// 覆盖（判据逐条）：
    ///   ① `off` 解析：带 off ⇒ Off=true；不带 ⇒ false；两种写法（修饰位 / 行尾）都认；
    ///      `send  turn off` 的 off 是**内容**，不许被吃掉（旧配置零变化）。
    ///   ② `off` 渲染：条目**在菜单里**、状态带 MF_GRAYED、**不在 IdMap**、不计入可点条目。
    ///   ③ `off` 写回：窄写回逐字节不变（含 off）；改了标题只重写那一行且 off 仍在；
    ///      把 off 关掉 ⇒ 这一行真的被重写（不留残影）。
    ///   ④ `class:` 解析：`class:类名` 识别为类名槽位，普通关键词仍是进程名槽位。
    ///   ⑤ `class:` 匹配：前台类名 = CabinetWClass 命中；类名 = Notepad 不命中。
    ///   ⑥ 混写组：`class:X bar.exe` 两条路径（类名 / 进程名）都能命中。
    ///   ⑦ 串起来能用：`SelectionClassifier` 注入类名 → `MatchEngine.Decide` 命中 proc 组。
    ///   ⑧ 与既有能力不互踩：缺件过滤（hideMissing）克隆后 `off` 仍在；
    ///      `off` 单条目不触发"扩展名一键直达"（否则禁用条目会被直接执行）。
    /// </summary>
    public static class ConfFlagsSelfTest
    {
        private static StringBuilder _sb;
        private static int _fail;
        private static int _pass;
        private static string _tmpRoot;

        public static int Run(string[] args)
        {
            if (args == null || args.Length < 3) return 2;
            string root = args[1];
            string outFile = args[2];
            if (!Path.IsPathRooted(outFile)) outFile = Path.Combine(root, outFile);

            _sb = new StringBuilder();
            _fail = 0;
            _pass = 0;
            _sb.AppendLine("=== 卡 E 自检（off 条目禁用 / class: 窗口类分组）===");
            _sb.AppendLine("  时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            _sb.AppendLine("  夹具: 临时目录（%TEMP%\\runever-cf-*），不碰真实配置");
            _sb.AppendLine();

            _tmpRoot = Path.Combine(Path.GetTempPath(), "runever-cf-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(_tmpRoot);

                string conf = WriteFixtureConf();
                MenuItem off = null, slotOff = null, plain = null, content = null, untouched = null;
                ParseCases(conf, out off, out slotOff, out plain, out content, out untouched);
                RenderCases(conf);
                WriteBackCases(conf);
                ClassKeywordCases();
                MatchCases();
                MixedCases();
                IntegrationCases();
                NoInterferenceCases(conf);
            }
            catch (Exception ex)
            {
                Fail("自检异常", ex.GetType().Name + ": " + ex.Message + " @" + FirstStackLine(ex));
            }
            finally
            {
                try { if (Directory.Exists(_tmpRoot)) Directory.Delete(_tmpRoot, true); } catch { }
            }

            _sb.AppendLine();
            _sb.AppendLine(_fail == 0
                ? "结果: 全部通过 ✓（" + _pass + " 项）"
                : "结果: 失败 " + _fail + " 项 ✗（通过 " + _pass + "）");

            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outFile, _sb.ToString(), new UTF8Encoding(false));
            }
            catch { }

            return _fail == 0 ? 0 : 1;
        }

        // ============================================================
        // 夹具：一份含 off 两种写法 + 内容里的 off + 注释 + 分隔线 的配置
        // ============================================================
        private static string WriteFixtureConf()
        {
            string dir = Path.Combine(_tmpRoot, "fixture", "config");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "menus.conf");

            var sb = new StringBuilder();
            sb.AppendLine("# 卡 E 夹具（注释必须原样保留）");
            sb.AppendLine("; 分号注释也一样");
            sb.AppendLine();
            sb.AppendLine("! 卡E  always");
            sb.AppendLine("  保留项  _K  | run  \"keep.exe\"");
            sb.AppendLine("  禁用项  _O  | open  \"D:\\proj app\"  off");
            sb.AppendLine("  槽位禁用  _S  | run off  \"s.exe\"");
            sb.AppendLine("  内容off  _C  | send  turn off");
            sb.AppendLine("  ---");
            sb.AppendLine("  无修饰  _N  | open  \"D:\\proj\"");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        // ============================================================
        // ① `off` 解析
        // ============================================================
        private static void ParseCases(string conf, out MenuItem off, out MenuItem slotOff,
            out MenuItem plain, out MenuItem content, out MenuItem untouched)
        {
            var p = new ConfParser(conf);
            p.Parse();
            var items = p.Groups.Count > 0 ? p.Groups[0].Items : new List<MenuItem>();

            off = Find(items, "禁用项");
            slotOff = Find(items, "槽位禁用");
            plain = Find(items, "保留项");
            content = Find(items, "内容off");
            untouched = Find(items, "无修饰");

            bool ok1 = plain != null && !plain.Off && plain.Target == "\"keep.exe\"";
            Check(ok1, "①-a 不带 off ⇒ Off=false（旧配置零变化）",
                "保留项 Off=" + (plain != null && plain.Off)
                + " 目标=[" + (plain == null ? "" : plain.Target) + "]（期望 false / \"keep.exe\"）");

            bool ok2 = off != null && off.Off && off.Target == "\"D:\\proj app\"";
            Check(ok2, "①-b 行尾写法 `open \"D:\\proj app\" off` ⇒ Off=true 且目标不含 off",
                "禁用项 Off=" + (off != null && off.Off)
                + " 目标=[" + (off == null ? "" : off.Target) + "]（期望 true / \"D:\\proj app\"）");

            bool ok3 = slotOff != null && slotOff.Off && slotOff.Target == "\"s.exe\"";
            Check(ok3, "①-c 修饰位写法 `run off \"s.exe\"` ⇒ Off=true 且目标完好",
                "槽位禁用 Off=" + (slotOff != null && slotOff.Off)
                + " 目标=[" + (slotOff == null ? "" : slotOff.Target) + "]（期望 true / \"s.exe\"）");

            bool ok4 = content != null && !content.Off && content.Target == "turn off";
            Check(ok4, "①-d send 的目标 `turn off` 不被吃成修饰位（内容 ≠ 修饰位）",
                "内容off Off=" + (content != null && content.Off)
                + " 目标=[" + (content == null ? "" : content.Target) + "]（期望 false / turn off）");

            bool ok5 = untouched != null && !untouched.Off;
            Check(ok5, "①-e 普通 open 目录条目不因新语法改变",
                "无修饰 Off=" + (untouched != null && untouched.Off)
                + " Sub=" + (untouched != null && untouched.Sub));

            // 克隆（缺件隐藏 / 树形编辑都走这条）必须带上 off —— 旁挂状态会在这里丢
            MenuItem clone = off != null ? off.Clone() : null;
            Check(clone != null && clone.Off && clone.Target == off.Target,
                "①-f Clone() 保留 Off（缺件隐藏 / 编辑器克隆路径）",
                "克隆 Off=" + (clone != null && clone.Off));
        }

        // ============================================================
        // ② `off` 渲染：在菜单里 + 灰显 + 不在 IdMap
        // ============================================================
        private static void RenderCases(string conf)
        {
            var p = new ConfParser(conf);
            p.Parse();
            var built = MenuBuilder.Build(p.Groups, null);
            try
            {
                int idxOff = FindRow(built.Menu, "禁用项");
                int idxPlain = FindRow(built.Menu, "保留项");
                uint stOff = idxOff >= 0
                    ? Win32.GetMenuState(built.Menu.Handle, (uint)idxOff, Win32.MF_BYPOSITION) : 0;
                bool grayed = (stOff & Win32.MF_GRAYED) != 0;
                int idOff = idxOff >= 0 ? built.Menu.GetItemIdAt(idxOff) : -1;
                int idPlain = idxPlain >= 0 ? built.Menu.GetItemIdAt(idxPlain) : -1;

                bool present = idxOff >= 0;
                bool notInMap = idOff > 0 && !built.IdMap.ContainsKey(idOff);
                bool plainInMap = idPlain > 0 && built.IdMap.ContainsKey(idPlain);

                Check(present && grayed && notInMap && plainInMap,
                    "② 渲染：off 条目在菜单里 + 灰显 + 不进 IdMap（可点条目照旧进）",
                    "存在=" + present + " 文本=[" + (idxOff >= 0 ? built.Menu.GetItemText(idxOff) : "") + "]"
                    + " 状态=0x" + stOff.ToString("X") + "（MF_GRAYED=" + grayed + "）"
                    + " id=" + idOff + " 在IdMap=" + (idOff > 0 && built.IdMap.ContainsKey(idOff))
                    + " | 保留项 id=" + idPlain + " 在IdMap=" + plainInMap);

                Check(built.ItemCount == 3 && built.GrayedCount == 2 && built.IdMap.Count == 3,
                    "②-b 计数口径：可点=3 / 灰显=2 / IdMap=3（互不串味）",
                    "ItemCount=" + built.ItemCount + " GrayedCount=" + built.GrayedCount
                    + " IdMap=" + built.IdMap.Count + "（期望 3/2/3）");

                // 灰显条目也不能被点开执行：IdMap 值里绝不能出现它们
                bool noOffInMap = true;
                foreach (var kv in built.IdMap)
                    if (kv.Value != null && kv.Value.Off) noOffInMap = false;
                Check(noOffInMap, "②-c IdMap 里没有任何 off 条目（点了不执行）",
                    "IdMap 含 off 条目=" + (!noOffInMap));
            }
            finally
            {
                built.Menu.Dispose();
            }
        }

        // ============================================================
        // ③ `off` 窄写回：原样保留 + 改了才重写 + 关掉真的落盘
        // ============================================================
        private static void WriteBackCases(string conf)
        {
            string text0 = File.ReadAllText(conf, Encoding.UTF8);
            var p0 = new ConfParser(conf);
            p0.Parse();
            var opaque0 = MenuConfWriter.OpaqueLines(text0, p0.Groups);

            var w = new MenuConfWriter(conf);
            string msg;
            bool saved = w.Save(p0.Groups, out msg);
            string text1 = File.ReadAllText(conf, Encoding.UTF8);

            var p1 = new ConfParser(conf);
            p1.Parse();
            var again = Find(p1.Groups.Count > 0 ? p1.Groups[0].Items : new List<MenuItem>(), "禁用项");
            var opaque1 = MenuConfWriter.OpaqueLines(text1, p1.Groups);

            Check(saved && again != null && again.Off && again.Target == "\"D:\\proj app\"",
                "③-a 原样保存：off 仍在、目标不变",
                "保存=" + saved + " 回读 Off=" + (again != null && again.Off)
                + " 目标=[" + (again == null ? "" : again.Target) + "]"
                + (saved ? "" : " 原因: " + msg));

            Check(string.Equals(text0, text1, StringComparison.Ordinal),
                "③-b 什么都没改 ⇒ 窄写回逐字节不变（BOM / 行尾 / 注释 / off 全保留）",
                "原 " + text0.Length + "B / 新 " + text1.Length + "B / 相同="
                + string.Equals(text0, text1, StringComparison.Ordinal));

            Check(SameSeq(opaque0, opaque1),
                "③-c 非条目行（注释 / 空行）逐字节不变",
                "原 " + opaque0.Count + " 行 / 新 " + opaque1.Count + " 行");

            // ---- 改标题：只重写这一行，off 那一行照抄 ----
            var items = p0.Groups[0].Items;
            var keep = Find(items, "保留项");
            if (keep != null) keep.Title = "保留项改";
            w.Save(p0.Groups, out msg);
            string text2 = File.ReadAllText(conf, Encoding.UTF8);
            var p2 = new ConfParser(conf);
            p2.Parse();
            var off2 = Find(p2.Groups.Count > 0 ? p2.Groups[0].Items : new List<MenuItem>(), "禁用项");
            var renamed = Find(p2.Groups.Count > 0 ? p2.Groups[0].Items : new List<MenuItem>(), "保留项改");
            Check(renamed != null && off2 != null && off2.Off
                && text2.IndexOf("保留项改", StringComparison.Ordinal) >= 0,
                "③-d 改标题：新标题落盘，off 行照抄（off 仍在）",
                "新标题=" + (renamed != null) + " 禁用项 Off=" + (off2 != null && off2.Off));

            // ---- 关掉 off：这一行必须真的被重写（否则"改了没落盘"）----
            var offIt = Find(p0.Groups[0].Items, "禁用项");
            if (offIt != null) offIt.Off = false;
            w.Save(p0.Groups, out msg);
            var p3 = new ConfParser(conf);
            p3.Parse();
            var off3 = Find(p3.Groups.Count > 0 ? p3.Groups[0].Items : new List<MenuItem>(), "禁用项");
            string line3 = FindRawLine(File.ReadAllText(conf, Encoding.UTF8), "禁用项");
            Check(off3 != null && !off3.Off && line3.IndexOf(" off", StringComparison.OrdinalIgnoreCase) < 0,
                "③-e 关掉 off ⇒ 该行被重写、文件里不再有 off（不照抄残影）",
                "回读 Off=" + (off3 != null && off3.Off) + " 行=[" + line3.Trim() + "]");

            // 复原（后续用例还要用 off 语义）
            if (offIt != null) offIt.Off = true;
            w.Save(p0.Groups, out msg);
            var p4 = new ConfParser(conf);
            p4.Parse();
            var off4 = Find(p4.Groups.Count > 0 ? p4.Groups[0].Items : new List<MenuItem>(), "禁用项");
            Check(off4 != null && off4.Off, "③-f 再打开 off ⇒ 文件行重新带上 off", "回读 Off=" + (off4 != null && off4.Off));
        }

        // ============================================================
        // ④ `class:` 解析（关键词槽位识别）
        // ============================================================
        private static void ClassKeywordCases()
        {
            string dir = Path.Combine(_tmpRoot, "classkw", "config");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "menus.conf");
            File.WriteAllText(path,
                "! 资源管理器  proc  class:CabinetWClass  explorer.exe" + Environment.NewLine
                + "  打开当前目录  _W  | run  explorer.exe" + Environment.NewLine,
                new UTF8Encoding(false));

            var p = new ConfParser(path);
            p.Parse();
            var g = p.Groups.Count > 0 ? p.Groups[0] : null;
            string kw0 = (g != null && g.Keywords.Count > 0) ? g.Keywords[0] : "";
            string kw1 = (g != null && g.Keywords.Count > 1) ? g.Keywords[1] : "";

            bool ok = MatchEngine.KeywordKind(kw0) == MatchEngine.ProcKeywordKind.WindowClass
                   && string.Equals(MatchEngine.WindowClassOf(kw0), "CabinetWClass", StringComparison.OrdinalIgnoreCase)
                   && MatchEngine.KeywordKind(kw1) == MatchEngine.ProcKeywordKind.Process
                   && MatchEngine.WindowClassOf(kw1) == "";
            Check(ok, "④ class: 解析：class:CabinetWClass ⇒ 类名槽位；explorer.exe ⇒ 进程名槽位",
                "kw0=[" + kw0 + "]→" + MatchEngine.KeywordKind(kw0)
                + " 类名=[" + MatchEngine.WindowClassOf(kw0) + "]"
                + " | kw1=[" + kw1 + "]→" + MatchEngine.KeywordKind(kw1));

            // 大小写不敏感的前缀
            bool okCase = MatchEngine.IsWindowClassKeyword("CLASS:Notepad")
                       && MatchEngine.IsWindowClassKeyword("Class:Notepad");
            Check(okCase, "④-b 前缀大小写不敏感（CLASS: / Class: 都算类名槽位）",
                "CLASS:=" + MatchEngine.IsWindowClassKeyword("CLASS:Notepad")
                + " Class:=" + MatchEngine.IsWindowClassKeyword("Class:Notepad"));
        }

        // ============================================================
        // ⑤ `class:` 匹配
        // ============================================================
        private static void MatchCases()
        {
            var g = MakeProcGroup("资源管理器", new[] { "class:CabinetWClass" });

            int hitClass = MatchEngine.MatchProcGroups(new List<MenuGroup> { g }, "other.exe", "CabinetWClass").Count;
            int missClass = MatchEngine.MatchProcGroups(new List<MenuGroup> { g }, "other.exe", "Notepad").Count;
            int noClass = MatchEngine.MatchProcGroups(new List<MenuGroup> { g }, "other.exe", "").Count;

            Check(hitClass == 1 && missClass == 0 && noClass == 0,
                "⑤ 类名匹配：CabinetWClass 命中 / Notepad 不命中 / 没拿到类名也不命中（不回退成进程名）",
                "命中=" + hitClass + "（期望 1） Notepad=" + missClass
                + "（期望 0） 无类名=" + noClass + "（期望 0）");
        }

        // ============================================================
        // ⑥ 混写组（class:X 与普通关键词并存 = 或）
        // ============================================================
        private static void MixedCases()
        {
            var g = MakeProcGroup("混写", new[] { "class:CabinetWClass", "bar.exe" });
            var list = new List<MenuGroup> { g };

            int byProc = MatchEngine.MatchProcGroups(list, "bar.exe", "Notepad").Count;
            int byClass = MatchEngine.MatchProcGroups(list, "nothing.exe", "CabinetWClass").Count;
            int byBoth = MatchEngine.MatchProcGroups(list, "bar.exe", "CabinetWClass").Count;
            int neither = MatchEngine.MatchProcGroups(list, "nothing.exe", "Notepad").Count;

            Check(byProc == 1 && byClass == 1 && byBoth == 1 && neither == 0,
                "⑥ 混写组 = 或：进程名路径 / 类名路径各自命中，都不沾才不命中",
                "按进程=" + byProc + " 按类名=" + byClass + " 都命中=" + byBoth
                + " 都不沾=" + neither + "（期望 1/1/1/0）");

            // 旧调用方（2 参重载）口径不变：只按进程名
            int legacyProc = MatchEngine.MatchProcGroups(list, "bar.exe").Count;
            int legacyClassOnly = MatchEngine.MatchProcGroups(list, "nothing.exe").Count;
            Check(legacyProc == 1 && legacyClassOnly == 0,
                "⑥-b 2 参重载（旧调用方）仍只按进程名，逐字不变",
                "bar.exe=" + legacyProc + " nothing.exe=" + legacyClassOnly + "（期望 1/0）");
        }

        // ============================================================
        // ⑦ 串起来：注入类名 → 判定命中 proc 组
        // ============================================================
        private static void IntegrationCases()
        {
            // 采集结果 → 判定上下文（类名必须原样搬过来）
            var captured = new Context { Proc = "explorer.exe", WinClass = "CabinetWClass" };
            var ctx = SelectionClassifier.FromCaptured(captured);
            Check(ctx.WinClass == "CabinetWClass" && ctx.ProcessName == "explorer.exe",
                "⑦-a 采集结果注入：Context.WinClass → MatchContext.WinClass",
                "WinClass=[" + ctx.WinClass + "] 进程=[" + ctx.ProcessName + "]");

            var g = MakeProcGroup("资源管理器", new[] { "class:CabinetWClass" });
            var groups = new List<MenuGroup> { g };

            ctx.FileCount = 0;
            ctx.FolderCount = 0;
            ctx.Text = "";
            var dHit = MatchEngine.Decide(ctx, groups);
            bool hit = dHit.Process != null && dHit.Process.Count == 1 && dHit.Process[0] == g;

            var ctxNo = new MatchContext { ProcessName = "explorer.exe", WinClass = "Notepad" };
            var dMiss = MatchEngine.Decide(ctxNo, groups);
            bool miss = dMiss.Process == null || dMiss.Process.Count == 0;

            Check(hit && miss, "⑦-b Decide(R7)：类名命中 ⇒ 该组进进程槽位；类名不符 ⇒ 不进",
                "命中=" + hit + " 不命中=" + miss);
        }

        // ============================================================
        // ⑧ 与既有能力不互踩
        // ============================================================
        private static void NoInterferenceCases(string conf)
        {
            // ---- 缺件隐藏（克隆 + 剪枝）之后 off 仍在 ----
            string realFile = Path.Combine(_tmpRoot, "real-target.txt");
            File.WriteAllText(realFile, "x", new UTF8Encoding(false));

            string dir = Path.Combine(_tmpRoot, "hide", "config");
            Directory.CreateDirectory(dir);
            string hideConf = Path.Combine(dir, "menus.conf");
            File.WriteAllText(hideConf,
                "! 缺件  always" + Environment.NewLine
                + "  存在但禁用  _H  | open  \"" + realFile + "\"  off" + Environment.NewLine,
                new UTF8Encoding(false));

            var p = new ConfParser(hideConf);
            p.Parse();
            var cache = new PathCache(Path.Combine(dir, "..", "cache", "paths.txt"));
            var filtered = MissingFilter.Apply(p.Groups, cache, true, null);
            MenuItem survivor = null;
            if (filtered.Groups.Count > 0 && filtered.Groups[0].Items.Count > 0)
                survivor = filtered.Groups[0].Items[0];

            Check(survivor != null && survivor.Off,
                "⑧-a 缺件过滤（克隆路径）后 off 仍在：'看不到' 与 '点不动' 互不影响",
                "存活=" + (survivor != null) + " Off=" + (survivor != null && survivor.Off));

            // ---- off 单条目不得触发扩展名一键直达 ----
            var ext = new MenuGroup { Name = "txt", Type = GroupType.Ext };
            ext.Keywords.Add("txt");
            ext.Items.Add(new MenuItem { Title = "禁用编辑器", Type = ActionType.Run, Target = "e.exe", Off = true });

            var ctx = new MatchContext
            {
                Kind = SelectionKind.File,
                FileCount = 1,
                ExtensionsLower = new List<string> { "txt" }
            };
            var d = MatchEngine.Decide(ctx, new List<MenuGroup> { ext });
            bool noOneShot = d.OneShot == null;
            Check(noOneShot, "⑧-b off 单条目不触发一键直达（否则禁用条目会被绕过菜单直接执行）",
                "Rule=" + d.Rule + " OneShot=" + (d.OneShot == null ? "<null>" : d.OneShot.Title)
                + " 理由=" + d.OneShotReason);

            // 对照：同样的组、条目可用 ⇒ 直达照旧存在（没把能力改死）
            ext.Items[0].Off = false;
            var d2 = MatchEngine.Decide(ctx, new List<MenuGroup> { ext });
            Check(d2.OneShot != null, "⑧-c 对照：条目可用时一键直达仍然成立（能力未被改死）",
                "OneShot=" + (d2.OneShot == null ? "<null>" : d2.OneShot.Title));

            // ---- --check-conf 的计数口径不受影响（off 条目仍占一行）----
            string rep;
            int rc = ConfCheck.Evaluate(conf, out rep);
            Check(rc == 0, "⑧-d --check-conf 不受新修饰位影响（exit=0、无 fatal）",
                "exit=" + rc);
        }

        // ============================================================
        // 小工具
        // ============================================================
        private static MenuGroup MakeProcGroup(string name, string[] keywords)
        {
            var g = new MenuGroup { Name = name, Type = GroupType.Proc };
            foreach (string k in keywords) g.Keywords.Add(k);
            return g;
        }

        private static MenuItem Find(List<MenuItem> items, string title)
        {
            if (items == null) return null;
            foreach (var it in items)
                if (it != null && it.Title == title) return it;
            return null;
        }

        private static int FindRow(NativeMenu menu, string title)
        {
            if (menu == null) return -1;
            int n = menu.Count();
            for (int i = 0; i < n; i++)
                if ((menu.GetItemText(i) ?? "").IndexOf(title, StringComparison.Ordinal) >= 0) return i;
            return -1;
        }

        private static string FindRawLine(string text, string marker)
        {
            foreach (string raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
                if (raw.IndexOf(marker, StringComparison.Ordinal) >= 0) return raw;
            return "";
        }

        private static bool SameSeq(List<string> a, List<string> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static string FirstStackLine(Exception ex)
        {
            if (ex == null || string.IsNullOrEmpty(ex.StackTrace)) return "";
            foreach (string raw in ex.StackTrace.Replace("\r\n", "\n").Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length > 0) return l;
            }
            return "";
        }

        private static void Check(bool ok, string label, string detail)
        {
            if (ok) _pass++; else _fail++;
            _sb.AppendLine((ok ? "[OK]   " : "[FAIL] ") + label);
            if (!string.IsNullOrEmpty(detail)) _sb.AppendLine("       " + detail);
        }

        private static void Fail(string label, string detail)
        {
            _fail++;
            _sb.AppendLine("[FAIL] " + label);
            if (!string.IsNullOrEmpty(detail)) _sb.AppendLine("       " + detail);
        }
    }
}
