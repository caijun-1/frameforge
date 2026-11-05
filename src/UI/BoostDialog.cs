using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace FrameForge.UI
{
    /// <summary>
    /// 自定义优化项弹窗:让用户逐项勾选本次加速要启用的项目。
    /// 返回 null 表示取消,否则返回 8 个开关的新值。
    /// </summary>
    public static class BoostDialog
    {
        /// <summary>数组顺序与 Settings.ApplyCustom 保持一致。</summary>
        public static readonly string[] Keys =
        {
            "priority", "power", "timer", "dvr", "mem", "bg", "affinity", "irq"
        };

        public static bool[] Edit(IWin32Window owner, bool[] initial)
        {
            using (var box = new Box(initial))
            {
                if (box.ShowDialog(owner) == DialogResult.OK) return box.Values;
                return null;
            }
        }

        private sealed class Box : Form
        {
            private readonly Toggle[] sw = new Toggle[Keys.Length];
            private readonly bool[] init = new bool[Keys.Length];
            private FlatButton btnOk, btnCancel;

            public bool[] Values
            {
                get
                {
                    var v = new bool[Keys.Length];
                    for (int i = 0; i < v.Length; i++) v[i] = sw[i].Checked;
                    return v;
                }
            }

            public Box(bool[] initial)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                BackColor = Theme.Card;
                DoubleBuffered = true;
                KeyPreview = true;

                float s = Ui.S;
                ClientSize = new Size((int)(560 * s), (int)(430 * s));

                for (int i = 0; i < Keys.Length; i++)
                {
                    init[i] = initial != null && i < initial.Length && initial[i];
                    var t = new Toggle();
                    t.SetSilent(init[i]);
                    sw[i] = t;
                    t.CheckedChanged += delegate { Invalidate(); };
                    Controls.Add(t);
                }

                btnCancel = new FlatButton();
                btnCancel.Primary = false;
                btnCancel.Text = I18n.T("boost.dialog.cancel");
                btnCancel.FontPt = 9.5f;
                btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
                Controls.Add(btnCancel);

                btnOk = new FlatButton();
                btnOk.Primary = true;
                btnOk.Text = I18n.T("boost.dialog.ok");
                btnOk.FontPt = 9.5f;
                btnOk.Click += delegate { DialogResult = DialogResult.OK; Close(); };
                Controls.Add(btnOk);

                LayoutItems();
                MouseDown += OnDrag;
                KeyDown += delegate(object o, KeyEventArgs k)
                {
                    if (k.KeyCode == System.Windows.Forms.Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                    if (k.KeyCode == System.Windows.Forms.Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
                };
            }

            private void LayoutItems()
            {
                int pad = Ui.Px(24);
                int gap = Ui.Px(20);
                int colW = (Width - pad * 2 - gap) / 2;
                int rowH = Ui.Px(54);
                int top = Ui.Px(94);

                for (int i = 0; i < Keys.Length; i++)
                {
                    int col = i % 2;
                    int row = i / 2;
                    int x = pad + col * (colW + gap);
                    int y = top + row * rowH;
                    sw[i].SetBounds(x + colW - Ui.Px(46), y + Ui.Px(2), Ui.Px(46), Ui.Px(26));
                }

                int bh = Ui.Px(36), bwOk = Ui.Px(110), bwCancel = Ui.Px(92);
                int bottom = Height - pad - bh;
                btnOk.SetBounds(Width - pad - bwOk, bottom, bwOk, bh);
                btnCancel.SetBounds(Width - pad - bwOk - Ui.Px(10) - bwCancel, bottom, bwCancel, bh);
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

                int pad = Ui.Px(24);
                var titleRect = new Rectangle(pad, Ui.Px(20), Width - pad * 2, Ui.Px(26));
                TextRenderer.DrawText(g, I18n.T("boost.dialog.title"), Theme.F(11.5f, FontStyle.Bold),
                    titleRect, Theme.Text, Ui.Flags(ContentAlignment.MiddleLeft, false));

                var bodyRect = new Rectangle(pad, Ui.Px(50), Width - pad * 2, Ui.Px(36));
                TextRenderer.DrawText(g, I18n.T("boost.dialog.hint"), Theme.F(9f, FontStyle.Regular),
                    bodyRect, Theme.Sub, Ui.Flags(ContentAlignment.TopLeft, true));

                int gap = Ui.Px(20);
                int colW = (Width - pad * 2 - gap) / 2;
                int rowH = Ui.Px(54);
                int top = Ui.Px(94);

                for (int i = 0; i < Keys.Length; i++)
                {
                    int col = i % 2;
                    int row = i / 2;
                    int x = pad + col * (colW + gap);
                    int y = top + row * rowH;

                    var nameRect = new Rectangle(x, y, colW - Ui.Px(56), Ui.Px(20));
                    TextRenderer.DrawText(g, I18n.T("boost." + Keys[i]), Theme.F(9.8f, FontStyle.Bold),
                        nameRect, Theme.Text, Ui.Flags(ContentAlignment.MiddleLeft, false));

                    var subRect = new Rectangle(x, y + Ui.Px(20), colW - Ui.Px(56), Ui.Px(20));
                    TextRenderer.DrawText(g, I18n.T("boost." + Keys[i] + ".sub"), Theme.F(8.2f, FontStyle.Regular),
                        subRect, Theme.Faint, Ui.Flags(ContentAlignment.TopLeft, false));

                    if (row > 0)
                    {
                        int lineY = y - Ui.Px(6);
                        using (var pen = new Pen(Theme.Alpha(Theme.Border, Theme.Dark ? 120 : 200), 1f))
                            g.DrawLine(pen, pad, lineY, Width - pad, lineY);
                    }
                }

                int dividerY = top + 4 * rowH + Ui.Px(2);
                using (var pen = new Pen(Theme.Border, 1f))
                    g.DrawLine(pen, pad, dividerY, Width - pad, dividerY);

                int count = 0;
                for (int i = 0; i < sw.Length; i++) if (sw[i].Checked) count++;
                var countRect = new Rectangle(pad, Height - Ui.Px(24) - Ui.Px(36), Ui.Px(220), Ui.Px(36));
                TextRenderer.DrawText(g, I18n.T("boost.dialog.count", count), Theme.F(8.5f, FontStyle.Regular),
                    countRect, count == 0 ? Theme.Faint : Theme.Sub, Ui.Flags(ContentAlignment.MiddleLeft, false));
            }
        }
    }
}