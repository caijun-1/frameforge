using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge.UI
{
    /// <summary>页面基类:统一背景与每秒刷新入口。</summary>
    public class Page : Ctl
    {
        protected readonly MainForm Host;

        public Page(MainForm host)
        {
            Host = host;
            BackColor = Theme.Bg;
        }

        /// <summary>页面底铺一层极淡的纵向渐变,卡片浮在上面才有影棚布光的柔和感。</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width <= 0 || Height <= 0) return;
            Graphics g = e.Graphics;
            Color from = Theme.BgTop;
            Color to = Theme.BgBottom;
            using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(ClientRectangle, from, to,
                       System.Drawing.Drawing2D.LinearGradientMode.Vertical))
                g.FillRectangle(b, ClientRectangle);
        }

        public virtual void Refresh(SysSample s) { }
    }


// =====================================================================
    //  加速页:三档模式 + 实时监测
    // =====================================================================
    public class BoostPage : Page
    {
        private const int OptCount = 8;

        private readonly HeroCard hero;
        private readonly Card cpuCard, gpuCard, radarCard, ramCard, stripCard, modeCard;
        private readonly TextLabel stateLbl, gameLbl, gameSub, hint;
        private readonly TabBar modes;
        private readonly FlatButton mainBtn, trimBtn, editBtn;
        // “游戏启动自动优化”的快捷开关,直接摆在一键加速旁边,不用再进设置页。
        private readonly Toggle tgAuto;
        private readonly TextLabel autoLbl;

        private readonly RingGauge cpuRing, gpuRing;
        private readonly RadarChart radar;
        private readonly ArcGauge ramArc;
        private readonly InfoStrip strip;
        private readonly ChipStrip chips;

        private readonly ToolTip tips;

        private string lastGame = "";
        private string lastSub = "";
        private bool lastBoosted;
        private int modeIndex;

        public BoostPage(MainForm host) : base(host)
        {
            hero = new HeroCard();
            // 卡片色要和子标签的继承底色一致,否则标签会在卡片上铺出一个个灰块。
            hero.BackColor = Theme.Card;
            cpuCard = NewCard();
            gpuCard = NewCard();
            radarCard = NewCard();
            ramCard = NewCard();
            stripCard = NewCard();
            modeCard = NewCard();

            modeIndex = CurrentMode();

            stateLbl = new TextLabel();
            stateLbl.Size = 9f;
            stateLbl.Style = FontStyle.Bold;
            stateLbl.ForeColor = Theme.Sub;
            stateLbl.Text = I18n.T("boost.state.idle");

            gameLbl = new TextLabel();
            gameLbl.Size = 20f;
            gameLbl.Style = FontStyle.Bold;
            gameLbl.Text = I18n.T("boost.nogame");
            gameLbl.Ellipsis = true;

            gameSub = new TextLabel();
            gameSub.Size = 9f;
            gameSub.ForeColor = Theme.Sub;
            gameSub.Ellipsis = true;

            // 结果行平时不占位:没有加速结果就收起,说明性文字改由悬停气泡承担。
            hint = new TextLabel();
            hint.Size = 8.5f;
            hint.ForeColor = Theme.Text;
            hint.Wrap = true;
            hint.Align = ContentAlignment.MiddleCenter;
            hint.Visible = false;

            // 状态胶囊:一眼看出当前模式打开了哪几项(亮起的才是生效项)。
            chips = new ChipStrip();
            chips.Labels = new string[]
            {
                I18n.T("boost.chip.priority"), I18n.T("boost.chip.power"),
                I18n.T("boost.chip.timer"), I18n.T("boost.chip.dvr")
            };
            chips.On = new bool[4];

            modes = new TabBar();
            modes.Items = new string[]
            {
                I18n.T("boost.tab.balanced"), I18n.T("boost.tab.beast"), I18n.T("boost.tab.custom")
            };
            modes.SetSilent(modeIndex);
            modes.Changed += OnModeChanged;

            mainBtn = new FlatButton();
            mainBtn.Text = I18n.T("boost.apply");
            mainBtn.FontPt = 11.5f;
            mainBtn.Click += delegate { Host.ApplyBoost(); };

            trimBtn = new FlatButton();
            trimBtn.Primary = false;
            trimBtn.FontPt = 8.5f;
            trimBtn.Text = I18n.T("boost.mem");
            trimBtn.Click += delegate { Host.RunTrim(); };

            tgAuto = new Toggle();
            tgAuto.SetSilent(Host.St.AutoBoost);
            tgAuto.CheckedChanged += delegate
            {
                Host.St.AutoBoost = tgAuto.Checked;
                Host.SaveSettingsSoon();
            };

            // 只给开关配一个短标签,不然光看一个圆点不知道它管什么。
            autoLbl = new TextLabel();
            autoLbl.Size = 8.5f;
            autoLbl.ForeColor = Theme.Sub;
            autoLbl.Align = ContentAlignment.MiddleRight;
            autoLbl.Ellipsis = true;
            autoLbl.Text = I18n.T("boost.auto");

            cpuRing = new RingGauge();
            cpuRing.Caption = I18n.T("mon.cpu");
            cpuRing.Ticks = true;

            gpuRing = new RingGauge();
            gpuRing.Caption = I18n.T("mon.gpu");
            gpuRing.Ticks = true;

            radar = new RadarChart();
            radar.Labels = new string[]
            {
                I18n.T("mon.cpu"), I18n.T("mon.gpu"), I18n.T("mon.ram"),
                I18n.T("mon.fps"), I18n.T("mon.temp")
            };

            ramArc = new ArcGauge();
            ramArc.Caption = I18n.T("mon.ram");

            strip = new InfoStrip();
            strip.Caps = new string[]
            {
                I18n.T("mon.temp"), I18n.T("mon.clock"), I18n.T("mon.power"), I18n.T("mon.vram")
            };
            strip.Vals = new string[] { "—", "—", "—", "—" };

            editBtn = new FlatButton();
            editBtn.Primary = false;
            editBtn.FontPt = 8f;
            editBtn.Text = I18n.T("boost.opt.edit");
            editBtn.Click += delegate { OnEditOptions(); };

            // 长说明收进悬停气泡,卡片上只留最短的一句话。
            tips = new ToolTip();
            tips.AutoPopDelay = 8000;
            tips.InitialDelay = 420;
            tips.ReshowDelay = 120;
            tips.SetToolTip(trimBtn, I18n.T("boost.mem.sub"));
            tips.SetToolTip(tgAuto, I18n.T("boost.autoboost.sub"));
            tips.SetToolTip(editBtn, I18n.T("boost.opt.edit"));

            hero.Controls.Add(stateLbl);
            hero.Controls.Add(gameLbl);
            hero.Controls.Add(gameSub);
            hero.Controls.Add(hint);
            hero.Controls.Add(chips);
            hero.Controls.Add(mainBtn);
            hero.Controls.Add(trimBtn);
            hero.Controls.Add(tgAuto);
            hero.Controls.Add(autoLbl);

            cpuCard.Controls.Add(cpuRing);
            gpuCard.Controls.Add(gpuRing);
            radarCard.Controls.Add(radar);
            ramCard.Controls.Add(ramArc);
            stripCard.Controls.Add(strip);
            // 先加编辑按钮:同一容器里先加入的控件 z 序更高,后加的模式条不会把它盖住。
            modeCard.Controls.Add(editBtn);
            modeCard.Controls.Add(modes);

            Controls.Add(hero);
            Controls.Add(cpuCard);
            Controls.Add(gpuCard);
            Controls.Add(radarCard);
            Controls.Add(ramCard);
            Controls.Add(stripCard);
            Controls.Add(modeCard);

            SyncState();
            ApplyAccent();
            RefreshModeText();
            LayoutCards();
            SyncEditButton();
        }

        // ---------------- 模式 ----------------

        /// <summary>读设置里的加速模式:0 均衡 / 1 野兽 / 2 自定义。</summary>
        private int CurrentMode()
        {
            string m = Host.St.BoostMode;
            if (m == "beast") return 1;
            if (m == "custom") return 2;
            return 0;
        }

        private void OnModeChanged(object sender, EventArgs e)
        {
            int idx = modes.Selected;
            if (idx < 0 || idx > 2 || idx == modeIndex) return;
            modeIndex = idx;
            Host.St.ApplyMode(idx == 1 ? "beast" : (idx == 2 ? "custom" : "balanced"));
            Host.SaveSettingsSoon();

            ApplyAccent();
            RefreshModeText();
            SyncChips();
            SyncState();
            SyncEditButton();
            // 切模式只改下一次加速的选项,已在跑的优化不会被中断。
            Host.Toast(I18n.T("boost.mode.reboost"));
        }

        /// <summary>
        /// 编辑选项只对自定义模式有意义:其他模式下直接隐藏,
        /// 既点不到,模式条上也不会多出一块无用的东西。
        /// </summary>
        private void SyncEditButton()
        {
            // 只切显隐,位置由 LayoutCards 固定算好,切模式时按钮不会挪动。
            editBtn.Visible = modeIndex == 2;
        }

        /// <summary>把强调色同步到本页所有自绘控件(均衡蓝 / 野兽红 / 自定义紫)。</summary>
        private void ApplyAccent()
        {
            // 全局配色只能由这里触发,否则 Theme.SetMode 永远不会被调用。
            Host.ApplyModeTheme(modeIndex);
            hero.Accent = Theme.Accent;
            chips.Accent = Theme.Accent;
            modes.AccentColor = Theme.Accent;
            mainBtn.AccentColor = Theme.Accent;
            editBtn.AccentColor = Theme.Accent;
            cpuRing.AccentColor = Theme.Accent;
            gpuRing.AccentColor = Theme.Accent;
            radar.AccentColor = Theme.Accent;
            ramArc.AccentColor = Theme.Accent;
            stateLbl.ForeColor = lastBoosted ? Theme.Accent : Theme.Sub;
            Invalidate(true);
        }

        /// <summary>悬停说明跟着当前模式走。</summary>
        private void RefreshModeText()
        {
            string key = modeIndex == 1 ? "boost.mode.beast.desc"
                : (modeIndex == 2 ? "boost.mode.custom.desc" : "boost.mode.balanced.desc");
            tips.SetToolTip(modeCard, I18n.T(key));
            tips.SetToolTip(mainBtn, I18n.T("boost.hint.short"));
            tips.SetToolTip(modeCard, I18n.T(key));
        }

        // ---------------- 状态 ----------------

        private void SyncChips()
        {
            BoostPlan p = Host.St.Plan();
            chips.On[0] = p.Priority;
            chips.On[1] = p.PowerPlan;
            chips.On[2] = p.Timer;
            chips.On[3] = p.Dvr;
            chips.Invalidate();
        }

        public void SetBusy(bool busy)
        {
            mainBtn.Busy = busy;
            mainBtn.Text = busy ? I18n.T("boost.working")
                : (Host.Boost != null && Host.Boost.Active ? I18n.T("boost.restore") : I18n.T("boost.apply"));
            mainBtn.Enabled = true;
        }

        public void SetTrimBusy(bool busy) { trimBtn.Busy = busy; }

        /// <summary>把本次加速的实际结果写进 Hero 的说明行;空串则收起该行。</summary>
        public void SetResult(string text)
        {
            hint.Text = text ?? "";
            hint.ForeColor = Theme.Text;
            hint.Visible = hint.Text.Length > 0;
            LayoutCards();
            Invalidate();
        }

        /// <summary>按引擎状态刷新按钮文案与状态行。</summary>
        public void SyncState()
        {
            bool active = Host.Boost != null && Host.Boost.Active;
            lastBoosted = active;
            stateLbl.Text = active ? I18n.T("boost.state.active") : I18n.T("boost.state.idle");
            stateLbl.ForeColor = active ? Theme.Accent : Theme.Sub;
            mainBtn.Text = active ? I18n.T("boost.restore") : I18n.T("boost.apply");
            SyncChips();
            Invalidate();
        }

        private void OnEditOptions()
        {
            BoostPlan p = Host.St.Plan();
            bool[] v = new bool[]
            {
                p.Priority, p.PowerPlan, p.Timer, p.Dvr,
                p.Memory, p.Background, p.Affinity, p.Irq
            };
            bool[] r = BoostDialog.Edit(FindForm(), v);
            if (r == null) return;

            Host.St.ApplyCustom(r);
            Host.SaveSettingsSoon();

            // 手动挑完选项就落到自定义模式,配色同步转紫。
            modeIndex = 2;
            modes.SetSilent(2);
            ApplyAccent();
            RefreshModeText();
            SyncChips();
            Host.Toast(I18n.T("boost.mode.reboost"));
        }

        // ---------------- 每秒刷新 ----------------

        public override void Refresh(SysSample s)
        {
            if (s == null) return;

            bool active = Host.Boost != null && Host.Boost.Active;
            if (active != lastBoosted) { lastBoosted = active; SyncState(); }

            // 优先用识别出来的游戏名,识别不到再退回进程名,绝不留空。
            string det = s.GameTitle.Length > 0 ? s.GameTitle : s.GameName;
            string game = "";
            if (s.GameRecent) game = det;
            if (game.Length == 0 && active && Host.Boost.GameName.Length > 0) game = Host.Boost.GameName;
            if (game.Length == 0 && s.GameRecent) game = I18n.T("mon.game.generic");

            string sub;
            if (game.Length == 0) sub = I18n.T("boost.nogame.sub");
            else if (active) sub = I18n.T("boost.since", Since()) + (s.GameName.Length > 0 ? "  ·  " + s.GameName : "");
            else if (s.GameName.Length > 0) sub = s.GameName;
            else if (!MemEngine.IsAdmin) sub = I18n.T("boost.hint.admin");
            else sub = I18n.T("boost.hint.short");

            if (game != lastGame || sub != lastSub)
            {
                lastGame = game;
                lastSub = sub;
                gameLbl.Text = game.Length > 0 ? game : I18n.T("boost.nogame");
                gameLbl.ForeColor = game.Length > 0 ? Theme.Text : Theme.Sub;
                gameSub.Text = sub;
            }

            string threads = Environment.ProcessorCount.ToString() + (I18n.IsEn ? " threads" : " 线程");
            cpuRing.SetValue(s.Cpu < 0 ? 0 : s.Cpu / 100.0, s.Cpu < 0 ? I18n.T("mon.none") : threads);

            string gpuSub = I18n.T("mon.none");
            if (s.GpuTemp >= 0) gpuSub = s.GpuTemp.ToString("0") + "°C";
            else if (s.GpuName.Length > 0) gpuSub = Shorten(s.GpuName, 18);
            gpuRing.SetValue(s.Gpu < 0 ? 0 : s.Gpu / 100.0, gpuSub);

            double fps = Host.Fps != null ? Host.Fps.Fps : -1;

            radar.SetValues(new double[]
            {
                N01(s.Cpu / 100.0),
                N01(s.Gpu / 100.0),
                N01(s.RamPct / 100.0),
                fps > 0 ? Math.Min(1.0, fps / 240.0) : 0,
                s.GpuTemp > 0 ? Math.Min(1.0, s.GpuTemp / 100.0) : 0
            });

            ramArc.SetValue(s.RamPct < 0 ? 0 : s.RamPct / 100.0, Fmt.Pair(s.RamUsed, s.RamTotal));

            strip.SetValues(new string[]
            {
                s.GpuTemp >= 0 ? s.GpuTemp.ToString("0") + "°C" : "—",
                s.GpuClock >= 0 ? s.GpuClock.ToString("0") + " MHz" : "—",
                s.GpuPower >= 0 ? s.GpuPower.ToString("0") + " W" : "—",
                s.VramTotal > 0
                    ? (s.VramUsed / 1073741824.0).ToString("0.0") + " / " +
                      (s.VramTotal / 1073741824.0).ToString("0.0") + " GB"
                    : "—"
            });
        }

        private static double N01(double v)
        {
            if (v < 0) return 0;
            return v > 1 ? 1 : v;
        }

        private string Since()
        {
            if (Host.Boost == null || Host.Boost.StartedAt == DateTime.MinValue) return "0:00";
            TimeSpan t = DateTime.Now - Host.Boost.StartedAt;
            if (t.TotalSeconds < 0) t = TimeSpan.Zero;
            return ((int)t.TotalMinutes).ToString() + ":" + t.Seconds.ToString("00");
        }

        private static string Shorten(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        // ---------------- 布局 ----------------

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCards();
        }

        private void LayoutCards()
        {
            if (Width <= 0 || Height <= 0) return;
            int pad = Ui.Px(20);
            int gap = Ui.Px(12);
            int heroH = Math.Max(Ui.Px(132), Math.Min(Ui.Px(150), Height / 5));
            int stripH = Ui.Px(46);
            int modeH = Ui.Px(56);
            int bodyW = Width - pad * 2;
            int y = pad;

            hero.SetBounds(pad, y, bodyW, heroH);
            y += heroH + gap;

            int dashH = Math.Max(Ui.Px(200), Height - y - stripH - modeH - gap * 3 - pad);
            int gaugeW = (int)(bodyW * 0.30f);
            int cw = bodyW - gaugeW * 2 - gap * 2;

            cpuCard.SetBounds(pad, y, gaugeW, dashH);
            gpuCard.SetBounds(pad + bodyW - gaugeW, y, gaugeW, dashH);

            int cx = pad + gaugeW + gap;
            // 中列上头是雷达图,其余高度全部留给内存弧表。
            int radarH = (int)(dashH * 0.57f);
            radarCard.SetBounds(cx, y, cw, radarH);

            int bottomY = y + radarH + gap;
            int bottomH = dashH - radarH - gap;
            ramCard.SetBounds(cx, bottomY, cw, bottomH);

            y += dashH + gap;
            stripCard.SetBounds(pad, y, bodyW, stripH);
            y += stripH + gap;
            modeCard.SetBounds(pad, y, bodyW, modeH);

            // Hero 内部:左列文字,右侧是一键加速组合。
            int hp = Ui.Px(20);
            int btnW = Math.Max(Ui.Px(210), Math.Min(Ui.Px(270), bodyW / 3));
            int btnX = bodyW - hp - btnW;
            int textW = Math.Max(Ui.Px(180), btnX - hp - Ui.Px(14));

            stateLbl.SetBounds(hp, Ui.Px(12), Ui.Px(120), Ui.Px(18));
            gameLbl.SetBounds(hp, Ui.Px(30), textW, Ui.Px(32));
            gameSub.SetBounds(hp, Ui.Px(62), textW, Ui.Px(16));
            // 状态胶囊移到标题下方:四种生效项一眼看全,也不再和"未加速"挤在同一行。
            chips.SetBounds(hp, Ui.Px(84), textW, Ui.Px(22));

            strip.SetBounds(Ui.Px(16), 0, bodyW - Ui.Px(32), stripH);
            // 右列:一键加速 + "自动优化"文字标签和开关并排,内存整理在下方同宽铺满。
            int btnH = Ui.Px(48);
            int trimH = Ui.Px(28);
            int tgW = Ui.Px(54);
            int lblW = Ui.Px(60);
            int btnBlockH = btnH + Ui.Px(8) + trimH;
            int by0 = Math.Max(Ui.Px(8), (heroH - btnBlockH) / 2);
            mainBtn.SetBounds(btnX, by0, btnW - tgW - lblW - Ui.Px(8), btnH);
            autoLbl.SetBounds(btnX + btnW - tgW - lblW, by0, lblW, btnH);
            tgAuto.SetBounds(btnX + btnW - tgW, by0, tgW, btnH);
            trimBtn.SetBounds(btnX, by0 + btnH + Ui.Px(8), btnW, trimH);
            // 释放结果贴在内存在理按钮下方,不再和状态胶囊抢左列的位置。
            int hintY = by0 + btnH + Ui.Px(8) + trimH + Ui.Px(4);
            hint.SetBounds(btnX, hintY, btnW, Math.Max(Ui.Px(14), heroH - hintY - Ui.Px(6)));

            // 两侧大圆表(刻度盘已包含标题)
            cpuRing.SetBounds(Ui.Px(4), Ui.Px(6), gaugeW - Ui.Px(8), dashH - Ui.Px(12));
            gpuRing.SetBounds(Ui.Px(4), Ui.Px(6), gaugeW - Ui.Px(8), dashH - Ui.Px(12));

            radar.SetBounds(Ui.Px(8), Ui.Px(6), cw - Ui.Px(16), radarH - Ui.Px(12));
            // 弧表按 5px 内缩:再靠外的话,控件方角会顶到卡片圆角外,露出一个白直角。
            ramArc.SetBounds(Ui.Px(5), Ui.Px(5), cw - Ui.Px(10), Math.Max(Ui.Px(90), bottomH - Ui.Px(10)));

            // 底部模式条:三种模式三等分铺满整张卡,野兽正好居中;编辑选项压在“自定义”块右端。
            int mp = Ui.Px(16);
            // 小一点:22 高的胶囊,只占自定义块右端一小段。
            int ebW = Ui.Px(64);
            int ebH = Ui.Px(22);
            int ebRight = Ui.Px(12);
            int modesW = bodyW - mp * 2;
            modes.SetBounds(mp, (modeH - Ui.Px(44)) / 2, modesW, Ui.Px(44));
            editBtn.SetBounds(mp + modesW - ebRight - ebW, (modeH - ebH) / 2, ebW, ebH);
            // 预留位在布局里一次算定,不随模式显隐变化:切到自定义时文字不会左右跳。
            modes.LastItemInset = ebRight + ebW + Ui.Px(8);
        }

        private static Card NewCard()
        {
            var c = new Card();
            // 卡片自身用卡片色:子控件继承同一个 BackColor,自绘标签铺底时才不会打出方块。
            c.BackColor = Theme.Card;
            return c;
        }

        /// <summary>Hero 里的状态胶囊:亮起的是当前模式真正生效的优化项。</summary>
        private sealed class ChipStrip : Ctl
        {
            public string[] Labels = new string[0];
            public bool[] On = new bool[0];
            public Color Accent = Color.Empty;

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Prep(g);
                if (Labels.Length == 0 || Width <= 0) return;

                Color acc = Accent.A == 0 ? Theme.Accent : Accent;
                float h = Math.Min(Height, Ui.Fp(22));
                float y = (Height - h) / 2f;
                float dot = Ui.Fp(5.5f);
                float gap = Ui.Fp(7);
                float x = 0;
                Font f = Theme.F(8f, FontStyle.Regular);

                for (int i = 0; i < Labels.Length; i++)
                {
                    bool on = i < On.Length && On[i];
                    System.Drawing.Size tsz = TextRenderer.MeasureText(Labels[i], f);
                    float w = tsz.Width + dot + Ui.Fp(22);
                    if (x + w > Width) break;

                    var r = new RectangleF(x, y, w, h);
                    // 只留下极淡的一层底,不再描边:四个胶囊排在一起时边框会互相抢眼。
                    Ui.Fill(g, r, h / 2f, on ? Theme.Alpha(acc, Theme.Dark ? 36 : 16)
                                               : Theme.Alpha(Theme.Track, Theme.Dark ? 100 : 70));

                    using (var b = new SolidBrush(on ? acc : Theme.Faint))
                    {
                        float dy = r.Y + (h - dot) / 2f;
                        g.FillEllipse(b, r.X + Ui.Fp(8), dy, dot, dot);
                        if (on)
                            using (var halo = new SolidBrush(Theme.Alpha(acc, Theme.Dark ? 54 : 34)))
                                g.FillEllipse(halo, r.X + Ui.Fp(8) - dot * 0.8f, dy - dot * 0.8f, dot * 2.6f, dot * 2.6f);
                    }

                    TextRenderer.DrawText(g, Labels[i], f,
                        Rectangle.Round(new RectangleF(r.X + Ui.Fp(8) + dot + Ui.Fp(6), r.Y, r.Width - (Ui.Fp(8) + dot + Ui.Fp(6)) - Ui.Fp(8), h)),
                        on ? Theme.Text : Theme.Faint,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

                    x += w + gap;
                }
            }
        }

        /// <summary>顶部主卡:左侧一条强调色,野兽模式下整条变红。</summary>
        private sealed class HeroCard : Card
        {
            public Color Accent = Color.Empty;

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                Color a = Accent.A == 0 ? Theme.Accent : Accent;

                float barW = Math.Max(2f, Ui.Fp(3));
                var bar = new RectangleF(Ui.Fp(1), Ui.Fp(14), barW, Math.Max(4f, Height - Ui.Fp(28)));
                // 左侧强调条:野兽模式下整条变红,是这张卡唯一的强色,其余留白更干净。
                using (GraphicsPath p = Ui.Round(bar, barW / 2f))
                using (var lg = new LinearGradientBrush(new RectangleF(bar.X, bar.Y, bar.Width, bar.Height),
                           a, Theme.Alpha(a, 26), LinearGradientMode.Vertical))
                    g.FillPath(lg, p);
            }
        }

        /// <summary>显卡传感器条:四列小标题 + 数值。</summary>
        private sealed class InfoStrip : Ctl
        {
            public string[] Caps = new string[0];
            public string[] Vals = new string[0];

            public void SetValues(string[] v)
            {
                Vals = v;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Prep(g);
                if (Caps.Length == 0) return;

                float w = (float)Width / Caps.Length;
                for (int i = 0; i < Caps.Length; i++)
                {
                    var capR = new Rectangle((int)(i * w), 0, (int)w, (int)(Height * 0.44f));
                    var valR = new Rectangle((int)(i * w), (int)(Height * 0.40f), (int)w, (int)(Height * 0.60f));
                    TextRenderer.DrawText(g, Caps[i], Theme.F(8f, FontStyle.Regular), capR, Theme.Faint,
                        Ui.Flags(ContentAlignment.MiddleCenter, false));
                    TextRenderer.DrawText(g, Vals.Length > i ? Vals[i] : "—", Theme.Num(11f), valR, Theme.Accent,
                        Ui.Flags(ContentAlignment.MiddleCenter, false));

                    if (i > 0)
                    {
                        int lx = (int)(i * w);
                        using (var pen = new Pen(Theme.Alpha(Theme.Border, Theme.Dark ? 170 : 220), 1f))
                            g.DrawLine(pen, lx, Height * 0.2f, lx, Height * 0.8f);
                    }
                }
            }
        }
    }

    // =====================================================================
    //  叠加层页
    // =====================================================================
    public class OverlayPage : Page
    {
        private readonly Card ovCard, itemCard, hintCard, prevCard;
        private readonly TextLabel ovTitle, onTitle, onSub, lockTitle, lockSub, hotkey;
        private readonly TextLabel styleTitle, itemTitle, itemNote, hintTitle, prevTitle;
        private readonly Toggle onToggle, lockToggle;
        private readonly Segmented style;
        private readonly Slider scale;
        private readonly OverlayPreview preview;
        private readonly Chip[] chips = new Chip[8];

        private static readonly string[] ItemKeys = { "fps", "low", "cpu", "gpu", "temp", "ram", "clock", "session" };

        public OverlayPage(MainForm host) : base(host)
        {
            ovCard = NewCard();
            itemCard = NewCard();
            hintCard = NewCard();
            prevCard = NewCard();

            ovTitle = Header(I18n.T("ov.title"));
            onTitle = RowTitle(I18n.T("ov.on"));
            onSub = RowSub(I18n.T("ov.on.sub"));

            onToggle = new Toggle();
            onToggle.SetSilent(host.St.OverlayOn);
            onToggle.CheckedChanged += delegate
            {
                if (Host.St.OverlayOn == onToggle.Checked) return;
                Host.ToggleOverlay();
            };

            styleTitle = RowTitle(I18n.T("ov.style"));
            style = new Segmented();
            style.Items = new string[] { I18n.T("ov.style.h"), I18n.T("ov.style.v"), I18n.T("ov.style.mini") };
            style.SetSilent(StyleIndex(host.St.OverlayStyle));
            style.Changed += delegate
            {
                string key = StyleKey(style.Selected);
                bool changed = Host.St.OverlayStyle != key;
                Host.St.OverlayStyle = key;
                // 每一种样式的默认摆位不同(横向居中 / 竖向靠左 / 简洁居中),
                // 换样式就把上次拖动保存的坐标清掉,让它按新样式重新摆放。
                if (changed) { Host.St.OverlayX = -1; Host.St.OverlayY = -1; }
                Host.St.Save();
                if (Host.Ov != null) Host.Ov.ApplySettings();
                preview.Invalidate();
            };

            scale = new Slider();
            scale.Min = 70;
            scale.Max = 200;
            scale.Unit = "%";
            scale.Label = I18n.T("ov.scale");
            scale.SetSilent((int)Math.Round(host.St.OverlayScale * 100));
            scale.Changed += delegate
            {
                Host.St.OverlayScale = scale.Value / 100.0;
                Host.SaveSettingsSoon();
                if (Host.Ov != null) Host.Ov.ApplySettings();
                preview.Invalidate();
            };

            lockTitle = RowTitle(I18n.T("ov.lock"));
            lockSub = RowSub(I18n.T("ov.lock.sub"));
            lockToggle = new Toggle();
            lockToggle.SetSilent(host.St.OverlayLocked);
            lockToggle.CheckedChanged += delegate
            {
                Host.St.OverlayLocked = lockToggle.Checked;
                Host.St.Save();
                if (Host.Ov != null) Host.Ov.ApplySettings();
            };

            hotkey = new TextLabel();
            hotkey.Size = 8.5f;
            hotkey.ForeColor = Theme.Faint;
            hotkey.Wrap = true;
            hotkey.Align = ContentAlignment.TopLeft;
            hotkey.Text = I18n.T("ov.hotkey");

            ovCard.Controls.Add(ovTitle);
            ovCard.Controls.Add(onTitle);
            ovCard.Controls.Add(onSub);
            ovCard.Controls.Add(onToggle);
            ovCard.Controls.Add(styleTitle);
            ovCard.Controls.Add(style);
            ovCard.Controls.Add(scale);
            ovCard.Controls.Add(lockTitle);
            ovCard.Controls.Add(lockSub);
            ovCard.Controls.Add(lockToggle);

            hintTitle = Header(I18n.T("ov.notes"));
            hintCard.Controls.Add(hintTitle);
            hintCard.Controls.Add(hotkey);

            itemTitle = Header(I18n.T("ov.items"));
            itemCard.Controls.Add(itemTitle);

            for (int i = 0; i < chips.Length; i++)
            {
                chips[i] = new Chip();
                chips[i].Text = I18n.T("ov.item." + ItemKeys[i]);
                chips[i].Selected = ItemValue(host.St, i);
                int idx = i;
                chips[i].Click += delegate
                {
                    chips[idx].Selected = !chips[idx].Selected;
                    chips[idx].Invalidate();
                    SetItem(Host.St, idx, chips[idx].Selected);
                    Host.St.Save();
                    if (Host.Ov != null) Host.Ov.ApplySettings();
                    preview.Invalidate();
                };
                itemCard.Controls.Add(chips[i]);
            }

            itemNote = new TextLabel();
            itemNote.Size = 8.5f;
            itemNote.ForeColor = Theme.Faint;
            itemNote.Wrap = true;
            itemNote.Align = ContentAlignment.TopLeft;
            itemNote.Text = I18n.T("ov.hint");
            hintCard.Controls.Add(itemNote);

            prevTitle = Header(I18n.T("ov.preview"));
            preview = new OverlayPreview(host.St);
            preview.BackColor = Theme.Card;
            prevCard.Controls.Add(prevTitle);
            prevCard.Controls.Add(preview);

            Controls.Add(ovCard);
            Controls.Add(itemCard);
            Controls.Add(hintCard);
            Controls.Add(prevCard);

            LayoutCards();
        }

        private static Card NewCard()
        {
            var c = new Card();
            // 卡片自身用卡片色:子控件继承同一个 BackColor,
            // 自绘标签铺底时才不会在卡片上打出背景色的方块。
            c.BackColor = Theme.Card;
            return c;
        }

        private static TextLabel Header(string text)
        {
            var l = new TextLabel();
            l.Size = 9.5f;
            l.Style = FontStyle.Bold;
            l.ForeColor = Theme.Sub;
            l.Text = text;
            return l;
        }

        private static TextLabel RowTitle(string text)
        {
            var l = new TextLabel();
            l.Size = 10f;
            l.Text = text;
            return l;
        }

        private static TextLabel RowSub(string text)
        {
            var l = new TextLabel();
            l.Size = 8.5f;
            l.ForeColor = Theme.Sub;
            l.Ellipsis = true;
            l.Text = text;
            return l;
        }

        private static int StyleIndex(string s)
        {
            if (s == "v") return 1;
            if (s == "mini") return 2;
            return 0;
        }

        private static string StyleKey(int i)
        {
            if (i == 1) return "v";
            if (i == 2) return "mini";
            return "h";
        }

        private static bool ItemValue(Settings st, int i)
        {
            switch (i)
            {
                case 0: return st.ShowFps;
                case 1: return st.ShowLow;
                case 2: return st.ShowCpu;
                case 3: return st.ShowGpu;
                case 4: return st.ShowTemp;
                case 5: return st.ShowRam;
                case 6: return st.ShowClock;
                default: return st.ShowSession;
            }
        }

        private static void SetItem(Settings st, int i, bool v)
        {
            switch (i)
            {
                case 0: st.ShowFps = v; break;
                case 1: st.ShowLow = v; break;
                case 2: st.ShowCpu = v; break;
                case 3: st.ShowGpu = v; break;
                case 4: st.ShowTemp = v; break;
                case 5: st.ShowRam = v; break;
                case 6: st.ShowClock = v; break;
                default: st.ShowSession = v; break;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCards();
        }

        private void LayoutCards()
        {
            if (Width <= 0 || Height <= 0) return;
            int gap = Ui.Px(12);
            int itemH = Ui.Px(116);
            // 说明卡只有三行字,留 146 就够;多出来的高度让给上面的设置卡和预览。
            int hintH = Ui.Px(146);
            int cardH = Height - itemH - hintH - gap * 2;
            if (cardH < Ui.Px(272))
            {
                cardH = Ui.Px(272);
                hintH = Math.Max(Ui.Px(140), Height - itemH - cardH - gap * 2);
            }

            // 顶部一行拆成两栏:左边是设置,右边是效果预览。
            int leftW = (int)(Width * 0.58);
            int rightW = Width - leftW - gap;
            if (rightW < Ui.Px(240))
            {
                // 窗口太窄时把预览收起来,优先保证设置项不被挤压。
                leftW = Width;
                rightW = 0;
            }
            ovCard.SetBounds(0, 0, leftW, cardH);
            // 注意:页面隐藏时 Control.Visible 的读值恒为 false(它看的是整条父链),
            // 这里必须用局部变量判断,否则预览卡永远排不到位置。
            bool showPrev = rightW >= Ui.Px(240);
            prevCard.Visible = showPrev;
            if (showPrev) prevCard.SetBounds(leftW + gap, 0, rightW, cardH);
            itemCard.SetBounds(0, cardH + gap, Width, itemH);
            hintCard.SetBounds(0, cardH + itemH + gap * 2, Width, hintH);

            int pad = Ui.Px(18);
            int right = ovCard.Width - pad;
            int toggleW = Ui.Px(48);

            prevTitle.SetBounds(pad, Ui.Px(12), prevCard.Width - pad * 2, Ui.Px(20));
            preview.SetBounds(pad, Ui.Px(38), prevCard.Width - pad * 2, Math.Max(Ui.Px(120), cardH - Ui.Px(56)));

            ovTitle.SetBounds(pad, Ui.Px(12), ovCard.Width - pad * 2, Ui.Px(20));

            // 卡片多出来的高度均摊进三段行距:行与行自然拉开,
            // 而不是在标题下方留一块空、底部再留一块空。
            int extra = Math.Max(0, cardH - Ui.Px(266));
            int top = extra / 5;
            int step = (extra - top) / 3;
            int off1 = top, off2 = top + step, off3 = top + step * 2, off4 = top + step * 3;

            int leftText = ovCard.Width - pad * 2 - toggleW - Ui.Px(12);
            onTitle.SetBounds(pad, Ui.Px(50) + off1, leftText, Ui.Px(20));
            onSub.SetBounds(pad, Ui.Px(70) + off1, leftText, Ui.Px(18));
            onToggle.SetBounds(right - toggleW, Ui.Px(52) + off1, toggleW, Ui.Px(24));

            int segW = Math.Min(Ui.Px(260), ovCard.Width - pad * 2 - Ui.Px(96));
            if (segW < Ui.Px(170)) segW = Ui.Px(170);
            styleTitle.SetBounds(pad, Ui.Px(108) + off2, Ui.Px(90), Ui.Px(28));
            style.SetBounds(right - segW, Ui.Px(104) + off2, segW, Ui.Px(32));

            scale.SetBounds(pad, Ui.Px(152) + off3, ovCard.Width - pad * 2, Ui.Px(44));

            lockTitle.SetBounds(pad, Ui.Px(214) + off4, leftText, Ui.Px(20));
            lockSub.SetBounds(pad, Ui.Px(234) + off4, leftText, Ui.Px(18));
            lockToggle.SetBounds(right - toggleW, Ui.Px(216) + off4, toggleW, Ui.Px(24));

            hintTitle.SetBounds(pad, Ui.Px(12), Width - pad * 2, Ui.Px(20));
            hotkey.SetBounds(pad, Ui.Px(46), Width - pad * 2, Ui.Px(40));
            itemNote.SetBounds(pad, Ui.Px(92), Width - pad * 2, Math.Max(Ui.Px(30), hintH - Ui.Px(104)));

            itemTitle.SetBounds(pad, Ui.Px(12), Width - pad * 2, Ui.Px(20));
            int n = chips.Length;
            int perRow = 4;
            int rows = (n + perRow - 1) / perRow;
            int cw = (Width - pad * 2 - gap * (perRow - 1)) / perRow;
            int chipH = Ui.Px(30);
            int blockH = rows * chipH + (rows - 1) * Ui.Px(6);
            int chipY = (itemH - blockH) / 2 + Ui.Px(10);
            for (int i = 0; i < n; i++)
            {
                int col = i % perRow, row = i / perRow;
                chips[i].SetBounds(pad + col * (cw + gap), chipY + row * (chipH + Ui.Px(6)), cw, chipH);
            }
        }

        /// <summary>
        /// 叠加层效果预览:画一块假想游戏画面,再按当前样式/缩放/显示项
        /// 把叠加层的排版复刻上去,不用进游戏也能看到实际效果。
        /// </summary>
        private sealed class OverlayPreview : Ctl
        {
            private readonly Settings st;

            // 假数据:数值长度覆盖 2~5 个字符,用来检验换行/挤位问题。
            private static readonly string[] Samples = { "144", "112", "38%", "76%", "64°", "9.8G", "21:40", "32:08" };

            public OverlayPreview(Settings s)
            {
                st = s;
                BackColor = Theme.Card;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Prep(g);
                if (Width < 80 || Height < 60) return;

                var r = new RectangleF(0, 0, Width - 1, Height - 1);
                float rad = Ui.Fp(9);
                using (GraphicsPath p = Ui.Round(r, rad))
                {
                    g.SetClip(p);
                    // 假想游戏画面:冷色天空 + 远山剪影 + 地面 + 一团柔光。
                    // 柔光和远山都是为了检验白字在亮背景上是否还看得清。
                    using (var b = new LinearGradientBrush(new RectangleF(0, 0, Width, Height),
                               Color.FromArgb(255, 26, 36, 52), Color.FromArgb(255, 74, 84, 96),
                               LinearGradientMode.Vertical))
                        g.FillRectangle(b, r);

                    // 柔光:用路径渐变从中心向外淡出,比实心圆更像光。
                    float sunR = Math.Min(Width, Height) * 0.30f;
                    float sunX = Width * 0.74f, sunY = Height * 0.30f;
                    using (GraphicsPath sun = new GraphicsPath())
                    {
                        sun.AddEllipse(sunX - sunR, sunY - sunR, sunR * 2, sunR * 2);
                        using (var pg = new PathGradientBrush(sun))
                        {
                            pg.CenterColor = Color.FromArgb(150, 255, 242, 214);
                            pg.SurroundColors = new Color[] { Color.FromArgb(0, 255, 242, 214) };
                            g.FillPath(pg, sun);
                        }
                    }

                    // 远山与近山两层,层差拉开才有纵深。
                    using (var b = new SolidBrush(Color.FromArgb(255, 61, 73, 89)))
                        g.FillPolygon(b, new PointF[]
                        {
                            new PointF(0, Height * 0.62f), new PointF(Width * 0.30f, Height * 0.34f),
                            new PointF(Width * 0.58f, Height * 0.60f), new PointF(Width * 0.80f, Height * 0.42f),
                            new PointF(Width, Height * 0.58f), new PointF(Width, Height), new PointF(0, Height)
                        });
                    using (var b = new SolidBrush(Color.FromArgb(255, 30, 38, 50)))
                        g.FillPolygon(b, new PointF[]
                        {
                            new PointF(0, Height * 0.80f), new PointF(Width * 0.22f, Height * 0.62f),
                            new PointF(Width * 0.52f, Height * 0.80f), new PointF(Width * 0.74f, Height * 0.66f),
                            new PointF(Width, Height * 0.82f), new PointF(Width, Height), new PointF(0, Height)
                        });
                    using (var b = new SolidBrush(Color.FromArgb(255, 17, 22, 30)))
                        g.FillRectangle(b, 0, Height * 0.88f, Width, Height * 0.12f);

                    // 顶部压一层淡黑:游戏内叠字通常在偏暗的画面上,这里还原那种对比度。
                    using (var b = new LinearGradientBrush(new RectangleF(0, 0, Width, Height * 0.42f),
                               Color.FromArgb(70, 0, 0, 0), Color.FromArgb(0, 0, 0, 0),
                               LinearGradientMode.Vertical))
                        g.FillRectangle(b, 0, 0, Width, Height * 0.42f);
                    g.ResetClip();
                }
                using (GraphicsPath p = Ui.Round(r, rad))
                using (var pen = new Pen(Theme.Border, 1f))
                    g.DrawPath(pen, p);

                Font lf = Theme.F(9.5f * Scaled(), FontStyle.Regular);
                Font vf = Theme.Bold(12.5f * Scaled());
                Color lc = Color.FromArgb(225, 208, 216, 228);
                Color vc = Color.White;

                int n = 0;
                for (int i = 0; i < ItemKeys.Length; i++) if (ItemValue(st, i)) n++;
                if (n == 0)
                {
                    ShadowText(g, I18n.T("ov.preview.none"), Theme.F(9.5f, FontStyle.Regular), lc,
                        Ui.Fp(16), Height / 2f - Ui.Fp(9));
                    return;
                }

                if (st.OverlayStyle == "mini")
                {
                    // 简洁样式:屏幕顶部居中,只有帧率和温度。
                    string s = "144 FPS";
                    if (st.ShowTemp) s += "   64°";
                    Font f = Theme.Bold(14f * Scaled());
                    Size sz = TextRenderer.MeasureText(g, s, f, Size.Empty, TextFormatFlags.NoPadding);
                    ShadowText(g, s, f, vc, (Width - sz.Width) / 2f, Ui.Fp(14));
                    return;
                }

                if (st.OverlayStyle == "v")
                {
                    // 竖向:两列贴左侧、纵向居中,列宽按最长标签收窄,和真实叠层的窄条一致。
                    float rowH = Ui.Fp(16) * Scaled();
                    float x = Ui.Fp(16);
                    float labelW = 0;
                    for (int i = 0; i < ItemKeys.Length; i++)
                    {
                        if (!ItemValue(st, i)) continue;
                        Size ls = TextRenderer.MeasureText(g, I18n.T("ov.item." + ItemKeys[i]), lf,
                            Size.Empty, TextFormatFlags.NoPadding);
                        if (ls.Width > labelW) labelW = ls.Width;
                    }
                    float valueX = x + labelW + Ui.Fp(12);
                    float y = (Height - n * rowH) / 2f;
                    for (int i = 0; i < ItemKeys.Length; i++)
                    {
                        if (!ItemValue(st, i)) continue;
                        ShadowText(g, I18n.T("ov.item." + ItemKeys[i]), lf, lc, x, y + Ui.Fp(2));
                        ShadowText(g, Samples[i], vf, vc, valueX, y);
                        y += rowH;
                    }
                    return;
                }

                // 横向:一行"标签 + 数值",水平居中、贴近顶部。
                // 显示项多时按可用宽度折行,否则整行会撑出卡片、两端都被裁掉。
                float gap1 = Ui.Fp(4) * Scaled(), gap2 = Ui.Fp(14) * Scaled();
                float[] lw = new float[ItemKeys.Length];
                float[] iw = new float[ItemKeys.Length];
                int[] vis = new int[ItemKeys.Length];
                int nv = 0;
                for (int i = 0; i < ItemKeys.Length; i++)
                {
                    if (!ItemValue(st, i)) continue;
                    lw[i] = TextRenderer.MeasureText(g, I18n.T("ov.item." + ItemKeys[i]), lf,
                        Size.Empty, TextFormatFlags.NoPadding).Width;
                    iw[i] = lw[i] + gap1 + TextRenderer.MeasureText(g, Samples[i], vf,
                        Size.Empty, TextFormatFlags.NoPadding).Width;
                    vis[nv++] = i;
                }
                float avail = Width - Ui.Fp(12) * 2f;
                float lineH = Ui.Fp(19) * Scaled();
                int start = 0, row = 0;
                while (start < nv)
                {
                    int end = start;
                    float lineW = 0;
                    while (end < nv)
                    {
                        float cand = lineW + (end > start ? gap2 : 0f) + iw[vis[end]];
                        if (end > start && cand > avail) break;
                        lineW = cand;
                        end++;
                    }
                    float hx = (Width - lineW) / 2f;
                    if (hx < Ui.Fp(8)) hx = Ui.Fp(8);
                    float hy = Ui.Fp(14) + row * lineH;
                    for (int k = start; k < end; k++)
                    {
                        int i = vis[k];
                        ShadowText(g, I18n.T("ov.item." + ItemKeys[i]), lf, lc, hx, hy + Ui.Fp(3));
                        ShadowText(g, Samples[i], vf, vc, hx + lw[i] + gap1, hy);
                        hx += iw[i] + gap2;
                    }
                    start = end;
                    row++;
                }
            }

            /// <summary>预览里的缩放:实际 0.7~2.0 映射到 0.8~1.35,避免撑破卡片。</summary>
            private float Scaled()
            {
                float k = (float)st.OverlayScale;
                if (k < 0.8f) k = 0.8f;
                if (k > 1.35f) k = 1.35f;
                return k;
            }

            private static void ShadowText(Graphics g, string s, Font f, Color c, float x, float y)
            {
                int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
                TextRenderer.DrawText(g, s, f, new Point(ix + 1, iy + 1),
                    Color.FromArgb(170, 0, 0, 0), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, s, f, new Point(ix, iy), c,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }
    }

    // =====================================================================
    //  设置页
    // =====================================================================
    public class SettingsPage : Page
    {
        private readonly Card genCard, memCard, fpsCard, adminCard, aboutCard, hotCard;
        private readonly Segmented segLang, segTheme;
        private readonly Toggle tgAuto, tgTray, tgMemAuto, tgBoostProfile, tgUpdates;
        private readonly Slider sldThreshold;
        private readonly TextLabel memInfo, fpsStatus, fpsNote, adminStatus, adminNote, aboutName, aboutNotice, aboutEnv;
        private readonly TextLabel[] adminItems = new TextLabel[3];
        private readonly TextLabel[] adminMarks = new TextLabel[3];
        private readonly Hairline adminLine;
        private readonly FlatButton btnFps, btnAdmin, btnLog, btnWhitelist, btnRestore;
        private readonly TextLabel langTitle, themeTitle, autoTitle, autoSub, trayTitle, traySub;
        private readonly TextLabel boostProfileTitle, boostProfileSub;
        private readonly TextLabel updateTitle, updateSub;
        private readonly TextLabel memAutoTitle, memAutoSub;
        private readonly TextLabel hotOvTitle, hotOvKey, hotBoostTitle, hotBoostKey, hotFlTitle, hotFlKey;

        public SettingsPage(MainForm host) : base(host)
        {
            genCard = NewCard();
            memCard = NewCard();
            fpsCard = NewCard();
            adminCard = NewCard();
            aboutCard = NewCard();
            hotCard = NewCard();

            CardHeader(genCard, I18n.T("set.general"));
            CardHeader(memCard, I18n.T("set.mem"));
            CardHeader(fpsCard, I18n.T("set.fps"));
            CardHeader(adminCard, I18n.T("set.admin"));
            CardHeader(aboutCard, I18n.T("set.about"));
            CardHeader(hotCard, I18n.T("set.hotkeys"));

            // 常规
            langTitle = RowTitle(I18n.T("set.lang"));
            segLang = new Segmented();
            segLang.Items = new string[] { "中文", "English" };
            segLang.SetSilent(I18n.IsEn ? 1 : 0);
            segLang.Changed += delegate
            {
                string lang = segLang.Selected == 1 ? "en" : "zh";
                if (Host.St.Language == lang) return;
                Host.St.Language = lang;
                Host.St.Save();
                Host.RebuildUi();
            };

            themeTitle = RowTitle(I18n.T("set.theme"));
            segTheme = new Segmented();
            segTheme.Items = new string[] { I18n.T("set.theme.light"), I18n.T("set.theme.dark") };
            segTheme.SetSilent(host.St.Dark ? 1 : 0);
            segTheme.Changed += delegate
            {
                bool dark = segTheme.Selected == 1;
                if (Host.St.Dark == dark) return;
                Host.St.Dark = dark;
                Host.St.Save();
                Host.ApplyTheme();
            };

            autoTitle = RowTitle(I18n.T("set.autostart"));
            autoSub = RowSub(I18n.T("set.autostart.sub"));
            tgAuto = new Toggle();
            tgAuto.SetSilent(host.St.Autostart);
            tgAuto.CheckedChanged += delegate
            {
                Host.St.Autostart = tgAuto.Checked;
                Host.St.Save();
                Host.SetAutostart(tgAuto.Checked);
            };

            trayTitle = RowTitle(I18n.T("set.tray"));
            traySub = RowSub(I18n.T("set.tray.sub"));
            tgTray = new Toggle();
            tgTray.SetSilent(host.St.CloseToTray);
            tgTray.CheckedChanged += delegate
            {
                Host.St.CloseToTray = tgTray.Checked;
                Host.SaveSettingsSoon();
            };

            genCard.Controls.Add(langTitle);
            genCard.Controls.Add(segLang);
            genCard.Controls.Add(themeTitle);
            genCard.Controls.Add(segTheme);
            genCard.Controls.Add(autoTitle);
            genCard.Controls.Add(autoSub);
            genCard.Controls.Add(tgAuto);
            genCard.Controls.Add(trayTitle);
            genCard.Controls.Add(traySub);
            genCard.Controls.Add(tgTray);

            // 加速自动化(与加速页的模式选择独立)
            boostProfileTitle = RowTitle(I18n.T("boost.profile"));
            boostProfileSub = RowSub(I18n.T("boost.profile.sub"));
            tgBoostProfile = new Toggle();
            tgBoostProfile.SetSilent(host.St.BoostProfile);
            tgBoostProfile.CheckedChanged += delegate
            {
                Host.St.BoostProfile = tgBoostProfile.Checked;
                Host.SaveSettingsSoon();
            };

            genCard.Controls.Add(boostProfileTitle);
            genCard.Controls.Add(boostProfileSub);
            genCard.Controls.Add(tgBoostProfile);

            // 版本更新
            updateTitle = RowTitle(I18n.T("set.update"));
            updateSub = RowSub(I18n.T("set.update.sub"));
            tgUpdates = new Toggle();
            tgUpdates.SetSilent(host.St.CheckUpdates);
            tgUpdates.CheckedChanged += delegate
            {
                Host.St.CheckUpdates = tgUpdates.Checked;
                Host.SaveSettingsSoon();
                if (tgUpdates.Checked) Host.CheckUpdates(true);
            };

            genCard.Controls.Add(updateTitle);
            genCard.Controls.Add(updateSub);
            genCard.Controls.Add(tgUpdates);

            // 内存
            memAutoTitle = RowTitle(I18n.T("set.memauto"));
            memAutoSub = RowSub(I18n.T("set.memauto.sub"));
            tgMemAuto = new Toggle();
            tgMemAuto.SetSilent(host.St.MemAuto);
            tgMemAuto.CheckedChanged += delegate
            {
                Host.St.MemAuto = tgMemAuto.Checked;
                Host.SaveSettingsSoon();
            };

            sldThreshold = new Slider();
            sldThreshold.Min = 60;
            sldThreshold.Max = 95;
            sldThreshold.Unit = "%";
            sldThreshold.Label = I18n.T("set.threshold");
            sldThreshold.SetSilent(host.St.MemThreshold);
            sldThreshold.Changed += delegate
            {
                Host.St.MemThreshold = sldThreshold.Value;
                Host.SaveSettingsSoon();
            };

            memInfo = new TextLabel();
            memInfo.Size = 8.5f;
            memInfo.ForeColor = Theme.Sub;
            memInfo.Ellipsis = true;

            memCard.Controls.Add(memAutoTitle);
            memCard.Controls.Add(memAutoSub);
            memCard.Controls.Add(tgMemAuto);
            memCard.Controls.Add(sldThreshold);
            memCard.Controls.Add(memInfo);

            // 帧率引擎
            fpsStatus = new TextLabel();
            fpsStatus.Size = 9.5f;
            fpsStatus.Ellipsis = true;
            fpsNote = new TextLabel();
            fpsNote.Size = 8.5f;
            fpsNote.ForeColor = Theme.Sub;
            fpsNote.Wrap = true;
            fpsNote.Text = I18n.T("set.fps.note");
            btnFps = new FlatButton();
            btnFps.FontPt = 9f;
            btnFps.Text = I18n.T("set.fps.download");
            btnFps.Click += delegate { Host.DownloadPresentMon(); };

            fpsCard.Controls.Add(fpsStatus);
            fpsCard.Controls.Add(fpsNote);
            fpsCard.Controls.Add(btnFps);

            // 管理员
            adminStatus = new TextLabel();
            adminStatus.Size = 9f;
            adminStatus.ForeColor = Theme.Sub;
            adminStatus.Wrap = true;
            adminStatus.Align = ContentAlignment.TopLeft;
            adminNote = new TextLabel();
            adminNote.Size = 8.5f;
            adminNote.ForeColor = Theme.Faint;
            adminNote.Wrap = true;
            adminNote.Align = ContentAlignment.TopLeft;
            adminNote.Text = I18n.T("set.admin.note");

            // 拿到管理员权限后具体能多做什么:三行清单,卡片下半截不再空一大片。
            string[] caps =
            {
                I18n.T("set.admin.cap.mem"),
                I18n.T("set.admin.cap.power"),
                I18n.T("set.admin.cap.timer")
            };
            for (int i = 0; i < caps.Length; i++)
            {
                var it = new TextLabel();
                it.Size = 8.8f;
                it.ForeColor = Theme.Sub;
                it.Ellipsis = true;
                it.Text = "·  " + caps[i];
                adminItems[i] = it;
                adminCard.Controls.Add(it);

                var mk = new TextLabel();
                mk.Size = 8.8f;
                mk.Align = ContentAlignment.MiddleRight;
                adminMarks[i] = mk;
                adminCard.Controls.Add(mk);
            }

            adminLine = new Hairline();
            adminCard.Controls.Add(adminLine);
            btnAdmin = new FlatButton();
            btnAdmin.Primary = false;
            btnAdmin.FontPt = 9f;
            btnAdmin.Text = I18n.T("set.admin.restart");
            btnAdmin.Click += delegate { MemEngine.RestartElevated(""); };

            // 保护名单与还原:一个避免误伤平台进程,一个兜底把系统改回去。
            btnWhitelist = new FlatButton();
            btnWhitelist.Primary = false;
            btnWhitelist.FontPt = 9f;
            btnWhitelist.Text = I18n.T("set.whitelist");
            btnWhitelist.Click += delegate { Host.EditWhitelist(); };

            btnRestore = new FlatButton();
            btnRestore.Primary = false;
            btnRestore.FontPt = 9f;
            btnRestore.Text = I18n.T("set.restoreall");
            btnRestore.Click += delegate { Host.RestoreAll(); };

            adminCard.Controls.Add(adminStatus);
            adminCard.Controls.Add(adminNote);
            adminCard.Controls.Add(btnAdmin);
            adminCard.Controls.Add(btnWhitelist);
            adminCard.Controls.Add(btnRestore);

            // 快捷键
            hotOvTitle = RowTitle(I18n.T("set.hotkey.overlay"));
            hotOvKey = RowKey("Ctrl + Alt + O");
            hotBoostTitle = RowTitle(I18n.T("set.hotkey.boost"));
            hotBoostKey = RowKey("Ctrl + Alt + B");
            hotFlTitle = RowTitle(I18n.T("set.hotkey.filter"));
            hotFlKey = RowKey("Ctrl + Alt + F");
            hotCard.Controls.Add(hotOvTitle);
            hotCard.Controls.Add(hotOvKey);
            hotCard.Controls.Add(hotBoostTitle);
            hotCard.Controls.Add(hotBoostKey);
            hotCard.Controls.Add(hotFlTitle);
            hotCard.Controls.Add(hotFlKey);

            // 关于
            aboutName = new TextLabel();
            aboutName.Size = 10f;
            aboutName.Style = FontStyle.Bold;
            aboutName.Text = I18n.T("set.version");
            aboutNotice = new TextLabel();
            aboutNotice.Size = 8.5f;
            aboutNotice.ForeColor = Theme.Sub;
            aboutNotice.Wrap = true;
            aboutNotice.Align = ContentAlignment.TopLeft;
            aboutNotice.Text = I18n.T("set.notice");
            aboutEnv = new TextLabel();
            aboutEnv.Size = 8.5f;
            aboutEnv.ForeColor = Theme.Faint;
            aboutEnv.Wrap = true;
            aboutEnv.Align = ContentAlignment.TopLeft;
            aboutEnv.Text = I18n.T("set.about.env");
            btnLog = new FlatButton();
            btnLog.Primary = false;
            btnLog.FontPt = 9f;
            btnLog.Text = I18n.T("set.log");
            btnLog.Click += delegate { Host.OpenLogFolder(); };

            aboutCard.Controls.Add(aboutName);
            aboutCard.Controls.Add(aboutNotice);
            aboutCard.Controls.Add(aboutEnv);
            aboutCard.Controls.Add(btnLog);

            Controls.Add(genCard);
            Controls.Add(hotCard);
            Controls.Add(memCard);
            Controls.Add(fpsCard);
            Controls.Add(adminCard);
            Controls.Add(aboutCard);

            UpdateStatus();
            LayoutCards();
        }

        // ---------------- Actions 调用 ----------------

        public void SyncState()
        {
            UpdateStatus();
        }

        public void SetDownloadState(bool busy, int pct)
        {
            btnFps.Busy = busy;
            btnFps.Enabled = !busy;
            btnFps.Text = busy
                ? (pct > 0 ? pct + "%" : I18n.T("set.fps.download"))
                : I18n.T("set.fps.download");
        }

        public void UpdateStatus()
        {
            if (Host.Fps != null)
            {
                FpsStatus st = Host.Fps.Status;
                if (st == FpsStatus.NotInstalled || !FpsEngine.Installed)
                {
                    fpsStatus.Text = I18n.T("set.fps.missing");
                    fpsStatus.ForeColor = Theme.Warn;
                }
                else if (st == FpsStatus.NeedAdmin)
                {
                    fpsStatus.Text = I18n.T("set.fps.needadmin");
                    fpsStatus.ForeColor = Theme.Warn;
                }
                else
                {
                    fpsStatus.Text = I18n.T("set.fps.ready") + "   ·   PresentMon " + FpsEngine.Version;
                    fpsStatus.ForeColor = Theme.Text;
                }
            }

            bool admin = MemEngine.IsAdmin;
            adminStatus.Text = admin ? I18n.T("set.admin.on") : I18n.T("set.admin.off");
            adminStatus.ForeColor = admin ? Theme.Text : Theme.Warn;
            btnAdmin.Visible = !admin;

            for (int i = 0; i < adminMarks.Length; i++)
            {
                adminMarks[i].Text = admin ? I18n.T("set.admin.cap.ok") : I18n.T("set.admin.cap.no");
                adminMarks[i].ForeColor = admin ? Theme.Accent : Theme.Faint;
            }
        }

        public override void Refresh(SysSample s)
        {
            if (s == null) return;
            memInfo.Text = Fmt.Pair(s.RamUsed, s.RamTotal) + (s.RamPct >= 0 ? "   ·   " + s.RamPct.ToString("0") + "%" : "");
        }

        // ---------------- 布局 ----------------

        private static Card NewCard()
        {
            var c = new Card();
            // 卡片自身用卡片色:子控件继承同一个 BackColor,
            // 自绘标签铺底时才不会在卡片上打出背景色的方块。
            c.BackColor = Theme.Card;
            return c;
        }

        private static TextLabel CardHeader(Card c, string text)
        {
            var l = new TextLabel();
            l.Size = 9.5f;
            l.Style = FontStyle.Bold;
            l.ForeColor = Theme.Sub;
            l.Text = text;
            l.Name = "header";
            c.Controls.Add(l);
            return l;
        }

        private static TextLabel RowTitle(string text)
        {
            var l = new TextLabel();
            l.Size = 9.8f;
            l.Text = text;
            return l;
        }

        private static TextLabel RowSub(string text)
        {
            var l = new TextLabel();
            l.Size = 8.2f;
            l.ForeColor = Theme.Sub;
            l.Ellipsis = true;
            l.Text = text;
            return l;
        }

        /// <summary>快捷键右侧的按键提示,右对齐弱化显示。</summary>
        private static TextLabel RowKey(string text)
        {
            var l = new TextLabel();
            l.Size = 8.5f;
            l.ForeColor = Theme.Faint;
            l.Align = ContentAlignment.MiddleRight;
            l.Text = text;
            return l;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCards();
        }

        private void LayoutCards()
        {
            if (Width <= 0 || Height <= 0) return;
            int gap = Ui.Px(12);
            int colW = (Width - gap) / 2;
            int rightX = colW + gap;
            int rightW = Width - rightX;

            int genH = Ui.Px(286);
            int hotH = Ui.Px(152);
            int aboutH = Height - genH - hotH - gap * 2;
            if (aboutH < Ui.Px(150))
            {
                aboutH = Ui.Px(150);
                genH = Math.Max(Ui.Px(274), Height - hotH - aboutH - gap * 2);
            }

            int memH = Ui.Px(160);
            int fpsH = Ui.Px(150);
            int adminH = Height - memH - fpsH - gap * 2;
            if (adminH < Ui.Px(184))
            {
                adminH = Ui.Px(184);
                memH = Math.Max(Ui.Px(160), Height - fpsH - adminH - gap * 2);
            }

            genCard.SetBounds(0, 0, colW, genH);
            hotCard.SetBounds(0, genH + gap, colW, hotH);
            aboutCard.SetBounds(0, genH + hotH + gap * 2, colW, aboutH);
            memCard.SetBounds(rightX, 0, rightW, memH);
            fpsCard.SetBounds(rightX, memH + gap, rightW, fpsH);
            adminCard.SetBounds(rightX, memH + fpsH + gap * 2, rightW, adminH);

            int pad = Ui.Px(18);
            int toggleW = Ui.Px(48);
            int segW = Ui.Px(200);

            PlaceHeader(genCard, pad);
            langTitle.SetBounds(pad, Ui.Px(40), Ui.Px(80), Ui.Px(28));
            segLang.SetBounds(colW - pad - segW, Ui.Px(38), segW, Ui.Px(30));
            themeTitle.SetBounds(pad, Ui.Px(78), Ui.Px(80), Ui.Px(28));
            segTheme.SetBounds(colW - pad - segW, Ui.Px(76), segW, Ui.Px(30));

            autoTitle.SetBounds(pad, Ui.Px(116), colW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(20));
            autoSub.SetBounds(pad, Ui.Px(134), colW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(18));
            tgAuto.SetBounds(colW - pad - toggleW, Ui.Px(118), toggleW, Ui.Px(24));

            trayTitle.SetBounds(pad, Ui.Px(154), colW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(20));
            traySub.SetBounds(pad, Ui.Px(172), colW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(18));
            tgTray.SetBounds(colW - pad - toggleW, Ui.Px(156), toggleW, Ui.Px(24));

            boostProfileTitle.SetBounds(pad, Ui.Px(192), colW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(20));
            boostProfileSub.SetBounds(pad, Ui.Px(210), colW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(18));
            tgBoostProfile.SetBounds(colW - pad - toggleW, Ui.Px(194), toggleW, Ui.Px(24));

            updateTitle.SetBounds(pad, Ui.Px(230), colW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(20));
            updateSub.SetBounds(pad, Ui.Px(248), colW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(18));
            tgUpdates.SetBounds(colW - pad - toggleW, Ui.Px(232), toggleW, Ui.Px(24));

            PlaceHeader(memCard, pad);
            memAutoTitle.SetBounds(pad, Ui.Px(44), rightW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(20));
            memAutoSub.SetBounds(pad, Ui.Px(64), rightW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(18));
            tgMemAuto.SetBounds(rightW - pad - toggleW, Ui.Px(46), toggleW, Ui.Px(24));
            sldThreshold.SetBounds(pad, Ui.Px(92), rightW - pad * 2, Ui.Px(44));
            memInfo.SetBounds(pad, memH - Ui.Px(20), rightW - pad * 2, Ui.Px(18));

            PlaceHeader(fpsCard, pad);
            fpsStatus.SetBounds(pad, Ui.Px(44), rightW - pad * 2, Ui.Px(20));
            fpsNote.SetBounds(pad, Ui.Px(66), rightW - pad * 2, Ui.Px(40));
            int bw = Ui.Px(132), bh = Ui.Px(30);
            btnFps.SetBounds(rightW - pad - bw, fpsH - bh - Ui.Px(14), bw, bh);

            // 管理员卡与关于卡会吃掉窗口剩余的整块高度:把多出来的空白
            // 均摊进各行之间的间距,内容铺满卡片,标题下方不会空一大截。
            PlaceHeader(adminCard, pad);
            // 卡片够高才铺开权限清单;窗口太矮时只留状态、说明和按钮,不硬塞导致重叠。
            bool caps = adminH >= Ui.Px(248);
            int adminExtra = Math.Max(0, adminH - Ui.Px(caps ? 248 : 184));
            // 重启按钮挪到状态行右侧,说明文字就能占满整行,不会被挤成省略号。
            int statusY = Ui.Px(44) + adminExtra / 8;
            adminStatus.SetBounds(pad, statusY, rightW - pad * 2 - (btnAdmin.Visible ? bw + Ui.Px(10) : 0), Ui.Px(22));
            btnAdmin.SetBounds(rightW - pad - bw, statusY - Ui.Px(4), bw, bh);

            int noteY = statusY + Ui.Px(32);
            adminNote.SetBounds(pad, noteY, rightW - pad * 2, Ui.Px(36));

            adminLine.Visible = caps;
            for (int i = 0; i < adminItems.Length; i++)
            {
                adminItems[i].Visible = caps;
                adminMarks[i].Visible = caps;
            }

            if (caps)
            {
                // 权限清单铺在说明和底部按钮之间,多出来的高度按行距均摊。
                int listTop = noteY + Ui.Px(42) + adminExtra / 8;
                int step = Ui.Px(22) + adminExtra / 24;
                adminLine.SetBounds(pad, listTop, rightW - pad * 2, Ui.Px(1));
                for (int i = 0; i < adminItems.Length; i++)
                {
                    int y = listTop + Ui.Px(9) + i * step;
                    adminItems[i].SetBounds(pad, y, rightW - pad * 2 - Ui.Px(62), Ui.Px(20));
                    adminMarks[i].SetBounds(rightW - pad - Ui.Px(58), y, Ui.Px(58), Ui.Px(20));
                }
            }

            int rowY2 = caps ? adminH - bh - Ui.Px(16) : Ui.Px(148) + adminExtra * 7 / 8;
            btnWhitelist.SetBounds(pad, rowY2, bw, bh);
            btnRestore.SetBounds(pad + bw + Ui.Px(8), rowY2, rightW - pad * 2 - bw - Ui.Px(8), bh);

            PlaceHeader(hotCard, pad);
            int keyW = Ui.Px(150);
            hotOvTitle.SetBounds(pad, Ui.Px(44), colW - pad * 2 - keyW - Ui.Px(8), Ui.Px(26));
            hotOvKey.SetBounds(colW - pad - keyW, Ui.Px(44), keyW, Ui.Px(26));
            hotBoostTitle.SetBounds(pad, Ui.Px(76), colW - pad * 2 - keyW - Ui.Px(8), Ui.Px(26));
            hotBoostKey.SetBounds(colW - pad - keyW, Ui.Px(76), keyW, Ui.Px(26));
            hotFlTitle.SetBounds(pad, Ui.Px(108), colW - pad * 2 - keyW - Ui.Px(8), Ui.Px(26));
            hotFlKey.SetBounds(colW - pad - keyW, Ui.Px(108), keyW, Ui.Px(26));

            PlaceHeader(aboutCard, pad);
            // 关于卡最小只需 174 逻辑像素:版本、声明、环境各一行,按钮收在卡内。
            // 之前按 228 算,窗口高度不够时按钮会被卡片下沿裁掉。
            int aboutExtra = Math.Max(0, aboutH - Ui.Px(174));
            aboutName.SetBounds(pad, Ui.Px(44) + aboutExtra / 8, colW - pad * 2, Ui.Px(22));
            aboutNotice.SetBounds(pad, Ui.Px(70) + aboutExtra * 2 / 8, colW - pad * 2, Ui.Px(30));
            aboutEnv.SetBounds(pad, Ui.Px(104) + aboutExtra * 5 / 8, colW - pad * 2, Ui.Px(18));
            btnLog.SetBounds(pad, Ui.Px(128) + aboutExtra * 7 / 8, bw, bh);
        }

        private static void PlaceHeader(Card c, int pad)
        {
            foreach (Control ctl in c.Controls)
            {
                if (ctl.Name == "header")
                {
                    ctl.SetBounds(pad, Ui.Px(12), c.Width - pad * 2, Ui.Px(20));
                    return;
                }
            }
        }
    }

    // =====================================================================
    //  滤镜页
    // =====================================================================
    public class FilterPage : Page
    {
        private readonly Card modeCard, presetCard, customCard;
        private readonly TextLabel modeTitle, modeNote, presetTitle, customTitle;
        private readonly Segmented mode, presetGroup;
        private readonly Chip[] chips = new Chip[Filter.PresetKeys.Length];
        private readonly FilterPreview preview;
        private readonly Slider sIntensity, sBright, sContrast, sSat, sWarm, sDark, sVibrance;
        private readonly FlatButton resetBtn;
        private readonly ToolTip tips;
        private readonly Timer brightTimer;
        private int brightPending;
        // 0 = 通用,1 = 热门游戏;只是分组显示,不写进设置。
        private int group;

        public FilterPage(MainForm host) : base(host)
        {
            modeCard = NewCard();
            presetCard = NewCard();
            customCard = NewCard();

            modeTitle = Header(I18n.T("flt.title"));
            mode = new Segmented();
            mode.Items = new string[]
            {
                I18n.T("flt.mode.off"), I18n.T("flt.mode.game"), I18n.T("flt.mode.always")
            };
            mode.SetSilent(ModeIndex(host.St.FilterMode));
            mode.Changed += delegate
            {
                Host.St.FilterMode = mode.Selected;
                Host.St.Save();
                Host.ApplyFilter();
                UpdateNote();
            };

            preview = new FilterPreview();

            modeNote = new TextLabel();
            modeNote.Size = 8.5f;
            modeNote.ForeColor = Theme.Faint;
            modeNote.Wrap = true;

            modeCard.Controls.Add(modeTitle);
            modeCard.Controls.Add(mode);
            modeCard.Controls.Add(preview);
            modeCard.Controls.Add(modeNote);

            presetTitle = Header(I18n.T("flt.preset"));
            presetCard.Controls.Add(presetTitle);

            // 预设分两组显示:通用调色和按游戏调的方案混在一行会看不清重点。
            group = Filter.IsGamePreset(host.St.FilterPreset) ? 1 : 0;
            presetGroup = new Segmented();
            presetGroup.Items = new string[] { I18n.T("flt.group.general"), I18n.T("flt.group.game") };
            presetGroup.SetSilent(group);
            presetGroup.Changed += delegate
            {
                group = presetGroup.Selected;
                LayoutChips();
            };
            presetCard.Controls.Add(presetGroup);

            tips = new ToolTip();
            tips.InitialDelay = 320;
            tips.AutoPopDelay = 15000;

            for (int i = 0; i < chips.Length; i++)
            {
                chips[i] = new Chip();
                chips[i].Text = I18n.T(Filter.PresetKeys[i]);
                chips[i].FontPt = 9f;
                chips[i].Selected = host.St.FilterPreset == i;
                int idx = i;
                chips[i].Click += delegate { PickPreset(idx); };
                if (Filter.IsGamePreset(i)) tips.SetToolTip(chips[i], I18n.T(Filter.PresetKeys[i] + ".tip"));
                presetCard.Controls.Add(chips[i]);
            }

            customTitle = Header(I18n.T("flt.custom"));
            resetBtn = new FlatButton();
            resetBtn.Primary = false;
            resetBtn.FontPt = 8.5f;
            resetBtn.Text = I18n.T("flt.reset");
            resetBtn.Click += delegate { ResetCustom(); };

            sIntensity = MakeSlider(I18n.T("flt.intensity"), 0, 100, "%", host.St.FilterIntensity);

            // 亮度直接写显示器硬件背光(DDC/CI),不再进滤镜矩阵。
            brightTimer = new Timer();
            brightTimer.Interval = 180;
            brightTimer.Tick += delegate
            {
                brightTimer.Stop();
                if (!Display.TrySet(brightPending)) Host.Toast(I18n.T("flt.bright.fail"));
            };
            sBright = new Slider();
            sBright.Label = I18n.T("flt.brightness");
            sBright.Min = 0;
            sBright.Max = 100;
            sBright.Unit = "%";
            sBright.SetSilent(0);
            sBright.Changed += delegate
            {
                brightPending = sBright.Value;
                brightTimer.Stop();
                brightTimer.Start();
            };

            sContrast = MakeSlider(I18n.T("flt.contrast"), -100, 100, "", host.St.FilterContrast);
            sSat = MakeSlider(I18n.T("flt.saturation"), -100, 100, "", host.St.FilterSaturation);
            sWarm = MakeSlider(I18n.T("flt.warmth"), -100, 100, "", host.St.FilterWarmth);
            sDark = MakeSlider(I18n.T("flt.dark"), 0, 100, "", host.St.FilterDark);
            sVibrance = MakeSlider(I18n.T("flt.vibrance"), 0, 100, "", host.St.FilterVibrance);

            customCard.Controls.Add(customTitle);
            customCard.Controls.Add(resetBtn);
            customCard.Controls.Add(sIntensity);
            customCard.Controls.Add(sBright);
            customCard.Controls.Add(sContrast);
            customCard.Controls.Add(sSat);
            customCard.Controls.Add(sWarm);
            customCard.Controls.Add(sDark);
            customCard.Controls.Add(sVibrance);

            Controls.Add(modeCard);
            Controls.Add(presetCard);
            Controls.Add(customCard);

            UpdateNote();
            UpdatePreview();
            LayoutCards();
        }

        /// <summary>切到滤镜页时把滑块同步成显示器当前亮度;不支持 DDC/CI 就禁用。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible)
            {
                brightTimer.Stop();
                return;
            }

            Display.Attach(Host.Handle);
            int v, lo, hi;
            bool ok = Display.TryGet(out v, out lo, out hi);
            sBright.Enabled = ok;
            sBright.Label = I18n.T(ok ? "flt.brightness" : "flt.brightness.unsupported");
            if (ok)
            {
                sBright.Min = lo;
                sBright.Max = hi;
                sBright.Unit = hi == 100 ? "%" : "";
                sBright.SetSilent(v);
            }
            sBright.Invalidate();
        }

        private static Card NewCard()
        {
            var c = new Card();
            // 卡片自身用卡片色:子控件继承同一个 BackColor,
            // 自绘标签铺底时才不会在卡片上打出背景色的方块。
            c.BackColor = Theme.Card;
            return c;
        }

        private static TextLabel Header(string text)
        {
            var l = new TextLabel();
            l.Size = 9.5f;
            l.Style = FontStyle.Bold;
            l.ForeColor = Theme.Sub;
            l.Text = text;
            return l;
        }

        private Slider MakeSlider(string label, int min, int max, string unit, int value)
        {
            var s = new Slider();
            s.Label = label;
            s.Min = min;
            s.Max = max;
            s.Unit = unit;
            s.SetSilent(value);
            s.Changed += delegate { CommitCustom(); };
            return s;
        }

        private static int ModeIndex(int v)
        {
            if (v < 0) return 0;
            return v > 2 ? 2 : v;
        }

        private void PickPreset(int idx)
        {
            Host.St.FilterPreset = idx;
            for (int i = 0; i < chips.Length; i++)
            {
                bool on = i == idx;
                if (chips[i].Selected == on) continue;
                chips[i].Selected = on;
                chips[i].Invalidate();
            }
            Host.St.Save();
            Host.ApplyFilter();
            UpdatePreview();
        }

        private void CommitCustom()
        {
            Host.St.FilterIntensity = sIntensity.Value;
            Host.St.FilterContrast = sContrast.Value;
            Host.St.FilterSaturation = sSat.Value;
            Host.St.FilterWarmth = sWarm.Value;
            Host.St.FilterDark = sDark.Value;
            Host.St.FilterVibrance = sVibrance.Value;
            Host.SaveSettingsSoon();
            Host.ApplyFilter();
            UpdatePreview();
        }

        private void ResetCustom()
        {
            sIntensity.SetSilent(100);
            sContrast.SetSilent(0);
            sSat.SetSilent(0);
            sWarm.SetSilent(0);
            sDark.SetSilent(0);
            sVibrance.SetSilent(0);
            CommitCustom();
        }

        private void UpdatePreview()
        {
            preview.SetMatrix(Filter.Current(Host.St));
        }

        private void UpdateNote()
        {
            bool ok = Filter.Supported;
            modeNote.Text = I18n.T(ok ? "flt.note" : "flt.unsupported");
            modeNote.ForeColor = ok ? Theme.Faint : Theme.Warn;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCards();
        }

        /// <summary>按当前分组铺预设按钮:通用 5 列、游戏 4 列,另一组直接隐藏。</summary>
        private void LayoutChips()
        {
            if (Width <= 0) return;
            int pad = Ui.Px(18);
            int perRow = group == 1 ? 4 : 5;
            int cgap = Ui.Px(8);
            int cw = (Width - pad * 2 - cgap * (perRow - 1)) / perRow;
            int ch = Ui.Px(32);
            // 游戏组只有两行,在通用组的三行高度里垂直居中,避免卡片底部塌出一块空白。
            int top = Ui.Px(46) + (group == 1 ? Ui.Px(20) : 0);
            int used = 0;
            for (int i = 0; i < chips.Length; i++)
            {
                bool show = Filter.IsGamePreset(i) == (group == 1);
                chips[i].Visible = show;
                if (!show) continue;
                int row = used / perRow, col = used % perRow;
                chips[i].SetBounds(pad + col * (cw + cgap), top + row * (ch + Ui.Px(6)), cw, ch);
                used++;
            }
        }

        private void LayoutCards()
        {
            if (Width <= 0 || Height <= 0) return;
            int gap = Ui.Px(12);
            int modeH = Ui.Px(150);
            int presetH = Ui.Px(176);
            int customH = Height - modeH - presetH - gap * 2;
            if (customH < Ui.Px(250))
            {
                customH = Ui.Px(250);
                modeH = Math.Max(Ui.Px(132), Height - presetH - customH - gap * 2);
            }

            modeCard.SetBounds(0, 0, Width, modeH);
            presetCard.SetBounds(0, modeH + gap, Width, presetH);
            customCard.SetBounds(0, modeH + presetH + gap * 2, Width, customH);

            int pad = Ui.Px(18);
            int segW = Ui.Px(330), segH = Ui.Px(32);
            modeTitle.SetBounds(pad, Ui.Px(12), Width - pad * 2 - segW - Ui.Px(12), Ui.Px(24));
            mode.SetBounds(Width - pad - segW, Ui.Px(9), segW, segH);

            int prevY = Ui.Px(48);
            preview.SetBounds(pad, prevY, Width - pad * 2, Ui.Px(54));
            modeNote.SetBounds(pad, prevY + Ui.Px(54) + Ui.Px(8),
                Width - pad * 2, Math.Max(Ui.Px(20), modeH - prevY - Ui.Px(70)));

            presetTitle.SetBounds(pad, Ui.Px(12), Width - pad * 2 - Ui.Px(196), Ui.Px(20));
            presetGroup.SetBounds(Width - pad - Ui.Px(180), Ui.Px(8), Ui.Px(180), Ui.Px(28));
            LayoutChips();

            customTitle.SetBounds(pad, Ui.Px(12), Ui.Px(200), Ui.Px(20));
            int bw = Ui.Px(96), bh = Ui.Px(26);
            resetBtn.SetBounds(Width - pad - bw, Ui.Px(9), bw, bh);

            Slider[] all = { sIntensity, sBright, sContrast, sDark, sSat, sWarm, sVibrance };
            int rows = (all.Length + 1) / 2;
            int colGap = Ui.Px(26);
            int colW = (Width - pad * 2 - colGap) / 2;
            int sh = Ui.Px(42), sy = Ui.Px(40);
            // 间距按卡片剩余高度分摊,避免自定义卡底部留出大片空白。
            int sgap = (customH - sy - sh * rows - Ui.Px(12)) / (rows - 1);
            if (sgap > Ui.Px(22)) sgap = Ui.Px(22);
            if (sgap < Ui.Px(4)) sgap = Ui.Px(4);
            for (int i = 0; i < all.Length; i++)
            {
                int col = i / rows, row = i % rows;
                all[i].SetBounds(pad + col * (colW + colGap), sy + row * (sh + sgap), colW, sh);
            }
        }
    }

    /// <summary>滤镜实时预览:同一组色标经过当前颜色矩阵变换后画成渐变。</summary>
    public class FilterPreview : Ctl
    {
        private float[] matrix = Filter.Identity();
        private bool hold;

        private static readonly Color[] Swatches =
        {
            // 起点故意压到接近纯黑:暗部场景平衡、对比度这类调整在这里的差别最明显。
            Color.FromArgb(10, 12, 18),
            Color.FromArgb(22, 24, 30),
            Color.FromArgb(58, 52, 84),
            Color.FromArgb(140, 68, 72),
            Color.FromArgb(212, 148, 78),
            Color.FromArgb(104, 162, 102),
            Color.FromArgb(64, 120, 182),
            Color.FromArgb(238, 242, 246)
        };

        public void SetMatrix(float[] m)
        {
            matrix = m == null ? Filter.Identity() : m;
            Invalidate();
        }

        /// <summary>矩阵等于恒等矩阵时预览没有任何变化,这时不该再提示"按住对比"。</summary>
        private static bool IsIdentity(float[] m)
        {
            if (m == null || m.Length < 25) return true;
            float[] id = Filter.Identity();
            for (int i = 0; i < 25; i++)
                if (Math.Abs(m[i] - id[i]) > 0.001f) return false;
            return true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !IsIdentity(matrix)) { hold = true; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (hold) { hold = false; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hold) { hold = false; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            if (Width < 8 || Height < 8) return;

            bool split = hold && !IsIdentity(matrix);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Ui.Round(r, Ui.Fp(10)))
            {
                if (split)
                {
                    // 对比态:左半原图、右半当前滤镜。两侧同色相邻,
                    // 预设只做了轻微调整时,接缝处的色差也比整块切换更容易看出来。
                    g.SetClip(new RectangleF(0, 0, Width / 2f, Height));
                    DrawBand(g, p, Filter.Identity());
                    g.SetClip(new RectangleF(Width / 2f, 0, Width - Width / 2f, Height));
                    DrawBand(g, p, matrix);
                    g.ResetClip();

                    using (var pen = new Pen(Color.FromArgb(215, 255, 255, 255), Math.Max(1f, Ui.Fp(1.5f))))
                        g.DrawLine(pen, Width / 2f, Ui.Px(7), Width / 2f, Height - Ui.Px(7));
                }
                else
                {
                    DrawBand(g, p, matrix);
                }
                using (var pen = new Pen(Theme.Border, 1f)) g.DrawPath(pen, p);
            }

            Font f = Theme.Bold(8f);
            if (split)
            {
                DrawTag(g, f, I18n.T("flt.ab.left"), Ui.Px(8), Ui.Px(5));
                string right = I18n.T("flt.ab.right");
                DrawTag(g, f, right, Width - g.MeasureString(right, f).Width - Ui.Px(18), Ui.Px(5));
            }

            // 右下角操作提示:没有滤镜效果时直说,避免"按住没反应"的错觉
            string tip = I18n.T("flt.ab.none");
            if (!IsIdentity(matrix)) tip = I18n.T(hold ? "flt.ab.orig" : "flt.ab");
            SizeF sz = g.MeasureString(tip, f);
            DrawTag(g, f, tip, Width - sz.Width - Ui.Px(18), Height - sz.Height - Ui.Px(7));
        }

        /// <summary>把一组色标按矩阵变换后画成渐变条。</summary>
        private void DrawBand(Graphics g, GraphicsPath path, float[] m)
        {
            int n = Swatches.Length;
            var colors = new Color[n];
            var pos = new float[n];
            for (int i = 0; i < n; i++)
            {
                colors[i] = Filter.Transform(Swatches[i], m);
                pos[i] = (float)i / (n - 1);
            }
            var blend = new ColorBlend();
            blend.Colors = colors;
            blend.Positions = pos;

            using (var br = new LinearGradientBrush(
                new RectangleF(0, 0, Math.Max(2, Width), Math.Max(2, Height)),
                colors[0], colors[n - 1], LinearGradientMode.Horizontal))
            {
                br.InterpolationColors = blend;
                g.FillPath(br, path);
            }
            // 底部压一点暗角,看起来像一帧画面而不是纯色卡
            using (var shade = new LinearGradientBrush(
                new RectangleF(0, Height * 0.55f, Math.Max(2, Width), Math.Max(2, Height * 0.45f)),
                Color.Transparent, Color.FromArgb(70, 0, 0, 0), LinearGradientMode.Vertical))
            {
                g.FillPath(shade, path);
            }
        }

        /// <summary>黑底白字的小标签。</summary>
        private void DrawTag(Graphics g, Font f, string text, float x, float y)
        {
            SizeF sz = g.MeasureString(text, f);
            var tr = new RectangleF(x, y, sz.Width + Ui.Px(10), sz.Height + Ui.Px(2));
            using (var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                g.FillRectangle(bg, tr);
            using (var fg = new SolidBrush(Color.White))
                g.DrawString(text, f, fg, tr.X + Ui.Px(5), tr.Y);
        }
    }

    // =====================================================================
    //  战绩页
    // =====================================================================
    public class ReportPage : Page
    {
        private readonly Card detailCard, listCard, optCard;
        private readonly TextLabel detailTitle, listTitle, listSub, autoLbl;
        private readonly FlatButton btnExport, btnDelete, btnClear;
        private readonly Toggle tgAuto;
        private readonly Slider sMin;
        private readonly SessionDetail view;
        private readonly SessionList list;
        private SessionRecord sel;

        public ReportPage(MainForm host) : base(host)
        {
            optCard = NewCard();
            tgAuto = new Toggle();
            tgAuto.SetSilent(Host.St.ReportAuto);
            tgAuto.CheckedChanged += delegate
            {
                Host.St.ReportAuto = tgAuto.Checked;
                Host.SaveSettingsSoon();
            };
            autoLbl = new TextLabel();
            autoLbl.Size = 9.2f;
            autoLbl.ForeColor = Theme.Sub;
            autoLbl.Ellipsis = true;
            autoLbl.Text = I18n.T("set.report.auto");

            sMin = new Slider();
            sMin.Min = 5;
            sMin.Max = 300;
            sMin.Unit = " s";
            sMin.Label = I18n.T("set.report.min");
            sMin.SetSilent(Host.St.ReportMinSec);
            sMin.Changed += delegate
            {
                Host.St.ReportMinSec = sMin.Value;
                if (Host.Tracker != null) Host.Tracker.MinSeconds = sMin.Value;
                Host.SaveSettingsSoon();
            };

            optCard.Controls.Add(tgAuto);
            optCard.Controls.Add(autoLbl);
            optCard.Controls.Add(sMin);

            detailCard = NewCard();
            listCard = NewCard();

            detailTitle = Head(I18n.T("report.detail"));
            view = new SessionDetail();
            btnDelete = new FlatButton();
            btnDelete.Text = I18n.T("report.delete");
            btnDelete.Click += delegate
            {
                if (sel == null) return;
                Host.Sessions.Remove(sel.Id);
                sel = null;
                Reload();
            };

            detailCard.Controls.Add(detailTitle);
            detailCard.Controls.Add(view);
            detailCard.Controls.Add(btnDelete);

            listTitle = Head(I18n.T("report.history"));
            listSub = new TextLabel();
            listSub.Size = 8.6f;
            listSub.ForeColor = Theme.Faint;
            listSub.Ellipsis = true;

            btnExport = new FlatButton();
            btnExport.Text = I18n.T("report.export");
            btnExport.Click += delegate { ExportCsv(); };

            btnClear = new FlatButton();
            btnClear.Text = I18n.T("report.clear");
            btnClear.Click += delegate
            {
                int n = Host.Sessions != null ? Host.Sessions.All.Count : 0;
                if (n == 0) return;
                bool go = DialogBox.Ask(this, I18n.T("report.confirm.clear.title"),
                    I18n.T("report.confirm.clear.body", n.ToString()),
                    I18n.T("report.clear"), I18n.T("dialog.cancel"));
                if (!go) return;
                Host.Sessions.Clear();
                sel = null;
                Reload();
                Host.Toast(I18n.T("report.cleared"));
            };

            list = new SessionList();
            list.SelectionChanged += delegate(SessionRecord r) { sel = r; view.Record = r; btnDelete.Enabled = r != null; };

            listCard.Controls.Add(listTitle);
            listCard.Controls.Add(listSub);
            listCard.Controls.Add(btnExport);
            listCard.Controls.Add(btnClear);
            listCard.Controls.Add(list);

            Controls.Add(optCard);
            Controls.Add(detailCard);
            Controls.Add(listCard);

            Reload();
        }

        private Card NewCard()
        {
            var c = new Card();
            c.BackColor = Theme.Card;
            return c;
        }

        private static TextLabel Head(string text)
        {
            var l = new TextLabel();
            l.Size = 9.5f;
            l.Style = FontStyle.Bold;
            l.ForeColor = Theme.Sub;
            l.Text = text;
            return l;
        }

        /// <summary>战绩变化(新对局结束、删除、清空)后重新读取存储。</summary>
        public void Reload()
        {
            SessionRecord[] all = Host.Sessions != null
                ? Host.Sessions.All.ToArray() : new SessionRecord[0];

            if (sel != null) sel = Host.Sessions != null ? Host.Sessions.Find(sel.Id) : null;
            if (sel == null && all.Length > 0) sel = all[0];

            list.Items = all;
            list.SelectedId = sel != null ? sel.Id : "";
            view.Record = sel;
            btnDelete.Enabled = sel != null;
            btnExport.Enabled = all.Length > 0;
            btnClear.Enabled = all.Length > 0;
            listSub.Text = I18n.T("report.entries", all.Length);
        }

        private void ExportCsv()
        {
            if (Host.Sessions == null || Host.Sessions.All.Count == 0) return;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "CSV (*.csv)|*.csv";
                dlg.FileName = "FrameForge-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Host.Sessions.Export(dlg.FileName);
                    Host.Toast(I18n.T("report.exported", System.IO.Path.GetFileName(dlg.FileName)));
                }
                catch (Exception ex) { Log.Write("export: " + ex.Message); }
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCards();
        }

        private void LayoutCards()
        {
            if (Width <= 0 || Height <= 0) return;
            int gap = Ui.Px(12);
            int optH = Ui.Px(64);
            int avail = Height - optH - gap * 2;
            int detailH = Math.Min(Ui.Px(312), avail * 3 / 5);
            int listH = avail - detailH;
            if (listH < Ui.Px(120)) { listH = Ui.Px(120); detailH = Math.Max(Ui.Px(120), avail - listH); }

            optCard.SetBounds(0, 0, Width, optH);
            detailCard.SetBounds(0, optH + gap, Width, detailH);
            listCard.SetBounds(0, optH + gap + detailH + gap, Width, listH);

            int pad = Ui.Px(18);
            tgAuto.SetBounds(pad, Ui.Px(20), Ui.Px(48), Ui.Px(26));
            autoLbl.SetBounds(pad + Ui.Px(58), Ui.Px(21), Math.Max(Ui.Px(80), Width / 2 - pad * 2 - Ui.Px(58)), Ui.Px(24));
            int sw = Math.Max(Ui.Px(160), Math.Min(Ui.Px(300), Width / 2 - pad));
            sMin.SetBounds(Width - pad - sw, Ui.Px(7), sw, Ui.Px(50));
            detailTitle.SetBounds(pad, Ui.Px(12), Ui.Px(200), Ui.Px(20));
            int bw = Ui.Px(96), bh = Ui.Px(26);
            btnDelete.SetBounds(Width - pad - bw, Ui.Px(9), bw, bh);
            view.SetBounds(pad, Ui.Px(38), Width - pad * 2, detailH - Ui.Px(50));

            listTitle.SetBounds(pad, Ui.Px(12), Ui.Px(110), Ui.Px(20));
            listSub.SetBounds(pad + Ui.Px(110), Ui.Px(14), Ui.Px(120), Ui.Px(16));
            int cw = Ui.Px(96);
            btnClear.SetBounds(Width - pad - cw, Ui.Px(9), cw, bh);
            btnExport.SetBounds(Width - pad - cw * 2 - Ui.Px(8), Ui.Px(9), cw, bh);
            list.SetBounds(pad, Ui.Px(38), Width - pad * 2, listH - Ui.Px(50));
        }
    }

    /// <summary>本局详情:三个大数字 + 六个指标,与退游弹窗同一套视觉。</summary>
    public class SessionDetail : Ctl
    {
        private SessionRecord rec;

        public SessionRecord Record
        {
            get { return rec; }
            set { rec = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            if (Width < 40 || Height < 40) return;

            if (rec == null)
            {
                TextRenderer.DrawText(g, I18n.T("report.empty.title"), Theme.Bold(13f),
                    ClientRectangle, Theme.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int gap = Ui.Px(8);
            int heroH = Ui.Px(78);
            int heroW = (Width - gap * 2) / 3;
            Hero(g, 0, 0, heroW, heroH, I18n.T("report.stat.fps"), Num(rec.FpsAvg, "0"), I18n.T("report.fps.unit"));
            Hero(g, heroW + gap, 0, heroW, heroH, I18n.T("report.stat.low"), Num(rec.Low1, "0"), I18n.T("report.fps.unit"));
            Hero(g, (heroW + gap) * 2, 0, Width - (heroW + gap) * 2, heroH,
                I18n.T("report.duration"), rec.Duration, "");

            int y = heroH + gap;
            int th = Ui.Px(62);
            int tw = (Width - gap * 2) / 3;
            Tile(g, 0, y, tw, th, I18n.T("report.stat.cpu"), Pct(rec.CpuAvg) + " / " + I18n.T("report.max") + " " + Pct(rec.CpuMax));
            Tile(g, tw + gap, y, tw, th, I18n.T("report.stat.gpu"), Pct(rec.GpuAvg) + " / " + I18n.T("report.max") + " " + Pct(rec.GpuMax));
            Tile(g, (tw + gap) * 2, y, Width - (tw + gap) * 2, th, I18n.T("report.stat.temp"),
                Temp(rec.GpuTempAvg) + " / " + I18n.T("report.max") + " " + Temp(rec.GpuTempMax));
            y += th + gap;
            Tile(g, 0, y, tw, th, I18n.T("report.stat.ram"), Mb(rec.RamAvg) + " / " + I18n.T("report.max") + " " + Mb(rec.RamMax));
            Tile(g, tw + gap, y, tw, th, I18n.T("report.stat.vram"), Mb(rec.VramMax));
            Tile(g, (tw + gap) * 2, y, Width - (tw + gap) * 2, th, I18n.T("report.stat.power"), I18n.T("report.w", rec.Power.ToString("0")));
            y += th + Ui.Px(6);

            string energy = I18n.T("report.energy", rec.Wh.ToString("0.0"))
                + "   ·   " + I18n.T("report.co2", rec.Co2.ToString("0"))
                + "   ·   " + I18n.T("report.at", rec.Start.ToString("MM-dd HH:mm"));
            if (y + Ui.Px(18) <= Height)
                TextRenderer.DrawText(g, energy, Theme.Regular(8.6f),
                    new Rectangle(0, y, Width, Height - y), Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private void Hero(Graphics g, int x, int y, int w, int h, string caption, string value, string unit)
        {
            Tile(g, x, y, w, h, caption, value);
            if (unit.Length > 0)
            {
                TextRenderer.DrawText(g, unit, Theme.Regular(8f),
                    new Rectangle(x + Ui.Px(14), y + h - Ui.Px(22), w - Ui.Px(28), Ui.Px(16)), Theme.Faint,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }
        }

        private void Tile(Graphics g, int x, int y, int w, int h, string caption, string value)
        {
            var r = new RectangleF(x, y, w, h);
            Ui.Fill(g, r, Ui.Fp(10), Theme.Bg);
            Ui.Stroke(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f), Ui.Fp(10), Theme.Border, 1f);
            TextRenderer.DrawText(g, caption, Theme.Regular(8.4f),
                new Rectangle(x + Ui.Px(14), y + Ui.Px(9), w - Ui.Px(28), Ui.Px(16)), Theme.Sub,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            bool big = h >= Ui.Px(70);
            TextRenderer.DrawText(g, value, big ? Theme.Num(24f) : Theme.Num(14f),
                new Rectangle(x + Ui.Px(14), y + Ui.Px(big ? 26 : 28), w - Ui.Px(28), big ? Ui.Px(40) : Ui.Px(26)),
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private static string Num(double v, string f) { return v > 0 ? v.ToString(f) : I18n.T("report.none"); }
        private static string Pct(double v) { return v > 0 ? v.ToString("0") + "%" : I18n.T("report.none"); }
        private static string Temp(double v) { return v > 0 ? v.ToString("0") + "°C" : I18n.T("report.none"); }
        private static string Mb(double v)
        {
            if (v <= 0) return I18n.T("report.none");
            return v >= 1024 ? (v / 1024.0).ToString("0.0") + " GB" : v.ToString("0") + " MB";
        }
    }

    /// <summary>战绩列表:自绘行,滚轮滚动,点击选中。</summary>
    public class SessionList : Ctl
    {
        private SessionRecord[] items = new SessionRecord[0];
        private string selectedId = "";
        private int scroll;

        public event Action<SessionRecord> SelectionChanged;

        public SessionRecord[] Items
        {
            set
            {
                items = value ?? new SessionRecord[0];
                scroll = 0;
                Invalidate();
            }
        }

        public string SelectedId
        {
            get { return selectedId; }
            set { selectedId = value ?? ""; Invalidate(); }
        }

        private int RowH() { return Ui.Px(58); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            if (Width < 20 || Height < 20) return;

            if (items.Length == 0)
            {
                TextRenderer.DrawText(g, I18n.T("report.empty.title"), Theme.Bold(11f),
                    new Rectangle(0, 0, Width, Height / 2), Theme.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, I18n.T("report.empty.sub", HostMin().ToString()), Theme.Regular(8.6f),
                    new Rectangle(Ui.Px(24), Height / 2, Width - Ui.Px(48), Ui.Px(40)), Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return;
            }

            int rowH = RowH();
            for (int i = 0; i < items.Length; i++)
            {
                int y = i * rowH - scroll;
                if (y > Height) break;
                if (y + rowH < 0) continue;
                SessionRecord r = items[i];
                bool on = r.Id == selectedId;
                var rr = new RectangleF(0, y + Ui.Px(3), Width, rowH - Ui.Px(6));
                if (on) Ui.Fill(g, rr, Ui.Fp(8), Theme.Alpha(Theme.Accent, 34));
                else Ui.Fill(g, rr, Ui.Fp(8), Theme.Bg);

                int pad = Ui.Px(12);
                int ico = Ui.Px(22);
                AppIcons.DrawApp(g, r.IconPath,
                    new Rectangle(pad, y + Ui.Px(9), ico, ico),
                    on ? Theme.Accent : Theme.Sub, AppGlyph.Gamepad);

                int textX = pad + ico + Ui.Px(10);
                int textW = Width - textX - Ui.Px(90);
                TextRenderer.DrawText(g, Safe(r.DisplayName), Theme.Bold(10.2f),
                    new Rectangle(textX, y + Ui.Px(6), textW, Ui.Px(20)),
                    on ? Theme.Accent : Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                string sub = r.Start.ToString("MM-dd HH:mm") + "   ·   " + r.Duration
                    + "   ·   " + I18n.T("report.avg") + " " + (r.FpsAvg > 0 ? r.FpsAvg.ToString("0") : "—")
                    + "   ·   1% Low " + (r.Low1 > 0 ? r.Low1.ToString("0") : "—");
                TextRenderer.DrawText(g, sub, Theme.Regular(8.3f),
                    new Rectangle(textX, y + Ui.Px(28), textW, Ui.Px(18)), Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, r.FpsAvg > 0 ? r.FpsAvg.ToString("0") : "—", Theme.Num(13f),
                    new Rectangle(Width - Ui.Px(88), y + Ui.Px(10), Ui.Px(74), Ui.Px(24)), Theme.Sub,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private int HostMin()
        {
            // 空状态提示里的最短时长写成 30 秒即可,真实阈值来自设置页。
            return 30;
        }

        private static string Safe(string s) { return string.IsNullOrEmpty(s) ? I18n.T("report.none") : s; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int idx = (e.Y + scroll) / RowH();
            if (idx < 0 || idx >= items.Length) return;
            selectedId = items[idx].Id;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(items[idx]);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int max = items.Length * RowH() - Height;
            if (max < 0) max = 0;
            scroll -= e.Delta / 120 * Ui.Px(46);
            if (scroll < 0) scroll = 0;
            if (scroll > max) scroll = max;
            Invalidate();
        }
    }
}
