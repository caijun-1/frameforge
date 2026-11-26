using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace FrameForge.Sys
{
    /// <summary>
    /// 画面滤镜:通过 Windows 放大镜的全屏颜色矩阵实现,不需要注入、不需要驱动。
    /// 颜色矩阵作用于整个桌面,因此只在用户选择时启用,切出游戏会自动还原。
    ///
    /// 调色思路参考 ReShade 的 WhiteBalance 与 LiftGammaGain:
    /// 色温/色调走黑体辐射曲线换算成三通道增益,而不是只拉红蓝两根轴;
    /// 预设可在阴影里加一层薄色、在高光里加一层补色,做出胶片感的分割色调。
    /// 受 5×5 矩阵限制,LUT / 锐化 / 去色带 / 幂曲线这类逐像素效果仍然做不了。
    /// </summary>
    public static class Filter
    {
        public static readonly string[] PresetKeys =
        {
            "flt.none", "flt.vivid", "flt.soft", "flt.bright", "flt.cinema",
            "flt.snow", "flt.day", "flt.night", "flt.gray",
            "flt.benq", "flt.fog", "flt.comp", "flt.anime",
            // 热门游戏预设:索引必须接在通用预设后面,旧的 filterPreset 数值才不会错位。
            "flt.g.delta", "flt.g.valorant", "flt.g.genshin", "flt.g.apex",
            "flt.g.cs2", "flt.g.pubg", "flt.g.naraka", "flt.g.lol",
            // 新增通用预设只能往后追加,否则用户已保存的 filterPreset 会指到别的方案上。
            "flt.cool", "flt.warm"
        };

        /// <summary>通用预设个数,之后是游戏预设。</summary>
        public const int GeneralCount = 13;

        /// <summary>最后一个游戏预设的下标,之后追加的仍算通用预设。</summary>
        public const int LastGamePreset = 20;

        public static bool IsGamePreset(int i) { return i >= GeneralCount && i <= LastGamePreset; }

        private static bool probed;       // 是否探测过本机是否支持放大镜
        private static bool available;    // 探测结果
        private static bool active;       // 当前是否初始化着(占着 DWM 的合成链)
        private static float[] last = null;
        // 最近一次真正卸下放大镜的时间。截图工具收起时前台会在取色层和主窗口之间来回跳,
        // 守卫跟着抖就会反复 MagInitialize / MagUninitialize 重建 DWM 上的效果链,
        // 那比常驻还容易把整个桌面卡住,所以卸下后先静一会儿再装回去。
        private static DateTime offAt = DateTime.MinValue;
        private const int CoolMs = 2000;

        // 独占全屏时 DWM 不参与合成,放大镜矩阵完全失效,只剩显卡的 gamma LUT 还能改画面。
        // LUT 只能做逐通道的亮度/对比度/色温,做不了跨通道饱和度,但至少不会"开了没反应"。
        private static readonly List<IntPtr> gammaDcs = new List<IntPtr>();
        private static readonly List<ushort[]> gammaSaved = new List<ushort[]>();
        private static readonly List<string> gammaDevices = new List<string>();
        private const string GammaMarkerName = "filter_gamma.dat";
        private static bool gammaDirty;
        // 最近一次写进 LUT 的矩阵:游戏切分辨率、驱动自我修复都会把 LUT 冲掉,
        // 需要定期按这份矩阵补写回去(见 RefreshGamma)。
        private static float[] gammaM;
        // LUT 单独承担全部调色时(放大镜不可用),额外用 S 曲线近似饱和度。
        // 0 = 不加,1 = 全量。独占全屏下放大镜多半也看不见,取半量兜底。
        private static double gammaApprox;
        // 每秒都会重试一次 gamma(见 MainForm.OnTick 的补装逻辑),
        // 驱动不支持时不能每次都写日志,否则日志会被刷爆。
        private static DateTime gammaLogAt = DateTime.MinValue;

        /// <summary>放大镜是否可用(远程桌面等场景不可用)。只探测,不长期占用。</summary>
        public static bool Supported
        {
            get
            {
                if (!probed)
                {
                    probed = true;
                    try { available = Native.MagInitialize(); }
                    catch (Exception ex) { available = false; Log.Write("magnification: " + ex.Message); }
                    // 探测完立刻还回去:常驻的放大镜会一直挂在 DWM 上,和截图工具抢同一帧。
                    if (available) { try { Native.MagUninitialize(); } catch { } }
                    if (!available) Log.Write("magnification unavailable");
                }
                return available;
            }
        }

        /// <summary>真正要上滤镜时才初始化。force 用于用户手动切换(不受冷却限制)。</summary>
        public static void Init(bool force)
        {
            if (active || !Supported) return;
            if (!force && (DateTime.UtcNow - offAt).TotalMilliseconds < CoolMs) return;
            try
            {
                if (Native.MagInitialize())
                {
                    active = true;
                    Log.Write("magnification init");
                }
            }
            catch (Exception ex) { Log.Write("magnification: " + ex.Message); available = false; }
        }

        /// <summary>
        /// 把放大镜整个卸下来。只推恒等矩阵不够:效果链还挂在 DWM 上,
        /// Win+Shift+S 的取色层一铺开整屏就会卡住,要等窗口重新上屏才恢复。
        /// </summary>
        public static void Suspend()
        {
            last = null;
            if (!active) return;
            active = false;
            offAt = DateTime.UtcNow;
            try { Native.MagUninitialize(); } catch { }
            Log.Write("magnification suspend");
        }

        public static void Shutdown()
        {
            Suspend();
            RestoreGamma();
            NvApi.Release();
            ReleaseGammaDcs();
            probed = false;
            available = false;
        }

        /// <summary>
        /// 按设置决定当前是否应该生效。
        /// 普通窗口/无边框全屏走放大镜(完整颜色矩阵);
        /// 独占全屏时 DWM 被绕过,放大镜看不见任何东西,改用显卡 gamma LUT,
        /// 保证"开了滤镜游戏里一定有变化",同时把放大镜卸掉避免退出全屏后效果叠加。
        /// </summary>
        public static void ApplySettings(Settings st, bool isGame, int gamePid)
        {
            ApplySettings(st, isGame, gamePid, false);
        }

        /// <summary>
        /// force 表示这是用户刚点的切换:冷却期内也要立刻装上,
        /// 否则会出现「界面已经切过去了,画面还是原样」。
        /// </summary>
        public static void ApplySettings(Settings st, bool isGame, int gamePid, bool force)
        {
            if (st == null) return;
            bool on = st.FilterMode == 2 || (st.FilterMode == 1 && isGame);
            // FF-038：HDR/ACM 打开时 DWM 走线性 scRGB，gamma LUT 与 SDR 颜色矩阵都会被绕过。
            bool hdr = Display.HdrOrAcmActive();
            if (hdr != lastHdr)
            {
                lastHdr = hdr;
                if (hdr) Log.Warn("filter: advanced color on (" + Display.AdvancedColorDetail + "), tone changes may be bypassed");
            }
            HdrActive = hdr;
            if (!on)
            {
                // 不需要滤镜时连放大镜一起卸掉,避免平时就占着系统的合成链。
                Suspend();
                RestoreGamma();
                NvApi.Restore();
                return;
            }

            float[] m = Current(st);

            bool exclusive = isGame && gamePid > 0 && Monitor.IsExclusiveFullscreen(gamePid);
            if (exclusive)
            {
                // 原来这里直接 Suspend(),理由是"独占全屏放大镜看不见"。
                // 但 QUNS 的判定并不等于 DWM 真的没合成(全屏优化、独立翻转误报都会踩中),
                // 一旦误报,饱和度就整块丢了,用户看到的就是"开了没用"。
                // 现在改成矩阵拆分:LUT 只负责每一行的行和(逐通道增益+平移),
                // 放大镜只负责行归一化之后的跨通道部分,两个都生效时正好还原整张矩阵,
                // 只有 LUT 生效时至少亮度/对比度/色温还在,也不会出现双重调色。
                // 另外,饱和度这种跨通道效果 gamma LUT 根本表达不了,
                // 所以只要 NVIDIA 驱动允许,就交给数字振动,独占全屏下也是真生效的。
                Init(force);
                double vib = VibranceFrac(m);
                bool nv = vib > 0.001 && NvApi.Boost(vib);
                if (nv)
                {
                    // 饱和度已由驱动负责,放大镜只推恒等矩阵,
                    // 避免 DWM 仍在合成时同一份饱和度被算两次。
                    if (active) Apply(Identity());
                    ApplyGamma(m, 0.0);
                }
                else
                {
                    if (active) Apply(ChromaOf(m));
                    ApplyGamma(m, active ? 0.5 : 1.0);
                }
                return;
            }

            // 回到普通窗口:先撤销 gamma 和驱动数字振动,否则放大镜和它们会叠在一起,画面被调两次。
            RestoreGamma();
            NvApi.Restore();
            Init(force);
            if (!active)
            {
                // 放大镜被系统关掉或被别的放大类工具占着时,颜色矩阵根本推不出去,
                // 表现就是「界面上已经开了,画面一点没变」。退回显卡 LUT,
                // 至少亮度/对比度/色温仍然起作用,饱和度用数字振动或 S 曲线补。
                double vib = VibranceFrac(m);
                bool nv = vib > 0.001 && NvApi.Boost(vib);
                ApplyGamma(m, nv ? 0.0 : 1.0);
                return;
            }
            // 放大镜能给出完整矩阵(含饱和度),驱动那边不能再叠一层。
            Apply(m);
        }

        /// <summary>滤镜当前是否真的在起作用(放大镜生效中或已改写 gamma LUT)。</summary>
        public static bool On
        {
            get { return active || gammaDirty || NvApi.Applied; }
        }

        /// <summary>FF-038：当前显示器是否处于 HDR/ACM 模式，界面据此提示。</summary>
        public static bool HdrActive { get; private set; }
        private static bool lastHdr;

        /// <summary>独占全屏下用显示器 gamma LUT 近似整套调色。</summary>
        private static void ApplyGamma(float[] m, double approximate)
        {
            EnsureGammaDcs();
            if (gammaDcs.Count == 0) return;

            gammaM = (float[])m.Clone();
            gammaApprox = approximate;
            if (!gammaDirty) SaveGammaMarker();
            ushort[] ramp = BuildRamp(m, approximate);
            bool ok = false;
            for (int i = 0; i < gammaDcs.Count; i++)
            {
                try { if (Native.SetDeviceGammaRamp(gammaDcs[i], ramp)) ok = true; }
                catch { }
            }
            if (ok)
            {
                // 只在状态从"没写"变"写了"时记一笔,定时补写不该刷日志。
                if (!gammaDirty) Log.Write("filter gamma lut" + (approximate > 0.01 ? " approx" : ""));
                gammaDirty = true;
            }
            else
            {
                if (!gammaDirty) DeleteGammaMarker();
                if ((DateTime.UtcNow - gammaLogAt).TotalSeconds > 30)
                {
                    gammaLogAt = DateTime.UtcNow;
                    Log.Write("SetDeviceGammaRamp failed");
                }
            }
        }

        /// <summary>
        /// 按上次的矩阵把 LUT 重新写一遍。游戏切分辨率、切换独占全屏、驱动重置
        /// 都会把 LUT 顶回默认值,只写一次的话用户会看到滤镜"过一会儿自己没了"。
        /// </summary>
        public static void RefreshGamma()
        {
            // NVIDIA App / 驱动会在游戏启动、Alt+Tab 时按配置文件重放显示设置,
            // 数字振动也要跟着补写,否则用户会看到饱和度"过一会儿自己没了"。
            NvApi.Reapply();
            if (!gammaDirty || gammaM == null) return;
            if (gammaDcs.Count == 0) return;
            ushort[] ramp = BuildRamp(gammaM, gammaApprox);
            for (int i = 0; i < gammaDcs.Count; i++)
            {
                try { Native.SetDeviceGammaRamp(gammaDcs[i], ramp); }
                catch { }
            }
        }

        /// <summary>gamma LUT 是否已被改写(独占全屏或放大镜不可用时的兜底路径)。</summary>
        public static bool GammaActive { get { return gammaDirty; } }

        /// <summary>放大镜是否正在生效(窗口/无边框全屏的完整矩阵路径)。</summary>
        public static bool MagActive { get { return active; } }

        /// <summary>数字振动是否正被接管(独占全屏下的饱和度来源)。</summary>
        public static bool NvActive { get { return NvApi.Applied; } }

        /// <summary>
        /// 从矩阵对角线估算饱和度,换算成驱动数字振动需要抬升的量程比例(0-1)。
        /// 饱和矩阵行和为 1、对角线之和为 1+2×sat,所以 sat=(和-1)/2。
        /// 返回 0 表示不需要驱动介入(没有增饱和,或者矩阵形状不是饱和度矩阵)。
        /// 不同驱动的 DVC 量程不一样(常见 0-100,也有 0-63),所以只给比例不给绝对值。
        /// </summary>
        private static double VibranceFrac(float[] m)
        {
            if (m == null || m.Length < 25) return 0;
            double sum = 0;
            for (int c = 0; c < 3; c++)
            {
                int row = c * 5;
                double gain = m[row] + m[row + 1] + m[row + 2];
                if (gain < 0.05) return 0;
                sum += m[row + c] / gain;
            }
            double sat = (sum - 1.0) / 2.0;
            if (sat <= 1.01) return 0;
            if (sat > 2.0) sat = 2.0;
            // 软压缩:预留顶部 25% 驱动余量,避免叠加数字振动后肤色炸裂。
            double v = (sat - 1.0) * 1.35;
            if (v > 0.75) v = 0.75;
            return v;
        }

        /// <summary>从矩阵对角线估算等效饱和度,SatCurve 与总饱和度钳位共用。</summary>
        private static double EstimateSaturation(float[] m)
        {
            if (m == null || m.Length < 25) return 1.0;
            double sum = 0;
            for (int c = 0; c < 3; c++)
            {
                int row = c * 5;
                double gain = m[row] + m[row + 1] + m[row + 2];
                if (gain < 0.05) return 1.0;
                sum += m[row + c] / gain;
            }
            double sat = (sum - 1.0) / 2.0;
            if (sat < 0) sat = 0;
            if (sat > 2.5) sat = 2.5;
            return sat;
        }

        /// <summary>
        /// 把矩阵拆出一半给 LUT:每一行除以行和,只留下跨通道的混合关系(饱和度/分割色调)。
        /// 行和交给 LUT 后,两者叠加得到的乘积正好是原矩阵;
        /// 独占全屏下放大镜失效时,LUT 那一半仍然独立生效。
        /// </summary>
        private static float[] ChromaOf(float[] m)
        {
            float[] r = Identity();
            if (m == null || m.Length < 25) return r;
            for (int c = 0; c < 3; c++)
            {
                int row = c * 5;
                double gain = m[row] + m[row + 1] + m[row + 2];
                if (gain < 0.02) gain = 1;
                for (int j = 0; j < 3; j++) r[row + j] = (float)(m[row + j] / gain);
                r[row + 4] = 0;
            }
            return r;
        }

        /// <summary>
        /// 从颜色矩阵对角线估算饱和度,换算成 S 曲线的强度。
        /// 饱和矩阵的行和为 1、对角线之和为 1+2×sat,所以 sat=(和-1)/2。
        /// 只有 sat&gt;1 才会加,S 曲线本身不会真的分离色彩通道,
        /// 但能让画面看起来更"透",比完全没变化的强。
        /// </summary>
        private static double SatCurve(float[] m)
        {
            double sat = EstimateSaturation(m);
            if (sat <= 1.0) return 0;
            // 上限 0.18:中心斜率约 1.54,不会再出现暗亮两端断层色带。
            return Math.Min(0.18, (sat - 1.0) * 0.55);
        }

        /// <summary>把显示器 LUT 还原成启动时保存的原样。</summary>
        private static void RestoreGamma()
        {
            if (!gammaDirty) return;
            gammaDirty = false;
            gammaM = null;
            gammaApprox = 0;
            for (int i = 0; i < gammaDcs.Count && i < gammaSaved.Count; i++)
            {
                try { if (gammaSaved[i] != null) Native.SetDeviceGammaRamp(gammaDcs[i], gammaSaved[i]); }
                catch { }
            }
            DeleteGammaMarker();
        }

        /// <summary>
        /// 按颜色矩阵生成逐通道 LUT:输入灰度 v 同时送进 R/G/B,
        /// 输出就是矩阵第 c 行的「前三个系数之和 × v + 平移量」。
        /// 饱和度的行和恒为 1(灰度不变),所以 LUT 下它不起作用,
        /// 真正生效的是亮度、对比度、色温和暗部提升。
        /// </summary>
        private static ushort[] BuildRamp(float[] m, double approximate)
        {
            var ramp = new ushort[3 * 256];
            if (m == null || m.Length < 25) m = Identity();
            // LUT 是逐通道的,饱和度这种跨通道混合只能靠 S 曲线近似:
            // 一个轻微的两端外扩、中间收拢,画面会更"透",至少不是完全没反应。
            double k = approximate > 0.01 ? SatCurve(m) * approximate : 0;
            for (int c = 0; c < 3; c++)
            {
                int row = c * 5;
                double gain = m[row] + m[row + 1] + m[row + 2];
                double offset = m[row + 4];
                for (int i = 0; i < 256; i++)
                {
                    double v = Clamp01(gain * (i / 255.0) + offset);
                    if (k > 0.001)
                    {
                        double t = v - 0.5;
                        v = Clamp01(v + k * t * (1.0 - Math.Abs(t) * 2.0) * 3.0);
                    }
                    ramp[c * 256 + i] = (ushort)Math.Round(v * 65535.0);
                }
            }
            return ramp;
        }

        private static void EnsureGammaDcs()
        {
            if (gammaDcs.Count > 0) return;
            Screen[] screens;
            try { screens = Screen.AllScreens; }
            catch { return; }

            for (int i = 0; i < screens.Length; i++)
            {
                IntPtr dc = IntPtr.Zero;
                try { dc = Native.CreateDC("DISPLAY", screens[i].DeviceName, IntPtr.Zero, IntPtr.Zero); }
                catch { }
                if (dc == IntPtr.Zero) continue;
                var saved = new ushort[3 * 256];
                bool got = false;
                try { got = Native.GetDeviceGammaRamp(dc, saved); }
                catch { }
                if (!got)
                {
                    try { Native.DeleteDC(dc); } catch { }
                    continue;
                }
                gammaDcs.Add(dc);
                gammaSaved.Add(saved);
                gammaDevices.Add(screens[i].DeviceName ?? "");
            }
            if (gammaDcs.Count > 0) Log.Write("filter gamma dc=" + gammaDcs.Count);
        }

        private static void ReleaseGammaDcs()
        {
            for (int i = 0; i < gammaDcs.Count; i++)
            {
                try { Native.DeleteDC(gammaDcs[i]); } catch { }
            }
            gammaDcs.Clear();
            gammaSaved.Clear();
            gammaDevices.Clear();
        }

        /// <summary>响应 WM_DISPLAYCHANGE:安全释放旧的 gamma HDC 并重新获取当前显示器句柄。</summary>
        public static void ReacquireGammaDcs()
        {
            bool wasDirty = gammaDirty;
            float[] m = gammaM;
            double approx = gammaApprox;
            ReleaseGammaDcs();
            EnsureGammaDcs();
            if (!wasDirty || m == null) return;
            if (gammaDcs.Count == 0)
            {
                gammaDirty = false;
                return;
            }
            ushort[] ramp = BuildRamp(m, approx);
            for (int i = 0; i < gammaDcs.Count; i++)
            {
                try { Native.SetDeviceGammaRamp(gammaDcs[i], ramp); }
                catch { }
            }
        }

        /// <summary>启动时检查崩溃遗留的 gamma LUT 备份标记,若存在则还原原值并删除标记。</summary>
        public static void RecoverStale()
        {
            string[] paths = GammaMarkerPaths();
            for (int p = 0; p < paths.Length; p++)
            {
                string path = paths[p];
                if (!File.Exists(path)) continue;
                int restored = 0;
                try
                {
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string dev = line.Substring(0, eq);
                        string b64 = line.Substring(eq + 1).Trim();
                        byte[] raw;
                        try { raw = Convert.FromBase64String(b64); }
                        catch { continue; }
                        if (raw.Length != 3 * 256 * 2) continue;
                        var ramp = new ushort[3 * 256];
                        Buffer.BlockCopy(raw, 0, ramp, 0, raw.Length);
                        IntPtr dc = IntPtr.Zero;
                        try
                        {
                            dc = Native.CreateDC("DISPLAY", dev, IntPtr.Zero, IntPtr.Zero);
                            if (dc != IntPtr.Zero && Native.SetDeviceGammaRamp(dc, ramp)) restored++;
                        }
                        catch { }
                        finally
                        {
                            if (dc != IntPtr.Zero) { try { Native.DeleteDC(dc); } catch { } }
                        }
                    }
                    if (restored > 0) Log.Write("filter gamma stale recovered=" + restored);
                }
                catch (Exception ex) { Log.Write("filter gamma recover: " + ex.Message); }
                try { File.Delete(path); } catch { }
            }
        }

        private static string[] GammaMarkerPaths()
        {
            string progDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FrameForge");
            return new string[]
            {
                Path.Combine(progDir, GammaMarkerName),
                Path.Combine(Settings.Dir, GammaMarkerName)
            };
        }

        private static void SaveGammaMarker()
        {
            if (gammaDevices.Count == 0 || gammaSaved.Count == 0) return;
            var sb = new StringBuilder();
            byte[] raw = new byte[3 * 256 * 2];
            for (int i = 0; i < gammaDevices.Count && i < gammaSaved.Count; i++)
            {
                if (string.IsNullOrEmpty(gammaDevices[i]) || gammaSaved[i] == null) continue;
                Buffer.BlockCopy(gammaSaved[i], 0, raw, 0, raw.Length);
                sb.Append(gammaDevices[i]).Append('=').Append(Convert.ToBase64String(raw)).Append('\n');
            }
            if (sb.Length == 0) return;
            string content = sb.ToString();
            string[] paths = GammaMarkerPaths();
            for (int i = 0; i < paths.Length; i++)
            {
                try
                {
                    string dir = Path.GetDirectoryName(paths[i]);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    SafeFile.WriteAllText(paths[i], content, new UTF8Encoding(false));
                    return;
                }
                catch { }
            }
        }

        private static void DeleteGammaMarker()
        {
            string[] paths = GammaMarkerPaths();
            for (int i = 0; i < paths.Length; i++)
            {
                try { if (File.Exists(paths[i])) File.Delete(paths[i]); }
                catch { }
            }
        }

        /// <summary>按设置算出的当前矩阵,供界面预览使用。</summary>
        public static float[] Current(Settings st)
        {
            if (st == null) return Identity();
            // ICC 与预设滤镜互斥:选了 ICC 就不再叠加预设调色。
            // 两者叠在一起时,ICC 是「把 sRGB 映射到这块屏」的校正,
            // 前面的调色会把画面推到 sRGB 之外,再走 ICC 就会二次偏色,
            // 而且用户要的是「一个滤镜」,不是两层调色。
            float[] baseM = null;
            if (!string.IsNullOrEmpty(st.FilterIcc)) baseM = Icc.For(st.FilterIcc);
            if (baseM == null)
                baseM = BuildCore(st.FilterPreset,
                    st.FilterContrast, st.FilterSaturation, st.FilterWarmth,
                    st.FilterTint);

            // 暗部场景平衡 / 数字振动是独立增强层,叠在预设或 ICC 之上。
            // 以前这两项混在 Build 里,选了 ICC 就完全不生效;做成独立开关后
            // 用户可以只用 ICC 校正 + 轻微抬暗部,两者不再互斥。
            int dark = st.FilterDarkOn ? st.FilterDark : 0;
            int vibrance = st.FilterVibranceOn ? st.FilterVibrance : 0;
            float[] m = EnhanceCore(baseM, dark, vibrance);
            // 强度滑杆只在最终合成矩阵后插值一次,避免 Build 与 Enhance 各缩一次。
            return WithIntensity(m, st.FilterIntensity);
        }

        /// <summary>把总强度插值(0..100)向恒等矩阵收敛。</summary>
        public static float[] WithIntensity(float[] m, int intensity)
        {
            if (m == null) m = Identity();
            double t = Clamp01(intensity / 100.0);
            if (t >= 0.9999) return m;
            float[] id = Identity();
            var final = new float[25];
            for (int i = 0; i < 25; i++)
                final[i] = (float)(id[i] + (m[i] - id[i]) * t);
            return final;
        }

        /// <summary>
        /// 内部矩阵按列向量构造(平移在 m[4,9,14]);Win32 MAGCOLOREFFECT 与 GDI+
        /// ColorMatrix 都要求行向量右乘(平移在 m[20..22]),推送前必须转置。
        /// 不转置时灰度会把纯白算成偏绿、暗部平衡方向也会反。
        /// </summary>
        public static float[] Transpose(float[] m)
        {
            if (m == null || m.Length < 25) return Identity();
            var r = new float[25];
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 5; j++) r[i * 5 + j] = m[j * 5 + i];
            return r;
        }

        /// <summary>
        /// 5×5 颜色矩阵相乘:返回 a×b,即先把 b 作用到颜色上再作用 a。
        /// </summary>
        public static float[] Mul(float[] a, float[] b)
        {
            var r = new float[25];
            for (int i = 0; i < 5; i++)
            {
                for (int j = 0; j < 5; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < 5; k++) sum += (double)a[i * 5 + k] * b[k * 5 + j];
                    r[i * 5 + j] = (float)sum;
                }
            }
            return r;
        }

        /// <summary>
        /// 暗部场景平衡 + 数字振动。dark/vibrance 均为 0..100,50 为中性偏轻。
        /// 强度不在这里缩放:调用方在最终合成后统一插值一次。
        /// </summary>
        public static float[] Enhance(float[] baseM, int dark, int vibrance, int intensity)
        {
            return WithIntensity(EnhanceCore(baseM, dark, vibrance), intensity);
        }

        /// <summary>
        /// 增强层核心:只负责画面提升,强度插值交给最外层。
        /// 暗部抬升后的净黑位与总饱和度都在这里收口。
        /// </summary>
        public static float[] EnhanceCore(float[] baseM, int dark, int vibrance)
        {
            if (baseM == null) baseM = Identity();
            if (dark <= 0 && vibrance <= 0) return baseM;

            double shadow = Clamp01(dark / 100.0);
            double vib = Clamp01(vibrance / 100.0);

            // 数字振动单层最大 +50%,并按底层已有饱和度做联合钳位(总上限 1.65)。
            double sat = 1.0 + vib * 0.50;
            double baseSat = EstimateSaturation(baseM);
            if (baseSat > 0.01 && baseSat * sat > 1.65) sat = 1.65 / baseSat;
            if (sat < 1.0) sat = 1.0;

            double gain = 1.0 - 0.15 * shadow;
            // 线性路径净黑位不超过 +0.055,防止整屏被洗成灰膜。
            double lift = Math.Min(0.055, 0.06 * shadow);

            double lr = 0.2126, lg = 0.7152, lb = 0.0722;
            float[] e =
            {
                (float)((lr + (1 - lr) * sat) * gain), (float)((lg - lg * sat) * gain), (float)((lb - lb * sat) * gain), 0, (float)lift,
                (float)((lr - lr * sat) * gain), (float)((lg + (1 - lg) * sat) * gain), (float)((lb - lb * sat) * gain), 0, (float)lift,
                (float)((lr - lr * sat) * gain), (float)((lg - lg * sat) * gain), (float)((lb + (1 - lb) * sat) * gain), 0, (float)lift,
                0, 0, 0, 1, 0,
                0, 0, 0, 0, 1
            };
            float[] m = Mul(e, baseM);
            CapBlack(m);
            return m;
        }

        /// <summary>净黑位上限 +0.055:只压平移列,不动彩度与对比度。</summary>
        private static void CapBlack(float[] m)
        {
            if (m == null || m.Length < 25) return;
            const double MaxBlack = 0.055;
            if (m[4] > MaxBlack) m[4] = (float)MaxBlack;
            if (m[9] > MaxBlack) m[9] = (float)MaxBlack;
            if (m[14] > MaxBlack) m[14] = (float)MaxBlack;
        }

        /// <summary>把 5×5 矩阵推给系统;已是最新则什么都不做。</summary>
        public static void Apply(float[] m)
        {
            if (!active) return;
            if (Same(last, m)) return;
            try
            {
                var effect = new Native.MAGCOLOREFFECT();
                // 出口统一转置成行向量右乘布局,平移落到 m[20..22]。
                effect.transform = Transpose(m);
                if (Native.MagSetFullscreenColorEffect(ref effect)) last = (float[])m.Clone();
                else Log.Write("MagSetFullscreenColorEffect failed " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { Log.Write("filter apply: " + ex.Message); }
        }

        public static float[] Identity()
        {
            return new float[]
            {
                1,0,0,0,0,
                0,1,0,0,0,
                0,0,1,0,0,
                0,0,0,1,0,
                0,0,0,0,1
            };
        }

        /// <summary>
        /// 组合预设与自定义参数。intensity 0..100 把结果向恒等矩阵插值,
        /// contrast/saturation/warmth/tint 均为 -100..100;亮度改由显示器硬件背光负责。
        /// </summary>
        public static float[] Build(int preset, int intensity, int contrast, int saturation, int warmth,
            int tint = 0)
        {
            return WithIntensity(BuildCore(preset, contrast, saturation, warmth, tint), intensity);
        }

        /// <summary>
        /// 基层矩阵(不含强度插值)。参数按 v1.5.1 规格表重标定:
        /// 所有预设净黑位 y(0) ≥ 0、高光裁切 &lt; 1.9%,护眼类锁 y(1) ≤ 0.965。
        /// </summary>
        public static float[] BuildCore(int preset, int contrast, int saturation, int warmth, int tint)
        {
            if (preset < 0 || preset >= PresetKeys.Length) preset = 0;
            double sat = 1.0, con = 1.0, bri = 0.0, warm = 0.0; double shadow = 0.0;
            // 分割色调:正值阴影偏青、高光偏暖,负值反过来。对应 ReShade 的 LiftGammaGain。
            double split = 0.0;
            switch (preset)
            {
                case 1: sat = 1.16; con = 1.04; bri = 0.012; warm = 0.02; shadow = 0.14; break;  // 鲜艳
                case 2: sat = 0.95; con = 0.96; bri = -0.015; warm = 0.06; break;                 // 柔和
                case 3: sat = 1.03; con = 1.02; bri = 0.025; shadow = 0.22; break;                // 高亮
                case 4: sat = 1.06; con = 1.03; bri = 0.006; warm = 0.10; shadow = 0.16; split = 0.55; break; // 电影
                case 5: sat = 0.96; con = 0.93; bri = -0.030; warm = 0.16; shadow = 0.05; break; // 防雪盲
                case 6: sat = 1.05; con = 1.03; bri = 0.015; warm = -0.04; shadow = 0.14; break; // 白天
                case 7: sat = 0.92; con = 0.95; bri = -0.025; warm = 0.53; break;                 // 夜间护眼
                case 8: sat = 0.0; break;                                                         // 灰度
                case 9: sat = 1.22; con = 1.06; bri = 0.015; warm = -0.04; shadow = 0.35; break;  // 明基亮丽
                case 10: sat = 1.10; con = 1.06; warm = -0.07; break;                             // 雾天增强(去雾方向)
                case 11: sat = 1.14; con = 1.06; bri = 0.015; warm = -0.05; shadow = 0.30; break; // 竞技
                case 12: sat = 1.15; con = 1.03; bri = 0.008; warm = -0.02; shadow = 0.14; break; // 二次元

                // ---- 热门游戏 ----
                // 战场类都靠"精准抬暗部 + 一点点冷调"把人和环境拉开距离;
                // shadow/con 联合标定保证净黑位 > 0,不再把暗道压成死黑。
                case 13: sat = 1.12; con = 1.06; bri = 0.015; warm = -0.06; shadow = 0.34; break; // 三角洲行动
                case 14: sat = 1.20; con = 1.06; bri = 0.014; warm = -0.08; shadow = 0.28; break; // 无畏契约
                case 15: sat = 1.14; con = 1.03; bri = 0.008; warm = 0.02; shadow = 0.14; break;  // 原神
                case 16: sat = 1.14; con = 1.06; bri = 0.015; warm = -0.06; shadow = 0.36; break; // Apex 英雄
                case 17: sat = 1.22; con = 1.06; bri = 0.014; warm = -0.04; shadow = 0.32; break; // CS2
                case 18: sat = 1.12; con = 1.07; bri = 0.016; warm = -0.04; shadow = 0.38; break; // PUBG
                case 19: sat = 1.14; con = 1.05; bri = 0.012; warm = 0.04; shadow = 0.24; break;  // 永劫无间
                case 20: sat = 1.10; con = 1.00; warm = 0.06; shadow = 0.08; break;               // 英雄联盟
                case 21: sat = 1.04; con = 1.03; bri = 0.008; warm = -0.24; shadow = 0.14; break; // 冷色
                case 22: sat = 1.04; con = 1.02; bri = 0.004; warm = 0.22; shadow = 0.12; break;  // 暖色
            }

            // 自定义滑杆叠加在预设之上。
            // 饱和度改分段映射:负半轴可一路去色到黑白,正半轴最多 +45%。
            double satMul;
            if (saturation >= 0) satMul = 1.0 + saturation / 100.0 * 0.45;
            else satMul = 1.0 + saturation / 100.0;
            sat *= satMul;
            con *= 1.0 + contrast / 100.0 * 0.35;

            if (sat < 0) sat = 0;
            if (sat > 1.45) sat = 1.45;
            if (con < 0.50) con = 0.50;
            if (con > 1.45) con = 1.45;
            if (shadow > 1) shadow = 1;
            if (shadow < 0) shadow = 0;
            if (warm > 1) warm = 1;
            if (warm < -1) warm = -1;

            // 饱和度矩阵(内部列向量布局:每一行对应一个输出通道)。
            double lr = 0.2126, lg = 0.7152, lb = 0.0722;
            double[] m =
            {
                lr + (1 - lr) * sat, lg - lg * sat, lb - lb * sat, 0, 0,
                lr - lr * sat, lg + (1 - lg) * sat, lb - lb * sat, 0, 0,
                lr - lr * sat, lg - lg * sat, lb + (1 - lb) * sat, 0, 0,
                0, 0, 0, 1, 0,
                0, 0, 0, 0, 1
            };

            // 对比度:围绕 0.5 缩放,平移一起乘(与 2.2 节传输函数一致)。
            double offset = 0.5 * (1 - con) + bri;
            for (int i = 0; i < 5; i++) for (int j = 0; j < 3; j++) m[i * 5 + j] *= con;
            m[4] += offset; m[9] += offset; m[14] += offset;

            // 暗部场景平衡:压一点高光、抬黑位,暗处细节更清楚(线性矩阵的近似做法)。
            if (shadow > 0)
            {
                double gain = 1.0 - 0.15 * shadow;
                double lift = 0.06 * shadow;
                for (int i = 0; i < 3; i++) for (int j = 0; j < 5; j++) m[i * 5 + j] *= gain;
                m[4] += lift; m[9] += lift; m[14] += lift;
            }

            // 分割色调:阴影加一层薄色(青),高光加一层补色(暖)。
            if (split != 0.0)
            {
                m[4] += -0.020 * split;
                m[9] += 0.004 * split;
                m[14] += 0.032 * split;
                double hot = 1.0 + 0.035 * split;
                double cold = 1.0 - 0.040 * split;
                for (int j = 0; j < 5; j++) { m[j] *= hot; m[10 + j] *= cold; }
            }

            // 白平衡:色温/色调换算成三通道增益,再归一化亮度,只改色相不改明暗。
            double tintVal = tint / 100.0;
            if (tintVal > 1) tintVal = 1;
            if (tintVal < -1) tintVal = -1;
            double[] gains = new double[3];
            WhiteBalance(warm, tintVal, warmth / 100.0, gains);
            for (int j = 0; j < 5; j++)
            {
                m[0 * 5 + j] *= gains[0];
                m[1 * 5 + j] *= gains[1];
                m[2 * 5 + j] *= gains[2];
            }

            var final = new float[25];
            for (int i = 0; i < 25; i++) final[i] = (float)m[i];
            CapBlack(final);
            return final;
        }

        /// <summary>
        /// 色温 -1..1 按 K = 6500 - 4500·warm 映射(兼容旧设置),色温滑杆再按
        /// Mired 对称步进(±100 ≈ ±75 MK^-1)叠加。曲线用连续 Planckian 拟合,
        /// 6500K 精确归一为 (1,1,1),全程无 6600K 品红跳变。输出为三通道增益。
        /// </summary>
        private static void WhiteBalance(double warm, double tint, double warmth, double[] gains)
        {
            double kelvin = 6500.0 - warm * 4500.0;
            if (warmth != 0)
            {
                double mired = 1e6 / kelvin + warmth * 75.0;
                if (mired < 100.0) mired = 100.0;      // 硬上限 10000K
                if (mired > 370.4) mired = 370.4;      // 下限 2700K
                kelvin = 1e6 / mired;
            }
            if (kelvin < 2700.0) kelvin = 2700.0;
            if (kelvin > 10000.0) kelvin = 10000.0;

            double[] cur = new double[3], basis = new double[3];
            Planckian(kelvin, cur);
            Planckian(6500.0, basis);
            double rr = cur[0] / basis[0];
            double gg = cur[1] / basis[1];
            double bb = cur[2] / basis[2];

            // 色调:正值偏洋红(压绿),负值偏绿;灵敏度收窄到 ±12%。
            gg *= 1.0 - tint * 0.12;

            // 线性光下归一化亮度,只改色相,不改整体明暗。
            double luma = 0.2126 * rr + 0.7152 * gg + 0.0722 * bb;
            if (luma < 0.0001) luma = 0.0001;
            gains[0] = rr / luma;
            gains[1] = gg / luma;
            gains[2] = bb / luma;
        }

        /// <summary>
        /// 连续 Planckian 轨迹 → 线性 sRGB 相对亮度(Kim 等 1960 UCS 三次拟合)。
        /// 输出未归一化的线性 RGB,供 WhiteBalance 做 6500K 相对增益。
        /// </summary>
        private static void Planckian(double kelvin, double[] rgb)
        {
            double t = kelvin;
            double x;
            if (t <= 4000.0)
                x = -0.2661239e9 / (t * t * t) - 0.2343589e6 / (t * t) + 0.8776956e3 / t + 0.179910;
            else
                x = -3.0258469e9 / (t * t * t) + 2.1070379e6 / (t * t) + 0.2226347e3 / t + 0.240390;

            double x2 = x * x, x3 = x2 * x;
            double y;
            if (t <= 2222.0) y = -1.1063814 * x3 - 1.34811020 * x2 + 2.18555832 * x - 0.20219683;
            else if (t <= 4000.0) y = -0.9549476 * x3 - 1.37418593 * x2 + 2.09137015 * x - 0.16748867;
            else y = 3.0817580 * x3 - 5.87338670 * x2 + 3.75112997 * x - 0.37001483;

            double X = x / y, Y = 1.0, Z = (1.0 - x - y) / y;
            double r = 3.2404542 * X - 1.5371385 * Y - 0.4985314 * Z;
            double g = -0.9692660 * X + 1.8760108 * Y + 0.0415560 * Z;
            double b = 0.0556434 * X - 0.2040259 * Y + 1.0572252 * Z;
            rgb[0] = r > 1e-6 ? r : 1e-6;
            rgb[1] = g > 1e-6 ? g : 1e-6;
            rgb[2] = b > 1e-6 ? b : 1e-6;
        }

        /// <summary>用矩阵变换单个颜色,给界面上的预览条使用(入参为内部列向量矩阵)。</summary>
        public static Color Transform(Color c, float[] m)
        {
            if (m == null || m.Length < 25) return c;
            // 内部矩阵每一行对应一个输出通道,直接按行加权即可。
            double r = c.R, g = c.G, b = c.B, a = c.A;
            double nr = r * m[0] + g * m[1] + b * m[2] + a * m[3] + 255.0 * m[4];
            double ng = r * m[5] + g * m[6] + b * m[7] + a * m[8] + 255.0 * m[9];
            double nb = r * m[10] + g * m[11] + b * m[12] + a * m[13] + 255.0 * m[14];
            double na = r * m[15] + g * m[16] + b * m[17] + a * m[18] + 255.0 * m[19];
            return Color.FromArgb(Byte(na), Byte(nr), Byte(ng), Byte(nb));
        }

        private static int Byte(double v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (int)Math.Round(v);
        }

        private static bool Same(float[] a, float[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > 0.0005f) return false;
            return true;
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }
}