using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace FrameForge.Sys
{
    /// <summary>
    /// 进程名 → 游戏名。三层识别:
    /// 1) 用户自己的 games.ini(优先级最高);
    /// 2) 内置库(常见游戏的启动进程名,含 Unreal 的 *-Win64-Shipping);
    /// 3) 进程文件里的产品名 / 安装目录名,最后才退回进程名。
    /// </summary>
    public static class GameDb
    {
        public static string FilePath { get { return Path.Combine(Settings.Dir, "games.ini"); } }

        // "中文名|English name"
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "gta5", "侠盗猎车手5|Grand Theft Auto V" },
            { "gta5_enhanced", "侠盗猎车手5 增强版|Grand Theft Auto V Enhanced" },
            { "gtaiv", "侠盗猎车手4|Grand Theft Auto IV" },
            { "rdr2", "荒野大镖客：救赎2|Red Dead Redemption 2" },
            { "cyberpunk2077", "赛博朋克2077|Cyberpunk 2077" },
            { "witcher3", "巫师3：狂猎|The Witcher 3: Wild Hunt" },
            { "r5apex", "Apex英雄|Apex Legends" },
            { "rainbowsix", "彩虹六号：围攻|Rainbow Six Siege" },
            { "rainbowsix_vulkan", "彩虹六号：围攻|Rainbow Six Siege" },
            { "rainbowsix_dx11", "彩虹六号：围攻|Rainbow Six Siege" },
            { "valorant", "无畏契约|VALORANT" },
            { "valorant-win64-shipping", "无畏契约|VALORANT" },
            { "cs2", "反恐精英2|Counter-Strike 2" },
            { "csgo", "反恐精英：全球攻势|Counter-Strike: Global Offensive" },
            { "hl2", "半条命2|Half-Life 2" },
            { "l4d2", "求生之路2|Left 4 Dead 2" },
            { "portal2", "传送门2|Portal 2" },
            { "overwatch", "守望先锋|Overwatch 2" },
            { "fortniteclient-win64-shipping", "堡垒之夜|Fortnite" },
            { "rocketleague", "火箭联盟|Rocket League" },
            { "league of legends", "英雄联盟|League of Legends" },
            { "leagueoflegends", "英雄联盟|League of Legends" },
            { "tslgame", "绝地求生|PUBG: BATTLEGROUNDS" },
            { "pubg", "绝地求生|PUBG: BATTLEGROUNDS" },
            { "narakabladepoint", "永劫无间|NARAKA: BLADEPOINT" },
            { "blackdesert64", "黑色沙漠|Black Desert" },
            { "dnf", "地下城与勇士|Dungeon & Fighter" },
            { "crossfire", "穿越火线|CrossFire" },
            { "genshinimpact", "原神|Genshin Impact" },
            { "yuanshen", "原神|Genshin Impact" },
            { "starrail", "崩坏：星穹铁道|Honkai: Star Rail" },
            { "zenlesszonezero", "绝区零|Zenless Zone Zero" },
            { "bh3", "崩坏3|Honkai Impact 3rd" },
            { "honorofkings", "王者荣耀|Honor of Kings" },
            { "deltaforce", "三角洲行动|Delta Force" },
            { "deltaforceclient-win64-shipping", "三角洲行动|Delta Force" },
            { "arena breakout", "暗区突围|Arena Breakout: Infinite" },
            { "escapefromtarkov", "逃离塔科夫|Escape from Tarkov" },
            { "huntgame", "猎杀：对决|Hunt: Showdown 1896" },
            { "deadlock", "死锁|Deadlock" },
            { "marvelrivals", "漫威争锋|Marvel Rivals" },
            { "marvel-win64-shipping", "漫威争锋|Marvel Rivals" },
            { "discovery", "总决赛|THE FINALS" },
            { "xdefiant", "不羁联盟|XDefiant" },
            { "fragpunk", "界外狂潮|FragPunk" },
            { "bf1", "战地1|Battlefield 1" },
            { "bf4", "战地4|Battlefield 4" },
            { "bfv", "战地5|Battlefield V" },
            { "bf2042", "战地2042|Battlefield 2042" },
            { "titanfall2", "泰坦陨落2|Titanfall 2" },
            { "modernwarfare", "使命召唤：现代战争|Call of Duty: Modern Warfare" },
            { "modernwarfare2", "使命召唤：现代战争II|Call of Duty: Modern Warfare II" },
            { "mw3", "使命召唤：现代战争III|Call of Duty: Modern Warfare III" },
            { "blackops6", "使命召唤：黑色行动6|Call of Duty: Black Ops 6" },
            { "cod", "使命召唤|Call of Duty" },
            { "codhq", "使命召唤：总部|Call of Duty HQ" },
            { "haloinfinite", "光环：无限|Halo Infinite" },
            { "mcc-win64-shipping", "光环：士官长合集|Halo: The Master Chief Collection" },
            { "destiny2", "命运2|Destiny 2" },
            { "warframe.x64", "星际战甲|Warframe" },
            { "warframe", "星际战甲|Warframe" },
            { "pathofexile", "流放之路|Path of Exile" },
            { "pathofexilesteam", "流放之路|Path of Exile" },
            { "diabloiv", "暗黑破坏神IV|Diablo IV" },
            { "diablo iii64", "暗黑破坏神III|Diablo III" },
            { "wow", "魔兽世界|World of Warcraft" },
            { "wowclassic", "魔兽世界经典怀旧服|World of Warcraft Classic" },
            { "ffxiv_dx11", "最终幻想XIV|Final Fantasy XIV" },
            { "ffxiv", "最终幻想XIV|Final Fantasy XIV" },
            { "lostark", "失落的方舟|Lost Ark" },
            { "neworld", "新世界|New World" },
            { "b1-win64-shipping", "黑神话：悟空|Black Myth: Wukong" },
            { "blackmythwukong", "黑神话：悟空|Black Myth: Wukong" },
            { "eldenring", "艾尔登法环|Elden Ring" },
            { "sekiro", "只狼：影逝二度|Sekiro: Shadows Die Twice" },
            { "darksoulsiii", "黑暗之魂3|Dark Souls III" },
            { "armoredcore6", "装甲核心6|Armored Core VI" },
            { "liesofp", "匹诺曹的谎言|Lies of P" },
            { "nioh2", "仁王2|Nioh 2" },
            { "wolong", "卧龙：苍天陨落|Wo Long: Fallen Dynasty" },
            { "stellarblade", "剑星|Stellar Blade" },
            { "monsterhunterworld", "怪物猎人：世界|Monster Hunter: World" },
            { "monsterhunterrise", "怪物猎人：崛起|Monster Hunter Rise" },
            { "monsterhunterwilds", "怪物猎人：荒野|Monster Hunter Wilds" },
            { "residentevil4", "生化危机4 重制版|Resident Evil 4" },
            { "re4", "生化危机4 重制版|Resident Evil 4" },
            { "re2", "生化危机2 重制版|Resident Evil 2" },
            { "re3", "生化危机3 重制版|Resident Evil 3" },
            { "re8", "生化危机8：村庄|Resident Evil Village" },
            { "devilmaycry5", "鬼泣5|Devil May Cry 5" },
            { "hogwartslegacy", "霍格沃茨之遗|Hogwarts Legacy" },
            { "starfield", "星空|Starfield" },
            { "fallout4", "辐射4|Fallout 4" },
            { "skyrimse", "上古卷轴5：天际|The Elder Scrolls V: Skyrim" },
            { "baldursgate3", "博德之门3|Baldur's Gate 3" },
            { "bg3", "博德之门3|Baldur's Gate 3" },
            { "bg3_dx11", "博德之门3|Baldur's Gate 3" },
            { "bg3_vulkan", "博德之门3|Baldur's Gate 3" },
            { "ittakestwo", "双人成行|It Takes Two" },
            { "splitfiction", "双影奇境|Split Fiction" },
            { "palworld", "幻兽帕鲁|Palworld" },
            { "palworld-win64-shipping", "幻兽帕鲁|Palworld" },
            { "valheim", "英灵神殿|Valheim" },
            { "lethalcompany", "致命公司|Lethal Company" },
            { "helldivers2", "绝地潜兵2|Helldivers 2" },
            { "deeprockgalactic", "深岩银河|Deep Rock Galactic" },
            { "phasmophobia", "恐鬼症|Phasmophobia" },
            { "amongus", "我们之中|Among Us" },
            { "terraria", "泰拉瑞亚|Terraria" },
            { "stardew valley", "星露谷物语|Stardew Valley" },
            { "minecraft.windows", "我的世界|Minecraft" },
            { "javaw", "我的世界|Minecraft" },
            { "robloxplayerbeta", "罗布乐思|Roblox" },
            { "osu!", "osu!" },
            { "geometrydash", "几何冲刺|Geometry Dash" },
            { "rustclient", "腐蚀|Rust" },
            { "dayz_x64", "DayZ|DayZ" },
            { "shootergame", "方舟：生存进化|ARK: Survival Evolved" },
            { "conansandbox", "流放者柯南|Conan Exiles" },
            { "grounded", "禁闭求生|Grounded" },
            { "subnautica", "深海迷航|Subnautica" },
            { "subnauticazero", "深海迷航：零度之下|Subnautica: Below Zero" },
            { "nms", "无人深空|No Man's Sky" },
            { "elitedangerous64", "精英：危险|Elite Dangerous" },
            { "starcitizen", "星际公民|Star Citizen" },
            { "squadgame", "战术小队|Squad" },
            { "arma3_x64", "武装突袭3|Arma 3" },
            { "aces", "战争雷霆|War Thunder" },
            { "worldoftanks", "坦克世界|World of Tanks" },
            { "worldofwarships", "战舰世界|World of Warships" },
            { "crossout", "创世战车|Crossout" },
            { "forzahorizon5", "极限竞速：地平线5|Forza Horizon 5" },
            { "forzahorizon4", "极限竞速：地平线4|Forza Horizon 4" },
            { "forzamotorsport", "极限竞速|Forza Motorsport" },
            { "microsoftflightsimulator", "微软模拟飞行|Microsoft Flight Simulator" },
            { "flightsimulator2024", "微软模拟飞行2024|Microsoft Flight Simulator 2024" },
            { "ts4_x64", "模拟人生4|The Sims 4" },
            { "cities", "城市：天际线|Cities: Skylines" },
            { "cities2", "城市：天际线II|Cities: Skylines II" },
            { "anno1800", "纪元1800|Anno 1800" },
            { "factorio", "异星工厂|Factorio" },
            { "rimworld", "环世界|RimWorld" },
            { "satisfactory-win64-shipping", "幸福工厂|Satisfactory" },
            { "civilizationvi", "文明6|Civilization VI" },
            { "civ6", "文明6|Civilization VI" },
            { "ageofempiresiv", "帝国时代4|Age of Empires IV" },
            { "totalwarwarhammer3", "全面战争：战锤3|Total War: WARHAMMER III" },
            { "hoi4", "钢铁雄心4|Hearts of Iron IV" },
            { "stellaris", "群星|Stellaris" },
            { "eu4", "欧陆风云4|Europa Universalis IV" },
            { "ck3", "十字军之王3|Crusader Kings III" },
            { "frostpunk2", "冰汽时代2|Frostpunk 2" },
            { "fifa23", "FIFA 23|FIFA 23" },
            { "fc24", "EA SPORTS FC 24|EA SPORTS FC 24" },
            { "fc25", "EA SPORTS FC 25|EA SPORTS FC 25" },
            { "pes2021", "eFootball PES 2021|eFootball PES 2021" },
            { "nba2k24", "NBA 2K24|NBA 2K24" },
            { "nba2k25", "NBA 2K25|NBA 2K25" },
            { "gtfo", "GTFO|GTFO" },
            { "theforest", "森林|The Forest" },
            { "sonsoftheforest", "森林之子|Sons of the Forest" },
            { "dyinglight2_x64", "消逝的光芒2|Dying Light 2" },
            { "deadisland2", "死亡岛2|Dead Island 2" },
            { "deadbydaylight-win64-shipping", "黎明杀机|Dead by Daylight" },
            { "outlast2", "逃生2|Outlast 2" },
            { "hollow_knight", "空洞骑士|Hollow Knight" },
            { "hades", "哈迪斯|Hades" },
            { "hades2", "哈迪斯II|Hades II" },
            { "celeste", "蔚蓝|Celeste" },
            { "cuphead", "茶杯头|Cuphead" },
            { "deadcells", "死亡细胞|Dead Cells" },
            { "vampire_survivors", "吸血鬼幸存者|Vampire Survivors" },
            { "balatro", "小丑牌|Balatro" },
            { "slaythespire", "杀戮尖塔|Slay the Spire" },
            { "dave the diver", "潜水员戴夫|Dave the Diver" },
            { "stray", "迷失|Stray" },
            { "detroitbecomehuman", "底特律：变人|Detroit: Become Human" },
            { "ds", "死亡搁浅|Death Stranding" },
            { "ghostoftsushima", "对马岛之魂|Ghost of Tsushima" },
            { "gowr", "战神：诸神黄昏|God of War Ragnarok" },
            { "gow", "战神|God of War" },
            { "horizonzerodawn", "地平线：零之曙光|Horizon Zero Dawn" },
            { "horizonforbiddenwest", "地平线：西之绝境|Horizon Forbidden West" },
            { "spider-man", "漫威蜘蛛侠|Marvel's Spider-Man" },
            { "tlou", "最后生还者|The Last of Us" },
            { "uncharted", "神秘海域4|Uncharted 4" },
            { "deadspace", "死亡空间|Dead Space" },
            { "alanwake2", "心灵杀手2|Alan Wake 2" },
            { "control", "控制|Control" },
            { "stalker2-win64-shipping", "潜行者2：切尔诺贝利之心|S.T.A.L.K.E.R. 2" },
            { "avowed", "宣誓|Avowed" },
            { "payday3", "收获日3|Payday 3" },
            { "remnant2", "遗迹2|Remnant 2" },
            { "spacemarine2", "战锤40K：星际战士2|Warhammer 40,000: Space Marine 2" },
            { "once human", "七日世界|Once Human" },
            { "oncehuman-win64-shipping", "七日世界|Once Human" },
            // 我的世界:基岩版进程固定叫 Minecraft.Windows.exe(上面已收录);Java 版是 javaw.exe,
            // 只能靠窗口标题认(见 FromWindow)。
            { "minecraft", "我的世界|Minecraft" },
            { "minecraftlauncher", "我的世界启动器|Minecraft Launcher" }
        };

        // 安装目录名 → 游戏名。用于 Client-Win64-Shipping.exe 这类所有 Unreal 游戏共用的进程名。
        private static readonly Dictionary<string, string> FolderMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "wuthering waves", "鸣潮|Wuthering Waves" },
            { "wuthering waves game", "鸣潮|Wuthering Waves" },
            { "wutheringwaves", "鸣潮|Wuthering Waves" },
            { "wuwa", "鸣潮|Wuthering Waves" },
            { "鸣潮", "鸣潮|Wuthering Waves" },
            { "blackmythwukong", "黑神话：悟空|Black Myth: Wukong" },
            { "narakabladepoint", "永劫无间|NARAKA: BLADEPOINT" },
            { "palworld", "幻兽帕鲁|Palworld" },
            { "marvelrivals", "漫威争锋|Marvel Rivals" },
            { "deltaforce", "三角洲行动|Delta Force" },
            { "arena breakout", "暗区突围|Arena Breakout: Infinite" },
            { "thefinals", "总决赛|THE FINALS" },
            { "deadbydaylight", "黎明杀机|Dead by Daylight" },
            { "fortnite", "堡垒之夜|Fortnite" },
            { "satisfactory", "幸福工厂|Satisfactory" },
            { "hogwarts legacy", "霍格沃茨之遗|Hogwarts Legacy" },
            { "stalker2", "潜行者2：切尔诺贝利之心|S.T.A.L.K.E.R. 2" },
            { "avowed", "宣誓|Avowed" },
            { "indianajones", "夺宝奇兵：古老之圈|Indiana Jones and the Great Circle" },
            { "multiversus", "多元宇宙大乱斗|MultiVersus" },
            { "gotham knights", "哥谭骑士|Gotham Knights" },
            { "dead island 2", "死亡岛2|Dead Island 2" },
            { "payday3", "收获日3|Payday 3" },
            { "remnant2", "遗迹2|Remnant 2" },
            { "spacemarine2", "战锤40K：星际战士2|Warhammer 40,000: Space Marine 2" },
            { "once human", "七日世界|Once Human" },
            { "thenightreign", "艾尔登法环：黑夜君临|Elden Ring Nightreign" },
            { "minecraft.windows", "我的世界|Minecraft" },
            { "minecraft", "我的世界|Minecraft" }
        };

        private static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 进程名 → exe 全路径。读程序图标要用;进程退出后仍能靠记下的路径继续显示图标。
        private static readonly Dictionary<string, string> paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 进程名 → 上次没找到的时刻。列表每次重绘都要图标,找不到时别反复扫进程。
        private static readonly Dictionary<string, DateTime> miss = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // pid → exe 路径。读 MainModule 要跨进程枚举模块,空闲扫描每 1.5 秒就会把
        // 所有有窗口的进程问一遍,缓存下来能把这部分开销抹掉。
        private static readonly Dictionary<int, CacheEntry> pidPaths = new Dictionary<int, CacheEntry>();
        // 进程名 → 认不出来时的兜底名字。目录名/窗口标题短时间内不会变。
        private static readonly Dictionary<string, CacheEntry> fallback = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private const int PathTtlSec = 20;
        private const int FallbackTtlSec = 8;

        /// <summary>带时刻的缓存值。pid 会被复用,靠启动时刻识别出来的是不是同一个进程。</summary>
        private sealed class CacheEntry
        {
            public string Text;
            public long Start;
            public DateTime At;
        }

        // exe 文件名 → 完整路径,来自资源管理器缓存(旧战绩记录只有进程名时靠它补图标)。
        private static Dictionary<string, string> mui;
        private static Dictionary<string, string> user;
        private static bool userLoaded;

        /// <summary>进程名 → 当前语言下的游戏名。取不到时返回美化后的进程名。</summary>
        public static string Title(int pid, string exe)
        {
            string pair = Resolve(pid, exe);
            int bar = pair.IndexOf('|');
            if (bar < 0) return pair;
            return I18n.Lang == "en" ? pair.Substring(bar + 1) : pair.Substring(0, bar);
        }

        public static string Title(string exe)
        {
            return Title(-1, exe);
        }

        /// <summary>是否命中内置库或用户配置(不是猜出来的)。</summary>
        public static bool Known(string exe)
        {
            string key = Norm(exe);
            if (key.Length == 0) return false;
            LoadUser();
            if (user.ContainsKey(key)) return true;
            return Map.ContainsKey(key);
        }

        /// <summary>给界面显示来源:内置库 / 用户配置 / 文件信息 / 进程名。</summary>
        public static string Source(int pid, string exe)
        {
            string key = Norm(exe);
            if (key.Length == 0) return "";
            LoadUser();
            if (user.ContainsKey(key)) return "user";
            if (Map.ContainsKey(key)) return "db";
            return "auto";
        }

        public static void ClearCache()
        {
            cache.Clear();
            paths.Clear();
            miss.Clear();
            pidPaths.Clear();
            fallback.Clear();
            mui = null;
            user = null;
            userLoaded = false;
        }

        private static string Resolve(int pid, string exe)
        {
            string key = Norm(exe);
            if (key.Length == 0) return "";

            string hit;
            if (cache.TryGetValue(key, out hit)) return hit;

            // 所有 Java 游戏的进程都叫 javaw.exe,进程名毫无意义,
            // 只能读窗口标题;认不出时显示进程名,不要留空。
            if (IsJava(key))
            {
                string jav = FromWindow(pid);
                return jav.Length > 0 ? jav : Pretty(key);
            }

            string pair = "";
            LoadUser();
            if (user.TryGetValue(key, out pair)) { cache[key] = pair; return pair; }
            if (Map.TryGetValue(key, out pair)) { cache[key] = pair; return pair; }

            string path = ProcessPath(pid);
            if (path.Length > 0) paths[key] = path;

            pair = FolderHint(path);
            if (pair.Length > 0) { cache[key] = pair; return pair; }

            pair = FromFile(path, key);
            if (pair.Length > 0) { cache[key] = pair; return pair; }

            // 真不认识:先看安装目录,再读窗口标题(鸣潮这类 Unreal 游戏的窗口标题
            // 就是游戏名),最后按用户要求退回进程名——总比显示"当前游戏"好。
            // 结果随进程变化,不写长效缓存,避免污染同名进程(Client-Win64-Shipping);
            // 但窗口标题要跨进程读,同一个进程短时间内重复问没有意义,只留几秒。
            CacheEntry fb;
            if (fallback.TryGetValue(key, out fb) && (DateTime.UtcNow - fb.At).TotalSeconds < FallbackTtlSec)
                return fb.Text;

            string pretty = IsGeneric(key) ? FolderName(path) : "";
            if (Junk(pretty, key)) pretty = "";
            if (pretty.Length == 0) pretty = WindowName(pid, key);
            if (pretty.Length == 0) pretty = ExeLabel(exe);
            if (pretty.Length > 0)
            {
                if (fallback.Count > 64) fallback.Clear();
                var e = new CacheEntry();
                e.Text = pretty;
                e.At = DateTime.UtcNow;
                fallback[key] = e;
            }
            return pretty;
        }

        /// <summary>进程名当游戏名:保留原始大小写,去掉 .exe。</summary>
        private static string ExeLabel(string exe)
        {
            string s = exe == null ? "" : exe.Trim();
            int slash = s.LastIndexOf('\\');
            if (slash >= 0) s = s.Substring(slash + 1);
            int dot = s.LastIndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            return s.Length > 0 ? s : "";
        }

        /// <summary>读窗口标题当游戏名(Unreal 游戏的标题通常就是游戏名)。</summary>
        private static string WindowName(int pid, string key)
        {
            if (pid <= 0) return "";
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    string t = Clean(p.MainWindowTitle);
                    if (t.Length == 0 || t.Length > 64) return "";
                    if (Junk(t, key)) return "";
                    return t;
                }
            }
            catch { return ""; }
        }

        private static void LoadUser()
        {
            if (userLoaded) return;
            userLoaded = true;
            user = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = Norm(line.Substring(0, eq));
                    string v = line.Substring(eq + 1).Trim();
                    if (k.Length > 0 && v.Length > 0) user[k] = v;
                }
            }
            catch (Exception ex) { Log.Write("games.ini: " + ex.Message); }
        }

        private static string ProcessPath(int pid)
        {
            if (pid <= 0) return "";

            CacheEntry hit;
            if (pidPaths.TryGetValue(pid, out hit) && (DateTime.UtcNow - hit.At).TotalSeconds < PathTtlSec)
            {
                // pid 会被复用:启动时刻对得上才说明还是同一个进程。
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                        if (p.StartTime.ToUniversalTime().Ticks == hit.Start) return hit.Text;
                }
                catch { return ""; }
                pidPaths.Remove(pid);
            }

            string path = "";
            long start = 0;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    start = p.StartTime.ToUniversalTime().Ticks;
                    path = p.MainModule.FileName;
                }
            }
            catch { }

            // 只缓存读成功的:权限不足之类的失败下次可能就好了,别把它记成结论。
            if (path != null && path.Length > 0 && start != 0)
            {
                if (pidPaths.Count > 96) PrunePidPaths();
                var e = new CacheEntry();
                e.Text = path;
                e.Start = start;
                e.At = DateTime.UtcNow;
                pidPaths[pid] = e;
            }
            return path == null ? "" : path;
        }

        /// <summary>清掉过期的 pid 缓存;进程实在太多时整体丢弃，下次重新读。</summary>
        private static void PrunePidPaths()
        {
            DateTime now = DateTime.UtcNow;
            List<int> drop = new List<int>();
            foreach (KeyValuePair<int, CacheEntry> kv in pidPaths)
                if ((now - kv.Value.At).TotalSeconds >= PathTtlSec) drop.Add(kv.Key);
            for (int i = 0; i < drop.Count; i++) pidPaths.Remove(drop[i]);
            if (pidPaths.Count > 128) pidPaths.Clear();
        }

        /// <summary>取某个进程的 exe 路径,取不到返回空。</summary>
        public static string PathOf(int pid)
        {
            string p = ProcessPath(pid);
            return p == null ? "" : p;
        }

        /// <summary>
        /// 按进程名找 exe 路径:先查记下的路径,再扫描同名运行中的进程,
        /// 最后从资源管理器缓存(MuiCache)里找曾经运行过的程序。
        /// 用来给战绩、报告、保护名单读程序自己的图标。
        /// </summary>
        public static string PathFor(string exe)
        {
            string key = Norm(exe);
            if (key.Length == 0) return "";

            string p;
            if (paths.TryGetValue(key, out p) && File.Exists(p)) return p;

            // 列表每次重绘都会来问一遍,短时间内找不到就别反复扫进程。
            DateTime at;
            if (miss.TryGetValue(key, out at) && (DateTime.UtcNow - at).TotalSeconds < 8) return "";

            p = RunningPath(key);
            if (p.Length == 0) p = MuiPath(key + ".exe");

            if (p.Length == 0)
            {
                miss[key] = DateTime.UtcNow;
                return "";
            }
            miss.Remove(key);
            paths[key] = p;
            return p;
        }

        private static string RunningPath(string key)
        {
            Process[] ps;
            try { ps = Process.GetProcessesByName(key); }
            catch { return ""; }
            try
            {
                for (int i = 0; i < ps.Length; i++)
                {
                    try
                    {
                        string f = ps[i].MainModule.FileName;
                        if (!string.IsNullOrEmpty(f)) return f;
                    }
                    catch { }
                }
            }
            finally
            {
                for (int i = 0; i < ps.Length; i++) { try { ps[i].Dispose(); } catch { } }
            }
            return "";
        }

        /// <summary>按 exe 文件名查 MuiCache 里记下的完整路径,没有返回空。</summary>
        private static string MuiPath(string file)
        {
            if (mui == null) LoadMui();
            string p;
            return mui.TryGetValue(file, out p) ? p : "";
        }

        /// <summary>
        /// 资源管理器会把它见过的每个程序记在 MuiCache 里(值名就是 exe 完整路径),
        /// 旧战绩记录没存路径、游戏又没在跑时,靠这里才拿得到真实图标。
        /// </summary>
        private static void LoadMui()
        {
            mui = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"))
                {
                    if (k == null) return;
                    string[] names = k.GetValueNames();
                    for (int i = 0; i < names.Length; i++)
                    {
                        string p = MuiExe(names[i]);
                        if (p.Length == 0) continue;
                        string file = Path.GetFileName(p);
                        if (file.Length == 0) continue;

                        // 同名 exe 会有一堆(launcher.exe),优先留在游戏目录里的那个。
                        string old;
                        if (mui.TryGetValue(file, out old) && !(UnderLibrary(p) && !UnderLibrary(old)))
                            continue;
                        mui[file] = p;
                    }
                }
            }
            catch (Exception ex) { Log.Write("muicache: " + ex.Message); }
        }

        /// <summary>MuiCache 的值名是「exe 完整路径.FriendlyAppName」,还原成 exe 路径。</summary>
        private static string MuiExe(string name)
        {
            const string friendly = ".FriendlyAppName";
            const string company = ".ApplicationCompany";
            string p = name;
            if (p.EndsWith(friendly, StringComparison.OrdinalIgnoreCase))
                p = p.Substring(0, p.Length - friendly.Length);
            else if (p.EndsWith(company, StringComparison.OrdinalIgnoreCase))
                p = p.Substring(0, p.Length - company.Length);
            else return "";
            if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "";
            try { return File.Exists(p) ? p : ""; }
            catch { return ""; }
        }

        private static bool UnderLibrary(string path)
        {
            if (path.Length == 0) return false;
            string p = path.ToLowerInvariant().Replace('/', '\\');
            // 平台自带的启动器 / 内嵌浏览器 / 运行库也装在游戏目录下,但它们不是游戏。
            if (p.IndexOf("\\launcher\\") >= 0 || p.IndexOf("\\portal\\") >= 0
                || p.IndexOf("\\cef\\") >= 0 || p.IndexOf("\\commonredist\\") >= 0
                || p.IndexOf("\\redistributables\\") >= 0 || p.IndexOf("\\steamworks shared\\") >= 0)
                return false;
            for (int i = 0; i < Libraries.Length; i++)
                if (p.IndexOf(Libraries[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// 按引擎特征识别游戏本体的进程。内置库没收录、又没装在常见游戏目录里的
        /// 游戏(独立游戏、自建安装目录)靠这一条也能认出来:
        /// Unreal 的 Binaries\Win64 + *-Win64-Shipping、Unity 的 &lt;exe&gt;_Data 或 UnityPlayer.dll、
        /// Godot 的 .pck。只认游戏本体的产物,Unreal 编辑器等 Engine\Binaries 一律排除。
        /// </summary>
        public static bool EngineGame(int pid)
        {
            string path = ProcessPath(pid);
            if (path.Length == 0) return false;
            string p = path.ToLowerInvariant().Replace('/', '\\');
            if (p.IndexOf("\\engine\\binaries\\") >= 0) return false;
            if (p.IndexOf("\\binaries\\win64\\") >= 0 || p.IndexOf("\\binaries\\win32\\") >= 0) return true;
            if (p.IndexOf("-win64-shipping") >= 0 || p.IndexOf("-win32-shipping") >= 0) return true;
            try
            {
                string dir = Path.GetDirectoryName(path);
                string stem = Path.GetFileNameWithoutExtension(path);
                if (dir != null && stem != null && stem.Length > 0)
                {
                    if (Directory.Exists(Path.Combine(dir, stem + "_Data"))) return true;   // Unity
                    if (File.Exists(Path.Combine(dir, stem + ".pck"))) return true;         // Godot
                }
                if (dir != null && File.Exists(Path.Combine(dir, "UnityPlayer.dll"))) return true;
            }
            catch { }
            return false;
        }

        /// <summary>Java 版我的世界:进程名是 javaw,只能靠窗口标题判断。</summary>
        public static bool JavaGame(int pid)
        {
            return FromWindow(pid) == "我的世界|Minecraft";
        }

        // 常见游戏平台/库的安装根目录。放在这些目录下的程序基本就是游戏本体,
        // 内置库还没收录的新游戏、独立游戏靠这一条也能认出来。
        private static readonly string[] Libraries =
        {
            "\\steamapps\\", "\\steamlibrary\\",
            "\\epic games\\", "\\gog games\\", "\\gog galaxy\\",
            "\\xboxgames\\", "\\wegame\\", "\\riot games\\",
            "\\battle.net\\", "\\ea games", "\\ubisoft",
            "\\mihoyo\\", "\\米哈游\\", "\\cognosphere\\"
        };

        /// <summary>exe 是否位于常见游戏安装目录下(用于兜底识别内置库没有的游戏)。</summary>
        public static bool LibraryGame(int pid)
        {
            return UnderLibrary(ProcessPath(pid));
        }

        private static bool IsJava(string key)
        {
            return key == "java" || key == "javaw";
        }

        private static string FromWindow(int pid)
        {
            if (pid <= 0) return "";
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    string t = p.MainWindowTitle;
                    if (t == null || t.Length == 0) return "";
                    string l = t.ToLowerInvariant();
                    if (l.IndexOf("minecraft") < 0) return "";
                    if (l.IndexOf("launcher") >= 0 || l.IndexOf("启动器") >= 0)
                        return "我的世界启动器|Minecraft Launcher";
                    return "我的世界|Minecraft";
                }
            }
            catch { return ""; }
        }

        /// <summary>从安装目录名识别 Unreal 一类共用进程名的游戏。</summary>
        private static string FolderHint(string path)
        {
            if (path.Length == 0) return "";
            try
            {
                string dir = Path.GetDirectoryName(path);
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
                {
                    string name = Path.GetFileName(dir.TrimEnd('\\', '/'));
                    if (name.Length > 0)
                    {
                        string hit;
                        if (FolderMap.TryGetValue(name, out hit)) return hit;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return "";
        }

        /// <summary>读取 exe 的资源信息里的产品名 / 文件说明。</summary>
        private static string FromFile(string path, string key)
        {
            if (path.Length == 0) return "";
            try
            {
                FileVersionInfo vi = FileVersionInfo.GetVersionInfo(path);
                string t = Clean(vi.ProductName);
                if (Junk(t, key)) t = Clean(vi.FileDescription);
                if (Junk(t, key)) return "";
                return t;
            }
            catch { return ""; }
        }

        private static string FolderName(string path)
        {
            if (path.Length == 0) return "";
            try
            {
                string dir = Path.GetDirectoryName(path);
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
                {
                    string name = Path.GetFileName(dir.TrimEnd('\\', '/'));
                    string low = name.ToLowerInvariant();
                    if (low == "win64" || low == "win32" || low == "x64" || low == "binaries"
                        || low == "binariesx64" || low == "client" || low == "engine"
                        || low == "common" || low == "steamapps" || low == "content"
                        || low == "retail" || low == "shipping" || low == "games")
                    {
                        dir = Path.GetDirectoryName(dir);
                        continue;
                    }
                    return Clean(name);
                }
            }
            catch { }
            return "";
        }

        private static bool IsGeneric(string key)
        {
            if (key.StartsWith("client-") && key.EndsWith("-shipping")) return true;
            if (key.EndsWith("-win64-shipping") || key.EndsWith("-win32-shipping")) return true;
            switch (key)
            {
                case "shipping":
                case "game":
                case "launcher":
                case "start":
                case "run":
                case "java":
                case "javaw":
                    return true;
                default:
                    return false;
            }
        }

        private static bool Junk(string s, string key)
        {
            if (s.Length < 2) return true;
            string l = s.ToLowerInvariant();
            if (l == key || l == key + ".exe") return true;
            if (l.Contains("unreal")) return true;
            if (l.Contains("shipping")) return true;
            if (l.Contains("redistributable")) return true;
            if (l.Contains("runtime")) return true;
            if (l.StartsWith("microsoft")) return true;
            if (l.StartsWith("windows ")) return true;
            if (l.Contains(".exe")) return true;
            switch (l)
            {
                case "client":
                case "game":
                case "application":
                case "launcher":
                    return true;
                default: return false;
            }
        }

        private static string Clean(string s)
        {
            if (s == null) return "";
            s = s.Replace("\u2122", "").Replace("\u00ae", "").Replace("(TM)", "").Replace("(R)", "");
            var sb = new StringBuilder(s.Length);
            bool space = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    if (!space) sb.Append(' ');
                    space = true;
                }
                else { sb.Append(c); space = false; }
            }
            return sb.ToString().Trim();
        }

        private static string Pretty(string key)
        {
            var sb = new StringBuilder(key.Length + 4);
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c == '_') { sb.Append(' '); continue; }
                if (c == '-') { sb.Append(' '); continue; }
                sb.Append(c);
            }
            string s = sb.ToString().Trim();
            if (s.Length == 0) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static string Norm(string exe)
        {
            if (exe == null) return "";
            string s = exe.Trim();
            int slash = s.LastIndexOf('\\');
            if (slash >= 0) s = s.Substring(slash + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s.Trim().ToLowerInvariant();
        }
    }
}