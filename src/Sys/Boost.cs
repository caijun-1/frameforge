using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    /// <summary>用户在加速页勾选的优化项。</summary>
    public class BoostPlan
    {
        public bool Priority = true;    // 游戏进程 -> 高优先级
        public bool PowerPlan = true;   // 高性能电源计划
        public bool Timer = true;       // 系统计时器 1ms
        public bool Dvr = true;         // 关闭 Game DVR / 全屏优化
        public bool Memory = true;      // 内存整理
        public bool Background = false; // 进阶:暂停后台进程
        public bool Affinity = false;   // 进阶:游戏独占核心
        public bool Irq = false;        // 进阶:设备中断避让(重启生效)
        public int Cores = 0;           // 0 = 自动(一半物理核心)

        public bool Any
        {
            get { return Priority || PowerPlan || Timer || Dvr || Memory || Background || Affinity || Irq; }
        }
    }

    /// <summary>一个后台进程,用于进阶模式的暂停列表。</summary>
    public class BackgroundProc
    {
        public int Pid;
        public string Name = "";
        public ulong WorkingSet;
    }

    /// <summary>一次加速的结果,界面直接展示。</summary>
    public class BoostResult
    {
        public bool Ok;
        public string Error = "";
        public bool NeedAdmin;
        public long MemReleased;
        public int Suspended;
        public int PriorityOk;
        public int PowerOk;
        public int TimerOk;
        public int DvrOk;
        public int MemoryOk;
        public int AffinityOk;
        public int AffinityCores;
        public int AffinityMoved;
        public int IrqOk;
        public int IrqDevices;
        public bool IrqNeedReboot;
    }

    /// <summary>
    /// 帧率加速引擎:只做有依据、可回滚的系统级调整。
    /// 1) 提升游戏进程优先级;2) 切到高性能电源计划;3) 计时器调到 1ms;
    /// 4) 关闭 Game DVR 与全屏优化;5) 整理内存;6) 进阶:暂停后台进程。
    /// 所有改动都会记录原值,「还原」或退出时逐项恢复。
    /// </summary>
    public sealed class BoostEngine : IDisposable
    {
        public const string SchemeHigh = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        public const string SchemeBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

        private const string GameConfigStore = @"System\GameConfigStore";
        private const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";

        private static readonly Regex GuidRx = new Regex(
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            RegexOptions.Compiled);

        private readonly List<RegSaved> regSaved = new List<RegSaved>();
        private readonly List<Suspended> suspended = new List<Suspended>();
        private readonly AffinityEngine affinity = new AffinityEngine();
        private readonly InterruptEngine irq = new InterruptEngine();

        private string prevScheme = "";
        private uint prevPriority;
        private bool timerSet;

        public bool Active;
        public int GamePid = -1;
        public string GameName = "";
        public DateTime StartedAt = DateTime.MinValue;

        public int SuspendedCount { get { return suspended.Count; } }

        public static bool IsAdmin
        {
            get { return MemEngine.IsAdmin; }
        }

        // =================================================================
        //  探测
        // =================================================================

        /// <summary>前台窗口铺满所在显示器即为游戏;其余情况返回 pid = -1。</summary>
        public static int DetectGamePid(out string name)
        {
            name = "";
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return -1;
            int pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid <= 4) return -1;
            string n = Native.ProcessName(pid);
            if (n.Length == 0) return -1;
            if (IsShell(n)) return -1;

            if (!Monitor.IsGameWindow(hwnd, pid)) return -1;

            name = n;
            return pid;
        }

        private static bool IsShell(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "explorer": case "dwm": case "frameforge": case "taskmgr":
                case "searchapp": case "shellexperiencehost": case "startmenuexperiencehost":
                case "applicationframehost": case "lockapp": case "textinputhost":
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 进阶模式的候选后台进程:占用高、没有可见窗口、不在保护名单里。
        /// 有可见窗口的程序不会被暂停,避免用户眼前的窗口卡死。
        /// </summary>
        public static BackgroundProc[] Candidates(int gamePid, int max)
        {
            var list = new List<BackgroundProc>();
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return new BackgroundProc[0]; }

            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in procs)
            {
                try
                {
                    if (p.Id <= 4 || p.Id == self || p.Id == gamePid) continue;
                    if (p.MainWindowHandle != IntPtr.Zero) continue;
                    long ws = p.WorkingSet64;
                    if (ws < 80L << 20) continue;
                    string name = p.ProcessName;
                    if (name.Length == 0 || Protected.Contains(name)) continue;
                    list.Add(new BackgroundProc { Pid = p.Id, Name = name, WorkingSet = (ulong)ws });
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            list.Sort(delegate(BackgroundProc a, BackgroundProc b) { return b.WorkingSet.CompareTo(a.WorkingSet); });
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list.ToArray();
        }

        internal static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Idle", "System", "Registry", "Memory Compression", "csrss", "wininit", "winlogon",
            "services", "lsass", "smss", "svchost", "MsMpEng", "MpDefenderCoreService",
            "dwm", "FrameForge", "audiodg", "SecurityHealthService", "WUDFHost", "fontdrvhost",
            "SystemSettings", "winlogon", "taskhostw", "ctfmon", "RuntimeBroker"
        };

        // =================================================================
        //  应用
        // =================================================================

        public BoostResult Apply(BoostPlan plan, int gamePid, string gameName)
        {
            var r = new BoostResult();
            if (plan == null || !plan.Any) { r.Error = "empty"; return r; }
            if (gamePid <= 0) { r.Error = "nogame"; return r; }
            if (Active) Restore();

            GamePid = gamePid;
            GameName = gameName ?? "";
            StartedAt = DateTime.Now;

            if (plan.Priority && ApplyPriority(gamePid)) r.PriorityOk = 1;
            if (plan.Affinity)
            {
                try
                {
                    AffinityResult a = affinity.Apply(gamePid, GameName, plan.Cores);
                    r.AffinityOk = a.Ok ? 1 : 0;
                    r.AffinityCores = a.Cores;
                    r.AffinityMoved = a.Moved;
                }
                catch (Exception ex) { Log.Write("boost affinity: " + ex.Message); }
            }
            if (plan.Irq)
            {
                try
                {
                    IrqResult q = irq.Apply(plan.Cores);
                    r.IrqOk = q.Ok ? 1 : 0;
                    r.IrqDevices = q.Set;
                    r.IrqNeedReboot = q.Ok;
                }
                catch (Exception ex) { Log.Write("boost irq: " + ex.Message); }
            }
            if (plan.PowerPlan && ApplyPowerPlan()) r.PowerOk = 1;
            if (plan.Timer && ApplyTimer()) r.TimerOk = 1;
            if (plan.Dvr && ApplyDvr()) r.DvrOk = 1;
            if (plan.Memory)
            {
                try
                {
                    OptimizeResult m = MemEngine.Optimize(OptimizeLevel.Standard, gamePid, null);
                    r.MemReleased = m.Released;
                    r.MemoryOk = 1;
                }
                catch (Exception ex) { Log.Write("boost memory: " + ex.Message); }
            }
            if (plan.Background)
            {
                try { r.Suspended = SuspendBackground(gamePid); }
                catch (Exception ex) { Log.Write("boost suspend: " + ex.Message); }
            }

            r.Ok = r.PriorityOk + r.PowerOk + r.TimerOk + r.DvrOk + r.MemoryOk + r.AffinityOk + r.IrqOk > 0
                || r.Suspended > 0;
            r.NeedAdmin = !IsAdmin && (r.PowerOk == 0 || r.TimerOk == 0);
            Active = true;
            Log.Write("boost apply pid=" + gamePid + " ok=" + (r.Ok ? "1" : "0")
                + " prio=" + r.PriorityOk + " power=" + r.PowerOk + " timer=" + r.TimerOk
                + " dvr=" + r.DvrOk + " mem=" + (r.MemReleased / 1048576L) + "MB"
                + " suspended=" + r.Suspended
                + " affinity=" + r.AffinityOk + "/" + r.AffinityCores + "c/" + r.AffinityMoved + "p"
                + " irq=" + r.IrqOk + "/" + r.IrqDevices + "d");
            return r;
        }

        private bool ApplyPriority(int pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return false;
                prevPriority = Native.GetPriorityClass(h);
                return Native.SetPriorityClass(h, Native.HIGH_PRIORITY_CLASS);
            }
            catch { return false; }
            finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
        }

        private bool ApplyPowerPlan()
        {
            try
            {
                string cur = ActiveSchemeGuid();
                if (cur.Length == 0) return false;
                if (string.Equals(cur, SchemeHigh, StringComparison.OrdinalIgnoreCase)) return true;
                prevScheme = cur;
                return SetScheme(SchemeHigh);
            }
            catch (Exception ex) { Log.Write("boost power: " + ex.Message); return false; }
        }

        private bool ApplyTimer()
        {
            try
            {
                if (Native.timeBeginPeriod(1) == 0) { timerSet = true; return true; }
            }
            catch { }
            return false;
        }

        private bool ApplyDvr()
        {
            try
            {
                SetDword(GameConfigStore, "GameDVR_Enabled", 0);
                SetDword(GameConfigStore, "GameDVR_FSEBehaviorMode", 2);
                SetDword(GameConfigStore, "GameDVR_HonorUserFSEBehaviorMode", 1);
                SetDword(GameConfigStore, "GameDVR_DXGIHonorFSEWindowsCompatible", 1);
                SetDword(GameDvrKey, "AppCaptureEnabled", 0);
                return true;
            }
            catch (Exception ex) { Log.Write("boost dvr: " + ex.Message); return false; }
        }

        private int SuspendBackground(int gamePid)
        {
            BackgroundProc[] list = Candidates(gamePid, 6);
            int done = 0;
            foreach (BackgroundProc b in list)
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, b.Pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    if (Native.NtSuspendProcess(h) == 0)
                    {
                        suspended.Add(new Suspended { Pid = b.Pid, Name = b.Name, WorkingSet = b.WorkingSet });
                        done++;
                    }
                }
                catch { }
                finally { Native.CloseHandle(h); }
            }
            return done;
        }

        // =================================================================
        //  还原
        // =================================================================

        public void Restore()
        {
            if (!Active) return;

            try { irq.Restore(); } catch { }
            try { affinity.Restore(); } catch { }

            for (int i = suspended.Count - 1; i >= 0; i--)
            {
                Suspended s = suspended[i];
                IntPtr h = IntPtr.Zero;
                try
                {
                    h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, s.Pid);
                    if (h != IntPtr.Zero && string.Equals(Native.ProcessName(s.Pid), s.Name, StringComparison.OrdinalIgnoreCase))
                        Native.NtResumeProcess(h);
                }
                catch { }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
            }
            suspended.Clear();

            if (timerSet)
            {
                try { Native.timeEndPeriod(1); } catch { }
                timerSet = false;
            }

            RestoreRegistry();

            if (prevScheme.Length > 0)
            {
                try { SetScheme(prevScheme); } catch { }
                prevScheme = "";
            }

            if (prevPriority != 0 && GamePid > 0)
            {
                IntPtr h = IntPtr.Zero;
                try
                {
                    h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION, false, GamePid);
                    if (h != IntPtr.Zero) Native.SetPriorityClass(h, prevPriority);
                }
                catch { }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
                prevPriority = 0;
            }

            Active = false;
            StartedAt = DateTime.MinValue;
            GamePid = -1;
            GameName = "";
            Log.Write("boost restore");
        }

        private class RegSaved
        {
            public string Path;
            public string Name;
            public bool Exists;
            public object Value;
        }

        private void SetDword(string path, string name, int value)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(path))
            {
                if (k == null) return;
                object old = k.GetValue(name, null);
                regSaved.Add(new RegSaved { Path = path, Name = name, Exists = old != null, Value = old });
                k.SetValue(name, value, RegistryValueKind.DWord);
            }
        }

        private void RestoreRegistry()
        {
            for (int i = regSaved.Count - 1; i >= 0; i--)
            {
                RegSaved s = regSaved[i];
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(s.Path))
                    {
                        if (k == null) continue;
                        if (s.Exists) k.SetValue(s.Name, s.Value);
                        else k.DeleteValue(s.Name, false);
                    }
                }
                catch { }
            }
            regSaved.Clear();
        }

        // =================================================================
        //  电源计划
        // =================================================================

        public static string ActiveSchemeGuid()
        {
            try
            {
                string outp = Run("powercfg", "/getactivescheme");
                Match m = GuidRx.Match(outp);
                return m.Success ? m.Value.ToLowerInvariant() : "";
            }
            catch { return ""; }
        }

        private static bool SetScheme(string guid)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/setactive " + guid);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(4000))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Log.Write("powercfg set: " + ex.Message);
                return false;
            }
        }

        private static string Run(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append(p.StandardOutput.ReadToEnd());
                    sb.Append(p.StandardError.ReadToEnd());
                    p.WaitForExit(3000);
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                Log.Write("run " + exe + ": " + ex.Message);
                return "";
            }
        }

        // =================================================================
        //  进程存活判断
        // =================================================================

        public bool GameAlive()
        {
            if (!Active || GamePid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(GamePid))
                    return !p.HasExited && p.ProcessName.Length > 0;
            }
            catch { return false; }
        }

        public string SuspendedNames()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < suspended.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(suspended[i].Name);
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            Restore();
        }

        private class Suspended
        {
            public int Pid;
            public string Name = "";
            public ulong WorkingSet;
        }
    }
}