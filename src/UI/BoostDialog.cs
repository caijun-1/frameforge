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

        public static bool[] Edit(IWin32Window owner, bool[] initial, int cores, out int newCores)
        {
            using (var box = new Box(initial, cores))
            {
                if (box.ShowDialog(owner) == DialogResult.OK)
                {
                    newCores = box.Cores;
                    return box.Values;
                }
                newCores = cores;
                return null;
            }
        }

        private sealed class Box : Form
        {
            private readonly Toggle[] sw = new Toggle[Keys.Length];
            private readonly bool[] init = new bool[Keys.Length];
            private readonly Segmented coreSeg;
            private readonly int[] coreVals;
            private FlatButton btnOk, btnCancel;

            public int Cores
            {
                get
                {
                    int i = coreSeg.Selected;
                    return (i >= 0 && i < coreVals.Length) ? coreVals[i] : 0;
                }
            }

            public bool[] Values
            {
                get
                {
                    var v = new bool[Keys.Length];
                    for (int i = 0; i < v.Length; i++) v[i] = sw[i].Checked;
                    return v;
                }
            }

            public Box(bool[] initial, int cores)
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

                // 核心数是"独占物理核心"的细项:默认不限制,想限制才选少一点。
                int phys = CpuTopology.CoreCount(CpuTopology.AllMask());
                var vals = new List<int>();
                int q1 = phys / 4, q2 = phys / 2, q3 = (phys * 3) / 4;
                if (q1 >= 2) vals.Add(q1);
                if (q2 >= 2 && q2 != q1) vals.Add(q2);
                if (q3 >= 2 && q3 != q2) vals.Add(q3);
                vals.Add(0);
                coreVals = vals.ToArray();

                string[] coreItems = new string[coreVals.Length];
                for (int i = 0; i < coreVals.Length; i++)
                    coreItems[i] = coreVals[i] == 0
                        ? I18n.T("boost.dialog.cores.all")
                        : coreVals[i].ToString();

                coreSeg = new Segmented();
                coreSeg.Items = coreItems;
                int sel = coreVals.Length - 1;
                for (int i = 0; i < coreVals.Length; i++)
                    if (cores > 0 && coreVals[i] == cores) sel = i;
                coreSeg.SetSilent(sel);
                coreSeg.Visible = coreVals.Length > 1;
                Controls.Add(coreSeg);

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

                int dividerY = top + 4 * rowH + Ui.Px(2);
                coreSeg.SetBounds(Width - pad - Ui.Px(250), dividerY + Ui.Px(12), Ui.Px(250), Ui.Px(28));

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

                if (coreSeg.Visible)
                {
                    var coreRect = new Rectangle(pad, dividerY + Ui.Px(12),
                        Width - pad * 2 - Ui.Px(266), Ui.Px(28));
                    TextRenderer.DrawText(g, I18n.T("boost.dialog.cores"), Theme.F(9.2f, FontStyle.Bold),
                        coreRect, Theme.Text, Ui.Flags(ContentAlignment.MiddleLeft, false));
                }

                int count = 0;
                for (int i = 0; i < sw.Length; i++) if (sw[i].Checked) count++;
                var countRect = new Rectangle(pad, Height - Ui.Px(24) - Ui.Px(36), Ui.Px(220), Ui.Px(36));
                TextRenderer.DrawText(g, I18n.T("boost.dialog.count", count), Theme.F(8.5f, FontStyle.Regular),
                    countRect, count == 0 ? Theme.Faint : Theme.Sub, Ui.Flags(ContentAlignment.MiddleLeft, false));
            }
        }
    }
}