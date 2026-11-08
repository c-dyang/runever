using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RunEverCs.Services;

namespace RunEverCs.Views
{
    /// <summary>
    /// 常规设置页 —— auto.conf 全字段（改一项存一项）。
    /// </summary>
    public class GeneralPage : UserControl
    {
        private readonly string _confPath;

        /// <summary>仓库根（同步按 root 定位 config\ 与 state\）。</summary>
        private readonly string _root;

        private CheckBox _enabled, _showDefault, _waitShown, _hideMissing;
        private TextBox _minLen, _cacheSec;
        private ComboBox _logLevel;
        private TextBlock _status;

        /// <summary>触发热键输入框 + 最后一份「有效的」热键（坏值不落盘）。</summary>
        private TextBox _hotkey;
        private string _hotkeyValid = "`";

        private bool _loading;

        /// <summary>`_loading` 期间的"控件回灌"阶段（Load 在给控件赋初值）。
        /// 段 1.6 B1：把「被 _loading 拦下的保存」区分成 Load 回灌 / Load 之外
        /// （后者 = `_loading` 没复位，正是"每次保存被静默吃掉"的形态 ⇒ 必须留痕）。</summary>
        private bool _loadingAssign;

        /// <summary>配置读取失败的原因（非 null ⇒ 页面控件停在默认值上）。</summary>
        private string _loadError;

        /// <summary>上一次保存是否成功（段 1.6 B1：关窗时据此提示"有修改没落盘"）。</summary>
        private bool _lastSaveOk = true;

        /// <summary>常规页是否有"没能落盘"的修改（SettingsWindow 关窗前提示用）。</summary>
        public bool HasSaveFailure { get { return !_lastSaveOk; } }

        /// <summary>
        /// ★ 段 1.6 B2：保存**成功后**的回调（与 MenuEditorPage.AfterSave 同款）。
        /// 由 SettingsWindow 注入托盘进程的动作（重载 auto.conf / 重算显示开关）；
        /// `null` = 独立进程打开设置（无运行实例），此时不回调。
        /// </summary>
        public Action AfterSave;

        // ---- v7：同步（WebDAV）----
        private TextBox _wdUrl, _wdUser, _wdDir;
        private PasswordBox _wdPass;
        private CheckBox _syncOnStart;
        private Button _testBtn, _syncNowBtn;

        public GeneralPage(string root)
        {
            _root = root;
            _confPath = Path.Combine(root, "config", "auto.conf");
            Build();
            Load();
        }

        private void Build()
        {
            var sp = new StackPanel { Margin = new Thickness(20) };

            sp.Children.Add(new TextBlock
            {
                Text = "自动执行（选中文本 → 匹配插件 → 直接执行，不弹菜单）",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var box1 = new GroupBox { Header = "触发", Padding = new Thickness(12) };
            var p0 = new StackPanel();
            p0.Children.Add(Lbl("触发热键（选中文本后按它：弹菜单 / 走自动执行）"));

            var hotRow = new StackPanel { Orientation = Orientation.Horizontal };
            _hotkey = new TextBox { Width = 120 };
            _hotkey.LostFocus += (s, e) => CommitHotkey();
            hotRow.Children.Add(_hotkey);

            var hotBtn = new Button
            {
                Content = "应用",
                Width = 70,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(6, 2, 6, 2)
            };
            hotBtn.Click += (s, e) => CommitHotkey();
            hotRow.Children.Add(hotBtn);
            p0.Children.Add(hotRow);

            p0.Children.Add(new TextBlock
            {
                Text = "写法：` （默认反引号）／ F1-F24 ／ Space Tab Enter Esc Home End PgUp PgDn 方向键\n"
                     + "修饰符前缀：^ = Ctrl    ! = Alt    + = Shift    # = Win（可叠加，如 ^!r、#F12）\n"
                     + "⚠ 裸字母/数字会被拒绝 —— 那等于按一下就弹菜单，会吞掉正常打字。\n"
                     + "保存后【关掉本窗口即生效】（托盘会重注册，不用重启程序）。",
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 2)
            });
            box1.Content = p0;
            sp.Children.Add(box1);

            var box2 = new GroupBox { Header = "基本", Padding = new Thickness(12), Margin = new Thickness(0, 14, 0, 0) };
            var p1 = new StackPanel();
            _enabled = new CheckBox { Content = "启用自动执行" };
            _enabled.Checked += (s, e) => Save();
            _enabled.Unchecked += (s, e) => Save();
            p1.Children.Add(_enabled);

            p1.Children.Add(Lbl("最短文本长度（低于此长度不触发，防单字误触）"));
            _minLen = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
            _minLen.LostFocus += (s, e) => Save();
            p1.Children.Add(_minLen);

            p1.Children.Add(Lbl("元数据缓存（秒；0 = 每次重扫）"));
            _cacheSec = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
            _cacheSec.LostFocus += (s, e) => Save();
            p1.Children.Add(_cacheSec);
            box2.Content = p1;
            sp.Children.Add(box2);

            // ---- 菜单显示（缺件自动隐藏）----
            var boxHide = new GroupBox
            {
                Header = "菜单显示",
                Padding = new Thickness(12),
                Margin = new Thickness(0, 14, 0, 0)
            };
            var pHide = new StackPanel();
            _hideMissing = new CheckBox { Content = "隐藏本机没有的条目（run / open）" };
            _hideMissing.Checked += (s, e) => Save();
            _hideMissing.Unchecked += (s, e) => Save();
            pHide.Children.Add(_hideMissing);
            pHide.Children.Add(new TextBlock
            {
                Text = "菜单配置可能来自另一台机器。勾上后：\n"
                     + "· run 条目 —— 程序名在【路径缓存 → PATH → App Paths】都解析不到 ⇒ 不显示\n"
                     + "· open 条目 —— 目标文件/目录不存在 ⇒ 不显示\n"
                     + "· url / action / send / keys 不判；目标含 %file%/%text% 等占位符的条目不判（防误杀）\n"
                     + "判定只查缓存与系统解析，不跑全盘搜索；想让更多条目解析到 → 托盘点「刷新路径缓存」。",
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 2)
            });
            boxHide.Content = pHide;
            sp.Children.Add(boxHide);

            var box3 = new GroupBox
            {
                Header = "新建插件的默认行为（插件本身没声明 RVE_FN_Show 时生效）",
                Padding = new Thickness(12),
                Margin = new Thickness(0, 14, 0, 0)
            };
            var p2 = new StackPanel();
            _showDefault = new CheckBox { Content = "默认显示执行窗口（命令行工具需要看输出时勾）" };
            _showDefault.Checked += (s, e) => Save();
            _showDefault.Unchecked += (s, e) => Save();
            p2.Children.Add(_showDefault);
            p2.Children.Add(new TextBlock
            {
                Text = "不勾 = 默认隐藏（推荐）。单个插件要显示 → 在「插件」页或插件文件里设 RVE_FN_Show := true",
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 6)
            });

            _waitShown = new CheckBox { Content = "显示窗口时等待命令结束（窗口留存，可看进度）" };
            _waitShown.Checked += (s, e) => Save();
            _waitShown.Unchecked += (s, e) => Save();
            p2.Children.Add(_waitShown);
            box3.Content = p2;
            sp.Children.Add(box3);

            // ---- 日志 ----
            var box4 = new GroupBox
            {
                Header = "日志",
                Padding = new Thickness(12),
                Margin = new Thickness(0, 14, 0, 0)
            };
            var p3 = new StackPanel();
            _logLevel = new ComboBox { Margin = new Thickness(0, 3, 0, 6), Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
            _logLevel.Items.Add("0 — 关闭日志（不写 runever-cs.log）");
            _logLevel.Items.Add("1 — 简略（默认：启动/采集/命中/耗时总计）");
            _logLevel.Items.Add("2 — 详细（额外记录分阶段耗时明细）");
            _logLevel.SelectionChanged += (s, e) => Save();
            p3.Children.Add(_logLevel);
            p3.Children.Add(new TextBlock
            {
                Text = "日志文件：state\\runever-cs.log\n关闭后热键响应略快（省去每次文件写入）。",
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            });
            box4.Content = p3;
            sp.Children.Add(box4);

            // ---- v7 同步（WebDAV 配置同步）----
            var boxSync = new GroupBox
            {
                Header = "同步（WebDAV：菜单配置 + 插件，多机对齐）",
                Padding = new Thickness(12),
                Margin = new Thickness(0, 14, 0, 0)
            };
            var pSync = new StackPanel();

            pSync.Children.Add(Lbl("WebDAV 地址（留空 = 不同步，一切保持原样）"));
            _wdUrl = new TextBox { Width = 340, HorizontalAlignment = HorizontalAlignment.Left };
            _wdUrl.LostFocus += (s, e) => Save();
            pSync.Children.Add(_wdUrl);

            pSync.Children.Add(Lbl("账号（留空 = 匿名）"));
            _wdUser = new TextBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
            _wdUser.LostFocus += (s, e) => Save();
            pSync.Children.Add(_wdUser);

            pSync.Children.Add(Lbl("密码（明文存 auto.conf；该文件不进同步范围）"));
            _wdPass = new PasswordBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
            _wdPass.LostFocus += (s, e) => Save();
            pSync.Children.Add(_wdPass);

            pSync.Children.Add(Lbl("远端目录（默认 RunEver）"));
            _wdDir = new TextBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
            _wdDir.LostFocus += (s, e) => Save();
            pSync.Children.Add(_wdDir);

            _syncOnStart = new CheckBox
            {
                Content = "启动时自动同步（托盘就绪后后台跑，不拖慢启动）",
                Margin = new Thickness(0, 8, 0, 0)
            };
            _syncOnStart.Checked += (s, e) => Save();
            _syncOnStart.Unchecked += (s, e) => Save();
            pSync.Children.Add(_syncOnStart);

            var syncRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _testBtn = new Button { Content = "测试连接", Width = 100, Padding = new Thickness(6, 2, 6, 2) };
            _testBtn.Click += (s, e) => TestConnectionAsync();
            syncRow.Children.Add(_testBtn);

            _syncNowBtn = new Button
            {
                Content = "立即同步",
                Width = 100,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(6, 2, 6, 2)
            };
            _syncNowBtn.Click += (s, e) => SyncNowAsync();
            syncRow.Children.Add(_syncNowBtn);
            pSync.Children.Add(syncRow);

            pSync.Children.Add(new TextBlock
            {
                Text = "同步范围：config\\menus.conf + config\\functions\\*.ahk"
                     + "（**不含** auto.conf / cache\\paths.txt —— 那些是本机专属）。\n"
                     + "规则：双向、按修改时间新者胜（±2 秒内视为同一版本）；覆盖前自动备份"
                     + " .bak.<时间戳>；永不删除文件。\n"
                     + "远端 mtime 无法取得时，第二轮是否重传由 state\\webdav-sync.txt 快照兜底。\n"
                     + "⚠ 两个按钮的网络操作都在后台线程跑，不会卡住本窗口；密码不会出现在日志/提示里。",
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 2)
            });
            boxSync.Content = pSync;
            sp.Children.Add(boxSync);

            sp.Children.Add(new TextBlock
            {
                Text = "※ 改一项存一项，即时生效，无需重启。\n※ 配置文件：" + _confPath,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 14, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            _status = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0) };
            sp.Children.Add(_status);

            var openBtn = new Button
            {
                Content = "用编辑器打开 auto.conf",
                Width = 170,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 10, 0, 0)
            };
            openBtn.Click += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _confPath,
                        UseShellExecute = true
                    });
                }
                catch { }
            };
            sp.Children.Add(openBtn);

            Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private TextBlock Lbl(string t)
        {
            return new TextBlock
            {
                Text = t,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 8, 0, 2)
            };
        }

        // ============================================================
        private void Load()
        {
            _loading = true;
            _loadingAssign = true;
            _loadError = null;
            try
            {
                var cfg = ReadConf();
                _hotkeyValid = GetStr(cfg, "hotkey", "`");
                _hotkey.Text = _hotkeyValid;
                _enabled.IsChecked = GetBool(cfg, "enabled", true);
                _minLen.Text = GetInt(cfg, "minlen", 4).ToString();
                _cacheSec.Text = GetInt(cfg, "cacheseconds", 300).ToString();
                _showDefault.IsChecked = GetBool(cfg, "showbydefault", false);
                _waitShown.IsChecked = GetBool(cfg, "waitwhenshown", true);
                _hideMissing.IsChecked = GetBool(cfg, "hidemissing", false);
                _logLevel.SelectedIndex = GetInt(cfg, "log", 1);
                if (_logLevel.SelectedIndex < 0 || _logLevel.SelectedIndex > 2)
                    _logLevel.SelectedIndex = 1;

                // ---- v7 同步 ----
                _wdUrl.Text = GetStr(cfg, "webdavUrl", "");
                _wdUser.Text = GetStr(cfg, "webdavUser", "");
                _wdPass.Password = GetStr(cfg, "webdavPass", "");
                _wdDir.Text = GetStr(cfg, "webdavDir", "RunEver");
                _syncOnStart.IsChecked = GetBool(cfg, "syncOnStart", false);

                _status.Foreground = Brushes.Gray;
                _status.Text = "已加载";
            }
            catch (Exception ex)
            {
                // ★ 段 1.6 B1：加载失败**必须留痕**——此时控件里全是默认值，
                //   任何一次保存都会拿默认值覆盖用户配置 ⇒ Save() 据此拒写（见 Save 开头）。
                _loadError = ex.Message;
                _status.Foreground = Brushes.Red;
                _status.Text = "✗ 加载失败（已禁用保存，避免默认值覆盖配置）：" + ex.Message;
                SettingsWriteLog.AppendLoadFailure(_root, _confPath, ex.Message);
            }
            finally
            {
                _loadingAssign = false;
                _loading = false;
            }
        }

        /// <summary>
        /// 保存（改一项存一项）。段 1.6 · B1 改造后**没有任何静默出口**：
        ///   ① 被 `_loading` 拦下      → 状态行 + settings-write.log(SKIP)
        ///   ② 加载失败（控件=默认值）  → 状态行红字 + settings-write.log(FAIL)，且**拒写**
        ///   ③ 写盘成功                 → 状态行「已保存 HH:mm:ss（字节 · SHA256）」+ log(OK) + 通知托盘
        ///   ④ 写盘失败/回读不过/回滚失败 → 状态行红字 + log(FAIL，原因在行里)
        /// </summary>
        private void Save()
        {
            // ① 被 _loading 拦下：不再静默
            if (_loading)
            {
                string phase = _loadingAssign
                    ? "Load 期间事件回灌（属正常）"
                    : "Load 之外——疑似 _loading 未复位（B1 静默路径）";
                _status.Foreground = Brushes.Gray;
                _status.Text = SettingsWriteLog.RecordSkippedByLoading(_root, _confPath, phase);
                return;
            }

            // ② 加载失败 ⇒ 控件值是默认值，写盘 = 拿默认值覆盖用户配置 ⇒ 拒写并喊出来
            if (_loadError != null)
            {
                _lastSaveOk = false;
                _status.Foreground = Brushes.Red;
                _status.Text = "✗ 保存失败：配置加载失败（" + _loadError
                    + "）—— 已拒绝写盘，以免用默认值覆盖现有配置";
                SettingsWriteLog.Append(_root, "save", "FAIL",
                    "conf=" + _confPath + " 原因=配置加载失败，已拒写 原始错误=" + _loadError);
                return;
            }

            int minLen, cacheSec;
            if (!int.TryParse(_minLen.Text.Trim(), out minLen) || minLen < 0) minLen = 4;
            if (!int.TryParse(_cacheSec.Text.Trim(), out cacheSec) || cacheSec < 0) cacheSec = 300;

            var vals = new Dictionary<string, string>
            {
                {"hotkey", _hotkeyValid},
                {"enabled", (_enabled.IsChecked == true) ? "1" : "0"},
                {"minlen", minLen.ToString()},
                {"cacheseconds", cacheSec.ToString()},
                {"showbydefault", (_showDefault.IsChecked == true) ? "1" : "0"},
                {"waitwhenshown", (_waitShown.IsChecked == true) ? "1" : "0"},
                {"hidemissing", (_hideMissing.IsChecked == true) ? "1" : "0"},
                {"log", (_logLevel.SelectedIndex < 0 ? 1 : _logLevel.SelectedIndex).ToString()},
                // ---- v7 同步（写回仍走同一套：.bak + 原子写 + 回读校验 + 回滚）----
                {"webdavUrl", (_wdUrl.Text ?? "").Trim()},
                {"webdavUser", (_wdUser.Text ?? "").Trim()},
                {"webdavPass", _wdPass.Password ?? ""},
                {"webdavDir", (_wdDir.Text ?? "").Trim()},
                {"syncOnStart", (_syncOnStart.IsChecked == true) ? "1" : "0"}
            };

            // ③④ 写盘 + 留痕（AutoConfStore 内部：备份 → 原子写 → 回读校验/回滚 → settings-write.log）
            //     ★ 脱敏钩子照旧：WriteConf 的"回读校验失败"消息里会带出 webdavPass 明文。
            ConfWriteResult r = null;
            try
            {
                r = WriteConf(vals);
            }
            catch (Exception ex)
            {
                // AutoConfStore.Write 声明不抛；这里只是最后一道保险，仍不静默
                r = new ConfWriteResult();
                r.Path = _confPath;
                r.Ok = false;
                r.Error = RedactPass("写盘异常（" + ex.GetType().Name + "）：" + ex.Message);
                SettingsWriteLog.Append(_root, "save", "FAIL", r.LogDetail());
            }

            if (r.Ok)
            {
                _lastSaveOk = true;
                _status.Foreground = Brushes.Gray;
                _status.Text = r.StatusText();
                if (AfterSave != null)
                {
                    // ★ B2：保存成功 ⇒ 通知托盘（重载 auto.conf / 重算显示开关），
                    //   失败也要留痕：通知断了 = 显示类开关"改了不生效"
                    try { AfterSave(); }
                    catch (Exception nex)
                    {
                        SettingsWriteLog.AppendNotifyFailure(_root,
                            RedactPass(nex.GetType().Name + "：" + nex.Message));
                    }
                }
            }
            else
            {
                _lastSaveOk = false;
                _status.Foreground = Brushes.Red;
                _status.Text = r.StatusText();
            }
        }

        /// <summary>凭据红线兜底：任何要落 UI / 日志的文本都先过这一道。</summary>
        private string RedactPass(string s)
        {
            try
            {
                string p = _wdPass == null ? null : _wdPass.Password;
                if (!string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(p))
                    s = s.Replace(p, "***");
            }
            catch { }
            return s;
        }

        /// <summary>读 auto.conf（key=value，跳过注释）。</summary>
        private Dictionary<string, string> ReadConf()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(_confPath)) return d;

            foreach (var raw in File.ReadAllLines(_confPath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        private static bool GetBool(Dictionary<string, string> d, string k, bool def)
        {
            string v;
            if (!d.TryGetValue(k, out v)) return def;
            v = v.ToLowerInvariant();
            return !(v == "0" || v == "false");
        }

        /// <summary>
        /// 提交触发热键：合法则规范化并落盘；非法则报错并把输入框退回上一个有效值
        /// （坏值绝不写进 auto.conf —— 否则托盘侧只会静默回落默认热键，用户以为设置没生效）。
        /// </summary>
        private void CommitHotkey()
        {
            if (_loading || _hotkey == null) return;

            HotkeySpec spec;
            string err;
            if (!HotkeySpec.TryParse(_hotkey.Text, out spec, out err))
            {
                _status.Text = "✗ 热键无效：" + err;
                _hotkey.Text = _hotkeyValid;
                return;
            }

            _hotkey.Text = spec.Text;
            if (spec.Text == _hotkeyValid)
            {
                _status.Text = "热键未变化（" + spec.Text + "）";
                return;
            }

            _hotkeyValid = spec.Text;
            Save();
            _status.Text = "热键已改为 " + spec.Text + "（关掉本窗口后生效）";
        }

        private static int GetInt(Dictionary<string, string> d, string k, int def)
        {
            string v;
            int n;
            if (d.TryGetValue(k, out v) && int.TryParse(v, out n)) return n;
            return def;
        }

        private static string GetStr(Dictionary<string, string> d, string k, string def)
        {
            string v;
            if (d.TryGetValue(k, out v) && v != null && v.Trim().Length > 0) return v.Trim();
            return def;
        }

        /// <summary>
        /// 写回 auto.conf —— 实现见 <see cref="AutoConfStore.Write"/>（段 1.6 B1 从本类抽出，
        /// 目的是让「备份 / 原子写 / 回读校验 / 回滚 / 留痕」五条路径能在自检里被驱动，
        /// 而不是只能靠"打开窗口点一下"验）。
        ///
        /// 旧实现的两个静默点（守门复查项，已修）：
        ///   ① `catch { backup = null; }` —— 备份失败连一行痕迹都没有；
        ///   ② 回滚失败只拼进消息里，消息最终由 UI 状态行（灰字）承载 ⇒ 用户根本不会注意。
        /// 现在：备份失败写 Warning（进状态行 + 日志），回滚失败写 Warning + Error 双留痕。
        /// </summary>
        private ConfWriteResult WriteConf(Dictionary<string, string> vals)
        {
            return AutoConfStore.Write(_confPath, vals, RedactPass, _root);
        }

        // ============================================================
        // v7 同步：两个按钮的网络 IO 一律后台线程 + 按钮禁用/恢复（UI 线程禁网络 IO）
        // ============================================================

        /// <summary>先把界面值落盘，再以**落盘值**为准——避免"界面 / 配置"两个真相。</summary>
        private bool SaveThenCheck()
        {
            Save();
            string s = _status.Text ?? "";
            return s.IndexOf("保存失败", StringComparison.Ordinal) < 0;
        }

        /// <summary>「测试连接」：PROPFIND Depth:0 于 &lt;url&gt;/&lt;dir&gt;/；**绝不回显口令**。</summary>
        private void TestConnectionAsync()
        {
            if (!SaveThenCheck()) return;

            var cfg = WebDavConfig.Load(_root);
            if (!cfg.IsConfigured)
            {
                _status.Text = "未配置 WebDAV 地址（webdavUrl 为空）—— 无需测试";
                return;
            }

            _testBtn.IsEnabled = false;
            _status.Text = "正在测试连接…";

            var worker = new Thread(() =>
            {
                string msg;
                try
                {
                    var err = new WebDavClient(cfg, 8000).TestConnection();
                    if (err == null) msg = "✓ 连接成功（远端目录可访问）";
                    else if (err.Kind == WebDavErrorKind.Auth) msg = "✗ 认证失败（检查账号 / 密码）";
                    else if (err.Kind == WebDavErrorKind.NotFound) msg = "✗ 远端目录不存在（同步时会自动创建）";
                    else if (err.Kind == WebDavErrorKind.Timeout) msg = "✗ 连接超时";
                    else msg = "✗ " + err.KindText()
                        + (string.IsNullOrEmpty(err.Message) ? "" : "：" + err.Message);
                }
                catch (Exception ex)
                {
                    msg = "✗ 测试失败：" + ex.Message;
                }

                Dispatch(delegate ()
                {
                    _status.Text = RedactPass(msg);
                    _testBtn.IsEnabled = true;
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>「立即同步」：与托盘 / CLI `--sync` **同一份** ConfigSync.Run。</summary>
        private void SyncNowAsync()
        {
            if (!SaveThenCheck()) return;

            var cfg = WebDavConfig.Load(_root);
            if (!cfg.IsConfigured)
            {
                _status.Text = "未配置 WebDAV 地址 —— 已跳过（未动任何文件）";
                return;
            }

            _syncNowBtn.IsEnabled = false;
            _status.Text = "正在同步…";

            var worker = new Thread(() =>
            {
                string msg;
                try
                {
                    var r = ConfigSync.Run(_root, null);
                    if (r.NotConfigured) msg = "未配置，已跳过";
                    else if (r.Ok) msg = "✓ 同步完成：" + r.SummaryLine();
                    else msg = "✗ 同步失败：" + r.ErrorKind
                        + (string.IsNullOrEmpty(r.Message) ? "" : "：" + r.Message);
                }
                catch (Exception ex)
                {
                    msg = "✗ 同步异常：" + ex.Message;
                }

                Dispatch(delegate ()
                {
                    _status.Text = RedactPass(msg);
                    _syncNowBtn.IsEnabled = true;
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>后台线程安全地回 UI 线程改控件。</summary>
        private void Dispatch(Action a)
        {
            try
            {
                var app = Application.Current;
                if (app != null && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.BeginInvoke(a);
                    return;
                }
            }
            catch { }
            a();
        }

    }
}
