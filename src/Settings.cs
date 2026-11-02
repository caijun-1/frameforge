using System;
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
        public int OverlayX = -1;             // -1 = 自动放右上角
        public int OverlayY = -1;
        public double OverlayScale = 1.0;
        public bool OverlayLocked = true;
        public bool ShowFps = true;
        public bool ShowCpu = false;
        public bool ShowGpu = true;
        public bool ShowTemp = true;
        public bool ShowRam = false;

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
        public int AffinityCores = 0;         // 0 = 自动

        // 画面滤镜
        public int FilterMode = 0;            // 0 关 | 1 仅游戏 | 2 始终
        public int FilterPreset = 0;
        public int FilterIntensity = 100;
        public int FilterBrightness = 0;
        public int FilterContrast = 0;
        public int FilterSaturation = 0;
        public int FilterWarmth = 0;

        // 内存
        public bool MemAuto = false;
        public int MemThreshold = 88;

        // 常规
        public bool Autostart = false;
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
            return s;
        }

        private void Apply(string k, string v)
        {
            switch (k.ToLowerInvariant())
            {
                case "language": Language = v == "en" ? "en" : "zh"; break;
                case "dark": Dark = Bool(v, true); break;
                case "overlayon": OverlayOn = Bool(v, false); break;
                case "overlaystyle": OverlayStyle = v; break;
                case "overlayx": OverlayX = Int(v, -1); break;
                case "overlayy": OverlayY = Int(v, -1); break;
                case "overlayscale": OverlayScale = Dbl(v, 1.0); break;
                case "overlaylocked": OverlayLocked = Bool(v, true); break;
                case "showfps": ShowFps = Bool(v, true); break;
                case "showcpu": ShowCpu = Bool(v, false); break;
                case "showgpu": ShowGpu = Bool(v, true); break;
                case "showtemp": ShowTemp = Bool(v, true); break;
                case "showram": ShowRam = Bool(v, false); break;
                case "boostpriority": BoostPriority = Bool(v, true); break;
                case "boostpowerplan": BoostPowerPlan = Bool(v, true); break;
                case "boosttimer": BoostTimer = Bool(v, true); break;
                case "boostdvr": BoostDvr = Bool(v, true); break;
                case "boostmem": BoostMem = Bool(v, true); break;
                case "boostbackground": BoostBackground = Bool(v, false); break;
                case "boostaffinity": BoostAffinity = Bool(v, false); break;
                case "boostirq": BoostIrq = Bool(v, false); break;
                case "boostprofile": BoostProfile = Bool(v, true); break;
                case "affinitycores": AffinityCores = Int(v, 0); break;
                case "filtermode": FilterMode = Int(v, 0); break;
                case "filterpreset": FilterPreset = Int(v, 0); break;
                case "filterintensity": FilterIntensity = Int(v, 100); break;
                case "filterbrightness": FilterBrightness = Int(v, 0); break;
                case "filtercontrast": FilterContrast = Int(v, 0); break;
                case "filtersaturation": FilterSaturation = Int(v, 0); break;
                case "filterwarmth": FilterWarmth = Int(v, 0); break;
                case "memauto": MemAuto = Bool(v, false); break;
                case "memthreshold": MemThreshold = Int(v, 88); break;
                case "autostart": Autostart = Bool(v, false); break;
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
                sb.AppendLine("overlayX=" + OverlayX.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("overlayY=" + OverlayY.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("overlayScale=" + OverlayScale.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("overlayLocked=" + B(OverlayLocked));
                sb.AppendLine("showFps=" + B(ShowFps));
                sb.AppendLine("showCpu=" + B(ShowCpu));
                sb.AppendLine("showGpu=" + B(ShowGpu));
                sb.AppendLine("showTemp=" + B(ShowTemp));
                sb.AppendLine("showRam=" + B(ShowRam));
                sb.AppendLine("boostPriority=" + B(BoostPriority));
                sb.AppendLine("boostPowerPlan=" + B(BoostPowerPlan));
                sb.AppendLine("boostTimer=" + B(BoostTimer));
                sb.AppendLine("boostDvr=" + B(BoostDvr));
                sb.AppendLine("boostMem=" + B(BoostMem));
                sb.AppendLine("boostBackground=" + B(BoostBackground));
                sb.AppendLine("boostAffinity=" + B(BoostAffinity));
                sb.AppendLine("boostIrq=" + B(BoostIrq));
                sb.AppendLine("boostProfile=" + B(BoostProfile));
                sb.AppendLine("affinityCores=" + AffinityCores.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterMode=" + FilterMode.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterPreset=" + FilterPreset.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterIntensity=" + FilterIntensity.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterBrightness=" + FilterBrightness.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterContrast=" + FilterContrast.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterSaturation=" + FilterSaturation.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filterWarmth=" + FilterWarmth.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("memAuto=" + B(MemAuto));
                sb.AppendLine("memThreshold=" + MemThreshold.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("autostart=" + B(Autostart));
                sb.AppendLine("closeToTray=" + B(CloseToTray));
                sb.AppendLine("askedElevation=" + B(AskedElevation));
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
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