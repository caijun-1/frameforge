using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FrameForge.Sys
{
    /// <summary>一个物理核心:Mask 里包含它的全部逻辑处理器(SMT 兄弟线程)。</summary>
    public class CpuCore
    {
        public int Index;   // 编号最小的逻辑处理器
        public long Mask;   // 该核心的全部逻辑处理器
        public bool Smt;
        public int Efficiency;  // EfficiencyClass:0 = 高性能核,越大越省电
        public int Llc;         // 末级缓存编号,用来区分 CCD / 小芯片
        public bool Efficient;  // 能效核(E-core)
        public bool Parked;     // 已停核
    }

    /// <summary>SYSTEM_CPU_SET_INFORMATION 里 CpuSet 分支的前 20 字节。</summary>
    public class CpuSetInfo
    {
        public int Id;
        public int Group;
        public int LogicalIndex;
        public int CoreIndex;
        public int LlcIndex;
        public int NumaIndex;
        public int Efficiency;
        public bool Parked;
    }

    /// <summary>
    /// CPU 拓扑查询:只读,不修改任何状态。按物理核心分组,
    /// 这样给游戏分配的是完整核心(含 SMT 兄弟),而不是半个核心。
    /// </summary>
    public static class CpuTopology
    {
        private const int RelationProcessorCore = 0;
        private const int CpuSetHeader = 20;

        private static readonly object Gate = new object();
        private static CpuSetInfo[] cpuSets;
        private static bool cpuSetsLoaded;
        private static bool softSupported = true;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref int returnLength);

        /// <summary>系统是否提供 CPU Sets(Win10 1703+);老系统上软亲和不可用。</summary>
        public static bool SoftSupported
        {
            get { CpuSets(); lock (Gate) { return softSupported; } }
        }

        /// <summary>是否混合架构(同时存在 P 核与 E 核)。</summary>
        public static bool IsHybrid
        {
            get
            {
                CpuCore[] cores = Cores();
                bool p = false, e = false;
                for (int i = 0; i < cores.Length; i++)
                {
                    if (cores[i].Efficient) e = true; else p = true;
                }
                return p && e;
            }
        }

        /// <summary>
        /// 读取系统的 CPU Set 列表并缓存。失败时返回空数组,调用方自然退回
        /// 老式 64 位亲和掩码;老系统缺入口点也只记一次,不反复触发异常。
        /// </summary>
        public static CpuSetInfo[] CpuSets()
        {
            lock (Gate)
            {
                if (cpuSetsLoaded) return cpuSets;
                cpuSetsLoaded = true;
                var list = new List<CpuSetInfo>();
                try
                {
                    int need;
                    Native.GetSystemCpuSetInformation(IntPtr.Zero, 0, out need, IntPtr.Zero, 0);
                    if (need > 0)
                    {
                        IntPtr buf = Marshal.AllocHGlobal(need);
                        try
                        {
                            int got;
                            if (Native.GetSystemCpuSetInformation(buf, need, out got, IntPtr.Zero, 0))
                            {
                                int off = 0;
                                while (off + CpuSetHeader <= got)
                                {
                                    IntPtr p = new IntPtr(buf.ToInt64() + off);
                                    int size = Marshal.ReadInt32(p, 0);
                                    if (size < CpuSetHeader) break;
                                    // Type 0 是 CpuSet,其余是缓存/调度类记录。
                                    if (Marshal.ReadInt32(p, 4) == 0)
                                    {
                                        var ci = new CpuSetInfo();
                                        ci.Id = Marshal.ReadInt32(p, 8);
                                        ci.Group = Marshal.ReadInt16(p, 12) & 0xFFFF;
                                        ci.LogicalIndex = Marshal.ReadByte(p, 14);
                                        ci.CoreIndex = Marshal.ReadByte(p, 15);
                                        ci.LlcIndex = Marshal.ReadByte(p, 16);
                                        ci.NumaIndex = Marshal.ReadByte(p, 17);
                                        ci.Efficiency = Marshal.ReadByte(p, 18);
                                        ci.Parked = (Marshal.ReadByte(p, 19) & 1) != 0;
                                        list.Add(ci);
                                    }
                                    off += size;
                                }
                            }
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    softSupported = false;
                }
                catch (Exception ex)
                {
                    softSupported = false;
                    Log.Write("cpuset: " + ex.Message);
                }
                cpuSets = list.ToArray();
                return cpuSets;
            }
        }

        public static CpuCore[] Cores()
        {
            var list = new List<CpuCore>();
            int len = 0;
            try
            {
                GetLogicalProcessorInformation(IntPtr.Zero, ref len);
                if (len <= 0) return list.ToArray();
                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (!GetLogicalProcessorInformation(buf, ref len)) return list.ToArray();
                    // 结构长度随指针宽度变化(x64 = 32,x86 = 24)。只读掩码与关系字段,
                    // 不去为那个联合体定义两套结构,避免布局踩坑。
                    int stride = IntPtr.Size == 8 ? 32 : 24;
                    long seen = 0;
                    for (int off = 0; off + stride <= len; off += stride)
                    {
                        IntPtr p = new IntPtr(buf.ToInt64() + off);
                        if (Marshal.ReadInt32(p, IntPtr.Size) != RelationProcessorCore) continue;
                        long mask = Marshal.ReadIntPtr(p, 0).ToInt64();
                        if (mask == 0 || (seen & mask) != 0) continue;
                        seen |= mask;
                        list.Add(new CpuCore { Index = LowestBit(mask), Mask = mask, Smt = PopCount(mask) > 1 });
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception ex) { Log.Write("topology: " + ex.Message); }
            list.Sort(delegate(CpuCore a, CpuCore b) { return a.Index.CompareTo(b.Index); });
            Enrich(list);
            return list.ToArray();
        }

        /// <summary>把 CPU Set 里的 P/E、CCD 信息补到物理核心上。</summary>
        private static void Enrich(List<CpuCore> cores)
        {
            CpuSetInfo[] info = CpuSets();
            if (info.Length == 0) return;
            for (int c = 0; c < cores.Count; c++)
            {
                CpuCore core = cores[c];
                for (int bit = 0; bit < 64; bit++)
                {
                    if ((core.Mask & (1L << bit)) == 0) continue;
                    CpuSetInfo ci = Find(info, bit);
                    if (ci == null) continue;
                    // 同一物理核心的兄弟线程共享效率等级与缓存编号,取到第一个就够。
                    core.Efficiency = ci.Efficiency;
                    core.Llc = ci.LlcIndex;
                    core.Efficient = ci.Efficiency > 0;
                    core.Parked = ci.Parked;
                    break;
                }
            }
        }

        private static CpuSetInfo Find(CpuSetInfo[] info, int logical)
        {
            for (int i = 0; i < info.Length; i++)
                if (info[i].Group == 0 && info[i].LogicalIndex == logical) return info[i];
            return null;
        }

        /// <summary>
        /// 给游戏挑核心:默认一半物理核心(至少 2、至多 8)。
        /// 先按效率等级升序(P 核在前),再按缓存编号、编号升序,
        /// 不再假设"编号低的一定是性能核"——混合架构上这个假设靠不住。
        /// </summary>
        public static long GameMask(int coresWanted, out int coresUsed)
        {
            coresUsed = 0;
            CpuCore[] cores = Cores();
            if (cores.Length == 0) return 0;
            int n = coresWanted;
            if (n <= 0) n = cores.Length / 2;
            if (n < 2) n = 2;
            if (n > 8) n = 8;
            if (n >= cores.Length) n = cores.Length > 1 ? cores.Length - 1 : 1;

            var sorted = (CpuCore[])cores.Clone();
            Array.Sort(sorted, delegate(CpuCore a, CpuCore b)
            {
                int c = a.Efficiency.CompareTo(b.Efficiency);
                if (c != 0) return c;
                c = a.Llc.CompareTo(b.Llc);
                if (c != 0) return c;
                return a.Index.CompareTo(b.Index);
            });

            long mask = 0;
            for (int i = 0; i < n && i < sorted.Length; i++) mask |= sorted[i].Mask;
            coresUsed = n;
            return mask;
        }

        public static long AllMask()
        {
            CpuCore[] cores = Cores();
            long mask = 0;
            for (int i = 0; i < cores.Length; i++) mask |= cores[i].Mask;
            return mask;
        }

        /// <summary>掩码里完整包含的物理核心数(含 SMT 兄弟线程时也只算一个核)。</summary>
        public static int CoreCount(long mask)
        {
            CpuCore[] cores = Cores();
            int n = 0;
            for (int i = 0; i < cores.Length; i++)
                if (cores[i].Mask != 0 && (cores[i].Mask & mask) == cores[i].Mask) n++;
            return n;
        }

        /// <summary>把传统亲和掩码翻译成 CPU Set Id 列表(只处理 0 号处理器组)。</summary>
        public static uint[] SetIdsForMask(long mask)
        {
            var list = new List<uint>();
            CpuSetInfo[] info = CpuSets();
            for (int i = 0; i < info.Length; i++)
            {
                CpuSetInfo ci = info[i];
                if (ci.Group != 0 || ci.LogicalIndex < 0 || ci.LogicalIndex >= 64) continue;
                if ((mask & (1L << ci.LogicalIndex)) != 0) list.Add((uint)ci.Id);
            }
            return list.ToArray();
        }

        public static int PopCount(long value)
        {
            ulong v = (ulong)value;
            int c = 0;
            while (v != 0) { c += (int)(v & 1UL); v >>= 1; }
            return c;
        }

        public static int LowestBit(long value)
        {
            ulong v = (ulong)value;
            int i = 0;
            while (i < 64 && (v & 1UL) == 0) { v >>= 1; i++; }
            return i;
        }
    }
}