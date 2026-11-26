using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge.UI
{
    /// <summary>
    /// 系统诊断工作区:顶部汇总状态,核心拓扑只读展示硬件,右侧逐项操作系统设置。
    /// </summary>
    public class HealthPage : Page
    {
        private const int Rows = 10;

        private readonly Panel scrollHost;
        private readonly Ctl surface;
        private readonly Card summaryCard, topologyCard, listCard;
        private readonly TextLabel head, sub, summaryLbl;
        private readonly TextLabel coreHead, coreSub, listHead, listCount;
        private readonly StatusDot summaryDot;
        private readonly CoreTopologyView topology;
        private readonly FlatButton btnGame, btnDaily, btnAudit;
        private readonly Hairline[] lines;
        private readonly StatusDot[] dots = new StatusDot[Rows];
        private readonly TextLabel[] titles = new TextLabel[Rows];
        private readonly TextLabel[] values = new TextLabel[Rows];
        private readonly TextLabel[] notes = new TextLabel[Rows];
        private readonly Toggle[] toggles = new Toggle[Rows];
        private readonly Segmented[] choices = new Segmented[Rows];
        private readonly FlatButton[] acts = new FlatButton[Rows];

        private List<HealthItem> items = new List<HealthItem>();
        private bool syncing;
        private bool checking;
        // 用户最后点过的预设;点过之后高亮就以它为准,不再靠读状态反推。
        private string activePreset = "";

        public HealthPage(MainForm host) : base(host)
        {
            BackColor = Theme.Bg;

            var viewport = new ScrollHost();
            scrollHost = viewport;
            scrollHost.Dock = DockStyle.Fill;
            scrollHost.AutoScroll = true;
            Controls.Add(scrollHost);

            // 内容表面也铺窗口底纹,而不是纯色:整页和卡片之间不再出现一块不透明矩形。
            surface = new Surface(viewport);
            scrollHost.Controls.Add(surface);

            summaryCard = NewCard();
            topologyCard = NewCard();
            listCard = NewCard();
            surface.Controls.Add(summaryCard);
            surface.Controls.Add(topologyCard);
            surface.Controls.Add(listCard);

            head = new TextLabel();
            head.Size = 14f;
            head.Style = FontStyle.Bold;
            head.Ellipsis = true;
            head.Text = I18n.T("health.title");
            summaryCard.Controls.Add(head);

            sub = new TextLabel();
            sub.Size = 8.5f;
            sub.ForeColor = Theme.Sub;
            sub.Ellipsis = true;
            sub.Text = I18n.T("health.sub");
            summaryCard.Controls.Add(sub);

            summaryLbl = new TextLabel();
            summaryLbl.Size = 9f;
            summaryLbl.Style = FontStyle.Bold;
            summaryLbl.Align = ContentAlignment.MiddleRight;
            summaryLbl.Ellipsis = true;
            summaryLbl.Text = I18n.T("health.summary.ok");
            summaryCard.Controls.Add(summaryLbl);

            summaryDot = new StatusDot();
            summaryDot.Dot = Theme.Accent;
            summaryCard.Controls.Add(summaryDot);

            coreHead = new TextLabel();
            coreHead.Size = 10.2f;
            coreHead.Style = FontStyle.Bold;
            coreHead.Ellipsis = true;
            coreHead.Text = I18n.T("health.core.title");
            topologyCard.Controls.Add(coreHead);

            coreSub = new TextLabel();
            coreSub.Size = 8.5f;
            coreSub.ForeColor = Theme.Sub;
            coreSub.Ellipsis = true;
            coreSub.Text = "—";
            topologyCard.Controls.Add(coreSub);

            topology = new CoreTopologyView();
            topology.BackColor = Theme.Card;
            topologyCard.Controls.Add(topology);

            listHead = new TextLabel();
            listHead.Size = 10.2f;
            listHead.Style = FontStyle.Bold;
            listHead.Ellipsis = true;
            listHead.Text = I18n.T("health.diag.title");
            listCard.Controls.Add(listHead);

            listCount = new TextLabel();
            listCount.Size = 9f;
            listCount.ForeColor = Theme.Faint;
            listCount.Align = ContentAlignment.MiddleRight;
            listCount.Text = "00 / " + Rows.ToString("00");
            listCard.Controls.Add(listCount);

            for (int i = 0; i < Rows; i++)
            {
                var d = new StatusDot();
                dots[i] = d;
                listCard.Controls.Add(d);

                var t = new TextLabel();
                t.Size = 10.2f;
                t.Ellipsis = true;
                titles[i] = t;
                listCard.Controls.Add(t);

                var v = new TextLabel();
                v.Size = 9f;
                v.Align = ContentAlignment.MiddleRight;
                v.Ellipsis = true;
                values[i] = v;
                listCard.Controls.Add(v);

                var n = new TextLabel();
                n.Size = 8.5f;
                n.ForeColor = Theme.Faint;
                n.Ellipsis = true;
                notes[i] = n;
                listCard.Controls.Add(n);

                var tg = new Toggle();
                tg.Visible = false;
                int ti = i;
                tg.CheckedChanged += delegate { if (!syncing) ApplyToggle(ti, tg.Checked); };
                toggles[i] = tg;
                listCard.Controls.Add(tg);

                var sg = new ConsoleSegmented();
                sg.Visible = false;
                int si = i;
                sg.Changed += delegate { if (!syncing) ApplyChoice(si, sg.Selected); };
                choices[i] = sg;
                listCard.Controls.Add(sg);

                var b = new FlatButton();
                b.FontPt = 8.5f;
                b.Radius = 4;
                b.Primary = false;
                b.Visible = false;
                int bi = i;
                b.Click += delegate { DoAction(bi); };
                acts[i] = b;
                listCard.Controls.Add(b);
            }

            btnGame = new FlatButton();
            btnGame.FontPt = 8.5f;
            btnGame.Radius = 4;
            btnGame.Primary = false;
            btnGame.Text = I18n.T("health.preset.game");
            btnGame.Click += delegate { ApplyPreset("game"); };
            summaryCard.Controls.Add(btnGame);

            btnDaily = new FlatButton();
            btnDaily.FontPt = 8.5f;
            btnDaily.Radius = 4;
            btnDaily.Primary = false;
            btnDaily.Text = I18n.T("health.preset.daily");
            btnDaily.Click += delegate { ApplyPreset("daily"); };
            summaryCard.Controls.Add(btnDaily);

            btnAudit = new FlatButton();
            btnAudit.FontPt = 8.5f;
            btnAudit.Radius = 4;
            btnAudit.Primary = false;
            btnAudit.Text = I18n.T("health.audit");
            btnAudit.Click += delegate { OpenAudit(); };
            summaryCard.Controls.Add(btnAudit);

            // 只保留结果列表中的细分割线,不再给每一行重复描边。
            lines = new Hairline[Rows - 1];
            for (int i = 0; i < lines.Length; i++)
            {
                var ln = new Hairline();
                lines[i] = ln;
                listCard.Controls.Add(ln);
            }
        }

        private static Card NewCard()
        {
            var c = new Card();
            c.BackColor = Theme.Card;
            c.Radius = 6;
            c.ShowBorder = true;
            return c;
        }

        /// <summary>每次切到这一页都重新读一遍,改完系统设置回来能看到变化。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Reload();
        }

        public void Reload()
        {
            if (checking) return;
            checking = true;
            try
            {
                items = Health.Check(Host == null || Host.Mon == null ? null : Host.Mon.Current);
            }
            catch (Exception ex)
            {
                Log.Write("health check: " + ex.Message);
                items = new List<HealthItem>();
            }
            finally { checking = false; }

            syncing = true;
            try
            {
                for (int i = 0; i < Rows; i++)
                {
                    bool has = i < items.Count;
                    dots[i].Visible = has;
                    titles[i].Visible = has;
                    // 开关和分段本身已经表达了状态,再叠一行"已开启/已关闭"只会制造噪音。
                    values[i].Visible = has && !items[i].CanToggle && !items[i].CanChoose;
                    notes[i].Visible = has;
                    toggles[i].Visible = has && items[i].CanToggle;
                    choices[i].Visible = has && items[i].CanChoose;
                    acts[i].Visible = has && !items[i].CanToggle && !items[i].CanChoose && items[i].Action.Length > 0;
                    if (!has) continue;

                    HealthItem it = items[i];
                    titles[i].Text = I18n.T(it.Key);
                    values[i].Text = it.Value;
                    values[i].ForeColor = LevelColor(it.Level);
                    notes[i].Text = ComposeNote(it);
                    // 说明行保持中性色,只让真正有问题的一行用红色:整页橙色会失去重点。
                    notes[i].ForeColor = it.Level == HealthLevel.Bad ? Theme.Danger : Theme.Faint;
                    dots[i].Dot = LevelColor(it.Level);

                    if (it.CanToggle) toggles[i].SetSilent(it.On);
                    if (it.CanChoose)
                    {
                        choices[i].Items = it.Choices;
                        choices[i].SetSilent(it.Selected);
                        choices[i].Invalidate();
                    }
                    if (acts[i].Visible)
                    {
                        acts[i].Text = it.ActKey.Length > 0 ? I18n.T(it.ActKey) : I18n.T("health.act.open");
                        acts[i].Primary = it.Action == "admin";
                    }
                }

                for (int i = 0; i < lines.Length; i++)
                    lines[i].Visible = items.Count > i + 1;
            }
            finally { syncing = false; }

            // 预设按钮的高亮:点过哪个就亮哪个;没点过时按系统当前状态推断。
            string preset = activePreset.Length > 0 ? activePreset : ActivePreset();
            btnGame.Primary = preset == "game";
            btnDaily.Primary = preset == "daily";
            btnGame.Invalidate();
            btnDaily.Invalidate();

            SyncSummary();
            SyncTopology();
            LayoutConsole();
            Invalidate();
        }

        private void SyncSummary()
        {
            int todo = 0;
            bool bad = false;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Level == HealthLevel.Bad) bad = true;
                if (items[i].Level == HealthLevel.Bad || items[i].Level == HealthLevel.Warn) todo++;
            }
            summaryLbl.Text = todo == 0 ? I18n.T("health.summary.ok") : I18n.T("health.summary.todo", todo);
            Color level = bad ? Theme.Danger : (todo > 0 ? Theme.Warn : Theme.Accent);
            summaryLbl.ForeColor = level;
            summaryDot.Dot = level;
            listCount.Text = items.Count.ToString("00") + " / " + Rows.ToString("00");
        }

        private void SyncTopology()
        {
            CpuCore[] cores;
            try { cores = CpuTopology.Cores(); }
            catch (Exception ex)
            {
                Log.Write("health topology: " + ex.Message);
                cores = new CpuCore[0];
            }
            topology.SetCores(cores);

            int p = 0, e = 0;
            for (int i = 0; i < cores.Length; i++)
            {
                if (cores[i].Efficient) e++; else p++;
            }
            coreSub.Text = cores.Length == 0 ? "—" : (e > 0
                ? I18n.T("core.summary", p, e)
                : I18n.T("core.summary.plain", cores.Length));
        }

        /// <summary>按当前三项关键设置判断更接近哪个预设,都不是就不高亮。</summary>
        private string ActivePreset()
        {
            bool hasPower = false, high = false, hagsOn = false, dvrOn = false;
            foreach (HealthItem it in items)
            {
                if (it.Id == "power") { hasPower = true; high = it.Selected == 0; }
                else if (it.Id == "hags") hagsOn = it.On;
                else if (it.Id == "dvr") dvrOn = it.On;
            }
            if (!hasPower) return "";
            if (hagsOn && !dvrOn && high) return "game";
            if (hagsOn && dvrOn && !high) return "daily";
            return "";
        }

        private void ApplyToggle(int i, bool on)
        {
            if (i >= items.Count) return;
            HealthItem it = items[i];
            if (it.Confirm && !ConfirmOff(it))
            {
                Reload();
                return;
            }
            if (it.Id == "fse")
            {
                // 「全屏优化」开关:开=保留系统优化,关=为该游戏写入禁用标记。
                Report(Tune.SetFullscreenOpt(it.Path, !on));
                Reload();
                return;
            }
            Report(Tune.SetToggle(it.Id, on));
            // 预设只包含电源 / HAGS / DVR,这三项被单独改过就不再高亮预设。
            if (it.Id == "power" || it.Id == "hags" || it.Id == "dvr") activePreset = "";
            Reload();
        }

        private void ApplyChoice(int i, int selected)
        {
            if (i >= items.Count) return;
            HealthItem it = items[i];
            if (it.Id != "power") return;
            activePreset = "";
            Report(Tune.SetPower(selected == 0));
            Reload();
        }

        private void ApplyPreset(string preset)
        {
            activePreset = preset;
            Report(Tune.ApplyPreset(preset));
            Reload();
        }

        /// <summary>硬件与游戏目录只做体检,不写任何设置。</summary>
        private void OpenAudit()
        {
            SysSample s = Host == null || Host.Mon == null ? null : Host.Mon.Current;
            List<SessionRecord> all = Host == null || Host.Sessions == null ? null : Host.Sessions.All;
            AuditDialog.Show(this, s, all);
        }

        /// <summary>说明行:推荐状态 + 现状影响,需要重启的再补一句。</summary>
        private static string ComposeNote(HealthItem it)
        {
            string text;
            if (it.Rec.Length > 0 && it.Note.Length > 0) text = it.Rec + "  ·  " + it.Note;
            else text = it.Rec.Length > 0 ? it.Rec : it.Note;
            return it.NeedReboot ? text + "  ·  " + I18n.T("health.reboot") : text;
        }

        /// <summary>关闭内存完整性前必须让用户确认,这不是普通画质开关。</summary>
        private bool ConfirmOff(HealthItem it)
        {
            if (it.Id != "vbs" || !it.On) return true;
            return MessageBox.Show(this, I18n.T("health.vbs.warn"), I18n.T("health.vbs.warn.title"),
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
        }

        private void Report(TuneResult r)
        {
            if (r.Ok)
            {
                if (r.NeedAdmin) Host.Toast(I18n.T("health.toast.partial", r.Applied));
                else if (r.Applied == 0) Host.Toast(I18n.T("health.toast.same"));
                else if (r.NeedReboot) Host.Toast(I18n.T("health.toast.reboot", r.Applied));
                else Host.Toast(I18n.T("health.toast.applied", r.Applied));
            }
            else if (r.NeedAdmin)
            {
                Host.Toast(I18n.T("health.toast.adminapply"));
            }
            else
            {
                Host.Toast(I18n.T("health.toast.applyfail"));
            }
        }

        private static Color LevelColor(HealthLevel l)
        {
            switch (l)
            {
                case HealthLevel.Ok: return Theme.Sub;
                case HealthLevel.Warn: return Theme.Warn;
                case HealthLevel.Bad: return Theme.Danger;
            }
            return Theme.Sub;
        }

        private void DoAction(int i)
        {
            if (i >= items.Count) return;
            if (items[i].Action == "admin") MemEngine.RestartElevated("");
        }

        /// <summary>滚动视口已经铺满整页,这一层不再重复铺底。</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutConsole();
        }

        private static int SummaryHeight(int width)
        {
            return width < Ui.Px(520) ? Ui.Px(150) : Ui.Px(112);
        }

        private static int RowHeight(int cardWidth)
        {
            int rowW = cardWidth - Ui.Px(40);
            return rowW < Ui.Px(440) ? Ui.Px(68) : Ui.Px(42);
        }

        private static int ListHeight(int cardWidth)
        {
            // 上下各留 20/16:右侧卡没有标题行,不需要再空出 56 像素。
            return Ui.Px(36) + Rows * RowHeight(cardWidth);
        }

        /// <summary>宽屏左右工作区,窄屏上下排列;低高度由页面纵向滚动承接。</summary>
        private void LayoutConsole()
        {
            if (Width <= 0 || Height <= 0 || surface == null) return;

            int w = Math.Max(Ui.Px(1), scrollHost.ClientRectangle.Width);
            int viewportH = Math.Max(Ui.Px(1), scrollHost.ClientRectangle.Height);
            int gap = Ui.Px(16);
            int summaryH = SummaryHeight(w);
            int y = summaryH + gap;
            bool stacked = w < Ui.Px(820);
            int contentH;

            if (stacked)
            {
                int listH = ListHeight(w);
                int topologyH = Math.Max(Ui.Px(250),
                    topology.PreferredHeight(Math.Max(Ui.Px(1), w - Ui.Px(40))) + Ui.Px(90));

                summaryCard.SetBounds(0, 0, w, summaryH);
                topologyCard.SetBounds(0, y, w, topologyH);
                int listY = y + topologyH + gap;
                listCard.SetBounds(0, listY, w, listH);
                contentH = listY + listH;
            }
            else
            {
                int leftW = Math.Max(Ui.Px(320), Math.Min(Ui.Px(400), (int)(w * 0.38f)));
                int rightX = leftW + gap;
                int rightW = Math.Max(Ui.Px(1), w - rightX);
                int mainH = Math.Max(ListHeight(rightW),
                    topology.PreferredHeight(leftW - Ui.Px(40)) + Ui.Px(90));
                mainH = Math.Max(mainH, viewportH - y);

                summaryCard.SetBounds(0, 0, w, summaryH);
                topologyCard.SetBounds(0, y, leftW, mainH);
                listCard.SetBounds(rightX, y, rightW, mainH);
                contentH = y + mainH;
            }

            LayoutSummary(w, summaryH);
            LayoutTopology();
            LayoutRows();

            contentH = Math.Max(contentH, viewportH);
            Point scroll = scrollHost.AutoScrollPosition;
            surface.SetBounds(-scroll.X, -scroll.Y, w, contentH);
            scrollHost.AutoScrollMinSize = new Size(0, contentH);
        }

        private void LayoutSummary(int w, int h)
        {
            int pad = Ui.Px(24);
            int innerW = Math.Max(Ui.Px(1), w - pad * 2);
            int gap = Ui.Px(8);
            int bh = Ui.Px(34);
            int dotW = Ui.Px(7);
            bool narrow = h > Ui.Px(112);

            if (narrow)
            {
                head.SetBounds(pad, Ui.Px(18), innerW, Ui.Px(26));
                summaryLbl.Align = ContentAlignment.MiddleLeft;
                summaryLbl.SetBounds(pad, Ui.Px(46), Math.Max(Ui.Px(1), innerW - dotW - gap), Ui.Px(18));
                summaryDot.SetBounds(w - pad - dotW, Ui.Px(51), dotW, dotW);
                sub.SetBounds(pad, Ui.Px(66), innerW, Ui.Px(18));

                int colBtnW = Math.Max(Ui.Px(1), (innerW - gap * 2) / 3);
                int by = Ui.Px(96);
                btnGame.SetBounds(pad, by, colBtnW, bh);
                btnDaily.SetBounds(pad + colBtnW + gap, by, colBtnW, bh);
                btnAudit.SetBounds(pad + (colBtnW + gap) * 2, by, innerW - colBtnW * 2 - gap * 2, bh);
                return;
            }

            int headW = Math.Min(Ui.Px(220), Math.Max(Ui.Px(140), innerW / 3));
            head.SetBounds(pad, Ui.Px(20), headW, Ui.Px(26));
            int summaryX = pad + headW + Ui.Px(12);
            int summaryW = Math.Max(Ui.Px(1), w - pad - dotW - gap - summaryX);
            summaryLbl.Align = ContentAlignment.MiddleRight;
            summaryLbl.SetBounds(summaryX, Ui.Px(20), summaryW, Ui.Px(26));
            summaryDot.SetBounds(w - pad - dotW, Ui.Px(29), dotW, dotW);

            int bw = Ui.Px(w < 700 ? 104 : 116);
            int buttonsW = bw * 3 + gap * 2;
            int bx = w - pad - buttonsW;
            int by2 = Ui.Px(56);
            int subW = Math.Max(Ui.Px(80), bx - pad - Ui.Px(14));
            sub.SetBounds(pad, by2, subW, bh);
            btnAudit.SetBounds(bx, by2, bw, bh);
            btnDaily.SetBounds(bx + bw + gap, by2, bw, bh);
            btnGame.SetBounds(bx + (bw + gap) * 2, by2, bw, bh);
        }

        private void LayoutTopology()
        {
            int pad = Ui.Px(24);
            int w = Math.Max(Ui.Px(1), topologyCard.Width - pad * 2);
            coreHead.SetBounds(pad, Ui.Px(20), w, Ui.Px(22));
            coreSub.SetBounds(pad, Ui.Px(44), w, Ui.Px(18));
            topology.SetBounds(pad, Ui.Px(70), w,
                Math.Max(Ui.Px(80), topologyCard.Height - Ui.Px(90)));
        }

        /// <summary>结果行保持 36px 节奏;极窄行把控制区移到第二行,不叠字。</summary>
        private void LayoutRows()
        {
            if (listCard == null || listCard.Width <= 0) return;

            int pad = Ui.Px(24);
            int rowW = Math.Max(Ui.Px(1), listCard.Width - pad * 2);
            int top = Ui.Px(20);
            int rowH = RowHeight(listCard.Width);
            bool compact = rowH > Ui.Px(50);
            int dotW = Ui.Px(6);
            int titleX = pad + dotW + Ui.Px(10);
            int ctrlH = compact ? Ui.Px(32) : Ui.Px(34);
            int ctrlW = compact
                ? Math.Min(Ui.Px(196), Math.Max(Ui.Px(144), rowW / 2))
                : Math.Min(Ui.Px(196), Math.Max(Ui.Px(176), rowW / 3));
            int ctrlX = pad + rowW - ctrlW;
            int valueW = Math.Min(Ui.Px(132), Math.Max(Ui.Px(76), rowW / 5));

            for (int i = 0; i < Rows; i++)
            {
                int y = top + rowH * i;

                if (compact)
                {
                    int valueRight = pad + rowW;
                    int valueLeft = valueRight - valueW;
                    titles[i].SetBounds(titleX, y, Math.Max(Ui.Px(40), valueLeft - titleX - Ui.Px(8)), Ui.Px(18));
                    values[i].SetBounds(valueLeft, y, valueW, Ui.Px(18));
                    notes[i].SetBounds(titleX, y + Ui.Px(18),
                        Math.Max(Ui.Px(40), valueRight - titleX), Ui.Px(16));
                    dots[i].SetBounds(pad, y + Ui.Px(6), dotW, dotW);

                    int cy = y + Ui.Px(34);
                    int tgW = Ui.Px(56);
                    int actW = Math.Min(Ui.Px(132), ctrlW);
                    toggles[i].SetBounds(ctrlX + (ctrlW - tgW) / 2, cy, tgW, ctrlH);
                    choices[i].SetBounds(ctrlX, cy, ctrlW, ctrlH);
                    acts[i].SetBounds(ctrlX + ctrlW - actW, cy, actW, ctrlH);
                }
                else
                {
                    // 开关行只占 76 像素,把省下的宽度让给说明文字,避免文案被过早截断。
                    int rowCtrlW = 0;
                    if (i < items.Count)
                    {
                        if (items[i].CanChoose) rowCtrlW = ctrlW;
                        else if (items[i].CanToggle) rowCtrlW = Ui.Px(76);
                        else if (items[i].Action.Length > 0) rowCtrlW = Math.Min(Ui.Px(132), ctrlW);
                    }
                    int rowCtrlX = pad + rowW - rowCtrlW;
                    bool rowHasCtrl = rowCtrlW > 0;
                    bool showValue = i >= items.Count || (!items[i].CanToggle && !items[i].CanChoose);
                    int valueRight = rowHasCtrl ? rowCtrlX - Ui.Px(12) : pad + rowW;
                    int valueW2 = showValue ? valueW : 0;
                    if (showValue && !rowHasCtrl && i < items.Count)
                    {
                        int textNeed = TextRenderer.MeasureText(values[i].Text ?? "",
                            Theme.F(values[i].Size, values[i].Style)).Width + Ui.Px(8);
                        valueW2 = Math.Min(Math.Max(valueW, textNeed), Math.Max(valueW, (int)(rowW * 0.48f)));
                    }
                    int valueLeft = valueRight - valueW2;

                    // 标题与说明合并成一行:标题按实际宽度占位,说明接在后面单行省略。
                    int cy = y + (rowH - ctrlH) / 2;
                    int textY = y + (rowH - Ui.Px(18)) / 2;
                    int textRight = valueRight - Ui.Px(8);
                    int titleW = TextRenderer.MeasureText(titles[i].Text ?? "",
                        Theme.F(10.2f, FontStyle.Regular)).Width + Ui.Px(2);
                    titleW = Math.Min(titleW, Math.Max(Ui.Px(60), textRight - titleX));
                    int noteX = titleX + titleW + Ui.Px(10);

                    dots[i].SetBounds(pad, y + (rowH - dotW) / 2, dotW, dotW);
                    titles[i].SetBounds(titleX, textY, titleW, Ui.Px(18));
                    values[i].SetBounds(valueLeft, textY, valueW2, Ui.Px(18));
                    notes[i].SetBounds(noteX, textY + Ui.Px(1),
                        Math.Max(Ui.Px(20), textRight - noteX), Ui.Px(16));

                    int tgW = Ui.Px(56);
                    int actW = Math.Min(Ui.Px(132), rowCtrlW);
                    toggles[i].SetBounds(rowCtrlX + (rowCtrlW - tgW) / 2, cy, tgW, ctrlH);
                    choices[i].SetBounds(rowCtrlX, cy, rowCtrlW, ctrlH);
                    acts[i].SetBounds(rowCtrlX + rowCtrlW - actW, cy, actW, ctrlH);
                }

                if (i < lines.Length)
                    lines[i].SetBounds(pad, y + rowH - Ui.Px(1), rowW, Ui.Px(1));
            }
        }

        /// <summary>只读物理核心图:P/E、SMT 与 LLC 编号来自系统拓扑,不引入监测写操作。</summary>
        private sealed class CoreTopologyView : Ctl
        {
            private CpuCore[] cores = new CpuCore[0];

            public void SetCores(CpuCore[] value)
            {
                cores = value ?? new CpuCore[0];
                Invalidate();
            }

            private int Columns(int width)
            {
                if (cores.Length == 0) return 1;
                // 16 个核心在 4 列下每格接近正方形，比 5 列挤满一行更容易扫读。
                int n = Math.Max(2, Math.Min(6, width / Math.Max(1, Ui.Px(76))));
                return Math.Max(1, Math.Min(cores.Length, n));
            }

            public int PreferredHeight(int width)
            {
                if (cores.Length == 0) return Ui.Px(120);
                int columns = Columns(width);
                int rows = (cores.Length + columns - 1) / columns;
                return Math.Max(Ui.Px(120), rows * Ui.Px(76) + (rows - 1) * Ui.Px(12));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Prep(g);
                if (Width <= 0 || Height <= 0) return;
                if (cores.Length == 0)
                {
                    TextRenderer.DrawText(g, "—", Theme.F(10f, FontStyle.Regular), ClientRectangle,
                        Theme.Faint, Ui.Flags(ContentAlignment.MiddleCenter, false));
                    return;
                }

                int columns = Columns(Width);
                int rows = (cores.Length + columns - 1) / columns;
                int gap = Ui.Px(12);
                float cellW = (Width - gap * (columns - 1)) / (float)columns;
                float cellH = Math.Min(Ui.Fp(76), (Height - gap * (rows - 1)) / (float)rows);
                cellH = Math.Max(Ui.Fp(40), cellH);
                float totalH = rows * cellH + (rows - 1) * gap;
                float startY = Math.Max(0f, (Height - totalH) / 2f);

                for (int i = 0; i < cores.Length; i++)
                {
                    CpuCore core = cores[i];
                    int col = i % columns;
                    int row = i / columns;
                    var r = new RectangleF(col * (cellW + gap), startY + row * (cellH + gap), cellW, cellH);
                    Color signal = core.Parked ? Theme.Faint : (core.Efficient ? Theme.Sub : Theme.Accent);
                    Color fill = Theme.Mix(Theme.CardAlt, signal, Theme.Dark ? 0.10 : 0.055);
                    Ui.Fill(g, r, Ui.Fp(4), fill);
                    Ui.Stroke(g, r, Ui.Fp(4), Theme.Alpha(Theme.Border, Theme.Dark ? 150 : 200), 1f);
                    Ui.Fill(g, new RectangleF(r.X, r.Y + Ui.Fp(10), Ui.Fp(2), r.Height - Ui.Fp(20)), Ui.Fp(1),
                        Theme.Alpha(signal, core.Parked ? 70 : 190));

                    string id = (core.Efficient ? "E" : "P") + core.Index.ToString("00");
                    var idRect = Rectangle.Round(new RectangleF(r.X + Ui.Fp(11), r.Y + Ui.Fp(8),
                        Math.Max(1f, r.Width - Ui.Fp(18)), Ui.Fp(22)));
                    TextRenderer.DrawText(g, id, Theme.F(9.6f, FontStyle.Bold), idRect,
                        core.Parked ? Theme.Faint : Theme.Text,
                        Ui.Flags(ContentAlignment.MiddleLeft, false) | TextFormatFlags.EndEllipsis);

                    string meta = core.LlcKnown ? "C" + core.Llc
                        : (core.Smt ? "SMT" : "CPU");
                    var metaRect = Rectangle.Round(new RectangleF(r.X + Ui.Px(11), r.Bottom - Ui.Px(26),
                        Math.Max(1f, r.Width - Ui.Px(18)), Ui.Px(16)));
                    TextRenderer.DrawText(g, meta, Theme.F(8f, FontStyle.Regular), metaRect,
                        Theme.Faint, Ui.Flags(ContentAlignment.MiddleLeft, false) | TextFormatFlags.EndEllipsis);
                }
            }
        }

        /// <summary>小号直角分段选择,保留原选择事件,避免控制台出现大胶囊。</summary>
        private sealed class ConsoleSegmented : Segmented
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Prep(g);
                float gap = Ui.Fp(2);
                var all = new RectangleF(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
                Ui.Fill(g, all, Ui.Fp(4), Theme.Track);
                if (Items.Length == 0 || Width <= 0 || Height <= 0) return;

                float itemW = (Width - gap * (Items.Length + 1)) / Items.Length;
                for (int i = 0; i < Items.Length; i++)
                {
                    var r = new RectangleF(gap + i * (itemW + gap), gap, itemW, Height - gap * 2);
                    bool selected = i == Selected;
                    if (selected) Ui.Fill(g, r, Ui.Fp(3), Theme.Accent);
                    TextRenderer.DrawText(g, Items[i], Theme.F(8.5f, selected ? FontStyle.Bold : FontStyle.Regular),
                        Rectangle.Round(r), selected ? Color.White : Theme.Sub,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }
        }
    }

    /// <summary>状态小圆点:绿=好,黄=可优化,红=有问题,灰=未检测到。</summary>
    internal sealed class StatusDot : Ctl
    {
        private Color dot = Theme.Sub;

        public StatusDot()
        {
            BackColor = Theme.Card;
        }

        public Color Dot
        {
            get { return dot; }
            set
            {
                if (dot == value) return;
                dot = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            int d = Math.Min(Width, Height);
            using (var b = new SolidBrush(dot))
                g.FillEllipse(b, (Width - d) / 2f, (Height - d) / 2f, d, d);
        }
    }

    /// <summary>滚动视口:透明底,自己铺页面底纹,不再用 BackColor 盖住整页背景。</summary>
    internal sealed class ScrollHost : Panel
    {
        public ScrollHost()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.PaintBg(e.Graphics, ClientRectangle, Ui.Origin(this));
        }
    }

    /// <summary>滚动内容层:底纹按窗口坐标固定,滚动时只有内容移动,卡片圆角外仍然无缝。</summary>
    internal sealed class Surface : Ctl
    {
        private readonly ScrollableControl host;

        public Surface(ScrollableControl h)
        {
            host = h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Hints(e.Graphics);
            Point o = Ui.Origin(this);
            // AutoScroll 会把内容层的 Top 变成负值,这里把滚动量加回去,让底纹固定。
            o.X -= host.HorizontalScroll.Value;
            o.Y -= host.VerticalScroll.Value;
            Theme.PaintBg(e.Graphics, ClientRectangle, o);
        }
    }

    /// <summary>结果行之间的细分隔线。</summary>
    internal sealed class Hairline : Ctl
    {
        public Hairline()
        {
            BackColor = Theme.Card;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            using (var b = new SolidBrush(Theme.Alpha(Theme.Border, Theme.Dark ? 125 : 180)))
                g.FillRectangle(b, 0, 0, Width, Height);
        }
    }
}
