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
    public sealed class BoostEngine : IDisposable
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
        public int AffinityMode { get { return affinity.Mode; } }
        /// <summary>游戏当前占用的核心掩码,未启用核心独占时为 0。</summary>
        public long AffinityMask { get { return affinity.Mask; } }

        public static bool IsAdmin
        {
            get { return MemEngine.IsAdmin; }
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

            GamePid = gamePid;
            GameName = gameName ?? "";
            StartedAt = DateTime.Now;
            steerCores = plan.Cores;

            string priorityFail = "";
            if (plan.Priority)
            {
                int applied;
                if (ApplyPriority(gamePid, plan.PriorityLevel, out priorityFail, out applied))
                {
                    r.PriorityOk = 1;
                    // 记录"实际生效"的档位:反作弊只允许较高的普通优先级时,
                    // 界面要如实写「高于正常」,不能还挂着用户选的「高」。
                    PriorityLevel = applied;
                }
            }
            // PCBoost 的两层一起落地:先把前台游戏提到高优先级,再立刻把主线程
            // 软引导到当前最空闲的核心;之后由 SteerIfDue() 每 2 分钟重新评估。
            if (plan.Priority)
            {
                try { TrySteer(plan.Cores); }
                catch (Exception ex) { Log.Write("boost steer: " + ex.Message); }
            }
            if (plan.Affinity)
            {
                try
                {
                    AffinityResult a = affinity.Apply(gamePid, GameName, plan.Cores);
                    r.AffinityOk = a.Ok ? 1 : 0;
                    r.AffinityCores = a.Cores;
                    r.AffinityMoved = a.Moved;
                }
                catch (Exception ex) { Log.Write("boost affinity: " + ex.Message); }
            }
            if (plan.IdealCpu)
            {
                try
                {
                    // 实验项:只建议线程去哪颗核,绝不改亲和性掩码,失败也不影响其它项。
                    r.IdealOk = ApplyIdealCpu(gamePid, plan.Cores);
                    IdealApplied = r.IdealOk > 0;
                }
                catch (Exception ex) { Log.Write("boost ideal: " + ex.Message); }
            }
            if (plan.Irq)
            {
                try
                {
                    IrqResult q = irq.Apply(plan.Cores);
                    r.IrqOk = q.Ok ? 1 : 0;
                    r.IrqDevices = q.Set;
                    r.IrqNeedReboot = q.Ok;
                }
                catch (Exception ex) { Log.Write("boost irq: " + ex.Message); }
            }
            if (plan.PowerPlan && ApplyPowerPlan()) r.PowerOk = 1;
            if (plan.Timer && ApplyTimer()) r.TimerOk = 1;
            if (plan.Dvr && ApplyDvr()) r.DvrOk = 1;
            if (plan.Memory)
            {
                try
                {
                    // 游戏正在跑:只归还后台进程的空闲工作集,不动系统文件缓存和待命列表。
                    OptimizeResult m = MemEngine.Optimize(OptimizeLevel.Standard, gamePid, null, false);
                    r.MemReleased = m.Released;
                    // 一个字节都没释放时不算生效,免得结果里出现虚的「已整理」。
                    r.MemoryOk = m.Released > 0 ? 1 : 0;
                }
                catch (Exception ex) { Log.Write("boost memory: " + ex.Message); }
            }
            if (plan.Background)
            {
                try { r.Suspended = SuspendBackground(gamePid); }
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

            // 先把原始值落到磁盘再返回:进程中途被杀或重启,下次启动还能自愈。
            try { BoostJournal.Save(Snapshot()); }
            catch (Exception ex) { Log.Write("journal save: " + ex.Message); }

            Log.Write("boost apply pid=" + gamePid + " ok=" + (r.Ok ? "1" : "0")
                + " prio=" + r.PriorityOk + (priorityFail.Length > 0 ? "(" + priorityFail + ")" : "")
                + " power=" + r.PowerOk + " timer=" + r.TimerOk
                + " dvr=" + r.DvrOk + " mem=" + (r.MemReleased / 1048576L) + "MB"
                + " suspended=" + r.Suspended
                + " affinity=" + r.AffinityOk + "/" + r.AffinityCores + "c/" + r.AffinityMoved + "p"
                + " irq=" + r.IrqOk + "/" + r.IrqDevices + "d"
                + " ideal=" + r.IdealOk + "t");
            return r;
        }

        /// <summary>
        /// 调整游戏进程优先级。带反作弊的游戏经常直接拒绝这类请求,所以这里把
        /// 「被拒绝」和普通失败分开报,并且回读一次确认真的生效——SetPriorityClass
        /// 返回成功但优先级没变的情况是存在的,不能据此谎报"已优化"。
        /// failKey 为空表示失败原因不明,由调用方用通用的「优先级」文案。
        /// </summary>
        private bool ApplyPriority(int pid, int level, out string failKey, out int applied)
        {
            failKey = "";
            applied = level;
            bool denied;
            if (TrySetPriority(pid, level, out denied)) return true;

            // 游戏刚启动时保护组件可能还没装好,隔半秒再试一次。
            // 自动加速跑在线程池里,不会卡住界面。
            if (denied)
            {
                try { System.Threading.Thread.Sleep(400); }
                catch { }
                if (TrySetPriority(pid, level, out denied)) return true;
            }

            // 不少带反作弊的游戏只拦「高」,会放过「高于正常」。退一档再试,
            // 至少让优先级真的比默认值高,而不是整项作废。
            if (level >= 4 && TrySetPriority(pid, 3, out denied))
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

        private bool TrySetPriority(int pid, int level, out bool denied)
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
        private int ApplyIdealCpu(int pid, int cores)
        {
            int ideal = IdealProcessor(cores);
            int ok = 0;
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
                    IntPtr th = Native.OpenThread(Native.THREAD_SET_INFORMATION, false, (int)e.th32ThreadID);
                    if (th == IntPtr.Zero) continue;
                    try
                    {
                        if (Native.SetThreadIdealProcessor(th, (uint)ideal) != 0xFFFFFFFF) ok++;
                    }
                    catch { }
                    finally { Native.CloseHandle(th); }
                }
                while (Native.Thread32Next(snap, ref e));
            }
            finally { Native.CloseHandle(snap); }
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
                if (best == null || all[i].Efficiency < best.Efficiency) best = all[i];
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
        /// 动态负载平衡:由界面每秒调用一次,内部节流到每 2 分钟一次。
        /// 重新挑当前最空闲的核心,把游戏主线程的理想处理器指过去。
        /// </summary>
        public void SteerIfDue()
        {
            if (!Active || GamePid <= 0) return;
            if ((DateTime.UtcNow - lastSteer).TotalSeconds < SteerSeconds) return;
            if (Interlocked.CompareExchange(ref steerBusy, 1, 0) != 0) return;
            lastSteer = DateTime.UtcNow;
            int pid = GamePid;
            int cores = steerCores;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { SteerOnce(pid, cores); }
                catch (Exception ex) { Log.Write("boost steer: " + ex.Message); }
                finally { Interlocked.Exchange(ref steerBusy, 0); }
            });
        }

        /// <summary>同步执行一次软引导(加速时首帧用,调用方已在后台线程)。</summary>
        private void TrySteer(int cores)
        {
            steerCores = cores;
            lastSteer = DateTime.UtcNow;
            SteerOnce(GamePid, cores);
        }

        /// <summary>
        /// 采样两次每核时间算出单核占用,挑最空闲的一颗,把游戏主线程指过去。
        /// CPU 0 承担网卡/鼠标等中断和大量系统杂务,几乎不会是最闲的那颗,
        /// 所以「挑最闲」这条规则本身就实现了与系统核心错峰,而不需要硬性避开。
        /// </summary>
        private int SteerOnce(int pid, int cores)
        {
            int cpu = PickIdleCore(cores);
            int tid = MainThreadId(pid);
            if (tid <= 0) return -1;
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
            long[] idle1, busy1, idle2, busy2;
            if (Native.CpuCoreTimes(out idle1, out busy1) <= 0) return 0;
            try { Thread.Sleep(SteerSampleMs); } catch { }
            if (Native.CpuCoreTimes(out idle2, out busy2) <= 0) return 0;

            int n = Math.Min(Math.Min(idle1.Length, busy1.Length), Math.Min(idle2.Length, busy2.Length));
            if (n <= 0) return 0;

            // 启用核心独占时只在游戏自己的核里挑,不会把主线程送到被排除的核上。
            long gameMask = 0;
            CpuCore[] all = CpuTopology.Cores();
            if (cores > 0)
            {
                int used;
                gameMask = CpuTopology.GameMask(cores, out used);
            }

            int best = -1;
            double bestLoad = 2;
            for (int i = 0; i < all.Length; i++)
            {
                CpuCore c = all[i];
                if (c.Parked) continue;
                if (gameMask != 0 && (c.Mask & gameMask) == 0) continue;
                int idx = c.Index;
                if (idx < 0 || idx >= n) continue;

                long di = idle2[idx] - idle1[idx];
                long db = busy2[idx] - busy1[idx];
                long total = di + db;
                if (total <= 0) total = 1;
                double load = (double)db / total;

                if (best < 0 || load < bestLoad - 0.0005) { best = idx; bestLoad = load; }
                else if (best == 0 && idx != 0 && Math.Abs(load - bestLoad) <= 0.0005) best = idx;
            }
            return best < 0 ? 0 : best;
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
                if (string.Equals(cur, SchemeHigh, StringComparison.OrdinalIgnoreCase)) return true;
                prevScheme = cur;
                return SetScheme(SchemeHigh);
            }
            catch (Exception ex) { Log.Write("boost power: " + ex.Message); return false; }
        }

        private bool ApplyTimer()
        {
            try
            {
                if (Native.timeBeginPeriod(1) == 0) { timerSet = true; return true; }
                Log.Write("boost timer: timeBeginPeriod(1) refused");
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
            BackgroundProc[] list = Candidates(gamePid, 6);
            int done = 0;
            foreach (BackgroundProc b in list)
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, b.Pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    if (Native.NtSuspendProcess(h) == 0)
                    {
                        suspended.Add(new Suspended { Pid = b.Pid, Name = b.Name, WorkingSet = b.WorkingSet });
                        done++;
                    }
                }
                catch { }
                finally { Native.CloseHandle(h); }
            }
            return done;
        }

        // =================================================================
        //  还原
        // =================================================================

        public void Restore()
        {
            if (!Active) return;

            // 还原失败不能只是"什么也没发生":用户的电源计划、注册表就停在加速状态了。
            // 记进日志,并且保留日志文件,下次启动还能再还原一次。
            bool clean = true;
            try { irq.Restore(); }
            catch (Exception ex) { clean = false; Log.Write("boost restore irq: " + ex.Message); }
            try { affinity.Restore(); }
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

            if (timerSet)
            {
                try { Native.timeEndPeriod(1); }
                catch (Exception ex) { clean = false; Log.Write("boost restore timer: " + ex.Message); }
                timerSet = false;
            }

            if (!RestoreRegistry()) clean = false;

            if (prevScheme.Length > 0)
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
                    h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION, false, GamePid);
                    if (h != IntPtr.Zero) Native.SetPriorityClass(h, prevPriority);
                }
                catch (Exception ex) { Log.Write("boost restore priority: " + ex.Message); }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
                prevPriority = 0;
            }

            // 软引导只是调度提示,进程退出就没了;还原时主动把主线程指回「任意核心」,
            // 避免游戏还在跑却一直被钉在之前挑的那颗核上。
            if (GamePid > 0)
            {
                int tid = MainThreadId(GamePid);
                if (tid > 0) SetIdeal(tid, (int)Native.MAXIMUM_PROCESSORS);
            }
            lastSteer = DateTime.MinValue;
            SteerCpu = -1;
            steerBusy = 0;

            Active = false;
            StartedAt = DateTime.MinValue;
            GamePid = -1;
            GameName = "";
            PriorityLevel = -1;
            IdealApplied = false;
            // 全部恢复完成才删日志:删早了中途崩溃就再也没机会自愈。
            if (clean)
            {
                try { BoostJournal.Clear(); } catch { }
            }
            else
            {
                Log.Write("boost restore incomplete, journal kept for next start");
            }
            Log.Write(clean ? "boost restore" : "boost restore (incomplete)");
        }

        /// <summary>把当前这次改动整理成可落盘的原始值,供崩溃后恢复。</summary>
        internal BoostJournal.State Snapshot()
        {
            var st = new BoostJournal.State();
            st.GamePid = GamePid;
            st.GameName = GameName;
            st.PrevScheme = prevScheme;
            foreach (RegSaved s in regSaved)
            {
                var e = new BoostJournal.Entry();
                e.Path = s.Path;
                e.Name = s.Name;
                e.Exists = s.Exists;
                if (s.Exists)
                {
                    try { e.Value = Convert.ToInt32(s.Value, CultureInfo.InvariantCulture); }
                    catch { e.Exists = false; }
                }
                st.Reg.Add(e);
            }
            return st;
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
                string outp = Run("powercfg", "/getactivescheme");
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
                var psi = new ProcessStartInfo("powercfg", "/setactive " + guid);
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