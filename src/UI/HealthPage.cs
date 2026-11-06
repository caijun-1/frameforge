using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge.UI
{
    /// <summary>
    /// 系统体检页:只读检查会影响帧率的关键设置,逐项说明,并把能直接处理的地方做成按钮。
    /// </summary>
    public class HealthPage : Page
    {
        private readonly Card card;
        private readonly TextLabel head, sub;
        private readonly FlatButton btnRecheck, btnFix;
        private readonly ScoreRing score;
        private readonly Hairline[] lines;
        private readonly Hairline foot;
        private readonly StatusDot[] dots = new StatusDot[6];
        private readonly TextLabel[] titles = new TextLabel[6];
        private readonly TextLabel[] values = new TextLabel[6];
        private readonly TextLabel[] notes = new TextLabel[6];
        private readonly FlatButton[] acts = new FlatButton[6];

        private List<HealthItem> items = new List<HealthItem>();
        private bool checking;

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

            for (int i = 0; i < titles.Length; i++)
            {
                var d = new StatusDot();
                dots[i] = d;
                card.Controls.Add(d);

                var t = new TextLabel();
                t.Size = 9.6f;
                titles[i] = t;
                card.Controls.Add(t);

                var v = new TextLabel();
                v.Size = 9f;
                v.Align = ContentAlignment.MiddleRight;
                v.Ellipsis = true;
                values[i] = v;
                card.Controls.Add(v);

                var n = new TextLabel();
                n.Size = 8.3f;
                n.ForeColor = Theme.Faint;
                n.Ellipsis = true;
                notes[i] = n;
                card.Controls.Add(n);

                var b = new FlatButton();
                b.FontPt = 8.6f;
                b.Primary = false;
                b.Visible = false;
                int idx = i;
                b.Click += delegate { DoAction(idx); };
                acts[i] = b;
                card.Controls.Add(b);
            }

            btnRecheck = new FlatButton();
            btnRecheck.FontPt = 9f;
            btnRecheck.Text = I18n.T("health.recheck");
            btnRecheck.Click += delegate { Reload(); };
            card.Controls.Add(btnRecheck);

            btnFix = new FlatButton();
            btnFix.FontPt = 9f;
            btnFix.Primary = true;
            btnFix.Text = I18n.T("health.fix");
            btnFix.Click += delegate { FixAll(); };
            card.Controls.Add(btnFix);

            // 右上角的健康度环:一眼看出这台机器现在几分。
            score = new ScoreRing();
            card.Controls.Add(score);

            // 行与行之间的细分隔线,让六项检查看起来是一条条独立的记录。
            lines = new Hairline[titles.Length - 1];
            for (int i = 0; i < lines.Length; i++)
            {
                var ln = new Hairline();
                lines[i] = ln;
                card.Controls.Add(ln);
            }

            // 底部操作条上方也压一条线,把列表和按钮分成两个区域。
            foot = new Hairline();
            card.Controls.Add(foot);

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

            for (int i = 0; i < titles.Length; i++)
            {
                bool has = i < items.Count;
                titles[i].Visible = has;
                values[i].Visible = has;
                notes[i].Visible = has;
                dots[i].Visible = has;
                acts[i].Visible = has && items[i].Action.Length > 0;
                if (!has) continue;

                HealthItem it = items[i];
                titles[i].Text = I18n.T(it.Key);
                values[i].Text = it.Value;
                values[i].ForeColor = LevelColor(it.Level);
                notes[i].Text = it.Note;
                notes[i].ForeColor = it.Level == HealthLevel.Bad ? Theme.Danger
                    : it.Level == HealthLevel.Warn ? Theme.Warn : Theme.Faint;
                dots[i].Dot = LevelColor(it.Level);
                SetAction(i, it);
            }

            int ok = 0;
            for (int i = 0; i < items.Count; i++)
                if (items[i].Level == HealthLevel.Ok) ok++;

            for (int i = 0; i < lines.Length; i++)
                lines[i].Visible = items.Count > i + 1;

            score.Visible = items.Count > 0;
            score.Value = items.Count > 0 ? (int)Math.Round(ok * 100.0 / items.Count) : 0;
        }

        private void SetAction(int i, HealthItem it)
        {
            if (it.Action.Length == 0) return;
            acts[i].Text = it.ActKey.Length > 0 ? I18n.T(it.ActKey) : I18n.T("health.act.open");
            acts[i].Primary = it.Action == "admin" || it.Action == "power";
        }

        /// <summary>
        /// 一键处理:只代劳能安全自动完成的项(高性能电源计划),
        /// 其余需要手动确认的项仍然由每行右侧按钮打开,避免替用户乱改系统。
        /// </summary>
        private void FixAll()
        {
            int fixedCount = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Action != "power" || items[i].Level == HealthLevel.Ok) continue;
                if (BoostEngine.SwitchToHighPerformance()) fixedCount++;
            }

            Reload();

            int todo = 0;
            for (int i = 0; i < items.Count; i++)
                if (items[i].Level == HealthLevel.Warn || items[i].Level == HealthLevel.Bad) todo++;

            if (fixedCount > 0) Host.Toast(I18n.T("health.toast.fixed", fixedCount));
            else if (todo == 0) Host.Toast(I18n.T("health.toast.alldone"));
            else if (!MemEngine.IsAdmin) Host.Toast(I18n.T("health.toast.admin"));
            else Host.Toast(I18n.T("health.toast.manual", todo));
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
            HealthItem it = items[i];
            if (it.Action == "admin")
            {
                MemEngine.RestartElevated("");
                return;
            }
            if (it.Action == "power")
            {
                bool ok = BoostEngine.SwitchToHighPerformance();
                Host.Toast(I18n.T(ok ? "health.toast.power" : "health.toast.powerfail"));
                Reload();
                return;
            }
            if (it.Action.StartsWith("url:", StringComparison.Ordinal))
            {
                try { Process.Start(it.Action.Substring(4)); }
                catch (Exception ex) { Log.Write("health open: " + ex.Message); }
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;

            int pad = Ui.Px(20);
            card.SetBounds(0, 0, Width, Height);

            int w = Width - pad * 2;

            // 右上角健康度环:标题和说明让开它,不压字。
            int ring = Ui.Px(62);
            score.SetBounds(Width - pad - ring, Ui.Px(14), ring, ring);

            int headW = Math.Max(Ui.Px(120), w - ring - Ui.Px(14));
            head.SetBounds(pad, Ui.Px(16), headW, Ui.Px(26));
            sub.SetBounds(pad, Ui.Px(45), headW, Ui.Px(36));

            int top = Ui.Px(92);
            int bottom = Ui.Px(76);
            int rowH = Math.Max(Ui.Px(52), (Height - top - bottom) / titles.Length);

            int dotW = Ui.Px(10);
            int btnW = Ui.Px(96), btnH = Ui.Px(28);
            int btnX = pad + w - btnW;

            // 数值右对齐到操作按钮左侧,标题列吃满剩下的宽度,长名字不会被截。
            int valueW = Math.Min(Ui.Px(168), Math.Max(Ui.Px(80), w / 3));
            int valueX = btnX - Ui.Px(14) - valueW;
            int titleX = pad + dotW + Ui.Px(9);
            int titleW = Math.Max(Ui.Px(120), valueX - titleX - Ui.Px(10));

            for (int i = 0; i < titles.Length; i++)
            {
                int y = top + rowH * i;
                // 行高随窗口拉伸,文字上下居中才不会把空白全堆在标题下面。
                int off = Math.Max(0, (rowH - Ui.Px(41)) / 2);
                dots[i].SetBounds(pad, y + off + (Ui.Px(20) - dotW) / 2, dotW, dotW);
                titles[i].SetBounds(titleX, y + off, titleW, Ui.Px(20));
                values[i].SetBounds(valueX, y + off, valueW, Ui.Px(20));
                notes[i].SetBounds(titleX, y + off + Ui.Px(21),
                    Math.Max(Ui.Px(120), btnX - titleX - Ui.Px(8)), Ui.Px(18));
                acts[i].SetBounds(btnX, y + (rowH - btnH) / 2, btnW, btnH);

                if (i < lines.Length) lines[i].SetBounds(pad, y + rowH - Ui.Px(1), w, Ui.Px(1));
            }

            // 底部操作条:左边一键优化,右边重新检测,中间留白。
            int by = Height - pad - Ui.Px(34);
            foot.SetBounds(pad, by - Ui.Px(12), w, Ui.Px(1));
            btnFix.SetBounds(pad, by, Ui.Px(128), Ui.Px(34));
            btnRecheck.SetBounds(pad + w - Ui.Px(116), by, Ui.Px(116), Ui.Px(34));
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

    /// <summary>健康度环:灰色轨道 + 彩色进度弧,中间显示百分比。</summary>
    internal sealed class ScoreRing : Ctl
    {
        private int value;

        public ScoreRing()
        {
            BackColor = Theme.Card;
        }

        public int Value
        {
            get { return value; }
            set
            {
                if (this.value == value) return;
                this.value = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);

            float thick = Math.Max(3f, Ui.Fp(5));
            float inset = thick / 2f + Ui.Fp(1);
            var r = new RectangleF(inset, inset, Width - inset * 2f, Height - inset * 2f);
            if (r.Width <= 0 || r.Height <= 0) return;

            using (var pen = new Pen(Theme.Track, thick))
                g.DrawEllipse(pen, r);

            if (value > 0)
            {
                Color c = value >= 100 ? Theme.Accent : (value >= 60 ? Theme.Warn : Theme.Danger);
                float sweep = Math.Min(359.5f, 360f * value / 100f);
                using (var pen = new Pen(c, thick))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, r, -90f, sweep);
                }
            }

            TextRenderer.DrawText(g, value + "%", Theme.Num(11.5f), ClientRectangle, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
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