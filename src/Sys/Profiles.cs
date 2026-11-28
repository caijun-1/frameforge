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
        // Name 是主键：新策略统一为小写进程名（不含 .exe）；旧策略可暂时保留显示名。
        public string Name = "";
        // 只用于展示和旧显示名查询兼容，不参与新策略的主键生成。
        public string DisplayName = "";
        public bool Priority = true;
        public bool PowerPlan = true;
        public bool Timer = true;
        public bool Dvr = true;
        public bool Memory = true;
        public bool Background;
        public bool Affinity;
        public bool Irq;
        public int Cores;
        // v1.4.7 方案 §6：档位落库，消除"命中游戏策略却沿用全局档位"的隐性覆盖。
        // 旧 profiles.ini 没有这一行时由 Apply() 回落到 4。
        public int PriorityLevel = 4;
        // Wave 1: 按游戏自动化覆盖项（默认值表示跟随全局）。
        public string CorePolicy = "";       // "" = 跟随全局 | "auto" | "pcore" | "singleccd"
        public int FilterPreset = -1;        // -1 = 跟随全局 | 0 = 关 | 1..22 = 预设索引
        public int FilterIntensity = -1;     // -1 = 跟随全局 | 0..100
        public int OverlayMode = -1;         // -1 = 跟随全局 | 0 = 隐藏 | 1 = 显示
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
            // 方案 §3.2/§6：命中专属策略时档位随策略生效，不再由 MaybeAutoBoost 改写。
            p.PriorityLevel = PriorityLevel;
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
                // §6.2 向下兼容：老文件无 prioritylevel 行时保持默认 4。
                case "prioritylevel": p.PriorityLevel = Math.Max(0, Math.Min(4, Int(v, 4))); break;
                case "corepolicy": p.CorePolicy = NormalizeCorePolicy(v); break;
                case "filterpreset": p.FilterPreset = Math.Max(-1, Math.Min(22, Int(v, -1))); break;
                case "filterintensity": p.FilterIntensity = Math.Max(-1, Math.Min(100, Int(v, -1))); break;
                case "overlaymode": p.OverlayMode = Math.Max(-1, Math.Min(1, Int(v, -1))); break;
                case "display": p.DisplayName = v; break;
                case "last": p.LastUsed = Date(v); break;
            }
        }

        private static string NormalizeCorePolicy(string v)
        {
            string s = (v ?? "").Trim().ToLowerInvariant();
            if (s == "auto" || s == "pcore" || s == "singleccd") return s;
            return "";
        }

        /// <summary>统一进程主键：去空白、转小写并去掉末尾的 .exe（支持 procName@folderKey 复合键）。</summary>
        public static string NormalizeKey(string name)
        {
            string key = (name ?? "").Trim().ToLowerInvariant();
            int at = key.IndexOf('@');
            if (at > 0)
            {
                string proc = key.Substring(0, at).Trim();
                if (proc.EndsWith(".exe", StringComparison.Ordinal)) proc = proc.Substring(0, proc.Length - 4);
                string folder = key.Substring(at + 1).Trim();
                return folder.Length > 0 ? (proc + "@" + folder) : proc;
            }
            if (key.EndsWith(".exe", StringComparison.Ordinal)) key = key.Substring(0, key.Length - 4);
            return key;
        }

        /// <summary>从 exe 路径提取用于区分通用二进制（如 Client-Win64-Shipping.exe）的目录键。</summary>
        public static string FolderKey(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return "";
            try
            {
                string dir = Path.GetDirectoryName(exePath);
                string fallback = "";
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
                {
                    string name = Path.GetFileName(dir.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(name))
                    {
                        string lower = name.Trim().ToLowerInvariant();
                        if (fallback.Length == 0) fallback = lower;
                        if (!IsGenericFolder(lower)) return lower;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
                return fallback;
            }
            catch { return ""; }
        }

        private static bool IsGenericFolder(string lower)
        {
            return lower == "win64" || lower == "win32" || lower == "x64" || lower == "x86"
                || lower == "binaries" || lower == "bin" || lower == "client"
                || lower == "game" || lower == "engine" || lower == "shipping"
                || lower == "release" || lower == "retail";
        }

        /// <summary>生成 procName@folderKey 复合主键；无法提取目录键时回退到 procName。</summary>
        public static string CompositeKey(string procName, string exePath)
        {
            string proc = NormalizeKey(procName);
            if (proc.Length == 0) return "";
            int at = proc.IndexOf('@');
            if (at > 0) proc = proc.Substring(0, at);
            string fk = FolderKey(exePath);
            return fk.Length > 0 ? (proc + "@" + fk) : proc;
        }

        /// <summary>先按进程主键查，再兼容旧的显示名节名及新策略的显示名别名。</summary>
        public GameProfile Find(string name)
        {
            return Find(name, null);
        }

        /// <summary>运行时传进程名与可用于回退的显示名；主键命中始终优先。</summary>
        public GameProfile Find(string processName, string displayName)
        {
            GameProfile hit = FindKey(processName);
            if (hit != null) return hit;
            return FindDisplay(!string.IsNullOrEmpty(displayName) ? displayName : processName);
        }

        /// <summary>三段式查询：procName@folderKey 复合键 -> procName -> displayName。</summary>
        public GameProfile Find(string procName, string exePath, string displayName)
        {
            if (!string.IsNullOrEmpty(exePath))
            {
                string composite = CompositeKey(procName, exePath);
                if (composite.IndexOf('@') > 0)
                {
                    GameProfile hitComposite = FindKey(composite);
                    if (hitComposite != null) return hitComposite;
                }
            }
            return Find(procName, displayName);
        }

        private GameProfile FindKey(string name)
        {
            string key = NormalizeKey(name);
            if (key.Length == 0) return null;
            for (int i = 0; i < items.Count; i++)
                if (string.Equals(NormalizeKey(items[i].Name), key, StringComparison.Ordinal)) return items[i];
            return null;
        }

        private static bool IsLegacyDisplay(GameProfile p, string displayName)
        {
            if (p == null) return false;
            string display = NormalizeKey(displayName);
            string name = NormalizeKey(p.Name);
            string alias = NormalizeKey(p.DisplayName);
            // [a] display=Shared 已属于进程 a，不能被同名显示的进程 b 接管。
            // 只有旧 [Title] 或 [Title] display=Title 才允许显示名回退。
            return display.Length > 0 && name == display && (alias.Length == 0 || alias == name);
        }

        private GameProfile FindDisplay(string displayName)
        {
            for (int i = 0; i < items.Count; i++)
                if (IsLegacyDisplay(items[i], displayName)) return items[i];
            return null;
        }

        /// <summary>有则更新、无则新增,然后整份落盘。</summary>
        public void Save(GameProfile p)
        {
            if (p == null) return;
            p.Name = NormalizeKey(p.Name);
            if (p.Name.Length == 0) return;
            GameProfile old = FindKey(p.Name);
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

        /// <summary>删除进程主键与它的旧显示名节，避免删除后旧节“复活”。</summary>
        public void RemoveAll(string processName, string displayName)
        {
            string key = NormalizeKey(processName);
            bool changed = false;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (NormalizeKey(items[i].Name) != key && !IsLegacyDisplay(items[i], displayName)) continue;
                items.RemoveAt(i);
                changed = true;
            }
            if (changed) WriteAll();
        }

        /// <summary>迁移保存后清掉旧显示名节，不碰其他进程主键。</summary>
        public void RemoveLegacy(string processName, string displayName)
        {
            string key = NormalizeKey(processName);
            bool changed = false;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (NormalizeKey(items[i].Name) == key || !IsLegacyDisplay(items[i], displayName)) continue;
                items.RemoveAt(i);
                changed = true;
            }
            if (changed) WriteAll();
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
                    if (p.DisplayName.Length > 0)
                        sb.AppendLine("display=" + p.DisplayName.Replace('\r', ' ').Replace('\n', ' '));
                    sb.AppendLine("priority=" + B(p.Priority));
                    sb.AppendLine("power=" + B(p.PowerPlan));
                    sb.AppendLine("timer=" + B(p.Timer));
                    sb.AppendLine("dvr=" + B(p.Dvr));
                    sb.AppendLine("memory=" + B(p.Memory));
                    sb.AppendLine("background=" + B(p.Background));
                    sb.AppendLine("affinity=" + B(p.Affinity));
                    sb.AppendLine("irq=" + B(p.Irq));
                    sb.AppendLine("cores=" + p.Cores.ToString(CultureInfo.InvariantCulture));
                    // §6.2：整份重写时补写新键；节名转义与其余字段保持不变。
                    sb.AppendLine("prioritylevel=" + p.PriorityLevel.ToString(CultureInfo.InvariantCulture));
                    string cp = NormalizeCorePolicy(p.CorePolicy);
                    if (cp.Length > 0)
                        sb.AppendLine("corepolicy=" + cp);
                    if (p.FilterPreset >= 0)
                        sb.AppendLine("filterpreset=" + Math.Min(22, p.FilterPreset).ToString(CultureInfo.InvariantCulture));
                    if (p.FilterIntensity >= 0)
                        sb.AppendLine("filterintensity=" + Math.Min(100, p.FilterIntensity).ToString(CultureInfo.InvariantCulture));
                    if (p.OverlayMode >= 0)
                        sb.AppendLine("overlaymode=" + Math.Min(1, p.OverlayMode).ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("last=" + p.LastUsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                }
                SafeFile.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
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