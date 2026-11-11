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
        public IntPtr GameHwnd = IntPtr.Zero; // 游戏主窗口:叠加层靠它定位到游戏上面
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
        private IntPtr lastGameHwnd = IntPtr.Zero;
        private int lastLoggedPid = -2;
        private int lastLoggedGamePid = -2;
        private int lastForegroundPid = -1;
        private DateTime lastScan = DateTime.MinValue;

        // 每个进程的 GPU 3D 引擎占用(来自 PDH 的 GPU Engine 计数器实例名),
        // 是识别"没见过的窗口化游戏"最硬的一条证据。
        private static Dictionary<int, double> gpuPidUsage = new Dictionary<int, double>();
        // 识别成功过的游戏进程。加载/菜单/切窗口时 GPU 和窗口特征会短暂变弱,
        // 记住它能避免状态来回跳。
        private static readonly Dictionary<int, DateTime> confirmedGames = new Dictionary<int, DateTime>();
        private const int GameScore = 50;

        // 用户手动指定的判定:pin 命中的进程无条件算游戏,block 命中的永远不算。
        // 自动识别终究是启发式的,这两个名单让用户能一锤定音。
        private static string pinnedGame = "";
        private static readonly HashSet<string> blockedGames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> knownGames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object manualLock = new object();

        /// <summary>同步「锁定游戏」与「不是游戏」两个名单,进程名大小写不敏感、可带 .exe。</summary>
        public static void SetManualTargets(string pin, string[] blocked)
        {
            string p = CleanProcName(pin);
            lock (manualLock)
            {
                pinnedGame = p;
                blockedGames.Clear();
                if (blocked != null)
                {
                    for (int i = 0; i < blocked.Length; i++)
                    {
                        string b = CleanProcName(blocked[i]);
                        if (b.Length > 0) blockedGames.Add(b);
                    }
                }
            }
        }

        /// <summary>同步「我的游戏」名单:扫描和手动添加的游戏都按游戏处理,不再靠启发式猜。</summary>
        public static void SetMyGames(string[] names)
        {
            lock (manualLock)
            {
                knownGames.Clear();
                if (names != null)
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        string n = CleanProcName(names[i]);
                        if (n.Length > 0) knownGames.Add(n);
                    }
                }
            }
        }

        private static bool IsKnownGameName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (manualLock) return knownGames.Contains(name);
        }

        private static string CleanProcName(string name)
        {
            string s = (name ?? "").Trim().ToLowerInvariant();
            if (s.EndsWith(".exe")) s = s.Substring(0, s.Length - 4);
            return s;
        }

        private static bool IsPinnedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (manualLock) return pinnedGame.Length > 0 && pinnedGame == name.ToLowerInvariant();
        }

        private static bool IsBlockedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (manualLock) return blockedGames.Contains(name);
        }

        /// <summary>手动标记/排除后立刻重扫,不必等下一个 1.5 秒周期。</summary>
        public void Rescan()
        {
            lastScan = DateTime.MinValue;
            lastForegroundPid = -1;
        }

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
            }

            // GPU Engine 计数器带 pid,即使 NVML 可用也要读:游戏识别用得到。
            if (gpuEngine.Valid)
            {
                gpuEngine.Collect();
                string[] names;
                double[] vals;
                gpuEngine.ReadNamed(out names, out vals);
                UpdateGpuUsage(names, vals);
                if (!nvml.Available)
                {
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

            // ---- 游戏识别 ----
            // 不再只看前台窗口:每 1.5 秒全量扫一遍顶层窗口,前台一变也立刻单独评估。
            // 游戏切分辨率、无边框/独占切换、启动器退场都会换窗口句柄,只盯前台必然漏。
            DateTime now = DateTime.UtcNow;
            int gamePid = -1;
            IntPtr gameHwnd = IntPtr.Zero;
            bool scanned = (now - lastScan).TotalMilliseconds >= 1500;
            if (scanned)
            {
                lastScan = now;
                PruneConfirmed();
                FindBestGameWindow(out gamePid, out gameHwnd);
            }
            else if (fgPid > 4 && fgPid != lastForegroundPid)
            {
                WindowInfo wi;
                if (InspectWindow(hwnd, fgPid, out wi) && ScoreWindow(ref wi) >= GameScore)
                {
                    gamePid = fgPid;
                    gameHwnd = hwnd;
                }
            }
            lastForegroundPid = fgPid;

            // 已经识别到的游戏只要进程还在、窗口还能找到就继续算有游戏。
            // 加载画面/切菜单时 GPU 占用会掉,全扫也可能一时没有窗口达标,
            // 这里让状态稳住,自动加速和帧率跟踪不会反复启停。
            if (gamePid <= 0 && lastGamePid > 0 && ProcessAlive(lastGamePid))
            {
                IntPtr h2 = (lastGameHwnd != IntPtr.Zero && Native.IsWindow(lastGameHwnd))
                    ? lastGameHwnd : FindWindowForPid(lastGamePid);
                if (h2 != IntPtr.Zero || GpuPid3D(lastGamePid) >= 1.0)
                {
                    gamePid = lastGamePid;
                    gameHwnd = h2;
                }
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
                    gameHwnd = lastGameHwnd;
                }
                if (name.Length == 0 && gamePid == lastGamePid) name = lastGameName;
                lastGamePid = gamePid;
                lastGameName = name;
                if (gameHwnd != IntPtr.Zero) lastGameHwnd = gameHwnd;
                lock (confirmedGames) { confirmedGames[gamePid] = DateTime.UtcNow; }
            }
            else if (lastGamePid > 0 && !ProcessAlive(lastGamePid))
            {
                // 游戏进程退出才清掉。以前是 20 秒宽限期,切出去看数据稍久一点
                // 就会跳回"未检测到游戏",连帧率跟踪也一起停。
                lock (confirmedGames) { confirmedGames.Remove(lastGamePid); }
                lastGamePid = -1;
                lastGameName = "";
                lastGameHwnd = IntPtr.Zero;
            }

            if (lastGamePid != lastLoggedGamePid)
            {
                lastLoggedGamePid = lastGamePid;
                if (lastGamePid > 0)
                    Log.Write("game pid=" + lastGamePid + " name=" + lastGameName
                        + " hwnd=0x" + lastGameHwnd.ToInt64().ToString("X"));
            }

            if (lastGamePid > 0)
            {
                s.GameRecent = true;
                s.GamePid = lastGamePid;
                s.GameHwnd = lastGameHwnd;
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

        /// <summary>清掉已经退出或确认超过 6 小时的记录,避免缓存无限增长。</summary>
        private static void PruneConfirmed()
        {
            List<int> dead = null;
            lock (confirmedGames)
            {
                foreach (KeyValuePair<int, DateTime> kv in confirmedGames)
                {
                    if ((DateTime.UtcNow - kv.Value).TotalHours > 6 || !ProcessAlive(kv.Key))
                    {
                        if (dead == null) dead = new List<int>();
                        dead.Add(kv.Key);
                    }
                }
                if (dead != null)
                    for (int i = 0; i < dead.Count; i++) confirmedGames.Remove(dead[i]);
            }
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

        /// <summary>一个候选窗口的全部特征。评分制比"命中一个条件就返回 true"可靠得多。</summary>
        private struct WindowInfo
        {
            public IntPtr Hwnd;
            public int Pid;
            public string Name;
            public string Class;
            public string Title;
            public int Width;
            public int Height;
            public bool Covers;
            public bool Foreground;
            /// <summary>用户手动锁定为游戏:跳过启发式判定,直接算游戏。</summary>
            public bool Pinned;
            /// <summary>在「我的游戏」名单里:同样跳过名字/标题过滤。</summary>
            public bool Known;
        }

        /// <summary>
        /// 枚举所有顶层窗口,逐个打分取最高分,过线才算游戏。
        /// 分层/穿透/工具/不激活/有属主/被 DWM 隐藏/最小化/过小的窗口直接淘汰:
        /// 桌宠、虚拟键盘、加速器悬浮窗、截图取色层、叠加层本身都在这一步被挡掉。
        /// </summary>
        private static bool FindBestGameWindow(out int pid, out IntPtr hwnd)
        {
            int bestPid = -1;
            IntPtr bestHwnd = IntPtr.Zero;
            int bestScore = 0;
            try
            {
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    int p;
                    Native.GetWindowThreadProcessId(h, out p);
                    if (p <= 4) return true;
                    WindowInfo wi;
                    if (!InspectWindow(h, p, out wi)) return true;
                    int score = ScoreWindow(ref wi);
                    if (score >= GameScore && score > bestScore)
                    {
                        bestScore = score;
                        bestPid = p;
                        bestHwnd = h;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            pid = bestPid;
            hwnd = bestHwnd;
            return bestPid > 0;
        }

        /// <summary>保留旧接口:找到任意一个过线的游戏窗口即返回。</summary>
        public static bool FindFullscreenWindow(out int pid)
        {
            IntPtr h;
            if (FindBestGameWindow(out pid, out h)) return true;
            pid = -1;
            return false;
        }

        /// <summary>窗口特征筛选。返回 false 表示这个窗口连候选资格都没有。</summary>
        private static bool InspectWindow(IntPtr h, int p, out WindowInfo wi)
        {
            wi = new WindowInfo();
            wi.Hwnd = h;
            wi.Pid = p;
            if (!Native.IsWindowVisible(h)) return false;
            if (Native.IsIconic(h)) return false;

            // 被 DWM 隐藏的窗口(典型是 UWP 的隐藏宿主、切换虚拟桌面后的残留)不可见。
            int cloaked;
            if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return false;

            // 这一条是误检的主要开关:桌宠、虚拟键盘、加速器/叠加层、截图取色层
            // 几乎都会用分层、穿透、工具窗口或不激活样式。
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            const int bad = Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED
                | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE;
            if ((ex & bad) != 0) return false;
            // 有属主的是对话框/弹窗,不是游戏本体。
            if (Native.GetWindow(h, (uint)Native.GW_OWNER) != IntPtr.Zero) return false;

            Native.RECT r;
            if (!WindowBounds(h, out r)) return false;
            int w = r.Right - r.Left;
            int ht = r.Bottom - r.Top;
            if (w < 320 || ht < 240) return false;

            Rectangle b = Screen.FromHandle(h).Bounds;
            // 至少要占显示器的 8%:过小的悬浮窗、桌面小工具直接排除。
            if ((long)w * ht < (long)b.Width * b.Height * 8 / 100) return false;

            string name = Native.ProcessName(p);
            // 用户点过「不是游戏」的进程直接淘汰,连手动锁定都救不回来。
            if (IsBlockedName(name)) return false;
            // 手动锁定的游戏不再看名字/标题特征:冷门游戏、改名版、模拟器外壳
            // 正是靠这一条保证能识别出来。
            bool pinned = IsPinnedName(name);
            bool known = IsKnownGameName(name);
            string cls = WindowClass(h);
            string title = WindowTitle(h);
            if (!pinned && !known)
            {
                if (IsNonGameName(name)) return false;
                if (IsNonGameClass(cls)) return false;
                if (IsUtilityTitle(title)) return false;
                if (IsUtilityName(name)) return false;
            }

            wi.Name = name;
            wi.Class = cls;
            wi.Title = title;
            wi.Width = w;
            wi.Height = ht;
            wi.Covers = w >= b.Width - 8 && ht >= b.Height - 8
                && (long)w * ht >= (long)b.Width * b.Height * 92 / 100;
            wi.Foreground = h == Native.GetForegroundWindow();
            wi.Pinned = pinned;
            wi.Known = known;
            return true;
        }

        /// <summary>
        /// 多信号打分:内置库/游戏目录/引擎特征 + 引擎窗口类 + 铺满屏幕 + 前台
        /// + 该进程真实的 GPU 3D 占用。没有任何一条单独就能定罪,避免误检。
        /// </summary>
        private static int ScoreWindow(ref WindowInfo wi)
        {
            // 手动锁定优先级最高:用户说了算,不再和启发式评分讨价还价。
            if (wi.Pinned) return 1000;
            // 「我的游戏」里的程序:用户扫描或手动添加过,直接按游戏处理。
            if (wi.Known) return 900;

            int score = 0;
            int level = KnownGameLevel(wi.Pid, wi.Name);
            if (level >= 3) score += 70;        // 内置游戏库明确认识
            else if (level == 2) score += 55;   // 装在游戏平台目录里
            else if (level == 1) score += 35;   // 只是加载了 Unity/Unreal 引擎

            if (IsGameClass(wi.Class)) score += 30;
            if (wi.Covers) score += 20;
            if (wi.Foreground) score += 10;
            if (wi.Width >= 640 && wi.Height >= 360) score += 5;

            double gpu = GpuPid3D(wi.Pid);
            if (gpu >= 10) score += 50;
            else if (gpu >= 3) score += 40;
            else if (gpu >= 1) score += 15;

            // 已经确认过的游戏进程:切到桌面/加载中时特征会变弱,给一点保底分。
            lock (confirmedGames)
            {
                DateTime seen;
                if (confirmedGames.TryGetValue(wi.Pid, out seen)
                    && (DateTime.UtcNow - seen).TotalHours < 6) score += 40;
            }
            return score;
        }

        private static double GpuPid3D(int pid)
        {
            lock (gpuPidUsage)
            {
                double v;
                return gpuPidUsage.TryGetValue(pid, out v) ? v : 0;
            }
        }

        /// <summary>
        /// 解析 PDH GPU Engine 实例名里的 pid,按进程汇总 3D 引擎占用。
        /// 实例名形如 pid_1234_luid_0x00000000_0x0000ABCD_phys_0_eng_0_engtype_3D。
        /// </summary>
        private static void UpdateGpuUsage(string[] names, double[] vals)
        {
            var map = new Dictionary<int, double>();
            int n = names == null ? 0 : names.Length;
            for (int i = 0; i < n && i < vals.Length; i++)
            {
                string s = names[i];
                if (string.IsNullOrEmpty(s)) continue;
                if (s.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!s.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) continue;
                int end = s.IndexOf('_', 4);
                if (end < 0) end = s.Length;
                int pid;
                if (!int.TryParse(s.Substring(4, end - 4), out pid)) continue;
                double v = vals[i];
                if (v <= 0) continue;
                double old;
                map.TryGetValue(pid, out old);
                map[pid] = old + v;
            }
            lock (gpuPidUsage) { gpuPidUsage = map; }
        }

        /// <summary>找回某一个进程面积最大的可见窗口:改分辨率/重建窗口后跟踪不丢。</summary>
        private static IntPtr FindWindowForPid(int pid)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            try
            {
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    int p;
                    Native.GetWindowThreadProcessId(h, out p);
                    if (p != pid) return true;
                    if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;

                    int cloaked;
                    if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                    int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                    if ((ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED)) != 0) return true;

                    Native.RECT r;
                    if (!Native.GetWindowRect(h, out r)) return true;
                    long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = h;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return best;
        }

        /// <summary>窗口矩形:优先 DWM 扩展边界(去掉 Win10 的隐形缩放边框),失败再退回 GetWindowRect。</summary>
        internal static bool WindowBounds(IntPtr h, out Native.RECT r)
        {
            r = new Native.RECT();
            try
            {
                int size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.RECT));
                if (Native.DwmGetWindowAttributeRect(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out r, size) == 0
                    && r.Right > r.Left && r.Bottom > r.Top)
                    return true;
            }
            catch { }
            return Native.GetWindowRect(h, out r);
        }

        private static string WindowTitle(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(512);
                if (Native.GetWindowText(hwnd, sb, sb.Capacity) > 0) return sb.ToString();
            }
            catch { }
            return "";
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
        /// 扫一遍所有顶层窗口,返回得分最高的游戏进程。窗口化、小窗口、无边框、
        /// 启动器退场后的本体窗口都能认出来;分层悬浮窗和启动器会被直接排除。
        /// </summary>
        public static bool FindKnownGame(out int pid)
        {
            IntPtr h;
            return FindBestGameWindow(out pid, out h);
        }

        /// <summary>当前有可见、未最小化顶层窗口的进程集合。</summary>
        private static HashSet<int> VisiblePids()
        {
            var set = new HashSet<int>();
            try
            {
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                    int cloaked;
                    if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                    int p;
                    Native.GetWindowThreadProcessId(h, out p);
                    if (p > 4) set.Add(p);
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return set;
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
            // 用户或内置的加速排除名单:自动检测时就不再把它当作游戏。
            if (BoostEngine.IsExcluded(name)) return true;

            string lower = name.ToLowerInvariant();
            // 反作弊组件的窗口(BattlEye Launcher、EAC 安装器)也常铺满屏幕,
            // 而且进程就在游戏目录下,不排除就会被当成游戏本体。
            if (GameDb.AntiCheatName(lower)) return true;

            // 触摸键盘 / 手写面板 / 输入法外壳:Win11 在桌面切屏时会把它们铺满屏幕,
            // 以前会被当成游戏,自动加速就转而去加速 TabTip/InputApp。
            if (lower.IndexOf("textinput") >= 0 || lower.IndexOf("tabtip") >= 0
                || lower.IndexOf("inputapp") >= 0 || lower.IndexOf("handwriting") >= 0
                || lower.IndexOf("composableshell") >= 0
                || lower == "osk" || lower == "ctfmon" || lower == "msctf")
                return true;

            // 浏览器全屏(F11)和 Chromium 内核的窗口经常铺满屏幕,但显然不是游戏。
            // 360 全家桶的进程名五花八门(360se / 360chrome / 360ChromeX ...),
            // 名字里带 360 的一律按浏览器处理,避免再被当成游戏去加速。
            if (IsBrowserName(lower)) return true;

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
                // 桌宠 / Live2D:它们经常用全屏透明窗口或 Unity 渲染,是误检重灾区
                case "shimeji":
                case "desktoppet":
                case "desktop_pet":
                case "vpet":
                case "desktopmate":
                case "live2dviewer":
                case "live2d":
                // 虚拟键盘 / 屏幕键盘
                case "touchkeyboard":
                case "virtualkeyboard":
                case "onboard":
                case "swkb":
                // 加速器 / 超频 / 监控工具:悬浮窗和主界面都不算游戏
                case "uugame":
                case "uu":
                case "xunyou":
                case "biubiu":
                case "leigod":
                case "qiyou":
                case "boosterx":
                case "gamebooster":
                case "wisegamebooster":
                case "cortex":
                case "razercentral":
                case "msiafterburner":
                case "rtss":
                case "nvsphelper64":
                case "nvcontainer":
                // 更多启动器
                case "mihoyolauncher":
                case "hoyoplay":
                case "hypergryph":
                case "nshlauncher":
                case "playnite":
                case "gamecenter":
                case "gamelauncher":
                // 3D 建模 / 视频剪辑:GPU 3D 占用很高,但显然不是游戏
                case "blender":
                case "photoshop":
                case "illustrator":
                case "premiere":
                case "aftereffects":
                case "davinci":
                case "resolve":
                case "3dsmax":
                case "maya":
                case "cinema4d":
                case "c4d":
                case "houdini":
                case "zbrush":
                case "unity":
                case "unrealeditor":
                case "wallpaper32":
                case "wallpaper64":
                case "wallpaperengine":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>浏览器 / Chromium 外壳的进程名关键词,统一在游戏识别里排除。</summary>
        private static bool IsBrowserName(string lower)
        {
            if (lower.Length == 0) return false;
            if (lower.StartsWith("360", StringComparison.Ordinal)) return true;

            string[] words =
            {
                "browser", "chrome", "chromium", "firefox", "msedge", "iexplore",
                "sogouexplorer", "2345explorer", "maxthon", "liebao", "quark",
                "wechatappex", "vivaldi", "electron"
            };
            for (int i = 0; i < words.Length; i++)
                if (lower.IndexOf(words[i], StringComparison.Ordinal) >= 0) return true;

            // Opera / Brave 只认精确的进程名:按关键词匹配会误伤 operation 之类的游戏名。
            if (lower == "opera" || lower.StartsWith("opera_", StringComparison.Ordinal)
                || lower == "brave" || lower.StartsWith("brave_", StringComparison.Ordinal))
                return true;
            return false;
        }

        /// <summary>启动器 / 更新器 / 加速器 / 桌宠 / 虚拟键盘的进程名关键词。</summary>
        private static readonly string[] UtilityWords =
        {
            "launcher", "updater", "patcher", "installer", "unins", "crashreport",
            "crashhandler", "accelerator", "speedup", "booster", "optimizer", "cleaner",
            "desktoppet", "desktop_pet", "shimeji", "vpet", "live2d",
            "virtualkeyboard", "touchkeyboard", "onboard", "tabtip", "textinput", "inputapp",
            "overlay", "msiafterburner", "rtss", "wallpaper32", "wallpaper64",
            "xunyou", "biubiu", "leigod", "qiyou", "uugame"
        };

        private static bool IsUtilityName(string name)
        {
            if (name.Length == 0) return false;
            string n = name.ToLowerInvariant();
            for (int i = 0; i < UtilityWords.Length; i++)
                if (n.IndexOf(UtilityWords[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>窗口标题就是启动器/更新器/加速器的,也不是游戏本体。</summary>
        private static readonly string[] UtilityTitles =
        {
            "启动器", "登录器", "更新程序", "加速器", "游戏平台", "官方网站",
            "launcher", "updater", "patcher", "installer"
        };

        private static bool IsUtilityTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            string t = title.ToLowerInvariant();
            for (int i = 0; i < UtilityTitles.Length; i++)
                if (t.IndexOf(UtilityTitles[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// 判定一个窗口是不是游戏:窗口样式先做硬性排除,再用多信号打分。
        /// 分层/穿透/工具/不激活/有属主/最小化/过小的窗口一律不算,
        /// 所以桌宠、虚拟键盘、加速器悬浮窗不会再被当成游戏。
        /// </summary>
        public static bool IsGameWindow(IntPtr hwnd, int pid)
        {
            if (hwnd == IntPtr.Zero || pid <= 4) return false;
            try
            {
                WindowInfo wi;
                if (!InspectWindow(hwnd, pid, out wi)) return false;
                return ScoreWindow(ref wi) >= GameScore;
            }
            catch { return false; }
        }

        /// <summary>
        /// 明确认得出的游戏进程等级:3=内置库,2=游戏平台目录,1=只是加载了引擎。
        /// 引擎特征单独一条不足以定罪——桌宠、启动器、工具也可能用 Unity。
        /// </summary>
        private static int KnownGameLevel(int pid, string name)
        {
            if (pid <= 0) return 0;
            // 所有 Java 进程都叫 javaw,内置库里的 javaw 只代表我的世界,
            // 必须再读窗口标题确认,否则任何 Java 程序都会被当成游戏。
            if (string.Equals(name, "java", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "javaw", StringComparison.OrdinalIgnoreCase))
                return GameDb.JavaGame(pid) ? 3 : 0;

            // 枚举窗口时同一个进程会被反复问到,缓存 2 秒避免重复读路径。
            DateTime now = DateTime.UtcNow;
            int hit;
            lock (gameProcCache)
            {
                if ((now - gameProcCacheAt).TotalSeconds > 2)
                {
                    gameProcCache.Clear();
                    gameProcCacheAt = now;
                }
                if (gameProcCache.TryGetValue(pid, out hit)) return hit;
            }

            int r = 0;
            try
            {
                if (name.Length > 0 && GameDb.Known(name)) r = 3;
                if (r == 0 && GameDb.LibraryGame(pid)) r = 2;
                if (r == 0 && GameDb.EngineGame(pid)) r = 1;
            }
            catch { r = 0; }

            lock (gameProcCache) { gameProcCache[pid] = r; }
            return r;
        }

        private static bool KnownGameProcess(int pid, string name)
        {
            return KnownGameLevel(pid, name) > 0;
        }

        private static readonly Dictionary<int, int> gameProcCache = new Dictionary<int, int>();
        private static DateTime gameProcCacheAt = DateTime.MinValue;

        private static string WindowClass(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                if (Native.GetClassName(hwnd, sb, sb.Capacity) > 0) return sb.ToString();
            }
            catch { }
            return "";
        }

        /// <summary>Chromium / 浏览器 / UWP 外壳的窗口类:全屏也不算游戏。</summary>
        private static bool IsNonGameClass(string cls)
        {
            if (cls.Length == 0) return false;
            if (cls.StartsWith("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)) return true;
            if (cls.StartsWith("MozillaWindowClass", StringComparison.OrdinalIgnoreCase)) return true;
            if (cls.StartsWith("IEFrame", StringComparison.OrdinalIgnoreCase)) return true;
            if (cls.StartsWith("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase)) return true;
            if (cls.StartsWith("Windows.UI.Core", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>常见游戏引擎的顶层窗口类:名字读不到时靠它兜底。</summary>
        private static bool IsGameClass(string cls)
        {
            if (cls.Length == 0) return false;
            string c = cls.ToLowerInvariant();
            return c == "unrealwindow" || c == "unitywndclass" || c == "valve001"
                || c == "cryengine" || c == "glfw30" || c == "sdl_app"
                || c.StartsWith("riotwindowclass") || c.StartsWith("godot");
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