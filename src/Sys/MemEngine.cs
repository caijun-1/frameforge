using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace FrameForge.Sys
{
    public enum OptimizeLevel
    {
        Light = 0,
        Standard = 1,
        Deep = 2
    }

    public class MemSample
    {
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong SystemCache;
        public ulong StandbyBytes;
        public uint LoadPercent;
        public int ProcessCount;

        public ulong UsedPhys { get { return TotalPhys > AvailPhys ? TotalPhys - AvailPhys : 0; } }
    }

    public class OptimizeResult
    {
        public OptimizeLevel Level;
        public bool Smart;
        public ulong Before;
        public ulong After;
        public long Released;
        public long StandbyFreed;
        public long CacheFreed;
        public int Touched;
        public int Skipped;
        public bool AdminRequired;
        public bool CacheCleared;
        public bool StandbyPurged;
    }

    /// <summary>
    /// 内存整理:移植自 MemSweep 的实现。
    /// 只裁剪后台进程的空闲工作集、清系统文件缓存、清待命列表;前台与保护进程永不触碰。
    /// </summary>
    public static class MemEngine
    {
        private static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Idle", "System", "Registry", "Memory Compression", "csrss", "wininit", "winlogon",
            "services", "lsass", "smss", "svchost", "MsMpEng", "MpDefenderCoreService",
            "dwm", "FrameForge", "audiodg", "SecurityHealthService", "WUDFHost", "fontdrvhost"
        };

        /// <summary>
        /// 反作弊组件一律不碰:暂停或裁剪 ACE / EAC / BattlEye 之类的进程,
        /// 会被当成篡改运行环境,后果远比省下的那点内存严重。
        /// 用前缀匹配覆盖同系列的不同版本和进程名。
        /// </summary>
        public static bool IsProtectedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (Protected.Contains(name)) return true;
            string n = name.ToLowerInvariant();
            return n.StartsWith("sguard") || n.StartsWith("ace") || n.StartsWith("anticheatexpert")
                || n.StartsWith("tenprotect") || n.StartsWith("tensafe") || n.StartsWith("qqprotect")
                || n.StartsWith("easyanticheat") || n.StartsWith("beservice") || n.StartsWith("battleye")
                || n.StartsWith("vgc") || n.StartsWith("nprotect") || n.StartsWith("gameguard")
                || n.StartsWith("xigncode") || n.StartsWith("krsdk") || n.StartsWith("kuro")
                || n.StartsWith("rockstar") || n.StartsWith("socialclub") || n.StartsWith("playgtav");
        }

        private const ulong MinWorkingSetBytes = 20UL << 20;
        private const ulong LightMaxWorkingSet = 300UL << 20;

        public static bool IsAdmin
        {
            get
            {
                try
                {
                    using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        // ---------------- 采样 ----------------

        public static MemSample Sample()
        {
            var s = new MemSample();
            var ms = new Native.MEMORYSTATUSEX();
            ms.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (Native.GlobalMemoryStatusEx(ref ms))
            {
                s.TotalPhys = ms.ullTotalPhys;
                s.AvailPhys = ms.ullAvailPhys;
                s.LoadPercent = ms.dwMemoryLoad;
            }

            ulong pageSize = 4096;
            var pi = new Native.PERFORMANCE_INFORMATION();
            int size = Marshal.SizeOf(typeof(Native.PERFORMANCE_INFORMATION));
            if (Native.GetPerformanceInfo(out pi, size))
            {
                if (pi.PageSize != IntPtr.Zero) pageSize = (ulong)pi.PageSize.ToInt64();
                s.SystemCache = Native.PagesToBytes(pi.SystemCache, pageSize);
                s.ProcessCount = pi.ProcessCount;
                if (s.TotalPhys == 0) s.TotalPhys = Native.PagesToBytes(pi.PhysicalTotal, pageSize);
                if (s.AvailPhys == 0) s.AvailPhys = Native.PagesToBytes(pi.PhysicalAvailable, pageSize);
            }

            s.StandbyBytes = ReadStandbyBytes(pageSize);
            return s;
        }

        /// <summary>SYSTEM_MEMORY_LIST_INFORMATION 前 5 个字段是各类页数,紧跟 8 个 standby 优先级桶。</summary>
        private static ulong ReadStandbyBytes(ulong pageSize)
        {
            int count = 13;
            int cb = IntPtr.Size * count;
            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal(cb);
                for (int i = 0; i < count; i++) Marshal.WriteIntPtr(buf, i * IntPtr.Size, IntPtr.Zero);
                int len;
                int ret = Native.NtQuerySystemInformation(Native.SystemMemoryListInformation, buf, cb, out len);
                if (ret == Native.StatusInfoLengthMismatch && len > cb)
                {
                    Marshal.FreeHGlobal(buf);
                    buf = Marshal.AllocHGlobal(len);
                    for (int i = 0; i < len / IntPtr.Size; i++) Marshal.WriteIntPtr(buf, i * IntPtr.Size, IntPtr.Zero);
                    ret = Native.NtQuerySystemInformation(Native.SystemMemoryListInformation, buf, len, out len);
                }
                if (ret != 0) return 0;
                ulong pages = 0;
                for (int i = 5; i < 13; i++)
                {
                    long v = Marshal.ReadIntPtr(buf, i * IntPtr.Size).ToInt64();
                    if (v > 0) pages += (ulong)v;
                }
                return pages * pageSize;
            }
            catch { return 0; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        // ---------------- 提权 ----------------

        public static bool EnablePrivilege(string name)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!Native.OpenProcessToken(Native.GetCurrentProcess(),
                        Native.TOKEN_ADJUST_PRIVILEGES | Native.TOKEN_QUERY, out token)) return false;
                Native.LUID luid;
                if (!Native.LookupPrivilegeValue(null, name, out luid)) return false;
                var tp = new Native.TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Privileges = new Native.LUID_AND_ATTRIBUTES();
                tp.Privileges.Luid = luid;
                tp.Privileges.Attributes = Native.SE_PRIVILEGE_ENABLED;
                if (!Native.AdjustTokenPrivileges(token, false, ref tp,
                        Marshal.SizeOf(typeof(Native.TOKEN_PRIVILEGES)), IntPtr.Zero, IntPtr.Zero)) return false;
                return Marshal.GetLastWin32Error() == 0;
            }
            catch { return false; }
            finally { if (token != IntPtr.Zero) Native.CloseHandle(token); }
        }

        private static bool EmptyOne(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_SET_QUOTA, false, pid);
            if (h == IntPtr.Zero)
                h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, pid);
            if (h == IntPtr.Zero) return false;
            try { return Native.EmptyWorkingSet(h); }
            finally { Native.CloseHandle(h); }
        }

        public static void TrimSelf()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }

        // ---------------- 系统级动作 ----------------

        public static bool ClearFileCache()
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                int cb = IntPtr.Size * 2;
                buf = Marshal.AllocHGlobal(cb);
                Marshal.WriteIntPtr(buf, IntPtr.Zero);
                Marshal.WriteIntPtr(buf, IntPtr.Size, IntPtr.Zero);
                return Native.NtSetSystemInformation(Native.SystemFileCacheInformation, buf, cb) == 0;
            }
            catch { return false; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        private static bool SetMemoryList(int command)
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(buf, command);
                return Native.NtSetSystemInformation(Native.SystemMemoryListInformation, buf, 4) == 0;
            }
            catch { return false; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        // ---------------- 进程枚举 ----------------

        private class ProcEntry : IComparable<ProcEntry>
        {
            public int Pid;
            public string Name;
            public ulong WorkingSet;

            public int CompareTo(ProcEntry o)
            {
                if (o == null) return 1;
                return WorkingSet.CompareTo(o.WorkingSet);
            }
        }

        private static List<ProcEntry> SnapshotProcesses()
        {
            var list = new List<ProcEntry>();
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return list; }

            foreach (Process p in procs)
            {
                try
                {
                    long w = p.WorkingSet64;
                    list.Add(new ProcEntry { Pid = p.Id, Name = p.ProcessName, WorkingSet = w > 0 ? (ulong)w : 0 });
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            list.Sort();
            return list;
        }

        private static int ForegroundPid()
        {
            try
            {
                IntPtr hwnd = Native.GetForegroundWindow();
                int pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                return pid;
            }
            catch { return -1; }
        }

        /// <summary>本次整理不要碰的进程:当前游戏与用户正在交互的前台进程。</summary>
        private static HashSet<int> KeepPids(int extra)
        {
            var set = new HashSet<int>();
            try
            {
                int fg = ForegroundPid();
                if (fg > 0) set.Add(fg);
                if (extra > 0) set.Add(extra);
            }
            catch { }
            return set;
        }

        private static ulong ReleaseCap(OptimizeLevel level, ulong totalPhys)
        {
            double pct = level == OptimizeLevel.Light ? 0.08 : (level == OptimizeLevel.Deep ? 0.25 : 0.18);
            ulong cap = (ulong)(totalPhys * pct);
            ulong min = 256UL << 20;
            if (cap < min) cap = min;
            ulong max = (ulong)(totalPhys * 0.25);
            if (max > 0 && cap > max) cap = max;
            return cap;
        }

        /// <summary>工作集归还给系统是异步的,采样到连续稳定为止并取峰值。</summary>
        private static MemSample Settle()
        {
            TrimSelf();
            MemSample last = Sample();
            ulong best = last.AvailPhys;
            int flat = 0;
            for (int k = 0; k < 10; k++)
            {
                Thread.Sleep(200);
                MemSample cur = Sample();
                if (cur.AvailPhys > best) best = cur.AvailPhys;
                if (cur.AvailPhys < last.AvailPhys + (2UL << 20)) flat++; else flat = 0;
                last = cur;
                if (flat >= 3 && k >= 3) break;
            }
            if (last.AvailPhys < best) last.AvailPhys = best;
            return last;
        }

        private static void FillResult(OptimizeResult r, MemSample before, MemSample after)
        {
            r.Before = before.AvailPhys;
            r.After = after.AvailPhys;
            long delta = (long)after.AvailPhys - (long)before.AvailPhys;
            r.Released = delta > 0 ? delta : 0;
            long sb = (long)before.StandbyBytes - (long)after.StandbyBytes;
            long cc = (long)before.SystemCache - (long)after.SystemCache;
            r.StandbyFreed = sb > 0 ? sb : 0;
            r.CacheFreed = cc > 0 ? cc : 0;
            Log.Write("trim level=" + r.Level + " smart=" + (r.Smart ? "1" : "0")
                + " releasedMB=" + (r.Released / 1048576L)
                + " standbyMB=" + (r.StandbyFreed / 1048576L)
                + " cacheMB=" + (r.CacheFreed / 1048576L)
                + " touched=" + r.Touched + " skipped=" + r.Skipped);
        }

        // ---------------- 主入口 ----------------

        /// <summary>
        /// 手动整理。gamePid 为当前游戏,永远不会被裁剪。
        /// systemWide=false 时只归还后台进程的空闲工作集,不清系统文件缓存——
        /// 游戏运行中清缓存会把刚加载的资源全部赶出去,读盘抖动直接变成掉帧。
        /// </summary>
        public static OptimizeResult Optimize(OptimizeLevel level, int gamePid, Action<string> progress, bool systemWide)
        {
            var result = new OptimizeResult();
            result.Level = level;
            var before = Sample();
            result.Before = before.AvailPhys;

            bool admin = IsAdmin;
            EnablePrivilege("SeIncreaseQuotaPrivilege");
            EnablePrivilege("SeProfileSingleProcessPrivilege");
            EnablePrivilege("SeIncreaseWorkingSetPrivilege");

            if (progress != null) progress("sweep");
            int self = Process.GetCurrentProcess().Id;
            HashSet<int> keep = KeepPids(gamePid);
            List<ProcEntry> entries = SnapshotProcesses();

            ulong cap = ReleaseCap(level, before.TotalPhys);
            ulong budget = 0;
            int touched = 0, skipped = 0;
            foreach (ProcEntry e in entries)
            {
                if (budget >= cap) break;
                if (e.Pid == self || e.Pid <= 4) { skipped++; continue; }
                if (string.IsNullOrEmpty(e.Name) || IsProtectedName(e.Name)) { skipped++; continue; }
                if (keep.Contains(e.Pid)) { skipped++; continue; }
                if (e.WorkingSet < MinWorkingSetBytes) { skipped++; continue; }
                if (level == OptimizeLevel.Light && e.WorkingSet > LightMaxWorkingSet) { skipped++; continue; }
                if (EmptyOne(e.Pid)) { touched++; budget += e.WorkingSet; }
                else skipped++;
            }

            if (level != OptimizeLevel.Light && systemWide)
            {
                if (progress != null) progress("cache");
                result.CacheCleared = ClearFileCache();
                if (!result.CacheCleared && !admin) result.AdminRequired = true;
            }

            if (level == OptimizeLevel.Deep && systemWide)
            {
                if (progress != null) progress("deep");
                SetMemoryList(Native.MemoryEmptyWorkingSets);
                SetMemoryList(Native.MemoryFlushModifiedList);
                result.StandbyPurged = SetMemoryList(Native.MemoryPurgeStandbyList);
                if (!admin && !result.StandbyPurged) result.AdminRequired = true;
            }

            FillResult(result, before, Settle());
            result.Touched = touched;
            result.Skipped = skipped;
            return result;
        }

        /// <summary>
        /// 自动维护:清文件缓存与待命列表。游戏运行中不要调用——
        /// 缓存被清空后游戏要重新读盘,帧率会出现明显的一次性下跌。
        /// </summary>
        public static OptimizeResult SmartClean(Action<string> progress)
        {
            var result = new OptimizeResult();
            result.Level = OptimizeLevel.Light;
            result.Smart = true;
            var before = Sample();
            result.Before = before.AvailPhys;

            bool admin = IsAdmin;
            EnablePrivilege("SeIncreaseQuotaPrivilege");
            EnablePrivilege("SeProfileSingleProcessPrivilege");

            if (progress != null) progress("cache");
            result.CacheCleared = ClearFileCache();
            if (!result.CacheCleared && !admin) result.AdminRequired = true;

            if (before.StandbyBytes * 100.0 / Math.Max(1UL, before.TotalPhys) >= 3.0)
            {
                if (progress != null) progress("standby");
                result.StandbyPurged = SetMemoryList(Native.MemoryPurgeStandbyList);
                if (!admin && !result.StandbyPurged) result.AdminRequired = true;
            }

            FillResult(result, before, Settle());
            return result;
        }

        public static void RestartElevated(string args)
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = Process.GetCurrentProcess().MainModule.FileName;
                psi.Arguments = args ?? "";
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                Environment.Exit(0);
            }
            catch (Exception ex) { Log.Write("elevate: " + ex.Message); }
        }
    }
}