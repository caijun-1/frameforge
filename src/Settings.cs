using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FrameForge.Sys;

namespace FrameForge
{
    /// <summary>用户设置:纯文本 ini,便于手改、备份与逐行排查。</summary>
    public class Settings
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameForge");

        public static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public string Language = "zh";
        public bool Dark = true;

        // 叠加层
        public bool OverlayOn = false;
        public string OverlayStyle = "h";     // h | v | mini
        // auto = 按样式默认摆位(可拖动)| tl/tr/bl/br = 固定在屏幕四角
        public string OverlayCorner = "auto";
        public int OverlayX = -1;             // -1 = 自动放右上角
        public int OverlayY = -1;
        public double OverlayScale = 1.0;
        public bool OverlayLocked = true;
        public bool ShowFps = true;
        public bool ShowCpu = false;
        public bool ShowGpu = true;
        public bool ShowCpuTemp = true;       // CPU 温度(WMI,取不到显示 --)
        public bool ShowGpuTemp = true;       // GPU 温度(NVML)
        public bool ShowRam = false;
        public bool ShowRamPct = false;       // 内存占用百分比
        public bool ShowLow = false;          // 1% Low
        public bool ShowClock = false;        // 帧时间 / 当前时间由 OverlayStyle 决定
        public bool ShowSession = false;      // 已游戏时长

        // 加速
        public bool BoostPriority = true;
        public bool BoostPowerPlan = true;
        public bool BoostTimer = true;
        public bool BoostDvr = true;
        public bool BoostMem = true;
        public bool BoostBackground = false;
        public bool BoostAffinity = false;
        public bool BoostIrq = false;
        public bool BoostProfile = true;
        public bool AutoBoost = true;         // 检测到游戏就自动套用加速(安全项)
        public int AffinityCores = 0;         // 0 = 全部核心(不限制)
        public string BoostMode = "balanced"; // balanced | beast | custom
        // 保护名单:这些进程永远不会被「暂停后台进程」选中。逗号分隔,不含 .exe。
        public string BoostWhitelist = "";

        // 画面滤镜
        public int FilterMode = 0;            // 0 关 | 1 仅游戏 | 2 始终
        public int FilterPreset = 0;
        public int FilterIntensity = 100;
        public int FilterContrast = 0;
        public int FilterSaturation = 0;
        public int FilterWarmth = 0;
        public int FilterTint = 0;            // 色调:负值偏绿、正值偏洋红 -100..100
        public int FilterDark = 0;            // 暗部场景平衡 0..100
        public int FilterVibrance = 0;        // 数字振动 0..100
        public string FilterIcc = "";         // 当前生效的 ICC 配置文件路径,空 = 只用 sRGB

        /// <summary>导入过的 ICC 配置文件:路径固定,名字由用户自己起。</summary>
        public class IccEntry
        {
            public string Name;
            public string Path;
        }
        public readonly List<IccEntry> IccList = new List<IccEntry>();

        // 战绩报告
        public bool ReportAuto = true;        // 退出游戏后自动弹出本局报告
        public int ReportMinSec = 30;         // 短于该时长的对局不记录

        // 内存
        public bool MemAuto = false;
        public int MemThreshold = 88;

        // 常规
        public bool Autostart = false;
        public bool CheckUpdates = false;      // 启动时检查 GitHub 新版本(默认关,避免后台联网)
        public bool CloseToTray = true;
        public bool AskedElevation = false;

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    s.Apply(k, v);
                }
            }
            catch (Exception ex) { Log.Write("settings load: " + ex.Message); }
            s.EnsureIccEntry();
            return s;
        }

        /// <summary>旧配置里只存了路径:补一条默认名字的条目,列表里才看得到在用的 profile。</summary>
        public void EnsureIccEntry()
        {
            if (string.IsNullOrEmpty(FilterIcc)) return;
            for (int i = 0; i < IccList.Count; i++)
                if (string.Equals(IccList[i].Path, FilterIcc, StringComparison.OrdinalIgnoreCase)) return;
            IccList.Insert(0, new IccEntry { Name = CleanIccName(Path.GetFileNameWithoutExtension(FilterIcc)), Path = FilterIcc });
        }

        /// <summary>名字写进 ini 的一行,去掉分隔符与换行,并限长。</summary>
        public static string CleanIccName(string n)
        {
            if (n == null) return "";
            n = n.Replace('|', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (n.Length > 40) n = n.Substring(0, 40).Trim();
            return n;
        }

        private void AddIccProfile(string v)
        {
            int bar = v.IndexOf('|');
            if (bar <= 0) return;
            string name = CleanIccName(v.Substring(0, bar));
            string path = v.Substring(bar + 1).Trim();
            if (name.Length == 0 || path.Length == 0) return;
            IccList.Add(new IccEntry { Name = name, Path = path });
        }

        private void Apply(string k, string v)
        {
            switch (k.ToLowerInvariant())
            {
                case "language": Language = v == "en" ? "en" : "zh"; break;
                case "dark": Dark = Bool(v, true); break;
                case "overlayon": OverlayOn = Bool(v, false); break;
                case "overlaystyle": OverlayStyle = v; break;
                case "overlaycorner": OverlayCorner = CornerKey(v); break;
                case "overlayx": OverlayX = Int(v, -1); break;
                case "overlayy": OverlayY = Int(v, -1); break;
                case "overlayscale": OverlayScale = Dbl(v, 1.0); break;
                case "overlaylocked": OverlayLocked = Bool(v, true); break;
                case "showfps": ShowFps = Bool(v, true); break;
                case "showcpu": ShowCpu = Bool(v, false); break;
                case "showgpu": ShowGpu = Bool(v, true); break;
                case "showtemp":
                    // 旧版本只有一个"温度"项,按 GPU 温度处理;两个都置为同值,升级后不会突然少一项。
                    bool t = Bool(v, true);
                    ShowCpuTemp = t; ShowGpuTemp = t;
                    break;
                case "showcputemp": ShowCpuTemp = Bool(v, true); break;
                case "showgputemp": ShowGpuTemp = Bool(v, true); break;
                case "showram": ShowRam = Bool(v, false); break;
                case "showrampct": ShowRamPct = Bool(v, false); break;
                case "showlow": ShowLow = Bool(v, false); break;
                case "showclock": ShowClock = Bool(v, false); break;
                case "showsession": ShowSession = Bool(v, false); break;
                case "boostpriority": BoostPriority = Bool(v, true); break;
                case "boostpowerplan": BoostPowerPlan = Bool(v, true); break;
                case "boosttimer": BoostTimer = Bool(v, true); break;
                case "boostdvr": BoostDvr = Bool(v, true); break;
                case "boostmem": BoostMem = Bool(v, true); break;
                case "boostbackground": BoostBackground = Bool(v, false); break;
                case "boostaffinity": BoostAffinity = Bool(v, false); break;
                case "boostirq": BoostIrq = Bool(v, false); break;
                case "boostprofile": BoostProfile = Bool(v, true); break;
                case "autoboost": AutoBoost = Bool(v, true); break;
                case "affinitycores": AffinityCores = Int(v, 0); break;
                case "boostmode": BoostMode = ModeKey(v); break;
                case "boostwhitelist": BoostWhitelist = v; break;
                case "filtermode": FilterMode = Int(v, 0); break;
                case "filterpreset": FilterPreset = Int(v, 0); break;
                case "filterintensity": FilterIntensity = Int(v, 100); break;
                case "filtercontrast": FilterContrast = Int(v, 0); break;
                case "filtersaturation": FilterSaturation = Int(v, 0); break;
                case "filterwarmth": FilterWarmth = Int(v, 0); break;
                case "filtertint": FilterTint = Int(v, 0); break;
                case "filterdark": FilterDark = Int(v, 0); break;
                case "filtervibrance": FilterVibrance = Int(v, 0); break;
                case "filtericc": FilterIcc = v; break;
                case "iccprofile": AddIccProfile(v); break;
                case "reportauto": ReportAuto = Bool(v, true); break;
                case "reportminsec": ReportMinSec = Int(v, 30); break;
                case "memauto": MemAuto = Bool(v, false); break;
                case "memthreshold": MemThreshold = Int(v, 88); break;
                case "autostart": Autostart = Bool(v, false); break;
                case "checkupdates": CheckUpdates = Bool(v, false); break;
                case "closetotray": CloseToTray = Bool(v, true); break;
                case "askedelevation": AskedElevation = Bool(v, false); break;
            }
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("; FrameForge settings. Delete this file to reset.");
                sb.AppendLine("language=" + Language);
                sb.AppendLine("dark=" + B(Dark));
                sb.AppendLine("overlayOn=" + B(OverlayOn));
                sb.AppendLine("overlayStyle=" + OverlayStyle);
                sb.AppendLine("overlayCorner=" + OverlayCorner);
                sb.AppendLine("overlayX=" + OverlayX.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("overlayY=" + OverlayY.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("overlayScale=" + OverlayScale.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("overlayLocked=" + B(OverlayLocked));
                sb.AppendLine("showFps=" + B(ShowFps));
                sb.AppendLine("showCpu=" + B(ShowCpu));
                sb.AppendLine("showGpu=" + B(ShowGpu));
                sb.AppendLine("showCpuTemp=" + B(ShowCpuTemp));
                sb.AppendLine("showGpuTemp=" + B(ShowGpuTemp));
                sb.AppendLine("showRam=" + B(ShowRam));
                sb.AppendLine("showRamPct=" + B(ShowRamPct));
                sb.AppendLine("showLow=" + B(ShowLow));
                sb.AppendLine("showClock=" + B(ShowClock));
                sb.AppendLine("showSession=" + B(ShowSession));
                sb.AppendLine("boostPriority=" + B(BoostPriority));
                sb.AppendLine("boostPowerPlan=" + B(BoostPowerPlan));
                sb.AppendLine("boostTimer=" + B(BoostTimer));
                sb.AppendLine("boostDvr=" + B(BoostDvr));
                sb.AppendLine("boostMem=" + B(BoostMem));
                sb.AppendLine("boostBackground=" + B(BoostBackground));
                sb.AppendLine("boostAffinity=" + B(BoostAffinity));
                sb.AppendLine("boostIrq=" + B(BoostIrq));
                sb.AppendLine("boostProfile=" + B(BoostProfile));
                sb.AppendLine("autoBoost=" + B(AutoBoost));
                sb.AppendLine("boostMode=" + BoostMode);
                sb.AppendLine("boostWhitelist=" + BoostWhitelist);
                sb.AppendLine("affinityCores=" + AffinityCores.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterMode=" + FilterMode.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterPreset=" + FilterPreset.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterIntensity=" + FilterIntensity.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterContrast=" + FilterContrast.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterSaturation=" + FilterSaturation.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterWarmth=" + FilterWarmth.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterTint=" + FilterTint.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterDark=" + FilterDark.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterVibrance=" + FilterVibrance.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterIcc=" + FilterIcc);
                for (int i = 0; i < IccList.Count; i++)
                    sb.AppendLine("iccProfile=" + IccList[i].Name + "|" + IccList[i].Path);
                sb.AppendLine("reportAuto=" + B(ReportAuto));
                sb.AppendLine("reportMinSec=" + ReportMinSec.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("memAuto=" + B(MemAuto));
                sb.AppendLine("memThreshold=" + MemThreshold.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("autostart=" + B(Autostart));
                sb.AppendLine("checkUpdates=" + B(CheckUpdates));
                sb.AppendLine("closeToTray=" + B(CloseToTray));
                sb.AppendLine("askedElevation=" + B(AskedElevation));
                SafeFile.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("settings save: " + ex.Message); }
        }

        public BoostPlan Plan()
        {
            var p = new BoostPlan();
            p.Priority = BoostPriority;
            p.PowerPlan = BoostPowerPlan;
            p.Timer = BoostTimer;
            p.Dvr = BoostDvr;
            p.Memory = BoostMem;
            p.Background = BoostBackground;
            p.Affinity = BoostAffinity;
            p.Irq = BoostIrq;
            p.Cores = AffinityCores;
            return p;
        }

        public static string ModeKey(string v)
        {
            if (v == "beast" || v == "custom") return v;
            return "balanced";
        }

        /// <summary>叠加层位置键:auto 或屏幕四角,未知值一律回到 auto。</summary>
        public static string CornerKey(string v)
        {
            if (v == "tl" || v == "tr" || v == "bl" || v == "br") return v;
            return "auto";
        }

        /// <summary>套用模式预设:均衡只开可回滚的安全项,野兽全开,自定义保留用户勾选。</summary>
        public void ApplyMode(string mode)
        {
            BoostMode = ModeKey(mode);
            if (BoostMode == "balanced") SetSafeItems();
            else if (BoostMode == "beast") SetAllItems();
        }

        /// <summary>自定义优化项:数组顺序为 priority/power/timer/dvr/mem/bg/affinity/irq。</summary>
        public void ApplyCustom(bool[] v)
        {
            if (v == null || v.Length < 8) return;
            BoostPriority = v[0];
            BoostPowerPlan = v[1];
            BoostTimer = v[2];
            BoostDvr = v[3];
            BoostMem = v[4];
            BoostBackground = v[5];
            BoostAffinity = v[6];
            BoostIrq = v[7];
            BoostMode = "custom";
        }

        public bool MatchesBalanced()
        {
            return BoostPriority && BoostPowerPlan && BoostTimer && BoostDvr && BoostMem
                && !BoostBackground && !BoostAffinity && !BoostIrq;
        }

        public bool MatchesBeast()
        {
            return BoostPriority && BoostPowerPlan && BoostTimer && BoostDvr && BoostMem
                && BoostBackground && BoostAffinity && BoostIrq;
        }

        private void SetSafeItems()
        {
            BoostPriority = true;
            BoostPowerPlan = true;
            BoostTimer = true;
            BoostDvr = true;
            BoostMem = true;
            BoostBackground = false;
            BoostAffinity = false;
            BoostIrq = false;
        }

        private void SetAllItems()
        {
            BoostPriority = true;
            BoostPowerPlan = true;
            BoostTimer = true;
            BoostDvr = true;
            BoostMem = true;
            BoostBackground = true;
            BoostAffinity = true;
            BoostIrq = true;
        }

        /// <summary>拆出保护名单里的进程名(逗号、分号或空白分隔)。</summary>
        public string[] WhitelistNames()
        {
            if (string.IsNullOrEmpty(BoostWhitelist)) return new string[0];
            return BoostWhitelist.Split(new char[] { ',', ';', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
        }

        private static string B(bool v) { return v ? "1" : "0"; }

        private static bool Bool(string v, bool fallback)
        {
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        private static int Int(string v, int fallback)
        {
            int r;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : fallback;
        }

        private static double Dbl(string v, double fallback)
        {
            double r;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : fallback;
        }
    }
}