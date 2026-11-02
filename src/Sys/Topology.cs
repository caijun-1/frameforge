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
    }

    /// <summary>
    /// CPU 拓扑查询:只读,不修改任何状态。按物理核心分组,
    /// 这样给游戏分配的是完整核心(含 SMT 兄弟),而不是半个核心。
    /// </summary>
    public static class CpuTopology
    {
        private const int RelationProcessorCore = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref int returnLength);

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
            return list.ToArray();
        }

        /// <summary>
        /// 给游戏挑核心:默认一半物理核心(至少 2、至多 8),从编号低的核心开始
        /// —— Intel 混合架构上 P-core 编号在前,E-core 在后,这样优先拿到大核。
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

            long mask = 0;
            for (int i = 0; i < n && i < cores.Length; i++) mask |= cores[i].Mask;
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