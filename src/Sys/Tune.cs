using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    /// <summary>一次调节的结果,界面据此给出准确反馈。</summary>
    public class TuneResult
    {
        public bool Ok;
        public bool NeedAdmin;
        public bool NeedReboot;
        public int Applied;
        public string Error = "";   // "" / "admin" / "failed"
    }

    /// <summary>
    /// 系统设置调节:把优化页的开关和预设真正写进系统。
    /// 每次改动前把原值写进 tune.journal,「恢复原样」按日志逐项写回。
    /// 需要管理员的项不会假装成功,失败会明确返回原因。
    /// </summary>
    public static class Tune
    {
        private const string HvciPath = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
        private const string VbsPath = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
        private const string GfxPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        private const string DvrStore = @"System\GameConfigStore";
        private const string DvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";

        private sealed class Saved
        {
            public string Hive = "HKCU";
            public string Path = "";
            public string Name = "";
            public bool Exists;
            public int Value;
            // 注册表里除了 DWORD,全屏优化与窗口化优化存的是字符串,日志要能原样写回。
            public string Type = "dword";   // "dword" / "sz"
            public string Text = "";
        }

        private static readonly List<Saved> saved = new List<Saved>();
        private static string savedScheme = "";
        private static bool loaded;

        public static string JournalPath
        {
            get { return Path.Combine(Settings.Dir, "tune.journal"); }
        }

        public static bool HasSaved
        {
            get
            {
                Load();
                return saved.Count > 0 || savedScheme.Length > 0;
            }
        }

        // =================================================================
        //  调节
        // =================================================================

        /// <summary>开关类:内存完整性、GPU 调度、Game DVR。</summary>
        public static TuneResult SetToggle(string id, bool on)
        {
            var r = new TuneResult();
            if (id == "vbs")
            {
                if (!MemEngine.IsAdmin) return Admin(r);
                int curHvci = ReadValue(RegistryHive.LocalMachine, HvciPath, "Enabled", 0);
                int curVbs = ReadValue(RegistryHive.LocalMachine, VbsPath, "EnableVirtualizationBasedSecurity", 0);
                int target = on ? 1 : 0;
                // 已经是目标状态就不要重复写,也不要谎报「已调整」。
                if (curHvci == target && curVbs == target) { r.Ok = true; return r; }

                Remember(RegistryHive.LocalMachine, HvciPath, "Enabled");
                Remember(RegistryHive.LocalMachine, VbsPath, "EnableVirtualizationBasedSecurity");
                bool admin;
                bool a = Write(RegistryHive.LocalMachine, HvciPath, "Enabled", target, out admin);
                bool b = Write(RegistryHive.LocalMachine, VbsPath, "EnableVirtualizationBasedSecurity", target, out admin);
                if (a && b) { r.Ok = true; r.Applied = 1; r.NeedReboot = true; }
                else return Fail(r, admin);
            }
            else if (id == "hags")
            {
                if (!MemEngine.IsAdmin) return Admin(r);
                int cur = ReadValue(RegistryHive.LocalMachine, GfxPath, "HwSchMode", 0);
                if ((on && cur == 2) || (!on && cur == 1)) { r.Ok = true; return r; }

                Remember(RegistryHive.LocalMachine, GfxPath, "HwSchMode");
                bool admin;
                if (!Write(RegistryHive.LocalMachine, GfxPath, "HwSchMode", on ? 2 : 1, out admin))
                    return Fail(r, admin);
                r.Ok = true; r.Applied = 1; r.NeedReboot = true;
            }
            else if (id == "dvr")
            {
                int target = on ? 1 : 0;
                int curA = ReadValue(RegistryHive.CurrentUser, DvrStore, "GameDVR_Enabled", 1);
                int curB = ReadValue(RegistryHive.CurrentUser, DvrKey, "AppCaptureEnabled", 1);
                if (curA == target && curB == target) { r.Ok = true; return r; }

                Remember(RegistryHive.CurrentUser, DvrStore, "GameDVR_Enabled");
                Remember(RegistryHive.CurrentUser, DvrKey, "AppCaptureEnabled");
                bool admin;
                bool a = Write(RegistryHive.CurrentUser, DvrStore, "GameDVR_Enabled", target, out admin);
                bool b = Write(RegistryHive.CurrentUser, DvrKey, "AppCaptureEnabled", target, out admin);
                if (a && b) { r.Ok = true; r.Applied = 1; }
                else return Fail(r, false);
            }
            else if (id == "windowed")
            {
                // 窗口化游戏优化默认就是开的,缺省值按「开」处理,不要一上来就报「已关闭」。
                string cur = ReadSz(RegistryHive.CurrentUser, Compat.GpuPrefsPath, Compat.GpuPrefsName);
                bool onNow = !Compat.HasPair(cur, Compat.SwapKey)
                    || Compat.PairValue(cur, Compat.SwapKey) != "0";
                if (onNow == on) { r.Ok = true; return r; }

                string next = Compat.SetPair(cur, Compat.SwapKey, on ? "1" : "0");
                RememberSz(RegistryHive.CurrentUser, Compat.GpuPrefsPath, Compat.GpuPrefsName);
                bool admin;
                if (!WriteSz(RegistryHive.CurrentUser, Compat.GpuPrefsPath, Compat.GpuPrefsName, next, out admin))
                    return Fail(r, admin);
                r.Ok = true; r.Applied = 1;
            }
            else if (id == "mpo")
            {
                // OverlayTestMode=5 表示禁用 MPO;删除该值即恢复系统默认调度。
                if (!MemEngine.IsAdmin) return Admin(r);
                bool offNow = ReadValue(RegistryHive.LocalMachine, Compat.DwmPath, Compat.MpoName, 0) == 5;
                if (offNow == !on) { r.Ok = true; return r; }

                Remember(RegistryHive.LocalMachine, Compat.DwmPath, Compat.MpoName);
                bool admin;
                bool ok;
                if (on) ok = Write(RegistryHive.LocalMachine, Compat.DwmPath, Compat.MpoName, 5, out admin);
                else ok = DeleteValue(RegistryHive.LocalMachine, Compat.DwmPath, Compat.MpoName, out admin);
                if (!ok) return Fail(r, admin);
                r.Ok = true; r.Applied = 1; r.NeedReboot = true;
            }
            else
            {
                r.Error = "failed";
            }
            return r;
        }

        /// <summary>按游戏关闭全屏优化:只改该 exe 的兼容性标记,不影响其他程序。</summary>
        public static TuneResult SetFullscreenOpt(string exe, bool off)
        {
            var r = new TuneResult();
            if (string.IsNullOrEmpty(exe)) { r.Error = "failed"; return r; }

            string cur = ReadSz(RegistryHive.CurrentUser, Compat.LayersPath, exe);
            bool offNow = Compat.HasToken(cur, Compat.FseToken);
            if (offNow == off) { r.Ok = true; return r; }

            string next = Compat.ToggleToken(cur, Compat.FseToken, off);
            RememberSz(RegistryHive.CurrentUser, Compat.LayersPath, exe);
            bool admin;
            bool ok = next.Length == 0
                ? DeleteValue(RegistryHive.CurrentUser, Compat.LayersPath, exe, out admin)
                : WriteSz(RegistryHive.CurrentUser, Compat.LayersPath, exe, next, out admin);
            if (!ok) return Fail(r, admin);
            r.Ok = true; r.Applied = 1;
            return r;
        }

        /// <summary>电源计划:高性能 / 平衡。</summary>
        public static TuneResult SetPower(bool high)
        {
            var r = new TuneResult();
            string target = high ? BoostEngine.SchemeHigh : BoostEngine.SchemeBalanced;
            string cur = BoostEngine.ActiveSchemeGuid();
            if (string.Equals(cur, target, StringComparison.OrdinalIgnoreCase))
            {
                r.Ok = true;
                return r;
            }
            if (!BoostEngine.SetScheme(target))
            {
                r.Error = "failed";
                return r;
            }

            Load();
            if (savedScheme.Length == 0)
            {
                savedScheme = cur;
                SaveJournal();
            }
            r.Ok = true;
            r.Applied = 1;
            return r;
        }

        /// <summary>一键预设:游戏优先 / 日常均衡。</summary>
        public static TuneResult ApplyPreset(string preset)
        {
            bool game = preset == "game";
            var total = new TuneResult();
            Merge(total, SetPower(game));
            Merge(total, SetToggle("hags", true));
            Merge(total, SetToggle("dvr", !game));
            // 三项都已经在目标状态时 Applied 为 0,这不算失败,界面会提示「已经是这个状态」。
            if (total.Error.Length == 0)
            {
                total.Ok = true;
                total.Error = "";
            }
            return total;
        }

        /// <summary>按日志恢复所有改过的原值,返回恢复项数。</summary>
        public static int RestoreAll(out bool needAdmin)
        {
            needAdmin = false;
            Load();
            if (saved.Count == 0 && savedScheme.Length == 0) return 0;

            int done = 0;
            if (savedScheme.Length > 0)
            {
                try { if (BoostEngine.SetScheme(savedScheme)) done++; }
                catch (Exception ex) { Log.Write("tune scheme: " + ex.Message); }
            }

            foreach (Saved s in saved)
            {
                RegistryHive hive = s.Hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                if (hive == RegistryHive.LocalMachine && !MemEngine.IsAdmin)
                {
                    needAdmin = true;
                    continue;
                }
                bool admin;
                bool ok;
                if (s.Type == "sz")
                {
                    if (s.Exists) ok = WriteSz(hive, s.Path, s.Name, s.Text, out admin);
                    else ok = DeleteValue(hive, s.Path, s.Name, out admin);
                }
                else
                {
                    if (s.Exists) ok = Write(hive, s.Path, s.Name, s.Value, out admin);
                    else ok = DeleteValue(hive, s.Path, s.Name, out admin);
                }
                if (ok) done++;
                if (admin) needAdmin = true;
            }

            if (!needAdmin)
            {
                saved.Clear();
                savedScheme = "";
                ClearJournal();
            }
            return done;
        }

        private static void Merge(TuneResult total, TuneResult part)
        {
            total.Applied += part.Applied;
            total.NeedReboot = total.NeedReboot || part.NeedReboot;
            total.NeedAdmin = total.NeedAdmin || part.NeedAdmin;
            if (!part.Ok && total.Error.Length == 0) total.Error = part.Error;
        }

        private static TuneResult Admin(TuneResult r)
        {
            r.NeedAdmin = true;
            r.Error = "admin";
            return r;
        }

        private static TuneResult Fail(TuneResult r, bool admin)
        {
            r.Error = admin ? "admin" : "failed";
            r.NeedAdmin = admin;
            return r;
        }

        // =================================================================
        //  注册表读写 + 原值日志
        // =================================================================

        private static bool Write(RegistryHive hive, string path, string name, int value, out bool admin)
        {
            admin = false;
            if (hive == RegistryHive.LocalMachine && !MemEngine.IsAdmin)
            {
                admin = true;
                return false;
            }
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                {
                    using (RegistryKey k = baseKey.CreateSubKey(path))
                    {
                        if (k == null) return false;
                        k.SetValue(name, value, RegistryValueKind.DWord);
                    }
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                admin = true;
                return false;
            }
            catch (Exception ex)
            {
                Log.Write("tune write: " + path + "\\" + name + " " + ex.Message);
                return false;
            }
        }

        private static bool DeleteValue(RegistryHive hive, string path, string name, out bool admin)
        {
            admin = false;
            if (hive == RegistryHive.LocalMachine && !MemEngine.IsAdmin)
            {
                admin = true;
                return false;
            }
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                {
                    using (RegistryKey k = baseKey.OpenSubKey(path, true))
                    {
                        if (k == null) return true;
                        k.DeleteValue(name, false);
                    }
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                admin = true;
                return false;
            }
            catch (Exception ex)
            {
                Log.Write("tune delete: " + path + "\\" + name + " " + ex.Message);
                return false;
            }
        }

        /// <summary>只读当前 DWORD;缺失或读不到时用 fallback。</summary>
        private static int ReadValue(RegistryHive hive, string path, string name, int fallback)
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

        private static bool WriteSz(RegistryHive hive, string path, string name, string value, out bool admin)
        {
            admin = false;
            if (hive == RegistryHive.LocalMachine && !MemEngine.IsAdmin)
            {
                admin = true;
                return false;
            }
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                {
                    using (RegistryKey k = baseKey.CreateSubKey(path))
                    {
                        if (k == null) return false;
                        k.SetValue(name, value ?? "", RegistryValueKind.String);
                    }
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                admin = true;
                return false;
            }
            catch (Exception ex)
            {
                Log.Write("tune write sz: " + path + "\\" + name + " " + ex.Message);
                return false;
            }
        }

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

        /// <summary>记下第一次改之前的值;之后再改不会覆盖原始值。</summary>
        private static void Remember(RegistryHive hive, string path, string name)
        {
            Load();
            string hiveName = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
            foreach (Saved s in saved)
                if (s.Hive == hiveName && s.Path == path && s.Name == name) return;

            object v = null;
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                {
                    using (RegistryKey k = baseKey.OpenSubKey(path))
                    {
                        if (k != null) v = k.GetValue(name, null);
                    }
                }
            }
            catch (Exception ex) { Log.Write("tune read: " + ex.Message); }

            var n = new Saved();
            n.Hive = hiveName;
            n.Path = path;
            n.Name = name;
            n.Exists = v != null;
            n.Value = Int(v);
            saved.Add(n);
            SaveJournal();
        }

        /// <summary>字符串值的原值记录:全屏优化标记与窗口化优化开关都走这里。</summary>
        private static void RememberSz(RegistryHive hive, string path, string name)
        {
            Load();
            string hiveName = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
            foreach (Saved s in saved)
                if (s.Hive == hiveName && s.Path == path && s.Name == name) return;

            string v = ReadSz(hive, path, name);
            var n = new Saved();
            n.Hive = hiveName;
            n.Path = path;
            n.Name = name;
            n.Type = "sz";
            n.Exists = v != null;
            n.Text = v ?? "";
            saved.Add(n);
            SaveJournal();
        }

        private static int Int(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(JournalPath)) return;
                foreach (string line in File.ReadAllLines(JournalPath, Encoding.UTF8))
                {
                    if (line.StartsWith("scheme="))
                    {
                        savedScheme = line.Substring(7).Trim();
                    }
                    else if (line.StartsWith("reg="))
                    {
                        string[] p = line.Substring(4).Split('|');
                        if (p.Length < 5) continue;
                        var s = new Saved();
                        s.Hive = p[0];
                        s.Path = p[1];
                        s.Name = p[2];
                        s.Exists = p[3] == "1";
                        if (p.Length >= 6)
                        {
                            s.Type = p[4] == "sz" ? "sz" : "dword";
                            if (s.Type == "sz") s.Text = Unescape(p[5]);
                            else s.Value = Int(p[5]);
                        }
                        else
                        {
                            s.Value = Int(p[4]);
                        }
                        saved.Add(s);
                    }
                }
            }
            catch (Exception ex) { Log.Write("tune journal load: " + ex.Message); }
        }

        private static void SaveJournal()
        {
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                var sb = new StringBuilder();
                sb.AppendLine("v=1");
                sb.AppendLine("scheme=" + savedScheme);
                foreach (Saved s in saved)
                {
                    sb.Append("reg=").Append(s.Hive).Append('|').Append(s.Path).Append('|')
                        .Append(s.Name).Append('|').Append(s.Exists ? "1" : "0").Append('|')
                        .Append(s.Type).Append('|');
                    if (s.Type == "sz")
                        sb.Append(Escape(s.Text));
                    else
                        sb.Append(s.Value.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine();
                }
                string tmp = JournalPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(JournalPath)) File.Delete(JournalPath);
                File.Move(tmp, JournalPath);
            }
            catch (Exception ex) { Log.Write("tune journal save: " + ex.Message); }
        }

        /// <summary>日志用 | 分隔,字符串值里的 | 与换行先转义,恢复时再还原。</summary>
        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("%", "%25").Replace("|", "%7C").Replace("\r", "%0D").Replace("\n", "%0A");
        }

        private static string Unescape(string s)
        {
            if (s == null) return "";
            return s.Replace("%0A", "\n").Replace("%0D", "\r").Replace("%7C", "|").Replace("%25", "%");
        }

        private static void ClearJournal()
        {
            try { if (File.Exists(JournalPath)) File.Delete(JournalPath); } catch { }
        }
    }
}