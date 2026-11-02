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

        public virtual void Refresh(SysSample s) { }
    }

    // =====================================================================
    //  加速页
    // =====================================================================
    public class BoostPage : Page
    {
        private const int OptCount = 9;

        private readonly Card hero, optCard, resCard;
        private readonly TextLabel stateLbl, gameLbl, gameSub, hint;
        private readonly TextLabel optTitle, resTitle, resText;
        private readonly FlatButton mainBtn, trimBtn;
        private readonly Toggle[] sw = new Toggle[OptCount];
        private readonly TextLabel[] names = new TextLabel[OptCount];
        private readonly TextLabel[] subs = new TextLabel[OptCount];

        private string lastGame = "";
        private bool lastBoosted;

        public BoostPage(MainForm host) : base(host)
        {
            hero = NewCard();
            optCard = NewCard();
            resCard = NewCard();

            stateLbl = new TextLabel();
            stateLbl.Size = 9f;
            stateLbl.Style = FontStyle.Bold;
            stateLbl.ForeColor = Theme.Sub;
            stateLbl.Text = I18n.T("boost.state.idle");

            gameLbl = new TextLabel();
            gameLbl.Size = 18f;
            gameLbl.Style = FontStyle.Bold;
            gameLbl.Text = I18n.T("boost.nogame");
            gameLbl.Ellipsis = true;

            gameSub = new TextLabel();
            gameSub.Size = 9f;
            gameSub.ForeColor = Theme.Sub;
            gameSub.Ellipsis = true;
            gameSub.Text = I18n.T("boost.nogame.sub");

            hint = new TextLabel();
            hint.Size = 8.5f;
            hint.ForeColor = Theme.Faint;
            hint.Wrap = true;
            hint.Text = I18n.T("boost.hint");

            mainBtn = new FlatButton();
            mainBtn.Text = I18n.T("boost.apply");
            mainBtn.Click += delegate { Host.ApplyBoost(); };

            hero.Controls.Add(stateLbl);
            hero.Controls.Add(gameLbl);
            hero.Controls.Add(gameSub);
            hero.Controls.Add(hint);
            hero.Controls.Add(mainBtn);

            optTitle = Title(I18n.T("boost.opt.title"));
            optCard.Controls.Add(optTitle);

            string[] keys = { "priority", "power", "timer", "dvr", "mem", "bg", "affinity", "irq", "profile" };
            for (int i = 0; i < OptCount; i++)
            {
                names[i] = new TextLabel();
                names[i].Size = 10f;
                names[i].Style = FontStyle.Bold;
                names[i].Ellipsis = true;
                names[i].Text = I18n.T("boost." + keys[i]);

                subs[i] = new TextLabel();
                subs[i].Size = 8.2f;
                subs[i].ForeColor = Theme.Sub;
                subs[i].Ellipsis = true;
                subs[i].Text = I18n.T("boost." + keys[i] + ".sub");

                sw[i] = new Toggle();
                sw[i].SetSilent(OptionValue(host.St, i));
                int idx = i;
                sw[i].CheckedChanged += delegate
                {
                    if (idx == 0) Host.St.BoostPriority = sw[0].Checked;
                    else if (idx == 1) Host.St.BoostPowerPlan = sw[1].Checked;
                    else if (idx == 2) Host.St.BoostTimer = sw[2].Checked;
                    else if (idx == 3) Host.St.BoostDvr = sw[3].Checked;
                    else if (idx == 4) Host.St.BoostMem = sw[4].Checked;
                    else if (idx == 5) Host.St.BoostBackground = sw[5].Checked;
                    else if (idx == 6) Host.St.BoostAffinity = sw[6].Checked;
                    else if (idx == 7) Host.St.BoostIrq = sw[7].Checked;
                    else Host.St.BoostProfile = sw[8].Checked;
                    Host.SaveSettingsSoon();
                };

                optCard.Controls.Add(names[i]);
                optCard.Controls.Add(subs[i]);
                optCard.Controls.Add(sw[i]);
                // 开关要压在最上层:标签是不透明绘制(整块铺卡片色),
                // 只要它和开关有任何重叠,开关被盖住的那几行就会被抹平,底部圆角就没了。
                sw[i].BringToFront();
            }

            resTitle = Title(I18n.T("boost.result.title"));
            resText = new TextLabel();
            resText.Size = 9f;
            resText.ForeColor = Theme.Sub;
            resText.Wrap = true;
            resText.Align = ContentAlignment.TopLeft;
            resText.Text = I18n.T("boost.result.empty");

            trimBtn = new FlatButton();
            trimBtn.Primary = false;
            trimBtn.FontPt = 8.5f;
            trimBtn.Text = I18n.T("boost.mem");
            trimBtn.Click += delegate { Host.RunTrim(); };

            resCard.Controls.Add(resTitle);
            resCard.Controls.Add(resText);
            resCard.Controls.Add(trimBtn);

            Controls.Add(hero);
            Controls.Add(optCard);
            Controls.Add(resCard);

            SyncState();
            LayoutCards();
        }

        private static bool OptionValue(Settings st, int i)
        {
            switch (i)
            {
                case 0: return st.BoostPriority;
                case 1: return st.BoostPowerPlan;
                case 2: return st.BoostTimer;
                case 3: return st.BoostDvr;
                case 4: return st.BoostMem;
                case 5: return st.BoostBackground;
                case 6: return st.BoostAffinity;
                case 7: return st.BoostIrq;
                default: return st.BoostProfile;
            }
        }

        private Card NewCard()
        {
            var c = new Card();
            // 卡片自身用卡片色:子控件继承同一个 BackColor,
            // 自绘标签铺底时才不会在卡片上打出背景色的方块。
            c.BackColor = Theme.Card;
            return c;
        }

        private static TextLabel Title(string text)
        {
            var l = new TextLabel();
            l.Size = 9.5f;
            l.Style = FontStyle.Bold;
            l.ForeColor = Theme.Sub;
            l.Text = text;
            return l;
        }

        // ---------------- Actions 调用 ----------------

        public void SetBusy(bool busy)
        {
            mainBtn.Busy = busy;
            mainBtn.Text = busy ? I18n.T("boost.working")
                : (Host.Boost != null && Host.Boost.Active ? I18n.T("boost.restore") : I18n.T("boost.apply"));
            mainBtn.Enabled = true;
        }

        public void SetTrimBusy(bool busy) { trimBtn.Busy = busy; }

        public void SetResult(string text)
        {
            resText.Text = text ?? "";
            resText.ForeColor = Theme.Text;
        }

        /// <summary>按引擎状态刷新按钮文案与状态行。</summary>
        public void SyncState()
        {
            bool active = Host.Boost != null && Host.Boost.Active;
            lastBoosted = active;
            stateLbl.Text = active ? I18n.T("boost.state.active") : I18n.T("boost.state.idle");
            stateLbl.ForeColor = active ? Theme.Accent : Theme.Sub;
            mainBtn.Text = active ? I18n.T("boost.restore") : I18n.T("boost.apply");
            if (!active) resText.ForeColor = Theme.Sub;
        }

        // ---------------- 每秒刷新 ----------------

        public override void Refresh(SysSample s)
        {
            bool active = Host.Boost != null && Host.Boost.Active;
            if (active != lastBoosted) { SyncState(); return; }

            // 取不到进程名(管理员/反作弊游戏)时也要显示成"已检测到游戏",
            // 否则明明认出了全屏游戏,界面还在说"未检测到游戏"。
            string game = "";
            if (active) game = Host.Boost.GameName.Length > 0 ? Host.Boost.GameName : I18n.T("mon.game.generic");
            else if (s != null && s.GameRecent)
                game = s.GameName.Length > 0 ? s.GameName : I18n.T("mon.game.generic");

            if (game == lastGame) return;
            lastGame = game;

            if (game.Length > 0)
            {
                gameLbl.Text = game;
                gameLbl.ForeColor = Theme.Text;
                if (active) gameSub.Text = I18n.T("boost.since", Since());
                else if (!MemEngine.IsAdmin) gameSub.Text = I18n.T("boost.hint.admin");
                else gameSub.Text = I18n.T("boost.hint");
            }
            else
            {
                gameLbl.Text = I18n.T("boost.nogame");
                gameLbl.ForeColor = Theme.Sub;
                gameSub.Text = I18n.T("boost.nogame.sub");
            }
        }

        private string Since()
        {
            if (Host.Boost == null || Host.Boost.StartedAt == DateTime.MinValue) return "0:00";
            TimeSpan t = DateTime.Now - Host.Boost.StartedAt;
            if (t.TotalSeconds < 0) t = TimeSpan.Zero;
            return ((int)t.TotalMinutes).ToString() + ":" + t.Seconds.ToString("00");
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
            int gap = Ui.Px(12);
            int heroH = Ui.Px(150);
            // 结果卡按内容给固定高度,剩余高度全部让给优化项卡(6 个开关会随卡片均分),避免底部留出大片空白。
            int resH = Ui.Px(168);
            int optH = Height - heroH - resH - gap * 2;
            if (optH < Ui.Px(220)) { optH = Ui.Px(220); resH = Math.Max(Ui.Px(90), Height - heroH - optH - gap * 2); }

            hero.SetBounds(0, 0, Width, heroH);
            optCard.SetBounds(0, heroH + gap, Width, optH);
            resCard.SetBounds(0, heroH + optH + gap * 2, Width, resH);

            int pad = Ui.Px(18);
            int textW = Width - pad * 2 - Ui.Px(200);
            stateLbl.SetBounds(pad, Ui.Px(14), Ui.Px(200), Ui.Px(18));
            gameLbl.SetBounds(pad, Ui.Px(34), textW, Ui.Px(30));
            gameSub.SetBounds(pad, Ui.Px(66), textW, Ui.Px(18));
            hint.SetBounds(pad, Ui.Px(88), textW, heroH - Ui.Px(96));

            int bw = Ui.Px(150), bh = Ui.Px(46);
            mainBtn.SetBounds(Width - pad - bw, (heroH - bh) / 2, bw, bh);

            optTitle.SetBounds(pad, Ui.Px(12), Width - pad * 2, Ui.Px(20));

            int cw = (Width - pad * 2 - gap * 2) / 3;
            // 三列三行:行距设上限后整组垂直居中,窗口再高也不会把开关拉成大空洞。
            int rh = Math.Min((optH - Ui.Px(44) - gap * 2) / 3, Ui.Px(84));
            int gridY = Ui.Px(40) + Math.Max(0, (optH - Ui.Px(44) - rh * 2 - Ui.Px(62)) / 2);
            for (int i = 0; i < OptCount; i++)
            {
                int col = i % 3, row = i / 3;
                int x = pad + col * (cw + gap);
                int y = gridY + row * rh;
                names[i].SetBounds(x, y, cw - Ui.Px(54), Ui.Px(20));
                sw[i].SetBounds(x + cw - Ui.Px(48), y - Ui.Px(2), Ui.Px(44), Ui.Px(24));
                subs[i].SetBounds(x, y + Ui.Px(20), cw - Ui.Px(54), Ui.Px(18));
            }

            resTitle.SetBounds(pad, Ui.Px(12), Ui.Px(160), Ui.Px(20));
            int tw = Ui.Px(96), th = Ui.Px(26);
            trimBtn.SetBounds(Width - pad - tw, Ui.Px(9), tw, th);
            resText.SetBounds(pad, Ui.Px(38), Width - pad * 2, resH - Ui.Px(48));
        }
    }

    // =====================================================================
    //  监测页
    // =====================================================================
    public class MonitorPage : Page
    {
        private readonly Card cpuCard, gpuCard, fpsCard, ramCard, gpuInfoCard;
        private readonly RingGauge cpuRing, gpuRing;
        private readonly TextLabel cpuTitle, gpuTitle, fpsTitle, fpsBig, fpsUnit, fpsLow;
        private readonly Sparkline fpsSpark;
        private readonly TextLabel ramTitle, ramPct, ramSub, ramFree, status;
        private readonly Meter ramMeter;
        private readonly TextLabel infoTitle;
        private readonly TextLabel[] infoCaption = new TextLabel[4];
        private readonly TextLabel[] infoValue = new TextLabel[4];
        // 页面 200ms 刷一次让帧率跟得上,但曲线窗口是 60 个点,按 1 秒采样才是 60 秒历史。
        private DateTime lastSpark = DateTime.MinValue;

        public MonitorPage(MainForm host) : base(host)
        {
            cpuCard = NewCard();
            gpuCard = NewCard();
            fpsCard = NewCard();
            ramCard = NewCard();
            gpuInfoCard = NewCard();

            cpuTitle = Title(I18n.T("mon.cpu"));
            cpuRing = new RingGauge();
            cpuCard.Controls.Add(cpuTitle);
            cpuCard.Controls.Add(cpuRing);

            gpuTitle = Title(I18n.T("mon.gpu"));
            gpuRing = new RingGauge();
            gpuCard.Controls.Add(gpuTitle);
            gpuCard.Controls.Add(gpuRing);

            fpsTitle = Title(I18n.T("mon.fps"));
            fpsBig = new TextLabel();
            fpsBig.Size = 32f;
            fpsBig.Style = FontStyle.Bold;
            fpsBig.Text = "—";
            fpsUnit = new TextLabel();
            fpsUnit.Size = 10f;
            fpsUnit.ForeColor = Theme.Sub;
            fpsUnit.Text = "FPS";
            fpsLow = new TextLabel();
            fpsLow.Size = 9f;
            fpsLow.ForeColor = Theme.Sub;
            fpsSpark = new Sparkline();
            fpsSpark.Capacity = 90;
            fpsCard.Controls.Add(fpsTitle);
            fpsCard.Controls.Add(fpsBig);
            fpsCard.Controls.Add(fpsUnit);
            fpsCard.Controls.Add(fpsLow);
            fpsCard.Controls.Add(fpsSpark);

            ramTitle = Title(I18n.T("mon.ram"));
            ramPct = new TextLabel();
            ramPct.Size = 20f;
            ramPct.Style = FontStyle.Bold;
            ramPct.Text = "—";
            ramSub = new TextLabel();
            ramSub.Size = 9f;
            ramSub.ForeColor = Theme.Sub;
            ramSub.Ellipsis = true;
            ramFree = new TextLabel();
            ramFree.Size = 8.5f;
            ramFree.ForeColor = Theme.Faint;
            ramFree.Ellipsis = true;
            ramMeter = new Meter();
            ramCard.Controls.Add(ramTitle);
            ramCard.Controls.Add(ramPct);
            ramCard.Controls.Add(ramSub);
            ramCard.Controls.Add(ramFree);
            ramCard.Controls.Add(ramMeter);

            infoTitle = Title(I18n.T("mon.gpuinfo"));
            gpuInfoCard.Controls.Add(infoTitle);

            string[] caps = { I18n.T("mon.temp"), I18n.T("mon.clock"), I18n.T("mon.power"), I18n.T("mon.vram") };
            for (int i = 0; i < 4; i++)
            {
                infoCaption[i] = new TextLabel();
                infoCaption[i].Size = 8.5f;
                infoCaption[i].ForeColor = Theme.Sub;
                infoCaption[i].Text = caps[i];
                infoCaption[i].Align = ContentAlignment.MiddleCenter;

                infoValue[i] = new TextLabel();
                infoValue[i].Size = 13f;
                infoValue[i].Style = FontStyle.Bold;
                infoValue[i].Text = "—";
                infoValue[i].Align = ContentAlignment.MiddleCenter;

                gpuInfoCard.Controls.Add(infoCaption[i]);
                gpuInfoCard.Controls.Add(infoValue[i]);
            }

            status = new TextLabel();
            status.Size = 9f;
            status.ForeColor = Theme.Sub;
            status.Align = ContentAlignment.MiddleCenter;
            status.Text = I18n.T("mon.nogame");

            Controls.Add(cpuCard);
            Controls.Add(gpuCard);
            Controls.Add(fpsCard);
            Controls.Add(ramCard);
            Controls.Add(gpuInfoCard);
            Controls.Add(status);

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

        private static TextLabel Title(string text)
        {
            var l = new TextLabel();
            l.Size = 9.5f;
            l.Style = FontStyle.Bold;
            l.ForeColor = Theme.Sub;
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
            int gap = Ui.Px(14);
            int row2 = Ui.Px(196);
            int row1 = Height - row2 - gap - Ui.Px(34);
            int cardW = (Width - gap * 2) / 3;

            cpuCard.SetBounds(0, 0, cardW, row1);
            gpuCard.SetBounds(cardW + gap, 0, cardW, row1);
            fpsCard.SetBounds((cardW + gap) * 2, 0, Width - (cardW + gap) * 2, row1);

            int y2 = row1 + gap;
            int ramW = (Width - gap) / 2;
            ramCard.SetBounds(0, y2, ramW, row2);
            gpuInfoCard.SetBounds(ramW + gap, y2, Width - ramW - gap, row2);
            status.SetBounds(0, y2 + row2 + Ui.Px(6), Width, Ui.Px(22));

            int pad = Ui.Px(16);
            int fw = Width - (cardW + gap) * 2;

            cpuTitle.SetBounds(pad, Ui.Px(12), cardW - pad * 2, Ui.Px(20));
            cpuRing.SetBounds(Ui.Px(6), Ui.Px(30), cardW - Ui.Px(12), row1 - Ui.Px(38));

            gpuTitle.SetBounds(pad, Ui.Px(12), cardW - pad * 2, Ui.Px(20));
            gpuRing.SetBounds(Ui.Px(6), Ui.Px(30), cardW - Ui.Px(12), row1 - Ui.Px(38));

            fpsTitle.SetBounds(pad, Ui.Px(12), fw - pad * 2, Ui.Px(20));
            fpsBig.SetBounds(pad, Ui.Px(26), Ui.Px(116), Ui.Px(70));
            fpsUnit.SetBounds(pad + Ui.Px(118), Ui.Px(62), Ui.Px(60), Ui.Px(22));
            fpsLow.SetBounds(pad, Ui.Px(100), fw - pad * 2, Ui.Px(20));
            int sparkY = Ui.Px(128);
            fpsSpark.SetBounds(pad, sparkY, fw - pad * 2, Math.Max(Ui.Px(30), row1 - sparkY - Ui.Px(14)));

            // 内存:整块内容在标题下方垂直居中
            ramTitle.SetBounds(pad, Ui.Px(12), ramW - pad * 2, Ui.Px(20));
            int ramY = Ui.Px(40) + Math.Max(0, (row2 - Ui.Px(54) - Ui.Px(100)) / 2);
            ramPct.SetBounds(pad, ramY, Ui.Px(104), Ui.Px(36));
            ramSub.SetBounds(pad + Ui.Px(108), ramY + Ui.Px(10), ramW - pad * 2 - Ui.Px(108), Ui.Px(22));
            ramMeter.SetBounds(pad, ramY + Ui.Px(52), ramW - pad * 2, Ui.Px(14));
            ramFree.SetBounds(pad, ramY + Ui.Px(74), ramW - pad * 2, Ui.Px(18));

            // 显卡详情:四列,整块垂直居中
            int infoW = Width - ramW - gap;
            infoTitle.SetBounds(pad, Ui.Px(12), infoW - pad * 2, Ui.Px(20));
            int iw = infoW / 4;
            int blockY = Ui.Px(42) + Math.Max(0, (row2 - Ui.Px(54) - Ui.Px(70)) / 2);
            for (int i = 0; i < 4; i++)
            {
                infoCaption[i].SetBounds(i * iw, blockY, iw, Ui.Px(18));
                infoValue[i].SetBounds(i * iw, blockY + Ui.Px(22), iw, Ui.Px(30));
            }
        }

        public override void Refresh(SysSample s)
        {
            if (s == null) return;

            string threads = Environment.ProcessorCount.ToString() + (I18n.IsEn ? " threads" : " 线程");
            cpuRing.SetValue(s.Cpu < 0 ? 0 : s.Cpu / 100.0, s.Cpu < 0 ? I18n.T("mon.none") : threads);

            string gpuSub = I18n.T("mon.none");
            if (s.GpuTemp >= 0) gpuSub = s.GpuTemp.ToString("0") + "°C";
            else if (s.GpuName.Length > 0) gpuSub = Shorten(s.GpuName, 18);
            gpuRing.SetValue(s.Gpu < 0 ? 0 : s.Gpu / 100.0, gpuSub);

            double fps = Host.Fps.Fps;
            double low = Host.Fps.Low1;
            fpsBig.Text = fps > 0 ? fps.ToString("0") : "—";
            // 没有帧率数据时用弱化色,避免那条占位横杠看起来像一道突兀的白线。
            fpsBig.ForeColor = fps > 0 ? Theme.Text : Theme.Faint;
            fpsLow.Text = fps > 0 ? I18n.T("mon.low", low > 0 ? low.ToString("0") : "—") : FpsHint();
            if (fps > 0)
            {
                DateTime now = DateTime.UtcNow;
                if ((now - lastSpark).TotalSeconds >= 1)
                {
                    lastSpark = now;
                    fpsSpark.Push((float)fps);
                }
            }

            ramPct.Text = s.RamPct >= 0 ? s.RamPct.ToString("0") + "%" : "—";
            ramSub.Text = Fmt.Pair(s.RamUsed, s.RamTotal);
            ramFree.Text = s.RamTotal > s.RamUsed
                ? I18n.T("mon.ram.free", Fmt.Bytes(s.RamTotal - s.RamUsed)) : "";
            if (s.RamPct >= 0) ramMeter.SetValue(s.RamPct / 100.0);

            infoValue[0].Text = s.GpuTemp >= 0 ? s.GpuTemp.ToString("0") + "°C" : "—";
            infoValue[1].Text = s.GpuClock >= 0 ? s.GpuClock.ToString("0") + " MHz" : "—";
            infoValue[2].Text = s.GpuPower >= 0 ? s.GpuPower.ToString("0") + " W" : "—";
            infoValue[3].Text = s.VramTotal > 0
                ? (s.VramUsed / 1073741824.0).ToString("0.0") + " / " + (s.VramTotal / 1073741824.0).ToString("0.0") + " GB"
                : "—";

            if (s.GameRecent)
                status.Text = I18n.T("mon.game") + ": "
                    + (s.GameName.Length > 0 ? s.GameName : I18n.T("mon.game.generic"))
                    + (Host.Boost != null && Host.Boost.Active ? "   ·   " + I18n.T("mon.boosted") : "");
            else
                status.Text = I18n.T("mon.nogame") + "   ·   " + I18n.T("mon.hint");
        }

        private string FpsHint()
        {
            if (Host.Fps.Status == FpsStatus.NotInstalled) return I18n.T("mon.fpsmiss");
            // PresentMon 的 ETW 图形事件要管理员权限才收得到,普通权限下永远是空数据,
            // 这里直说原因,免得看起来像坏了。
            if (!MemEngine.IsAdmin) return I18n.T("mon.fpswait");
            if (Host.Fps.Status == FpsStatus.NeedAdmin) return I18n.T("mon.fpswait");
            return I18n.T("mon.fps") + " —";
        }

        private static string Shorten(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }

    // =====================================================================
    //  叠加层页
    // =====================================================================
    public class OverlayPage : Page
    {
        private readonly Card ovCard, itemCard, hintCard;
        private readonly TextLabel ovTitle, onTitle, onSub, lockTitle, lockSub, hotkey;
        private readonly TextLabel styleTitle, itemTitle, itemNote, hintTitle;
        private readonly Toggle onToggle, lockToggle;
        private readonly Segmented style;
        private readonly Slider scale;
        private readonly Chip[] chips = new Chip[5];

        private static readonly string[] ItemKeys = { "fps", "cpu", "gpu", "temp", "ram" };

        public OverlayPage(MainForm host) : base(host)
        {
            ovCard = NewCard();
            itemCard = NewCard();
            hintCard = NewCard();

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

            Controls.Add(ovCard);
            Controls.Add(itemCard);
            Controls.Add(hintCard);

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
                case 1: return st.ShowCpu;
                case 2: return st.ShowGpu;
                case 3: return st.ShowTemp;
                default: return st.ShowRam;
            }
        }

        private static void SetItem(Settings st, int i, bool v)
        {
            switch (i)
            {
                case 0: st.ShowFps = v; break;
                case 1: st.ShowCpu = v; break;
                case 2: st.ShowGpu = v; break;
                case 3: st.ShowTemp = v; break;
                default: st.ShowRam = v; break;
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
            int hintH = Ui.Px(176);
            int cardH = Height - itemH - hintH - gap * 2;
            if (cardH < Ui.Px(272))
            {
                cardH = Ui.Px(272);
                hintH = Math.Max(Ui.Px(140), Height - itemH - cardH - gap * 2);
            }

            ovCard.SetBounds(0, 0, Width, cardH);
            itemCard.SetBounds(0, cardH + gap, Width, itemH);
            hintCard.SetBounds(0, cardH + itemH + gap * 2, Width, hintH);

            int pad = Ui.Px(18);
            int right = Width - pad;
            int toggleW = Ui.Px(48);

            ovTitle.SetBounds(pad, Ui.Px(12), Width - pad * 2, Ui.Px(20));

            // 卡片多出来的高度均摊进三段行距:行与行自然拉开,
            // 而不是在标题下方留一块空、底部再留一块空。
            int extra = Math.Max(0, cardH - Ui.Px(266));
            int top = extra / 5;
            int step = (extra - top) / 3;
            int off1 = top, off2 = top + step, off3 = top + step * 2, off4 = top + step * 3;

            onTitle.SetBounds(pad, Ui.Px(50) + off1, Width - pad * 2 - toggleW - Ui.Px(12), Ui.Px(20));
            onSub.SetBounds(pad, Ui.Px(70) + off1, Width - pad * 2 - toggleW - Ui.Px(12), Ui.Px(18));
            onToggle.SetBounds(right - toggleW, Ui.Px(52) + off1, toggleW, Ui.Px(24));

            int segW = Ui.Px(260);
            styleTitle.SetBounds(pad, Ui.Px(108) + off2, Ui.Px(90), Ui.Px(28));
            style.SetBounds(right - segW, Ui.Px(104) + off2, segW, Ui.Px(32));

            scale.SetBounds(pad, Ui.Px(152) + off3, Width - pad * 2, Ui.Px(44));

            lockTitle.SetBounds(pad, Ui.Px(214) + off4, Width - pad * 2 - toggleW - Ui.Px(12), Ui.Px(20));
            lockSub.SetBounds(pad, Ui.Px(234) + off4, Width - pad * 2 - toggleW - Ui.Px(12), Ui.Px(18));
            lockToggle.SetBounds(right - toggleW, Ui.Px(216) + off4, toggleW, Ui.Px(24));

            hintTitle.SetBounds(pad, Ui.Px(12), Width - pad * 2, Ui.Px(20));
            hotkey.SetBounds(pad, Ui.Px(46), Width - pad * 2, Ui.Px(40));
            itemNote.SetBounds(pad, Ui.Px(92), Width - pad * 2, Math.Max(Ui.Px(30), hintH - Ui.Px(104)));

            itemTitle.SetBounds(pad, Ui.Px(12), Width - pad * 2, Ui.Px(20));
            int n = chips.Length;
            int cw = (Width - pad * 2 - gap * (n - 1)) / n;
            int chipY = (itemH - Ui.Px(34)) / 2 + Ui.Px(8);
            for (int i = 0; i < n; i++)
                chips[i].SetBounds(pad + i * (cw + gap), chipY, cw, Ui.Px(34));
        }
    }

    // =====================================================================
    //  设置页
    // =====================================================================
    public class SettingsPage : Page
    {
        private readonly Card genCard, memCard, fpsCard, adminCard, aboutCard, hotCard;
        private readonly Segmented segLang, segTheme;
        private readonly Toggle tgAuto, tgTray, tgMemAuto;
        private readonly Slider sldThreshold;
        private readonly TextLabel memInfo, fpsStatus, fpsNote, adminStatus, adminNote, aboutName, aboutNotice, aboutEnv;
        private readonly FlatButton btnFps, btnAdmin, btnLog;
        private readonly TextLabel langTitle, themeTitle, autoTitle, autoSub, trayTitle, traySub;
        private readonly TextLabel memAutoTitle, memAutoSub;
        private readonly TextLabel hotOvTitle, hotOvKey, hotBoostTitle, hotBoostKey;

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
            btnAdmin = new FlatButton();
            btnAdmin.Primary = false;
            btnAdmin.FontPt = 9f;
            btnAdmin.Text = I18n.T("set.admin.restart");
            btnAdmin.Click += delegate { MemEngine.RestartElevated(""); };

            adminCard.Controls.Add(adminStatus);
            adminCard.Controls.Add(adminNote);
            adminCard.Controls.Add(btnAdmin);

            // 快捷键
            hotOvTitle = RowTitle(I18n.T("set.hotkey.overlay"));
            hotOvKey = RowKey("Ctrl + Alt + O");
            hotBoostTitle = RowTitle(I18n.T("set.hotkey.boost"));
            hotBoostKey = RowKey("Ctrl + Alt + B");
            hotCard.Controls.Add(hotOvTitle);
            hotCard.Controls.Add(hotOvKey);
            hotCard.Controls.Add(hotBoostTitle);
            hotCard.Controls.Add(hotBoostKey);

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

            int genH = Ui.Px(230);
            int hotH = Ui.Px(122);
            int aboutH = Height - genH - hotH - gap * 2;
            if (aboutH < Ui.Px(180))
            {
                aboutH = Ui.Px(180);
                genH = Math.Max(Ui.Px(200), Height - hotH - aboutH - gap * 2);
            }

            int memH = Ui.Px(168);
            int fpsH = Ui.Px(176);
            int adminH = Height - memH - fpsH - gap * 2;
            if (adminH < Ui.Px(150))
            {
                adminH = Ui.Px(150);
                memH = Math.Max(Ui.Px(140), Height - fpsH - adminH - gap * 2);
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
            langTitle.SetBounds(pad, Ui.Px(44), Ui.Px(80), Ui.Px(28));
            segLang.SetBounds(colW - pad - segW, Ui.Px(42), segW, Ui.Px(30));
            themeTitle.SetBounds(pad, Ui.Px(86), Ui.Px(80), Ui.Px(28));
            segTheme.SetBounds(colW - pad - segW, Ui.Px(84), segW, Ui.Px(30));

            autoTitle.SetBounds(pad, Ui.Px(130), colW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(20));
            autoSub.SetBounds(pad, Ui.Px(150), colW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(18));
            tgAuto.SetBounds(colW - pad - toggleW, Ui.Px(132), toggleW, Ui.Px(24));

            trayTitle.SetBounds(pad, Ui.Px(184), colW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(20));
            traySub.SetBounds(pad, Ui.Px(204), colW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(18));
            tgTray.SetBounds(colW - pad - toggleW, Ui.Px(186), toggleW, Ui.Px(24));

            PlaceHeader(memCard, pad);
            memAutoTitle.SetBounds(pad, Ui.Px(44), rightW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(20));
            memAutoSub.SetBounds(pad, Ui.Px(64), rightW - pad * 2 - toggleW - Ui.Px(10), Ui.Px(18));
            tgMemAuto.SetBounds(rightW - pad - toggleW, Ui.Px(46), toggleW, Ui.Px(24));
            sldThreshold.SetBounds(pad, Ui.Px(92), rightW - pad * 2, Ui.Px(44));
            memInfo.SetBounds(pad, Ui.Px(138), rightW - pad * 2, Ui.Px(18));

            PlaceHeader(fpsCard, pad);
            fpsStatus.SetBounds(pad, Ui.Px(44), rightW - pad * 2, Ui.Px(20));
            fpsNote.SetBounds(pad, Ui.Px(66), rightW - pad * 2, Ui.Px(40));
            int bw = Ui.Px(132), bh = Ui.Px(30);
            btnFps.SetBounds(rightW - pad - bw, fpsH - bh - Ui.Px(14), bw, bh);

            // 管理员卡与关于卡会吃掉窗口剩余的整块高度:把多出来的空白
            // 均摊进各行之间的间距,内容铺满卡片,标题下方不会空一大截。
            PlaceHeader(adminCard, pad);
            int adminExtra = Math.Max(0, adminH - Ui.Px(210));
            adminStatus.SetBounds(pad, Ui.Px(44) + adminExtra / 8, rightW - pad * 2, Ui.Px(22));
            adminNote.SetBounds(pad, Ui.Px(70) + adminExtra * 3 / 8, rightW - pad * 2, Ui.Px(56));
            btnAdmin.SetBounds(rightW - pad - bw, Ui.Px(140) + adminExtra * 7 / 8, bw, bh);

            PlaceHeader(hotCard, pad);
            int keyW = Ui.Px(150);
            hotOvTitle.SetBounds(pad, Ui.Px(44), colW - pad * 2 - keyW - Ui.Px(8), Ui.Px(26));
            hotOvKey.SetBounds(colW - pad - keyW, Ui.Px(44), keyW, Ui.Px(26));
            hotBoostTitle.SetBounds(pad, Ui.Px(76), colW - pad * 2 - keyW - Ui.Px(8), Ui.Px(26));
            hotBoostKey.SetBounds(colW - pad - keyW, Ui.Px(76), keyW, Ui.Px(26));

            PlaceHeader(aboutCard, pad);
            int aboutExtra = Math.Max(0, aboutH - Ui.Px(228));
            aboutName.SetBounds(pad, Ui.Px(44) + aboutExtra / 8, colW - pad * 2, Ui.Px(22));
            aboutNotice.SetBounds(pad, Ui.Px(72) + aboutExtra * 3 / 8, colW - pad * 2, Ui.Px(44));
            aboutEnv.SetBounds(pad, Ui.Px(122) + aboutExtra * 5 / 8, colW - pad * 2, Ui.Px(40));
            btnLog.SetBounds(pad, Ui.Px(176) + aboutExtra * 7 / 8, bw, bh);
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
        private readonly Segmented mode;
        private readonly Chip[] chips = new Chip[Filter.PresetKeys.Length];
        private readonly FilterPreview preview;
        private readonly Slider sIntensity, sBright, sContrast, sSat, sWarm;
        private readonly FlatButton resetBtn;

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
            for (int i = 0; i < chips.Length; i++)
            {
                chips[i] = new Chip();
                chips[i].Text = I18n.T(Filter.PresetKeys[i]);
                chips[i].FontPt = 9f;
                chips[i].Selected = host.St.FilterPreset == i;
                int idx = i;
                chips[i].Click += delegate { PickPreset(idx); };
                presetCard.Controls.Add(chips[i]);
            }

            customTitle = Header(I18n.T("flt.custom"));
            resetBtn = new FlatButton();
            resetBtn.Primary = false;
            resetBtn.FontPt = 8.5f;
            resetBtn.Text = I18n.T("flt.reset");
            resetBtn.Click += delegate { ResetCustom(); };

            sIntensity = MakeSlider(I18n.T("flt.intensity"), 0, 100, "%", host.St.FilterIntensity);
            sBright = MakeSlider(I18n.T("flt.brightness"), -100, 100, "", host.St.FilterBrightness);
            sContrast = MakeSlider(I18n.T("flt.contrast"), -100, 100, "", host.St.FilterContrast);
            sSat = MakeSlider(I18n.T("flt.saturation"), -100, 100, "", host.St.FilterSaturation);
            sWarm = MakeSlider(I18n.T("flt.warmth"), -100, 100, "", host.St.FilterWarmth);

            customCard.Controls.Add(customTitle);
            customCard.Controls.Add(resetBtn);
            customCard.Controls.Add(sIntensity);
            customCard.Controls.Add(sBright);
            customCard.Controls.Add(sContrast);
            customCard.Controls.Add(sSat);
            customCard.Controls.Add(sWarm);

            Controls.Add(modeCard);
            Controls.Add(presetCard);
            Controls.Add(customCard);

            UpdateNote();
            UpdatePreview();
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
            Host.St.FilterBrightness = sBright.Value;
            Host.St.FilterContrast = sContrast.Value;
            Host.St.FilterSaturation = sSat.Value;
            Host.St.FilterWarmth = sWarm.Value;
            Host.SaveSettingsSoon();
            Host.ApplyFilter();
            UpdatePreview();
        }

        private void ResetCustom()
        {
            sIntensity.SetSilent(100);
            sBright.SetSilent(0);
            sContrast.SetSilent(0);
            sSat.SetSilent(0);
            sWarm.SetSilent(0);
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

        private void LayoutCards()
        {
            if (Width <= 0 || Height <= 0) return;
            int gap = Ui.Px(12);
            int modeH = Ui.Px(158);
            int presetH = Ui.Px(96);
            int customH = Height - modeH - presetH - gap * 2;
            if (customH < Ui.Px(250))
            {
                customH = Ui.Px(250);
                modeH = Math.Max(Ui.Px(140), Height - presetH - customH - gap * 2);
            }

            modeCard.SetBounds(0, 0, Width, modeH);
            presetCard.SetBounds(0, modeH + gap, Width, presetH);
            customCard.SetBounds(0, modeH + presetH + gap * 2, Width, customH);

            int pad = Ui.Px(18);
            int segW = Ui.Px(330), segH = Ui.Px(32);
            modeTitle.SetBounds(pad, Ui.Px(12), Width - pad * 2 - segW - Ui.Px(12), Ui.Px(24));
            mode.SetBounds(Width - pad - segW, Ui.Px(9), segW, segH);

            int prevY = Ui.Px(48);
            preview.SetBounds(pad, prevY, Width - pad * 2, Ui.Px(56));
            modeNote.SetBounds(pad, prevY + Ui.Px(56) + Ui.Px(8),
                Width - pad * 2, Math.Max(Ui.Px(20), modeH - prevY - Ui.Px(72)));

            presetTitle.SetBounds(pad, Ui.Px(12), Width - pad * 2, Ui.Px(20));
            int n = chips.Length;
            int cgap = Ui.Px(8);
            int cw = (Width - pad * 2 - cgap * (n - 1)) / n;
            for (int i = 0; i < n; i++)
                chips[i].SetBounds(pad + i * (cw + cgap), Ui.Px(42), cw, Ui.Px(36));

            customTitle.SetBounds(pad, Ui.Px(12), Ui.Px(200), Ui.Px(20));
            int bw = Ui.Px(96), bh = Ui.Px(26);
            resetBtn.SetBounds(Width - pad - bw, Ui.Px(9), bw, bh);

            Slider[] all = { sIntensity, sBright, sContrast, sSat, sWarm };
            int sh = Ui.Px(44), sy = Ui.Px(40);
            // 间距按卡片剩余高度分摊,避免自定义卡底部留出大片空白。
            int sgap = (customH - sy - sh * all.Length - Ui.Px(14)) / (all.Length - 1);
            if (sgap > Ui.Px(26)) sgap = Ui.Px(26);
            if (sgap < Ui.Px(4)) sgap = Ui.Px(4);
            for (int i = 0; i < all.Length; i++)
                all[i].SetBounds(pad, sy + i * (sh + sgap), Width - pad * 2, sh);
        }
    }

    /// <summary>滤镜实时预览:同一组色标经过当前颜色矩阵变换后画成渐变。</summary>
    public class FilterPreview : Ctl
    {
        private float[] matrix = Filter.Identity();

        private static readonly Color[] Swatches =
        {
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

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            if (Width < 8 || Height < 8) return;

            int n = Swatches.Length;
            var colors = new Color[n];
            var pos = new float[n];
            for (int i = 0; i < n; i++)
            {
                colors[i] = Filter.Transform(Swatches[i], matrix);
                pos[i] = (float)i / (n - 1);
            }
            var blend = new ColorBlend();
            blend.Colors = colors;
            blend.Positions = pos;

            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Ui.Round(r, Ui.Fp(10)))
            {
                using (var br = new LinearGradientBrush(
                    new RectangleF(0, 0, Math.Max(2, Width), Math.Max(2, Height)),
                    colors[0], colors[n - 1], LinearGradientMode.Horizontal))
                {
                    br.InterpolationColors = blend;
                    g.FillPath(br, p);
                }
                // 底部压一点暗角,看起来像一帧画面而不是纯色卡
                using (var shade = new LinearGradientBrush(
                    new RectangleF(0, Height * 0.55f, Math.Max(2, Width), Math.Max(2, Height * 0.45f)),
                    Color.Transparent, Color.FromArgb(90, 0, 0, 0), LinearGradientMode.Vertical))
                {
                    g.FillPath(shade, p);
                }
                using (var pen = new Pen(Theme.Border, 1f)) g.DrawPath(pen, p);
            }
        }
    }
}