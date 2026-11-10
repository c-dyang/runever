using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using RunEverCs.Models;
using RunEverCs.Services;
using RunEverCs.Views;

// System.Windows.Controls 里也有 MenuItem（WPF 菜单项）⇒ 显式指向配置条目模型
using MenuItem = RunEverCs.Models.MenuItem;

namespace RunEverCs
{
    /// <summary>
    /// 全库搜索自检 —— S1，2026-11-10（第 22 套）。
    ///
    /// 用法：RunEverCs.exe --selftest-search &lt;root&gt; &lt;out.txt&gt;
    ///   口径同 --selftest-arch / --selftest-iconurl：args[1]=root、args[2]=out.txt，**禁带 --root**。
    ///
    /// 覆盖（对应 HANDOFF §4 的四条）：
    ///   A 拼音：全拼 / 首字母 / 大小写 / 缓存复用（底层 ChnCharInfo.dll 真的能加载）
    ///   B 匹配口径：名称走「子串 + 首字母 + 全拼」；目标/分组**只走子串**
    ///   C 虚拟固定项：恒在（空库 / 全缺件也在）/ 保留 ID / 不进 IdMap / 末尾 + 分隔线 / 不参与剪枝
    ///   D 可见集合：搜索页条目集合 == 菜单可见集合（同一 hideMissing 口径），含空壳组不出现
    ///   E 执行语义：选择分流表 + **执行载荷与菜单点击同一构造**（假 handler 断言目标）
    ///   F 窗口契约：480×400 默认 / 按键表 / 无静态常驻实例 / 节头不可选
    ///
    /// ★ 全程离线、无 UI：不开窗口、不联网、不启动任何程序；夹具只落 `state\cbS1\search-sandbox`。
    /// </summary>
    public static class PinyinSearchSelfTest
    {
        private static readonly StringBuilder Sb = new StringBuilder();
        private static int _total;
        private static int _failed;

        public static int Run(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("用法: RunEverCs.exe --selftest-search <root> <out.txt>");
                return 2;
            }

            string root = args[1];
            string outFile = args[2];

            Sb.Length = 0;
            _total = 0;
            _failed = 0;

            string sandbox = Path.Combine(root, "state", "cbS1", "search-sandbox");
            Header(root, sandbox);

            try
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
                Directory.CreateDirectory(sandbox);
            }
            catch (Exception ex)
            {
                Check("环境 沙箱目录可用: " + sandbox, false, ex.GetType().Name + ": " + ex.Message);
                return Finish(outFile);
            }

            try
            {
                // 夹具：一个真存在的假程序、一个"缓存里有但文件已删"的程序、一个真目录
                string existsExe = Path.Combine(sandbox, "exists.exe");
                File.WriteAllText(existsExe, "x", new UTF8Encoding(false));
                string staleExe = Path.Combine(sandbox, "gone.exe");          // 故意不创建
                string existsDir = Path.Combine(sandbox, "existdir");
                Directory.CreateDirectory(existsDir);

                string cacheFile = Path.Combine(sandbox, "paths.txt");
                File.WriteAllLines(cacheFile, new[]
                {
                    "existsprog.exe=" + existsExe,
                    "staleprog.exe=" + staleExe
                }, new UTF8Encoding(false));
                var cache = new PathCache(cacheFile);
                Check("夹具: 路径缓存载入 2 条", cache.Count == 2, "实际 " + cache.Count);

                SectionA_Pinyin();
                SectionB_Match(cache);
                SectionC_Fixed(cache);
                SectionD_VisibleSet(cache);
                SectionE_Execute(cache);
                SectionF_WindowContract();
            }
            catch (Exception ex)
            {
                Check("异常中断", false, ex.GetType().Name + ": " + ex.Message);
                string[] frames = (ex.StackTrace ?? "").Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < frames.Length && i < 6; i++)
                    if (frames[i].Trim().Length > 0) Line("        " + frames[i].Trim());
            }

            return Finish(outFile);
        }

        // ============================================================
        // A 拼音
        // ============================================================
        private static void SectionA_Pinyin()
        {
            Line("");
            Line("---- A 拼音（PinyinHelper / ChnCharInfo.dll）----");

            Check("A1 全拼: 哔哩哔哩 → bilibili",
                PinyinHelper.GetFull("哔哩哔哩") == "bilibili", PinyinHelper.GetFull("哔哩哔哩"));
            Check("A2 首字母: 哔哩哔哩 → blbl",
                PinyinHelper.GetInitials("哔哩哔哩") == "blbl", PinyinHelper.GetInitials("哔哩哔哩"));
            Check("A3 全拼: 任务管理器 → renwuguanliqi",
                PinyinHelper.GetFull("任务管理器") == "renwuguanliqi", PinyinHelper.GetFull("任务管理器"));
            Check("A4 首字母: 任务管理器 → rwglq",
                PinyinHelper.GetInitials("任务管理器") == "rwglq", PinyinHelper.GetInitials("任务管理器"));

            string mixedFull = PinyinHelper.GetFull("微信 WeChat");
            string mixedInit = PinyinHelper.GetInitials("微信 WeChat");
            Check("A5 中英混合: 微信 WeChat → weixinwechat / wxwechat",
                mixedFull == "weixinwechat" && mixedInit == "wxwechat",
                mixedFull + " / " + mixedInit);

            Check("A6 空串/null 不抛且返回空串",
                PinyinHelper.GetFull("") == "" && PinyinHelper.GetInitials(null) == "", "");

            // 字符级缓存：同一批汉字第二次转换不再进转换器
            PinyinHelper.GetFull("网易云音乐播放器");
            int c0 = PinyinHelper.CachedCharCount;
            string again1 = PinyinHelper.GetFull("网易云音乐播放器");
            string again2 = PinyinHelper.GetFull("网易云音乐播放器");
            Check("A7 静态缓存: 重复转换不再新增缓存条目（一次汉字只转一次）",
                c0 == PinyinHelper.CachedCharCount && again1 == again2 && again1.Length > 0,
                "缓存 " + c0 + " → " + PinyinHelper.CachedCharCount);

            string chang1 = PinyinHelper.GetFull("长");
            string chang2 = PinyinHelper.GetFull("长");
            Check("A8 多音字取首读音且稳定（长 → 固定一个读音）",
                chang1.Length > 0 && chang1 == chang2, chang1);

            Check("A9 非汉字原样转小写: A-1_b → a1b（符号跳过）",
                PinyinHelper.GetFull("A-1_b") == "a1b", PinyinHelper.GetFull("A-1_b"));
        }

        // ============================================================
        // B 匹配口径
        // ============================================================
        private static void SectionB_Match(PathCache cache)
        {
            Line("");
            Line("---- B 匹配口径（名称=子串/首字母/全拼；目标·分组=只子串）----");

            var data = FullSearchModel.Build(MakeGroups(), cache, false, null, null);
            Check("B0 夹具: 可见条目 8 条 / 4 个分组", data.ItemCount == 8 && data.SectionCount == 4,
                "条目=" + data.ItemCount + " 节=" + data.SectionCount);

            Check("B1 中文子串: 记事 → 命中 记事本",
                Only(data.Query("记事"), "记事本"), Titles(data.Query("记事")));

            Check("B2 名称全拼: jishiben → 命中 记事本",
                Only(data.Query("jishiben"), "记事本"), Titles(data.Query("jishiben")));

            Check("B3 名称首字母: jsb → 命中 记事本",
                Only(data.Query("jsb"), "记事本"), Titles(data.Query("jsb")));

            Check("B4 大小写不敏感: JiShiBen / CALC.EXE 都命中",
                Only(data.Query("JiShiBen"), "记事本") && Only(data.Query("CALC.EXE"), "计算器"),
                Join(Titles(data.Query("JiShiBen"))) + " | " + Join(Titles(data.Query("CALC.EXE"))));

            Check("B5 目标子串: windows\\system32\\calc → 命中 计算器",
                Only(data.Query("windows\\system32\\calc"), "计算器"),
                Titles(data.Query("windows\\system32\\calc")));

            Check("B6 目标**不走**拼音: ziliao（资料）不命中任何条目",
                data.Query("ziliao").Count == 0, Titles(data.Query("ziliao")));

            Check("B7 分组**不走**拼音: gd（固定）/ wb（文本）不命中任何条目",
                data.Query("gd").Count == 0 && data.Query("wb").Count == 0,
                Titles(data.Query("gd")));

            Check("B8 分组名走子串: 固定 → 命中该分组下 5 条",
                Titles(data.Query("固定")).Count == 5, Titles(data.Query("固定")));

            Check("B9 查询串前后空白被忽略: \"  jsb  \" == \"jsb\"",
                Join(Titles(data.Query("  jsb  "))) == Join(Titles(data.Query("jsb"))),
                Join(Titles(data.Query("  jsb  "))));

            var q = data.Query("浏览器");
            Check("B10 查询结果首行是分组节头、且节头不可选（进程→固定）",
                q.Count == 4 && q[0].IsHeader && !q[0].IsSelectable
                && HeaderNames(q) == "进程,固定",
                (q.Count > 0 ? q[0].DisplayTitle : "空") + " header=" + (q.Count > 0 ? q[0].IsHeader : false)
                + " 行=" + q.Count + " 节头=" + HeaderNames(q));

            Check("B11 空节不出现（无命中 ⇒ 行数 0）",
                data.Query("zzz-不存在-zzz").Count == 0, "");

            // 空查询 = 全部有效条目，且分组序 + 组内序与配置一致
            var all = data.Query("");
            var allTitles = Titles(all);
            Check("B12 空查询 = 全部有效条目（menuOrder 默认: 动态→进程→固定，组内保序）",
                string.Join(",", allTitles.ToArray())
                == "缺件条目,打开临时目录,浏览器工具,记事本,计算器,谷歌浏览器,控制面板,系统信息",
                string.Join(",", allTitles.ToArray()));

            Check("B13 行数 = 条目 + 节头（8 + 4）且节头不重复",
                all.Count == 12 && CountHeaders(all) == 4,
                "行=" + all.Count + " 节头=" + CountHeaders(all));
        }

        // ============================================================
        // C 虚拟固定项
        // ============================================================
        private static void SectionC_Fixed(PathCache cache)
        {
            Line("");
            Line("---- C 虚拟固定项「全库搜索」----");

            // C1 空库：Build 出来是空菜单，追加后只有这一项
            var emptyBuilt = MenuBuilder.Build(new List<MenuGroup>());
            try
            {
                Check("C1 空库: Build 的可点条目 = 0（既有语义不变）",
                    emptyBuilt.ItemCount == 0, "ItemCount=" + emptyBuilt.ItemCount);

                int id = MenuBuilder.AppendFullSearch(emptyBuilt);
                Check("C2 空库: 固定项仍在（菜单最后一项 = 全库搜索）",
                    emptyBuilt.Menu.Count() == 1
                    && emptyBuilt.Menu.GetItemText(0) == MenuBuilder.FullSearchTitle
                    && id > 0,
                    "项数=" + emptyBuilt.Menu.Count()
                    + " 文本=[" + emptyBuilt.Menu.GetItemText(0) + "] id=" + id);

                Check("C3 固定项用保留 ID（IdMap 之外），不与条目 ID 撞",
                    id == MenuBuilder.FullSearchId
                    && MenuBuilder.FullSearchId == MenuBuilder.FixedCommandIdBase + 1
                    && !emptyBuilt.IdMap.ContainsKey(id),
                    "id=" + id + " 保留段起点=" + MenuBuilder.FixedCommandIdBase);

                Check("C4 空库时不再补分隔线（前面没有内容）",
                    emptyBuilt.Menu.Count() == 1, "项数=" + emptyBuilt.Menu.Count());
            }
            finally { emptyBuilt.Menu.Dispose(); }

            // C5 正常菜单：末尾 + 分隔线，且不影响条目计数
            var built = MenuBuilder.Build(MakeGroups());
            try
            {
                int pruneBefore = MenuBuilder.LastPrunedSubtrees;
                int itemsBefore = built.ItemCount;
                int iconHitBefore = built.IconHit;

                int id = MenuBuilder.AppendFullSearch(built);

                Check("C5 正常菜单: 固定项在**最后**（顶层最后一项）",
                    built.Menu.GetItemText(built.Menu.Count() - 1) == MenuBuilder.FullSearchTitle,
                    "最后项=[" + built.Menu.GetItemText(built.Menu.Count() - 1) + "]");

                Check("C6 正常菜单: 固定项前有分隔线（与组间分隔线同风格）",
                    built.Menu.Count() >= 2
                    && built.Menu.GetItemIdAt(built.Menu.Count() - 2) == 0,
                    "倒数第二项 id=" + built.Menu.GetItemIdAt(built.Menu.Count() - 2));

                Check("C7 固定项不计入可点条目（ItemCount 不变）",
                    built.ItemCount == itemsBefore, itemsBefore + " → " + built.ItemCount);

                Check("C8 固定项不参与剪枝（剪枝计数不受影响）",
                    MenuBuilder.LastPrunedSubtrees == pruneBefore,
                    pruneBefore + " → " + MenuBuilder.LastPrunedSubtrees);

                Check("C9 固定项 id 可被分流识别为 FullSearch",
                    RunEverApp.ClassifyPick(id, built) == RunEverApp.MenuPick.FullSearch,
                    "id=" + id);

                var firstId = 0;
                foreach (var kv in built.IdMap) { firstId = kv.Key; break; }
                Check("C10 普通条目 id 仍判为 Execute（分流不误伤）",
                    firstId != 0 && RunEverApp.ClassifyPick(firstId, built) == RunEverApp.MenuPick.Execute,
                    "条目 id=" + firstId);

                Check("C11 固定项图标 " + MenuBuilder.FullSearchIcon + " 真的抽到了（命中数 +1）",
                    built.IconHit == iconHitBefore + 1,
                    "图标命中 " + iconHitBefore + " → " + built.IconHit
                    + " 兜底=" + built.IconFallback);
            }
            finally { built.Menu.Dispose(); }

            // C11 隐藏失效把条目全隐藏（可点条目=0）⇒ 固定项依然在
            var allMissing = MakeAllMissingGroups();
            var missingBuilt = MenuBuilder.Build(
                FullSearchModel.VisibleLikeMenu(allMissing, cache, true, null));
            try
            {
                int hiddenItems = missingBuilt.ItemCount;
                int id = MenuBuilder.AppendFullSearch(missingBuilt);
                Check("C12 隐藏失效把条目全隐藏时: 固定项仍在（不参与隐藏失效）",
                    hiddenItems == 0 && id > 0
                    && missingBuilt.Menu.GetItemText(missingBuilt.Menu.Count() - 1) == MenuBuilder.FullSearchTitle,
                    "可见条目=" + hiddenItems + " 项数=" + missingBuilt.Menu.Count());
            }
            finally { missingBuilt.Menu.Dispose(); }
        }

        // ============================================================
        // D 搜索页条目集合 == 菜单可见集合
        // ============================================================
        private static void SectionD_VisibleSet(PathCache cache)
        {
            Line("");
            Line("---- D 可见集合：搜索页 == 菜单（同一 hideMissing 口径）----");

            var groups = MakeGroups();

            // hideMissing = 1
            // ★ 两边都用**同一份**可见分组 + 同一份 menuOrder 重排后再比 —— 这正是生产路径：
            //   RunEverApp 交给 MenuBuilder 的就是 decision.Flat(menuOrder)，搜索页用的是同一序。
            var visible = FullSearchModel.OrderByMenuOrder(
                FullSearchModel.VisibleLikeMenu(groups, cache, true, null), null);
            var data1 = FullSearchModel.Build(groups, cache, true, null, null);
            var menu1 = MenuBuilder.Build(visible);
            try
            {
                var searchTitles = Titles(data1.Query(""));
                var menuTitles = LeafTitles(visible);

                Check("D1 hideMissing=1: 搜索页条目数 == 菜单可点条目数",
                    data1.ItemCount == menu1.ItemCount,
                    data1.ItemCount + " / " + menu1.ItemCount);

                Check("D2 hideMissing=1: 条目集合与顺序完全一致",
                    Join(searchTitles) == Join(menuTitles),
                    "搜索=" + Join(searchTitles) + " 菜单=" + Join(menuTitles));

                Check("D3 hideMissing=1: 缺件条目在搜索页也看不到（不绕过过滤器）",
                    searchTitles.IndexOf("缺件条目") < 0 && searchTitles.IndexOf("缺件子项") < 0,
                    Join(searchTitles));

                Check("D4 hideMissing=1: 空壳子菜单（子项全缺）不占节",
                    data1.SectionCount == 3, "节数=" + data1.SectionCount);
            }
            finally { menu1.Menu.Dispose(); }

            // hideMissing = 0
            var visible0 = FullSearchModel.OrderByMenuOrder(
                FullSearchModel.VisibleLikeMenu(groups, cache, false, null), null);
            var data0 = FullSearchModel.Build(groups, cache, false, null, null);
            var menu0 = MenuBuilder.Build(visible0);
            try
            {
                var searchTitles0 = Titles(data0.Query(""));
                Check("D5 hideMissing=0: 缺件条目参与（不过滤）且集合仍与菜单一致",
                    searchTitles0.IndexOf("缺件条目") >= 0
                    && data0.ItemCount == menu0.ItemCount
                    && Join(searchTitles0) == Join(LeafTitles(visible0)),
                    "搜索=" + data0.ItemCount + " 菜单=" + menu0.ItemCount
                    + " 含缺件=" + (searchTitles0.IndexOf("缺件条目") >= 0));
            }
            finally { menu0.Menu.Dispose(); }

            // menuOrder：三段顺序（默认 dynamic,process,always；自定义 always,process,dynamic）
            var def = FullSearchModel.Build(groups, cache, false, MenuOrderSpec.Default, null);
            var rev = FullSearchModel.Build(groups, cache, false,
                new[] { MenuOrderSpec.Always, MenuOrderSpec.Process, MenuOrderSpec.Dynamic }, null);

            Check("D6 分组顺序跟 auto.conf 的 menuOrder: 默认 = 文本 → 兜底 → 进程 → 固定",
                SectionNames(def) == "文本,兜底,进程,固定", SectionNames(def));
            Check("D7 分组顺序跟 auto.conf 的 menuOrder: 反序 = 固定 → 进程 → 文本 → 兜底",
                SectionNames(rev) == "固定,进程,文本,兜底", SectionNames(rev));
            Check("D8 换顺序不丢分组（两向都是 4 节）",
                def.SectionCount == 4 && rev.SectionCount == 4,
                def.SectionCount + " / " + rev.SectionCount);

            // 手改配置可能出现两个同名分组 ⇒ 必须各占一节（按名字分节会把两节并成一节）
            var dupName = new List<MenuGroup>
            {
                new MenuGroup { Name = "工具", Type = GroupType.Always, Line = 1,
                    Items = new List<MenuItem> { It("甲", ActionType.Keys, "^a") } },
                new MenuGroup { Name = "工具", Type = GroupType.Always, Line = 9,
                    Items = new List<MenuItem> { It("乙", ActionType.Keys, "^b") } }
            };
            var dupData = FullSearchModel.Build(dupName, null, false, null, null);
            Check("D9 同名分组各占一节（不按名字合并）",
                dupData.SectionCount == 2 && CountHeaders(dupData.Query("")) == 2
                && Titles(dupData.Query("")).Count == 2,
                "节=" + dupData.SectionCount + " 节头=" + HeaderNames(dupData.Query("")));
        }

        // ============================================================
        // E 执行语义
        // ============================================================
        private static void SectionE_Execute(PathCache cache)
        {
            Line("");
            Line("---- E 执行语义（选择 → Executor，与菜单点击同一条）----");

            var built = MenuBuilder.Build(new List<MenuGroup>());
            try
            {
                int fixedId = MenuBuilder.AppendFullSearch(built);
                Check("E1 分流表: 取消(0) = None / 未知 ID = Unknown",
                    RunEverApp.ClassifyPick(0, built) == RunEverApp.MenuPick.None
                    && RunEverApp.ClassifyPick(987654, built) == RunEverApp.MenuPick.Unknown,
                    "0 / 987654");

                Check("E2 分流表: 固定项 = FullSearch（先判固定项、再查 IdMap）",
                    RunEverApp.ClassifyPick(fixedId, built) == RunEverApp.MenuPick.FullSearch,
                    "id=" + fixedId);
            }
            finally { built.Menu.Dispose(); }

            var groups = MakeGroups();
            var data = FullSearchModel.Build(groups, cache, false, null, null);
            var menu = MenuBuilder.Build(FullSearchModel.VisibleLikeMenu(groups, cache, false, null));
            try
            {
                // 找到「记事本」那条：搜索页给出 MenuItem，菜单点击给出 IdMap 里的同一条
                var row = FindRow(data.Query("jsb"), "记事本");
                Check("E3 搜索页能定位到目标条目（记事本）",
                    row != null && row.Item != null && row.Item.Target == "existsprog.exe",
                    row == null ? "未命中" : row.Item.Target);

                if (row == null) return;

                // 假 handler：模拟"用户点了它" —— 断言真的把这一条交出去
                MenuItem handed = null;
                Action<MenuItem> handler = delegate (MenuItem it) { handed = it; };
                handler(row.Item);

                Check("E4 假 handler 收到的就是被选条目（目标不被改写/代换）",
                    handed != null && ReferenceEquals(handed, row.Item)
                    && handed.Target == "existsprog.exe",
                    handed == null ? "空" : handed.Target);

                // 菜单侧的同一条（经 IdMap 派发的那条）
                MenuItem menuItem = null;
                foreach (var kv in menu.IdMap)
                {
                    if (kv.Value != null && kv.Value.Title == "记事本") { menuItem = kv.Value; break; }
                }

                var ctx = new Context
                {
                    Text = "选中的文本",
                    Files = new List<string> { "C:\\a.txt" },
                    Proc = "chrome",
                    WinTitle = "标题",
                    Clipboard = "剪贴板"
                };
                var tSearch = RunEverApp.BuildDispatchTask(handed, ctx, null);
                var tMenu = RunEverApp.BuildDispatchTask(menuItem, ctx, null);

                Check("E5 执行载荷同一构造: type/target/flag/上下文全等（搜索 == 菜单点击）",
                    tSearch != null && tMenu != null
                    && tSearch.Type == tMenu.Type
                    && tSearch.Target == tMenu.Target
                    && tSearch.Flag == tMenu.Flag
                    && tSearch.Text == tMenu.Text
                    && tSearch.Proc == tMenu.Proc
                    && tSearch.WinTitle == tMenu.WinTitle
                    && tSearch.Clipboard == tMenu.Clipboard,
                    tSearch == null || tMenu == null ? "空载荷"
                        : tSearch.Type + " " + tSearch.Target + " text=" + tSearch.Text.Length + "B");

                Check("E6 载荷形态: type 小写、目标原样、文件列表与采集一致",
                    tSearch.Type == "run" && tSearch.Target == "existsprog.exe"
                    && tSearch.Files != null && tSearch.Files.Count == 1,
                    tSearch.Type + " files=" + (tSearch.Files == null ? -1 : tSearch.Files.Count));
            }
            finally { menu.Menu.Dispose(); }
        }

        // ============================================================
        // F 窗口契约（不开窗）
        // ============================================================
        private static void SectionF_WindowContract()
        {
            Line("");
            Line("---- F 窗口契约（无 UI 断言）----");

            Check("F1 默认尺寸 480×400（用户定案）",
                FullSearchWindow.DefaultWidth == 480 && FullSearchWindow.DefaultHeight == 400,
                FullSearchWindow.DefaultWidth + "×" + FullSearchWindow.DefaultHeight);

            Check("F2 按键表: Esc=关 / Enter=执行 / ↑↓=选择 / 其他=放行",
                FullSearchWindow.ResolveKey(Key.Escape) == FullSearchKeyIntent.Cancel
                && FullSearchWindow.ResolveKey(Key.Enter) == FullSearchKeyIntent.Accept
                && FullSearchWindow.ResolveKey(Key.Up) == FullSearchKeyIntent.MoveUp
                && FullSearchWindow.ResolveKey(Key.Down) == FullSearchKeyIntent.MoveDown
                && FullSearchWindow.ResolveKey(Key.A) == FullSearchKeyIntent.None,
                "");

            // 关闭即释放的结构证据：类里不许有静态窗口/数据字段（有 = 常驻实例）
            var statics = typeof(FullSearchWindow).GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            bool resident = false;
            string residentName = "";
            foreach (var f in statics)
            {
                if (typeof(Window).IsAssignableFrom(f.FieldType)
                    || f.FieldType == typeof(FullSearchData)
                    || f.FieldType == typeof(FullSearchSection)
                    || f.FieldType == typeof(List<FullSearchRow>))
                {
                    resident = true;
                    residentName = f.Name + ":" + f.FieldType.Name;
                    break;
                }
            }
            Check("F3 无静态常驻实例（窗口/数据都不许挂在静态字段上）",
                !resident, resident ? "发现静态字段 " + residentName : "静态字段 " + statics.Length + " 个（全是常量）");

            var rows = new List<FullSearchRow>
            {
                new FullSearchRow { IsHeader = true, DisplayTitle = "组" },
                new FullSearchRow { IsHeader = false, Item = new MenuItem { Title = "x" } }
            };
            Check("F4 节头不可选 / 条目可选（节头在窗口里是 disabled 项）",
                !rows[0].IsSelectable && rows[1].IsSelectable, "");

            // 只读浏览：构建不改传入的分组（非破坏性）
            var groups = MakeGroups();
            int before = CountItems(groups);
            var d1 = FullSearchModel.Build(groups, null, false, null, null);
            var d2 = FullSearchModel.Build(groups, null, false, null, null);
            Check("F5 只读浏览: 构建前后条目数不变、两次构建结果一致（不改 conf 对象）",
                CountItems(groups) == before && d1.ItemCount == d2.ItemCount,
                before + " → " + CountItems(groups) + " 构建 " + d1.ItemCount + "/" + d2.ItemCount);

            // 窗口能真正造出来（不 Show：本套不弹 UI）—— 把"模板/样式/绑定写错"挡在门内
            FullSearchWindow win = null;
            bool created = false;
            string err = "";
            try
            {
                win = new FullSearchWindow(d1, null);      // root=null ⇒ 不读写 state 尺寸文件
                created = win != null && win.Content != null;
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (win != null) { try { win.Release(); } catch { } }
            }
            Check("F6 窗口可构造（模板/样式/绑定/尺寸读取都不抛）且关闭即释放", created, err);
        }

        // ============================================================
        // 夹具
        // ============================================================
        /// <summary>
        /// 四组夹具（配置序：固定(always) → 文本(ext) → 进程(proc) → 兜底(fallback)）：
        ///   固定: 记事本 / 计算器 / 谷歌浏览器 / 深层(子菜单) → 控制面板 / 系统信息
        ///   文本: 缺件条目（本机不存在）
        /// ★ 目标里刻意放中文目录（"资料"），用来证明"目标只走子串、不走拼音"。
        /// ★ 层级从 **1** 起：与 menus.conf 的真实缩进一致（3 空格 ⇒ level=1）；
        ///   0 级在 MenuBuilder 的层级栈里没有意义（那是"未缩进"的容忍值，不是菜单顶层）。
        /// </summary>
        private static List<MenuGroup> MakeGroups()
        {
            var always = new MenuGroup { Name = "固定", Type = GroupType.Always, Line = 1 };
            always.Items.Add(It("记事本", ActionType.Run, "existsprog.exe"));
            always.Items.Add(It("计算器", ActionType.Run, "C:\\Windows\\System32\\calc.exe"));
            always.Items.Add(It("谷歌浏览器", ActionType.Url, "https://www.google.com"));
            always.Items.Add(It("深层", ActionType.Group, "", 1));
            // ★ 目标带 % 占位符 ⇒ 缺件判定直接跳过（不依赖本机有没有这个 lnk），
            //   同时保留中文目录"资料"用来证明"目标只走子串、不走拼音"。
            always.Items.Add(It("控制面板", ActionType.Open, "%userprofile%\\资料\\控制面板.lnk", 2));
            always.Items.Add(It("系统信息", ActionType.Run, "existsprog.exe", 2));

            var ext = new MenuGroup { Name = "文本", Type = GroupType.Ext, Line = 20 };
            ext.Keywords.Add("txt");
            ext.Items.Add(It("缺件条目", ActionType.Run, "staleprog.exe"));

            var proc = new MenuGroup { Name = "进程", Type = GroupType.Proc, Line = 30 };
            proc.Keywords.Add("chrome");
            proc.Items.Add(It("浏览器工具", ActionType.Url, "https://example.com/chrome"));

            var fallback = new MenuGroup { Name = "兜底", Type = GroupType.Fallback, Line = 40 };
            fallback.Items.Add(It("打开临时目录", ActionType.Open, "%temp%"));

            return new List<MenuGroup> { always, ext, proc, fallback };
        }

        /// <summary>全部条目本机都解析不到（隐藏失效会把可点条目清 0）—— 固定项必须仍在。</summary>
        private static List<MenuGroup> MakeAllMissingGroups()
        {
            var g = new MenuGroup { Name = "全缺", Type = GroupType.Always, Line = 1 };
            g.Items.Add(It("缺一", ActionType.Run, "no-such-prog-aaa.exe"));
            g.Items.Add(It("缺二", ActionType.Open, "C:\\no-such-dir-xyz\\a.txt"));
            return new List<MenuGroup> { g };
        }

        private static MenuItem It(string title, ActionType t, string target, int level = 1)
        {
            return new MenuItem { Title = title, Type = t, Target = target, Level = level };
        }

        // ============================================================
        // 断言辅助
        // ============================================================

        /// <summary>查询结果里"可选条目"的标题（跳过节头）。</summary>
        private static List<string> Titles(List<FullSearchRow> rows)
        {
            var l = new List<string>();
            foreach (var r in rows) if (r.IsSelectable) l.Add(r.DisplayTitle);
            return l;
        }

        private static int CountHeaders(List<FullSearchRow> rows)
        {
            int n = 0;
            foreach (var r in rows) if (r.IsHeader) n++;
            return n;
        }

        /// <summary>行列表里的节头名（按出现顺序；用来断言分节顺序）。</summary>
        private static string HeaderNames(List<FullSearchRow> rows)
        {
            var l = new List<string>();
            foreach (var r in rows) if (r.IsHeader) l.Add(r.DisplayTitle);
            return Join(l);
        }

        private static string Join(List<string> l)
        {
            return l == null ? "" : string.Join(",", l.ToArray());
        }

        private static int CountItems(List<MenuGroup> groups)
        {
            int n = 0;
            if (groups != null)
                foreach (var g in groups)
                    if (g != null && g.Items != null) n += g.Items.Count;
            return n;
        }

        /// <summary>查询结果恰好是这一条（且只有这一条）。</summary>
        private static bool Only(List<FullSearchRow> rows, string title)
        {
            var t = Titles(rows);
            return t.Count == 1 && t[0] == title;
        }

        private static FullSearchRow FindRow(List<FullSearchRow> rows, string title)
        {
            foreach (var r in rows) if (r.IsSelectable && r.DisplayTitle == title) return r;
            return null;
        }

        /// <summary>菜单会渲染出来的可点条目标题（递归进子菜单，与 MenuBuilder 的 ItemCount 同口径）。</summary>
        private static List<string> LeafTitles(List<MenuGroup> groups)
        {
            var l = new List<string>();
            if (groups == null) return l;
            foreach (var g in groups)
                foreach (var it in g.Items)
                    if (it != null && !it.IsGroupHeader) l.Add(it.Title);
            return l;
        }

        private static string SectionNames(FullSearchData data)
        {
            var names = new List<string>();
            foreach (var s in data.Sections) names.Add(s.Name);
            return string.Join(",", names.ToArray());
        }

        // ============================================================
        // 报表
        // ============================================================
        private static void Header(string root, string sandbox)
        {
            Sb.AppendLine("=== 全库搜索自检 · S1 · 2026-11-10（第 22 套）===");
            Sb.AppendLine("root   = " + root);
            Sb.AppendLine("用法   = RunEverCs.exe --selftest-search <root> <out.txt>（禁带 --root）");
            Sb.AppendLine("口径   = 虚拟固定项恒在 / 搜索页可见集合 = 菜单可见集合 / 名称走子串+首字母+全拼 /");
            Sb.AppendLine("         目标·分组只走子串 / 执行载荷与菜单点击同一构造 / 全离线无 UI");
            Sb.AppendLine("沙箱   = " + sandbox);
            Sb.AppendLine();
        }

        private static void Line(string s) { Sb.AppendLine(s); }

        private static void Check(string name, bool ok, string detail)
        {
            _total++;
            if (!ok) _failed++;
            Sb.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name
                + (string.IsNullOrEmpty(detail) ? "" : "   ← " + detail));
        }

        /// <summary>明细为标题列表时的便捷重载（排版成 a,b,c）。</summary>
        private static void Check(string name, bool ok, List<string> detail)
        {
            Check(name, ok, detail == null ? "" : string.Join(",", detail.ToArray()));
        }

        private static int Finish(string outFile)
        {
            Sb.AppendLine();
            Sb.AppendLine("[计数] 断言 " + _total + " 条 / 失败 " + _failed + " 条");
            Sb.AppendLine();
            Sb.AppendLine(_failed == 0
                ? "结果: 全部通过 ✓"
                : "结果: " + _failed + " 项失败 ✗");

            try { File.WriteAllText(outFile, Sb.ToString(), new UTF8Encoding(false)); } catch { }
            Console.WriteLine(Sb.ToString());
            return _failed == 0 ? 0 : 1;
        }
    }
}
