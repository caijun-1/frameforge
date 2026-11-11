using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    /// <summary>
    /// AMD 显卡传感器。NVML 只管 NVIDIA,AMD 卡(比如 RX 6750 GRE)的温度、
    /// 频率、功耗要问驱动自带的 ADL(atiadlxx.dll),否则界面上只剩一堆 —。
    ///
    /// 驱动版本之间差别很大:新驱动导出带 context 的 ADL2_*,老驱动只有全局的 ADL_*;
    /// 有的机器要过几秒 ADL 才就绪。所以这里两套接口都试,读不到就隔一会儿重试,
    /// 每一步的返回码都写进 trace,配合 FrameForge.exe --gpudiag 可以查到具体卡在哪。
    /// </summary>
    public sealed class AmdGpu : IDisposable
    {
        private const int ModeNone = 0;
        private const int ModeAdl2 = 1;    // ADL2_* + context
        private const int ModeLegacy = 2;  // 老式 ADL_* 全局接口

        private int mode = ModeNone;
        private IntPtr context = IntPtr.Zero;
        private int adapter = -1;
        private int adapterCount;
        private int powerType = -1;
        private int tempPath;   // 1=Overdrive6 2=Overdrive5 3=OverdriveN,0 表示还没定
        private int statusPath; // 同上,频率走哪一套接口
        private bool control;   // ADL 控制块创建成功
        private bool produced;  // 至少成功读到过一个真实数值
        private bool broken;    // 访问违例之类,彻底停用
        private bool dllMissing;
        private DateTime lastProbe = DateTime.MinValue;
        private readonly StringBuilder trace = new StringBuilder();

        /// <summary>显卡型号,取自注册表,拿不到就写 AMD GPU。</summary>
        public string DeviceName = "";
        /// <summary>ADL 是否已经接上(不一定代表能读到数)。</summary>
        public bool Available { get { return control && !broken; } }
        /// <summary>是否真的读到过温度 / 频率 / 功耗。</summary>
        public bool Produced { get { return produced; } }
        /// <summary>初始化过程的返回码,给 --gpudiag 用。</summary>
        public string Trace { get { return trace.ToString(); } }

        // ADL 自己分配内存时回调到这个函数;必须用一个静态字段把委托钉住,
        // 否则 GC 回收掉之后再回调就是野指针。
        private delegate IntPtr MallocCallback(int size);
        private static readonly MallocCallback malloc = MallocImpl;

        private static IntPtr MallocImpl(int size) { return Marshal.AllocHGlobal(size); }

        private void Note(string line)
        {
            if (trace.Length < 4000) trace.AppendLine(line);
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        public bool Init()
        {
            if (control || broken || dllMissing) return Available;

            DeviceName = GpuInfo.Name();
            if (DeviceName.Length == 0) DeviceName = "AMD GPU";

            if (CreateAdl2()) mode = ModeAdl2;
            else if (!dllMissing && CreateLegacy()) mode = ModeLegacy;
            else
            {
                Log.Write("adl: unavailable, no usable sensor api");
                broken = true;
                return false;
            }

            control = true;
            adapterCount = ReadAdapterCount();
            Note("adl: mode=" + mode + " adapters=" + adapterCount);
            Log.Write("adl: mode=" + mode + " adapters=" + adapterCount + " " + DeviceName);
            Probe();
            return true;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private bool CreateAdl2()
        {
            try
            {
                IntPtr ctx;
                int rc = Adl.ADL2_Main_Control_Create(malloc, 1, out ctx);
                Note("adl2 create: rc=" + rc + " ctx=" + ctx);
                if (rc == 0 && ctx != IntPtr.Zero)
                {
                    context = ctx;
                    return true;
                }
                Log.Write("adl: ADL2_Main_Control_Create rc=" + rc);
            }
            catch (DllNotFoundException)
            {
                dllMissing = true;
                Log.Write("adl: atiadlxx.dll not installed");
            }
            catch (EntryPointNotFoundException) { Log.Write("adl: ADL2 entry points missing"); }
            catch (Exception ex) { Log.Write("adl2 create: " + ex.GetType().Name + " " + ex.Message); }
            return false;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private bool CreateLegacy()
        {
            try
            {
                int rc = Adl.ADL_Main_Control_Create(malloc, 1);
                Note("adl legacy create: rc=" + rc);
                if (rc == 0) return true;
                Log.Write("adl: ADL_Main_Control_Create rc=" + rc);
            }
            catch (DllNotFoundException)
            {
                dllMissing = true;
                Log.Write("adl: atiadlxx.dll not installed");
            }
            catch (EntryPointNotFoundException) { Log.Write("adl: legacy entry points missing"); }
            catch (Exception ex) { Log.Write("adl legacy create: " + ex.GetType().Name + " " + ex.Message); }
            return false;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private int ReadAdapterCount()
        {
            try
            {
                int n;
                int rc = mode == ModeAdl2
                    ? Adl.ADL2_Adapter_NumberOfAdapters_Get(context, out n)
                    : Adl.ADL_Adapter_NumberOfAdapters_Get(out n);
                Note("adl adapters: rc=" + rc + " count=" + n);
                return rc == 0 ? n : 0;
            }
            catch (Exception ex)
            {
                Log.Write("adl adapters: " + ex.GetType().Name + " " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// 逐个适配器试一遍温度 / 频率 / 功耗,挑一个真能读出数的。
        /// 返回码全部记进 trace,换一台机器看日志就知道是哪一步不认。
        /// </summary>
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private void Probe()
        {
            if (!control) return;
            lastProbe = DateTime.UtcNow;

            int n = adapterCount > 0 ? adapterCount : 1;
            int best = -1;
            for (int i = 0; i < n; i++)
            {
                int t = 0;
                int rt = Temp(i, out t);
                Adl.ADLOD6CurrentStatus st = new Adl.ADLOD6CurrentStatus();
                int rs = Status(i, out st);
                int rp = -1;
                int pv = 0;
                if (rs == 0) rp = Power(i, 0, out pv);
                Note("adl probe " + i + ": temp rc=" + rt + " raw=" + t
                    + " | status rc=" + rs + " engine=" + st.EngineClock + " mem=" + st.MemoryClock
                    + " | power rc=" + rp + " raw=" + pv);

                bool ok = (rt == 0 && NormTemp(t) > 0) || (rs == 0 && st.EngineClock > 0);
                if (ok)
                {
                    produced = true;
                    if (best < 0) best = i;
                }
                else if (best < 0 && i == 0)
                {
                    // 全都读不到时也先记住 0 号,Read 里会继续偶尔重试。
                    adapter = 0;
                }
            }
            if (best >= 0) adapter = best;
            if (adapter < 0) adapter = n > 0 ? 0 : -1;
            Note("adl: adapter=" + adapter + " produced=" + produced
                + " tempPath=" + tempPath + " statusPath=" + statusPath);
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
            if (!control || broken) return g;

            try
            {
                // 开机自启时 ADL 可能还没就绪,没读到数就每 30 秒重新探一次。
                if (!produced && (DateTime.UtcNow - lastProbe).TotalSeconds > 30) Probe();
                if (adapter < 0) return g;

                int t = 0;
                if (Temp(adapter, out t) == 0)
                {
                    double nt = NormTemp(t);
                    if (nt > 0) { g.Temp = nt; produced = true; }
                }

                Adl.ADLOD6CurrentStatus st = new Adl.ADLOD6CurrentStatus();
                if (Status(adapter, out st) == 0)
                {
                    double c = NormClock(st.EngineClock);
                    if (c > 0) { g.ClockCore = c; produced = true; }
                    double m = NormClock(st.MemoryClock);
                    if (m > 0) g.ClockMem = m;
                }

                g.Power = ReadPower();
                if (g.Power > 0) produced = true;
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
                if (Power(adapter, powerType, out v) == 0)
                {
                    double w = v / 256.0;
                    if (w >= 5 && w <= 1000) return w;
                }
                return -1;
            }
            for (int type = 0; type <= 3; type++)
            {
                int v;
                if (Power(adapter, type, out v) != 0) continue;
                double w = v / 256.0;
                if (w >= 5 && w <= 1000)
                {
                    powerType = type;
                    return w;
                }
            }
            return -1;
        }

        // ---- 两套 ADL 接口的统一入口 ----

        /// <summary>
        /// 温度:Overdrive6 在新驱动上对 RDNA 卡经常返回不支持,Overdrive5 和老式的
        /// OverdriveN 反而能读。三条路都试一遍,记住能用的那条,之后就不再乱试。
        /// </summary>
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private int Temp(int index, out int value)
        {
            value = 0;
            if (tempPath == 1 || tempPath == 0)
            {
                int rc = TempOd6(index, out value);
                if (rc == 0 && NormTemp(value) > 0) { tempPath = 1; return 0; }
                value = 0;
            }
            if (tempPath == 2 || tempPath == 0)
            {
                int v;
                if (TempOd5(index, out v) == 0 && NormTemp(v) > 0)
                {
                    tempPath = 2;
                    value = v;
                    return 0;
                }
            }
            if (tempPath == 3 || tempPath == 0)
            {
                int v;
                if (TempOdn(index, out v) == 0 && NormTemp(v) > 0)
                {
                    tempPath = 3;
                    value = v;
                    return 0;
                }
            }
            return -1;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private int TempOd6(int index, out int value)
        {
            value = 0;
            try
            {
                if (mode == ModeAdl2) return Adl.ADL2_Overdrive6_Temperature_Get(context, index, out value);
                if (mode == ModeLegacy) return Adl.ADL_Overdrive6_Temperature_Get(index, out value);
            }
            catch (EntryPointNotFoundException) { return -1; }
            catch (Exception ex) { StopOnException("temp6", ex); }
            return -1;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private int TempOd5(int index, out int value)
        {
            value = 0;
            try
            {
                var t = new Adl.ADLTemperature();
                t.Size = Marshal.SizeOf(typeof(Adl.ADLTemperature));
                int rc = mode == ModeAdl2
                    ? Adl.ADL2_Overdrive5_Temperature_Get(context, index, 0, ref t)
                    : Adl.ADL_Overdrive5_Temperature_Get(index, 0, ref t);
                if (rc == 0) value = t.Temperature;
                return rc;
            }
            catch (EntryPointNotFoundException) { return -1; }
            catch (Exception ex) { StopOnException("temp5", ex); }
            return -1;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private int TempOdn(int index, out int value)
        {
            value = 0;
            try
            {
                // 1 = ADL_ODN_TEMP_EDGE,显卡核心温度。
                if (mode == ModeAdl2) return Adl.ADL2_OverdriveN_Temperature_Get(context, index, 1, out value);
                if (mode == ModeLegacy) return Adl.ADL_OverdriveN_Temperature_Get(index, 1, out value);
            }
            catch (EntryPointNotFoundException) { return -1; }
            catch (Exception ex) { StopOnException("tempN", ex); }
            return -1;
        }

        /// <summary>频率:Overdrive6 的 CurrentStatus 优先,读不到换 Overdrive5 的活动状态。</summary>
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private int Status(int index, out Adl.ADLOD6CurrentStatus status)
        {
            status = new Adl.ADLOD6CurrentStatus();
            if (statusPath == 1 || statusPath == 0)
            {
                Adl.ADLOD6CurrentStatus st;
                if (StatusOd6(index, out st) == 0
                    && (NormClock(st.EngineClock) > 0 || NormClock(st.MemoryClock) > 0))
                {
                    statusPath = 1;
                    status = st;
                    return 0;
                }
            }
            if (statusPath == 2 || statusPath == 0)
            {
                int core, mem;
                if (ClockOd5(index, out core, out mem) == 0
                    && (NormClock(core) > 0 || NormClock(mem) > 0))
                {
                    statusPath = 2;
                    status.EngineClock = core;
                    status.MemoryClock = mem;
                    return 0;
                }
            }
            return -1;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private int StatusOd6(int index, out Adl.ADLOD6CurrentStatus status)
        {
            status = new Adl.ADLOD6CurrentStatus();
            try
            {
                if (mode == ModeAdl2) return Adl.ADL2_Overdrive6_CurrentStatus_Get(context, index, out status);
                if (mode == ModeLegacy) return Adl.ADL_Overdrive6_CurrentStatus_Get(index, out status);
            }
            catch (EntryPointNotFoundException) { return -1; }
            catch (Exception ex) { StopOnException("status6", ex); }
            return -1;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private int ClockOd5(int index, out int core, out int mem)
        {
            core = 0;
            mem = 0;
            try
            {
                var a = new Adl.ADLPMActivity();
                a.Size = Marshal.SizeOf(typeof(Adl.ADLPMActivity));
                int rc = mode == ModeAdl2
                    ? Adl.ADL2_Overdrive5_CurrentActivity_Get(context, index, ref a)
                    : Adl.ADL_Overdrive5_CurrentActivity_Get(index, ref a);
                if (rc == 0)
                {
                    core = a.EngineClock;
                    mem = a.MemoryClock;
                }
                return rc;
            }
            catch (EntryPointNotFoundException) { return -1; }
            catch (Exception ex) { StopOnException("clock5", ex); }
            return -1;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private int Power(int index, int type, out int value)
        {
            value = 0;
            try
            {
                if (mode == ModeAdl2) return Adl.ADL2_Overdrive6_CurrentPower_Get(context, index, type, out value);
                if (mode == ModeLegacy) return Adl.ADL_Overdrive6_CurrentPower_Get(index, type, out value);
            }
            catch (EntryPointNotFoundException) { return -1; }
            catch (Exception ex) { StopOnException("power", ex); }
            return -1;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private void StopOnException(string what, Exception ex)
        {
            broken = true;
            Note("adl " + what + " threw " + ex.GetType().Name + ": " + ex.Message);
            Log.Write("adl " + what + ": " + ex.GetType().Name + " " + ex.Message + " (amd stats off)");
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

        /// <summary>
        /// iEngineClock / iMemoryClock 文档写的是 10 KHz 单位,除以 100 得 MHz;
        /// 个别驱动直接给 MHz,按数值大小区分,两种都认。
        /// </summary>
        private static double NormClock(int v)
        {
            if (v <= 0) return -1;
            if (v >= 5000 && v <= 600000) return Math.Round(v / 100.0);
            if (v >= 200 && v <= 6000) return v;
            return -1;
        }

        private void Destroy()
        {
            try
            {
                if (mode == ModeAdl2 && context != IntPtr.Zero) Adl.ADL2_Main_Control_Destroy(context);
                else if (mode == ModeLegacy && control) Adl.ADL_Main_Control_Destroy();
            }
            catch { }
            context = IntPtr.Zero;
            control = false;
        }

        public void Dispose() { Destroy(); }

        // ---------------- 诊断 ----------------

        /// <summary>
        /// 给 --gpudiag 用:把 ADL 的加载情况、导出的函数、每一步返回码都摊开,
        /// 换一台读不到温度的机器时把这个字符串发回来就能定位。
        /// </summary>
        public static string Diagnose()
        {
            var sb = new StringBuilder();
            sb.AppendLine("FrameForge GPU diagnostics");
            sb.AppendLine("time:    " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("os:      " + Environment.OSVersion.VersionString
                + (Environment.Is64BitOperatingSystem ? " x64" : " x86"));
            sb.AppendLine("process: " + (Environment.Is64BitProcess ? "x64" : "x86"));
            sb.AppendLine("name:    " + GpuInfo.Name());
            sb.AppendLine("vram:    " + GpuInfo.TotalVram() + " bytes");
            sb.AppendLine();
            sb.AppendLine(AdlProbe.Report());
            sb.AppendLine();

            var nv = new Nvml();
            nv.Init();
            sb.AppendLine("nvml:    " + (nv.Available ? "ok, " + nv.DeviceName : "unavailable"));
            nv.Dispose();

            var amd = new AmdGpu();
            bool ok = amd.Init();
            sb.AppendLine("adl init: " + ok);
            sb.AppendLine(amd.Trace);
            GpuSample g = amd.Read();
            sb.AppendLine("read:    temp=" + g.Temp + " clock=" + g.ClockCore
                + " memclock=" + g.ClockMem + " power=" + g.Power);
            amd.Dispose();
            return sb.ToString();
        }

        /// <summary>ADL 的 P/Invoke 声明。只有 AMD 驱动装过 atiadlxx.dll 才会走到这里。</summary>
        private static class Adl
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct ADLOD6CurrentStatus
            {
                public int EngineClock;      // 10 KHz
                public int MemoryClock;      // 10 KHz
                public int ActivityPercent;
                public int CurrentPerformanceLevel;
                public int CurrentBusSpeed;
                public int CurrentBusLanes;
                public int MaximumBusLanes;
                public int ExtValue;
                public int ExtMask;
            }

            // Overdrive5 的温度结构:Size 必须先填,温度是千分之一摄氏度。
            [StructLayout(LayoutKind.Sequential)]
            public struct ADLTemperature
            {
                public int Size;
                public int Temperature;
            }

            // Overdrive5 的活动状态:EngineClock / MemoryClock 单位 10 KHz。
            [StructLayout(LayoutKind.Sequential)]
            public struct ADLPMActivity
            {
                public int Size;
                public int EngineClock;
                public int MemoryClock;
                public int Vddc;
                public int ActivityPercent;
                public int CurrentPerformanceLevel;
                public int CurrentBusSpeed;
                public int CurrentBusLanes;
                public int MaximumBusLanes;
            }

            // 新接口:ADL2_* 带 context,驱动 13.x 以后都有。
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

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL2_Overdrive5_Temperature_Get(IntPtr context, int index, int controller, ref ADLTemperature temperature);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL2_Overdrive5_CurrentActivity_Get(IntPtr context, int index, ref ADLPMActivity activity);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL2_OverdriveN_Temperature_Get(IntPtr context, int index, int type, out int temperature);

            // 老接口:没有 context,某些驱动只认这一套。
            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL_Main_Control_Create(MallocCallback callback, int enumConnectedAdapters);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL_Main_Control_Destroy();

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL_Adapter_NumberOfAdapters_Get(out int numAdapters);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL_Overdrive6_Temperature_Get(int index, out int temperature);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL_Overdrive6_CurrentStatus_Get(int index, out ADLOD6CurrentStatus status);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL_Overdrive6_CurrentPower_Get(int index, int type, out int power);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL_Overdrive5_Temperature_Get(int index, int controller, ref ADLTemperature temperature);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL_Overdrive5_CurrentActivity_Get(int index, ref ADLPMActivity activity);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ADL_OverdriveN_Temperature_Get(int index, int type, out int temperature);
        }
    }

    /// <summary>诊断用:确认 atiadlxx.dll 到底能不能加载、导出了哪些函数。</summary>
    internal static class AdlProbe
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileName(IntPtr module, StringBuilder path, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        private static readonly string[] Exports = new string[]
        {
            "ADL2_Main_Control_Create",
            "ADL2_Adapter_NumberOfAdapters_Get",
            "ADL2_Overdrive6_Temperature_Get",
            "ADL2_Overdrive6_CurrentStatus_Get",
            "ADL2_Overdrive6_CurrentPower_Get",
            "ADL2_Overdrive5_Temperature_Get",
            "ADL2_Overdrive5_CurrentActivity_Get",
            "ADL2_OverdriveN_Temperature_Get",
            "ADL_Main_Control_Create",
            "ADL_Overdrive6_Temperature_Get",
            "ADL_Overdrive6_CurrentStatus_Get",
            "ADL_Overdrive6_CurrentPower_Get",
            "ADL_Overdrive5_Temperature_Get",
            "ADL_Overdrive5_CurrentActivity_Get",
            "ADL_OverdriveN_Temperature_Get",
        };

        public static string Report()
        {
            var sb = new StringBuilder();
            IntPtr module = LoadLibrary("atiadlxx.dll");
            if (module == IntPtr.Zero)
            {
                sb.AppendLine("atiadlxx.dll: LOAD FAILED, win32 error " + Marshal.GetLastWin32Error());
                return sb.ToString();
            }
            var path = new StringBuilder(520);
            GetModuleFileName(module, path, (uint)path.Capacity);
            sb.AppendLine("atiadlxx.dll: " + path.ToString());
            for (int i = 0; i < Exports.Length; i++)
            {
                IntPtr fn = GetProcAddress(module, Exports[i]);
                sb.AppendLine("  " + Exports[i] + ": " + (fn != IntPtr.Zero ? "yes" : "NO"));
            }
            FreeLibrary(module);
            return sb.ToString();
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