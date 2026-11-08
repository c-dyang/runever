using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RunEverCs.Services
{
    /// <summary>
    /// 常驻采集客户端（2026-11-07）—— 把 AHK 采集器改成**主程序的子进程**，
    /// 用 stdin/stdout 行协议往返，不再每次触发都起一个进程。
    ///
    /// 为什么（实测）：
    ///   · 一次性模式每次 ~396ms（编译版 CaptureRunner.exe 全流程），
    ///     其中绝大部分是解释器启动 + 进程创建；
    ///   · 常驻后单次往返 ~3ms；常驻内存 +11.4MB 工作集（私有仅 +2.4MB）。
    ///
    /// 生命周期（用户要求"按进程树挂，死了一起死"）：
    ///   ① 管道 EOF：主进程退出或被强杀 ⇒ OS 关闭 stdin 句柄 ⇒ AHK 读到 EOF 自行 ExitApp；
    ///   ② Job Object（JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE）：主进程消失 ⇒ job 句柄关闭
    ///      ⇒ 内核直接杀掉子进程，覆盖"AHK 卡在 SendInput/剪贴板上"的场景。
    ///   两条独立生效，任一条可用都不会留孤儿进程。
    ///
    /// ★ 采集仍在 AHK 侧（用户 2026-10-03 定案）：SendInput + ClipWait + ClipboardAll
    ///   这套组合在 C# 里重写风险高、收益低，本类只负责管道和 JSON 解析。
    /// </summary>
    public class ResidentCaptureClient : IDisposable
    {
        private readonly string _runnerPath;   // CaptureRunner.exe 或 .ahk
        private readonly string _ahkExe;       // 解释器（runnerPath 是 .ahk 时才用到）
        private readonly int _timeoutMs;

        private readonly object _gate = new object();
        private Process _proc;
        private IntPtr _job = IntPtr.Zero;

        /// <summary>累计"真正请求了一次采集"的次数（自检断言用，语义同 CaptureClient）。</summary>
        public int CaptureCount { get; private set; }

        /// <summary>采集器（exe 或 ahk）路径，排障用。</summary>
        public string RunnerPath { get { return _runnerPath; } }

        /// <summary>常驻进程当前是否活着。</summary>
        public bool IsRunning
        {
            get
            {
                try { return _proc != null && !_proc.HasExited; }
                catch { return false; }
            }
        }

        public ResidentCaptureClient(string runnerPath, string ahkExe = null, int timeoutMs = 3000)
        {
            _runnerPath = runnerPath;
            _ahkExe = string.IsNullOrEmpty(ahkExe)
                ? @"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe"
                : ahkExe;
            _timeoutMs = timeoutMs;
        }

        public bool IsAvailable()
        {
            return !string.IsNullOrEmpty(_runnerPath) && File.Exists(_runnerPath);
        }

        /// <summary>采集一次；任何失败都返回空 Context（绝不抛异常）。</summary>
        public Context Capture(Action<string> log = null)
        {
            lock (_gate)
            {
                CaptureCount++;
                var ctx = new Context();

                if (!IsAvailable())
                {
                    if (log != null) log("常驻采集: 采集器不存在 " + _runnerPath);
                    return ctx;
                }
                if (!EnsureStarted(log)) return ctx;

                var sw = Stopwatch.StartNew();
                try
                {
                    _proc.StandardInput.WriteLine("cap");
                    _proc.StandardInput.Flush();

                    var read = _proc.StandardOutput.ReadLineAsync();
                    if (!read.Wait(_timeoutMs))
                    {
                        if (log != null) log("常驻采集超时（" + _timeoutMs + "ms），重启采集进程");
                        KillProcess();
                        return ctx;
                    }

                    string line = read.Result;
                    if (line == null)
                    {
                        if (log != null) log("常驻采集进程已断开，下次触发会重启");
                        KillProcess();
                        return ctx;
                    }

                    CaptureClient.ParseInto(line, ctx);
                    if (log != null)
                        log("采集耗时 " + sw.ElapsedMilliseconds + "ms（常驻 pid=" + SafePid() + "）");
                }
                catch (Exception ex)
                {
                    if (log != null) log("常驻采集异常: " + ex.Message);
                    KillProcess();
                }
                return ctx;
            }
        }

        /// <summary>主动关停（正常退出时调用；不收尾也能靠管道 EOF / Job Object 兜住）。</summary>
        public void Shutdown()
        {
            lock (_gate) KillProcess();
        }

        public void Dispose()
        {
            Shutdown();
            CloseJob();
        }

        // ============================================================
        // 进程生命周期
        // ============================================================
        private bool EnsureStarted(Action<string> log)
        {
            if (IsRunning) return true;

            KillProcess();
            try
            {
                var psi = new ProcessStartInfo();
                if (_runnerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    psi.FileName = _runnerPath;
                    psi.Arguments = "--serve";
                }
                else
                {
                    psi.FileName = _ahkExe;
                    psi.Arguments = "\"" + _runnerPath + "\" --serve";
                }
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                // AHK 侧以 UTF-8 读写；这里必须对上，否则非 ASCII 路径会废。
                // ★ 只设输出编码：net48 的 ProcessStartInfo 没有 StandardInputEncoding
                //   （那是 .NET Core 才有的），而请求行是纯 ASCII（cap / quit），
                //   输入编码无所谓。
                psi.StandardOutputEncoding = new UTF8Encoding(false);

                var p = Process.Start(psi);
                if (p == null)
                {
                    if (log != null) log("常驻采集: 进程启动失败");
                    return false;
                }

                _proc = p;
                AssignToJob(p);
                if (log != null) log("常驻采集: 已启动 pid=" + SafePid() + "（" + Path.GetFileName(_runnerPath) + " --serve）");
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("常驻采集: 启动异常 " + ex.Message);
                KillProcess();
                return false;
            }
        }

        private void KillProcess()
        {
            var p = _proc;
            _proc = null;
            if (p == null) return;

            try { p.StandardInput.Close(); } catch { }   // → EOF → AHK 自行 ExitApp
            try
            {
                if (!p.WaitForExit(1200)) p.Kill();
            }
            catch { try { p.Kill(); } catch { } }
            try { p.Dispose(); } catch { }
        }

        private string SafePid()
        {
            try { return _proc == null ? "?" : _proc.Id.ToString(); } catch { return "?"; }
        }

        // ============================================================
        // Job Object：主进程一消失，内核就把子进程杀掉
        //   即使 AHK 正卡在 SendInput / 剪贴板上（读不到 EOF），这条也会生效。
        // ============================================================
        private void AssignToJob(Process p)
        {
            try
            {
                if (_job == IntPtr.Zero) _job = CreateKillOnCloseJob();
                if (_job == IntPtr.Zero) return;
                AssignProcessToJobObject(_job, p.Handle);
            }
            catch { /* 分不到 job 也不致命：管道 EOF 仍是主兜底 */ }
        }

        private void CloseJob()
        {
            try
            {
                if (_job != IntPtr.Zero) CloseHandle(_job);
            }
            catch { }
            finally { _job = IntPtr.Zero; }
        }

        private static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = IntPtr.Zero;
            IntPtr info = IntPtr.Zero;
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;

                var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                ext.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                int len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                info = Marshal.AllocHGlobal(len);
                Marshal.StructureToPtr(ext, info, false);

                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, info, (uint)len))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
                return job;
            }
            catch
            {
                if (job != IntPtr.Zero) { try { CloseHandle(job); } catch { } }
                return IntPtr.Zero;
            }
            finally
            {
                if (info != IntPtr.Zero) { try { Marshal.FreeHGlobal(info); } catch { } }
            }
        }

        // ---------------- P/Invoke ----------------
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfo);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
