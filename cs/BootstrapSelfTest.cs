using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 配置自举 + 触发热键解析 自检：
    ///   ① **结构比对**（B6-2）：模板 / 现役配置各自的结构自洽 ——
    ///      分组数、条目数、必需键齐备、层级合规（无跳级、父后紧跟子）。
    ///      ★ 不再拿模板与"用户手改过"的现役配置比逐字节 hash（那是天然不同的两个东西）；
    ///        模板漂移改由「同一模板两次释放产物逐字节一致」（②c）来防。
    ///      `RVE_B6_LEGACY=1` 可把旧 hash 断言恢复成 FAIL，仅供"旧口径能红"的对照取证。
    ///   ② 空目录里 Ensure() 能释放 menus.conf + auto.conf，内容与模板一致
    ///   ③ 已存在时 Ensure() 绝不覆盖用户配置
    ///   ④ HotkeySpec 解析：合法写法通过、裸字母/空值/只有修饰符 被拒
    ///
    /// 用法：RunEverCs.exe --selftest-bootstrap &lt;root&gt; &lt;out.txt&gt;
    /// </summary>
    public static class BootstrapSelfTest
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
            void info(string label, string extra = "")
            {
                sb.AppendLine("  [INFO] " + label + (extra != "" ? "   <<< " + extra : ""));
            }

            // B6-2 取证开关：把旧口径（模板 vs 现役配置逐字节 hash）恢复成 FAIL。
            bool legacy = Environment.GetEnvironmentVariable("RVE_B6_LEGACY") == "1";

            string testDir = Path.Combine(root, "state", "bootstrap-test");
            try
            {
                sb.AppendLine("=== 配置自举 + 热键解析自检 ===");
                if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
                Directory.CreateDirectory(testDir);

                // ---- ① 结构比对（B6-2：不比逐字节 hash）----
                //   为什么改口径：模板是"出厂默认"，config\ 里的现役文件是**用户手改过**的
                //   （热键、espath、webdav、菜单条目都在变）。两者逐字节必然不同 ⇒
                //   旧断言恒红，且红的信息量为零（"不同"是设计，不是漂移）。
                //   现在只问结构问题：能不能解析、分组/条目数是否非零、层级是否合规、
                //   产品要读的必需键是否齐备。差异本身降级成 [INFO]。
                sb.AppendLine("--- ① 结构比对（模板 / 现役配置各自自洽）---");
                if (legacy)
                    sb.AppendLine("  [INFO] RVE_B6_LEGACY=1 ⇒ 旧「逐字节 hash」断言按 FAIL 计（仅取证，非发布口径）");

                foreach (var name in ConfigBootstrap.TemplateNames)
                {
                    string tpl = ConfigBootstrap.FindTemplate(root, name);
                    chk(tpl != null, "① 找到模板 " + name, "FindTemplate 返回 null");
                    if (tpl == null) continue;

                    ConfShape ts = ConfShape.Of(tpl, name);
                    chk(ts.Parsed, "① 模板 " + name + " 可解析（" + RuleText(name) + "）", ts.Error);
                    chk(ts.Problems.Count == 0,
                        "① 模板 " + name + " 结构合规（" + RuleText(name) + "）", Join(ts.Problems));
                    chk(ts.Units > 0, "① 模板 " + name + " 非空",
                        "分组=" + ts.Groups + " 条目=" + ts.Items + " 键=" + ts.Keys.Count);
                    info("① 模板 " + name + " 结构摘要", ts.Summary());
                    // 模板缺"后来新增的必需键"= 模板陈旧（不影响老用户，但会让新装用户少默认值）——
                    // 记为信息项：这是产品/模板同步的事，不是自检该判红的事。
                    var tplMissing = MissingKeys(ts, name);
                    if (tplMissing.Count > 0)
                        info("① 模板 " + name + " 缺后来新增的键（模板陈旧，仅信息）", Join(tplMissing));

                    string cur = Path.Combine(root, "config", name);
                    if (!File.Exists(cur))
                    {
                        chk(false, "① 现役 config\\" + name + " 存在", "文件缺失（自举未生效？）");
                        continue;
                    }

                    ConfShape cs = ConfShape.Of(cur, name);
                    chk(cs.Parsed, "① 现役 " + name + " 可解析（解析器不抛）", cs.Error);
                    chk(cs.Problems.Count == 0,
                        "① 现役 " + name + " 结构合规（" + RuleText(name) + "）", Join(cs.Problems));
                    info("① 现役 " + name + " 结构摘要", cs.Summary());

                    // 必需键：产品要读的配置项必须还在（缺了 = 功能静默退回默认值）
                    foreach (string k in RequiredKeys(name))
                        chk(cs.Keys.Contains(k),
                            "① 现役 " + name + " 必需键 " + k + " 齐备",
                            "缺失 ⇒ 该配置项读不到（结构不符项：缺键 " + k + "）");

                    info("① 模板↔现役 结构差异（仅信息；用户手改过 ⇒ 计数不同属正常）",
                        DiffText(ts, cs));

                    if (legacy)
                    {
                        string h1 = Hash(tpl), h2 = Hash(cur);
                        chk(h1 == h2, "①〔旧口径〕模板与现有 " + name + " 逐字节一致",
                            "模板=" + h1.Substring(0, 12) + " 现行=" + h2.Substring(0, 12));
                    }
                }

                // ---- ①c 判别力自证：坏样本必须被打红（环境无关）----
                //   没有这一段，"结构合规"全绿也不能证明判据有判别力。
                sb.AppendLine("  --- ①c 判别力自证（合成坏样本）---");
                string negDir = Path.Combine(testDir, "struct-neg");
                Directory.CreateDirectory(negDir);

                string badAuto = Path.Combine(negDir, "auto-bad.conf");
                File.WriteAllText(badAuto,
                    "enabled=1" + Environment.NewLine + "minLen=4" + Environment.NewLine,
                    new UTF8Encoding(false));
                var negAuto = ConfShape.Of(badAuto, "auto.conf");
                var negMissing = MissingKeys(negAuto, "auto.conf");
                chk(negAuto.Parsed && negMissing.Contains("menuOrder") && negMissing.Contains("memTrimIdle"),
                    "①c 删掉必需键（menuOrder/memTrimIdle）⇒ 结构检查必须报缺键",
                    "缺键=" + Join(negMissing));

                string badMenus = Path.Combine(negDir, "menus-jump.conf");
                File.WriteAllText(badMenus,
                    "! 坏组  always" + Environment.NewLine
                    + "      跳级条目  _X | run a.exe" + Environment.NewLine    // 6 空格 ⇒ 层级 3，prev=-1 无父
                    + "  正常条目  _A | run b.exe" + Environment.NewLine,
                    new UTF8Encoding(false));
                var negMenus = ConfShape.Of(badMenus, "menus.conf");
                chk(negMenus.Parsed && HasProblem(negMenus, "跳级"),
                    "①c 层级从 0 跳到 3（父级缺失）⇒ 结构检查必须报跳级",
                    Join(negMenus.Problems));

                string badDup = Path.Combine(negDir, "auto-dup.conf");
                File.WriteAllText(badDup,
                    "hideMissing=1" + Environment.NewLine + "hideMissing=0" + Environment.NewLine
                    + "menuOrder=dynamic" + Environment.NewLine + "memTrimIdle=20" + Environment.NewLine,
                    new UTF8Encoding(false));
                var negDup = ConfShape.Of(badDup, "auto.conf");
                chk(negDup.Parsed && HasProblem(negDup, "重复"),
                    "①c 同一键出现两次 ⇒ 结构检查必须报重复键",
                    Join(negDup.Problems));

                // ---- ② 空目录自举释放 ----
                //   模板按「<目标root>\templates\」查找 ⇒ 测试也要摆一份模板到测试根，
                //   否则测的是"找不到模板"的分支而不是发布形态。
                string tplDir = Path.Combine(testDir, "templates");
                Directory.CreateDirectory(tplDir);
                foreach (var name in ConfigBootstrap.TemplateNames)
                {
                    string src = ConfigBootstrap.FindTemplate(root, name);
                    if (src != null) File.Copy(src, Path.Combine(tplDir, name), true);
                }
                //   示例插件模板也要摆一份（否则测的是"没有示例"的分支）
                string tplFn = ConfigBootstrap.FindTemplateDir(root, "functions");
                if (tplFn != null)
                {
                    string dstFn = Path.Combine(tplDir, "functions");
                    Directory.CreateDirectory(dstFn);
                    foreach (var f in Directory.GetFiles(tplFn, "*.ahk"))
                        File.Copy(f, Path.Combine(dstFn, Path.GetFileName(f)), true);
                }

                bool released = ConfigBootstrap.Ensure(testDir, s => sb.AppendLine("     · " + s));
                chk(released, "② Ensure() 报告已释放模板");

                foreach (var name in ConfigBootstrap.TemplateNames)
                {
                    string dest = Path.Combine(testDir, "config", name);
                    chk(File.Exists(dest), "② 释放出 config\\" + name);

                    string tpl = ConfigBootstrap.FindTemplate(root, name);
                    if (File.Exists(dest) && tpl != null)
                        chk(Hash(dest) == Hash(tpl), "② 释放内容与模板一致 " + name);
                }

                chk(Directory.Exists(Path.Combine(testDir, "config", "functions")),
                    "② 建出插件目录 config\\functions");

                // ---- ②c 模板产物确定性（B6-2 要求 2：真正防"模板漂移"的那条）----
                //   口径：**同一份模板**释放两次，两次产物必须逐字节一致（且与模板一致）。
                //   这样模板被改动时，要么②（产物==模板）红，要么这里红 —— 都是"模板自身"的问题；
                //   而"用户与模板不同"永远不再被判红。
                string rel2 = Path.Combine(testDir, "release-2");
                Directory.CreateDirectory(Path.Combine(rel2, "templates"));
                foreach (var name in ConfigBootstrap.TemplateNames)
                {
                    string src = Path.Combine(tplDir, name);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(rel2, "templates", name), true);
                }
                ConfigBootstrap.Ensure(rel2, null);
                foreach (var name in ConfigBootstrap.TemplateNames)
                {
                    string a = Path.Combine(testDir, "config", name);
                    string b = Path.Combine(rel2, "config", name);
                    if (!File.Exists(a) || !File.Exists(b))
                    {
                        chk(false, "②c 两次释放都产出 " + name, "缺文件（a=" + File.Exists(a)
                            + " b=" + File.Exists(b) + "）");
                        continue;
                    }
                    chk(Hash(a) == Hash(b),
                        "②c 同一模板两次释放产物逐字节一致 " + name,
                        "第一次=" + Hash(a).Substring(0, 12) + " 第二次=" + Hash(b).Substring(0, 12));
                }

                // ---- ②b 示例插件自举（自动执行的"非空起点"）----
                string fnDir = Path.Combine(testDir, "config", "functions");
                int fnCnt = Directory.Exists(fnDir) ? Directory.GetFiles(fnDir, "*.ahk").Length : 0;
                chk(fnCnt > 0, "②b 释放出示例插件（清理机器上自动执行不再空壳）",
                    "config\\functions 里 0 个 .ahk");

                if (fnCnt > 0)
                {
                    string one = Directory.GetFiles(fnDir, "*.ahk")[0];
                    string hBefore = Hash(one);
                    File.AppendAllText(one, Environment.NewLine + "; 用户改动", new UTF8Encoding(false));
                    string hAfterEdit = Hash(one);

                    chk(!ConfigBootstrap.ReleasePluginsIfMissing(testDir, null),
                        "②b 二次释放无动作（已存在即跳过）");
                    chk(Hash(one) == hAfterEdit && hAfterEdit != hBefore,
                        "②b 已存在的插件不被模板覆盖（用户改动保留）");
                }
                chk(Directory.Exists(Path.Combine(testDir, "cache")), "② 建出 cache 目录");
                chk(Directory.Exists(Path.Combine(testDir, "state")), "② 建出 state 目录");

                // ---- ③ 已存在不覆盖 ----
                string autoPath = Path.Combine(testDir, "config", "auto.conf");
                File.AppendAllText(autoPath, Environment.NewLine + "# 用户改过的痕迹", new UTF8Encoding(false));
                string before = Hash(autoPath);
                ConfigBootstrap.Ensure(testDir, null);
                chk(Hash(autoPath) == before, "③ 已存在的配置不被覆盖（用户改动保留）");
                chk(!ConfigBootstrap.Ensure(testDir, null), "③ 二次 Ensure() 无模板可释放（幂等）");

                // ---- ④ 热键解析 ----
                chk(ParseOk("`", 0xC0, 0), "④ ` → VK_OEM_3 无修饰符");
                chk(ParseOk("F8", 0x77, 0), "④ F8 → 0x77");
                chk(ParseOk("^!r", 0x52, HotkeySpec.MOD_CONTROL | HotkeySpec.MOD_ALT), "④ ^!r → Ctrl+Alt+R");
                chk(ParseOk("#F12", 0x7B, HotkeySpec.MOD_WIN), "④ #F12 → Win+F12");
                chk(ParseOk("esc", 0x1B, 0), "④ esc（大小写不敏感）→ 0x1B");
                chk(ParseOk("^`", 0xC0, HotkeySpec.MOD_CONTROL), "④ ^` → Ctrl+反引号");

                chk(ParseFail("a"), "④ 裸字母 a 被拒（会吞掉打字）");
                chk(ParseFail("7"), "④ 裸数字 7 被拒");
                chk(ParseFail(""), "④ 空值被拒");
                chk(ParseFail("^"), "④ 只有修饰符被拒");
                chk(ParseFail("F99"), "④ 不存在的键名 F99 被拒");

                // 规范化文本
                HotkeySpec s1;
                string e1;
                HotkeySpec.TryParse("^!r", out s1, out e1);
                chk(s1 != null && s1.Text == "^!R", "④ 规范化文本为大写主键 ^!R",
                    s1 == null ? "解析失败" : s1.Text);

                sb.AppendLine();
                sb.AppendLine("结果: " + (fail == 0 ? "全部通过 ✓" : "失败 " + fail + " 项 ✗")
                    + "   (通过 " + pass + " / 失败 " + fail + ")");
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("  FAIL 异常: " + ex.Message + "\n" + ex.StackTrace);
            }

            try
            {
                File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }

            return fail == 0 ? 0 : 1;
        }

        private static bool ParseOk(string raw, uint vk, uint mods)
        {
            HotkeySpec s;
            string err;
            if (!HotkeySpec.TryParse(raw, out s, out err)) return false;
            return s.Vk == vk && s.Modifiers == mods;
        }

        private static bool ParseFail(string raw)
        {
            HotkeySpec s;
            string err;
            return !HotkeySpec.TryParse(raw, out s, out err);
        }

        // ================================================================
        // B6-2 · 结构比对（只读：解析产品配置，不看字节）
        // ================================================================

        /// <summary>
        /// 产品要读的**必需键**（缺了功能会静默退回默认值 ⇒ 属"结构不符"）。
        /// menus.conf 不是键值文件，靠分组/条目/层级判定，故此处返回空表。
        /// </summary>
        private static string[] RequiredKeys(string name)
        {
            if (string.Equals(name, "auto.conf", StringComparison.OrdinalIgnoreCase))
                return new[] { "menuOrder", "memTrimIdle", "hideMissing" };   // 菜单段序 / 内存整理 / 缺件隐藏
            return new string[0];
        }

        /// <summary>判据文字的"人话版"，用于断言标签（FAIL 时一眼看出是哪条规则不满足）。</summary>
        private static string RuleText(string name)
        {
            if (string.Equals(name, "menus.conf", StringComparison.OrdinalIgnoreCase))
                return "分组数 / 条目数 / 层级合规（无跳级、父先于子）";
            return "键非空 / 键唯一 / 必需键齐备";
        }

        /// <summary>结构摘要（供 [INFO] 行；模板与现役各一行，便于人眼比对）。</summary>
        private sealed class ConfShape
        {
            public bool Parsed = true;
            public string Error = "";
            public int Groups;
            public int Items;
            public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Problems = new List<string>();

            public int Units { get { return Groups + Items + Keys.Count; } }

            public string Summary()
            {
                if (Keys.Count > 0 || Groups == 0)
                    return "键=" + Keys.Count + " 结构不符项=" + Problems.Count;
                return "分组=" + Groups + " 条目=" + Items + " 结构不符项=" + Problems.Count;
            }

            /// <summary>按文件名选解析器：menus.conf 走产品 ConfParser，其余走 key=value 结构扫描。</summary>
            public static ConfShape Of(string path, string name)
            {
                var s = new ConfShape();
                if (!string.Equals(name, "menus.conf", StringComparison.OrdinalIgnoreCase))
                    return s.ReadKeyValues(path);

                try
                {
                    var cp = new ConfParser(path);
                    cp.Parse();
                    s.Groups = cp.Groups.Count;
                    if (s.Groups == 0) s.Problems.Add("没有任何分组（`! 名称 类型` 行）");

                    foreach (var g in cp.Groups)
                    {
                        s.Items += g.Items.Count;
                        if (g.Items.Count == 0)
                            s.Problems.Add("分组「" + g.Name + "」（第 " + g.Line + " 行）没有任何条目");

                        // ★ 层级基准：**分组行自己就是层级 0 的父**。
                        //   `! 通用菜单  always` 下的条目从层级 1 开始是合规的，
                        //   层级 2 必须紧跟一个层级 1 的父条目（`工具`），层级 3 必须紧跟层级 2 ……
                        //   所以：prev 从 0（分组头）起，seen 先塞 0。
                        int prev = 0;
                        var seen = new HashSet<int>();
                        seen.Add(0);
                        foreach (var it in g.Items)
                        {
                            if (it.Level > prev + 1)
                                s.Problems.Add("分组「" + g.Name + "」第 " + it.Line + " 行层级 "
                                    + prev + "→" + it.Level + " 跳级（缺层级 " + (it.Level - 1) + " 的父条目）");
                            else if (it.Level > 0 && !seen.Contains(it.Level - 1))
                                s.Problems.Add("分组「" + g.Name + "」第 " + it.Line + " 行层级 " + it.Level
                                    + "，但前面没有层级 " + (it.Level - 1) + " 的父条目");
                            prev = it.Level;
                            seen.Add(it.Level);
                        }
                    }
                }
                catch (Exception ex)
                {
                    s.Parsed = false;
                    s.Error = ex.Message;
                }
                return s;
            }

            /// <summary>key=value 结构扫描：键非空、键唯一、至少一键（与 AutoConfStore.Read 同解析口径）。</summary>
            private ConfShape ReadKeyValues(string path)
            {
                if (!File.Exists(path)) { Parsed = false; Error = "文件不存在: " + path; return this; }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string text = File.ReadAllText(path, Encoding.UTF8)
                        .Replace("\r\n", "\n").Replace("\r", "\n");
                    string[] lines = text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length == 0) continue;
                        if (line[0] == '#' || line[0] == ';') continue;

                        int eq = line.IndexOf('=');
                        if (eq <= 0)
                        {
                            Problems.Add("第 " + (i + 1) + " 行不是 key=value（内容=" + Clip(line) + "）");
                            continue;
                        }
                        string k = line.Substring(0, eq).Trim();
                        if (k.Length == 0)
                        {
                            Problems.Add("第 " + (i + 1) + " 行键为空");
                            continue;
                        }
                        if (!seen.Add(k))
                            Problems.Add("重复键 " + k + "（第 " + (i + 1) + " 行；后写的值会覆盖先写的）");
                        Keys.Add(k);
                    }
                }
                catch (Exception ex)
                {
                    Parsed = false;
                    Error = ex.Message;
                }
                if (Keys.Count == 0) Problems.Add("没有任何 key=value 行");
                return this;
            }

            private static string Clip(string s)
            {
                s = s ?? "";
                return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
            }
        }

        /// <summary>必需键中缺哪几个（空表 = 齐备）。</summary>
        private static List<string> MissingKeys(ConfShape s, string name)
        {
            var miss = new List<string>();
            foreach (string k in RequiredKeys(name))
                if (!s.Keys.Contains(k)) miss.Add(k);
            return miss;
        }

        private static bool HasProblem(ConfShape s, string keyword)
        {
            foreach (string p in s.Problems)
                if (p.IndexOf(keyword, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>模板↔现役的结构差异（**只报告，不判红**：用户手改过是设计，不是漂移）。</summary>
        private static string DiffText(ConfShape tpl, ConfShape cur)
        {
            var parts = new List<string>();
            if (tpl.Groups > 0 || cur.Groups > 0)
                parts.Add("分组 " + tpl.Groups + "→" + cur.Groups + "、条目 " + tpl.Items + "→" + cur.Items);

            var onlyTpl = new List<string>();
            foreach (string k in tpl.Keys) if (!cur.Keys.Contains(k)) onlyTpl.Add(k);
            var onlyCur = new List<string>();
            foreach (string k in cur.Keys) if (!tpl.Keys.Contains(k)) onlyCur.Add(k);

            if (tpl.Keys.Count > 0 || cur.Keys.Count > 0)
            {
                parts.Add("键 " + tpl.Keys.Count + "→" + cur.Keys.Count);
                parts.Add("仅模板有[" + (onlyTpl.Count == 0 ? "-" : string.Join(",", onlyTpl.ToArray())) + "]");
                parts.Add("仅现役有[" + (onlyCur.Count == 0 ? "-" : string.Join(",", onlyCur.ToArray())) + "]");
            }
            return string.Join("  ", parts.ToArray());
        }

        private static string Join(IEnumerable<string> xs)
        {
            var list = new List<string>();
            foreach (string x in xs) list.Add(x);
            return list.Count == 0 ? "<无>" : string.Join("; ", list.ToArray());
        }

        private static string Hash(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                byte[] h = sha.ComputeHash(fs);
                var sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
