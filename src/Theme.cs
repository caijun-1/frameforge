using System;
using System.Collections.Generic;
using System.Drawing;

namespace FrameForge
{
    /// <summary>浅色 / 深色两套调色板 + 字体缓存。</summary>
    public static class Theme
    {
        public static bool Dark = false;
        /// <summary>强调色档位:0 均衡(蓝) / 1 野兽(红) / 2 自定义(紫),切主题后仍然保持。</summary>
        public static int Mode = 0;

        public static Color Bg;
        public static Color Card;
        /// <summary>卡片渐变的下沿色:纯白卡片带一点冷灰,叠加上高光才有柔和的光泽感。</summary>
        public static Color CardAlt;
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

        /// <summary>页面背景纵向渐变的起止色:卡片圆角外侧要按同一条渐变铺底,否则会留下一块偏暗的直角。</summary>
        public static Color BgTop { get { return Bg; } }
        public static Color BgBottom
        {
            get { return Mix(Bg, Dark ? Color.Black : Color.White, Dark ? 0.22 : 0.42); }
        }

        private static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();

        static Theme() { Set(false); }

        public static void Set(bool dark)
        {
            Dark = dark;
            if (!dark)
            {
                // 浅色走"柔光白"路线:底偏冷灰、卡片近纯白,靠极淡的层差而不是重描边分层。
                Bg = Hex("#F2F4F7");
                Card = Hex("#FFFFFF");
                CardAlt = Hex("#FAFBFD");
                Text = Hex("#1B1E23");
                Sub = Hex("#6E7783");
                Faint = Hex("#A2AAB6");
                Border = Hex("#E8ECF2");
                Track = Hex("#EAEEF4");
                Hover = Hex("#F1F4F8");
            }
            else
            {
                // 夜间模式不要纯黑堆叠:底色偏深蓝黑,卡片逐层提亮,靠层差而不是描边区分层次。
                Bg = Hex("#0E1116");
                Card = Hex("#191E26");
                CardAlt = Hex("#1D232C");
                Text = Hex("#EAEEF5");
                Sub = Hex("#9BA6B4");
                Faint = Hex("#6F7A88");
                Border = Hex("#2A3140");
                Track = Hex("#2A313C");
                Hover = Hex("#222933");
            }
            ApplyAccent();
            Warn = Hex("#F0A020");
            Danger = Hex("#EF5350");
        }

        /// <summary>切换强调色档位:均衡蓝、野兽红、自定义紫。</summary>
        public static void SetMode(int mode)
        {
            if (mode < 0 || mode > 2) mode = 0;
            if (Mode == mode) return;
            Mode = mode;
            ApplyAccent();
        }

        private static void ApplyAccent()
        {
            if (Mode == 1)
            {
                // 夜间用更亮的红保证对比度,日间用更深的红避免刺眼。
                Accent = Dark ? Hex("#FF4D4F") : Hex("#D92D20");
                AccentHover = Dark ? Hex("#FF6B6D") : Hex("#B42318");
            }
            else if (Mode == 2)
            {
                // 自定义模式走紫:和野兽的红拉开区分,深浅两套都保证文字对比度。
                Accent = Dark ? Hex("#A78BFA") : Hex("#6D3BE4");
                AccentHover = Dark ? Hex("#B9A2FF") : Hex("#5A2BCB");
            }
            else
            {
                Accent = Dark ? Hex("#4C8DFF") : Hex("#2F7BF5");
                AccentHover = Dark ? Hex("#3A7BF0") : Hex("#2565D9");
            }
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
