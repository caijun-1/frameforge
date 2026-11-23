using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FrameForge.Sys
{
    // boost-core §B3/T04：核心调度"策略层"。不新建执行器：
    //   gameMask != 0 → 委托 AffinityEngine.ApplyWithMask（软亲和优先）
    //   Auto/PCoreFirst → 不改亲和，只改候选过滤
    //   cpuBound == false → 返回空计划（诚实收益护栏）
    // 策略键只做全局级，直接读 settings.ini 的 key=value，不依赖 Settings 新增字段。

    /// <summary>
    /// 从 settings.ini 直接读取四个全局 core* 键（默认 auto/120/0.02/false）。
    /// 由 UI 工程师在 Settings.cs 增加同名字段后自动生效，本层无编译期依赖。
    /// </summary>
    public static class CoreConfig
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, string> cache;
        private static DateTime cacheAt = DateTime.MinValue;
        private const double CacheSeconds = 2.0;

        private static string Read(string key, string def)
        {
            lock (Gate)
            {
                try
                {
                    if (cache == null || (DateTime.UtcNow - cacheAt).TotalSeconds > CacheSeconds)
                    {
                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        string path = FrameForge.Settings.FilePath;
                        if (File.Exists(path))
                        {
                            string[] lines = File.ReadAllLines(path);
                            for (int i = 0; i < lines.Length; i++)
                            {
                                string line = lines[i];
                                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                                int eq = line.IndexOf('=');
                                if (eq <= 0) continue;
                                map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                            }
                        }
                        cache = map;
                        cacheAt = DateTime.UtcNow;
                    }
                    string v;
                    if (cache.TryGetValue(key, out v) && v.Length > 0) return v;
                }
                catch { }
                return def;
            }
        }

        public static CorePolicy Policy()
        {
            string s = Read("corePolicy", "auto").ToLowerInvariant();
            if (s == "pcorefirst") return CorePolicy.PCoreFirst;
            if (s == "singleccd") return CorePolicy.SingleCcd;
            return CorePolicy.Auto;
        }

        public static int RebalanceSeconds()
        {
            int v;
            if (!int.TryParse(Read("coreRebalanceSeconds", "120"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out v)) return 120;
            return Math.Max(15, Math.Min(120, v));
        }

        public static double Hysteresis()
        {
            double v;
            if (double.TryParse(Read("coreHysteresis", "0.02"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out v) && v >= 0 && v <= 1) return v;
            return 0.02;
        }

        public static bool SingleCcdEnabled()
        {
            string s = Read("coreSingleCcd", "false").ToLowerInvariant();
            return s == "1" || s == "true" || s == "yes";
        }
    }

    /// <summary>ICoreScheduler 的默认实现；默认 Auto 行为与原 SteerOnce 完全一致。</summary>
    public sealed class CoreScheduler : ICoreScheduler
    {
        private const int SampleMs = 220;
        private readonly AffinityEngine affinity = new AffinityEngine();
        private CoreSchedulerState state = new CoreSchedulerState();

        public CorePlan Plan(CpuTopologySnapshot topo, ProcessTelemetry tel, CorePolicy policy)
        {
            var plan = new CorePlan();
            plan.Policy = policy;
            plan.Cores = UsedCoresFrom(tel);
            plan.SkipAffinity = policy == CorePolicy.SingleCcd && plan.Cores == 0;
            if (topo == null) topo = CpuTopology.Snapshot();
            if (tel == null || !tel.CpuBound)
            {
                plan.Reason = "cpu-bound=false，跳过核心改动";
                return plan;
            }

            if (policy == CorePolicy.SingleCcd)
            {
                if (!CoreConfig.SingleCcdEnabled())
                {
                    plan.Reason = FrameForge.I18n.T("core.reason.singleccd.disabled");
                    return plan;
                }
                int used;
                long mask = CpuTopology.GameMaskForPolicy(topo, CorePolicy.SingleCcd, plan.Cores, out used);
                plan.GameMask = mask;
                plan.IdealCandidates = CandidateIds(topo, CorePolicy.SingleCcd, mask);
                plan.Reason = mask != 0 && plan.IdealCandidates.Length > 0
                    ? "single-ccd 软亲和"
                    : FrameForge.I18n.T("core.reason.singleccd.noCandidate");
                return plan;
            }

            if (policy == CorePolicy.PCoreFirst)
            {
                plan.IdealCandidates = CandidateIds(topo, CorePolicy.PCoreFirst, 0);
                plan.Reason = plan.IdealCandidates.Length > 0 ? "pcore-first 仅 P 核候选" : "no p-core candidate";
                return plan;
            }

            plan.IdealCandidates = CandidateIds(topo, CorePolicy.Auto, 0);
            plan.Reason = "auto 现状行为";
            return plan;
        }

        public CoreApplyResult Apply(CorePlan plan, ProcessGuard game)
        {
            var res = new CoreApplyResult();
            if (plan == null) { res.Error = "noplan"; return res; }
            if (game == null || game.Pid <= 0) { res.Error = "nogame"; return res; }
            res.Reason = plan.Reason;
            if (plan.Policy == CorePolicy.SingleCcd)
            {
                if (!CoreConfig.SingleCcdEnabled())
                {
                    res.Error = "singleccd-disabled";
                    res.Reason = FrameForge.I18n.T("core.reason.singleccd.disabled");
                    return res;
                }
                if (plan.GameMask == 0 || plan.IdealCandidates == null || plan.IdealCandidates.Length == 0)
                {
                    res.Error = "singleccd-no-candidate";
                    res.Reason = FrameForge.I18n.T("core.reason.singleccd.noCandidate");
                    return res;
                }
            }
            if (plan.SkipAffinity)
            {
                // “全部核心”仍执行 SingleCcd 校验与理想核候选，但不写硬亲和。
                res.Ok = true;
                res.Reason = "singleccd-ideal-only";
                return res;
            }
            if (plan.GameMask == 0)
            {
                // Auto/PCoreFirst 不改亲和，由 BoostEngine 的 TrySteer 走软引导。
                res.Ok = true;
                return res;
            }
            AffinityResult a = affinity.ApplyWithMask(game.Pid, game.Name, plan.GameMask);
            res.Ok = a.Ok;
            res.Error = a.Error;
            res.Applied = a.Ok ? 1 : 0;
            res.Moved = a.Moved;
            res.Mask = a.Mask;
            res.Mode = a.Ok ? (a.Soft ? 1 : 2) : 0;
            if (a.Ok)
            {
                state.Mode = res.Mode;
                state.UsedCores = a.Cores;
                state.AffinityMode = res.Mode;
                state.AffinityMask = a.Mask;
                state.Reason = plan.Reason;
                state.Policy = plan.Policy;
            }
            else
            {
                // ApplyWithMask 会先还原旧值；失败时不能继续公开旧亲和状态。
                ClearAffinityState();
            }
            return res;
        }

        /// <summary>返回 null = 滞后判定不迁移（保留缓存亲和，避免抖动）。</summary>
        public CorePlan Rebalance(CpuTopologySnapshot topo, ProcessTelemetry tel, CorePlan current)
        {
            if (current == null || tel == null || !tel.CpuBound) return null;
            if (topo == null) topo = CpuTopology.Snapshot();
            int[] candidates = current.IdealCandidates;
            if (candidates == null || candidates.Length == 0)
            {
                if (current.Policy == CorePolicy.SingleCcd)
                {
                    if (!CoreConfig.SingleCcdEnabled()) return null;
                    int used;
                    long mask = CpuTopology.GameMaskForPolicy(topo, current.Policy, current.Cores, out used);
                    candidates = CandidateIds(topo, current.Policy, mask);
                }
                else
                {
                    candidates = CandidateIds(topo, current.Policy, 0);
                }
            }
            if (candidates.Length == 0) return null;

            long[] idle1, busy1, idle2, busy2;
            if (Native.CpuCoreTimes(out idle1, out busy1) <= 0) return null;
            try { Thread.Sleep(SampleMs); } catch { }
            if (Native.CpuCoreTimes(out idle2, out busy2) <= 0) return null;
            int n = Math.Min(Math.Min(idle1.Length, busy1.Length), Math.Min(idle2.Length, busy2.Length));
            if (n <= 0) return null;

            int best = -1;
            double bestLoad = 2;
            double hysteresis = CoreConfig.Hysteresis();
            for (int i = 0; i < candidates.Length; i++)
            {
                int idx = candidates[i];
                if (idx < 0 || idx >= n || idx >= 64) continue;    // SetThreadIdealProcessor 只支持 0..63
                long di = idle2[idx] - idle1[idx];
                long db = busy2[idx] - busy1[idx];
                long total = di + db;
                if (total <= 0) total = 1;
                double load = (double)db / total;
                if (best < 0 || load < bestLoad - hysteresis) { best = idx; bestLoad = load; }
            }
            if (best < 0) return null;

            double currentLoad = LoadAt(current.IdealCpu, idle1, busy1, idle2, busy2);
            if (currentLoad >= 0 && Math.Abs(currentLoad - bestLoad) < hysteresis) return null;

            var next = new CorePlan();
            next.Policy = current.Policy;
            next.Cores = current.Cores;
            next.IdealCandidates = candidates;
            next.IdealCpu = best;
            next.Reason = "rebalanced to cpu#" + best.ToString(CultureInfo.InvariantCulture);
            return next;
        }

        public void Restore()
        {
            affinity.Restore();
            state = new CoreSchedulerState();
        }

        /// <summary>SetIdealFor 成功后由执行方调用；决策阶段不提前公开 ideal。</summary>
        public void MarkIdealApplied(CorePolicy policy, int idealCpu)
        {
            state.Policy = policy;
            state.IdealCpu = idealCpu;
        }

        public List<JournalAction> SnapshotCoreActions()
        {
            var list = new List<JournalAction>();
            AffinitySnapshot snap = affinity.Snapshot();
            if (snap.GameMask != 0)
            {
                var a = BoostJournal.NewAction(new Transaction(), ActionKind.AFFINITY_MASK,
                    BoostJournal.MakeGuard(snap.GamePid, snap.GameName, -1),
                    snap.OriginalGameMask.ToString("X", CultureInfo.InvariantCulture),
                    snap.GameMask.ToString("X", CultureInfo.InvariantCulture));
                a.Status = ActionStatus.APPLIED_CONFIRMED;
                list.Add(a);
            }
            if (state.IdealCpu >= 0)
            {
                var a = BoostJournal.NewAction(new Transaction(), ActionKind.IDEAL_CPU,
                    new ProcessGuard(), "64", state.IdealCpu.ToString(CultureInfo.InvariantCulture));
                a.Status = ActionStatus.APPLIED_CONFIRMED;
                list.Add(a);
            }
            return list;
        }

        public CoreSchedulerState SnapshotState()
        {
            var s = new CoreSchedulerState();
            s.Policy = state.Policy;
            s.IdealCpu = state.IdealCpu;
            s.UsedCores = state.UsedCores;
            s.Mode = state.Mode;
            s.AffinityMode = state.AffinityMode;
            s.AffinityMask = state.AffinityMask;
            s.Reason = state.Reason;
            return s;
        }

        private static int UsedCoresFrom(ProcessTelemetry tel)
        {
            return tel == null ? 0 : tel.Cores;
        }

        private void ClearAffinityState()
        {
            state.Mode = 0;
            state.UsedCores = 0;
            state.AffinityMode = 0;
            state.AffinityMask = 0;
        }

        private static int[] CandidateIds(CpuTopologySnapshot topo, CorePolicy policy, long mask)
        {
            CpuCore[] cores = CpuTopology.PolicyCandidates(policy, topo);
            var list = new List<int>();
            for (int i = 0; i < cores.Length; i++)
            {
                if (cores[i].Parked) continue;
                if (mask != 0 && (cores[i].Mask & mask) == 0) continue;
                int idx = cores[i].Index;
                if (idx >= 0 && idx < 64) list.Add(idx);
            }
            return list.ToArray();
        }

        private static double LoadAt(int cpu, long[] idle1, long[] busy1, long[] idle2, long[] busy2)
        {
            if (cpu < 0) return -1;
            int n = Math.Min(Math.Min(idle1.Length, busy1.Length), Math.Min(idle2.Length, busy2.Length));
            if (cpu >= n) return -1;
            long di = idle2[cpu] - idle1[cpu];
            long db = busy2[cpu] - busy1[cpu];
            long total = di + db;
            if (total <= 0) return -1;
            return (double)db / total;
        }
    }
}
