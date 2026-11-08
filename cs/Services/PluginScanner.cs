using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RunEverCs.Services
{
    /// <summary>
    /// 插件元数据 —— 对应 AHK AutoMatcher 的插件条目。
    /// </summary>
    public class PluginInfo
    {
        public string File = "";
        public string Path = "";
        public string Name = "";
        public string Match = "";
        public bool Whole = true;
        public int Prio = 100;
        public string Out = "";
        public string Tpl = "";
        public string Icon = "";
        public bool Menu;
        public string Arg = "text";

        /// <summary>显示执行窗口：-1 继承 / 0 隐藏 / 1 显示。</summary>
        public int Show = -1;

        public bool Enabled = true;

        /// <summary>元数据非法时的原因（非空表示加载失败）。</summary>
        public string BadReason = "";

        public bool IsFormB { get { return string.IsNullOrEmpty(Tpl); } }

        public string ShowText
        {
            get
            {
                if (Show < 0) return "继承";
                return Show == 1 ? "显示" : "隐藏";
            }
        }
    }

    /// <summary>
    /// 插件扫描器 —— 对应 AHK AutoMatcher.ReadMeta / Scan。
    ///
    /// 元数据规范（写在 .ahk 头部）：
    ///   global RVE_FN_Name  := "顺丰快递"
    ///   global RVE_FN_Match := "^SF\d{10,}$"
    ///   global RVE_FN_Whole := true
    ///   global RVE_FN_Prio  := 100
    ///   global RVE_FN_Out   := "url"
    ///   global RVE_FN_Tpl   := "https://...{0}"
    ///   global RVE_FN_Icon  := "chrome.exe"
    ///   global RVE_FN_Show  := true
    ///   global RVE_FN_Enabled := true
    /// </summary>
    public class PluginScanner
    {
        public static readonly string[] OutTypes = { "run", "open", "url", "action", "send", "keys" };

        public List<PluginInfo> Plugins = new List<PluginInfo>();
        public List<PluginInfo> BadPlugins = new List<PluginInfo>();

        private readonly string _dir;

        public PluginScanner(string root)
        {
            _dir = Path.Combine(root, "config", "functions");
        }

        public string Dir { get { return _dir; } }

        /// <summary>扫描目录，按优先级降序（同优先级按文件名升序）。</summary>
        public void Scan()
        {
            Plugins.Clear();
            BadPlugins.Clear();

            if (!Directory.Exists(_dir)) return;

            var files = new List<string>(Directory.GetFiles(_dir, "*.ahk"));
            files.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var f in files)
            {
                var p = ReadMeta(f);
                if (p == null) continue;

                if (!string.IsNullOrEmpty(p.BadReason))
                    BadPlugins.Add(p);
                else
                    Plugins.Add(p);
            }

            // 稳定排序：Prio 降序 → 文件名升序
            Plugins.Sort((a, b) =>
            {
                int c = b.Prio.CompareTo(a.Prio);
                if (c != 0) return c;
                return string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>读单个插件文件的元数据（只解析头部 80 行）。</summary>
        public PluginInfo ReadMeta(string path)
        {
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch
            {
                return null;
            }

            var info = new PluginInfo
            {
                File = Path.GetFileName(path),
                Path = path
            };

            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int limit = Math.Min(80, lines.Length);

            for (int i = 0; i < limit; i++)
            {
                var m = Regex.Match(lines[i],
                    @"^\s*global\s+RVE_FN_(\w+)\s*:=\s*(.+)$",
                    RegexOptions.IgnoreCase);
                if (!m.Success) continue;

                string key = m.Groups[1].Value.ToLowerInvariant();
                string raw = StripInlineComment(m.Groups[2].Value).Trim();
                raw = Unquote(raw);

                switch (key)
                {
                    case "name": info.Name = raw; break;
                    case "match": info.Match = raw; break;
                    case "tpl": info.Tpl = raw; break;
                    case "out": info.Out = raw.ToLowerInvariant(); break;
                    case "icon": info.Icon = raw; break;
                    case "arg": info.Arg = raw.ToLowerInvariant(); break;
                    case "whole": info.Whole = !(raw.ToLowerInvariant() == "false" || raw == "0"); break;
                    case "menu": info.Menu = (raw.ToLowerInvariant() == "true" || raw == "1"); break;
                    case "prio":
                        int pr;
                        info.Prio = int.TryParse(raw, out pr) ? pr : 100;
                        break;
                    case "show":
                        info.Show = (raw.ToLowerInvariant() == "true" || raw == "1") ? 1 : 0;
                        break;
                    case "enabled":
                        info.Enabled = !(raw.ToLowerInvariant() == "false" || raw == "0");
                        break;
                }
            }

            if (string.IsNullOrEmpty(info.Name) || string.IsNullOrEmpty(info.Match)
                || string.IsNullOrEmpty(info.Out))
            {
                // 不是插件文件（缺必需元数据）→ 静默跳过，不算错误
                return null;
            }

            bool outOk = false;
            foreach (var t in OutTypes)
                if (info.Out == t) { outOk = true; break; }

            if (!outOk)
                info.BadReason = "未知产出形态 out=" + info.Out
                    + "（合法值: " + string.Join("/", OutTypes) + "）";

            return info;
        }

        /// <summary>剥掉引号外的行内注释（; 或 #）。</summary>
        public static string StripInlineComment(string s)
        {
            bool inQuote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') inQuote = !inQuote;
                else if (!inQuote && (c == ';' || c == '#'))
                    return s.Substring(0, i);
            }
            return s;
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2);
            return s;
        }
    }
}
