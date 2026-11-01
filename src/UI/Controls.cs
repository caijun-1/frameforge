using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
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
        public float Radius = 12;
        public bool ShowBorder = true;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            // 卡片用纯色:子控件是按自身 BackColor 铺底的,卡片一旦用渐变,
            // 文字标签就会在渐变上露出一个个色块。
            Ui.Fill(g, r, Ui.Fp(Radius), Theme.Card);
            if (Theme.Dark)
            {
                // 夜间模式卡片顶部加一道极淡的高光边,让纯色块有"玻璃边缘"的层次感。
                using (GraphicsPath p = Ui.Round(r, Ui.Fp(Radius)))
                using (var pen = new Pen(Theme.Alpha(Color.White, 12), 1f))
                {
                    g.SetClip(p);
                    g.DrawLine(pen, 0f, 0.5f, Width, 0.5f);
                    g.ResetClip();
                }
            }
            if (ShowBorder) Ui.Stroke(g, r, Ui.Fp(Radius), Theme.Border, 1f);
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
        public float Radius = -1;
        public float FontPt = 10.5f;
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
                fill = !Enabled ? Theme.Mix(Theme.Accent, BackColor, 0.55) : (down ? Theme.AccentHover : (hover ? Theme.AccentHover : Theme.Accent));
                fg = Color.White;
            }
            else
            {
                fill = down ? Theme.Track : (hover ? Theme.Hover : Theme.Card);
                fg = Enabled ? Theme.Text : Theme.Sub;
            }
            Ui.Fill(g, r, rad, fill);
            if (!Primary) Ui.Stroke(g, r, rad, Theme.Border, 1f);

            Rectangle textRect = ClientRectangle;
            if (busy) textRect = new Rectangle(Ui.Px(14), 0, Width - Ui.Px(14), Height);
            TextRenderer.DrawText(g, Text ?? "", Theme.F(FontPt, FontStyle.Bold), textRect, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            if (busy)
            {
                float d = Height * 0.42f;
                var sr = new RectangleF(Width - d - Ui.Fp(14), (Height - d) / 2f, d, d);
                using (var pen = new Pen(Color.FromArgb(160, fg), Math.Max(1.5f, d * 0.14f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, sr, spin, 260);
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
                        // 月亮:实心圆挖掉一块
                        using (var b = new SolidBrush(fg))
                            g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                        using (var b2 = new SolidBrush(BackColor))
                            g.FillEllipse(b2, cx - r * 0.35f, cy - r * 1.25f, r * 2.0f, r * 2.0f);
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

    /// <summary>开关。</summary>
    public class Toggle : Ctl
    {
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
            float h = Math.Min(Height, Ui.Fp(24));
            float w = Ui.Fp(44);
            var track = new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h);
            Color on = Theme.Accent, off = Theme.Dark ? Theme.Track : Theme.Mix(Theme.Track, Theme.Sub, 0.25);
            Ui.Fill(g, track, h / 2f, Theme.Mix(off, on, anim));
            float knob = h - Ui.Fp(4);
            float x = track.X + Ui.Fp(2) + (track.Width - knob - Ui.Fp(4)) * anim;
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, x, track.Y + Ui.Fp(2), knob, knob);
        }
    }

    /// <summary>分段选择器(胶囊)。</summary>
    public class Segmented : Ctl
    {
        public string[] Items = new string[0];
        public event EventHandler Changed;
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
            Ui.Fill(g, pill, pill.Height / 2f, Theme.Accent);

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

    /// <summary>圆环仪表:占用率 + 中心百分比 + 下方已用/总量。</summary>
    public class RingGauge : Ctl
    {
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
            float rad = side * 0.40f;
            float thick = Math.Max(2f, side * (14f / 220f));
            float cx = Width / 2f, cy = Height / 2f;
            var arc = new RectangleF(cx - rad + thick / 2f, cy - rad + thick / 2f, rad * 2f - thick, rad * 2f - thick);

            using (var pen = new Pen(Theme.Track, thick))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, arc, -90, 359.98f);
            }

            Color col = Theme.Accent;
            if (disp >= 0.80) col = Theme.Danger;
            else if (disp >= 0.60) col = Theme.Warn;

            float sweep = (float)(disp * 360.0);
            if (sweep > 0.5f)
            {
                if (sweep >= 359.5f) sweep = 359.5f;
                bool gradient = col == Theme.Accent;
                Pen pen = null;
                LinearGradientBrush br = null;
                try
                {
                    if (gradient)
                    {
                        br = new LinearGradientBrush(arc, Theme.AccentHover, Theme.Mix(Theme.Accent, Color.White, 0.30), LinearGradientMode.ForwardDiagonal);
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
            }

            // 中心大号百分比(约 48 逻辑像素)
            string pct = ((int)Math.Round(disp * 100)) + "%";
            float pt = side / Math.Max(1f, Ui.S) * 0.165f;
            var txtRect = new RectangleF(0, cy - rad * 0.62f, Width, rad * 0.64f);
            TextRenderer.DrawText(g, pct, Theme.Num(pt), Rectangle.Round(txtRect), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            // 圆环内下方:已用 / 总量
            var subRect = new RectangleF(0, cy + rad * 0.06f, Width, rad * 0.40f);
            TextRenderer.DrawText(g, sub, Theme.F(9f, FontStyle.Regular), Rectangle.Round(subRect), Theme.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>迷你折线图,渐变填充,无坐标轴。</summary>
    public class Sparkline : Ctl
    {
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
            if (vals.Count < 2) return;

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
                    Theme.Alpha(Theme.Accent, 70), Theme.Alpha(Theme.Accent, 4), LinearGradientMode.Vertical))
                    g.FillPath(b, path);
            }
            using (var pen = new Pen(Theme.Accent, Math.Max(1.6f, 2f * Ui.S)))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLines(pen, pts);
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
}
