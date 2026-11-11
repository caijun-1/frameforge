using System;
using System.Collections.Generic;
using System.Drawing;
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
        private static bool gammaDirty;
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
            if (!on)
            {
                // 不需要滤镜时连放大镜一起卸掉,避免平时就占着系统的合成链。
                Suspend();
                RestoreGamma();
                return;
            }

            float[] m = Current(st);

            bool exclusive = isGame && gamePid > 0 && Monitor.IsExclusiveFullscreen(gamePid);
            if (exclusive)
            {
                Suspend();
                ApplyGamma(m);
                return;
            }

            // 回到普通窗口:先撤销 gamma,否则放大镜和 LUT 会叠在一起,画面被调两次。
            RestoreGamma();
            Init(force);
            if (!active)
            {
                // 放大镜被系统关掉或被别的放大类工具占着时,颜色矩阵根本推不出去,
                // 表现就是「界面上已经开了,画面一点没变」。退回显卡 LUT,
                // 至少亮度/对比度/色温仍然起作用(饱和度仍只能靠放大镜)。
                ApplyGamma(m);
                return;
            }
            Apply(m);
        }

        /// <summary>滤镜当前是否真的在起作用(放大镜生效中或已改写 gamma LUT)。</summary>
        public static bool On
        {
            get { return active || gammaDirty; }
        }

        /// <summary>独占全屏下用显示器 gamma LUT 近似整套调色。</summary>
        private static void ApplyGamma(float[] m)
        {
            EnsureGammaDcs();
            if (gammaDcs.Count == 0) return;

            ushort[] ramp = BuildRamp(m);
            bool ok = false;
            for (int i = 0; i < gammaDcs.Count; i++)
            {
                try { if (Native.SetDeviceGammaRamp(gammaDcs[i], ramp)) ok = true; }
                catch { }
            }
            if (ok)
            {
                gammaDirty = true;
                Log.Write("filter gamma lut");
            }
            else if ((DateTime.UtcNow - gammaLogAt).TotalSeconds > 30)
            {
                gammaLogAt = DateTime.UtcNow;
                Log.Write("SetDeviceGammaRamp failed");
            }
        }

        /// <summary>把显示器 LUT 还原成启动时保存的原样。</summary>
        private static void RestoreGamma()
        {
            if (!gammaDirty) return;
            gammaDirty = false;
            for (int i = 0; i < gammaDcs.Count && i < gammaSaved.Count; i++)
            {
                try { if (gammaSaved[i] != null) Native.SetDeviceGammaRamp(gammaDcs[i], gammaSaved[i]); }
                catch { }
            }
        }

        /// <summary>
        /// 按颜色矩阵生成逐通道 LUT:输入灰度 v 同时送进 R/G/B,
        /// 输出就是矩阵第 c 行的「前三个系数之和 × v + 平移量」。
        /// 饱和度的行和恒为 1(灰度不变),所以 LUT 下它不起作用,
        /// 真正生效的是亮度、对比度、色温和暗部提升。
        /// </summary>
        private static ushort[] BuildRamp(float[] m)
        {
            var ramp = new ushort[3 * 256];
            if (m == null || m.Length < 25) m = Identity();
            for (int c = 0; c < 3; c++)
            {
                int row = c * 5;
                double gain = m[row] + m[row + 1] + m[row + 2];
                double offset = m[row + 4];
                for (int i = 0; i < 256; i++)
                {
                    double v = Clamp01(gain * (i / 255.0) + offset);
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
                baseM = Build(st.FilterPreset, st.FilterIntensity,
                    st.FilterContrast, st.FilterSaturation, st.FilterWarmth,
                    st.FilterTint);

            // 暗部场景平衡 / 数字振动是独立增强层,叠在预设或 ICC 之上。
            // 以前这两项混在 Build 里,选了 ICC 就完全不生效;做成独立开关后
            // 用户可以只用 ICC 校正 + 轻微抬暗部,两者不再互斥。
            int dark = st.FilterDarkOn ? st.FilterDark : 0;
            int vibrance = st.FilterVibranceOn ? st.FilterVibrance : 0;
            return Enhance(baseM, dark, vibrance, st.FilterIntensity);
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
        /// 暗部场景平衡 + 数字振动。dark/vibrance 均为 0..100,50 为中性偏轻,
        /// intensity 决定增强层整体强度(和滑杆区一致,拉到 0 就等于不增强)。
        /// </summary>
        public static float[] Enhance(float[] baseM, int dark, int vibrance, int intensity)
        {
            if (baseM == null) baseM = Identity();
            if (dark <= 0 && vibrance <= 0) return baseM;

            double t = Clamp01(intensity / 100.0);
            if (t <= 0) return baseM;

            double sat = 1.0 + vibrance / 100.0 * 0.55 * t;    // 数字振动:只往上加饱和度
            double shadow = dark / 100.0 * 0.5 * t;            // 暗部场景平衡
            double gain = 1.0 - 0.15 * shadow;
            double lift = 0.06 * shadow;

            double lr = 0.2126, lg = 0.7152, lb = 0.0722;
            float[] e =
            {
                (float)((lr + (1 - lr) * sat) * gain), (float)((lg - lg * sat) * gain), (float)((lb - lb * sat) * gain), 0, (float)lift,
                (float)((lr - lr * sat) * gain), (float)((lg + (1 - lg) * sat) * gain), (float)((lb - lb * sat) * gain), 0, (float)lift,
                (float)((lr - lr * sat) * gain), (float)((lg - lg * sat) * gain), (float)((lb + (1 - lb) * sat) * gain), 0, (float)lift,
                0, 0, 0, 1, 0,
                0, 0, 0, 0, 1
            };
            return Mul(e, baseM);
        }

        /// <summary>把 5×5 矩阵推给系统;已是最新则什么都不做。</summary>
        public static void Apply(float[] m)
        {
            if (!active) return;
            if (Same(last, m)) return;
            try
            {
                var effect = new Native.MAGCOLOREFFECT();
                effect.transform = (float[])m.Clone();
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
            double sat = 1.0, con = 1.0, bri = 0.0, warm = 0.0; double shadow = 0.0;
            // 分割色调:正值阴影偏青、高光偏暖,负值反过来。对应 ReShade 的 LiftGammaGain。
            double split = 0.0;
            switch (preset)
            {
                case 1: sat = 1.12; con = 1.04; break;                    // 鲜艳
                case 2: sat = 0.98; con = 0.97; bri = 0.02; break;        // 柔和
                case 3: con = 1.02; bri = 0.05; break;                    // 高亮
                case 4: sat = 1.05; con = 1.03; warm = 0.10; shadow = 0.06; split = 0.70; break; // 电影
                case 5: con = 0.94; bri = -0.03; sat = 0.99; break;       // 防雪盲
                case 6: con = 1.0; bri = 0.04; sat = 1.02; break;         // 白天
                case 7: con = 0.96; bri = -0.02; warm = 0.45; break;      // 夜间护眼
                case 8: sat = 0.0; break;                                 // 灰度
                case 9: con = 1.04; bri = 0.03; sat = 1.06; shadow = 0.06; split = 0.25; break; // 明基亮丽
                case 10: con = 0.92; bri = 0.01; sat = 0.94; shadow = 0.16; break; // 雾天增强
                case 11: con = 1.06; sat = 1.04; shadow = 0.10; break;    // 竞技
                case 12: sat = 1.12; con = 1.02; bri = 0.02; shadow = 0.06; break; // 二次元

                // ---- 热门游戏 ----
                // 战场类都靠"轻抬暗部 + 一点点冷调"把人和环境拉开距离,
                // 数值刻意保守:全屏矩阵过强会让素材发灰、HUD 变色,
                // 玩久了还会有明显的眩晕感。
                case 13: sat = 1.04; con = 1.04; bri = 0.01; warm = -0.04; shadow = 0.10; break; // 三角洲行动
                case 14: sat = 1.06; con = 1.06; bri = 0.02; warm = -0.06; shadow = 0.08; break; // 无畏契约
                case 15: sat = 1.10; con = 1.02; bri = 0.03; warm = 0.06; shadow = 0.04; break;  // 原神
                case 16: sat = 1.05; con = 1.05; bri = 0.01; warm = -0.05; shadow = 0.12; break; // Apex 英雄
                case 17: sat = 1.04; con = 1.07; shadow = 0.08; break;                           // CS2
                case 18: sat = 1.03; con = 0.98; bri = 0.02; warm = -0.03; shadow = 0.18; break; // PUBG
                case 19: sat = 1.08; con = 1.03; bri = 0.01; warm = 0.08; shadow = 0.06; break;  // 永劫无间
                case 20: sat = 1.06; con = 1.0; bri = 0.02; warm = 0.03; shadow = 0.04; break;   // 英雄联盟
                case 21: sat = 1.03; con = 1.03; bri = 0.01; warm = -0.55; shadow = 0.08; break; // 冷色
                case 22: sat = 1.03; con = 1.02; warm = 0.55; shadow = 0.06; break;              // 暖色
            }

            // 自定义滑杆叠加在预设之上:倍率整体收窄,拉到顶也不会过曝或发灰。
            sat *= 1.0 + saturation / 100.0 * 0.4;
            con *= 1.0 + contrast / 100.0 * 0.35;
            warm += warmth / 100.0 * 0.4;

            if (sat < 0) sat = 0;
            if (con < 0.2) con = 0.2;
            if (shadow > 1) shadow = 1;
            if (shadow < 0) shadow = 0;
            if (warm > 1) warm = 1;
            if (warm < -1) warm = -1;

            // 饱和度矩阵
            double lr = 0.2126, lg = 0.7152, lb = 0.0722;
            double[] m =
            {
                lr + (1 - lr) * sat, lg - lg * sat, lb - lb * sat, 0, 0,
                lr - lr * sat, lg + (1 - lg) * sat, lb - lb * sat, 0, 0,
                lr - lr * sat, lg - lg * sat, lb + (1 - lb) * sat, 0, 0,
                0, 0, 0, 1, 0,
                0, 0, 0, 0, 1
            };

            // 对比度:围绕 0.5 缩放
            double offset = 0.5 * (1 - con) + bri;
            for (int i = 0; i < 5; i++) for (int j = 0; j < 3; j++) m[i * 5 + j] *= con;
            m[4] += offset; m[9] += offset; m[14] += offset;

            // 暗部场景平衡:压一点高光、抬黑位,暗处细节更清楚(线性矩阵的近似做法)。
            // gain/lift 都收得很轻,主要是不让暗部彻底死黑,同时避免整屏发灰。
            if (shadow > 0)
            {
                double gain = 1.0 - 0.15 * shadow;
                double lift = 0.06 * shadow;
                for (int i = 0; i < 3; i++) for (int j = 0; j < 5; j++) m[i * 5 + j] *= gain;
                m[4] += lift; m[9] += lift; m[14] += lift;
            }

            // 分割色调:阴影加一层薄色(青),高光加一层补色(暖)。
            // lift 是常量偏移、gain 是乘性缩放,两者叠加就能在矩阵里近似出电影调色。
            if (split != 0.0)
            {
                m[4] += -0.020 * split;
                m[9] += 0.004 * split;
                m[14] += 0.032 * split;
                double hot = 1.0 + 0.035 * split;
                double cold = 1.0 - 0.040 * split;
                for (int j = 0; j < 5; j++) { m[j] *= hot; m[10 + j] *= cold; }
            }

            // 白平衡(参考 ReShade 的 WhiteBalance):按黑体辐射曲线把色温和色调
            // 换算成三通道增益,再归一化亮度,调整时不会顺带把画面压暗或提亮。
            double tintVal = tint / 100.0;
            if (tintVal > 1) tintVal = 1;
            if (tintVal < -1) tintVal = -1;
            double[] gains = new double[3];
            WhiteBalance(warm, tintVal, gains);
            for (int j = 0; j < 5; j++)
            {
                m[0 * 5 + j] *= gains[0];
                m[1 * 5 + j] *= gains[1];
                m[2 * 5 + j] *= gains[2];
            }

            double t = Clamp01(intensity / 100.0);
            float[] identity = Identity();
            var final = new float[25];
            for (int i = 0; i < 25; i++)
                final[i] = (float)(identity[i] + (m[i] - identity[i]) * t);
            return final;
        }

        /// <summary>
        /// 色温 -1..1 映射到 2000K..11000K(0 为 6500K 中性白),
        /// 色调 -1..1 在绿与洋红之间偏移。输出为三通道增益。
        /// </summary>
        private static void WhiteBalance(double warm, double tint, double[] gains)
        {
            double kelvin = 6500.0 - warm * 4500.0;
            if (kelvin < 2000.0) kelvin = 2000.0;
            if (kelvin > 12000.0) kelvin = 12000.0;

            double rr = BlackBodyR(kelvin) / BlackBodyR(6500.0);
            double gg = BlackBodyG(kelvin) / BlackBodyG(6500.0);
            double bb = BlackBodyB(kelvin) / BlackBodyB(6500.0);

            // 色调:正值偏洋红(压绿),负值偏绿。
            gg *= 1.0 - tint * 0.18;

            // 归一化亮度:只改色相,不改整体明暗。
            // 显示器是 gamma 编码的,先按 2.2 还原成线性光再算权重,否则暖色会顺带压暗。
            double luma = 0.2126 * Math.Pow(rr, 2.2) + 0.7152 * Math.Pow(gg, 2.2) + 0.0722 * Math.Pow(bb, 2.2);
            if (luma < 0.0001) luma = 0.0001;
            luma = Math.Pow(luma, 1.0 / 2.2);
            gains[0] = rr / luma;
            gains[1] = gg / luma;
            gains[2] = bb / luma;
        }

        // 黑体辐射色温到 sRGB 的近似( Tanner Helland ),只用其相对比例。
        private static double BlackBodyR(double kelvin)
        {
            double t = kelvin / 100.0;
            double v = t <= 66.0 ? 255.0 : 329.698727446 * Math.Pow(t - 60.0, -0.1332047592);
            return Clamp01(v / 255.0);
        }

        private static double BlackBodyG(double kelvin)
        {
            double t = kelvin / 100.0;
            double v = t <= 66.0
                ? 99.4708025861 * Math.Log(t) - 161.1195681661
                : 288.1221695283 * Math.Pow(t - 60.0, -0.0755148492);
            return Clamp01(v / 255.0);
        }

        private static double BlackBodyB(double kelvin)
        {
            double t = kelvin / 100.0;
            double v;
            if (t >= 66.0) v = 255.0;
            else if (t <= 19.0) v = 0.0;
            else v = 138.5177312231 * Math.Log(t - 10.0) - 305.0447927307;
            return Clamp01(v / 255.0);
        }

        /// <summary>用矩阵变换单个颜色,给界面上的预览条使用。</summary>
        public static Color Transform(Color c, float[] m)
        {
            if (m == null || m.Length < 25) return c;
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