using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        /// <summary>页面底部状态行（写盘失败要当场红字，不许静默）。</summary>
        private TextBlock _statusLine;

        protected AutoConfPage(string root)
        {
            Root = root;
            ConfPath = Path.Combine(root, "config", "auto.conf");
            Reload();

            // ★ B4b-1：页面自带一条状态行。设置项是「改完立即落盘」的，
            //   一旦写盘失败（文件只读 / 目录被占 / 磁盘满），用户必须**当场**看见，
            //   而不是下一次开窗才发现值没变。
            _statusLine = new TextBlock
            {
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(28, 0, 28, 10),
                Visibility = Visibility.Collapsed
            };

            var dock = new DockPanel();
            DockPanel.SetDock(_statusLine, Dock.Bottom);
            dock.Children.Add(_statusLine);
            dock.Children.Add(SettingsKit.PageHost(Build()));
            Content = dock;
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

        /// <summary>改一个键并立刻落盘；成功则触发 AfterSave，失败则在状态行红字说明（控件由 SettingsKit 回滚）。</summary>
        protected bool Set(string key, string val)
        {
            var vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { key, val ?? "" } };
            var r = AutoConfStore.Write(ConfPath, vals, RedactPass, Root);
            LastWriteStatus = r.Ok ? "已保存 " + key : ("保存失败：" + r.Error);
            if (r.Ok)
            {
                _vals[key] = val ?? "";
                ShowStatus("已保存：" + key, false);
                var cb = AfterSave;
                if (cb != null) { try { cb(); } catch { } }
            }
            else
            {
                // ★ B4b-1：以前这里是"静默吞掉" —— 开关看着像成功了，磁盘上其实没改。
                //   现在状态行给红字，并把完整路径写清楚（多半是权限/占用问题）。
                ShowStatus("保存失败（" + key + "）：" + r.Error
                    + "\n该设置已还原为原来的值。文件：" + ConfPath, true);
            }
            return r.Ok;
        }

        /// <summary>在页面底部状态行显示一行结果（isError=true 时红字）。</summary>
        private void ShowStatus(string text, bool isError)
        {
            if (_statusLine == null) return;
            try
            {
                _statusLine.Text = text;
                _statusLine.Foreground = isError ? Brushes.Firebrick : SettingsKit.TextSecondary;
                _statusLine.Visibility = Visibility.Visible;
            }
            catch { }
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
