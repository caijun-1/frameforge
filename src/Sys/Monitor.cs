using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Text;
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

        // 最近检测到的游戏:切出游戏去看数据时仍算"有游戏",否则界面会立刻变回
        // "未检测到游戏",帧率跟踪也会被停掉,看起来就像没识别到。
        public int GamePid = -1;
        public string GameName = "";
        public bool GameRecent;

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

        private int lastGamePid = -1;
        private string lastGameName = "";
        private int lastLoggedPid = -2;
        private int lastLoggedGamePid = -2;

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
            int fgPid = 0;
            Native.GetWindowThreadProcessId(hwnd, out fgPid);
            s.ForegroundPid = fgPid;
            s.ForegroundName = Native.ProcessName(fgPid);

            bool fgFull = IsGameWindow(hwnd, fgPid);
            int gamePid = fgFull ? fgPid : -1;

            // 游戏加加那类工具只认前台全屏窗口,一按 Alt+Tab 就变成"未检测到游戏"。
            // 这里再枚举一遍所有顶层窗口:游戏的无边框全屏窗口在后台仍然铺满屏幕,
            // 只要它还在就继续算"有游戏",玩家切出来看帧率时状态不会跳。
            if (gamePid <= 0)
            {
                int found;
                if (FindFullscreenWindow(out found)) gamePid = found;
            }
            s.Fullscreen = gamePid > 0;

            // 换前台窗口时记一行,识别不出来时能直接看到窗口类名/尺寸/屏幕尺寸。
            if (fgPid != lastLoggedPid)
            {
                lastLoggedPid = fgPid;
                Native.RECT fr;
                string rectText = "";
                if (Native.GetWindowRect(hwnd, out fr))
                    rectText = (fr.Right - fr.Left) + "x" + (fr.Bottom - fr.Top);
                var cls = new StringBuilder(256);
                try { Native.GetClassName(hwnd, cls, cls.Capacity); } catch { }
                Rectangle sb = Screen.FromHandle(hwnd).Bounds;
                Log.Write("fg pid=" + fgPid + " name=" + s.ForegroundName + " class=" + cls
                    + " rect=" + rectText + " screen=" + sb.Width + "x" + sb.Height
                    + " game=" + (s.Fullscreen ? "1" : "0"));
            }

            if (gamePid > 0)
            {
                string name = Native.ProcessName(gamePid);
                if (name.Length == 0 && gamePid == lastGamePid) name = lastGameName;
                lastGamePid = gamePid;
                lastGameName = name;
            }
            else if (lastGamePid > 0 && !ProcessAlive(lastGamePid))
            {
                // 游戏进程退出才清掉。以前是 20 秒宽限期,切出去看数据稍久一点
                // 就会跳回"未检测到游戏",连帧率跟踪也一起停。
                lastGamePid = -1;
                lastGameName = "";
            }

            if (lastGamePid != lastLoggedGamePid)
            {
                lastLoggedGamePid = lastGamePid;
                if (lastGamePid > 0) Log.Write("game pid=" + lastGamePid + " name=" + lastGameName);
            }

            if (lastGamePid > 0)
            {
                s.GameRecent = true;
                s.GamePid = lastGamePid;
                s.GameName = lastGameName;
            }

            return s;
        }

        private static bool ProcessAlive(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch { return false; }
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

        /// <summary>
        /// 枚举所有顶层窗口,找出铺满某个显示器的窗口(游戏在后台时也能认出来)。
        /// 排除最小化、被 DWM 隐藏的 UWP 窗口,以及浏览器/播放器这类经常全屏但不是游戏的应用。
        /// </summary>
        public static bool FindFullscreenWindow(out int pid)
        {
            int found = -1;
            try
            {
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    int p;
                    Native.GetWindowThreadProcessId(h, out p);
                    if (p <= 4) return true;
                    if (!Native.IsWindowVisible(h)) return true;
                    if (Native.IsIconic(h)) return true;

                    int cloaked;
                    if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;

                    if (!IsGameWindow(h, p)) return true;
                    found = p;
                    return false;   // 找到一个就够了
                }, IntPtr.Zero);
            }
            catch { }
            pid = found;
            return found > 0;
        }

        /// <summary>常见"会全屏但不是游戏"的应用:浏览器按 F11、播放器全屏都不该被当成游戏。</summary>
        private static bool IsNonGameName(string name)
        {
            if (name.Length == 0) return false;
            switch (name.ToLowerInvariant())
            {
                case "explorer":
                case "frameforge":
                case "taskmgr":
                case "textinputhost":
                case "chrome":
                case "msedge":
                case "firefox":
                case "opera":
                case "brave":
                case "vlc":
                case "mpv":
                case "potplayermini":
                case "potplayermini64":
                case "wmplayer":
                case "video.ui":
                case "photos":
                case "powerpnt":
                case "wpspresentation":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 前台窗口铺满其所在显示器即视为游戏(含无边框全屏)。
        /// 只看窗口尺寸是否覆盖显示器:不少游戏的无边框全屏窗口仍带 WS_CAPTION 样式却看不到
        /// 标题栏,以前按窗口样式排除,会把这类游戏误判成普通窗口(界面显示"未检测到游戏")。
        /// </summary>
        public static bool IsGameWindow(IntPtr hwnd, int pid)
        {
            if (hwnd == IntPtr.Zero || pid <= 4) return false;
            try
            {
                // 名字取不到时不能直接判否:管理员运行、带反作弊的游戏往往打不开进程,
                // 那样就会出现"明明开着全屏游戏却显示未检测到"。
                string name = Native.ProcessName(pid);
                if (IsNonGameName(name)) return false;
                if (!Native.IsWindowVisible(hwnd)) return false;

                Native.RECT r;
                if (!Native.GetWindowRect(hwnd, out r)) return false;
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return false;

                Rectangle b = Screen.FromHandle(hwnd).Bounds;
                // 允许 8px 误差:不同 DPI 下窗口矩形和显示器边界会差一两个像素,
                // 部分窗口还带一圈不可见的缩放边框。
                if (w < b.Width - 8 || h < b.Height - 8) return false;
                return (long)w * h >= (long)b.Width * b.Height * 95 / 100;
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