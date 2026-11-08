using System;
using System.IO;
using System.Text;
using RunEverCs.Native;

namespace RunEverCs
{
    /// <summary>
    /// 「菜单 owner 能不能成为前台窗口」自检 —— ① 抢焦点修复的**无人值守判据**。
    /// 用法：RunEverCs.exe --selftest-focus &lt;out.txt&gt;
    ///
    /// 为什么需要它：`--trigger` 会真弹菜单（禁无人值守），但"快捷键无效"的根因就是
    /// **owner 不可激活 ⇒ 菜单模态循环拿不到键盘**。本自检用**同一个产品类**
    /// （`OwnerWindow`）与**同一份置前台实现**（`NativeMenu.ForceForeground`）实测
    /// `GetForegroundWindow()` 是否真的等于 owner 句柄 —— 这一点成立，
    /// 菜单期拿键盘才有前提。
    ///
    /// ⚠️ 会**短暂抢一下前台**（毫秒级），结束后立即把前台还给进入前的窗口。
    /// </summary>
    public static class FocusSelfTest
    {
        public static int Run(string[] args)
        {
            if (args.Length < 2) return 2;
            string outFile = args[1];

            var sb = new StringBuilder();
            int fail = 0;
            bool undetermined = false;   // ★ 探针不可用时判"未定论"，**不判 FAIL**（见下）

            OwnerWindow owner = null;
            IntPtr prev = IntPtr.Zero;
            try
            {
                prev = NativeMenu.CurrentForeground();
                owner = new OwnerWindow();

                sb.AppendLine("owner 句柄        = 0x" + owner.Handle.ToInt64().ToString("X"));
                sb.AppendLine("owner 可见        = " + owner.IsVisible());
                sb.AppendLine("owner 描述        = " + owner.Describe());
                sb.AppendLine("进入前前台窗口     = 0x" + prev.ToInt64().ToString("X"));

                if (!owner.IsVisible())
                {
                    fail++;
                    sb.AppendLine("FAIL  ① owner 必须可见（不可见 ⇒ 无法成为前台）");
                }
                else
                {
                    sb.AppendLine("PASS  ① owner 已创建且可见");
                }

                bool got = NativeMenu.ForceForeground(owner.Handle);
                IntPtr now = NativeMenu.CurrentForeground();
                sb.AppendLine("置前台序列结果     = " + (got ? "成功" : "失败")
                    + "（当前前台=0x" + now.ToInt64().ToString("X") + "）");

                if (prev == IntPtr.Zero)
                {
                    // ★ 先验探针自身可用性（EXP「探针为空/为假时先验证探针」）：
                    //   某些执行上下文（非交互 / 受限令牌 / 非当前桌面）里 GetForegroundWindow()
                    //   恒返回 0 ⇒ 这里**测不出结论**，不能记成"修复失败"。
                    undetermined = true;
                    sb.AppendLine("⚠  本执行上下文观测不到前台窗口（GetForegroundWindow() 恒为 0）");
                    sb.AppendLine("   ⇒ **判未定论，不判失败**：请在交互桌面会话里跑一次，或直接按热键实机验。");
                }
                else if (got && now == owner.Handle)
                {
                    sb.AppendLine("PASS  ② owner 可成为前台窗口（去掉 WS_EX_NOACTIVATE 后生效）");
                }
                else
                {
                    fail++;
                    sb.AppendLine("FAIL  ② owner 未能成为前台窗口 —— 菜单期就拿不到键盘（快捷键仍会失效）");
                    sb.AppendLine("      可能原因：前台锁定被别的进程占着 / 焦点被抢走。");
                }
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("FAIL  异常：" + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                // ★ 把前台还给进入前的窗口（自检不许留下"焦点跑到自己身上"的副作用）
                if (prev != IntPtr.Zero && NativeMenu.IsWindowAlive(prev))
                    NativeMenu.ForceForeground(prev);
                if (owner != null) owner.Dispose();
                sb.AppendLine("已把前台还给       = 0x" + prev.ToInt64().ToString("X"));
            }

            sb.AppendLine();
            sb.AppendLine("说明：菜单本身（--trigger）会真弹窗，禁无人值守；本自检只验证**前提条件** ——");
            sb.AppendLine("      owner 能被置为前台 ⇒ 菜单模态循环才可能收到键盘（&X 助记键、Esc、方向键）。");
            if (undetermined)
            {
                sb.AppendLine("结果: 环境不支持探测（未定论，非失败）—— 请在交互桌面会话重跑或按热键实机验");
                try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false)); } catch { }
                return 3;   // ★ 3 = 未定论（区别于 1 = 真失败）
            }
            sb.AppendLine("结果: " + (fail == 0 ? "全部通过 ✓" : fail + " 项失败 ✗"));

            try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false)); } catch { }
            return fail == 0 ? 0 : 1;
        }
    }
}
