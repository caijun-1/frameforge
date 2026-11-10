using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge.UI
{
    /// <summary>
    /// 系统优化页:逐项显示会影响帧率的设置,能改的直接给开关或分段选择,
    /// 每项带一句游戏时的推荐状态,顶部两个预设一键套用。
    /// </summary>
    public class HealthPage : Page
    {
        private const int Rows = 10;

        private readonly Card card;
        private readonly TextLabel head, sub;
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
            card = new Card();
            card.BackColor = Theme.Card;

            head = new TextLabel();
            head.Size = 12f;
            head.Style = FontStyle.Bold;
            head.Text = I18n.T("health.title");
            card.Controls.Add(head);

            sub = new TextLabel();
            sub.Size = 8.6f;
            sub.ForeColor = Theme.Sub;
            sub.Wrap = true;
            sub.Text = I18n.T("health.sub");
            card.Controls.Add(sub);

            for (int i = 0; i < Rows; i++)
            {
                var d = new StatusDot();
                dots[i] = d;
                card.Controls.Add(d);

                var t = new TextLabel();
                t.Size = 9.8f;
                titles[i] = t;
                card.Controls.Add(t);

                var v = new TextLabel();
                v.Size = 9f;
                v.Align = ContentAlignment.MiddleRight;
                v.Ellipsis = true;
                values[i] = v;
                card.Controls.Add(v);

                var n = new TextLabel();
                n.Size = 8.5f;
                n.ForeColor = Theme.Faint;
                n.Ellipsis = true;
                notes[i] = n;
                card.Controls.Add(n);

                var tg = new Toggle();
                tg.Visible = false;
                int ti = i;
                tg.CheckedChanged += delegate { if (!syncing) ApplyToggle(ti, tg.Checked); };
                toggles[i] = tg;
                card.Controls.Add(tg);

                var sg = new Segmented();
                sg.Visible = false;
                int si = i;
                sg.Changed += delegate { if (!syncing) ApplyChoice(si, sg.Selected); };
                choices[i] = sg;
                card.Controls.Add(sg);

                var b = new FlatButton();
                b.FontPt = 8.6f;
                b.Primary = false;
                b.Visible = false;
                int bi = i;
                b.Click += delegate { DoAction(bi); };
                acts[i] = b;
                card.Controls.Add(b);
            }

            btnGame = new FlatButton();
            btnGame.FontPt = 8.8f;
            btnGame.Primary = false;
            btnGame.Text = I18n.T("health.preset.game");
            btnGame.Click += delegate { ApplyPreset("game"); };
            card.Controls.Add(btnGame);

            btnDaily = new FlatButton();
            btnDaily.FontPt = 8.8f;
            btnDaily.Primary = false;
            btnDaily.Text = I18n.T("health.preset.daily");
            btnDaily.Click += delegate { ApplyPreset("daily"); };
            card.Controls.Add(btnDaily);

            btnAudit = new FlatButton();
            btnAudit.FontPt = 8.8f;
            btnAudit.Primary = false;
            btnAudit.Ghost = true;
            btnAudit.Text = I18n.T("health.audit");
            btnAudit.Click += delegate { OpenAudit(); };
            card.Controls.Add(btnAudit);

            // 行与行之间的细分隔线,让每一项设置看起来是一条条独立的记录。
            lines = new Hairline[Rows - 1];
            for (int i = 0; i < lines.Length; i++)
            {
                var ln = new Hairline();
                lines[i] = ln;
                card.Controls.Add(ln);
            }

            Controls.Add(card);
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
                    values[i].Visible = has;
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
                    notes[i].ForeColor = it.Level == HealthLevel.Bad ? Theme.Danger
                        : it.Level == HealthLevel.Warn ? Theme.Warn : Theme.Faint;
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

            LayoutRows();
            Invalidate();
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
            string text = it.Rec.Length > 0 ? it.Rec + "  ·  " + it.Note : it.Note;
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
                case HealthLevel.Ok: return Theme.Accent;
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

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;

            int pad = Ui.Px(20);
            card.SetBounds(0, 0, Width, Height);

            int w = Width - pad * 2;

            // 顶部右侧两个预设按钮 + 体检入口,标题和说明让开它们,不压字。
            int pbw = Ui.Px(104), pbh = Ui.Px(30), pgap = Ui.Px(8);
            int pbx = pad + w - pbw;
            btnGame.SetBounds(pbx, Ui.Px(14), pbw, pbh);
            btnDaily.SetBounds(pbx - pbw - pgap, Ui.Px(14), pbw, pbh);
            btnAudit.SetBounds(pbx - (pbw + pgap) * 2, Ui.Px(14), pbw, pbh);

            int headW = Math.Max(Ui.Px(120), w - pbw * 3 - pgap * 2 - Ui.Px(16));
            head.SetBounds(pad, Ui.Px(16), headW, Ui.Px(26));
            sub.SetBounds(pad, Ui.Px(45), headW, Ui.Px(36));

            LayoutRows();
        }

        /// <summary>逐行摆位。没有右侧控件的行(如显卡),数值和说明可以一直用到卡片右边,长名字不会被省略。</summary>
        private void LayoutRows()
        {
            if (Width <= 0 || Height <= 0) return;

            int pad = Ui.Px(20);
            int w = Width - pad * 2;
            int top = Ui.Px(92);
            int bottom = pad;
            // 行高下限 43:十行加上顶部标题在最小窗口高度里也刚好排得下。
            // 标题 20 + 说明 18 的节奏仍保留,只是行间不再硬留 46 的余量。
            int rowH = Math.Max(Ui.Px(43), (Height - top - bottom) / Rows);

            int dotW = Ui.Px(10);
            // 右侧留一块固定宽度的控件区,开关居中、分段选择吃满,数值列才能对齐。
            int ctrlW = Ui.Px(160), ctrlH = Ui.Px(30);
            int ctrlX = pad + w - ctrlW;

            int valueW = Math.Min(Ui.Px(168), Math.Max(Ui.Px(80), w / 4));
            int valueX = ctrlX - Ui.Px(14) - valueW;
            int titleX = pad + dotW + Ui.Px(9);
            int titleW = Math.Max(Ui.Px(120), valueX - titleX - Ui.Px(10));

            for (int i = 0; i < Rows; i++)
            {
                int y = top + rowH * i;
                // 行高随窗口拉伸,文字上下居中才不会把空白全堆在标题下面。
                int off = Math.Max(0, (rowH - Ui.Px(41)) / 2);

                bool hasCtrl = true;
                if (i < items.Count)
                    hasCtrl = items[i].CanToggle || items[i].CanChoose || items[i].Action.Length > 0;
                // 数值右边缘所有行统一对齐;没有右侧控件时按文字实际宽度向左让出空间,显卡全名不会被省略。
                int valueRight = valueX + valueW;
                int valueW2 = valueW;
                int valueLeft = valueX;
                if (!hasCtrl)
                {
                    int titleNeed = TextRenderer.MeasureText(titles[i].Text ?? "",
                        Theme.F(titles[i].Size, titles[i].Style)).Width + Ui.Px(12);
                    int textNeed = TextRenderer.MeasureText(values[i].Text ?? "",
                        Theme.F(values[i].Size, values[i].Style)).Width + Ui.Px(6);
                    int avail = Math.Max(Ui.Px(80), valueRight - titleX - titleNeed);
                    valueW2 = Math.Min(Math.Max(valueW, textNeed), avail);
                    valueLeft = valueRight - valueW2;
                }

                dots[i].SetBounds(pad, y + off + (Ui.Px(20) - dotW) / 2, dotW, dotW);
                titles[i].SetBounds(titleX, y + off, titleW, Ui.Px(20));
                values[i].SetBounds(valueLeft, y + off, valueW2, Ui.Px(20));
                notes[i].SetBounds(titleX, y + off + Ui.Px(21),
                    Math.Max(Ui.Px(120), valueRight - titleX - Ui.Px(8)), Ui.Px(18));

                int cy = y + (rowH - ctrlH) / 2;
                int tgW = Ui.Px(56);
                toggles[i].SetBounds(ctrlX + (ctrlW - tgW) / 2, cy, tgW, ctrlH);
                choices[i].SetBounds(ctrlX, cy, ctrlW, ctrlH);
                acts[i].SetBounds(ctrlX + (ctrlW - Ui.Px(96)) / 2, cy + Ui.Px(1), Ui.Px(96), Ui.Px(28));

                if (i < lines.Length) lines[i].SetBounds(pad, y + rowH - Ui.Px(1), w, Ui.Px(1));
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

    /// <summary>行与行之间的 1px 分隔线。</summary>
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
            using (var b = new SolidBrush(Theme.Border))
                g.FillRectangle(b, 0, 0, Width, Height);
        }
    }
}