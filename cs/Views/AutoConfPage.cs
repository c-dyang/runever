using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RunEverCs.Services;

namespace RunEverCs.Views
{
    /// <summary>
    /// auto.conf 设置页基类（2026-11-07）。
    ///
    /// 统一三件事，避免每个页面各写一遍：
    ///   ① 读：`AutoConfStore.Read` 一次性拿全量键值（保留注释由 Write 负责）；
    ///   ② 写：**改一项立刻落盘**（官方口径「改完立即生效，不留确认按钮」）；
    ///   ③ 通知：保存成功后调 AfterSave，让托盘重载（hideMissing / showByDefault 等）。
    ///
    /// 与旧 GeneralPage 的差别：那个是 600 行单体、6 个 GroupBox 挤一页；
    /// 现在按主题拆成多页，每页只关心自己的键。
    /// </summary>
    internal abstract class AutoConfPage : UserControl
    {
        protected readonly string Root;
        protected readonly string ConfPath;
        private Dictionary<string, string> _vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>保存成功后的回调（托盘用它重载 auto.conf 显示类开关）。</summary>
        public Action AfterSave;

        /// <summary>最近一次写盘的结果（供诊断页/状态栏显示）。</summary>
        public string LastWriteStatus { get; private set; } = "";

        protected AutoConfPage(string root)
        {
            Root = root;
            ConfPath = Path.Combine(root, "config", "auto.conf");
            Reload();
            Content = SettingsKit.PageHost(Build());
        }

        /// <summary>子类实现：构建页面内容（返回一个纵向 StackPanel 即可）。</summary>
        protected abstract UIElement Build();

        protected void Reload()
        {
            try { _vals = AutoConfStore.Read(ConfPath) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
            catch { _vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        }

        protected string Get(string key, string def = "")
        {
            string v;
            return _vals.TryGetValue(key, out v) ? v : def;
        }

        protected bool GetBool(string key, bool def)
        {
            string v = Get(key, "");
            if (string.IsNullOrEmpty(v)) return def;
            return v != "0";     // 与 AutoMatcher.IsFalse 口径一致：只有 "0" 算假
        }

        protected int GetInt(string key, int def)
        {
            int n;
            return int.TryParse(Get(key, ""), out n) ? n : def;
        }

        /// <summary>改一个键并立刻落盘；成功则触发 AfterSave。</summary>
        protected bool Set(string key, string val)
        {
            var vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { key, val ?? "" } };
            var r = AutoConfStore.Write(ConfPath, vals, RedactPass, Root);
            LastWriteStatus = r.Ok ? "已保存 " + key : ("保存失败：" + r.Error);
            if (r.Ok)
            {
                _vals[key] = val ?? "";
                var cb = AfterSave;
                if (cb != null) { try { cb(); } catch { } }
            }
            return r.Ok;
        }

        protected bool SetBool(string key, bool v) { return Set(key, v ? "1" : "0"); }
        protected bool SetInt(string key, int v) { return Set(key, v.ToString()); }

        /// <summary>口令脱敏（与旧常规页同一钩子，避免日志泄漏 webdavPass）。</summary>
        protected static string RedactPass(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= 2 ? "**" : s.Substring(0, 1) + new string('*', Math.Min(8, s.Length - 1));
        }
    }
}
