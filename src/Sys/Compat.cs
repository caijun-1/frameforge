using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    /// <summary>
    /// 游戏兼容与硬件体检:按游戏关闭全屏优化、窗口化优化与 MPO 开关、
    /// 上采样组件扫描、刷新率与内存通道读取。只读写单个注册表值,不动游戏文件。
    /// </summary>
    public static class Compat
    {
        public const string LayersPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        public const string GpuPrefsPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        public const string GpuPrefsName = "DirectXUserGlobalSettings";
        public const string SwapKey = "SwapEffectUpgradeEnable";
        public const string DwmPath = @"SOFTWARE\Microsoft\Windows\Dwm";
        public const string MpoName = "OverlayTestMode";
        public const string FseToken = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

        private const int EnumCurrentSettings = -1;

        // =================================================================
        //  游戏进程
        // =================================================================

        /// <summary>由进程号取完整 exe 路径;权限不足时返回空串,调用方按「未检测到」处理。</summary>
        public static string GameExePath(int pid)
        {
            if (pid <= 0) return "";
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(600);
                int size = sb.Capacity;
                if (!Native.QueryFullProcessImageName(h, 0, sb, ref size)) return "";
                return sb.ToString(0, size);
            }
            catch { return ""; }
            finally { Native.CloseHandle(h); }
        }

        // =================================================================
        //  全屏优化(按 exe 的兼容性标记)
        // =================================================================

        public static bool FullscreenOptOff(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            return HasToken(ReadSz(RegistryHive.CurrentUser, LayersPath, exe), FseToken);
        }

        public static bool HasToken(string cur, string token)
        {
            if (string.IsNullOrEmpty(cur)) return false;
            string[] parts = cur.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                if (string.Equals(parts[i], token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>在兼容性标记串里加上或去掉一个词,保留用户原有的其他标记。</summary>
        public static string ToggleToken(string cur, string token, bool add)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(cur))
                parts.AddRange(cur.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

            bool has = HasToken(cur, token);
            if (add == has) return cur ?? "";

            if (add)
            {
                bool tilde = false;
                for (int i = 0; i < parts.Count; i++)
                    if (parts[i] == "~") tilde = true;
                if (!tilde) parts.Insert(0, "~");
                parts.Add(token);
                return string.Join(" ", parts.ToArray());
            }

            parts.RemoveAll(delegate(string s)
            {
                return string.Equals(s, token, StringComparison.OrdinalIgnoreCase);
            });
            if (parts.Count == 0) return "";
            if (parts.Count == 1 && parts[0] == "~") return "";
            return string.Join(" ", parts.ToArray());
        }

        // =================================================================
        //  窗口化游戏优化 / MPO
        // =================================================================

        /// <summary>DirectXUserGlobalSettings 里没有该项时按系统默认(开)处理。</summary>
        public static bool WindowedOptOn()
        {
            string cur = ReadSz(RegistryHive.CurrentUser, GpuPrefsPath, GpuPrefsName);
            return !HasPair(cur, SwapKey) || PairValue(cur, SwapKey) != "0";
        }

        public static bool MpoEnabled()
        {
            return ReadDword(RegistryHive.LocalMachine, DwmPath, MpoName, 0) != 5;
        }

        public static bool HasPair(string settings, string key)
        {
            return PairValue(settings, key) != null;
        }

        public static string PairValue(string settings, string key)
        {
            if (string.IsNullOrEmpty(settings)) return null;
            string[] parts = settings.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(parts[i].Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return parts[i].Substring(eq + 1).Trim();
            }
            return null;
        }

        /// <summary>只改一个键值,保留 DirectXUserGlobalSettings 里其他项的顺序。</summary>
        public static string SetPair(string settings, string key, string value)
        {
            var parts = new List<string>();
            bool done = false;
            if (!string.IsNullOrEmpty(settings))
            {
                string[] arr = settings.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < arr.Length; i++)
                {
                    string p = arr[i].Trim();
                    if (p.Length == 0) continue;
                    int eq = p.IndexOf('=');
                    if (eq > 0 && string.Equals(p.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Add(key + "=" + value);
                        done = true;
                    }
                    else
                    {
                        parts.Add(p);
                    }
                }
            }
            if (!done) parts.Add(key + "=" + value);
            return string.Join(";", parts.ToArray()) + ";";
        }

        // =================================================================
        //  上采样组件扫描
        // =================================================================

        /// <summary>扫描游戏目录里的上采样 DLL,返回 DLSS / DLSS-FG / FSR / XeSS。</summary>
        public static string[] DetectUpscaling(string exe)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(exe)) return found.ToArray();

            string dir = "";
            try { dir = Path.GetDirectoryName(exe); }
            catch { }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return found.ToArray();

            var files = new List<string>();
            int budget = 4000;
            ScanDlls(dir, 0, files, ref budget);

            for (int i = 0; i < files.Count; i++)
            {
                string n = files[i];
                if (n == "nvngx_dlssg.dll") AddName(found, "DLSS-FG");
                else if (n == "nvngx_dlss.dll") AddName(found, "DLSS");
                else if (n == "libxess.dll") AddName(found, "XeSS");
                else if (n.StartsWith("amd_fidelityfx")) AddName(found, "FSR");
            }
            return found.ToArray();
        }

        private static void AddName(List<string> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == name) return;
            list.Add(name);
        }

        private static void ScanDlls(string dir, int depth, List<string> files, ref int budget)
        {
            if (budget <= 0) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(f).ToLowerInvariant();
                    if (name.EndsWith(".dll") &&
                        (name.Contains("dlss") || name.Contains("fidelityfx") || name.Contains("xess")))
                        files.Add(name);
                    budget--;
                    if (budget <= 0) return;
                }
            }
            catch { }

            if (depth >= 3) return;
            try
            {
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(d).ToLowerInvariant();
                    if (name == "cache" || name == "logs" || name.StartsWith(".")) continue;
                    ScanDlls(d, depth + 1, files, ref budget);
                    if (budget <= 0) return;
                }
            }
            catch { }
        }

        // =================================================================
        //  显示器与硬件
        // =================================================================

        /// <summary>主显示器当前刷新率;读取失败返回 0。</summary>
        public static int RefreshHz()
        {
            try
            {
                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(null, EnumCurrentSettings, ref dm))
                    return dm.dmDisplayFrequency;
            }
            catch { }
            return 0;
        }

        /// <summary>物理内存条数量、总容量、最高频率与是否双通道(按容量对称判断)。</summary>
        public static bool MemoryInfo(out int modules, out int totalGb, out int speed, out bool dual)
        {
            modules = 0;
            totalGb = 0;
            speed = 0;
            dual = false;
            int first = -1;
            bool same = true;

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Capacity, Speed FROM Win32_PhysicalMemory"))
                {
                    using (ManagementObjectCollection rows = searcher.Get())
                    {
                        foreach (ManagementObject mo in rows)
                        {
                            try
                            {
                                int capGb = (int)Math.Round(
                                    Convert.ToDouble(mo["Capacity"], CultureInfo.InvariantCulture) / 1073741824.0);
                                int sp = 0;
                                try { sp = Convert.ToInt32(mo["Speed"], CultureInfo.InvariantCulture); }
                                catch { }
                                if (capGb <= 0) continue;
                                modules++;
                                totalGb += capGb;
                                if (sp > speed) speed = sp;
                                if (first < 0) first = capGb;
                                else if (capGb != first) same = false;
                            }
                            catch { }
                            finally { mo.Dispose(); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("compat wmi: " + ex.Message);
                return false;
            }

            dual = modules >= 2 && same;
            return modules > 0;
        }

        /// <summary>系统盘可用空间与总容量(GB)。</summary>
        public static bool SystemDriveFree(out int freeGb, out int totalGb)
        {
            freeGb = 0;
            totalGb = 0;
            try
            {
                string root = Path.GetPathRoot(Environment.SystemDirectory);
                if (string.IsNullOrEmpty(root)) return false;
                var d = new DriveInfo(root);
                if (!d.IsReady) return false;
                freeGb = (int)(d.AvailableFreeSpace / 1073741824L);
                totalGb = (int)(d.TotalSize / 1073741824L);
                return true;
            }
            catch { return false; }
        }

        // =================================================================
        //  注册表读取
        // =================================================================

        private static string ReadSz(RegistryHive hive, string path, string name)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                {
                    using (RegistryKey k = baseKey.OpenSubKey(path))
                    {
                        if (k == null) return null;
                        object v = k.GetValue(name, null);
                        return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch { return null; }
        }

        private static int ReadDword(RegistryHive hive, string path, string name, int fallback)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                {
                    using (RegistryKey k = baseKey.OpenSubKey(path))
                    {
                        if (k == null) return fallback;
                        object v = k.GetValue(name, null);
                        if (v == null) return fallback;
                        return Convert.ToInt32(v, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch { return fallback; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    }
}