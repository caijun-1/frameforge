using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge.UI
{
    /// <summary>
    /// 游戏体检:上采样组件、刷新率与帧率上限、内存通道、系统盘空间、显卡温度。
    /// 只读本机信息,不修改任何设置,也不联网。
    /// </summary>
    public sealed class AuditDialog : Form
    {
        private struct Row
        {
            public string Title;
            public string Value;
            public string Note;
            public bool Warn;
        }

        private readonly List<Row> rows = new List<Row>();
        private readonly FlatButton btnClose;

        public static void Show(IWin32Window owner, SysSample s, List<SessionRecord> records)
        {
            using (var f = new AuditDialog(s, records)) f.ShowDialog(owner);
        }

        private AuditDialog(SysSample s, List<SessionRecord> records)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = I18n.T("audit.title");
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            ClientSize = new Size(Ui.Px(680), Ui.Px(600));

            btnClose = new FlatButton();
            btnClose.Primary = true;
            btnClose.FontPt = 9.5f;
            btnClose.Text = I18n.T("audit.close");
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            Build(s, records);
            LayoutButton();
        }

        private void LayoutButton()
        {
            int bh = Ui.Px(36);
            int text = TextRenderer.MeasureText(btnClose.Text, Theme.F(9.5f, FontStyle.Bold)).Width;
            int bw = Math.Max(Ui.Px(96), text + Ui.Px(32));
            btnClose.SetBounds(Width - Ui.Px(24) - bw, Height - Ui.Px(24) - bh, bw, bh);
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

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
        }

        // =================================================================
        //  采集
        // =================================================================

        private void Build(SysSample s, List<SessionRecord> records)
        {
            string exe = "";
            string game = "";
            if (s != null && s.GamePid > 0)
            {
                exe = Compat.GameExePath(s.GamePid);
                game = s.GameTitle.Length > 0 ? s.GameTitle : s.GameName;
            }
            if (records != null)
            {
                for (int i = records.Count - 1; i >= 0 && exe.Length == 0; i--)
                {
                    string p = records[i].ExePath;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        exe = p;
                        if (game.Length == 0) game = records[i].Title;
                    }
                }
            }

            // ---- 上采样 ----
            var up = new Row();
            up.Title = I18n.T("audit.upscale");
            up.Value = game.Length > 0 ? game : "—";
            if (exe.Length == 0)
            {
                up.Note = I18n.T("audit.upscale.nogame");
            }
            else
            {
                string[] tech = Compat.DetectUpscaling(exe);
                up.Note = tech.Length > 0
                    ? string.Format(I18n.T("audit.upscale.found"), string.Join(" · ", tech))
                    : I18n.T("audit.upscale.none");
            }
            rows.Add(up);

            // ---- 刷新率与帧率上限 ----
            int hz = Compat.RefreshHz();
            var hzRow = new Row();
            hzRow.Title = I18n.T("audit.hz");
            hzRow.Value = hz > 0 ? string.Format(CultureInfo.InvariantCulture, "{0} Hz", hz) : "—";
            hzRow.Note = hz > 30
                ? string.Format(I18n.T("audit.hz.tip"), hz - 3)
                : I18n.T("audit.hz.unknown");
            rows.Add(hzRow);

            // ---- 内存 ----
            int modules, totalGb, speed;
            bool dual;
            var ram = new Row();
            ram.Title = I18n.T("audit.ram");
            if (Compat.MemoryInfo(out modules, out totalGb, out speed, out dual))
            {
                int each = modules > 0 ? (int)Math.Round(totalGb / (double)modules) : totalGb;
                ram.Value = string.Format(CultureInfo.InvariantCulture, "{0} × {1} GB @ {2} MT/s",
                    modules, each, speed);
                ram.Note = I18n.T(dual ? "audit.ram.dual" : "audit.ram.single");
                ram.Warn = !dual;
            }
            else
            {
                ram.Value = "—";
                ram.Note = I18n.T("audit.ram.none");
            }
            rows.Add(ram);

            // ---- 系统盘 ----
            int freeGb, diskGb;
            var disk = new Row();
            disk.Title = I18n.T("audit.disk");
            if (Compat.SystemDriveFree(out freeGb, out diskGb))
            {
                disk.Value = string.Format(CultureInfo.InvariantCulture, "{0} / {1} GB", freeGb, diskGb);
                disk.Note = I18n.T(freeGb < 15 ? "audit.disk.tip" : "audit.disk.ok");
                disk.Warn = freeGb < 15;
            }
            else
            {
                disk.Value = "—";
                disk.Note = I18n.T("audit.disk.none");
            }
            rows.Add(disk);

            // ---- 显卡温度 ----
            var gpu = new Row();
            gpu.Title = I18n.T("audit.gpu");
            if (s != null && s.GpuTemp > 0)
            {
                gpu.Value = string.Format(CultureInfo.InvariantCulture, "{0:0} °C", s.GpuTemp);
                gpu.Note = I18n.T(s.GpuTemp >= 83 ? "audit.gpu.hot" : "audit.gpu.ok");
                gpu.Warn = s.GpuTemp >= 83;
            }
            else
            {
                gpu.Value = "—";
                gpu.Note = I18n.T("audit.gpu.none");
            }
            rows.Add(gpu);
        }

        // =================================================================
        //  绘制
        // =================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

            int pad = Ui.Px(24);
            Txt(g, I18n.T("audit.title"), Theme.Bold(15f), Theme.Text,
                new Rectangle(pad, Ui.Px(24), Width - pad * 2, Ui.Px(30)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            Txt(g, I18n.T("audit.sub"), Theme.Regular(9f), Theme.Sub,
                new Rectangle(pad, Ui.Px(60), Width - pad * 2, Ui.Px(20)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            int rowH = Ui.Px(74);
            var card = new Rectangle(pad, Ui.Px(100), Width - pad * 2, rowH * rows.Count);
            var cardF = new RectangleF(card.X, card.Y, card.Width, card.Height);
            Ui.Fill(g, cardF, Ui.Fp(6), Theme.Card);
            Ui.Stroke(g, new RectangleF(card.X + 0.5f, card.Y + 0.5f, card.Width - 1f, card.Height - 1f),
                Ui.Fp(6), Theme.Border, 1f);

            using (var line = new Pen(Theme.Border, 1f))
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    Row it = rows[i];
                    int top = card.Top + rowH * i;
                    int inner = Ui.Px(16);
                    int labelW = Ui.Px(190);

                    Txt(g, it.Title, Theme.Bold(9.5f), Theme.Text,
                        new Rectangle(card.Left + inner, top + Ui.Px(10), labelW - inner, Ui.Px(20)),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    Txt(g, it.Value, Theme.Bold(9.5f), it.Warn ? Theme.Warn : Theme.Text,
                        new Rectangle(card.Left + labelW, top + Ui.Px(10),
                            card.Width - labelW - inner, Ui.Px(20)),
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    Txt(g, it.Note, Theme.Regular(8.8f), it.Warn ? Theme.Warn : Theme.Sub,
                        new Rectangle(card.Left + inner, top + Ui.Px(36),
                            card.Width - inner * 2, Ui.Px(32)),
                        TextFormatFlags.Left | TextFormatFlags.WordBreak);

                    if (i < rows.Count - 1)
                        g.DrawLine(line, card.Left + inner, top + rowH - 1, card.Right - inner, top + rowH - 1);
                }
            }

            int noteY = card.Bottom + Ui.Px(16);
            Txt(g, I18n.T("audit.note"), Theme.Regular(8.8f), Theme.Faint,
                new Rectangle(pad, noteY, btnClose.Left - pad - Ui.Px(16), Ui.Px(32)),
                TextFormatFlags.Left | TextFormatFlags.WordBreak);

            using (var pen = new Pen(Theme.Border, 1f))
            {
                g.DrawLine(pen, pad, btnClose.Top - Ui.Px(16), Width - pad, btnClose.Top - Ui.Px(16));
                using (var p = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.Fp(8)))
                    g.DrawPath(pen, p);
            }
        }

        private static void Txt(Graphics g, string text, Font font, Color color, Rectangle rect, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, font, rect, color, flags | TextFormatFlags.NoPrefix);
        }

    }
}