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
    /// 自定义优化项弹窗:让用户逐项勾选本次加速要启用的项目,
    /// 并选择进程优先级档位。返回 null 表示取消。
    /// </summary>
    public static class BoostDialog
    {
        /// <summary>数组顺序与 Settings.ApplyCustom 保持一致。</summary>
        public static readonly string[] Keys =
        {
            "priority", "power", "timer", "dvr", "mem", "bg", "affinity", "irq", "ideal"
        };

        public static bool[] Edit(IWin32Window owner, bool[] initial, int cores, int level,
            out int newCores, out int newLevel)
        {
            string unused;
            return Edit(owner, initial, cores, level, "auto", out newCores, out newLevel, out unused);
        }

        public static bool[] Edit(IWin32Window owner, bool[] initial, int cores, int level,
            string corePolicy, out int newCores, out int newLevel, out string newCorePolicy)
        {
            using (var box = new Box(initial, cores, level, corePolicy))
            {
                if (box.ShowDialog(owner) == DialogResult.OK)
                {
                    newCores = box.Cores;
                    newLevel = box.Level;
                    newCorePolicy = box.CorePolicy;
                    return box.Values;
                }
                newCores = cores;
                newLevel = level;
                newCorePolicy = Settings.CorePolicyKey(corePolicy);
                return null;
            }
        }

        private sealed class Box : Form
        {
            private readonly Toggle[] sw = new Toggle[Keys.Length];
            private readonly bool[] init = new bool[Keys.Length];
            private readonly Segmented coreSeg;
            private readonly Segmented prioSeg;
            private readonly Segmented policySeg;
            private readonly string topoHint;
            private readonly int[] coreVals;
            private readonly ToolTip tips = new ToolTip();
            private FlatButton btnOk, btnCancel, btnReset;

            public int Cores
            {
                get
                {
                    int i = coreSeg.Selected;
                    return (i >= 0 && i < coreVals.Length) ? coreVals[i] : 0;
                }
            }

            /// <summary>0 低 / 1 低于正常 / 2 正常 / 3 高于正常 / 4 高。</summary>
            public int Level
            {
                get
                {
                    int i = prioSeg.Selected;
                    return (i >= 0 && i <= 4) ? i : 4;
                }
            }

            public string CorePolicy
            {
                get
                {
                    if (policySeg.Selected == 1) return "pcorefirst";
                    if (policySeg.Selected == 2) return "singleccd";
                    return "auto";
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

            public Box(bool[] initial, int cores, int level, string corePolicy)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                BackColor = Theme.Card;
                DoubleBuffered = true;
                KeyPreview = true;

                float s = Ui.S;
                ClientSize = new Size((int)(720 * s), (int)(668 * s));

                for (int i = 0; i < Keys.Length; i++)
                {
                    init[i] = initial != null && i < initial.Length && initial[i];
                    var t = new Toggle();
                    t.SetSilent(init[i]);
                    sw[i] = t;
                    t.CheckedChanged += delegate { Invalidate(); };
                    tips.SetToolTip(t, I18n.T("boost." + Keys[i] + ".sub"));
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

                string[] prioItems = new string[5];
                for (int i = 0; i < 5; i++) prioItems[i] = I18n.T("boost.prio." + i);
                prioSeg = new Segmented();
                prioSeg.Items = prioItems;
                prioSeg.SetSilent(Math.Max(0, Math.Min(4, level)));
                prioSeg.Visible = Keys.Length > 0;
                Controls.Add(prioSeg);

                policySeg = new Segmented();
                policySeg.Items = new string[]
                {
                    SafeT("core.policy.auto", "Auto"),
                    SafeT("core.policy.pcorefirst", "P-Core"),
                    SafeT("core.policy.singleccd", "Single CCD")
                };
                policySeg.SetSilent(PolicyIndex(corePolicy));
                Controls.Add(policySeg);
                topoHint = BuildTopoHint();

                btnReset = new FlatButton();
                btnReset.Primary = false;
                btnReset.Text = I18n.T("boost.dialog.reset");
                btnReset.FontPt = 9.5f;
                btnReset.Click += delegate
                {
                    bool[] rec = { true, true, true, true, true, false, false, false, false };
                    for (int i = 0; i < sw.Length && i < rec.Length; i++) sw[i].SetSilent(rec[i]);
                    coreSeg.Selected = coreVals.Length - 1;
                    prioSeg.Selected = 4;
                    policySeg.Selected = 0;
                    Invalidate();
                };
                Controls.Add(btnReset);

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
                int rowH = Ui.Px(50);
                int top = Ui.Px(124);

                for (int i = 0; i < Keys.Length; i++)
                {
                    int col = i % 2;
                    int row = i / 2;
                    int x = pad + col * (colW + gap);
                    int y = top + row * rowH;
                    sw[i].SetBounds(x + colW - Ui.Px(46), y + (rowH - Ui.Px(26)) / 2, Ui.Px(46), Ui.Px(26));
                }

                int rows = (Keys.Length + 1) / 2;
                int dividerY = top + rows * rowH + Ui.Px(16);
                int segW = Ui.Px(330);
                coreSeg.SetBounds(Width - pad - segW, dividerY + Ui.Px(16), segW, Ui.Px(32));
                prioSeg.SetBounds(Width - pad - segW, dividerY + Ui.Px(60), segW, Ui.Px(32));
                policySeg.SetBounds(Width - pad - segW, dividerY + Ui.Px(104), segW, Ui.Px(32));

                int bh = Ui.Px(36);
                int bottom = Height - pad - bh;
                int okW = ButtonWidth(btnOk, 96);
                int cancelW = ButtonWidth(btnCancel, 88);
                int resetW = ButtonWidth(btnReset, 104);
                btnOk.SetBounds(Width - pad - okW, bottom, okW, bh);
                btnCancel.SetBounds(btnOk.Left - Ui.Px(12) - cancelW, bottom, cancelW, bh);
                btnReset.SetBounds(pad, bottom, resetW, bh);
            }

            private static int ButtonWidth(FlatButton button, int minimum)
            {
                int text = TextRenderer.MeasureText(button.Text, Theme.F(9.5f, FontStyle.Bold)).Width;
                return Math.Max(Ui.Px(minimum), text + Ui.Px(32));
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
                    using (GraphicsPath p = Ui.Round(new RectangleF(0, 0, Width, Height), Ui.Fp(8)))
                        Region = new Region(p);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                using (var p = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.Fp(8)))
                using (var pen = new Pen(Theme.Border, 1f))
                    g.DrawPath(pen, p);

                int pad = Ui.Px(24);
                var titleRect = new Rectangle(pad, Ui.Px(24), Width - pad * 2, Ui.Px(30));
                TextRenderer.DrawText(g, I18n.T("boost.dialog.title"), Theme.F(15f, FontStyle.Bold),
                    titleRect, Theme.Text, Ui.Flags(ContentAlignment.MiddleLeft, false));

                // 固定说明区：英文完整占两行，不与下面的开关行争用高度。
                var bodyRect = new Rectangle(pad, Ui.Px(62), Width - pad * 2, Ui.Px(46));
                TextRenderer.DrawText(g, I18n.T("boost.dialog.hint"), Theme.F(9.5f, FontStyle.Regular),
                    bodyRect, Theme.Sub, Ui.Flags(ContentAlignment.TopLeft, true));

                int gap = Ui.Px(20);
                int colW = (Width - pad * 2 - gap) / 2;
                int rowH = Ui.Px(50);
                int top = Ui.Px(124);

                for (int i = 0; i < Keys.Length; i++)
                {
                    int col = i % 2;
                    int row = i / 2;
                    int x = pad + col * (colW + gap);
                    int y = top + row * rowH;

                    var nameRect = new Rectangle(x, y, colW - Ui.Px(58), Ui.Px(20));
                    TextRenderer.DrawText(g, I18n.T("boost." + Keys[i]), Theme.F(9.5f, FontStyle.Bold),
                        nameRect, Theme.Text,
                        Ui.Flags(ContentAlignment.MiddleLeft, false) | TextFormatFlags.EndEllipsis);

                    var subRect = new Rectangle(x, y + Ui.Px(22), colW - Ui.Px(58), Ui.Px(20));
                    TextRenderer.DrawText(g, I18n.T("boost." + Keys[i] + ".sub"), Theme.F(8.5f, FontStyle.Regular),
                        subRect, Theme.Sub,
                        Ui.Flags(ContentAlignment.TopLeft, false) | TextFormatFlags.EndEllipsis);

                    if (row > 0)
                    {
                        int lineY = y - Ui.Px(5);
                        using (var pen = new Pen(Theme.Border, 1f))
                            g.DrawLine(pen, x, lineY, x + colW, lineY);
                    }
                }

                int rows = (Keys.Length + 1) / 2;
                int dividerY = top + rows * rowH + Ui.Px(16);
                using (var pen = new Pen(Theme.Border, 1f))
                {
                    g.DrawLine(pen, pad, dividerY, Width - pad, dividerY);
                    int footerY = Height - pad - Ui.Px(36);
                    g.DrawLine(pen, pad, footerY - Ui.Px(16), Width - pad, footerY - Ui.Px(16));
                }

                if (coreSeg.Visible)
                    DrawSettingLabel(g, I18n.T("boost.dialog.cores"), coreSeg);
                DrawSettingLabel(g, I18n.T("boost.dialog.priority"), prioSeg);
                DrawSettingLabel(g, I18n.T("prof.core_policy"), policySeg);
                if (topoHint.Length > 0)
                {
                    TextRenderer.DrawText(g, topoHint, Theme.F(8.3f, FontStyle.Regular),
                        new Rectangle(pad, policySeg.Bottom + Ui.Px(4), Width - pad * 2, Ui.Px(18)),
                        Theme.Sub, Ui.Flags(ContentAlignment.MiddleLeft, false) | TextFormatFlags.EndEllipsis);
                }

                int count = 0;
                for (int i = 0; i < sw.Length; i++) if (sw[i].Checked) count++;
                int countX = btnReset.Right + Ui.Px(16);
                var countRect = new Rectangle(countX, btnReset.Top,
                    Math.Max(0, btnCancel.Left - Ui.Px(16) - countX), btnReset.Height);
                TextRenderer.DrawText(g, I18n.T("boost.dialog.count", count), Theme.F(8.8f, FontStyle.Regular),
                    countRect, count == 0 ? Theme.Faint : Theme.Sub, Ui.Flags(ContentAlignment.MiddleLeft, false));
            }

            private void DrawSettingLabel(Graphics g, string label, Segmented seg)
            {
                TextRenderer.DrawText(g, label, Theme.F(9.5f, FontStyle.Bold),
                    new Rectangle(Ui.Px(24), seg.Top, seg.Left - Ui.Px(40), seg.Height),
                    Theme.Text, Ui.Flags(ContentAlignment.MiddleLeft, false));
            }

            private static int PolicyIndex(string policy)
            {
                string s = (policy ?? "").Trim().ToLowerInvariant();
                if (s == "pcorefirst" || s == "pcore") return 1;
                if (s == "singleccd") return 2;
                return 0;
            }

            private static string SafeT(string key, string fallback)
            {
                string v = I18n.T(key);
                return v == key ? fallback : v;
            }

            private static string BuildTopoHint()
            {
                try
                {
                    CpuCore[] cores = CpuTopology.Cores();
                    if (cores == null || cores.Length == 0) return "";
                    int p = 0, e = 0;
                    var llcs = new HashSet<int>();
                    for (int i = 0; i < cores.Length; i++)
                    {
                        if (cores[i].Efficient) e++; else p++;
                        if (cores[i].LlcKnown) llcs.Add(cores[i].Llc);
                    }
                    string s = e > 0 ? I18n.T("core.summary", p, e) : I18n.T("core.summary.plain", p);
                    if (llcs.Count > 1) s += " · " + llcs.Count + " CCD";
                    return s;
                }
                catch { return ""; }
            }
        }
    }
}