using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
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

        private static readonly object Gate = new object();
        private Process proc;
        private Thread reader;
        private int trackedPid = -1;
        private volatile bool stopping;
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
            try
            {
                if (!Directory.Exists(BinDir)) Directory.CreateDirectory(BinDir);
                string target = Path.Combine(BinDir, "presentmon.exe");
                string temp = target + ".tmp";
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "FrameForge/1.0");
                    if (progress != null) wc.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
                    { try { progress(e.ProgressPercentage); } catch { } };
                    wc.DownloadFile(new Uri(DownloadUrl), temp);
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
                Log.Write("presentmon downloaded " + new FileInfo(target).Length + " bytes");
                return "";
            }
            catch (Exception ex)
            {
                Log.Write("presentmon download: " + ex.Message);
                return ex.Message;
            }
        }

        /// <summary>开始跟踪某个进程;pid 未变化时什么也不做。</summary>
        public void Track(int pid)
        {
            if (pid == trackedPid && proc != null && !proc.HasExited) return;
            Stop();
            trackedPid = pid;
            if (pid <= 0) return;
            if (!Installed) { Status = FpsStatus.NotInstalled; return; }
            if (!MemEngine.IsAdmin) { Status = FpsStatus.NeedAdmin; return; }
            Start(pid);
        }

        private void Start(int pid)
        {
            try
            {
                lock (Gate) { window.Clear(); }
                Fps = -1;
                Low1 = -1;
                stopping = false;

                var psi = new ProcessStartInfo();
                psi.FileName = ExePath;
                psi.Arguments = "--output_stdout --no_console_stats --v1_metrics --no_track_input"
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
                Status = FpsStatus.Running;
                Message = "";
                Log.Write("presentmon start pid=" + pid);
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
            }
            reader = null;
            trackedPid = -1;
            Fps = -1;
            Low1 = -1;
            if (Status == FpsStatus.Running) Status = FpsStatus.Idle;
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
                    if (line[0] == 'A' && line.StartsWith("Application", StringComparison.Ordinal))
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
                if (!stopping)
                {
                    Status = FpsStatus.Failed;
                    Message = "presentmon exited";
                }
            }
        }

        private void Push(double t, double ms)
        {
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
            // PresentMon 输出不含引号字段,直接按逗号切分
            return line.Split(',');
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