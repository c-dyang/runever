using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RunEverCs.Services
{
    /// <summary>
    /// 图标磁盘缓存（2026-11-07）。
    ///
    /// 为什么要它：实测冷启动抽 22 个真图标要 **125ms**（`ExtractIconExW` + DIB 转换），
    /// 命中内存缓存后是 0ms。托盘常驻模式下这 125ms 只在启动后第一次弹菜单付一次；
    /// 但 `--show-menu` 一次性模式每次都是新进程 ⇒ 每次都付。
    /// 落盘后冷启动只需读 22 个小 PNG（约 20-30ms）。
    ///
    /// ★ 键的选取（比 RunAny 优越的地方）：
    ///   RunAny 按 `程序名_ico.png` 存 ⇒ **程序升级换图标后缓存不会更新**。
    ///   这里用 `sha1(exe全路径)_<exe的mtime>.png`：
    ///     · 程序升级 ⇒ mtime 变 ⇒ 键变 ⇒ 自动重抽；
    ///     · 程序搬家 ⇒ 路径变 ⇒ 键变 ⇒ 不会读到别人的图标。
    ///
    /// 边界：任何 IO 异常一律吞掉并回落"内存直取"，缓存损坏绝不能影响菜单显示。
    /// </summary>
    internal static class IconDiskCache
    {
        private static string _dir;
        private static bool _dirTried;
        private static readonly object Gate = new object();

        public static void Init(string root)
        {
            _dirTried = true;
            try { _dir = Path.Combine(root, "state", "icons"); } catch { _dir = null; }
        }

        private static string Dir
        {
            get
            {
                if (!_dirTried) return null;
                if (string.IsNullOrEmpty(_dir)) return null;
                try { if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir); }
                catch { return null; }
                return _dir;
            }
        }

        /// <summary>计算缓存键；exe 路径无效或文件不存在时返回 null（表示不该缓存）。</summary>
        private static string KeyFor(string exePath)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath)) return null;
                string p = exePath;
                // 支持 "文件,索引" 形态（ExtractIconExW 的写法）—— 索引不参与 mtime
                int comma = p.LastIndexOf(',');
                if (comma > 2 && comma == p.Length - 2) p = p.Substring(0, comma);
                if (!File.Exists(p)) return null;

                long ticks = File.GetLastWriteTimeUtc(p).Ticks;
                using (var sha = SHA1.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(p.ToLowerInvariant()));
                    var sb = new StringBuilder(16);
                    for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                    return sb + "_" + ticks.ToString() + ".png";
                }
            }
            catch { return null; }
        }

        /// <summary>尝试从磁盘读图标；失败返回 null（调用方继续走提取路径）。</summary>
        public static ImageSource TryLoad(string exePath)
        {
            string dir = Dir;
            if (dir == null) return null;
            string key = KeyFor(exePath);
            if (key == null) return null;

            string file = Path.Combine(dir, key);
            try
            {
                if (!File.Exists(file)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 16;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>把已提取的图标写到磁盘（尽力而为，失败静默）。</summary>
        public static void Store(string exePath, BitmapSource icon)
        {
            if (icon == null) return;
            string dir = Dir;
            if (dir == null) return;
            string key = KeyFor(exePath);
            if (key == null) return;

            string file = Path.Combine(dir, key);
            string tmp = file + ".tmp";
            try
            {
                if (File.Exists(file)) return;          // 已有人写过同一个键
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(icon));
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    enc.Save(fs);
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        /// <summary>清理过期缓存（键里带 mtime，旧版本永远不会再被命中）。</summary>
        public static int Prune(int keepDays = 30)
        {
            string dir = Dir;
            if (dir == null) return 0;
            int removed = 0;
            try
            {
                var limit = DateTime.UtcNow.AddDays(-keepDays);
                foreach (var f in Directory.GetFiles(dir, "*.png"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) < limit) { File.Delete(f); removed++; }
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }
    }
}
