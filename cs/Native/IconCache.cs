using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 注意：本文件里有两套互不相干的缓存
//   · IconDiskCache —— 本地 exe/ico 的**抽图**结果（state\icons\…）
//   · UrlIconCache   —— 网页 favicon 的**下载**结果（cache\icons\…，U-C 新增）
// 不要合并：键的来源不同（文件 mtime vs URL），失效语义也不同。

namespace RunEverCs.Native
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
        private static string _root;
        private static bool _dirTried;
        private static readonly object Gate = new object();

        /// <summary>初始化时传入的程序根目录（URL 图标缓存在此之下定位 `cache\icons`）。</summary>
        public static string RootDir { get { return _root; } }

        public static void Init(string root)
        {
            _dirTried = true;
            _root = root;
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

    /// <summary>
    /// URL 图标（网页 favicon）磁盘缓存 —— 2026-11-09 · U-C。
    ///
    /// 落盘三件套（同一 hash 前缀，都在 `cache\icons\`）：
    ///   · `&lt;hash&gt;.png` —— 口径要求的缓存物（sha256(url) 前 16 位小写 hex）
    ///   · `&lt;hash&gt;.ico` —— **渲染用**。菜单位图走 `ExtractIconExW`（NativeMenuWin32.ExtractHBitmap），
    ///                      它读 exe/dll/ico，**不读 PNG**；只回 PNG 路径会拿到系统的"图片文件"通用图标。
    ///   · `&lt;hash&gt;.txt` —— 原始 URL + ETag / Last-Modified（失效判断；也是"缓存被清后找回 URL"的唯一线索）
    ///
    /// 边界：任何 IO / 解码异常一律吞掉并返回失败；缓存损坏绝不能影响菜单显示。
    /// </summary>
    internal static class UrlIconCache
    {
        /// <summary>取图总超时（ms）。★ 硬约束 ≤ 3000：用户口径是"宁可只显示兜底图标"。</summary>
        public const int FetchTimeoutMs = 3000;

        /// <summary>响应体上限 —— 防止一个巨大的响应把后台线程与内存拖死（favicon 正常 ≤ 100KB）。</summary>
        public const int MaxBytes = 2 * 1024 * 1024;

        /// <summary>缓存键长度：SHA256 前 16 位（= 前 8 字节的小写 hex）。</summary>
        public const int HashHexLen = 16;

        /// <summary>ICO 里的渲染尺寸。菜单用 SM_CXSMICON(16)，存 32 便于高 DPI 下缩。</summary>
        private const int RenderSide = 32;

        private static string _root;
        private static string _dir;
        private static bool _dirReady;
        private static readonly object Gate = new object();
        private static string _lastLog;

        /// <summary>最近一条日志（自检断言"拒绝留了一行"用，不依赖日志文件是否可写）。</summary>
        public static string LastLogLine { get { return _lastLog; } }

        public static void Init(string root)
        {
            if (string.IsNullOrEmpty(root)) return;
            _root = root;
            try { _dir = Path.Combine(root, "cache", "icons"); } catch { _dir = null; }
            _dirReady = false;
        }

        /// <summary>缓存目录（惰性创建；不可用时返回 null ⇒ 全部降级为"不缓存"）。</summary>
        public static string Dir
        {
            get
            {
                if (string.IsNullOrEmpty(_dir))
                {
                    string root = _root;
                    if (string.IsNullOrEmpty(root)) root = IconDiskCache.RootDir;
                    if (string.IsNullOrEmpty(root)) return null;
                    try { _dir = Path.Combine(root, "cache", "icons"); } catch { return null; }
                }
                if (_dirReady) return _dir;
                try
                {
                    if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
                    _dirReady = true;
                }
                catch { return null; }
                return _dir;
            }
        }

        /// <summary>URL → 缓存键：SHA256 前 16 位小写 hex（斜杠/问号不会进文件名）。</summary>
        public static string Hash(string url)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(url ?? ""));
                var sb = new StringBuilder(HashHexLen);
                for (int i = 0; i < HashHexLen / 2; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// 缓存命中（**纯文件存在性检查**：无网络、无解码、无锁）。
        /// 命中返回渲染用的 .ico 路径；未命中返回 false（调用方先给兜底图标）。
        /// </summary>
        public static bool TryGet(string url, out string icoPath)
        {
            icoPath = null;
            string d = Dir;
            if (d == null) return false;
            try
            {
                string h = Hash(url);
                string png = Path.Combine(d, h + ".png");
                string ico = Path.Combine(d, h + ".ico");
                if (!File.Exists(png) || !File.Exists(ico)) return false;
                icoPath = ico;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 从一个**图标源字符串**反查它原本的 URL。
        ///
        /// 为什么需要：`IconResolver.ResolveAll` 是就地回写 `IconSource` 的，URL 图标一旦命中缓存，
        /// 写回去的就是 `cache\icons\&lt;hash&gt;.ico`。若缓存后来被清掉，只靠路径就再也找不到原网址了。
        /// 这里用同名 sidecar（`.txt` 里的 `url=`）把它找回来 ⇒ 能自愈（重新入队取图）。
        /// 快筛：绝大多数图标源（exe 路径）在第一次字符串判断就被挡掉，不进 IO。
        /// </summary>
        public static string TryRecoverUrl(string iconSource)
        {
            if (string.IsNullOrEmpty(iconSource)) return null;
            if (iconSource.IndexOf("cache", StringComparison.OrdinalIgnoreCase) < 0) return null;
            if (iconSource.IndexOf("icons", StringComparison.OrdinalIgnoreCase) < 0) return null;

            string d = Dir;
            if (d == null) return null;
            try
            {
                string full = Path.GetFullPath(iconSource);
                // 两边都规范化：Init 传进来的 root 可能带正斜杠（`--selftest-*` 的参数就是这样），
                // GetFullPath 会把它们统一成反斜杠，直接比原串会永远不等。
                string prefix = Path.GetFullPath(d).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

                string name = Path.GetFileNameWithoutExtension(full);
                if (name == null || name.Length != HashHexLen) return null;

                string meta = Path.Combine(d, name + ".txt");
                if (!File.Exists(meta)) return null;

                foreach (string raw in File.ReadAllLines(meta, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
                    {
                        string u = line.Substring(4).Trim();
                        return u.Length > 0 ? u : null;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>读 sidecar 里的 ETag / Last-Modified（条件请求用；没有就返回 null）。</summary>
        public static void ReadMeta(string url, out string etag, out string lastModified)
        {
            etag = null;
            lastModified = null;
            string d = Dir;
            if (d == null) return;
            try
            {
                string meta = Path.Combine(d, Hash(url) + ".txt");
                if (!File.Exists(meta)) return;
                foreach (string raw in File.ReadAllLines(meta, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("etag=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring(5).Trim();
                        if (v.Length > 0) etag = v;
                    }
                    else if (line.StartsWith("last-modified=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring(14).Trim();
                        if (v.Length > 0) lastModified = v;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 把下载到的字节写进缓存（解码 → PNG + ICO + sidecar）。
        /// 只有**能解码的图片**才会落盘；非图片/损坏图片返回 false（调用方记日志、菜单照常显示）。
        /// </summary>
        public static bool Store(string url, byte[] bytes, string etag, string lastModified, out string error)
        {
            error = null;
            string d = Dir;
            if (d == null) { error = "缓存目录不可用"; return false; }

            BitmapSource bmp = Decode(bytes, out error);
            if (bmp == null) return false;

            byte[] pngBytes;
            byte[] icoBytes;
            try
            {
                pngBytes = EncodePng(bmp);
                icoBytes = EncodeIco(bmp);
            }
            catch (Exception ex)
            {
                error = "编码失败: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            if (pngBytes == null || pngBytes.Length == 0) { error = "PNG 编码结果为空"; return false; }
            if (icoBytes == null || icoBytes.Length == 0) { error = "ICO 编码结果为空"; return false; }

            string h = Hash(url);
            string png = Path.Combine(d, h + ".png");
            string ico = Path.Combine(d, h + ".ico");
            string meta = Path.Combine(d, h + ".txt");

            lock (Gate)
            {
                // 先删 sidecar：中途失败时不会留下"三件套齐了"的假象（TryGet 要 .png + .ico）
                try { if (File.Exists(meta)) File.Delete(meta); } catch { }
                if (!WriteAtomic(png, pngBytes)) { error = "写 .png 失败"; return false; }
                if (!WriteAtomic(ico, icoBytes)) { error = "写 .ico 失败"; return false; }

                var sb = new StringBuilder();
                sb.AppendLine("url=" + url);
                sb.AppendLine("etag=" + (etag ?? ""));
                sb.AppendLine("last-modified=" + (lastModified ?? ""));
                sb.AppendLine("fetched=" + DateTime.UtcNow.ToString("o"));
                sb.AppendLine("bytes=" + bytes.Length);
                if (!WriteAtomic(meta, new UTF8Encoding(false).GetBytes(sb.ToString())))
                {
                    error = "写 .txt 失败";
                    return false;
                }
            }
            return true;
        }

        /// <summary>日志一行（拒绝/取图失败）。落 `state\iconurl.log`，写不了也不影响主流程。</summary>
        public static void Log(string msg)
        {
            _lastLog = msg;
            try
            {
                string root = _root;
                if (string.IsNullOrEmpty(root)) root = IconDiskCache.RootDir;
                if (string.IsNullOrEmpty(root)) return;

                string dir = Path.Combine(root, "state");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "iconurl.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        // ------------------------------------------------------------
        // 内部：解码 / 编码 / 写盘
        // ------------------------------------------------------------

        /// <summary>文件头判据 —— 比 Content-Type 可信（很多站点对 favicon 给 octet-stream）。</summary>
        private static bool LooksLikeImage(byte[] b)
        {
            if (b == null || b.Length < 8) return false;
            // PNG
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
            // JPEG
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
            // GIF
            if (b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') return true;
            // BMP
            if (b[0] == (byte)'B' && b[1] == (byte)'M') return true;
            // ICO / CUR
            if (b[0] == 0 && b[1] == 0 && (b[2] == 1 || b[2] == 2) && b[3] == 0) return true;
            // WEBP（RIFF....WEBP）
            if (b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
                && b.Length >= 12 && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
                return true;
            return false;
        }

        private static BitmapSource Decode(byte[] bytes, out string error)
        {
            error = null;
            if (!LooksLikeImage(bytes))
            {
                error = "不是图片（文件头不匹配 png/jpeg/gif/bmp/ico）";
                return null;
            }

            try
            {
                using (var ms = new MemoryStream(bytes, false))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                    bi.StreamSource = ms;
                    bi.EndInit();
                    bi.Freeze();

                    if (bi.PixelWidth <= 0 || bi.PixelHeight <= 0) { error = "图片尺寸为 0"; return null; }

                    int side = Math.Max(bi.PixelWidth, bi.PixelHeight);
                    if (side <= RenderSide) return bi;

                    double scale = (double)RenderSide / side;
                    var tb = new TransformedBitmap(bi, new ScaleTransform(scale, scale));
                    tb.Freeze();
                    return tb;
                }
            }
            catch (Exception ex)
            {
                error = "图片解码失败: " + ex.GetType().Name;
                return null;
            }
        }

        private static byte[] EncodePng(BitmapSource bmp)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// PNG 帧 → ICO 容器。菜单位图只认 ICO（ExtractIconExW），所以这一步是"能看见"的前提。
        ///
        /// 手写容器而不是找编码器：WPF **只有 IconBitmapDecoder，没有 ICO 编码器**
        /// （`IconBitmapEncoder` 在 .NET Framework 里不存在），引第三方包又违反零依赖。
        /// 结构 = ICONDIR + ICONDIRENTRY + BITMAPINFOHEADER + 32bpp XOR 位图（自下而上）+ AND 掩码。
        /// </summary>
        private static byte[] EncodeIco(BitmapSource bmp)
        {
            BitmapSource src = bmp;
            if (src.Format != PixelFormats.Bgra32)
            {
                var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                conv.Freeze();
                src = conv;
            }

            int w = src.PixelWidth;
            int h = src.PixelHeight;
            if (w <= 0 || h <= 0 || w > 256 || h > 256)
                throw new InvalidOperationException("ICO 尺寸非法: " + w + "x" + h);

            int stride = w * 4;
            var xor = new byte[stride * h];
            src.CopyPixels(xor, stride, 0);

            int maskRow = ((w + 31) / 32) * 4;      // 每行 1bpp，4 字节对齐
            int maskSize = maskRow * h;
            int imageSize = 40 + xor.Length + maskSize;
            var ico = new byte[6 + 16 + imageSize];

            int p = 0;
            PutU16(ico, ref p, 0);                  // ICONDIR.reserved
            PutU16(ico, ref p, 1);                  // ICONDIR.type = icon
            PutU16(ico, ref p, 1);                  // ICONDIR.count

            ico[p++] = (byte)(w >= 256 ? 0 : w);    // 0 表示 256
            ico[p++] = (byte)(h >= 256 ? 0 : h);
            ico[p++] = 0;                           // colorCount
            ico[p++] = 0;                           // reserved
            PutU16(ico, ref p, 1);                  // planes
            PutU16(ico, ref p, 32);                 // bitCount
            PutU32(ico, ref p, (uint)imageSize);
            PutU32(ico, ref p, 22);                 // 图像数据偏移

            PutU32(ico, ref p, 40);                 // BITMAPINFOHEADER.biSize
            PutU32(ico, ref p, (uint)w);
            PutU32(ico, ref p, (uint)(h * 2));      // height = XOR + AND
            PutU16(ico, ref p, 1);                  // planes
            PutU16(ico, ref p, 32);                 // bitCount
            PutU32(ico, ref p, 0);                  // BI_RGB
            PutU32(ico, ref p, (uint)(xor.Length + maskSize));
            PutU32(ico, ref p, 0);
            PutU32(ico, ref p, 0);
            PutU32(ico, ref p, 0);
            PutU32(ico, ref p, 0);

            // XOR 位图：DIB 是自下而上
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(xor, (h - 1 - y) * stride, ico, p + y * stride, stride);
            // AND 掩码：全 0（32bpp 的真实透明度由 alpha 通道给）
            return ico;
        }

        private static void PutU16(byte[] b, ref int p, int v)
        {
            b[p++] = (byte)(v & 0xFF);
            b[p++] = (byte)((v >> 8) & 0xFF);
        }

        private static void PutU32(byte[] b, ref int p, uint v)
        {
            b[p++] = (byte)(v & 0xFF);
            b[p++] = (byte)((v >> 8) & 0xFF);
            b[p++] = (byte)((v >> 16) & 0xFF);
            b[p++] = (byte)((v >> 24) & 0xFF);
        }

        private static bool WriteAtomic(string file, byte[] data)
        {
            string tmp = file + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, data);
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
                return true;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }
    }
}
