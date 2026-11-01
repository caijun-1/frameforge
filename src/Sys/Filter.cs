using System;
using System.Drawing;

namespace FrameForge.Sys
{
    /// <summary>
    /// 画面滤镜:通过 Windows 放大镜的全屏颜色矩阵实现,不需要注入、不需要驱动。
    /// 颜色矩阵作用于整个桌面,因此只在用户选择时启用,切出游戏会自动还原。
    /// </summary>
    public static class Filter
    {
        public static readonly string[] PresetKeys =
        {
            "flt.none", "flt.vivid", "flt.soft", "flt.bright", "flt.cinema",
            "flt.snow", "flt.day", "flt.night", "flt.gray"
        };

        private static bool inited;
        private static bool supported;
        private static float[] last = null;

        /// <summary>放大镜是否可用(远程桌面等场景不可用)。</summary>
        public static bool Supported
        {
            get
            {
                if (!inited) Init();
                return supported;
            }
        }

        public static void Init()
        {
            if (inited) return;
            inited = true;
            try { supported = Native.MagInitialize(); }
            catch (Exception ex) { supported = false; Log.Write("magnification: " + ex.Message); }
            if (!supported) Log.Write("magnification unavailable");
        }

        public static void Shutdown()
        {
            if (!inited) return;
            try { if (supported) Native.MagUninitialize(); }
            catch { }
            supported = false;
            inited = false;
            last = null;
        }

        /// <summary>按设置决定当前是否应该生效,并把矩阵推给系统(矩阵未变化时跳过)。</summary>
        public static void ApplySettings(Settings st, bool isGame)
        {
            if (st == null) return;
            bool on = st.FilterMode == 2 || (st.FilterMode == 1 && isGame);
            if (!on)
            {
                Clear();
                return;
            }
            Apply(Current(st));
        }

        /// <summary>按设置算出的当前矩阵,供界面预览使用。</summary>
        public static float[] Current(Settings st)
        {
            if (st == null) return Identity();
            return Build(st.FilterPreset, st.FilterIntensity,
                st.FilterBrightness, st.FilterContrast, st.FilterSaturation, st.FilterWarmth);
        }

        public static void Clear()
        {
            if (!supported) return;
            if (last == null) return;
            Apply(Identity());
        }

        /// <summary>把 5×5 矩阵推给系统;已是最新则什么都不做。</summary>
        public static void Apply(float[] m)
        {
            if (!Supported) return;
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
        /// brightness/contrast/saturation/warmth 均为 -100..100。
        /// </summary>
        public static float[] Build(int preset, int intensity, int brightness, int contrast, int saturation, int warmth)
        {
            double sat = 1.0, con = 1.0, bri = 0.0, warm = 0.0;
            switch (preset)
            {
                case 1: sat = 1.32; con = 1.06; break;                    // 鲜艳
                case 2: sat = 0.92; con = 0.94; bri = 0.02; break;        // 柔和
                case 3: con = 1.04; bri = 0.06; break;                    // 高亮
                case 4: sat = 1.12; con = 1.08; bri = -0.01; warm = 0.12; break; // 电影
                case 5: con = 0.90; bri = -0.05; sat = 0.96; break;       // 防雪盲
                case 6: con = 0.98; bri = 0.05; sat = 1.05; break;        // 白天
                case 7: con = 0.94; bri = -0.04; warm = 0.35; break;      // 夜间护眼
                case 8: sat = 0.0; break;                                 // 灰度
            }

            // 自定义滑杆叠加在预设之上
            sat *= 1.0 + saturation / 100.0 * 0.6;
            con *= 1.0 + contrast / 100.0 * 0.5;
            bri += brightness / 100.0 * 0.18;
            warm += warmth / 100.0 * 0.5;

            if (sat < 0) sat = 0;
            if (con < 0.2) con = 0.2;

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

            // 色温:暖色抬高红、压低蓝
            double rg = 1.0 + warm * 0.16;
            double bg = 1.0 - warm * 0.16;
            for (int j = 0; j < 5; j++) { m[0 * 5 + j] *= rg; m[2 * 5 + j] *= bg; }

            double t = Clamp01(intensity / 100.0);
            float[] identity = Identity();
            var final = new float[25];
            for (int i = 0; i < 25; i++)
                final[i] = (float)(identity[i] + (m[i] - identity[i]) * t);
            return final;
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