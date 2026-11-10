using System;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;

namespace FrameForge.Sys
{
    /// <summary>NVML 封装:NVIDIA 显卡的温度 / 占用 / 频率 / 功耗 / 显存。</summary>
    public sealed class Nvml : IDisposable
    {
        private IntPtr device = IntPtr.Zero;
        private bool ready;
        private bool broken;

        public string DeviceName = "";
        public bool Available { get { return ready; } }

        // 显卡驱动复位 / 驱动自身出问题时,nvml.dll 里可能直接抛访问违例(CSE),
        // 普通 catch 接不住会让整个程序退出(日志里 11:29 那次就是这样崩的)。
        // 加上这两个标记就能接住,并且把 NVML 停掉,不再反复踩。
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        public bool Init()
        {
            try
            {
                if (Native.nvmlInit_v2() != 0) return false;
                uint count;
                if (Native.nvmlDeviceGetCount_v2(out count) != 0 || count == 0)
                {
                    Native.nvmlShutdown();
                    return false;
                }
                if (Native.nvmlDeviceGetHandleByIndex_v2(0, out device) != 0)
                {
                    Native.nvmlShutdown();
                    return false;
                }
                var sb = new StringBuilder(96);
                if (Native.nvmlDeviceGetName(device, sb, (uint)sb.Capacity) == 0)
                {
                    string name = sb.ToString();
                    if (name.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(7);
                    DeviceName = name;
                }
                ready = true;
                return true;
            }
            catch (DllNotFoundException) { Log.Write("nvml: not installed"); return false; }
            catch (Exception ex) { Log.Write("nvml init: " + ex.Message); return false; }
        }

        /// <summary>读取一块显卡的实时数据;失败字段保持 -1,界面显示 —。</summary>
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        public GpuSample Read()
        {
            var g = new GpuSample();
            g.Name = DeviceName;
            g.Util = -1; g.Temp = -1; g.ClockCore = -1; g.ClockMem = -1;
            g.Power = -1; g.PowerLimit = -1; g.MemUsed = 0; g.MemTotal = 0; g.Fan = -1;
            if (!ready || broken) return g;

            try
            {
                uint u;
                Native.NVMLUTILIZATION util;
                Native.NVMLMEMORY mem;
                if (Native.nvmlDeviceGetUtilizationRates(device, out util) == 0)
                {
                    g.Util = util.Gpu;
                    g.MemUtil = util.Memory;
                }
                if (Native.nvmlDeviceGetTemperature(device, Native.NVML_TEMPERATURE_GPU, out u) == 0) g.Temp = u;
                if (Native.nvmlDeviceGetClockInfo(device, Native.NVML_CLOCK_GRAPHICS, out u) == 0) g.ClockCore = u;
                if (Native.nvmlDeviceGetClockInfo(device, Native.NVML_CLOCK_MEM, out u) == 0) g.ClockMem = u;
                if (Native.nvmlDeviceGetPowerUsage(device, out u) == 0) g.Power = u / 1000.0;
                if (Native.nvmlDeviceGetEnforcedPowerLimit(device, out u) == 0) g.PowerLimit = u / 1000.0;
                if (Native.nvmlDeviceGetFanSpeed(device, out u) == 0) g.Fan = u;
                if (Native.nvmlDeviceGetMemoryInfo(device, out mem) == 0)
                {
                    g.MemTotal = mem.Total;
                    g.MemUsed = mem.Used;
                }
            }
            catch (Exception ex)
            {
                broken = true;
                Log.Write("nvml read: " + ex.GetType().Name + " " + ex.Message + " (gpu stats off)");
            }
            return g;
        }

        public void Dispose()
        {
            try { if (ready) Native.nvmlShutdown(); }
            catch { }
            ready = false;
            device = IntPtr.Zero;
        }
    }

    public class GpuSample
    {
        public string Name = "";
        public double Util;
        public double MemUtil;
        public double Temp;
        public double ClockCore;
        public double ClockMem;
        public double Power;
        public double PowerLimit;
        public double Fan;
        public ulong MemUsed;
        public ulong MemTotal;
    }
}