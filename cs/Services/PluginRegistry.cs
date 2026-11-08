using System;
using System.IO;
using System.Text;

namespace RunEverCs.Services
{
    /// <summary>
    /// 插件登记文件（`state\plugins.json`）—— **格式与 AHK 版严格一致**。
    ///
    /// 为什么必须严格一致：AHK 与 C# 双轨共存，两边写的**是同一个路径**
    ///   （AHK `AutoMatcher.WriteRegistry` → `state\plugins.json`）；
    ///   格式不同 = 两边互相覆盖成对方读不懂的内容（守门 AC-1 打回项）。
    ///
    /// 契约来源：`src\AutoMatcher.ahk:342-401 WriteRegistry`，字段与顺序：
    ///   generatedAt / pluginDir / enabled / minLen / cacheSeconds / count /
    ///   plugins[ file,name,match,whole,prio,out,tpl,icon,menu,arg,show,enabled,path ] /
    ///   badPlugins[ file,reason ]
    /// ★ `show` 是**整数三态**（-1 继承 / 0 隐藏 / 1 显示），不是字符串
    ///   —— AHK 注释写明「UI 要区分继承」。
    ///
    /// 写盘方式：原子（`.tmp` → 替换），避免双轨同时写时读到半截 JSON。
    /// </summary>
    public static class PluginRegistry
    {
        /// <summary>默认路径：`<root>\state\plugins.json`。</summary>
        public static string DefaultPath(string root)
        {
            return Path.Combine(root, "state", "plugins.json");
        }

        /// <summary>按 AHK 契约写出插件登记；失败返回 false（调用方决定怎么提示）。</summary>
        public static bool Write(AutoMatcher am, string path, Action<string> log = null)
        {
            if (am == null || string.IsNullOrEmpty(path)) return false;

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"generatedAt\": \"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\",");
                sb.AppendLine("  \"pluginDir\": \"" + Js(am.Dir) + "\",");
                sb.AppendLine("  \"enabled\": " + (am.Conf.Enabled ? "true" : "false") + ",");
                sb.AppendLine("  \"minLen\": " + am.Conf.MinLen + ",");
                sb.AppendLine("  \"cacheSeconds\": " + am.Conf.CacheSec + ",");
                sb.AppendLine("  \"count\": " + am.Plugins.Count + ",");
                sb.AppendLine("  \"plugins\": [");

                for (int i = 0; i < am.Plugins.Count; i++)
                {
                    var p = am.Plugins[i];
                    sb.Append("    {");
                    sb.Append("\"file\": \"" + Js(p.File) + "\", ");
                    sb.Append("\"name\": \"" + Js(p.Name) + "\", ");
                    sb.Append("\"match\": \"" + Js(p.Match) + "\", ");
                    sb.Append("\"whole\": " + (p.Whole ? "true" : "false") + ", ");
                    sb.Append("\"prio\": " + p.Prio + ", ");
                    sb.Append("\"out\": \"" + Js(p.Out) + "\", ");
                    sb.Append("\"tpl\": \"" + Js(p.Tpl) + "\", ");
                    sb.Append("\"icon\": \"" + Js(p.Icon) + "\", ");
                    sb.Append("\"menu\": " + (p.Menu ? "true" : "false") + ", ");
                    sb.Append("\"arg\": \"" + Js(p.Arg) + "\", ");
                    sb.Append("\"show\": " + p.Show + ", ");
                    sb.Append("\"enabled\": " + (p.Enabled ? "true" : "false") + ", ");
                    sb.Append("\"path\": \"" + Js(p.Path) + "\"");
                    sb.Append("}");
                    if (i < am.Plugins.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }

                sb.AppendLine("  ],");
                sb.AppendLine("  \"badPlugins\": [");
                for (int i = 0; i < am.BadPlugins.Count; i++)
                {
                    var b = am.BadPlugins[i];
                    sb.Append("    {\"file\": \"" + Js(b.File) + "\", \"reason\": \""
                        + Js(b.BadReason) + "\"}");
                    if (i < am.BadPlugins.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }
                sb.AppendLine("  ]");
                sb.AppendLine("}");

                string tmp = path + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);

                if (log != null) log("插件登记已写出: " + path);
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("⚠ 写插件登记失败: " + ex.Message);
                return false;
            }
        }

        private static string Js(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }
}
