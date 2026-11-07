using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FrameForge.Sys
{
    public class AffinityResult
    {
        public bool Ok;
        public int Cores;
        public int Moved;
        public long Mask;
        public bool Soft;
        public string Error = "";
    }

    /// <summary>
    /// 核心独占:把游戏绑定到一组完整物理核心,再把其他可访问的进程挪到剩余核心。
    /// 所有被改动的进程都记录了原掩码,还原时按进程名核对后写回,
    /// 避免 PID 复用把别人的亲和性改掉。
    /// </summary>
    public sealed class AffinityEngine
    {
        private sealed class MaskSaved
        {
            public int Pid;
            public string Name = "";
            public long Mask;
        }

        private const int MaxMoved = 64;

        private readonly List<MaskSaved> saved = new List<MaskSaved>();
        private long gameMask;
        private long originalGameMask;
        private int gamePid = -1;
        private string gameName = "";
        private int mode;

        /// <summary>0 = 未生效,1 = 软亲和(CPU Sets),2 = 硬亲和(亲和掩码)。</summary>
        public int Mode { get { return mode; } }
        public long Mask { get { return gameMask; } }

        public int Cores { get { return CpuTopology.CoreCount(gameMask); } }

        public AffinityResult Apply(int pid, string name, int coresWanted)
        {
            var r = new AffinityResult();
            if (pid <= 0) { r.Error = "nopid"; return r; }
            Restore();

            // 0 = 全部核心:不做任何绑定,也就没有需要还原的东西。
            if (coresWanted <= 0)
            {
                r.Ok = true;
                r.Cores = CpuTopology.CoreCount(CpuTopology.AllMask());
                Log.Write("affinity unrestricted pid=" + pid + " cores=" + r.Cores);
                return r;
            }

            int cores;
            long wanted = CpuTopology.GameMask(coresWanted, out cores);
            if (wanted == 0) { r.Error = "notopology"; return r; }

            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_INFORMATION
                | Native.PROCESS_SET_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
                h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_INFORMATION, false, pid);
            if (h == IntPtr.Zero) { r.Error = "open"; return r; }
            try
            {
                IntPtr procMask, sysMask;
                if (!Native.GetProcessAffinityMask(h, out procMask, out sysMask)) { r.Error = "read"; return r; }

                // 只在系统允许的处理器范围内挑,超过 64 线程的机器上会自然收窄。
                long usable = wanted & sysMask.ToInt64();
                if (usable == 0) { r.Error = "nomask"; return r; }

                originalGameMask = procMask.ToInt64();
                gamePid = pid;
                gameName = name ?? "";

                // 软亲和优先:只改默认 CPU Sets,不动其它进程,也不需要管理员权限。
                uint[] ids = CpuTopology.SetIdsForMask(usable);
                bool soft = ids.Length > 0 && Native.SetProcessDefaultCpuSets(h, ids, (uint)ids.Length);
                if (soft)
                {
                    // 回读一次,确认默认集合真的落进去了;没落进去就走硬亲和。
                    uint need;
                    Native.GetProcessDefaultCpuSets(h, null, 0, out need);
                    soft = need > 0;
                }
                if (soft)
                {
                    mode = 1;
                    gameMask = usable;
                    r.Ok = true;
                    r.Soft = true;
                    r.Mask = usable;
                    r.Cores = CpuTopology.CoreCount(usable);
                    Log.Write("affinity soft pid=" + pid + " sets=" + ids.Length + " cores=" + r.Cores);
                    return r;
                }

                // 老系统或接口被拒:退回亲和掩码,再挪走其它进程。
                if (!Native.SetProcessAffinityMask(h, new IntPtr(usable)))
                {
                    r.Error = "set";
                    gamePid = -1;
                    gameName = "";
                    originalGameMask = 0;
                    return r;
                }

                mode = 2;
                gameMask = usable;
                r.Ok = true;
                r.Mask = usable;
                r.Cores = CpuTopology.CoreCount(usable);
            }
            finally { Native.CloseHandle(h); }

            MoveOthers(pid, ref r);
            Log.Write("affinity hard pid=" + pid + " cores=" + r.Cores + " moved=" + r.Moved);
            return r;
        }

        private void MoveOthers(int pid, ref AffinityResult r)
        {
            int self = Process.GetCurrentProcess().Id;
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return; }

            foreach (Process p in procs)
            {
                IntPtr h = IntPtr.Zero;
                try
                {
                    if (p.Id <= 4 || p.Id == self || p.Id == pid) continue;
                    string name = p.ProcessName;
                    if (name.Length == 0 || BoostEngine.Protected.Contains(name)) continue;

                    h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_INFORMATION, false, p.Id);
                    if (h == IntPtr.Zero) continue;

                    IntPtr procMask, sysMask;
                    if (!Native.GetProcessAffinityMask(h, out procMask, out sysMask)) continue;
                    long cur = procMask.ToInt64();
                    if ((cur & gameMask) == 0) continue;

                    long target = cur & ~gameMask;
                    if (target == 0 || target == cur) continue;
                    if (!Native.SetProcessAffinityMask(h, new IntPtr(target))) continue;

                    saved.Add(new MaskSaved { Pid = p.Id, Name = name, Mask = cur });
                    r.Moved++;
                    if (r.Moved >= MaxMoved) break;
                }
                catch { }
                finally
                {
                    if (h != IntPtr.Zero) Native.CloseHandle(h);
                    try { p.Dispose(); } catch { }
                }
            }
        }

        public void Restore()
        {
            for (int i = saved.Count - 1; i >= 0; i--)
            {
                MaskSaved s = saved[i];
                IntPtr h = IntPtr.Zero;
                try
                {
                    h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_INFORMATION, false, s.Pid);
                    if (h != IntPtr.Zero && string.Equals(Native.ProcessName(s.Pid), s.Name, StringComparison.OrdinalIgnoreCase))
                        Native.SetProcessAffinityMask(h, new IntPtr(s.Mask));
                }
                catch { }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
            }
            saved.Clear();

            if (gamePid > 0)
            {
                IntPtr h = IntPtr.Zero;
                try
                {
                    h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_INFORMATION
                        | Native.PROCESS_SET_LIMITED_INFORMATION, false, gamePid);
                    if (h != IntPtr.Zero
                        && string.Equals(Native.ProcessName(gamePid), gameName, StringComparison.OrdinalIgnoreCase))
                    {
                        // 软亲和:清空默认 CPU Sets 即恢复;硬亲和:写回原掩码。
                        if (mode == 1) Native.SetProcessDefaultCpuSets(h, null, 0);
                        else if (originalGameMask != 0) Native.SetProcessAffinityMask(h, new IntPtr(originalGameMask));
                    }
                }
                catch { }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
            }

            if (gamePid > 0) Log.Write("affinity restore pid=" + gamePid + " mode=" + mode);
            gamePid = -1;
            gameMask = 0;
            originalGameMask = 0;
            gameName = "";
            mode = 0;
        }
    }
}