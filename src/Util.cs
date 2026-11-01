using System;
using System.Globalization;
using System.IO;
using System.Text;

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

    /// <summary>最小日志:%LOCALAPPDATA%\FrameForge\log.txt,超过 1 MB 轮转一份。</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 1 << 20;

        public static string FilePath { get { return Path.Combine(Settings.Dir, "log.txt"); } }

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                    var f = new FileInfo(FilePath);
                    if (f.Exists && f.Length > MaxBytes)
                    {
                        try
                        {
                            string old = Path.Combine(Settings.Dir, "log.old.txt");
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(FilePath, old);
                        }
                        catch { }
                    }
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg + Environment.NewLine;
                    File.AppendAllText(FilePath, line, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}