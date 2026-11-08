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
            ClientSize = new Size(Ui.Px(640), Ui.Px(526));

            btnClose = new FlatButton();
            btnClose.Primary = true;
            btnClose.FontPt = 9f;
            btnClose.Text = I18n.T("audit.close");
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            Build(s, records);
            LayoutButton();
        }

        private void LayoutButton()
        {
            int bw = Ui.Px(148), bh = Ui.Px(40);
            btnClose.SetBounds(Width - Ui.Px(24) - bw, Height - Ui.Px(22) - bh, bw, bh);
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
            using (var b = new SolidBrush(Theme.Accent))
                g.FillRectangle(b, 0, 0, Width, Ui.Px(4));

            int pad = Ui.Px(24);
            int y = Ui.Px(22);

            Txt(g, I18n.T("audit.title"), Theme.Bold(13f), Theme.Text,
                new Rectangle(pad, y, Width - pad * 2, Ui.Px(26)), TextFormatFlags.Left);
            y += Ui.Px(30);
            Txt(g, I18n.T("audit.sub"), Theme.Regular(8.8f), Theme.Faint,
                new Rectangle(pad, y, Width - pad * 2, Ui.Px(20)), TextFormatFlags.Left);
            y += Ui.Px(28);

            int rowH = Ui.Px(70);
            var card = new Rectangle(pad, y, Width - pad * 2, rowH * rows.Count);
            using (GraphicsPath p = Round(card, Ui.Px(12)))
            using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);

            using (var line = new Pen(Theme.Border, 1f))
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    Row it = rows[i];
                    int top = card.Top + rowH * i;
                    int inner = Ui.Px(16);

                    Txt(g, it.Title, Theme.Bold(9.6f), Theme.Text,
                        new Rectangle(card.Left + inner, top + Ui.Px(10),
                            card.Width / 2, Ui.Px(20)),
                        TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    Txt(g, it.Value, Theme.Bold(9.6f), it.Warn ? Theme.Warn : Theme.Accent,
                        new Rectangle(card.Left + card.Width / 2, top + Ui.Px(10),
                            card.Width / 2 - inner, Ui.Px(20)),
                        TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
                    Txt(g, it.Note, Theme.Regular(8.2f), it.Warn ? Theme.Warn : Theme.Sub,
                        new Rectangle(card.Left + inner, top + Ui.Px(32),
                            card.Width - inner * 2, Ui.Px(32)),
                        TextFormatFlags.Left | TextFormatFlags.WordBreak);

                    if (i < rows.Count - 1)
                        g.DrawLine(line, card.Left + inner, top + rowH - 1, card.Right - inner, top + rowH - 1);
                }
            }

            y = card.Bottom + Ui.Px(10);
            Txt(g, I18n.T("audit.note"), Theme.Regular(8f), Theme.Faint,
                new Rectangle(pad, y, Width - pad * 2 - Ui.Px(160), Ui.Px(18)), TextFormatFlags.Left);
        }

        private static void Txt(Graphics g, string text, Font font, Color color, Rectangle rect, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, font, rect, color, flags | TextFormatFlags.NoPrefix);
        }

        private static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}