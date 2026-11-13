using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    /// <summary>
    /// RTSS(RivaTuner Statistics Server)OSD 后端。
    ///
    /// 独占全屏时 DWM 不参与合成,FrameForge 自己的分层窗口根本画不上去;
    /// RTSS 是注入到游戏的 present 链里画的,全屏也能显示。这里只往 RTSS 的
    /// 共享内存 OSD 槽里写文字,由 RTSS 负责画:不注入、不碰游戏、不装驱动。
    /// 瓦罗兰特这类反作弊会拦住 RTSS 的游戏,依然显示不了。
    /// </summary>
    public static class Rtss
    {
        private const string MapName = "RTSSSharedMemoryV2";
        private const string Owner = "FrameForge";
        private const int Signature = 0x52545353;   // 'RTSS'
        private const uint FileMapAllAccess = 0xF001F;
        // v2.0 结构:9 个 DWORD 头 + OSD 槽数组 + APP 槽数组。
        private const int OffVersion = 4, OffOsdEntrySize = 20, OffOsdArrOffset = 24, OffOsdArrSize = 28, OffOsdFrame = 32;
        // 单个 OSD 槽里:szOSD[256] + szOSDOwner[256];v2.7 起又在后面加了 szOSDEx[4096]。
        private const int OffOwner = 256, OffTextEx = 512, TextCap = 4096;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr addr);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static IntPtr map, view;
        // 后台线程轮询状态时会调 Running→Ensure,界面上每秒也会写 OSD,
        // 共享内存的打开/关闭/写入必须串起来,不然一边刚释放另一边还在写。
        private static readonly object osdLock = new object();
        private static int entrySize, arrOffset, arrSize, version, slot = -1;
        private static string lastText = "";
        private static DateTime nextTry = DateTime.MinValue;
        private static DateTime nextInstallCheck = DateTime.MinValue;
        private static string installDir;
        private static bool lastProcAlive;
        private static DateTime nextProcCheck = DateTime.MinValue;

        /// <summary>
        /// RTSS 正在运行:共享内存能打开,而且进程真的还活着。
        /// 只看共享内存会被骗——RTSS 被强杀后它注入的钩子还握着那块内存,
        /// 签名一直是有效的,进程早没了却仍会显示“运行中”。
        /// </summary>
        public static bool Running { get { return Ensure() && ProcAlive(); } }

        /// <summary>
        /// OSD 内嵌颜色标签用的 RRGGBB(调用处再补 FF 当 alpha,RTSS 不接受全透明)。
        /// 键来自设置页,未知值一律回到白色。
        /// </summary>
        public static string ColorHex(string key)
        {
            switch (key)
            {
                case "pink": return "FF4FD8";
                case "green": return "57E389";
                case "yellow": return "FFD166";
                case "cyan": return "4FD8E8";
                default: return "FFFFFF";
            }
        }

        /// <summary>RTSS 主进程在不在。1 秒缓存,界面每秒查也不心疼。</summary>
        private static bool ProcAlive()
        {
            if (DateTime.UtcNow < nextProcCheck) return lastProcAlive;
            lastProcAlive = ProcAliveNow();
            nextProcCheck = DateTime.UtcNow.AddSeconds(1);
            return lastProcAlive;
        }

        /// <summary>不走缓存的进程检测:确认启停结果时用它。</summary>
        private static bool ProcAliveNow()
        {
            bool alive = false;
            try
            {
                Process[] ps = Process.GetProcessesByName("RTSS");
                alive = ps.Length > 0;
                foreach (Process p in ps) p.Dispose();
            }
            catch (Exception ex) { Log.Write("rtss proc: " + ex.Message); }
            return alive;
        }

        // ---- 在设置页里直接装 ----
        // 官方安装包(Guru3D 镜像上 winget 用的同一个文件)。版本和哈希都写死:
        // 装的是一个确定的东西,校验不过就退回手动下载页,绝不执行来源不明的东西。
        private const string InstallerUrl =
            "https://ftp.nluug.nl/pub/games/PC/guru3d/afterburner/%5BGuru3D%5D-RTSSSetup737Build28314.zip";
        private const string InstallerSha = "9B084A8CB3E53EC1A673894D0B66E22B16C9FD8785636B020B2D422F3F2A820E";
        private const string InstallerExe = "RTSSSetup737.exe";
        public const string DownloadPage =
            "https://www.guru3d.com/files-details/rtss-rivatuner-statistics-server-download.html";

        /// <summary>安装进度:stage = download / unzip / run / done / fail。</summary>
        public delegate void InstallProgress(int percent, string stage);

        /// <summary>本机装了 RTSS(不一定在运行)。</summary>
        public static bool Installed { get { return ExePath.Length > 0; } }

        /// <summary>装好的 RTSS.exe 路径;没装返回空串。结果按 5 秒缓存。</summary>
        public static string ExePath
        {
            get
            {
                if (installDir == null || (installDir.Length == 0 && DateTime.UtcNow >= nextInstallCheck))
                {
                    installDir = FindInstall();
                    nextInstallCheck = DateTime.UtcNow.AddSeconds(5);
                }
                return installDir.Length > 0 ? Path.Combine(installDir, "RTSS.exe") : "";
            }
        }

        /// <summary>装完/装到一半时把检测缓存清掉,状态行才能立刻变。</summary>
        public static void RefreshDetection()
        {
            installDir = null;
            nextInstallCheck = DateTime.MinValue;
        }

        /// <summary>启动已经装好的 RTSS(它自己会缩到托盘)。</summary>
        public static bool Launch()
        {
            string exe = ExePath;
            if (exe.Length == 0 || !File.Exists(exe)) return false;
            // 主进程不在、钩子加载器还在的时候,新 RTSS 会以为已经有实例在跑,
            // 起来几秒就自己退出,界面上看起来就是“点了启动却没反应”。
            if (!ProcAliveNow()) KillHooks();
            try
            {
                var psi = new ProcessStartInfo(exe);
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = true;
                Process.Start(psi);
                nextProcCheck = DateTime.MinValue;
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("rtss launch: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 停掉正在运行的 RTSS(不卸载文件)。先把自己写的 OSD 清掉,
        /// 再结束进程,屏幕上的字不会留在那里。
        /// </summary>
        public static bool Stop()
        {
            Clear();
            bool hit = false;
            // 只杀 RTSS 主进程不够:它派生的钩子加载器会继续握着共享内存,
            // 让状态一直显示“运行中”,新起的 RTSS 也会以为已经有实例在跑。
            string[] names = { "RTSS", "RTSSHooksLoader", "RTSSHooksLoader64" };
            for (int i = 0; i < names.Length; i++)
            {
                Process[] ps;
                try { ps = Process.GetProcessesByName(names[i]); }
                catch (Exception ex)
                {
                    Log.Write("rtss stop " + names[i] + ": " + ex.Message);
                    continue;
                }
                foreach (Process p in ps)
                {
                    try
                    {
                        p.Kill();
                        if (names[i] == "RTSS") hit = true;
                        // Kill 只是发出请求:不等它真的退出,紧接着的启动会撞上
                        // 还没死透的旧实例,连着点几次就再也起不来了。
                        p.WaitForExit(4000);
                    }
                    catch (Exception ex) { Log.Write("rtss stop " + names[i] + ": " + ex.Message); }
                    p.Dispose();
                }
            }

            // 进程没了,共享内存里的内容也随之失效,必须先松开映射。
            Release();
            nextTry = DateTime.UtcNow.AddSeconds(1);
            nextProcCheck = DateTime.MinValue;
            return hit;
        }

        /// <summary>清掉没有主进程看着的钩子加载器(它们会一直攥着共享内存不放)。</summary>
        private static void KillHooks()
        {
            string[] names = { "RTSSHooksLoader", "RTSSHooksLoader64" };
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(names[i]))
                    {
                        try { p.Kill(); p.WaitForExit(2000); }
                        catch (Exception ex) { Log.Write("rtss hooks " + names[i] + ": " + ex.Message); }
                        p.Dispose();
                    }
                }
                catch (Exception ex) { Log.Write("rtss hooks " + names[i] + ": " + ex.Message); }
            }
        }

        /// <summary>
        /// 调用 RTSS 自带的卸载器静默卸载。它注入的钩子要重启后才彻底清掉,
        /// 重启的提示由界面负责。
        /// </summary>
        public static bool Uninstall()
        {
            try
            {
                string exe = ExePath;
                string dir = exe.Length > 0 ? Path.GetDirectoryName(exe) : "";
                string un = dir.Length > 0 ? Path.Combine(dir, "uninstall.exe") : "";
                if (un.Length == 0 || !File.Exists(un))
                {
                    Log.Write("rtss uninstall: uninstall.exe not found");
                    return false;
                }

                // 卸载器自己会关 RTSS,但先停掉更稳,免得文件被占用卸不干净。
                Stop();
                Thread.Sleep(1000);

                var psi = new ProcessStartInfo(un, "/S");
                psi.WorkingDirectory = dir;
                psi.UseShellExecute = true;
                using (Process p = Process.Start(psi))
                    if (p != null) p.WaitForExit(180000);

                // NSIS 卸载器会把自己复制到临时目录再干活,原进程可能先退出,
                // 所以这里再等一会,直到安装目录/注册表真的消失。
                for (int i = 0; i < 60; i++)
                {
                    RefreshDetection();
                    if (!Installed) return true;
                    Thread.Sleep(1000);
                }
                Log.Write("rtss uninstall: 卸载器跑完了但还检测到 RTSS");
                return false;
            }
            catch (Exception ex)
            {
                Log.Write("rtss uninstall: " + ex.Message);
                return false;
            }
        }

        /// <summary>卸载放到后台跑,回调在后台线程上。</summary>
        public static void UninstallAsync(Action<bool> done)
        {
            var t = new Thread(delegate()
            {
                bool ok = false;
                try { ok = Uninstall(); }
                catch (Exception ex) { Log.Write("rtss uninstall: " + ex.Message); }
                if (done != null) done(ok);
            });
            t.IsBackground = true;
            t.Name = "rtss-uninstall";
            t.Start();
        }

        /// <summary>装不了或装完仍检测不到时,退回官方下载页让用户手动装。</summary>
        public static bool OpenDownloadPage()
        {
            try
            {
                Process.Start(DownloadPage);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("rtss page: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 后台下载并静默安装 RTSS;装完由界面负责把它启动起来。
        /// 回调在后台线程上,界面自己切回 UI 线程。
        /// </summary>
        public static void InstallAsync(InstallProgress cb)
        {
            var t = new Thread(delegate() { Install(cb); });
            t.IsBackground = true;
            t.Name = "rtss-install";
            t.Start();
        }

        private static void Install(InstallProgress cb)
        {
            string dir = Path.Combine(Path.GetTempPath(), "FrameForge");
            try
            {
                PrepareHttp();
                Directory.CreateDirectory(dir);
                string zip = Path.Combine(dir, "RTSSSetup737.zip");
                string unpack = Path.Combine(dir, "rtss-setup");
                string exe = Path.Combine(unpack, InstallerExe);

                if (!File.Exists(exe))
                {
                    if (File.Exists(zip)) File.Delete(zip);
                    Download(zip, cb);
                    if (Sha256(zip) != InstallerSha) throw new InvalidDataException("sha256 mismatch");

                    if (cb != null) cb(100, "unzip");
                    if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
                    ZipFile.ExtractToDirectory(zip, unpack);
                    File.Delete(zip);
                    if (!File.Exists(exe)) throw new FileNotFoundException(InstallerExe);
                }

                // NSIS 的 /S 是静默安装。本程序本身就带管理员权限,不会再多弹一次 UAC。
                if (cb != null) cb(100, "run");
                var psi = new ProcessStartInfo(exe, "/S");
                psi.WorkingDirectory = unpack;
                psi.UseShellExecute = true;
                using (Process p = Process.Start(psi))
                    if (p != null) p.WaitForExit(180000);

                RefreshDetection();
                if (cb != null) cb(100, "start");
                StartAndWait();
                RefreshDetection();
                if (cb != null) cb(100, "done");
            }
            catch (Exception ex)
            {
                Log.Write("rtss install: " + ex.Message
                    + (ex.InnerException != null ? " / " + ex.InnerException.Message : ""));
                if (cb != null) cb(0, "fail");
            }
        }

        /// <summary>安装过程中那行进度文案,叠加层页和设置页共用一套。</summary>
        public static string StageText(int percent, string stage)
        {
            if (stage == "download") return I18n.T("ov.rtss.downloading", percent);
            if (stage == "unzip") return I18n.T("ov.rtss.unzip");
            if (stage == "start") return I18n.T("ov.rtss.starting");
            if (stage == "stop") return I18n.T("set.rtss.stopping");
            if (stage == "uninstall") return I18n.T("set.rtss.uninstalling");
            return I18n.T("ov.rtss.installing");
        }

        /// <summary>
        /// 装完就让它跑起来,不用用户再点一次。
        /// 安装包有时自己会拉起 RTSS,所以先等一会,没起来再手动启动;
        /// 启动后还要等共享内存出现,这时状态行才会是“正在运行”。
        /// </summary>
        private static void StartAndWait()
        {
            for (int i = 0; i < 6; i++)
            {
                if (Running) return;
                Thread.Sleep(500);
            }
            Launch();
            for (int i = 0; i < 40; i++)
            {
                if (Running) return;
                Thread.Sleep(500);
            }
            Log.Write("rtss install: 装好了但没等到 RTSS 启动");
        }

        /// <summary>
        /// 本机 .NET 的默认协议不含 TLS 1.2,请求 nluug 会直接报
        /// “未能创建 SSL/TLS 安全通道”;显式打开 TLS 1.2/1.1 再发请求。
        /// </summary>
        private static void PrepareHttp()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)768;
                ServicePointManager.Expect100Continue = false;
            }
            catch { }
        }

        // ---- 下载 ----
        // 官方镜像在荷兰,nluug 对单个连接限速得厉害:实测单线程只有 160 KB/s,
        // 18 MB 要两分多钟。同一个文件切成 6 段并发拉,实测能到 3 MB/s 左右,
        // 几秒就下完。服务器不支持按段下载时退回单线程,慢一点但不会失败。
        private const int Parts = 6;

        private static void Download(string dest, InstallProgress cb)
        {
            long size = ProbeSize();
            if (size <= 0)
            {
                DownloadPlain(dest, cb);
                return;
            }

            long chunk = size / Parts;
            string[] partFile = new string[Parts];
            long[] from = new long[Parts], to = new long[Parts];
            for (int i = 0; i < Parts; i++)
            {
                from[i] = i * chunk;
                to[i] = (i == Parts - 1) ? size - 1 : (i + 1) * chunk - 1;
                partFile[i] = dest + ".p" + i;
            }

            long done = 0;
            int failed = 0;
            object gate = new object();
            int lastPct = -1;
            var threads = new Thread[Parts];
            for (int i = 0; i < Parts; i++)
            {
                int idx = i;
                threads[i] = new Thread(delegate()
                {
                    try
                    {
                        DownloadPart(partFile[idx], from[idx], to[idx], delegate(long n)
                        {
                            long total = Interlocked.Add(ref done, n);
                            int pct = (int)Math.Min(99, total * 100 / size);
                            bool report = false;
                            // 进度只在百分比变化时回报,不然界面会被几十次/秒的回调刷爆。
                            lock (gate)
                            {
                                if (pct != lastPct) { lastPct = pct; report = true; }
                            }
                            if (report && cb != null) cb(pct, "download");
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Write("rtss part " + idx + ": " + ex.Message);
                        Interlocked.Increment(ref failed);
                    }
                });
                threads[i].IsBackground = true;
                threads[i].Start();
            }
            for (int i = 0; i < Parts; i++) threads[i].Join();

            // 有一段没拉下来就整包重下,绝不用拼不完整的文件去装。
            if (failed > 0)
            {
                for (int i = 0; i < Parts; i++) TryDelete(partFile[i]);
                DownloadPlain(dest, cb);
                return;
            }

            using (FileStream outFs = File.Create(dest))
            {
                for (int i = 0; i < Parts; i++)
                {
                    using (FileStream inFs = File.OpenRead(partFile[i])) inFs.CopyTo(outFs);
                    File.Delete(partFile[i]);
                }
            }
            if (cb != null) cb(100, "download");
        }

        private static void DownloadPart(string path, long from, long to, Action<long> add)
        {
            Exception last = null;
            // 网络抖动只重试这一段;以前一段失败就整包重下,等于白等好几分钟。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                long got = 0;
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(InstallerUrl);
                    req.UserAgent = "FrameForge";
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 60000;
                    req.AddRange(from, to);
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (FileStream fs = File.Create(path))
                    {
                        byte[] buf = new byte[65536];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, n);
                            add(n);
                            got += n;
                        }
                    }
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    // 把这次尝试已经计入的字节退回去,重试时进度才不会虚高。
                    if (got > 0) add(-got);
                    TryDelete(path);
                    Thread.Sleep(500 * (attempt + 1));
                }
            }
            throw new InvalidOperationException("part failed", last);
        }

        /// <summary>文件大小;不支持分段(或拿不到大小)时返回 -1。</summary>
        private static long ProbeSize()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(InstallerUrl);
                req.Method = "HEAD";
                req.UserAgent = "FrameForge";
                req.Timeout = 15000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return -1;
                    string ranges = resp.Headers["Accept-Ranges"];
                    if (ranges != null && ranges.Equals("none", StringComparison.OrdinalIgnoreCase)) return -1;
                    return resp.ContentLength;
                }
            }
            catch (Exception ex)
            {
                Log.Write("rtss probe: " + ex.Message);
                return -1;
            }
        }

        private static void DownloadPlain(string dest, InstallProgress cb)
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "FrameForge");
                if (cb != null)
                {
                    wc.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
                    {
                        cb(e.ProgressPercentage, "download");
                    };
                }
                wc.DownloadFile(new Uri(InstallerUrl), dest);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static string Sha256(string path)
        {
            if (!File.Exists(path)) return "";
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] h = sha.ComputeHash(fs);
                var sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("X2"));
                return sb.ToString();
            }
        }

        /// <summary>把一段文字写进 RTSS 的 OSD;传空串清掉自己的内容。</summary>
        public static void Push(string text)
        {
            if (text == null) text = "";
            lock (osdLock)
            {
                if (text == lastText) return;
                try
                {
                    if (Write(text)) lastText = text;
                }
                catch (Exception ex) { Log.Write("rtss: " + ex.Message); }
            }
        }

        /// <summary>退出/关闭叠加层时把自己的 OSD 内容清空,别留在屏幕上。</summary>
        public static void Clear()
        {
            lastText = "";
            try { Write(""); }
            catch { }
        }

        private static bool Write(string text)
        {
            lock (osdLock) return WriteLocked(text);
        }

        private static bool WriteLocked(string text)
        {
            if (!EnsureLocked()) return false;

            int textOffset = version >= 0x00020007 ? OffTextEx : 0;
            int room = Math.Min(TextCap, entrySize - textOffset);
            if (room <= 1) return false;

            byte[] bytes = Encoding.Default.GetBytes(text);
            if (bytes.Length > room - 1)
            {
                // 只截断到字符边界,免得半个汉字把 RTSS 的字体渲染弄乱。
                int n = room - 1;
                while (n > 0 && (bytes[n] & 0xC0) == 0x80) n--;
                Array.Resize(ref bytes, n);
            }

            int idx = FindSlot();
            if (idx < 0) return false;
            IntPtr entry = (IntPtr)(view.ToInt64() + arrOffset + (long)idx * entrySize);

            ZeroMemory((IntPtr)(entry.ToInt64() + textOffset), room);
            if (bytes.Length > 0)
                Marshal.Copy(bytes, 0, (IntPtr)(entry.ToInt64() + textOffset), bytes.Length);
            // 通知 RTSS 重画 OSD。
            Marshal.WriteInt32((IntPtr)(view.ToInt64() + OffOsdFrame), Marshal.ReadInt32((IntPtr)(view.ToInt64() + OffOsdFrame)) + 1);
            return true;
        }

        /// <summary>找自己的槽;没有就占一个空槽(0 号留给 RTSS 自己)。</summary>
        private static int FindSlot()
        {
            if (slot >= 1 && slot < arrSize && OwnerAt(slot)) return slot;
            slot = -1;
            for (int i = 1; i < arrSize; i++)
            {
                IntPtr entry = (IntPtr)(view.ToInt64() + arrOffset + (long)i * entrySize);
                if (Marshal.ReadByte((IntPtr)(entry.ToInt64() + OffOwner)) == 0)
                {
                    WriteAnsi((IntPtr)(entry.ToInt64() + OffOwner), 255, Owner);
                    slot = i;
                    return i;
                }
            }
            return -1;
        }

        private static bool OwnerAt(int idx)
        {
            IntPtr p = (IntPtr)(view.ToInt64() + arrOffset + (long)idx * entrySize + OffOwner);
            return ReadAnsi(p, 255) == Owner;
        }

        /// <summary>打开共享内存并读取头;RTSS 没运行/已退出时返回 false,并限速重试。</summary>
        private static bool Ensure()
        {
            lock (osdLock) return EnsureLocked();
        }

        private static bool EnsureLocked()
        {
            if (view != IntPtr.Zero)
            {
                // RTSS 退出时会把签名改成 0xDEAD,这时视图虽然还映射着也不能再写。
                if (Marshal.ReadInt32(view) == Signature) return true;
                ReleaseLocked();
                nextTry = DateTime.UtcNow.AddSeconds(2);
                return false;
            }
            if (DateTime.UtcNow < nextTry) return false;

            map = OpenFileMapping(FileMapAllAccess, false, MapName);
            if (map == IntPtr.Zero)
                map = OpenFileMapping(0x0002, false, MapName);   // 降级成只拿写权限
            if (map == IntPtr.Zero)
            {
                nextTry = DateTime.UtcNow.AddSeconds(3);
                return false;
            }
            view = MapViewOfFile(map, FileMapAllAccess, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero)
            {
                CloseHandle(map);
                map = IntPtr.Zero;
                nextTry = DateTime.UtcNow.AddSeconds(3);
                return false;
            }
            if (Marshal.ReadInt32(view) != Signature)
            {
                Release();
                nextTry = DateTime.UtcNow.AddSeconds(3);
                return false;
            }

            version = Marshal.ReadInt32((IntPtr)(view.ToInt64() + OffVersion));
            entrySize = Marshal.ReadInt32((IntPtr)(view.ToInt64() + OffOsdEntrySize));
            arrOffset = Marshal.ReadInt32((IntPtr)(view.ToInt64() + OffOsdArrOffset));
            arrSize = Marshal.ReadInt32((IntPtr)(view.ToInt64() + OffOsdArrSize));
            slot = -1;
            if (version < 0x00020000 || entrySize < 512 || arrSize < 2 || arrOffset < 36)
            {
                Release();
                nextTry = DateTime.UtcNow.AddSeconds(3);
                return false;
            }
            return true;
        }

        private static void Release()
        {
            lock (osdLock) ReleaseLocked();
        }

        private static void ReleaseLocked()
        {
            if (view != IntPtr.Zero) { UnmapViewOfFile(view); view = IntPtr.Zero; }
            if (map != IntPtr.Zero) { CloseHandle(map); map = IntPtr.Zero; }
            slot = -1;
            // 共享内存换了新的(RTSS 重启/刚装好),旧文字就不在里面了,
            // 不去掉这个去重缓存,Push 同样的内容会一直以为已经写过。
            lastText = "";
        }

        private static void WriteAnsi(IntPtr dst, int cap, string s)
        {
            byte[] b = Encoding.Default.GetBytes(s);
            int n = Math.Min(b.Length, cap);
            ZeroMemory(dst, cap + 1);
            Marshal.Copy(b, 0, dst, n);
        }

        private static string ReadAnsi(IntPtr src, int cap)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < cap; i++)
            {
                byte b = Marshal.ReadByte((IntPtr)(src.ToInt64() + i));
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        [DllImport("ntdll.dll", EntryPoint = "RtlZeroMemory")]
        private static extern void ZeroMemory(IntPtr dst, int len);

        /// <summary>RTSS 装在哪个目录:先看注册表,再按默认位置找。</summary>
        private static string FindInstall()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Unwinder\RTSS"))
                {
                    if (k != null)
                    {
                        string dir = k.GetValue("InstallDir") as string;
                        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "RTSS.exe")))
                            return dir.TrimEnd('\\');
                    }
                }
            }
            catch { }

            try
            {
                string[] dirs = new string[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RivaTuner Statistics Server")
                };
                for (int i = 0; i < dirs.Length; i++)
                    if (File.Exists(Path.Combine(dirs[i], "RTSS.exe"))) return dirs[i];
            }
            catch { }
            return "";
        }
    }
}