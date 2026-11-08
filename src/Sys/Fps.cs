using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace FrameForge.Sys
{
    public enum FpsStatus
    {
        Idle = 0,
        Running = 1,
        NotInstalled = 2,
        NeedAdmin = 3,
        Failed = 4
    }

    /// <summary>
    /// 帧率引擎:启动 Intel PresentMon 子进程读 ETW 呈现事件,解析 CSV 得到 FPS 与 1% Low。
    /// 全程不注入游戏进程。
    /// </summary>
    public sealed class FpsEngine : IDisposable
    {
        public const string Version = "2.6.0";
        private const string DownloadUrl =
            "https://github.com/GameTechDev/PresentMon/releases/download/v" + Version + "/PresentMon-" + Version + "-x64.exe";

        // GitHub 在国内经常连不上(部分用户会看到"无法连接到远程服务器"),
        // 按顺序依次尝试,前一个失败就换下一个,而不是让用户自己去捣鼓网络。
        private static readonly string[] DownloadUrls = new string[]
        {
            DownloadUrl,
            "https://gh-proxy.com/" + DownloadUrl,
            "https://ghfast.top/" + DownloadUrl
        };

        private static readonly object Gate = new object();
        private readonly object startGate = new object();
        private volatile Process proc;
        private Thread reader;
        private Thread errReader;
        private int trackedPid = -1;
        private DateTime lastTry = DateTime.MinValue;
        private DateTime nextRecoverAt = DateTime.MinValue;
        private DateTime lastEtwReset = DateTime.MinValue;
        private int watchFails;
        private long lastRowTicks;
        private volatile bool stopping;
        private volatile bool gotRow;
        private readonly Queue<Frame> window = new Queue<Frame>();

        public volatile FpsStatus Status = FpsStatus.Idle;
        public double Fps = -1;
        public double Low1 = -1;
        public string Message = "";

        private struct Frame
        {
            public double T;
            public double Ms;
        }

        public static string BinDir
        {
            get { return Path.Combine(Settings.Dir, "bin"); }
        }

        public static string ExePath
        {
            get
            {
                string local = Path.Combine(BinDir, "presentmon.exe");
                if (File.Exists(local)) return local;
                string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "presentmon.exe");
                if (File.Exists(beside)) return beside;
                return local;
            }
        }

        public static bool Installed
        {
            get
            {
                try
                {
                    var f = new FileInfo(ExePath);
                    return f.Exists && f.Length > 200 * 1024;
                }
                catch { return false; }
            }
        }

        /// <summary>下载 PresentMon 到 %LOCALAPPDATA%\FrameForge\bin。返回空串表示成功。</summary>
        public static string Download(Action<int> progress)
        {
            string temp = Path.Combine(BinDir, "presentmon.exe.tmp");
            string last = "";
            try
            {
                if (!Directory.Exists(BinDir)) Directory.CreateDirectory(BinDir);
                string target = Path.Combine(BinDir, "presentmon.exe");
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

                for (int i = 0; i < DownloadUrls.Length; i++)
                {
                    try
                    {
                        Fetch(DownloadUrls[i], temp, progress);
                        // 代理站点出错时可能回一个网页,按大小和 PE 头挡掉,别把垃圾当引擎装进去。
                        var fi = new FileInfo(temp);
                        if (fi.Length < 200 * 1024) throw new Exception("downloaded file is too small (" + fi.Length + " bytes)");
                        using (var fs = new FileStream(temp, FileMode.Open, FileAccess.Read))
                        {
                            if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new Exception("downloaded file is not an exe");
                        }
                        // 大小和 MZ 头只能挡网页;镜像站被劫持时能给出同样大小的恶意 exe,
                        // 而它随后会以管理员身份启动,所以必须验 Intel 的代码签名。
                        if (!SignedByIntel(temp)) throw new Exception(I18n.T("err.sign"));
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(temp, target);
                        Log.Write("presentmon downloaded from " + (i + 1) + ". source, " + fi.Length + " bytes");
                        return "";
                    }
                    catch (Exception ex)
                    {
                        last = ex.Message;
                        Log.Write("presentmon download try " + (i + 1) + ": " + ex.Message);
                        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                        if (progress != null) { try { progress(0); } catch { } }
                    }
                }
            }
            catch (Exception ex)
            {
                last = ex.Message;
            }
            if (last.Length == 0) last = I18n.T("err.net");
            Log.Write("presentmon download failed: " + last);
            return last;
        }

        /// <summary>下载单个地址。带超时,否则连不上的源会让按钮一直转圈。</summary>
        private static void Fetch(string url, string temp, Action<int> progress)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "FrameForge/1.0";
            req.Timeout = 12000;
            req.ReadWriteTimeout = 20000;
            req.AllowAutoRedirect = true;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (Stream src = resp.GetResponseStream())
            using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                long total = resp.ContentLength;
                byte[] buf = new byte[65536];
                long got = 0;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    got += n;
                    if (progress != null && total > 0)
                    {
                        try { progress((int)(got * 100 / total)); } catch { }
                    }
                }
            }
        }

        /// <summary>
        /// 判断文件是否是 Intel 签名的 exe:签名链完整可信,且签名方是 Intel。
        /// 只做 SHA256 比对的话,上游重发同一版本就会误伤用户;验签没有这个问题。
        /// </summary>
        public static bool SignedByIntel(string path)
        {
            // 先只查本机缓存,避免离线/墙内环境下 WinVerifyTrust 卡在取根证书上;
            // 缓存里没有中间证书时再允许联网补一次。
            if (!VerifyTrust(path, true) && !VerifyTrust(path, false)) return false;
            try
            {
                X509Certificate cert = X509Certificate.CreateFromSignedFile(path);
                if (cert.Subject.IndexOf("Intel Corporation", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                Log.Write("presentmon signed by unexpected publisher: " + cert.Subject);
                return false;
            }
            catch (Exception ex)
            {
                Log.Write("presentmon signature inspect: " + ex.Message);
                return false;
            }
        }

        private static readonly Guid VerifyAction = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WintrustFileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WintrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern uint WinVerifyTrust(IntPtr hwnd,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, IntPtr data);

        private static bool VerifyTrust(string path, bool cacheOnly)
        {
            IntPtr filePtr = IntPtr.Zero;
            IntPtr dataPtr = IntPtr.Zero;
            try
            {
                var file = new WintrustFileInfo();
                file.cbStruct = (uint)Marshal.SizeOf(typeof(WintrustFileInfo));
                file.pcwszFilePath = path;
                filePtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WintrustFileInfo)));
                Marshal.StructureToPtr(file, filePtr, false);

                var data = new WintrustData();
                data.cbStruct = (uint)Marshal.SizeOf(typeof(WintrustData));
                data.dwUIChoice = 2;            // WTD_UI_NONE:不弹任何对话框
                data.fdwRevocationChecks = 0;   // WTD_REVOKE_NONE:不查吊销,免得离线时失败
                data.dwUnionChoice = 1;         // WTD_CHOICE_FILE
                data.pFile = filePtr;
                data.dwStateAction = 1;         // WTD_STATEACTION_VERIFY
                data.dwProvFlags = cacheOnly ? 0x1000u : 0u; // WTD_CACHE_ONLY_URL_RETRIEVAL
                dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WintrustData)));
                Marshal.StructureToPtr(data, dataPtr, false);

                uint result = WinVerifyTrust(IntPtr.Zero, VerifyAction, dataPtr);

                // 验完必须 CLOSE 一次,否则 wintrust 的内部状态会一直挂着。
                data = (WintrustData)Marshal.PtrToStructure(dataPtr, typeof(WintrustData));
                data.dwStateAction = 2;         // WTD_STATEACTION_CLOSE
                Marshal.StructureToPtr(data, dataPtr, false);
                WinVerifyTrust(IntPtr.Zero, VerifyAction, dataPtr);

                return result == 0;
            }
            catch (Exception ex)
            {
                Log.Write("presentmon verify trust: " + ex.Message);
                return false;
            }
            finally
            {
                if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
                if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
            }
        }

        /// <summary>开始跟踪某个进程;pid 未变化时什么也不做。</summary>
        public void Track(int pid)
        {
            if (pid == trackedPid && proc != null && !proc.HasExited)
            {
                // PresentMon 有时"活着但不再输出":ETW 实时会话被系统诊断会话挤占时,
                // 进程不退出、stderr 只刷 lost events,界面上的帧率就停在最后一个数字。
                // 按"最近一行数据"做心跳,超过 15 秒没有新行才重启采集会话。
                // 读盘/加载场景本来就不出帧,定得太短会在加载时反复重启,
                // 每次重启都要拆一次 ETW 实时会话,那本身就是卡顿源。
                long last = Interlocked.Read(ref lastRowTicks);
                if (last == 0) return;
                if ((DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalSeconds < 15) return;
                if (DateTime.UtcNow < nextRecoverAt) return;
                nextRecoverAt = DateTime.UtcNow.AddSeconds(6);
                Log.Write("presentmon stalled, restart pid=" + pid);
                Stop();
                trackedPid = pid;
                lastTry = DateTime.UtcNow;
                Start(pid);
                return;
            }
            if (pid <= 0) { Stop(); return; }
            if (!Installed) { trackedPid = pid; Status = FpsStatus.NotInstalled; return; }

            // PresentMon 2.x 在普通权限下也能采集同账户进程的帧事件(只有跨账户/短命进程
            // 才需要提权),以前在这里直接因"不是管理员"返回,叠加层就永远只显示 --。
            // 但它异常退出后会占着 ETW 会话名,失败后先等 3 秒再重启,避免每秒开一次进程。
            if (pid == trackedPid && Status == FpsStatus.NeedAdmin) return;
            if (pid == trackedPid && Status == FpsStatus.Failed
                && (DateTime.UtcNow - lastTry).TotalSeconds < 3) return;

            Stop();
            trackedPid = pid;
            lastTry = DateTime.UtcNow;
            Start(pid);
        }

        private void Start(int pid)
        {
            lock (startGate)
            {
                if (pid != trackedPid) return;
                // 界面心跳(Track)和看门狗会同时走到这里。两个 PresentMon 抢同一个
                // ETW 会话名时,后启动的会把先启动的踢掉,日志里就是 start 紧跟一行
                // exited code=0,帧率窗口被反复清空,叠加上层统计全是抖的。
                if (proc != null && !proc.HasExited) return;
                StartCore(pid);
            }
        }

        private void StartCore(int pid)
        {
            if (pid != trackedPid) return;
            try
            {
                lock (Gate) { window.Clear(); }
                Fps = -1;
                Low1 = -1;
                stopping = false;
                gotRow = false;
                Interlocked.Exchange(ref lastRowTicks, DateTime.UtcNow.Ticks);

                var psi = new ProcessStartInfo();
                psi.FileName = ExePath;
                psi.Arguments = "--output_stdout --no_console_stats --v1_metrics --no_track_input --stop_existing_session"
                              + " --process_id " + pid.ToString(CultureInfo.InvariantCulture)
                              + " --session_name FrameForge";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                proc = new Process();
                proc.StartInfo = psi;
                proc.Start();
                reader = new Thread(ReadLoop);
                reader.IsBackground = true;
                reader.Name = "FrameForge.Fps";
                reader.Start(proc);

                // stderr 必须有人读走。PresentMon 会刷大量 "ETW events were lost",
                // 管道写满后它会阻塞在 stderr 上,stdout 的帧数据也跟着停,
                // 表现就是帧率只出一下、之后再也不更新。
                errReader = new Thread(ErrLoop);
                errReader.IsBackground = true;
                errReader.Name = "FrameForge.FpsErr";
                errReader.Start(proc);
                Status = FpsStatus.Running;
                Message = "";
                Log.Write("presentmon start pid=" + pid);
                StartWatchdog(pid);
            }
            catch (Exception ex)
            {
                Status = FpsStatus.Failed;
                Message = ex.Message;
                Log.Write("presentmon start: " + ex.Message);
            }
        }

        public void Stop()
        {
            stopping = true;
            Process p = proc;
            proc = null;
            if (p != null)
            {
                try { if (!p.HasExited) p.Kill(); }
                catch { }
                try { p.Dispose(); }
                catch { }
                // 被 Kill 掉的 PresentMon 会把实时会话名一直占着,下次启动虽然带了
                // --stop_existing_session,但残留会话会干扰采集,这里顺手停干净。
                Logman("stop \"FrameForge\" -ets", false);
            }
            reader = null;
            errReader = null;
            trackedPid = -1;
            Interlocked.Exchange(ref lastRowTicks, 0);
            Fps = -1;
            Low1 = -1;
            if (Status == FpsStatus.Running || Status == FpsStatus.NeedAdmin) Status = FpsStatus.Idle;
        }

        /// <summary>
        /// 启动后 10 秒还一行数据都没有,说明 PresentMon 的 ETW 会话抢不到事件
        /// (本机实测:系统诊断会话把实时缓冲占满时,它只会刷 "ETW events were lost")。
        /// 这时清理一次噪声会话并重启,而不是一直空转。
        /// </summary>
        private void StartWatchdog(int pid)
        {
            Thread t = new Thread(delegate()
            {
                // 连续失败时退避:10 / 20 / 40 秒。以前固定 6 秒重试,
                // 采集抢不到事件时会变成一直杀进程、反复跑 logman,抖动本身就是卡顿源。
                // 另外游戏加载/编译着色器时本来就不出帧,首行常常要等十几秒。
                int waitSec = 10;
                for (int k = 0; k < watchFails && k < 2; k++) waitSec *= 2;
                for (int i = 0; i < waitSec * 2; i++)
                {
                    Thread.Sleep(500);
                    if (stopping || gotRow || proc == null || trackedPid != pid) return;
                }
                if (stopping || gotRow || proc == null || trackedPid != pid) return;
                if (Status == FpsStatus.NeedAdmin) return;

                watchFails++;
                Log.Write("presentmon watchdog: no rows in " + waitSec + "s, retry #" + watchFails);
                Process p = proc;
                proc = null;
                try { if (!p.HasExited) p.Kill(); }
                catch { }
                try { p.Dispose(); }
                catch { }

                // 清理噪声会话要为每个会话起一次 logman,代价不小,10 分钟内只做一次。
                if ((DateTime.UtcNow - lastEtwReset).TotalMinutes >= 10)
                {
                    lastEtwReset = DateTime.UtcNow;
                    ResetEtw();
                }
                if (!stopping && trackedPid == pid) Start(pid);
            });
            t.IsBackground = true;
            t.Name = "FrameForge.FpsWatch";
            t.Start();
        }

        /// <summary>
        /// 只停"诊断/记录"这一类高噪声实时会话,系统其它会话不动。
        /// 依据:本机 ETWSessionRecorder + WinDiag-* + SHS-* 同时运行时,
        /// PresentMon 5 秒收到 0 行;停掉后立刻变成 1900+ 行。
        /// </summary>
        private static void ResetEtw()
        {
            Logman("stop \"FrameForge\" -ets", false);
            Logman("stop ETWSessionRecorder -ets", false);

            string q = Logman("query -ets", true);
            if (q.Length == 0) return;
            string[] lines = q.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string[] cols = lines[i].Trim().Split(new char[] { ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 2) continue;
                string name = cols[0];
                if (name.IndexOf("WinDiag", StringComparison.OrdinalIgnoreCase) != 0
                    && name.IndexOf("SHS-", StringComparison.OrdinalIgnoreCase) != 0
                    && name.IndexOf("DiagLog", StringComparison.OrdinalIgnoreCase) != 0) continue;

                Logman("stop \"" + name + "\" -ets", false);
                Log.Write("etw stop noisy session " + name);
            }
        }

        /// <summary>跑一次 logman(不弹窗)。capture=true 时返回输出,否则返回空串。</summary>
        private static string Logman(string args, bool capture)
        {
            try
            {
                var psi = new ProcessStartInfo("logman", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = capture;
                psi.RedirectStandardError = !capture;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return "";
                    if (!capture)
                    {
                        p.StandardError.ReadToEnd();
                        p.WaitForExit(800);
                        return "";
                    }
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2000);
                    return o;
                }
            }
            catch { return ""; }
        }

        private void ReadLoop(object state)
        {
            Process p = (Process)state;
            int tCol = -1, msCol = -1;
            try
            {
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (stopping) break;
                    if (line.Length == 0) continue;
                    // 表头判定不能依赖首列叫 "Application":不同版本的 PresentMon 可能带 BOM、
                    // 引号或改了首列名,只要这一行含 TimeInSeconds 就说明是 CSV 表头。
                    if (line.IndexOf("TimeInSeconds", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string[] head = SplitCsv(line);
                        tCol = IndexOf(head, "TimeInSeconds");
                        msCol = IndexOf(head, "MsBetweenPresents");
                        if (msCol < 0) msCol = IndexOf(head, "MsBetweenDisplayChange");
                        continue;
                    }
                    if (tCol < 0 || msCol < 0) continue;
                    string[] cols = SplitCsv(line);
                    if (cols.Length <= Math.Max(tCol, msCol)) continue;
                    double t, ms;
                    if (!double.TryParse(cols[tCol], NumberStyles.Float, CultureInfo.InvariantCulture, out t)) continue;
                    if (!double.TryParse(cols[msCol], NumberStyles.Float, CultureInfo.InvariantCulture, out ms)) continue;
                    if (ms <= 0 || ms > 5000) continue;
                    Push(t, ms);
                }
            }
            catch { }
            finally
            {
                // 换游戏时会先 Stop 再 Start,旧线程的 finally 可能在新实例启动之后才跑到。
                // 只在自己仍是当前进程时改状态,否则会把新实例的 Running 覆盖成 Failed。
                if (!stopping && ReferenceEquals(proc, p) && Status != FpsStatus.NeedAdmin)
                {
                    Status = FpsStatus.Failed;
                    Message = "presentmon exited";
                    string code = "";
                    try { if (p.HasExited) code = " code=" + p.ExitCode; } catch { }
                    Log.Write("presentmon exited" + code + " rows=" + (gotRow ? "yes" : "no"));
                }
            }
        }

        /// <summary>把 stderr 读空(只记首行和行数),避免它把管道堵住。</summary>
        private void ErrLoop(object state)
        {
            Process p = (Process)state;
            int lines = 0;
            string first = "";
            try
            {
                string line;
                while ((line = p.StandardError.ReadLine()) != null)
                {
                    lines++;
                    if (first.Length == 0 && line.Trim().Length > 0) first = line.Trim();
                    // PresentMon 没提权时会直接拒绝采集,界面要提示"需要管理员权限",
                    // 而不是一直显示 -- 让人以为工具坏了。
                    if (Status == FpsStatus.Running
                        && line.IndexOf("elevated privilege", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Status = FpsStatus.NeedAdmin;
                        Message = "presentmon needs admin";
                        Log.Write("presentmon needs admin privilege");
                    }
                }
            }
            catch { }
            if (lines > 0) Log.Write("presentmon stderr " + lines + " line(s): " + first);
        }

        private void Push(double t, double ms)
        {
            Interlocked.Exchange(ref lastRowTicks, DateTime.UtcNow.Ticks);
            if (!gotRow)
            {
                gotRow = true;
                watchFails = 0;
                Log.Write("presentmon first row t=" + t.ToString("0.00", CultureInfo.InvariantCulture));
            }
            lock (Gate)
            {
                window.Enqueue(new Frame { T = t, Ms = ms });
                while (window.Count > 0 && window.Peek().T < t - 5.0) window.Dequeue();

                if (window.Count < 2) return;

                // FPS:最后一秒内的帧数
                int framesLastSecond = 0;
                double oldest = double.MaxValue;
                foreach (Frame f in window)
                {
                    if (f.T >= t - 1.0) framesLastSecond++;
                    oldest = f.T;
                }
                double span = t - oldest;
                double fps;
                if (framesLastSecond >= 2 && span > 0.05) fps = framesLastSecond / Math.Min(1.0, span);
                else
                {
                    double sum = 0;
                    foreach (Frame f in window) sum += f.Ms;
                    fps = 1000.0 / (sum / window.Count);
                }

                // 1% Low:5 秒窗口内帧时间的 99 分位
                var times = new List<double>(window.Count);
                foreach (Frame f in window) times.Add(f.Ms);
                times.Sort();
                int idx = (int)Math.Floor((times.Count - 1) * 0.99);
                double p99 = times[Math.Min(idx, times.Count - 1)];

                Fps = fps > 0 ? fps : -1;
                Low1 = p99 > 0 ? 1000.0 / p99 : -1;
            }
        }

        private static string[] SplitCsv(string line)
        {
            // PresentMon 输出不含带逗号的引号字段,直接按逗号切分;字段可能带引号或空格
            string[] parts = line.Split(',');
            for (int i = 0; i < parts.Length; i++)
                parts[i] = parts[i].Trim().Trim('"');
            return parts;
        }

        private static int IndexOf(string[] arr, string name)
        {
            for (int i = 0; i < arr.Length; i++)
                if (string.Equals(arr[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}