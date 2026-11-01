using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FrameForge.Sys
{
    /// <summary>单个 PDH 计数器。构造时建立查询并预热两次,之后每次 Read 前需要 Collect。</summary>
    public sealed class PdhCounter : IDisposable
    {
        private IntPtr query = IntPtr.Zero;
        private IntPtr counter = IntPtr.Zero;
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

        /// <summary>读多实例计数器的全部值,实例名形如 pid_1234_luid_0x..._engtype_3D。</summary>
        public double[] ReadArray()
        {
            if (!valid || counter == IntPtr.Zero) return new double[0];
            uint size = 0;
            uint count = 0;
            IntPtr buf = IntPtr.Zero;
            try
            {
                uint ret = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
                if (ret != Native.PDH_MORE_DATA || size == 0) return new double[0];
                buf = Marshal.AllocHGlobal((int)size);
                ret = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, buf);
                if (ret != 0) return new double[0];

                int stride = Marshal.SizeOf(typeof(Native.PDH_FMT_COUNTERVALUE_ITEM));
                var list = new List<double>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(buf.ToInt64() + i * stride);
                    var v = (Native.PDH_FMT_COUNTERVALUE_ITEM)Marshal.PtrToStructure(item, typeof(Native.PDH_FMT_COUNTERVALUE_ITEM));
                    if (v.FmtValue.CStatus == 0) list.Add(v.FmtValue.doubleValue);
                }
                return list.ToArray();
            }
            catch { return new double[0]; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        public void Dispose()
        {
            try { if (query != IntPtr.Zero) Native.PdhCloseQuery(query); }
            catch { }
            query = IntPtr.Zero;
            counter = IntPtr.Zero;
            valid = false;
        }
    }
}