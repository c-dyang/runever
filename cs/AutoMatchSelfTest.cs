using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 自动执行（匹配块）链自检。
    /// 用法：RunEverCs.exe --selftest-automatch &lt;root&gt; &lt;out.txt&gt;
    ///
    /// 覆盖（对照 src\AutoMatcher.ahk + src\RunEver.ahk 定案语义）：
    ///   · 匹配式编译：正则 / 前缀糖 / whole 补锚 / 双重锚防护 / 正则转义
    ///   · 匹配：正则型命中与不命中、whole=false 允许尾部、Prio 降序、
    ///     同 Prio 文件名升序、enabled=0 恒不命中、minLen 边界、模板代入
    ///   · show 三态（-1 继承 / 0 隐藏 / 1 显示）
    ///   · 元数据缓存（cacheSeconds>0 命中缓存；0 = 每次重扫）
    ///   · 形态 B：PluginRunner 往返（AHK 驱动脚本 → Run(text) → 输出文件回传）+ 异常回落空串
    ///   · 实机 root：AHK `RunEver.ahk:411-416` 的四个用例口径
    /// </summary>
    public static class AutoMatchSelfTest
    {
        private static StringBuilder _sb;
        private static int _fail;

        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;      // 与 --selftest-capture 同口径
            string root = args[1];
            string outFile = args[2];
            if (!Path.IsPathRooted(outFile)) outFile = Path.Combine(root, outFile);

            _sb = new StringBuilder();
            _fail = 0;
            _sb.AppendLine("=== RunEver 自动执行（匹配块）自检 ===");
            _sb.AppendLine("root = " + root);

            // ---- 1. 编译/匹配单元用例（纯内存，无 IO）----
            UnitCases();

            // ---- 2. 沙箱夹具：端到端匹配 ----
            SandboxCases();

            // ---- 3. 元数据缓存 ----
            CacheCases();

            // ---- 4. 形态 B：PluginRunner ----
            RunnerCases();

            // ---- 5. 1.5 步早退 + 三层兜底（RunEverApp.TryAutoExec）----
            StepCases();

            // ---- 6. 实机 root（AHK 自检口径）----
            RealRootCases(root);

            _sb.AppendLine(_fail == 0
                ? "=== 自动执行自检全部通过 ==="
                : "=== 自动执行自检失败 " + _fail + " 项 ===");

            try
            {
                string dir = Path.GetDirectoryName(outFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(outFile, _sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return _fail == 0 ? 0 : 1;
        }

        // ============================================================
        // 断言辅助
        // ============================================================
        private static void Check(string title, bool ok, string detail)
        {
            if (ok) _sb.AppendLine("[OK]   " + title + (detail.Length == 0 ? "" : "  " + detail));
            else
            {
                _sb.AppendLine("[FAIL] " + title + "  " + detail);
                _fail++;
            }
        }

        private static void Eq(string title, string got, string want)
        {
            Check(title, got == want, got == want ? "" : "期望=[" + want + "] 实得=[" + got + "]");
        }

        // ============================================================
        // 1. 编译 / 匹配 单元用例
        //    ★ 前缀糖语义读 src\AutoMatcher.ahk:463-479 定案，禁猜
        // ============================================================
        private static void UnitCases()
        {
            _sb.AppendLine("--- 1. 匹配式编译 ---");

            // 正则 + whole=true：缺锚点才补
            Eq("Compile 正则补锚", AutoMatcher.Compile(@"SF\d{10,}", true), @"^SF\d{10,}$");
            // ★ 双重锚防护：原式已自带锚点 ⇒ 一个都不补
            Eq("Compile 双重锚防护",
                AutoMatcher.Compile(@"^SF\d{10,}$", true), @"^SF\d{10,}$");
            // whole=false：原样
            Eq("Compile whole=false 原样",
                AutoMatcher.Compile(@"^choco\s+install\b", false), @"^choco\s+install\b");

            // 前缀糖：以 * 结尾且无正则元字符
            Eq("Compile 前缀糖 whole=true", AutoMatcher.Compile("abc*", true), "^abc.*$");
            Eq("Compile 前缀糖 whole=false", AutoMatcher.Compile("abc*", false), "^abc");
            // 前缀糖要转义正则元字符（. 不是通配）
            Eq("Compile 前缀糖转义", AutoMatcher.Compile("a.b*", true), @"^a\.b.*$");
            // 含元字符 ⇒ 不做糖（按正则走）
            Eq("Compile 含元字符不做糖", AutoMatcher.Compile("a|b*", true), "^a|b*$");
            // 空串 ⇒ 不命中
            Eq("Compile 空串", AutoMatcher.Compile("   ", true), "");

            // whole 的语义差异：whole=true 加 $ ⇒ 尾部有内容即不命中
            string pat = @"^TAG\d+";
            Check("whole=true 尾部内容不命中",
                !Regex.IsMatch("TAG123xyz", AutoMatcher.Compile(pat, true)), "");
            Check("whole=false 尾部内容命中",
                Regex.IsMatch("TAG123xyz", AutoMatcher.Compile(pat, false)), "");

            _sb.AppendLine("--- 1b. 模板代入 ---");
            Eq("Fill {0}", AutoMatcher.Fill("https://x/{0}", "SF123"), "https://x/SF123");
            Eq("Fill 多占位", AutoMatcher.Fill("{0}|{0}", "ab"), "ab|ab");
        }

        // ============================================================
        // 沙箱夹具：临时 root（config\auto.conf + config\functions\*.ahk）
        //   为什么不用实机 root：实机插件集是用户资产，自检要能断言
        //   "Prio 高者胜 / 同 Prio 文件名小者胜 / enabled=0 不命中" 这类
        //   需要精确夹具的用例，不能在用户配置上碰运气。
        // ============================================================
        private static string MakeSandbox(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(),
                "runever-am-" + tag + "-" + DateTime.Now.Ticks);
            Directory.CreateDirectory(Path.Combine(dir, "config", "functions"));
            return dir;
        }

        private static void WriteConf(string root, bool enabled, int minLen, int cacheSec,
            int showByDefault)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 自检夹具 auto.conf");
            sb.AppendLine("enabled=" + (enabled ? "1" : "0"));
            sb.AppendLine("minLen=" + minLen);
            sb.AppendLine("cacheSeconds=" + cacheSec);
            sb.AppendLine("showByDefault=" + showByDefault);
            sb.AppendLine("waitWhenShown=1");
            File.WriteAllText(Path.Combine(root, "config", "auto.conf"),
                sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>写一个合法插件文件；show = -1 表示不写 RVE_FN_Show（继承）。</summary>
        private static void WritePlugin(string root, string file, string name, string match,
            bool whole, int prio, string outType, string tpl, int show, bool enabled)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#Requires AutoHotkey v2.0");
            sb.AppendLine("global RVE_FN_Name  := \"" + name + "\"");
            sb.AppendLine("global RVE_FN_Match := \"" + match + "\"");
            sb.AppendLine("global RVE_FN_Whole := " + (whole ? "true" : "false")
                + "          ; 行内注释：必须被剥掉（曾因注释使 whole 误判）");
            sb.AppendLine("global RVE_FN_Prio  := " + prio);
            sb.AppendLine("global RVE_FN_Out   := \"" + outType + "\"");
            if (tpl != null)
                sb.AppendLine("global RVE_FN_Tpl   := \"" + tpl + "\"");
            if (show >= 0)
                sb.AppendLine("global RVE_FN_Show  := " + (show == 1 ? "true" : "false"));
            if (!enabled)
                sb.AppendLine("global RVE_FN_Enabled := false");
            sb.AppendLine("; ===== 夹具插件（自检生成，不是形态 B）=====");
            File.WriteAllText(Path.Combine(root, "config", "functions", file),
                sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>写一份最小可用的 menus.conf（只为让 RunEverApp.Init 通过解析）。</summary>
        private static void WriteMenusConf(string root)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 自检夹具 menus.conf（本段只跑自动执行，不弹菜单）");
            sb.AppendLine("! 测试  always");
            sb.AppendLine("   占位条目  | action exit 0");
            File.WriteAllText(Path.Combine(root, "config", "menus.conf"),
                sb.ToString(), new UTF8Encoding(false));
        }

        private static void DeleteSandbox(string root)
        {
            try { Directory.Delete(root, true); } catch { }
        }

        // ============================================================
        // 2. 沙箱端到端：命中/不命中/Prio/同Prio/enabled/minLen/模板/show
        // ============================================================
        private static void SandboxCases()
        {
            _sb.AppendLine("--- 2. 沙箱匹配（夹具 root）---");
            string rootA = MakeSandbox("a");
            try
            {
                WriteConf(rootA, true, 6, 0, 1);
                WritePlugin(rootA, "a-sf.ahk", "A顺丰", @"^SF\d{10,}$", true, 100, "url",
                    "https://sf/{0}", -1, true);
                WritePlugin(rootA, "c-tie.ahk", "C同Prio", @"^SF\d{10,}$", true, 100, "url",
                    "https://c/{0}", -1, true);
                WritePlugin(rootA, "d-hi.ahk", "D高", @"^PRIO\d+$", true, 300, "url",
                    "https://hi/{0}", -1, true);
                WritePlugin(rootA, "e-lo.ahk", "E低", @"^PRIO\d+$", true, 100, "url",
                    "https://lo/{0}", -1, true);
                WritePlugin(rootA, "f-off.ahk", "F停用", @"^OFF\d+$", true, 500, "url",
                    "https://off/{0}", -1, false);
                WritePlugin(rootA, "g-prefix.ahk", "G前缀糖", "abc*", true, 100, "run",
                    "RUN {0}", -1, true);
                WritePlugin(rootA, "h-false.ahk", "H包含", @"^TAG\d+", false, 100, "url",
                    "https://w/{0}", -1, true);
                WritePlugin(rootA, "j-min.ahk", "JminLen", @"^MIN\d+$", true, 100, "url",
                    "https://min/{0}", -1, true);
                WritePlugin(rootA, "k-hide.ahk", "K显式隐藏", @"^HIDE\d+$", true, 100, "url",
                    "{0}", 0, true);
                WritePlugin(rootA, "b-choco.ahk", "Bchoco", @"^choco\s+(install|upgrade|uninstall)\b",
                    false, 200, "action", null, -1, true);

                var am = new AutoMatcher(rootA, s => _sb.AppendLine("  [am] " + s));
                int n = am.Load(true);
                Check("夹具加载插件数", n == 10, "实得=" + n);
                Check("夹具无非法插件", am.BadPlugins.Count == 0,
                    "实得=" + am.BadPlugins.Count);

                // 正则型命中 + 模板代入 + show 继承（showByDefault=1）
                var r = am.Match("SF1234567890");
                Check("正则型命中", r.Hit && r.Plugin != null && r.Plugin.Name == "A顺丰",
                    "实得=" + (r.Hit ? r.Plugin.Name : "未命中"));
                Eq("模板代入 {0}", r.Target, "https://sf/SF1234567890");
                Eq("产出形态", r.Out, "url");
                Check("show 继承 showByDefault=1", r.Hit && r.Show == 1, "实得=" + r.Show);
                // ★ 同 Prio（100）⇒ 文件名升序：a-sf.ahk < c-tie.ahk
                Check("同 Prio 文件名小者胜",
                    r.Hit && r.Plugin.File == "a-sf.ahk", "实得=" + (r.Hit ? r.Plugin.File : "-"));

                // 正则型不命中
                Check("正则型不命中", !am.Match("XX123456").Hit, "SF 正则不该命中 XX123456");
                // 前缀糖：整段（whole=true ⇒ ^abc.*$）
                var rp = am.Match("abcdef");
                Check("前缀糖命中", rp.Hit && rp.Plugin.Name == "G前缀糖",
                    "实得=" + (rp.Hit ? rp.Plugin.Name : "未命中"));
                Check("前缀糖整段不命中", !am.Match("xxabcdef").Hit,
                    "whole=true 时 xxabcdef 不该命中 ^abc.*$");
                // whole=false：允许尾部内容
                var rf = am.Match("TAG123xyz");
                Check("whole=false 允许尾部", rf.Hit && rf.Plugin.Name == "H包含",
                    "实得=" + (rf.Hit ? rf.Plugin.Name : "未命中"));
                // Prio 降序：300 胜 100
                var rd = am.Match("PRIO123");
                Check("Prio 高者胜", rd.Hit && rd.Plugin.Name == "D高",
                    "实得=" + (rd.Hit ? rd.Plugin.Name : "未命中"));
                // enabled=0 恒不命中（即便 Prio=500 且正则命中）
                bool offRe = Regex.IsMatch("OFF123",
                    AutoMatcher.Compile(@"^OFF\d+$", true));
                var roff = am.Match("OFF123");
                Check("enabled=0 恒不命中", !roff.Hit && offRe,
                    "正则命中=" + offRe + " 实际命中=" + roff.Hit);
                // minLen 边界：len=minLen-1 不命中 / =minLen 命中（两者正则都命中）
                bool minRe = Regex.IsMatch("MIN12", AutoMatcher.Compile(@"^MIN\d+$", true));
                Check("minLen 边界 len=5 不命中（minLen=6）",
                    !am.Match("MIN12").Hit && minRe, "正则命中=" + minRe);
                var rmin = am.Match("MIN123");
                Check("minLen 边界 len=6 命中",
                    rmin.Hit && rmin.Plugin.Name == "JminLen",
                    "实得=" + (rmin.Hit ? rmin.Plugin.Name : "未命中"));
                // 形态 B：无 Tpl ⇒ Target 留空（交给 PluginRunner）
                var rb = am.Match("choco install 7zip --force");
                Check("形态 B 命中且 Target 留空",
                    rb.Hit && rb.Plugin.IsFormB && rb.Target == "",
                    "实得 name=" + (rb.Hit ? rb.Plugin.Name : "-")
                    + " formB=" + (rb.Hit ? rb.Plugin.IsFormB.ToString() : "-")
                    + " target=[" + rb.Target + "]");
                // show 显式 0
                var rk = am.Match("HIDE123");
                Check("show 显式隐藏=0", rk.Hit && rk.Show == 0, "实得=" + rk.Show);

                // 总开关 enabled=0：全部不命中
                WriteConf(rootA, false, 6, 0, 1);
                am.Load(true);
                Check("总开关 enabled=0 恒不命中", !am.Match("SF1234567890").Hit, "");
            }
            catch (Exception ex)
            {
                Check("沙箱夹具 A 异常", false, ex.Message);
            }
            finally { DeleteSandbox(rootA); }

            // ---- 2b. show 三态：-1 继承会随 showByDefault 翻转 ----
            string rootB = MakeSandbox("b");
            try
            {
                WriteConf(rootB, true, 4, 0, 0);
                WritePlugin(rootB, "inherit.ahk", "INH继承", @"^INH\d+$", true, 100, "url",
                    "{0}", -1, true);
                WritePlugin(rootB, "strue.ahk", "STR显式显示", @"^STR\d+$", true, 100, "url",
                    "{0}", 1, true);
                WritePlugin(rootB, "sfalse.ahk", "SFA显式隐藏", @"^SFA\d+$", true, 100, "url",
                    "{0}", 0, true);

                var am = new AutoMatcher(rootB);
                am.Load(true);
                Check("show -1 继承 showByDefault=0",
                    am.Match("INH123").Show == 0, "实得=" + am.Match("INH123").Show);
                Check("show 显式 true 覆盖继承",
                    am.Match("STR123").Show == 1, "实得=" + am.Match("STR123").Show);
                Check("show 显式 false",
                    am.Match("SFA123").Show == 0, "实得=" + am.Match("SFA123").Show);

                WriteConf(rootB, true, 4, 0, 1);
                am.Load(true);
                Check("show -1 继承随 showByDefault 翻转（→1）",
                    am.Match("INH123").Show == 1, "实得=" + am.Match("INH123").Show);
            }
            catch (Exception ex)
            {
                Check("沙箱夹具 B 异常", false, ex.Message);
            }
            finally { DeleteSandbox(rootB); }
        }

        // ============================================================
        // 3. 元数据缓存：cacheSeconds>0 命中缓存；0 = 每次重扫
        //    （对照 auto.conf 注释"cacheSeconds 插件元数据缓存（秒）；0 = 每次重扫"）
        // ============================================================
        private static void CacheCases()
        {
            _sb.AppendLine("--- 3. 元数据缓存 ---");
            string root = MakeSandbox("cache");
            try
            {
                WriteConf(root, true, 4, 300, 0);
                WritePlugin(root, "base.ahk", "BASE", @"^CACHE\d+$", true, 100, "url",
                    "{0}", -1, true);

                var am = new AutoMatcher(root);
                int n1 = am.Load(true);
                Check("缓存：首扫插件数=1", n1 == 1, "实得=" + n1);

                // 扫后才能加文件：cacheSeconds=300 未过期 ⇒ Load 不该重扫
                WritePlugin(root, "added.ahk", "ADDED", @"^ADD\d+$", true, 100, "url",
                    "{0}", -1, true);
                int n2 = am.Load(false);
                Check("缓存命中：新增文件未被看到", n2 == 1, "实得=" + n2);

                // cacheSeconds=0 ⇒ 每次重扫
                WriteConf(root, true, 4, 0, 0);
                int n3 = am.Load(false);
                Check("cacheSeconds=0 每次重扫", n3 == 2, "实得=" + n3);
                Check("重扫后新插件可用", am.Match("ADD123").Hit, "");
            }
            catch (Exception ex)
            {
                Check("缓存夹具异常", false, ex.Message);
            }
            finally { DeleteSandbox(root); }
        }

        // ============================================================
        // 4. 形态 B：PluginRunner（临时驱动脚本 → Run(text) → 输出文件）
        //    ★ 走真 AHK 运行时；运行时缺失则整体 SKIP
        // ============================================================
        private static void RunnerCases()
        {
            _sb.AppendLine("--- 4. 形态 B（PluginRunner）---");
            string root = MakeSandbox("runner");
            try
            {
                var runner = new PluginRunner(root, null, 3000, s => _sb.AppendLine("  [pr] " + s));
                if (!File.Exists(runner.AhkExe))
                {
                    _sb.AppendLine("[SKIP] PluginRunner  (AHK 运行时缺失: " + runner.AhkExe + ")");
                    return;
                }
                _sb.AppendLine("AHK 运行时 = " + runner.AhkExe);

                WriteConf(root, true, 4, 0, 0);
                WriteFormB(root, "runner-echo.ahk", "RE回显", @"^ECHO\d+$",
                    "Run(text) {\r\n    return \"ECHO:\" text\r\n}\r\n");
                WriteFormB(root, "runner-throw.ahk", "RT抛错", @"^THROW\d+$",
                    "Run(text) {\r\n    throw Error(\"boom\")\r\n}\r\n");
                WriteFormB(root, "runner-empty.ahk", "RE空产出", @"^EMPTY\d+$",
                    "Run(text) {\r\n    return \"\"\r\n}\r\n");

                var am = new AutoMatcher(root);
                am.Load(true);

                string tmp = runner.TmpDir;
                int before = Directory.Exists(tmp) ? Directory.GetFiles(tmp).Length : 0;

                var p1 = am.Match("ECHO123");
                Check("形态 B 命中", p1.Hit && p1.Plugin.Name == "RE回显",
                    "实得=" + (p1.Hit ? p1.Plugin.Name : "未命中"));
                string out1 = runner.Run(p1.Plugin, "ECHO123");
                Eq("PluginRunner 往返（Run(text) 产出）", out1, "ECHO:ECHO123");
                Check("PluginRunner 产出被 Trim", out1 == out1.Trim(), "[" + out1 + "]");

                // 插件内抛错 ⇒ 空串（触发兜底②，调用方回落弹菜单）
                var p2 = am.Match("THROW123");
                Eq("插件抛错回落空串", runner.Run(p2.Plugin, "THROW123"), "");
                // 插件返回空串 ⇒ 空串
                var p3 = am.Match("EMPTY123");
                Eq("插件空产出回落空串", runner.Run(p3.Plugin, "EMPTY123"), "");

                int after = Directory.Exists(tmp) ? Directory.GetFiles(tmp).Length : 0;
                Check("临时目录已清理（state\\autotmp 无新增残留）", after <= before,
                    "前=" + before + " 后=" + after);
            }
            catch (Exception ex)
            {
                Check("PluginRunner 异常", false, ex.Message);
            }
            finally { DeleteSandbox(root); }
        }

        // ============================================================
        // 5. 1.5 步早退：三层兜底（对照 AHK `RunEver.ahk:624-627`）
        //    ★ 走真的 RunEverApp.TryAutoExec（不是复刻逻辑）：
        //      ① 无命中 → false ② 空 target → false ③ 派发失败 → false
        //      ④ 命中且派发成功 → true（跳过弹菜单）
        //    只用一个无害动作验证成功路径：action "exit 0"（隐藏 cmd，立即退出）。
        // ============================================================
        private static void StepCases()
        {
            _sb.AppendLine("--- 5. 1.5 步早退 / 三层兜底（RunEverApp.TryAutoExec）---");
            string root = MakeSandbox("step");
            try
            {
                // ★ RunEverApp.Init 会 Parse config\menus.conf，缺文件直接抛
                //   "配置文件不存在" ⇒ 先落一份最小可用夹具（本段不弹菜单，内容无关）
                WriteMenusConf(root);
                var app = new RunEverApp(root);
                bool ok = app.Init();
                if (!ok)
                {
                    _sb.AppendLine("[SKIP] TryAutoExec  (沙箱 root 初始化失败，见 runever-cs.log)");
                    return;
                }
                app.LogEnabled = false;      // 自检不刷日志文件

                // 夹具在 Init 之后写：Init 里的首启自举只建目录，不覆盖夹具
                WriteConf(root, true, 4, 0, 0);
                WritePlugin(root, "step-run.ahk", "S派发失败", @"^FAILX\d+$", true, 100, "run",
                    @"Z:\no-such-prog-xyz\app.exe", -1, true);
                WritePlugin(root, "step-action.ahk", "S派发成功", @"^OKX\d+$", true, 100, "action",
                    "exit 0", -1, true);
                WriteFormB(root, "step-empty.ahk", "S空产出", @"^EMPTYX\d+$",
                    "Run(text) {\r\n    return \"\"\r\n}\r\n");
                WriteFormB(root, "step-echo.ahk", "S回显", @"^ECHOX\d+$",
                    "Run(text) {\r\n    return \"exit 0\"\r\n}\r\n", "action");

                // ① 无命中 → false（弹菜单）
                Check("兜底① 无命中 → 回落弹菜单",
                    !app.TryAutoExec(new Context { Text = "zzz-no-match-12345" }), "");
                // 空文本 → false
                Check("空文本 → 回落弹菜单",
                    !app.TryAutoExec(new Context { Text = "   " }), "");
                // ② 形态 B 产出空 target → false
                Check("兜底② 形态 B 空产出 → 回落弹菜单",
                    !app.TryAutoExec(new Context { Text = "EMPTYX123" }), "");
                // ③ 派发失败（目标程序不存在）→ false
                Check("兜底③ 派发失败 → 回落弹菜单",
                    !app.TryAutoExec(new Context { Text = "FAILX123" }), "");
                // ④ 形态 A 命中 + 派发成功 → true（跳过弹菜单）
                Check("命中且派发成功 → true（跳过弹菜单）",
                    app.TryAutoExec(new Context { Text = "OKX123" }), "");
                // ④b 形态 B 产出目标 + 派发成功 → true
                Check("形态 B 产出目标后派发成功 → true",
                    app.TryAutoExec(new Context { Text = "ECHOX123" }), "");
            }
            catch (Exception ex)
            {
                Check("TryAutoExec 异常", false, ex.Message);
            }
            finally { DeleteSandbox(root); }
        }

        /// <summary>写一个形态 B 插件（无 Tpl，带 Run(text) 脚本体）。</summary>
        private static void WriteFormB(string root, string file, string name, string match,
            string body, string outType = "url")
        {
            var sb = new StringBuilder();
            sb.AppendLine("#Requires AutoHotkey v2.0");
            sb.AppendLine("global RVE_FN_Name  := \"" + name + "\"");
            sb.AppendLine("global RVE_FN_Match := \"" + match + "\"");
            sb.AppendLine("global RVE_FN_Whole := true");
            sb.AppendLine("global RVE_FN_Prio  := 100");
            sb.AppendLine("global RVE_FN_Out   := \"" + outType + "\"");
            sb.AppendLine("; ===== 形态 B：无 RVE_FN_Tpl，目标由 Run(text) 产出 =====");
            sb.Append(body);
            File.WriteAllText(Path.Combine(root, "config", "functions", file),
                sb.ToString(), new UTF8Encoding(false));
        }

        // ============================================================
        // 5. 实机 root：对照 AHK `RunEver.ahk:403-441` 自检口径
        //    用例表逐字照抄（:411-416），期望值不猜
        // ============================================================
        private static void RealRootCases(string root)
        {
            _sb.AppendLine("--- 6. 实机 root ---");
            try
            {
                var am = new AutoMatcher(root, s => _sb.AppendLine("  [am] " + s));
                int n = am.Load(true);
                _sb.AppendLine("[OK]   自动执行  插件数=" + n
                    + " enabled=" + (am.Conf.Enabled ? "1" : "0")
                    + " minLen=" + am.Conf.MinLen
                    + " cache=" + am.Conf.CacheSec + "s"
                    + " showByDefault=" + am.Conf.ShowByDefault
                    + " waitWhenShown=" + am.Conf.WaitWhenShown);
                Check("实机插件数 > 0", n > 0, "实得=" + n);
                Check("实机无非法插件", am.BadPlugins.Count == 0,
                    "实得=" + am.BadPlugins.Count);

                bool anyFormB = false;
                foreach (var p in am.Plugins) if (p.IsFormB) anyFormB = true;
                Check("实机含形态 B 插件（无 Tpl）", anyFormB, "");

                if (!am.Conf.Enabled)
                {
                    _sb.AppendLine("[SKIP] 实机用例  (auto.conf enabled=0，自动执行已关闭)");
                    return;
                }

                string[][] cases = {
                    new[] { "SF1234567890",       "顺丰快递查询" },
                    new[] { "https://a.com",      "打开网址" },
                    new[] { "choco install 7zip", "执行 Chocolatey 命令" },
                    new[] { "普通文字啊啊啊",       "" }
                };
                int caseFail = 0;
                foreach (var c in cases)
                {
                    var r = am.Match(c[0]);
                    string got = r.Hit ? r.Plugin.Name : "";
                    bool ok = got == c[1];
                    if (!ok) caseFail++;
                    Check("实机用例 [" + c[0] + "]", ok,
                        ok ? "" : "期望=" + (c[1] == "" ? "(不命中)" : c[1])
                             + " 实得=" + (got == "" ? "(不命中)" : got));
                }
                if (caseFail == 0)
                    _sb.AppendLine("[OK]   实机用例  " + cases.Length + " 个全通过");
            }
            catch (Exception ex)
            {
                Check("实机 root 自动执行异常", false, ex.Message);
            }
        }
    }
}
