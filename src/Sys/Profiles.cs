using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FrameForge.Sys
{
    /// <summary>某个游戏记住的加速项。只存开关,不存系统状态。</summary>
    public class GameProfile
    {
        public string Name = "";
        public bool Priority = true;
        public bool PowerPlan = true;
        public bool Timer = true;
        public bool Dvr = true;
        public bool Memory = true;
        public bool Background;
        public bool Affinity;
        public bool Irq;
        public int Cores;
        public DateTime LastUsed = DateTime.MinValue;

        public BoostPlan Plan()
        {
            var p = new BoostPlan();
            p.Priority = Priority;
            p.PowerPlan = PowerPlan;
            p.Timer = Timer;
            p.Dvr = Dvr;
            p.Memory = Memory;
            p.Background = Background;
            p.Affinity = Affinity;
            p.Irq = Irq;
            p.Cores = Cores;
            return p;
        }
    }

    /// <summary>
    /// 按游戏保存策略:%LOCALAPPDATA%\FrameForge\profiles.ini,一节一个游戏。
    /// 纯文本、可手改,进程内缓存,写入时整份重写。
    /// </summary>
    public sealed class ProfileStore
    {
        private readonly List<GameProfile> items = new List<GameProfile>();

        public static string FilePath { get { return Path.Combine(Settings.Dir, "profiles.ini"); } }

        public List<GameProfile> All { get { return items; } }

        public void Load()
        {
            items.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                GameProfile cur = null;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    if (line.StartsWith("["))
                    {
                        int end = line.IndexOf(']');
                        string name = end > 1 ? line.Substring(1, end - 1).Trim() : "";
                        cur = name.Length > 0 ? new GameProfile { Name = name } : null;
                        if (cur != null) items.Add(cur);
                        continue;
                    }
                    if (cur == null) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    Apply(cur, line.Substring(0, eq).Trim().ToLowerInvariant(), line.Substring(eq + 1).Trim());
                }
            }
            catch (Exception ex) { Log.Write("profiles load: " + ex.Message); }
        }

        private static void Apply(GameProfile p, string key, string v)
        {
            switch (key)
            {
                case "priority": p.Priority = Bool(v, true); break;
                case "power": p.PowerPlan = Bool(v, true); break;
                case "timer": p.Timer = Bool(v, true); break;
                case "dvr": p.Dvr = Bool(v, true); break;
                case "memory": p.Memory = Bool(v, true); break;
                case "background": p.Background = Bool(v, false); break;
                case "affinity": p.Affinity = Bool(v, false); break;
                case "irq": p.Irq = Bool(v, false); break;
                case "cores": p.Cores = Int(v, 0); break;
                case "last": p.LastUsed = Date(v); break;
            }
        }

        public GameProfile Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < items.Count; i++)
                if (string.Equals(items[i].Name, name, StringComparison.OrdinalIgnoreCase)) return items[i];
            return null;
        }

        /// <summary>有则更新、无则新增,然后整份落盘。</summary>
        public void Save(GameProfile p)
        {
            if (p == null || p.Name.Length == 0) return;
            GameProfile old = Find(p.Name);
            if (old == null) items.Add(p);
            else items[items.IndexOf(old)] = p;
            p.LastUsed = DateTime.Now;
            WriteAll();
        }

        public void Remove(string name)
        {
            GameProfile old = Find(name);
            if (old == null) return;
            items.Remove(old);
            WriteAll();
        }

        private void WriteAll()
        {
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                var sb = new StringBuilder();
                sb.AppendLine("; FrameForge per-game profiles.");
                for (int i = 0; i < items.Count; i++)
                {
                    GameProfile p = items[i];
                    sb.AppendLine();
                    sb.AppendLine("[" + p.Name.Replace('[', '_').Replace(']', '_') + "]");
                    sb.AppendLine("priority=" + B(p.Priority));
                    sb.AppendLine("power=" + B(p.PowerPlan));
                    sb.AppendLine("timer=" + B(p.Timer));
                    sb.AppendLine("dvr=" + B(p.Dvr));
                    sb.AppendLine("memory=" + B(p.Memory));
                    sb.AppendLine("background=" + B(p.Background));
                    sb.AppendLine("affinity=" + B(p.Affinity));
                    sb.AppendLine("irq=" + B(p.Irq));
                    sb.AppendLine("cores=" + p.Cores.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("last=" + p.LastUsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                }
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("profiles save: " + ex.Message); }
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

        private static DateTime Date(string v)
        {
            DateTime r;
            return DateTime.TryParseExact(v, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out r) ? r : DateTime.MinValue;
        }
    }
}