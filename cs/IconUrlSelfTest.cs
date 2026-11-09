using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RunEverCs.Models;
using RunEverCs.Native;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// URL 图标（网页 favicon）自检 —— U-C，2026-11-09。
    ///
    /// 用法：RunEverCs.exe --selftest-iconurl &lt;root&gt; &lt;out.txt&gt;
    ///   口径同 --selftest-arch：args[1]=root、args[2]=out.txt，**禁带 --root**。
    ///
    /// ★ 全部断言**离线可跑**：把 IconResolver.BackgroundFetchEnabled 关掉，
    ///   后台队列只记账、不起线程 ⇒ 不会真的联网（CI / 无网机器上必须也绿）。
    ///   所有落盘都在 `state\cbUC\iconurl-sandbox`，绝不碰真实 `cache\icons`。
    /// </summary>
    public static class IconUrlSelfTest
    {
        private const int FetchTimeoutLimitMs = 3000;

        private static readonly StringBuilder Sb = new StringBuilder();
        private static int _total;
        private static int _failed;

        public static int Run(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("用法: RunEverCs.exe --selftest-iconurl <root> <out.txt>");
                return 2;
            }

            string root = args[1];
            string outFile = args[2];

            Sb.Length = 0;
            _total = 0;
            _failed = 0;

            Header(root);

            // 离线铁律：先关后台取图，再做任何可能触发入队的断言
            IconResolver.BackgroundFetchEnabled = false;

            string sandbox = Path.Combine(root, "state", "cbUC", "iconurl-sandbox");
            string cacheDir = Path.Combine(sandbox, "cache", "icons");

            try
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
                Directory.CreateDirectory(cacheDir);
            }
            catch (Exception ex)
            {
                Line("[FAIL] 环境 沙箱目录不可用: " + sandbox + " → " + ex.GetType().Name + ": " + ex.Message);
                _total++;
                _failed++;
            }
            UrlIconCache.Init(sandbox);

            var ir = new IconResolver(null);   // 本套不测 run 类型的路径解析 ⇒ 不需要 PathCache

            // ------------------------------------------------------------
            // A1 协议白名单：只 http/https 算 URL 图标
            // ------------------------------------------------------------
            Check("A1 协议白名单 file: 不算 URL 图标",
                !IconResolver.IsUrlIcon("file:///C:/icons/a.ico"),
                "file:///C:/icons/a.ico");
            Check("A1 协议白名单 相对路径 不算 URL 图标",
                !IconResolver.IsUrlIcon("icons/a.png"),
                "icons/a.png");
            Check("A1 协议白名单 ftp: 不算 URL 图标",
                !IconResolver.IsUrlIcon("ftp://example.com/a.ico"),
                "ftp://example.com/a.ico");
            Check("A1 协议白名单 http 与 https **都**认（大小写不敏感）",
                IconResolver.IsUrlIcon("http://example.com/a.png")
                && IconResolver.IsUrlIcon("https://example.com/a.png")
                && IconResolver.IsUrlIcon("HTTPS://EXAMPLE.COM/a.png"),
                "http/https/HTTPS");

            var bad = new MenuItem { Type = ActionType.Url, Target = "x", IconSource = "file:///C:/icons/a.ico" };
            string badResolved = ir.Resolve(bad);
            Check("A1 非白名单协议被拒绝 ⇒ 返回空（走兜底图标，不抛）",
                badResolved == "",
                "resolved=[" + badResolved + "]");
            Check("A1 拒绝留了日志一行（含「拒绝」，不弹窗）",
                (UrlIconCache.LastLogLine ?? "").IndexOf("拒绝", StringComparison.Ordinal) >= 0,
                "log=[" + (UrlIconCache.LastLogLine ?? "") + "]");

            // ------------------------------------------------------------
            // A2 缓存命中：零网络
            // ------------------------------------------------------------
            string hitUrl = "https://example.com/favicon.ico";
            byte[] png = MakePng(16);
            string err;
            bool stored = UrlIconCache.Store(hitUrl, png, "\"etag-uc-1\"", "Mon, 09 Nov 2026 00:00:00 GMT", out err);
            Check("A2 预置缓存写盘成功（PNG+ICO+sidecar）", stored, err ?? "");

            string hash = UrlIconCache.Hash(hitUrl);
            string pngPath = Path.Combine(cacheDir, hash + ".png");
            string icoPath = Path.Combine(cacheDir, hash + ".ico");
            Check("A2 缓存文件名 = sha256(url) 前 16 位（" + hash + ".png）",
                File.Exists(pngPath), pngPath);
            Check("A2 渲染用的 .ico 与 .png 同键", File.Exists(icoPath), icoPath);

            int q0 = IconResolver.EnqueuedCount;
            var hitItem = new MenuItem { Type = ActionType.Url, Target = hitUrl, IconSource = hitUrl };
            string hitResolved = ir.Resolve(hitItem);
            Check("A2 命中 ⇒ 直接返回缓存图标路径（File.Exists）",
                hitResolved.Length > 0 && File.Exists(hitResolved),
                "resolved=[" + hitResolved + "]");
            Check("A2 命中 ⇒ **零网络**（未排入取图队列）",
                IconResolver.EnqueuedCount == q0,
                "enqueued " + q0 + " → " + IconResolver.EnqueuedCount);

            // ------------------------------------------------------------
            // A3 未命中：当次兜底、不抛、不阻塞，后台排队
            // ------------------------------------------------------------
            string missUrl = "https://no-such-host.invalid/favicon.ico";
            int q1 = IconResolver.EnqueuedCount;
            var missItem = new MenuItem { Type = ActionType.Url, Target = missUrl, IconSource = missUrl };

            bool threw = false;
            string missResolved = null;
            long t0 = Stopwatch.GetTimestamp();
            try { missResolved = ir.Resolve(missItem); }
            catch { threw = true; }
            double elapsedMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

            Check("A3 未命中不抛异常", !threw, threw ? "抛了" : "ok");
            Check("A3 未命中当次返回空 ⇒ 渲染层用兜底图标",
                missResolved == "", "resolved=[" + (missResolved ?? "null") + "]");
            Check("A3 未命中排入后台取图队列",
                IconResolver.EnqueuedCount == q1 + 1,
                "enqueued " + q1 + " → " + IconResolver.EnqueuedCount);
            Check("A3 未命中不阻塞（耗时 < 50ms，实测 " + elapsedMs.ToString("F2") + "ms）",
                elapsedMs < 50.0, elapsedMs.ToString("F2") + "ms");

            var groups = new List<MenuGroup>
            {
                new MenuGroup { Name = "URL", Type = GroupType.Always, Items = new List<MenuItem> { missItem } }
            };
            ir.ResolveAll(groups);
            Check("A3 ResolveAll 未命中时保住 URL（下一次弹菜单才能命中缓存）",
                missItem.IconSource == missUrl, "IconSource=[" + missItem.IconSource + "]");

            // ------------------------------------------------------------
            // A4 超时硬约束 ≤ 3s
            // ------------------------------------------------------------
            Check("A4 取图超时常量 ≤ 3000ms（实测 " + IconResolver.UrlFetchTimeoutMs + "ms）",
                IconResolver.UrlFetchTimeoutMs > 0 && IconResolver.UrlFetchTimeoutMs <= FetchTimeoutLimitMs,
                IconResolver.UrlFetchTimeoutMs + "ms");
            Check("A4 超时常量与缓存层同一来源（无第二份常量）",
                IconResolver.UrlFetchTimeoutMs == UrlIconCache.FetchTimeoutMs,
                IconResolver.UrlFetchTimeoutMs + " / " + UrlIconCache.FetchTimeoutMs);

            // ------------------------------------------------------------
            // A5 缓存键 = SHA256 前 16 位小写 hex（稳定 / 不撞 / 不含路径字符）
            // ------------------------------------------------------------
            string h1 = UrlIconCache.Hash("https://a.example/x");
            string h2 = UrlIconCache.Hash("https://b.example/x");
            string h1b = UrlIconCache.Hash("https://a.example/x");
            Check("A5 同一 URL 稳定", h1 == h1b, h1 + " / " + h1b);
            Check("A5 不同 URL 不撞", h1 != h2, h1 + " / " + h2);
            Check("A5 长度 16 且全小写 hex", h1.Length == 16 && IsLowerHex(h1), h1);
            Check("A5 不含路径分隔符 / 问号 / 冒号（斜杠 URL 不会炸路径）",
                h1.IndexOfAny(new[] { '/', '\\', '?', ':', '*' }) < 0, h1);

            // ------------------------------------------------------------
            // A6 缓存 .ico 能被菜单位图链路读出（ExtractIconExW）
            //   没有这条，"缓存命中"只是字符串对了、图标其实显示不出来
            // ------------------------------------------------------------
            Check("A6 缓存 .ico 可被 ExtractIconExW 读取（菜单位图链路真实可用）",
                IcoReadable(icoPath), icoPath);

            // ------------------------------------------------------------
            // A7 缓存被清 ⇒ 从 sidecar 找回 URL 并重新入队（自愈）
            // ------------------------------------------------------------
            ir.ResolveAll(new List<MenuGroup>
            {
                new MenuGroup { Name = "URL", Type = GroupType.Always, Items = new List<MenuItem> { hitItem } }
            });
            Check("A7 命中后 IconSource 写回缓存路径", hitItem.IconSource == icoPath, hitItem.IconSource);

            try { File.Delete(icoPath); File.Delete(pngPath); } catch { }
            int q2 = IconResolver.EnqueuedCount;
            string afterDelete = ir.Resolve(hitItem);
            Check("A7 缓存丢失后从 sidecar 找回 URL（返回空 ⇒ 本次兜底）",
                afterDelete == "", "resolved=[" + afterDelete + "]");
            Check("A7 缓存丢失后重新排入取图队列（自愈）",
                IconResolver.EnqueuedCount == q2 + 1,
                "enqueued " + q2 + " → " + IconResolver.EnqueuedCount);

            // ------------------------------------------------------------
            // 收尾
            // ------------------------------------------------------------
            Sb.AppendLine();
            Sb.AppendLine("[计数] 断言 " + _total + " 条 / 失败 " + _failed + " 条 / 后台取图累计入队 "
                + IconResolver.EnqueuedCount + " 次（本套离线，实际联网 0 次）");
            Sb.AppendLine();
            Sb.AppendLine(_failed == 0
                ? "结果: 全部通过 ✓"
                : "结果: " + _failed + " 项失败 ✗");

            try { File.WriteAllText(outFile, Sb.ToString(), new UTF8Encoding(false)); } catch { }

            Console.WriteLine(Sb.ToString());
            return _failed;
        }

        // ============================================================
        // 断言 / 报表
        // ============================================================

        private static void Header(string root)
        {
            Sb.AppendLine("=== URL 图标（网页 favicon）自检 · U-C · 2026-11-09 ===");
            Sb.AppendLine("root   = " + root);
            Sb.AppendLine("用法   = RunEverCs.exe --selftest-iconurl <root> <out.txt>（禁带 --root）");
            Sb.AppendLine("口径   = 只 http/https；超时 ≤ " + FetchTimeoutLimitMs + "ms；未命中先兜底、后台补齐；离线可跑");
            Sb.AppendLine("沙箱   = " + Path.Combine(root, "state", "cbUC", "iconurl-sandbox"));
            Sb.AppendLine();
        }

        private static void Check(string name, bool ok, string detail)
        {
            _total++;
            if (!ok) _failed++;
            Sb.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name
                + (string.IsNullOrEmpty(detail) ? "" : "   ← " + detail));
        }

        private static void Line(string s)
        {
            Sb.AppendLine(s);
        }

        private static bool IsLowerHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        /// <summary>菜单位图链路是否真能读出这个 .ico（与 NativeMenuWin32.ExtractHBitmap 同一 API）。</summary>
        private static bool IcoReadable(string icoPath)
        {
            if (string.IsNullOrEmpty(icoPath) || !File.Exists(icoPath)) return false;
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            try
            {
                uint n = Win32.ExtractIconExW(icoPath, 0, large, small, 1);
                bool ok = n > 0 && (small[0] != IntPtr.Zero || large[0] != IntPtr.Zero);
                if (large[0] != IntPtr.Zero) Win32.DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) Win32.DestroyIcon(small[0]);
                return ok;
            }
            catch { return false; }
        }

        /// <summary>造一张纯色 PNG（自检不依赖 assets，也不联网）。</summary>
        private static byte[] MakePng(int side)
        {
            var wb = new WriteableBitmap(side, side, 96, 96, PixelFormats.Bgra32, null);
            int stride = side * 4;
            var px = new byte[stride * side];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i + 0] = 0x40;   // B
                px[i + 1] = 0x90;   // G
                px[i + 2] = 0xE0;   // R
                px[i + 3] = 0xFF;   // A
            }
            wb.WritePixels(new Int32Rect(0, 0, side, side), px, stride, 0);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(wb));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                return ms.ToArray();
            }
        }
    }
}
