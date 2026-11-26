using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FrameForge.Sys
{
    /// <summary>单个 PDH 计数器。构造时建立查询并预热两次,之后每次 Read 前需要 Collect。</summary>
    public sealed class PdhCounter : IDisposable
    {
        private static readonly int ItemStride = Marshal.SizeOf(typeof(Native.PDH_FMT_COUNTERVALUE_ITEM));
        private static readonly int FmtOffset = Marshal.OffsetOf(typeof(Native.PDH_FMT_COUNTERVALUE_ITEM), "FmtValue").ToInt32();

        private IntPtr query = IntPtr.Zero;
        private IntPtr counter = IntPtr.Zero;
        private IntPtr buf = IntPtr.Zero;
        private uint bufCap;
        private readonly List<double> valList = new List<double>();
        private readonly List<string> nameList = new List<string>();
        private bool valid;

        public PdhCounter(string path)
        {
            try
            {
                if (Native.PdhOpenQuery(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
                IntPtr c;
                if (Native.PdhAddEnglishCounter(query, path, IntPtr.Zero, out c) != 0) return;
                counter = c;
                Native.PdhCollectQueryData(query);
                System.Threading.Thread.Sleep(120);
                Native.PdhCollectQueryData(query);
                valid = true;
            }
            catch (Exception ex)
            {
                Log.Write("pdh open " + path + ": " + ex.Message);
                valid = false;
            }
        }

        public bool Valid { get { return valid; } }

        public void Collect()
        {
            if (!valid) return;
            try { Native.PdhCollectQueryData(query); }
            catch { valid = false; }
        }

        /// <summary>读第一个实例的值(单实例计数器用)。</summary>
        public double Read()
        {
            double[] v = ReadArray();
            return v.Length > 0 ? v[0] : -1;
        }

        private void EnsureBuffer(uint size)
        {
            if (size <= bufCap && buf != IntPtr.Zero) return;
            if (buf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buf);
                buf = IntPtr.Zero;
                bufCap = 0;
            }
            buf = Marshal.AllocHGlobal((int)size);
            bufCap = size;
        }

        /// <summary>读多实例计数器的全部值,实例名形如 pid_1234_luid_0x..._engtype_3D。</summary>
        public double[] ReadArray()
        {
            if (!valid || counter == IntPtr.Zero) return new double[0];
            uint size = 0;
            uint count = 0;
            try
            {
                uint ret = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
                if (ret != Native.PDH_MORE_DATA || size == 0) return new double[0];
                EnsureBuffer(size);
                ret = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, buf);
                if (ret != 0) return new double[0];

                valList.Clear();
                if (valList.Capacity < (int)count) valList.Capacity = (int)count;
                long baseAddr = buf.ToInt64();
                for (uint i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(baseAddr + i * ItemStride);
                    if (Marshal.ReadInt32(item, FmtOffset) == 0)
                        valList.Add(BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, FmtOffset + 8)));
                }
                return valList.ToArray();
            }
            catch { return new double[0]; }
        }

        /// <summary>
        /// 连同实例名一起读。GPU Engine 的实例名里带 pid,可以算出每个进程占了多少 3D 引擎,
        /// 这是识别「没见过的游戏」最可靠的一条证据。
        /// </summary>
        public void ReadNamed(out string[] names, out double[] values)
        {
            names = new string[0];
            values = new double[0];
            if (!valid || counter == IntPtr.Zero) return;
            uint size = 0;
            uint count = 0;
            try
            {
                uint ret = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
                if (ret != Native.PDH_MORE_DATA || size == 0) return;
                EnsureBuffer(size);
                ret = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, buf);
                if (ret != 0) return;

                nameList.Clear();
                valList.Clear();
                if (nameList.Capacity < (int)count) nameList.Capacity = (int)count;
                if (valList.Capacity < (int)count) valList.Capacity = (int)count;
                long baseAddr = buf.ToInt64();
                for (uint i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(baseAddr + i * ItemStride);
                    if (Marshal.ReadInt32(item, FmtOffset) != 0) continue;
                    IntPtr namePtr = Marshal.ReadIntPtr(item, 0);
                    if (namePtr == IntPtr.Zero) continue;
                    nameList.Add(Marshal.PtrToStringUni(namePtr));
                    valList.Add(BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, FmtOffset + 8)));
                }
                names = nameList.ToArray();
                values = valList.ToArray();
            }
            catch
            {
                names = new string[0];
                values = new double[0];
            }
        }

        public void Dispose()
        {
            try { if (query != IntPtr.Zero) Native.PdhCloseQuery(query); }
            catch { }
            if (buf != IntPtr.Zero)
            {
                try { Marshal.FreeHGlobal(buf); } catch { }
                buf = IntPtr.Zero;
                bufCap = 0;
            }
            query = IntPtr.Zero;
            counter = IntPtr.Zero;
            valid = false;
        }
    }
}