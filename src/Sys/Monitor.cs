using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows.Forms;

namespace FrameForge.Sys
{
    /// <summary>一次系统采样。取不到的字段保持 -1,界面显示 — 而不是猜。</summary>
    public class SysSample
    {
        public double Cpu = -1;
        public double Gpu = -1;
        public double GpuTemp = -1;
        public double GpuClock = -1;
        public double GpuPower = -1;
        public double GpuPowerLimit = -1;
        public ulong VramUsed;
        public ulong VramTotal;

        public ulong RamUsed;
        public ulong RamTotal;
        public double RamPct = -1;

        public double NetDown;
        public double NetUp;

        public int ForegroundPid = -1;
        public string ForegroundName = "";
        public bool Fullscreen;

        public string GpuName = "";
        public bool GpuFromNvml;
    }

    /// <summary>每秒采样一次 CPU / GPU / 内存 / 网速 / 前台应用。单线程,开销极低。</summary>
    public sealed class Monitor : IDisposable
    {
        private readonly PdhCounter cpuUtil;
        private readonly PdhCounter cpuUtility;
        private readonly PdhCounter gpuEngine;
        private readonly Nvml nvml;
        private readonly List<NetworkInterface> nics = new List<NetworkInterface>();
        private Thread thread;
        private volatile bool running;
        private long lastIdle, lastKernel, lastUser;
        private long lastRx, lastTx;
        private DateTime lastNetAt = DateTime.MinValue;
        private bool firstNet = true;

        public volatile SysSample Current = new SysSample();
        public event Action<SysSample> Updated;

        public Monitor()
        {
            cpuUtil = new PdhCounter(@"\Processor Information(_Total)\% Processor Time");
            cpuUtility = new PdhCounter(@"\Processor Information(_Total)\% Processor Utility");
            gpuEngine = new PdhCounter(@"\GPU Engine(*)\Utilization Percentage");
            nvml = new Nvml();
            nvml.Init();
            CacheNics();
            Native.GetSystemTimes(out lastIdle, out lastKernel, out lastUser);
        }

        public string GpuName { get { return nvml.Available ? nvml.DeviceName : ""; } }

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "FrameForge.Monitor";
            thread.Priority = ThreadPriority.BelowNormal;
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            Thread t = thread;
            thread = null;
            if (t != null)
            {
                try { t.Join(1200); }
                catch { }
            }
        }

        private void Loop()
        {
            while (running)
            {
                try { Current = Sample(); }
                catch (Exception ex) { Log.Write("monitor: " + ex.Message); }
                Action<SysSample> cb = Updated;
                if (cb != null) { try { cb(Current); } catch { } }
                for (int i = 0; i < 10 && running; i++) Thread.Sleep(100);
            }
        }

        private SysSample Sample()
        {
            var s = new SysSample();

            // ---- CPU ----
            if (cpuUtility.Valid)
            {
                cpuUtility.Collect();
                double v = cpuUtility.Read();
                if (v >= 0) s.Cpu = Clamp(v, 0, 100);
            }
            if (s.Cpu < 0 && cpuUtil.Valid)
            {
                cpuUtil.Collect();
                double v = cpuUtil.Read();
                if (v >= 0) s.Cpu = Clamp(v, 0, 100);
            }
            if (s.Cpu < 0) s.Cpu = CpuByTimes();

            // ---- GPU ----
            GpuSample g = nvml.Read();
            if (nvml.Available)
            {
                s.GpuFromNvml = true;
                s.GpuName = g.Name;
                s.Gpu = g.Util;
                s.GpuTemp = g.Temp;
                s.GpuClock = g.ClockCore;
                s.GpuPower = g.Power;
                s.GpuPowerLimit = g.PowerLimit;
                s.VramUsed = g.MemUsed;
                s.VramTotal = g.MemTotal;
            }
            else
            {
                s.GpuName = "GPU";
                if (gpuEngine.Valid)
                {
                    gpuEngine.Collect();
                    double[] vals = gpuEngine.ReadArray();
                    double sum = 0;
                    for (int i = 0; i < vals.Length; i++) if (vals[i] > 0) sum += vals[i];
                    if (sum > 0) s.Gpu = Clamp(sum, 0, 100);
                }
            }

            // ---- 内存 ----
            var ms = new Native.MEMORYSTATUSEX();
            ms.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (Native.GlobalMemoryStatusEx(ref ms))
            {
                s.RamTotal = ms.ullTotalPhys;
                s.RamUsed = ms.ullTotalPhys > ms.ullAvailPhys ? ms.ullTotalPhys - ms.ullAvailPhys : 0;
                s.RamPct = ms.dwMemoryLoad;
            }

            // ---- 网络 ----
            ReadNet(s);

            // ---- 前台窗口 ----
            IntPtr hwnd = Native.GetForegroundWindow();
            int pid = 0;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            s.ForegroundPid = pid;
            s.ForegroundName = Native.ProcessName(pid);
            s.Fullscreen = IsFullscreen(hwnd, pid);

            return s;
        }

        private double CpuByTimes()
        {
            long idle, kernel, user;
            Native.GetSystemTimes(out idle, out kernel, out user);
            long dIdle = idle - lastIdle;
            long dKernel = kernel - lastKernel;
            long dUser = user - lastUser;
            lastIdle = idle; lastKernel = kernel; lastUser = user;
            long total = dKernel + dUser;
            if (total <= 0) return -1;
            double busy = (total - dIdle) * 100.0 / total;
            return Clamp(busy, 0, 100);
        }

        private void CacheNics()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    nics.Add(ni);
                }
            }
            catch { }
        }

        private void ReadNet(SysSample s)
        {
            try
            {
                long rx = 0, tx = 0;
                foreach (NetworkInterface ni in nics)
                {
                    try
                    {
                        IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                        rx += st.BytesReceived;
                        tx += st.BytesSent;
                    }
                    catch { }
                }
                DateTime now = DateTime.UtcNow;
                if (!firstNet && lastNetAt != DateTime.MinValue)
                {
                    double sec = (now - lastNetAt).TotalSeconds;
                    if (sec > 0.2)
                    {
                        s.NetDown = Math.Max(0, (rx - lastRx) / sec);
                        s.NetUp = Math.Max(0, (tx - lastTx) / sec);
                    }
                }
                lastRx = rx; lastTx = tx; lastNetAt = now; firstNet = false;
            }
            catch { }
        }

        /// <summary>前台窗口铺满其所在显示器即视为全屏(含无边框全屏)。</summary>
        private static bool IsFullscreen(IntPtr hwnd, int pid)
        {
            if (hwnd == IntPtr.Zero || pid <= 4) return false;
            try
            {
                string name = Native.ProcessName(pid);
                if (name.Length == 0) return false;
                if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return false;
                if (name.Equals("FrameForge", StringComparison.OrdinalIgnoreCase)) return false;
                if (name.Equals("Taskmgr", StringComparison.OrdinalIgnoreCase)) return false;
                if ((Native.GetWindowLong(hwnd, Native.GWL_STYLE) & Native.WS_CAPTION) != 0) return false;

                Native.RECT r;
                if (!Native.GetWindowRect(hwnd, out r)) return false;
                Screen sc = Screen.FromHandle(hwnd);
                Rectangle b = sc.Bounds;
                return Math.Abs(r.Left - b.Left) <= 2 && Math.Abs(r.Top - b.Top) <= 2
                    && Math.Abs(r.Right - b.Right) <= 2 && Math.Abs(r.Bottom - b.Bottom) <= 2;
            }
            catch { return false; }
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        public void Dispose()
        {
            Stop();
            cpuUtil.Dispose();
            cpuUtility.Dispose();
            gpuEngine.Dispose();
            nvml.Dispose();
        }
    }
}