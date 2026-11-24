using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace FrameForge
{
    /// <summary>人类可读的数字格式化,界面与自检共用。</summary>
    public static class Fmt
    {
        public static string Bytes(ulong bytes)
        {
            double gb = bytes / 1073741824.0;
            if (gb >= 1.0) return gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            double mb = bytes / 1048576.0;
            if (mb >= 1.0) return mb.ToString("0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        }

        /// <summary>可用/总计,统一到同一单位,避免 "0.9 GB / 15.8 GB" 的混排。</summary>
        public static string Pair(ulong used, ulong total)
        {
            if (total >= 1073741824UL)
                return (used / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " / " +
                       (total / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return (used / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " / " +
                   (total / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        public static string Speed(double bytesPerSecond)
        {
            if (bytesPerSecond < 0) bytesPerSecond = 0;
            if (bytesPerSecond >= 1048576.0)
                return (bytesPerSecond / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            if (bytesPerSecond >= 1024.0)
                return (bytesPerSecond / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB/s";
            return bytesPerSecond.ToString("0", CultureInfo.InvariantCulture) + " B/s";
        }

        public static string Num(double v, string format)
        {
            return v.ToString(format, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 配置/数据文件的写入:先落临时文件再整体替换。
    /// 直接 File.WriteAllText 写到一半被强杀,用户会得到一个截断的配置,
    /// 保护名单、滤镜参数会一起回到默认;替换能保证旧文件始终完整。
    /// </summary>
    public static class SafeFile
    {
        public static void WriteAllText(string path, string text, Encoding enc)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, enc);
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception)
            {
                // FAT/exFAT 上 File.Replace 不被支持,退回覆盖写;至少临时文件是完整的。
                File.Copy(tmp, path, true);
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    /// <summary>
    /// 最小日志:%LOCALAPPDATA%\FrameForge\log.txt,超过 1 MB 轮转一份。
    /// FF-042：分级(INFO/WARN/ERROR,可用 FRAMEFORGE_LOG_LEVEL 过滤)、
    /// 单行化 + 路径/账号脱敏、跨进程命名互斥(看门狗进程会同时写)。
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 1 << 20;
        private const int MaxLineChars = 2000;

        private const int LevelInfo = 1;
        private const int LevelWarn = 2;
        private const int LevelError = 3;
        private static readonly int MinLevel = ParseLevel();
        private static readonly string HomeDir = UserHome();
        private static readonly string UserName = SafeUserName();
        private static Mutex fileMutex;

        public static string FilePath { get { return Path.Combine(Settings.Dir, "log.txt"); } }

        private static int ParseLevel()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("FRAMEFORGE_LOG_LEVEL");
                if (string.IsNullOrEmpty(v)) return LevelInfo;
                switch (v.Trim().ToUpperInvariant())
                {
                    case "ERROR": return LevelError;
                    case "WARN": return LevelWarn;
                    case "DEBUG": return LevelInfo;
                    default: return LevelInfo;
                }
            }
            catch { return LevelInfo; }
        }

        private static string UserHome()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            catch { return ""; }
        }

        private static string SafeUserName()
        {
            try { return Environment.UserName; }
            catch { return ""; }
        }

        /// <summary>普通信息,兼容原有调用点。</summary>
        public static void Write(string msg) { WriteLine(LevelInfo, "INFO ", msg); }

        public static void Warn(string msg) { WriteLine(LevelWarn, "WARN ", msg); }

        public static void Error(string msg) { WriteLine(LevelError, "ERROR", msg); }

        private static void WriteLine(int level, string tag, string msg)
        {
            if (level < MinLevel) return;
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + "  " + tag + "  " + Sanitize(msg) + Environment.NewLine;
                lock (Gate)
                {
                    if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                    Rotate();
                    if (fileMutex == null)
                    {
                        try { fileMutex = new Mutex(false, "Local\\FrameForge.Log"); }
                        catch { fileMutex = null; }
                    }
                    bool held = false;
                    try
                    {
                        if (fileMutex != null)
                        {
                            try { held = fileMutex.WaitOne(2000); }
                            catch (AbandonedMutexException) { held = true; }
                        }
                        File.AppendAllText(FilePath, line, new UTF8Encoding(false));
                    }
                    finally { if (held) { try { fileMutex.ReleaseMutex(); } catch { } } }
                }
            }
            catch { }
        }

        private static void Rotate()
        {
            var f = new FileInfo(FilePath);
            if (!f.Exists || f.Length <= MaxBytes) return;
            try
            {
                string old = Path.Combine(Settings.Dir, "log.old.txt");
                if (File.Exists(old)) File.Delete(old);
                File.Move(FilePath, old);
            }
            catch { }
        }

        /// <summary>
        /// 换行会让一条日志伪装成多条,路径与用户名属于隐私。两者都在落盘前处理，
        /// 而不是指望每个调用点自觉。
        /// </summary>
        private static string Sanitize(string msg)
        {
            if (msg == null) return "";
            string s = msg.Replace("\r\n", " | ").Replace('\r', ' ').Replace('\n', ' ');
            if (HomeDir.Length > 3)
                s = s.Replace(HomeDir, "%USERPROFILE%");
            if (UserName.Length >= 3)
                s = s.Replace(UserName, "%USER%");
            if (s.Length > MaxLineChars) s = s.Substring(0, MaxLineChars) + "...";
            return s;
        }
    }
}