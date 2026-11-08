// ============================================================
// RunEver · Everything SDK 直连后端（EV）—— P/Invoke 层 + 可用性探针
//
// 为什么不用静态 DllImport：
//   Everything64.dll 是本程序的可选依赖（可能没随发布包带上、位数不符、被改名）。
//   静态 DllImport 在 DLL 缺失时会抛 DllNotFoundException；若发生在托盘消息循环里
//   就是"崩进程"——违反本项目"绝不崩"的硬约束。
//   ⇒ 统一走 LoadLibraryW + GetProcAddress + 委托：加载失败只等于"后端不可用"。
//
// 本机实测（2026-10-03）：Everything GUI 1.4.1.1032 ⇒ 配 1.4 SDK 的 Everything64.dll。
//   1.5a 的 Everything3_x64.dll 不可混用（导出面/名称都不同）。
//
// 探针判据：命中数 > 0 且 GetLastError()==0 ⇒ 可用。
//   "能 LoadLibrary" ≠ "能用"——IPC 版本不符时 LoadLibrary 成功但查询 0 命中且 LastError=2。
//   一律走 W（Unicode）入口：中文程序名必须。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RunEverCs.Services
{
    /// <summary>当前生效的搜索后端种类。降级链末级为 None（只走 PATH + App Paths）。</summary>
    public enum BackendKind { None = 0, Everything = 1, Es = 2 }

    /// <summary>后端可用性结论（探针产物）。单一结论对象，供自检/窗口/引擎共用。</summary>
    public sealed class BackendStatus
    {
        public BackendKind Kind = BackendKind.None;
        public string DllPath = "";
        public string EsPath = "";
        public string Reason = "";
        public bool DbLoaded;
        public int ProbeHits;
        public int LastError = -1;
        public string EvVersion = "";
        public long ProbeMs = -1;
        public bool FullPathVerified;
        public bool Degraded { get { return Kind != BackendKind.Everything; } }
        public bool EvUsable { get { return Kind == BackendKind.Everything; } }

        public string KindText
        {
            get
            {
                if (Kind == BackendKind.Everything) return "EV";
                if (Kind == BackendKind.Es) return "es.exe";
                return "none";
            }
        }

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("backend=").Append(KindText);
            if (DllPath.Length > 0) sb.Append(" dll=").Append(DllPath);
            if (EsPath.Length > 0) sb.Append(" es=").Append(EsPath);
            if (EvVersion.Length > 0) sb.Append(" ver=").Append(EvVersion);
            if (ProbeMs >= 0) sb.Append(" probe=").Append(ProbeMs).Append("ms");
            if (ProbeHits > 0) sb.Append(" hits=").Append(ProbeHits);
            sb.Append(" dbLoaded=").Append(DbLoaded ? "1" : "0");
            sb.Append(" lastError=").Append(LastError);
            if (Reason.Length > 0) sb.Append(" reason=").Append(Reason);
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------
    // 委托原型（名字与 Everything.h 一一对应）
    // ------------------------------------------------------------
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void EvSetSearchW([MarshalAs(UnmanagedType.LPWStr)] string s);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void EvSetBool([MarshalAs(UnmanagedType.Bool)] bool b);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void EvSetUInt(uint v);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool EvQueryW([MarshalAs(UnmanagedType.Bool)] bool wait);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint EvUIntVoid();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void EvGetFullPathW(uint idx, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder buf, uint nMax);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr EvGetFileNameW(uint idx);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool EvBoolVoid();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool EvBoolUInt(uint idx);

    /// <summary>kernel32 入口（恒在，静态 DllImport 安全）。</summary>
    internal static class EvNative
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr LoadLibraryW(string lpFileName);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        internal static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool FreeLibrary(IntPtr hModule);
    }

    /// <summary>
    /// 已加载的 Everything64.dll 实例（持有委托）。加载/绑定失败 ⇒ TryLoad 返回 false，
    /// **绝不抛异常到调用方**。可选导出缺失只降级对应能力，不整体判失败。
    /// </summary>
    internal sealed class EvApi : IDisposable
    {
        private IntPtr _mod = IntPtr.Zero;
        public string DllPath = "";

        internal EvSetSearchW SetSearchW;
        internal EvSetBool SetMatchPath, SetMatchCase, SetMatchWholeWord, SetRegex;
        internal EvSetUInt SetMax, SetOffset;
        internal EvQueryW QueryW;
        internal EvUIntVoid GetNumResults, GetTotResults, GetLastError, Reset, CleanUp;
        internal EvGetFullPathW GetResultFullPathNameW;
        internal EvGetFileNameW GetResultFileNameW;
        internal EvBoolVoid IsDBLoaded, IsAdmin;
        internal EvBoolUInt IsFileResult, IsFolderResult;
        internal EvUIntVoid GetMajorVersion, GetMinorVersion, GetRevision, GetBuildNumber;

        private T Bind<T>(string name, bool required) where T : class
        {
            IntPtr p = EvNative.GetProcAddress(_mod, name);
            if (p == IntPtr.Zero)
            {
                if (required) throw new MissingMemberException("missing export: " + name);
                return null;
            }
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        /// <summary>加载并绑定。失败时 out reason 给出可读原因（含 Win32 码 / 缺哪个导出）。</summary>
        public bool TryLoad(string path, out string reason)
        {
            reason = "";
            if (string.IsNullOrEmpty(path)) { reason = "DLL 路径为空"; return false; }
            if (!File.Exists(path)) { reason = "DLL 不存在: " + path; return false; }
            try
            {
                _mod = EvNative.LoadLibraryW(path);
            }
            catch (Exception ex)
            {
                reason = "LoadLibrary 异常: " + ex.Message;
                _mod = IntPtr.Zero;
                return false;
            }
            if (_mod == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                // 193 = ERROR_BAD_EXE_FORMAT（位数不符），126 = 找不到模块（依赖缺失）
                reason = "LoadLibrary 失败(win32=" + err + (err == 193 ? " 位数不符" : err == 126 ? " 依赖缺失" : "") + ")";
                return false;
            }
            try
            {
                SetSearchW = Bind<EvSetSearchW>("Everything_SetSearchW", true);
                SetMatchPath = Bind<EvSetBool>("Everything_SetMatchPath", true);
                SetMatchCase = Bind<EvSetBool>("Everything_SetMatchCase", true);
                SetMatchWholeWord = Bind<EvSetBool>("Everything_SetMatchWholeWord", true);
                SetRegex = Bind<EvSetBool>("Everything_SetRegex", true);
                SetMax = Bind<EvSetUInt>("Everything_SetMax", true);
                SetOffset = Bind<EvSetUInt>("Everything_SetOffset", true);
                QueryW = Bind<EvQueryW>("Everything_QueryW", true);
                GetNumResults = Bind<EvUIntVoid>("Everything_GetNumResults", true);
                GetLastError = Bind<EvUIntVoid>("Everything_GetLastError", true);
                Reset = Bind<EvUIntVoid>("Everything_Reset", true);
                CleanUp = Bind<EvUIntVoid>("Everything_CleanUp", true);
                GetResultFullPathNameW = Bind<EvGetFullPathW>("Everything_GetResultFullPathNameW", true);
                GetTotResults = Bind<EvUIntVoid>("Everything_GetTotResults", false);
                GetResultFileNameW = Bind<EvGetFileNameW>("Everything_GetResultFileNameW", false);
                IsDBLoaded = Bind<EvBoolVoid>("Everything_IsDBLoaded", false);
                IsAdmin = Bind<EvBoolVoid>("Everything_IsAdmin", false);
                IsFileResult = Bind<EvBoolUInt>("Everything_IsFileResult", false);
                IsFolderResult = Bind<EvBoolUInt>("Everything_IsFolderResult", false);
                GetMajorVersion = Bind<EvUIntVoid>("Everything_GetMajorVersion", false);
                GetMinorVersion = Bind<EvUIntVoid>("Everything_GetMinorVersion", false);
                GetRevision = Bind<EvUIntVoid>("Everything_GetRevision", false);
                GetBuildNumber = Bind<EvUIntVoid>("Everything_GetBuildNumber", false);
            }
            catch (Exception ex)
            {
                reason = "导出绑定失败: " + ex.Message;
                Dispose();
                return false;
            }
            DllPath = path;
            return true;
        }

        public string VersionText()
        {
            try
            {
                if (GetMajorVersion == null) return "";
                return GetMajorVersion() + "." + GetMinorVersion() + "." + GetRevision() + "." + GetBuildNumber();
            }
            catch { return ""; }
        }

        public bool QueryDbLoaded()
        {
            try { return IsDBLoaded != null && IsDBLoaded(); }
            catch { return false; }
        }

        public void Cleanup()
        {
            try { if (CleanUp != null) CleanUp(); } catch { }
        }

        /// <summary>读回第 idx 条结果的全路径（失败返回 ""）。</summary>
        public string FullPathAt(uint idx)
        {
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                GetResultFullPathNameW(idx, sb, (uint)sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        public void Dispose()
        {
            if (_mod != IntPtr.Zero)
            {
                try { if (CleanUp != null) CleanUp(); } catch { }
                try { EvNative.FreeLibrary(_mod); } catch { }
                _mod = IntPtr.Zero;
            }
        }
    }

    /// <summary>按进程位数选 DLL 名：x64 只认 Everything64.dll（dll\ 里并存 32/ARM/ARM64，误选必 BadImageFormat）。</summary>
    public static class EvDllLocator
    {
        public static string ProcessDllName()
        {
            if (IntPtr.Size == 8) return "Everything64.dll";
            return "Everything32.dll";
        }

        /// <summary>
        /// 候选路径（有序）。配置了 evdll= 即"优先且独占"（填错就降级，不再猜）。
        /// 返回列表只含按位数过滤后的文件名，可能为空（= 无候选）。
        /// </summary>
        public static List<string> Candidates(string configured)
        {
            List<string> list = new List<string>();
            if (!string.IsNullOrEmpty(configured) && configured.Trim().Length > 0)
            {
                list.Add(configured.Trim());
                return list;
            }
            string env = "";
            try { env = Environment.GetEnvironmentVariable("RUNEVER_EVDLL"); } catch { }
            if (!string.IsNullOrEmpty(env) && env.Trim().Length > 0)
            {
                list.Add(env.Trim());
                return list;
            }

            string fn = ProcessDllName();
            string home = "";
            try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }
            string tmp = "";
            try { tmp = Path.GetTempPath(); } catch { }

            Add(list, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fn));
            if (home.Length > 0)
            {
                Add(list, Path.Combine(home, @"WorkBuddy\Claw\claw-tools\quicknote\everything_sdk\dll\" + fn));
                Add(list, Path.Combine(home, @"WorkBuddy\Claw\tools\QuickNote\" + fn));
            }
            if (tmp.Length > 0) Add(list, Path.Combine(tmp, "runany-src", fn));
            return list;
        }

        private static void Add(List<string> list, string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            foreach (string s in list) { if (string.Equals(s, p, StringComparison.OrdinalIgnoreCase)) return; }
            list.Add(p);
        }
    }
}
