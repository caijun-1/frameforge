using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    /// <summary>一款本机游戏:显示名、启动 exe、来源平台、启动协议 URI。</summary>
    public sealed class InstalledGame
    {
        public string Name = "";
        public string ExePath = "";
        /// <summary>steam | epic | gog | xbox | hoyoplay | riot | battlenet | ea | ubisoft | wegame | reg | disk | manual</summary>
        public string Source = "";
        public string LaunchUri = "";

        /// <summary>进程名(不含 .exe),识别时按它匹配。</summary>
        public string ProcName
        {
            get
            {
                if (string.IsNullOrEmpty(ExePath)) return "";
                try { return Path.GetFileNameWithoutExtension(ExePath); }
                catch { return ""; }
            }
        }
    }

    /// <summary>
    /// 扫描本机已安装的游戏:Steam 库清单、Epic 清单、GOG 注册表、卸载注册表。
    /// 只读文件与注册表找启动 exe,不启动进程、不改系统。
    /// </summary>
    public static class GameScanner
    {
        /// <summary>扫一遍全部来源,按名称排序返回。</summary>
        public static InstalledGame[] Scan()
        {
            var found = new List<InstalledGame>();
            var seen = new Dictionary<string, InstalledGame>(StringComparer.OrdinalIgnoreCase);

            try { ScanSteam(found, seen); }
            catch (Exception ex) { Log.Write("gamescan steam: " + ex.Message); }
            try { ScanEpic(found, seen); }
            catch (Exception ex) { Log.Write("gamescan epic: " + ex.Message); }
            try { ScanGog(found, seen); }
            catch (Exception ex) { Log.Write("gamescan gog: " + ex.Message); }
            try { ScanXbox(found, seen); }
            catch (Exception ex) { Log.Write("gamescan xbox: " + ex.Message); }
            try { ScanHoYoPlay(found, seen); }
            catch (Exception ex) { Log.Write("gamescan hoyoplay: " + ex.Message); }
            try { ScanRiot(found, seen); }
            catch (Exception ex) { Log.Write("gamescan riot: " + ex.Message); }
            try { ScanBattleNet(found, seen); }
            catch (Exception ex) { Log.Write("gamescan battlenet: " + ex.Message); }
            try { ScanEaApp(found, seen); }
            catch (Exception ex) { Log.Write("gamescan ea: " + ex.Message); }
            try { ScanUbisoft(found, seen); }
            catch (Exception ex) { Log.Write("gamescan ubisoft: " + ex.Message); }
            try { ScanWeGame(found, seen); }
            catch (Exception ex) { Log.Write("gamescan wegame: " + ex.Message); }
            try { ScanUninstall(found, seen); }
            catch (Exception ex) { Log.Write("gamescan reg: " + ex.Message); }
            // 鸣潮、三角洲这类新游戏有的不走 Steam/Epic,注册表里也不带安装位置,
            // 最后按常见目录名补扫一遍,只认内置名单里的游戏。
            try { ScanKnownDirs(found, seen); }
            catch (Exception ex) { Log.Write("gamescan dirs: " + ex.Message); }

            found.Sort(delegate(InstalledGame a, InstalledGame b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return found.ToArray();
        }

        // ---------------- Steam ----------------

        private static void ScanSteam(List<InstalledGame> found, Dictionary<string, InstalledGame> seen)
        {
            var roots = new List<string>();
            string steam = RegStr(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            if (steam.Length == 0)
                steam = RegStr(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            if (steam.Length == 0)
                steam = RegStr(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            if (steam.Length == 0)
            {
                try
                {
                    steam = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
                }
                catch { }
            }
            if (steam.Length > 0)
            {
                steam = steam.Replace('/', '\\').TrimEnd('\\');
                if (Directory.Exists(steam)) roots.Add(steam);
            }

            var libs = new List<string>();
            for (int i = 0; i < roots.Count; i++)
            {
                libs.Add(roots[i]);
                ReadLibraryFolders(Path.Combine(Path.Combine(roots[i], "steamapps"), "libraryfolders.vdf"), libs);
                ReadLibraryFolders(Path.Combine(Path.Combine(roots[i], "config"), "libraryfolders.vdf"), libs);
            }

            for (int i = 0; i < libs.Count; i++)
            {
                string apps = Path.Combine(libs[i], "steamapps");
                if (!Directory.Exists(apps)) continue;
                string[] acfs;
                try { acfs = Directory.GetFiles(apps, "appmanifest_*.acf"); }
                catch { continue; }
                for (int k = 0; k < acfs.Length; k++)
                {
                    string text;
                    try { text = File.ReadAllText(acfs[k], Encoding.UTF8); }
                    catch { continue; }
                    string appid = Acf(text, "appid");
                    string name = Acf(text, "name");
                    string installdir = Acf(text, "installdir");
                    if (name.Length == 0 || installdir.Length == 0) continue;
                    if (LooksLikeTool(name)) continue;
                    string dir = Path.Combine(Path.Combine(apps, "common"), installdir);
                    string exe = FindExe(dir, name);
                    string uri = appid.Length > 0 ? "steam://rungameid/" + appid : "";
                    Add(found, seen, name, exe, "steam", uri);
                }
            }
        }

        private static void ReadLibraryFolders(string file, List<string> libs)
        {
            if (!File.Exists(file)) return;
            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); }
            catch { return; }
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int q1 = line.IndexOf('"');
                if (q1 < 0) continue;
                int q2 = line.IndexOf('"', q1 + 1);
                if (q2 < 0) continue;
                string key = line.Substring(q1 + 1, q2 - q1 - 1);
                if (!string.Equals(key, "path", StringComparison.OrdinalIgnoreCase)) continue;
                int q3 = line.IndexOf('"', q2 + 1);
                if (q3 < 0) continue;
                int q4 = line.IndexOf('"', q3 + 1);
                if (q4 < 0) continue;
                string val = line.Substring(q3 + 1, q4 - q3 - 1).Replace("\\\\", "\\").Trim();
                if (val.Length == 0 || !Directory.Exists(val)) continue;
                bool dup = false;
                for (int k = 0; k < libs.Count; k++)
                    if (string.Equals(libs[k], val, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup) libs.Add(val);
            }
        }

        private static string Acf(string text, string key)
        {
            string pat = "\"" + key + "\"";
            int i = text.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            int q1 = text.IndexOf('"', i + pat.Length);
            if (q1 < 0) return "";
            int q2 = text.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";
            return text.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\").Trim();
        }

        // ---------------- Epic ----------------

        private static void ScanEpic(List<InstalledGame> found, Dictionary<string, InstalledGame> seen)
        {
            string dir;
            try
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Epic\EpicGamesLauncher\Data\Manifests");
            }
            catch { return; }
            if (!Directory.Exists(dir)) return;

            string[] items;
            try { items = Directory.GetFiles(dir, "*.item"); }
            catch { return; }
            for (int i = 0; i < items.Length; i++)
            {
                string text;
                try { text = File.ReadAllText(items[i], Encoding.UTF8); }
                catch { continue; }
                string name = JsonStr(text, "DisplayName");
                string loc = JsonStr(text, "InstallLocation");
                if (name.Length == 0 || loc.Length == 0) continue;
                if (LooksLikeTool(name)) continue;
                loc = loc.Replace('/', '\\').TrimEnd('\\');
                if (!Directory.Exists(loc)) continue;

                string exe = "";
                string launch = JsonStr(text, "LaunchExecutable");
                if (launch.Length > 0)
                {
                    launch = launch.Replace('/', '\\');
                    try
                    {
                        exe = Path.IsPathRooted(launch) ? launch : Path.Combine(loc, launch);
                    }
                    catch { exe = ""; }
                }
                if (exe.Length == 0 || !File.Exists(exe)) exe = FindExe(loc, name);
                string appName = JsonStr(text, "AppName");
                string uri = appName.Length > 0
                    ? "com.epicgames.launcher://apps/" + appName + "?action=launch&silent=true"
                    : "";
                Add(found, seen, name, exe, "epic", uri);
            }
        }

        private static string JsonStr(string text, string key)
        {
            string pat = "\"" + key + "\"";
            int i = text.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return "";
            int colon = text.IndexOf(':', i + pat.Length);
            if (colon < 0) return "";
            int q = text.IndexOf('"', colon + 1);
            if (q < 0) return "";

            var sb = new StringBuilder();
            for (int j = q + 1; j < text.Length; j++)
            {
                char ch = text[j];
                if (ch == '\\' && j + 1 < text.Length)
                {
                    char n = text[j + 1];
                    if (n == '\\' || n == '"' || n == '/') { sb.Append(n); j++; continue; }
                    if (n == 'u' && j + 5 < text.Length)
                    {
                        int code;
                        if (int.TryParse(text.Substring(j + 2, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            j += 5;
                            continue;
                        }
                    }
                    sb.Append(ch);
                    continue;
                }
                if (ch == '"') return sb.ToString().Trim();
                sb.Append(ch);
            }
            return "";
        }

        // ---------------- GOG ----------------

        private static void ScanGog(List<InstalledGame> found, Dictionary<string, InstalledGame> seen)
        {
            RegistryKey[] roots = new RegistryKey[]
            {
                Registry.LocalMachine, Registry.CurrentUser
            };
            string[] paths = new string[]
            {
                @"SOFTWARE\WOW6432Node\GOG.com\Games",
                @"SOFTWARE\GOG.com\Games"
            };
            for (int r = 0; r < roots.Length; r++)
            {
                for (int p = 0; p < paths.Length; p++)
                {
                    try
                    {
                        using (RegistryKey key = roots[r].OpenSubKey(paths[p]))
                        {
                            if (key == null) continue;
                            string[] subs = key.GetSubKeyNames();
                            for (int i = 0; i < subs.Length; i++)
                            {
                                using (RegistryKey g = key.OpenSubKey(subs[i]))
                                {
                                    if (g == null) continue;
                                    string name = Val(g, "gameName");
                                    string dir = Val(g, "path");
                                    string exeName = Val(g, "exe");
                                    if (name.Length == 0 || dir.Length == 0) continue;
                                    if (LooksLikeTool(name)) continue;
                                    string exe = "";
                                    if (exeName.Length > 0)
                                    {
                                        try { exe = Path.Combine(dir, exeName.Replace('/', '\\')); }
                                        catch { exe = ""; }
                                    }
                                    if (exe.Length == 0 || !File.Exists(exe)) exe = FindExe(dir, name);
                                    Add(found, seen, name, exe, "gog");
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        // ---------------- Xbox / Microsoft Store ----------------

        private static void ScanXbox(List<InstalledGame> found, Dictionary<string, InstalledGame> seen)
        {
            var roots = new List<string>();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return; }

            for (int d = 0; d < drives.Length; d++)
            {
                try
                {
                    if (drives[d].DriveType != DriveType.Fixed || !drives[d].IsReady) continue;
                    string root = drives[d].RootDirectory.FullName.TrimEnd('\\') + "\\";

                    string gamingRoot = Path.Combine(root, ".GamingRoot");
                    if (File.Exists(gamingRoot))
                    {
                        try
                        {
                            byte[] raw = File.ReadAllBytes(gamingRoot);
                            string text = Encoding.Unicode.GetString(raw).Replace("\0", "").Trim();
                            if (text.Length == 0)
                                text = Encoding.UTF8.GetString(raw).Replace("\0", "").Trim();
                            int idx = text.IndexOf('\\');
                            if (idx >= 0) text = text.Substring(idx).Trim();
                            text = text.TrimStart('\\');
                            if (text.Length > 0)
                            {
                                string cand = Path.Combine(root, text);
                                if (Directory.Exists(cand)) AddUniqueDir(roots, cand);
                            }
                        }
                        catch { }
                    }

                    string xboxDir = Path.Combine(root, "XboxGames");
                    if (Directory.Exists(xboxDir)) AddUniqueDir(roots, xboxDir);
                }
                catch { }
            }

            for (int r = 0; r < roots.Count; r++)
            {
                string[] games;
                try { games = Directory.GetDirectories(roots[r]); }
                catch { continue; }
                for (int i = 0; i < games.Length && i < 300; i++)
                {
                    string gameDir = games[i];
                    string cfg = Path.Combine(gameDir, "MicrosoftGame.config");
                    string contentDir = gameDir;
                    if (!File.Exists(cfg))
                    {
                        contentDir = Path.Combine(gameDir, "Content");
                        cfg = Path.Combine(contentDir, "MicrosoftGame.config");
                    }
                    if (!File.Exists(cfg)) continue;

                    string xml;
                    try { xml = File.ReadAllText(cfg, Encoding.UTF8); }
                    catch { continue; }

                    string name = XmlAttr(xml, "ShellVisuals", "DefaultDisplayName");
                    if (name.Length == 0 || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        name = Path.GetFileName(gameDir);
                    if (LooksLikeTool(name)) continue;

                    string exeRel = XmlAttr(xml, "Executable", "Name");
                    string exe = "";
                    if (exeRel.Length > 0)
                    {
                        try
                        {
                            string cand = Path.Combine(contentDir, exeRel.Replace('/', '\\'));
                            if (File.Exists(cand)) exe = cand;
                        }
                        catch { exe = ""; }
                    }
                    if (exe.Length == 0) exe = FindExe(contentDir, name);
                    Add(found, seen, name, exe, "xbox", "");
                }
            }
        }

        // ---------------- HoYoPlay / miHoYo Launcher ----------------

        private static void ScanHoYoPlay(List<InstalledGame> found, Dictionary<string, InstalledGame> seen)
        {
            var gameDirs = new List<string>();
            string[] hypKeys = new string[]
            {
                @"Software\Cognosphere\HYP",
                @"Software\miHoYo\HYP"
            };
            for (int k = 0; k < hypKeys.Length; k++)
            {
                try
                {
                    using (RegistryKey hyp = Registry.CurrentUser.OpenSubKey(hypKeys[k]))
                    {
                        if (hyp == null) continue;
                        CollectRegInstallPaths(hyp, gameDirs, 0);
                    }
                }
                catch { }
            }

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { drives = new DriveInfo[0]; }
            for (int d = 0; d < drives.Length; d++)
            {
                try
                {
                    if (drives[d].DriveType != DriveType.Fixed || !drives[d].IsReady) continue;
                    string root = drives[d].RootDirectory.FullName.TrimEnd('\\') + "\\";
                    string[] hypBases = new string[]
                    {
                        root + "HoYoPlay\\games",
                        root + "Program Files\\HoYoPlay\\games",
                        root + "miHoYo Launcher\\games",
                        root + "Program Files\\miHoYo Launcher\\games"
                    };
                    for (int b = 0; b < hypBases.Length; b++)
                    {
                        if (!Directory.Exists(hypBases[b])) continue;
                        string[] subs;
                        try { subs = Directory.GetDirectories(hypBases[b]); }
                        catch { continue; }
                        for (int i = 0; i < subs.Length && i < 100; i++)
                            AddUniqueDir(gameDirs, subs[i]);
                    }
                }
                catch { }
            }

            for (int i = 0; i < gameDirs.Count; i++)
            {
                string dir = gameDirs[i];
                if (!Directory.Exists(dir)) continue;
                string ini = Path.Combine(dir, "config.ini");
                string targetDir = dir;
                string startExe = "";
                if (File.Exists(ini))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(ini, Encoding.UTF8);
                        for (int l = 0; l < lines.Length; l++)
                        {
                            string line = lines[l].Trim();
                            if (line.StartsWith("game_install_path=", StringComparison.OrdinalIgnoreCase))
                            {
                                string p = line.Substring("game_install_path=".Length).Trim().Replace('/', '\\');
                                if (Directory.Exists(p)) targetDir = p;
                            }
                            else if (line.StartsWith("game_start_name=", StringComparison.OrdinalIgnoreCase))
                            {
                                startExe = line.Substring("game_start_name=".Length).Trim();
                            }
                        }
                    }
                    catch { }
                }

                string dirName = Path.GetFileName(targetDir.TrimEnd('\\'));
                string exe = "";
                if (startExe.Length > 0)
                {
                    try
                    {
                        string cand = Path.Combine(targetDir, startExe);
                        if (File.Exists(cand)) exe = cand;
                    }
                    catch { exe = ""; }
                }
                if (exe.Length == 0) exe = FindExe(targetDir, dirName, 45);
                if (exe.Length > 0) Add(found, seen, dirName, exe, "hoyoplay", "");
            }
        }

        // ---------------- Riot Client ----------------

        private static void ScanRiot(List<InstalledGame> found, Dictionary<string, InstalledGame> seen)
        {
            string riotData;
            try
            {
                riotData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Riot Games");
            }
            catch { return; }
            if (!Directory.Exists(riotData)) return;

            string metaDir = Path.Combine(riotData, "Metadata");
            if (Directory.Exists(metaDir))
            {
                string[] products;
                try { products = Directory.GetDirectories(metaDir); }
                catch { products = new string[0]; }
                for (int i = 0; i < products.Length; i++)
                {
                    string[] yamls;
                    try { yamls = Directory.GetFiles(products[i], "*.product_settings.yaml"); }
                    catch { continue; }
                    for (int k = 0; k < yamls.Length; k++)
                    {
                        string[] lines;
                        try { lines = File.ReadAllLines(yamls[k], Encoding.UTF8); }
                        catch { continue; }
                        string installPath = "";
                        for (int l = 0; l < lines.Length; l++)
                        {
                            string t = lines[l].Trim();
                            if (t.StartsWith("product_install_full_path:", StringComparison.OrdinalIgnoreCase))
                            {
                                installPath = t.Substring("product_install_full_path:".Length).Trim().Trim('"', '\'');
                                break;
                            }
                            if (installPath.Length == 0 && t.StartsWith("product_install_root:", StringComparison.OrdinalIgnoreCase))
                            {
                                installPath = t.Substring("product_install_root:".Length).Trim().Trim('"', '\'');
                            }
                        }
                        installPath = installPath.Replace('/', '\\').TrimEnd('\\');
                        if (installPath.Length == 0 || !Directory.Exists(installPath)) continue;
                        string name = Path.GetFileName(installPath);
                        if (LooksLikeTool(name)) continue;
                        string exe = FindExe(installPath, name);
                        Add(found, seen, name, exe, "riot", "");
                    }
                }
            }

            string installsJson = Path.Combine(riotData, "RiotClientInstalls.json");
            if (File.Exists(installsJson))
            {
                try
                {
                    string json = File.ReadAllText(installsJson, Encoding.UTF8);
                    string rc = JsonStr(json, "rc_default");
                    if (rc.Length > 0)
                    {
                        string rcDir = Path.GetDirectoryName(rc.Replace('/', '\\'));
                        if (!string.IsNullOrEmpty(rcDir))
                        {
                            string parent = Path.GetDirectoryName(rcDir);
                            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                            {
                                string[] subs = Directory.GetDirectories(parent);
                                for (int i = 0; i < subs.Length && i < 40; i++)
                                {
                                    string n = Path.GetFileName(subs[i]);
                                    if (string.Equals(n, "Riot Client", StringComparison.OrdinalIgnoreCase)) continue;
                                    if (LooksLikeTool(n)) continue;
                                    string exe = FindExe(subs[i], n);
                                    Add(found, seen, n, exe, "riot", "");
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        // ---------------- Battle.net ----------------

        private static void ScanBattleNet(List<InstalledGame> found, Dictionary<string, InstalledGame> seen)
        {
            string[] regPaths = new string[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            for (int p = 0; p < regPaths.Length; p++)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(regPaths[p]))
                    {
                        if (key == null) continue;
                        string[] subs = key.GetSubKeyNames();
                        for (int i = 0; i < subs.Length; i++)
                        {
                            using (RegistryKey g = key.OpenSubKey(subs[i]))
                            {
                                if (g == null) continue;
                                string pub = Val(g, "Publisher");
                                if (pub.IndexOf("Blizzard", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                string name = Val(g, "DisplayName");
                                if (name.Length == 0 || LooksLikeTool(name)) continue;
                                string dir = Val(g, "InstallLocation");
                                if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                                string exe = FindExe(dir, name);
                                Add(found, seen, name, exe, "battlenet", "");
                            }
                        }
                    }
                }
                catch { }
            }

            try
            {
                string db = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Battle.net\Agent\product.db");
                if (File.Exists(db))
                {
                    byte[] bytes = File.ReadAllBytes(db);
                    string ascii = Encoding.UTF8.GetString(bytes);
                    int idx = 0;
                    while (idx < ascii.Length)
                    {
                        int colon = ascii.IndexOf(":\\", idx, StringComparison.Ordinal);
                        if (colon < 0) colon = ascii.IndexOf(":/", idx, StringComparison.Ordinal);
                        if (colon < 0) break;
                        if (colon >= 1 && char.IsLetter(ascii[colon - 1]))
                        {
                            int start = colon - 1;
                            int end = colon + 2;
                            while (end < ascii.Length && ascii[end] >= 32 && ascii[end] != '"' && ascii[end] != '<' && ascii[end] != '>')
                                end++;
                            string cand = ascii.Substring(start, end - start).Replace('/', '\\').TrimEnd('\\');
                            if (cand.Length > 3 && cand.Length < 200 && Directory.Exists(cand))
                            {
                                string n = Path.GetFileName(cand);
                                if (!LooksLikeTool(n) && n.IndexOf("Battle.net", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    string exe = FindExe(cand, n);
                                    Add(found, seen, n, exe, "battlenet", "");
                                }
                            }
                            idx = end;
                        }
                        else
                        {
                            idx = colon + 2;
                        }
                    }
                }
            }
            catch { }
        }

        // ---------------- EA App ----------------

        private static void ScanEaApp(List<InstalledGame> found, Dictionary<string, InstalledGame> seen)
        {
            string[] roots = new string[]
            {
                @"SOFTWARE\Electronic Arts",
                @"SOFTWARE\WOW6432Node\Electronic Arts",
                @"SOFTWARE\Origin Games",
                @"SOFTWARE\WOW6432Node\Origin Games"
            };
            for (int r = 0; r < roots.Length; r++)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(roots[r]))
                    {
                        if (key == null) continue;
                        string[] subs = key.GetSubKeyNames();
                        for (int i = 0; i < subs.Length; i++)
                        {
                            if (string.Equals(subs[i], "EA Desktop", StringComparison.OrdinalIgnoreCase)) continue;
                            using (RegistryKey g = key.OpenSubKey(subs[i]))
                            {
                                if (g == null) continue;
                                string dir = Val(g, "Install Dir");
                                if (dir.Length == 0) dir = Val(g, "InstallDir");
                                dir = dir.Replace('/', '\\').TrimEnd('\\');
                                if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                                string name = Val(g, "DisplayName");
                                if (name.Length == 0) name = Path.GetFileName(dir);
                                if (LooksLikeTool(name)) continue;
                                string exe = FindExe(dir, name);
                                Add(found, seen, name, exe, "ea", "");
                            }
                        }
                    }
                }
                catch { }
            }
        }

        // ---------------- Ubisoft Connect ----------------

        private static void ScanUbisoft(List<InstalledGame> found, Dictionary<string, InstalledGame> seen)
        {
            string[] paths = new string[]
            {
                @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs",
                @"SOFTWARE\Ubisoft\Launcher\Installs"
            };
            for (int p = 0; p < paths.Length; p++)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(paths[p]))
                    {
                        if (key == null) continue;
                        string[] subs = key.GetSubKeyNames();
                        for (int i = 0; i < subs.Length; i++)
                        {
                            using (RegistryKey g = key.OpenSubKey(subs[i]))
                            {
                                if (g == null) continue;
                                string dir = Val(g, "InstallDir").Replace('/', '\\').TrimEnd('\\');
                                if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                                string name = Path.GetFileName(dir);
                                if (LooksLikeTool(name)) continue;
                                string exe = FindExe(dir, name);
                                string uri = subs[i].Length > 0 ? "uplay://launch/" + subs[i] + "/0" : "";
                                Add(found, seen, name, exe, "ubisoft", uri);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        // ---------------- WeGame ----------------

        private static void ScanWeGame(List<InstalledGame> found, Dictionary<string, InstalledGame> seen)
        {
            var wegameDirs = new List<string>();
            string[] regPaths = new string[]
            {
                @"Software\Tencent\WeGame",
                @"SOFTWARE\WOW6432Node\Tencent\WeGame",
                @"SOFTWARE\Tencent\WeGame"
            };
            RegistryKey[] regRoots = new RegistryKey[]
            {
                Registry.CurrentUser,
                Registry.LocalMachine,
                Registry.LocalMachine
            };
            for (int i = 0; i < regPaths.Length; i++)
            {
                string p = RegStr(regRoots[i], regPaths[i], "InstallPath");
                if (p.Length == 0) p = RegStr(regRoots[i], regPaths[i], "Path");
                if (p.Length > 0) AddUniqueDir(wegameDirs, p.Replace('/', '\\').TrimEnd('\\'));
            }

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { drives = new DriveInfo[0]; }
            for (int d = 0; d < drives.Length; d++)
            {
                try
                {
                    if (drives[d].DriveType != DriveType.Fixed || !drives[d].IsReady) continue;
                    string root = drives[d].RootDirectory.FullName.TrimEnd('\\') + "\\";
                    AddUniqueDir(wegameDirs, root + "WeGameApps\\rail_apps");
                    AddUniqueDir(wegameDirs, root + "WeGameApps\\common_apps");
                }
                catch { }
            }

            for (int w = 0; w < wegameDirs.Count; w++)
            {
                string baseDir = wegameDirs[w];
                if (!Directory.Exists(baseDir)) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(baseDir); }
                catch { continue; }
                for (int i = 0; i < subs.Length && i < 200; i++)
                {
                    string dirName = Path.GetFileName(subs[i]);
                    if (LooksLikeTool(dirName)) continue;
                    string exe = FindExe(subs[i], dirName, 45);
                    if (exe.Length > 0) Add(found, seen, dirName, exe, "wegame", "");
                }
            }
        }

        private static void AddUniqueDir(List<string> dirs, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string clean = path.Replace('/', '\\').TrimEnd('\\');
            if (clean.Length == 0) return;
            for (int i = 0; i < dirs.Count; i++)
                if (string.Equals(dirs[i], clean, StringComparison.OrdinalIgnoreCase)) return;
            dirs.Add(clean);
        }

        private static void CollectRegInstallPaths(RegistryKey key, List<string> dirs, int depth)
        {
            if (depth > 3 || key == null) return;
            string[] names;
            try { names = key.GetValueNames(); }
            catch { names = new string[0]; }
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].IndexOf("InstallPath", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string p = Val(key, names[i]).Replace('/', '\\').TrimEnd('\\');
                    if (p.Length > 0 && Directory.Exists(p)) AddUniqueDir(dirs, p);
                }
            }
            string[] subs;
            try { subs = key.GetSubKeyNames(); }
            catch { return; }
            for (int i = 0; i < subs.Length; i++)
            {
                try
                {
                    using (RegistryKey sub = key.OpenSubKey(subs[i]))
                        CollectRegInstallPaths(sub, dirs, depth + 1);
                }
                catch { }
            }
        }

        private static string XmlAttr(string xml, string tagName, string attrName)
        {
            if (string.IsNullOrEmpty(xml)) return "";
            int tagIdx = xml.IndexOf("<" + tagName, StringComparison.OrdinalIgnoreCase);
            if (tagIdx < 0) return "";
            int endIdx = xml.IndexOf('>', tagIdx);
            if (endIdx < 0) return "";
            string header = xml.Substring(tagIdx, endIdx - tagIdx);
            string pat = attrName + "=";
            int aIdx = header.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
            if (aIdx < 0) return "";
            int qStart = aIdx + pat.Length;
            if (qStart >= header.Length) return "";
            char quote = header[qStart];
            if (quote != '"' && quote != '\'') return "";
            int qEnd = header.IndexOf(quote, qStart + 1);
            if (qEnd < 0) return "";
            return header.Substring(qStart + 1, qEnd - qStart - 1).Trim();
        }

        // ---------------- 卸载注册表 ----------------

        private static void ScanUninstall(List<InstalledGame> found, Dictionary<string, InstalledGame> seen)
        {
            string[] paths = new string[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            RegistryKey[] roots = new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser };

            for (int r = 0; r < roots.Length; r++)
            {
                for (int p = 0; p < paths.Length; p++)
                {
                    try
                    {
                        using (RegistryKey key = roots[r].OpenSubKey(paths[p]))
                        {
                            if (key == null) continue;
                            string[] subs = key.GetSubKeyNames();
                            for (int i = 0; i < subs.Length; i++)
                            {
                                using (RegistryKey g = key.OpenSubKey(subs[i]))
                                {
                                    if (g == null) continue;
                                    string name = Val(g, "DisplayName");
                                    if (name.Length == 0 || LooksLikeTool(name)) continue;
                                    if (Val(g, "SystemComponent") == "1") continue;

                                    string dir = Val(g, "InstallLocation");
                                    string icon = Val(g, "DisplayIcon");
                                    string exe = "";
                                    if (icon.Length > 0)
                                    {
                                        icon = icon.Trim('"');
                                        int comma = icon.LastIndexOf(',');
                                        if (comma > 0) icon = icon.Substring(0, comma).Trim('"');
                                        if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                            && File.Exists(icon)
                                            && (GameDb.Known(Path.GetFileNameWithoutExtension(icon))
                                                || GameDb.FolderKnown(icon)
                                                || UnderGameRoot(icon)
                                                || KnownGameDir(icon)))
                                            exe = icon;
                                    }
                                    if (exe.Length == 0 && dir.Length > 0)
                                    {
                                        if (UnderGameRoot(dir) || KnownGameDir(dir))
                                            exe = FindExe(dir, name);
                                        else if (BigInstall(g))
                                            // 不在白名单目录、图标又是通用客户端名的,
                                            // 靠「安装体积 ≥3 GB + 引擎特征 exe」兜底。
                                            exe = FindExe(dir, name, 60);
                                    }
                                    if (exe.Length > 0 && BadStem(Path.GetFileNameWithoutExtension(exe)))
                                        exe = "";
                                    Add(found, seen, name, exe, "reg");
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        /// <summary>只在明确的游戏平台目录里认领,避免把普通软件塞进「我的游戏」。</summary>
        private static bool UnderGameRoot(string path)
        {
            string low = (path ?? "").Replace('/', '\\').ToLowerInvariant();
            if (low.Length == 0) return false;
            string[] roots = new string[]
            {
                "\\steamapps\\common\\", "\\steamapps\\", "\\epic games\\", "\\gog games\\",
                "\\gog galaxy\\", "\\ubisoft\\", "\\ubisoft game launcher\\", "\\battle.net\\",
                "\\ea games\\", "\\origin games\\", "\\wegame\\", "\\wegameapps\\", "\\riot games\\",
                "\\xboxgames\\", "\\mihoyo\\", "\\米哈游\\", "\\cognosphere\\"
            };
            for (int i = 0; i < roots.Length; i++)
                if (low.IndexOf(roots[i]) >= 0) return true;
            return false;
        }

        /// <summary>内置名单里的游戏目录:鸣潮、三角洲这些不走常见平台目录也能认出来。</summary>
        private static bool KnownGameDir(string path)
        {
            string low = (path ?? "").Replace('/', '\\').ToLowerInvariant();
            if (low.Length == 0) return false;
            for (int i = 0; i < KnownFolders.Length; i++)
                if (low.IndexOf("\\" + KnownFolders[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>卸载项记录的安装体积 ≥3 GB 才当成游戏,普通软件很少有这么大。</summary>
        private static bool BigInstall(RegistryKey g)
        {
            long kb;
            return long.TryParse(Val(g, "EstimatedSize"), out kb) && kb >= 3L * 1024 * 1024;
        }

        // ---------------- 目录补扫 ----------------

        private static readonly string[] KnownFolders = new string[]
        {
            "wuthering waves", "wuthering waves game", "wutheringwaves", "wuwa", "鸣潮",
            "delta force", "deltaforce", "三角洲行动", "kuro games", "库洛游戏",
            "infinity nikki", "无限暖暖",
            // 原神:老版直接装在 Genshin Impact 目录,新版在米哈游启动器的 games 下。
            "genshin impact", "genshin impact game", "genshinimpact", "yuanshen", "原神"
        };

        /// <summary>
        /// 在各固定磁盘的常见安装位置找内置名单里的游戏目录。
        /// 只列目录名、命中才进去找 exe,开销很小。
        /// </summary>
        private static void ScanKnownDirs(List<InstalledGame> found, Dictionary<string, InstalledGame> seen)
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return; }

            for (int d = 0; d < drives.Length; d++)
            {
                try
                {
                    if (drives[d].DriveType != DriveType.Fixed || !drives[d].IsReady) continue;
                    string root = drives[d].RootDirectory.FullName.TrimEnd('\\') + "\\";
                    string[] bases = new string[]
                    {
                        root, root + "Games", root + "Program Files", root + "Program Files (x86)",
                        // WeGame 的游戏装在这两个目录里,目录名后面还带 (appid)。
                        root + "WeGameApps", root + "WeGameApps\\rail_apps",
                        root + "WeGameApps\\common_apps",
                        // 腾讯系独立客户端常见的两层安装路径。
                        root + "Tencent", root + "Program Files\\Tencent",
                        root + "Program Files (x86)\\Tencent", root + "Program Files\\Level Infinite",
                        root + "SteamLibrary\\steamapps\\common",
                        // 米哈游启动器把游戏放在 <启动器>\games\<游戏名> 下,
                        // 原神的路径是 D:\miHoYo Launcher\games\Genshin Impact Game。
                        root + "miHoYo Launcher\\games",
                        root + "Program Files\\miHoYo Launcher\\games",
                        root + "Program Files (x86)\\miHoYo Launcher\\games"
                    };
                    for (int b = 0; b < bases.Length; b++)
                    {
                        string[] subs;
                        try
                        {
                            if (!Directory.Exists(bases[b])) continue;
                            subs = Directory.GetDirectories(bases[b]);
                        }
                        catch { continue; }
                        for (int i = 0; i < subs.Length && i < 400; i++)
                        {
                            string dirName = Path.GetFileName(subs[i]);
                            if (!KnownFolderName(dirName)) continue;
                            AddKnownDir(found, seen, subs[i], dirName);
                        }
                    }
                }
                catch (Exception ex) { Log.Write("gamescan dir: " + ex.Message); }
            }
        }

        private static void AddKnownDir(List<InstalledGame> found, Dictionary<string, InstalledGame> seen,
            string dir, string name)
        {
            string exe = FindExe(dir, name, 45);
            if (exe.Length == 0)
            {
                // Kuro Games\Wuthering Waves 这种多一层的目录。
                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch { return; }
                for (int i = 0; i < subs.Length && i < 40; i++)
                {
                    string subName = Path.GetFileName(subs[i]);
                    if (!KnownFolderName(subName)) continue;
                    exe = FindExe(subs[i], subName, 45);
                    if (exe.Length > 0) { name = subName; break; }
                }
            }
            if (exe.Length == 0) return;
            Add(found, seen, GameDb.DisplayName(name, exe), exe, "disk");
        }

        private static bool KnownFolderName(string name)
        {
            string l = (name ?? "").Trim().ToLowerInvariant();
            if (l.Length == 0) return false;
            // WeGame 的目录形如「三角洲行动(2001234)」,去掉括号里的 appid 再比。
            int par = l.IndexOf('(');
            if (par > 0) l = l.Substring(0, par).Trim();
            for (int i = 0; i < KnownFolders.Length; i++)
            {
                string k = KnownFolders[i];
                if (l == k || l.StartsWith(k + " ", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // ---------------- 公共 ----------------

        private static void Add(List<InstalledGame> found, Dictionary<string, InstalledGame> seen,
            string name, string exe, string source)
        {
            Add(found, seen, name, exe, source, "");
        }

        private static void Add(List<InstalledGame> found, Dictionary<string, InstalledGame> seen,
            string name, string exe, string source, string launchUri)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
            string norm = NormalizeExePath(exe);
            if (norm.Length == 0) return;

            string uri = launchUri ?? "";
            InstalledGame existing;
            if (seen.TryGetValue(norm, out existing))
            {
                if (existing.LaunchUri.Length == 0 && uri.Length > 0)
                {
                    existing.LaunchUri = uri;
                    existing.Source = source;
                }
                return;
            }

            var g = new InstalledGame();
            // 名字统一在这里收口:平台给的真名保留,机器名换成游戏名。
            g.Name = GameDb.DisplayName(name, exe);
            if (g.Name.Length == 0) g.Name = name;
            g.ExePath = exe;
            g.Source = source;
            g.LaunchUri = uri;
            seen[norm] = g;
            found.Add(g);
        }

        private static string NormalizeExePath(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return "";
            try
            {
                return Path.GetFullPath(exe).Trim().TrimEnd('\\');
            }
            catch
            {
                return exe.Trim().Replace('/', '\\').TrimEnd('\\');
            }
        }

        private static bool LooksLikeTool(string name)
        {
            string l = (name ?? "").ToLowerInvariant();
            if (l.Length == 0) return true;
            string[] bad = new string[]
            {
                "soundtrack", "dedicated server", "steamworks", "proton", "steam linux runtime",
                "redistributable", "wallpaper engine", "3dmark", "benchmark", "sdk", "runtime",
                "visual c++", "directx", ".net framework", "更新程序", "运行库",
                // 反作弊组件自己也有一条卸载项,名字就是 AntiCheatExpert 之类。
                "anticheat", "anti-cheat", "反作弊", "battleye", "easyanticheat"
            };
            for (int i = 0; i < bad.Length; i++)
                if (l.IndexOf(bad[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // 平台客户端本身不是游戏:WeGame/Steam 这类目录也在「游戏根目录」里,
            // 不按名字排掉就会把启动器自己收进我的游戏。
            if (IsLauncherName(l)) return true;
            return false;
        }

        private static bool IsLauncherName(string l)
        {
            string[] launchers = new string[]
            {
                "wegame", "steam", "epic games launcher", "gog galaxy", "battle.net",
                "blizzard battle.net", "origin", "ea app", "ea desktop", "ubisoft connect",
                "uplay", "riot client", "rockstar games launcher", "xbox",
                "minecraft launcher", "geforce experience", "playnite", "game center"
            };
            for (int i = 0; i < launchers.Length; i++)
                if (l == launchers[i]) return true;
            return false;
        }

        /// <summary>在游戏目录里挑一个最像"主程序"的 exe(Unreal/Unity/自研都覆盖)。</summary>
        private static string FindExe(string dir, string hint)
        {
            return FindExe(dir, hint, 20);
        }

        private static string FindExe(string dir, string hint, int minScore)
        {
            if (string.IsNullOrEmpty(dir)) return "";
            try { if (!Directory.Exists(dir)) return ""; }
            catch { return ""; }

            var files = new List<string>();
            CollectExes(dir, 0, files);
            string best = "";
            int bestScore = minScore;
            long bestSize = 0;
            for (int i = 0; i < files.Count; i++)
            {
                int sc;
                long size;
                ScoreExe(files[i], hint, out sc, out size);
                if (sc <= 0) continue;
                if (sc > bestScore || (sc == bestScore && size > bestSize))
                {
                    best = files[i];
                    bestScore = sc;
                    bestSize = size;
                }
            }
            return best;
        }

        private static void CollectExes(string dir, int depth, List<string> files)
        {
            // 累计看过的目录数:大目录里递归很深时,别让扫描拖住界面。
            CollectExes(dir, depth, files, new int[] { 900 });
        }

        private static void CollectExes(string dir, int depth, List<string> files, int[] budget)
        {
            // 鸣潮的主程序在 <游戏根>\xx Game\Client\Binaries\Win64\ 下,要走 4~5 层。
            if (depth > 5 || files.Count >= 300 || budget[0] <= 0) return;
            budget[0]--;
            if (SkipDir(Path.GetFileName(dir))) return;

            string[] exes;
            try { exes = Directory.GetFiles(dir, "*.exe"); }
            catch { exes = new string[0]; }
            for (int i = 0; i < exes.Length && files.Count < 300; i++) files.Add(exes[i]);

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return; }
            for (int i = 0; i < subs.Length && budget[0] > 0; i++)
            {
                try
                {
                    if ((File.GetAttributes(subs[i]) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                CollectExes(subs[i], depth + 1, files, budget);
            }
        }

        private static bool SkipDir(string name)
        {
            string l = (name ?? "").ToLowerInvariant();
            if (l.Length == 0 || l.StartsWith(".")) return true;

            // 反作弊 / 运行库目录里面一定没有游戏本体(Win64 下的 AntiCheatExpert
            // 里只有 ACE-Setup64.exe),整棵子树都不用进去。
            string[] never = new string[]
            {
                "anticheat", "anticheatexpert", "battleye", "easyanticheat",
                "redist", "redistributable", "vcredist", "directx", "dotnet", "_commonredist"
            };
            for (int i = 0; i < never.Length; i++)
                if (l.StartsWith(never[i], StringComparison.Ordinal)) return true;

            string[] bad = new string[]
            {
                "support", "tools", "tool", "sdk", "crash", "crashreport", "engine",
                "cef", "steamworks", "plugins", "cache", "logs"
            };
            for (int i = 0; i < bad.Length; i++)
                if (l == bad[i]) return true;
            return false;
        }

        private static void ScoreExe(string path, string hint, out int score, out long size)
        {
            score = 5;
            size = 0;
            string stem = Path.GetFileNameWithoutExtension(path);
            string stemLow = (stem ?? "").ToLowerInvariant();
            string low = path.ToLowerInvariant().Replace('/', '\\');

            if (BadStem(stemLow)) { score = 0; return; }
            try { size = new FileInfo(path).Length; } catch { }

            if (GameDb.Known(stem)) score += 120;
            if (stemLow.IndexOf("shipping", StringComparison.Ordinal) >= 0) score += 45;
            if (low.IndexOf("\\binaries\\win64\\", StringComparison.Ordinal) >= 0) score += 40;
            if (low.IndexOf("\\binaries\\win32\\", StringComparison.Ordinal) >= 0) score += 25;

            string dir = "";
            try { dir = Path.GetDirectoryName(path); } catch { }
            if (!string.IsNullOrEmpty(dir))
            {
                try
                {
                    if (Directory.Exists(Path.Combine(dir, stem + "_Data"))) score += 55;
                    if (File.Exists(Path.Combine(dir, "UnityPlayer.dll"))) score += 45;
                }
                catch { }
                try
                {
                    if (File.Exists(Path.Combine(dir, stem + ".pck"))) score += 45;
                }
                catch { }
            }

            score += (int)Math.Min(30, size / (20L * 1024 * 1024));

            if (stemLow.IndexOf("launcher") >= 0) score -= 45;
            if (stemLow.StartsWith("start", StringComparison.Ordinal)) score -= 20;
            if (stemLow.IndexOf("config", StringComparison.Ordinal) >= 0) score -= 30;
            if (stemLow.IndexOf("settings", StringComparison.Ordinal) >= 0) score -= 30;
            if (low.IndexOf("\\redist", StringComparison.Ordinal) >= 0) score -= 60;

            if (!string.IsNullOrEmpty(hint))
            {
                string h = hint.ToLowerInvariant();
                h = h.Replace(" ", "").Replace(":", "").Replace("'", "").Replace("-", "");
                string s = stemLow.Replace(" ", "").Replace(":", "").Replace("'", "").Replace("-", "");
                if (h.Length >= 3 && s.Length >= 3 && (s.IndexOf(h, StringComparison.Ordinal) >= 0
                    || h.IndexOf(s, StringComparison.Ordinal) >= 0))
                    score += 25;
            }
        }

        private static bool BadStem(string stem)
        {
            if (stem.Length == 0) return true;
            string[] bad = new string[]
            {
                "unins", "setup", "install", "installer", "dxsetup", "dxwebsetup", "vcredist",
                "dotnet", "crashhandler", "crashreport", "crashpad", "eac", "battleye",
                "easyanticheat", "vc_redist", "steamworks"
            };
            for (int i = 0; i < bad.Length; i++)
                if (stem.StartsWith(bad[i], StringComparison.Ordinal)) return true;

            // 反作弊安装器(ACE-Setup64 之类)和启动器都不是游戏本体;
            // 鸣潮的 launcher.exe 就躺在安装目录第一层,很容易被误选。
            if (stem.IndexOf("anticheat", StringComparison.Ordinal) >= 0) return true;
            if (stem.StartsWith("ace-", StringComparison.Ordinal)
                || stem.StartsWith("ace_", StringComparison.Ordinal)) return true;
            if (stem == "launcher" || stem.EndsWith("launcher", StringComparison.Ordinal)) return true;

            // 启动器自己的 exe 不能当游戏主程序,否则「启动游戏」只会拉起平台。
            string[] launchers = new string[]
            {
                "wegame", "steam", "steamwebhelper", "epicgameslauncher", "galaxyclient",
                "battle.net", "origin", "eadesktop", "ubisoftconnect", "uplay",
                "riotclient", "rockstarlauncher", "xboxapp"
            };
            for (int i = 0; i < launchers.Length; i++)
                if (stem == launchers[i]) return true;
            return false;
        }

        private static string RegStr(RegistryKey root, string path, string name)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(path))
                {
                    if (k == null) return "";
                    object v = k.GetValue(name);
                    return v == null ? "" : v.ToString().Trim();
                }
            }
            catch { return ""; }
        }

        private static string Val(RegistryKey k, string name)
        {
            try
            {
                object v = k.GetValue(name);
                return v == null ? "" : v.ToString().Trim();
            }
            catch { return ""; }
        }
    }
}