using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace FrameForge.Sys
{
    /// <summary>
    /// 实时加速:常驻监视前台程序,把前台进程提到高优先级、同名子进程提到高于正常,
    /// 并叠加高性能电源计划与 1ms 计时器;关闭时逐项还原。
    /// </summary>
    public sealed class GlobalBoost : IDisposable
    {
        private const int TickMs = 1000;
        /// <summary>进程枚举(常用程序、同名进程计数)的重活间隔,单位秒;每秒全表枚举太浪费。</summary>
        private const int SlowEverySeconds = 5;
        /// <summary>内存检查间隔,单位秒。</summary>
        private const int MemCheckSeconds = 60;
        /// <summary>可用内存低于总内存的这个百分比就整理一次。</summary>
        private const ulong MemLowPercent = 20;

        /// <summary>系统关键进程:即使被切到前台也不碰,避免影响登录、桌面与安全组件。</summary>
        private static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "registry", "memcompression", "memory compression",
            "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsm",
            "svchost", "dwm", "explorer", "fontdrvhost", "ctfmon", "sihost",
            "taskhostw", "runtimebroker", "searchhost", "searchindexer", "searchapp",
            "startmenuexperiencehost", "shellexperiencehost", "textinputhost",
            "audiodg", "conhost", "dllhost", "spoolsv",
            "securityhealthservice", "securityhealthsystray", "msmpeng", "nissrv",
            "tiworker", "trustedinstaller", "wuauclt", "usoclient", "wmiprvse",
            "sppsvc", "logonui", "userinit", "smartscreen", "taskmgr"
        };

        /// <summary>常用程序:即使没在前台,也把同名进程提一档,后台同步/消息拉取更跟手。</summary>
        private static readonly string[] CommonNames =
        {
            "chrome", "msedge", "firefox", "brave", "opera", "360chrome", "qqbrowser",
            "QQ", "WeChat", "Weixin", "Telegram", "Discord"
        };

        /// <summary>被本模式改过优先级的进程及改动前的档位。</summary>
        private sealed class SavedPriority
        {
            public int Pid;
            public string Name = "";
            public uint Priority;
        }

        private static readonly int SelfPid = Process.GetCurrentProcess().Id;

        private readonly object gate = new object();
        private readonly Dictionary<int, SavedPriority> saved = new Dictionary<int, SavedPriority>();

        private Timer timer;
        private bool active;
        private int ticking;
        private bool timerPeriod;
        private string prevScheme = "";
        private string currentName = "";
        private int currentCount;
        private int lastPid;
        private string lastBoostName = "";
        private DateTime nextSlow = DateTime.MinValue;
        private DateTime lastMemTrim = DateTime.MinValue;
        private DateTime nextMemCheck = DateTime.MinValue;

        /// <summary>是否处于实时加速状态。</summary>
        public bool Active
        {
            get { lock (gate) { return active; } }
        }

        /// <summary>最近一次加速的前台进程名(带 .exe);还没轮到可加速的程序时为空。</summary>
        public string CurrentName
        {
            get { lock (gate) { return currentName; } }
        }

        /// <summary>当前应用被一起提升的同名进程数(含前台进程本身)。</summary>
        public int CurrentCount
        {
            get { lock (gate) { return currentCount; } }
        }

        /// <summary>上次因内存偏低而清理系统待命内存的时间;从未清理过为 MinValue。</summary>
        public DateTime LastMemTrim
        {
            get { lock (gate) { return lastMemTrim; } }
        }

        public void Start()
        {
            lock (gate)
            {
                if (active) return;
                active = true;
                currentName = "";
                currentCount = 0;
                lastPid = 0;
                lastBoostName = "";
                nextSlow = DateTime.UtcNow;
                lastMemTrim = DateTime.MinValue;
                nextMemCheck = DateTime.UtcNow.AddSeconds(MemCheckSeconds);
            }

            // 先记住原来的电源计划再切高性能:切失败也只是没有这一项,循环照常跑。
            try
            {
                string cur = BoostEngine.ActiveSchemeGuid();
                if (cur.Length > 0)
                {
                    lock (gate)
                    {
                        if (!active) return;
                        prevScheme = cur;
                    }
                    BoostEngine.SwitchToHighPerformance();
                }
            }
            catch (Exception ex) { Log.Write("global boost power: " + ex.Message); }

            try
            {
                if (Native.timeBeginPeriod(1) == 0)
                {
                    lock (gate)
                    {
                        if (active) timerPeriod = true;
                        else Native.timeEndPeriod(1);
                    }
                }
            }
            catch (Exception ex) { Log.Write("global boost timer: " + ex.Message); }

            lock (gate)
            {
                if (!active) return;
                timer = new Timer(OnTick, null, 0, TickMs);
            }
            Log.Write("global boost: started");
        }

        public void Stop()
        {
            Timer t = null;
            bool period;
            string scheme;
            lock (gate)
            {
                if (!active) return;
                active = false;
                t = timer;
                timer = null;
                period = timerPeriod;
                timerPeriod = false;
                scheme = prevScheme;
                prevScheme = "";
                currentName = "";
                currentCount = 0;
                lastPid = 0;
                lastBoostName = "";
                nextSlow = DateTime.MinValue;
                nextMemCheck = DateTime.MinValue;
            }

            if (t != null)
            {
                try { t.Dispose(); } catch { }
            }

            // 等正在跑的一轮守护循环收尾,避免它刚记下的 PID 抢在还原之后又被提升。
            // 循环里可能正赶上内存整理,给到 3 秒;正常一轮只需几十毫秒。
            for (int i = 0; i < 300 && Interlocked.CompareExchange(ref ticking, 0, 0) != 0; i++)
                Thread.Sleep(10);

            RestorePriorities();

            if (period)
            {
                try { Native.timeEndPeriod(1); }
                catch (Exception ex) { Log.Write("global boost timer end: " + ex.Message); }
            }

            if (scheme.Length > 0)
            {
                try { BoostEngine.SetScheme(scheme); }
                catch (Exception ex) { Log.Write("global boost power restore: " + ex.Message); }
            }
            Log.Write("global boost: stopped");
        }

        public void Dispose()
        {
            Stop();
        }

        // ---------------- 守护循环 ----------------

        private void OnTick(object state)
        {
            if (Interlocked.Exchange(ref ticking, 1) == 1) return;
            try
            {
                if (!Active) return;
                TickOnce();
            }
            catch (Exception ex) { Log.Write("global boost tick: " + ex.Message); }
            finally { Interlocked.Exchange(ref ticking, 0); }
        }

        private void TickOnce()
        {
            IntPtr fg = Native.GetForegroundWindow();
            int pid = 0;
            if (fg != IntPtr.Zero) Native.GetWindowThreadProcessId(fg, out pid);

            // 前台窗口每秒看一次就够;真正重的进程枚举(常用程序、同名进程)每 5 秒一次。
            bool slow = false;
            lock (gate)
            {
                if (!active) return;
                if (DateTime.UtcNow >= nextSlow)
                {
                    nextSlow = DateTime.UtcNow.AddSeconds(SlowEverySeconds);
                    slow = true;
                }
            }

            if (pid > 0 && pid != SelfPid && !SkipPid(pid))
            {
                string name = Native.ProcessName(pid);
                if (name.Length > 0 && !SkipNames.Contains(name) && !name.StartsWith("frameforge", StringComparison.OrdinalIgnoreCase))
                    BoostForeground(pid, name, slow);
            }

            if (slow) BoostCommon();
            PurgeDeadIfNeeded();
            CheckMemory();
        }

        /// <summary>常用浏览器/聊天软件即使不在前台,也把它们的同名进程提到高于正常。</summary>
        private void BoostCommon()
        {
            for (int i = 0; i < CommonNames.Length; i++)
            {
                Process[] list = null;
                try { list = Process.GetProcessesByName(CommonNames[i]); }
                catch (Exception ex) { Log.Write("global boost common: " + ex.Message); }
                if (list == null) continue;
                for (int j = 0; j < list.Length; j++)
                {
                    Process p = list[j];
                    try
                    {
                        int id = p.Id;
                        if (id == SelfPid || p.SessionId == 0) continue;
                        if (IsSaved(id)) continue;
                        uint prev;
                        if (SetPrioritySmart(id, Native.ABOVE_NORMAL_PRIORITY_CLASS, out prev) == 1)
                            Record(id, CommonNames[i], prev);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
        }

        /// <summary>
        /// 前台进程提为高,它的同名进程(浏览器/QQ/微信的子进程)提到高于正常。
        /// 前台没变且还没到慢速刷新时直接返回,避免每秒重复枚举整张进程表。
        /// </summary>
        private void BoostForeground(int pid, string name, bool slow)
        {
            bool sameFront;
            lock (gate)
            {
                sameFront = pid == lastPid && string.Equals(lastBoostName, name, StringComparison.OrdinalIgnoreCase);
                lastPid = pid;
                lastBoostName = name;
            }
            if (sameFront && !slow) return;

            if (!IsSaved(pid))
            {
                uint prev;
                if (SetPrioritySmart(pid, Native.HIGH_PRIORITY_CLASS, out prev) == 1) Record(pid, name, prev);
            }

            int count = 1;
            Process[] same = null;
            try { same = Process.GetProcessesByName(name); }
            catch (Exception ex) { Log.Write("global boost enum: " + ex.Message); }

            if (same != null)
            {
                for (int i = 0; i < same.Length; i++)
                {
                    Process p = same[i];
                    try
                    {
                        int id = p.Id;
                        if (id == pid || id == SelfPid) continue;
                        if (p.SessionId == 0) continue;
                        count++;
                        if (IsSaved(id)) continue;
                        uint prev;
                        if (SetPrioritySmart(id, Native.ABOVE_NORMAL_PRIORITY_CLASS, out prev) == 1) Record(id, name, prev);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }

            lock (gate)
            {
                if (!active) return;
                currentName = name + ".exe";
                currentCount = count;
            }
        }

        /// <summary>只提升,不降级:已经是高优先级的同名进程不会被拉回高于正常。</summary>
        /// <returns>0 = 跳过/失败;1 = 已修改(prev 有效);2 = 本来就不低于目标档位。</returns>
        private static int SetPrioritySmart(int pid, uint want, out uint prev)
        {
            prev = 0;
            IntPtr h = Native.OpenProcess(
                Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                uint cur = Native.GetPriorityClass(h);
                if (cur == 0) return 0;
                if (cur == want) return 2;
                if (cur == Native.HIGH_PRIORITY_CLASS && want != Native.HIGH_PRIORITY_CLASS) return 2;
                if (cur == Native.REALTIME_PRIORITY_CLASS) return 2;
                if (!Native.SetPriorityClass(h, want)) return 0;
                prev = cur;
                return 1;
            }
            catch { return 0; }
            finally { Native.CloseHandle(h); }
        }

        private void Record(int pid, string name, uint priority)
        {
            lock (gate)
            {
                if (!active) return;
                if (saved.ContainsKey(pid)) return;
                var item = new SavedPriority();
                item.Pid = pid;
                item.Name = name;
                item.Priority = priority;
                saved[pid] = item;
            }
        }

        private bool IsSaved(int pid)
        {
            lock (gate) { return saved.ContainsKey(pid); }
        }

        private static bool SkipPid(int pid)
        {
            // 0/4 是 Idle 与 System;SessionId==0 是服务会话,只动当前登录用户的普通进程。
            if (pid <= 4) return true;
            try
            {
                using (Process p = Process.GetProcessById(pid)) return p.SessionId == 0;
            }
            catch { return true; }
        }

        private void RestorePriorities()
        {
            SavedPriority[] items;
            lock (gate)
            {
                items = new SavedPriority[saved.Count];
                saved.Values.CopyTo(items, 0);
                saved.Clear();
            }

            for (int i = 0; i < items.Length; i++)
            {
                SavedPriority s = items[i];
                IntPtr h = IntPtr.Zero;
                try
                {
                    // 进程已退出或者 PID 已被复用,直接跳过,不能改到别的程序。
                    if (!string.Equals(Native.ProcessName(s.Pid), s.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_INFORMATION, false, s.Pid);
                    if (h == IntPtr.Zero) continue;
                    uint cur = Native.GetPriorityClass(h);
                    if (cur != Native.HIGH_PRIORITY_CLASS && cur != Native.ABOVE_NORMAL_PRIORITY_CLASS) continue;
                    Native.SetPriorityClass(h, s.Priority);
                }
                catch { }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
            }
        }

        /// <summary>长时间运行后清掉已退出进程的记录,免得字典越攒越大。</summary>
        private void PurgeDeadIfNeeded()
        {
            int n;
            lock (gate) { n = saved.Count; }
            if (n < 64) return;

            var dead = new List<int>();
            lock (gate)
            {
                foreach (KeyValuePair<int, SavedPriority> kv in saved)
                {
                    if (!Alive(kv.Key, kv.Value.Name)) dead.Add(kv.Key);
                }
                for (int i = 0; i < dead.Count; i++) saved.Remove(dead[i]);
            }
        }

        private static bool Alive(int pid, string name)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                    return string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ---------------- 内存 ----------------

        private void CheckMemory()
        {
            bool due;
            lock (gate)
            {
                if (!active) return;
                due = DateTime.UtcNow >= nextMemCheck;
                if (due) nextMemCheck = DateTime.UtcNow.AddSeconds(MemCheckSeconds);
            }
            if (!due) return;

            try
            {
                var mem = new Native.MEMORYSTATUSEX();
                mem.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
                if (!Native.GlobalMemoryStatusEx(ref mem)) return;
                if (mem.ullTotalPhys == 0) return;
                if (mem.ullAvailPhys * 100 >= mem.ullTotalPhys * MemLowPercent) return;

                // 只清文件缓存与待命内存,不动各进程的工作集:
                // 实时加速若把浏览器/微信的工作集也清掉,下次打开反而要重新读盘。
                MemEngine.SmartClean(null);
                lock (gate)
                {
                    if (active) lastMemTrim = DateTime.Now;
                }
                Log.Write("global boost: trimmed standby memory");
            }
            catch (Exception ex) { Log.Write("global boost mem: " + ex.Message); }
        }
    }
}