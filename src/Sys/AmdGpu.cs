using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    /// <summary>
    /// AMD 显卡传感器。NVML 只管 NVIDIA,AMD 卡(比如 RX 6750 GRE)的温度、
    /// 频率、功耗要问驱动自带的 ADL(atiadlxx.dll),否则界面上只剩一堆 —。
    /// </summary>
    public sealed class AmdGpu : IDisposable
    {
        private IntPtr context = IntPtr.Zero;
        private int adapter = -1;
        private int powerType = -1;
        private bool ready;
        private bool broken;

        /// <summary>显卡型号,取自注册表,拿不到就写 AMD GPU。</summary>
        public string DeviceName = "";
        public bool Available { get { return ready; } }

        // ADL 自己分配内存时回调到这个函数;必须用一个静态字段把委托钉住,
        // 否则 GC 回收掉之后再回调就是野指针。
        private delegate IntPtr MallocCallback(int size);
        private static readonly MallocCallback malloc = MallocImpl;

        private static IntPtr MallocImpl(int size) { return Marshal.AllocHGlobal(size); }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        public bool Init()
        {
            try
            {
                if (Adl.ADL2_Main_Control_Create(malloc, 1, out context) != 0)
                {
                    context = IntPtr.Zero;
                    return false;
                }
                int count = 0;
                if (Adl.ADL2_Adapter_NumberOfAdapters_Get(context, out count) != 0 || count <= 0)
                {
                    Destroy();
                    return false;
                }

                // 逐个适配器试:能读到温度或频率的才算可用。
                // 不解析 AdapterInfo 结构体,换驱动版本也不会因为字段对不上而失效。
                for (int i = 0; i < count && adapter < 0; i++)
                {
                    int t;
                    if (Adl.ADL2_Overdrive6_Temperature_Get(context, i, out t) == 0 && NormTemp(t) > 0)
                    {
                        adapter = i;
                        break;
                    }
                    Adl.ADLOD6CurrentStatus st;
                    if (Adl.ADL2_Overdrive6_CurrentStatus_Get(context, i, out st) == 0
                        && st.EngineClock > 0)
                    {
                        adapter = i;
                    }
                }
                if (adapter < 0)
                {
                    Destroy();
                    return false;
                }

                DeviceName = GpuInfo.Name();
                if (DeviceName.Length == 0) DeviceName = "AMD GPU";
                ready = true;
                Log.Write("adl: adapter " + adapter + " " + DeviceName);
                return true;
            }
            catch (DllNotFoundException) { Log.Write("adl: atiadlxx.dll not installed"); return false; }
            catch (EntryPointNotFoundException) { Log.Write("adl: overdrive api missing"); return false; }
            catch (Exception ex) { Log.Write("adl init: " + ex.Message); return false; }
        }

        /// <summary>读一次 AMD 显卡数据;取不到的字段保持 -1,界面显示 —。</summary>
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        public GpuSample Read()
        {
            var g = new GpuSample();
            g.Name = DeviceName;
            g.Util = -1; g.Temp = -1; g.ClockCore = -1; g.ClockMem = -1;
            g.Power = -1; g.PowerLimit = -1; g.MemUsed = 0; g.MemTotal = 0; g.Fan = -1;
            if (!ready || broken || context == IntPtr.Zero || adapter < 0) return g;

            try
            {
                int t;
                if (Adl.ADL2_Overdrive6_Temperature_Get(context, adapter, out t) == 0)
                    g.Temp = NormTemp(t);

                Adl.ADLOD6CurrentStatus st;
                if (Adl.ADL2_Overdrive6_CurrentStatus_Get(context, adapter, out st) == 0)
                {
                    // iEngineClock 的单位是 10 KHz,换成 MHz。
                    if (st.EngineClock >= 5000 && st.EngineClock <= 600000)
                        g.ClockCore = st.EngineClock / 100.0;
                    if (st.MemoryClock >= 5000 && st.MemoryClock <= 600000)
                        g.ClockMem = st.MemoryClock / 100.0;
                }

                g.Power = ReadPower();
            }
            catch (Exception ex)
            {
                broken = true;
                Log.Write("adl read: " + ex.GetType().Name + " " + ex.Message + " (amd stats off)");
            }
            return g;
        }

        /// <summary>
        /// 功耗:驱动按「瓦特 + 8 位小数」返回,除以 256 才是瓦。
        /// 不同驱动接受的电源类型不一样,试出第一个合理的值就记住它。
        /// </summary>
        private double ReadPower()
        {
            if (powerType >= 0)
            {
                int v;
                if (Adl.ADL2_Overdrive6_CurrentPower_Get(context, adapter, powerType, out v) == 0)
                {
                    double w = v / 256.0;
                    if (w >= 5 && w <= 1000) return w;
                }
                return -1;
            }
            for (int type = 0; type <= 3; type++)
            {
                int v;
                if (Adl.ADL2_Overdrive6_CurrentPower_Get(context, adapter, type, out v) != 0) continue;
                double w = v / 256.0;
                if (w >= 5 && w <= 1000)
                {
                    powerType = type;
                    return w;
                }
            }
            return -1;
        }

        /// <summary>
        /// ADL 的温度单位在不同驱动上分别是摄氏度 / 十分之一度 / 千分之一度,
        /// 这里按数值大小判断,只接受 15~120 度这种像人的结果。
        /// </summary>
        private static double NormTemp(int v)
        {
            if (v <= 0) return -1;
            double[] divs = new double[] { 1, 10, 100, 1000 };
            for (int i = 0; i < divs.Length; i++)
            {
                double t = v / divs[i];
                if (t >= 15 && t <= 120) return Math.Round(t);
            }
            return -1;
        }

        private void Destroy()
        {
            try { if (context != IntPtr.Zero) Adl.ADL2_Main_Control_Destroy(context); }
            catch { }
            context = IntPtr.Zero;
            ready = false;
        }

        public void Dispose() { Destroy(); }

        /// <summary>ADL 的 P/Invoke 声明。只有 AMD 驱动装过 atiadlxx.dll 才会走到这里。</summary>
        private static class Adl
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct ADLOD6CurrentStatus
            {
                public int EngineClock;      // 10 KHz
                public int MemoryClock;      // 10 KHz
                public int CurrentPerformanceLevel;
                public int CurrentBusSpeed;
                public int CurrentBusLanes;
                public int MaximumBusLanes;
                public int ActivityPercent;
                public int ExtValue;
                public int ExtMask;
            }

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL2_Main_Control_Create(MallocCallback callback, int enumConnectedAdapters, out IntPtr context);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL2_Main_Control_Destroy(IntPtr context);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int numAdapters);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL2_Overdrive6_Temperature_Get(IntPtr context, int index, out int temperature);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL2_Overdrive6_CurrentStatus_Get(IntPtr context, int index, out ADLOD6CurrentStatus status);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL2_Overdrive6_CurrentPower_Get(IntPtr context, int index, int type, out int power);
        }
    }

    /// <summary>不依赖任何厂商 SDK 的显卡信息:从显示适配器注册表读型号和显存容量。</summary>
    public static class GpuInfo
    {
        private const string ClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static bool loaded;
        private static string name = "";
        private static ulong vram;

        public static string Name()
        {
            Load();
            return name;
        }

        public static ulong TotalVram()
        {
            Load();
            return vram;
        }

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey cls = baseKey.OpenSubKey(ClassKey))
                {
                    if (cls == null) return;
                    string[] subs = cls.GetSubKeyNames();
                    for (int i = 0; i < subs.Length; i++)
                    {
                        // 显卡实例都是 0000、0001 这种四位数字,其余子键是配置项。
                        string s = subs[i];
                        if (s.Length != 4) continue;
                        bool digits = true;
                        for (int c = 0; c < s.Length; c++)
                            if (!char.IsDigit(s[c])) { digits = false; break; }
                        if (!digits) continue;

                        using (RegistryKey k = cls.OpenSubKey(s))
                        {
                            if (k == null) continue;
                            string desc = k.GetValue("DriverDesc", "") as string;
                            if (string.IsNullOrEmpty(desc)) continue;
                            ulong size = ReadSize(k);
                            // 多显卡时选显存最大的那块,核显不会被当成主卡。
                            if (size >= vram && desc.Length > 0)
                            {
                                vram = size;
                                if (size > 0 || name.Length == 0) name = desc.Trim();
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("gpu info: " + ex.Message); }
        }

        private static ulong ReadSize(RegistryKey k)
        {
            try
            {
                object v = k.GetValue("HardwareInformation.qwMemorySize", null);
                if (v is long) { long l = (long)v; return l > 0 ? (ulong)l : 0; }
                byte[] b = v as byte[];
                if (b != null && b.Length == 8) return BitConverter.ToUInt64(b, 0);

                // 老驱动只有 DWORD 版本,单位是字节,超过 4 GB 会被截断,只当兜底。
                object m = k.GetValue("HardwareInformation.MemorySize", null);
                if (m is int) { int n = (int)m; return n > 0 ? (ulong)n : 0; }
            }
            catch { }
            return 0;
        }
    }
}