using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace FrameForge.UI
{
    /// <summary>绘制与缩放工具。Ui.S 由主窗口按当前 DPI 设置(150% → 1.5)。</summary>
    public static class Ui
    {
        public static float S = 1f;

        public static int Px(double v) { return (int)Math.Round(v * S); }
        public static float Fp(double v) { return (float)(v * S); }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            if (rad <= 0.5f) { p.AddRectangle(r); return p; }
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            if (c.A == 0) return;
            using (GraphicsPath p = Round(r, rad))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w)
        {
            if (c.A == 0) return;
            using (GraphicsPath p = Round(r, rad))
            using (var pen = new Pen(c, w))
                g.DrawPath(pen, p);
        }

        /// <summary>自上而下的柔和渐变填充,用来做卡片的顶亮底暗。</summary>
        /// <summary>缝线:虚线沿圆角内侧走一圈,像皮革上的手缝边。</summary>
        public static void Stitch(Graphics g, RectangleF r, float rad, Color c)
        {
            if (c.A == 0 || r.Width <= 4 || r.Height <= 4) return;
            using (GraphicsPath p = Round(r, rad))
            using (var pen = new Pen(c, 1f))
            {
                pen.DashStyle = DashStyle.Custom;
                pen.DashPattern = new float[] { 3f, 3f };
                g.DrawPath(pen, p);
            }
        }

        /// <summary>控件在窗口坐标系里的位置,用来让底纹跨控件对齐。</summary>
        public static Point Origin(Control c)
        {
            int x = 0, y = 0;
            Control p = c;
            // 顶层窗口的 Left/Top 是屏幕坐标,加进来反而会让底纹错位。
            while (p != null && !(p is Form))
            {
                x += p.Left;
                y += p.Top;
                p = p.Parent;
            }
            return new Point(x, y);
        }

        public static void FillVGrad(Graphics g, RectangleF r, float rad, Color top, Color bottom)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = Round(r, rad))
            using (var b = new LinearGradientBrush(
                       new RectangleF(r.X, r.Y - 1f, r.Width, r.Height + 2f), top, bottom, LinearGradientMode.Vertical))
                g.FillPath(b, p);
        }

        public static TextFormatFlags Flags(ContentAlignment a, bool wrap)
        {
            TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            if (wrap) f |= TextFormatFlags.WordBreak;
            else f |= TextFormatFlags.SingleLine;
            if (a == ContentAlignment.TopLeft || a == ContentAlignment.MiddleLeft || a == ContentAlignment.BottomLeft)
                f |= TextFormatFlags.Left;
            else if (a == ContentAlignment.TopCenter || a == ContentAlignment.MiddleCenter || a == ContentAlignment.BottomCenter)
                f |= TextFormatFlags.HorizontalCenter;
            else
                f |= TextFormatFlags.Right;
            if (a == ContentAlignment.TopLeft || a == ContentAlignment.TopCenter || a == ContentAlignment.TopRight)
                f |= TextFormatFlags.Top;
            else if (a == ContentAlignment.MiddleLeft || a == ContentAlignment.MiddleCenter || a == ContentAlignment.MiddleRight)
                f |= TextFormatFlags.VerticalCenter;
            else
                f |= TextFormatFlags.Bottom;
            return f;
        }
    }

    /// <summary>所有自绘控件的基类:统一高质量绘制、无闪烁。</summary>
    public class Ctl : Control
    {
        public Ctl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        // 默认只铺底色:裸 Ctl 用作容器时(如页面容器)也需要有背景,否则双缓冲表面会残留黑色
        // 调用 base 以触发 Paint 事件,容器可以在自身坐标系里绘制内容
        protected override void OnPaint(PaintEventArgs e)
        {
            Prep(e.Graphics);
            base.OnPaint(e);
        }

        protected void Prep(Graphics g)
        {
            Hints(g);
            // 卡片里的控件必须铺上和卡片一致的玻璃底:控件单独刷新时不会清屏,
            // 不铺底就会把上一帧的文字叠在下面,出现重影。
            Control p = Parent;
            while (p != null)
            {
                Card card = p as Card;
                if (card != null)
                {
                    card.PaintGlassFor(g, this);
                    return;
                }
                if (p is Form || p is Page) break;
                p = p.Parent;
            }
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
        }

        /// <summary>只设绘制质量、不铺底色:标题栏上需要透出窗口底纹的控件用它。</summary>
        protected void Hints(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }
    }

    /// <summary>圆角卡片。</summary>
    public class Card : Ctl
    {
        public float Radius = 10;
        public bool ShowBorder = true;

        // 卡面玻璃只算一次并缓存:卡片内每个子控件都从这张图裁同一块,
        // 避免每个标签/按钮各自再叠一层半透明底,在卡面上留下亮块和硬边。
        private Bitmap glass;
        private Size glassSize;
        private bool glassDark;
        private Point glassOrigin;

        // 整张卡片的最终外观(圆角外清晰背景 + 内层玻璃 + 边框)也只算一次,
        // 每帧只贴一张图,切页时不再每张卡都重画背景和玻璃。
        private Bitmap face;
        private Size faceSize;
        private bool faceDark;
        private Point faceOrigin;

        /// <summary>按窗口坐标重建页面底纹,圆角外和页面无缝衔接;无卡片底的控件也能复用。</summary>
        public static void PaintBackdrop(Graphics g, Control c)
        {
            if (c == null || c.Width <= 0 || c.Height <= 0) return;
            Theme.PaintBg(g, c.ClientRectangle, Ui.Origin(c));
        }

        private Bitmap Glass()
        {
            if (Width <= 0 || Height <= 0) return null;
            Point o = Ui.Origin(this);
            if (glass != null && glassSize.Width == Width && glassSize.Height == Height
                && glassDark == Theme.Dark && glassOrigin == o)
                return glass;

            if (glass != null) { glass.Dispose(); glass = null; }
            glass = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            glassSize = new Size(Width, Height);
            glassDark = Theme.Dark;
            glassOrigin = o;
            using (Graphics gg = Graphics.FromImage(glass))
            {
                gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                Theme.PaintGlass(gg, ClientRectangle, o, o.Y, Height);
            }
            return glass;
        }

        // 缓存按尺寸自检重建,不在这里清空:可见性/布局切换也会触发 Resize,
        // 一清就把预热好的卡面白白扔掉,首次显示又变慢。
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (glass != null) { glass.Dispose(); glass = null; }
                if (face != null) { face.Dispose(); face = null; }
            }
            base.Dispose(disposing);
        }

        /// <summary>提前把整卡外观算好,首次切到该页时不再现场生成。</summary>
        public void WarmFace()
        {
            Face();
        }

        private Bitmap Face()
        {
            if (Width <= 0 || Height <= 0) return null;
            Point o = Ui.Origin(this);
            if (face != null && faceSize.Width == Width && faceSize.Height == Height
                && faceDark == Theme.Dark && faceOrigin == o)
                return face;

            if (face != null) { face.Dispose(); face = null; }
            Bitmap bmp = Glass();
            var img = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (Graphics gg = Graphics.FromImage(img))
            {
                gg.SmoothingMode = SmoothingMode.AntiAlias;
                gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                PaintBackdrop(gg, this);

                float rad = Ui.Fp(Radius);
                using (GraphicsPath gp = Ui.Round(new RectangleF(0, 0, Width, Height), rad))
                {
                    GraphicsState st = gg.Save();
                    gg.SetClip(gp);
                    if (bmp != null) gg.DrawImageUnscaled(bmp, 0, 0);
                    gg.Restore(st);
                }
                if (ShowBorder)
                    Ui.Stroke(gg, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), rad, Theme.Border, 1f);
            }
            face = img;
            faceSize = new Size(Width, Height);
            faceDark = Theme.Dark;
            faceOrigin = o;
            return face;
        }

        /// <summary>卡片里的子控件调用:直接从卡片玻璃缓存裁同一块,和卡面完全一致。</summary>
        public void PaintGlassFor(Graphics g, Control c)
        {
            if (c == null || c.Width <= 0 || c.Height <= 0) return;
            Bitmap bmp = Glass();
            if (bmp == null) return;
            Point co = Ui.Origin(c);
            int sx = co.X - glassOrigin.X, sy = co.Y - glassOrigin.Y;
            var src = new Rectangle(sx, sy, c.Width, c.Height);
            Rectangle clip = Rectangle.Intersect(src, new Rectangle(0, 0, bmp.Width, bmp.Height));
            if (clip.Width <= 0 || clip.Height <= 0) return;
            var dest = new Rectangle(clip.X - sx, clip.Y - sy, clip.Width, clip.Height);
            g.DrawImage(bmp, dest, clip, GraphicsUnit.Pixel);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 整卡已经缓存成一张图,这里只做一次位图拷贝。
            Bitmap f = Face();
            if (f != null) g.DrawImageUnscaled(f, 0, 0);
        }
    }

    /// <summary>纯文本标签。</summary>
    public class TextLabel : Ctl
    {
        public new float Size = 9f;
        public FontStyle Style = FontStyle.Regular;
        public ContentAlignment Align = ContentAlignment.MiddleLeft;
        public bool Wrap = false;
        public bool Ellipsis = false;

        public TextLabel()
        {
            // 控件默认前景色是黑色,夜间模式下会变成黑底黑字。
            ForeColor = Theme.Text;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            TextFormatFlags f = Ui.Flags(Align, Wrap);
            if (Ellipsis) f |= TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, Text ?? "", Theme.F(Size, Style), ClientRectangle, ForeColor, f);
        }
    }

    /// <summary>扁平按钮,主按钮使用强调色。支持忙碌态。</summary>
    public class FlatButton : Ctl
    {
        public bool Primary = true;
        /// <summary>幽灵按钮:无底色,只描边 + 强调色文字,和实心/普通按钮区分开。</summary>
        public bool Ghost;
        public float Radius = -1;
        public float FontPt = 10.5f;
        /// <summary>主按钮强调色,为空时用主题色(野兽模式会改成红色)。</summary>
        public Color AccentColor = Color.Empty;
        private bool hover, down;

        private bool busy;
        private int spin;
        private Timer anim;

        public bool Busy
        {
            get { return busy; }
            set
            {
                if (busy == value) return;
                busy = value;
                if (busy)
                {
                    if (anim == null)
                    {
                        anim = new Timer();
                        anim.Interval = 40;
                        anim.Tick += new EventHandler(OnSpin);
                    }
                    spin = 0;
                    anim.Start();
                }
                else if (anim != null) anim.Stop();
                Invalidate();
            }
        }

        private void OnSpin(object sender, EventArgs e)
        {
            spin = (spin + 12) % 360;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            // 控件使用紧凑的 6px 圆角，不再把所有操作都做成胶囊。
            float rad = Radius < 0 ? Math.Min(Ui.Fp(6), Height / 2f) : Ui.Fp(Radius);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            Color fill, fg;
            if (Primary)
            {
                // 朱砂实心 + 上亮下暗的轻微渐变 + 一圈金线,像一枚漆器按钮。
                Color acc = AccentColor.A == 0 ? Theme.Accent : AccentColor;
                if (!Enabled) acc = Theme.Mix(acc, Theme.Card, 0.55);
                fill = down ? Theme.Mix(acc, Color.Black, 0.20)
                    : (hover ? Theme.Mix(acc, Color.White, 0.09) : acc);
                Ui.FillVGrad(g, r, rad, Theme.Mix(fill, Color.White, Theme.Dark ? 0f : 0.03),
                    Theme.Mix(fill, Color.Black, Theme.Dark ? 0.08 : 0.05));
                Ui.Stroke(g, r, rad, Theme.Alpha(Theme.Mix(acc, Color.White, 0.25), Enabled ? 210 : 90), 1f);
                fg = Enabled ? Theme.Hex("#FBEFDD") : Theme.Alpha(Theme.Hex("#FBEFDD"), 150);
            }
            else if (Ghost)
            {
                fill = hover || down ? Theme.Hover : Theme.Alpha(Theme.Accent, Theme.Dark ? 26 : 14);
                Ui.Fill(g, r, rad, fill);
                Ui.Stroke(g, r, rad, Theme.Mix(Theme.Accent, Theme.Border, 0.35), 1f);
                fg = Enabled ? Theme.Accent : Theme.Sub;
            }
            else
            {
                // 次级按钮用半透明白,让卡面的水墨纹理透出来,不再是一块实心卡色。
                int wa = Theme.Dark ? 18 : 96;
                fill = down ? Color.FromArgb(Theme.Dark ? 34 : 150, 255, 255, 255)
                    : (hover ? Color.FromArgb(wa + (Theme.Dark ? 12 : 28), 255, 255, 255)
                             : Color.FromArgb(wa, 255, 255, 255));
                Ui.Fill(g, r, rad, fill);
                Ui.Stroke(g, r, rad, Theme.Border, 1f);
                fg = Enabled ? Theme.Text : Theme.Sub;
            }

            // 转圈固定画在按钮右侧留白里。
            float d = Height * 0.42f;
            RectangleF sr = new RectangleF(Width - d - Ui.Fp(14), (Height - d) / 2f, d, d);

            // 文字始终在整颗按钮里居中:忙碌时也不缩小文字区域,避免点下去文字突然前移。
            Font font = Theme.F(FontPt, Primary ? FontStyle.Bold : FontStyle.Regular);
            TextRenderer.DrawText(g, Text ?? "", font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            // 转圈只放在文字右侧的空白里:挤不下就跳过,绝不压住或推走文字。
            if (busy)
            {
                int tw = TextRenderer.MeasureText(Text ?? "", font).Width;
                bool room = (Width + tw) / 2 + Ui.Px(6) <= sr.X;
                if (room)
                {
                    using (var pen = new Pen(Color.FromArgb(160, fg), Math.Max(1.5f, d * 0.14f)))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        g.DrawArc(pen, sr, spin, 260);
                    }
                }
            }
        }
    }

    public enum Glyph { Lang, Theme, Minimize, Close }

    /// <summary>标题栏图标按钮:悬停圆形高亮,关闭按钮悬停变红。</summary>
    public class IconButton : Ctl
    {
        public Glyph Kind;
        public string LangText = "中";
        private bool hover, down;

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // 标题栏有底纹,按钮不能铺一块纯色底。
            Hints(g);
            Theme.PaintBg(g, ClientRectangle, Ui.Origin(this));
            float d = Math.Min(Width, Height) - Ui.Fp(4);
            var c = new RectangleF((Width - d) / 2f, (Height - d) / 2f, d, d);
            Color hoverFill = Kind == Glyph.Close ? Color.FromArgb(down ? 255 : 230, Theme.Danger) : Color.FromArgb(Theme.Dark ? 26 : 14, Theme.Text);
            if (hover) Ui.Fill(g, c, Ui.Fp(5), hoverFill);

            Color fg = Kind == Glyph.Close && hover ? Color.White : Theme.Sub;
            if (Kind == Glyph.Lang)
            {
                float pt = d / Ui.S * 0.30f;
                TextRenderer.DrawText(g, LangText, Theme.F(pt, FontStyle.Regular), Rectangle.Round(c), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                return;
            }

            using (var pen = new Pen(fg, Math.Max(1.4f, d * 0.075f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float cx = c.X + c.Width / 2f, cy = c.Y + c.Height / 2f;
                float r = d * 0.22f;
                if (Kind == Glyph.Minimize)
                {
                    g.DrawLine(pen, cx - r, cy, cx + r, cy);
                }
                else if (Kind == Glyph.Close)
                {
                    float k = r * 0.95f;
                    g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
                    g.DrawLine(pen, cx + k, cy - k, cx - k, cy + k);
                }
                else if (Kind == Glyph.Theme)
                {
                    // 两张参考图都使用太阳图标,主题按钮不再随夜间模式换成月亮。
                    using (var b = new SolidBrush(fg))
                        g.FillEllipse(b, cx - r * 0.55f, cy - r * 0.55f, r * 1.1f, r * 1.1f);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        g.DrawLine(pen,
                            cx + (float)(Math.Cos(a) * r * 0.95), cy + (float)(Math.Sin(a) * r * 0.95),
                            cx + (float)(Math.Cos(a) * r * 1.35), cy + (float)(Math.Sin(a) * r * 1.35));
                    }
                }
            }
        }
    }

    /// <summary>
    /// 开关:胶囊轨道 + 白色旋钮,开合带动画。
    /// 轨道比控件小一圈并用整数坐标绘制——圆角一旦贴着控件边界,
    /// 父容器裁剪或 DPI 取整只要吃掉 1 像素,下沿就会被削成直线,看着像开关缺了一条边。
    /// </summary>
    public class Toggle : Ctl
    {
        private const float TrackW = 44f;
        private const float TrackH = 24f;

        private bool check;
        private float anim;
        private Timer t;

        public event EventHandler CheckedChanged;

        public Toggle() { Cursor = Cursors.Hand; }

        public bool Checked
        {
            get { return check; }
            set
            {
                if (check == value) return;
                check = value;
                StartAnim();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public void SetSilent(bool v) { check = v; anim = v ? 1f : 0f; Invalidate(); }

        private void StartAnim()
        {
            if (t == null)
            {
                t = new Timer();
                t.Interval = 15;
                t.Tick += new EventHandler(OnTick);
            }
            t.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            float target = check ? 1f : 0f;
            float d = target - anim;
            if (Math.Abs(d) < 0.06f) { anim = target; t.Stop(); }
            else anim += d * 0.28f;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) Checked = !Checked;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);

            // 轨道用整数像素计算:控件尺寸经过 DPI 取整后常带小数(如 125% 下 55x30),
            // 按浮点居中会让上下两条边各落在半个像素上,抗锯齿一重一轻,下沿看着比上沿"扁",
            // 像被父容器裁掉了一条。这里先取整宽高,再用整数坐标居中。
            int m = Math.Max(1, (int)Math.Round(Ui.Fp(1)));
            int tw = Math.Min(Width - m * 2, (int)Math.Round(Ui.Fp(TrackW)));
            int th = Math.Min(Height - m * 2, (int)Math.Round(Ui.Fp(TrackH)));
            int cap = (int)Math.Round(tw * 0.62f);
            if (th > cap) th = cap;
            if (tw < 12 || th < 8) return;

            var track = new RectangleF((Width - tw) / 2, (Height - th) / 2, tw, th);
            float rad = track.Height * 0.5f;

            // 两套主题一致:开启时是朱砂轨道 + 白色旋钮。
            Color on = Theme.Dark ? Theme.Hex("#A62E2A") : Theme.Hex("#A62F29");
            Color off = Theme.Dark ? Theme.Hex("#2B2B2C") : Theme.Hex("#D6D3CD");
            Color fill = Theme.Mix(off, on, anim);
            Color edge = Theme.Mix(fill, Color.Black, Theme.Dark ? 0.30f : 0.14f);

            // 描边画成"外圈填边色 + 内圈填底色"的环,而不是拿 1px 画笔去描路径:
            // 画笔是骑在路径上的,一半会溢出到轨道外面,再叠上父容器裁剪,
            // 下沿那一半就没了。环永远完整落在轨道矩形内,四边一样粗。
            Ui.Fill(g, track, rad, edge);
            var inner = new RectangleF(track.X + 1f, track.Y + 1f, track.Width - 2f, track.Height - 2f);
            Ui.Fill(g, inner, Math.Max(0f, rad - 1f), fill);

            // 旋钮:四周留出内边距,滑到两端也不会贴死圆弧。
            float inset = Math.Max(2f, (float)Math.Round(track.Height * 0.085f));
            float knob = track.Height - inset * 2f;
            if (knob <= 3f) return;
            float from = track.X + inset;
            float to = track.Right - inset - knob;
            float kx = (float)Math.Round(from + (to - from) * anim);
            float ky = (float)Math.Round(track.Y + inset);
            Color kc = Theme.Mix(Theme.Hex("#E7E4DE"), Color.White, anim);
            using (var b = new SolidBrush(kc))
                g.FillEllipse(b, kx, ky, knob, knob);
            using (var pen = new Pen(Theme.Alpha(Color.Black, 25), 1f))
                g.DrawEllipse(pen, kx + 0.5f, ky + 0.5f, knob - 1f, knob - 1f);
        }
    }

    /// <summary>分段选择器。</summary>
    public class Segmented : Ctl
    {
        public string[] Items = new string[0];
        public event EventHandler Changed;
        /// <summary>选中胶囊颜色,为空时用主题色(野兽模式会改成红色)。</summary>
        public Color AccentColor = Color.Empty;
        private int sel;
        private float anim = 0;
        private int hoverIndex = -1;
        private Timer t;

        public int Selected
        {
            get { return sel; }
            set
            {
                if (value < 0) value = 0;
                if (Items.Length > 0 && value >= Items.Length) value = Items.Length - 1;
                if (sel == value) { anim = value; Invalidate(); return; }
                sel = value;
                StartAnim();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
        }

        public void SetSilent(int v)
        {
            sel = v;
            anim = v;
            Invalidate();
        }

        private void StartAnim()
        {
            if (t == null)
            {
                t = new Timer();
                t.Interval = 15;
                t.Tick += new EventHandler(OnTick);
            }
            t.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            float d = sel - anim;
            if (Math.Abs(d) < 0.03f) { anim = sel; t.Stop(); }
            else anim += d * 0.3f;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = Hit(e.X);
            if (i != hoverIndex) { hoverIndex = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = Hit(e.X);
            if (i >= 0) Selected = i;
            base.OnMouseDown(e);
        }

        private int Hit(int x)
        {
            if (Items.Length == 0 || Width <= 0) return -1;
            float itemW = (float)Width / Items.Length;
            int hit = (int)(x / itemW);
            if (hit < 0) hit = 0;
            if (hit >= Items.Length) hit = Items.Length - 1;
            return hit;
        }

        /// <summary>铺所在容器的底:卡片里画同一层毛玻璃,标题栏/页面上铺窗口底纹。</summary>
        private void Backdrop(Graphics g)
        {
            Control p = Parent;
            while (p != null)
            {
                Card card = p as Card;
                if (card != null)
                {
                    card.PaintGlassFor(g, this);
                    return;
                }
                if (p is Form || p is Page) break;
                p = p.Parent;
            }
            Theme.PaintBg(g, ClientRectangle, Ui.Origin(this));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // 导航条直接压在标题栏底纹上,卡片里的分段控件则必须跟卡片共用毛玻璃底。
            Hints(g);
            Backdrop(g);
            if (Items.Length == 0) return;

            float itemW = (float)Width / Items.Length;
            var pill = new RectangleF(anim * itemW + Ui.Fp(1), 0,
                itemW - Ui.Fp(2), Height - Ui.Fp(2));
            Ui.Fill(g, pill, Ui.Fp(6), Theme.NavPill);
            Ui.Stroke(g, pill, Ui.Fp(6), Theme.NavBorder, 1f);
            Color acc = AccentColor.A == 0 ? Theme.Accent : AccentColor;
            float uw = Math.Min(Ui.Fp(26), pill.Width * 0.34f);
            Ui.Fill(g, new RectangleF(pill.X + (pill.Width - uw) / 2f, pill.Bottom - Ui.Fp(5), uw, Ui.Fp(2)),
                Ui.Fp(1), acc);

            for (int i = 0; i < Items.Length; i++)
            {
                var r = new RectangleF(i * itemW, 0, itemW, Height - Ui.Fp(3));
                bool selected = i == sel;
                Color fg = selected ? Theme.Text : (i == hoverIndex ? Theme.Mix(Theme.Sub, Theme.Text, 0.6) : Theme.Sub);
                TextRenderer.DrawText(g, Items[i], Theme.F(9.4f, selected ? FontStyle.Bold : FontStyle.Regular),
                    Rectangle.Round(r), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>底部模式条:纯文字标签 + 选中下划光条,仪表盘风格。</summary>
    public class TabBar : Ctl
    {
        public string[] Items = new string[0];
        public event EventHandler Changed;
        /// <summary>选中下划线颜色,为空时用主题色(野兽模式会改成红色)。</summary>
        public Color AccentColor = Color.Empty;
        /// <summary>最后一项右侧预留的像素宽度,用来在“自定义”块里摆一个跟随的小按钮。</summary>
        public int LastItemInset = 0;
        private int sel;
        private int hoverIndex = -1;

        public int Selected
        {
            get { return sel; }
            set
            {
                if (value < 0) value = 0;
                if (Items.Length > 0 && value >= Items.Length) value = Items.Length - 1;
                if (sel == value) return;
                sel = value;
                Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
        }

        public void SetSilent(int v) { sel = v < 0 ? 0 : v; Invalidate(); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = Hit(e.X);
            if (i != hoverIndex) { hoverIndex = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = Hit(e.X);
            if (i >= 0) Selected = i;
            base.OnMouseDown(e);
        }

        private int Hit(int x)
        {
            if (Items.Length == 0 || Width <= 0) return -1;
            int i = (int)(x / ((float)Width / Items.Length));
            if (i < 0) i = 0;
            if (i >= Items.Length) i = Items.Length - 1;
            return i;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            if (Items.Length == 0) return;

            Color selCol = AccentColor.A == 0 ? Theme.Accent : AccentColor;
            float itemW = (float)Width / Items.Length;
            for (int i = 0; i < Items.Length; i++)
            {
                var r = new RectangleF(i * itemW, 0, itemW, Height);
                // 最后一块右侧要给“编辑选项”小按钮腾位,文字和下划线都在让开后的区域里居中。
                if (i == Items.Length - 1 && LastItemInset > 0)
                    r.Width = Math.Max(Ui.Fp(24), r.Width - LastItemInset);
                bool on = i == sel;
                Color fg = on ? (Theme.Dark ? Theme.Mix(selCol, Color.White, 0.18f) : selCol)
                    : (i == hoverIndex ? Theme.Mix(Theme.Sub, Theme.Text, 0.55) : Theme.Sub);
                TextRenderer.DrawText(g, Items[i], Theme.F(10.5f, on ? FontStyle.Bold : FontStyle.Regular),
                    Rectangle.Round(r), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

                if (on)
                {
                    Color acc = AccentColor.A == 0 ? Theme.Accent : AccentColor;
                    float uw = Math.Min(Ui.Fp(30), r.Width * 0.34f);
                    var bar = new RectangleF(r.X + (r.Width - uw) / 2f, Height - Ui.Fp(7), uw, Math.Max(2f, Ui.Fp(2.5)));
                    // 一条实心下划线就够了:去掉外圈光晕,让文字自己说明选中状态。
                    Ui.Fill(g, bar, bar.Height / 2f, acc);
                }
            }
        }
    }

    /// <summary>圆环仪表:占用率 + 中心百分比 + 下方已用/总量。</summary>
    public class RingGauge : Ctl
    {
        /// <summary>低于警告阈值时的强调色,为空时用主题色。</summary>
        public Color AccentColor = Color.Empty;
        /// <summary>圆环外侧的刻度盘(仪表盘风格)。</summary>
        public bool Ticks = false;
        /// <summary>环内上方的小标题,如 CPU / GPU。</summary>
        public string Caption = "";
        private double target, disp;
        private Timer t;
        private string sub = "";

        public void SetValue(double v, string subText)
        {
            if (v < 0) v = 0;
            if (v > 1) v = 1;
            bool need = Math.Abs(v - target) > 0.0001 || subText != sub;
            target = v;
            sub = subText;
            if (t == null)
            {
                t = new Timer();
                t.Interval = 16;
                t.Tick += new EventHandler(OnTick);
                disp = v;
            }
            if (need && !t.Enabled) t.Start();
            Invalidate();
        }

        private void OnTick(object sender, EventArgs e)
        {
            double d = target - disp;
            if (Math.Abs(d) < 0.002) { disp = target; t.Stop(); }
            else disp += d * 0.22;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);

            // 刻度、投影、金属圈、内盘、进度弧分层绘制,尺寸按参考图的大表盘比例收口。
            float side = Math.Min(Width, Height);
            float cx = Width / 2f, cy = Height / 2f;
            float outer = side * (Ticks ? 0.385f : 0.39f);
            float band = Math.Max(Ui.Fp(5), side * 0.036f);
            if (Ticks) DrawTicks(g, cx, cy, outer + Ui.Fp(10), side);

            var ro = new RectangleF(cx - outer, cy - outer, outer * 2f, outer * 2f);
            var ri = new RectangleF(cx - outer + band, cy - outer + band,
                (outer - band) * 2f, (outer - band) * 2f);

            // 柔和的右下投影让金属圈从背景上浮起来,不使用生硬的纯黑单边。
            for (int i = 4; i >= 1; i--)
            {
                float grow = Ui.Fp(i * 1.2f);
                using (var pen = new Pen(Color.FromArgb(Theme.Dark ? 13 : 8, 0, 0, 0), Ui.Fp(2.2f)))
                    g.DrawEllipse(pen, ro.X - grow + Ui.Fp(1), ro.Y - grow + Ui.Fp(3),
                        ro.Width + grow * 2f, ro.Height + grow * 2f);
            }

            using (var ring = new GraphicsPath())
            {
                ring.FillMode = FillMode.Alternate;
                ring.AddEllipse(ro);
                ring.AddEllipse(ri);
                using (var b = new LinearGradientBrush(ro, Color.White, Color.Black, 48f))
                {
                    ColorBlend blend = new ColorBlend(4);
                    blend.Colors = Theme.Dark
                        ? new Color[] { Theme.Hex("#484849"), Theme.Hex("#171718"), Theme.Hex("#2B2B2C"), Theme.Hex("#0C0C0D") }
                        : new Color[] { Theme.Hex("#F4F3F0"), Theme.Hex("#999691"), Theme.Hex("#D9D6D0"), Theme.Hex("#85827D") };
                    blend.Positions = new float[] { 0f, 0.38f, 0.72f, 1f };
                    b.InterpolationColors = blend;
                    g.FillPath(b, ring);
                }
                using (var pen = new Pen(Theme.Alpha(Theme.Dark ? Color.Black : Theme.Hex("#625F5A"), 190), Ui.Fp(1.1f)))
                {
                    g.DrawEllipse(pen, ro);
                    g.DrawEllipse(pen, ri);
                }
                using (var pen = new Pen(Theme.Alpha(Color.White, Theme.Dark ? 52 : 165), Ui.Fp(1.2f)))
                {
                    float hr = outer - Ui.Fp(2.5f);
                    g.DrawArc(pen, new RectangleF(cx - hr, cy - hr, hr * 2f, hr * 2f), 192f, 128f);
                }
            }

            using (var pgp = new GraphicsPath())
            {
                pgp.AddEllipse(ri);
                using (var b = new PathGradientBrush(pgp))
                {
                    b.CenterColor = Theme.Dark ? Theme.Hex("#2B2B2C") : Theme.Hex("#F1F0EC");
                    b.SurroundColors = new Color[] { Theme.Dark ? Theme.Hex("#1B1B1C") : Theme.Hex("#D8D5CF") };
                    g.FillPath(b, pgp);
                }
                using (var pen = new Pen(Theme.Alpha(Theme.Dark ? Color.Black : Theme.Hex("#77736C"), 145), Ui.Fp(1.2f)))
                    g.DrawEllipse(pen, ri.X + 0.5f, ri.Y + 0.5f, ri.Width - 1f, ri.Height - 1f);
            }

            // 弧线落在内盘边缘,左端暗、上端亮,不再压在金属外圈上。
            Color acc = AccentColor.A == 0 ? Theme.Accent : AccentColor;
            if (Theme.Dark) acc = Color.FromArgb((int)(acc.R * 0.93f), (int)(acc.G * 0.88f), (int)(acc.B * 0.88f));
            float midR = outer - band * 1.9f;
            var arc = new RectangleF(cx - midR, cy - midR, midR * 2f, midR * 2f);
            double sweep = 80.0 + disp * 240.0;
            float thick = band * 1.55f;
            GradientArc(g, arc, 205f, (float)sweep, thick, acc, true);
            double ea2 = (205.0 + sweep) * Math.PI / 180.0;
            float ex = cx + (float)Math.Cos(ea2) * midR;
            float ey = cy + (float)Math.Sin(ea2) * midR;
            float tr = Math.Max(2.2f, thick * 0.24f);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, ex - tr, ey - tr, tr * 2f, tr * 2f);

            float iconY = cy - side * 0.180f;
            DrawSword(g, cx, iconY, side * 0.030f);
            if (Caption.Length > 0)
            {
                var capRect = new RectangleF(0, cy - side * 0.147f, Width, side * 0.05f);
                TextRenderer.DrawText(g, Caption, Theme.F(8.5f, FontStyle.Regular), Rectangle.Round(capRect),
                    Theme.Mix(Theme.Sub, Theme.Text, 0.25f),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }

            string pct = ((int)Math.Round(disp * 100)) + "%";
            float pt = side / Math.Max(1f, Ui.S) * 0.142f;
            var txtRect = new RectangleF(0, cy - side * 0.093f, Width, side * 0.17f);
            TextRenderer.DrawText(g, pct, Theme.Num(pt), Rectangle.Round(txtRect), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            var subRect = new RectangleF(0, cy + side * 0.129f, Width, side * 0.05f);
            TextRenderer.DrawText(g, sub, Theme.F(8.5f, FontStyle.Regular), Rectangle.Round(subRect), Theme.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        /// <summary>沿圆弧做明暗过渡;一次描边完成,避免上百段 DrawArc 拖慢重绘。</summary>
        public static void GradientArc(Graphics g, RectangleF rect, float start, float sweep,
            float thick, Color acc, bool metalStart)
        {
            if (sweep <= 0f) return;
            Color first = metalStart
                ? (Theme.Dark ? Theme.Hex("#4E4C4C") : Theme.Hex("#8C8780"))
                : Theme.Mix(acc, Color.Black, 0.38f);
            Color end = Theme.Dark
                ? Color.FromArgb(Math.Min(255, acc.R + 16), acc.G, acc.B)
                : Theme.Mix(acc, Color.White, 0.05f);

            RectangleF box = rect;
            box.Inflate(thick, thick);
            using (var br = new LinearGradientBrush(box, first, end, 315f))
            using (var pen = new Pen(br, thick))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, rect, start, sweep);
            }
        }

        /// <summary>内盘上方的小剑标记,使用提供的透明 PNG,夜间转成米色。</summary>
        private static void DrawSword(Graphics g, float cx, float cy, float r)
        {
            Bitmap sw = Theme.Sword;
            if (sw != null)
            {
                Rectangle src = Theme.SwordBounds;
                float h = r * 2.9f;
                float w = h * src.Width / Math.Max(1, src.Height);
                int dw = Math.Max(2, (int)Math.Round(w));
                int dh = Math.Max(2, (int)Math.Round(h));
                int dx = (int)Math.Round(cx - dw / 2f);
                int dy = (int)Math.Round(cy - dh / 2f);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (Theme.Dark)
                {
                    // 黑色线稿在深色内盘上看不见:用偏移矩阵整体提亮成米色,保留 alpha。
                    ColorMatrix cm = new ColorMatrix(new float[][] {
                        new float[]{1,0,0,0,0},
                        new float[]{0,1,0,0,0},
                        new float[]{0,0,1,0,0},
                        new float[]{0,0,0,1,0},
                        new float[]{0.86f,0.80f,0.68f,0,1}
                    });
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(cm);
                        g.DrawImage(sw, new Rectangle(dx, dy, dw, dh),
                            src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                    }
                }
                else
                {
                    g.DrawImage(sw, new Rectangle(dx, dy, dw, dh),
                        src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel);
                }
                return;
            }

            // 资源缺失时退回矢量小剑
            using (var pen = new Pen(Theme.Alpha(Theme.Sub, 230), Math.Max(1.1f, r * 0.18f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx, cy - r, cx, cy + r * 0.35f);
                g.DrawLine(pen, cx - r * 0.6f, cy + r * 0.35f, cx + r * 0.6f, cy + r * 0.35f);
                g.DrawLine(pen, cx, cy + r * 0.35f, cx, cy + r * 0.95f);
            }
        }

        /// <summary>圆环外侧刻度:小刻度弱、每 6 格一格主刻度。</summary>
        private void DrawTicks(Graphics g, float cx, float cy, float r, float side)
        {
            int count = 60;
            float minor = Math.Max(2.5f, side * 0.018f);
            float major = Math.Max(5f, side * 0.035f);
            using (var pen = new Pen(Theme.Alpha(Theme.Text, Theme.Dark ? 46 : 38), Math.Max(1f, Ui.Fp(1))))
            {
                for (int i = 0; i < count; i++)
                {
                    bool big = i % 6 == 0;
                    double a = (i * (360.0 / count) - 90) * Math.PI / 180.0;
                    float len = big ? major : minor;
                    float x1 = cx + (float)Math.Cos(a) * r;
                    float y1 = cy + (float)Math.Sin(a) * r;
                    float x2 = cx + (float)Math.Cos(a) * (r + len);
                    float y2 = cy + (float)Math.Sin(a) * (r + len);
                    pen.Color = big ? Theme.Alpha(Theme.Text, Theme.Dark ? 100 : 76)
                                    : Theme.Alpha(Theme.Text, Theme.Dark ? 56 : 46);
                    g.DrawLine(pen, x1, y1, x2, y2);
                }
            }
        }
    }

    /// <summary>迷你折线图,渐变填充,无坐标轴。</summary>
    public class Sparkline : Ctl
    {
        /// <summary>折线与渐变颜色,为空时用主题色。</summary>
        public Color AccentColor = Color.Empty;
        private readonly List<float> vals = new List<float>();
        public int Capacity = 60;

        public void Push(float v)
        {
            vals.Add(v);
            while (vals.Count > Capacity) vals.RemoveAt(0);
            Invalidate();
        }

        public void Clear() { vals.Clear(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            if (vals.Count < 2)
            {
                // 还没有采样时什么都不画:留白比几条假虚线干净。
                return;
            }

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < vals.Count; i++)
            {
                if (vals[i] < min) min = vals[i];
                if (vals[i] > max) max = vals[i];
            }
            if (max - min < 0.02f) { max += 0.01f; min -= 0.01f; }

            float padT = Ui.Fp(6);
            float w = Width - 1, h = Height - padT - Ui.Fp(2);

            var pts = new PointF[vals.Count];
            for (int i = 0; i < vals.Count; i++)
            {
                // 最新采样贴右边,历史向左延伸
                float x = w - (float)(vals.Count - 1 - i) / (Capacity - 1) * w;
                float y = padT + (1f - (vals[i] - min) / (max - min)) * h;
                pts[i] = new PointF(x, y);
            }

            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                path.AddLine(pts[pts.Length - 1].X, pts[pts.Length - 1].Y, pts[pts.Length - 1].X, Height);
                path.AddLine(pts[pts.Length - 1].X, Height, pts[0].X, Height);
                path.CloseFigure();
                using (var b = new SolidBrush(Theme.Alpha(AccentColor.A == 0 ? Theme.Accent : AccentColor, Theme.Dark ? 22 : 16)))
                    g.FillPath(b, path);
            }
            using (var pen = new Pen(AccentColor.A == 0 ? Theme.Accent : AccentColor, Math.Max(1.6f, 2f * Ui.S)))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLines(pen, pts);
            }

            // 最新采样使用小型方形游标，不叠加光晕。
            PointF last = pts[pts.Length - 1];
            Color ac = AccentColor.A == 0 ? Theme.Accent : AccentColor;
            float dr = Math.Max(2f, Ui.Fp(2.2f));
            using (var b = new SolidBrush(ac))
                g.FillRectangle(b, last.X - dr, last.Y - dr, dr * 2f, dr * 2f);
        }
    }

    /// <summary>轻提示条,几秒后淡出。</summary>
    public class Toast : Ctl
    {
        private int alpha;
        private int ticks;
        private int phase; // 0 淡入 1 保持 2 淡出 3 隐藏
        private Timer t;

        public void Show(string text)
        {
            Text = text;
            alpha = 0;
            ticks = 60;
            phase = 0;
            Visible = true;
            BringToFront();
            if (t == null)
            {
                t = new Timer();
                t.Interval = 30;
                t.Tick += new EventHandler(OnTick);
            }
            t.Start();
            Invalidate();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (phase == 0)
            {
                alpha += 28;
                if (alpha >= 255) { alpha = 255; phase = 1; }
            }
            else if (phase == 1)
            {
                if (--ticks <= 0) phase = 2;
            }
            else if (phase == 2)
            {
                alpha -= 22;
                if (alpha <= 0) { alpha = 0; phase = 3; t.Stop(); Visible = false; }
            }
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084) { m.Result = (IntPtr)(-1); return; } // HTTRANSPARENT
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

            int a = alpha;
            Color bg = Theme.Dark ? Color.FromArgb(a, 46, 34, 27) : Color.FromArgb(a, 38, 27, 19);
            var r = new RectangleF(0, 0, Width, Height);
            Ui.Fill(g, r, Ui.Fp(6), bg);
            Ui.Stroke(g, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Ui.Fp(6), Color.FromArgb(a, Theme.Gold), 1f);
            TextRenderer.DrawText(g, Text ?? "", Theme.F(9.5f, FontStyle.Regular), ClientRectangle,
                Color.FromArgb(a, 246, 236, 219),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>轻量主题化对话框(替代系统 MessageBox)。</summary>
    public static class DialogBox
    {
        public static void Info(IWin32Window owner, string title, string body)
        {
            Show(owner, title, body, I18n.T("dialog.ok"), null);
        }

        public static bool Ask(IWin32Window owner, string title, string body, string ok, string cancel)
        {
            return Show(owner, title, body, ok, cancel);
        }

        /// <summary>带一个输入框的询问:确定时把去掉首尾空白的文本带出来。</summary>
        public static bool Prompt(IWin32Window owner, string title, string body, string initial, out string text)
        {
            using (var f = new DialogForm(title, body, I18n.T("dialog.ok"), I18n.T("dialog.cancel"), initial))
            {
                bool ok = f.ShowDialog(owner) == DialogResult.OK;
                text = ok ? f.Value : "";
                return ok;
            }
        }

        private static bool Show(IWin32Window owner, string title, string body, string ok, string cancel)
        {
            using (var f = new DialogForm(title, body, ok, cancel))
                return f.ShowDialog(owner) == DialogResult.OK;
        }

        private class DialogForm : Form
        {
            private readonly string dTitle, dBody, dOk, dCancel;
            private FlatButton btnOk, btnCancel;
            private TextBox edit;

            public string Value
            {
                get { return edit == null ? "" : edit.Text.Trim(); }
            }

            public DialogForm(string title, string body, string ok, string cancel) : this(title, body, ok, cancel, null) { }

            public DialogForm(string title, string body, string ok, string cancel, string initial)
            {
                dTitle = title; dBody = body; dOk = ok; dCancel = cancel;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                BackColor = Theme.Card;
                DoubleBuffered = true;
                float s = Ui.S;
                ClientSize = new Size((int)(400 * s), (int)((initial != null ? 240 : 190) * s));
                KeyPreview = true;

                if (initial != null)
                {
                    edit = new TextBox();
                    edit.BorderStyle = BorderStyle.FixedSingle;
                    edit.BackColor = Theme.CardAlt;
                    edit.ForeColor = Theme.Text;
                    edit.Font = Theme.F(10f, FontStyle.Regular);
                    edit.Text = initial;
                    edit.MaxLength = 40;
                    Controls.Add(edit);
                }

                if (cancel != null)
                {
                    btnCancel = new FlatButton();
                    btnCancel.Primary = false;
                    btnCancel.Text = cancel;
                    btnCancel.FontPt = 9.5f;
                    btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
                    Controls.Add(btnCancel);
                }
                btnOk = new FlatButton();
                btnOk.Primary = true;
                btnOk.Text = ok;
                btnOk.FontPt = 9.5f;
                btnOk.Click += delegate { DialogResult = DialogResult.OK; Close(); };
                Controls.Add(btnOk);

                int bh = (int)(34 * s), bw = (int)(120 * s);
                int bottom = ClientSize.Height - (int)(18 * s);
                btnOk.SetBounds(ClientSize.Width - (int)(18 * s) - bw, bottom - bh, bw, bh);
                if (btnCancel != null)
                    btnCancel.SetBounds(ClientSize.Width - (int)(18 * s) - bw * 2 - (int)(10 * s), bottom - bh, bw, bh);
                if (edit != null)
                {
                    edit.SetBounds((int)(22 * s), (int)(100 * s), ClientSize.Width - (int)(44 * s), (int)(30 * s));
                    edit.SelectAll();
                }

                MouseDown += OnDrag;
                KeyDown += delegate (object o, KeyEventArgs k)
                {
                    if (k.KeyCode == Keys.Escape && btnCancel != null) { DialogResult = DialogResult.Cancel; Close(); }
                    if (k.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
                };
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                // 输入框要在窗口显示后再抢焦点,构造函数里设置会被按钮抢走。
                if (edit != null) { edit.Focus(); edit.SelectAll(); }
            }

            private void OnDrag(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                Native.ReleaseCapture();
                Native.SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                int pref = 2;
                if (Native.DwmSetWindowAttribute(Handle, 33, ref pref, 4) != 0)
                {
                    using (GraphicsPath p = Ui.Round(new RectangleF(0, 0, Width, Height), Ui.Fp(8)))
                        Region = new Region(p);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                using (var p = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.Fp(8)))
                using (var pen = new Pen(Theme.Border, 1f))
                    g.DrawPath(pen, p);

                float s = Ui.S;
                var titleRect = new Rectangle((int)(22 * s), (int)(20 * s), Width - (int)(44 * s), (int)(26 * s));
                TextRenderer.DrawText(g, dTitle, Theme.F(11.5f, FontStyle.Bold), titleRect, Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

                var bodyRect = new Rectangle((int)(22 * s), (int)(50 * s), Width - (int)(44 * s), (int)(edit == null ? 78 : 44));
                TextRenderer.DrawText(g, dBody, Theme.F(9.5f, FontStyle.Regular), bodyRect, Theme.Sub,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>列表与报告前面用的小图标。</summary>
    public enum AppGlyph
    {
        App,
        Gamepad,
        Chart,
        Shield
    }

    /// <summary>
    /// 自绘矢量小图标:线宽、留白按方框大小等比缩放,颜色跟随主题,
    /// 不依赖外部图片,也不受 DPI 影响。
    /// </summary>
    public static class AppIcons
    {
        public static void Draw(Graphics g, AppGlyph kind, Rectangle box, Color color)
        {
            if (box.Width <= 0 || box.Height <= 0) return;

            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float x = box.X, y = box.Y, w = box.Width, h = box.Height;
            float sw = Math.Max(1f, Math.Min(w, h) * 0.075f);

            switch (kind)
            {
                case AppGlyph.Shield: Shield(g, x, y, w, h, color, sw); break;
                case AppGlyph.Gamepad: Pad(g, x, y, w, h, color, sw); break;
                case AppGlyph.Chart: Chart(g, x, y, w, h, color, sw); break;
                default: Window(g, x, y, w, h, color, sw); break;
            }

            g.SmoothingMode = old;
        }

        // 程序图标缓存:同一个 exe 只解一次,列表滚动时不重复读文件。
        private static readonly Dictionary<string, Image> pics = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        /// <summary>读 exe 自带的图标(就是资源管理器里看到的那个);读不到返回 null。</summary>
        public static Image Load(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            Image img;
            if (pics.TryGetValue(exePath, out img)) return img;
            img = null;
            try
            {
                if (File.Exists(exePath))
                {
                    using (Icon ic = Icon.ExtractAssociatedIcon(exePath))
                        if (ic != null) img = ic.ToBitmap();
                }
            }
            catch { img = null; }
            pics[exePath] = img;
            return img;
        }

        /// <summary>
        /// 优先画 exe 自己的图标(圆角裁切,和系统任务栏观感一致),
        /// 取不到图标时退回自绘矢量图标,保证列表不会出现空位。
        /// </summary>
        public static void DrawApp(Graphics g, string exePath, Rectangle box, Color fallback, AppGlyph kind)
        {
            Image img = Load(exePath);
            if (img == null)
            {
                Draw(g, kind, box, fallback);
                return;
            }
            if (box.Width <= 0 || box.Height <= 0) return;

            GraphicsState st = g.Save();
            try
            {
                using (GraphicsPath p = Ui.Round(new RectangleF(box.X, box.Y, box.Width, box.Height), box.Width * 0.26f))
                    g.SetClip(p, CombineMode.Intersect);
                InterpolationMode old = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, box);
                g.InterpolationMode = old;
            }
            finally { g.Restore(st); }
        }

        private static void Shield(Graphics g, float x, float y, float w, float h, Color c, float sw)
        {
            using (var p = new GraphicsPath())
            {
                p.AddLine(x + w * 0.5f, y + h * 0.08f, x + w * 0.9f, y + h * 0.2f);
                p.AddLine(x + w * 0.9f, y + h * 0.2f, x + w * 0.9f, y + h * 0.5f);
                p.AddBezier(x + w * 0.9f, y + h * 0.5f, x + w * 0.9f, y + h * 0.74f,
                    x + w * 0.72f, y + h * 0.9f, x + w * 0.5f, y + h * 0.94f);
                p.AddBezier(x + w * 0.5f, y + h * 0.94f, x + w * 0.28f, y + h * 0.9f,
                    x + w * 0.1f, y + h * 0.74f, x + w * 0.1f, y + h * 0.5f);
                p.AddLine(x + w * 0.1f, y + h * 0.5f, x + w * 0.1f, y + h * 0.2f);
                p.CloseFigure();

                using (var b = new SolidBrush(Theme.Alpha(c, 26))) g.FillPath(b, p);
                using (var pen = new Pen(c, sw))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.DrawPath(pen, p);
                }
            }

            using (var pen = new Pen(c, sw))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new PointF[] {
                    new PointF(x + w * 0.33f, y + h * 0.5f),
                    new PointF(x + w * 0.45f, y + h * 0.62f),
                    new PointF(x + w * 0.68f, y + h * 0.38f) });
            }
        }

        private static void Pad(Graphics g, float x, float y, float w, float h, Color c, float sw)
        {
            var body = new RectangleF(x + w * 0.04f, y + h * 0.29f, w * 0.92f, h * 0.44f);
            float rad = body.Height * 0.5f;
            Ui.Fill(g, body, rad, Theme.Alpha(c, 24));
            Ui.Stroke(g, body, rad, c, sw);

            float cx = body.X + body.Width * 0.28f, cy = body.Y + body.Height * 0.5f;
            float arm = body.Height * 0.19f;
            using (var pen = new Pen(c, sw))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
                g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
            }

            float r = Math.Max(1f, body.Height * 0.09f);
            using (var b = new SolidBrush(c))
            {
                float ry = cy - body.Height * 0.04f;
                g.FillEllipse(b, body.X + body.Width * 0.66f - r, ry - r, r * 2, r * 2);
                g.FillEllipse(b, body.X + body.Width * 0.79f - r, ry - r, r * 2, r * 2);
            }
        }

        private static void Chart(Graphics g, float x, float y, float w, float h, Color c, float sw)
        {
            float bw = w * 0.2f;
            float gap = w * 0.12f;
            float baseY = y + h * 0.86f;
            float x0 = x + (w - (bw * 3 + gap * 2)) / 2f;
            float[] hs = new float[] { h * 0.32f, h * 0.54f, h * 0.74f };

            for (int i = 0; i < 3; i++)
            {
                var r = new RectangleF(x0 + i * (bw + gap), baseY - hs[i], bw, hs[i]);
                Ui.Fill(g, r, bw * 0.34f, Theme.Alpha(c, i == 2 ? 58 : 32));
                Ui.Stroke(g, r, bw * 0.34f, c, sw * 0.85f);
            }
        }

        private static void Window(Graphics g, float x, float y, float w, float h, Color c, float sw)
        {
            var r = new RectangleF(x + w * 0.1f, y + h * 0.16f, w * 0.8f, h * 0.68f);
            float rad = w * 0.13f;
            Ui.Fill(g, r, rad, Theme.Alpha(c, 22));
            Ui.Stroke(g, r, rad, c, sw);

            float barY = r.Y + r.Height * 0.28f;
            using (var pen = new Pen(c, sw * 0.8f))
                g.DrawLine(pen, r.X + 1f, barY, r.Right - 1f, barY);
        }
    }
}
