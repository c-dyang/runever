using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Native;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// ★ 卡 D（2026-11-17b）：菜单项右键上下文菜单的**离线**自检。
    ///
    /// 用法：RunEverCs.exe --selftest-menurbuttonup &lt;root&gt; &lt;out.txt&gt;
    ///       **禁带 --root**（root 由位置参数给，与 --selftest-menu / --selftest-checkconf 同形）
    ///
    /// ★ 全程**不弹任何菜单**（无人值守纪律）：只建 HMENU、只看 Win32 的菜单项信息，
    ///   弹菜单那一段（TrackPopupMenuEx）由自检 ② 用 flags 纯函数断言、结构上不调用。
    ///
    /// 断言（逐条对应 HANDOFF 卡 D「判据 1」的五项）：
    ///   ① WM_MENURBUTTONUP 常量 + 索引解析：给定 index + hMenu 解析出的 MenuItem 与 IdMap **同引用**
    ///      （含子菜单里的条目：hMenu 换成子菜单句柄也要对）
    ///   ② TPM_RECURSE 已置入主菜单 flags（纯函数），且 TPM_RETURNCMD | TPM_NONOTIFY + 对齐位一位没改
    ///   ③ 编辑 / 删除 / 重命名三个动作的落盘效果：MenuConfWriter 写出 + .bak 生成 +
    ///      **非条目行逐字节不变** + BOM 保留（窄写回口径）
    ///   ④ 索引越界 / 分隔线 / 子菜单项：三种边界都不抛异常，返回"不可编辑"并给出原因
    ///   ⑤ 重建菜单：动作后 Build 拿到新 IdMap（新 HMENU），旧 id 不再指向被改条目的原身份
    ///
    /// 另加（本卡实现约束的结构性断言）：
    ///   ⑥ 输入回调只在"菜单已关闭"那一段（PopupClosed → RunPending → Apply）里被调用；
    ///      没有输入框时编辑/重命名**放弃且不写盘**；删除不需要输入框。
    /// </summary>
    public static class MenuItemContextMenuSelfTest
    {
        private static readonly List<string> Lines = new List<string>();
        private static int _pass;
        private static int _fail;

        private static void Chk(string name, bool ok, string detail)
        {
            if (ok) _pass++; else _fail++;
            Lines.Add((ok ? "[OK]   " : "[FAIL] ") + name
                + (string.IsNullOrEmpty(detail) ? "" : "  |  " + detail));
        }

        public static int Run(string[] args)
        {
            if (args == null || args.Length < 3) return 2;

            string root = args[1];
            string outFile = args[2];
            if (!Path.IsPathRooted(outFile)) outFile = Path.Combine(root, outFile);

            string baseDir = Directory.Exists(root) ? root : Path.GetDirectoryName(Path.GetFullPath(root));
            string sandbox = Path.Combine(baseDir ?? ".", "state", "menurbuttonup-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            Lines.Clear();
            _pass = 0;
            _fail = 0;
            Lines.Add("=== 菜单项右键上下文菜单自检（--selftest-menurbuttonup）===");
            Lines.Add("  时间    : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Lines.Add("  沙箱    : " + sandbox + "（跑完自删；不碰 config\\menus.conf）");
            Lines.Add("  纪律    : 全程不弹菜单（只建 HMENU + 读菜单项信息）");
            Lines.Add("");

            try
            {
                Directory.CreateDirectory(sandbox);

                CaseConstants();          // ①（常量部分）
                CaseFlagBits();           // ②
                CaseIndexResolution(sandbox);   // ①（解析部分）+ ④（边界）
                CaseWriteEffects(sandbox);      // ③
                CaseRebuild(sandbox);           // ⑤
                CaseDeferredInput(sandbox);     // ⑥
            }
            catch (Exception ex)
            {
                _fail++;
                Lines.Add("[FAIL] 自检异常: " + ex.GetType().Name + ": " + ex.Message);
                Lines.Add(ex.StackTrace);
            }
            finally
            {
                try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); } catch { }
            }

            Lines.Add("");
            Lines.Add("汇总: 通过 " + _pass + " / 失败 " + _fail
                + " → " + (_fail == 0 ? "全部通过 ✓（exit=0）" : "有失败 ✗（exit=1）"));

            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outFile, string.Join(Environment.NewLine, Lines.ToArray()) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }

            return _fail == 0 ? 0 : 1;
        }

        // ============================================================
        // 沙箱夹具：BOM + CRLF + 注释 + 空行 + 分隔线 + 一个真子菜单
        //   ⇒ 窄写回"非条目行逐字节不变"有东西可证（纯条目文件证不出来）
        // ============================================================
        private const string BaseConf =
            "# 头注释（窄写回必须逐字节保留）" + "\r\n" +
            "! 右键测试  always" + "\r\n" +
            "  甲  _A  | run  a.exe" + "\r\n" +
            "  # 条目之间的注释（也必须原样保留）" + "\r\n" +
            "\r\n" +
            "  乙  _B  | run  b.exe" + "\r\n" +
            "  容器  _C  | group" + "\r\n" +
            "    子丁  | run  d.exe" + "\r\n" +
            "  丙  | run  c.exe" + "\r\n" +
            "  ---" + "\r\n";

        private static string MakeSandbox(string sandbox, string name)
        {
            string confDir = Path.Combine(sandbox, name, "config");
            Directory.CreateDirectory(confDir);
            string path = Path.Combine(confDir, "menus.conf");
            File.WriteAllText(path, BaseConf, new UTF8Encoding(true));    // ★ 带 BOM
            return path;
        }

        // ============================================================
        // ① 常量
        // ============================================================
        private static void CaseConstants()
        {
            Chk("①a WM_MENURBUTTONUP = 0x0122", NativeMenu.WM_MENURBUTTONUP == 0x0122,
                "实值=0x" + NativeMenu.WM_MENURBUTTONUP.ToString("X4"));
            Chk("①b TPM_RECURSE = 0x0001", NativeMenu.TPM_RECURSE == 0x0001,
                "实值=0x" + NativeMenu.TPM_RECURSE.ToString("X4"));
            Chk("①c 三个动作枚举值稳定（编辑=1/重命名=2/删除=3）",
                (int)MenuItemAction.EditTarget == 1 && (int)MenuItemAction.Rename == 2
                && (int)MenuItemAction.Delete == 3,
                "编辑=" + (int)MenuItemAction.EditTarget + " 重命名=" + (int)MenuItemAction.Rename
                + " 删除=" + (int)MenuItemAction.Delete);
        }

        // ============================================================
        // ② flags：TPM_RECURSE 在，既有语义一位没改
        // ============================================================
        private static void CaseFlagBits()
        {
            uint plain = NativeMenu.BuildPopupFlags(false, false);
            uint expected = NativeMenu.TPM_RECURSE | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY;

            Chk("②a 主菜单 flags 含 TPM_RECURSE", (plain & NativeMenu.TPM_RECURSE) != 0,
                "flags=0x" + plain.ToString("X4"));
            Chk("②b 主菜单 flags = RECURSE|RETURNCMD|NONOTIFY（对齐位为 0）", plain == expected,
                "实值=0x" + plain.ToString("X4") + " 期望=0x" + expected.ToString("X4"));
            Chk("②c TPM_RETURNCMD 未被改动（仍靠返回值取选中项）",
                (plain & Win32.TPM_RETURNCMD) != 0, "");
            Chk("②d TPM_NONOTIFY 未被改动（不许 WM_COMMAND 打进来）",
                (plain & Win32.TPM_NONOTIFY) != 0, "");

            uint right = NativeMenu.BuildPopupFlags(true, false);
            uint bottom = NativeMenu.BuildPopupFlags(false, true);
            Chk("②e 右对齐位仍是 TPM_RIGHTALIGN(0x8)",
                (right & Win32.TPM_RIGHTALIGN) != 0 && (right & Win32.TPM_LEFTALIGN) == 0,
                "right=0x" + right.ToString("X4"));
            Chk("②f 下对齐位仍是 TPM_BOTTOMALIGN(0x20)",
                (bottom & Win32.TPM_BOTTOMALIGN) != 0 && (bottom & Win32.TPM_TOPALIGN) == 0,
                "bottom=0x" + bottom.ToString("X4"));

            Chk("②g 二级菜单 flags 也带 TPM_RECURSE（外层菜单还开着）",
                (NativeMenu.ContextPopupFlags & NativeMenu.TPM_RECURSE) != 0,
                "flags=0x" + NativeMenu.ContextPopupFlags.ToString("X4"));
        }

        // ============================================================
        // ① 索引 → MenuItem（同引用） + ④ 三种边界
        // ============================================================
        private static void CaseIndexResolution(string sandbox)
        {
            string conf = MakeSandbox(sandbox, "resolve");

            List<MenuGroup> groups;
            try
            {
                var p = new ConfParser(conf);
                p.Parse();
                groups = p.Groups;
            }
            catch (Exception ex)
            {
                Chk("①d 沙箱配置可解析", false, ex.Message);
                return;
            }

            var built = MenuBuilder.Build(groups, null);
            try
            {
                int resolved = 0, mismatch = 0, leaks = 0;
                int sepIndex = -1, subIndex = -1;

                for (int i = 0; i < built.Menu.Count(); i++)
                {
                    int cmdId;
                    bool isSep, isSub;
                    string why;
                    // ★ 返回值 false 本身就是"分隔线 / 子菜单项"的预期结果 —— 两个 out 位照样有效
                    NativeMenu.TryGetMenuItemInfo(built.Menu.Handle, i, out cmdId, out isSep, out isSub, out why);

                    if (isSep) sepIndex = i;
                    if (isSub) subIndex = i;

                    MenuItem item;
                    string why2;
                    bool ok = MenuItemContextMenu.TryResolveItem(built.Menu.Handle, i, built.IdMap, out item, out why2);

                    if (!ok) continue;      // 分隔线 / 子菜单 / 虚拟项：④ 单独断言
                    resolved++;

                    MenuItem expectedItem;
                    if (!built.IdMap.TryGetValue(cmdId, out expectedItem) || !object.ReferenceEquals(item, expectedItem))
                        mismatch++;
                    if (item.Line <= 0) leaks++;
                }

                Chk("①d 顶层每个可点条目都能解析且与 IdMap 同引用",
                    resolved >= 3 && mismatch == 0,
                    "解析成功=" + resolved + " 引用不一致=" + mismatch);

                Chk("①e 解析出的条目都带配置行号（Line>0 ⇒ 可回写定位）",
                    leaks == 0, "无行号的条目=" + leaks);

                // ①f 子菜单里的条目：hMenu 换成子菜单句柄，同样按索引解析
                bool subOk = false;
                string subDetail = "配置里没有真子菜单";
                if (subIndex >= 0)
                {
                    IntPtr sh = built.Menu.GetSubMenuHandle(subIndex);
                    if (sh != IntPtr.Zero)
                    {
                        MenuItem subItem;
                        string why3;
                        subOk = MenuItemContextMenu.TryResolveItem(sh, 0, built.IdMap, out subItem, out why3);
                        subDetail = subOk ? "子菜单[0] →「" + subItem.Title + "」line=" + subItem.Line
                                          : "解析失败: " + why3;
                        if (subOk)
                        {
                            int subId;
                            bool s1, s2;
                            string w;
                            NativeMenu.TryGetMenuItemInfo(sh, 0, out subId, out s1, out s2, out w);
                            MenuItem exp;
                            subOk = built.IdMap.TryGetValue(subId, out exp) && object.ReferenceEquals(exp, subItem);
                            if (!subOk) subDetail += "（但引用不一致）";
                        }
                    }
                }
                Chk("①f 子菜单里的条目也用 hMenu+index 解析到同一条", subOk, subDetail);

                // ---- ④ 三种边界：不抛异常、返回 false、有原因 ----
                var edges = new List<string>();
                bool edgeOk = true;

                // ④a 负索引
                edgeOk &= ExpectReject(built, built.Menu.Handle, -1, "索引越界", edges);
                // ④b 远越界
                edgeOk &= ExpectReject(built, built.Menu.Handle, built.Menu.Count() + 100, "索引越界", edges);
                // ④c 分隔线
                if (sepIndex >= 0) edgeOk &= ExpectReject(built, built.Menu.Handle, sepIndex, "分隔线", edges);
                else { edgeOk = false; edges.Add("沙箱里没找到分隔线（夹具坏了）"); }
                // ④d 子菜单项
                if (subIndex >= 0) edgeOk &= ExpectReject(built, built.Menu.Handle, subIndex, "子菜单", edges);
                else { edgeOk = false; edges.Add("沙箱里没找到子菜单项（夹具坏了）"); }

                Chk("④ 越界 / 分隔线 / 子菜单：不抛异常、返回不可编辑、原因可读",
                    edgeOk, string.Join("；", edges.ToArray()));
            }
            finally
            {
                built.Menu.Dispose();
            }
        }

        /// <summary>断言"这个 index 不可编辑"：不抛异常 + false + 原因含关键字。</summary>
        private static bool ExpectReject(MenuBuilder.BuildResult built, IntPtr hMenu, int index,
            string keyword, List<string> notes)
        {
            try
            {
                MenuItem item;
                string why;
                bool ok = MenuItemContextMenu.TryResolveItem(hMenu, index, built.IdMap, out item, out why);
                bool pass = !ok && item == null && !string.IsNullOrEmpty(why)
                    && why.IndexOf(keyword, StringComparison.Ordinal) >= 0;
                notes.Add("idx=" + index + (pass ? " OK" : " FAIL") + "（why=" + (why ?? "") + "）");
                return pass;
            }
            catch (Exception ex)
            {
                notes.Add("idx=" + index + " 抛异常 " + ex.GetType().Name);
                return false;
            }
        }

        // ============================================================
        // ③ 三个动作的落盘效果
        // ============================================================
        private static void CaseWriteEffects(string sandbox)
        {
            // ---- ③a 重命名（改标题）----
            CheckWriteAction(sandbox, "rename", "甲", MenuItemAction.Rename, "甲改",
                delegate (string conf, MenuItemContextResult r)
                {
                    var p = new ConfParser(conf); p.Parse();
                    var it = FindByTitle(p.Groups, "甲改");
                    return r.Ok
                        && it != null && it.Line > 0
                        && FindByTitle(p.Groups, "甲") == null
                        && r.NewTitle == "甲改"
                        && r.Message.IndexOf("窄写回", StringComparison.Ordinal) >= 0
                        && r.Message.IndexOf("改 1 行", StringComparison.Ordinal) >= 0;
                }, "标题 → 甲改；窄写回改 1 行");

            // ---- ③b 编辑（改动作目标）----
            CheckWriteAction(sandbox, "edit", "乙", MenuItemAction.EditTarget, "b2.exe",
                delegate (string conf, MenuItemContextResult r)
                {
                    var p = new ConfParser(conf); p.Parse();
                    var it = FindByTitle(p.Groups, "乙");
                    return r.Ok && it != null && it.Target == "b2.exe"
                        && r.Message.IndexOf("窄写回", StringComparison.Ordinal) >= 0
                        && r.Message.IndexOf("改 1 行", StringComparison.Ordinal) >= 0;
                }, "目标 → b2.exe；窄写回改 1 行");

            // ---- ③c 删除 ----
            CheckWriteAction(sandbox, "delete", "丙", MenuItemAction.Delete, null,
                delegate (string conf, MenuItemContextResult r)
                {
                    var p = new ConfParser(conf); p.Parse();
                    return r.Ok
                        && FindByTitle(p.Groups, "丙") == null
                        && r.RemovedItems == 1
                        && r.Message.IndexOf("删除 1", StringComparison.Ordinal) >= 0;
                }, "条目已删；窄写回删除 1 行");

            // ---- ③d 拒绝：标题带 `|` / 空标题 ⇒ 不写盘 ----
            string confPath = MakeSandbox(sandbox, "reject");
            byte[] before = File.ReadAllBytes(confPath);
            var parsed = new ConfParser(confPath); parsed.Parse();
            var target = ItemRefOf(parsed.Groups, "甲");

            bool refused = true;
            string detail = "";
            try
            {
                var okPipe = MenuItemContextMenu.Apply(confPath, target, MenuItemAction.Rename, "甲|乙");
                var okEmpty = MenuItemContextMenu.Apply(confPath, target, MenuItemAction.Rename, "   ");
                refused = !okPipe.Ok && !okEmpty.Ok;
                detail = "含| → " + (okPipe.Ok ? "写了(错)" : "拒绝") + "；空标题 → " + (okEmpty.Ok ? "写了(错)" : "拒绝");
            }
            catch (Exception ex)
            {
                refused = false;
                detail = "抛异常: " + ex.GetType().Name;
            }
            bool untouched = BytesEqual(before, File.ReadAllBytes(confPath));
            Chk("③d 非法标题（含 `|` / 空）被拒且文件一个字节没动", refused && untouched, detail);
        }

        /// <summary>对一个独立沙箱执行一次动作并核对"写出 / .bak / 非条目行逐字节不变 / BOM"。</summary>
        private static void CheckWriteAction(string sandbox, string name, string targetTitle,
            MenuItemAction action, string newText,
            Func<string, MenuItemContextResult, bool> extraCheck, string expectDetail)
        {
            string confPath = MakeSandbox(sandbox, name);
            string confDir = Path.GetDirectoryName(confPath);

            byte[] beforeBytes = File.ReadAllBytes(confPath);
            bool bomBefore = beforeBytes.Length >= 3 && beforeBytes[0] == 0xEF && beforeBytes[1] == 0xBB && beforeBytes[2] == 0xBF;
            string beforeText = Encoding.UTF8.GetString(beforeBytes, bomBefore ? 3 : 0,
                beforeBytes.Length - (bomBefore ? 3 : 0));

            var beforeParser = new ConfParser(confPath);
            beforeParser.Parse();
            var opaqueBefore = MenuConfWriter.OpaqueLines(beforeText, beforeParser.Groups);

            var target = ItemRefOf(beforeParser.Groups, targetTitle);
            if (target == null)
            {
                Chk("③ " + name + "：沙箱里找得到「" + targetTitle + "」", false, "夹具坏了");
                return;
            }

            MenuItemContextResult r;
            try
            {
                r = MenuItemContextMenu.Apply(confPath, target, action, newText);
            }
            catch (Exception ex)
            {
                Chk("③ " + name + "：Apply 不抛异常", false, ex.GetType().Name + ": " + ex.Message);
                return;
            }

            byte[] afterBytes = File.ReadAllBytes(confPath);
            bool bomAfter = afterBytes.Length >= 3 && afterBytes[0] == 0xEF && afterBytes[1] == 0xBB && afterBytes[2] == 0xBF;
            string afterText = Encoding.UTF8.GetString(afterBytes, bomAfter ? 3 : 0,
                afterBytes.Length - (bomAfter ? 3 : 0));

            var afterParser = new ConfParser(confPath);
            afterParser.Parse();
            var opaqueAfter = MenuConfWriter.OpaqueLines(afterText, afterParser.Groups);

            int diffAt = OpaqueDiff(opaqueBefore, opaqueAfter);
            bool opaqueSame = diffAt < 0;

            string[] baks;
            try { baks = Directory.GetFiles(confDir, "menus.conf.bak.*"); }
            catch { baks = new string[0]; }

            bool extra = false;
            string extraDetail = "";
            try { extra = extraCheck(confPath, r); extraDetail = expectDetail; }
            catch (Exception ex) { extraDetail = "核对抛异常: " + ex.GetType().Name + ": " + ex.Message; }

            bool ok = r.Ok && bomAfter && opaqueSame && baks.Length >= 1 && extra;
            Chk("③ " + name + "：" + ActionName(action) + " 落盘（MenuConfWriter + .bak + 非条目行逐字节不变）",
                ok,
                "Ok=" + r.Ok + " BOM=" + bomAfter + " 非条目行一致=" + opaqueSame
                + (diffAt >= 0 ? "（首个差异 #" + diffAt + "）" : "")
                + " .bak=" + baks.Length + "；" + (r.Message ?? "").Replace("\r", " ").Replace("\n", " ")
                + (extraDetail.Length > 0 ? "；核对: " + extraDetail : ""));

            if (!r.Ok)
                Lines.Add("       失败原因: " + (r.Message ?? ""));
        }

        private static string ActionName(MenuItemAction a)
        {
            switch (a)
            {
                case MenuItemAction.Rename: return "重命名";
                case MenuItemAction.EditTarget: return "编辑目标";
                case MenuItemAction.Delete: return "删除";
                default: return a.ToString();
            }
        }

        /// <summary>两串"非条目行"逐条比较，返回首个差异下标（-1 = 完全一致）。</summary>
        private static int OpaqueDiff(List<string> a, List<string> b)
        {
            int n = Math.Min(a.Count, b.Count);
            for (int i = 0; i < n; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return i;
            if (a.Count != b.Count) return n;
            return -1;
        }

        // ============================================================
        // ⑤ 重建：新 IdMap / 新 HMENU，旧 id 不再指向原条目
        // ============================================================
        private static void CaseRebuild(string sandbox)
        {
            string confPath = MakeSandbox(sandbox, "rebuild");

            List<MenuGroup> groups;
            var p0 = new ConfParser(confPath);
            p0.Parse();
            groups = p0.Groups;

            var before = MenuBuilder.Build(groups, null);
            int oldId = 0;
            int oldCount;
            try
            {
                oldCount = before.ItemCount;
                foreach (var kv in before.IdMap)
                    if (kv.Value != null && kv.Value.Title == "丙") { oldId = kv.Key; break; }

                if (oldId == 0)
                {
                    Chk("⑤a 旧菜单里找得到「丙」的命令 ID", false, "夹具坏了");
                    return;
                }

                var parsed = new ConfParser(confPath);
                parsed.Parse();
                var target = ItemRefOf(parsed.Groups, "丙");
                var r = MenuItemContextMenu.Apply(confPath, target, MenuItemAction.Delete, null);

                var rebuilt = MenuItemContextMenu.RebuildFromFile(confPath);
                try
                {
                    MenuItem stillThere = null;
                    bool oldIdHitsOldItem = rebuilt.IdMap.TryGetValue(oldId, out stillThere)
                        && stillThere != null && stillThere.Title == "丙";

                    Chk("⑤a 删除后重建：可点条目少 1 条",
                        r.Ok && rebuilt.ItemCount == oldCount - 1,
                        "删除 Ok=" + r.Ok + " 旧=" + oldCount + " 新=" + rebuilt.ItemCount);

                    Chk("⑤b 重建后「丙」不再在 IdMap 里，旧 id 不再指向它",
                        !ContainsTitle(rebuilt.IdMap.Values, "丙") && !oldIdHitsOldItem,
                        "旧 id=" + oldId + (oldIdHitsOldItem ? " 仍指向「丙」(错)" : " 已不指向「丙」"));

                    Chk("⑤c 重建用的是**新 HMENU**（旧句柄不复用）",
                        rebuilt.Menu.Handle != IntPtr.Zero && rebuilt.Menu.Handle != before.Menu.Handle,
                        "旧=0x" + before.Menu.Handle.ToInt64().ToString("X")
                        + " 新=0x" + rebuilt.Menu.Handle.ToInt64().ToString("X"));

                    // 旧句柄在重建后仍然是"旧世界"（未被人就地改过）—— 这正是必须丢弃它的原因
                    Chk("⑤d 旧 IdMap 仍是旧世界（条款数没变 ⇒ 不能拿来派发）",
                        before.IdMap.Count == oldCount,
                        "旧 IdMap=" + before.IdMap.Count + " 旧可点条目=" + oldCount);
                }
                finally
                {
                    rebuilt.Menu.Dispose();
                }
            }
            finally
            {
                before.Menu.Dispose();
            }
        }

        // ============================================================
        // ⑥ 延后输入（结构约束）：输入只在菜单关闭后；无输入框 ⇒ 放弃且不写盘
        // ============================================================
        private static void CaseDeferredInput(string sandbox)
        {
            string confPath = MakeSandbox(sandbox, "deferred");

            var p = new ConfParser(confPath);
            p.Parse();
            var built = MenuBuilder.Build(p.Groups, null);
            try
            {
                // ---- ⑥a Attach 真的把两个钩子挂上（WM_MENURBUTTONUP / PopupClosed）----
                int promptCalls = 0;
                string seenCurrent = null;
                var ctx = MenuItemContextMenu.Attach(
                    built.Menu, built.IdMap, confPath,
                    null,
                    delegate (string current, MenuItemAction a)
                    {
                        promptCalls++;
                        seenCurrent = current;
                        return "甲改";
                    });

                Chk("⑥a Attach 挂上 MenuButtonUp + PopupClosed（右键链路就位）",
                    built.Menu.MenuButtonUp != null && built.Menu.PopupClosed != null,
                    "MenuButtonUp=" + (built.Menu.MenuButtonUp != null)
                    + " PopupClosed=" + (built.Menu.PopupClosed != null));

                MenuItem jia = null;
                foreach (var kv in built.IdMap)
                    if (kv.Value != null && kv.Value.Title == "甲") { jia = kv.Value; break; }

                if (jia == null)
                {
                    Chk("⑥b 沙箱里找得到「甲」", false, "夹具坏了");
                    ctx.Detach();
                    return;
                }

                byte[] before = File.ReadAllBytes(confPath);
                var r1 = ctx.ExecuteDeferred(jia, MenuItemAction.Rename);

                Chk("⑥b 延后输入：回调收到当前值、动作按返回文本落盘",
                    r1.Ok && promptCalls == 1 && seenCurrent == "甲" && ctx.PromptCallCount == 1,
                    "Ok=" + r1.Ok + " 回调次数=" + promptCalls + " 收到的当前值=「" + seenCurrent + "」");

                var after1 = new ConfParser(confPath); after1.Parse();
                Chk("⑥c 文件里「甲」已变「甲改」",
                    FindByTitle(after1.Groups, "甲改") != null && FindByTitle(after1.Groups, "甲") == null, "");

                // ---- ⑥d 用户取消（回调返回 null）⇒ 丢弃，文件一字节不动 ----
                var jiaGai = FindByTitle(after1.Groups, "甲改");
                ctx.Detach();
                var ctx2 = MenuItemContextMenu.Attach(
                    built.Menu, built.IdMap, confPath, null,
                    delegate (string current, MenuItemAction a) { return null; });

                byte[] beforeCancel = File.ReadAllBytes(confPath);
                var r2 = ctx2.ExecuteDeferred(jiaGai, MenuItemAction.Rename);
                bool untouched = BytesEqual(beforeCancel, File.ReadAllBytes(confPath));

                Chk("⑥d 输入取消 ⇒ 不写盘（文件逐字节未变）、结果标失败",
                    !r2.Ok && untouched, "Ok=" + r2.Ok + " 文件未变=" + untouched);
                ctx2.Detach();

                // ---- ⑥e 没有输入框 ⇒ 编辑/重命名放弃且不写盘；删除不需要输入框 ----
                var ctx3 = MenuItemContextMenu.Attach(built.Menu, built.IdMap, confPath, null, null);
                byte[] beforeNoPrompt = File.ReadAllBytes(confPath);
                var parsed3 = new ConfParser(confPath); parsed3.Parse();
                var bing = FindByTitle(parsed3.Groups, "丙");
                var r3 = ctx3.ExecuteDeferred(bing, MenuItemAction.Rename);
                bool untouched3 = BytesEqual(beforeNoPrompt, File.ReadAllBytes(confPath));

                Chk("⑥e 未提供输入框 ⇒ 重命名放弃、配置未改动",
                    !r3.Ok && untouched3 && ctx3.PromptCallCount == 0,
                    "Ok=" + r3.Ok + " 文件未变=" + untouched3 + " 回调次数=" + ctx3.PromptCallCount);

                // ---- ⑥f 删除不经过输入回调 ----
                var parsed4 = new ConfParser(confPath); parsed4.Parse();
                var bing2 = FindByTitle(parsed4.Groups, "丙");
                var r4 = ctx3.ExecuteDeferred(bing2, MenuItemAction.Delete);
                Chk("⑥f 删除不需要输入框（回调 0 次）且成功落盘",
                    r4.Ok && ctx3.PromptCallCount == 0,
                    "Ok=" + r4.Ok + " 回调次数=" + ctx3.PromptCallCount);
                ctx3.Detach();

                Chk("⑥g Detach 后钩子已摘（重建菜单前必须解绑）",
                    built.Menu.MenuButtonUp == null && built.Menu.PopupClosed == null, "");
            }
            finally
            {
                built.Menu.Dispose();
            }
        }

        // ============================================================
        // 小工具
        // ============================================================
        private static MenuItem FindByTitle(List<MenuGroup> groups, string title)
        {
            if (groups == null) return null;
            foreach (var g in groups)
            {
                if (g == null || g.Items == null) continue;
                foreach (var it in g.Items)
                    if (it != null && string.Equals(it.Title ?? "", title, StringComparison.Ordinal)) return it;
            }
            return null;
        }

        private static MenuItemRef ItemRefOf(List<MenuGroup> groups, string title)
        {
            var it = FindByTitle(groups, title);
            return it == null ? null : MenuItemRef.From(it);
        }

        /// <summary>某个可选集合里还有没有这个标题（⑤b 用：重建后的 IdMap）。</summary>
        private static bool ContainsTitle(IEnumerable<MenuItem> items, string title)
        {
            if (items == null) return false;
            foreach (var it in items)
                if (it != null && string.Equals(it.Title ?? "", title, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
