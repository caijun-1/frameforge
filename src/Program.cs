using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge
{
    internal static class Program
    {
        private const string MutexName = "Global\\FrameForge.SingleInstance";

        [STAThread]
        private static int Main(string[] args)
        {
            bool minimized = HasArg(args, "--minimized");
            bool selftest = HasArg(args, "--selftest");

            if (selftest) return SelfTest();
            if (HasArg(args, "--restore")) return RestoreCli();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    ActivateExisting();
                    return 0;
                }

                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                { Log.Write("ui thread: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                { Log.Write("unhandled: " + e.ExceptionObject); };

                Settings st = Settings.Load();
                BoostEngine.SetWhitelist(st.WhitelistNames());

                // 上次加速如果没来得及还原(崩溃/强杀),这里把注册表和电源计划拨回去。
                try { BoostJournal.RecoverStale(); }
                catch (Exception ex) { Log.Write("journal recover: " + ex.Message); }

                try
                {
                    Application.Run(new UI.MainForm(st, minimized));
                }
                catch (Exception ex)
                {
                    Log.Write("fatal: " + ex);
                    return 1;
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        private static bool HasArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>命令行还原:FrameForge.exe --restore,没有待还原的改动时返回 2。</summary>
        private static int RestoreCli()
        {
            int n = BoostJournal.RestoreAll();
            Log.Write("restore cli: " + n + " item(s)");
            return n > 0 ? 0 : 2;
        }

        /// <summary>单实例:把已运行的窗口叫回前台。</summary>
        private static void ActivateExisting()
        {
            for (int i = 0; i < 12; i++)
            {
                IntPtr hwnd = Native.FindWindow(null, "FrameForge");
                if (hwnd != IntPtr.Zero)
                {
                    Native.PostMessage(hwnd, Native.WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                Thread.Sleep(120);
            }
        }

        // ---------------- 自检 ----------------

        /// <summary>无界面自检:写日志到 %TEMP%\frameforge_selftest.log,全部通过返回 0。</summary>
        private static int SelfTest()
        {
            var sb = new StringBuilder();
            int fail = 0;

            Action<string, bool, string> check = delegate(string name, bool ok, string detail)
            {
                if (!ok) fail++;
                sb.AppendLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  (" + detail + ")" : ""));
            };

            // 虚拟机、远程桌面、CI 机器上拿不到显卡信息/显卡计数器是正常的,
            // 这种情况记为 SKIP 而不是 FAIL,免得环境问题挡住真正的故障。
            int skipped = 0;
            Action<string, string> skip = delegate(string name, string reason)
            {
                skipped++;
                sb.AppendLine("SKIP  " + name + (reason.Length > 0 ? "  (" + reason + ")" : ""));
            };

            sb.AppendLine("FrameForge self test  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("os: " + Environment.OSVersion.VersionString + "  x64=" + Environment.Is64BitProcess);
            sb.AppendLine("admin: " + MemEngine.IsAdmin);
            sb.AppendLine();

            // 1. 内存采样
            try
            {
                MemSample m = MemEngine.Sample();
                check("memory sample", m.TotalPhys > 0 && m.AvailPhys > 0,
                    "total=" + Fmt.Bytes(m.TotalPhys) + " avail=" + Fmt.Bytes(m.AvailPhys) +
                    " load=" + m.LoadPercent + "%");
                check("standby list readable", m.StandbyBytes >= 0, Fmt.Bytes(m.StandbyBytes));
            }
            catch (Exception ex) { check("memory sample", false, ex.Message); }

            // 2. 自身工作集裁剪
            try
            {
                long before = Environment.WorkingSet;
                MemEngine.TrimSelf();
                check("trim self", true, "before=" + Fmt.Bytes((ulong)before) +
                    " after=" + Fmt.Bytes((ulong)Environment.WorkingSet));
            }
            catch (Exception ex) { check("trim self", false, ex.Message); }

            // 3. 监控采样
            Sys.Monitor mon = null;
            try
            {
                mon = new Sys.Monitor();
                mon.Start();
                Thread.Sleep(1800);
                SysSample s = mon.Current;
                check("cpu sample", s != null && s.Cpu >= 0 && s.Cpu <= 100,
                    s == null ? "null" : s.Cpu.ToString("0.0", CultureInfo.InvariantCulture) + "%");
                check("ram sample", s != null && s.RamPct >= 0,
                    s == null ? "null" : s.RamPct.ToString("0.0", CultureInfo.InvariantCulture) + "%");
                if (s == null) check("gpu name", false, "null");
                else if (s.GpuName.Length == 0) skip("gpu name", "no display adapter reported");
                else check("gpu name", true, s.GpuName);

                if (s == null) check("gpu load", false, "null");
                else if (s.Gpu < 0) skip("gpu load", "no GPU performance counters");
                else check("gpu load", true,
                    s.Gpu.ToString("0.0", CultureInfo.InvariantCulture) + "%" +
                        (s.GpuFromNvml ? " nvml" : " pdh"));
            }
            catch (Exception ex) { check("monitor", false, ex.Message); }
            finally { if (mon != null) mon.Dispose(); }

            // 4. 加速引擎:前台探测、电源计划、后台候选
            try
            {
                string name;
                int pid = BoostEngine.DetectGamePid(out name);
                check("game detect", pid == -1 || pid > 4, pid == -1 ? "no fullscreen game" : name + " pid=" + pid);
            }
            catch (Exception ex) { check("game detect", false, ex.Message); }

            try
            {
                string guid = BoostEngine.ActiveSchemeGuid();
                check("power scheme", guid.Length == 36, guid.Length > 0 ? guid : "unreadable");
            }
            catch (Exception ex) { check("power scheme", false, ex.Message); }

            try
            {
                BackgroundProc[] cands = BoostEngine.Candidates(-1, 6);
                check("background candidates", cands.Length <= 6, cands.Length + " candidate(s)");
            }
            catch (Exception ex) { check("background candidates", false, ex.Message); }

            // 5. 设置映射(不落盘,避免覆盖用户配置)
            try
            {
                var st = new Settings();
                st.BoostBackground = true;
                st.BoostAffinity = true;
                st.BoostIrq = true;
                BoostPlan p = st.Plan();
                check("settings plan", p.Background && p.Affinity && p.Irq && p.Priority && p.Any,
                    "boost items mapped");
            }
            catch (Exception ex) { check("settings plan", false, ex.Message); }

            // 6. CPU 拓扑与核心分配(只读)
            try
            {
                CpuCore[] cores = CpuTopology.Cores();
                check("cpu topology", cores.Length > 0, cores.Length + " physical core(s)");
                int used;
                long mask = CpuTopology.GameMask(0, out used);
                check("core mask", mask != 0 && CpuTopology.PopCount(mask) >= used,
                    "cores=" + used + " cpu=" + CpuTopology.PopCount(mask)
                    + " mask=0x" + mask.ToString("X", CultureInfo.InvariantCulture));

                CpuSetInfo[] sets = CpuTopology.CpuSets();
                check("cpu sets", true, CpuTopology.SoftSupported
                    ? sets.Length + " set(s)" : "unsupported, hard affinity only");
                int pc = 0, ec = 0, ccd = 0;
                for (int i = 0; i < cores.Length; i++)
                {
                    if (cores[i].Efficient) ec++; else pc++;
                    bool seen = false;
                    for (int j = 0; j < i; j++)
                        if (cores[j].Llc == cores[i].Llc) { seen = true; break; }
                    if (!seen) ccd++;
                }
                check("p/e cores", pc + ec > 0, pc + "P/" + ec + "E hybrid=" + CpuTopology.IsHybrid
                    + " ccd=" + ccd);
            }
            catch (Exception ex) { check("cpu topology", false, ex.Message); }

            // 7. 按游戏策略(只读)
            try
            {
                var store = new ProfileStore();
                store.Load();
                check("profiles", true, store.All.Count + " saved profile(s)");
            }
            catch (Exception ex) { check("profiles", false, ex.Message); }

            // 8. 中断设备枚举(只读)
            try
            {
                IrqDevice[] devs = InterruptEngine.Enumerate();
                check("irq devices", true, devs.Length + " MSI net/display device(s)");
            }
            catch (Exception ex) { check("irq devices", false, ex.Message); }

            // 9. 帧率组件
            try
            {
                bool ok = FpsEngine.Installed;
                check("presentmon", true, ok ? FpsEngine.ExePath : "not installed, download in Settings");
            }
            catch (Exception ex) { check("presentmon", false, ex.Message); }

            // 10. 叠加层可构造
            try
            {
                using (var ov = new Overlay(new Settings())) ov.ApplySettings();
                check("overlay init", true, "layered window ready");
            }
            catch (Exception ex) { check("overlay init", false, ex.Message); }

            // 11. 进程名
            try
            {
                string name = Native.ProcessName(Process.GetCurrentProcess().Id);
                check("process name", name.Length > 0, name);
            }
            catch (Exception ex) { check("process name", false, ex.Message); }

            // 12. 游戏名识别
            try
            {
                string gta = GameDb.Title("gta5_enhanced.exe");
                string unreal = GameDb.Title("client-win64-shipping.exe");
                check("game title db", gta.Length > 0 && gta != "gta5_enhanced",
                    "gta5_enhanced.exe -> " + gta);
                check("game title fallback", unreal.Length > 0,
                    "client-win64-shipping.exe -> " + (unreal.Length == 0 ? "(empty!)" : unreal));
                check("game title known", GameDb.Known("r5apex.exe"), "r5apex.exe");
                string mc = GameDb.Title("minecraft.windows.exe");
                check("game title minecraft", mc.IndexOf("我的世界") >= 0 || mc.IndexOf("Minecraft") >= 0,
                    "minecraft.windows.exe -> " + mc);

                // 进程扫描兜底不能把 FrameForge 自己当成游戏。
                int spid;
                bool scanned = Sys.Monitor.FindKnownGame(out spid);
                check("game scan", spid != Process.GetCurrentProcess().Id,
                    scanned ? "running game pid=" + spid : "no game running");
            }
            catch (Exception ex) { check("game title", false, ex.Message); }

            // 13. 战绩记录序列化往返(不写入用户数据)
            try
            {
                var r = new SessionRecord();
                r.Id = "selftest";
                r.Exe = "gta5_enhanced";
                r.Title = "侠盗猎车手 V";
                r.Start = DateTime.Now.AddMinutes(-35);
                r.End = r.Start.AddSeconds(1800);
                r.Seconds = 1800;
                r.FpsAvg = 118.4;
                r.FpsMax = 165;
                r.FpsMin = 42;
                r.Low1 = 86.2;
                r.CpuAvg = 41; r.CpuMax = 70;
                r.GpuAvg = 83; r.GpuMax = 96;
                r.GpuTempAvg = 63; r.GpuTempMax = 71;
                r.RamAvg = 11264; r.RamMax = 12800;
                r.VramMax = 7168;
                r.Power = 312;
                r.FpsSeries = new double[] { 100, 118.4, 96, 140 };
                r.FpsStep = 4;

                SessionRecord back = SessionRecord.Parse(r.Serialize());
                check("session roundtrip", back != null && back.Id == r.Id && back.Title == r.Title
                    && Math.Abs(back.FpsAvg - r.FpsAvg) < 0.01 && back.Seconds == r.Seconds,
                    back == null ? "null" : back.Duration + " avg=" + back.FpsAvg.ToString("0.0", CultureInfo.InvariantCulture));
                check("session series", back != null && back.FpsSeries.Length == 4
                    && back.FpsStep == 4 && Math.Abs(back.FpsSeries[3] - 140) < 0.1,
                    back == null ? "null" : back.FpsSeries.Length + " points");
                check("session energy", r.Wh > 0 && r.Co2 > 0,
                    r.Wh.ToString("0.0", CultureInfo.InvariantCulture) + "Wh "
                    + r.Co2.ToString("0", CultureInfo.InvariantCulture) + "g CO2");
            }
            catch (Exception ex) { check("session record", false, ex.Message); }

            // 13. 报告卡绘制(含帧率曲线):不弹窗,直接画到位图上验证布局与绘图不抛异常
            try
            {
                var rr = new SessionRecord();
                rr.Exe = "Client-Win64-Shipping.exe";
                rr.Title = "鸣潮";
                rr.Seconds = 1800;
                rr.FpsAvg = 118.4;
                rr.FpsMax = 144;
                rr.Low1 = 92;
                rr.FpsSeries = new double[] { 100, 118.4, 96, 140, 132, 121 };
                rr.FpsStep = 4;
                using (var f = new UI.ReportForm(rr, null))
                {
                    f.CreateControl();
                    using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                        f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                }
                check("report paint", true, "chart + stats");
            }
            catch (Exception ex) { check("report paint", false, ex.Message); }

            // 14. 加速三模式(不落盘)
            try
            {
                var ms = new Settings();
                ms.ApplyMode("beast");
                check("boost mode beast",
                    ms.BoostMode == "beast" && ms.BoostPriority && ms.BoostPowerPlan && ms.BoostTimer
                    && ms.BoostDvr && ms.BoostMem && ms.BoostBackground && ms.BoostAffinity && ms.BoostIrq
                    && ms.MatchesBeast(), "all 8 items on");

                ms.ApplyMode("balanced");
                check("boost mode balanced",
                    ms.BoostMode == "balanced" && ms.BoostPriority && ms.BoostPowerPlan && ms.BoostTimer
                    && ms.BoostDvr && ms.BoostMem && !ms.BoostBackground && !ms.BoostAffinity && !ms.BoostIrq
                    && ms.MatchesBalanced(), "safe 5 items on");

                ms.ApplyCustom(new bool[] { true, false, true, false, true, false, true, false });
                check("boost mode custom",
                    ms.BoostMode == "custom" && ms.BoostPriority && !ms.BoostPowerPlan
                    && ms.BoostTimer && !ms.BoostDvr && ms.BoostMem && !ms.BoostBackground
                    && ms.BoostAffinity && !ms.BoostIrq && !ms.MatchesBalanced() && !ms.MatchesBeast(),
                    "custom flags kept");

                check("boost mode key", Settings.ModeKey("BEAST") == "balanced" && Settings.ModeKey(null) == "balanced",
                    "unknown keys fall back to balanced");
            }
            catch (Exception ex) { check("boost modes", false, ex.Message); }

            // 15. 保护名单归一化(不落盘)
            try
            {
                BoostEngine.SetWhitelist(new string[] { "TestProc.exe", " keepme " });
                check("boost whitelist",
                    BoostEngine.UserWhitelist.Contains("testproc") && BoostEngine.UserWhitelist.Contains("keepme"),
                    BoostEngine.UserWhitelist.Count + " name(s)");
                BoostEngine.SetWhitelist(new string[0]);
            }
            catch (Exception ex) { check("boost whitelist", false, ex.Message); }

            // 16. 加速日志往返(有未还原的日志时跳过,避免覆盖真实数据)
            try
            {
                if (BoostJournal.Pending)
                {
                    check("boost journal", true, "pending journal, skipped");
                }
                else
                {
                    var js = new BoostJournal.State();
                    js.GamePid = 0;
                    js.GameName = "selftest";
                    js.PrevScheme = BoostEngine.SchemeBalanced;
                    var je = new BoostJournal.Entry();
                    je.Path = @"Software\FrameForge\Selftest";
                    je.Name = "v";
                    je.Exists = false;
                    je.Value = 7;
                    js.Reg.Add(je);
                    BoostJournal.Save(js);

                    BoostJournal.State back = BoostJournal.Load();
                    check("boost journal", back != null && back.Reg.Count == 1
                        && back.Reg[0].Value == 7 && back.PrevScheme == BoostEngine.SchemeBalanced,
                        back == null ? "null" : back.Reg.Count + " entry");
                    BoostJournal.Clear();
                }
            }
            catch (Exception ex) { check("boost journal", false, ex.Message); }

            // 17. 系统体检(只读)
            try
            {
                System.Collections.Generic.List<HealthItem> items = Health.Check(null);
                check("health check", items.Count >= 6, items.Count + " item(s)");
            }
            catch (Exception ex) { check("health check", false, ex.Message); }

            // 18. 优化页绘制:逐项开关 / 分段选择 + 预设按钮,直接画到位图验证不抛异常
            try
            {
                using (var hp = new UI.HealthPage(null))
                {
                    hp.Size = new System.Drawing.Size(900, 620);
                    hp.CreateControl();
                    using (var bmp = new System.Drawing.Bitmap(900, 620))
                        hp.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, 900, 620));
                }
                check("health paint", true, "rows + presets");
            }
            catch (Exception ex) { check("health paint", false, ex.Message); }

            // 19. 程序图标读取:战绩、报告、保护名单共用的 exe 图标
            try
            {
                System.Drawing.Image ico = UI.AppIcons.Load(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
                check("app icon", ico != null && ico.Width > 0, ico == null ? "null" : ico.Width + "px");
            }
            catch (Exception ex) { check("app icon", false, ex.Message); }

            sb.AppendLine();
            sb.AppendLine(fail == 0
                ? "RESULT: OK" + (skipped > 0 ? " (" + skipped + " skipped)" : "")
                : "RESULT: " + fail + " failure(s)");

            string path = Path.Combine(Path.GetTempPath(), "frameforge_selftest.log");
            try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); }
            catch { }
            Log.Write("selftest: " + (fail == 0 ? "ok" : fail + " failure(s)"));
            return fail == 0 ? 0 : 1;
        }
    }
}