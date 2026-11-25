using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge
{
    internal static class Program
    {
        // FF-042：同一用户会话内单实例即可。Global\ 会让远程桌面/快速切换用户时
        // 互相挡住，而且把进程名暴露在整个终端的命名空间里。
        private const string MutexName = "Local\\FrameForge.SingleInstance";

        [STAThread]
        private static int Main(string[] args)
        {
            // 在任何厂商 DLL(nvml/atiadlxx/dwmapi/pdh/wintrust)被 P/Invoke 解析之前,
            // 把默认 DLL 搜索目录锁到 System32。便携版 exe 常放在用户可写目录,
            // 否则同名 DLL 会被优先加载并以管理员权限运行(报告 FF-001)。
            try { Native.SetDefaultDllDirectories(0x00000800); } catch { }

            bool minimized = HasArg(args, "--minimized");
            bool selftest = HasArg(args, "--selftest");

            if (selftest) return SelfTest();
            if (HasArg(args, "--restore")) return RestoreCli();
            if (HasArg(args, "--gpudiag")) return GpuDiagCli();
            // S2 看门狗旁路：复用同一个 exe，绕过单实例 Mutex（仿 --restore）。
            string watchdogParent = ArgValue(args, "--watchdog");
            if (watchdogParent != null)
            {
                int parentPid;
                int.TryParse(watchdogParent, NumberStyles.Integer, CultureInfo.InvariantCulture, out parentPid);
                // P1-5：第二个参数是父进程启动时刻，用于识别 PID 复用。
                long parentStart = 0;
                string ticks = ArgValueAt(args, "--watchdog", 2);
                if (ticks != null)
                    long.TryParse(ticks, NumberStyles.Integer, CultureInfo.InvariantCulture, out parentStart);
                return WatchdogCli(parentPid, parentStart);
            }

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
                {
                    Log.Write("unhandled: " + e.ExceptionObject);
                    // 崩溃路径绝不删 journal：已落盘的 PREPARED/COMMITTED 凭据留给看门狗/下次启动。
                    try { if (BoostJournal.Pending) Log.Write("unhandled: journal preserved for recovery"); }
                    catch { }
                };

                Settings st = Settings.Load();
                BoostEngine.SetWhitelist(st.WhitelistNames());
                BoostEngine.SetExclude(st.ExcludeNames());
                // 手动锁定/排除名单要在启动采样前就生效,否则第一轮扫描还会误判。
                FrameForge.Sys.Monitor.SetManualTargets(st.BoostPin, st.BlockNames());
                // 「我的游戏」里的程序直接按游戏处理,启动时先灌进去。
                FrameForge.Sys.Monitor.SetMyGames(st.MyGameNames());

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

        /// <summary>--watchdog &lt;pid&gt; 的取值；没有下一个参数时返回 null。</summary>
        private static string ArgValue(string[] args, string name)
        {
            return ArgValueAt(args, name, 1);
        }

        /// <summary>取 name 之后第 offset 个参数（offset=1 即紧随其后的值）。</summary>
        private static string ArgValueAt(string[] args, string name, int offset)
        {
            for (int i = 0; i < args.Length - offset; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + offset];
            return null;
        }

        /// <summary>父进程身份是否仍是当初启动看门狗的那个进程（防止 PID 复用）。</summary>
        private static bool ParentAlive(int pid, long startTicks)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    // 没有记录启动时刻（旧版本启动的看门狗）时只按存在性判断。
                    if (startTicks > 0 && p.StartTime.ToUniversalTime().Ticks != startTicks) return false;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 看门狗只跟随父进程生命周期。正常退出清日志后无需动作；崩溃后统一对账两类事务。
        /// 没有 12 小时上限，也不会因暂时没有日志而提前退出。
        /// P1-5：先校验父进程身份；PID 被复用时仍执行 RecoverStale，不跳过恢复。
        /// </summary>
        private static int WatchdogCli(int parentPid, long parentStart)
        {
            if (parentPid <= 0) return 2;
            IntPtr h = IntPtr.Zero;
            try { h = Native.OpenProcess(Native.SYNCHRONIZE, false, parentPid); }
            catch (Exception ex) { Log.Write("watchdog open: " + ex.Message); }

            bool alive = ParentAlive(parentPid, parentStart);
            if (!alive)
            {
                // 父进程已退出，或 PID 已被别的进程复用：句柄可能等错对象，直接进入恢复。
                Log.Write("watchdog parent gone or pid reused pid=" + parentPid);
                if (h != IntPtr.Zero) { try { Native.CloseHandle(h); } catch { } h = IntPtr.Zero; }
            }

            if (alive)
            {
                if (h == IntPtr.Zero)
                {
                    // 父进程仍在但打不开时，不能把活动事务误判成崩溃遗留。
                    Log.Write("watchdog parent access denied pid=" + parentPid);
                    return 1;
                }
                try
                {
                    while (true)
                    {
                        uint w = Native.WaitForSingleObject(h, 60000);
                        if (w == Native.WAIT_OBJECT_0 || w == Native.WAIT_ABANDONED) break;
                        if (w == Native.WAIT_FAILED)
                        {
                            Log.Write("watchdog wait failed; active transaction left untouched");
                            return 1;
                        }
                    }
                }
                finally { try { Native.CloseHandle(h); } catch { } }
            }

            if (!BoostJournal.Pending) return 0;
            BoostJournal.RecoverStale();
            return 0;
        }

        /// <summary>
        /// 命令行还原:FrameForge.exe --restore。
        /// 退出码：0 全部还原；1 部分失败/仍有未决项；2 无待还原。
        /// </summary>
        private static int RestoreCli()
        {
            var rep = new ReconcileReport();
            int n = BoostJournal.RestoreAll(rep);
            Log.Write("restore cli: reverted=" + n + " conflict=" + rep.Conflict + " failed=" + rep.Failed);
            if (!rep.Clean) return 1;
            return n > 0 ? 0 : 2;
        }

        /// <summary>
        /// 命令行显卡诊断:FrameForge.exe --gpudiag。
        /// 把 atiadlxx.dll 的加载情况、导出的函数、每次 ADL 调用的返回码和原始数值
        /// 写进 gpudiag.log 并弹窗显示,读不到温度时拿这个就能定位。
        /// </summary>
        private static int GpuDiagCli()
        {
            string text;
            try { text = AmdGpu.Diagnose(); }
            catch (Exception ex) { text = "diagnostics failed: " + ex; }

            string path = Path.Combine(Settings.Dir, "gpudiag.log");
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch { }
            Log.Write("gpudiag: " + text.Replace("\r", "").Replace("\n", " | "));

            try
            {
                MessageBox.Show(text + Environment.NewLine + "已保存到: " + path,
                    "FrameForge 显卡诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
            return 0;
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

        private sealed class SelftestResourceOwner : ISystemResourceOwner
        {
            private readonly OwnerKind kind;
            public SelftestResourceOwner(OwnerKind value) { kind = value; }
            public OwnerKind Kind() { return kind; }
            public bool Needs(ResourceKind r) { return true; }
            public void OnGranted(ResourceKind r, uint refCount) { }
            public void OnShared(ResourceKind r, uint refCount) { }
            public void OnReleased(ResourceKind r) { }
        }

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
                        (s.GpuFromNvml ? " nvml" : (s.GpuFromAmd ? " adl" : " pdh")));

                // 温度:有厂商传感器(NVML/ADL)就必须读出来,系统计数器本身没有温度。
                if (s == null) check("gpu temp", false, "null");
                else if (!s.GpuFromNvml && !s.GpuFromAmd) skip("gpu temp", "no vendor sensor");
                else if (s.GpuTemp <= 0) check("gpu temp", false, "sensor returned nothing");
                else check("gpu temp", true,
                    s.GpuTemp.ToString("0", CultureInfo.InvariantCulture) + " C");
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

            // 5b. 中英词条键必须一一对应（P2-4）：新增文案漏翻一边会在这里暴露。
            try
            {
                string diff = I18n.KeyDiff();
                check("i18n key parity", diff.Length == 0,
                    diff.Length == 0 ? I18n.KeyCounts() : diff);
            }
            catch (Exception ex) { check("i18n key parity", false, ex.Message); }

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

            // 8.1 注册表快照往返:加速改的每一项都要能原样拨回去
            try
            {
                check("boost registry round trip", BoostEngine.SelfTestRegistryRoundTrip(), "HKCU sandbox key");
            }
            catch (Exception ex) { check("boost registry round trip", false, ex.Message); }

            // 8.2 配置/会话文件的原子写往返:先写临时文件再替换,写到一半断电也只丢这次改动
            try
            {
                string p = Path.Combine(Path.GetTempPath(), "frameforge_selftest_atomic.ini");
                string tmp = p + ".tmp";
                try { File.Delete(p); } catch { }
                try { File.Delete(tmp); } catch { }
                SafeFile.WriteAllText(p, "one", new UTF8Encoding(false));
                string first = File.ReadAllText(p);
                SafeFile.WriteAllText(p, "two", new UTF8Encoding(false));
                string second = File.ReadAllText(p);
                bool leftTmp = File.Exists(tmp);
                check("atomic write", first == "one" && second == "two" && !leftTmp,
                    "replace + overwrite, tmp=" + (leftTmp ? "left" : "clean"));
                try { File.Delete(p); } catch { }
            }
            catch (Exception ex) { check("atomic write", false, ex.Message); }

            // 8.3 ICC 解析:现造一份 sRGB profile,sRGB 进 sRGB 出必须接近恒等
            try
            {
                string iccNote;
                check("icc profile", Icc.SelfTest(out iccNote), iccNote);
            }
            catch (Exception ex) { check("icc profile", false, ex.Message); }

            // 9. 帧率组件
            try
            {
                bool ok = FpsEngine.Installed;
                check("presentmon", true, ok ? FpsEngine.ExePath : "not installed, download in Settings");
                // 引擎是以管理员身份拉起来的,签名不对就不能放行;没装时没有可校验的对象。
                if (!ok) skip("presentmon signature", "engine not installed");
                else
                {
                    bool signed = FpsEngine.SignedByIntel(FpsEngine.ExePath);
                    check("presentmon signature", signed, signed ? "intel code signing" : "untrusted or modified exe");
                }
            }
            catch (Exception ex) { check("presentmon", false, ex.Message); }

            try
            {
                string aggregationNote;
                check("fps aggregation 240fps", FpsEngine.SelfTestAggregation(out aggregationNote), aggregationNote);
            }
            catch (Exception ex) { check("fps aggregation 240fps", false, ex.Message); }

            // 10. 叠加层可构造与视觉管线自检
            try
            {
                using (var ov = new Overlay(new Settings())) ov.ApplySettings();
                string ovNote;
                bool ovOk = Overlay.SelfTestVisuals(out ovNote);
                check("overlay init", ovOk, ovOk ? ovNote : ("visuals failed: " + ovNote));
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

                ms.ApplyCustom(new bool[] { true, false, true, false, true, false, true, false, true });
                check("boost mode custom",
                    ms.BoostMode == "custom" && ms.BoostPriority && !ms.BoostPowerPlan
                    && ms.BoostTimer && !ms.BoostDvr && ms.BoostMem && !ms.BoostBackground
                    && ms.BoostAffinity && !ms.BoostIrq && ms.BoostIdealCpu && !ms.MatchesBalanced() && !ms.MatchesBeast(),
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

            // 15b. 加速排除名单(内置界面项 + 用户项,不落盘)
            try
            {
                var xs = new Settings();
                check("boost exclude default", xs.ExcludeNames().Length >= 5,
                    xs.ExcludeNames().Length + " default name(s)");

                BoostEngine.SetExclude(new string[] { "OBS64.exe", " discord ", "" });
                check("boost exclude",
                    BoostEngine.IsExcluded("obs64") && BoostEngine.IsExcluded("DISCORD.exe")
                    && !BoostEngine.IsExcluded("game") && BoostEngine.IsExcluded("ctfmon"),
                    "normalized + built-in shell");
                BoostEngine.SetExclude(Settings.Load().ExcludeNames());

                check("boost priority key", BoostEngine.PriorityKey(0) == "boost.prio.0"
                    && BoostEngine.PriorityKey(4) == "boost.prio.4"
                    && BoostEngine.PriorityClassFor(4) == 0x00000080
                    && BoostEngine.PriorityClassFor(0) == 0x00000040, "mapping ok");
            }
            catch (Exception ex) { check("boost exclude", false, ex.Message); }

            // 16. SCTL v2 事务日志往返 + checksum + v1 迁移（有真实待还原日志时跳过）
            try
            {
                if (BoostJournal.Pending)
                {
                    check("boost journal v2", true, "pending journal, skipped");
                }
                else
                {
                    var tx = BoostJournal.NewTransaction(OwnerKind.ONE_SHOT, 0, "selftest", -1);
                    var g = BoostJournal.MakeGuard(0, "selftest", -1);
                    g.StartTime = 123456789;
                    g.Tid = 456;
                    BoostJournal.NewAction(tx, ActionKind.PRIORITY_CLASS, g, "32", "128");
                    BoostJournal.NewAction(tx, ActionKind.IDEAL_CPU, g, "64", "3");
                    BoostJournal.NewAction(tx, ActionKind.AFFINITY_MASK, g, "FF", "0F");
                    var irqAction = BoostJournal.NewAction(tx, ActionKind.IRQ_AFFINITY, new ProcessGuard(),
                        BoostJournal.Base64(new byte[] { 1, 2, 3, 4 }), BoostJournal.Base64(new byte[] { 4, 3, 2, 1 }));
                    irqAction.Path = "HKLM\\Selftest\\IRQ";
                    irqAction.Name = BoostJournal.EncodeIrqMeta(4, true, true);
                    BoostJournal.NewAction(tx, ActionKind.POWER_SCHEME, new ProcessGuard(),
                        BoostEngine.SchemeBalanced, BoostEngine.SchemeHigh).Owner = OwnerKind.ONE_SHOT;
                    BoostJournal.NewAction(tx, ActionKind.TIMER_RESOLUTION, new ProcessGuard(), "1", "1")
                        .RequiresLiveOwner = true;
                    var regAction = BoostJournal.NewAction(tx, ActionKind.REG_DWORD, new ProcessGuard(),
                        BoostJournal.Base64Int(5), BoostJournal.Base64Int(7));
                    regAction.Path = "Software\\FrameForge\\Selftest";
                    regAction.Name = "v";
                    regAction.Existed = true;
                    BoostJournal.NewAction(tx, ActionKind.SUSPEND_PROCESS, g, "RUNNING", "SUSPENDED");
                    BoostJournal.NewAction(tx, ActionKind.MEMORY_TRIM, new ProcessGuard(), "", "").Revertible = false;
                    tx.State = TxState.COMMITTED;

                    string text = BoostJournal.Serialize(tx);
                    int sumAt = text.IndexOf("sum=", StringComparison.Ordinal);
                    string body = sumAt > 0 ? text.Substring(0, sumAt) : text;
                    check("journal checksum", sumAt > 0 && BoostJournal.Fnv1a(body) == tx.Checksum,
                        tx.Checksum);
                    check("journal checksum tamper", BoostJournal.Fnv1a(body + "x") != tx.Checksum,
                        "fnv1a detects one-byte change");

                    BoostJournal.Save(tx);
                    Transaction back = BoostJournal.Load();
                    check("boost journal v2", back != null && back.Actions.Count == 9
                        && back.State == TxState.COMMITTED && back.MigratedFrom == 0
                        && back.Checksum == tx.Checksum,
                        back == null ? "null" : back.Actions.Count + " action(s)");
                    check("journal guard roundtrip", back != null && back.Actions[0].Target != null
                        && back.Actions[0].Target.StartTime == 123456789
                        && back.Actions[0].Target.Tid == 456, "StartTime + Tid persisted");
                    check("journal reg fields", back != null
                        && back.Actions[6].Path == "Software\\FrameForge\\Selftest"
                        && back.Actions[6].Name == "v" && back.Actions[6].Existed
                        && back.Actions[3].Path == "HKLM\\Selftest\\IRQ"
                        && back.Actions[3].Name == BoostJournal.EncodeIrqMeta(4, true, true),
                        "registry path/name/existed roundtrip");

                    var realtime = BoostJournal.NewTransaction(OwnerKind.REALTIME, 0, "selftest-realtime", -1);
                    BoostJournal.NewAction(realtime, ActionKind.PRIORITY_CLASS, g, "32", "128");
                    realtime.State = TxState.COMMITTED;
                    BoostJournal.Save(realtime);
                    Transaction realtimeBack = BoostJournal.Load(OwnerKind.REALTIME);
                    check("journal realtime isolated", realtimeBack != null && realtimeBack.Owner == OwnerKind.REALTIME
                        && realtimeBack.Actions.Count == 1 && realtimeBack.Actions[0].Owner == OwnerKind.REALTIME
                        && BoostJournal.Load() != null,
                        "owner and journal files stay isolated and compatible");
                    BoostJournal.Clear(OwnerKind.REALTIME);

                    // v1 迁移：只构造 POWER_SCHEME + REG_DWORD，不虚构其它 7 类。
                    string v1 = "game=0|selftest\n" + "scheme=" + BoostEngine.SchemeBalanced + "\n"
                        + "reg=Software\\FrameForge\\Selftest|v|0|7\n";
                    SafeFile.WriteAllText(BoostJournal.FilePath, v1, new UTF8Encoding(false));
                    Transaction migrated = BoostJournal.Load();
                    check("journal v1 migrate", migrated != null && migrated.MigratedFrom == 1
                        && migrated.Actions.Count == 2,
                        migrated == null ? "null" : migrated.Actions.Count + " action(s)");

                    // 损坏日志 fixture（P2-7）：checksum 对不上时必须隔离并返回 null，
                    // 不能抛异常，也不能把坏内容当成有效事务继续还原。
                    string corrupt = "v=2\ntx=dead|state=COMMITTED|owner=ONE_SHOT|created=x|updated=y\n"
                        + "host=1|1|2|0|z\ngame=0|selftest|-1\nres=pw:NONE,tm:NONE\n"
                        + "sum=00000000:00000000000000000000000000000000\n";
                    SafeFile.WriteAllText(BoostJournal.FilePath, corrupt, new UTF8Encoding(false));
                    Transaction bad = BoostJournal.Load();
                    check("journal corrupt quarantine", bad == null && !File.Exists(BoostJournal.FilePath),
                        bad == null ? "bad checksum moved aside" : "corrupt journal was accepted");
                    BoostJournal.Clear();
                }
            }
            catch (Exception ex) { check("boost journal v2", false, ex.Message); }

            // 16b. Reconcile 清日志判定（纯逻辑，不碰真实系统设置）
            try
            {
                var rep = new ReconcileReport();
                bool cleanAtZero = rep.Clean;
                rep.Conflict = 1;
                check("reconcile clean gate", cleanAtZero && !rep.Clean, "conflict keeps journal");
            }
            catch (Exception ex) { check("reconcile clean gate", false, ex.Message); }

            // 16d. 双引擎仲裁：SHARED 也持有引用，最后一个 Release 才执行 restore。
            try
            {
                var arbiter = SystemResourceArbiter.Instance;
                if (arbiter.CurrentHolder(ResourceKind.TimerResolution) == OwnerKind.NONE
                    && arbiter.RefCount(ResourceKind.TimerResolution) == 0)
                {
                    int applies = 0, restores = 0;
                    var one = new SelftestResourceOwner(OwnerKind.ONE_SHOT);
                    var real = new SelftestResourceOwner(OwnerKind.REALTIME);
                    Func<string> readOld = delegate() { return "1"; };
                    Func<bool> apply = delegate() { applies++; return true; };
                    Func<string, bool> restore = delegate(string old) { restores++; return true; };
                    AcquireResult first = arbiter.Acquire(one, ResourceKind.TimerResolution, readOld, apply);
                    AcquireResult second = arbiter.Acquire(real, ResourceKind.TimerResolution, readOld, apply);
                    arbiter.Release(real, ResourceKind.TimerResolution, restore);
                    bool sharedHeld = arbiter.RefCount(ResourceKind.TimerResolution) == 1 && restores == 0;
                    arbiter.Release(one, ResourceKind.TimerResolution, restore);
                    check("arbiter shared refcount", first.Ok && first.State == AcquireState.GRANTED
                        && second.Ok && second.State == AcquireState.SHARED && applies == 1 && sharedHeld
                        && restores == 1 && arbiter.RefCount(ResourceKind.TimerResolution) == 0
                        && arbiter.CurrentHolder(ResourceKind.TimerResolution) == OwnerKind.NONE,
                        "SHARED acquire -> release -> final release");
                }
                else check("arbiter shared refcount", false, "timer resource already in use");
            }
            catch (Exception ex) { check("arbiter shared refcount", false, ex.Message); }

            // 16e. Profile 主键：进程名优先，旧显示名仍可回退命中，不写磁盘。
            try
            {
                var profiles = new ProfileStore();
                var legacy = new GameProfile();
                legacy.Name = "My Game";
                legacy.DisplayName = "My Game";
                profiles.All.Add(legacy);
                check("profile display fallback", object.ReferenceEquals(legacy, profiles.Find("game.exe", "My Game")),
                    "legacy display-name profile remains reachable");
                var primary = new GameProfile();
                primary.Name = "game";
                primary.DisplayName = "My Game";
                profiles.All.Add(primary);
                check("profile process key", object.ReferenceEquals(primary, profiles.Find("GAME.EXE", "My Game")),
                    "normalized process key has priority");
                var assignedOnly = new ProfileStore();
                var assigned = new GameProfile();
                assigned.Name = "game";
                assigned.DisplayName = "My Game";
                assignedOnly.All.Add(assigned);
                check("profile alias ownership", assignedOnly.Find("other", "My Game") == null,
                    "another process cannot steal an assigned process profile");
            }
            catch (Exception ex) { check("profile process key", false, ex.Message); }

            // 16f. SingleCcd 纯伪拓扑：不修改真实进程亲和性。
            try
            {
                var topo = new CpuTopologySnapshot();
                topo.Cores = new CpuCore[4];
                topo.CcdByCore = new int[] { 0, 0, 1, 1 };
                topo.NumaByCore = new int[] { 0, 0, 0, 0 };
                topo.IsHybrid = true;
                for (int i = 0; i < 4; i++)
                {
                    var core = new CpuCore();
                    core.Index = i;
                    core.Mask = 1L << i;
                    core.Llc = i < 2 ? 0 : 1;
                    core.LlcKnown = true;
                    core.Efficient = i == 2;
                    core.Efficiency = core.Efficient ? 1 : 0;
                    topo.Cores[i] = core;
                }
                int used;
                long mask = CpuTopology.GameMaskForPolicy(topo, CorePolicy.SingleCcd, 0, out used);
                check("singleccd p-core tie break", mask == 3 && used == 2, "equal-size CCD chooses more P cores");
                for (int i = 0; i < 4; i++) topo.Cores[i].LlcKnown = false;
                mask = CpuTopology.GameMaskForPolicy(topo, CorePolicy.SingleCcd, 0, out used);
                check("singleccd unknown llc", mask == 0 && used == 0, "unknown LLC is not a real CCD 0");
                for (int i = 0; i < 4; i++) topo.Cores[i].LlcKnown = true;

                var cacheField = typeof(CoreConfig).GetField("cache", BindingFlags.NonPublic | BindingFlags.Static);
                var cache = (Dictionary<string, string>)cacheField.GetValue(null);
                if (cache == null)
                {
                    cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    cacheField.SetValue(null, cache);
                }
                try
                {
                    typeof(CoreConfig).GetField("cacheAt", BindingFlags.NonPublic | BindingFlags.Static)
                        .SetValue(null, DateTime.UtcNow);
                    cache["coreSingleCcd"] = "1";
                    var scheduler = new CoreScheduler();
                    var tel = new ProcessTelemetry();
                    tel.Pid = -1;
                    tel.Name = "selftest";
                    tel.Cores = 2;
                    CorePlan plan = scheduler.Plan(topo, tel, CorePolicy.SingleCcd);
                    check("singleccd cores propagated", plan.Cores == 2 && plan.GameMask == 1,
                        "requested core count reaches the plan; tiny CCD keeps one reserve core");
                    tel.Cores = 0;
                    CorePlan unlimited = scheduler.Plan(topo, tel, CorePolicy.SingleCcd);
                    var softGame = new ProcessGuard();
                    softGame.Pid = 123;
                    softGame.Name = "selftest";
                    CoreApplyResult soft = scheduler.Apply(unlimited, softGame);
                    check("singleccd unlimited soft only", unlimited.SkipAffinity && soft.Ok && soft.Mask == 0,
                        "all cores keeps policy validation and soft steering without hard affinity");
                    cache["coreSingleCcd"] = "0";
                    CorePlan disabled = scheduler.Plan(topo, tel, CorePolicy.SingleCcd);
                    var fakeGame = new ProcessGuard();
                    fakeGame.Pid = 123;
                    fakeGame.Name = "selftest";
                    CoreApplyResult applied = scheduler.Apply(disabled, fakeGame);
                    check("singleccd disabled honest", !applied.Ok && applied.Error == "singleccd-disabled",
                        "disabled policy never reports false affinity success");
                }
                finally
                {
                    cache.Clear();
                    typeof(CoreConfig).GetField("cacheAt", BindingFlags.NonPublic | BindingFlags.Static)
                        .SetValue(null, DateTime.MinValue);
                }
            }
            catch (Exception ex) { check("singleccd policy selftest", false, ex.Message); }

            // 16g. 对账状态机与注册表缺失原值，只操作独立测试键。
            if (!BoostJournal.Pending)
            {
                const string regPath = "Software\\FrameForge\\SelftestReconcile";
                var ctx = new ReconcileContext();
                ctx.IsSameProcess = true;
                ctx.IsAdmin = true;
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(regPath))
                        key.SetValue("v", 99, Microsoft.Win32.RegistryValueKind.DWord);
                    var conflictTx = BoostJournal.NewTransaction(OwnerKind.ONE_SHOT, 0, "selftest-conflict", -1);
                    var conflictAction = BoostJournal.NewAction(conflictTx, ActionKind.REG_DWORD, null,
                        BoostJournal.Base64Int(1), BoostJournal.Base64Int(2));
                    conflictAction.Path = regPath;
                    conflictAction.Name = "v";
                    conflictAction.Existed = true;
                    conflictAction.Status = ActionStatus.CONFLICT_SKIPPED;
                    conflictTx.State = TxState.COMMITTED;
                    BoostJournal.Save(conflictTx);
                    ReconcileReport conflict = BoostJournal.Reconcile(BoostJournal.Load(), ctx);
                    check("journal conflict retained", conflict.Conflict == 1 && File.Exists(BoostJournal.FilePath),
                        "historical conflict is retried, never silently cleared");
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(regPath, true))
                        key.SetValue("v", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    ReconcileReport resolved = BoostJournal.Reconcile(BoostJournal.Load(), ctx);
                    check("journal conflict resolved", resolved.Clean && !File.Exists(BoostJournal.FilePath),
                        "only current==old allows journal cleanup");

                    BoostJournal.Clear();
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(regPath))
                        key.SetValue("v", 0, Microsoft.Win32.RegistryValueKind.DWord);
                    var missingTx = BoostJournal.NewTransaction(OwnerKind.ONE_SHOT, 0, "selftest-missing", -1);
                    var missingAction = BoostJournal.NewAction(missingTx, ActionKind.REG_DWORD, null,
                        BoostJournal.Base64Int(0), BoostJournal.Base64Int(0));
                    missingAction.Path = regPath;
                    missingAction.Name = "v";
                    missingAction.Existed = false;
                    missingAction.Status = ActionStatus.APPLIED_CONFIRMED;
                    missingTx.State = TxState.COMMITTED;
                    BoostJournal.Save(missingTx);
                    ReconcileReport missing = BoostJournal.Reconcile(BoostJournal.Load(), ctx);
                    object left;
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(regPath))
                        left = key == null ? null : key.GetValue("v", null);
                    check("journal missing reg original", missing.Clean && left == null,
                        "old missing + new zero deletes the created value");

                    var pendingTx = BoostJournal.NewTransaction(OwnerKind.ONE_SHOT, 0, "selftest-pending", -1);
                    var pendingAction = BoostJournal.NewAction(pendingTx, ActionKind.REG_DWORD, null,
                        BoostJournal.Base64Int(0), BoostJournal.Base64Int(7));
                    pendingAction.Path = regPath;
                    pendingAction.Name = "v";
                    pendingAction.Existed = false;
                    pendingTx.State = TxState.COMMITTED;
                    BoostJournal.Save(pendingTx);
                    ReconcileReport pending = BoostJournal.Reconcile(BoostJournal.Load(), ctx);
                    check("journal pending missing noop", pending.Clean && pending.Conflict == 0,
                        "PENDING before Apply already matches the missing original");
                }
                catch (Exception ex) { check("journal reconcile regression", false, ex.Message); }
                finally
                {
                    BoostJournal.Clear();
                    BoostJournal.Clear(OwnerKind.REALTIME);
                    try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(regPath, false); } catch { }
                }
            }
            else check("journal reconcile regression", false, "real boost journal exists");

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

            // 19. 滤镜矩阵:行向量转置 + 灰度中性 + 参数安全区间(v1.5.1 规格)
            try
            {
                float[] gray = Filter.BuildCore(8, 0, 0, 0, 0);
                System.Drawing.Color gw = Filter.Transform(System.Drawing.Color.White, gray);
                System.Drawing.Color gg = Filter.Transform(System.Drawing.Color.Lime, gray);
                check("filter gray neutral",
                    Math.Abs(gw.R - gw.G) <= 1 && Math.Abs(gw.G - gw.B) <= 1
                    && Math.Abs(gg.R - gg.G) <= 1 && Math.Abs(gg.G - gg.B) <= 1
                    && Math.Abs(gg.R - 182) <= 3 && Math.Abs(gw.R - 255) <= 1,
                    "white=" + gw.R + "," + gw.G + "," + gw.B + " green=" + gg.R + "," + gg.G + "," + gg.B);

                float[] t = Filter.Transpose(gray);
                check("filter row-vector export",
                    Math.Abs(t[20] - gray[4]) < 0.0001f && t[4] == 0f, "offset -> m20");

                float[] night = Filter.BuildCore(7, 0, 0, 0, 0);
                System.Drawing.Color nw = Filter.Transform(System.Drawing.Color.White, night);
                double nightLum = (0.2126 * nw.R + 0.7152 * nw.G + 0.0722 * nw.B) / 255.0;
                check("filter night range", night[4] >= 0f && night[4] <= 0.009f
                    && nightLum >= 0.86 && nightLum <= 0.97,
                    "y0=" + night[4].ToString("0.0000") + " y1=" + nightLum.ToString("0.000"));

                float[] cs2 = Filter.BuildCore(17, 0, 0, 0, 0);
                System.Drawing.Color cs2g = Filter.Transform(System.Drawing.Color.FromArgb(26, 26, 26), cs2);
                double cs2Lum = (0.2126 * cs2g.R + 0.7152 * cs2g.G + 0.0722 * cs2g.B);
                check("filter cs2 shadows", cs2Lum >= 26.5, "10% grey=" + cs2Lum.ToString("0.0"));
            }
            catch (Exception ex) { check("filter gray neutral", false, ex.Message); }

            // HDR / 自动颜色管理探测:只报告状态,探测失败也算可诊断信息而不是失败。
            try
            {
                bool hdr = Display.HdrOrAcmActive();
                check("advanced color probe", true,
                    "active=" + hdr + " detail=" + Display.AdvancedColorDetail);
            }
            catch (Exception ex) { check("advanced color probe", false, ex.Message); }

            // 21. 仲裁者并发:两个引擎同时抢计时器,各自持锁外回调,不允许互等死锁。
            try
            {
                ISystemResourceOwner one = new SelftestResourceOwner(OwnerKind.ONE_SHOT);
                ISystemResourceOwner real = new SelftestResourceOwner(OwnerKind.REALTIME);
                SystemResourceArbiter arb = SystemResourceArbiter.Instance;
                Exception threadError = null;
                ParameterizedThreadStart work = delegate(object state)
                {
                    ISystemResourceOwner me = (ISystemResourceOwner)state;
                    try
                    {
                        for (int i = 0; i < 30; i++)
                        {
                            AcquireResult r = arb.Acquire(me, ResourceKind.TimerResolution,
                                delegate { return "10000"; },
                                delegate { Thread.Sleep(2); return true; });
                            if (!r.Ok) throw new InvalidOperationException("acquire denied: " + r.Reason);
                            arb.Release(me, ResourceKind.TimerResolution,
                                delegate(string oldValue) { Thread.Sleep(2); return true; });
                        }
                    }
                    catch (Exception ex) { threadError = ex; }
                };
                var t1 = new Thread(work);
                var t2 = new Thread(work);
                t1.IsBackground = true;
                t2.IsBackground = true;
                t1.Start(one);
                t2.Start(real);
                bool joined = t1.Join(8000) & t2.Join(8000);
                check("arbiter concurrent", joined && threadError == null,
                    !joined ? "deadlock/timeout" : (threadError == null ? "60 cycles" : threadError.Message));
            }
            catch (Exception ex) { check("arbiter concurrent", false, ex.Message); }

            // 20. 程序图标读取:战绩、报告、保护名单共用的 exe 图标
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