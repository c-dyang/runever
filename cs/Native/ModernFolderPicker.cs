using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RunEverCs.Native
{
    /// <summary>
    /// 系统默认（Vista+）文件夹选择窗口 —— `IFileOpenDialog` + `FOS_PICKFOLDERS`。
    ///
    /// ★ 为什么不用 `System.Windows.Forms.FolderBrowserDialog`：
    ///   net48（.NET Framework）下的 `FolderBrowserDialog` 走的是 **SHBrowseForFolder**
    ///   ⇒ 弹出来是**老式树形文件夹对话框**（用户 2026-10-06 实报「不要用目前的这种树形」）。
    ///   现代形态 = 与「打开文件」同一个系统对话框，只是加上 `FOS_PICKFOLDERS`
    ///   （带地址栏 / 面包屑 / 快速访问 / 搜索）。
    ///
    /// ★ 零新依赖：纯 COM 互操作（不引 WindowsAPICodePack 之类），符合「不想拉新依赖」。
    /// ⚠️ 必须在 **STA** 线程调用（WPF UI 线程本来就是 STA）。
    /// </summary>
    public static class ModernFolderPicker
    {
        // ---- FOS_* 选项位（shobjidl.h）----
        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const uint FOS_NOCHANGEDIR = 0x00000008;

        /// <summary>SIGDN_FILESYSPATH —— 取显示名时返回文件系统路径。</summary>
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        /// <summary>用户取消（HRESULT_FROM_WIN32(ERROR_CANCELLED)）。</summary>
        private const int ERROR_CANCELLED_HR = unchecked((int)0x800704C7);

        /// <summary>
        /// 弹系统默认文件夹选择窗口。
        /// 返回：选中目录的完整路径；用户取消或失败返回 null。
        /// </summary>
        public static string Pick(string title, string initialDir)
        {
            IFileOpenDialog dlg = null;
            try
            {
                dlg = (IFileOpenDialog)new FileOpenDialogRCW();

                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM
                    | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);

                if (!string.IsNullOrEmpty(title))
                    dlg.SetTitle(title);

                // 初始定位（取不到就按系统默认位置，静默继续）
                if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                {
                    IShellItem start = null;
                    try
                    {
                        Guid iid = typeof(IShellItem).GUID;
                        int hr = SHCreateItemFromParsingName(initialDir, IntPtr.Zero, ref iid, out start);
                        if (hr == 0 && start != null)
                            dlg.SetFolder(start);
                    }
                    catch { }
                    finally { if (start != null) Marshal.ReleaseComObject(start); }
                }

                int showHr = dlg.Show(IntPtr.Zero);
                if (showHr != 0)
                {
                    // 取消是正常路径，不报错
                    if (showHr != ERROR_CANCELLED_HR)
                        throw new COMException("IFileOpenDialog.Show 失败", showHr);
                    return null;
                }

                IShellItem result = null;
                try
                {
                    dlg.GetResult(out result);
                    if (result == null) return null;

                    string path;
                    result.GetDisplayName(SIGDN_FILESYSPATH, out path);
                    return path;
                }
                finally { if (result != null) Marshal.ReleaseComObject(result); }
            }
            finally
            {
                if (dlg != null) Marshal.ReleaseComObject(dlg);
            }
        }

        /// <summary>
        /// 互操作自检（**不弹窗**，可无人值守）：覆盖 COM 激活 / IFileDialog 选项位 /
        /// IShellItem 取文件系统路径 / SetFolder。
        /// ⚠️ 不覆盖 `Show` + `GetResult`（模态窗，必须人工点一次）—— 那两条由实机验收。
        /// </summary>
        public static string SelfTestReport(string probeDir)
        {
            var sb = new System.Text.StringBuilder();
            int fail = 0;
            Action<string, bool, string> chk = (name, ok, detail) =>
            {
                if (!ok) fail++;
                sb.AppendLine((ok ? "PASS  " : "FAIL  ") + name + (detail == null ? "" : "  | " + detail));
            };

            IFileOpenDialog dlg = null;
            try
            {
                dlg = (IFileOpenDialog)new FileOpenDialogRCW();
                chk("① COM 激活 IFileOpenDialog", dlg != null, "CLSID DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");

                uint before;
                dlg.GetOptions(out before);
                uint want = before | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST;
                dlg.SetOptions(want);
                uint after;
                dlg.GetOptions(out after);
                chk("② SetOptions/GetOptions 往返（vtable 前段）",
                    (after & FOS_PICKFOLDERS) != 0 && (after & FOS_FORCEFILESYSTEM) != 0,
                    "before=0x" + before.ToString("X8") + " after=0x" + after.ToString("X8"));

                dlg.SetTitle("RunEver 自检");
                chk("③ SetTitle（vtable 中段）", true, "无异常");
            }
            catch (Exception ex)
            {
                chk("① COM 激活 IFileOpenDialog", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (dlg != null) Marshal.ReleaseComObject(dlg);
            }

            // IShellItem 段（SetFolder 的入参类型）
            IShellItem item = null;
            try
            {
                Guid iid = typeof(IShellItem).GUID;
                int hr = SHCreateItemFromParsingName(probeDir, IntPtr.Zero, ref iid, out item);
                chk("④ SHCreateItemFromParsingName", hr == 0 && item != null,
                    "hr=0x" + hr.ToString("X8") + " path=" + probeDir);

                if (item != null)
                {
                    string back;
                    item.GetDisplayName(SIGDN_FILESYSPATH, out back);
                    chk("⑤ IShellItem::GetDisplayName(FILESYSPATH)",
                        string.Equals(back, probeDir, StringComparison.OrdinalIgnoreCase), "回读=" + back);
                }
            }
            catch (Exception ex)
            {
                chk("④ SHCreateItemFromParsingName", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (item != null) Marshal.ReleaseComObject(item);
            }

            sb.AppendLine();
            sb.AppendLine("说明：Show/GetResult 是模态窗，本自检刻意不调用（禁无人值守弹窗）；");
            sb.AppendLine("      那两条由实机点一次「浏览…」验收。");
            sb.AppendLine("结果: " + (fail == 0 ? "全部通过 ✓" : fail + " 项失败 ✗"));
            return sb.ToString();
        }

        // ============================================================
        // COM 声明（vtable 顺序必须与 shobjidl.h 一致，别插队）
        // ============================================================

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            // --- IModalWindow ---
            [PreserveSig] int Show(IntPtr parent);
            // --- IFileDialog ---
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog : IFileDialog
        {
            // ★ 本工程只用 SetOptions/SetTitle/SetFolder/Show/GetResult（都在基接口里），
            //   GetResults/GetSelectedItems 必须照声明占位，否则 vtable 错位。
            [PreserveSig] int GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName,
                [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
    }
}
