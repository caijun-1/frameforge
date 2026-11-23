using System;
using System.Collections.Generic;

namespace FrameForge.Sys
{
    // SCTL v2 / boost-core §B3.2：核心调度策略层的纯契约定义。
    // 本文件只放类型与接口，不引用任何尚未由 UI 工程师新增的 Settings 字段。
    // 全局策略键由 CoreConfig 以 key=value 文本直接读取（见 CoreScheduler.cs）。

    /// <summary>核心调度策略。Auto 必须与 v1.4.7 现有行为逐字一致（零回归基线）。</summary>
    public enum CorePolicy
    {
        Auto = 0,
        PCoreFirst = 1,
        SingleCcd = 2
    }

    /// <summary>
    /// 拓扑只读快照。group0Only 诚实反映 Topology.Find/SetIdsForMask 只覆盖 0 号处理器组。
    /// </summary>
    public class CpuTopologySnapshot
    {
        public CpuCore[] Cores = new CpuCore[0];
        public int[] CcdByCore = new int[0];
        public int[] NumaByCore = new int[0];
        public bool IsHybrid;
        public bool Group0Only = true;
    }

    /// <summary>
    /// 被调度进程的遥测。StartTime 用于 PID 复用防护（与 GlobalBoost A-5 同源）。
    /// ProcessGuard.name 是 ProcessName（不含 .exe），仅用于身份校验，不是 profile 关联键。
    /// </summary>
    public class ProcessTelemetry
    {
        public int Pid = -1;
        public string Name = "";
        public long StartTime;
        public int MainTid = -1;
        public bool CpuBound = true;
        public bool AntiCheatDenied;
        /// <summary>本次加速的核心档位；0 表示不限制。</summary>
        public int Cores;
    }

    /// <summary>一次调度决策。gameMask == 0 表示不改亲和（Auto/PCoreFirst 走软引导）。</summary>
    public class CorePlan
    {
        public CorePolicy Policy = CorePolicy.Auto;
        /// <summary>本次加速的核心档位，由 ProcessTelemetry.Cores 传入。</summary>
        public int Cores;
        /// <summary>全部核心：保留 SingleCcd 软调度，但不写硬亲和。</summary>
        public bool SkipAffinity;
        public long GameMask;
        public int[] IdealCandidates = new int[0];
        /// <summary>Rebalance 选出的目标逻辑核；-1 表示尚未选定。</summary>
        public int IdealCpu = -1;
        public string Reason = "";
    }

    /// <summary>沿用 BoostResult / AffinityResult 的 Ok/Error/Reason 风格。</summary>
    public class CoreApplyResult
    {
        public bool Ok;
        public int Applied;
        public int Moved;
        public long Mask;
        public int Mode;
        public string Error = "";
        public string Reason = "";
    }

    /// <summary>供 PagesStateHub 展示的只读状态。</summary>
    public class CoreSchedulerState
    {
        public CorePolicy Policy = CorePolicy.Auto;
        public int IdealCpu = -1;
        public int UsedCores;
        public int Mode;   // 0 未生效 / 1 软亲和 / 2 硬亲和
        /// <summary>实际生效的亲和模式；只在 AffinityEngine 成功后发布。</summary>
        public int AffinityMode;
        /// <summary>实际生效的亲和掩码；未生效或已还原时为 0。</summary>
        public long AffinityMask;
        public string Reason = "";
    }

    /// <summary>
    /// 核心调度策略层。不新建执行器：gameMask != 0 时委托 AffinityEngine；
    /// Auto/PCoreFirst 只改候选过滤；cpuBound == false 返回空计划（诚实收益护栏）。
    /// </summary>
    public interface ICoreScheduler
    {
        CorePlan Plan(CpuTopologySnapshot topo, ProcessTelemetry tel, CorePolicy policy);
        CoreApplyResult Apply(CorePlan plan, ProcessGuard game);
        /// <summary>返回 null 表示滞后判定不迁移，保留缓存亲和。</summary>
        CorePlan Rebalance(CpuTopologySnapshot topo, ProcessTelemetry tel, CorePlan current);
        void Restore();
        /// <summary>仅供 SetIdealFor 实际成功后发布 ideal 状态。</summary>
        void MarkIdealApplied(CorePolicy policy, int idealCpu);
        List<JournalAction> SnapshotCoreActions();
        CoreSchedulerState SnapshotState();
    }
}
