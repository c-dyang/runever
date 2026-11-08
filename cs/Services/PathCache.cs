using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RunEverCs.Services
{
    /// <summary>
    /// 程序路径缓存 —— 对应 AHK PathCache.ahk。
    ///
    /// 格式（cache\paths.txt）：每行 "程序名=全路径"
    /// 用途：run 类型条目的图标解析 + 执行器查路径。
    ///
    /// ★ 线程安全（v8 新增）：运行实例在**热键路径**里读它、后台解析线程写它
    ///   （时机①/②/③ 就地回灌）。所有 `_map` 访问都在 `_sync` 下 —— 读是纯内存
    ///   字典查找（无等待、无 IO），不违反 I-5「热键路径禁阻塞」。
    /// </summary>
    public class PathCache
    {
        private readonly string _path;
        private readonly object _sync = new object();
        private readonly Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public PathCache(string path)
        {
            _path = path;
            Load();
        }

        public int Count { get { lock (_sync) { return _map.Count; } } }

        /// <summary>从盘上重载（就地回灌：不清空引用，图标解析器 / 执行器自动看到新内容）。</summary>
        public void Reload()
        {
            Load();
        }

        public void Load()
        {
            var fresh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(_path))
            {
                lock (_sync) { _map.Clear(); }
                return;
            }

            try
            {
                string text = File.ReadAllText(_path, Encoding.UTF8);
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim(' ', '\t', '\r');
                    if (line.Length == 0) continue;
                    if (line[0] == '#' || line[0] == ';') continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    if (k.Length > 0 && v.Length > 0)
                        fresh[k] = v;
                }
            }
            catch { return; }

            // ★ 原子替换：读失败的半成品不会污染在用映射
            lock (_sync)
            {
                _map.Clear();
                foreach (var kv in fresh) _map[kv.Key] = kv.Value;
            }
        }

        /// <summary>查程序全路径；未命中返回空串。</summary>
        public string Get(string progName)
        {
            if (string.IsNullOrEmpty(progName)) return "";
            lock (_sync)
            {
                string v;
                return _map.TryGetValue(progName, out v) ? v : "";
            }
        }

        /// <summary>
        /// 移除单个条目（2026-11-07 · t041875 后半截）。
        ///
        /// 场景：一个程序卸载了 / 改名了，解析必然失败。旧实现在失败分支只记 stats、
        /// **不动缓存** ⇒ 陈旧路径一直留着，两个后果：
        ///   ① `hideMissing` 判定"本机有"，菜单照常显示，点下去执行失败；
        ///   ② 增量分支因"缓存命中且文件仍存在"（旧路径文件还没删时）直接跳过，
        ///      永远不再重试解析。
        /// 这里提供删除入口，由 PathCacheBuilder 的失败分支调用。
        /// </summary>
        public bool Remove(string progName)
        {
            if (string.IsNullOrEmpty(progName)) return false;
            lock (_sync) { return _map.Remove(progName); }
        }

        public void Set(string progName, string fullPath)
        {
            if (string.IsNullOrEmpty(progName)) return;
            lock (_sync) { _map[progName] = fullPath; }
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("# RunEver 路径缓存（程序名=全路径）");
                sb.AppendLine("# 由程序自动维护；删除本文件会触发重建");
                lock (_sync)
                {
                    foreach (var kv in _map)
                        sb.AppendLine(kv.Key + "=" + kv.Value);
                }

                string tmp = _path + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch { }
        }

        /// <summary>从 PATH 环境变量 + App Paths 注册表解析程序名。</summary>
        public static string ResolveProgram(string progName)
        {
            if (string.IsNullOrEmpty(progName)) return "";

            // ① 已含路径
            if (progName.IndexOf('\\') >= 0 || progName.IndexOf('/') >= 0)
                return File.Exists(progName) ? progName : "";

            // ② 补 .exe 后在 PATH 里找
            string name = progName;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name += ".exe";

            try
            {
                string[] paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
                foreach (var d in paths)
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    try
                    {
                        string full = Path.Combine(d.Trim(), name);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }
            catch { }

            // ③ App Paths 注册表（HKLM + HKCU）
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    using (var k = hive.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name))
                    {
                        if (k != null)
                        {
                            string v = k.GetValue("") as string;
                            if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                        }
                    }
                }
                catch { }
            }

            return "";
        }
    }
}
