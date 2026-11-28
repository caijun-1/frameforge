using System;
using System.Collections.Generic;

namespace FrameForge.Sys
{
    /// <summary>
    /// 进程保护与反作弊识别的单一事实来源（Single Source of Truth）。
    /// 所有反作弊进程名、反作弊安装路径特征、受保护后台进程均在此集中维护。
    /// </summary>
    public static class ProcessGuardList
    {
        private static readonly HashSet<string> AntiCheatProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // ACE / SGuard (Tencent)
            "sguard64",
            "sguardsvc64",
            "sguard",
            "sguard_x64",
            "ace-tray",
            "ace-guard",
            "ace-helper",
            "ace-setup64",
            "ace-service",
            "ace-csi64",
            "anticheatexpert",
            "tenprotect",
            "tensafe",
            "qqprotect",
            "krsdk",

            // Easy Anti-Cheat (EAC)
            "easyanticheat",
            "easyanticheat_eos",
            "easyanticheat_x64",
            "easyanticheat_x86",
            "easyanticheat_setup",

            // BattlEye
            "beservice",
            "beservice_x64",
            "bedaisy",
            "battleye",

            // Riot Vanguard (exact names only to avoid false positives)
            "vgc",
            "vgtray",

            // EA Javelin AntiCheat
            "eaanticheat.gameservice",
            "eaanticheat.gameservicelauncher",
            "eaanticheat.installer",
            "eaanticheat",

            // NetEase NeacSafe
            "neacsafe",
            "neacclient",
            "neacsafe64",
            "neacprotect",

            // nProtect GameGuard
            "gamemon",
            "gamemon64",
            "npggnt",
            "nprotect",
            "gameguard",

            // Xigncode3
            "xxd-0.xem",
            "xigncode",
            "xigncode3",

            // miHoYo / HoYoverse
            "mhyprot2",
            "mhyprot3",
            "mhyprotect",
            "hoyokprotect",

            // FairFight & Ricochet
            "fairfight",
            "ricochet",
            "randgrid"
        };

        private static readonly string[] AntiCheatKeywords = new string[]
        {
            "battleye",
            "beservice",
            "easyanticheat",
            "anticheatexpert",
            "ace-guard",
            "ace-helper",
            "ace-tray",
            "sguard",
            "tenprotect",
            "tensafe",
            "qqprotect",
            "gameguard",
            "nprotect",
            "xigncode",
            "krsdk",
            "eaanticheat",
            "neacsafe",
            "mhyprot",
            "hoyokprotect",
            "fairfight",
            "ricochet"
        };

        private static readonly string[] AntiCheatPathSegments = new string[]
        {
            "easyanticheat",
            "easy anti-cheat",
            "battleye",
            "anticheatexpert",
            "riot vanguard",
            "ea\\ac",
            "neac"
        };

        private static readonly HashSet<string> ProtectedBackgroundProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Audio / Voice / Recording
            "obs64",
            "obs32",
            "discord",
            "discordptb",
            "discordcanary",
            "kook",
            "teamspeak",
            "ts3client_win64",
            "ts3client_win32",
            "ts5",
            "yy",
            "voicemeeter",
            "voicemeeterpro",
            "voicemeeter8",
            "voicemeeter8x64",
            "nvidia broadcast",
            "nvidiabroadcast",
            "nvidia_broadcast",

            // Peripheral / Driver / Overlay Suites
            "lghub",
            "lghub_agent",
            "lghub_updater",
            "razer synapse",
            "rzsynapse",
            "razercentral",
            "icue",
            "armoury",
            "armourycrate",
            "armourycrate.usersessionhelper",
            "rtss",
            "rtsshooksloader64",
            "msiafterburner",
            "presentmon",

            // Game Launchers
            "steam",
            "steamwebhelper",
            "steamservice",
            "epicgameslauncher",
            "epicwebhelper",
            "battlenet",
            "battle.net",
            "riotclientservices",
            "riotclientux",
            "hoyoplay",
            "wegame",
            "eadesktop",
            "eabackgroundservice",
            "upc",
            "ubisoftconnect",
            "xboxapp",
            "gamingservices",
            "galaxyclient",

            // System Shells & Core Windows Processes
            "explorer",
            "dwm",
            "csrss",
            "winlogon",
            "wininit",
            "services",
            "lsass",
            "smss",
            "svchost",
            "taskmgr",
            "audiodg",
            "conhost",
            "sihost",
            "shellexperiencehost",
            "startmenuexperiencehost",
            "searchhost",
            "textinputhost",
            "ctfmon",
            "fontdrvhost",
            "system",
            "idle"
        };

        /// <summary>判断进程名是否为反作弊 / 内核防护组件。</summary>
        public static bool IsAntiCheat(string procName)
        {
            string n = NormalizeProcName(procName);
            if (n.Length == 0) return false;
            if (AntiCheatProcs.Contains(n)) return true;

            string low = n.ToLowerInvariant();
            for (int i = 0; i < AntiCheatKeywords.Length; i++)
            {
                if (low.IndexOf(AntiCheatKeywords[i], StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>判断路径是否位于反作弊组件目录内。</summary>
        public static bool IsAntiCheatPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.Replace('/', '\\').ToLowerInvariant();
            for (int i = 0; i < AntiCheatPathSegments.Length; i++)
            {
                string seg = AntiCheatPathSegments[i];
                if (p.IndexOf("\\" + seg + "\\", StringComparison.Ordinal) >= 0) return true;
                if (p.EndsWith("\\" + seg, StringComparison.Ordinal)) return true;
                if (seg == "easyanticheat" && p.IndexOf("\\easyanticheat", StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>判断进程名是否为应受保护的后台进程（语音/录屏/外设驱动/平台启动器/系统外壳）。</summary>
        public static bool IsProtectedBackground(string procName)
        {
            string n = NormalizeProcName(procName);
            if (n.Length == 0) return false;
            return ProtectedBackgroundProcs.Contains(n);
        }

        private static string NormalizeProcName(string procName)
        {
            if (procName == null) return "";
            string s = procName.Trim();
            if (s.Length == 0) return "";
            int slash = s.LastIndexOfAny(new char[] { '\\', '/' });
            if (slash >= 0) s = s.Substring(slash + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);
            return s.Trim();
        }
    }
}
