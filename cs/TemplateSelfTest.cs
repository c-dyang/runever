using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 插件模板生成自检：
    ///   ① 四种模板都能生成
    ///   ② 生成的文件能被 PluginScanner 正确解析回来（元数据往返一致）
    ///   ③ 生成的文件语法合法（可选：交给 AHK /validate）
    /// 用法：RunEverCs.exe --selftest-template &lt;root&gt; &lt;out.txt&gt;
    /// </summary>
    public static class TemplateSelfTest
    {
        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;
            string root = args[1];
            string outFile = args[2];

            var sb = new StringBuilder();
            int pass = 0, fail = 0;

            // ★ 局部函数：第三参可省略
            void chk(bool c, string label, string extra = "")
            {
                if (c) { pass++; sb.AppendLine("  OK   " + label); }
                else { fail++; sb.AppendLine("  FAIL " + label + (extra != "" ? "   <<< " + extra : "")); }
            }

            string testDir = Path.Combine(root, "state", "template-test");
            try
            {
                sb.AppendLine("=== 插件模板生成自检 ===");
                if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
                Directory.CreateDirectory(testDir);

                // ---- ① 匹配文本 → 执行命令 ----
                var s1 = new PluginTemplate.Spec
                {
                    Kind = PluginTemplate.Kind.CmdRun,
                    Name = "测试-CHOCO命令",
                    Match = @"^choco\s+install\b",
                    Whole = false,
                    Prio = 200,
                    Command = "choco install {0}",
                    ShowWindow = true,
                    Icon = "SHELL32.dll,71"
                };
                string err;
                string p1 = PluginTemplate.Write(testDir, s1, out err);
                chk(!string.IsNullOrEmpty(p1), "① 命令模板生成成功", err);
                if (!string.IsNullOrEmpty(p1))
                {
                    string code = File.ReadAllText(p1, Encoding.UTF8);
                    chk(code.Contains("RVE_FN_Show  := true"), "① 显示窗口元数据正确");
                    chk(code.Contains("RVE_FN_Out   := \"action\""), "① 产出类型=action");
                    // ★ 关键回归断言：拼接必须用 Run() 的形参名 text。
                    //   曾硬编码 vText ⇒ 生成的插件报 "never assigned a value"
                    //   并弹模态警告框（用户截图实证），插件完全不可用。
                    chk(code.Contains("\"choco install \" text"), "① 命令用形参 text 拼接");
                    chk(!code.Contains("vText"), "① 不得残留未定义的 vText");

                    // 扫描回来验证元数据
                    var sc = new PluginScanner(root);
                    var info = sc.ReadMeta(p1);
                    chk(info != null, "① 元数据可被扫描器解析");
                    if (info != null)
                    {
                        chk(info.Name == "测试-CHOCO命令", "① 名称往返", info.Name);
                        chk(info.Match == @"^choco\s+install\b", "① 匹配式往返", info.Match);
                        chk(info.Whole == false, "① whole=false 往返", info.Whole.ToString());
                        chk(info.Prio == 200, "① 优先级往返", info.Prio.ToString());
                        chk(info.Out == "action", "① 产出往返", info.Out);
                        chk(info.Show == 1, "① show=1 往返", info.Show.ToString());
                        chk(info.IsFormB, "① 识别为逻辑型（无 Tpl）");
                    }
                }

                // ---- ② 匹配文本 → 打开网址 ----
                var s2 = new PluginTemplate.Spec
                {
                    Kind = PluginTemplate.Kind.OpenUrl,
                    Name = "测试-快递查询",
                    Match = @"^SF\d{10,}$",
                    Whole = true,
                    Prio = 100,
                    UrlTemplate = "https://www.sf-express.com/chn/sc/waybill/waybill-detail/{0}"
                };
                string p2 = PluginTemplate.Write(testDir, s2, out err);
                chk(!string.IsNullOrEmpty(p2), "② URL 模板生成成功", err);
                if (!string.IsNullOrEmpty(p2))
                {
                    var sc = new PluginScanner(root);
                    var info = sc.ReadMeta(p2);
                    chk(info != null, "② 元数据可解析");
                    if (info != null)
                    {
                        chk(info.Out == "url", "② 产出=url", info.Out);
                        chk(info.Tpl.Contains("{0}"), "② 模板含 {0}", info.Tpl);
                        chk(!info.IsFormB, "② 识别为模板型（有 Tpl）");
                    }
                }

                // ---- ③ 打开路径 ----
                var s3 = new PluginTemplate.Spec
                {
                    Kind = PluginTemplate.Kind.OpenPath,
                    Name = "测试-打开路径",
                    Match = @"^[A-Za-z]:\\",
                    Whole = false,
                    Prio = 120
                };
                string p3 = PluginTemplate.Write(testDir, s3, out err);
                chk(!string.IsNullOrEmpty(p3), "③ 路径模板生成成功", err);
                if (!string.IsNullOrEmpty(p3))
                {
                    var sc = new PluginScanner(root);
                    var info = sc.ReadMeta(p3);
                    chk(info != null && info.Out == "open", "③ 产出=open",
                        info == null ? "解析失败" : info.Out);
                }

                // ---- ④ 空壳 ----
                var s4 = new PluginTemplate.Spec
                {
                    Kind = PluginTemplate.Kind.Custom,
                    Name = "测试-空壳",
                    Match = @"^custom:",
                    Whole = false
                };
                string p4 = PluginTemplate.Write(testDir, s4, out err);
                chk(!string.IsNullOrEmpty(p4), "④ 空壳模板生成成功", err);

                // ---- 重名保护 ----
                string p4b = PluginTemplate.Write(testDir, s4, out err);
                chk(string.IsNullOrEmpty(p4b) && err.Contains("已存在"),
                    "重名保护：同名不覆盖", err);

                // ---- 匹配式为空应被拒 ----
                var s5 = new PluginTemplate.Spec { Name = "x", Match = "" };
                string p5 = PluginTemplate.Write(testDir, s5, out err);
                chk(string.IsNullOrEmpty(p5), "空匹配式被拒绝", err);

                // ---- 列出生成的文件 ----
                sb.AppendLine();
                sb.AppendLine("生成的文件：");
                foreach (var f in Directory.GetFiles(testDir, "*.ahk"))
                    sb.AppendLine("  " + Path.GetFileName(f) + "  (" + new FileInfo(f).Length + " B)");

                // 保留产物供 AHK 语法校验（不在自检里删）
                sb.AppendLine();
                sb.AppendLine("产物目录（供语法校验）：" + testDir);
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex.Message + "\n" + ex.StackTrace);
                fail++;
            }

            sb.AppendLine();
            sb.AppendLine("=== 结果: 通过 " + pass + " / 失败 " + fail + " ===");
            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
            return fail > 0 ? 1 : 0;
        }
    }
}
