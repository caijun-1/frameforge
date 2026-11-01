using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

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

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
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
            // 双极性滑杆(-100~100)正值补一个加号,数值方向一眼可辨。
            string shown = val > 0 && Min < 0 ? "+" + val : val.ToString();
            TextRenderer.DrawText(g, Label, Theme.F(9f, FontStyle.Regular),
                new Rectangle(0, 0, Width, (int)labelH), Theme.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, shown + Unit, Theme.F(9.5f, FontStyle.Bold),
                new Rectangle(0, 0, Width, (int)labelH), Theme.Text,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            float cy = labelH + (Height - labelH) / 2f;
            float left = Ui.Fp(2), right = Width - Ui.Fp(2);
            var track = new RectangleF(left, cy - Ui.Fp(2), right - left, Ui.Fp(4));
            Ui.Fill(g, track, track.Height / 2f, Theme.Track);

            double t = (val - Min) / (double)Math.Max(1, Max - Min);
            // 双极性滑杆从中间零点向两侧填充:0 值不该显示成"半条蓝线"。
            bool bipolar = Min < 0 && Max > 0;
            float from = bipolar ? left + (right - left) * (float)((0 - Min) / (double)(Max - Min)) : left;
            float to = left + (right - left) * (float)t;
            var filled = new RectangleF(Math.Min(from, to), cy - Ui.Fp(2), Math.Abs(to - from), Ui.Fp(4));
            if (filled.Width > 0.5f)
                Ui.Fill(g, filled, filled.Height / 2f, Theme.Accent);

            float knob = Ui.Fp(13);
            float kx = left + (float)((right - left) * t) - knob / 2f;
            // 两端收进控件内,满值/零值时滑钮不会被裁掉半个。
            if (kx < 0f) kx = 0f;
            if (kx > Width - knob) kx = Width - knob;
            var kr = new RectangleF(kx, cy - knob / 2f, knob, knob);
            using (var b = new SolidBrush(Theme.Card)) g.FillEllipse(b, kr);
            using (var pen = new Pen(hover || drag ? Theme.AccentHover : Theme.Accent, Math.Max(2f, Ui.Fp(2.6))))
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
            Ui.Fill(g, r, rad, fill);
            if (!Selected) Ui.Stroke(g, r, rad, Theme.Border, 1f);
            TextRenderer.DrawText(g, Text ?? "", Theme.F(FontPt, Selected ? FontStyle.Bold : FontStyle.Regular),
                ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>细进度条,用于内存占用。</summary>
    public class Meter : Ctl
    {
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
                Color col = Theme.Accent;
                if (disp >= 0.9) col = Theme.Danger;
                else if (disp >= 0.75) col = Theme.Warn;
                Ui.Fill(g, fg, fg.Height / 2f, col);
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
}