using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge.UI
{
    /// <summary>
    /// 单游戏优化设置弹窗（game-profile §3.4）。
    /// 语义与 BoostDialog 不同：这里编辑的是"为该游戏记住的一份策略"，
    /// 因此新增页眉/边界说明/删除策略，但 9 项开关与 Segmented 完全复用现有控件和 boost.* 文案。
    /// 返回 true 表示保存或删除成功，调用方按名重读 profiles.ini 刷新列表。
    /// </summary>
    public static class GameProfileDialog
    {
        /// <summary>与 BoostDialog.Keys / Settings.ApplyCustom 完全同序（顺序 8 项落库，ideal 仅展示）。</summary>
        public static readonly string[] Keys = BoostDialog.Keys;

        public static bool Edit(IWin32Window owner, MainForm host, InstalledGame game)
        {
            if (host == null || game == null) return false;
            using (var box = new Box(host, game))
                return box.ShowDialog(owner) == DialogResult.OK;
        }

        private sealed class Box : Form
        {
            private readonly MainForm host;
            private readonly InstalledGame game;
            private readonly GameProfile existing;
            private readonly Toggle[] sw = new Toggle[Keys.Length];
            private readonly Segmented coreSeg;
            private readonly Segmented prioSeg;
            private readonly Segmented policySeg;
            private readonly ComboBox filterCombo;
            private readonly Segmented overlaySeg;
            private readonly int[] coreVals;
            private readonly ToolTip tips = new ToolTip();
            private readonly FlatButton btnSave;
            private readonly FlatButton btnFill;
            private readonly FlatButton btnRec;
            private readonly FlatButton btnDel;
            private readonly FlatButton btnCancel;

            private int Pad { get { return Ui.Px(24); } }
            private new int Top { get { return Ui.Px(100); } }
            private int RowH { get { return Ui.Px(50); } }
            private int DividerY { get { return Top + 5 * RowH + Ui.Px(16); } }

            public Box(MainForm host, InstalledGame game)
            {
                this.host = host;
                this.game = game;
                // 主键统一为进程名；显示名只用于展示，并回退查找旧版按显示名保存的策略。
                existing = host.Profiles != null ? host.Profiles.Find(game.ProcName, game.Name) : null;

                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                BackColor = Theme.Card;
                DoubleBuffered = true;
                KeyPreview = true;

                float s = Ui.S;
                ClientSize = new Size((int)(720 * s), (int)(792 * s));

                // 未配置时把全局计划当作"继承值"展示，让用户看清当前生效的是什么。
                BoostPlan seed = existing != null ? existing.Plan()
                    : (host.St != null ? host.St.Plan() : new BoostPlan());
                bool[] init = new bool[Keys.Length];
                init[0] = seed.Priority;
                init[1] = seed.PowerPlan;
                init[2] = seed.Timer;
                init[3] = seed.Dvr;
                init[4] = seed.Memory;
                init[5] = seed.Background;
                init[6] = seed.Affinity;
                init[7] = seed.Irq;
                init[8] = seed.IdealCpu;

                for (int i = 0; i < Keys.Length; i++)
                {
                    var t = new Toggle();
                    t.SetSilent(init[i]);
                    sw[i] = t;
                    t.CheckedChanged += delegate { Invalidate(); };
                    string tip = I18n.T("boost." + Keys[i] + ".sub");
                    if (AutoSkipped(i)) tip += " · " + I18n.T("games.opt.auto.skip");
                    tips.SetToolTip(t, tip);
                    Controls.Add(t);
                }

                // 核心档位与 BoostDialog 同一算法：1/4、1/2、3/4 物理核 + 全部。
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
                        : coreVals[i].ToString(CultureInfo.InvariantCulture);
                coreSeg = new Segmented();
                coreSeg.Items = coreItems;
                coreSeg.SetSilent(CoreIndex(seed.Cores));
                coreSeg.Visible = coreVals.Length > 1;
                Controls.Add(coreSeg);

                string[] prioItems = new string[5];
                for (int i = 0; i < 5; i++) prioItems[i] = I18n.T("boost.prio." + i);
                prioSeg = new Segmented();
                prioSeg.Items = prioItems;
                prioSeg.SetSilent(Math.Max(0, Math.Min(4, seed.PriorityLevel)));
                Controls.Add(prioSeg);

                policySeg = new Segmented();
                policySeg.Items = new string[]
                {
                    I18n.T("prof.follow_global"),
                    SafeT("core.policy.auto", "Auto"),
                    SafeT("core.policy.pcorefirst", "P-Core"),
                    SafeT("core.policy.singleccd", "Single CCD")
                };
                policySeg.SetSilent(CorePolicyIndex(existing != null ? existing.CorePolicy : ""));
                Controls.Add(policySeg);

                filterCombo = new ComboBox();
                filterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
                filterCombo.FlatStyle = FlatStyle.Flat;
                filterCombo.BackColor = Theme.Bg;
                filterCombo.ForeColor = Theme.Text;
                filterCombo.Font = Theme.F(9f, FontStyle.Regular);
                filterCombo.Items.Add(I18n.T("prof.follow_global"));
                filterCombo.Items.Add(I18n.T("prof.force_off"));
                for (int i = 1; i < Filter.PresetKeys.Length; i++)
                    filterCombo.Items.Add(I18n.T(Filter.PresetKeys[i]));
                int fp = existing != null ? existing.FilterPreset : -1;
                if (fp < -1 || fp > Filter.PresetKeys.Length - 1) fp = -1;
                filterCombo.SelectedIndex = fp + 1;
                Controls.Add(filterCombo);

                overlaySeg = new Segmented();
                overlaySeg.Items = new string[]
                {
                    I18n.T("prof.follow_global"),
                    I18n.T("prof.force_off"),
                    I18n.T("prof.force_on")
                };
                int om = existing != null ? existing.OverlayMode : -1;
                if (om < -1 || om > 1) om = -1;
                overlaySeg.SetSilent(om + 1);
                Controls.Add(overlaySeg);

                btnSave = NewButton(I18n.T("games.opt.save"), true, 118);
                btnFill = NewButton(I18n.T("games.opt.fill"), false, 118);
                btnRec = NewButton(I18n.T("games.opt.recommend"), false, 92);
                btnDel = NewButton(I18n.T("games.opt.delete"), false, 104);
                btnDel.AccentColor = Theme.Danger;
                btnDel.Enabled = existing != null;
                btnCancel = NewButton(I18n.T("games.opt.cancel"), false, 80);

                btnSave.Click += delegate { Save(); };
                btnFill.Click += delegate { FillFromGlobal(); };
                btnRec.Click += delegate { ResetRecommended(); };
                btnDel.Click += delegate { Delete(); };
                btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

                Controls.Add(btnFill);
                Controls.Add(btnRec);
                Controls.Add(btnDel);
                Controls.Add(btnCancel);
                Controls.Add(btnSave);

                LayoutItems();
                MouseDown += OnDrag;
                KeyDown += delegate(object o, KeyEventArgs k)
                {
                    if (k.KeyCode == System.Windows.Forms.Keys.Escape)
                    {
                        DialogResult = DialogResult.Cancel;
                        Close();
                    }
                    else if (k.KeyCode == System.Windows.Forms.Keys.Enter)
                    {
                        Save();
                    }
                };
            }

            private static bool AutoSkipped(int i)
            {
                // 方案 §3.4 要求标注"自动不执行"；与 MaybeAutoBoost 实际强制 false 的三项对齐：
                // Background / Irq / IdealCpu（Affinity + Cores 在自动路径仍会执行）。
                return i == 5 || i == 7 || i == 8;
            }

            private static FlatButton NewButton(string text, bool primary, double width)
            {
                var b = new FlatButton();
                b.Text = text;
                b.Primary = primary;
                b.FontPt = 9.5f;
                b.Tag = width;
                return b;
            }

            private int Cores
            {
                get
                {
                    int i = coreSeg.Selected;
                    return (i >= 0 && i < coreVals.Length) ? coreVals[i] : 0;
                }
            }

            private int Level
            {
                get
                {
                    int i = prioSeg.Selected;
                    return (i >= 0 && i <= 4) ? i : 4;
                }
            }

            private int CoreIndex(int cores)
            {
                int sel = coreVals.Length - 1;
                for (int i = 0; i < coreVals.Length; i++)
                    if (cores > 0 && coreVals[i] == cores) sel = i;
                return sel;
            }

            private void SetToggles(bool[] v)
            {
                for (int i = 0; i < sw.Length && i < v.Length; i++) sw[i].SetSilent(v[i]);
                Invalidate();
            }

            /// <summary>用当前全局设置填充；IdealCpu 同样只展示，不落 profile。</summary>
            private void FillFromGlobal()
            {
                BoostPlan p = host.St != null ? host.St.Plan() : new BoostPlan();
                SetToggles(new bool[]
                {
                    p.Priority, p.PowerPlan, p.Timer, p.Dvr, p.Memory,
                    p.Background, p.Affinity, p.Irq, p.IdealCpu
                });
                coreSeg.SetSilent(CoreIndex(p.Cores));
                prioSeg.SetSilent(Math.Max(0, Math.Min(4, p.PriorityLevel)));
                policySeg.SetSilent(0);
                filterCombo.SelectedIndex = 0;
                overlaySeg.SetSilent(0);
                Invalidate();
            }

            /// <summary>与 BoostDialog 的推荐数组一致：五项安全项开、四项进阶/实验项关。</summary>
            private void ResetRecommended()
            {
                SetToggles(new bool[] { true, true, true, true, true, false, false, false, false });
                coreSeg.SetSilent(coreVals.Length - 1);
                prioSeg.SetSilent(4);
                policySeg.SetSilent(0);
                filterCombo.SelectedIndex = 0;
                overlaySeg.SetSilent(0);
                Invalidate();
            }

            /// <summary>保存为该游戏策略：键名固定为小写进程名，旧显示名策略在保存时原位迁移。</summary>
            private void Save()
            {
                string key = ProfileStore.NormalizeKey(game.ProcName);
                if (key.Length == 0)
                {
                    host.Toast(I18n.T("games.opt.toast.invalid"));
                    return;
                }
                if (host.Profiles == null) return;

                string name = game.Name.Length > 0 ? game.Name : key;
                GameProfile p = existing != null ? existing : new GameProfile();
                p.Name = key;
                p.DisplayName = name;
                p.Priority = sw[0].Checked;
                p.PowerPlan = sw[1].Checked;
                p.Timer = sw[2].Checked;
                p.Dvr = sw[3].Checked;
                p.Memory = sw[4].Checked;
                p.Background = sw[5].Checked;
                p.Affinity = sw[6].Checked;
                p.Irq = sw[7].Checked;
                // sw[8] (IdealCpu) 按方案 P0 不落库：自动路径强制 false，避免"设了没效果"。
                p.Cores = Cores;
                p.PriorityLevel = Level;
                p.CorePolicy = SelectedCorePolicy();
                p.FilterPreset = filterCombo.SelectedIndex - 1;
                p.OverlayMode = overlaySeg.Selected - 1;
                host.Profiles.Save(p);
                host.Profiles.RemoveLegacy(p.Name, game.Name);
                host.Toast(I18n.T("games.opt.toast.saved", name));
                DialogResult = DialogResult.OK;
                Close();
            }

            /// <summary>删除策略需要二次确认，默认焦点在"取消"。</summary>
            private void Delete()
            {
                if (existing == null || host.Profiles == null) return;
                DialogResult r = MessageBox.Show(this,
                    I18n.T("games.opt.confirm.delete", game.Name),
                    I18n.T("games.opt.confirm.title"),
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.OK) return;
                // 主键与残留旧显示名节一起删除，避免下次打开时策略复活。
                host.Profiles.RemoveAll(game.ProcName, game.Name);
                host.Toast(I18n.T("games.opt.toast.deleted", game.Name));
                DialogResult = DialogResult.OK;
                Close();
            }

            private void LayoutItems()
            {
                int pad = Pad;
                int gap = Ui.Px(20);
                int colW = (Width - pad * 2 - gap) / 2;

                for (int i = 0; i < Keys.Length; i++)
                {
                    int col = i % 2;
                    int row = i / 2;
                    int x = pad + col * (colW + gap);
                    int y = Top + row * RowH;
                    sw[i].SetBounds(x + colW - Ui.Px(46), y + (RowH - Ui.Px(26)) / 2, Ui.Px(46), Ui.Px(26));
                }

                int segW = Ui.Px(330);
                coreSeg.SetBounds(Width - pad - segW, DividerY + Ui.Px(14), segW, Ui.Px(32));
                prioSeg.SetBounds(Width - pad - segW, DividerY + Ui.Px(54), segW, Ui.Px(32));
                policySeg.SetBounds(Width - pad - segW, DividerY + Ui.Px(94), segW, Ui.Px(32));
                filterCombo.SetBounds(Width - pad - segW, DividerY + Ui.Px(136), segW, Ui.Px(28));
                overlaySeg.SetBounds(Width - pad - segW, DividerY + Ui.Px(174), segW, Ui.Px(32));

                int footerH = Ui.Px(36);
                int footerY = Height - pad - footerH;
                int toolH = Ui.Px(34);
                int toolY = footerY - Ui.Px(32) - toolH;

                int saveW = ButtonWidth(btnSave);
                int cancelW = ButtonWidth(btnCancel);
                btnSave.SetBounds(Width - pad - saveW, footerY, saveW, footerH);
                btnCancel.SetBounds(btnSave.Left - Ui.Px(12) - cancelW, footerY, cancelW, footerH);

                // 工具操作独立一层；危险操作远离保存，且只使用描边样式。
                int delW = ButtonWidth(btnDel);
                btnDel.SetBounds(Width - pad - delW, toolY, delW, toolH);
                int fillW = ButtonWidth(btnFill);
                int recW = ButtonWidth(btnRec);
                btnFill.SetBounds(pad, toolY, fillW, toolH);
                btnRec.SetBounds(btnFill.Right + Ui.Px(12), toolY, recW, toolH);
            }

            private static int ButtonWidth(FlatButton button)
            {
                int minimum = Ui.Px((double)button.Tag);
                int text = TextRenderer.MeasureText(button.Text, Theme.F(9.5f, FontStyle.Bold)).Width;
                return Math.Max(minimum, text + Ui.Px(32));
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

                int pad = Pad;
                string name = game.Name.Length > 0 ? game.Name : I18n.T("games.opt.unnamed");
                var titleRect = new Rectangle(pad, Ui.Px(24), Width - pad * 2 - Ui.Px(100), Ui.Px(30));
                TextRenderer.DrawText(g, I18n.T("games.opt.title", name), Theme.F(15f, FontStyle.Bold),
                    titleRect, Theme.Text,
                    Ui.Flags(ContentAlignment.MiddleLeft, false) | TextFormatFlags.EndEllipsis);

                string saved = existing != null && existing.LastUsed != DateTime.MinValue
                    ? I18n.T("games.opt.header.saved", existing.LastUsed.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                    : I18n.T("games.opt.header.never");
                string meta = I18n.T("games.opt.header.source", SourceLabel(game.Source)) + "   ·   " + saved;
                TextRenderer.DrawText(g, meta, Theme.F(8.8f, FontStyle.Regular),
                    new Rectangle(pad, Ui.Px(60), Width - pad * 2, Ui.Px(20)),
                    Theme.Sub, Ui.Flags(ContentAlignment.MiddleLeft, false) | TextFormatFlags.EndEllipsis);

                bool own = existing != null;
                Color statusColor = own ? Theme.Accent : Theme.Faint;
                int statusX = Width - pad - Ui.Px(84);
                using (var b = new SolidBrush(statusColor))
                    g.FillRectangle(b, statusX, Ui.Px(31), Ui.Px(2), Ui.Px(16));
                TextRenderer.DrawText(g, I18n.T(own ? "games.opt.badge.own" : "games.opt.badge.global"),
                    Theme.F(8.5f, FontStyle.Bold),
                    new Rectangle(statusX + Ui.Px(10), Ui.Px(24), Ui.Px(74), Ui.Px(30)),
                    statusColor, Ui.Flags(ContentAlignment.MiddleLeft, false));

                using (var pen = new Pen(Theme.Border, 1f))
                {
                    g.DrawLine(pen, pad, DividerY, Width - pad, DividerY);
                    int footerLine = Height - pad - Ui.Px(36) - Ui.Px(16);
                    g.DrawLine(pen, pad, footerLine, Width - pad, footerLine);
                }

                int gap = Ui.Px(20);
                int colW = (Width - pad * 2 - gap) / 2;
                for (int i = 0; i < Keys.Length; i++)
                {
                    int col = i % 2;
                    int row = i / 2;
                    int x = pad + col * (colW + gap);
                    int y = Top + row * RowH;
                    bool skip = AutoSkipped(i);

                    int nameW = colW - Ui.Px(58) - (skip ? Ui.Px(78) : 0);
                    TextRenderer.DrawText(g, I18n.T("boost." + Keys[i]), Theme.F(9.5f, FontStyle.Bold),
                        new Rectangle(x, y, nameW, Ui.Px(20)), Theme.Text,
                        Ui.Flags(ContentAlignment.MiddleLeft, false) | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, I18n.T("boost." + Keys[i] + ".sub"),
                        Theme.F(8.5f, FontStyle.Regular),
                        new Rectangle(x, y + Ui.Px(22), colW - Ui.Px(58), Ui.Px(20)),
                        Theme.Sub,
                        Ui.Flags(ContentAlignment.TopLeft, false) | TextFormatFlags.EndEllipsis);

                    if (skip)
                    {
                        TextRenderer.DrawText(g, I18n.T("games.opt.auto.skip"),
                            Theme.F(8f, FontStyle.Regular),
                            new Rectangle(x + colW - Ui.Px(132), y, Ui.Px(72), Ui.Px(20)),
                            Theme.Warn, Ui.Flags(ContentAlignment.MiddleRight, false));
                    }
                    if (row > 0)
                    {
                        int lineY = y - Ui.Px(5);
                        using (var pen = new Pen(Theme.Border, 1f))
                            g.DrawLine(pen, x, lineY, x + colW, lineY);
                    }
                }

                if (coreSeg.Visible)
                    DrawSettingRow(g, I18n.T("boost.dialog.cores"), coreSeg);
                DrawSettingRow(g, I18n.T("boost.dialog.priority"), prioSeg);
                DrawSettingRow(g, I18n.T("prof.core_policy"), policySeg);
                DrawSettingRowBounds(g, I18n.T("prof.filter_preset"), filterCombo.Left, filterCombo.Top, filterCombo.Height);
                DrawSettingRow(g, I18n.T("prof.overlay_mode"), overlaySeg);

                // 固定两行边界说明，英文与低优先级警告都不会侵入工具栏。
                bool low = prioSeg.Selected <= 1;
                string boundary = low ? I18n.T("boost.prio.warn") : I18n.T("games.opt.boundary");
                TextRenderer.DrawText(g, boundary, Theme.F(8.8f, FontStyle.Regular),
                    new Rectangle(pad, DividerY + Ui.Px(218), Width - pad * 2, Ui.Px(46)),
                    low ? Theme.Warn : Theme.Sub, Ui.Flags(ContentAlignment.TopLeft, true));
            }

            private void DrawSettingRow(Graphics g, string label, Segmented seg)
            {
                DrawSettingRowBounds(g, label, seg.Left, seg.Top, seg.Height);
            }

            private void DrawSettingRowBounds(Graphics g, string label, int ctrlLeft, int ctrlTop, int ctrlHeight)
            {
                TextRenderer.DrawText(g, label, Theme.F(9.5f, FontStyle.Bold),
                    new Rectangle(Pad, ctrlTop, ctrlLeft - Pad - Ui.Px(16), ctrlHeight),
                    Theme.Text, Ui.Flags(ContentAlignment.MiddleLeft, false));
            }

            private string SelectedCorePolicy()
            {
                switch (policySeg.Selected)
                {
                    case 1: return "auto";
                    case 2: return "pcore";
                    case 3: return "singleccd";
                    default: return "";
                }
            }

            private static int CorePolicyIndex(string policy)
            {
                string s = (policy ?? "").Trim().ToLowerInvariant();
                if (s == "auto") return 1;
                if (s == "pcore" || s == "pcorefirst") return 2;
                if (s == "singleccd") return 3;
                return 0;
            }

            private static string SafeT(string key, string fallback)
            {
                string v = I18n.T(key);
                return v == key ? fallback : v;
            }

            private static string SourceLabel(string src)
            {
                switch ((src ?? "").ToLowerInvariant())
                {
                    case "steam": return I18n.T("games.src.steam");
                    case "epic": return I18n.T("games.src.epic");
                    case "gog": return I18n.T("games.src.gog");
                    case "disk": return I18n.T("games.src.disk");
                    case "manual": return I18n.T("games.src.manual");
                    default: return I18n.T("games.src.reg");
                }
            }
        }
    }
}
