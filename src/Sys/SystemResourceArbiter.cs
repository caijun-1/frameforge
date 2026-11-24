using System;
using System.Globalization;
using System.Threading;

namespace FrameForge.Sys
{
    // boost-core §A3 / system_design.md §5：双引擎共享全局资源的单一仲裁者。
    // 不变量 I1：同一 ResourceKind 同一时刻最多一个 holder。
    // 不变量 I2：只有 holder 保存 old 值；非 holder 只递增引用计数，绝不覆盖 old。
    // 不变量 I3：refCount 降到 0 时才写回 old；写回前做幂等读回（由 restore 委托实现）。
    // 不变量 I4：计时器跨进程（isSameProcess=false）恢复一律跳过 timeEndPeriod（见 BoostJournal.Reconcile）。
    // 不变量 I5（FF-010）：gate 内只做状态转移；readOld/apply/restore 与 owner 回调一律在锁外执行。
    //   两个引擎的锁序因此永远单向：GlobalBoost.gate → Arbiter.gate，不再出现 AB-BA 互等。

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

        /// <summary>一种受仲裁资源的状态。Busy 表示锁外正在做阻塞 I/O，其它线程需等待。</summary>
        private sealed class Slot
        {
            public OwnerKind Holder = OwnerKind.NONE;
            public uint Ref;
            public uint OneShot;
            public uint Realtime;
            public string Old = "";
            public ISystemResourceOwner OneShotOwner;
            public ISystemResourceOwner RealtimeOwner;
            public bool Busy;
        }

        private readonly Slot power = new Slot();
        private readonly Slot timer = new Slot();

        // A-9：内存整理共享节流。两个引擎都经这里登记，避免反复挤压游戏工作集。
        private static readonly object MemGate = new object();
        private static DateTime lastMemoryTrimUtc = DateTime.MinValue;
        private const double MemoryTrimThrottleSeconds = 30.0;

        private SystemResourceArbiter() { }

        /// <summary>
        /// 首次：锁外 readOld() → apply()，再回到锁内登记 holder/refCount → GRANTED。
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

            Slot slot = SlotOf(r);
            if (slot == null) { res.Reason = "badresource"; return res; }

            bool becomeHolder = false;
            uint sharedRef = 0;

            lock (gate)
            {
                // 另一个线程正在锁外执行 powercfg / timeBeginPeriod，等它收尾。
                while (slot.Busy) System.Threading.Monitor.Wait(gate);

                if (RefOf(kind, slot) > 0)
                {
                    // 同一 owner 已登记，幂等返回，不重复 apply。
                    res.Ok = true;
                    res.State = slot.Holder == kind ? AcquireState.GRANTED : AcquireState.SHARED;
                    res.Old = slot.Old;
                    res.Reason = "dedup";
                    return res;
                }
                if (slot.Holder == OwnerKind.NONE)
                {
                    // 预约成功：先占住，避免两个线程同时做 apply。
                    slot.Busy = true;
                    becomeHolder = true;
                }
                else
                {
                    // 对方已持有：叠加，不改系统、不覆盖 old。
                    slot.Ref++;
                    SetRef(kind, 1, slot);
                    sharedRef = slot.Ref;
                    res.Ok = true;
                    res.State = AcquireState.SHARED;
                    res.Old = slot.Old;
                }
            }

            if (!becomeHolder)
            {
                // 锁外回调：GlobalBoost.OnShared 会拿自己的 gate，不能在 Arbiter.gate 内调用。
                NotifyShared(owner, r, sharedRef);
                return res;
            }

            string got = "";
            bool applied = false;
            try { got = readOld == null ? "" : (readOld() ?? ""); }
            catch (Exception ex) { Log.Write("arbiter readOld " + r + ": " + ex.Message); }
            try { applied = apply != null && apply(); }
            catch (Exception ex) { Log.Write("arbiter apply " + r + ": " + ex.Message); }

            lock (gate)
            {
                slot.Busy = false;
                if (applied)
                {
                    slot.Holder = kind;
                    slot.Old = got;
                    slot.Ref = 1;
                    SetRef(kind, 1, slot);
                    if (kind == OwnerKind.ONE_SHOT) slot.OneShotOwner = owner;
                    else slot.RealtimeOwner = owner;
                }
                System.Threading.Monitor.PulseAll(gate);
            }

            if (!applied) { res.Reason = "apply-failed"; return res; }

            res.Ok = true;
            res.State = AcquireState.GRANTED;
            res.Old = got;
            NotifyGranted(owner, r, 1);
            return res;
        }

        /// <summary>
        /// refCount--；仅当 ==0 且 owner==holder 时执行幂等读回后的 restore(old)；清 holder。
        /// 写回同样放在锁外，并用 Busy 挡住期间的 Acquire，避免旧值覆盖新方案。
        /// </summary>
        public void Release(ISystemResourceOwner owner, ResourceKind r, Func<string, bool> restore)
        {
            if (owner == null) return;
            OwnerKind kind = owner.Kind();
            Slot slot = SlotOf(r);
            if (slot == null) return;

            bool doRestore = false;
            bool transferred = false;
            string oldValue = "";
            ISystemResourceOwner nextOwner = null;
            uint nextRef = 0;

            lock (gate)
            {
                while (slot.Busy) System.Threading.Monitor.Wait(gate);
                if (RefOf(kind, slot) == 0) return;
                SetRef(kind, 0, slot);
                if (slot.Ref > 0) slot.Ref--;
                if (kind == OwnerKind.ONE_SHOT) slot.OneShotOwner = null;
                else slot.RealtimeOwner = null;

                if (slot.Ref > 0)
                {
                    // holder 先走但还有叠加用户：恢复责任转移给仍持有者，orig old 不变。
                    if (slot.Holder == kind)
                    {
                        slot.Holder = slot.OneShot > 0 ? OwnerKind.ONE_SHOT : OwnerKind.REALTIME;
                        nextOwner = slot.OneShot > 0 ? slot.OneShotOwner : slot.RealtimeOwner;
                        nextRef = slot.Ref;
                        transferred = true;
                    }
                }
                else
                {
                    // 只有 holder 自己释放最后一份引用时才写回 old；叠加用户不覆盖原值。
                    if (slot.Holder == kind && restore != null && slot.Old.Length > 0)
                    {
                        doRestore = true;
                        oldValue = slot.Old;
                        slot.Busy = true;
                    }
                    slot.Holder = OwnerKind.NONE;
                    slot.Old = "";
                }
            }

            if (doRestore)
            {
                try { restore(oldValue); }
                catch (Exception ex) { Log.Write("arbiter restore " + r + ": " + ex.Message); }
                lock (gate)
                {
                    slot.Busy = false;
                    System.Threading.Monitor.PulseAll(gate);
                }
            }

            // FF-011：holder 转移后，留存引擎要立刻把新的 resourceOwnership 落盘。
            if (transferred && nextOwner != null) NotifyGranted(nextOwner, r, nextRef);
            NotifyReleased(owner, r);
        }

        public OwnerKind CurrentHolder(ResourceKind r)
        {
            Slot slot = SlotOf(r);
            lock (gate) { return slot == null ? OwnerKind.NONE : slot.Holder; }
        }

        public uint RefCount(ResourceKind r)
        {
            Slot slot = SlotOf(r);
            lock (gate) { return slot == null ? 0u : slot.Ref; }
        }

        /// <summary>共享资源时读取最初 old，供另一引擎在 Acquire 前先写 PENDING。</summary>
        public string CurrentOld(ResourceKind r)
        {
            Slot slot = SlotOf(r);
            lock (gate) { return slot == null ? "" : slot.Old; }
        }

        /// <summary>供 journal v2 落盘 resourceOwnership。</summary>
        public ResourceOwnership Snapshot()
        {
            lock (gate)
            {
                var o = new ResourceOwnership();
                o.Power = power.Holder;
                o.Timer = timer.Holder;
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

        private Slot SlotOf(ResourceKind r)
        {
            if (r == ResourceKind.PowerScheme) return power;
            if (r == ResourceKind.TimerResolution) return timer;
            return null;
        }

        private static uint RefOf(OwnerKind kind, Slot slot)
        {
            return kind == OwnerKind.ONE_SHOT ? slot.OneShot : (kind == OwnerKind.REALTIME ? slot.Realtime : 0u);
        }

        private static void SetRef(OwnerKind kind, uint value, Slot slot)
        {
            if (kind == OwnerKind.ONE_SHOT) slot.OneShot = value;
            else if (kind == OwnerKind.REALTIME) slot.Realtime = value;
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
