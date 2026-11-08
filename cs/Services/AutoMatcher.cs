using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RunEverCs.Services
{
    /// <summary>匹配结果 —— 对应 AHK `AutoMatcher.Match` 返回的 Map。</summary>
    public class AutoMatchResult
    {
        /// <summary>是否命中（只有 true 才可派发，否则回落弹菜单）。</summary>
        public bool Hit;

        /// <summary>命中的插件（未命中为 null）。</summary>
        public PluginInfo Plugin;

        /// <summary>产出形态 run/open/url/action/send/keys。</summary>
        public string Out = "";

        /// <summary>
        /// 产出目标。形态 A（有 Tpl）= 已代入 {0} 的结果；
        /// 形态 B（无 Tpl）= 空串 —— 目标由 PluginRunner 调插件 Run() 产出。
        /// </summary>
        public string Target = "";

        /// <summary>是否显示执行窗口（已按 -1 继承 auto.conf 的 showByDefault 解析完）。</summary>
        public int Show;
    }

    /// <summary>auto.conf 的自动执行段参数 —— 对应 AHK `AutoMatcher.ReadConf`。</summary>
    public class AutoConf
    {
        public bool Enabled = true;
        public int MinLen = 4;
        public int CacheSec = 300;
        public int ShowByDefault;       // 0 隐藏 / 1 显示
        public int WaitWhenShown = 1;   // 显示执行窗口时是否等待其结束

        /// <summary>缺件自动隐藏：0 = 关（默认）/ 1 = 本机解析不到的条目不显示（见 MissingFilter）。</summary>
        public int HideMissing = 0;

        /// <summary>
        /// 菜单三段的显示顺序（2026-11-08）：dynamic / process / always 的排列。
        /// 动态与兜底同格（兜底在 Dynamic 槽位里），故不单列 fallback 段。
        /// 未配置 = <see cref="MenuOrderSpec.Default"/>（动态 → 兜底 → 进程 → 固定，与历史行为一致）。
        /// 消费点 = `RunEverApp.ShowMenuFlow` 的 `decision.Flat(order)`。
        /// </summary>
        public string[] MenuOrder = MenuOrderSpec.Default;

        /// <summary>
        /// 菜单关闭后空闲多少秒做一次内存整理（2026-11-08）：0 = 关（一直常驻）。
        /// 默认 20s：WPF/MahApps 主题首次解析后常驻的那几十 MB 不丢，但工作集还给系统。
        /// 消费点 = `RunEverApp` 菜单关闭后 `MemTrim.Schedule(...)`。
        /// </summary>
        public int MemTrimIdle = 20;
    }

    /// <summary>
    /// 段 1.7：热键路径一次 stat 取到的"显示类开关"快照（值语义 —— 取走即冻结）。
    /// ★ 只装**判定层/执行器**要用的两个键：`hideMissing`（判定层）、`waitWhenShown`（执行器镜像）。
    ///   其余键各有消费点（见契约 §4 的三类分栏表），不在这里。
    /// </summary>
    public struct LiveConfSnapshot
    {
        /// <summary>hidemissing（0/1）—— 判定层 `PathResolvePolicy.InjectEntryResolved` 的入参。</summary>
        public int HideMissing;

        /// <summary>waitwhenshown（0/1）—— 执行器镜像字段 `Executor.WaitWhenShown` 的目标值。</summary>
        public int WaitWhenShown;

        /// <summary>
        /// `menuOrder`（三段顺序，永远非空 —— 取不到时由 LiveShowConf 填默认序）。
        /// 消费点 = 判定后的 `decision.Flat(order)`，因此必须与 hideMissing 同一份快照取走。
        /// </summary>
        public string[] MenuOrder;
    }

    /// <summary>
    /// 自动执行匹配器 —— 移植 AHK `src\AutoMatcher.ahk` 的匹配侧能力。
    ///
    /// ★ 职责分离（与 AHK 版同构）：
    ///   · 元数据扫描   = PluginScanner（已有，语义不动，设置页在用；本类只在外面包时间戳缓存）
    ///   · 匹配编译/命中 = 本类（Compile / MatchOne / Match）
    ///   · 形态 B 目标产出 = PluginRunner
    ///
    /// ★ 热键路径纪律（为什么要有缓存）：
    ///   config\functions\*.ahk 全量重扫是磁盘 IO，热键路径（菜单弹出前）不能白付。
    ///   故与 AHK 一致按 auto.conf 的 cacheSeconds 判过期，只有过期才重扫。
    ///
    /// ★ 加载期异常一律吞掉 → 回落"未命中"（零回归）：自动执行绝不能炸掉热键。
    /// </summary>
    public class AutoMatcher
    {
        private readonly string _root;
        private readonly string _confPath;
        private readonly PluginScanner _scanner;
        private readonly Action<string> _log;

        private List<PluginInfo> _plugins = new List<PluginInfo>();
        private List<PluginInfo> _badPlugins = new List<PluginInfo>();
        private bool _loaded;
        private long _lastScanTicks;

        public AutoConf Conf { get; private set; }

        public AutoMatcher(string root, Action<string> log = null)
        {
            _root = root;
            _log = log;
            _confPath = Path.Combine(root, "config", "auto.conf");
            _scanner = new PluginScanner(root);
            Conf = new AutoConf();
        }

        public string ConfPath { get { return _confPath; } }
        public string Dir { get { return _scanner.Dir; } }
        public List<PluginInfo> Plugins { get { return _plugins; } }
        public List<PluginInfo> BadPlugins { get { return _badPlugins; } }

        private void L(string s)
        {
            if (_log != null) _log(s);
        }

        // ============================================================
        // 读 auto.conf（对照 AHK `AutoMatcher.ahk:106-172`）
        //   · 默认值 = AHK 硬编码默认：enabled=1 minLen=4 cache=300s
        //     showByDefault=0 waitWhenShown=1
        //   · 文件不存在 / 读失败 → 用默认值（不阻断启动）
        //   · 行格式 key=value，# 与 ; 开头为注释；键名大小写不敏感
        // ============================================================
        public void ReadConf()
        {
            _confReadCount++;          // ★ 段 1.7：读盘计数器（只增不改既有语义；"未变 ⇒ 0 读盘"的行为级证据）
            var c = new AutoConf();
            Conf = c;

            if (!File.Exists(_confPath))
            {
                L("  自动执行: auto.conf 不存在，用默认参数");
                return;
            }

            try
            {
                string[] lines = File.ReadAllText(_confPath, Encoding.UTF8)
                    .Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

                // ★ 损坏探测（2026-10-03 实测教训）：文件存在却解析出 0 个键
                //   ⇒ 多半是文件被写坏了（例如换行被吞成一行）—— 此时**全部设置静默回落默认值**，
                //   用户会以为"设置没生效"。必须显式喊出来，别让它无声无息。
                int parsedKeys = 0;

                foreach (var raw in lines)
                {
                    string line = raw.Trim(' ', '\t');
                    if (line.Length == 0) continue;
                    if (line[0] == '#' || line[0] == ';') continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    parsedKeys++;
                    // ★ 只取第一个 = ：值里可能含 = （如路径/正则），不能 Split 后取 [1]
                    string key = line.Substring(0, eq).Trim(' ', '\t').ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim(' ', '\t');

                    switch (key)
                    {
                        case "enabled":
                            c.Enabled = (val != "0");
                            break;
                        case "minlen":
                            c.MinLen = ParseIntOr(val, c.MinLen);
                            break;
                        case "cacheseconds":
                            c.CacheSec = ParseIntOr(val, c.CacheSec);
                            break;
                        case "showbydefault":
                            c.ShowByDefault = IsFalse(val) ? 0 : 1;
                            break;
                        case "waitwhenshown":
                            c.WaitWhenShown = IsFalse(val) ? 0 : 1;
                            break;
                        // ★ 缺件自动隐藏（功能 A，用户 2026-10-03）：0 = 关（默认，与存量一致）
                        //   1 = 本机解析不到的 run/open 条目在菜单里不显示。
                        //   该判定不在自动执行链上，而在菜单渲染前（见 MissingFilter）。
                        case "hidemissing":
                            c.HideMissing = IsFalse(val) ? 0 : 1;
                            break;
                        // ★ 菜单三段顺序（2026-11-08）：dynamic,process,always 的排列（旧 fallback 词并入 dynamic）
                        //   未知词忽略、缺段补末尾 ⇒ 写错退回默认序，绝不吞段（见 MenuOrderSpec）
                        case "menuorder":
                            c.MenuOrder = MenuOrderSpec.Parse(val);
                            break;
                        // ★ 菜单关闭后的内存整理空闲秒数（2026-11-08）：0 = 关
                        case "memtrimidle":
                            c.MemTrimIdle = ParseIntOr(val, c.MemTrimIdle);
                            break;
                    }
                }

                if (parsedKeys == 0)
                {
                    L("  ⚠ auto.conf 存在但解析出 0 个键 —— 文件可能已损坏（换行被吞 / 编码异常）？"
                        + " 本次全部设置按默认值生效：" + _confPath);
                }
            }
            catch (Exception ex)
            {
                // 配置读失败 → 保留上面已写入的值 / 默认值，不抛（不阻断启动）
                L("  自动执行: auto.conf 读失败（用默认参数）: " + ex.Message);
            }
        }

        /// <summary>"0" 或 "false"（大小写不敏感）视为假 —— 与 AHK 判定一致。</summary>
        private static bool IsFalse(string v)
        {
            return v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================
        // ★ 段 1.6 B2：显示类开关的「热键期实时取值」
        //
        // 问题形态（已确证的三层）：
        //   · 显示类开关只在 Load() 里从 auto.conf 读，而 Load() 的唯一**非强制**调用点在
        //     RunEverApp.TryAutoExec 内 —— 该函数"没选中文本"时提前 return ⇒ 根本走不到；
        //   · 即便走到，插件扫描那条 CacheSec（默认 300s）节流还在；
        //   · 常规设置页保存后没有任何东西通知托盘（SettingsWindow 只给菜单页接了钩子）
        //   ⇒ 用户勾了「隐藏本机没有的条目」，托盘仍按启动时读到的旧值判定 = 开关"失效"。
        //
        // 选型（二选一里选"直接读该键"，并保留通知做第二条腿）：
        //   · 只靠"变更通知"不成立：它要求保存方进程 == 托盘进程（只有托盘内开设置才成立），
        //     用户在记事本改 auto.conf、或 WebDAV 同步覆盖 config 时，通知不会发生；
        //   · 直接读的成本可控：先做一次 file stat（长度 + mtimeUtc）判"文件没变"，
        //     未变 ⇒ **0 次读盘**（仅 stat，µs 级）；变了 ⇒ 重读一次几百字节的 auto.conf。
        //     全程不扫插件目录、**不重建 AutoMatcher**（I-5：热键路径禁阻塞）。
        // ============================================================
        private long _liveStampTicks = -1;
        private long _liveStampLen = -1;

        // ★ 段 1.7：读盘计数器（只增不改 —— 供自检做"未变 ⇒ 0 读盘"的行为级证明）
        private int _confReadCount;

        /// <summary>本实例累计"整份读 auto.conf"的次数（自检证据；未变时不得增长）。</summary>
        public int ConfReadCount { get { return _confReadCount; } }

        /// <summary>
        /// 重读 auto.conf（只读这一个文件：不扫插件目录、不重建匹配器）。
        /// 供托盘收到「常规页保存成功」通知时立即同步内存态（B2 第二条腿）。
        /// </summary>
        public void ReloadConf()
        {
            ReadConf();
        }

        /// <summary>auto.conf 变了才整份重读；返回是否发生了重读。</summary>
        private bool RefreshConfIfChanged()
        {
            try
            {
                var fi = new FileInfo(_confPath);
                if (!fi.Exists)
                {
                    if (_liveStampTicks == -1 && _liveStampLen == -1) return false;
                    _liveStampTicks = -1;
                    _liveStampLen = -1;
                    ReadConf();                       // 文件被删 ⇒ 回落默认值（并记一行日志）
                    return true;
                }

                long ticks = fi.LastWriteTimeUtc.Ticks;
                long len = fi.Length;
                if (ticks == _liveStampTicks && len == _liveStampLen) return false;   // 快路径：不读盘

                _liveStampTicks = ticks;
                _liveStampLen = len;
                ReadConf();
                return true;
            }
            catch (Exception ex)
            {
                L("  实时读 auto.conf 失败（沿用当前值）: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 热键路径专用：拿到**当前**的 hidemissing（顺带把整份开关刷成现况）。
        /// 调用点 = RunEverApp 判定层之前 ⇒「运行中改开关 → 下一次判定即生效」，不吃 CacheSec 节流。
        /// </summary>
        public int LiveHideMissing()
        {
            RefreshConfIfChanged();
            return Conf == null ? 0 : Conf.HideMissing;
        }

        /// <summary>
        /// 段 1.7：热键路径的"显示类开关"实时快照 —— **一次 stat 取两个值**。
        ///
        /// 为什么收成一次（而不是两次各取一个键）：热键回调里 I-5 只允许"一次 stat"级别的开销，
        /// 两次各调一次 `RefreshConfIfChanged()` 就是两次 stat（虽仍 µs 级，但没有理由白付）。
        /// ★ `waitWhenShown` 的消费点是 `Executor.WaitWhenShown`（镜像标量），**不在**判定链上；
        ///   调用方（RunEverApp）拿到值后仅在**值发生变化**时写回执行器 —— 见 RunEverApp.HotReloadCheck。
        ///
        /// ★★ `auto.conf` 全键的**三类分栏**（AC-C2；完整表见 `docs\架构契约.md` §4 与 `progress-fix-v8d.md`）：
        ///   · 类型 i（经 `AutoConf`/`AutoMatcher`）：
        ///       `hideMissing` → 判定层之前取值（RunEverApp.ShowMenuFlow，**与选中文本无关**）；
        ///       `waitWhenShown` → 执行器镜像字段（同上）；
        ///       `enabled` / `minLen` / `showByDefault` → `Match()`，**在 `TryAutoExec` 内**⇒ 需有选中文本；
        ///       `cacheSeconds` → `Load()` 的插件重扫节流，同样经 `TryAutoExec`。
        ///   · 类型 ii（各自现读文件）：`espath`/`evdll`（`ReadAutoConfKey`）、`webdav*`（`WebDavConfig.Load`）。
        ///   · 类型 iii（生命周期键）：`log`/`loglevel`（启动期）；`syncOnStart`（下次启动）。
        /// ⇒ **不许把这三类笼统写成"全键实时"**。
        ///
        /// ★ `hotkey` **不在**上述任何一类：它是**注册期键**，由 `TrayApp.ApplyHotkey()` 现读文件重注册，
        ///   只有两条调用链（设置窗关闭 / 托盘「重载配置」）⇒ **外部编辑 auto.conf 不会自动重注册**；
        ///   改触发键需关设置窗或走托盘「重载配置」。**禁止**把 `hotkey` 塞进 `AutoConf`（否则形成
        ///   "文件 vs 内存"两条读取路径）。
        /// </summary>
        public LiveConfSnapshot LiveShowConf()
        {
            RefreshConfIfChanged();
            var s = new LiveConfSnapshot();
            if (Conf == null)
            {
                s.HideMissing = 0;
                s.WaitWhenShown = 1;
                s.MenuOrder = MenuOrderSpec.Default;
                return s;
            }
            s.HideMissing = Conf.HideMissing;
            s.WaitWhenShown = Conf.WaitWhenShown;
            s.MenuOrder = Conf.MenuOrder ?? MenuOrderSpec.Default;
            return s;
        }

        /// <summary>自检/日志用：本类的"实时读取"当前认为的版本标记（未读过 = -1）。</summary>
        public long LiveStampTicks { get { return _liveStampTicks; } }

        /// <summary>整数解析失败时保留原值（AHK 会抛错被 catch 吞掉，语义等价且更稳）。</summary>
        private static int ParseIntOr(string v, int fallback)
        {
            int n;
            return int.TryParse(v.Trim(), out n) ? n : fallback;
        }

        // ============================================================
        // 加载：读配置 → （缓存过期才）扫插件目录
        //   返回可用插件数。对照 AHK `Load`。
        //
        // ★ 读盘分层（热键路径纪律）：
        //   · auto.conf = 每次 Load 都读（几百字节的小文件，与 AHK 一致）——
        //     为什么不缓存：设置页的"总开关/最小长度"改完必须【立即】生效，
        //     否则用户会以为开关坏了；真正的开销在插件目录扫描，那一层才受缓存约束。
        //   · config\functions\*.ahk = 只有缓存过期才重扫（菜单弹出前不做多余扫描）。
        //
        // ★ CacheSec=0 = 每次都重扫（auto.conf 的成文口径：
        //   "cacheSeconds 插件元数据缓存（秒）；0 = 每次重扫"）。
        //   ⚠ 与 AHK 字面实现有差异：AHK 写的是 `CacheSec > 0 && 过期` 才扫，
        //     即 0 ⇒ 永不重扫；此处按 auto.conf 的成文语义执行（已在报告标注）。
        // ============================================================
        public int Load(bool forceRescan = false)
        {
            ReadConf();

            long now = Environment.TickCount;
            bool needScan = forceRescan || !_loaded
                || (Conf.CacheSec <= 0
                    || (now - _lastScanTicks) > (long)Conf.CacheSec * 1000);

            if (needScan) Scan();
            return _plugins.Count;
        }

        /// <summary>
        /// 重扫插件目录并刷新缓存时间戳。
        ///
        /// ★ 不修改 PluginScanner.Scan() 的任何语义 —— 设置页在读它，
        ///   本类只是"扫完拷一份 + 记时间戳"的外包层。
        /// </summary>
        public void Scan()
        {
            try
            {
                _scanner.Scan();
                _plugins = new List<PluginInfo>(_scanner.Plugins);
                _badPlugins = new List<PluginInfo>(_scanner.BadPlugins);
                _loaded = true;
                _lastScanTicks = Environment.TickCount;

                foreach (var b in _badPlugins)
                    L("  自动执行: 插件非法已跳过 " + b.File + " —— " + b.BadReason);
            }
            catch (Exception ex)
            {
                L("  自动执行: 扫描插件失败: " + ex.Message);
                _plugins = new List<PluginInfo>();
                _badPlugins = new List<PluginInfo>();
            }
        }

        // ============================================================
        // 匹配：选中文本 → 唯一命中的插件
        //   命中多个时取"排序后的第一个"：Prio 降序 → 文件名升序
        //   （排序由 PluginScanner.Scan 保证，本类按序短路返回）
        //
        //   对照 AHK `AutoMatcher.ahk:407-437`
        // ============================================================
        public AutoMatchResult Match(string text)
        {
            var none = new AutoMatchResult { Hit = false, Out = "", Target = "", Show = 0 };

            if (!Conf.Enabled) return none;

            // AHK Trim(text, " `t`r`n")
            string t = (text ?? "").Trim(' ', '\t', '\r', '\n');
            if (t.Length < Conf.MinLen) return none;

            foreach (var p in _plugins)
            {
                // ★ 停用的插件直接跳过（设置页里勾掉的）
                if (!p.Enabled) continue;
                if (!MatchOne(p, t)) continue;

                // 形态 A：模板代入；形态 B：目标留给 PluginRunner（此处只标记）
                string target = "";
                if (!string.IsNullOrEmpty(p.Tpl)) target = Fill(p.Tpl, t);

                return new AutoMatchResult
                {
                    Hit = true,
                    Plugin = p,
                    Out = p.Out,
                    Target = target,
                    // ★ 三态解析：-1 继承 auto.conf 的 showByDefault
                    Show = (p.Show >= 0) ? p.Show : Conf.ShowByDefault
                };
            }
            return none;
        }

        /// <summary>
        /// 单条匹配（对照 AHK `MatchOne`）。
        /// ★ 统一走正则：whole=true 时 Compile 已补锚；whole=false 时原样正则。
        ///   （AHK 曾对 whole=false 走 InStr 分支导致正则失效 —— 别重犯）
        /// </summary>
        public bool MatchOne(PluginInfo p, string text)
        {
            string pat = Compile(p.Match, p.Whole);
            if (string.IsNullOrEmpty(pat)) return false;
            try
            {
                return Regex.IsMatch(text, pat);
            }
            catch
            {
                // 正则编译/匹配失败 = 不命中（不抛给热键路径）
                return false;
            }
        }

        // ============================================================
        // 匹配式编译：正则 或 前缀糖（对照 AHK `Compile`）
        //
        // ★★ 关键：不要无脑加 ^ $ —— 原式可能已自带锚点，
        //    双重锚（^^xxx$$）会导致永不匹配（AHK 2026-10-03 实测翻车）。
        //
        // 前缀糖语义（读原文定案）：以 * 结尾、且整式不含正则元字符
        //   [^$[]()|\  ⇒ 视作"前缀匹配"：剥掉 *，转义前缀，补 ^
        //   whole=true  → "^" + 转义前缀 + ".*$"   （整段：前缀开头到结尾）
        //   whole=false → "^" + 转义前缀            （行首/段首起，后面随意见）
        // ============================================================
        public static string Compile(string pat, bool whole)
        {
            pat = (pat ?? "").Trim(' ', '\t');
            if (pat.Length == 0) return "";

            // 前缀糖：以 * 结尾 ⇒ ^前缀
            if (pat[pat.Length - 1] == '*' && !Regex.IsMatch(pat, @"[\^$\[\]\(\)\|\\]"))
            {
                string prefix = pat.Substring(0, pat.Length - 1);
                return "^" + RegEsc(prefix) + (whole ? ".*$" : "");
            }

            // 已是正则
            if (!whole) return pat;               // 包含匹配：原样

            // 整段匹配：只在缺锚点时补
            string o = pat;
            if (o[0] != '^') o = "^" + o;
            if (o[o.Length - 1] != '$') o = o + "$";
            return o;
        }

        /// <summary>正则转义（前缀糖用）—— 与 AHK `RegEsc` 同字符集。</summary>
        public static string RegEsc(string s)
        {
            const string special = "\\\\.^$|?*+()[]{}";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (special.IndexOf(c) >= 0) sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>模板代入：{0} = 选中文本（对照 AHK `AutoMatcher.ahk:493-497`）。</summary>
        public static string Fill(string tpl, string text)
        {
            if (string.IsNullOrEmpty(tpl)) return "";
            return tpl.Replace("{0}", text ?? "");
        }

        /// <summary>调试输出（对照 AHK `Dump`）—— 设置页/自检用。</summary>
        public string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine("自动执行: enabled=" + (Conf.Enabled ? "1" : "0")
                + " minLen=" + Conf.MinLen + " cache=" + Conf.CacheSec + "s"
                + " showByDefault=" + Conf.ShowByDefault
                + " 插件数=" + _plugins.Count);
            foreach (var p in _plugins)
            {
                sb.AppendLine("  [" + p.Prio + "] " + p.Name
                    + "  match=" + p.Match + " whole=" + (p.Whole ? "1" : "0")
                    + " out=" + p.Out
                    + (string.IsNullOrEmpty(p.Tpl) ? " (形态B)" : " tpl=" + p.Tpl)
                    + " show=" + p.ShowText
                    + (p.Enabled ? "" : " ★已停用"));
            }
            return sb.ToString();
        }
    }
}
