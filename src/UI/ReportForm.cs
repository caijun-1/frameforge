using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        private readonly SessionBaseline baseline;
        private readonly Action openRecords;
        private readonly FlatButton btnList, btnClose;
        private Point dragFrom;
        private bool dragging;

        public ReportForm(SessionRecord r, Action openRecords) : this(r, null, openRecords) { }

        public ReportForm(SessionRecord r, SessionBaseline baseline, Action openRecords)
        {
            rec = r;
            this.baseline = baseline;
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

            int w = Ui.Px(680), h = Ui.Px(742);
            Size = new Size(w, h);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + Math.Max(0, (wa.Width - w) / 2),
                wa.Top + Math.Max(0, (wa.Height - h) / 2));

            btnList = new FlatButton();
            btnList.Primary = false;
            btnList.FontPt = 9.5f;
            btnList.Text = I18n.T("report.open");
            btnList.Click += delegate { Close(); if (openRecords != null) openRecords(); };

            btnClose = new FlatButton();
            btnClose.Primary = true;
            btnClose.FontPt = 9.5f;
            btnClose.Text = I18n.T("report.close");
            btnClose.Click += delegate { Close(); };

            Controls.Add(btnList);
            Controls.Add(btnClose);
            LayoutButtons();
        }

        private void LayoutButtons()
        {
            int bh = Ui.Px(36);
            int y = Height - Ui.Px(24) - bh;
            int closeW = ButtonWidth(btnClose, 88);
            int listW = ButtonWidth(btnList, 112);
            btnClose.SetBounds(Width - Ui.Px(24) - closeW, y, closeW, bh);
            btnList.SetBounds(btnClose.Left - Ui.Px(12) - listW, y, listW, bh);
        }

        private static int ButtonWidth(FlatButton button, int minimum)
        {
            int text = TextRenderer.MeasureText(button.Text, Theme.F(9.5f, FontStyle.Bold)).Width;
            return Math.Max(Ui.Px(minimum), text + Ui.Px(32));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (btnClose != null) LayoutButtons();
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
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

            int pad = Ui.Px(24);
            Txt(g, I18n.T("report.title"), Theme.Bold(15f), Theme.Text,
                new Rectangle(pad, Ui.Px(24), Width - pad * 2, Ui.Px(30)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Txt(g, I18n.T("report.sub"), Theme.Regular(8.8f), Theme.Sub,
                new Rectangle(pad, Ui.Px(58), Width - pad * 2, Ui.Px(18)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            int ico = Ui.Px(30);
            int gameY = Ui.Px(88);
            AppIcons.DrawApp(g, rec.IconPath, new Rectangle(pad, gameY + Ui.Px(1), ico, ico), Theme.Accent, AppGlyph.Chart);
            Txt(g, Safe(rec.DisplayName), Theme.Bold(18f), Theme.Text,
                new Rectangle(pad + ico + Ui.Px(12), gameY, Width - pad * 2 - ico - Ui.Px(12), Ui.Px(32)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            string when = rec.Start.ToString("yyyy-MM-dd HH:mm") + "   ·   "
                + I18n.T("report.duration") + " " + rec.Duration;
            Txt(g, when, Theme.Regular(8.8f), Theme.Faint,
                new Rectangle(pad, Ui.Px(124), Width - pad * 2, Ui.Px(18)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            bool hasBase = baseline != null && baseline.Count > 0;
            bool fpsGood = true, lowGood = true, tempGood = true;
            string fpsDelta = PctDelta(rec.FpsAvg, hasBase ? baseline.AvgFps : 0, true, out fpsGood);
            string lowDelta = PctDelta(rec.Low1, hasBase ? baseline.AvgLow1 : 0, true, out lowGood);
            string tempDelta = TempDelta(rec.GpuTempAvg, hasBase ? baseline.AvgTemp : 0, out tempGood);

            // ---- 三项核心读数 ----
            int heroH = Ui.Px(88);
            Rectangle hero = new Rectangle(pad, Ui.Px(156), Width - pad * 2, heroH);
            Tile(g, hero);
            int third = hero.Width / 3;
            BigStat(g, new Rectangle(hero.Left, hero.Top, third, heroH),
                I18n.T("report.stat.fps"), Fmt(rec.FpsAvg, "0"), I18n.T("report.fps.unit"),
                fpsDelta, fpsGood ? Theme.Accent : Theme.Danger);
            BigStat(g, new Rectangle(hero.Left + third, hero.Top, third, heroH),
                I18n.T("report.stat.low"), Fmt(rec.Low1, "0"), I18n.T("report.fps.unit"),
                lowDelta, lowGood ? Theme.Accent : Theme.Danger);
            BigStat(g, new Rectangle(hero.Left + third * 2, hero.Top, hero.Width - third * 2, heroH),
                I18n.T("report.duration"), rec.Duration, "", "", Theme.Faint);
            for (int i = 1; i < 3; i++)
            {
                int x = hero.Left + third * i;
                using (var p = new Pen(Theme.Border, 1f))
                    g.DrawLine(p, x, hero.Top + Ui.Px(16), x, hero.Bottom - Ui.Px(16));
            }

            // ---- 0.1% Low / 卡顿次数 / 近10局基线 ----
            Rectangle subBar = new Rectangle(pad, Ui.Px(254), Width - pad * 2, Ui.Px(36));
            Tile(g, subBar);
            int subW = subBar.Width / 3;
            string low01Val = rec.Low01 > 0
                ? rec.Low01.ToString("0") + " " + I18n.T("report.fps.unit")
                : I18n.T("report.none");
            string baseVal = hasBase && baseline.AvgFps > 0
                ? baseline.AvgFps.ToString("0") + " " + I18n.T("report.fps.unit")
                : I18n.T("report.none");
            SubCell(g, new Rectangle(subBar.Left, subBar.Top, subW, subBar.Height),
                I18n.T("rep.low01"), low01Val);
            SubCell(g, new Rectangle(subBar.Left + subW, subBar.Top, subW, subBar.Height),
                I18n.T("rep.stutters"), rec.StutterCount.ToString());
            SubCell(g, new Rectangle(subBar.Left + subW * 2, subBar.Top, subBar.Width - subW * 2, subBar.Height),
                I18n.T("rep.baseline"), baseVal);
            for (int i = 1; i < 3; i++)
            {
                int x = subBar.Left + subW * i;
                using (var p = new Pen(Theme.Border, 1f))
                    g.DrawLine(p, x, subBar.Top + Ui.Px(8), x, subBar.Bottom - Ui.Px(8));
            }

            // ---- 帧率曲线 ----
            Chart(g, new Rectangle(pad, Ui.Px(302), Width - pad * 2, Ui.Px(118)));

            // ---- 六个指标 ----
            int gap = Ui.Px(12);
            int tw = (Width - pad * 2 - gap * 2) / 3;
            int th = Ui.Px(70);
            int statsY = Ui.Px(432);
            Stat(g, pad, statsY, tw, th, I18n.T("report.stat.cpu"), Pct(rec.CpuAvg), Pct(rec.CpuMax), "", Theme.Faint);
            Stat(g, pad + tw + gap, statsY, tw, th, I18n.T("report.stat.gpu"), Pct(rec.GpuAvg), Pct(rec.GpuMax), "", Theme.Faint);
            Stat(g, pad + (tw + gap) * 2, statsY, tw, th, I18n.T("report.stat.temp"), Temp(rec.GpuTempAvg), Temp(rec.GpuTempMax),
                tempDelta, tempGood ? Theme.Accent : Theme.Warn);
            statsY += th + gap;
            Stat(g, pad, statsY, tw, th, I18n.T("report.stat.ram"), Mb(rec.RamAvg), Mb(rec.RamMax), "", Theme.Faint);
            Stat(g, pad + tw + gap, statsY, tw, th, I18n.T("report.stat.vram"), Mb(rec.VramMax), "", "", Theme.Faint);
            Stat(g, pad + (tw + gap) * 2, statsY, tw, th, I18n.T("report.stat.power"),
                I18n.T("report.w", rec.Power.ToString("0")), "", "", Theme.Faint);

            // ---- 能耗 ----
            string energy = I18n.T("report.energy", rec.Wh.ToString("0.0"))
                + "      ·      " + I18n.T("report.co2", rec.Co2.ToString("0"));
            Rectangle eRect = new Rectangle(pad, Ui.Px(596), Width - pad * 2, Ui.Px(40));
            Tile(g, eRect);
            Txt(g, energy, Theme.Regular(9.5f), Theme.Sub,
                new Rectangle(eRect.Left + Ui.Px(16), eRect.Top, eRect.Width - Ui.Px(32), eRect.Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (rec.Exe.Length > 0)
            {
                string exe = rec.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? rec.Exe : rec.Exe + ".exe";
                Txt(g, I18n.T("report.exe", exe), Theme.Regular(8.5f), Theme.Faint,
                    new Rectangle(pad, btnList.Top, btnList.Left - pad - Ui.Px(16), btnList.Height),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            using (var pen = new Pen(Theme.Border, 1f))
            {
                g.DrawLine(pen, pad, btnClose.Top - Ui.Px(16), Width - pad, btnClose.Top - Ui.Px(16));
                using (var p = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.Fp(8)))
                    g.DrawPath(pen, p);
            }
        }

        private void Tile(Graphics g, Rectangle r)
        {
            Ui.Fill(g, new RectangleF(r.X, r.Y, r.Width, r.Height), Ui.Fp(6), Theme.Card);
            Ui.Stroke(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f), Ui.Fp(6), Theme.Border, 1f);
        }

        private void BigStat(Graphics g, Rectangle r, string caption, string value, string unit, string delta, Color deltaColor)
        {
            int pad = Ui.Px(16);
            Txt(g, caption, Theme.Regular(9f), Theme.Sub,
                new Rectangle(r.Left + pad, r.Top + Ui.Px(12), r.Width - pad * 2, Ui.Px(18)), TextFormatFlags.Left);
            if (!string.IsNullOrEmpty(delta))
            {
                Txt(g, delta, Theme.Bold(8.8f), deltaColor,
                    new Rectangle(r.Left + pad, r.Top + Ui.Px(12), r.Width - pad * 2, Ui.Px(18)), TextFormatFlags.Right);
            }

            int unitW = unit.Length > 0 ? Ui.Px(46) : 0;
            int avail = r.Width - pad * 2 - unitW;
            // 时长这种长一点的数值按可用宽度自动缩小字号,避免被卡片边缘裁掉。
            Font big = Theme.Num(28f);
            Size need = TextRenderer.MeasureText(value, big);
            if (need.Width > avail && need.Width > 0)
            {
                float size = 28f * avail / need.Width;
                if (size < 14f) size = 14f;
                big = Theme.Num(size);
            }
            Txt(g, value, big, Theme.Text,
                new Rectangle(r.Left + pad, r.Top + Ui.Px(30), avail, Ui.Px(44)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (unitW > 0)
            {
                Txt(g, unit, Theme.Regular(9.5f), Theme.Faint,
                    new Rectangle(r.Right - pad - unitW, r.Top + Ui.Px(44), unitW, Ui.Px(20)),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        /// <summary>本局帧率曲线:淡色平涂 + 细折线,再叠一条平均线。</summary>
        private void Chart(Graphics g, Rectangle r)
        {
            Tile(g, r);
            int pad = Ui.Px(16);
            Txt(g, I18n.T("report.chart"), Theme.Bold(9f), Theme.Sub,
                new Rectangle(r.Left + pad, r.Top + Ui.Px(10), r.Width - pad * 2, Ui.Px(18)),
                TextFormatFlags.Left);

            double[] series = FpsChart.Series(rec);
            Rectangle plot = new Rectangle(r.Left + pad, r.Top + Ui.Px(36),
                r.Width - pad * 2, r.Height - Ui.Px(52));
            if (series == null || series.Length < 2 || plot.Height < Ui.Px(30))
            {
                Txt(g, I18n.T("report.chart.none"), Theme.Regular(9f), Theme.Faint,
                    new Rectangle(r.Left + pad, r.Top + Ui.Px(40), r.Width - pad * 2, Ui.Px(60)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }

            double max = series[0], sum = 0;
            for (int i = 0; i < series.Length; i++)
            {
                if (series[i] > max) max = series[i];
                sum += series[i];
            }
            double avg = sum / series.Length;

            Txt(g, I18n.T("report.chart.avg") + " " + avg.ToString("0")
                    + "      " + I18n.T("report.chart.peak") + " " + max.ToString("0"),
                Theme.Regular(8.6f), Theme.Faint,
                new Rectangle(r.Left + pad, r.Top + Ui.Px(10), r.Width - pad * 2, Ui.Px(18)),
                TextFormatFlags.Right);

            PlotFlat(g, plot, series);
        }

        private static void PlotFlat(Graphics g, Rectangle plot, double[] series)
        {
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

            double span = Math.Max(max - min, Math.Max(max * 0.06, 4));
            double lo = Math.Max(0, min - span * 0.35);
            double hi = max + span * 0.25;

            g.SetClip(plot);
            using (var grid = new Pen(Theme.Border, 1f) { DashStyle = DashStyle.Dash })
            {
                for (int i = 0; i < 3; i++)
                {
                    float gy = plot.Top + (float)i * plot.Height / 2f;
                    g.DrawLine(grid, plot.Left, gy, plot.Right, gy);
                }
            }

            int n = series.Length;
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                float fx = plot.Left + (float)i * (plot.Width - 1) / Math.Max(1, n - 1);
                float fy = plot.Bottom - (float)((series[i] - lo) / (hi - lo)) * (plot.Height - 1);
                pts[i] = new PointF(fx, fy);
            }

            using (var area = new GraphicsPath())
            {
                area.AddLines(pts);
                area.AddLine(pts[n - 1], new PointF(pts[n - 1].X, plot.Bottom));
                area.AddLine(new PointF(pts[n - 1].X, plot.Bottom), new PointF(pts[0].X, plot.Bottom));
                area.CloseFigure();
                using (var b = new SolidBrush(Theme.Alpha(Theme.Accent, Theme.Dark ? 26 : 18)))
                    g.FillPath(b, area);
            }
            using (var line = new Pen(Theme.Accent, Math.Max(1.25f, Ui.Fp(1.5f))) { LineJoin = LineJoin.Round })
                g.DrawLines(line, pts);

            float ay = plot.Bottom - (float)((avg - lo) / (hi - lo)) * (plot.Height - 1);
            using (var p = new Pen(Theme.Sub, Math.Max(1f, Ui.Fp(1))) { DashStyle = DashStyle.Dash })
                g.DrawLine(p, plot.Left, ay, plot.Right, ay);

            using (var b = new SolidBrush(Theme.Accent))
                g.FillEllipse(b, pts[maxAt].X - Ui.Fp(3), pts[maxAt].Y - Ui.Fp(3), Ui.Fp(6), Ui.Fp(6));
            g.ResetClip();
        }

        private void SubCell(Graphics g, Rectangle r, string caption, string value)
        {
            int pad = Ui.Px(14);
            Txt(g, caption, Theme.Regular(8.8f), Theme.Sub,
                new Rectangle(r.Left + pad, r.Top, r.Width - pad * 2, r.Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Txt(g, value, Theme.Bold(9.2f), Theme.Text,
                new Rectangle(r.Left + pad, r.Top, r.Width - pad * 2, r.Height),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void Stat(Graphics g, int x, int y, int w, int h, string caption, string avg, string max, string delta, Color deltaColor)
        {
            Rectangle r = new Rectangle(x, y, w, h);
            Tile(g, r);
            int pad = Ui.Px(14);
            Txt(g, caption, Theme.Regular(8.8f), Theme.Sub,
                new Rectangle(x + pad, y + Ui.Px(7), w - pad * 2, Ui.Px(14)), TextFormatFlags.Left);
            if (!string.IsNullOrEmpty(delta))
            {
                Txt(g, delta, Theme.Bold(8.5f), deltaColor,
                    new Rectangle(x + pad, y + Ui.Px(7), w - pad * 2, Ui.Px(14)), TextFormatFlags.Right);
            }
            // 主数值上移两像素、「最高」下移到卡片底部，中间留出至少 3px，
            // 避免 16pt 数字的下缘压到小字（高 DPI 下尤其明显）。
            Txt(g, avg, Theme.Num(16f), Theme.Text,
                new Rectangle(x + pad, y + Ui.Px(21), w - pad * 2, Ui.Px(27)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (max.Length > 0)
            {
                string tail = I18n.T("report.max") + " " + max;
                Txt(g, tail, Theme.Regular(8.5f), Theme.Faint,
                    new Rectangle(x + pad, y + h - Ui.Px(19), w - pad * 2, Ui.Px(14)), TextFormatFlags.Left);
            }
        }

        private static void Txt(Graphics g, string text, Font f, Color c, Rectangle r, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, f, r, c, flags | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        private static string PctDelta(double cur, double baseVal, bool higherIsBetter, out bool good)
        {
            good = true;
            if (cur <= 0 || baseVal <= 0) return "";
            double pct = (cur - baseVal) / baseVal * 100.0;
            int rounded = (int)Math.Round(pct);
            if (rounded == 0) return "";
            good = higherIsBetter ? (rounded > 0) : (rounded < 0);
            return (rounded > 0 ? "+" : "") + rounded.ToString() + "%";
        }

        private static string TempDelta(double cur, double baseVal, out bool good)
        {
            good = true;
            if (cur <= 0 || baseVal <= 0) return "";
            int diff = (int)Math.Round(cur - baseVal);
            if (diff == 0) return "";
            good = diff < 0;
            return (diff > 0 ? "+" : "") + diff.ToString() + "°C";
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
