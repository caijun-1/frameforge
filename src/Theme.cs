using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FrameForge
{
    /// <summary>白昼宣纸 / 影刃皮革两套调色板、程序化底纹与字体缓存。</summary>
    public static class Theme
    {
        public static bool Dark = false;
        /// <summary>强调色档位:0 均衡 / 1 野兽 / 2 自定义,都是金属红系,只有明度差。</summary>
        public static int Mode = 0;

        public static Color Bg;
        public static Color Card;
        /// <summary>卡片渐变的下沿色:上沿偏亮,往下一点点压暗,像压了一层纸。</summary>
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
        /// <summary>金属描边 / 主按钮外圈的金线。</summary>
        public static Color Gold;
        /// <summary>卡片内侧的缝线。</summary>
        public static Color Stitch;
        /// <summary>导航选中胶囊。</summary>
        public static Color NavPill;
        public static Color NavBorder;

        /// <summary>页面背景纵向渐变的起止色:卡片圆角外侧要按同一条渐变铺底,否则会留下一块偏暗的直角。</summary>
        public static Color BgTop { get { return Bg; } }
        public static Color BgBottom { get { return Bg; } }

        private static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();
        private static Bitmap tex;
        private static TextureBrush texBrush;
        // 水墨底图:打包成资源,按窗口尺寸缩放后缓存;另存一张模糊版给玻璃卡片用。
        private static Bitmap bgArt, bgArtDark;
        private static Bitmap bgCache, bgBlur;
        private static Size bgSize;
        private static bool bgDark;

        // 表盘上的小剑图标(去背景 PNG),按非透明区域裁切后绘制。
        public static Bitmap Sword;
        public static Rectangle SwordBounds;

        // 滤镜 / 叠加层预览里展示的游戏画面。
        public static Bitmap PlayArt;

        static Theme() { LoadArt(); Set(false); }

        private static void LoadArt()
        {
            try
            {
                using (System.IO.Stream s = typeof(Theme).Assembly.GetManifestResourceStream("bg_light.png"))
                {
                    if (s != null) bgArt = new Bitmap(s);
                }
                using (System.IO.Stream s = typeof(Theme).Assembly.GetManifestResourceStream("bg_dark.png"))
                {
                    if (s != null) bgArtDark = new Bitmap(s);
                }
                using (System.IO.Stream s = typeof(Theme).Assembly.GetManifestResourceStream("icon_sword.png"))
                {
                    if (s != null)
                    {
                        Sword = new Bitmap(s);
                        SwordBounds = AlphaBounds(Sword);
                    }
                }
                using (System.IO.Stream s = typeof(Theme).Assembly.GetManifestResourceStream("scene_play.png"))
                {
                    if (s != null) PlayArt = new Bitmap(s);
                }
            }
            catch { }
        }

        /// <summary>找出图片里真正的非透明范围,避免把 1536 画布的大片留白也算进尺寸。</summary>
        private static Rectangle AlphaBounds(Bitmap b)
        {
            try
            {
                Rectangle full = new Rectangle(0, 0, b.Width, b.Height);
                BitmapData d = b.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] px = new byte[d.Stride * b.Height];
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
                b.UnlockBits(d);

                int minX = b.Width, minY = b.Height, maxX = -1, maxY = -1;
                for (int y = 0; y < b.Height; y++)
                {
                    int row = y * d.Stride;
                    for (int x = 0; x < b.Width; x++)
                    {
                        if (px[row + x * 4 + 3] > 8)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
                if (maxX < 0) return full;
                minX = Math.Max(0, minX - 4); minY = Math.Max(0, minY - 4);
                maxX = Math.Min(b.Width - 1, maxX + 4); maxY = Math.Min(b.Height - 1, maxY + 4);
                return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            }
            catch { return new Rectangle(0, 0, b.Width, b.Height); }
        }

        public static void Set(bool dark)
        {
            Dark = dark;
            if (!dark)
            {
                // 白昼:暖灰宣纸 + 中性墨字 + 不刺眼的朱砂红。
                Bg = Hex("#EAE8E3");
                Card = Hex("#ECEAE6");
                CardAlt = Hex("#E5E2DD");
                Text = Hex("#343334");
                Sub = Hex("#74716C");
                Faint = Hex("#97928A");
                Border = Hex("#C9C6C0");
                Track = Hex("#DFDCD6");
                Hover = Hex("#E3E0DA");
                Gold = Hex("#B99A82");
                Stitch = Color.FromArgb(0, 166, 146, 118);
                NavPill = Hex("#FAF9F6");
                NavBorder = Hex("#D2CFC9");
                Warn = Hex("#B5761C");
                Danger = Hex("#A3211B");
            }
            else
            {
                // 影刃:中性烟灰皮革 + 米白正文,避免整屏泛黄、泛棕。
                Bg = Hex("#141416");
                Card = Hex("#1B1B1C");
                CardAlt = Hex("#181819");
                Text = Hex("#F0EBE3");
                Sub = Hex("#AAA59D");
                Faint = Hex("#7E7A74");
                Border = Hex("#3B3A39");
                Track = Hex("#2B2B2C");
                Hover = Hex("#2A2929");
                Gold = Hex("#A88770");
                Stitch = Color.FromArgb(0, 236, 214, 182);
                NavPill = Hex("#29292A");
                NavBorder = Hex("#3A3938");
                Warn = Hex("#E0A03C");
                Danger = Hex("#C0281F");
            }
            ApplyAccent();
            BuildTexture();
        }

        /// <summary>切换强调色档位:三档都是朱砂红系,只调明度。</summary>
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
                // 野兽:最亮的朱砂。
                Accent = Dark ? Hex("#8A2724") : Hex("#A03C34");
                AccentHover = Dark ? Hex("#A03631") : Hex("#B34A41");
            }
            else if (Mode == 2)
            {
                // 自定义:偏赭的暗红,和野兽的红拉开一点。
                Accent = Dark ? Hex("#B44A2E") : Hex("#8E3B24");
                AccentHover = Dark ? Hex("#CC5C3C") : Hex("#AA4A2E");
            }
            else
            {
                Accent = Dark ? Hex("#B5271D") : Hex("#9C2A22");
                AccentHover = Dark ? Hex("#D03627") : Hex("#B73229");
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

        /// <summary>铺满整个控件区域的底纹。offset 是控件在窗口坐标里的位置。</summary>
        public static void PaintBg(Graphics g, Rectangle r)
        {
            PaintBg(g, r, Point.Empty);
        }

        private static bool CropCache(Graphics g, Bitmap src, Rectangle r, Point offset)
        {
            if (src == null) return false;
            Rectangle s = new Rectangle(offset.X + r.X, offset.Y + r.Y, r.Width, r.Height);
            if (s.X < 0 || s.Y < 0 || s.Right > src.Width || s.Bottom > src.Height) return false;
            g.DrawImage(src, r, s, GraphicsUnit.Pixel);
            return true;
        }

        public static void PaintBg(Graphics g, Rectangle r, Point offset)
        {
            if (CropCache(g, bgCache, r, offset)) return;
            if (texBrush == null) BuildTexture();
            if (offset.IsEmpty)
            {
                g.FillRectangle(texBrush, r);
                return;
            }
            using (TextureBrush b = (TextureBrush)texBrush.Clone())
            {
                b.TranslateTransform(-offset.X, -offset.Y);
                g.FillRectangle(b, r);
            }
        }

        /// <summary>
        /// 毛玻璃卡面:模糊底图 + 半透明奶白/烟灰 + 极淡的顶部反光。
        /// cardTop/cardHeight 用窗口坐标描述整张卡片,这样卡片内每个子控件
        /// 画出来的反光都接得上,不会在每个标签顶上留下一条白带。
        /// </summary>
        public static void PaintGlass(Graphics g, Rectangle r, Point offset, int cardTop, int cardHeight)
        {
            if (!CropCache(g, Dark ? bgCache : bgBlur, r, offset)) PaintBg(g, r, offset);
            // 小尺寸底栏更柔和,大表盘卡保留更多水墨;透明度按整卡高度统一计算。
            int logicalH = Math.Max(1, (int)Math.Round(cardHeight / (double)FrameForge.UI.Ui.S));
            int alpha = Dark
                ? (logicalH <= 70 ? 116 : (logicalH <= 160 ? 50 : 60))
                : (logicalH <= 70 ? 158 : (logicalH <= 160 ? 172 : 102));
            Color glassTint = Dark
                ? (logicalH <= 70 ? Color.FromArgb(alpha, 22, 22, 24) : Color.FromArgb(alpha, 16, 16, 18))
                : (logicalH <= 160 ? Color.FromArgb(alpha, 248, 245, 240)
                                   : Color.FromArgb(alpha, 234, 230, 225));
            using (var b = new SolidBrush(glassTint)) g.FillRectangle(b, r);

            int hh = Math.Max(2, cardHeight * 2 / 5);
            int sy = cardTop - offset.Y;
            using (var lg = new LinearGradientBrush(
                new Rectangle(0, sy, 1, hh),
                Color.FromArgb(Dark ? 10 : 24, 255, 255, 255), Color.Transparent, LinearGradientMode.Vertical))
                g.FillRectangle(lg, new Rectangle(r.X, sy, r.Width, hh));
        }

        /// <summary>按窗口尺寸生成底图缓存与模糊版,主题或尺寸变化时重建。</summary>
        public static void PrepareBackground(Size size)
        {
            if (size.Width <= 0 || size.Height <= 0) return;
            if (bgCache != null && bgSize == size && bgDark == Dark) return;

            Bitmap art = Dark ? bgArtDark : bgArt;
            Bitmap nb = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(nb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                if (art != null)
                {
                    float sc = Math.Max((float)size.Width / art.Width, (float)size.Height / art.Height);
                    int w = (int)Math.Ceiling(art.Width * sc);
                    int h = (int)Math.Ceiling(art.Height * sc);
                    g.DrawImage(art, new Rectangle((size.Width - w) / 2, (size.Height - h) / 2, w, h));
                    // 只轻微统一色温,保留原图的中性灰水墨和暗红墨痕。
                    using (var tint = new SolidBrush(Dark ? Color.FromArgb(8, 20, 20, 24)
                                                          : Color.FromArgb(12, 240, 236, 228)))
                        g.FillRectangle(tint, 0, 0, size.Width, size.Height);
                }
                else
                {
                    if (texBrush == null) BuildTexture();
                    g.FillRectangle(texBrush, 0, 0, size.Width, size.Height);
                }
                // 暗角一次性烘进缓存,页面和卡片都不用再画一遍。
                using (var vp = new GraphicsPath())
                {
                    vp.AddRectangle(new Rectangle(0, 0, size.Width + 1, size.Height + 1));
                    using (var pgb = new PathGradientBrush(vp))
                    {
                        pgb.CenterColor = Color.Transparent;
                        Color edge = Color.FromArgb(Dark ? 100 : 22, 0, 0, 0);
                        pgb.SurroundColors = new Color[] { edge, edge, edge, edge };
                        g.FillRectangle(pgb, 0, 0, size.Width, size.Height);
                    }
                }
            }
            if (bgCache != null) bgCache.Dispose();
            if (bgBlur != null) bgBlur.Dispose();
            bgCache = nb;
            bgBlur = Blur(nb);
            bgSize = size;
            bgDark = Dark;
        }

        /// <summary>缩小再放大得到的柔和模糊,足够做毛玻璃底,而且只在尺寸变化时算一次。</summary>
        private static Bitmap Blur(Bitmap src)
        {
            int divisor = Dark ? 4 : 6;
            int sw = Math.Max(1, src.Width / divisor), sh = Math.Max(1, src.Height / divisor);
            Bitmap big = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
            using (var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb))
            {
                using (Graphics sg = Graphics.FromImage(small))
                {
                    sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    sg.DrawImage(src, 0, 0, sw, sh);
                }
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(small, 0, 0, src.Width, src.Height);
                }
            }
            return big;
        }

        /// <summary>生成 512×512 可平铺底纹(没有底图时兜底):宣纸纤维 / 皮革纹理。</summary>
        private static void BuildTexture()
        {
            if (texBrush != null) { texBrush.Dispose(); texBrush = null; }
            if (tex != null) { tex.Dispose(); tex = null; }

            Bitmap bmp = new Bitmap(512, 512, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Dark ? Hex("#161110") : Hex("#EFE9DC"));
                Random rnd = new Random(Dark ? 7331 : 4211);

                if (!Dark)
                {
                    // 大块纸浆晕染(跨边界的椭圆要同步画到相邻 tile,否则平铺后会出现直线接缝)
                    for (int i = 0; i < 46; i++)
                    {
                        int w = rnd.Next(70, 230), h = rnd.Next(50, 180);
                        int x = rnd.Next(-70, 512), y = rnd.Next(-70, 512);
                        bool light = rnd.Next(2) == 0;
                        Color c = light
                            ? Color.FromArgb(rnd.Next(8, 20), 255, 252, 243)
                            : Color.FromArgb(rnd.Next(7, 17), 196, 183, 162);
                        using (var b = new SolidBrush(c)) Blob(g, b, x, y, w, h);
                    }
                    // 纸纤维:短而碎的明暗细线
                    for (int i = 0; i < 2600; i++)
                    {
                        int x = rnd.Next(512), y = rnd.Next(512), len = rnd.Next(2, 9);
                        bool light = rnd.Next(2) == 0;
                        using (var pen = new Pen(light
                            ? Color.FromArgb(rnd.Next(5, 15), 255, 252, 242)
                            : Color.FromArgb(rnd.Next(4, 12), 150, 132, 108), 1f))
                            g.DrawLine(pen, x, y, x + len, y + rnd.Next(-1, 2));
                    }
                    // 大理石纹:几条极淡的灰褐曲纹,补足宣纸的层次
                    for (int i = 0; i < 7; i++)
                    {
                        using (var pen = new Pen(Color.FromArgb(rnd.Next(6, 13), 150, 138, 120), rnd.Next(6, 16)))
                        {
                            pen.StartCap = LineCap.Round;
                            pen.EndCap = LineCap.Round;
                            using (var p = new GraphicsPath())
                            {
                                float x0 = rnd.Next(-40, 300), y0 = rnd.Next(-20, 512);
                                p.AddBezier(x0, y0, x0 + rnd.Next(80, 180), y0 + rnd.Next(-90, 90),
                                    x0 + rnd.Next(200, 320), y0 + rnd.Next(60, 200),
                                    x0 + rnd.Next(340, 520), y0 + rnd.Next(-40, 140));
                                for (int dx = -1; dx <= 1; dx++)
                                    for (int dy = -1; dy <= 1; dy++)
                                    {
                                        GraphicsState st = g.Save();
                                        g.TranslateTransform(dx * 512f, dy * 512f);
                                        g.DrawPath(pen, p);
                                        g.Restore(st);
                                    }
                            }
                        }
                    }
                    // 边缘朱砂墨擦
                    DrawInk(g, rnd, 24, 150, 48, 38);
                }
                else
                {
                    // 皮革:深色斑块 + 褶皱
                    for (int i = 0; i < 40; i++)
                    {
                        int w = rnd.Next(80, 240), h = rnd.Next(60, 200);
                        int x = rnd.Next(-70, 512), y = rnd.Next(-70, 512);
                        using (var b = new SolidBrush(Color.FromArgb(rnd.Next(10, 26), 46, 33, 25)))
                            Blob(g, b, x, y, w, h);
                    }
                    for (int i = 0; i < 70; i++)
                    {
                        int x = rnd.Next(512), y = rnd.Next(512);
                        float len = rnd.Next(30, 130);
                        double ang = rnd.NextDouble() * Math.PI;
                        using (var pen = new Pen(Color.FromArgb(rnd.Next(10, 26),
                            rnd.Next(2) == 0 ? 70 : 5, rnd.Next(2) == 0 ? 54 : 3, rnd.Next(2) == 0 ? 42 : 2), 1f))
                            g.DrawLine(pen, x, y, x + (float)Math.Cos(ang) * len, y + (float)Math.Sin(ang) * len);
                    }
                    for (int i = 0; i < 1500; i++)
                    {
                        int x = rnd.Next(512), y = rnd.Next(512);
                        using (var b = new SolidBrush(Color.FromArgb(rnd.Next(4, 14), 210, 190, 160)))
                            g.FillEllipse(b, x, y, 1, 1);
                    }
                    DrawInk(g, rnd, 40, 176, 32, 22);
                }

                // 注意:底纹必须可无缝平铺,不能在这里做四边压暗,
                // 否则每块 tile 的边缘都会显出一条灰带,整个页面像贴了格子。
            }
            tex = bmp;
            texBrush = new TextureBrush(bmp, WrapMode.Tile);
        }

        /// <summary>画一个跨边界也能无缝平铺的椭圆色斑。</summary>
        private static void Blob(Graphics g, Brush b, float x, float y, float w, float h)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    g.FillEllipse(b, x + dx * 512f, y + dy * 512f, w, h);
        }

        /// <summary>几笔半透明朱砂,模拟纸/皮上的红色墨迹。</summary>
        private static void DrawInk(Graphics g, Random rnd, int alpha, int r, int gr, int b)
        {
            for (int i = 0; i < 3; i++)
            {
                using (var pen = new Pen(Color.FromArgb(alpha - i * 5, r, gr, b), 12f + i * 7f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    using (var p = new GraphicsPath())
                    {
                        float x0 = rnd.Next(-60, 80);
                        float y0 = rnd.Next(-40, 300);
                        p.AddBezier(x0, y0, 140, y0 + rnd.Next(40, 160),
                            320, y0 + rnd.Next(-80, 60), 560, y0 + rnd.Next(30, 200));
                        for (int dx = -1; dx <= 1; dx++)
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                GraphicsState st = g.Save();
                                g.TranslateTransform(dx * 512f, dy * 512f);
                                g.DrawPath(pen, p);
                                g.Restore(st);
                            }
                    }
                }
            }
        }

        public static string Family { get { return I18n.IsEn ? "Segoe UI" : "Microsoft YaHei UI"; } }

        public static void ResetFonts()
        {
            foreach (Font f in Fonts.Values)
            {
                if (f != null)
                {
                    try { f.Dispose(); }
                    catch { }
                }
            }
            Fonts.Clear();
        }

        /// <summary>字体按点数创建,由 GDI+ 依据 DPI 自动换算像素,天然适配缩放。</summary>
        public static Font F(float size, FontStyle style)
        {
            return F(Family, size, style);
        }

        /// <summary>所有读数统一用软件原来的无衬线数字体,和参考图的无衬线数字一致。</summary>
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
