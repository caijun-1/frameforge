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
                check("gpu name", s != null && s.GpuName.Length > 0, s == null ? "null" : s.GpuName);
                check("gpu load", s != null && s.Gpu >= 0,
                    s == null ? "null" : s.Gpu.ToString("0.0", CultureInfo.InvariantCulture) + "%" +
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

            sb.AppendLine();
            sb.AppendLine(fail == 0 ? "RESULT: OK" : "RESULT: " + fail + " failure(s)");

            string path = Path.Combine(Path.GetTempPath(), "frameforge_selftest.log");
            try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); }
            catch { }
            Log.Write("selftest: " + (fail == 0 ? "ok" : fail + " failure(s)"));
            return fail == 0 ? 0 : 1;
        }
    }
}