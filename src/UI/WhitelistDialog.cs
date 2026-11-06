using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge.UI
{
    /// <summary>
    /// 保护名单:直接勾选正在运行的程序,不用手输进程名。
    /// 名单里的进程不会被「暂停后台进程」动到;游戏平台与录制工具默认保护,不可取消。
    /// </summary>
    public sealed class WhitelistDialog : Form
    {
        private readonly Panel list;
        private readonly List<CheckRow> rows = new List<CheckRow>();
        private readonly HashSet<string> picked;

        /// <summary>勾选结果,逗号分隔,不含 .exe。</summary>
        public string Result = "";

        public WhitelistDialog(string[] names)
        {
            picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names != null)
                foreach (string n in names)
                    if (!string.IsNullOrEmpty(n)) picked.Add(n);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Card;
            DoubleBuffered = true;
            KeyPreview = true;

            float s = Ui.S;
            ClientSize = new Size((int)(540 * s), (int)(500 * s));

            int pad = Ui.Px(24);
            int bw = Ui.Px(120), bh = Ui.Px(34);
            int bottom = ClientSize.Height - Ui.Px(18) - bh;

            list = new Panel();
            list.BackColor = Theme.Card;
            list.AutoScroll = true;
            list.SetBounds(pad, Ui.Px(116), ClientSize.Width - pad * 2, bottom - Ui.Px(14) - Ui.Px(116));
            list.Resize += delegate { LayoutRows(); };
            Controls.Add(list);

            BoostEngine.AppChoice[] items = BoostEngine.ProtectChoices();
            for (int i = 0; i < items.Length; i++)
            {
                BoostEngine.AppChoice c = items[i];
                var row = new CheckRow();
                row.Proc = c.Name;
                row.Label = c.Label;
                row.Locked = c.BuiltIn;
                row.Checked = c.BuiltIn || picked.Contains(c.Name);
                row.IconPath = c.Running ? GameDb.PathFor(c.Name) : "";
                row.Note = c.BuiltIn ? I18n.T("wl.locked")
                    : c.Running ? Fmt.Bytes(c.Memory) : I18n.T("wl.offline");
                rows.Add(row);
                list.Controls.Add(row);
            }
            LayoutRows();

            var btnCancel = new FlatButton();
            btnCancel.Primary = false;
            btnCancel.Text = I18n.T("dialog.cancel");
            btnCancel.FontPt = 9.5f;
            btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            btnCancel.SetBounds(ClientSize.Width - pad - bw * 2 - Ui.Px(10), bottom, bw, bh);
            Controls.Add(btnCancel);

            var btnOk = new FlatButton();
            btnOk.Primary = true;
            btnOk.Text = I18n.T("dialog.ok");
            btnOk.FontPt = 9.5f;
            btnOk.Click += delegate
            {
                Result = BuildResult();
                DialogResult = DialogResult.OK;
                Close();
            };
            btnOk.SetBounds(ClientSize.Width - pad - bw, bottom, bw, bh);
            Controls.Add(btnOk);

            MouseDown += OnDrag;
            KeyDown += delegate(object o, KeyEventArgs k)
            {
                if (k.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
        }

        private void LayoutRows()
        {
            if (list == null || rows.Count == 0) return;
            int rowH = Ui.Px(38);
            // 预留滚动条宽度,免得列表短时居中、长时抖动。
            int w = list.ClientSize.Width - Ui.Px(6) - SystemInformation.VerticalScrollBarWidth;
            if (w < Ui.Px(120)) w = Ui.Px(120);
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].SetBounds(Ui.Px(2), i * rowH, w, rowH);
            }
        }

        /// <summary>长按内置项不参与保存:它们本来就不在可暂停范围里。</summary>
        private string BuildResult()
        {
            var list2 = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                CheckRow r = rows[i];
                if (!r.Checked || r.Locked) continue;
                list2.Add(r.Proc);
            }
            return string.Join(",", list2.ToArray());
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

            using (GraphicsPath p = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.Fp(12)))
            using (Pen pen = new Pen(Theme.Border, 1f))
                g.DrawPath(pen, p);

            int pad = Ui.Px(24);
            Rectangle titleRect = new Rectangle(pad, Ui.Px(20), Width - pad * 2, Ui.Px(26));
            TextRenderer.DrawText(g, I18n.T("wl.title"), Theme.F(11.5f, FontStyle.Bold),
                titleRect, Theme.Text, Ui.Flags(ContentAlignment.MiddleLeft, false));

            Rectangle hintRect = new Rectangle(pad, Ui.Px(52), Width - pad * 2, Ui.Px(56));
            TextRenderer.DrawText(g, I18n.T("wl.hint"), Theme.F(8.6f, FontStyle.Regular),
                hintRect, Theme.Sub, Ui.Flags(ContentAlignment.TopLeft, true));
        }
    }

    /// <summary>一行「勾选 + 程序名 + 备注」,整行可点。</summary>
    internal sealed class CheckRow : Ctl
    {
        public string Proc = "";   // 进程名,保存用
        public string Label = "";
        public string Note = "";
        /// <summary>程序自己的图标路径,取不到就用盾牌/窗口矢量图标兜底。</summary>
        public string IconPath = "";
        public bool Checked;
        public bool Locked;

        private bool hover;

        public CheckRow()
        {
            BackColor = Theme.Card;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (Locked || e.Button != MouseButtons.Left) return;
            Checked = !Checked;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);

            if (hover && !Locked)
            {
                using (var b = new SolidBrush(Theme.Hover))
                    g.FillRectangle(b, new Rectangle(0, Ui.Px(2), Width, Height - Ui.Px(4)));
            }

            int box = Ui.Px(18);
            int ico = Ui.Px(17);
            int gx = Ui.Px(12);
            int by = (Height - box) / 2;
            AppIcons.DrawApp(g, IconPath,
                new Rectangle(gx, (Height - ico) / 2, ico, ico),
                Locked ? Theme.Accent : Theme.Sub, Locked ? AppGlyph.Shield : AppGlyph.App);

            int bx = gx + ico + Ui.Px(9);
            var boxRect = new RectangleF(bx, by, box, box);
            Color accent = Theme.Accent;

            if (Checked)
            {
                Ui.Fill(g, boxRect, Ui.Fp(5), Locked ? Theme.Mix(accent, Theme.Card, 0.45) : accent);
                using (var pen = new Pen(Color.White, Math.Max(1.6f, Ui.Fp(1.8f))))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    float x0 = bx + box * 0.24f, y0 = by + box * 0.52f;
                    float x1 = bx + box * 0.43f, y1 = by + box * 0.72f;
                    float x2 = bx + box * 0.78f, y2 = by + box * 0.30f;
                    g.DrawLines(pen, new PointF[] {
                        new PointF(x0, y0), new PointF(x1, y1), new PointF(x2, y2) });
                }
            }
            else
            {
                Ui.Stroke(g, boxRect, Ui.Fp(5), Theme.Faint, 1.4f);
            }

            int noteW = Ui.Px(96);
            int textX = bx + box + Ui.Px(12);
            int textW = Width - textX - noteW - Ui.Px(8);
            if (textW < Ui.Px(60)) textW = Ui.Px(60);

            Color fg = Locked ? Theme.Sub : Theme.Text;
            TextRenderer.DrawText(g, Label, Theme.F(9.4f, FontStyle.Regular),
                new Rectangle(textX, 0, textW, Height), fg,
                Ui.Flags(ContentAlignment.MiddleLeft, false) | TextFormatFlags.EndEllipsis);

            TextRenderer.DrawText(g, Note, Theme.F(8.4f, FontStyle.Regular),
                new Rectangle(Width - noteW - Ui.Px(12), 0, noteW, Height), Theme.Faint,
                Ui.Flags(ContentAlignment.MiddleRight, false));
        }
    }
}