using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge.UI
{
    /// <summary>
    /// 退出游戏后弹出的本局报告卡:平均帧率 / 1% Low / 时长,再按六个指标列出
    /// 平均与最高值,最后给出耗电与碳排放估算。样式对齐游戏加加的战绩弹窗。
    /// </summary>
    public sealed class ReportForm : Form
    {
        private readonly SessionRecord rec;
        private readonly Action openRecords;
        private readonly FlatButton btnList, btnClose;
        private Point dragFrom;
        private bool dragging;

        public ReportForm(SessionRecord r, Action openRecords)
        {
            rec = r;
            this.openRecords = openRecords;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = I18n.T("report.title");
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);

            int w = Ui.Px(640), h = Ui.Px(700);
            Size = new Size(w, h);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + Math.Max(0, (wa.Width - w) / 2),
                wa.Top + Math.Max(0, (wa.Height - h) / 2));

            btnList = new FlatButton();
            btnList.Primary = false;
            btnList.Text = I18n.T("report.open");
            btnList.Click += delegate { Close(); if (openRecords != null) openRecords(); };

            btnClose = new FlatButton();
            btnClose.Text = I18n.T("report.close");
            btnClose.Click += delegate { Close(); };

            Controls.Add(btnList);
            Controls.Add(btnClose);
            LayoutButtons();
        }

        private void LayoutButtons()
        {
            int bw = Ui.Px(148), bh = Ui.Px(42), gap = Ui.Px(10);
            int y = Height - Ui.Px(18) - bh;
            btnClose.SetBounds(Width - Ui.Px(22) - bw, y, bw, bh);
            btnList.SetBounds(Width - Ui.Px(22) - bw * 2 - gap, y, bw, bh);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (btnClose != null) LayoutButtons();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

            // 顶部一条强调色,作为"刚打完一局"的视觉提示
            using (var b = new SolidBrush(Theme.Accent))
                g.FillRectangle(b, 0, 0, Width, Ui.Px(4));

            int pad = Ui.Px(24);
            int y = Ui.Px(24);

            // 行高要留够:SingleLine 下矩形比字形略矮就会裁掉文字底部(高 DPI 下尤其明显)。
            Txt(g, I18n.T("report.title"), Theme.Bold(11f), Theme.Sub,
                new Rectangle(pad, y, Width - pad * 2, Ui.Px(26)), TextFormatFlags.Left);
            y += Ui.Px(28);

            int ico = Ui.Px(30);
            AppIcons.DrawApp(g, rec.IconPath, new Rectangle(pad, y + Ui.Px(5), ico, ico), Theme.Accent, AppGlyph.Chart);

            Txt(g, Safe(rec.DisplayName), Theme.Bold(21f), Theme.Text,
                new Rectangle(pad + ico + Ui.Px(12), y, Width - pad * 2 - ico - Ui.Px(12), Ui.Px(40)),
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            y += Ui.Px(42);

            string when = rec.Start.ToString("yyyy-MM-dd HH:mm") + "   ·   "
                + I18n.T("report.duration") + " " + rec.Duration;
            Txt(g, when, Theme.Regular(9f), Theme.Faint,
                new Rectangle(pad, y, Width - pad * 2, Ui.Px(18)), TextFormatFlags.Left);
            y += Ui.Px(26);

            // ---- 三个大数字 ----
            int heroH = Ui.Px(104);
            Rectangle hero = new Rectangle(pad, y, Width - pad * 2, heroH);
            Tile(g, hero);
            int third = hero.Width / 3;
            BigStat(g, new Rectangle(hero.Left, hero.Top, third, heroH),
                I18n.T("report.stat.fps"), Fmt(rec.FpsAvg, "0"), I18n.T("report.fps.unit"));
            BigStat(g, new Rectangle(hero.Left + third, hero.Top, third, heroH),
                I18n.T("report.stat.low"), Fmt(rec.Low1, "0"), I18n.T("report.fps.unit"));
            BigStat(g, new Rectangle(hero.Left + third * 2, hero.Top, hero.Width - third * 2, heroH),
                I18n.T("report.duration"), rec.Duration, "");
            for (int i = 1; i < 3; i++)
            {
                int x = hero.Left + third * i;
                using (var p = new Pen(Theme.Border, 1f))
                    g.DrawLine(p, x, hero.Top + Ui.Px(18), x, hero.Bottom - Ui.Px(18));
            }
            y += heroH + Ui.Px(12);

            // ---- 帧率曲线 ----
            int chartH = Ui.Px(140);
            Chart(g, new Rectangle(pad, y, Width - pad * 2, chartH));
            y += chartH + Ui.Px(12);

            // ---- 六个指标 ----
            int gap = Ui.Px(10);
            int tw = (Width - pad * 2 - gap * 2) / 3;
            int th = Ui.Px(78);
            Stat(g, pad, y, tw, th, I18n.T("report.stat.cpu"), Pct(rec.CpuAvg), Pct(rec.CpuMax));
            Stat(g, pad + tw + gap, y, tw, th, I18n.T("report.stat.gpu"), Pct(rec.GpuAvg), Pct(rec.GpuMax));
            Stat(g, pad + (tw + gap) * 2, y, tw, th, I18n.T("report.stat.temp"), Temp(rec.GpuTempAvg), Temp(rec.GpuTempMax));
            y += th + gap;
            Stat(g, pad, y, tw, th, I18n.T("report.stat.ram"), Mb(rec.RamAvg), Mb(rec.RamMax));
            Stat(g, pad + tw + gap, y, tw, th, I18n.T("report.stat.vram"), Mb(rec.VramMax), "");
            Stat(g, pad + (tw + gap) * 2, y, tw, th, I18n.T("report.stat.power"),
                I18n.T("report.w", rec.Power.ToString("0")), "");
            y += th + Ui.Px(12);

            // ---- 能耗 ----
            string energy = I18n.T("report.energy", rec.Wh.ToString("0.0"))
                + "      ·      " + I18n.T("report.co2", rec.Co2.ToString("0"));
            Rectangle eRect = new Rectangle(pad, y, Width - pad * 2, Ui.Px(44));
            Tile(g, eRect);
            Txt(g, energy, Theme.Regular(9.5f), Theme.Sub,
                new Rectangle(eRect.Left + Ui.Px(16), eRect.Top, eRect.Width - Ui.Px(32), eRect.Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            if (rec.Exe.Length > 0)
            {
                string exe = rec.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? rec.Exe : rec.Exe + ".exe";
                Txt(g, I18n.T("report.exe", exe), Theme.Regular(8.2f), Theme.Faint,
                    new Rectangle(pad, Height - Ui.Px(70), Width - pad * 2, Ui.Px(16)), TextFormatFlags.Left);
            }
        }

        private void Tile(Graphics g, Rectangle r)
        {
            Ui.Fill(g, new RectangleF(r.X, r.Y, r.Width, r.Height), Ui.Fp(12), Theme.Card);
            Ui.Stroke(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f), Ui.Fp(12), Theme.Border, 1f);
        }

        private void BigStat(Graphics g, Rectangle r, string caption, string value, string unit)
        {
            int pad = Ui.Px(16);
            Txt(g, caption, Theme.Regular(9f), Theme.Sub,
                new Rectangle(r.Left + pad, r.Top + Ui.Px(12), r.Width - pad * 2, Ui.Px(18)), TextFormatFlags.Left);

            int unitW = unit.Length > 0 ? Ui.Px(46) : 0;
            int avail = r.Width - pad * 2 - unitW;
            // 时长这种长一点的数值按可用宽度自动缩小字号,避免被卡片边缘裁掉。
            Font big = Theme.Num(30f);
            Size need = TextRenderer.MeasureText(value, big);
            if (need.Width > avail && need.Width > 0)
            {
                float size = 30f * avail / need.Width;
                if (size < 14f) size = 14f;
                big = Theme.Num(size);
            }
            Txt(g, value, big, Theme.Text,
                new Rectangle(r.Left + pad, r.Top + Ui.Px(32), avail, Ui.Px(46)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (unitW > 0)
            {
                Txt(g, unit, Theme.Regular(9f), Theme.Faint,
                    new Rectangle(r.Right - pad - unitW, r.Top + Ui.Px(46), unitW, Ui.Px(20)),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        /// <summary>抽稀后的帧率序列;旧记录没有曲线时退回一条平均帧率水平线。</summary>
        private double[] Series()
        {
            if (rec.FpsSeries != null && rec.FpsSeries.Length >= 2)
            {
                var list = new List<double>(rec.FpsSeries.Length);
                for (int i = 0; i < rec.FpsSeries.Length; i++)
                    if (rec.FpsSeries[i] > 0) list.Add(rec.FpsSeries[i]);
                if (list.Count >= 2) return list.ToArray();
            }
            if (rec.FpsAvg > 0) return new double[] { rec.FpsAvg, rec.FpsAvg };
            return null;
        }

        /// <summary>本局帧率曲线:面积 + 折线,再叠一条平均线。</summary>
        private void Chart(Graphics g, Rectangle r)
        {
            Tile(g, r);
            int pad = Ui.Px(16);
            Txt(g, I18n.T("report.chart"), Theme.Bold(9f), Theme.Sub,
                new Rectangle(r.Left + pad, r.Top + Ui.Px(10), r.Width - pad * 2, Ui.Px(18)),
                TextFormatFlags.Left);

            double[] series = Series();
            Rectangle plot = new Rectangle(r.Left + pad, r.Top + Ui.Px(36),
                r.Width - pad * 2, r.Height - Ui.Px(52));
            if (series == null || series.Length < 2 || plot.Height < Ui.Px(30))
            {
                Txt(g, I18n.T("report.chart.none"), Theme.Regular(9f), Theme.Faint,
                    new Rectangle(r.Left + pad, r.Top + Ui.Px(40), r.Width - pad * 2, Ui.Px(60)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }

            double min = series[0], max = series[0], sum = 0;
            int maxAt = 0;
            for (int i = 0; i < series.Length; i++)
            {
                double v = series[i];
                if (v < min) min = v;
                if (v > max) { max = v; maxAt = i; }
                sum += v;
            }
            double avg = sum / series.Length;

            Txt(g, I18n.T("report.chart.avg") + " " + avg.ToString("0")
                    + "      " + I18n.T("report.chart.peak") + " " + max.ToString("0"),
                Theme.Regular(8.6f), Theme.Faint,
                new Rectangle(r.Left + pad, r.Top + Ui.Px(10), r.Width - pad * 2, Ui.Px(18)),
                TextFormatFlags.Right);

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
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddLines(pts);
                path.AddLine(pts[pts.Length - 1].X, plot.Bottom, pts[0].X, plot.Bottom);
                path.CloseFigure();
                using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(plot.Left, plot.Top, Math.Max(1, plot.Width), Math.Max(1, plot.Height)),
                    Color.FromArgb(96, Theme.Accent), Color.FromArgb(0, Theme.Accent), 90f))
                    g.FillPath(lg, path);
            }

            using (var p = new Pen(Theme.Accent, 2f))
            {
                p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                g.DrawLines(p, pts);
            }

            // 平均线
            float ay = plot.Bottom - (float)((avg - lo) / (hi - lo)) * plot.Height;
            using (var p = new Pen(Color.FromArgb(150, Theme.Sub), 1f))
            {
                p.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                g.DrawLine(p, plot.Left, ay, plot.Right, ay);
            }

            // 峰值点
            using (var b = new SolidBrush(Theme.Accent))
                g.FillEllipse(b, pts[maxAt].X - Ui.Px(3), pts[maxAt].Y - Ui.Px(3), Ui.Px(6), Ui.Px(6));
        }

        private void Stat(Graphics g, int x, int y, int w, int h, string caption, string avg, string max)
        {
            Rectangle r = new Rectangle(x, y, w, h);
            Tile(g, r);
            int pad = Ui.Px(14);
            Txt(g, caption, Theme.Regular(8.6f), Theme.Sub,
                new Rectangle(x + pad, y + Ui.Px(10), w - pad * 2, Ui.Px(16)), TextFormatFlags.Left);
            Txt(g, avg, Theme.Num(16f), Theme.Text,
                new Rectangle(x + pad, y + Ui.Px(28), w - pad * 2, Ui.Px(26)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (max.Length > 0)
            {
                string tail = I18n.T("report.max") + " " + max;
                Txt(g, tail, Theme.Regular(8.2f), Theme.Faint,
                    new Rectangle(x + pad, y + h - Ui.Px(24), w - pad * 2, Ui.Px(16)), TextFormatFlags.Left);
            }
        }

        private static void Txt(Graphics g, string text, Font f, Color c, Rectangle r, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, f, r, c, flags | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        private static string Safe(string s)
        {
            return string.IsNullOrEmpty(s) ? I18n.T("report.none") : s;
        }

        private static string Fmt(double v, string format)
        {
            return v > 0 ? v.ToString(format) : I18n.T("report.none");
        }

        private static string Pct(double v)
        {
            return v > 0 ? v.ToString("0") + "%" : I18n.T("report.none");
        }

        private static string Temp(double v)
        {
            return v > 0 ? v.ToString("0") + "°C" : I18n.T("report.none");
        }

        private static string Mb(double v)
        {
            if (v <= 0) return I18n.T("report.none");
            if (v >= 1024) return (v / 1024.0).ToString("0.0") + " GB";
            return v.ToString("0") + " MB";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                dragFrom = e.Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Point p = PointToScreen(e.Location);
                Location = new Point(p.X - dragFrom.X, p.Y - dragFrom.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }
    }
}
