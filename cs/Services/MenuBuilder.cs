using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Native;

namespace RunEverCs.Services
{
    /// <summary>
    /// 菜单渲染器 —— 把「选中的分组列表」渲染成 Win32 原生菜单。
    ///
    /// 与 AHK 版 RunEver.BuildMenu 的语义对齐：
    ///   · 顶层平铺（不显示分组名），组间插分隔线
    ///   · 条目按 Level 建真子菜单（层级栈维护父子关系）
    ///   · 同名条目天然共存（命令 ID 与显示文本分离）
    /// </summary>
    public class MenuBuilder
    {
        public class BuildResult
        {
            public NativeMenu Menu;
            public Dictionary<int, MenuItem> IdMap = new Dictionary<int, MenuItem>();
            public int ItemCount;
            public int IconHit;
            public int IconFallback;

            /// <summary>
            /// 本次构建里 `off`（临时禁用）条目数 —— **不在 <see cref="IdMap"/> 里**、不计入
            /// <see cref="ItemCount"/>（ItemCount 的口径仍是"可点条目"，与 IdMap 一一对应）。
            /// 供自检 / 日志区分"看不到"与"看得到但点不动"。
            /// </summary>
            public int GrayedCount;

            /// <summary>
            /// 降级重建说明（空 = 本次没有截断，正常构建）。
            /// ★ 文本含 <c>已截断 N 项（原 M 项）</c>，与 <see cref="MenuBuilder.LastTruncationNote"/>
            ///   同一份 —— 托盘 / 自检 / 日志共用一条证据，不各写各的。
            /// </summary>
            public string TruncationNote = "";

            /// <summary>
            /// 固定项「全库搜索」的命令 ID（0 = 本次没有追加）。
            /// ★ 它**不在** <see cref="IdMap"/> 里 —— 派发方必须先判这个字段，再查 IdMap
            ///   （见 RunEverApp.ShowMenuFlow 的 MenuPick 分流）。
            /// </summary>
            public int FixedSearchId;

            /// <summary>
            /// 虚拟项「选中的文件」的命令 ID（0 = 本次没有选中文件 ⇒ 未插入）。
            /// ★ 同 <see cref="FixedSearchId"/>：不在 IdMap 里，派发方先判字段再查 IdMap。
            /// </summary>
            public int SelectedFileId;
        }

        // ============================================================
        // ★ S1（2026-11-10）：虚拟固定项 —— 「全库搜索」
        //
        // 用户定案：菜单**最后**恒有这一项，且**虚拟化**：
        //   · 不进 menus.conf（配置里根本没有这一行）
        //   · 不占条目 ID（用保留段 0x7E00–0x7EFF；条目 ID 由 NativeMenu.AllocId 从 1 递增）
        //   · 不可编辑/删除（不进 IdMap ⇒ 条目编辑器、回写、保存校验闸都看不见它）
        //   · 不参与剪枝/隐藏失效（**Build 之后**才追加，任何过滤都碰不到它）
        //   · 只在渲染层末尾追加（本方法就是唯一入口）
        //
        // ★ 为什么不塞进 Build()：`Build` 的语义是"分组 → 菜单"，固定项不属于配置；
        //   塞进去会让每个调用方（含全部既有自检的菜单计数）凭空多一条与配置无关的项。
        //   追加点收在 `RunEverApp.ShowMenuFlow` 弹菜单前一行。
        // ============================================================

        /// <summary>固定项显示文本。无快捷键、无 `&` 助记（它不该抢按键）。</summary>
        public const string FullSearchTitle = "全库搜索";

        /// <summary>固定项图标：shell32.dll 的放大镜（用户定案）。</summary>
        public const string FullSearchIcon = "SHELL32.dll,22";

        /// <summary>虚拟固定项的保留命令 ID 段起点（0x7E00–0x7EFF）。</summary>
        public const int FixedCommandIdBase = 0x7E00;

        /// <summary>「全库搜索」的命令 ID（IdMap 之外的保留 ID，与条目 ID 不重叠）。</summary>
        public const int FullSearchId = FixedCommandIdBase + 1;

        /// <summary>
        /// 在菜单**末尾**追加固定项「全库搜索」，返回它的命令 ID（0 = 追加失败）。
        /// 菜单非空时先补一条分隔线（与"组间分隔线"同一风格），避免与最后一条条目粘连。
        /// ★ 条目数（<see cref="BuildResult.ItemCount"/>）不因它变化 —— 它不是一个配置条目。
        /// </summary>
        public static int AppendFullSearch(BuildResult built)
        {
            if (built == null || built.Menu == null) return 0;
            var menu = built.Menu;

            // 先放分隔线：Count()>0 说明上面已经有内容（含"全空菜单"的禁用占位项）
            if (menu.Count() > 0) menu.AddSeparator();

            int id = FullSearchId;
            if (built.IdMap != null && built.IdMap.ContainsKey(id))
            {
                // 理论不可达（从 1 递增的分配器要产出 0x7E01 得有 32256 个条目）。
                // 真撞上就退化成"正常分配一个 ID" —— 宁可多占一个 ID，也不许点错派发。
                id = menu.AddItem(FullSearchTitle);
            }
            else
            {
                // 固定 ID 只能绕过 NativeMenu.AddItem（它强制分配新 ID）⇒ 直接走 Win32，
                // flags 与 AddItem 完全相同，只有 ID 由我们指定。
                if (!Win32.AppendMenuW(menu.Handle, Win32.MF_STRING, new IntPtr(id), FullSearchTitle))
                    return 0;
            }

            if (menu.SetIconById(id, FullSearchIcon)) built.IconHit++;
            else built.IconFallback++;

            built.FixedSearchId = id;
            return id;
        }

        // ============================================================
        // ★ S2（2026-11-11）：虚拟固定项 —— 「选中的文件」（置顶第一项）
        //
        // 用户定案：有选中文件时，菜单**最前**恒有这一项；点它 = 打开选中的文件。
        //   · 单选：显示文件名；多选：显示「打开选中的 N 个文件」
        //   · 深度复用执行器：Task{Type="open", Target="", Files=ctx.Files}
        //     ⇒ Executor 的 U-D 循环体逐文件追加引号路径（每个文件一次，天然并发）
        //   · 多选确认框在 RunEverApp.OpenSelectedFiles（渲染层不管交互）
        //   · 位置靠 InsertMenuW(0) 实现（AppendMenu 只能加末尾）
        // ============================================================

        /// <summary>虚拟项「选中的文件」的命令 ID。</summary>
        public const int SelectedFileId = FixedCommandIdBase + 2;

        /// <summary>置顶项的兜底图标（通用文件）。</summary>
        public const string SelectedFileIcon = "SHELL32.dll,0";

        /// <summary>
        /// 在菜单**最前**插入虚拟项「选中的文件」，返回命令 ID（0 = 没插）。
        /// ★ 不选中文件 ⇒ 返回 0，不占菜单位。
        /// </summary>
        public static int AppendSelectedFile(BuildResult built, IList<string> files)
        {
            if (built == null || built.Menu == null || files == null || files.Count == 0) return 0;

            string title;
            if (files.Count == 1)
            {
                string name;
                try { name = Path.GetFileName(files[0]); }
                catch { name = files[0]; }
                if (string.IsNullOrEmpty(name)) name = files[0];
                if (name.Length > 40) name = name.Substring(0, 37) + "…";
                title = "打开  " + name;
            }
            else
            {
                title = "打开选中的 " + files.Count + " 个文件";
            }

            int id = SelectedFileId;
            if (built.IdMap != null && built.IdMap.ContainsKey(id)) return 0;

            // 位置 0 + MF_BYPOSITION ⇒ 插到最前（这正是不用 AppendMenu 的原因）
            if (!Win32.InsertMenuW(built.Menu.Handle, 0,
                Win32.MF_BYPOSITION | Win32.MF_STRING, new IntPtr(id), title))
                return 0;

            // 图标：按第一个文件取；取不到退通用文件图标（不许因图标失败丢整项）
            string icon = SelectedFileIcon;
            try { if (File.Exists(files[0])) icon = files[0] + ",0"; } catch { }
            if (built.Menu.SetIconById(id, icon)) built.IconHit++;
            else if (icon != SelectedFileIcon && built.Menu.SetIconById(id, SelectedFileIcon)) built.IconHit++;
            else built.IconFallback++;

            // 用户要求（2026-11-11）：虚拟项后面补一条分隔线，与下方配置条目视觉分隔
            Win32.InsertMenuW(built.Menu.Handle, 1,
                Win32.MF_BYPOSITION | Win32.MF_SEPARATOR, IntPtr.Zero, null);

            built.SelectedFileId = id;
            return id;
        }

        /// <summary>2026-11-07 分段计时（只读探针，不改行为）—— 用于决定"缓存什么才真正省时间"。</summary>
        public static long LastBuildMs;
        public static long LastItemMs;
        public static long LastIconMs;

        /// <summary>本次构建里被**渲染护栏**挡掉的空壳子菜单数（正常为 0；与剪枝统计同源展示）。</summary>
        public static int LastPrunedSubtrees;

        /// <summary>本次构建里被挡掉的空壳子菜单标题（日志用）。</summary>
        public static List<string> LastPrunedTitles = new List<string>();

        // ============================================================
        // ★ 2026-11-09（U-B）：`open sub <目录>` —— 渲染期动态子菜单
        //   只在这条 open 落到**本次已命中**的段里才枚举（Win32 菜单无懒加载）；
        //   枚举结果只进 NativeMenu / idmap，**绝不写回 menus.conf**。
        // ============================================================

        /// <summary>本次构建真的建出来的目录子菜单数（含递归展开的层级）。</summary>
        public static int LastSubmenus;

        /// <summary>本次构建动态生成的子项数（= 可点条目里来自目录枚举的那部分）。</summary>
        public static int LastSubmenuItems;

        /// <summary>本次构建里标记了 sub、但目录不存在/无权限/为空 ⇒ 退化为普通 open 的条目数。</summary>
        public static int LastSubmenuFallbacks;

        /// <summary>本次构建里目录枚举命中 mtime 缓存的次数（性能证据：第二遍构建不重扫）。</summary>
        public static int LastDirCacheHits;

        /// <summary>构建菜单（不显示，纯构建，供自检调用）。conf = null ⇒ 不展开子菜单（历史行为）。</summary>
        public static BuildResult Build(List<MenuGroup> groups)
        {
            return Build(groups, null);
        }

        // ============================================================
        // ★ 卡 A（2026-11-17）：构建失败 ⇒ 降级重建（别整盘放弃）
        //
        // 现场：单菜单超过 1168 项（本机实测 Win32 硬上限）时 AppendMenuW 返回 FALSE ⇒
        //   NativeMenu.AddItem 抛 InvalidOperationException ⇒ 整棵菜单构建中断 ⇒ 一个都不弹。
        // 定案（本批 HANDOFF 决策表）：
        //   · 捕获构建期异常后按「**每个菜单各自**截断到 N 项」重建（不是砍掉后半张配置）；
        //   · N 从 1000 起、失败减半（500/250/125/62），最多 5 轮，取第一个成功的；
        //   · 成功即返回可用 BuildResult（**不抛**），写日志「已截断 N 项（原 M 项）」；
        //   · 5 轮全失败才向上抛 —— 那时确实不是"项数太多"能解释的问题。
        // ============================================================

        /// <summary>降级重建的起始单菜单项数上限（实测硬上限 1168，留余量）。</summary>
        public const int DegradeFirstLimit = 1000;

        /// <summary>降级重建最多尝试轮数（1000 / 500 / 250 / 125 / 62）。</summary>
        public const int DegradeMaxRounds = 5;

        /// <summary>
        /// 降级日志出口（托盘可注入主日志）。未注入 ⇒ 追加到**已存在**的
        /// <c>&lt;root&gt;\state\runever-cs.log</c>（找得到 root 且文件已存在时；不主动创建文件）。
        /// </summary>
        public static Action<string> LogSink;

        /// <summary>最近一次构建的降级说明（空 = 没降级）。自检 / 日志共用同一份文本。</summary>
        public static string LastTruncationNote = "";

        /// <summary>
        /// 构建菜单。<paramref name="conf"/> = auto.conf 的内存快照（AutoMatcher.Conf）。
        /// ★ 只有 <c>SubmenuEnabled=1</c> 才展开 `open sub`；其余键（limit/order/recursive/depth）
        ///   全来自这一份快照 —— 与 AutoMatcher 的解析同源，不在这里重读文件。
        /// ★ 构建期异常 ⇒ 按「每个菜单各自截断」降级重建，见本段开头说明。
        /// </summary>
        public static BuildResult Build(List<MenuGroup> groups, AutoConf conf)
        {
            LastTruncationNote = "";
            var owned = new List<NativeMenu>();     // 每次尝试的根菜单（失败时当场销毁，不留半成品句柄）

            try
            {
                return BuildCore(groups, conf, 0, owned);
            }
            catch (Exception first)
            {
                DisposeAll(owned);

                int original = CountEffectiveEntries(groups);
                int limit = DegradeFirstLimit;
                Exception last = first;

                for (int round = 1; round <= DegradeMaxRounds && limit >= 1; round++)
                {
                    try
                    {
                        var built = BuildCore(groups, conf, limit, owned);
                        string note = "菜单构建失败（" + first.GetType().Name + ": " + first.Message
                            + "）⇒ 已截断 " + built.ItemCount + " 项（原 " + original
                            + " 项；单菜单上限=" + limit + "，第 " + round + " 轮）";
                        built.TruncationNote = note;
                        LastTruncationNote = note;
                        EmitLog(note);
                        return built;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        DisposeAll(owned);
                        limit /= 2;
                    }
                }

                EmitLog("菜单构建降级 " + DegradeMaxRounds + " 轮仍失败（最后异常: "
                    + last.GetType().Name + ": " + last.Message + "）");
                throw last;
            }
        }

        /// <summary>销毁本轮尝试留下的根菜单（Dispose 幂等：已销毁的 Handle 已是 Zero）。</summary>
        private static void DisposeAll(List<NativeMenu> menus)
        {
            if (menus == null) return;
            foreach (var m in menus)
            {
                try { if (m != null) m.Dispose(); } catch { }
            }
            menus.Clear();
        }

        /// <summary>
        /// 一次真实构建。<paramref name="perMenuLimit"/> &gt; 0 ⇒ 每个菜单各自最多这么多**有效项**
        /// （含 root；分隔线不占额度；一个子菜单项占父菜单 1 个额度）。0 = 不限（正常路径，行为不变）。
        /// </summary>
        private static BuildResult BuildCore(List<MenuGroup> groups, AutoConf conf, int perMenuLimit,
            List<NativeMenu> ownedRoots)
        {
            if (groups == null) groups = new List<MenuGroup>();

            var swAll = System.Diagnostics.Stopwatch.StartNew();
            long iconMs = 0;
            var root = new NativeMenu();
            if (ownedRoots != null) ownedRoots.Add(root);
            var idmap = new Dictionary<int, MenuItem>();
            int total = 0, iconHit = 0, iconFallback = 0, grayed = 0;
            var counts = new Dictionary<IntPtr, int>();   // 每个菜单当前项数（键 = HMENU）

            LastPrunedSubtrees = 0;
            LastPrunedTitles = new List<string>();
            LastSubmenus = 0;
            LastSubmenuItems = 0;
            LastSubmenuFallbacks = 0;
            LastDirCacheHits = 0;

            // ★ 重置全局 ID 分配器：保证本次菜单树内 ID 唯一且从 1 起
            //   （跨菜单实例共享分配器 ⇒ 子菜单不再与根菜单撞 ID）
            NativeMenu.ResetIdAllocator();

            bool firstGroup = true;
            int lastTopLevel = 0;

            foreach (var g in groups)
            {
                if (g == null || g.Items == null || g.Items.Count == 0) continue;

                // ★ 渲染护栏：子树里没有任何可点条目 ⇒ 连子菜单都不建（永不生成 0 项子菜单）
                var emptyShells = FindEmptyShells(g.Items);
                if (emptyShells.Count >= g.Items.Count) continue;    // 整组都是空壳 ⇒ 不占一行/一条分隔线

                // 组间分隔线（第一组不加）；分隔线不占有效项额度，但菜单已满时也不再补
                if (!firstGroup && HasRoom(counts, root, perMenuLimit)) root.AddSeparator();
                firstGroup = false;

                // 层级栈：stack[depth] = 该深度对应的菜单
                var stack = new List<NativeMenu>();
                stack.Add(root);

                int prevLevel = 0;
                foreach (var it in g.Items)
                {
                    if (it == null || emptyShells.Contains(it)) continue;
                    int lv = it.Level;

                    // 从 prevLevel 走到 lv：上读时弹栈；跳级时用栈顶补齐（★ 与 CountPerMenu 同一份算术）
                    SyncStackDepth(stack, lv);

                    var target = stack[stack.Count - 1];

                    // ★ 条目型分割线（配置文件里一行 `---`）：
                    //   · 只在"当前菜单已有内容"时插，避免首条就是分割线、或连续两条粘在一起；
                    //   · 不占命令 ID、不进 IdMap、不参与点击派发（纯渲染）；
                    //   · 不改变 stack，因此它不打断父子连续性（层级不变式不受影响）。
                    if (it.IsSeparator)
                    {
                        if (target.Count() > 0 && HasRoom(counts, target, perMenuLimit)) target.AddSeparator();
                        continue;
                    }

                    if (it.IsGroupHeader)
                    {
                        // 本菜单已满 ⇒ 本条连同其整棵子树一起跳过（子项也只会落在这个菜单里）
                        if (!TryAddRow(counts, target, perMenuLimit)) continue;

                        // 分组标题 → 建真子菜单
                        var sub = new NativeMenu();
                        int subIdx = target.AddSubMenu(FormatMenuText(it.Title, it.Hotkey, true), sub);

                        // ★ 2026-11-08：子菜单项用**系统文件夹图标**（popup 项没有命令 ID ⇒ 按位置设）。
                        //   子菜单没有动作 ⇒ 本来就没有图标源；按用户口径统一给文件夹图标，
                        //   不再"继承第一个子项的图标"。
                        long tIconS = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (target.SetFolderIconByPosition(subIdx)) iconHit++;
                        else iconFallback++;
                        iconMs += (System.Diagnostics.Stopwatch.GetTimestamp() - tIconS)
                                  * 1000L / System.Diagnostics.Stopwatch.Frequency;

                        stack.Add(sub);
                    }
                    else
                    {
                        // 本菜单已满 ⇒ 跳过本条（占位规则同上：一个叶条目 / 一个目录子菜单项都算 1 行）
                        if (!TryAddRow(counts, target, perMenuLimit)) continue;

                        // ★ U-B：`open sub <目录>` —— 目录可枚举就当场建子菜单，否则落到下面的普通叶分支
                        //   ★ 2026-11-17 定案：**取消全局闸门**（原 `submenuEnabled` 总开关）。
                        //     只有显式 sub（条目编辑页「以子菜单展示」勾选 / menus.conf 写 `open sub`）才展开；
                        //     普通 `open <目录>` 条目**永不**自动展开，一律当普通叶（避免大目录把菜单撑爆）。
                        //     旧键 `submenuEnabled` 仍可读（兼容旧配置），但不再参与任何判定。
                        //   ★ 卡 E：带 `off` 的条目不展开子菜单（灰显的父项点不开），直接按灰显叶条目渲染。
                        if (it.Type == ActionType.Open && conf != null && it.Sub && !it.Off)
                        {
                            var sr = TryBuildDirectoryMenu(it.Target, conf, 1, idmap);
                            if (sr != null)
                            {
                                int subIdx = target.AddSubMenu(
                                    FormatMenuText(it.Title, it.Hotkey, true), sr.Menu);

                                // 子菜单项本身用系统文件夹图标（popup 项没有命令 ID ⇒ 按位置设）
                                long tIconS = System.Diagnostics.Stopwatch.GetTimestamp();
                                if (target.SetFolderIconByPosition(subIdx)) iconHit++;
                                else iconFallback++;
                                iconMs += (System.Diagnostics.Stopwatch.GetTimestamp() - tIconS)
                                          * 1000L / System.Diagnostics.Stopwatch.Frequency;

                                total += sr.Items;
                                iconHit += sr.IconHit;
                                iconFallback += sr.IconFallback;
                                LastSubmenus += 1 + sr.Submenus;
                                LastSubmenuItems += sr.Items;

                                prevLevel = lv;
                                continue;
                            }

                            // 目录不存在/无权限/为空 ⇒ 退化普通 open（绝不建空子菜单）
                            LastSubmenuFallbacks++;
                        }

                        // 叶条目
                        string text = FormatMenuText(it.Title, it.Hotkey, false);

                        // ★ 卡 E（2026-11-17b）：`off` ⇒ **照常出现在菜单但灰显**、点了不执行。
                        //   · 用既有的 `AddItemDisabled`（MF_GRAYED：禁用 + 变灰，一条 flag 就够）；
                        //   · **不进 idmap** ⇒ 派发方查不到这个 ID ⇒ 就算点到也什么都不做；
                        //   · 不计入 ItemCount（它不可点），另计 GrayedCount（自检/日志要能分开数）；
                        //   · 与 `hideMissing`（缺件隐藏）正交：那个是"看不到"，这个是"看得到点不动"。
                        int id;
                        if (it.Off)
                        {
                            id = target.AddItemDisabled(text);
                            grayed++;
                        }
                        else
                        {
                            id = target.AddItem(text);
                            idmap[id] = it;
                            total++;
                        }

                        // 图标
                        long tIcon0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (!string.IsNullOrEmpty(it.IconSource))
                        {
                            if (target.SetIconById(id, it.IconSource)) iconHit++;
                            else iconFallback++;
                        }
                        else if (it.Type == ActionType.Open && IsDirectoryTarget(it))
                        {
                            // ★ 2026-11-10：文件夹条目（open 指向目录）用**系统文件夹图标**，
                            //   不再走程序兜底图标（兜底是放大镜，对文件夹是错的）。
                            if (target.SetFolderIconById(id)) iconHit++;
                            else iconFallback++;
                        }
                        else
                        {
                            // 无图标源 → 兜底图标
                            if (target.SetIconById(id, "")) iconFallback++;
                        }
                        iconMs += (System.Diagnostics.Stopwatch.GetTimestamp() - tIcon0)
                                  * 1000L / System.Diagnostics.Stopwatch.Frequency;
                    }

                    prevLevel = lv;
                }

                lastTopLevel++;
            }

            LastIconMs = iconMs;
            LastBuildMs = swAll.ElapsedMilliseconds;
            LastItemMs = LastBuildMs - iconMs;

            return new BuildResult
            {
                Menu = root,
                IdMap = idmap,
                ItemCount = total,
                GrayedCount = grayed,
                IconHit = iconHit,
                IconFallback = iconFallback
            };
        }

        /// <summary>
        /// 层级栈算术（**Build 与 --check-conf 共用这一份**）：把栈调整到 <paramref name="level"/> 深。
        /// 上读弹栈、跳级用栈顶补齐 —— 与 ConfParser.NormalizeStructure / SubtreePruner 同款口径。
        ///
        /// ★ 2026-11-17（卡 A）：弹栈**保底留 root**（stack[0] 恒是根菜单）。
        ///   原写法 `while (Count &gt; level)` 遇到 `Level=0` 的条目会把 root 也弹掉 ⇒
        ///   `stack[-1]` 抛 ArgumentOutOfRangeException ⇒ 整棵菜单崩（手写配置"顶格条目"必中）。
        ///   对 `level &gt;= 1` 的输入，两种写法**完全等价**（`Count &gt; level` ⇒ `Count &gt; 1`），
        ///   所以现役配置 / 设置页产物 / 全部既有自检的行为一个字没变。
        /// </summary>
        internal static void SyncStackDepth<T>(List<T> stack, int level)
        {
            if (stack == null || stack.Count == 0) return;
            while (stack.Count > 1 && stack.Count > level)
                stack.RemoveAt(stack.Count - 1);
            while (stack.Count < level)
                stack.Add(stack[stack.Count - 1]);
        }

        /// <summary>该菜单是否还有额度（分隔线用：只看不占）。limit &lt;= 0 ⇒ 不限流，恒 true。</summary>
        private static bool HasRoom(Dictionary<IntPtr, int> counts, NativeMenu menu, int perMenuLimit)
        {
            if (perMenuLimit <= 0) return true;
            int c;
            counts.TryGetValue(menu.Handle, out c);
            return c < perMenuLimit;
        }

        /// <summary>占一行额度（有效项用）。返回 false = 该菜单已满，本行应跳过。</summary>
        private static bool TryAddRow(Dictionary<IntPtr, int> counts, NativeMenu menu, int perMenuLimit)
        {
            if (perMenuLimit <= 0) return true;
            int c;
            counts.TryGetValue(menu.Handle, out c);
            if (c >= perMenuLimit) return false;
            counts[menu.Handle] = c + 1;
            return true;
        }

        /// <summary>
        /// 配置里「本该渲染成可点行 / 子菜单行的条目」总数（不含分隔线、不含空壳子树）——
        /// 降级日志里的「原 M 项」。与 Build 的跳过口径一致，但不展开 `open sub` 目录。
        /// </summary>
        private static int CountEffectiveEntries(List<MenuGroup> groups)
        {
            if (groups == null) return 0;
            int n = 0;
            foreach (var g in groups)
            {
                if (g == null || g.Items == null || g.Items.Count == 0) continue;
                var emptyShells = FindEmptyShells(g.Items);
                if (emptyShells.Count >= g.Items.Count) continue;
                foreach (var it in g.Items)
                    if (it != null && !it.IsSeparator && !emptyShells.Contains(it)) n++;
            }
            return n;
        }

        /// <summary>
        /// 降级日志出口：注入了 <see cref="LogSink"/> 就走调用方；否则追加到**已存在**的
        /// <c>&lt;root&gt;\state\runever-cs.log</c>（与托盘的 runever-cs.log 同一份）。
        /// 不主动创建文件 —— 自检临时目录 / 只读引用零副作用；任何写失败都不影响构建。
        /// </summary>
        private static void EmitLog(string line)
        {
            if (LogSink != null)
            {
                try { LogSink(line); } catch { }
                return;
            }

            try
            {
                string root = GuessRootDir();
                if (string.IsNullOrEmpty(root)) return;
                string log = Path.Combine(root, "state", "runever-cs.log");
                if (!File.Exists(log)) return;    // 不创建：没有日志就没有输出
                File.AppendAllText(log,
                    DateTime.Now.ToString("HH:mm:ss") + " [menu] " + line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>从 exe 目录向上找含 <c>config\menus.conf</c> 的根本（与 Program.FindRoot 同口径，5 层）。</summary>
        private static string GuessRootDir()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, "config", "menus.conf"))) return dir;
                }
                catch { return null; }

                string parent = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(parent) || parent == dir) break;
                dir = parent;
            }
            return null;
        }

        /// <summary>open 条目的目标是不是可枚举目录（无模板占位符 + 目录存在）。</summary>
        private static bool IsDirectoryTarget(MenuItem it)
        {
            if (it == null || it.Type != ActionType.Open) return false;
            string t = (it.Target ?? "").Trim(' ', '\t', '"');
            if (t.Length == 0 || t.IndexOf('%') >= 0) return false;
            try { return System.IO.Directory.Exists(t); }
            catch { return false; }
        }

        /// <summary>
        /// 菜单文本格式化 —— 让快捷键【真正可点】。
        ///
        /// ★★ 关键（2026-10-03 用户实测暴露）：
        ///   Win32 菜单的快捷键靠 `&X` 前缀（X 会带下划线，按 X 即可激活），
        ///   而不是 `[X]` 这种纯文本提示。
        ///   AHK 版只写了 `\t[X]` —— 看起来像快捷键，实际【按了没反应】。
        ///   这里改用 `&X`，Tab 后的 `[X]` 仅作视觉提示保留。
        ///
        /// 规则：
        ///   · 标题里若已有 `&`，不再重复注入（尊重手写）
        ///   · 找到快捷键字母在标题中的位置 → 在其前插 `&`
        ///   · ★ 2026-10-06（用户拍板 A 方案）：标题里**没有**这个字母时（中文标题常态，
        ///     如「任务管理器 _T」），**不再回退成纯提示**，而是补一个 `(&T)` 后缀 ——
        ///     让字母成为标题的一部分 ⇒ Win32 才有地方挂助记。**不改配置语义**（配置里
        ///     仍是 `_T`），只是渲染层补全。补后缀的条目不再加 `\t[X]`（避免同字母显示两次）。
        ///
        /// ⚠️ 光有 `&` 还不够：菜单还得真的拿到键盘焦点（见 `NativeMenu.Show` 的抢焦点）。
        ///    两个成因各自独立，缺一个都表现为"快捷键无效"。
        /// </summary>
        public static string FormatMenuText(string title, string hotkey, bool isSubMenu)
        {
            if (string.IsNullOrEmpty(title)) title = "";

            bool injected = false;
            bool suffixFallback = false;   // ★ 补了 `(&X)` 后缀

            if (!string.IsNullOrEmpty(hotkey) && hotkey.Length >= 1)
            {
                string key = hotkey.Substring(0, 1);
                if (title.IndexOf('&') < 0)
                {
                    int pos = title.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                    if (pos < 0)
                        pos = title.IndexOf(key.ToUpperInvariant(), StringComparison.Ordinal);
                    if (pos < 0)
                        pos = title.IndexOf(key.ToLowerInvariant(), StringComparison.Ordinal);

                    if (pos >= 0)
                    {
                        title = title.Substring(0, pos) + "&" + title.Substring(pos);
                        injected = true;
                    }
                    else
                    {
                        // ★ A 方案：标题不含该字母（中文标题常态）⇒ 补 `(&X)` 后缀
                        title += "(&" + key.ToUpperInvariant() + ")";
                        injected = true;
                        suffixFallback = true;
                    }
                }
                else
                {
                    injected = true;   // 标题自带 & → 视为已注入
                }
            }

            // Tab 后的提示文本：Win32 会把 Tab 右侧右对齐显示
            // ★ 补后缀的条目跳过（字母已经在标题里了）
            if (!string.IsNullOrEmpty(hotkey) && !suffixFallback)
            {
                if (!isSubMenu)
                    title += "\t[" + hotkey + "]";
                else if (!injected)
                    title += "\t[" + hotkey + "]";
            }

            return title;
        }

        /// <summary>
        /// 渲染护栏：找出"子树里没有任何可点条目"的组标题，连同其整棵子树一起返回（调用方跳过不渲染）。
        ///
        /// 为什么还要这一层：剪枝是数据层的收尾，但渲染是最后一道闸 —— 任何来源的分组列表
        /// （自检构造 / 未来新调用方）都不该让 Win32 建出 0 项 popup（点开一片空白）。
        /// 判据只看"这个标题跨度内有没有非标题条目"，与层级栈口径一致。
        /// </summary>
        private static HashSet<MenuItem> FindEmptyShells(IList<MenuItem> items)
        {
            var hide = new HashSet<MenuItem>();
            if (items == null) return hide;

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it == null || !it.IsGroupHeader) continue;

                int j = i + 1;
                int visible = 0;
                while (j < items.Count && items[j] != null && items[j].Level > it.Level)
                {
                    // 分割线不是"可点条目"，不能让一个只含分割线的空壳子菜单逃过剪枝
                    if (!items[j].IsGroupHeader && !items[j].IsSeparator) visible++;
                    j++;
                }

                if (visible > 0) continue;

                for (int m = i; m < j; m++) hide.Add(items[m]);
                LastPrunedSubtrees++;
                LastPrunedTitles.Add(it.Title);
                i = j - 1;
            }
            return hide;
        }

        // ============================================================
        // ★ U-B：目录子菜单（渲染期枚举，不写回配置）
        // ============================================================

        /// <summary>一个目录的枚举快照（按目录 mtime 缓存；只读）。</summary>
        private sealed class DirListing
        {
            public long Stamp;                                   // 目录 LastWriteTimeUtc.Ticks
            public readonly List<string> Dirs = new List<string>();
            public readonly List<string> Files = new List<string>();
        }

        /// <summary>一棵动态子菜单的构建产物（计数回传给 Build 汇总）。</summary>
        private sealed class SubMenuBuild
        {
            public NativeMenu Menu;
            public int Items;
            public int IconHit;
            public int IconFallback;
            public int Submenus;
        }

        /// <summary>目录枚举缓存：mtime 未变 ⇒ 0 次目录枚举（只 stat 一次）。</summary>
        private static readonly Dictionary<string, DirListing> DirCache =
            new Dictionary<string, DirListing>(StringComparer.OrdinalIgnoreCase);
        private static readonly object DirCacheLock = new object();

        /// <summary>缓存上限（超过就整体清空 —— 菜单只碰用户点过的那几个目录，64 足够）。</summary>
        private const int DirCacheLimit = 64;

        /// <summary>
        /// 把一条 `open sub` 的目录展开成一棵子菜单。
        /// 返回 null = **退化普通 open**（目录不存在 / 无权限 / 为空 / 枚举异常）。
        ///
        /// depth 从 1 起（第一层展开 = 1）；只有 `SubmenuRecursive=1` 且
        /// `depth &lt; SubmenuDepth` 时才把子目录再展开 —— 默认递归关，只展开一层。
        /// </summary>
        private static SubMenuBuild TryBuildDirectoryMenu(string target, AutoConf conf, int depth,
            Dictionary<int, MenuItem> idmap)
        {
            string dir = NormalizeDirectoryTarget(target);
            if (dir.Length == 0) return null;

            var dirs = new List<string>();
            var files = new List<string>();
            if (!ReadDirectory(dir, dirs, files)) return null;

            // ---- 排序：folder-first（目录在前）/ name（纯按名称）----
            var ordered = new List<KeyValuePair<string, bool>>();   // Key = 全路径，Value = 是否目录
            bool byName = string.Equals(conf.SubmenuOrder, "name", StringComparison.OrdinalIgnoreCase);
            if (byName)
            {
                foreach (var d in dirs) ordered.Add(new KeyValuePair<string, bool>(d, true));
                foreach (var f in files) ordered.Add(new KeyValuePair<string, bool>(f, false));
                ordered.Sort(delegate (KeyValuePair<string, bool> a, KeyValuePair<string, bool> b)
                {
                    return StringComparer.CurrentCultureIgnoreCase.Compare(
                        Path.GetFileName(a.Key), Path.GetFileName(b.Key));
                });
            }
            else
            {
                // ReadDirectory 已按名称排好；目录段在前，文件段在后
                foreach (var d in dirs) ordered.Add(new KeyValuePair<string, bool>(d, true));
                foreach (var f in files) ordered.Add(new KeyValuePair<string, bool>(f, false));
            }

            // ---- 上限截断（防大目录把菜单撑爆）----
            int limit = conf.SubmenuLimit;
            if (limit < 1) limit = 1;
            if (ordered.Count > limit) ordered.RemoveRange(limit, ordered.Count - limit);

            // 空目录 / 全被过滤 ⇒ 不建子菜单（与 F1 剪枝口径一致）
            if (ordered.Count == 0) return null;

            var menu = new NativeMenu();
            var result = new SubMenuBuild { Menu = menu };
            try
            {
                foreach (var e in ordered)
                {
                    string name = Path.GetFileName(e.Key);
                    if (name.Length == 0) name = e.Key;
                    // 文件名里的 & 在 Win32 菜单里是助记符前缀 ⇒ 双写才是字面量（"R&D.txt" 不能变成 R_D.txt）
                    string label = name.Replace("&", "&&");

                    // 递归：只有显式开启且还没到层数上限
                    if (e.Value && conf.SubmenuRecursive == 1 && depth < conf.SubmenuDepth)
                    {
                        var nested = TryBuildDirectoryMenu(e.Key, conf, depth + 1, idmap);
                        if (nested != null)
                        {
                            int idx = menu.AddSubMenu(label, nested.Menu);
                            if (menu.SetFolderIconByPosition(idx)) result.IconHit++;
                            else result.IconFallback++;

                            result.Items += nested.Items;
                            result.Submenus += 1 + nested.Submenus;
                            continue;
                        }
                    }

                    // 叶：点它 = open 这个文件/目录（与普通 open 条目同一条派发路径）
                    int id = menu.AddItem(label);
                    idmap[id] = new MenuItem
                    {
                        Title = name,
                        Type = ActionType.Open,
                        Target = e.Key,
                        Level = 0
                    };
                    result.Items++;

                    int pos = menu.Count() - 1;
                    bool iconOk = e.Value ? menu.SetFolderIconByPosition(pos)
                                          : menu.SetFileIconByPosition(pos);
                    if (iconOk) result.IconHit++;
                    else result.IconFallback++;
                }
            }
            catch
            {
                // 建菜单中途失败 ⇒ 整棵丢弃，调用方退化为普通 open
                menu.Dispose();
                return null;
            }

            return result;
        }

        /// <summary>
        /// 读一个目录（缓存优先）。返回 false = 不存在 / 无权限 / IO 错 ⇒ 调用方退化普通 open。
        /// ★ 缓存键 = 目录全路径；失效判据 = 目录 LastWriteTimeUtc（增删条目才会变）。
        ///   命中缓存时只花一次 stat，不枚举整棵树 —— 这是"每次弹菜单不重扫"的关键。
        /// </summary>
        private static bool ReadDirectory(string dir, List<string> outDirs, List<string> outFiles)
        {
            DirectoryInfo di;
            long stamp;
            try
            {
                di = new DirectoryInfo(dir);
                if (!di.Exists) return false;
                stamp = di.LastWriteTimeUtc.Ticks;
            }
            catch { return false; }

            lock (DirCacheLock)
            {
                DirListing hit;
                if (DirCache.TryGetValue(dir, out hit) && hit.Stamp == stamp)
                {
                    outDirs.AddRange(hit.Dirs);
                    outFiles.AddRange(hit.Files);
                    LastDirCacheHits++;
                    return true;
                }
            }

            var fresh = new DirListing { Stamp = stamp };
            try
            {
                foreach (var fsi in di.GetFileSystemInfos())
                {
                    try
                    {
                        FileAttributes a = fsi.Attributes;
                        // 隐藏/系统条目不进菜单（与资源管理器默认视图口径一致）
                        if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if ((a & FileAttributes.Directory) != 0) fresh.Dirs.Add(fsi.FullName);
                        else fresh.Files.Add(fsi.FullName);
                    }
                    catch { }   // 单个条目读属性失败 ⇒ 跳过，不影响整目录
                }
            }
            catch { return false; }

            fresh.Dirs.Sort(StringComparer.CurrentCultureIgnoreCase);
            fresh.Files.Sort(StringComparer.CurrentCultureIgnoreCase);

            outDirs.AddRange(fresh.Dirs);
            outFiles.AddRange(fresh.Files);

            lock (DirCacheLock)
            {
                if (DirCache.Count >= DirCacheLimit) DirCache.Clear();
                DirCache[dir] = fresh;
            }
            return true;
        }

        /// <summary>目标 → 目录路径：去首尾空白与包裹引号，展开环境变量。空串 = 不可用。</summary>
        private static string NormalizeDirectoryTarget(string target)
        {
            string t = (target ?? "").Trim();
            if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"')
                t = t.Substring(1, t.Length - 2).Trim();
            if (t.Length == 0) return "";
            try { t = Environment.ExpandEnvironmentVariables(t); }
            catch { }
            return t;
        }

        /// <summary>递归收集菜单树里所有叶条目的命令 ID（用于跨菜单冲突检测）。</summary>
        private static void CollectIds(NativeMenu menu, List<int> ids, int depth)
        {
            if (menu == null || menu.Handle == IntPtr.Zero || depth > 8) return;
            int n = menu.Count();
            for (int i = 0; i < n; i++)
            {
                int id = menu.GetItemIdAt(i);
                if (id == -1)
                {
                    // 子菜单项 → 递归
                    IntPtr sh = menu.GetSubMenuHandle(i);
                    if (sh != IntPtr.Zero)
                    {
                        var sub = NativeMenu.FromHandle(sh);
                        CollectIds(sub, ids, depth + 1);
                    }
                }
                else if (id > 0)
                {
                    ids.Add(id);
                }
            }
        }

        /// <summary>自检：断言菜单结构与 AHK 版一致（不弹菜单）。</summary>
        public static string SelfTest(List<MenuGroup> groups)
        {
            var sb = new StringBuilder();
            var r = Build(groups);
            try
            {
                sb.AppendLine("顶层项数 = " + r.Menu.Count());
                sb.AppendLine("可点条目 = " + r.ItemCount);
                sb.AppendLine("灰显条目（off） = " + r.GrayedCount);
                sb.AppendLine("图标: 命中=" + r.IconHit + " 兜底=" + r.IconFallback);

                // ---- 断言 0：命令 ID 全局唯一（★ 关键回归断言）----
                //   历史 bug：每个菜单实例各自从 1 编号 ⇒ 子菜单与根菜单撞 ID ⇒
                //   idmap 被覆盖 ⇒ 点任何 ID=1 的项都执行"计算器"。
                var idSeen = new Dictionary<int, MenuItem>();
                int dupIds = 0;
                foreach (var kv in r.IdMap)
                {
                    if (idSeen.ContainsKey(kv.Key)) dupIds++;
                    else idSeen[kv.Key] = kv.Value;
                }
                bool idOk = (dupIds == 0) && (r.IdMap.Count == r.ItemCount);
                sb.AppendLine("ID 唯一断言: idmap项数=" + r.IdMap.Count
                    + " 可点条目=" + r.ItemCount
                    + " 重复=" + dupIds
                    + " → " + (idOk ? "OK 全局唯一 ✓" : "FAIL"));

                // 收集所有子菜单里的 ID，确认不与根菜单撞车
                var allIds = new List<int>();
                CollectIds(r.Menu, allIds, 0);
                var idSet = new HashSet<int>();
                int crossDup = 0;
                foreach (var id in allIds)
                    if (!idSet.Add(id)) crossDup++;
                sb.AppendLine("跨菜单 ID 冲突检测: 全部叶项=" + allIds.Count
                    + " 唯一=" + idSet.Count
                    + " 冲突=" + crossDup
                    + " → " + (crossDup == 0 ? "OK 无冲突 ✓" : "FAIL(会有错点)"));

                // ---- 断言 1：同名项共存 ----
                // 构造一个含两条同名项的菜单，验证 ID 不同、文本相同
                var dupMenu = new NativeMenu();
                int id1 = dupMenu.AddItem("Rename");
                int id2 = dupMenu.AddItem("Rename");
                bool dupOk = (dupMenu.Count() == 2) && (id1 != id2)
                    && (dupMenu.GetItemText(0) == dupMenu.GetItemText(1));
                sb.AppendLine("重名断言: 项数=" + dupMenu.Count()
                    + " id=" + id1 + "/" + id2
                    + " 文本相同=" + (dupMenu.GetItemText(0) == dupMenu.GetItemText(1))
                    + " → " + (dupOk ? "OK 同名项共存 ✓" : "FAIL"));
                dupMenu.Dispose();

                // ---- 断言 2：子菜单层级 ----
                int subCount = r.Menu.CountSubMenus();
                sb.AppendLine("层级断言: 顶层子菜单数=" + subCount
                    + " → " + (subCount >= 1 ? "OK ✓" : "FAIL(应为 >=1)"));
                for (int i = 0; i < subCount && i < 8; i++)
                {
                    IntPtr sh = r.Menu.GetSubMenuHandle(i);
                    if (sh != IntPtr.Zero)
                    {
                        int n = Win32.GetMenuItemCount(sh);
                        sb.AppendLine("  子菜单[" + i + "] 项数=" + n);
                    }
                }

                // ---- 断言 3：组间分隔线 ----
                int sepCount = 0;
                for (int i = 0; i < r.Menu.Count(); i++)
                {
                    if (Win32.GetMenuItemID(r.Menu.Handle, i) == 0) sepCount++;
                }
                sb.AppendLine("分隔线数 = " + sepCount);

                sb.AppendLine("结果: " + (dupOk && subCount >= 1 ? "全部通过 ✓" : "有失败"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex.Message);
            }
            finally
            {
                r.Menu.Dispose();
            }
            return sb.ToString();
        }

        // ============================================================
        // ★ 卡 A（2026-11-17）：--check-conf 的计数内核
        // ============================================================

        /// <summary>一个菜单的项数（<see cref="Path"/> = 从 root 起的容器路径，便于点名）。</summary>
        public sealed class MenuCount
        {
            public string Path;
            public int Items;
        }

        /// <summary>
        /// 按 menus.conf 结构数「每个菜单各自多少项」（**不建任何 Win32 句柄**）。
        ///
        /// 与 BuildCore 同一套语义（不是另写一套近似逻辑）：
        ///   · 空分组 / 整组都是空壳 ⇒ 跳过；
        ///   · `Level` 的父子关系走**同一份** <see cref="SyncStackDepth"/>（上读弹栈、跳级补栈）；
        ///   · 子菜单标题在父菜单里占 1 行，同时自己另起一个菜单计数（容器换栈顶）；
        ///   · `---` 与组间分隔线**不占有效项**。
        /// ★ 不展开 `open sub` 目录（那要看磁盘与 auto.conf；目录内容另有 submenuLimit 兜底）——
        ///   预检要抓的是"配置本身写得太大"，与 App 走哪台机器无关。
        /// </summary>
        public static List<MenuCount> CountPerMenu(List<MenuGroup> groups)
        {
            var rootNode = new MenuCount { Path = "（顶层 root）" };
            var all = new List<MenuCount> { rootNode };
            if (groups == null) return all;

            foreach (var g in groups)
            {
                if (g == null || g.Items == null || g.Items.Count == 0) continue;

                var emptyShells = FindEmptyShells(g.Items);
                if (emptyShells.Count >= g.Items.Count) continue;

                var stack = new List<MenuCount> { rootNode };

                foreach (var it in g.Items)
                {
                    if (it == null || emptyShells.Contains(it)) continue;

                    SyncStackDepth(stack, it.Level);
                    var target = stack[stack.Count - 1];
                    if (target == null) continue;

                    if (it.IsSeparator) continue;    // 分隔线不占有效项

                    if (it.IsGroupHeader)
                    {
                        target.Items++;              // 容器在父菜单里占 1 行
                        var node = new MenuCount
                        {
                            Path = UniquePath(all, target.Path + " / " + (it.Title ?? ""))
                        };
                        all.Add(node);
                        stack.Add(node);             // 容器换栈顶：更深的条目归它
                    }
                    else
                    {
                        target.Items++;
                    }
                }
            }
            return all;
        }

        /// <summary>同级重名容器加 `#2/#3` 后缀 —— 两个同名子菜单本来就是两个菜单，不能合并计数。</summary>
        private static string UniquePath(List<MenuCount> all, string path)
        {
            if (FindPath(all, path) == null) return path;
            for (int n = 2; ; n++)
            {
                string p = path + " #" + n;
                if (FindPath(all, p) == null) return p;
            }
        }

        private static MenuCount FindPath(List<MenuCount> all, string path)
        {
            foreach (var m in all) if (m.Path == path) return m;
            return null;
        }
    }

    /// <summary>
    /// `--check-conf`：menus.conf 规模预检 —— 弹菜单**之前**就报"哪个菜单多少项 / 会不会炸"。
    ///
    /// 判据（本批 HANDOFF 决策表）：单菜单 &gt;900 项 ⇒ WARN；&gt;1168 项 ⇒ fatal。
    /// 计数口径与 <see cref="MenuBuilder.Build"/> **同一套分栈语义**（见 MenuBuilder.CountPerMenu）：
    /// 空壳子树跳过、分隔符不占有效项、容器换栈顶。
    ///
    /// 用法：
    ///   RunEverCs.exe --check-conf &lt;menus.conf&gt; &lt;out.txt&gt;        （**不收 root**，与 --selftest-menu 同形）
    ///   RunEverCs.exe --selftest-checkconf &lt;root|menus.conf&gt; &lt;out.txt&gt;
    ///
    /// 退出码：有 fatal ⇒ 3；只有 WARN ⇒ 0（报告里标明）；全正常 ⇒ 0；参数/文件/解析失败 ⇒ 2。
    /// </summary>
    public static class ConfCheck
    {
        /// <summary>告警阈值（单菜单项数）。</summary>
        public const int WarnThreshold = 900;

        /// <summary>fatal 阈值（单菜单项数，本机实测 Win32 菜单硬上限 1168）。</summary>
        public const int FatalThreshold = 1168;

        private const int TopN = 20;

        public static int Run(string[] args)
        {
            if (args == null || args.Length < 3) return 2;

            string report;
            int rc = Evaluate(args[1], out report);
            WriteText(args[2], report);
            return rc;
        }

        /// <summary>
        /// 预检核心（CLI 与自检共用）：返回 3 = 有 fatal / 0 = 正常或仅 WARN / 2 = 无法预检。
        /// 报告文本经 <paramref name="report"/> 返回（WinExe 无 stdout，一律写文件）。
        /// </summary>
        public static int Evaluate(string confOrRoot, out string report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== menus.conf 规模预检（--check-conf）===");

            string confPath = confOrRoot;
            try
            {
                if (Directory.Exists(confPath))
                    confPath = Path.Combine(confPath, "config", "menus.conf");

                if (!File.Exists(confPath))
                {
                    sb.AppendLine("  参数错误：配置文件不存在 → " + confPath);
                    sb.AppendLine("  用法：RunEverCs.exe --check-conf <menus.conf> <out.txt>（不收 root）");
                    sb.AppendLine("结果: 无法预检 ✗（exit=2）");
                    report = sb.ToString();
                    return 2;
                }

                var p = new ConfParser(confPath);
                p.Parse();

                var menus = MenuBuilder.CountPerMenu(p.Groups);

                int rowCount = 0;
                foreach (var g in p.Groups)
                {
                    if (g == null || g.Items == null) continue;
                    foreach (var it in g.Items)
                        if (it != null && !it.IsSeparator) rowCount++;
                }

                var fatal = new List<MenuBuilder.MenuCount>();
                var warn = new List<MenuBuilder.MenuCount>();
                foreach (var m in menus)
                {
                    if (m.Items > FatalThreshold) fatal.Add(m);
                    else if (m.Items > WarnThreshold) warn.Add(m);
                }

                var sorted = new List<MenuBuilder.MenuCount>(menus);
                sorted.Sort(delegate (MenuBuilder.MenuCount a, MenuBuilder.MenuCount b)
                {
                    int c = b.Items.CompareTo(a.Items);
                    return c != 0 ? c : string.Compare(a.Path, b.Path, StringComparison.Ordinal);
                });

                sb.AppendLine("  配置    : " + Path.GetFullPath(confPath));
                sb.AppendLine("  时间    : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("  分组数  : " + p.Groups.Count + "   条目行: " + rowCount + "（不含分隔线）");
                sb.AppendLine("  阈值    : 单菜单 >" + WarnThreshold + " 项 = WARN；>"
                    + FatalThreshold + " 项 = fatal（exit=3）");
                sb.AppendLine("  口径    : 与 MenuBuilder 同一分栈（Level 父子 / 分隔符不占项 / 空壳子树跳过）；"
                    + "不展开 open sub 目录");
                sb.AppendLine();
                sb.AppendLine("[菜单项数 Top " + TopN + "]（降序，共 " + menus.Count + " 个菜单）");
                for (int i = 0; i < sorted.Count && i < TopN; i++)
                {
                    var m = sorted[i];
                    sb.AppendLine(string.Format("  {0,2}) {1,6} 项  {2,-6} {3}",
                        i + 1, m.Items, LevelTag(m.Items), m.Path));
                }
                if (sorted.Count > TopN)
                    sb.AppendLine("  … 其余 " + (sorted.Count - TopN) + " 个菜单省略（超限清单不受 Top "
                        + TopN + " 限制）");

                sb.AppendLine();
                sb.AppendLine("[超限清单]");
                if (fatal.Count + warn.Count == 0)
                {
                    sb.AppendLine("  （无：所有菜单都 ≤ " + WarnThreshold + " 项）");
                }
                else
                {
                    foreach (var m in fatal)
                        sb.AppendLine("  FATAL  " + m.Path + " = " + m.Items + " 项（> " + FatalThreshold
                            + "，弹菜单会被截断或整盘失败）");
                    foreach (var m in warn)
                        sb.AppendLine("  WARN   " + m.Path + " = " + m.Items + " 项（> " + WarnThreshold
                            + "，接近上限，建议拆组）");
                }

                sb.AppendLine();
                if (fatal.Count > 0)
                    sb.AppendLine("结果: fatal " + fatal.Count + " 项 ⇒ exit=3（必须先拆组/瘦身）");
                else if (warn.Count > 0)
                    sb.AppendLine("结果: 仅 WARN " + warn.Count + " 项 ⇒ exit=0（能弹，但已在危险区）");
                else
                    sb.AppendLine("结果: 全部正常 ⇒ exit=0");

                report = sb.ToString();
                return fatal.Count > 0 ? 3 : 0;
            }
            catch (Exception ex)
            {
                sb.AppendLine("  异常：" + ex.GetType().Name + ": " + ex.Message);
                sb.AppendLine("结果: 无法预检 ✗（exit=2）");
                report = sb.ToString();
                return 2;
            }
        }

        private static string LevelTag(int items)
        {
            if (items > FatalThreshold) return "FATAL";
            if (items > WarnThreshold) return "WARN";
            return "";
        }

        private static void WriteText(string path, string text)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch { }
        }

        // ============================================================
        // --selftest-checkconf：预检 + 与 Build 同构 + 降级重建（A2）的自检
        // ============================================================

        public static int SelfTestRun(string[] args)
        {
            if (args == null || args.Length < 3) return 2;

            string confOrRoot = args[1];
            string outPath = args[2];
            var sb = new StringBuilder();
            int fails = 0;

            sb.AppendLine("=== 配置预检自检（--selftest-checkconf）===");
            sb.AppendLine("  时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            string liveConf = Directory.Exists(confOrRoot)
                ? Path.Combine(confOrRoot, "config", "menus.conf")
                : confOrRoot;
            sb.AppendLine("  现役配置: " + liveConf);

            string tmpRoot = Path.Combine(Path.GetTempPath(),
                "runever-checkconf-" + Guid.NewGuid().ToString("N"));

            try
            {
                // ---- ① 现役配置：exit=0，无 fatal ----
                string rep;
                int rc = Evaluate(liveConf, out rep);
                bool ok1 = rc == 0;
                if (!ok1) fails++;
                sb.AppendLine((ok1 ? "[OK]   " : "[FAIL] ")
                    + "① 现役配置预检 exit=" + rc + "（期望 0）  " + FirstFatalLine(rep));

                // ---- ② 单菜单 2000 项 ⇒ exit=3，且点名该菜单与项数 ----
                string bigConf = WriteConf(tmpRoot, "big", BuildBigConf("巨量菜单", 2000));
                rc = Evaluate(bigConf, out rep);
                bool ok2 = rc == 3
                        && rep.IndexOf("巨量菜单", StringComparison.Ordinal) >= 0
                        && rep.IndexOf("2000", StringComparison.Ordinal) >= 0
                        && rep.IndexOf("FATAL", StringComparison.Ordinal) >= 0;
                if (!ok2) fails++;
                sb.AppendLine((ok2 ? "[OK]   " : "[FAIL] ")
                    + "② 单菜单 2000 项: exit=" + rc + "（期望 3）  点名含「巨量菜单」+2000+FATAL="
                    + (rep.IndexOf("巨量菜单", StringComparison.Ordinal) >= 0)
                    + "/" + (rep.IndexOf("2000", StringComparison.Ordinal) >= 0)
                    + "/" + (rep.IndexOf("FATAL", StringComparison.Ordinal) >= 0));

                // ---- ③ 单菜单 950 项 ⇒ 只 WARN、exit=0（阈值分界：>900 才告警）----
                string warnConf = WriteConf(tmpRoot, "warn", BuildBigConf("临界菜单", 950));
                rc = Evaluate(warnConf, out rep);
                bool ok3 = rc == 0
                        && rep.IndexOf("WARN", StringComparison.Ordinal) >= 0
                        && rep.IndexOf("临界菜单", StringComparison.Ordinal) >= 0;
                if (!ok3) fails++;
                sb.AppendLine((ok3 ? "[OK]   " : "[FAIL] ")
                    + "③ 单菜单 950 项: exit=" + rc + "（期望 0）  报告标 WARN="
                    + (rep.IndexOf("WARN", StringComparison.Ordinal) >= 0));

                // ---- ④ 与 Build 同构：计数 == 真实菜单行数（root + 每个容器子菜单）----
                var p = new ConfParser(liveConf);
                p.Parse();
                var counted = MenuBuilder.CountPerMenu(p.Groups);
                var built = MenuBuilder.Build(p.Groups, null);
                bool ok4;
                string detail4;
                try
                {
                    int rootRows = CountRows(built.Menu);
                    int subChecked = 0, subOk = 0;
                    var mism = new StringBuilder();

                    for (int i = 0; i < built.Menu.Count(); i++)
                    {
                        IntPtr sh = built.Menu.GetSubMenuHandle(i);
                        if (sh == IntPtr.Zero) continue;

                        string title = built.Menu.GetItemText(i).Replace("&", "");
                        int tab = title.IndexOf('\t');
                        if (tab >= 0) title = title.Substring(0, tab);
                        title = title.Trim();

                        var node = FindBySuffix(counted, " / " + title);
                        if (node == null) continue;

                        int rows = CountRows(NativeMenu.FromHandle(sh));
                        subChecked++;
                        if (rows == node.Items) subOk++;
                        else mism.Append(" [" + title + " 计数=" + node.Items + " 实建=" + rows + "]");
                    }

                    ok4 = (rootRows == counted[0].Items) && subChecked >= 1 && subOk == subChecked;
                    detail4 = "root 计数=" + counted[0].Items + " 实建行=" + rootRows
                        + "；子菜单 " + subOk + "/" + subChecked + " 一致" + mism;
                }
                finally
                {
                    built.Menu.Dispose();
                }
                if (!ok4) fails++;
                sb.AppendLine((ok4 ? "[OK]   " : "[FAIL] ") + "④ 计数与 Build 同构: " + detail4);

                // ---- ⑤ 分隔线不占有效项 ----
                string sepConf = WriteConf(tmpRoot, "sep",
                    "! 分隔线 always" + Environment.NewLine
                    + "  甲  _A  | run  a.exe" + Environment.NewLine
                    + "  ---" + Environment.NewLine
                    + "  乙  _B  | run  b.exe" + Environment.NewLine);
                var sepParser = new ConfParser(sepConf);
                sepParser.Parse();
                var sepCounted = MenuBuilder.CountPerMenu(sepParser.Groups);
                bool ok5 = sepCounted.Count == 1 && sepCounted[0].Items == 2;
                if (!ok5) fails++;
                sb.AppendLine((ok5 ? "[OK]   " : "[FAIL] ")
                    + "⑤ 分隔线不占项: root 计数=" + sepCounted[0].Items + "（期望 2，配置含 1 条 ---）");

                // ---- ⑥ A2：内存构造单菜单 2000 项 ⇒ Build 不抛、可点条目>0、日志含「已截断」----
                var logLines = new List<string>();
                Action<string> oldSink = MenuBuilder.LogSink;
                int bigTotal = 0;
                string note = "";
                string err = null;
                bool ok6 = false;
                try
                {
                    MenuBuilder.LogSink = s => logLines.Add(s);
                    var bigGroups = new List<MenuGroup> { MakeBigGroup("巨量内存", 2000) };
                    var bigBuilt = MenuBuilder.Build(bigGroups, null);
                    try
                    {
                        bigTotal = bigBuilt.ItemCount;
                        note = bigBuilt.TruncationNote ?? "";
                        bool logged = false;
                        foreach (string l in logLines)
                            if (l.IndexOf("已截断", StringComparison.Ordinal) >= 0) logged = true;
                        ok6 = bigTotal > 0 && note.IndexOf("已截断", StringComparison.Ordinal) >= 0
                           && logged && bigBuilt.Menu.Count() > 0
                           && bigTotal < 2000;
                        if (!ok6)
                            err = "total=" + bigTotal + " note=[" + note + "] 日志条数=" + logLines.Count;
                    }
                    finally
                    {
                        bigBuilt.Menu.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    err = ex.GetType().Name + ": " + ex.Message + " @" + FirstStackLine(ex);
                    ok6 = false;
                }
                finally
                {
                    MenuBuilder.LogSink = oldSink;
                }
                if (!ok6) fails++;
                sb.AppendLine((ok6 ? "[OK]   " : "[FAIL] ")
                    + "⑥ 降级重建（2000 项）: 不抛=" + (err == null)
                    + " 可点条目=" + bigTotal + "（期望 >0 且 <2000）"
                    + " 日志含「已截断」=" + (note.IndexOf("已截断", StringComparison.Ordinal) >= 0)
                    + (err != null ? "  异常/明细: " + err : ""));
                if (note.Length > 0) sb.AppendLine("       日志: " + note);

                // ---- ⑦ A2：截断是「每个菜单各自」的，不是整张配置砍一半 ----
                //   两个 2000 项容器 ⇒ 降级后**两个**子菜单各 1000 行（总数 ≈2000），
                //   若实现成"全局只留 1000"就会有一个容器变成空的。
                int sub1 = -1, sub2 = -1, total7 = 0;
                string err7 = null;
                bool ok7 = false;
                try
                {
                    var two = new List<MenuGroup> { MakeTwoBigGroup("容器甲", "容器乙", 2000) };
                    var b7 = MenuBuilder.Build(two, null);
                    try
                    {
                        total7 = b7.ItemCount;
                        int rootRows7 = CountRows(b7.Menu);
                        for (int i = 0; i < b7.Menu.Count(); i++)
                        {
                            IntPtr sh = b7.Menu.GetSubMenuHandle(i);
                            if (sh == IntPtr.Zero) continue;
                            int rows = CountRows(NativeMenu.FromHandle(sh));
                            if (sub1 < 0) sub1 = rows; else sub2 = rows;
                        }
                        ok7 = rootRows7 == 2 && sub1 == MenuBuilder.DegradeFirstLimit
                           && sub2 == MenuBuilder.DegradeFirstLimit
                           && total7 == MenuBuilder.DegradeFirstLimit * 2;
                        if (!ok7)
                            err7 = "root行=" + rootRows7 + " 子菜单=" + sub1 + "/" + sub2
                                 + " total=" + total7;
                    }
                    finally
                    {
                        b7.Menu.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    err7 = ex.GetType().Name + ": " + ex.Message;
                }
                if (!ok7) fails++;
                sb.AppendLine((ok7 ? "[OK]   " : "[FAIL] ")
                    + "⑦ 每菜单各自截断（2×2000 项）: 子菜单=" + sub1 + "/" + sub2
                    + "（期望各 " + MenuBuilder.DegradeFirstLimit + "） 可点条目合计=" + total7
                    + (err7 != null ? "  明细: " + err7 : ""));

                // ---- ⑧ 顶格条目（Level=0）不再崩：预检能数、构建能出菜单 ----
                //   手写配置里"第一列直接写条目"是常态；旧分栈会连 root 一起弹掉 ⇒ 越界崩。
                var l0 = new StringBuilder();
                l0.AppendLine("! 顶格 always");
                for (int i = 1; i <= 2000; i++) l0.AppendLine("顶" + i + "  | run  y" + i + ".exe");
                string l0Conf = WriteConf(tmpRoot, "level0", l0.ToString());

                string repL0;
                int rcL0 = Evaluate(l0Conf, out repL0);

                int l0Total = 0;
                bool l0Threw = false;
                var gL0 = new MenuGroup { Name = "顶格", Type = GroupType.Always };
                for (int i = 1; i <= 2000; i++)
                    gL0.Items.Add(new MenuItem { Title = "顶" + i, Type = ActionType.Run, Target = "y" + i + ".exe", Level = 0 });
                try
                {
                    var bL0 = MenuBuilder.Build(new List<MenuGroup> { gL0 });
                    try { l0Total = bL0.ItemCount; }
                    finally { bL0.Menu.Dispose(); }
                }
                catch { l0Threw = true; }

                bool ok8 = rcL0 == 3 && repL0.IndexOf("2000", StringComparison.Ordinal) >= 0
                        && !l0Threw && l0Total > 0;
                if (!ok8) fails++;
                sb.AppendLine((ok8 ? "[OK]   " : "[FAIL] ")
                    + "⑧ 顶格条目(Level=0) 2000 项: 预检 exit=" + rcL0 + "（期望 3）"
                    + " 构建抛异常=" + l0Threw + " 可点条目=" + l0Total + "（期望 >0，截断到 "
                    + MenuBuilder.DegradeFirstLimit + "）");

                sb.AppendLine();
                sb.AppendLine("结果: " + (fails == 0 ? "全部通过 ✓" : "失败 " + fails + " 项 ✗"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex.GetType().Name + ": " + ex.Message);
                sb.AppendLine(ex.StackTrace);
                sb.AppendLine();
                sb.AppendLine("结果: 失败（异常） ✗");
                fails++;
            }
            finally
            {
                try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true); } catch { }
            }

            WriteText(outPath, sb.ToString());
            return fails == 0 ? 0 : 1;
        }

        /// <summary>异常栈顶一行（自检报告里定位用；不整段铺开）。</summary>
        private static string FirstStackLine(Exception ex)
        {
            if (ex == null || string.IsNullOrEmpty(ex.StackTrace)) return "";
            foreach (string raw in ex.StackTrace.Replace("\r\n", "\n").Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length > 0) return l;
            }
            return "";
        }

        /// <summary>统计一个菜单的**有效行**数（不含分隔线：Win32 里分隔线的命令 ID = 0）。</summary>
        private static int CountRows(NativeMenu menu)
        {
            int n = 0;
            int c = menu.Count();
            for (int i = 0; i < c; i++)
                if (menu.GetItemIdAt(i) != 0) n++;
            return n;
        }

        private static MenuBuilder.MenuCount FindBySuffix(List<MenuBuilder.MenuCount> list, string suffix)
        {
            foreach (var m in list)
                if (m.Path.EndsWith(suffix, StringComparison.Ordinal)) return m;
            return null;
        }

        private static string FirstFatalLine(string report)
        {
            foreach (string raw in (report ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith("FATAL", StringComparison.Ordinal)) return "首个超限: " + l;
            }
            return "";
        }

        /// <summary>写一份临时菜单配置（沙箱，绝不碰真实配置）。</summary>
        private static string WriteConf(string rootDir, string name, string text)
        {
            string dir = Path.Combine(rootDir, name, "config");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "menus.conf");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        /// <summary>夹具：一个容器子菜单里塞 <paramref name="count"/> 条叶项（写在 level 1/2）。</summary>
        private static string BuildBigConf(string menuTitle, int count)
        {
            var sb = new StringBuilder();
            sb.AppendLine("! 规模 always");
            sb.AppendLine("  " + menuTitle + "  _G  | group");
            for (int i = 1; i <= count; i++)
                sb.AppendLine("    项" + i + "  | run  x" + i + ".exe");
            return sb.ToString();
        }

        /// <summary>夹具：同组里两个容器，各塞 <paramref name="count"/> 条子项（层级形状同上）。</summary>
        private static MenuGroup MakeTwoBigGroup(string titleA, string titleB, int count)
        {
            var g = new MenuGroup { Name = "规模2", Type = GroupType.Always };
            g.Items.Add(new MenuItem { Title = titleA, Type = ActionType.Group, Level = 1 });
            for (int i = 1; i <= count; i++)
                g.Items.Add(new MenuItem { Title = "甲" + i, Type = ActionType.Run, Target = "a" + i + ".exe", Level = 2 });
            g.Items.Add(new MenuItem { Title = titleB, Type = ActionType.Group, Level = 1 });
            for (int i = 1; i <= count; i++)
                g.Items.Add(new MenuItem { Title = "乙" + i, Type = ActionType.Run, Target = "b" + i + ".exe", Level = 2 });
            return g;
        }

        /// <summary>夹具：内存里的 2000 项分组（不落盘，避免碰真实配置）。</summary>
        /// ★ 层级形状照抄现役配置的容器（标题 Level=1、子项 Level=2）—— 分栈语义只认这一种
        ///   「同组里 level 0 是叶、容器在 level ≥1」的形状（既有不变式，本卡不动）。
        /// </summary>
        private static MenuGroup MakeBigGroup(string menuTitle, int count)
        {
            var g = new MenuGroup { Name = "规模", Type = GroupType.Always };
            g.Items.Add(new MenuItem
            {
                Title = menuTitle,
                Hotkey = "",
                Type = ActionType.Group,
                Level = 1
            });
            for (int i = 1; i <= count; i++)
            {
                g.Items.Add(new MenuItem
                {
                    Title = "项" + i,
                    Type = ActionType.Run,
                    Target = "x" + i + ".exe",
                    Level = 2
                });
            }
            return g;
        }
    }
}
