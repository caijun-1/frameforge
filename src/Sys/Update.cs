using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FrameForge.Sys
{
    /// <summary>
    /// 版本检查。只有用户在设置里打开"检查更新"才会联网,
    /// 请求的是 GitHub 公开的 releases/latest 接口,不带任何本机信息。
    /// </summary>
    public static class UpdateCheck
    {
        private const string Repo = "caijun-1/frameforge";
        private const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";
        private const string FallbackUrl = "https://github.com/" + Repo + "/releases/latest";

        public static string LatestTag = "";
        public static string LatestUrl = "";
        public static string ExeUrl = "";
        public static string ShaUrl = "";
        public static bool Busy;

        public static string CurrentVersion
        {
            get
            {
                Version v = typeof(UpdateCheck).Assembly.GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        /// <summary>后台请求最新版本,回调参数为"是否有更新"。回调在后台线程执行。</summary>
        public static void Start(Action<bool> done)
        {
            if (Busy) return;
            Busy = true;
            Thread t = new Thread(delegate()
            {
                bool newer = false;
                try
                {
                    TryEnableTls12();
                    string json = Get(Api);
                    string tag = Field(json, "tag_name");
                    string url = Field(json, "html_url");
                    if (tag.Length > 0)
                    {
                        LatestTag = Fix(tag).TrimStart('v', 'V');
                        LatestUrl = url.Length > 0 ? Fix(url) : FallbackUrl;
                        ExeUrl = Asset(json, "FrameForge.exe");
                        ShaUrl = Asset(json, "FrameForge.exe.sha256");
                        newer = Compare(LatestTag, CurrentVersion) > 0;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("update check failed: " + ex.Message);
                }
                Busy = false;
                if (done != null)
                {
                    try { done(newer); }
                    catch (Exception ex) { Log.Write("update callback failed: " + ex.Message); }
                }
            });
            t.IsBackground = true;
            t.Name = "update-check";
            t.Start();
        }

        public static void OpenDownloadPage()
        {
            string url = string.IsNullOrEmpty(LatestUrl) ? FallbackUrl : LatestUrl;
            try { Process.Start(url); }
            catch (Exception ex) { Log.Write("open update page failed: " + ex.Message); }
        }

        /// <summary>
        /// 下载新版 exe,用发布里的 .sha256 校验,再交给一个等待本进程退出的脚本替换并重启。
        /// 返回空字符串表示脚本已启动;否则返回给用户看的原因。必须在后台线程调用。
        /// </summary>
        public static string DownloadAndInstall(Action<int> progress)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "FrameForge.update.exe");
            try
            {
                if (ExeUrl.Length == 0) return I18n.T("upd.err.asset");
                if (ShaUrl.Length == 0) return I18n.T("upd.err.sha");
                if (!Trusted(ExeUrl) || !Trusted(ShaUrl)) return I18n.T("upd.err.url");

                TryEnableTls12();
                string want = FirstHash(Get(ShaUrl));
                if (want.Length != 64) return I18n.T("upd.err.sha");

                if (progress != null) progress(0);
                DownloadFile(ExeUrl, tmp, progress);

                if (!string.Equals(HashFile(tmp), want, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    return I18n.T("upd.err.hash");
                }

                string err;
                return LaunchInstaller(tmp, out err) ? "" : err;
            }
            catch (Exception ex)
            {
                Log.Write("update install failed: " + ex.Message);
                return ex.Message;
            }
        }

        /// <summary>只信任 GitHub 的 https 地址,避免被改过的更新地址骗去下别的程序。</summary>
        private static bool Trusted(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps) return false;
            return string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        private static void DownloadFile(string url, string path, Action<int> progress)
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "FrameForge/" + CurrentVersion);
                if (progress != null)
                    wc.DownloadProgressChanged += delegate(object sender, DownloadProgressChangedEventArgs e)
                    {
                        try { progress(e.ProgressPercentage); }
                        catch (Exception) { }
                    };
                wc.DownloadFile(new Uri(url), path);
            }
        }

        private static string HashFile(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
                return Hex(sha.ComputeHash(fs));
        }

        private static string Hex(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++)
                sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>校验文件里既有纯 hash,也有「hash  文件名」两种写法,取前 64 位十六进制。</summary>
        private static string FirstHash(string text)
        {
            Match m = Regex.Match(text ?? "", "[0-9a-fA-F]{64}");
            return m.Success ? m.Value.ToLowerInvariant() : "";
        }

        /// <summary>写一个临时脚本:等本进程退出 -> 覆盖 exe -> 启动新版,失败时保留旧版。</summary>
        private static bool LaunchInstaller(string file, out string error)
        {
            error = "";
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                int pid = Process.GetCurrentProcess().Id;
                string script = Path.Combine(Path.GetTempPath(), "frameforge_update.cmd");

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("setlocal enableextensions");
                sb.AppendLine("for /l %%i in (1,1,150) do (");
                sb.AppendLine("  tasklist /fi \"PID eq " + pid + "\" 2>nul | find \"" + pid + "\" >nul");
                sb.AppendLine("  if errorlevel 1 goto copy");
                sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
                sb.AppendLine(")");
                sb.AppendLine(":copy");
                sb.AppendLine("for /l %%i in (1,1,30) do (");
                sb.AppendLine("  copy /y \"" + file + "\" \"" + exe + "\" >nul 2>&1");
                sb.AppendLine("  if not errorlevel 1 goto run");
                sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
                sb.AppendLine(")");
                sb.AppendLine(":run");
                sb.AppendLine("start \"\" \"" + exe + "\"");
                sb.AppendLine("del \"%~f0\"");
                File.WriteAllText(script, sb.ToString(), Encoding.Default);

                var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + script + "\"\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>在 releases JSON 里找某个资产名的下载地址。</summary>
        private static string Asset(string json, string name)
        {
            Match m = Regex.Match(json,
                "\"name\"\\s*:\\s*\"" + Regex.Escape(name) +
                "\"[\\s\\S]{0,2000}?\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? Fix(m.Groups[1].Value) : "";
        }

        /// <summary>比较 a.b.c 形式的主版本号,a 更大返回正数。</summary>
        public static int Compare(string a, string b)
        {
            string[] x = (a ?? "").Split('.');
            string[] y = (b ?? "").Split('.');
            int n = Math.Max(x.Length, y.Length);
            for (int i = 0; i < n; i++)
            {
                int xi = i < x.Length ? Num(x[i]) : 0;
                int yi = i < y.Length ? Num(y[i]) : 0;
                if (xi != yi) return xi > yi ? 1 : -1;
            }
            return 0;
        }

        private static int Num(string s)
        {
            int v;
            return int.TryParse(s.Trim(), out v) ? v : 0;
        }

        private static string Get(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "FrameForge/" + CurrentVersion;
            req.Accept = "application/vnd.github+json";
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static void TryEnableTls12()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            }
            catch (Exception) { }
        }

        private static string Field(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>还原 JSON 里最常见的转义。</summary>
        private static string Fix(string s)
        {
            return s.Replace("\\/", "/").Replace("\\\"", "\"").Replace("\\n", " ");
        }
    }
}