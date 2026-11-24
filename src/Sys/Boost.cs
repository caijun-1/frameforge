using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    /// <summary>用户在加速页勾选的优化项。</summary>
    public class BoostPlan
    {
        public bool Priority = true;    // 游戏进程 -> 提升优先级
        public bool PowerPlan = true;   // 高性能电源计划
        public bool Timer = true;       // 系统计时器 1ms
        public bool Dvr = true;         // 关闭 Game DVR / 全屏优化
        public bool Memory = true;      // 内存整理
        public bool Background = false; // 进阶:暂停后台进程
        public bool Affinity = false;   // 进阶:游戏独占核心
        public bool Irq = false;        // 进阶:设备中断避让(重启生效)
        public bool IdealCpu = false;   // 实验:线程理想处理器(不改亲和性)
        public int Cores = 0;           // 0 = 全部核心(不限制)
        public int PriorityLevel = 4;   // 0 低 / 1 低于正常 / 2 正常 / 3 高于正常 / 4 高

        public bool Any
        {
            get { return Priority || PowerPlan || Timer || Dvr || Memory || Background || Affinity || Irq || IdealCpu; }
        }
    }

    /// <summary>一个后台进程,用于进阶模式的暂停列表。</summary>
    public class BackgroundProc
    {
        public int Pid;
        public string Name = "";
        public ulong WorkingSet;
    }

    /// <summary>一次加速的结果,界面直接展示。</summary>
    public class BoostResult
    {
        public bool Ok;
        public string Error = "";
        public bool NeedAdmin;
        public long MemReleased;
        public int Suspended;
        public int PriorityOk;
        public int PowerOk;
        public int TimerOk;
        public int DvrOk;
        public int MemoryOk;
        public int AffinityOk;
        public int AffinityCores;
        public int AffinityMoved;
        public int IrqOk;
        public int IrqDevices;
        public bool IrqNeedReboot;
        /// <summary>理想处理器设置成功的线程数(实验项)。</summary>
        public int IdealOk;
        /// <summary>本次请求的项数(用来算「实际生效 / 跳过」)。</summary>
        public int Requested;
        /// <summary>没有生效的项,元素是 i18n 键,由界面翻译成原因。</summary>
        public List<string> Skipped = new List<string>();
    }

    /// <summary>
    /// 帧率加速引擎:只做有依据、可回滚的系统级调整。
    /// 1) 提升游戏进程优先级;2) 切到高性能电源计划;3) 计时器调到 1ms;
    /// 4) 关闭 Game DVR 与全屏优化;5) 整理内存;6) 进阶:暂停后台进程。
    /// 所有改动都会记录原值,「还原」或退出时逐项恢复。
    /// </summary>
    public sealed class BoostEngine : IDisposable, ISystemResourceOwner
    {
        public const string SchemeHigh = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        public const string SchemeBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

        private const string GameConfigStore = @"System\GameConfigStore";
        private const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";

        private static readonly Regex GuidRx = new Regex(
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            RegexOptions.Compiled);

        private readonly List<RegSaved> regSaved = new List<RegSaved>();
        private readonly List<Suspended> suspended = new List<Suspended>();
        private readonly AffinityEngine affinity = new AffinityEngine();
        private readonly InterruptEngine irq = new InterruptEngine();

        private string prevScheme = "";
        private uint prevPriority;
        private bool timerSet;

        // SCTL v2 / boost-core §A3：本引擎对全局资源的持有标记与仲裁回调。
        private bool powerAcquired;
        private bool timerAcquired;
        private bool needPower;
        private bool needTimer;
        private Transaction currentTx;

        // 看门狗跟随父进程生命周期；异常退出后下一次加速允许重新拉起。
        private static readonly object WatchdogLock = new object();
        private static Process watchdogProcess;

        // 核心调度策略层：Auto 保持零回归，非 Auto 只做候选过滤。
        private readonly ICoreScheduler coreScheduler = new CoreScheduler();

        public bool Active;
        public int GamePid = -1;
        public string GameName = "";
        /// <summary>实际生效的优先级档位,-1 表示这次没有改优先级。</summary>
        public int PriorityLevel = -1;
        /// <summary>理想处理器是否至少设置成功了一个线程。</summary>
        public bool IdealApplied;
        /// <summary>当前软引导到的逻辑核,-1 表示还没引导过。</summary>
        public int SteerCpu = -1;
        public DateTime StartedAt = DateTime.MinValue;

        // PCBoost 的第二层:每 2 分钟重新挑一次「当前最空闲的核心」,
        // 把游戏主线程的理想处理器指过去。这里只设调度提示(SetThreadIdealProcessor),
        // 不改亲和性掩码——游戏需要多核爆发时依然可以调用包括 CPU 0 在内的全部核心。
        private const int SteerSeconds = 120;
        private const int SteerSampleMs = 220;
        private DateTime lastSteer = DateTime.MinValue;
        private int steerCores;
        private int steerBusy;

        public int SuspendedCount { get { return suspended.Count; } }

        /// <summary>核心独占的实际方式:0 未生效 / 1 软亲和 / 2 硬亲和。</summary>
        public int AffinityMode
        {
            get
            {
                CoreSchedulerState state = coreScheduler.SnapshotState();
                return state.AffinityMode != 0 ? state.AffinityMode : affinity.Mode;
            }
        }
        /// <summary>游戏当前占用的核心掩码,未启用核心独占时为 0。</summary>
        public long AffinityMask
        {
            get
            {
                CoreSchedulerState state = coreScheduler.SnapshotState();
                return state.AffinityMask != 0 ? state.AffinityMask : affinity.Mask;
            }
        }

        public static bool IsAdmin
        {
            get { return MemEngine.IsAdmin; }
        }

        // ---------------- ISystemResourceOwner（boost-core §A3.2） ----------------

        public OwnerKind Kind() { return OwnerKind.ONE_SHOT; }
        public bool Needs(ResourceKind r)
        {
            return r == ResourceKind.PowerScheme ? needPower : needTimer;
        }
        public void OnGranted(ResourceKind r, uint refCount)
        {
            Log.Write("boost arbiter granted " + r + " ref=" + refCount);
            // FF-011：holder 转移后由留存引擎落盘，崩溃恢复才认得当前归属。
            PersistOwnership();
        }
        public void OnShared(ResourceKind r, uint refCount)
        {
            Log.Write("boost arbiter shared " + r + " ref=" + refCount);
        }
        public void OnReleased(ResourceKind r)
        {
            Log.Write("boost arbiter released " + r);
        }

        private void PersistOwnership()
        {
            try
            {
                Transaction tx = currentTx;
                if (tx == null) return;
                tx.Res = SystemResourceArbiter.Instance.Snapshot();
                BoostJournal.Save(tx);
            }
            catch (Exception ex) { Log.Write("boost persist ownership: " + ex.Message); }
        }

        // =================================================================
        //  探测
        // =================================================================

        /// <summary>前台窗口铺满所在显示器即为游戏;其余情况返回 pid = -1。</summary>
        public static int DetectGamePid(out string name)
        {
            name = "";
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return -1;
            int pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid <= 4) return -1;
            string n = Native.ProcessName(pid);
            if (n.Length == 0) return -1;
            if (IsShell(n) || IsExcluded(n)) return -1;

            if (!Monitor.IsGameWindow(hwnd, pid)) return -1;

            name = n;
            return pid;
        }

        private static bool IsShell(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "explorer": case "dwm": case "frameforge": case "taskmgr":
                case "searchapp": case "shellexperiencehost": case "startmenuexperiencehost":
                case "applicationframehost": case "lockapp": case "textinputhost":
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 进阶模式的候选后台进程:占用高、没有可见窗口、不在保护名单里。
        /// 有可见窗口的程序不会被暂停,避免用户眼前的窗口卡死。
        /// </summary>
        public static BackgroundProc[] Candidates(int gamePid, int max)
        {
            var list = new List<BackgroundProc>();
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return new BackgroundProc[0]; }

            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in procs)
            {
                try
                {
                    if (p.Id <= 4 || p.Id == self || p.Id == gamePid) continue;
                    if (p.MainWindowHandle != IntPtr.Zero) continue;
                    long ws = p.WorkingSet64;
                    if (ws < 80L << 20) continue;
                    // 占用不到阈值的程序暂停了也省不下多少内存,反而容易弄坏输入法、
                    // 更新器这类小组件,所以自动归入保护。占用涨过阈值后,下一次
                    // 重新计算会自然掉出这档,重新回到候选列表。
                    if (ws <= AutoWhitelistBytes) continue;
                    string name = p.ProcessName;
                    if (name.Length == 0) continue;
                    // 系统进程、自动启动的服务和开机自启程序,关掉会被系统拉起来,
                    // 暂停没有意义;用户手动加的也不参与。
                    if (IsPermanent(name) || UserWhitelist.Contains(name)) continue;
                    list.Add(new BackgroundProc { Pid = p.Id, Name = name, WorkingSet = (ulong)ws });
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            list.Sort(delegate(BackgroundProc a, BackgroundProc b) { return b.WorkingSet.CompareTo(a.WorkingSet); });
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list.ToArray();
        }

        internal static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Idle", "System", "Registry", "Memory Compression", "csrss", "wininit", "winlogon",
            "services", "lsass", "smss", "svchost", "MsMpEng", "MpDefenderCoreService",
            "dwm", "FrameForge", "audiodg", "SecurityHealthService", "WUDFHost", "fontdrvhost",
            "SystemSettings", "winlogon", "taskhostw", "ctfmon", "RuntimeBroker",
            // 桌面外壳和系统界面:结束/暂停后 Windows 会自动把它们重新拉起来。
            "explorer", "sihost", "ShellExperienceHost", "StartMenuExperienceHost",
            "SearchApp", "SearchHost", "ApplicationFrameHost", "LockApp", "TextInputHost",
            // WebView2 是宿主程序的内嵌组件,暂停后宿主会立刻重建,还容易白屏。
            "msedgewebview2",
            // 游戏平台:暂停客户端会导致游戏掉线或无法启动,永远不碰。
            "steam", "steamwebhelper", "SteamService", "GameOverlayUI",
            "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "BlizzardBrowser",
            "Origin", "EADesktop", "EABackgroundService", "UbisoftConnect", "upc",
            "RiotClientServices", "RiotClientUx", "WeGame", "TencentDL",
            "GalaxyClient", "GalaxyClientHelper", "GOGGalaxy",
            // 录制 / 叠加工具:暂停后画面和帧率都会断,不能当后台进程处理。
            "Discord", "obs64", "obs32", "nvsphelper64", "NVIDIA Share", "NVIDIA Overlay",
            "RTSS", "RTSSHooksLoader64", "MSIAfterburner", "Overwolf"
        };

        /// <summary>用户自定义保护名单,由设置页维护;与内置名单同等生效。</summary>
        public static readonly HashSet<string> UserWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void SetWhitelist(string[] names)
        {
            UserWhitelist.Clear();
            if (names == null) return;
            foreach (string n in names)
            {
                string s = (n ?? "").Trim();
                if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    s = s.Substring(0, s.Length - 4);
                if (s.Length > 0) UserWhitelist.Add(s);
            }
        }

        /// <summary>低于这个内存的程序自动保护;超过后下一次计算会自动移出。</summary>
        public const long AutoWhitelistBytes = 200L << 20;

        // =================================================================
        //  加速排除名单
        // =================================================================

        /// <summary>
        /// 默认排除:壁纸、直播和语音叠加类程序。它们经常占着前台或全屏,
        /// 但并不是游戏,加速它们只会让真正的游戏排不上队。
        /// </summary>
        public const string DefaultExclude = "wallpaper32,wallpaper64,obs64,streamlabs,discord";

        /// <summary>系统界面进程:始终排除,不可取消,也不写回设置。</summary>
        public static readonly string[][] ExcludeBuiltIn = new string[][]
        {
            new string[] { "tabtip", "TabTip" },
            new string[] { "textinputhost", "TextInputHost" },
            new string[] { "inputapp", "InputApp" },
            new string[] { "osk", "OSK" },
            new string[] { "ctfmon", "ctfmon" },
            new string[] { "composableshell", "ComposableShell" },
            new string[] { "handwriting", "Handwriting" }
        };

        private static readonly HashSet<string> BuiltInExclude = MakeNameSet(ExcludeBuiltIn);

        /// <summary>用户可编辑的排除名单,由设置页维护。</summary>
        public static readonly HashSet<string> UserExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void SetExclude(string[] names)
        {
            UserExclude.Clear();
            if (names == null) return;
            foreach (string n in names)
            {
                string s = CleanName(n);
                if (s.Length > 0) UserExclude.Add(s);
            }
        }

        /// <summary>该进程是否不参与加速(系统界面恒排除,其余按用户名单)。</summary>
        public static bool IsExcluded(string name)
        {
            string s = CleanName(name);
            if (s.Length == 0) return false;
            return BuiltInExclude.Contains(s) || UserExclude.Contains(s);
        }

        /// <summary>去掉扩展名并统一大小写;名单比较都用这个名字。</summary>
        public static string CleanName(string n)
        {
            string s = (n ?? "").Trim();
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);
            return s.ToLowerInvariant();
        }

        private static HashSet<string> MakeNameSet(string[][] items)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < items.Length; i++)
            {
                string s = CleanName(items[i][0]);
                if (s.Length > 0) set.Add(s);
            }
            return set;
        }

        private static readonly object permGate = new object();
        private static HashSet<string> permNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static DateTime permAt = DateTime.MinValue;
        private const double PermRefreshSeconds = 300;

        /// <summary>
        /// 永久保护:系统关键进程、自动启动的 Windows 服务、注册表 Run 里的自启程序。
        /// 这些进程被结束或暂停后都会被系统重新拉起来,暂停它们既省不到内存,
        /// 又可能让桌面、显卡面板、输入法之类的东西失灵。
        /// </summary>
        public static bool IsPermanent(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (Protected.Contains(name) || MemEngine.IsProtectedName(name)) return true;
            RefreshPermanent();
            lock (permGate) { return permNames.Contains(name); }
        }

        private static void RefreshPermanent()
        {
            lock (permGate)
            {
                // 服务/自启项可能被用户新装或卸载,每 5 分钟重新扫一次。
                if (permNames.Count > 0 && (DateTime.UtcNow - permAt).TotalSeconds < PermRefreshSeconds) return;
                HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddAutoServices(set);
                AddRunEntries(set);
                permNames = set;
                permAt = DateTime.UtcNow;
            }
        }

        /// <summary>只收"自动启动"的服务(Start=2);驱动和手动服务不算关掉会回来。</summary>
        private static void AddAutoServices(HashSet<string> set)
        {
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (root == null) return;
                    string[] keys = root.GetSubKeyNames();
                    for (int i = 0; i < keys.Length; i++)
                    {
                        using (RegistryKey k = root.OpenSubKey(keys[i]))
                        {
                            if (k == null) continue;
                            object start = k.GetValue("Start");
                            if (!(start is int) || (int)start != 2) continue;
                            string exe = ExeName(k.GetValue("ImagePath") as string);
                            if (exe.Length > 0) set.Add(exe);
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        private static void AddRunEntries(HashSet<string> set)
        {
            RegistryKey[] roots = new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine };
            for (int i = 0; i < roots.Length; i++)
            {
                using (RegistryKey k = roots[i].OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (k != null) AddRunValues(k, set);
                }
                using (RegistryKey k = roots[i].OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce"))
                {
                    if (k != null) AddRunValues(k, set);
                }
            }
        }

        private static void AddRunValues(RegistryKey k, HashSet<string> set)
        {
            try
            {
                string[] vals = k.GetValueNames();
                for (int i = 0; i < vals.Length; i++)
                {
                    string exe = ExeName(k.GetValue(vals[i]) as string);
                    if (exe.Length > 0) set.Add(exe);
                }
            }
            catch (Exception) { }
        }

        /// <summary>从服务路径或自启命令行里取出不带 .exe 的进程名。</summary>
        private static string ExeName(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            string s = cmd.Trim();
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = s.IndexOf('"', 1);
                s = end > 1 ? s.Substring(1, end - 1) : s.Trim('"');
            }
            else
            {
                int sp = s.IndexOf(' ');
                if (sp > 0) s = s.Substring(0, sp);
            }
            int slash = s.LastIndexOfAny(new char[] { '\\', '/' });
            if (slash >= 0) s = s.Substring(slash + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s.Trim();
        }

        /// <summary>保护名单里的一行:给小白看的勾选项,不用手打进程名。</summary>
        public class AppChoice
        {
            public string Name = "";      // 进程名(不带 .exe)
            public string Label = "";     // 显示名,常见平台用中文友好名
            public ulong Memory;
            public bool Running;
            public bool BuiltIn;          // 游戏平台/录制工具,永远保护,不可取消
            // 0 普通 / 1 永久(系统、服务、自启) / 2 自动(小内存) / 3 用户手动添加 / 4 推荐排除
            public int Kind;
        }

        /// <summary>常见平台与录制工具:即使没在运行也列出来,方便提前保护。</summary>
        private static readonly string[][] Presets = new string[][]
        {
            new string[] { "steam", "Steam" },
            new string[] { "EpicGamesLauncher", "Epic Games" },
            new string[] { "Battle.net", "Battle.net" },
            new string[] { "WeGame", "WeGame" },
            new string[] { "Discord", "Discord" },
            new string[] { "obs64", "OBS Studio" },
            new string[] { "RTSS", "RTSS" },
            new string[] { "MSIAfterburner", "MSI Afterburner" }
        };

        private static bool IsPreset(string name)
        {
            for (int i = 0; i < Presets.Length; i++)
                if (string.Equals(Presets[i][0], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 生成保护名单的可勾选列表:正在运行的程序(按内存排序) + 常用平台 + 已保存但没在跑的项。
        /// 每个条目带来源标记:永久(系统/服务/自启)、自动(小内存)、手动(用户勾选)。
        /// 从没露过面的系统小进程不列出,避免把列表塞满。
        /// </summary>
        public static AppChoice[] ProtectChoices()
        {
            var map = new Dictionary<string, AppChoice>(StringComparer.OrdinalIgnoreCase);
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { procs = new Process[0]; }

            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in procs)
            {
                try
                {
                    if (p.Id <= 4 || p.Id == self) continue;
                    string name = p.ProcessName;
                    if (name.Length == 0) continue;

                    bool preset = IsPreset(name);

                    long ws = 0;
                    try { ws = p.WorkingSet64; } catch (Exception) { }
                    bool visible = false;
                    try { visible = p.MainWindowHandle != IntPtr.Zero; } catch (Exception) { }
                    // 只留用户能认出来的:有窗口的、吃过 120MB 的,以及固定预设。
                    if (!preset && !visible && ws < (120L << 20)) continue;

                    bool permanent = preset || IsPermanent(name);
                    // 小内存程序自动保护,不需要用户勾;内存涨过阈值后
                    // 下一次打开列表就会变回普通项,想保护再手动勾。
                    bool autoThin = !permanent && ws > 0 && ws <= AutoWhitelistBytes;

                    AppChoice c;
                    if (map.TryGetValue(name, out c))
                    {
                        if ((ulong)ws > c.Memory) c.Memory = (ulong)ws;
                        if (permanent) { c.BuiltIn = true; c.Kind = 1; }
                        else if (c.Kind == 0 && autoThin) c.Kind = 2;
                        continue;
                    }
                    map[name] = new AppChoice
                    {
                        Name = name,
                        Label = name,
                        Memory = (ulong)ws,
                        Running = true,
                        BuiltIn = permanent,
                        Kind = permanent ? 1 : (autoThin ? 2 : 0)
                    };
                }
                catch (Exception) { }
                finally { try { p.Dispose(); } catch (Exception) { } }
            }

            for (int i = 0; i < Presets.Length; i++)
            {
                if (map.ContainsKey(Presets[i][0])) continue;
                map[Presets[i][0]] = new AppChoice
                {
                    Name = Presets[i][0],
                    Label = Presets[i][1],
                    Running = false,
                    BuiltIn = true,
                    Kind = 1
                };
            }

            // 用户手动加过、但当前没在运行的程序也要留住,否则一保存就"消失"了。
            foreach (string n in UserWhitelist)
            {
                if (n.Length == 0) continue;
                AppChoice c;
                if (map.TryGetValue(n, out c))
                {
                    if (c.Kind == 0) c.Kind = 3;
                    continue;
                }
                map[n] = new AppChoice { Name = n, Label = n, Kind = IsPreset(n) ? 1 : 3 };
            }

            var list = new List<AppChoice>(map.Values);
            list.Sort(delegate(AppChoice a, AppChoice b)
            {
                int ga = Group(a), gb = Group(b);
                if (ga != gb) return ga.CompareTo(gb);
                if (a.Running != b.Running) return a.Running ? -1 : 1;
                if (a.Running) return b.Memory.CompareTo(a.Memory);
                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
            return list.ToArray();
        }

        /// <summary>排序分组:0 永久保护,1 普通/手动,2 自动保护(小内存)。</summary>
        private static int Group(AppChoice c)
        {
            if (c.Kind == 1) return 0;
            if (c.Kind == 2) return 2;
            return 1;
        }

        // ---------------- 加速排除名单 ----------------

        /// <summary>推荐排除的常见叠加/直播程序,即使没在运行也列出来。</summary>
        private static readonly string[][] ExcludePresets = new string[][]
        {
            new string[] { "wallpaper32", "Wallpaper Engine" },
            new string[] { "wallpaper64", "Wallpaper Engine (64)" },
            new string[] { "obs64", "OBS Studio" },
            new string[] { "streamlabs", "Streamlabs" },
            new string[] { "discord", "Discord" }
        };

        private static bool IsExcludePreset(string name)
        {
            for (int i = 0; i < ExcludePresets.Length; i++)
                if (string.Equals(ExcludePresets[i][0], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ExcludeLabel(string name)
        {
            for (int i = 0; i < ExcludePresets.Length; i++)
                if (string.Equals(ExcludePresets[i][0], name, StringComparison.OrdinalIgnoreCase)) return ExcludePresets[i][1];
            return name;
        }

        /// <summary>
        /// 生成加速排除名单的可勾选列表:推荐项、正在运行的程序、已保存项,
        /// 最后是恒排除的系统界面进程(锁定,不可取消)。
        /// </summary>
        public static AppChoice[] ExcludeChoices()
        {
            var map = new Dictionary<string, AppChoice>(StringComparer.OrdinalIgnoreCase);
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { procs = new Process[0]; }

            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in procs)
            {
                try
                {
                    if (p.Id <= 4 || p.Id == self) continue;
                    string name = p.ProcessName;
                    if (name.Length == 0) continue;

                    bool preset = IsExcludePreset(name);
                    long ws = 0;
                    try { ws = p.WorkingSet64; } catch (Exception) { }
                    bool visible = false;
                    try { visible = p.MainWindowHandle != IntPtr.Zero; } catch (Exception) { }
                    if (!preset && !visible && ws < (120L << 20)) continue;

                    AppChoice c;
                    if (map.TryGetValue(name, out c))
                    {
                        if ((ulong)ws > c.Memory) c.Memory = (ulong)ws;
                        if (preset) { c.BuiltIn = true; c.Kind = 4; c.Label = ExcludeLabel(name); }
                        continue;
                    }
                    map[name] = new AppChoice
                    {
                        Name = name,
                        Label = preset ? ExcludeLabel(name) : name,
                        Memory = (ulong)ws,
                        Running = true,
                        BuiltIn = preset,
                        Kind = preset ? 4 : 0
                    };
                }
                catch (Exception) { }
                finally { try { p.Dispose(); } catch (Exception) { } }
            }

            for (int i = 0; i < ExcludePresets.Length; i++)
            {
                if (map.ContainsKey(ExcludePresets[i][0])) continue;
                map[ExcludePresets[i][0]] = new AppChoice
                {
                    Name = ExcludePresets[i][0],
                    Label = ExcludePresets[i][1],
                    Running = false,
                    BuiltIn = true,
                    Kind = 4
                };
            }

            // 系统输入法/触摸键盘等界面进程:始终排除,不能取消。
            for (int i = 0; i < ExcludeBuiltIn.Length; i++)
            {
                map[ExcludeBuiltIn[i][0]] = new AppChoice
                {
                    Name = ExcludeBuiltIn[i][0],
                    Label = ExcludeBuiltIn[i][1],
                    Running = false,
                    BuiltIn = true,
                    Kind = 1
                };
            }

            foreach (string n in UserExclude)
            {
                if (n.Length == 0) continue;
                AppChoice c;
                if (map.TryGetValue(n, out c))
                {
                    if (c.Kind == 0) c.Kind = 3;
                    continue;
                }
                map[n] = new AppChoice { Name = n, Label = n, Kind = 3 };
            }

            var list = new List<AppChoice>(map.Values);
            list.Sort(delegate(AppChoice a, AppChoice b)
            {
                int ga = ExcludeGroup(a), gb = ExcludeGroup(b);
                if (ga != gb) return ga.CompareTo(gb);
                if (a.Running != b.Running) return a.Running ? -1 : 1;
                if (a.Running) return b.Memory.CompareTo(a.Memory);
                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
            return list.ToArray();
        }

        /// <summary>排除列表分组:0 推荐排除,1 普通,2 系统界面(锁定)。</summary>
        private static int ExcludeGroup(AppChoice c)
        {
            if (c.Kind == 4) return 0;
            if (c.Kind == 1) return 2;
            return 1;
        }

        // =================================================================
        //  应用
        // =================================================================

        public BoostResult Apply(BoostPlan plan, int gamePid, string gameName)
        {
            var r = new BoostResult();
            if (plan == null || !plan.Any) { r.Error = "empty"; return r; }
            if (gamePid <= 0) { r.Error = "nogame"; return r; }
            if (Active) Restore();
            if (!BoostJournal.RecoverBeforeNew(OwnerKind.ONE_SHOT, true))
            {
                r.Error = "pending-recovery";
                return r;
            }

            GamePid = gamePid;
            GameName = gameName ?? "";
            StartedAt = DateTime.Now;
            steerCores = plan.Cores;
            needPower = plan.PowerPlan;
            needTimer = plan.Timer;

            // ===== 阶段一：在任何系统改动之前捕获全部 old 并落 PREPARED journal（§3.4 先记后改） =====
            int gameSid = -1;
            try { using (Process gp = Process.GetProcessById(gamePid)) gameSid = gp.SessionId; }
            catch { }
            CorePolicy policy = CoreConfig.Policy();
            bool willSteer = plan.Priority && !plan.IdealCpu && policy != CorePolicy.SingleCcd;
            bool willIdealAll = plan.IdealCpu && policy == CorePolicy.Auto;
            bool useSchedulerAffinity = plan.Affinity && policy == CorePolicy.SingleCcd;

            Transaction tx = BoostJournal.NewTransaction(
                OwnerKind.ONE_SHOT, gamePid, GameName, gameSid);
            BackgroundProc[] suspendCandidates = null;
            BuildPreparedActions(tx, plan, gamePid, GameName, gameSid, policy,
                willSteer, useSchedulerAffinity, out suspendCandidates);
            currentTx = tx;
            tx.State = TxState.PREPARED;
            BoostJournal.Save(tx);
            tx.State = TxState.APPLYING;

            // ===== 阶段二：逐类改动，每个确认点原子落盘 =====
            string priorityFail = "";
            if (plan.Priority)
            {
                int applied;
                if (ApplyPriority(gamePid, plan.PriorityLevel, tx, out priorityFail, out applied))
                {
                    r.PriorityOk = 1;
                    // 反作弊只允许较高的普通优先级时,界面要如实写「高于正常」。
                    PriorityLevel = applied;
                    Confirm(tx, ActionKind.PRIORITY_CLASS, true, "",
                        PriorityClassFor(applied).ToString(CultureInfo.InvariantCulture));
                }
                else Confirm(tx, ActionKind.PRIORITY_CLASS, false, priorityFail, "");
            }

            // ideal 双写修复（§B3.2 第 2 条）：TrySteer 与 ApplyIdealCpu 只保留一个写入者。
            if (willSteer)
            {
                try
                {
                    TrySteer(plan.Cores, policy);
                    bool ok = SteerCpu >= 0;
                    Confirm(tx, ActionKind.IDEAL_CPU, ok, ok ? "" : "ideal-refused",
                        ok ? SteerCpu.ToString(CultureInfo.InvariantCulture) : "");
                }
                catch (Exception ex)
                { Log.Write("boost steer: " + ex.Message); Confirm(tx, ActionKind.IDEAL_CPU, false, ex.Message, ""); }
            }
            else if (willIdealAll)
            {
                try
                {
                    r.IdealOk = ApplyIdealCpu(gamePid, plan.Cores, tx);
                    IdealApplied = r.IdealOk > 0;
                }
                catch (Exception ex)
                { Log.Write("boost ideal: " + ex.Message); Confirm(tx, ActionKind.IDEAL_CPU, false, ex.Message, ""); }
            }

            // 核心调度策略层：不新建执行器，SingleCcd 走 AffinityEngine 的显式 mask。
            if (useSchedulerAffinity)
            {
                try
                {
                    ProcessTelemetry tel = MakeTelemetry(gamePid, GameName, plan);
                    CorePlan cp = coreScheduler.Plan(CpuTopology.Snapshot(), tel, policy);
                    ProcessGuard guard = BoostJournal.MakeGuard(gamePid, GameName, gameSid);
                    CoreApplyResult cr = coreScheduler.Apply(cp, guard);
                    r.AffinityOk = cr.Ok ? 1 : 0;
                    r.AffinityCores = CpuTopology.CoreCount(cr.Mask);
                    r.AffinityMoved = cr.Moved;
                    Confirm(tx, ActionKind.AFFINITY_MASK, cr.Ok, cr.Error, "");
                    if (cp.GameMask != 0) coreScheduler.SnapshotCoreActions();
                }
                catch (Exception ex) { Log.Write("boost core scheduler: " + ex.Message); }
            }
            else if (plan.Affinity)
            {
                try
                {
                    AffinityResult a = affinity.Apply(gamePid, GameName, plan.Cores);
                    r.AffinityOk = a.Ok ? 1 : 0;
                    r.AffinityCores = a.Cores;
                    r.AffinityMoved = a.Moved;
                    Confirm(tx, ActionKind.AFFINITY_MASK, a.Ok, a.Error, "");
                    // 被挪走的进程原掩码在 Apply 时才知道，补记进同一事务（status 已确认）。
                    AppendMovedAffinityActions(tx, gamePid, GameName, gameSid);
                    BoostJournal.Save(tx);
                }
                catch (Exception ex)
                { Log.Write("boost affinity: " + ex.Message); Confirm(tx, ActionKind.AFFINITY_MASK, false, ex.Message, ""); }
            }

            if (plan.Irq)
            {
                try
                {
                    IrqResult q = irq.Apply(plan.Cores);
                    r.IrqOk = q.Ok ? 1 : 0;
                    r.IrqDevices = q.Set;
                    r.IrqNeedReboot = q.Ok;
                    Confirm(tx, ActionKind.IRQ_AFFINITY, q.Ok, q.Error, "");
                }
                catch (Exception ex)
                { Log.Write("boost irq: " + ex.Message); Confirm(tx, ActionKind.IRQ_AFFINITY, false, ex.Message, ""); }
            }
            if (plan.PowerPlan)
            {
                bool ok = ApplyPowerPlan();
                if (ok) r.PowerOk = 1;
                Confirm(tx, ActionKind.POWER_SCHEME, ok, ok ? "" : "power-refused", "");
            }
            if (plan.Timer)
            {
                bool ok = ApplyTimer();
                if (ok) r.TimerOk = 1;
                Confirm(tx, ActionKind.TIMER_RESOLUTION, ok, ok ? "" : "timer-refused", "");
            }
            if (plan.Dvr)
            {
                bool ok = ApplyDvr();
                if (ok) r.DvrOk = 1;
                Confirm(tx, ActionKind.REG_DWORD, ok, ok ? "" : "dvr-refused", "");
            }
            if (plan.Memory)
            {
                try
                {
                    // A-9：与实时加速共享节流窗口，避免反复挤压游戏工作集。
                    if (SystemResourceArbiter.TryNoteMemoryTrim())
                    {
                        OptimizeResult m = MemEngine.Optimize(OptimizeLevel.Standard, gamePid, null, false);
                        r.MemReleased = m.Released;
                        r.MemoryOk = m.Released > 0 ? 1 : 0;
                    }
                    Confirm(tx, ActionKind.MEMORY_TRIM, true, "",
                        r.MemReleased.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                { Log.Write("boost memory: " + ex.Message); Confirm(tx, ActionKind.MEMORY_TRIM, false, ex.Message, ""); }
            }
            if (plan.Background && suspendCandidates != null)
            {
                try { r.Suspended = SuspendBackground(gamePid, suspendCandidates, tx); }
                catch (Exception ex) { Log.Write("boost suspend: " + ex.Message); }
            }

            r.Ok = r.PriorityOk + r.PowerOk + r.TimerOk + r.DvrOk + r.MemoryOk + r.AffinityOk + r.IrqOk > 0
                || r.IdealOk > 0 || r.Suspended > 0;
            r.NeedAdmin = !IsAdmin && (r.PowerOk == 0 || r.TimerOk == 0);
            Active = true;

            // 把「请求了什么、哪些没生效」记清楚:界面只报确认生效的数量。
            r.Requested = (plan.Priority ? 1 : 0) + (plan.PowerPlan ? 1 : 0) + (plan.Timer ? 1 : 0)
                + (plan.Dvr ? 1 : 0) + (plan.Memory ? 1 : 0) + (plan.Background ? 1 : 0)
                + (plan.Affinity ? 1 : 0) + (plan.Irq ? 1 : 0) + (plan.IdealCpu ? 1 : 0);
            if (plan.Priority && r.PriorityOk == 0)
                r.Skipped.Add(priorityFail.Length > 0 ? priorityFail : "boost.skip.priority");
            if (plan.PowerPlan && r.PowerOk == 0) r.Skipped.Add("boost.skip.power");
            if (plan.Timer && r.TimerOk == 0) r.Skipped.Add("boost.skip.timer");
            if (plan.Dvr && r.DvrOk == 0) r.Skipped.Add("boost.skip.dvr");
            if (plan.Memory && r.MemoryOk == 0) r.Skipped.Add("boost.skip.mem");
            if (plan.Background && r.Suspended == 0) r.Skipped.Add("boost.skip.bg");
            if (plan.Affinity && r.AffinityOk == 0) r.Skipped.Add("boost.skip.affinity");
            if (plan.Irq && r.IrqOk == 0) r.Skipped.Add("boost.skip.irq");
            if (plan.IdealCpu && r.IdealOk == 0) r.Skipped.Add("boost.skip.ideal");

            // COMMITTED：原子落盘；此后 journal 即"待还原凭据"。
            tx.Res = SystemResourceArbiter.Instance.Snapshot();
            tx.State = TxState.COMMITTED;
            BoostJournal.Save(tx);
            StartWatchdog();

            Log.Write("boost apply pid=" + gamePid + " ok=" + (r.Ok ? "1" : "0")
                + " prio=" + r.PriorityOk + (priorityFail.Length > 0 ? "(" + priorityFail + ")" : "")
                + " power=" + r.PowerOk + " timer=" + r.TimerOk
                + " dvr=" + r.DvrOk + " mem=" + (r.MemReleased / 1048576L) + "MB"
                + " suspended=" + r.Suspended
                + " affinity=" + r.AffinityOk + "/" + r.AffinityCores + "c/" + r.AffinityMoved + "p"
                + " irq=" + r.IrqOk + "/" + r.IrqDevices + "d"
                + " ideal=" + r.IdealOk + "t"
                + " tx=" + tx.TxId + " actions=" + tx.Actions.Count);
            return r;
        }

        // =================================================================
        //  SCTL v2 事务辅助（§3.4 先记后改 / 逐动作确认点）
        // =================================================================

        private const string IrqPolicyRel = @"\Device Parameters\Interrupt Management\Affinity Policy";
        private const string IrqEnumRoot = @"SYSTEM\CurrentControlSet\Enum\PCI";

        private static readonly string[][] DvrSpecs = new string[][]
        {
            new string[] { GameConfigStore, "GameDVR_Enabled", "0" },
            new string[] { GameConfigStore, "GameDVR_FSEBehaviorMode", "2" },
            new string[] { GameConfigStore, "GameDVR_HonorUserFSEBehaviorMode", "1" },
            new string[] { GameConfigStore, "GameDVR_DXGIHonorFSEWindowsCompatible", "1" },
            new string[] { GameDvrKey, "AppCaptureEnabled", "0" }
        };

        /// <summary>阶段一：把 9 类改动的 old 值全部捕获成 PENDING action，尚未改动系统。</summary>
        private void BuildPreparedActions(Transaction tx, BoostPlan plan, int pid, string name,
            int sid, CorePolicy policy, bool buildIdeal, bool useSchedulerAffinity,
            out BackgroundProc[] suspendCandidates)
        {
            ProcessGuard guard = BoostJournal.MakeGuard(pid, name, sid);

            if (plan.Priority)
            {
                uint oldClass = PeekPriority(pid);
                BoostJournal.NewAction(tx, ActionKind.PRIORITY_CLASS, guard,
                    oldClass.ToString(CultureInfo.InvariantCulture),
                    PriorityClassFor(plan.PriorityLevel).ToString(CultureInfo.InvariantCulture)).Owner = OwnerKind.ONE_SHOT;
            }
            if (buildIdeal)
            {
                JournalAction ia = BoostJournal.NewAction(tx, ActionKind.IDEAL_CPU, guard,
                    Native.MAXIMUM_PROCESSORS.ToString(CultureInfo.InvariantCulture),
                    Native.MAXIMUM_PROCESSORS.ToString(CultureInfo.InvariantCulture));
                ia.Target.Tid = MainThreadIdFor(pid);
            }
            if (useSchedulerAffinity)
            {
                ProcessTelemetry tel = MakeTelemetry(pid, name, plan);
                CorePlan cp = coreScheduler.Plan(CpuTopology.Snapshot(), tel, policy);
                long wanted = cp.GameMask != 0 ? cp.GameMask : CpuTopology.AllMask();
                BoostJournal.NewAction(tx, ActionKind.AFFINITY_MASK, guard, HexStr(PeekAffinity(pid)), HexStr(wanted));
            }
            else if (plan.Affinity)
            {
                int used;
                long wanted = CpuTopology.GameMask(plan.Cores, out used);
                if (wanted == 0) wanted = CpuTopology.AllMask();
                BoostJournal.NewAction(tx, ActionKind.AFFINITY_MASK, guard, HexStr(PeekAffinity(pid)), HexStr(wanted));
            }
            if (plan.Irq) CaptureIrqActions(tx, plan.Cores);

            if (plan.PowerPlan)
            {
                string cur = ActiveSchemeGuid();
                BoostJournal.NewAction(tx, ActionKind.POWER_SCHEME, new ProcessGuard(),
                    cur, SchemeHigh).Owner = OwnerKind.ONE_SHOT;
            }
            if (plan.Timer)
            {
                JournalAction ta = BoostJournal.NewAction(tx, ActionKind.TIMER_RESOLUTION,
                    new ProcessGuard(), "1", "1");
                ta.RequiresLiveOwner = true;
                ta.Owner = OwnerKind.ONE_SHOT;
            }
            if (plan.Dvr)
            {
                for (int i = 0; i < DvrSpecs.Length; i++)
                {
                    object oldValue = PeekDword(DvrSpecs[i][0], DvrSpecs[i][1]);
                    int ov = oldValue == null ? 0 : Convert.ToInt32(oldValue, CultureInfo.InvariantCulture);
                    int nv = int.Parse(DvrSpecs[i][2], CultureInfo.InvariantCulture);
                    JournalAction ra = BoostJournal.NewAction(tx, ActionKind.REG_DWORD, new ProcessGuard(),
                        BoostJournal.Base64Int(ov), BoostJournal.Base64Int(nv));
                    ra.Path = DvrSpecs[i][0];
                    ra.Name = DvrSpecs[i][1];
                    ra.Existed = oldValue != null;
                    ra.KindOfValue = ValueKind.DWORD;
                }
            }
            if (plan.Memory)
            {
                BoostJournal.NewAction(tx, ActionKind.MEMORY_TRIM, new ProcessGuard(), "", "").Revertible = false;
            }

            suspendCandidates = null;
            if (plan.Background)
            {
                suspendCandidates = Candidates(pid, 6);
                for (int i = 0; i < suspendCandidates.Length; i++)
                {
                    BackgroundProc b = suspendCandidates[i];
                    int procSid = sid;
                    try { using (Process bp = Process.GetProcessById(b.Pid)) procSid = bp.SessionId; }
                    catch { }
                    BoostJournal.NewAction(tx, ActionKind.SUSPEND_PROCESS,
                        BoostJournal.MakeGuard(b.Pid, b.Name, procSid), "RUNNING", "SUSPENDED");
                }
            }
        }

        /// <summary>把同类 PENDING action 标为已确认/失败并原子落盘——这就是逐动作确认点。</summary>
        private static void Confirm(Transaction tx, ActionKind kind, bool ok,
            string note, string newIfConfirmed)
        {
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            for (int i = 0; i < tx.Actions.Count; i++)
            {
                JournalAction a = tx.Actions[i];
                if (a.Kind != kind || a.Status != ActionStatus.PENDING) continue;
                if (ok)
                {
                    a.Status = ActionStatus.APPLIED_CONFIRMED;
                    a.AppliedUtc = now;
                    a.VerifiedUtc = now;
                    if (newIfConfirmed.Length > 0) a.New = newIfConfirmed;
                }
                else
                {
                    a.Status = ActionStatus.FAILED;
                    a.Note = note ?? "";
                }
            }
            BoostJournal.Save(tx);
        }

        private static ProcessTelemetry MakeTelemetry(int pid, string name, BoostPlan plan)
        {
            var tel = new ProcessTelemetry();
            tel.Pid = pid;
            tel.Name = name ?? "";
            tel.MainTid = MainThreadIdFor(pid);
            tel.Cores = plan == null ? 0 : plan.Cores;
            // 本项目无逐游戏 CPU 受限采样数据，默认视为 CPU 受限；GPU 受限场景由用户关闭策略。
            tel.CpuBound = true;
            tel.AntiCheatDenied = false;
            try
            {
                using (Process p = Process.GetProcessById(pid)) tel.StartTime = p.StartTime.Ticks;
            }
            catch { tel.StartTime = 0; }
            return tel;
        }

        /// <summary>硬亲和管理者把别人的进程挪走后，把它们的原掩码补记进同一事务。</summary>
        private void AppendMovedAffinityActions(Transaction tx, int pid, string name, int sid)
        {
            AffinitySnapshot snap = affinity.Snapshot();
            if (snap == null || snap.Moved == null) return;
            for (int i = 0; i < snap.Moved.Count; i++)
            {
                MaskSnapshot m = snap.Moved[i];
                ProcessGuard g = BoostJournal.MakeGuard(m.Pid, m.Name, -1);
                JournalAction a = BoostJournal.NewAction(tx, ActionKind.AFFINITY_MASK, g,
                    HexStr(m.Mask), HexStr(m.Mask & ~snap.GameMask));
                a.Status = ActionStatus.APPLIED_CONFIRMED;
                a.AppliedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>IRQ 原值在改动前从 HKLM 读取；只记录真正写入的设备。</summary>
        private static void CaptureIrqActions(Transaction tx, int cores)
        {
            if (!IsAdmin) return;
            int used;
            long game = CpuTopology.GameMask(cores, out used);
            long avoid = CpuTopology.AllMask() & ~game;
            if (avoid == 0) return;
            byte[] newBytes = MaskBytes(avoid);
            IrqDevice[] devs = InterruptEngine.Enumerate();
            for (int i = 0; i < devs.Length; i++)
            {
                string path = IrqEnumRoot + "\\" + devs[i].Key + IrqPolicyRel;
                bool existed = false;
                bool hadPolicy = false, hadMask = false;
                int policy = -1;
                byte[] oldMask = new byte[0];
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                    {
                        existed = k != null;
                        if (k != null)
                        {
                            object pv = k.GetValue("DevicePolicy", null);
                            if (pv != null)
                            {
                                hadPolicy = true;
                                policy = Convert.ToInt32(pv, CultureInfo.InvariantCulture);
                            }
                            object mv = k.GetValue("AssignmentSetOverride", null);
                            if (mv is byte[])
                            {
                                hadMask = true;
                                oldMask = (byte[])mv;
                            }
                        }
                    }
                }
                catch (Exception ex) { Log.Write("boost irq peek: " + ex.Message); }

                JournalAction a = BoostJournal.NewAction(tx, ActionKind.IRQ_AFFINITY, new ProcessGuard(),
                    BoostJournal.Base64(oldMask), BoostJournal.Base64(newBytes));
                a.Path = path;
                a.Name = BoostJournal.EncodeIrqMeta(policy, hadPolicy, hadMask);
                a.Existed = existed;
                a.CreatedKey = !existed;
                a.Owner = OwnerKind.ONE_SHOT;
            }
        }

        private static byte[] MaskBytes(long mask)
        {
            int n = Environment.Is64BitOperatingSystem ? 8 : 4;
            byte[] b = new byte[n];
            ulong u = (ulong)mask;
            for (int i = 0; i < n; i++) b[i] = (byte)(u >> (i * 8));
            return b;
        }

        private static uint PeekPriority(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return Native.NORMAL_PRIORITY_CLASS;
            try { return Native.GetPriorityClass(h); }
            catch { return Native.NORMAL_PRIORITY_CLASS; }
            finally { Native.CloseHandle(h); }
        }

        private static long PeekAffinity(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                IntPtr procMask, sysMask;
                if (!Native.GetProcessAffinityMask(h, out procMask, out sysMask)) return 0;
                return procMask.ToInt64();
            }
            catch { return 0; }
            finally { Native.CloseHandle(h); }
        }

        private static object PeekDword(string path, string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path))
                    return k == null ? null : k.GetValue(name, null);
            }
            catch { return null; }
        }

        private static string HexStr(long value)
        {
            return value.ToString("X", CultureInfo.InvariantCulture);
        }

        /// <summary>看门狗跟随父进程；意外退出后，下一次加速可以重新拉起。</summary>
        public static void EnsureWatchdog()
        {
            lock (WatchdogLock)
            {
                try
                {
                    if (watchdogProcess != null)
                    {
                        if (!watchdogProcess.HasExited) return;
                        watchdogProcess.Dispose();
                        watchdogProcess = null;
                    }
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    string args = "--watchdog " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
                    var psi = new ProcessStartInfo(exe, args);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    watchdogProcess = Process.Start(psi);
                    if (watchdogProcess != null) Log.Write("watchdog started pid=" + watchdogProcess.Id);
                }
                catch (Exception ex)
                {
                    watchdogProcess = null;
                    Log.Write("watchdog start: " + ex.Message);
                }
            }
        }

        private static void StartWatchdog()
        {
            EnsureWatchdog();
        }

        // Reconcile 及 selftest 用的内部入口（不改动原有私有方法语义）。
        internal static int MainThreadIdFor(int pid) { return MainThreadId(pid); }
        internal static bool SetIdealFor(int tid, int cpu) { return SetIdeal(tid, cpu); }

        /// <summary>
        /// 调整游戏进程优先级。带反作弊的游戏经常直接拒绝这类请求,所以这里把
        /// 「被拒绝」和普通失败分开报,并且回读一次确认真的生效——SetPriorityClass
        /// 返回成功但优先级没变的情况是存在的,不能据此谎报"已优化"。
        /// failKey 为空表示失败原因不明,由调用方用通用的「优先级」文案。
        /// </summary>
        private bool ApplyPriority(int pid, int level, Transaction tx, out string failKey, out int applied)
        {
            failKey = "";
            applied = level;
            bool denied;
            if (TrySetPriority(pid, level, tx, out denied)) return true;

            // 游戏刚启动时保护组件可能还没装好,隔半秒再试一次。
            // 自动加速跑在线程池里,不会卡住界面。
            if (denied)
            {
                try { System.Threading.Thread.Sleep(400); }
                catch { }
                if (TrySetPriority(pid, level, tx, out denied)) return true;
            }

            // 不少带反作弊的游戏只拦「高」,会放过「高于正常」。退一档再试,
            // 至少让优先级真的比默认值高,而不是整项作废。
            if (level >= 4 && TrySetPriority(pid, 3, tx, out denied))
            {
                applied = 3;
                Log.Write("boost priority: high denied, fell back to above normal");
                return true;
            }

            if (denied) failKey = IsAdmin ? "boost.skip.priorityDenied" : "boost.skip.priorityAdmin";
            return false;
        }

        /// <summary>档位 -> Win32 优先级类;档位越界时按「高」处理。</summary>
        public static uint PriorityClassFor(int level)
        {
            switch (level)
            {
                case 0: return Native.IDLE_PRIORITY_CLASS;
                case 1: return Native.BELOW_NORMAL_PRIORITY_CLASS;
                case 2: return Native.NORMAL_PRIORITY_CLASS;
                case 3: return Native.ABOVE_NORMAL_PRIORITY_CLASS;
                default: return Native.HIGH_PRIORITY_CLASS;
            }
        }

        /// <summary>档位对应的界面文案键。</summary>
        public static string PriorityKey(int level)
        {
            if (level < 0) level = 0;
            if (level > 4) level = 4;
            return "boost.prio." + level;
        }

        private static void NotePriorityAttempt(Transaction tx, int pid, uint cls)
        {
            if (tx == null) return;
            for (int i = tx.Actions.Count - 1; i >= 0; i--)
            {
                JournalAction a = tx.Actions[i];
                if (a.Kind != ActionKind.PRIORITY_CLASS || a.Target == null || a.Target.Pid != pid) continue;
                a.New = cls.ToString(CultureInfo.InvariantCulture);
                a.NewClass = (int)cls;
                a.Status = ActionStatus.PENDING;
                BoostJournal.Save(tx);
                return;
            }
        }

        private static void NoteIdealAttempt(Transaction tx, int pid, int tid, int cpu)
        {
            if (tx == null) return;
            for (int i = tx.Actions.Count - 1; i >= 0; i--)
            {
                JournalAction a = tx.Actions[i];
                if (a.Kind != ActionKind.IDEAL_CPU || a.Target == null || a.Target.Pid != pid) continue;
                a.Target.Tid = tid;
                a.New = cpu.ToString(CultureInfo.InvariantCulture);
                a.NewClass = cpu;
                a.Status = ActionStatus.PENDING;
                BoostJournal.Save(tx);
                return;
            }
        }

        private bool TrySetPriority(int pid, int level, Transaction tx, out bool denied)
        {
            denied = false;
            uint cls = PriorityClassFor(level);
            IntPtr h = IntPtr.Zero;
            try
            {
                h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero)
                {
                    // ERROR_ACCESS_DENIED:游戏比我们权限高,或被反作弊拦下。
                    denied = Marshal.GetLastWin32Error() == 5;
                    return false;
                }

                uint before = Native.GetPriorityClass(h);
                NotePriorityAttempt(tx, pid, cls);
                if (!Native.SetPriorityClass(h, cls))
                {
                    denied = Marshal.GetLastWin32Error() == 5;
                    return false;
                }

                // 回读校验:返回 true 不代表值变了。
                uint after_ = Native.GetPriorityClass(h);
                if (after_ != 0 && after_ != cls)
                {
                    denied = true;
                    return false;
                }

                prevPriority = before;
                return true;
            }
            catch { return false; }
            finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
        }

        /// <summary>
        /// 实验项:逐个给游戏线程设置「理想处理器」,让调度器优先把线程放到性能核上。
        /// 只调用 SetThreadIdealProcessor,绝不修改亲和性掩码;权限不足或被反作弊
        /// 忽略时静默返回 0,不影响其它加速项。
        /// </summary>
        private int ApplyIdealCpu(int pid, int cores, Transaction tx)
        {
            int ideal = IdealProcessor(cores);
            int ok = 0;
            // FF-026：所有线程不再一起塞到同一颗核上，而是按可用核轮流分配，
            // 主线程仍优先落到 IdealProcessor 挑出的那颗。
            int[] cpus = TargetCpus(cores);
            int turn = 0;
            ProcessGuard processGuard = BoostJournal.MakeGuard(pid, GameName, -1);
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPTHREAD, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
            try
            {
                var e = new Native.THREADENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(Native.THREADENTRY32));
                if (!Native.Thread32First(snap, ref e)) return 0;
                do
                {
                    if (e.th32OwnerProcessID != (uint)pid) continue;
                    int tid = (int)e.th32ThreadID;
                    int cpu = cpus.Length == 0 ? ideal : cpus[turn++ % cpus.Length];
                    IntPtr th = Native.OpenThread(Native.THREAD_SET_INFORMATION, false, tid);
                    if (th == IntPtr.Zero) continue;
                    JournalAction action = null;
                    try
                    {
                        var guard = new ProcessGuard();
                        guard.Pid = processGuard.Pid;
                        guard.Name = processGuard.Name;
                        guard.SessionId = processGuard.SessionId;
                        guard.StartTime = processGuard.StartTime;
                        guard.Tid = tid;
                        action = BoostJournal.NewAction(tx, ActionKind.IDEAL_CPU, guard,
                            Native.MAXIMUM_PROCESSORS.ToString(CultureInfo.InvariantCulture),
                            cpu.ToString(CultureInfo.InvariantCulture));
                        action.NewClass = cpu;
                        // FF-026：只在循环结束后统一落盘一次，不再每个线程写一遍 journal。
                        uint previous = Native.SetThreadIdealProcessor(th, (uint)cpu);
                        if (previous != 0xFFFFFFFF)
                        {
                            ok++;
                            action.Old = previous.ToString(CultureInfo.InvariantCulture);
                            action.Status = ActionStatus.APPLIED_CONFIRMED;
                            action.AppliedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                            action.VerifiedUtc = action.AppliedUtc;
                        }
                        else
                        {
                            action.Status = ActionStatus.FAILED;
                            action.Note = "ideal-refused";
                        }
                    }
                    catch (Exception ex)
                    {
                        if (action != null) { action.Status = ActionStatus.FAILED; action.Note = ex.Message; }
                    }
                    finally { Native.CloseHandle(th); }
                }
                while (Native.Thread32Next(snap, ref e));
            }
            finally
            {
                Native.CloseHandle(snap);
                BoostJournal.Save(tx);
            }
            return ok;
        }

        /// <summary>挑一颗性能核作为理想处理器;启用核心独占时优先用游戏自己的核。</summary>
        private static int IdealProcessor(int cores)
        {
            CpuCore[] all = CpuTopology.Cores();
            if (all.Length == 0) return 0;
            CpuCore best = null;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Parked) continue;
                if (best == null) { best = all[i]; continue; }
                // 先看最高频率(P-core),频率相同再退回 EfficiencyClass 与编号,
                // 避免不同 OEM 对 EfficiencyClass 的编号差异把理想核选到 E-core。
                if (all[i].Mhz > best.Mhz) { best = all[i]; continue; }
                if (all[i].Mhz == best.Mhz)
                {
                    if (all[i].Efficiency < best.Efficiency) best = all[i];
                    else if (all[i].Efficiency == best.Efficiency && all[i].Index < best.Index) best = all[i];
                }
            }
            if (best == null) best = all[0];

            if (cores > 0)
            {
                int used;
                long mask = CpuTopology.GameMask(cores, out used);
                if (mask != 0)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i].Parked) continue;
                        if ((all[i].Mask & mask) != 0) { best = all[i]; break; }
                    }
                }
            }
            return best.Index;
        }

        /// <summary>
        /// FF-026：可分配的逻辑核列表(0..63)。核心独占时只取游戏自己的核，
        /// 其余情况取全部未停用核；线程按这份列表轮流设置理想处理器。
        /// </summary>
        private static int[] TargetCpus(int cores)
        {
            var list = new List<int>();
            int used;
            long mask = cores > 0 ? CpuTopology.GameMask(cores, out used) : CpuTopology.AllMask();
            if (mask == 0) mask = CpuTopology.AllMask();
            for (int i = 0; i < 64; i++)
                if ((mask & (1L << i)) != 0) list.Add(i);
            return list.ToArray();
        }

        /// <summary>
        /// 动态负载平衡:由界面每秒调用一次,内部节流到每 2 分钟一次。
        /// 重新挑当前最空闲的核心,把游戏主线程的理想处理器指过去。
        /// </summary>
        public void SteerIfDue()
        {
            if (!Active || GamePid <= 0) return;
            // P1-B：再平衡周期可配（coreRebalanceSeconds），默认 120 与原 SteerSeconds 一致。
            if ((DateTime.UtcNow - lastSteer).TotalSeconds < CoreConfig.RebalanceSeconds()) return;
            if (Interlocked.CompareExchange(ref steerBusy, 1, 0) != 0) return;
            lastSteer = DateTime.UtcNow;
            int pid = GamePid;
            int cores = steerCores;
            CorePolicy policy = CoreConfig.Policy();
            ICoreScheduler scheduler = coreScheduler;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // P1：非 Auto 策略把再平衡决策交给策略层；返回 null 表示滞后不迁移。
                    if (policy == CorePolicy.Auto)
                    {
                        SteerOnce(pid, cores, policy);
                    }
                    else
                    {
                        CpuTopologySnapshot topo = CpuTopology.Snapshot();
                        ProcessTelemetry tel = MakeTelemetry(pid, GameName, null);
                        CorePlan current = new CorePlan();
                        current.Policy = policy;
                        current.Cores = cores;
                        current.IdealCpu = SteerCpu;
                        current.Reason = "rebalance";
                        CorePlan next = scheduler.Rebalance(topo, tel, current);
                        if (next != null && next.IdealCpu >= 0)
                        {
                            int tid = MainThreadIdFor(pid);
                            if (tid > 0 && SetIdealFor(tid, next.IdealCpu))
                            {
                                SteerCpu = next.IdealCpu;
                                IdealApplied = true;
                                scheduler.MarkIdealApplied(policy, next.IdealCpu);
                                Log.Write("boost steer rebalance pid=" + pid + " cpu=" + next.IdealCpu);
                            }
                        }
                    }
                }
                catch (Exception ex) { Log.Write("boost steer: " + ex.Message); }
                finally { Interlocked.Exchange(ref steerBusy, 0); }
            });
        }

        /// <summary>同步执行一次软引导(加速时首帧用,调用方已在后台线程)。</summary>
        private void TrySteer(int cores)
        {
            TrySteer(cores, CorePolicy.Auto);
        }

        private void TrySteer(int cores, CorePolicy policy)
        {
            steerCores = cores;
            lastSteer = DateTime.UtcNow;
            SteerOnce(GamePid, cores, policy);
        }

        /// <summary>
        /// 采样两次每核时间算出单核占用,挑最空闲的一颗,把游戏主线程指过去。
        /// CPU 0 承担网卡/鼠标等中断和大量系统杂务,几乎不会是最闲的那颗,
        /// 所以「挑最闲」这条规则本身就实现了与系统核心错峰,而不需要硬性避开。
        /// </summary>
        private int SteerOnce(int pid, int cores)
        {
            return SteerOnce(pid, cores, CorePolicy.Auto);
        }

        private int SteerOnce(int pid, int cores, CorePolicy policy)
        {
            int cpu = PickIdleCore(cores, policy);
            // P0-B：>64 核或采样失败时返回 -1（跳过），不再退化成 CPU 0。
            if (cpu < 0) return -1;
            int tid = MainThreadId(pid);
            if (tid <= 0) return -1;
            NoteIdealAttempt(currentTx, pid, tid, cpu);
            if (!SetIdeal(tid, cpu)) return -1;

            if (SteerCpu != cpu) Log.Write("boost steer pid=" + pid + " cpu=" + cpu);
            SteerCpu = cpu;
            IdealApplied = true;
            return cpu;
        }

        /// <summary>
        /// 两次采样之间各逻辑核的忙碌占比,取最低的那个。
        /// 负载相同(差值在 0.05% 内)时优先编号非 0 的核心,把 CPU 0 让给系统中断。
        /// </summary>
        private static int PickIdleCore(int cores)
        {
            return PickIdleCore(cores, CorePolicy.Auto);
        }

        private static int PickIdleCore(int cores, CorePolicy policy)
        {
            long[] idle1, busy1, idle2, busy2;
            // P0-B：Native.CpuCoreTimes 已改为动态容量；任一次采样失败即放弃本轮引导。
            if (Native.CpuCoreTimes(out idle1, out busy1) <= 0) return -1;
            try { Thread.Sleep(SteerSampleMs); } catch { }
            if (Native.CpuCoreTimes(out idle2, out busy2) <= 0) return -1;

            int n = Math.Min(Math.Min(idle1.Length, busy1.Length), Math.Min(idle2.Length, busy2.Length));
            if (n <= 0) return -1;

            // 启用核心独占时只在游戏自己的核里挑,不会把主线程送到被排除的核上。
            long gameMask = 0;
            CpuCore[] all = CpuTopology.Cores();
            if (cores > 0)
            {
                int used;
                gameMask = CpuTopology.GameMask(cores, out used);
            }

            double hysteresis = CoreConfig.Hysteresis();
            int best = -1;
            double bestLoad = 2;
            for (int i = 0; i < all.Length; i++)
            {
                CpuCore c = all[i];
                if (c.Parked) continue;
                // P1-A：PCoreFirst 只在 P 核候选中挑最闲核。
                if (policy == CorePolicy.PCoreFirst && c.Efficient) continue;
                if (gameMask != 0 && (c.Mask & gameMask) == 0) continue;
                int idx = c.Index;
                // SetThreadIdealProcessor 只支持 0..63；>63 的核不参与本轮引导。
                if (idx < 0 || idx >= n || idx >= 64) continue;

                long di = idle2[idx] - idle1[idx];
                long db = busy2[idx] - busy1[idx];
                long total = di + db;
                if (total <= 0) total = 1;
                double load = (double)db / total;

                if (best < 0 || load < bestLoad - hysteresis) { best = idx; bestLoad = load; }
                else if (best == 0 && idx != 0 && Math.Abs(load - bestLoad) <= hysteresis) best = idx;
            }
            return best < 0 ? -1 : best;
        }

        /// <summary>进程里 TID 最小的线程就是主线程(游戏逻辑与渲染提交都压在它上面)。</summary>
        private static int MainThreadId(int pid)
        {
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPTHREAD, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return -1;
            int main = -1;
            try
            {
                var e = new Native.THREADENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(Native.THREADENTRY32));
                if (!Native.Thread32First(snap, ref e)) return -1;
                do
                {
                    if (e.th32OwnerProcessID != (uint)pid) continue;
                    int tid = (int)e.th32ThreadID;
                    if (main < 0 || tid < main) main = tid;
                }
                while (Native.Thread32Next(snap, ref e));
            }
            catch { return -1; }
            finally { Native.CloseHandle(snap); }
            return main;
        }

        /// <summary>设置理想处理器;MAXIMUM_PROCESSORS 表示「交给调度器自己挑」。</summary>
        private static bool SetIdeal(int tid, int cpu)
        {
            IntPtr th = Native.OpenThread(Native.THREAD_SET_INFORMATION, false, tid);
            if (th == IntPtr.Zero) return false;
            try { return Native.SetThreadIdealProcessor(th, (uint)cpu) != 0xFFFFFFFF; }
            catch { return false; }
            finally { Native.CloseHandle(th); }
        }

        private bool ApplyPowerPlan()
        {
            try
            {
                string cur = ActiveSchemeGuid();
                if (cur.Length == 0) return false;
                // FF-011：删除「已经是高性能就直接返回」的捷径。那一刻的高性能可能由
                // 实时加速持有，直接返回会让一键加速不占引用，实时加速一停就把方案拨回去。
                string captured = cur;
                AcquireResult ar = SystemResourceArbiter.Instance.Acquire(this, ResourceKind.PowerScheme,
                    delegate { return captured; },
                    delegate { return SetScheme(SchemeHigh); });
                if (!ar.Ok) return false;
                // SHARED 同样持有一个引用，停止时必须对称 Release。
                powerAcquired = true;
                if (ar.State == AcquireState.GRANTED) prevScheme = ar.Old;
                return true;
            }
            catch (Exception ex) { Log.Write("boost power: " + ex.Message); return false; }
        }

        private bool ApplyTimer()
        {
            try
            {
                // §A2/A3：timer 引用计数收敛到仲裁者，Begin/End 严格配对。
                AcquireResult ar = SystemResourceArbiter.Instance.Acquire(this, ResourceKind.TimerResolution,
                    delegate { return "1"; },
                    delegate { return Native.timeBeginPeriod(1) == 0; });
                if (!ar.Ok) return false;
                timerAcquired = true;
                timerSet = ar.State == AcquireState.GRANTED;
                return true;
            }
            catch (Exception ex) { Log.Write("boost timer: " + ex.Message); }
            return false;
        }

        private bool ApplyDvr()
        {
            try
            {
                SetDword(GameConfigStore, "GameDVR_Enabled", 0);
                SetDword(GameConfigStore, "GameDVR_FSEBehaviorMode", 2);
                SetDword(GameConfigStore, "GameDVR_HonorUserFSEBehaviorMode", 1);
                SetDword(GameConfigStore, "GameDVR_DXGIHonorFSEWindowsCompatible", 1);
                SetDword(GameDvrKey, "AppCaptureEnabled", 0);
                return true;
            }
            catch (Exception ex) { Log.Write("boost dvr: " + ex.Message); return false; }
        }

        private int SuspendBackground(int gamePid)
        {
            return SuspendBackground(gamePid, Candidates(gamePid, 6), null);
        }

        /// <summary>使用 PREPARED 阶段已捕获的候选列表，挂起成功即原子落确认点。</summary>
        private int SuspendBackground(int gamePid, BackgroundProc[] list, Transaction tx)
        {
            if (list == null) return 0;
            int done = 0;
            for (int i = 0; i < list.Length; i++)
            {
                BackgroundProc b = list[i];
                IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, b.Pid);
                if (h == IntPtr.Zero)
                {
                    MarkSuspend(tx, b.Pid, false, "open-failed");
                    continue;
                }
                try
                {
                    if (Native.NtSuspendProcess(h) == 0)
                    {
                        suspended.Add(new Suspended { Pid = b.Pid, Name = b.Name, WorkingSet = b.WorkingSet });
                        done++;
                        MarkSuspend(tx, b.Pid, true, "");
                    }
                    else MarkSuspend(tx, b.Pid, false, "suspend-refused");
                }
                catch (Exception ex) { MarkSuspend(tx, b.Pid, false, ex.Message); }
                finally { Native.CloseHandle(h); }
            }
            return done;
        }

        private static void MarkSuspend(Transaction tx, int pid, bool ok, string note)
        {
            if (tx == null) return;
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            for (int i = 0; i < tx.Actions.Count; i++)
            {
                JournalAction a = tx.Actions[i];
                if (a.Kind != ActionKind.SUSPEND_PROCESS || a.Target == null
                    || a.Target.Pid != pid || a.Status != ActionStatus.PENDING) continue;
                if (ok)
                {
                    a.Status = ActionStatus.APPLIED_CONFIRMED;
                    a.AppliedUtc = now;
                    a.VerifiedUtc = now;
                }
                else
                {
                    a.Status = ActionStatus.FAILED;
                    a.Note = note ?? "";
                }
            }
            BoostJournal.Save(tx);
        }

        // =================================================================
        //  还原
        // =================================================================

        public void Restore()
        {
            if (!Active) return;
            Transaction tx = BoostJournal.Load();
            if (tx == null) tx = currentTx;

            // 同进程快速路径：先把内存态原值拨回去；随后统一走 Reconcile 做一致性复核
            // 并复用它的"未决项为 0 才清日志"判定（消除内存版/磁盘版两套 Restore 分叉）。
            bool clean = true;
            bool timerEnded = false;
            try { irq.Restore(); }
            catch (Exception ex) { clean = false; Log.Write("boost restore irq: " + ex.Message); }
            try { affinity.Restore(); clean &= coreSchedulerRestored(); }
            catch (Exception ex) { clean = false; Log.Write("boost restore affinity: " + ex.Message); }

            for (int i = suspended.Count - 1; i >= 0; i--)
            {
                Suspended s = suspended[i];
                IntPtr h = IntPtr.Zero;
                try
                {
                    h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, s.Pid);
                    if (h != IntPtr.Zero && string.Equals(Native.ProcessName(s.Pid), s.Name, StringComparison.OrdinalIgnoreCase))
                        Native.NtResumeProcess(h);
                }
                catch { }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
            }
            suspended.Clear();

            if (timerAcquired)
            {
                // I3：只有 holder 且引用计数归零时才 End；幂等由仲裁者保证。
                SystemResourceArbiter.Instance.Release(this, ResourceKind.TimerResolution,
                    delegate(string old) { Native.timeEndPeriod(1); timerEnded = true; return true; });
                timerAcquired = false;
                timerSet = false;
            }
            else if (timerSet)
            {
                try { Native.timeEndPeriod(1); timerEnded = true; }
                catch (Exception ex) { clean = false; Log.Write("boost restore timer: " + ex.Message); }
                timerSet = false;
            }
            if (timerEnded && tx != null)
            {
                BoostJournal.MarkReverted(tx, ActionKind.TIMER_RESOLUTION);
                BoostJournal.Save(tx);
            }

            if (!RestoreRegistry()) clean = false;

            if (powerAcquired)
            {
                SystemResourceArbiter.Instance.Release(this, ResourceKind.PowerScheme,
                    delegate(string old)
                    {
                        // 幂等读回：current == new(SchemeHigh) 才写回 old。
                        string cur = ActiveSchemeGuid();
                        if (string.Equals(cur, SchemeHigh, StringComparison.OrdinalIgnoreCase))
                            return SetScheme(old);
                        Log.Write("boost restore power: current changed by user, skipped");
                        return true;
                    });
                powerAcquired = false;
                prevScheme = "";
            }
            else if (prevScheme.Length > 0)
            {
                try { SetScheme(prevScheme); }
                catch (Exception ex) { clean = false; Log.Write("boost restore power plan: " + ex.Message); }
                prevScheme = "";
            }

            if (prevPriority != 0 && GamePid > 0)
            {
                IntPtr h = IntPtr.Zero;
                try
                {
                    // PID 复用防护：名字不一致绝不写回旧档位。
                    if (string.Equals(Native.ProcessName(GamePid), GameName, StringComparison.OrdinalIgnoreCase))
                    {
                        h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION, false, GamePid);
                        if (h != IntPtr.Zero) Native.SetPriorityClass(h, prevPriority);
                    }
                }
                catch (Exception ex) { Log.Write("boost restore priority: " + ex.Message); }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
                prevPriority = 0;
            }

            // 软引导只是调度提示,进程退出就没了;还原时主动把主线程指回「任意核心」。
            if (GamePid > 0)
            {
                int tid = MainThreadId(GamePid);
                if (tid > 0) SetIdeal(tid, (int)Native.MAXIMUM_PROCESSORS);
            }
            lastSteer = DateTime.MinValue;
            SteerCpu = -1;
            steerBusy = 0;

            int restoredPid = GamePid;
            string restoredName = GameName;
            Active = false;
            StartedAt = DateTime.MinValue;
            GamePid = -1;
            GameName = "";
            PriorityLevel = -1;
            IdealApplied = false;
            needPower = false;
            needTimer = false;

            // S3：统一入口。快速路径已还原的值会被对账判为 current==old → noop；
            // 清日志与否完全由 ReconcileReport.Clean 决定。
            if (tx != null)
            {
                var ctx = new ReconcileContext();
                ctx.IsSameProcess = true;
                ctx.IsAdmin = IsAdmin;
                ReconcileReport rep = BoostJournal.Reconcile(tx, ctx);
                clean = clean && rep.Clean;
                Log.Write("boost restore reconcile: reverted=" + rep.Reverted + " noop=" + rep.Noop
                    + " conflict=" + rep.Conflict + " failed=" + rep.Failed);
            }
            else if (!clean)
            {
                Log.Write("boost restore incomplete, journal kept for next start");
            }
            if (!clean) Log.Write("boost restore incomplete (pid=" + restoredPid + " " + restoredName + ")");
            Log.Write(clean ? "boost restore" : "boost restore (incomplete)");
            currentTx = null;
        }

        private bool coreSchedulerRestored()
        {
            try { coreScheduler.Restore(); return true; }
            catch (Exception ex) { Log.Write("boost restore core scheduler: " + ex.Message); return false; }
        }

        /// <summary>
        /// SCTL v2 §3.3：把当前这次改动的 9 类状态整理成事务快照。
        /// Apply 走 BuildPreparedActions（改动前捕获）；此处保留为"当前值"快照，
        /// 供诊断/自检对照，不参与 Apply 的先记后改路径。
        /// </summary>
        internal Transaction Snapshot()
        {
            int sid = -1;
            try { using (Process p = Process.GetProcessById(GamePid)) sid = p.SessionId; }
            catch { }
            Transaction tx = BoostJournal.NewTransaction(
                OwnerKind.ONE_SHOT, GamePid, GameName, sid);
            ProcessGuard guard = BoostJournal.MakeGuard(GamePid, GameName, sid);

            if (PriorityLevel >= 0)
                BoostJournal.NewAction(tx, ActionKind.PRIORITY_CLASS, guard,
                    prevPriority.ToString(CultureInfo.InvariantCulture),
                    PriorityClassFor(PriorityLevel).ToString(CultureInfo.InvariantCulture));
            if (IdealApplied)
                BoostJournal.NewAction(tx, ActionKind.IDEAL_CPU, guard,
                    Native.MAXIMUM_PROCESSORS.ToString(CultureInfo.InvariantCulture),
                    SteerCpu.ToString(CultureInfo.InvariantCulture));
            if (AffinityMask != 0)
                BoostJournal.NewAction(tx, ActionKind.AFFINITY_MASK, guard,
                    HexStr(affinity.Snapshot().OriginalGameMask), HexStr(AffinityMask));
            for (int i = 0; i < affinity.Snapshot().Moved.Count; i++)
            {
                MaskSnapshot m = affinity.Snapshot().Moved[i];
                BoostJournal.NewAction(tx, ActionKind.AFFINITY_MASK,
                    BoostJournal.MakeGuard(m.Pid, m.Name, -1), HexStr(m.Mask), HexStr(m.Mask & ~AffinityMask));
            }
            if (prevScheme.Length > 0)
                BoostJournal.NewAction(tx, ActionKind.POWER_SCHEME, new ProcessGuard(), prevScheme, SchemeHigh);
            if (timerSet)
            {
                JournalAction ta = BoostJournal.NewAction(tx, ActionKind.TIMER_RESOLUTION,
                    new ProcessGuard(), "1", "1");
                ta.RequiresLiveOwner = true;
            }
            for (int i = 0; i < regSaved.Count; i++)
            {
                RegSaved s = regSaved[i];
                int ov = 0;
                if (s.Value != null)
                {
                    try { ov = Convert.ToInt32(s.Value, CultureInfo.InvariantCulture); } catch { ov = 0; }
                }
                JournalAction ra = BoostJournal.NewAction(tx, ActionKind.REG_DWORD, new ProcessGuard(),
                    BoostJournal.Base64Int(ov), BoostJournal.Base64Int(ov));
                ra.Path = s.Path;
                ra.Name = s.Name;
                ra.Existed = s.Exists;
                ra.KindOfValue = ValueKind.DWORD;
            }
            for (int i = 0; i < suspended.Count; i++)
            {
                Suspended s = suspended[i];
                BoostJournal.NewAction(tx, ActionKind.SUSPEND_PROCESS,
                    BoostJournal.MakeGuard(s.Pid, s.Name, sid), "RUNNING", "SUSPENDED");
            }
            BoostJournal.NewAction(tx, ActionKind.MEMORY_TRIM, new ProcessGuard(), "", "").Revertible = false;
            tx.State = TxState.COMMITTED;
            tx.Res = SystemResourceArbiter.Instance.Snapshot();
            return tx;
        }

        private class RegSaved
        {
            public string Path;
            public string Name;
            public bool Exists;
            public object Value;
        }

        private void SetDword(string path, string name, int value)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(path))
            {
                if (k == null) return;
                object old = k.GetValue(name, null);
                regSaved.Add(new RegSaved { Path = path, Name = name, Exists = old != null, Value = old });
                k.SetValue(name, value, RegistryValueKind.DWord);
            }
        }

        private bool RestoreRegistry()
        {
            bool ok = true;
            for (int i = regSaved.Count - 1; i >= 0; i--)
            {
                RegSaved s = regSaved[i];
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(s.Path))
                    {
                        if (k == null) continue;
                        if (s.Exists) k.SetValue(s.Name, s.Value);
                        else k.DeleteValue(s.Name, false);
                    }
                }
                catch (Exception ex)
                {
                    ok = false;
                    Log.Write("boost restore registry " + s.Path + "\\" + s.Name + ": " + ex.Message);
                }
            }
            regSaved.Clear();
            return ok;
        }

        /// <summary>
        /// 自检用:只碰 HKCU 下自建的沙箱键,走一遍"记原值 → 改值 → 还原",
        /// 确认注册表快照真的能把值拨回原样。不接触电源计划、计时器等真实系统设置。
        /// </summary>
        public static bool SelfTestRegistryRoundTrip()
        {
            const string key = @"Software\FrameForge\Selftest";
            const string name = "probe";
            // 不进 using:Dispose 会触发 Restore,这里只需要注册表这一小块逻辑。
            var engine = new BoostEngine();
            bool ok = true;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key))
                {
                    if (k == null) return false;
                    k.DeleteValue(name, false);
                }

                engine.SetDword(key, name, 7);                      // 原来不存在 → 还原时应该删掉
                ok = ReadDword(key, name) == 7;
                ok = engine.RestoreRegistry() && ok;
                ok = ok && ReadDword(key, name) == -1;

                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key))
                {
                    if (k == null) return false;
                    k.SetValue(name, 5, RegistryValueKind.DWord);
                }
                engine.SetDword(key, name, 9);                  // 原来是 5 → 还原应回到 5
                ok = ok && ReadDword(key, name) == 9;
                ok = engine.RestoreRegistry() && ok;
                ok = ok && ReadDword(key, name) == 5;
            }
            catch (Exception ex)
            {
                Log.Write("selftest registry round trip: " + ex.Message);
                ok = false;
            }
            finally
            {
                try { engine.RestoreRegistry(); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(key, false); } catch { }
            }
            return ok;
        }

        private static int ReadDword(string path, string name)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path))
            {
                if (k == null) return -1;
                object v = k.GetValue(name, null);
                if (v == null) return -1;
                return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            }
        }

        // =================================================================
        //  电源计划
        // =================================================================

        public static string ActiveSchemeGuid()
        {
            try
            {
                string outp = Run(Native.System32("powercfg.exe"), "/getactivescheme");
                Match m = GuidRx.Match(outp);
                return m.Success ? m.Value.ToLowerInvariant() : "";
            }
            catch { return ""; }
        }

        /// <summary>体检页/命令行用:把电源计划切到高性能。</summary>
        public static bool SwitchToHighPerformance()
        {
            try
            {
                if (string.Equals(ActiveSchemeGuid(), SchemeHigh, StringComparison.OrdinalIgnoreCase)) return true;
                return SetScheme(SchemeHigh);
            }
            catch { return false; }
        }

        public static bool SetScheme(string guid)
        {
            try
            {
                var psi = new ProcessStartInfo(Native.System32("powercfg.exe"), "/setactive " + guid);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(4000))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Log.Write("powercfg set: " + ex.Message);
                return false;
            }
        }

        private static string Run(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append(p.StandardOutput.ReadToEnd());
                    sb.Append(p.StandardError.ReadToEnd());
                    p.WaitForExit(3000);
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                Log.Write("run " + exe + ": " + ex.Message);
                return "";
            }
        }

        // =================================================================
        //  进程存活判断
        // =================================================================

        public bool GameAlive()
        {
            if (!Active || GamePid <= 0) return false;
            try
            {
                // 只要还能拿到进程对象就算活着。带反作弊的游戏(如三角洲的 ACE)
                // 会拒绝读取进程名/退出状态,以前这里读 ProcessName 抛异常,
                // 被当成"游戏已退出"而错误还原——挂后台、切屏都会触发。
                using (Process p = Process.GetProcessById(GamePid)) return true;
            }
            catch (ArgumentException)
            {
                // 进程不存在:这才是真正的退出。
                return false;
            }
            catch
            {
                // 权限不足等读不到信息的情况,不能据此判定退出。
                return true;
            }
        }

        public string SuspendedNames()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < suspended.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(suspended[i].Name);
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            Restore();
        }

        private class Suspended
        {
            public int Pid;
            public string Name = "";
            public ulong WorkingSet;
        }
    }
}