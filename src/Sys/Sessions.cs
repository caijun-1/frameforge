using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FrameForge.Sys
{
    /// <summary>一局游戏的性能记录,对应「战绩」页和退出游戏后的报告卡。</summary>
    public sealed class SessionRecord
    {
        public string Id = "";
        public string Exe = "";
        public string Title = "";
        /// <summary>exe 全路径,读程序图标用;老记录没有这个字段时按进程名现找。</summary>
        public string ExePath = "";
        public DateTime Start;
        public DateTime End;
        public int Seconds;

        public double FpsAvg;
        public double FpsMax;
        public double FpsMin;
        public double Low1;
        public double CpuAvg;
        public double CpuMax;
        public double GpuAvg;
        public double GpuMax;
        public double GpuTempAvg;
        public double GpuTempMax;
        public double RamAvg;      // MB
        public double RamMax;      // MB
        public double VramMax;     // MB
        public double Power;       // 平均整机功耗估算 W

        // 本局帧率曲线:每 FpsStep 秒一个采样点,报告卡里画折线图用。
        public double[] FpsSeries = new double[0];
        public int FpsStep = 4;

        public double Wh { get { return Power * Seconds / 3600.0; } }
        /// <summary>按中国电网约 0.581 kg CO₂/kWh 估算,和游戏加加的算法口径接近。</summary>
        public double Co2 { get { return Wh * 0.581; } }

        public string Duration
        {
            get
            {
                int s = Seconds;
                int h = s / 3600;
                int m = (s % 3600) / 60;
                if (h > 0) return h + "h " + m + "m";
                return m + "m " + (s % 60) + "s";
            }
        }

        /// <summary>
        /// 展示用名字:存档里已经识别出游戏名就直接用;旧记录或识别失败时存的是进程名,
        /// 再用内置库重认一次,保证报告和历史战绩看到的是同一个名字。
        /// </summary>
        public string DisplayName
        {
            get
            {
                string t = Title == null ? "" : Title.Trim();
                if (t.Length == 0) t = Exe == null ? "" : Exe;
                if (Exe != null && Exe.Length > 0 && LooksLikeProcess(t, Exe))
                {
                    string g = GameDb.Title(Exe);
                    if (g.Length > 0 && !LooksLikeProcess(g, Exe)) return g;
                }
                return t;
            }
        }

        private static bool LooksLikeProcess(string name, string exe)
        {
            string a = Strip(name);
            string b = Strip(exe);
            return b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string Strip(string s)
        {
            if (s == null) return "";
            string t = s.Trim();
            if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(0, t.Length - 4);
            return t.Trim();
        }

        /// <summary>读程序图标用的 exe 路径:存档里有就用,老记录按进程名现找。</summary>
        public string IconPath
        {
            get
            {
                if (ExePath != null && ExePath.Length > 0 && File.Exists(ExePath)) return ExePath;
                return Exe != null && Exe.Length > 0 ? GameDb.PathFor(Exe) : "";
            }
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append(Id).Append('|');
            sb.Append(Start.Ticks.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(Seconds.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(Safe(Exe)).Append('|');
            sb.Append(Safe(Title)).Append('|');
            sb.Append(N(FpsAvg)).Append('|').Append(N(FpsMax)).Append('|').Append(N(FpsMin)).Append('|').Append(N(Low1)).Append('|');
            sb.Append(N(CpuAvg)).Append('|').Append(N(CpuMax)).Append('|');
            sb.Append(N(GpuAvg)).Append('|').Append(N(GpuMax)).Append('|');
            sb.Append(N(GpuTempAvg)).Append('|').Append(N(GpuTempMax)).Append('|');
            sb.Append(N(RamAvg)).Append('|').Append(N(RamMax)).Append('|').Append(N(VramMax)).Append('|');
            sb.Append(N(Power)).Append('|');
            sb.Append(FpsStep.ToString(CultureInfo.InvariantCulture)).Append('|');
            for (int i = 0; i < FpsSeries.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(FpsSeries[i].ToString("0.#", CultureInfo.InvariantCulture));
            }
            sb.Append('|').Append(Safe(ExePath));
            return sb.ToString();
        }

        public static SessionRecord Parse(string line)
        {
            if (line == null) return null;
            string[] p = line.Split('|');
            if (p.Length < 18) return null;
            try
            {
                var r = new SessionRecord();
                r.Id = p[0];
                long ticks;
                if (!long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) return null;
                r.Start = new DateTime(ticks);
                r.Seconds = I(p[2]);
                r.End = r.Start.AddSeconds(r.Seconds);
                r.Exe = p[3];
                r.Title = p[4];
                r.FpsAvg = D(p[5]); r.FpsMax = D(p[6]); r.FpsMin = D(p[7]); r.Low1 = D(p[8]);
                r.CpuAvg = D(p[9]); r.CpuMax = D(p[10]);
                r.GpuAvg = D(p[11]); r.GpuMax = D(p[12]);
                r.GpuTempAvg = D(p[13]); r.GpuTempMax = D(p[14]);
                r.RamAvg = D(p[15]); r.RamMax = D(p[16]); r.VramMax = D(p[17]);
                if (p.Length > 18) r.Power = D(p[18]);
                if (p.Length > 19) r.FpsStep = I(p[19]);
                if (r.FpsStep <= 0) r.FpsStep = 4;
                if (p.Length > 20 && p[20].Length > 0)
                {
                    string[] parts = p[20].Split(',');
                    r.FpsSeries = new double[parts.Length];
                    for (int i = 0; i < parts.Length; i++) r.FpsSeries[i] = D(parts[i]);
                }
                if (p.Length > 21) r.ExePath = p[21];
                if (r.Title.Length == 0) r.Title = r.Exe;
                return r;
            }
            catch { return null; }
        }

        private static string Safe(string s)
        {
            if (s == null) return "";
            return s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
        }

        private static string N(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static int I(string s)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static double D(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }

    /// <summary>战绩存储:一行一局,放在 %LOCALAPPDATA%\FrameForge\sessions.dat。</summary>
    public sealed class SessionStore
    {
        public const int MaxRecords = 300;

        public static string FilePath { get { return Path.Combine(Settings.Dir, "sessions.dat"); } }

        private readonly List<SessionRecord> all = new List<SessionRecord>();
        public List<SessionRecord> All { get { return all; } }

        public void Load()
        {
            all.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    SessionRecord r = SessionRecord.Parse(line);
                    if (r != null) all.Add(r);
                }
            }
            catch (Exception ex) { Log.Write("sessions load: " + ex.Message); }
            Sort();
        }

        public void Add(SessionRecord r)
        {
            if (r == null) return;
            all.Insert(0, r);
            Sort();
            while (all.Count > MaxRecords) all.RemoveAt(all.Count - 1);
            Save();
        }

        public void Remove(string id)
        {
            for (int i = all.Count - 1; i >= 0; i--)
                if (all[i].Id == id) all.RemoveAt(i);
            Save();
        }

        public void Clear()
        {
            all.Clear();
            Save();
        }

        public SessionRecord Find(string id)
        {
            for (int i = 0; i < all.Count; i++) if (all[i].Id == id) return all[i];
            return null;
        }

        private void Sort()
        {
            all.Sort(delegate(SessionRecord a, SessionRecord b) { return b.Start.CompareTo(a.Start); });
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                var sb = new StringBuilder();
                sb.AppendLine("# FrameForge 战绩记录,一行一局,可直接删除");
                for (int i = 0; i < all.Count; i++) sb.AppendLine(all[i].Serialize());
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("sessions save: " + ex.Message); }
        }

        /// <summary>导出成 CSV,方便在 Excel 里回看。</summary>
        public void Export(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("game,exe,start,seconds,fps_avg,fps_max,fps_min,fps_1low,cpu_avg,cpu_max,gpu_avg,gpu_max,gpu_temp_avg,gpu_temp_max,ram_avg_mb,ram_max_mb,vram_max_mb,power_w,wh,co2_g,fps_series");
            for (int i = 0; i < all.Count; i++)
            {
                SessionRecord r = all[i];
                sb.Append(Csv(r.Title)).Append(',').Append(Csv(r.Exe)).Append(',')
                  .Append(r.Start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.Seconds).Append(',')
                  .Append(N(r.FpsAvg)).Append(',').Append(N(r.FpsMax)).Append(',').Append(N(r.FpsMin)).Append(',').Append(N(r.Low1)).Append(',')
                  .Append(N(r.CpuAvg)).Append(',').Append(N(r.CpuMax)).Append(',')
                  .Append(N(r.GpuAvg)).Append(',').Append(N(r.GpuMax)).Append(',')
                  .Append(N(r.GpuTempAvg)).Append(',').Append(N(r.GpuTempMax)).Append(',')
                  .Append(N(r.RamAvg)).Append(',').Append(N(r.RamMax)).Append(',').Append(N(r.VramMax)).Append(',')
                  .Append(N(r.Power)).Append(',').Append(N(r.Wh)).Append(',').Append(N(r.Co2)).Append(',')
                  .Append(Csv(SeriesText(r.FpsSeries)));
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string SeriesText(double[] series)
        {
            if (series == null || series.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < series.Length; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(series[i].ToString("0.#", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string N(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>把每秒采样攒成一局:检测到游戏开始计时,进程退出后完成记录并触发回调。</summary>
    public sealed class SessionTracker
    {
        private readonly SessionStore store;
        private int pid = -1;
        private string exe = "";
        private string title = "";
        private string exePath = "";
        private DateTime start;

        private double fpsSum, fpsMax, fpsMin;
        private int fpsN;
        private double lowSum;
        private int lowN;
        private double cpuSum, cpuMax, gpuSum, gpuMax;
        private int loadN;
        private double tempSum, tempMax;
        private int tempN;
        private double ramSum, ramMax, vramMax;
        private int ramN;
        private double powerSum;
        private int powerN;

        // 帧率曲线采样:每 SeriesStep 秒记一个点,最多 SeriesMax 个(约 20 分钟)。
        private const int SeriesStep = 4;
        private const int SeriesMax = 300;
        private readonly List<double> fpsSeries = new List<double>();
        private DateTime lastSeriesAt = DateTime.MinValue;
        // 最近一次确认"游戏还在"的时刻,给检测抖动留宽限。
        private DateTime lastSeenAt = DateTime.MinValue;

        public event Action<SessionRecord> Completed;

        /// <summary>短于该秒数的对局不记录。</summary>
        public int MinSeconds = 30;

        public SessionTracker(SessionStore store)
        {
            this.store = store;
        }

        public bool Active { get { return pid > 0; } }
        public string Title { get { return title.Length > 0 ? title : exe; } }
        public int GamePid { get { return pid; } }
        public TimeSpan Elapsed { get { return pid > 0 ? DateTime.Now - start : TimeSpan.Zero; } }

        public void Feed(SysSample s, double fps, double low1)
        {
            if (s == null) return;
            DateTime now = DateTime.Now;

            if (s.GamePid > 0)
            {
                lastSeenAt = now;
                if (pid != s.GamePid)
                {
                    Finish(now);
                    Begin(s, now);
                }
                else if (s.GameTitle.Length > 0)
                {
                    title = s.GameTitle;
                    exe = s.GameName.Length > 0 ? s.GameName : exe;
                }
            }
            else
            {
                // 切全屏、UAC 弹窗、启动器抢焦点都会让检测短暂掉成"没有游戏"。
                // 以前一掉就结算,一局会被拆成两段、帧率曲线也跟着重来;
                // 这里留 3 秒宽限,同一个进程回来就继续算同一局。
                if (pid > 0 && (now - lastSeenAt).TotalSeconds >= 3) Finish(now);
                return;
            }

            if (fps > 0)
            {
                fpsSum += fps;
                fpsN++;
                if (now - start > TimeSpan.FromSeconds(3))
                {
                    if (fps > fpsMax) fpsMax = fps;
                    if (fpsMin <= 0 || fps < fpsMin) fpsMin = fps;
                }
                if ((now - lastSeriesAt).TotalSeconds >= SeriesStep && fpsSeries.Count < SeriesMax)
                {
                    fpsSeries.Add(fps);
                    lastSeriesAt = now;
                }
            }
            if (low1 > 0) { lowSum += low1; lowN++; }

            if (s.Cpu >= 0) { cpuSum += s.Cpu; if (s.Cpu > cpuMax) cpuMax = s.Cpu; loadN++; }
            if (s.Gpu >= 0) { gpuSum += s.Gpu; if (s.Gpu > gpuMax) gpuMax = s.Gpu; }

            if (s.GpuTemp >= 0) { tempSum += s.GpuTemp; if (s.GpuTemp > tempMax) tempMax = s.GpuTemp; tempN++; }

            if (s.RamUsed > 0)
            {
                double mb = s.RamUsed / 1048576.0;
                ramSum += mb;
                if (mb > ramMax) ramMax = mb;
                ramN++;
            }
            double vram = s.VramUsed / 1048576.0;
            if (vram > vramMax) vramMax = vram;

            if (s.GpuPower > 0) { powerSum += s.GpuPower; powerN++; }
        }

        /// <summary>应用退出前把当前这局也存下来。</summary>
        public void Flush()
        {
            Finish(DateTime.Now);
        }

        private void Begin(SysSample s, DateTime now)
        {
            pid = s.GamePid;
            exe = s.GameName;
            title = s.GameTitle.Length > 0 ? s.GameTitle : GameDb.Title(pid, exe);
            // 进程还活着时先记下路径,退出后就能靠它读出游戏图标。
            exePath = GameDb.PathOf(pid);
            if (exePath.Length == 0 && exe.Length > 0) exePath = GameDb.PathFor(exe);
            start = now;
            lastSeenAt = now;
            fpsSum = fpsMax = fpsMin = 0; fpsN = 0;
            lowSum = 0; lowN = 0;
            cpuSum = cpuMax = gpuSum = gpuMax = 0; loadN = 0;
            tempSum = tempMax = 0; tempN = 0;
            ramSum = ramMax = vramMax = 0; ramN = 0;
            powerSum = 0; powerN = 0;
            fpsSeries.Clear();
            lastSeriesAt = now;
        }

        private void Finish(DateTime now)
        {
            if (pid <= 0) return;
            int sec = (int)Math.Round((now - start).TotalSeconds);
            int min = MinSeconds;

            var r = new SessionRecord();
            r.Id = start.Ticks.ToString(CultureInfo.InvariantCulture);
            r.Exe = exe;
            r.Title = title.Length > 0 ? title : GameDb.Title(exe);
            r.ExePath = exePath;
            r.Start = start;
            r.Seconds = sec;
            r.End = now;
            if (fpsN > 0) r.FpsAvg = fpsSum / fpsN;
            r.FpsMax = fpsMax;
            r.FpsMin = fpsMin;
            if (lowN > 0) r.Low1 = lowSum / lowN;
            if (loadN > 0) { r.CpuAvg = cpuSum / loadN; r.GpuAvg = gpuSum / loadN; }
            r.CpuMax = cpuMax;
            r.GpuMax = gpuMax;
            if (tempN > 0) r.GpuTempAvg = tempSum / tempN;
            r.GpuTempMax = tempMax;
            if (ramN > 0) r.RamAvg = ramSum / ramN;
            r.RamMax = ramMax;
            r.VramMax = vramMax;
            if (powerN > 0) r.Power = powerSum / powerN + 20 + 0.55 * r.CpuAvg + 25;
            else r.Power = 20 + 0.55 * r.CpuAvg + 60;
            if (fpsSeries.Count >= 2)
            {
                r.FpsSeries = fpsSeries.ToArray();
                r.FpsStep = SeriesStep;
            }

            pid = -1;
            exePath = "";
            Action<SessionRecord> cb = Completed;
            if (sec >= min)
            {
                if (store != null) store.Add(r);
                if (cb != null)
                {
                    try { cb(r); }
                    catch (Exception ex) { Log.Write("session complete: " + ex.Message); }
                }
            }
        }
    }
}