using System;
using System.Globalization;

namespace FrameForge.Sys
{
    // boost-core §A3 / system_design.md §5：双引擎共享全局资源的单一仲裁者。
    // 不变量 I1：同一 ResourceKind 同一时刻最多一个 holder。
    // 不变量 I2：只有 holder 保存 old 值；非 holder 只递增引用计数，绝不覆盖 old。
    // 不变量 I3：refCount 降到 0 时才写回 old；写回前做幂等读回（由 restore 委托实现）。
    // 不变量 I4：计时器跨进程（isSameProcess=false）恢复一律跳过 timeEndPeriod（见 BoostJournal.Reconcile）。

    /// <summary>与 journal owner / resourceOwnership 同源；BOTH 仅用于顶层 tx.owner。</summary>
    public enum OwnerKind
    {
        NONE = 0,
        ONE_SHOT = 1,
        REALTIME = 2,
        BOTH = 3
    }

    /// <summary>受仲裁的两种系统级全局资源。</summary>
    public enum ResourceKind
    {
        PowerScheme = 0,
        TimerResolution = 1
    }

    public enum AcquireState
    {
        GRANTED = 0,   // 本引擎成为持有者并记 old
        SHARED = 1,    // 叠加用户，不记 old
        DENIED = 2
    }

    public class AcquireResult
    {
        public bool Ok;
        public AcquireState State = AcquireState.DENIED;
        /// <summary>首次 GRANTED 时带回原值；SHARED 时带回当前 holder 记下的原值（只读，勿覆盖）。</summary>
        public string Old = "";
        public string Reason = "";
    }

    /// <summary>落进 journal v2 的 resourceOwnership 快照。</summary>
    public class ResourceOwnership
    {
        public OwnerKind Power = OwnerKind.NONE;
        public OwnerKind Timer = OwnerKind.NONE;
    }

    /// <summary>引擎身份：BoostEngine=ONE_SHOT，GlobalBoost=REALTIME。</summary>
    public interface ISystemResourceOwner
    {
        OwnerKind Kind();
        bool Needs(ResourceKind r);
        void OnGranted(ResourceKind r, uint refCount);
        void OnShared(ResourceKind r, uint refCount);
        void OnReleased(ResourceKind r);
    }

    /// <summary>
    /// 进程内单例，零依赖（静态字段 + lock）。所有电源/计时器改动必须经此总闸。
    /// </summary>
    public sealed class SystemResourceArbiter
    {
        private static readonly SystemResourceArbiter InstanceField = new SystemResourceArbiter();
        public static SystemResourceArbiter Instance { get { return InstanceField; } }

        private readonly object gate = new object();

        private OwnerKind powerHolder = OwnerKind.NONE;
        private uint powerRef;
        private uint powerRefOneShot;
        private uint powerRefRealtime;
        private string powerOld = "";

        private OwnerKind timerHolder = OwnerKind.NONE;
        private uint timerRef;
        private uint timerRefOneShot;
        private uint timerRefRealtime;
        private string timerOld = "";

        // A-9：内存整理共享节流。两个引擎都经这里登记，避免反复挤压游戏工作集。
        private static readonly object MemGate = new object();
        private static DateTime lastMemoryTrimUtc = DateTime.MinValue;
        private const double MemoryTrimThrottleSeconds = 30.0;

        private SystemResourceArbiter() { }

        /// <summary>
        /// 首次：readOld() → apply() → holder=owner.Kind → refCount=1 → GRANTED。
        /// 已有：refCount++ → SHARED，不再调用 readOld/apply，避免覆盖原值。
        /// 同一 owner 重复 Acquire：按 owner 去重，只记一次，返回 SHARED。
        /// </summary>
        public AcquireResult Acquire(ISystemResourceOwner owner, ResourceKind r,
            Func<string> readOld, Func<bool> apply)
        {
            var res = new AcquireResult();
            if (owner == null) { res.Reason = "noowner"; return res; }
            OwnerKind kind = owner.Kind();
            if (kind == OwnerKind.NONE || kind == OwnerKind.BOTH) { res.Reason = "badowner"; return res; }

            lock (gate)
            {
                if (r == ResourceKind.PowerScheme)
                {
                    if (RefOf(kind, powerRefOneShot, powerRefRealtime) > 0)
                    {
                        // 同一 owner 已登记，幂等返回，不重复 apply。
                        res.Ok = true;
                        res.State = powerHolder == kind ? AcquireState.GRANTED : AcquireState.SHARED;
                        res.Old = powerOld;
                        res.Reason = "dedup";
                        return res;
                    }
                    if (powerHolder == OwnerKind.NONE)
                    {
                        string oldValue = "";
                        try { oldValue = readOld == null ? "" : (readOld() ?? ""); }
                        catch (Exception ex) { Log.Write("arbiter readOld power: " + ex.Message); }
                        bool applied = false;
                        try { applied = apply != null && apply(); }
                        catch (Exception ex) { Log.Write("arbiter apply power: " + ex.Message); }
                        if (!applied) { res.Reason = "apply-failed"; return res; }

                        powerHolder = kind;
                        powerOld = oldValue;
                        powerRef = 1;
                        SetRef(kind, 1, ref powerRefOneShot, ref powerRefRealtime);
                        NotifyGranted(owner, r, powerRef);
                        res.Ok = true;
                        res.State = AcquireState.GRANTED;
                        res.Old = oldValue;
                        return res;
                    }
                    // 对方已持有：叠加，不改系统、不覆盖 old。
                    powerRef++;
                    SetRef(kind, 1, ref powerRefOneShot, ref powerRefRealtime);
                    NotifyShared(owner, r, powerRef);
                    res.Ok = true;
                    res.State = AcquireState.SHARED;
                    res.Old = powerOld;
                    return res;
                }

                if (r == ResourceKind.TimerResolution)
                {
                    if (RefOf(kind, timerRefOneShot, timerRefRealtime) > 0)
                    {
                        res.Ok = true;
                        res.State = timerHolder == kind ? AcquireState.GRANTED : AcquireState.SHARED;
                        res.Old = timerOld;
                        res.Reason = "dedup";
                        return res;
                    }
                    if (timerHolder == OwnerKind.NONE)
                    {
                        string oldValue = "";
                        try { oldValue = readOld == null ? "" : (readOld() ?? ""); }
                        catch (Exception ex) { Log.Write("arbiter readOld timer: " + ex.Message); }
                        bool applied = false;
                        try { applied = apply != null && apply(); }
                        catch (Exception ex) { Log.Write("arbiter apply timer: " + ex.Message); }
                        if (!applied) { res.Reason = "apply-failed"; return res; }

                        timerHolder = kind;
                        timerOld = oldValue;
                        timerRef = 1;
                        SetRef(kind, 1, ref timerRefOneShot, ref timerRefRealtime);
                        NotifyGranted(owner, r, timerRef);
                        res.Ok = true;
                        res.State = AcquireState.GRANTED;
                        res.Old = oldValue;
                        return res;
                    }
                    timerRef++;
                    SetRef(kind, 1, ref timerRefOneShot, ref timerRefRealtime);
                    NotifyShared(owner, r, timerRef);
                    res.Ok = true;
                    res.State = AcquireState.SHARED;
                    res.Old = timerOld;
                    return res;
                }

                res.Reason = "badresource";
                return res;
            }
        }

        /// <summary>
        /// refCount--；仅当 ==0 且 owner==holder 时执行幂等读回后的 restore(old)；清 holder。
        /// </summary>
        public void Release(ISystemResourceOwner owner, ResourceKind r, Func<string, bool> restore)
        {
            if (owner == null) return;
            OwnerKind kind = owner.Kind();
            lock (gate)
            {
                if (r == ResourceKind.PowerScheme)
                {
                    if (RefOf(kind, powerRefOneShot, powerRefRealtime) == 0) return;
                    SetRef(kind, 0, ref powerRefOneShot, ref powerRefRealtime);
                    if (powerRef > 0) powerRef--;
                    // holder 先走但还有叠加用户：恢复责任转移给仍持有者，orig old 不变。
                    if (powerRef > 0 && powerHolder == kind)
                        powerHolder = powerRefOneShot > 0 ? OwnerKind.ONE_SHOT
                            : (powerRefRealtime > 0 ? OwnerKind.REALTIME : OwnerKind.NONE);
                    bool last = powerRef == 0;
                    string oldValue = powerOld;
                    if (last)
                    {
                        // 只有 holder 自己释放最后一份引用时才写回 old；叠加用户不覆盖原值。
                        if (powerHolder == kind && restore != null && oldValue.Length > 0)
                        {
                            try { restore(oldValue); }
                            catch (Exception ex) { Log.Write("arbiter restore power: " + ex.Message); }
                        }
                        powerHolder = OwnerKind.NONE;
                        powerOld = "";
                    }
                    NotifyReleased(owner, r);
                    return;
                }

                if (r == ResourceKind.TimerResolution)
                {
                    if (RefOf(kind, timerRefOneShot, timerRefRealtime) == 0) return;
                    SetRef(kind, 0, ref timerRefOneShot, ref timerRefRealtime);
                    if (timerRef > 0) timerRef--;
                    if (timerRef > 0 && timerHolder == kind)
                        timerHolder = timerRefOneShot > 0 ? OwnerKind.ONE_SHOT
                            : (timerRefRealtime > 0 ? OwnerKind.REALTIME : OwnerKind.NONE);
                    bool last = timerRef == 0;
                    string oldValue = timerOld;
                    if (last)
                    {
                        // 同进程 Release 且自己是 holder 才调用；跨进程恢复由 Reconcile 一律跳过（I4）。
                        if (timerHolder == kind && restore != null && oldValue.Length > 0)
                        {
                            try { restore(oldValue); }
                            catch (Exception ex) { Log.Write("arbiter restore timer: " + ex.Message); }
                        }
                        timerHolder = OwnerKind.NONE;
                        timerOld = "";
                    }
                    NotifyReleased(owner, r);
                }
            }
        }

        public OwnerKind CurrentHolder(ResourceKind r)
        {
            lock (gate) { return r == ResourceKind.PowerScheme ? powerHolder : timerHolder; }
        }

        public uint RefCount(ResourceKind r)
        {
            lock (gate) { return r == ResourceKind.PowerScheme ? powerRef : timerRef; }
        }

        /// <summary>共享资源时读取最初 old，供另一引擎在 Acquire 前先写 PENDING。</summary>
        public string CurrentOld(ResourceKind r)
        {
            lock (gate) { return r == ResourceKind.PowerScheme ? powerOld : timerOld; }
        }

        /// <summary>供 journal v2 落盘 resourceOwnership。</summary>
        public ResourceOwnership Snapshot()
        {
            lock (gate)
            {
                var o = new ResourceOwnership();
                o.Power = powerHolder;
                o.Timer = timerHolder;
                return o;
            }
        }

        /// <summary>
        /// 崩溃恢复入口：按 tx.resourceOwnership 判定谁该还原，统一走 BoostJournal.Reconcile。
        /// 新进程内仲裁状态必然为空，此处仅做委托，不重复实现还原算法。
        /// </summary>
        public void ReconcileFromJournal(Transaction tx, ReconcileContext ctx)
        {
            if (tx == null) return;
            try
            {
                BoostJournal.Reconcile(tx, ctx);
            }
            catch (Exception ex) { Log.Write("arbiter reconcile: " + ex.Message); }
        }

        // ---------------- 内存整理共享节流（A-9） ----------------

        /// <summary>距上次任意引擎整理不足阈值时返回 false，调用方应放弃本次整理。</summary>
        public static bool TryNoteMemoryTrim()
        {
            lock (MemGate)
            {
                DateTime now = DateTime.UtcNow;
                if (lastMemoryTrimUtc != DateTime.MinValue
                    && (now - lastMemoryTrimUtc).TotalSeconds < MemoryTrimThrottleSeconds) return false;
                lastMemoryTrimUtc = now;
                return true;
            }
        }

        public static DateTime LastMemoryTrimUtc
        {
            get { lock (MemGate) { return lastMemoryTrimUtc; } }
        }

        // ---------------- 内部辅助 ----------------

        private static uint RefOf(OwnerKind kind, uint oneShotRef, uint realtimeRef)
        {
            return kind == OwnerKind.ONE_SHOT ? oneShotRef : (kind == OwnerKind.REALTIME ? realtimeRef : 0u);
        }

        private static void SetRef(OwnerKind kind, uint value, ref uint oneShotRef, ref uint realtimeRef)
        {
            if (kind == OwnerKind.ONE_SHOT) oneShotRef = value;
            else if (kind == OwnerKind.REALTIME) realtimeRef = value;
        }

        private static void NotifyGranted(ISystemResourceOwner owner, ResourceKind r, uint refCount)
        {
            try { owner.OnGranted(r, refCount); } catch { }
        }

        private static void NotifyShared(ISystemResourceOwner owner, ResourceKind r, uint refCount)
        {
            try { owner.OnShared(r, refCount); } catch { }
        }

        private static void NotifyReleased(ISystemResourceOwner owner, ResourceKind r)
        {
            try { owner.OnReleased(r); } catch { }
        }
    }
}
