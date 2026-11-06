using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    /// <summary>
    /// 加速前的原始值日志。加速会改注册表和电源计划,如果程序被强杀或崩溃,
    /// 内存里的回滚状态就丢了。这里在改动的同时把原值写到磁盘,
    /// 下次启动发现游戏已经退出,就按日志把这些系统设置恢复回去。
    /// </summary>
    public static class BoostJournal
    {
        public sealed class Entry
        {
            public string Path = "";
            public string Name = "";
            public bool Exists;
            public int Value;
        }

        public sealed class State
        {
            public int GamePid;
            public string GameName = "";
            public string PrevScheme = "";
            public readonly List<Entry> Reg = new List<Entry>();
        }

        public static string FilePath
        {
            get { return Path.Combine(Settings.Dir, "boost.journal"); }
        }

        public static bool Pending
        {
            get { try { return File.Exists(FilePath); } catch { return false; } }
        }

        /// <summary>落盘当前这次加速的原始值。</summary>
        public static void Save(State st)
        {
            if (st == null) return;
            var sb = new StringBuilder();
            sb.AppendLine("v=1");
            sb.AppendLine("game=" + st.GamePid.ToString(CultureInfo.InvariantCulture) + "|" + st.GameName);
            sb.AppendLine("scheme=" + st.PrevScheme);
            foreach (Entry e in st.Reg)
            {
                sb.Append("reg=").Append(e.Path).Append('|').Append(e.Name).Append('|')
                    .Append(e.Exists ? "1" : "0").Append('|')
                    .Append(e.Value.ToString(CultureInfo.InvariantCulture)).AppendLine();
            }

            if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        public static State Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                var st = new State();
                foreach (string line in lines)
                {
                    if (line.StartsWith("game="))
                    {
                        string body = line.Substring(5);
                        int cut = body.IndexOf('|');
                        if (cut >= 0)
                        {
                            st.GamePid = Int(body.Substring(0, cut));
                            st.GameName = body.Substring(cut + 1);
                        }
                    }
                    else if (line.StartsWith("scheme="))
                    {
                        st.PrevScheme = line.Substring(7).Trim();
                    }
                    else if (line.StartsWith("reg="))
                    {
                        string[] p = line.Substring(4).Split('|');
                        if (p.Length < 4) continue;
                        var e = new Entry();
                        e.Path = p[0];
                        e.Name = p[1];
                        e.Exists = p[2] == "1";
                        e.Value = Int(p[3]);
                        st.Reg.Add(e);
                    }
                }
                return st;
            }
            catch (Exception ex)
            {
                Log.Write("journal load: " + ex.Message);
                return null;
            }
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }

        /// <summary>按日志恢复注册表和电源计划,返回恢复的项数。</summary>
        public static int Restore(State st)
        {
            if (st == null) return 0;
            int done = 0;
            for (int i = st.Reg.Count - 1; i >= 0; i--)
            {
                Entry e = st.Reg[i];
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(e.Path))
                    {
                        if (k == null) continue;
                        if (e.Exists) k.SetValue(e.Name, e.Value, RegistryValueKind.DWord);
                        else k.DeleteValue(e.Name, false);
                        done++;
                    }
                }
                catch (Exception ex) { Log.Write("journal reg: " + e.Path + "\\" + e.Name + " " + ex.Message); }
            }
            if (st.PrevScheme.Length > 0)
            {
                try { if (BoostEngine.SetScheme(st.PrevScheme)) done++; }
                catch (Exception ex) { Log.Write("journal scheme: " + ex.Message); }
            }
            Clear();
            return done;
        }

        /// <summary>一键还原:不管当前是否在加速状态,有日志就恢复。</summary>
        public static int RestoreAll()
        {
            return Restore(Load());
        }

        /// <summary>
        /// 启动时自愈:只有日志存在且当时的游戏已经退出才动手,
        /// 游戏还在跑说明用户可能只是重启了界面,这时不能把系统设置改回去。
        /// </summary>
        public static int RecoverStale()
        {
            if (!Pending) return 0;
            State st = Load();
            if (st == null) { Clear(); return 0; }
            if (Alive(st.GamePid, st.GameName)) return 0;
            int n = Restore(st);
            if (n > 0) Log.Write("journal recovered=" + n);
            return n;
        }

        private static bool Alive(int pid, string name)
        {
            if (pid <= 0) return false;
            try
            {
                Process p = Process.GetProcessById(pid);
                if (p.HasExited) return false;
                if (name.Length == 0) return true;
                return string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static int Int(string s)
        {
            int v;
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
            return v;
        }
    }
}