using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
        }
    }

    /// <summary>圆角卡片。</summary>
    public class Card : Ctl
    {
        public float Radius = 15;
        public bool ShowBorder = true;

        /// <summary>按父容器坐标重建页面背景渐变,圆角外和页面无缝衔接;无卡片底的控件也能复用。</summary>
        public static void PaintBackdrop(Graphics g, Control c)
        {
            if (c == null || c.Width <= 0 || c.Height <= 0) return;
            RectangleF area = c.Parent != null
                ? new RectangleF(-c.Left, -c.Top, c.Parent.ClientRectangle.Width, c.Parent.ClientRectangle.Height)
                : new RectangleF(0, 0, c.Width, c.Height);
            bool onPage = c.Parent is Page;
            Color bgFrom = onPage ? Theme.BgTop : (c.Parent != null ? c.Parent.BackColor : Theme.Bg);
            Color bgTo = onPage ? Theme.BgBottom : bgFrom;
            if (area.Width <= 0 || area.Height <= 0) return;
            using (var b = new LinearGradientBrush(area, bgFrom, bgTo, LinearGradientMode.Vertical))
                g.FillRectangle(b, c.ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 圆角外侧不能铺卡片色:控件本身是矩形,而 BackColor 又被子控件继承成卡片色,
            // 整块铺底就会在四个圆角外面留下四个直角(浅色主题下就是四个白角)。
            // 页面背景是纵向渐变,只铺平色会在圆角外留下一块比周围更暗的灰角,
            // 这里按父容器坐标重建同一条渐变,圆角外和页面无缝衔接。
            PaintBackdrop(g, this);

            // 卡片保持纯白:子控件是自带底色的矩形,卡片一旦上渐变,
            // 文字标签就会在渐变上露出一块块白斑。层次感交给柔和底色和细描边。
            float rad = Ui.Fp(Radius);
            Ui.Fill(g, new RectangleF(0, 0, Width, Height), rad, Theme.Card);
            if (ShowBorder)
                Ui.Stroke(g, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), rad, Theme.Border, 1f);
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
            float rad = Radius < 0 ? Height / 2f : Ui.Fp(Radius);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            Color fill, fg;
            if (Primary)
            {
                Color acc = AccentColor.A == 0 ? Theme.Accent : AccentColor;
                Color hoverCol = Theme.Mix(acc, Theme.Dark ? Color.Black : Color.FromArgb(255, 0, 0, 0), Theme.Dark ? 0.10 : 0.12);
                fill = !Enabled ? Theme.Mix(acc, BackColor, 0.55) : (hover || down ? hoverCol : acc);
                fg = Color.White;
            }
            else
            {
                fill = down ? Theme.Track : (hover ? Theme.Hover : Theme.Card);
                fg = Enabled ? Theme.Text : Theme.Sub;
                if (Ghost)
                {
                    fill = hover || down ? Theme.Hover : Theme.Card;
                    fg = Enabled ? Theme.Accent : Theme.Sub;
                }
            }
            if (Primary)
            {
                // 主按钮做上亮下暗的薄渐变,像一颗有弧面的实体键。
                Ui.FillVGrad(g, r, rad, Theme.Mix(fill, Color.White, Theme.Dark ? 0.10 : 0.20),
                    Theme.Mix(fill, Color.Black, Theme.Dark ? 0.18 : 0.08));
            }
            else
            {
                Ui.Fill(g, r, rad, fill);
                Ui.Stroke(g, r, rad, Ghost ? Theme.Mix(Theme.Border, Theme.Accent, 0.45) : Theme.Border, 1f);
            }

            // 转圈固定画在按钮右侧留白里。
            float d = Height * 0.42f;
            RectangleF sr = new RectangleF(Width - d - Ui.Fp(14), (Height - d) / 2f, d, d);

            // 文字始终在整颗按钮里居中:忙碌时也不缩小文字区域,避免点下去文字突然前移。
            Font font = Theme.F(FontPt, FontStyle.Bold);
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
            Prep(g);
            float d = Math.Min(Width, Height) - Ui.Fp(4);
            var c = new RectangleF((Width - d) / 2f, (Height - d) / 2f, d, d);
            Color hoverFill = Kind == Glyph.Close ? Color.FromArgb(down ? 255 : 230, Theme.Danger) : Color.FromArgb(Theme.Dark ? 26 : 14, Theme.Text);
            if (hover) Ui.Fill(g, c, d / 2f, hoverFill);

            Color fg = Kind == Glyph.Close && hover ? Color.White : Theme.Sub;
            if (Kind == Glyph.Lang)
            {
                float pt = d / Ui.S * 0.30f;
                TextRenderer.DrawText(g, LangText, Theme.F(pt, FontStyle.Bold), Rectangle.Round(c), fg,
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
                    if (Theme.Dark)
                    {
                        // 月亮:拿两个圆的交点拼出一条双圆弧路径再填充。
                        // 早先是"画个实心圆,再用 BackColor 盖掉一块"——底色一变(悬停高亮、换页)
                        // 盖子就会在图标上留下一块补丁,而且盖出来的边没有抗锯齿。
                        float bx = cx + r * 0.65f, by = cy - r * 0.25f;
                        double dx = bx - cx, dy = by - cy;
                        double d2 = Math.Sqrt(dx * dx + dy * dy);
                        if (d2 < r * 0.1) d2 = r * 0.1;
                        double proj = d2 / 2.0;                       // 两圆等径,交点连线正好在中间
                        double half = Math.Sqrt(Math.Max(0.0, (double)r * r - proj * proj));
                        double baseAng = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                        double off = Math.Atan2(half, proj) * 180.0 / Math.PI;

                        var ob = new RectangleF(cx - r, cy - r, r * 2, r * 2);
                        var ib = new RectangleF(bx - r, by - r, r * 2, r * 2);
                        using (var mp = new GraphicsPath())
                        {
                            mp.AddArc(ob, (float)(baseAng + off), (float)(360.0 - off * 2.0));
                            mp.AddArc(ib, (float)(baseAng + 180.0 + off), (float)(-off * 2.0));
                            mp.CloseFigure();
                            using (var b = new SolidBrush(fg)) g.FillPath(b, mp);
                        }
                    }
                    else
                    {
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

            Color on = Theme.Accent;
            Color off = Theme.Dark
                ? Theme.Mix(Theme.Track, Theme.Text, 0.12f)
                : Theme.Mix(Theme.Track, Theme.Sub, 0.32f);
            Color fill = Theme.Mix(off, on, anim);
            Color edge = Theme.Mix(fill, Color.Black, Theme.Dark ? 0.22f : 0.08f);

            // 描边画成"外圈填边色 + 内圈填底色"的环,而不是拿 1px 画笔去描路径:
            // 画笔是骑在路径上的,一半会溢出到轨道外面,再叠上父容器裁剪,
            // 下沿那一半就没了。环永远完整落在轨道矩形内,四边一样粗。
            Ui.Fill(g, track, rad, edge);
            var inner = new RectangleF(track.X + 1f, track.Y + 1f, track.Width - 2f, track.Height - 2f);
            Ui.Fill(g, inner, Math.Max(0f, rad - 1f), fill);

            // 旋钮:四周留出内边距,滑到两端也不会贴死圆弧。
            float inset = Math.Max(2f, (float)Math.Round(track.Height * 0.145f));
            float knob = track.Height - inset * 2f;
            if (knob <= 3f) return;
            float from = track.X + inset;
            float to = track.Right - inset - knob;
            float kx = (float)Math.Round(from + (to - from) * anim);
            float ky = (float)Math.Round(track.Y + inset);
            using (var sh = new SolidBrush(Color.FromArgb(Theme.Dark ? 90 : 30, 15, 23, 42)))
                g.FillEllipse(sh, kx, ky + Ui.Fp(0.8f), knob, knob);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, kx, ky, knob, knob);
        }
    }

    /// <summary>分段选择器(胶囊)。</summary>
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
            int i = (int)(x / itemW);
            if (i < 0) i = 0;
            if (i >= Items.Length) i = Items.Length - 1;
            return i;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            var all = new RectangleF(0, 0, Width - 1, Height - 1);
            float rad = Height / 2f;
            Ui.Fill(g, all, rad, Theme.Track);
            if (Items.Length == 0) return;

            float itemW = (float)Width / Items.Length;
            float pillW = itemW - Ui.Fp(4);
            var pill = new RectangleF(Ui.Fp(2) + anim * itemW, Ui.Fp(2), pillW, Height - Ui.Fp(4));
            Color acc = AccentColor.A == 0 ? Theme.Accent : AccentColor;
            // 选中胶囊带一层同色投影 + 顶部提亮,像一颗凸起的按键。
            Ui.Fill(g, new RectangleF(pill.X, pill.Y + Ui.Fp(1.5f), pill.Width, pill.Height),
                pill.Height / 2f, Theme.Alpha(acc, Theme.Dark ? 70 : 45));
            Ui.FillVGrad(g, pill, pill.Height / 2f, Theme.Mix(acc, Color.White, 0.18),
                Theme.Mix(acc, Color.Black, 0.10));

            for (int i = 0; i < Items.Length; i++)
            {
                var r = new RectangleF(i * itemW, 0, itemW, Height);
                bool selected = i == sel;
                Color fg = selected ? Color.White : (i == hoverIndex ? Theme.Text : Theme.Sub);
                TextRenderer.DrawText(g, Items[i], Theme.F(9.5f, selected ? FontStyle.Bold : FontStyle.Regular),
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

            float itemW = (float)Width / Items.Length;
            for (int i = 0; i < Items.Length; i++)
            {
                var r = new RectangleF(i * itemW, 0, itemW, Height);
                // 最后一块右侧要给“编辑选项”小按钮腾位,文字和下划线都在让开后的区域里居中。
                if (i == Items.Length - 1 && LastItemInset > 0)
                    r.Width = Math.Max(Ui.Fp(24), r.Width - LastItemInset);
                bool on = i == sel;
                Color fg = on ? Theme.Text : (i == hoverIndex ? Theme.Mix(Theme.Sub, Theme.Text, 0.55) : Theme.Sub);
                TextRenderer.DrawText(g, Items[i], Theme.F(10.5f, on ? FontStyle.Bold : FontStyle.Regular),
                    Rectangle.Round(r), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

                if (on)
                {
                    Color acc = AccentColor.A == 0 ? Theme.Accent : AccentColor;
                    float uw = Math.Min(Ui.Fp(30), r.Width * 0.34f);
                    var bar = new RectangleF(r.X + (r.Width - uw) / 2f, Height - Ui.Fp(6), uw, Math.Max(2f, Ui.Fp(2.5)));
                    // 光晕 + 实线:选中项像仪表盘上被点亮的刻度。
                    Ui.Fill(g, new RectangleF(bar.X - Ui.Fp(4), bar.Y - Ui.Fp(2), bar.Width + Ui.Fp(8), bar.Height + Ui.Fp(4)),
                        bar.Height, Theme.Alpha(acc, Theme.Dark ? 60 : 40));
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

            // 全部度量按控件自身尺寸比例计算:半径 = min(w,h)*0.40,线宽 = 14/220
            float side = Math.Min(Width, Height);
            // 带刻度盘时把圆环收小一点,把外圈让给刻度。
            float rad = side * (Ticks ? 0.355f : 0.40f);
            float thick = Math.Max(2f, side * (14f / 220f));
            float cx = Width / 2f, cy = Height / 2f;
            if (Ticks) DrawTicks(g, cx, cy, rad + thick * 1.45f, side);

            // 内圈做一块微微凸起的"表盘玻璃":上亮下暗,边缘一圈几乎看不见的细线。
            float innerR = rad - thick * 1.05f;
            if (innerR > 4f)
            {
                var inner = new RectangleF(cx - innerR, cy - innerR, innerR * 2f, innerR * 2f);
                Color top = Theme.Dark ? Theme.CardAlt : Color.White;
                Color bottom = Theme.Dark ? Theme.Mix(Theme.Card, Color.Black, 0.18)
                                          : Theme.Mix(Theme.Card, Theme.Track, 0.35);
                Ui.FillVGrad(g, inner, innerR, top, bottom);
                using (var pen = new Pen(Theme.Alpha(Theme.Border, Theme.Dark ? 110 : 90), 1f))
                    g.DrawEllipse(pen, inner);
                // 内圈底部一道极淡的内阴影:玻璃盘有厚度,不会像贴上去的白纸。
                using (var pen = new Pen(Theme.Alpha(Theme.Text, Theme.Dark ? 30 : 16), Math.Max(2f, innerR * 0.045f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    var sd = new RectangleF(cx - innerR * 0.86f, cy - innerR * 0.86f, innerR * 1.72f, innerR * 1.72f);
                    g.DrawArc(pen, sd, 42f, 96f);
                }
            }

            var arc = new RectangleF(cx - rad + thick / 2f, cy - rad + thick / 2f, rad * 2f - thick, rad * 2f - thick);

            using (var pen = new Pen(Theme.Mix(Theme.Track, Theme.Sub, Theme.Dark ? 0.10 : 0.20), thick))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, arc, -90, 359.98f);
            }
            // 80%~100% 压一层淡色"警戒区":颜色跟随当前模式,均衡/自定义是浅蓝、野兽是浅红。
            // 读数真的越过 80% 时,进度弧与数字会自己转成红色,警示靠那一层传达。
            Color baseCol = AccentColor.A == 0 ? Theme.Accent : AccentColor;
            using (var pen = new Pen(Theme.Alpha(baseCol, Theme.Dark ? 54 : 34), thick))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, arc, -90 + 288f, 71.5f);
            }
            // 轨道外侧一条细轮廓线:圆环有"金属边"的收口,不会糊成一圈灰。
            using (var pen = new Pen(Theme.Alpha(Theme.Border, Theme.Dark ? 110 : 130), 1f))
                g.DrawEllipse(pen, new RectangleF(cx - rad - thick / 2f + 0.5f, cy - rad - thick / 2f + 0.5f,
                    rad * 2f + thick - 1f, rad * 2f + thick - 1f));

            Color col = baseCol;
            if (disp >= 0.80) col = Theme.Danger;
            else if (disp >= 0.60) col = Theme.Warn;

            float sweep = (float)(disp * 360.0);
            if (sweep > 0.5f)
            {
                if (sweep >= 359.5f) sweep = 359.5f;

                // 光晕:由内到外叠四层同心弧,宽度递增、透明度递减,
                // 读数色会像灯光一样洇到卡片上,而不是贴上去的一条硬色带。
                float[] gw = new float[] { 1.28f, 1.58f, 1.94f, 2.34f };
                int[] ga = Theme.Dark ? new int[] { 58, 34, 20, 11 } : new int[] { 42, 26, 15, 8 };
                for (int i = 0; i < gw.Length; i++)
                {
                    using (var glow = new Pen(Theme.Alpha(col, ga[i]), thick * gw[i]))
                    {
                        glow.StartCap = LineCap.Round;
                        glow.EndCap = LineCap.Round;
                        g.DrawArc(glow, arc, -90, sweep);
                    }
                }

                bool gradient = col == baseCol;
                Pen pen = null;
                LinearGradientBrush br = null;
                try
                {
                    if (gradient)
                    {
                        br = new LinearGradientBrush(arc, Theme.Mix(baseCol, Color.Black, 0.18), Theme.Mix(baseCol, Color.White, 0.30), LinearGradientMode.ForwardDiagonal);
                        pen = new Pen(br, thick);
                    }
                    else pen = new Pen(col, thick);
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, arc, -90, sweep);
                }
                finally
                {
                    if (pen != null) pen.Dispose();
                    if (br != null) br.Dispose();
                }

                // 弧尖一颗小亮点:读数停在哪里一眼可见,像表针的针尖。
                if (sweep > 8f)
                {
                    // 弧线是沿着 rad-thick/2 的中线画的,亮点必须落在同一条中线上,
                    // 否则会飘到轨道外侧去。
                    double ea = (-90.0 + sweep) * Math.PI / 180.0;
                    float mid = rad - thick / 2f;
                    float ex = cx + (float)Math.Cos(ea) * mid;
                    float ey = cy + (float)Math.Sin(ea) * mid;
                    float tr = Math.Max(1.6f, thick * 0.30f);
                    using (var b = new SolidBrush(Theme.Alpha(col, Theme.Dark ? 90 : 60)))
                        g.FillEllipse(b, ex - tr * 2f, ey - tr * 2f, tr * 4f, tr * 4f);
                    using (var b = new SolidBrush(Color.White))
                        g.FillEllipse(b, ex - tr, ey - tr, tr * 2f, tr * 2f);
                }
            }

            // 环内上方小标题:仪表盘上比百分比更容易一眼看到是哪个部件。
            if (Caption.Length > 0)
            {
                var capRect = new RectangleF(0, cy - rad * 0.72f, Width, rad * 0.30f);
                TextRenderer.DrawText(g, Caption, Theme.F(10.5f, FontStyle.Bold), Rectangle.Round(capRect), Theme.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }

            // 中心大号百分比(约 48 逻辑像素)
            string pct = ((int)Math.Round(disp * 100)) + "%";
            float pt = side / Math.Max(1f, Ui.S) * (Ticks ? 0.145f : 0.165f);
            var txtRect = new RectangleF(0, cy - rad * (Ticks ? 0.34f : 0.62f), Width, rad * (Ticks ? 0.62f : 0.64f));
            TextRenderer.DrawText(g, pct, Theme.Num(pt), Rectangle.Round(txtRect), col,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            // 圆环内下方:已用 / 总量
            var subRect = new RectangleF(0, cy + rad * (Ticks ? 0.26f : 0.06f), Width, rad * 0.40f);
            TextRenderer.DrawText(g, sub, Theme.F(9f, FontStyle.Regular), Rectangle.Round(subRect), Theme.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        /// <summary>圆环外侧刻度:小刻度弱、每 6 格一格主刻度。</summary>
        private void DrawTicks(Graphics g, float cx, float cy, float r, float side)
        {
            int count = 72;
            float minor = Math.Max(3f, side * 0.016f);
            float major = Math.Max(5f, side * 0.030f);
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
                using (var b = new LinearGradientBrush(new RectangleF(0, 0, Width, Height),
                    Theme.Alpha(AccentColor.A == 0 ? Theme.Accent : AccentColor, 70), Theme.Alpha(AccentColor.A == 0 ? Theme.Accent : AccentColor, 4), LinearGradientMode.Vertical))
                    g.FillPath(b, path);
            }
            using (var pen = new Pen(AccentColor.A == 0 ? Theme.Accent : AccentColor, Math.Max(1.6f, 2f * Ui.S)))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLines(pen, pts);
            }

            // 末端亮点:最新一次采样,像示波器上的游标。
            PointF last = pts[pts.Length - 1];
            Color ac = AccentColor.A == 0 ? Theme.Accent : AccentColor;
            float dr = Math.Max(2.2f, Ui.Fp(2.6f));
            using (var b = new SolidBrush(Theme.Alpha(ac, Theme.Dark ? 90 : 60)))
                g.FillEllipse(b, last.X - dr * 2.1f, last.Y - dr * 2.1f, dr * 4.2f, dr * 4.2f);
            using (var b = new SolidBrush(ac))
            using (var rim = new Pen(Theme.Card, Math.Max(1f, Ui.Fp(1.2f))))
            {
                g.FillEllipse(b, last.X - dr, last.Y - dr, dr * 2f, dr * 2f);
                g.DrawEllipse(rim, last.X - dr, last.Y - dr, dr * 2f, dr * 2f);
            }
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

            // 提示条浮在底部图表卡上,先用卡片底色铺满整个控件矩形,
            // 再画抗锯齿胶囊——这样圆角边缘会和背景平滑融合,没有 Region 裁剪产生的锯齿毛边。
            using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, ClientRectangle);

            int a = alpha;
            Color bg = Theme.Dark ? Color.FromArgb(a, 38, 44, 54) : Color.FromArgb(a, 26, 29, 33);
            var r = new RectangleF(0, 0, Width, Height);
            Ui.Fill(g, r, Height / 2f, bg);
            TextRenderer.DrawText(g, Text ?? "", Theme.F(9.5f, FontStyle.Regular), ClientRectangle,
                Color.FromArgb(a, 255, 255, 255),
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

        private static bool Show(IWin32Window owner, string title, string body, string ok, string cancel)
        {
            using (var f = new DialogForm(title, body, ok, cancel))
                return f.ShowDialog(owner) == DialogResult.OK;
        }

        private class DialogForm : Form
        {
            private readonly string dTitle, dBody, dOk, dCancel;
            private FlatButton btnOk, btnCancel;

            public DialogForm(string title, string body, string ok, string cancel)
            {
                dTitle = title; dBody = body; dOk = ok; dCancel = cancel;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                BackColor = Theme.Card;
                DoubleBuffered = true;
                float s = Ui.S;
                ClientSize = new Size((int)(400 * s), (int)(190 * s));
                KeyPreview = true;

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

                MouseDown += OnDrag;
                KeyDown += delegate (object o, KeyEventArgs k)
                {
                    if (k.KeyCode == Keys.Escape && btnCancel != null) { DialogResult = DialogResult.Cancel; Close(); }
                    if (k.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
                };
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
                    using (GraphicsPath p = Ui.Round(new RectangleF(0, 0, Width, Height), Ui.Fp(12)))
                        Region = new Region(p);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                using (var p = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.Fp(12)))
                using (var pen = new Pen(Theme.Border, 1f))
                    g.DrawPath(pen, p);

                float s = Ui.S;
                var titleRect = new Rectangle((int)(22 * s), (int)(20 * s), Width - (int)(44 * s), (int)(26 * s));
                TextRenderer.DrawText(g, dTitle, Theme.F(11.5f, FontStyle.Bold), titleRect, Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

                var bodyRect = new Rectangle((int)(22 * s), (int)(50 * s), Width - (int)(44 * s), (int)(78 * s));
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
