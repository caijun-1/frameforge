using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Management;
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
        public double CpuTemp = -1;   // 主板 ACPI 热区温度(WMI),取不到保持 -1
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
        public string GameName = "";       // 进程名,带 .exe 去掉扩展名
        public string GameTitle = "";      // 识别出来的游戏名(中文/英文)
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
        private DateTime lastScan = DateTime.MinValue;

        // CPU 温度只能走 WMI(ACPI 热区),一次查询要几十毫秒,没必要每秒都查。
        // 3 秒缓存一次,查不到就一直是 -1,界面显示 -- 而不是编一个数字。
        private ManagementObjectSearcher thermal;
        private double lastCpuTemp = -1;
        private DateTime lastCpuTempAt = DateTime.MinValue;

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

            // ---- CPU 温度 ----
            s.CpuTemp = ReadCpuTemp();

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

            // 最后一层兜底:窗口化、小窗口、窗口尺寸读不到的游戏都够不到上面的全屏判定。
            // 直接扫进程,只要进程是内置库里认识的游戏,且有可见窗口,就算"检测到游戏"。
            // 已经在跟踪的游戏还活着就不用扫,省开销。
            if (gamePid <= 0 && lastGamePid <= 0 && (DateTime.UtcNow - lastScan).TotalMilliseconds > 1500)
            {
                lastScan = DateTime.UtcNow;
                int known;
                if (FindKnownGame(out known)) gamePid = known;
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
                // 已经跟踪着一个还活着的游戏时,不要因为读不到新进程的名字就换目标:
                // 反作弊进程、系统外壳都可能读不到名字,切屏时就会把跟踪对象切走,
                // 之后自动加速便转去加速别的东西。
                if (name.Length == 0 && lastGamePid > 0 && gamePid != lastGamePid && ProcessAlive(lastGamePid))
                {
                    gamePid = lastGamePid;
                    name = lastGameName;
                }
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
                s.GameTitle = GameDb.Title(lastGamePid, lastGameName);
            }

            return s;
        }

        private static bool ProcessAlive(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid)) return true;
            }
            catch (ArgumentException) { return false; }  // 进程真的不在了
            catch { return true; }                        // 权限不足,不能判定已退出
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

        private static int lastQuns = -1;

        /// <summary>
        /// 当前是否有 D3D 应用处于独占全屏。独占全屏的交换链不经过 DWM,
        /// 任何分层置顶窗口都会被挡在游戏后面——这不是叠加层坏了,是系统限制。
        /// </summary>
        public static bool IsExclusiveFullscreen(int gamePid)
        {
            try
            {
                int state;
                if (Native.SHQueryUserNotificationState(out state) != 0) return false;
                // 只在状态变化时记一行:下次再遇到「全屏不显示」能直接看到系统给的判定值,
                // 不用猜是检测漏了还是叠加层真被独占全屏挡住。
                if (state != lastQuns)
                {
                    lastQuns = state;
                    Log.Write("quns state=" + state + " game pid=" + gamePid);
                }
                if (state == Native.QUNS_RUNNING_D3D_FULL_SCREEN) return true;
                // 有些驱动/系统版本不返回 3,只报「有全屏程序在跑」。单看 2 会把
                // 演示模式也算进来,所以再用这个游戏自己的窗口铺满显示器确认一次。
                if (state == Native.QUNS_BUSY) return gamePid > 0 && CoversMonitor(gamePid);
                return false;
            }
            catch { return false; }
        }

        /// <summary>该进程有没有一个正好铺满显示器的可见窗口(无边框和独占全屏都满足)。</summary>
        private static bool CoversMonitor(int pid)
        {
            bool cover = false;
            try
            {
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    int p;
                    Native.GetWindowThreadProcessId(h, out p);
                    if (p != pid) return true;
                    if (!Native.IsWindowVisible(h)) return true;
                    if (Native.IsIconic(h)) return true;

                    int cloaked;
                    if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;

                    Native.RECT r;
                    if (!Native.GetWindowRect(h, out r)) return true;
                    Rectangle b = Screen.FromHandle(h).Bounds;
                    if (r.Left <= b.Left + 1 && r.Top <= b.Top + 1
                        && r.Right >= b.Right - 1 && r.Bottom >= b.Bottom - 1)
                        cover = true;
                    return !cover;
                }, IntPtr.Zero);
            }
            catch { }
            return cover;
        }

        /// <summary>
        /// 最后一层兜底:直接扫进程。只要进程是内置库里认识的游戏(或 Java 版我的世界),
        /// 并且有可见、未最小化的窗口,就认为"有游戏在跑"。
        /// 窗口化、小窗口、无边框但尺寸读不到的情况都能靠这一层认出来。
        /// </summary>
        public static bool FindKnownGame(out int pid)
        {
            pid = -1;
            Process[] list = null;
            try { list = Process.GetProcesses(); }
            catch { return false; }
            if (list == null) return false;

            try
            {
                for (int i = 0; i < list.Length; i++)
                {
                    Process p = list[i];
                    try
                    {
                        if (p.Id <= 4) continue;
                        string name = p.ProcessName;
                        if (name.Length == 0) continue;
                        // 启动器不是游戏本体:我的世界启动器、各种 game launcher 都要跳过。
                        if (name.IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (IsNonGameName(name)) continue;

                        IntPtr h = p.MainWindowHandle;
                        if (h == IntPtr.Zero) continue;
                        if (!Native.IsWindowVisible(h)) continue;
                        if (Native.IsIconic(h)) continue;

                        // 通用引擎进程名(Client-Win64-Shipping 之类)不认,否则会把
                        // 任何 Unreal 程序当成游戏;Java 进程靠窗口标题判断;
                        // 内置库没收录的新游戏,装在 Steam/Epic 等游戏目录下也认。
                        bool game = GameDb.Known(name);
                        if (!game && (string.Equals(name, "java", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, "javaw", StringComparison.OrdinalIgnoreCase)))
                            game = GameDb.JavaGame(p.Id);
                        if (!game) game = GameDb.LibraryGame(p.Id);
                        if (!game) continue;

                        pid = p.Id;
                        return true;
                    }
                    catch { }
                }
            }
            finally
            {
                for (int i = 0; i < list.Length; i++)
                {
                    try { list[i].Dispose(); } catch { }
                }
            }
            return false;
        }

        /// <summary>按进程名找正在运行的游戏,优先返回有可见窗口的那个;找不到返回 -1。</summary>
        public static int FindProcessByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            Process[] list = null;
            try { list = Process.GetProcesses(); }
            catch { return -1; }
            if (list == null) return -1;

            int first = -1;
            try
            {
                for (int i = 0; i < list.Length; i++)
                {
                    Process p = list[i];
                    try
                    {
                        if (p.Id <= 4) continue;
                        if (!string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) continue;
                        if (first < 0) first = p.Id;
                        if (p.MainWindowHandle != IntPtr.Zero) return p.Id;
                    }
                    catch { }
                }
            }
            finally
            {
                for (int i = 0; i < list.Length; i++)
                {
                    try { list[i].Dispose(); } catch { }
                }
            }
            return first;
        }

        /// <summary>
        /// 截图 / 标注类工具。这份名单同时用于两件事:
        /// 1) 不把它们的全屏取色窗口当成游戏;2) 截图进行时临时让出滤镜和叠加层。
        /// 两个功能共用一份数据,以后加工具只改这里。
        /// </summary>
        private static readonly string[] CaptureTools =
        {
            "snipaste", "sharex", "greenshot", "lightshot", "picpick", "pixpin",
            "snagit", "snagit32", "snagit64", "screenpresso", "hypersnap", "fscapture",
            "flameshot", "jietu", "xnip", "gyazo",
            // Windows 自带截图:Win10 截图工具 / Win11 截图工具 / Win+Shift+S 的取色层
            "snippingtool", "screensketch", "screenclip", "screenclippinghost"
        };

        // 全窗口扫描的结果与时间:前台没变时直接复用,避免每 150ms 扫一遍所有窗口。
        private static DateTime captureScanTime = DateTime.MinValue;
        private static bool captureScanHit;

        public static bool IsCaptureTool(string name)
        {
            if (name.Length == 0) return false;
            string n = name.ToLowerInvariant();
            for (int i = 0; i < CaptureTools.Length; i++)
                if (CaptureTools[i] == n) return true;
            return false;
        }

        /// <summary>
        /// 截图工具是否正在截屏。多数工具会铺一层全屏取色窗口,但它常常刻意不抢前台
        /// (靠全局键盘钩子收按键),所以不能只看前台进程,还要扫一遍铺满屏幕的顶层窗口。
        /// 先比窗口尺寸再取进程名:全屏窗口通常只有一两个,省掉大量 OpenProcess。
        /// </summary>
        public static bool CaptureActive()
        {
            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                int fgPid;
                Native.GetWindowThreadProcessId(fg, out fgPid);
                // 快路径:Win+Shift+S 一按下,取色层会立刻抢到前台,这一步是毫秒级。
                if (fgPid > 4 && IsCaptureTool(Native.ProcessName(fgPid))) return true;

                // 慢路径(全窗口扫描)限流到每秒一次:前台没变就不用反复扫,
                // 于是守卫轮询可以提到 150ms,按下热键第一时间就能让开。
                DateTime now = DateTime.UtcNow;
                if ((now - captureScanTime).TotalMilliseconds < 1000) return captureScanHit;
                captureScanTime = now;
                captureScanHit = ScanFullscreenCapture();
                return captureScanHit;
            }
            catch { return false; }
        }

        /// <summary>枚举所有顶层窗口,找铺满整屏的截图取色层。</summary>
        private static bool ScanFullscreenCapture()
        {
            try
            {
                IntPtr hit = IntPtr.Zero;
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                    Native.RECT r;
                    if (!Native.GetWindowRect(h, out r)) return true;
                    int w = r.Right - r.Left;
                    int hh = r.Bottom - r.Top;
                    if (w <= 0 || hh <= 0) return true;

                    Rectangle b = Screen.FromHandle(h).Bounds;
                    // 截图的取色层一定铺满整块屏;小于 85% 的窗口(托盘气泡、粘贴小窗)直接跳过。
                    if (w < b.Width * 85 / 100 || hh < b.Height * 85 / 100) return true;

                    int pid;
                    Native.GetWindowThreadProcessId(h, out pid);
                    if (pid <= 4 || !IsCaptureTool(Native.ProcessName(pid))) return true;
                    hit = h;
                    return false;
                }, IntPtr.Zero);
                return hit != IntPtr.Zero;
            }
            catch { return false; }
        }

        /// <summary>常见"会全屏但不是游戏"的应用:浏览器按 F11、播放器全屏都不该被当成游戏。</summary>
        private static bool IsNonGameName(string name)
        {
            if (name.Length == 0) return false;
            if (IsCaptureTool(name)) return true;

            // 触摸键盘 / 手写面板 / 输入法外壳:Win11 在桌面切屏时会把它们铺满屏幕,
            // 以前会被当成游戏,自动加速就转而去加速 TabTip/InputApp。
            string lower = name.ToLowerInvariant();
            if (lower.IndexOf("textinput") >= 0 || lower.IndexOf("tabtip") >= 0
                || lower.IndexOf("inputapp") >= 0 || lower.IndexOf("handwriting") >= 0
                || lower.IndexOf("composableshell") >= 0
                || lower == "osk" || lower == "ctfmon" || lower == "msctf")
                return true;

            switch (lower)
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
                case "iexplore":
                case "vlc":
                case "mpv":
                case "potplayermini":
                case "potplayermini64":
                case "potplayer":
                case "wmplayer":
                case "video.ui":
                case "photos":
                case "powerpnt":
                case "wpspresentation":
                case "wpp":
                case "winword":
                case "excel":
                case "wps":
                case "acrobat":
                case "acrord32":
                // 聊天 / 会议 / 直播:全屏时窗口同样铺满屏幕,但显然不是游戏
                case "wechat":
                case "weixin":
                case "qq":
                case "tim":
                case "wemeetapp":
                case "dingtalk":
                case "feishu":
                case "lark":
                case "teams":
                case "ms-teams":
                case "zoom":
                case "skype":
                case "telegram":
                case "whatsapp":
                case "discord":
                case "thunder":
                case "iqiyi":
                case "qyclient":
                case "qqlive":
                case "youku":
                case "bilibili":
                case "douyu":
                case "huya":
                case "cloudmusic":
                case "qqmusic":
                case "kugou":
                case "spotify":
                // 录制 / 剪辑
                case "obs":
                case "obs64":
                case "obs32":
                case "camtasia":
                case "bandicam":
                // 游戏平台 / 启动器:商店界面一全屏就会被当成游戏,但真正在跑的游戏是它另外拉起的进程
                case "wegame":
                case "wegamelauncher":
                case "tgp":
                case "tencentdl":
                case "steam":
                case "steamwebhelper":
                case "epicgameslauncher":
                case "battle.net":
                case "blizzardbrowser":
                case "galaxyclient":
                case "ubisoftconnect":
                case "upc":
                case "eadesktop":
                case "origin":
                case "goggalaxy":
                case "xboxpcapp":
                case "riotclientservices":
                case "riotclientux":
                // 截图 / 标注类不在这里列:CaptureTools 已经统一处理,见 IsCaptureTool。
                // 系统外壳
                case "applicationframehost":
                case "searchapp":
                case "startmenuexperiencehost":
                case "shellexperiencehost":
                case "lockapp":
                case "systemsettings":
                case "gamebar":
                case "nvidia share":
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
                long area = (long)w * h;
                long screen = (long)b.Width * b.Height;
                if (w >= b.Width - 8 && h >= b.Height - 8 && area >= screen * 95 / 100) return true;

                // 我的世界这类常以「最大化窗口」跑的游戏够不到 95%,
                // 为了不漏检,对明确认得出的游戏放宽到 88%(普通软件仍然不算)。
                if (area < screen * 88 / 100) return false;
                return GameDb.Known(name) || GameDb.JavaGame(pid) || GameDb.LibraryGame(pid);
            }
            catch { return false; }
        }

        /// <summary>
        /// CPU 温度:WMI 的 MSAcpi_ThermalZoneTemperature 以"开尔文×10"返回。
        /// 优先取名里带 CPU/Processor 的热区,没有就取其他热区里最高的一个。
        /// 主板不暴露 ACPI 热区(部分台式机/虚拟机)就返回 -1,界面显示 --。
        /// </summary>
        private double ReadCpuTemp()
        {
            if (lastCpuTempAt != DateTime.MinValue && (DateTime.Now - lastCpuTempAt).TotalSeconds < 3)
                return lastCpuTemp;
            lastCpuTempAt = DateTime.Now;
            try
            {
                if (thermal == null)
                    thermal = new ManagementObjectSearcher("root\\WMI",
                        "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");

                double best = -1, other = -1;
                foreach (ManagementBaseObject o in thermal.Get())
                {
                    try
                    {
                        object raw = o["CurrentTemperature"];
                        if (raw == null) continue;
                        double c = Convert.ToDouble(raw, CultureInfo.InvariantCulture) / 10.0 - 273.15;
                        if (c <= 0 || c >= 150) continue;
                        string name = o["InstanceName"] as string;
                        name = name == null ? "" : name.ToLowerInvariant();
                        if (name.IndexOf("cpu") >= 0 || name.IndexOf("proc") >= 0)
                        {
                            if (c > best) best = c;
                        }
                        else if (c > other) other = c;
                    }
                    finally { o.Dispose(); }
                }
                lastCpuTemp = best >= 0 ? best : other;
            }
            catch { lastCpuTemp = -1; }
            return lastCpuTemp;
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
            try { if (thermal != null) thermal.Dispose(); } catch { }
        }
    }
}