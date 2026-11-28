using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace FrameForge.Sys
{
    /// <summary>
    /// 实时加速:常驻监视前台程序,把前台进程提到高优先级、同名子进程提到高于正常,
    /// 并叠加高性能电源计划与 1ms 计时器;关闭时逐项还原。
    /// </summary>
    public sealed class GlobalBoost : IDisposable, ISystemResourceOwner
    {
        private const int TickMs = 1000;
        /// <summary>进程枚举(常用程序、同名进程计数)的重活间隔,单位秒;每秒全表枚举太浪费。</summary>
        private const int SlowEverySeconds = 5;
        /// <summary>内存检查间隔,单位秒。</summary>
        private const int MemCheckSeconds = 60;
        /// <summary>待命内存清理冷却时间,单位秒。</summary>
        private const int MemPurgeCooldownSeconds = 90;
        /// <summary>可用内存低于总内存的这个百分比就整理一次(FreeBytes==0 时的回退阈值)。</summary>
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

        /// <summary>浏览器进程:绝不抢占游戏优先级,也不在后台提权。</summary>
        private static readonly HashSet<string> BrowserSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "opera", "360chrome", "qqbrowser",
            "vivaldi", "arc", "waterfox"
        };

        /// <summary>
        /// FF-026：提权失败的 PID 冷却表。旧实现每 5 秒对同一个受保护进程重复
        /// 「记 PENDING + 记 FAILED」两次落盘，SSD 与 CPU 都被白白消耗。
        /// </summary>
        private readonly Dictionary<int, DateTime> failedUntil = new Dictionary<int, DateTime>();
        private const int FailCooldownSeconds = 300;

        /// <summary>
        /// 被本模式改过优先级的进程及改动前的档位。
        /// A-5：StartTime + 完整路径双校验，避免 PID 复用同名时改到新进程。
        /// </summary>
        private sealed class SavedPriority
        {
            public int Pid;
            public string Name = "";
            public uint Priority;
            public long StartTime;
            public string Path = "";
            /// <summary>1 = 前台进程(HIGH)，2 = 同名/常用进程(ABOVE_NORMAL)。</summary>
            public int Role;
        }

        /// <summary>A-7：只读降级状态快照，供状态行显示"部分生效"。</summary>
        public class RealBoostState
        {
            public bool Active;
            public bool PowerEffective;
            public bool TimerEffective;
            public string CurrentName = "";
            public int CurrentCount;
            public DateTime LastMemTrim;
            public string DegradedReason = "";
        }

        private static readonly int SelfPid = Process.GetCurrentProcess().Id;

        private readonly object gate = new object();
        private readonly Dictionary<int, SavedPriority> saved = new Dictionary<int, SavedPriority>();
        private readonly Dictionary<int, SavedPriority> tamed = new Dictionary<int, SavedPriority>();
        private readonly Dictionary<int, long> prevCpuTicks = new Dictionary<int, long>();
        private DateTime prevCpuAtUtc = DateTime.MinValue;
        private DateTime lastMemTrimUtc = DateTime.MinValue;

        private Timer timer;
        private bool active;
        private int ticking;
        private bool timerPeriod;
        private string prevScheme = "";
        private bool powerAcquired;
        private bool timerAcquired;
        private bool powerEffective;
        private bool timerEffective;
        private string degradedReason = "";
        private int lastForegroundPid;
        private string currentName = "";
        private int currentCount;
        private int lastPid;
        private string lastBoostName = "";
        private Transaction realtimeTx;
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

        // ---- A-7：降级状态（新增只读快照，旧属性保持兼容，Pages.cs 无需改动） ----

        public bool PowerEffective { get { lock (gate) { return powerEffective; } } }
        public bool TimerEffective { get { lock (gate) { return timerEffective; } } }
        public string DegradedReason { get { lock (gate) { return degradedReason; } } }

        public RealBoostState State
        {
            get
            {
                lock (gate)
                {
                    var s = new RealBoostState();
                    s.Active = active;
                    s.PowerEffective = powerEffective;
                    s.TimerEffective = timerEffective;
                    s.CurrentName = currentName;
                    s.CurrentCount = currentCount;
                    s.LastMemTrim = lastMemTrim;
                    s.DegradedReason = degradedReason;
                    return s;
                }
            }
        }

        /// <summary>PagesStateHub 的统一只读入口（MainForm 工程师可选接线）。</summary>
        public RealBoostState SnapshotState() { return State; }

        // ---------------- ISystemResourceOwner（boost-core §A3.2） ----------------

        public OwnerKind Kind() { return OwnerKind.REALTIME; }
        public bool Needs(ResourceKind r) { return true; }
        public void OnGranted(ResourceKind r, uint refCount)
        {
            lock (gate)
            {
                if (r == ResourceKind.PowerScheme) powerEffective = true;
                else timerEffective = true;
                // P1-1：一键加速释放并将 holder 移交给实时加速时，立刻把新所有权快照落盘，
                // 避免后续强杀/崩溃对账时因 boost.realtime.journal 仍记录旧 holder 而跳过电源还原。
                SaveRealtimeTxLocked();
            }
            Log.Write("global boost arbiter granted " + r + " ref=" + refCount);
        }
        public void OnShared(ResourceKind r, uint refCount)
        {
            lock (gate)
            {
                if (r == ResourceKind.PowerScheme) powerEffective = true;
                else timerEffective = true;
            }
            Log.Write("global boost arbiter shared " + r + " ref=" + refCount);
        }
        public void OnReleased(ResourceKind r)
        {
            lock (gate)
            {
                if (r == ResourceKind.PowerScheme) powerEffective = false;
                else timerEffective = false;
            }
        }

        public void Start()
        {
            lock (gate) { if (active) return; }
            if (!BoostJournal.RecoverBeforeNew(OwnerKind.REALTIME, true))
            {
                degradedReason = "pending-recovery";
                return;
            }
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

            string powerOld = SystemResourceArbiter.Instance.CurrentHolder(ResourceKind.PowerScheme) != OwnerKind.NONE
                ? SystemResourceArbiter.Instance.CurrentOld(ResourceKind.PowerScheme)
                : BoostEngine.ActiveSchemeGuid();
            lock (gate) { EnsureRealtimeTransactionLocked(powerOld); }

            // A-8：电源改动进仲裁者的原子 Acquire，消除"记下 prevScheme 后、切换前"的竞态。
            try
            {
                string captured = BoostEngine.ActiveSchemeGuid();
                if (captured.Length == 0) AddDegraded("power-unreadable");
                else
                {
                    AcquireResult ar = SystemResourceArbiter.Instance.Acquire(this, ResourceKind.PowerScheme,
                        delegate { return captured; },
                        delegate { return BoostEngine.SwitchToHighPerformance(); });
                    if (!ar.Ok) AddDegraded("power-denied");
                    else
                    {
                        string oldScheme = ar.Old;
                        powerOld = oldScheme;
                        MarkRealtimeResourceApplied(ResourceKind.PowerScheme, oldScheme);
                        bool granted = ar.State == AcquireState.GRANTED;
                        bool keep;
                        lock (gate)
                        {
                            keep = active;
                            if (keep)
                            {
                                powerAcquired = true;
                                if (granted) prevScheme = oldScheme;
                            }
                        }
                        // Stop 正好在 Acquire 期间发生：立刻对称释放，不留残留。
                        if (!keep)
                            SystemResourceArbiter.Instance.Release(this, ResourceKind.PowerScheme,
                                DelegatePowerRestore);
                    }
                }
            }
            catch (Exception ex) { Log.Write("global boost power: " + ex.Message); AddDegraded("power-denied"); }

            try
            {
                AcquireResult ar = SystemResourceArbiter.Instance.Acquire(this, ResourceKind.TimerResolution,
                    delegate { return "1"; },
                    delegate { return BoostEngine.AcquireHighResTimer(); });
                if (!ar.Ok) AddDegraded("timer-denied");
                else
                {
                    MarkRealtimeResourceApplied(ResourceKind.TimerResolution, ar.Old);
                    bool granted = ar.State == AcquireState.GRANTED;
                    bool keep;
                    lock (gate)
                    {
                        keep = active;
                        if (keep)
                        {
                            timerAcquired = true;
                            timerPeriod = granted;
                        }
                    }
                    if (!keep)
                        SystemResourceArbiter.Instance.Release(this, ResourceKind.TimerResolution,
                            DelegateTimerRestore);
                }
            }
            catch (Exception ex) { Log.Write("global boost timer: " + ex.Message); AddDegraded("timer-denied"); }

            bool startWatchdog = false;
            lock (gate)
            {
                if (!active) return;
                EnsureRealtimeTransactionLocked(powerOld);
                startWatchdog = realtimeTx != null && realtimeTx.Actions.Count > 0;
                timer = new Timer(OnTick, null, 0, TickMs);
            }
            if (startWatchdog) BoostEngine.EnsureWatchdog();
            Log.Write("global boost: started");
        }

        public void Stop()
        {
            Timer t = null;
            bool period, power;
            bool timerEnded = false;
            Transaction tx;
            lock (gate)
            {
                if (!active) return;
                active = false;
                t = timer;
                timer = null;
                period = timerAcquired;
                timerAcquired = false;
                timerPeriod = false;
                power = powerAcquired;
                powerAcquired = false;
                prevScheme = "";
                powerEffective = false;
                timerEffective = false;
                degradedReason = "";
                lastForegroundPid = 0;
                currentName = "";
                currentCount = 0;
                lastPid = 0;
                lastBoostName = "";
                nextSlow = DateTime.MinValue;
                nextMemCheck = DateTime.MinValue;
                tx = realtimeTx;
                realtimeTx = null;
                if (tx == null) tx = BoostJournal.Load(OwnerKind.REALTIME);
            }

            if (t != null)
            {
                try { t.Dispose(); } catch { }
            }

            // 提权与原值登记已在 gate 内成对完成；这里的短等待只让枚举尽快收尾，
            // 不再承担还原正确性，UI 关窗也不会被长时间阻塞。
            for (int i = 0; i < 30 && Interlocked.CompareExchange(ref ticking, 0, 0) != 0; i++)
                Thread.Sleep(10);

            RestorePriorities();

            if (period)
                SystemResourceArbiter.Instance.Release(this, ResourceKind.TimerResolution,
                    delegate(string old) { BoostEngine.ReleaseHighResTimer(); timerEnded = true; return true; });
            if (timerEnded && tx != null)
            {
                BoostJournal.MarkReverted(tx, ActionKind.TIMER_RESOLUTION);
                BoostJournal.Save(tx);
            }
            if (power)
                SystemResourceArbiter.Instance.Release(this, ResourceKind.PowerScheme, DelegatePowerRestore);

            if (tx != null)
            {
                var ctx = new ReconcileContext();
                ctx.IsSameProcess = true;
                ctx.IsAdmin = MemEngine.IsAdmin;
                ReconcileReport rep = BoostJournal.Reconcile(tx, ctx);
                Log.Write("global boost reconcile: reverted=" + rep.Reverted + " noop=" + rep.Noop
                    + " conflict=" + rep.Conflict + " failed=" + rep.Failed);
            }

            Log.Write("global boost: stopped");
        }

        private static bool DelegatePowerRestore(string old)
        {
            // I3 幂等读回：只有仍是高性能才写回，避免覆盖用户事后手动切换的方案。
            string cur = BoostEngine.ActiveSchemeGuid();
            if (string.Equals(cur, BoostEngine.SchemeHigh, StringComparison.OrdinalIgnoreCase))
                return BoostEngine.SetScheme(old);
            Log.Write("global boost power restore: current changed, skipped");
            return true;
        }

        private static bool DelegateTimerRestore(string old)
        {
            // 同进程 Release 才到这里；跨进程恢复由 BoostJournal.Reconcile 一律跳过。
            BoostEngine.ReleaseHighResTimer();
            return true;
        }

        private void EnsureRealtimeTransactionLocked(string powerOld)
        {
            if (realtimeTx != null) return;
            realtimeTx = BoostJournal.NewTransaction(OwnerKind.REALTIME, 0, "", Process.GetCurrentProcess().SessionId);
            realtimeTx.State = TxState.COMMITTED;
            JournalAction power = BoostJournal.NewAction(realtimeTx, ActionKind.POWER_SCHEME, null,
                powerOld, BoostEngine.SchemeHigh);
            power.Owner = OwnerKind.REALTIME;
            JournalAction timerAction = BoostJournal.NewAction(realtimeTx, ActionKind.TIMER_RESOLUTION, null, "1", "1");
            timerAction.Owner = OwnerKind.REALTIME;
            timerAction.RequiresLiveOwner = true;
            // 第一个系统写操作前就落 PENDING 并启动看门狗。
            SaveRealtimeTxLocked();
        }

        private void MarkRealtimeResourceApplied(ResourceKind resource, string oldValue)
        {
            lock (gate)
            {
                if (realtimeTx == null) return;
                ActionKind kind = resource == ResourceKind.PowerScheme ? ActionKind.POWER_SCHEME : ActionKind.TIMER_RESOLUTION;
                for (int i = 0; i < realtimeTx.Actions.Count; i++)
                {
                    JournalAction a = realtimeTx.Actions[i];
                    if (a.Kind != kind) continue;
                    a.Owner = OwnerKind.REALTIME;
                    if (!string.IsNullOrEmpty(oldValue)) a.Old = oldValue;
                    MarkRealtimeApplied(a);
                }
                SaveRealtimeTxLocked();
            }
        }

        private static void MarkRealtimeApplied(JournalAction a)
        {
            a.Status = ActionStatus.APPLIED_CONFIRMED;
            a.AppliedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            a.VerifiedUtc = a.AppliedUtc;
        }

        private void SaveRealtimeTxLocked()
        {
            if (realtimeTx == null || realtimeTx.Actions.Count == 0) return;
            PruneRealtimeActionsLocked();
            realtimeTx.Res = SystemResourceArbiter.Instance.Snapshot();
            BoostJournal.Save(realtimeTx);
            BoostEngine.EnsureWatchdog();
        }

        /// <summary>
        /// FF-026：常驻模式一开几小时，每个进过前台的进程都会留一条 action，
        /// 日志只增不减。超过上限时优先丢掉已经还原完的记录，保留仍在生效的。
        /// </summary>
        private void PruneRealtimeActionsLocked()
        {
            if (realtimeTx.Actions.Count <= 256) return;
            for (int i = realtimeTx.Actions.Count - 1; i >= 0 && realtimeTx.Actions.Count > 128; i--)
                if (realtimeTx.Actions[i].Status == ActionStatus.REVERTED)
                    realtimeTx.Actions.RemoveAt(i);
        }

        private JournalAction FindRealtimePriority(int pid, bool pendingOnly)
        {
            if (realtimeTx == null) return null;
            for (int i = realtimeTx.Actions.Count - 1; i >= 0; i--)
            {
                JournalAction a = realtimeTx.Actions[i];
                if (a.Kind != ActionKind.PRIORITY_CLASS || a.Target == null || a.Target.Pid != pid) continue;
                if (pendingOnly && a.Status != ActionStatus.PENDING && a.Status != ActionStatus.FAILED) continue;
                if (!pendingOnly && a.Status == ActionStatus.REVERTED) continue;
                return a;
            }
            return null;
        }

        /// <summary>在 SetPriorityClass 前落盘，崩溃后可按 current==old/new 对账。</summary>
        private void RecordPriorityPending(int pid, string name, uint want, uint old)
        {
            EnsureRealtimeTransactionLocked("");
            JournalAction a = FindRealtimePriority(pid, true);
            ProcessGuard guard = BoostJournal.MakeGuard(pid, name, -1);
            if (a == null)
                a = BoostJournal.NewAction(realtimeTx, ActionKind.PRIORITY_CLASS, guard,
                    old.ToString(CultureInfo.InvariantCulture), want.ToString(CultureInfo.InvariantCulture));
            a.Target = guard;
            a.Owner = OwnerKind.REALTIME;
            a.Old = old.ToString(CultureInfo.InvariantCulture);
            a.New = want.ToString(CultureInfo.InvariantCulture);
            a.NewClass = (int)want;
            a.Status = ActionStatus.PENDING;
            a.Note = "";
            SaveRealtimeTxLocked();
        }

        private void RecordPriorityApplied(int pid)
        {
            JournalAction a = FindRealtimePriority(pid, true);
            if (a == null) return;
            MarkRealtimeApplied(a);
            SaveRealtimeTxLocked();
        }

        private void RecordPriorityFailed(int pid, string note)
        {
            JournalAction a = FindRealtimePriority(pid, true);
            if (a == null) return;
            a.Status = ActionStatus.FAILED;
            a.Note = (note ?? "").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
            SaveRealtimeTxLocked();
        }

        private void MarkRealtimePriorityReverted(int pid)
        {
            JournalAction a = FindRealtimePriority(pid, false);
            if (a == null) return;
            a.Status = ActionStatus.REVERTED;
            a.RevertedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            SaveRealtimeTxLocked();
        }

        private void AddDegraded(string reason)
        {
            lock (gate)
            {
                if (degradedReason.Length == 0) degradedReason = reason;
                else if (degradedReason.IndexOf(reason, StringComparison.Ordinal) < 0)
                    degradedReason += "," + reason;
            }
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

            int activeGamePid = 0;
            bool gameActive = Monitor.FindFullscreenWindow(out activeGamePid) && activeGamePid > 0;

            // 前台窗口每秒看一次就够;真正重的进程枚举(后台 EcoQoS 降噪)每 5 秒一次。
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
                if (name.Length > 0
                    && !SkipNames.Contains(name)
                    && !name.StartsWith("frameforge", StringComparison.OrdinalIgnoreCase)
                    && !ProcessGuardList.IsAntiCheat(name)
                    && !BoostEngine.IsExcluded(name)
                    && !BrowserSet.Contains(name)
                    && (!gameActive || pid == activeGamePid))
                {
                    BoostForeground(pid, name, slow);
                }
            }

            if (slow) TameHighCpuBackground(activeGamePid, pid);
            PurgeDeadIfNeeded();
            CheckMemory();
        }

        /// <summary>
        /// 5 秒慢速轮询:识别非保护、非游戏的高 CPU 后台进程,施以 EcoQoS + BelowNormal(不冻结)。
        /// 绝不触碰反作弊、受保护后台进程及浏览器/游戏窗口。
        /// </summary>
        private void TameHighCpuBackground(int activeGamePid, int fgPid)
        {
            Process[] all = null;
            try { all = Process.GetProcesses(); }
            catch (Exception ex) { Log.Write("global boost tame: " + ex.Message); return; }

            DateTime now = DateTime.UtcNow;
            double elapsedMs = prevCpuAtUtc == DateTime.MinValue ? 0 : (now - prevCpuAtUtc).TotalMilliseconds;
            prevCpuAtUtc = now;
            var nextTicks = new Dictionary<int, long>();
            double thresholdTicks = elapsedMs > 500
                ? elapsedMs * 10000.0 * Math.Max(1, Environment.ProcessorCount) * 0.05
                : double.MaxValue;

            for (int i = 0; i < all.Length; i++)
            {
                Process p = all[i];
                try
                {
                    int id = p.Id;
                    if (id <= 4 || id == SelfPid || id == activeGamePid || id == fgPid) continue;
                    if (p.SessionId == 0 || p.MainWindowHandle != IntPtr.Zero) continue;
                    string name = p.ProcessName;
                    if (string.IsNullOrEmpty(name) || SkipNames.Contains(name)) continue;
                    if (ProcessGuardList.IsAntiCheat(name) || ProcessGuardList.IsProtectedBackground(name)) continue;
                    if (BoostEngine.IsPermanent(name) || BoostEngine.UserWhitelist.Contains(name)) continue;

                    long ticks = p.TotalProcessorTime.Ticks;
                    nextTicks[id] = ticks;

                    long prev;
                    if (elapsedMs <= 500 || !prevCpuTicks.TryGetValue(id, out prev)) continue;
                    if (ticks - prev < thresholdTicks) continue;

                    TameOneProcess(id, name, p);
                }
                catch { }
                finally { p.Dispose(); }
            }

            lock (gate)
            {
                prevCpuTicks.Clear();
                foreach (KeyValuePair<int, long> kv in nextTicks) prevCpuTicks[kv.Key] = kv.Value;
            }
        }

        private void TameOneProcess(int pid, string name, Process proc)
        {
            lock (gate)
            {
                if (!active || tamed.ContainsKey(pid) || saved.ContainsKey(pid)) return;
                DateTime until;
                if (failedUntil.TryGetValue(pid, out until) && DateTime.UtcNow < until) return;
            }

            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                lock (gate) { failedUntil[pid] = DateTime.UtcNow.AddSeconds(FailCooldownSeconds); }
                return;
            }
            try
            {
                uint cur = Native.GetPriorityClass(h);
                if (cur == 0) return;
                if (cur == Native.NORMAL_PRIORITY_CLASS || cur == Native.ABOVE_NORMAL_PRIORITY_CLASS)
                    Native.SetPriorityClass(h, Native.BELOW_NORMAL_PRIORITY_CLASS);
                BoostEngine.SetProcessEcoQoS(h, true);

                var item = new SavedPriority();
                item.Pid = pid;
                item.Name = name;
                item.Priority = cur;
                item.Role = 3;
                try { item.StartTime = proc.StartTime.Ticks; } catch { item.StartTime = 0; }
                try { item.Path = Native.ProcessPath(pid); } catch { item.Path = ""; }
                lock (gate)
                {
                    if (active && !tamed.ContainsKey(pid)) tamed[pid] = item;
                }
            }
            catch { }
            finally { Native.CloseHandle(h); }
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

            // A-4：旧前台进程回落原值，避免长时间开启后一批历史前台程序都留在 HIGH。
            DemotePreviousForeground(pid);
            lock (gate) { if (active) lastForegroundPid = pid; }

            BoostProcess(pid, name, Native.HIGH_PRIORITY_CLASS, 1);

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
                        BoostProcess(id, name, Native.ABOVE_NORMAL_PRIORITY_CLASS, 2);
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

        /// <summary>提权和原值登记在同一把锁内完成，Stop 不会夹在两者之间。</summary>
        private void BoostProcess(int pid, string name, uint want, int role)
        {
            lock (gate)
            {
                if (!active || saved.ContainsKey(pid)) return;
                DateTime until;
                if (failedUntil.TryGetValue(pid, out until) && DateTime.UtcNow < until) return;
                uint prev;
                if (SetPrioritySmart(pid, name, want, out prev) == 1) Record(pid, name, prev, role);
                else failedUntil[pid] = DateTime.UtcNow.AddSeconds(FailCooldownSeconds);
            }
        }

        /// <summary>只提升,不降级:已经是高优先级的同名进程不会被拉回高于正常。</summary>
        /// <returns>0 = 跳过/失败;1 = 已修改(prev 有效);2 = 本来就不低于目标档位。</returns>
        private int SetPrioritySmart(int pid, string name, uint want, out uint prev)
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
                RecordPriorityPending(pid, name, want, cur);
                if (!Native.SetPriorityClass(h, want))
                {
                    RecordPriorityFailed(pid, "set-priority-refused");
                    return 0;
                }
                uint after = Native.GetPriorityClass(h);
                if (after != want)
                {
                    RecordPriorityFailed(pid, "priority-readback-mismatch");
                    return 0;
                }
                prev = cur;
                return 1;
            }
            catch (Exception ex)
            {
                RecordPriorityFailed(pid, ex.Message);
                return 0;
            }
            finally { Native.CloseHandle(h); }
        }

        private void Record(int pid, string name, uint priority, int role)
        {
            lock (gate)
            {
                if (!active) return;
                if (saved.ContainsKey(pid)) return;
                var item = new SavedPriority();
                item.Pid = pid;
                item.Name = name;
                item.Priority = priority;
                item.Role = role;
                // A-5：StartTime + 完整路径双校验证据，在提权改档位之前抓取。
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        item.StartTime = p.StartTime.Ticks;
                        try { item.Path = Native.ProcessPath(p.Id); } catch { item.Path = ""; }
                    }
                }
                catch { item.StartTime = 0; item.Path = ""; }
                saved[pid] = item;
                RecordPriorityApplied(pid);
            }
        }

        /// <summary>A-4：前台切换后把上一个前台进程从 HIGH 回落原值（同名/常用进程不动）。</summary>
        private void DemotePreviousForeground(int newPid)
        {
            lock (gate)
            {
                int oldPid = lastForegroundPid;
                if (oldPid <= 0 || oldPid == newPid) return;

                SavedPriority target = null;
                if (saved.TryGetValue(oldPid, out target) && target.Role == 1)
                {
                    saved.Remove(oldPid);
                    failedUntil.Remove(oldPid);
                }
                else target = null;
                if (target == null) return;

                bool restored = false;
                IntPtr h = IntPtr.Zero;
                try
                {
                    if (!Matches(target)) restored = true; // 已退出或 PID 已复用，无需再写。
                    else
                    {
                        h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_INFORMATION,
                            false, target.Pid);
                        if (h != IntPtr.Zero)
                        {
                            uint cur = Native.GetPriorityClass(h);
                            if (cur == target.Priority) restored = true;
                            else if (cur == Native.HIGH_PRIORITY_CLASS || cur == Native.ABOVE_NORMAL_PRIORITY_CLASS)
                                restored = Native.SetPriorityClass(h, target.Priority);
                        }
                    }
                }
                catch { }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
                if (restored) MarkRealtimePriorityReverted(oldPid);
            }
        }

        /// <summary>A-5：名字 + StartTime + 完整路径三重校验（取不到的证据不参与判定）。</summary>
        private static bool Matches(SavedPriority s)
        {
            try
            {
                using (Process p = Process.GetProcessById(s.Pid))
                {
                    if (!string.Equals(p.ProcessName, s.Name, StringComparison.OrdinalIgnoreCase)) return false;
                    if (s.StartTime > 0 && p.StartTime.Ticks != s.StartTime) return false;
                    if (s.Path.Length > 0)
                    {
                        // 走 QueryFullProcessImageName,避免 MainModule 触发 PROCESS_VM_READ。
                        string actual = Native.ProcessPath(s.Pid);
                        if (actual.Length > 0 && !string.Equals(actual, s.Path, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    return true;
                }
            }
            catch { return false; }
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
            SavedPriority[] tamedItems;
            lock (gate)
            {
                items = new SavedPriority[saved.Count];
                saved.Values.CopyTo(items, 0);
                saved.Clear();
                tamedItems = new SavedPriority[tamed.Count];
                tamed.Values.CopyTo(tamedItems, 0);
                tamed.Clear();
                prevCpuTicks.Clear();
                prevCpuAtUtc = DateTime.MinValue;
            }

            for (int i = 0; i < items.Length; i++)
            {
                SavedPriority s = items[i];
                IntPtr h = IntPtr.Zero;
                try
                {
                    // 进程已退出或者 PID 已被复用（含同名重启），直接跳过。
                    if (!Matches(s)) continue;
                    h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_INFORMATION, false, s.Pid);
                    if (h == IntPtr.Zero) continue;
                    uint cur = Native.GetPriorityClass(h);
                    if (cur != Native.HIGH_PRIORITY_CLASS && cur != Native.ABOVE_NORMAL_PRIORITY_CLASS) continue;
                    Native.SetPriorityClass(h, s.Priority);
                }
                catch { }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
            }

            for (int i = 0; i < tamedItems.Length; i++)
            {
                SavedPriority s = tamedItems[i];
                IntPtr h = IntPtr.Zero;
                try
                {
                    if (!Matches(s)) continue;
                    h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, s.Pid);
                    if (h == IntPtr.Zero) continue;
                    BoostEngine.SetProcessEcoQoS(h, false);
                    uint cur = Native.GetPriorityClass(h);
                    if (cur == Native.BELOW_NORMAL_PRIORITY_CLASS && s.Priority != 0)
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
            // A-6：阈值 64 → 16，避免长期常驻时字典缓慢膨胀。
            if (n < 16) return;

            var dead = new List<int>();
            lock (gate)
            {
                foreach (KeyValuePair<int, SavedPriority> kv in saved)
                {
                    if (!Matches(kv.Value)) dead.Add(kv.Key);
                }
                for (int i = 0; i < dead.Count; i++)
                {
                    saved.Remove(dead[i]);
                    failedUntil.Remove(dead[i]);
                }
                // 冷却表本身也要收敛:超过阈值时顺手清掉已过期的 PID。
                if (failedUntil.Count > 64)
                {
                    var expired = new List<int>();
                    DateTime now = DateTime.UtcNow;
                    foreach (KeyValuePair<int, DateTime> kv in failedUntil)
                        if (kv.Value <= now) expired.Add(kv.Key);
                    for (int i = 0; i < expired.Count; i++) failedUntil.Remove(expired[i]);
                }
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
            bool inCooldown;
            lock (gate)
            {
                if (!active) return;
                DateTime now = DateTime.UtcNow;
                due = now >= nextMemCheck;
                if (due) nextMemCheck = now.AddSeconds(MemCheckSeconds);
                inCooldown = (now - lastMemTrimUtc).TotalSeconds < MemPurgeCooldownSeconds;
            }
            if (!due || inCooldown) return;

            try
            {
                MemSample sample = MemEngine.Sample();
                bool needPurge;
                if (sample.FreeBytes > 0)
                {
                    needPurge = sample.FreeBytes < 1024L * 1024 * 1024
                        && sample.StandbyBytes > 2048UL * 1024 * 1024;
                }
                else
                {
                    if (sample.TotalPhys == 0) return;
                    needPurge = sample.AvailPhys * 100 < sample.TotalPhys * MemLowPercent;
                }
                if (!needPurge) return;

                // 只在确实有全屏游戏时清理待命列表:桌面/办公场景下清空全系统 Standby
                // 会让浏览器、微信下次访问时重新读盘,反而更卡(报告 FF-009)。
                int gamePid;
                if (!Monitor.FindFullscreenWindow(out gamePid)) return;

                // A-9：与一键加速共享节流窗口，避免两项叠加反复挤压工作集造成抖动。
                if (!SystemResourceArbiter.TryNoteMemoryTrim()) return;
                // 只清待命列表,不刷新已修改列表也不动工作集。
                if (!MemEngine.PurgeStandbyOnly()) return;
                lock (gate)
                {
                    if (active)
                    {
                        lastMemTrimUtc = DateTime.UtcNow;
                        lastMemTrim = DateTime.Now;
                    }
                }
                Log.Write("global boost: purged standby memory");
            }
            catch (Exception ex) { Log.Write("global boost mem: " + ex.Message); }
        }
    }
}