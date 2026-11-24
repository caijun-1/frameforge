using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    // SCTL v2（system_design.md §3.2/§3.3/§4/§6）：统一系统改动事务日志。
    // 关键不变量：status 只是提示，唯一真相是"系统当前值与 old/new 的比较"。
    // 所有写操作统一走 SafeFile.WriteAllText；还原统一走 Reconcile 三分支对账。

    public enum TxState
    {
        PREPARED = 0,
        APPLYING = 1,
        COMMITTED = 2,
        REVERTING = 3
    }

    public enum ActionKind
    {
        PRIORITY_CLASS = 0,
        IDEAL_CPU = 1,
        AFFINITY_MASK = 2,
        IRQ_AFFINITY = 3,
        POWER_SCHEME = 4,
        TIMER_RESOLUTION = 5,
        REG_DWORD = 6,
        SUSPEND_PROCESS = 7,
        MEMORY_TRIM = 8
    }

    public enum ActionStatus
    {
        PENDING = 0,
        APPLIED_CONFIRMED = 1,
        FAILED = 2,
        REVERTED = 3,
        CONFLICT_SKIPPED = 4
    }

    public enum ValueKind
    {
        NONE = 0,
        DWORD = 1,
        QWORD = 2,
        BINARY = 3,
        STRING = 4,
        MULTI_SZ = 5
    }

    /// <summary>进程类动作的 guard。name 是 ProcessName（不含 .exe），用于 PID 复用检测。</summary>
    public class ProcessGuard
    {
        public int Pid = -1;
        public string Name = "";
        public int SessionId = -1;
        public long StartTime;   // A-5：PID 复用双校验，0 表示未记录
        public int Tid = -1;     // IDEAL_CPU 专用：被引导的主线程 ID
    }

    /// <summary>事务里的一个可回滚动作。</summary>
    public class JournalAction
    {
        public string ActionId = "0";
        public ActionKind Kind = ActionKind.MEMORY_TRIM;
        public ActionStatus Status = ActionStatus.PENDING;
        public ProcessGuard Target;
        /// <summary>注册表/HKLM 全路径；非注册表类为空。</summary>
        public string Path = "";
        /// <summary>注册表值名；IRQ 动作在此放 "policy=..;hadPolicy=..;hadMask=.." 元信息。</summary>
        public string Name = "";
        public ValueKind KindOfValue = ValueKind.NONE;
        public string Old = "";
        public string New = "";
        public int NewClass = -1;
        public bool Existed;
        public bool CreatedKey;
        public bool RequiresLiveOwner;
        public bool Revertible = true;
        public string AppliedUtc = "";
        public string VerifiedUtc = "";
        public string RevertedUtc = "";
        public string Note = "";
        /// <summary>该动作归属的引擎；仅全局资源动作使用。</summary>
        public OwnerKind Owner = OwnerKind.ONE_SHOT;
    }

    public class JournalHost
    {
        public int AppPid;
        public int SessionId;
        public string BootId = "";
        public bool IsAdmin;
        public string ExePath = "";
    }

    public class JournalGame
    {
        public int Pid = -1;
        public string Name = "";
        public int SessionId = -1;
    }

    /// <summary>一次"一键加速"事务的完整落盘结构（schemaVersion=2）。</summary>
    public class Transaction
    {
        public int SchemaVersion = 2;
        public int MigratedFrom;
        public string TxId = "";
        public TxState State = TxState.PREPARED;
        public OwnerKind Owner = OwnerKind.ONE_SHOT;
        public string CreatedUtc = "";
        public string UpdatedUtc = "";
        public JournalHost Host = new JournalHost();
        public JournalGame Game = new JournalGame();
        public ResourceOwnership Res = new ResourceOwnership();
        public readonly List<JournalAction> Actions = new List<JournalAction>();
        public string Checksum = "";
    }

    public class ReconcileContext
    {
        /// <summary>true = 同进程正常还原（BoostEngine.Restore）；false = 看门狗/启动自愈/--restore。</summary>
        public bool IsSameProcess;
        public bool IsAdmin;
    }

    public class ReconcileReport
    {
        public int Reverted;
        public int Noop;
        public int Conflict;
        public int Failed;
        /// <summary>未决项（FAILED + CONFLICT_SKIPPED）为 0 才允许删日志。</summary>
        public bool Clean { get { return Conflict == 0 && Failed == 0; } }
    }

    public static class BoostJournal
    {
        private const string RecoveryMutexName = "Global\\FrameForge.Recovery";

        public static string FilePath
        {
            get { return FilePathFor(OwnerKind.ONE_SHOT); }
        }

        public static string FilePathFor(OwnerKind owner)
        {
            return Path.Combine(Settings.Dir, owner == OwnerKind.REALTIME ? "boost.realtime.journal" : "boost.journal");
        }

        public static bool Pending
        {
            get
            {
                try { return File.Exists(FilePathFor(OwnerKind.ONE_SHOT)) || File.Exists(FilePathFor(OwnerKind.REALTIME)); }
                catch { return false; }
            }
        }

        /// <summary>系统启动标识：TickCount 在同一开机周期内稳定可用（§3.2.1 bootId）。</summary>
        public static string CurrentBootId()
        {
            return Environment.TickCount.ToString(CultureInfo.InvariantCulture);
        }

        private static string NowUtc()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        /// <summary>新建事务：立即捕获 host/game/resourceOwnership 快照。</summary>
        public static Transaction NewTransaction(OwnerKind owner, int gamePid, string gameName, int gameSid)
        {
            var tx = new Transaction();
            tx.TxId = Guid.NewGuid().ToString("N");
            tx.State = TxState.PREPARED;
            tx.Owner = owner;
            tx.CreatedUtc = NowUtc();
            tx.UpdatedUtc = tx.CreatedUtc;
            tx.Host.AppPid = Process.GetCurrentProcess().Id;
            tx.Host.SessionId = Process.GetCurrentProcess().SessionId;
            tx.Host.BootId = CurrentBootId();
            tx.Host.IsAdmin = MemEngine.IsAdmin;
            try { tx.Host.ExePath = Process.GetCurrentProcess().MainModule.FileName; }
            catch { tx.Host.ExePath = ""; }
            tx.Game.Pid = gamePid;
            tx.Game.Name = gameName ?? "";
            tx.Game.SessionId = gameSid;
            tx.Res = SystemResourceArbiter.Instance.Snapshot();
            return tx;
        }

        public static JournalAction NewAction(Transaction tx, ActionKind kind, ProcessGuard target,
            string oldValue, string newValue)
        {
            var a = new JournalAction();
            a.ActionId = tx.Actions.Count.ToString(CultureInfo.InvariantCulture);
            a.Kind = kind;
            a.Target = target;
            a.Old = oldValue ?? "";
            a.New = newValue ?? "";
            tx.Actions.Add(a);
            return a;
        }

        public static ProcessGuard MakeGuard(int pid, string name, int sid)
        {
            var g = new ProcessGuard();
            g.Pid = pid;
            g.Name = Sanitize(name ?? "");
            g.SessionId = sid;
            try
            {
                if (pid > 0)
                    using (Process p = Process.GetProcessById(pid)) g.StartTime = p.StartTime.Ticks;
            }
            catch { g.StartTime = 0; }
            return g;
        }

        // =================================================================
        //  落盘 / 解析
        // =================================================================

        /// <summary>v2 key=value 文本序列化。调用方在保存前把 state/updated 设好。</summary>
        public static string Serialize(Transaction tx)
        {
            if (tx == null) return "";
            tx.UpdatedUtc = NowUtc();
            var sb = new StringBuilder();
            sb.Append("v=2").Append('\n');
            sb.Append("tx=").Append(Sanitize(tx.TxId)).Append("|state=").Append(tx.State.ToString())
              .Append("|owner=").Append(tx.Owner.ToString())
              .Append("|created=").Append(Sanitize(tx.CreatedUtc))
              .Append("|updated=").Append(Sanitize(tx.UpdatedUtc)).Append('\n');
            sb.Append("host=").Append(tx.Host.AppPid.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(tx.Host.SessionId.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(Sanitize(tx.Host.BootId)).Append('|')
              .Append(tx.Host.IsAdmin ? "1" : "0").Append('|')
              .Append(Sanitize(tx.Host.ExePath)).Append('\n');
            sb.Append("game=").Append(tx.Game.Pid.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(Sanitize(tx.Game.Name)).Append('|')
              .Append(tx.Game.SessionId.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (tx.MigratedFrom > 0) sb.Append("migrated=").Append(tx.MigratedFrom).Append('\n');
            sb.Append("res=pw:").Append(tx.Res.Power.ToString()).Append(",tm:").Append(tx.Res.Timer.ToString()).Append('\n');

            for (int i = 0; i < tx.Actions.Count; i++)
            {
                JournalAction a = tx.Actions[i];
                ProcessGuard g = a.Target ?? new ProcessGuard();
                bool hasPath = a.Kind == ActionKind.REG_DWORD || a.Kind == ActionKind.IRQ_AFFINITY;
                string f3 = hasPath ? a.Path : g.Pid.ToString(CultureInfo.InvariantCulture);
                string f4 = hasPath ? a.Name : g.Name;
                string f5 = hasPath ? (a.Existed ? "1" : "0") : g.SessionId.ToString(CultureInfo.InvariantCulture);
                sb.Append("a=").Append(a.ActionId).Append('|')
                  .Append(a.Kind.ToString()).Append('|')
                  .Append(a.Status.ToString()).Append('|')
                  .Append(Sanitize(f3)).Append('|')
                  .Append(Sanitize(f4)).Append('|')
                  .Append(Sanitize(f5)).Append('|')
                  .Append(Sanitize(a.Old)).Append('|')
                  .Append(Sanitize(a.New)).Append('|')
                  .Append(BuildFlags(a)).Append('|')
                  .Append(EncodeMeta(a)).Append('\n');
            }
            string body = sb.ToString();
            tx.Checksum = Fnv1a(body);
            // 日志放在便携目录,普通用户可改;FNV 只能防手滑不能防伪造。
            // 用管理员侧密钥再做一次 HMAC,阻止伪造事务在下次提权启动时被"还原"。
            // 两个摘要写在同一行(checksum:hmac),加载时共用同一份 body,避免重建偏差。
            byte[] key = MacKey();
            sb.Append("sum=").Append(tx.Checksum);
            if (key != null) sb.Append(':').Append(Hmac(key, body));
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>零依赖 FNV-1a 32，返回 8 位小写十六进制。selftest 直接用它验往返。</summary>
        public static string Fnv1a(string text)
        {
            uint hash = 2166136261u;
            if (text == null) text = "";
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                // 高字节先入（UTF-16 低 8 位在前会与 ASCII 行为一致，这里按字节序显式处理）。
                hash ^= (uint)(c >> 8);
                hash *= 16777619u;
                hash ^= (uint)(c & 0xFF);
                hash *= 16777619u;
            }
            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }

        private static byte[] macKey;
        private static bool macTried;
        private static bool macExisted;

        /// <summary>
        /// 日志 HMAC 密钥:首次在 %ProgramData%\FrameForge 下生成(普通用户无写权限),
        /// 之后只读不改。拿不到时返回 null,退回纯 FNV 校验,不阻断自检与受限场景。
        /// </summary>
        private static byte[] MacKey()
        {
            if (macTried) return macKey;
            macTried = true;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FrameForge");
                string file = Path.Combine(dir, "journal.key");
                if (File.Exists(file))
                {
                    byte[] k = File.ReadAllBytes(file);
                    if (k.Length == 32) { macExisted = true; macKey = k; return macKey; }
                }
                byte[] rnd = new byte[32];
                using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(rnd);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(file, rnd);
                macKey = rnd;
            }
            catch (Exception ex) { Log.Write("journal key: " + ex.Message); macKey = null; }
            return macKey;
        }

        private static string Hmac(byte[] key, string text)
        {
            using (var h = new HMACSHA256(key))
                return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(text ?? "")));
        }

        private static bool MacEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>改动前/确认点/提交点统一原子落盘。</summary>
        public static void Save(Transaction tx)
        {
            if (tx == null) return;
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                SafeFile.WriteAllText(FilePathFor(tx.Owner), Serialize(tx), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("journal save: " + ex.Message); }
        }

        /// <summary>v1 兼容 + 未知版本/损坏隔离；返回 null 表示不可安全还原。</summary>
        public static Transaction Load()
        {
            return Load(OwnerKind.ONE_SHOT);
        }

        public static Transaction Load(OwnerKind owner)
        {
            string path = FilePathFor(owner);
            try
            {
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path, Encoding.UTF8);
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                string version = "";
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("v=", StringComparison.Ordinal))
                    {
                        version = lines[i].Substring(2).Trim();
                        break;
                    }
                }
                if (version.Length == 0) return ParseV1(lines);   // §6.1：缺 v 按 v1 容错
                if (version == "1") return ParseV1(lines);
                if (version != "2")
                {
                    Quarantine(owner, "unknown", "unknown schema v=" + version);
                    return null;
                }

                // checksum：对 sum= 行之前的全部内容重新摘要。
                int sumAt = -1;
                string sum = "";
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    if (lines[i].StartsWith("sum=", StringComparison.Ordinal))
                    {
                        sumAt = i;
                        sum = lines[i].Substring(4).Trim();
                        break;
                    }
                }
                if (sumAt < 0) { Quarantine(owner, "bad", "missing checksum"); return null; }
                string fnv = sum;
                string mac = null;
                int colon = sum.IndexOf(':');
                if (colon > 0)
                {
                    fnv = sum.Substring(0, colon);
                    mac = sum.Substring(colon + 1);
                }
                var body = new StringBuilder();
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i == sumAt) continue;
                    // Serialize 写出的文本以单个 '\n' 结尾，Split('\n') 会因此在末尾
                    // 多造一个空串。重建被摘要的 body 时必须跳过它，否则会多算一个
                    // '\n'，导致 checksum 与 Serialize 时不一致而被误判为损坏。
                    if (i == lines.Length - 1 && lines[i].Length == 0) continue;
                    body.Append(lines[i]).Append('\n');
                }
                if (!string.Equals(Fnv1a(body.ToString()), fnv, StringComparison.OrdinalIgnoreCase))
                {
                    Quarantine(owner, "bad", "checksum mismatch");
                    return null;
                }

                // HMAC：密钥文件已存在时,缺失或对不上都视为伪造(防止删掉 mac 降级)。
                if (mac != null)
                {
                    byte[] key = MacKey();
                    if (key != null && !MacEquals(Hmac(key, body.ToString()), mac))
                    {
                        Quarantine(owner, "bad", "mac mismatch");
                        return null;
                    }
                }
                else
                {
                    byte[] key = MacKey();
                    if (key != null && macExisted)
                    {
                        Quarantine(owner, "bad", "missing mac");
                        return null;
                    }
                }

                Transaction tx = ParseV2(lines);
                if (tx != null) tx.Checksum = fnv;
                else Quarantine(owner, "bad", "parse failed");
                return tx;
            }
            catch (Exception ex)
            {
                Log.Write("journal load: " + ex.Message);
                Quarantine(owner, "bad", "exception: " + ex.Message);
                return null;
            }
        }

        private static Transaction ParseV2(string[] lines)
        {
            var tx = new Transaction();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0 || line.StartsWith("v=", StringComparison.Ordinal)
                    || line.StartsWith("sum=", StringComparison.Ordinal)) continue;

                if (line.StartsWith("tx=", StringComparison.Ordinal))
                {
                    string[] p = line.Split('|');
                    for (int k = 0; k < p.Length; k++)
                    {
                        if (p[k].StartsWith("tx=", StringComparison.Ordinal)) tx.TxId = p[k].Substring(3);
                        else if (p[k].StartsWith("state=", StringComparison.Ordinal))
                            tx.State = (TxState)Enum.Parse(typeof(TxState), p[k].Substring(6), true);
                        else if (p[k].StartsWith("owner=", StringComparison.Ordinal))
                            tx.Owner = ParseOwner(p[k].Substring(6));
                        else if (p[k].StartsWith("created=", StringComparison.Ordinal)) tx.CreatedUtc = p[k].Substring(8);
                        else if (p[k].StartsWith("updated=", StringComparison.Ordinal)) tx.UpdatedUtc = p[k].Substring(8);
                    }
                }
                else if (line.StartsWith("host=", StringComparison.Ordinal))
                {
                    string[] p = line.Substring(5).Split('|');
                    if (p.Length < 5) throw new FormatException("bad host line");
                    tx.Host.AppPid = Int(p[0]);
                    tx.Host.SessionId = Int(p[1]);
                    tx.Host.BootId = p[2];
                    tx.Host.IsAdmin = p[3] == "1";
                    tx.Host.ExePath = p[4];
                }
                else if (line.StartsWith("game=", StringComparison.Ordinal))
                {
                    string[] p = line.Substring(5).Split('|');
                    if (p.Length < 3) throw new FormatException("bad game line");
                    tx.Game.Pid = Int(p[0]);
                    tx.Game.Name = p[1];
                    tx.Game.SessionId = Int(p[2]);
                }
                else if (line.StartsWith("migrated=", StringComparison.Ordinal))
                {
                    tx.MigratedFrom = Int(line.Substring(9));
                }
                else if (line.StartsWith("res=", StringComparison.Ordinal))
                {
                    string body = line.Substring(4);
                    string[] p = body.Split(',');
                    for (int k = 0; k < p.Length; k++)
                    {
                        if (p[k].StartsWith("pw:", StringComparison.Ordinal)) tx.Res.Power = ParseOwner(p[k].Substring(3));
                        else if (p[k].StartsWith("tm:", StringComparison.Ordinal)) tx.Res.Timer = ParseOwner(p[k].Substring(3));
                    }
                }
                else if (line.StartsWith("a=", StringComparison.Ordinal))
                {
                    JournalAction a = ParseAction(line.Substring(2));
                    if (a != null)
                    {
                        // 早期 realtime v2 的 action 默认写成 ONE_SHOT，按事务 owner 兼容修正。
                        if (tx.Owner == OwnerKind.REALTIME) a.Owner = OwnerKind.REALTIME;
                        tx.Actions.Add(a);
                    }
                }
            }
            if (tx.TxId.Length == 0 || tx.Actions.Count == 0) throw new FormatException("empty transaction");
            return tx;
        }

        private static JournalAction ParseAction(string body)
        {
            string[] p = body.Split('|');
            if (p.Length < 9) throw new FormatException("bad action line");
            var a = new JournalAction();
            a.ActionId = p[0];
            a.Kind = (ActionKind)Enum.Parse(typeof(ActionKind), p[1], true);
            a.Status = (ActionStatus)Enum.Parse(typeof(ActionStatus), p[2], true);
            a.Old = p[6];
            a.New = p[7];
            if (p.Length > 9) DecodeMeta(a, p[9]);

            bool hasPath = a.Kind == ActionKind.REG_DWORD || a.Kind == ActionKind.IRQ_AFFINITY;
            if (hasPath)
            {
                // 兼容早期 v2 错写的 0|path|name 布局；新格式为 path|name|existed。
                if (p[3] == "0" && p[4].Length > 0)
                {
                    a.Path = p[4];
                    a.Name = p[5];
                }
                else
                {
                    a.Path = p[3];
                    a.Name = p[4];
                }
                // Existed 以 flags 的 ex 为准，兼容上述两种布局。
            }
            else
            {
                var g = new ProcessGuard();
                g.Pid = Int(p[3]);
                g.Name = p[4];
                g.SessionId = Int(p[5]);
                a.Target = g;
            }
            ApplyFlags(a, p[8]);
            return a;
        }

        /// <summary>v1 → v2 内存迁移：只构造 POWER_SCHEME + REG_DWORD，不虚构其它 7 类。</summary>
        private static Transaction ParseV1(string[] lines)
        {
            var tx = new Transaction();
            tx.SchemaVersion = 2;
            tx.MigratedFrom = 1;
            tx.TxId = Guid.NewGuid().ToString("N");
            tx.State = TxState.COMMITTED;
            tx.Owner = OwnerKind.ONE_SHOT;
            tx.CreatedUtc = NowUtc();
            tx.UpdatedUtc = tx.CreatedUtc;
            tx.Host.AppPid = Process.GetCurrentProcess().Id;
            tx.Host.SessionId = Process.GetCurrentProcess().SessionId;
            tx.Host.BootId = CurrentBootId();
            tx.Host.IsAdmin = MemEngine.IsAdmin;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("game=", StringComparison.Ordinal))
                {
                    string[] p = line.Substring(5).Split('|');
                    if (p.Length >= 2)
                    {
                        tx.Game.Pid = Int(p[0]);
                        tx.Game.Name = p[1];
                        if (p.Length >= 3) tx.Game.SessionId = Int(p[2]);
                    }
                }
                else if (line.StartsWith("scheme=", StringComparison.Ordinal))
                {
                    string guid = line.Substring(7).Trim();
                    if (guid.Length > 0)
                    {
                        JournalAction a = NewAction(tx, ActionKind.POWER_SCHEME, new ProcessGuard(), guid,
                            BoostEngine.SchemeHigh);
                        a.Status = ActionStatus.APPLIED_CONFIRMED;
                        a.Owner = OwnerKind.ONE_SHOT;
                        a.AppliedUtc = tx.CreatedUtc;
                    }
                }
                else if (line.StartsWith("reg=", StringComparison.Ordinal))
                {
                    string[] p = line.Substring(4).Split('|');
                    if (p.Length < 4) continue;
                    JournalAction a = NewAction(tx, ActionKind.REG_DWORD, new ProcessGuard(),
                        Base64Int(Int(p[3])), Base64Int(Int(p[3])));
                    a.Path = p[0];
                    a.Name = p[1];
                    a.Existed = p[2] == "1";
                    a.KindOfValue = ValueKind.DWORD;
                    a.Status = ActionStatus.APPLIED_CONFIRMED;
                    a.AppliedUtc = tx.CreatedUtc;
                    // v1 没有"写入值"记录：old==new 表示只按 current==new 分支还原到原 DWORD。
                }
            }
            if (tx.Actions.Count == 0) throw new FormatException("empty v1 journal");
            return tx;
        }

        private static void Quarantine(OwnerKind owner, string tag, string reason)
        {
            string path = FilePathFor(owner);
            try
            {
                if (!File.Exists(path)) return;
                string ts = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                string dst = path + "." + tag + "." + ts;
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(path, dst);
                Log.Write("journal quarantined as " + Path.GetFileName(dst) + ": " + reason);
            }
            catch (Exception ex) { Log.Write("journal quarantine: " + ex.Message); }
        }

        public static void Clear()
        {
            Clear(OwnerKind.ONE_SHOT);
        }

        public static void Clear(OwnerKind owner)
        {
            string path = FilePathFor(owner);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // =================================================================
        //  统一还原核心：Reconcile 三分支对账（§4.3/§4.5）
        // =================================================================

        /// <summary>
        /// 所有还原入口（正常退出、启动自愈、看门狗、--restore）共用的唯一核心。
        /// 命名 Mutex 串行化，避免用户立刻重启时两边并发还原。
        /// </summary>
        public static ReconcileReport Reconcile(Transaction tx, ReconcileContext ctx)
        {
            var report = new ReconcileReport();
            if (tx == null || ctx == null) return report;
            Mutex m = null;
            bool owned = false;
            try
            {
                try { m = new Mutex(false, RecoveryMutexName); }
                catch (Exception ex) { Log.Write("recovery mutex: " + ex.Message); }
                if (m != null)
                {
                    try { owned = m.WaitOne(15000); }
                    catch (AbandonedMutexException) { owned = true; }
                    catch (Exception ex) { Log.Write("recovery mutex wait: " + ex.Message); }
                }
                if (m == null || !owned)
                {
                    // 没拿到跨进程恢复锁就不能安全写系统；保留日志，下次继续。
                    report.Failed = 1;
                    Log.Write("reconcile deferred: recovery mutex unavailable");
                    return report;
                }
                DoReconcile(tx, ctx, report);
            }
            finally
            {
                if (owned && m != null)
                {
                    try { m.ReleaseMutex(); } catch { }
                }
                if (m != null) { try { m.Dispose(); } catch { } }
            }
            return report;
        }

        private static void DoReconcile(Transaction tx, ReconcileContext ctx, ReconcileReport report)
        {
            try { tx.State = TxState.REVERTING; } catch { }
            // 逆 actionId 还原（后进先出）。
            for (int i = tx.Actions.Count - 1; i >= 0; i--)
            {
                JournalAction a = tx.Actions[i];
                if (a.Status == ActionStatus.REVERTED) continue;
                try
                {
                    ReconcileOne(a, tx, ctx, report);
                }
                catch (Exception ex)
                {
                    a.Status = ActionStatus.FAILED;
                    a.Note = Sanitize(ex.Message);
                    report.Failed++;
                    Log.Write("reconcile " + a.Kind + ": " + ex.Message);
                }
            }
            tx.UpdatedUtc = NowUtc();
            if (report.Clean)
            {
                // 只有未决项为 0 才删日志（修正原 BoostJournal 的无条件 Clear）。
                Clear(tx.Owner);
            }
            else
            {
                // 保留 journal 下次续做：CONFLICT_SKIPPED/FAILED 项下次会被重试。
                Save(tx);
                Log.Write("reconcile incomplete: reverted=" + report.Reverted + " noop=" + report.Noop
                    + " conflict=" + report.Conflict + " failed=" + report.Failed);
            }
        }

        private static void ReconcileOne(JournalAction a, Transaction tx, ReconcileContext ctx,
            ReconcileReport report)
        {
            switch (a.Kind)
            {
                case ActionKind.PRIORITY_CLASS:
                    ReconcilePriority(a, ctx, report);
                    return;
                case ActionKind.IDEAL_CPU:
                    ReconcileIdeal(a, ctx, report);
                    return;
                case ActionKind.AFFINITY_MASK:
                    ReconcileAffinity(a, report);
                    return;
                case ActionKind.IRQ_AFFINITY:
                    ReconcileIrq(a, ctx, report);
                    return;
                case ActionKind.POWER_SCHEME:
                    ReconcilePower(a, tx, ctx, report);
                    return;
                case ActionKind.TIMER_RESOLUTION:
                    ReconcileTimer(a, ctx, report);
                    return;
                case ActionKind.REG_DWORD:
                    ReconcileReg(a, report, tx.MigratedFrom > 0);
                    return;
                case ActionKind.SUSPEND_PROCESS:
                    ReconcileSuspend(a, report);
                    return;
                default:
                    // MEMORY_TRIM 物理不可逆，仅审计标记。
                    a.Status = ActionStatus.REVERTED;
                    a.RevertedUtc = NowUtc();
                    a.Note = "audit-only: memory trim is not revertible";
                    report.Noop++;
                    return;
            }
        }

        private static void ReconcilePriority(JournalAction a, ReconcileContext ctx, ReconcileReport report)
        {
            ProcessGuard g = a.Target;
            IntPtr h = OpenGuard(g, Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION);
            if (h == IntPtr.Zero) { OpenFailed(a, report); return; }
            try
            {
                uint cur = Native.GetPriorityClass(h);
                uint oldValue = (uint)UInt(a.Old, (uint)Native.NORMAL_PRIORITY_CLASS);
                uint newValue = (uint)UInt(a.New, (uint)Native.HIGH_PRIORITY_CLASS);
                if (cur == oldValue) { Noop(a, report, "current==old"); return; }
                if (cur != newValue)
                {
                    Conflict(a, report, "current priority changed by other party");
                    return;
                }
                if (!Native.SetPriorityClass(h, oldValue))
                {
                    a.Status = ActionStatus.FAILED;
                    a.Note = "setpriority failed";
                    report.Failed++;
                    return;
                }
                Done(a, report);
            }
            finally { Native.CloseHandle(h); }
        }

        private static void ReconcileIdeal(JournalAction a, ReconcileContext ctx, ReconcileReport report)
        {
            // IdealProcessor 存在游戏线程上，FrameForge 退出并不会自动清除；崩溃恢复同样要还原。
            ProcessGuard g = a.Target;
            IntPtr h = OpenGuard(g, Native.PROCESS_QUERY_LIMITED_INFORMATION);
            if (h == IntPtr.Zero) { OpenFailed(a, report); return; }
            Native.CloseHandle(h);

            int tid = g.Tid > 0 ? g.Tid : BoostEngine.MainThreadIdFor(g.Pid);
            if (tid <= 0) { Fail(a, report, "ideal target thread unavailable"); return; }
            IntPtr thread = Native.OpenThread(Native.THREAD_SET_INFORMATION, false, tid);
            if (thread == IntPtr.Zero) { Fail(a, report, "ideal thread access denied"); return; }
            try
            {
                uint oldValue = UInt(a.Old, Native.MAXIMUM_PROCESSORS);
                uint newValue = UInt(a.New, Native.MAXIMUM_PROCESSORS);
                // 返回值是修改前的 ideal，借此取得 current；随后必须按三分支写回正确值。
                uint current = Native.SetThreadIdealProcessor(thread, Native.MAXIMUM_PROCESSORS);
                if (current == 0xFFFFFFFF) { Fail(a, report, "ideal probe refused"); return; }
                if (current == oldValue)
                {
                    if (oldValue == Native.MAXIMUM_PROCESSORS
                        || Native.SetThreadIdealProcessor(thread, oldValue) != 0xFFFFFFFF)
                        Done(a, report);
                    else Fail(a, report, "ideal old restore refused");
                    return;
                }
                if (current == newValue)
                {
                    if (Native.SetThreadIdealProcessor(thread, oldValue) != 0xFFFFFFFF) Done(a, report);
                    else Fail(a, report, "ideal restore refused");
                    return;
                }

                // 第三方在加速后又改了 ideal：立即复原探测前的值并保留冲突。
                if (Native.SetThreadIdealProcessor(thread, current) == 0xFFFFFFFF)
                    Fail(a, report, "ideal probe rollback failed");
                else Conflict(a, report, "ideal changed by other party");
            }
            finally { Native.CloseHandle(thread); }
        }

        private static void ReconcileAffinity(JournalAction a, ReconcileReport report)
        {
            ProcessGuard g = a.Target;
            IntPtr h = OpenGuard(g, Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_INFORMATION);
            if (h == IntPtr.Zero) { OpenFailed(a, report); return; }
            try
            {
                IntPtr pm, sm;
                if (!Native.GetProcessAffinityMask(h, out pm, out sm))
                { Conflict(a, report, "affinity unreadable"); return; }
                long cur = pm.ToInt64();
                long oldValue = Hex(a.Old);
                long newValue = Hex(a.New);
                if (cur == oldValue) { Noop(a, report, "current==old"); return; }
                if (cur != newValue) { Conflict(a, report, "current affinity changed"); return; }
                if (!Native.SetProcessAffinityMask(h, new IntPtr(oldValue)))
                { a.Status = ActionStatus.FAILED; a.Note = "setaffinity failed"; report.Failed++; return; }
                Done(a, report);
            }
            finally { Native.CloseHandle(h); }
        }

        private static void ReconcileIrq(JournalAction a, ReconcileContext ctx, ReconcileReport report)
        {
            // HKLM 中断项需与写入时同完整性级别；非提权启动一律跳过并告警。
            if (!ctx.IsAdmin) { Conflict(a, report, "admin required for HKLM IRQ restore"); return; }
            if (a.Path.Length == 0) { Noop(a, report, "no registry path"); return; }

            byte[] oldMask = FromBase64(a.Old);
            byte[] newMask = FromBase64(a.New);
            int policy = -1;
            bool hadPolicy = false, hadMask = false;
            string[] meta = a.Name.Split(';');
            for (int i = 0; i < meta.Length; i++)
            {
                if (meta[i].StartsWith("policy=", StringComparison.Ordinal))
                { policy = Int(meta[i].Substring(7)); hadPolicy = policy >= 0; }
                else if (meta[i].StartsWith("hadMask=", StringComparison.Ordinal)) hadMask = meta[i].Substring(8) == "1";
            }

            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(a.Path, true))
                {
                    if (k == null)
                    {
                        if (a.CreatedKey) { Noop(a, report, "created key already gone"); return; }
                        Conflict(a, report, "IRQ key missing");
                        return;
                    }
                    byte[] cur = k.GetValue("AssignmentSetOverride") as byte[];
                    object policyObj = k.GetValue("DevicePolicy", null);
                    bool hasPolicy = policyObj != null;
                    int curPolicy = hasPolicy ? Convert.ToInt32(policyObj, CultureInfo.InvariantCulture) : -1;

                    bool oldPolicyMatches = hadPolicy ? hasPolicy && curPolicy == policy : !hasPolicy;
                    bool oldMaskMatches = hadMask ? cur != null && SameBytes(cur, oldMask) : cur == null;
                    if (oldPolicyMatches && oldMaskMatches) { Noop(a, report, "current==old"); return; }

                    // 只有 policy 与 mask 都仍是我们写入的值，才允许覆盖回原值。
                    if (!hasPolicy || curPolicy != 4 || cur == null || !SameBytes(cur, newMask))
                    { Conflict(a, report, "IRQ settings changed by other party"); return; }
                    if (hadMask) k.SetValue("AssignmentSetOverride", oldMask, RegistryValueKind.Binary);
                    else k.DeleteValue("AssignmentSetOverride", false);
                    if (hadPolicy && policy >= 0) k.SetValue("DevicePolicy", policy, RegistryValueKind.DWord);
                    else k.DeleteValue("DevicePolicy", false);
                }
                if (a.CreatedKey)
                {
                    try { Registry.LocalMachine.DeleteSubKeyTree(a.Path, false); } catch { }
                }
                Done(a, report);
            }
            catch (Exception ex)
            {
                a.Status = ActionStatus.FAILED;
                a.Note = Sanitize(ex.Message);
                report.Failed++;
            }
        }

        private static void ReconcilePower(JournalAction a, Transaction tx, ReconcileContext ctx,
            ReconcileReport report)
        {
            // 仲裁者仍有 live holder（同进程内另一引擎还在用）：绝不改回去。
            // 跨进程看门狗/启动时仲裁状态为空，此检查天然不影响崩溃恢复。
            OwnerKind live = SystemResourceArbiter.Instance.CurrentHolder(ResourceKind.PowerScheme);
            if (live != OwnerKind.NONE)
            { Noop(a, report, "power still held by live owner " + live); return; }
            // 单一恢复权：只有 holder 是自己（或无人持有）时才还原，避免双引擎互覆。
            if (tx.Res.Power != OwnerKind.NONE && tx.Res.Power != a.Owner)
            { Noop(a, report, "power held by " + tx.Res.Power); return; }
            string cur = BoostEngine.ActiveSchemeGuid();
            if (cur.Length == 0) { Conflict(a, report, "power scheme unreadable"); return; }
            if (string.Equals(cur, a.Old, StringComparison.OrdinalIgnoreCase))
            { Noop(a, report, "current==old"); return; }
            if (!string.Equals(cur, a.New, StringComparison.OrdinalIgnoreCase))
            { Conflict(a, report, "power scheme changed by user"); return; }
            if (!BoostEngine.SetScheme(a.Old))
            { a.Status = ActionStatus.FAILED; a.Note = "SetScheme failed"; report.Failed++; return; }
            Done(a, report);
        }

        private static void ReconcileTimer(JournalAction a, ReconcileContext ctx, ReconcileReport report)
        {
            // timeBeginPeriod 是进程级资源：崩溃由 OS 清理，正常停止由 Arbiter 的最后持有者 End。
            // 对账仅凭 holder==NONE 无法判断是否已 End，绝不能在这里再次调用 timeEndPeriod。
            if (!ctx.IsSameProcess) { Noop(a, report, "cross-process: timer auto-released by OS"); return; }
            if (SystemResourceArbiter.Instance.CurrentHolder(ResourceKind.TimerResolution) != OwnerKind.NONE)
            { Noop(a, report, "timer still held by live owner"); return; }
            Noop(a, report, "timer already released by arbiter or not applied");
        }

        private static void ReconcileReg(JournalAction a, ReconcileReport report, bool migratedV1)
        {
            if (a.Path.Length == 0 || a.Name.Length == 0) { Noop(a, report, "no reg target"); return; }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(a.Path, true))
                {
                    object curObj = k == null ? null : k.GetValue(a.Name, null);
                    if (curObj == null)
                    {
                        // 原值不存在时，"现在也不存在"就是已还原，不能与整数 0 混为一谈。
                        if (!a.Existed) { Noop(a, report, "missing value matches missing original"); return; }
                        Conflict(a, report, "original reg value is missing");
                        return;
                    }

                    int cur = Convert.ToInt32(curObj, CultureInfo.InvariantCulture);
                    int oldValue = FromBase64Int(a.Old);
                    int newValue = FromBase64Int(a.New);
                    bool legacyV1 = migratedV1 && string.Equals(a.Old, a.New, StringComparison.Ordinal);

                    if (!a.Existed)
                    {
                        // old==new==0 也可能是"原值缺失 → 写入 0"，仍应删除新建值。
                        if (!legacyV1 && cur != newValue) { Conflict(a, report, "reg value changed"); return; }
                        k.DeleteValue(a.Name, false);
                        Done(a, report);
                        return;
                    }

                    if (cur == oldValue) { Noop(a, report, "current==old"); return; }
                    // v1 迁移项没有记录"我们写入的值"，old==new；此时 current!=old 即视为我们造成。
                    if (!legacyV1 && cur != newValue) { Conflict(a, report, "reg value changed"); return; }
                    k.SetValue(a.Name, oldValue, RegistryValueKind.DWord);
                }
                Done(a, report);
            }
            catch (Exception ex)
            {
                a.Status = ActionStatus.FAILED;
                a.Note = Sanitize(ex.Message);
                report.Failed++;
            }
        }

        private static void ReconcileSuspend(JournalAction a, ReconcileReport report)
        {
            // §3.3：不输血游戏存活门控。进程存活且 name 匹配就 resume。
            ProcessGuard g = a.Target;
            IntPtr h = OpenGuard(g, Native.PROCESS_SUSPEND_RESUME);
            if (h == IntPtr.Zero) { OpenFailed(a, report); return; }
            try
            {
                int st = Native.NtResumeProcess(h);
                if (st == 0) Done(a, report);
                else { a.Status = ActionStatus.FAILED; a.Note = "NtResumeProcess=" + st; report.Failed++; }
            }
            finally { Native.CloseHandle(h); }
        }

        /// <summary>快速路径已经完成不可逆状态切换时，先标记再对账，避免重复 End/Restore。</summary>
        public static void MarkReverted(Transaction tx, ActionKind kind)
        {
            if (tx == null) return;
            string now = NowUtc();
            for (int i = 0; i < tx.Actions.Count; i++)
            {
                JournalAction a = tx.Actions[i];
                if (a.Kind != kind || a.Status == ActionStatus.REVERTED) continue;
                a.Status = ActionStatus.REVERTED;
                a.RevertedUtc = now;
            }
        }

        private static void Done(JournalAction a, ReconcileReport report)
        {
            a.Status = ActionStatus.REVERTED;
            a.RevertedUtc = NowUtc();
            report.Reverted++;
        }

        private static void Noop(JournalAction a, ReconcileReport report, string note)
        {
            a.Status = ActionStatus.REVERTED;
            a.RevertedUtc = NowUtc();
            a.Note = Sanitize(note);
            report.Noop++;
        }

        private static void Fail(JournalAction a, ReconcileReport report, string note)
        {
            a.Status = ActionStatus.FAILED;
            a.Note = Sanitize(note);
            report.Failed++;
            Log.Write("reconcile failed " + a.Kind + ": " + note);
        }

        private static void Conflict(JournalAction a, ReconcileReport report, string note)
        {
            a.Status = ActionStatus.CONFLICT_SKIPPED;
            a.Note = Sanitize(note);
            report.Conflict++;
            Log.Write("reconcile conflict " + a.Kind + ": " + note);
        }

        private static IntPtr OpenGuard(ProcessGuard g, uint access)
        {
            if (g == null || g.Pid <= 0) return IntPtr.Zero;
            string actual = Native.ProcessName(g.Pid);
            if (actual.Length == 0 || !string.Equals(actual, g.Name, StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;
            if (g.StartTime > 0)
            {
                try
                {
                    using (Process p = Process.GetProcessById(g.Pid))
                    {
                        if (p.StartTime.Ticks != g.StartTime) return IntPtr.Zero;
                    }
                }
                catch { return IntPtr.Zero; }
            }
            if (g.SessionId >= 0)
            {
                try
                {
                    using (Process p = Process.GetProcessById(g.Pid))
                    {
                        if (p.SessionId != g.SessionId) return IntPtr.Zero;
                    }
                }
                catch { return IntPtr.Zero; }
            }
            return Native.OpenProcess(access, false, g.Pid);
        }

        /// <summary>OpenProcess 失败不能直接等同于进程退出；权限拒绝必须保留日志等待重试。</summary>
        private static void OpenFailed(JournalAction a, ReconcileReport report)
        {
            ProcessGuard g = a.Target;
            bool alive = false;
            try
            {
                using (Process p = Process.GetProcessById(g.Pid))
                {
                    alive = true;
                    try
                    {
                        if (!string.Equals(p.ProcessName, g.Name, StringComparison.OrdinalIgnoreCase)) alive = false;
                        if (alive && g.SessionId >= 0 && p.SessionId != g.SessionId) alive = false;
                        if (alive && g.StartTime > 0 && p.StartTime.Ticks != g.StartTime) alive = false;
                    }
                    catch
                    {
                        // 进程对象存在，但属性被权限/保护机制挡住：按仍存活处理。
                        alive = true;
                    }
                }
            }
            catch { alive = false; }

            if (alive) Fail(a, report, "target exists but cannot be opened");
            else Noop(a, report, "target gone or pid reused");
        }

        /// <summary>新事务前按 owner 清理旧凭据；未决日志绝不能被下一份事务覆盖。</summary>
        public static bool RecoverBeforeNew(OwnerKind owner, bool sameProcess)
        {
            Transaction tx = Load(owner);
            if (tx == null) return true;
            var ctx = new ReconcileContext();
            ctx.IsSameProcess = sameProcess;
            ctx.IsAdmin = MemEngine.IsAdmin;
            ReconcileReport report = Reconcile(tx, ctx);
            if (report.Clean) return true;
            Log.Write("new " + owner + " transaction blocked by unresolved journal");
            return false;
        }

        /// <summary>启动自愈：有日志就按 v2 核心对账；挂起进程不再受"游戏存活"门控。</summary>
        public static int RecoverStale()
        {
            if (!Pending) return 0;
            int total = 0;
            OwnerKind[] owners = new OwnerKind[] { OwnerKind.ONE_SHOT, OwnerKind.REALTIME };
            for (int i = 0; i < owners.Length; i++)
            {
                Transaction tx = Load(owners[i]);
                if (tx == null) continue;
                var ctx = new ReconcileContext();
                ctx.IsSameProcess = false;
                ctx.IsAdmin = MemEngine.IsAdmin;
                ReconcileReport rep = Reconcile(tx, ctx);
                total += rep.Reverted;
                Log.Write("journal recover " + owners[i] + ": reverted=" + rep.Reverted
                    + " noop=" + rep.Noop + " conflict=" + rep.Conflict + " failed=" + rep.Failed);
            }
            if (total > 0) Log.Write("journal recovered=" + total);
            return total;
        }

        /// <summary>--restore 命令行入口：与启动自愈共用同一 Reconcile。</summary>
        public static int RestoreAll()
        {
            return RecoverStale();
        }

        // =================================================================
        //  编码 / 解析辅助
        // =================================================================

        private static string BuildFlags(JournalAction a)
        {
            var sb = new StringBuilder();
            sb.Append("ex:").Append(a.Existed ? '1' : '0')
              .Append(";ck:").Append(a.CreatedKey ? '1' : '0')
              .Append(";live:").Append(a.RequiresLiveOwner ? '1' : '0')
              .Append(";rev:").Append(a.Revertible ? '1' : '0')
              .Append(";vk:").Append(a.KindOfValue.ToString())
              .Append(";cl:").Append(a.NewClass.ToString(CultureInfo.InvariantCulture))
              .Append(";ow:").Append(a.Owner.ToString())
              .Append(";st:").Append((a.Target == null ? 0 : a.Target.StartTime).ToString(CultureInfo.InvariantCulture))
              .Append(";tid:").Append((a.Target == null ? -1 : a.Target.Tid).ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static void ApplyFlags(JournalAction a, string flags)
        {
            string[] p = (flags ?? "").Split(';');
            for (int i = 0; i < p.Length; i++)
            {
                string[] kv = p[i].Split(new char[] { ':' }, 2);
                if (kv.Length < 2) continue;
                switch (kv[0])
                {
                    case "ex": a.Existed = kv[1] == "1"; break;
                    case "ck": a.CreatedKey = kv[1] == "1"; break;
                    case "live": a.RequiresLiveOwner = kv[1] == "1"; break;
                    case "rev": a.Revertible = kv[1] == "1"; break;
                    case "vk": a.KindOfValue = (ValueKind)Enum.Parse(typeof(ValueKind), kv[1], true); break;
                    case "cl": a.NewClass = Int(kv[1]); break;
                    case "ow": a.Owner = ParseOwner(kv[1]); break;
                    case "st": if (a.Target != null) a.Target.StartTime = long.Parse(kv[1], CultureInfo.InvariantCulture); break;
                    case "tid": if (a.Target != null) a.Target.Tid = Int(kv[1]); break;
                }
            }
        }

        private static string EncodeMeta(JournalAction a)
        {
            var sb = new StringBuilder();
            sb.Append("note=").Append(a.Note ?? "")
              .Append("&applied=").Append(a.AppliedUtc ?? "")
              .Append("&verified=").Append(a.VerifiedUtc ?? "")
              .Append("&reverted=").Append(a.RevertedUtc ?? "");
            byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
            return Convert.ToBase64String(bytes).Replace('=', '_');
        }

        private static void DecodeMeta(JournalAction a, string meta)
        {
            if (meta.Length == 0) return;
            try
            {
                string s = meta.Replace('_', '=');
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(s));
                string[] p = text.Split('&');
                for (int i = 0; i < p.Length; i++)
                {
                    int eq = p[i].IndexOf('=');
                    if (eq < 0) continue;
                    string key = p[i].Substring(0, eq);
                    string val = p[i].Substring(eq + 1);
                    if (key == "note") a.Note = val;
                    else if (key == "applied") a.AppliedUtc = val;
                    else if (key == "verified") a.VerifiedUtc = val;
                    else if (key == "reverted") a.RevertedUtc = val;
                }
            }
            catch { }
        }

        private static OwnerKind ParseOwner(string s)
        {
            try { return (OwnerKind)Enum.Parse(typeof(OwnerKind), s, true); }
            catch { return OwnerKind.NONE; }
        }

        private static string Sanitize(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '|' || c == '\r' || c == '\n' || c == '\0') sb.Append('_');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static int Int(string s)
        {
            int v;
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
            return v;
        }

        private static uint UInt(string s, uint def)
        {
            uint v;
            if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        private static long Hex(string s)
        {
            long v;
            if (long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return v;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        /// <summary>把 int 原值编码成 4 字节 base64（与 §3.2.2 的 base64 约定一致）。</summary>
        public static string Base64Int(int value)
        {
            byte[] b = new byte[4];
            b[0] = (byte)value;
            b[1] = (byte)(value >> 8);
            b[2] = (byte)(value >> 16);
            b[3] = (byte)(value >> 24);
            return Convert.ToBase64String(b).Replace('=', '_');
        }

        private static int FromBase64Int(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            try
            {
                byte[] b = Convert.FromBase64String(s.Replace('_', '='));
                if (b.Length < 4) return 0;
                return (int)((uint)b[0] | ((uint)b[1] << 8) | ((uint)b[2] << 16) | ((uint)b[3] << 24));
            }
            catch { return 0; }
        }

        /// <summary>把二进制原值编码成 base64（IRQ mask / REG 二进制）。</summary>
        public static string Base64(byte[] bytes)
        {
            if (bytes == null) return "";
            return Convert.ToBase64String(bytes).Replace('=', '_');
        }

        private static byte[] FromBase64(string s)
        {
            if (string.IsNullOrEmpty(s)) return new byte[0];
            try { return Convert.FromBase64String(s.Replace('_', '=')); }
            catch { return new byte[0]; }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null) a = new byte[0];
            if (b == null) b = new byte[0];
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // IRQ 元信息编码：写入时由 BoostEngine 调用，存进 action.Name（不含 |）。
        public static string EncodeIrqMeta(int oldPolicy, bool hadPolicy, bool hadMask)
        {
            return "policy=" + oldPolicy.ToString(CultureInfo.InvariantCulture)
                + ";hadPolicy=" + (hadPolicy ? "1" : "0")
                + ";hadMask=" + (hadMask ? "1" : "0");
        }
    }
}
