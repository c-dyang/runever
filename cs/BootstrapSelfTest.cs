using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 配置自举 + 触发热键解析 自检：
    ///   ① 模板随产物存在，且与 config\ 现行文件**逐字节一致**（hash 比对）
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

            string testDir = Path.Combine(root, "state", "bootstrap-test");
            try
            {
                sb.AppendLine("=== 配置自举 + 热键解析自检 ===");
                if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
                Directory.CreateDirectory(testDir);

                // ---- ① 模板存在且与现行配置一致 ----
                foreach (var name in ConfigBootstrap.TemplateNames)
                {
                    string tpl = ConfigBootstrap.FindTemplate(root, name);
                    chk(tpl != null, "① 找到模板 " + name, "FindTemplate 返回 null");

                    string cur = Path.Combine(root, "config", name);
                    if (tpl != null && File.Exists(cur))
                    {
                        string h1 = Hash(tpl), h2 = Hash(cur);
                        chk(h1 == h2, "① 模板与现有 " + name + " 逐字节一致",
                            "模板=" + h1.Substring(0, 12) + " 现行=" + h2.Substring(0, 12));
                    }
                }

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
