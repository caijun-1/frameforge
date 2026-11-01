using System;
using System.Collections.Generic;
using System.Drawing;

namespace FrameForge
{
    /// <summary>浅色 / 深色两套调色板 + 字体缓存。</summary>
    public static class Theme
    {
        public static bool Dark = false;

        public static Color Bg;
        public static Color Card;
        public static Color Text;
        public static Color Sub;
        public static Color Faint;
        public static Color Border;
        public static Color Track;
        public static Color Hover;
        public static Color Accent;
        public static Color AccentHover;
        public static Color Warn;
        public static Color Danger;

        private static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();

        static Theme() { Set(false); }

        public static void Set(bool dark)
        {
            Dark = dark;
            if (!dark)
            {
                Bg = Hex("#F4F6F9");
                Card = Hex("#FFFFFF");
                Text = Hex("#1A1D21");
                Sub = Hex("#6B7280");
                Faint = Hex("#9AA1AC");
                Border = Hex("#E6E9EF");
                Track = Hex("#EDF0F5");
                Hover = Hex("#F0F2F6");
            }
            else
            {
                // 夜间模式不要纯黑堆叠:底色偏深蓝黑,卡片逐层提亮,靠层差而不是描边区分层次。
                Bg = Hex("#0E1116");
                Card = Hex("#191E26");
                Text = Hex("#EAEEF5");
                Sub = Hex("#9BA6B4");
                Faint = Hex("#6F7A88");
                Border = Hex("#2A3140");
                Track = Hex("#2A313C");
                Hover = Hex("#222933");
            }
            Accent = dark ? Hex("#4C8DFF") : Hex("#2F7BF5");
            AccentHover = dark ? Hex("#3A7BF0") : Hex("#2565D9");
            Warn = Hex("#F0A020");
            Danger = Hex("#EF5350");
        }

        public static Color Hex(string s)
        {
            return ColorTranslator.FromHtml(s);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int alpha)
        {
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        public static string Family { get { return I18n.IsEn ? "Segoe UI" : "Microsoft YaHei UI"; } }

        public static void ResetFonts() { Fonts.Clear(); }

        /// <summary>字体按点数创建,由 GDI+ 依据 DPI 自动换算像素,天然适配缩放。</summary>
        public static Font F(float size, FontStyle style)
        {
            return F(Family, size, style);
        }

        /// <summary>数字大标题用 Segoe UI Semibold,中英文界面一致。</summary>
        public static Font Num(float size)
        {
            return F("Segoe UI Semibold", size, FontStyle.Regular);
        }

        private static Font F(string family, float size, FontStyle style)
        {
            string key = family + "|" + size.ToString("0.##") + "|" + (int)style;
            Font f;
            if (Fonts.TryGetValue(key, out f)) return f;
            try { f = new Font(family, size, style, GraphicsUnit.Point); }
            catch { f = new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point); }
            Fonts[key] = f;
            return f;
        }

        public static Font Bold(float size) { return F(size, FontStyle.Bold); }
        public static Font Regular(float size) { return F(size, FontStyle.Regular); }
    }
}
