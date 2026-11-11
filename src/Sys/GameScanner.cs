using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    /// <summary>一款本机游戏:显示名、启动 exe、来源平台。</summary>
    public sealed class InstalledGame
    {
        public string Name = "";
        public string ExePath = "";
        /// <summary>steam | epic | gog | reg | manual</summary>
        public string Source = "";

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
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try { ScanSteam(found, seen); }
            catch (Exception ex) { Log.Write("gamescan steam: " + ex.Message); }
            try { ScanEpic(found, seen); }
            catch (Exception ex) { Log.Write("gamescan epic: " + ex.Message); }
            try { ScanGog(found, seen); }
            catch (Exception ex) { Log.Write("gamescan gog: " + ex.Message); }
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

        private static void ScanSteam(List<InstalledGame> found, HashSet<string> seen)
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
                    string name = Acf(text, "name");
                    string installdir = Acf(text, "installdir");
                    if (name.Length == 0 || installdir.Length == 0) continue;
                    if (LooksLikeTool(name)) continue;
                    string dir = Path.Combine(Path.Combine(apps, "common"), installdir);
                    string exe = FindExe(dir, name);
                    Add(found, seen, name, exe, "steam");
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

        private static void ScanEpic(List<InstalledGame> found, HashSet<string> seen)
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
                Add(found, seen, name, exe, "epic");
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

        private static void ScanGog(List<InstalledGame> found, HashSet<string> seen)
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

        // ---------------- 卸载注册表 ----------------

        private static void ScanUninstall(List<InstalledGame> found, HashSet<string> seen)
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
            "infinity nikki", "无限暖暖"
        };

        /// <summary>
        /// 在各固定磁盘的常见安装位置找内置名单里的游戏目录。
        /// 只列目录名、命中才进去找 exe,开销很小。
        /// </summary>
        private static void ScanKnownDirs(List<InstalledGame> found, HashSet<string> seen)
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
                        root + "SteamLibrary\\steamapps\\common"
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

        private static void AddKnownDir(List<InstalledGame> found, HashSet<string> seen,
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

        private static void Add(List<InstalledGame> found, HashSet<string> seen,
            string name, string exe, string source)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
            if (!seen.Add(exe)) return;
            var g = new InstalledGame();
            // 名字统一在这里收口:平台给的真名保留,机器名换成游戏名。
            g.Name = GameDb.DisplayName(name, exe);
            if (g.Name.Length == 0) g.Name = name;
            g.ExePath = exe;
            g.Source = source;
            found.Add(g);
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