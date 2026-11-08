using System;
using System.IO;

namespace RunEverCs.Services
{
    /// <summary>
    /// 首次启动自举 —— 对应 AHK `RunEver.ahk:1088-1112 EnsureLayout() + FileInstall`。
    ///
    /// AHK 的做法：把 `config\menus.conf` / `config\auto.conf` 编译进 exe（FileInstall），
    /// 启动时若目标缺失就释放。C# 侧等价物 = **模板文件随产物发布**（`templates\`），
    /// 缺失即复制 —— 逐字节一致，不做任何字符串转义/格式化。
    ///
    /// ★ 为什么不能用"代码里嵌字符串常量"：配置里有 `%clip%` / `%file%` / `"%clip%"`
    ///   这类字面量，进字符串常量就要过转义，等于给首启模板埋坑（AHK 注释里记了同一条教训）。
    ///
    /// 模板查找顺序（第一个存在者胜）：
    ///   <root>\templates\<name>
    ///   <exe 所在目录>\templates\<name>
    ///   <exe 所在目录>\..\templates\<name>
    /// </summary>
    public static class ConfigBootstrap
    {
        /// <summary>自举模板文件名（配置类；插件模板另见 templates\functions\）。</summary>
        public static readonly string[] TemplateNames = { "menus.conf", "auto.conf", "choco-map.conf" };

        /// <summary>
        /// 建齐运行目录 + 缺失配置按模板释放。
        /// 返回：本次是否释放了至少一个模板（供日志/自检断言）。
        /// </summary>
        public static bool Ensure(string root, Action<string> log)
        {
            Action<string> lg = log ?? (s => { });

            // ---- 目录（幂等；含插件目录，首启就要存在）----
            foreach (var d in new[] { "config", "config/functions", "cache", "state" })
            {
                string p = Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar));
                try { if (!Directory.Exists(p)) Directory.CreateDirectory(p); }
                catch (Exception ex) { lg("⚠ 建目录失败 " + p + ": " + ex.Message); }
            }

            bool released = false;
            foreach (var name in TemplateNames)
                released |= ReleaseIfMissing(root, name, lg);

            // 示例插件（自动执行的"非空起点"）：只补缺，绝不覆盖用户已有插件
            released |= ReleasePluginsIfMissing(root, lg);

            return released;
        }

        /// <summary>
        /// 释放示例插件：`templates\functions\*.ahk` → `config\functions\`（仅当目标不存在）。
        ///
        /// 为什么需要：AHK 版只自举 menus.conf + auto.conf，干净机器上 `config\functions\`
        ///   是空的 ⇒ "选中文本自动执行"这个核心能力看起来是坏的（没插件可命中）。
        ///   这里给几个能用的例子（正则+url / {0} 直开 / 本地路径 open / action+显示窗口），
        ///   用户照抄改一个就是自己的插件。
        /// </summary>
        public static bool ReleasePluginsIfMissing(string root, Action<string> log)
        {
            Action<string> lg = log ?? (s => { });
            string dstDir = Path.Combine(root, "config", "functions");
            string srcDir = FindTemplateDir(root, "functions");

            if (srcDir == null) return false;   // 没有示例模板不是错误

            bool any = false;
            try
            {
                foreach (var src in Directory.GetFiles(srcDir, "*.ahk"))
                {
                    string dst = Path.Combine(dstDir, Path.GetFileName(src));
                    if (File.Exists(dst)) continue;      // ★ 绝不覆盖用户改过的插件

                    File.Copy(src, dst, false);
                    lg("首次启动：已释放示例插件 → config\\functions\\" + Path.GetFileName(dst));
                    any = true;
                }
            }
            catch (Exception ex)
            {
                lg("⚠ 释放示例插件失败: " + ex.Message);
            }
            return any;
        }

        /// <summary>目标缺失 → 从模板释放（已存在则什么都不做，绝不覆盖用户配置）。</summary>
        public static bool ReleaseIfMissing(string root, string name, Action<string> log)
        {
            Action<string> lg = log ?? (s => { });
            string dest = Path.Combine(root, "config", name);

            if (File.Exists(dest)) return false;

            string src = FindTemplate(root, name);
            if (src == null)
            {
                lg("⚠ 配置模板缺失，未能释放 config\\" + name
                    + "（找过：<root>\\templates\\、<exe>\\templates\\、<exe>\\..\\templates\\）");
                return false;
            }

            try
            {
                File.Copy(src, dest, false);

                // 逐字节校验（长度 + 内容），失败就当没释放并说明
                byte[] a = File.ReadAllBytes(src);
                byte[] b = File.ReadAllBytes(dest);
                bool same = (a.Length == b.Length);
                if (same)
                    for (int i = 0; i < a.Length; i++)
                        if (a[i] != b[i]) { same = false; break; }

                if (!same)
                {
                    lg("⚠ 模板释放后校验不一致：" + dest + "（源 " + a.Length + "B / 目标 " + b.Length + "B）");
                    return false;
                }

                lg("首次启动：已生成默认配置模板 → config\\" + name
                    + "（" + b.Length + "B，来源 " + src + "）");
                return true;
            }
            catch (Exception ex)
            {
                lg("⚠ 释放模板失败 config\\" + name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>按三级顺序找模板；找不到返回 null。</summary>
        public static string FindTemplate(string root, string name)
        {
            foreach (var c in CandidatePaths(root, name))
            {
                try { if (File.Exists(c)) return Path.GetFullPath(c); }
                catch { }
            }
            return null;
        }

        /// <summary>按三级顺序找模板目录（如 templates\functions）；找不到返回 null。</summary>
        public static string FindTemplateDir(string root, string sub)
        {
            foreach (var c in CandidatePaths(root, sub))
            {
                try { if (Directory.Exists(c)) return Path.GetFullPath(c); }
                catch { }
            }
            return null;
        }

        private static System.Collections.Generic.List<string> CandidatePaths(string root, string name)
        {
            var cands = new System.Collections.Generic.List<string>();

            cands.Add(Path.Combine(root, "templates", name));

            try
            {
                string exeDir = Path.GetDirectoryName(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    cands.Add(Path.Combine(exeDir, "templates", name));
                    cands.Add(Path.Combine(exeDir, "..", "templates", name));
                }
            }
            catch { }

            return cands;
        }
    }
}
