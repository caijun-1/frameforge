using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge.UI
{
    /// <summary>带标题与数值的横向滑杆。</summary>
    public class Slider : Ctl
    {
        public int Min = 0;
        public int Max = 100;
        public string Label = "";
        public string Unit = "%";
        public event EventHandler Changed;

        private int val;
        private bool drag;
        private bool hover;

        public int Value
        {
            get { return val; }
            set
            {
                int v = value;
                if (v < Min) v = Min;
                if (v > Max) v = Max;
                if (val == v) return;
                val = v;
                Invalidate();
            }
        }

        public void SetSilent(int v)
        {
            if (v < Min) v = Min;
            if (v > Max) v = Max;
            val = v;
            Invalidate();
        }

        public Slider() { Cursor = Cursors.Hand; }

        /// <summary>标签左侧显示一个迷你开关,用于带独立开关的滑块。</summary>
        public bool ShowSwitch;
        /// <summary>开关状态。关闭时滑杆不生效(轨道置灰)。</summary>
        public bool Switched = true;
        public event EventHandler SwitchChanged;

        private RectangleF SwitchBox()
        {
            float h = Ui.Fp(14);
            return new RectangleF(0, (Ui.Fp(18) - h) / 2f, Ui.Fp(26), h);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            hover = false;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (ShowSwitch)
                {
                    RectangleF sb = SwitchBox();
                    if (e.X <= sb.Right + Ui.Fp(4) && e.Y <= sb.Bottom + Ui.Fp(3))
                    {
                        Switched = !Switched;
                        Invalidate();
                        if (SwitchChanged != null) SwitchChanged(this, EventArgs.Empty);
                        base.OnMouseDown(e);
                        return;
                    }
                }
                drag = true;
                Apply(e.X);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag) Apply(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            drag = false;
            base.OnMouseUp(e);
        }

        private void Apply(int x)
        {
            // 直接拖滑杆视为要使用这一项:自动打开开关,免得拖了没反应。
            if (ShowSwitch && !Switched)
            {
                Switched = true;
                if (SwitchChanged != null) SwitchChanged(this, EventArgs.Empty);
            }
            float left = Ui.Fp(2);
            float right = Width - Ui.Fp(2);
            float t = (x - left) / Math.Max(1f, right - left);
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            int nv = Min + (int)Math.Round(t * (Max - Min));
            if (nv == val) return;
            val = nv;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);

            float s = Ui.S;
            float labelH = Ui.Fp(18);
            bool live = Enabled && (!ShowSwitch || Switched);

            float labelLeft = 0;
            if (ShowSwitch)
            {
                RectangleF sb = SwitchBox();
                Ui.Fill(g, sb, sb.Height / 2f, Switched ? Theme.Accent : Theme.Track);
                float d = sb.Height - Ui.Fp(4);
                float bx = Switched ? sb.Right - d - Ui.Fp(2) : sb.Left + Ui.Fp(2);
                using (var b = new SolidBrush(Switched ? Theme.Card : Theme.Faint))
                    g.FillEllipse(b, bx, sb.Y + Ui.Fp(2), d, d);
                labelLeft = sb.Right + Ui.Fp(8);
            }

            // 双极性滑杆(-100~100)正值补一个加号,数值方向一眼可辨。
            string shown = val > 0 && Min < 0 ? "+" + val : val.ToString();
            TextRenderer.DrawText(g, Label, Theme.F(9f, FontStyle.Regular),
                Rectangle.Round(new RectangleF(labelLeft, 0, Width - labelLeft, labelH)), Enabled ? Theme.Sub : Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, shown + Unit, Theme.F(9.5f, FontStyle.Bold),
                new Rectangle(0, 0, Width, (int)labelH), Enabled ? Theme.Accent : Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            float cy = labelH + (Height - labelH) / 2f;
            float left = Ui.Fp(2), right = Width - Ui.Fp(2);
            var track = new RectangleF(left, cy - Ui.Fp(3), right - left, Ui.Fp(6));
            Ui.Fill(g, track, track.Height / 2f, Theme.Track);

            double t = (val - Min) / (double)Math.Max(1, Max - Min);
            // 双极性滑杆从中间零点向两侧填充:0 值不该显示成"半条蓝线"。
            bool bipolar = Min < 0 && Max > 0;
            float from = bipolar ? left + (right - left) * (float)((0 - Min) / (double)(Max - Min)) : left;
            float to = left + (right - left) * (float)t;
            var filled = new RectangleF(Math.Min(from, to), cy - Ui.Fp(3), Math.Abs(to - from), Ui.Fp(6));
            if (filled.Width > 0.5f)
                Ui.Fill(g, filled, filled.Height / 2f, live ? Theme.Accent : Theme.Track);

            float knob = Ui.Fp(14);
            float stroke = Math.Max(2f, Ui.Fp(2.6));
            // 夹紧时要把描边和投影的余量留出来:贴到 0 / Width 时圆环外半圈会被控件边界裁平。
            float inset = stroke / 2f + 1f;
            float kx = left + (float)((right - left) * t) - knob / 2f;
            if (kx < inset) kx = inset;
            if (kx > Width - knob - inset) kx = Width - knob - inset;
            var kr = new RectangleF(kx, cy - knob / 2f, knob, knob);
            using (var sh = new SolidBrush(Color.FromArgb(Theme.Dark ? 90 : 26, 15, 23, 42)))
                g.FillEllipse(sh, kr.X, kr.Y + Ui.Fp(1.2f), kr.Width, kr.Height);
            using (var b = new SolidBrush(Theme.Card)) g.FillEllipse(b, kr);
            using (var pen = new Pen(!live ? Theme.Faint : hover || drag ? Theme.AccentHover : Theme.Accent, stroke))
                g.DrawEllipse(pen, kr);
        }
    }

    /// <summary>可选中的圆角胶囊,用于滤镜预设与显示项开关。</summary>
    public class Chip : Ctl
    {
        public bool Selected;
        public float FontPt = 9.5f;
        private bool hover;

        public Chip() { Cursor = Cursors.Hand; }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            float rad = Ui.Fp(9);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            Color fill;
            Color fg;
            if (Selected)
            {
                fill = hover ? Theme.AccentHover : Theme.Accent;
                fg = Color.White;
            }
            else
            {
                fill = hover ? Theme.Hover : Theme.Card;
                fg = Theme.Sub;
            }
            if (Selected)
            {
                Ui.FillVGrad(g, r, rad, Theme.Mix(fill, Color.White, 0.16), Theme.Mix(fill, Color.Black, 0.08));
            }
            else
            {
                Ui.Fill(g, r, rad, fill);
                Ui.Stroke(g, r, rad, Theme.Border, 1f);
            }
            TextRenderer.DrawText(g, Text ?? "", Theme.F(FontPt, Selected ? FontStyle.Bold : FontStyle.Regular),
                ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>细进度条,用于内存占用。</summary>
    public class Meter : Ctl
    {
        /// <summary>正常区间颜色,为空时用主题色。</summary>
        public Color AccentColor = Color.Empty;
        private double target, disp;
        private Timer t;

        public void SetValue(double v)
        {
            if (v < 0) v = 0;
            if (v > 1) v = 1;
            if (Math.Abs(v - target) < 0.0005) return;
            target = v;
            if (t == null)
            {
                t = new Timer();
                t.Interval = 16;
                t.Tick += delegate
                {
                    double d = target - disp;
                    if (Math.Abs(d) < 0.003) { disp = target; t.Stop(); }
                    else disp += d * 0.25;
                    Invalidate();
                };
                disp = v;
            }
            if (!t.Enabled) t.Start();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            var bg = new RectangleF(0, Height / 2f - Ui.Fp(3), Width, Ui.Fp(6));
            Ui.Fill(g, bg, bg.Height / 2f, Theme.Track);
            var fg = new RectangleF(0, bg.Y, (float)(Width * disp), bg.Height);
            if (fg.Width > 0.5f)
            {
                Color col = AccentColor.A == 0 ? Theme.Accent : AccentColor;
                if (disp >= 0.9) col = Theme.Danger;
                else if (disp >= 0.75) col = Theme.Warn;
                Ui.Fill(g, fg, fg.Height / 2f, col);
            }
        }
    }

    /// <summary>半圆表:仪表盘下半区的 DRAM 式弧表。</summary>
    public class ArcGauge : Ctl
    {
        /// <summary>弧线颜色,为空时用主题色。</summary>
        public Color AccentColor = Color.Empty;
        /// <summary>弧内标题,如“内存”。</summary>
        public string Caption = "";
        /// <summary>中心读数文本,为空时显示百分比。帧率表用它显示原始帧数。</summary>
        public string Readout = "";
        /// <summary>右端刻度标注,默认 100。</summary>
        public string RangeMax = "100";
        private double target, disp;
        private Timer t;
        private string sub = "";

        public void SetValue(double v, string subText)
        {
            if (v < 0) v = 0;
            if (v > 1) v = 1;
            sub = subText == null ? "" : subText;
            if (Math.Abs(v - target) < 0.0005) { Invalidate(); return; }
            target = v;
            if (t == null)
            {
                t = new Timer();
                t.Interval = 16;
                t.Tick += delegate
                {
                    double d = target - disp;
                    if (Math.Abs(d) < 0.003) { disp = target; t.Stop(); }
                    else disp += d * 0.25;
                    Invalidate();
                };
                disp = v;
            }
            if (!t.Enabled) t.Start();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            // 圆弧下移、半径按高度收一点,外圈刻度才不会顶出控件上沿。
            float rad = Math.Min(Width * 0.42f, Height * 0.52f);
            float thick = Math.Max(2f, rad * 0.10f);
            float cx = Width / 2f, cy = Height * 0.72f;

            // 外圈刻度(上半圆),让弧表有机械仪表盘的味道。
            using (var pen = new Pen(Theme.Alpha(Theme.Text, Theme.Dark ? 60 : 42), Math.Max(1f, Ui.Fp(1))))
            {
                for (int i = 0; i <= 30; i++)
                {
                    bool big = i % 5 == 0;
                    double a = (180.0 + i * 6.0) * Math.PI / 180.0;
                    float r0 = rad + thick * 1.35f;
                    float r1 = r0 + (big ? Ui.Fp(7) : Ui.Fp(4));
                    pen.Color = big ? Theme.Alpha(Theme.Text, Theme.Dark ? 110 : 70)
                                    : Theme.Alpha(Theme.Text, Theme.Dark ? 48 : 34);
                    g.DrawLine(pen, cx + (float)Math.Cos(a) * r0, cy + (float)Math.Sin(a) * r0,
                        cx + (float)Math.Cos(a) * r1, cy + (float)Math.Sin(a) * r1);
                }
            }

            var arc = new RectangleF(cx - rad, cy - rad, rad * 2f, rad * 2f);
            using (var pen = new Pen(Theme.Mix(Theme.Track, Theme.Sub, Theme.Dark ? 0.12 : 0.28), thick))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, arc, 180f, 180f);
            }

            Color acc = AccentColor.A == 0 ? Theme.Accent : AccentColor;
            if (disp > 0.001)
            {
                using (var glow = new Pen(Theme.Alpha(acc, Theme.Dark ? 44 : 20), thick * 1.8f))
                {
                    glow.StartCap = LineCap.Round;
                    glow.EndCap = LineCap.Round;
                    g.DrawArc(glow, arc, 180f, (float)(180.0 * disp));
                }
                Color col = acc;
                // 渐变范围向外各扩一个线宽:否则圆头端帽超出渐变区,平铺模式会取到
                // 另一端的浅色,左下角出现一块浅色竖条。两点构造的画笔不支持 Clamp,只能靠扩范围。
                using (var brush = new LinearGradientBrush(new PointF(arc.Left - thick, arc.Bottom),
                    new PointF(arc.Right + thick, arc.Bottom), Theme.Mix(col, Color.Black, 0.18), Theme.Mix(col, Color.White, 0.25)))
                using (var pen = new Pen(brush, thick))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, arc, 180f, (float)(180.0 * disp));
                }

                // 弧尖亮点:像仪表指针的针尖,一眼看出读到哪。
                double ea = (180.0 + 180.0 * disp) * Math.PI / 180.0;
                float ex = cx + (float)Math.Cos(ea) * rad;
                float ey = cy + (float)Math.Sin(ea) * rad;
                float tr = Math.Max(1.8f, thick * 0.30f);
                using (var b = new SolidBrush(Theme.Alpha(acc, Theme.Dark ? 90 : 55)))
                    g.FillEllipse(b, ex - tr * 2f, ey - tr * 2f, tr * 4f, tr * 4f);
                using (var b = new SolidBrush(Color.White))
                    g.FillEllipse(b, ex - tr, ey - tr, tr * 2f, tr * 2f);
            }

            // 两端刻度标注:弧表有了 0 / 100,读数值才有参照。
            TextRenderer.DrawText(g, "0", Theme.F(7.5f, FontStyle.Regular),
                Rectangle.Round(new RectangleF(cx - rad - Ui.Fp(18), cy + Ui.Fp(6), Ui.Fp(36), Ui.Fp(14))), Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, RangeMax, Theme.F(7.5f, FontStyle.Regular),
                Rectangle.Round(new RectangleF(cx + rad - Ui.Fp(18), cy + Ui.Fp(6), Ui.Fp(36), Ui.Fp(14))), Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            if (Caption.Length > 0)
                TextRenderer.DrawText(g, Caption, Theme.F(10f, FontStyle.Bold),
                    Rectangle.Round(new RectangleF(0, cy - rad * 0.66f, Width, rad * 0.30f)), Theme.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            string pct = Readout.Length > 0 ? Readout : ((int)Math.Round(disp * 100)) + "%";
            float pt = rad / Math.Max(1f, Ui.S) * 0.30f;
            TextRenderer.DrawText(g, pct, Theme.Num(pt),
                Rectangle.Round(new RectangleF(0, cy - rad * 0.44f, Width, rad * 0.44f)), acc,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, sub, Theme.F(8.5f, FontStyle.Regular),
                Rectangle.Round(new RectangleF(0, cy - rad * 0.05f, Width, Ui.Fp(18))), Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>五轴雷达图:中间那张综合状态图。</summary>
    public class RadarChart : Ctl
    {
        /// <summary>数据多边形颜色,为空时用主题色。</summary>
        public Color AccentColor = Color.Empty;
        public string[] Labels = new string[0];
        private double[] vals = new double[0];

        public void SetValues(double[] v)
        {
            if (v == null) return;
            if (vals.Length != v.Length) vals = new double[v.Length];
            for (int i = 0; i < v.Length; i++)
                vals[i] = v[i] < 0 ? 0 : (v[i] > 1 ? 1 : v[i]);
            Invalidate();
        }

        private static PointF P(float cx, float cy, float r, double deg)
        {
            double a = (deg - 90) * Math.PI / 180.0;
            return new PointF(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            int n = Labels.Length;
            if (n < 3) return;
            float cx = Width / 2f, cy = Height / 2f;
            // 半径要给轴标签留出位置,否则最上面的标签会被控件上沿切掉。
            float rad = Math.Max(Ui.Fp(30f), Math.Min(Width * 0.40f, Math.Min(Width / 2f - Ui.Fp(42), Height / 2f - Ui.Fp(26))));
            Color acc = AccentColor.A == 0 ? Theme.Accent : AccentColor;

            // 网格圈 + 轴线
            for (int lv = 1; lv <= 4; lv++)
            {
                var pts = new PointF[n];
                for (int i = 0; i < n; i++) pts[i] = P(cx, cy, rad * lv / 4f, 360.0 / n * i);
                using (var pen = new Pen(Theme.Alpha(Theme.Text, Theme.Dark ? (lv == 4 ? 62 : 34) : (lv == 4 ? 48 : 26)), lv == 4 ? 1.4f : 1f))
                    g.DrawPolygon(pen, pts);
            }
            using (var pen = new Pen(Theme.Alpha(Theme.Text, Theme.Dark ? 34 : 24), 1f))
            {
                for (int i = 0; i < n; i++)
                {
                    PointF p = P(cx, cy, rad, 360.0 / n * i);
                    g.DrawLine(pen, cx, cy, p.X, p.Y);
                }
            }
            // 外圈每个轴交点一个小刻度点,雷达盘更像刻度盘而不是一张网。
            using (var dot = new SolidBrush(Theme.Alpha(Theme.Text, Theme.Dark ? 78 : 58)))
            {
                for (int i = 0; i < n; i++)
                {
                    PointF p = P(cx, cy, rad, 360.0 / n * i);
                    float d = Math.Max(1.6f, Ui.Fp(1.8f));
                    g.FillEllipse(dot, p.X - d, p.Y - d, d * 2f, d * 2f);
                }
            }

            // 数据多边形
            var dp = new PointF[n];
            for (int i = 0; i < n; i++)
                dp[i] = P(cx, cy, rad * (float)(i < vals.Length ? vals[i] : 0), 360.0 / n * i);
            // 外圈先画一圈柔光,数据面像浮在刻度盘上方。
            using (var glow = new Pen(Theme.Alpha(acc, Theme.Dark ? 66 : 40), Math.Max(4f, Ui.Fp(5))))
            {
                glow.LineJoin = LineJoin.Round;
                g.DrawPolygon(glow, dp);
            }
            double maxV = 0;
            for (int i = 0; i < n; i++) if (i < vals.Length && vals[i] > maxV) maxV = vals[i];
            if (maxV < 0.01)
            {
                // 还没有数据时多边形退化成一个点,径向渐变会画不出来,退回平涂。
                using (var fill = new SolidBrush(Theme.Alpha(acc, Theme.Dark ? 58 : 42)))
                using (var pen = new Pen(acc, Math.Max(1.5f, Ui.Fp(2))))
                {
                    g.FillPolygon(fill, dp);
                    g.DrawPolygon(pen, dp);
                }
            }
            else
            {
                using (var fill = new PathGradientBrush(dp))
                using (var pen = new Pen(acc, Math.Max(1.5f, Ui.Fp(2))))
                {
                    // 由外向内提亮的径向渐变:数据面有中间鼓起来的体积感,不是一张平涂的色块。
                    Color[] ring = new Color[n];
                    for (int i = 0; i < n; i++) ring[i] = Theme.Alpha(acc, Theme.Dark ? 26 : 16);
                    fill.CenterColor = Theme.Alpha(acc, Theme.Dark ? 96 : 66);
                    fill.CenterPoint = new PointF(cx, cy);
                    fill.SurroundColors = ring;
                    g.FillPolygon(fill, dp);
                    g.DrawPolygon(pen, dp);
                }
            }
            // 中心一个极小的圆点,把五条轴线收在一处。
            using (var hub = new SolidBrush(Theme.Alpha(acc, Theme.Dark ? 150 : 110)))
            {
                float d = Math.Max(1.8f, Ui.Fp(2.2f));
                g.FillEllipse(hub, cx - d, cy - d, d * 2f, d * 2f);
            }
            using (var dot = new SolidBrush(acc))
            {
                for (int i = 0; i < n; i++) g.FillEllipse(dot, dp[i].X - Ui.Fp(2.5f), dp[i].Y - Ui.Fp(2.5f), Ui.Fp(5), Ui.Fp(5));
            }

            // 轴标签
            for (int i = 0; i < n; i++)
            {
                PointF p = P(cx, cy, rad + Ui.Fp(15), 360.0 / n * i);
                var r = new RectangleF(p.X - Ui.Fp(34), p.Y - Ui.Fp(8), Ui.Fp(68), Ui.Fp(16));
                TextRenderer.DrawText(g, Labels[i], Theme.F(8f, FontStyle.Regular), Rectangle.Round(r), Theme.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>设置页用的整行说明:左侧标题+副标题,右侧控件。</summary>
    public class SettingRow : Ctl
    {
        public string Title = "";
        public string Sub = "";
        public bool ShowDivider = true;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            float s = Ui.S;
            int pad = Ui.Px(4);
            var titleRect = new Rectangle(pad, (int)(8 * s), Width - pad * 2, (int)(18 * s));
            TextRenderer.DrawText(g, Title, Theme.F(10f, FontStyle.Regular), titleRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Sub.Length > 0)
            {
                var subRect = new Rectangle(pad, (int)(26 * s), Width - pad * 2, (int)(18 * s));
                TextRenderer.DrawText(g, Sub, Theme.F(8.5f, FontStyle.Regular), subRect, Theme.Sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            if (ShowDivider)
            {
                using (var pen = new Pen(Theme.Border, 1f))
                    g.DrawLine(pen, pad, Height - 1, Width - pad, Height - 1);
            }
        }
    }

    /// <summary>帧率折线图:退游报告卡和战绩详情共用同一套画法。</summary>
    internal static class FpsChart
    {
        /// <summary>抽稀后的帧率序列;旧记录没有曲线时退回一条平均帧率水平线。</summary>
        public static double[] Series(SessionRecord r)
        {
            if (r == null) return null;
            if (r.FpsSeries != null && r.FpsSeries.Length >= 2)
            {
                int n = 0;
                for (int i = 0; i < r.FpsSeries.Length; i++)
                    if (r.FpsSeries[i] > 0) n++;
                if (n >= 2)
                {
                    var list = new double[n];
                    int k = 0;
                    for (int i = 0; i < r.FpsSeries.Length; i++)
                        if (r.FpsSeries[i] > 0) list[k++] = r.FpsSeries[i];
                    return list;
                }
            }
            if (r.FpsAvg > 0) return new double[] { r.FpsAvg, r.FpsAvg };
            return null;
        }

        /// <summary>面积 + 折线 + 平均线 + 峰值点;plot 是曲线可用的矩形,不含标题。</summary>
        public static void Plot(Graphics g, Rectangle plot, double[] series)
        {
            if (series == null || series.Length < 2) return;
            if (plot.Width < 4 || plot.Height < 4) return;

            double min = series[0], max = series[0];
            int maxAt = 0;
            for (int i = 1; i < series.Length; i++)
            {
                double v = series[i];
                if (v < min) min = v;
                if (v > max) { max = v; maxAt = i; }
            }

            // 纵轴留一点余量,曲线不会贴顶或贴底;下限不低于 0。
            double span = Math.Max(4, max - min);
            double lo = Math.Max(0, min - span * 0.15);
            double hi = max + span * 0.15;
            if (hi - lo < 1) hi = lo + 1;

            using (var p = new Pen(Theme.Border, 1f))
            {
                for (int i = 1; i <= 3; i++)
                {
                    int gy = plot.Top + plot.Height * i / 4;
                    g.DrawLine(p, plot.Left, gy, plot.Right, gy);
                }
            }

            PointF[] pts = new PointF[series.Length];
            for (int i = 0; i < series.Length; i++)
            {
                float x = plot.Left + (float)plot.Width * i / (series.Length - 1);
                float v = plot.Bottom - (float)((series[i] - lo) / (hi - lo)) * plot.Height;
                if (v < plot.Top) v = plot.Top;
                if (v > plot.Bottom) v = plot.Bottom;
                pts[i] = new PointF(x, v);
            }

            // 面积渐变:顶部强调色,向下淡出。
            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                path.AddLine(pts[pts.Length - 1].X, plot.Bottom, pts[0].X, plot.Bottom);
                path.CloseFigure();
                using (var lg = new LinearGradientBrush(
                    new Rectangle(plot.Left, plot.Top, Math.Max(1, plot.Width), Math.Max(1, plot.Height)),
                    Color.FromArgb(96, Theme.Accent), Color.FromArgb(0, Theme.Accent), 90f))
                    g.FillPath(lg, path);
            }

            using (var p = new Pen(Theme.Accent, 2f))
            {
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, pts);
            }

            // 平均线
            double sum = 0;
            for (int i = 0; i < series.Length; i++) sum += series[i];
            float ay = plot.Bottom - (float)((sum / series.Length - lo) / (hi - lo)) * plot.Height;
            using (var p = new Pen(Color.FromArgb(150, Theme.Sub), 1f))
            {
                p.DashStyle = DashStyle.Dash;
                g.DrawLine(p, plot.Left, ay, plot.Right, ay);
            }

            // 峰值点
            using (var b = new SolidBrush(Theme.Accent))
                g.FillEllipse(b, pts[maxAt].X - Ui.Px(3), pts[maxAt].Y - Ui.Px(3), Ui.Px(6), Ui.Px(6));
        }
    }
}