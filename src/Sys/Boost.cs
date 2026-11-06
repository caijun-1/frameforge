using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    /// <summary>用户在加速页勾选的优化项。</summary>
    public class BoostPlan
    {
        public bool Priority = true;    // 游戏进程 -> 高优先级
        public bool PowerPlan = true;   // 高性能电源计划
        public bool Timer = true;       // 系统计时器 1ms
        public bool Dvr = true;         // 关闭 Game DVR / 全屏优化
        public bool Memory = true;      // 内存整理
        public bool Background = false; // 进阶:暂停后台进程
        public bool Affinity = false;   // 进阶:游戏独占核心
        public bool Irq = false;        // 进阶:设备中断避让(重启生效)
        public int Cores = 0;           // 0 = 自动(一半物理核心)

        public bool Any
        {
            get { return Priority || PowerPlan || Timer || Dvr || Memory || Background || Affinity || Irq; }
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
        public DateTime StartedAt = DateTime.MinValue;

        public int SuspendedCount { get { return suspended.Count; } }

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
            if (IsShell(n)) return -1;

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
                    string name = p.ProcessName;
                    if (name.Length == 0) continue;
                    // 内置保护(系统进程 + 平台/录制工具)和用户白名单都不参与暂停。
                    if (Protected.Contains(name) || UserWhitelist.Contains(name)) continue;
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

        /// <summary>保护名单里的一行:给小白看的勾选项,不用手打进程名。</summary>
        public class AppChoice
        {
            public string Name = "";      // 进程名(不带 .exe)
            public string Label = "";     // 显示名,常见平台用中文友好名
            public ulong Memory;
            public bool Running;
            public bool BuiltIn;          // 游戏平台/录制工具,永远保护,不可取消
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
        /// 系统进程不出现,避免把用户吓到。
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
                    if (!preset && Protected.Contains(name)) continue;

                    long ws = 0;
                    try { ws = p.WorkingSet64; } catch (Exception) { }
                    bool visible = false;
                    try { visible = p.MainWindowHandle != IntPtr.Zero; } catch (Exception) { }
                    // 只留用户能认出来的:有窗口的、吃过 120MB 的,以及固定预设。
                    if (!preset && !visible && ws < (120L << 20)) continue;

                    AppChoice c;
                    if (map.TryGetValue(name, out c))
                    {
                        if ((ulong)ws > c.Memory) c.Memory = (ulong)ws;
                        continue;
                    }
                    map[name] = new AppChoice
                    {
                        Name = name,
                        Label = name,
                        Memory = (ulong)ws,
                        Running = true,
                        BuiltIn = preset
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
                    BuiltIn = true
                };
            }

            // 用户之前保存过、但当前没在运行的程序也要留住,否则一保存就"消失"了。
            foreach (string n in UserWhitelist)
            {
                if (n.Length == 0 || map.ContainsKey(n)) continue;
                map[n] = new AppChoice { Name = n, Label = n, BuiltIn = IsPreset(n) };
            }

            var list = new List<AppChoice>(map.Values);
            list.Sort(delegate(AppChoice a, AppChoice b)
            {
                int ga = Group(a), gb = Group(b);
                if (ga != gb) return ga.CompareTo(gb);
                if (a.Running && b.Running) return b.Memory.CompareTo(a.Memory);
                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
            return list.ToArray();
        }

        /// <summary>排序分组:0 正在运行,1 常用软件,2 默认保护。</summary>
        private static int Group(AppChoice c)
        {
            if (c.BuiltIn) return 2;
            return c.Running ? 0 : 1;
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

            if (plan.Priority && ApplyPriority(gamePid)) r.PriorityOk = 1;
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
                    OptimizeResult m = MemEngine.Optimize(OptimizeLevel.Standard, gamePid, null);
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
                || r.Suspended > 0;
            r.NeedAdmin = !IsAdmin && (r.PowerOk == 0 || r.TimerOk == 0);
            Active = true;

            // 把「请求了什么、哪些没生效」记清楚:界面只报确认生效的数量。
            r.Requested = (plan.Priority ? 1 : 0) + (plan.PowerPlan ? 1 : 0) + (plan.Timer ? 1 : 0)
                + (plan.Dvr ? 1 : 0) + (plan.Memory ? 1 : 0) + (plan.Background ? 1 : 0)
                + (plan.Affinity ? 1 : 0) + (plan.Irq ? 1 : 0);
            if (plan.Priority && r.PriorityOk == 0) r.Skipped.Add("boost.skip.priority");
            if (plan.PowerPlan && r.PowerOk == 0) r.Skipped.Add("boost.skip.power");
            if (plan.Timer && r.TimerOk == 0) r.Skipped.Add("boost.skip.timer");
            if (plan.Dvr && r.DvrOk == 0) r.Skipped.Add("boost.skip.dvr");
            if (plan.Memory && r.MemoryOk == 0) r.Skipped.Add("boost.skip.mem");
            if (plan.Background && r.Suspended == 0) r.Skipped.Add("boost.skip.bg");
            if (plan.Affinity && r.AffinityOk == 0) r.Skipped.Add("boost.skip.affinity");
            if (plan.Irq && r.IrqOk == 0) r.Skipped.Add("boost.skip.irq");

            // 先把原始值落到磁盘再返回:进程中途被杀或重启,下次启动还能自愈。
            try { BoostJournal.Save(Snapshot()); }
            catch (Exception ex) { Log.Write("journal save: " + ex.Message); }

            Log.Write("boost apply pid=" + gamePid + " ok=" + (r.Ok ? "1" : "0")
                + " prio=" + r.PriorityOk + " power=" + r.PowerOk + " timer=" + r.TimerOk
                + " dvr=" + r.DvrOk + " mem=" + (r.MemReleased / 1048576L) + "MB"
                + " suspended=" + r.Suspended
                + " affinity=" + r.AffinityOk + "/" + r.AffinityCores + "c/" + r.AffinityMoved + "p"
                + " irq=" + r.IrqOk + "/" + r.IrqDevices + "d");
            return r;
        }

        private bool ApplyPriority(int pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return false;
                prevPriority = Native.GetPriorityClass(h);
                return Native.SetPriorityClass(h, Native.HIGH_PRIORITY_CLASS);
            }
            catch { return false; }
            finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
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
            }
            catch { }
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

            try { irq.Restore(); } catch { }
            try { affinity.Restore(); } catch { }

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
                try { Native.timeEndPeriod(1); } catch { }
                timerSet = false;
            }

            RestoreRegistry();

            if (prevScheme.Length > 0)
            {
                try { SetScheme(prevScheme); } catch { }
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
                catch { }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
                prevPriority = 0;
            }

            Active = false;
            StartedAt = DateTime.MinValue;
            GamePid = -1;
            GameName = "";
            // 全部恢复完成才删日志:删早了中途崩溃就再也没机会自愈。
            try { BoostJournal.Clear(); } catch { }
            Log.Write("boost restore");
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

        private void RestoreRegistry()
        {
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
                catch { }
            }
            regSaved.Clear();
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
                using (Process p = Process.GetProcessById(GamePid))
                    return !p.HasExited && p.ProcessName.Length > 0;
            }
            catch { return false; }
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