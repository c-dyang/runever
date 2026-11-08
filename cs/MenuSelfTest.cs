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
    }
}
