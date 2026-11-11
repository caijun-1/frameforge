using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge.UI
{
    /// <summary>
    /// 无边框主窗口:自绘标题栏 + 四页导航 + 轻提示。
    /// 外壳与布局在本文件,业务动作在 MainForm.Actions.cs,页面内容在 Pages.cs。
    /// </summary>
    public sealed partial class MainForm : Form
    {
        public Settings St;
        public Monitor Mon;
        public FpsEngine Fps;
        public Overlay Ov;
        public BoostEngine Boost;
        public ProfileStore Profiles;
        public SessionStore Sessions;
        public SessionTracker Tracker;
        /// <summary>加速前后帧率对比,只记录数据,不做系统修改。</summary>
        public PerfCompare Perf = new PerfCompare();

        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;

        private readonly bool startMinimized;

        private Segmented nav;
        private IconButton btnLang;
        private IconButton btnTheme;
        private IconButton btnMin;
        private IconButton btnClose;
        private FrameForge.UI.Toast toast;
        private ToolTip tips;

        private Page[] pages;
        private bool[] pageDirty;
        private int activePage;
        private BoostPage boostPage;
        private SettingsPage setPage;
        private ReportPage reportPage;
        private HealthPage healthPage;

        private Timer ui;
        private Timer saveTimer;
        private Timer updateTimer;
        private Timer captureTimer;
        private bool captureGuard;
        private NotifyIcon tray;
        private Icon appIcon;
        private bool trayHinted;
        private string trayTip = "";

        private bool ovShown;
        private bool lastFullscreen;
        private int lastFilterPid = -1;
        private int exclusiveWarnPid = -1;
        private bool trimming;
        private bool downloading;
        private DateTime lastAutoTrim = DateTime.MinValue;
        private DateTime lastPageRefresh = DateTime.MinValue;

        public MainForm(Settings st, bool minimized)
        {
            St = st;
            startMinimized = minimized;

            I18n.Lang = st.Language == "en" ? "en" : "zh";
            Theme.Set(st.Dark);
            Theme.ResetFonts();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            Text = "FrameForge";

            // 自绘窗口也开双缓冲:重建页面、拖动改大小时不会闪出一块没画过的底色。
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            // 先创建句柄,再由窗口句柄读取实际 DPI:Form.DeviceDpi 在部分系统上恒为 96,
            // 会导致文字按 DPI 放大而布局仍按 1x 计算,控件互相压盖。
            IntPtr forceHandle = Handle;
            GC.KeepAlive(forceHandle);
            Ui.S = DpiScale();

            MinimumSize = new Size(Ui.Px(840), Ui.Px(640));
            appIcon = LoadIcon(32);
            Icon = appIcon;

            // CenterScreen 在设置 ClientSize 之前就定好了位置,高分屏上会偏出屏幕;
            // 这里手动按工作区居中。
            StartPosition = FormStartPosition.Manual;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            float maxW = wa.Width / Ui.S - 32;
            float maxH = wa.Height / Ui.S - 32;
            ClientSize = new Size(Ui.Px(Math.Min(1020f, maxW)), Ui.Px(Math.Min(720f, maxH)));
            Location = new Point(wa.Left + Math.Max(0, (wa.Width - Width) / 2),
                wa.Top + Math.Max(0, (wa.Height - Height) / 2));

            Log.Write("ui: dpi=" + (int)Math.Round(Ui.S * 96) + " scale=" + Ui.S.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                + " client=" + ClientSize.Width + "x" + ClientSize.Height
                + " screen=" + wa.Width + "x" + wa.Height
                + " admin=" + (MemEngine.IsAdmin ? 1 : 0) + " dark=" + (Theme.Dark ? 1 : 0));

            BuildTitle();
            BuildPages();
            BuildToast();
            BuildTray();
            BuildTimers();

            Mon = new Monitor();
            Fps = new FpsEngine();
            Ov = new Overlay(St);
            Boost = new BoostEngine();
            Profiles = new ProfileStore();
            Profiles.Load();

            Sessions = new SessionStore();
            Sessions.Load();
            if (reportPage != null) reportPage.Reload();
            Tracker = new SessionTracker(Sessions);
            Tracker.MinSeconds = St.ReportMinSec;
            Tracker.Completed += delegate(SessionRecord r) { OnSessionDone(r); };

            // 启动 6 秒后再查更新:让界面先画出来,也避开开机时的一堆启动项。
            if (St.CheckUpdates)
            {
                updateTimer = new Timer();
                updateTimer.Interval = 6000;
                updateTimer.Tick += delegate
                {
                    updateTimer.Stop();
                    updateTimer.Dispose();
                    updateTimer = null;
                    CheckUpdates(false);
                };
                updateTimer.Start();
            }

            LayoutAll();
        }

        private int TitleH() { return Ui.Px(52); }

        /// <summary>以句柄的真实 DPI 计算缩放系数,失败时退回 DeviceDpi。</summary>
        private float DpiScale()
        {
            uint dpi = 0;
            try { if (IsHandleCreated) dpi = Native.GetDpiForWindow(Handle); } catch { }
            if (dpi < 96) dpi = (uint)DeviceDpi;
            float s = dpi / 96f;
            return s < 1f ? 1f : s;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // 细边框样式:无边框窗口也能拖动边缘改变大小,并带系统阴影
                cp.Style |= WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
                return cp;
            }
        }

        // ---------------- 构建 ----------------

        private void BuildTitle()
        {
            tips = new ToolTip();

            nav = new Segmented();
            nav.Items = NavItems();
            nav.SetSilent(0);
            nav.Changed += delegate { ShowPage(nav.Selected); };

            btnLang = new IconButton();
            btnLang.Kind = Glyph.Lang;
            btnLang.LangText = I18n.IsEn ? "中" : "EN";
            btnLang.Click += delegate { ToggleLanguage(); };

            btnTheme = new IconButton();
            btnTheme.Kind = Glyph.Theme;
            btnTheme.Click += delegate { ToggleTheme(); };

            btnMin = new IconButton();
            btnMin.Kind = Glyph.Minimize;
            btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };

            btnClose = new IconButton();
            btnClose.Kind = Glyph.Close;
            btnClose.Click += delegate { CloseRequested(); };

            Controls.Add(nav);
            Controls.Add(btnLang);
            Controls.Add(btnTheme);
            Controls.Add(btnMin);
            Controls.Add(btnClose);

            tips.SetToolTip(btnLang, I18n.T("title.lang"));
            tips.SetToolTip(btnTheme, I18n.T("title.theme"));
            tips.SetToolTip(btnMin, I18n.T("title.min"));
            tips.SetToolTip(btnClose, I18n.T("title.close"));
        }

        /// <summary>
        /// 只建首屏:其余 5 页标成待建,第一次切过去再建。
        /// 启动时省掉 5 棵控件树的构造和字体测量,常驻内存也少一截。
        /// </summary>
        private void BuildPages()
        {
            // 先建好首屏再整体发布:页面构造期间可能回调 Host.ApplyModeTheme,
            // 那时 pages 里还是空位,提前发布会让它命中 null。
            Page first = CreatePage(0);
            first.Visible = false;

            pages = new Page[6];
            pageDirty = new bool[6];
            for (int i = 0; i < pageDirty.Length; i++) pageDirty[i] = i != 0;
            pages[0] = first;
            Controls.Add(first);
            ShowPage(0);
        }

        /// <summary>按导航序号新建页面,并同步对应的缓存字段。</summary>
        private Page CreatePage(int i)
        {
            switch (i)
            {
                case 0: boostPage = new BoostPage(this); return boostPage;
                case 1: return new FilterPage(this);
                case 2: return new OverlayPage(this);
                case 3: reportPage = new ReportPage(this); return reportPage;
                case 4: healthPage = new HealthPage(this); return healthPage;
                default: setPage = new SettingsPage(this); return setPage;
            }
        }

        /// <summary>
        /// 只重建一页:切语言/主题时用。旧页面先隐藏再销毁,避免在原位置留下
        /// 没重绘的空洞;新页面建好后立刻摆位,免得出现一张 0x0 的白板。
        /// </summary>
        private void RebuildPage(int i)
        {
            if (pages == null || i < 0 || i >= pages.Length) return;
            Page old = pages[i];
            // 惰性建页时这里本来就是空的(第一次切过去),只有已经建过的才需要拆。
            if (old != null)
            {
                old.Visible = false;
                Controls.Remove(old);
                old.Dispose();
                if (ReferenceEquals(boostPage, old)) boostPage = null;
                if (ReferenceEquals(setPage, old)) setPage = null;
                if (ReferenceEquals(reportPage, old)) reportPage = null;
                if (ReferenceEquals(healthPage, old)) healthPage = null;
            }
            // 新页构造期间可能回调 Host.ApplyModeTheme 遍历 pages,不能让它碰到已销毁的旧页。
            pages[i] = null;

            Page p = CreatePage(i);
            p.Visible = false;
            Controls.Add(p);
            pages[i] = p;
            pageDirty[i] = false;
            // 新页面拿到尺寸后才会在 OnResize 里摆好内部卡片;不摆位就是一张 0x0 的白板。
            LayoutAll();
        }

        /// <summary>把除当前页以外的页面标记为待重建,真正切过去时再建。</summary>
        private void MarkPagesDirtyExcept(int keep)
        {
            if (pageDirty == null) return;
            for (int i = 0; i < pageDirty.Length; i++) pageDirty[i] = i != keep;
        }

        private void BuildToast()
        {
            toast = new FrameForge.UI.Toast();
            toast.BackColor = Theme.Bg;
            toast.Visible = false;
            Controls.Add(toast);
            toast.BringToFront();
        }

        private void BuildTray()
        {
            // 菜单只留两个动作,其余功能都在主界面里,托盘保持安静。
            var menu = new TrayMenu();
            menu.Add(I18n.T("tray.open"), delegate { RestoreFromTray(); });

            ToolStripMenuItem exitItem = menu.Add(I18n.T("tray.exit"), delegate { Close(); });
            exitItem.Tag = Theme.Danger;

            tray = new NotifyIcon();
            tray.Icon = appIcon;
            tray.Text = "FrameForge";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { RestoreFromTray(); };
            // 托盘图标现在很常见是"点一下打开",双击只是习惯之一,两种都支持。
            tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) RestoreFromTray();
            };
            tray.Visible = true;
        }

        private void BuildTimers()
        {
            ui = new Timer();
            // 200ms 一跳:叠加层与监测页的帧率要跟得上,页面刷新在 OnTick 里按 1 秒节流。
            ui.Interval = 200;
            ui.Tick += delegate { OnTick(); };

            saveTimer = new Timer();
            saveTimer.Interval = 700;
            saveTimer.Tick += delegate { saveTimer.Stop(); St.Save(); };

            // 截图守卫要跟着截图的"按下即开始",200ms 那一跳太慢,单独 150ms 轮询。
            // 判定本身有快路径(看前台进程)兜着,慢路径限流到每秒一次,这里不必省。
            captureTimer = new Timer();
            captureTimer.Interval = 150;
            captureTimer.Tick += delegate { CheckCaptureGuard(); };
        }

        /// <summary>
        /// 截图守卫:截图工具铺开全屏取色层时,先把滤镜还原、叠加层收起。
        /// 全屏颜色矩阵会让部分工具抓到的画面发黑甚至抓不到,置顶的叠加层还会被一起截进去;
        /// 工具退出后(取色层消失)自动恢复,用户不需要手动关滤镜。
        /// </summary>
        private void CheckCaptureGuard()
        {
            bool active = false;
            try { active = Monitor.CaptureActive(); } catch { }
            if (active == captureGuard) return;
            captureGuard = active;
            Log.Write("capture guard: " + (active ? "on" : "off"));
            // 这里在 UI 线程上跑,任何异常都不能让消息循环断掉。
            try { ApplyFilter(); } catch (Exception ex) { Log.Write("guard filter: " + ex.Message); }
            try { SyncOverlay(); } catch (Exception ex) { Log.Write("guard overlay: " + ex.Message); }
        }

        /// <summary>托盘提示跟着检测结果走,收起窗口后也能一眼看到当前在加速哪个游戏。</summary>
        private void UpdateTrayTip(SysSample s)
        {
            string text = "FrameForge";
            if (s != null && s.GamePid > 0)
            {
                string name = s.GameTitle.Length > 0 ? s.GameTitle : s.GameName;
                if (name.Length > 0) text = "FrameForge · " + name;
            }
            if (text == trayTip) return;
            trayTip = text;
            if (text.Length > 63) text = text.Substring(0, 62) + "…";
            if (tray != null) tray.Text = text;
        }

        private string[] NavItems()
        {
            return new string[]
            {
                I18n.T("nav.boost"), I18n.T("nav.filter"),
                I18n.T("nav.overlay"), I18n.T("nav.report"),
                I18n.T("nav.health"), I18n.T("nav.settings")
            };
        }

        // ---------------- 运行 ----------------

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Mon.Start();
            ui.Start();
            captureTimer.Start();
            if (setPage != null) setPage.UpdateStatus();
            SyncOverlay();
            // 放大镜按需初始化(ApplyFilter 内部决定),不再开机就挂着。
            ApplyFilter();
            if (startMinimized) { HideToTray(false); return; }
            if (!St.AskedElevation && !MemEngine.IsAdmin) AskElevationOnce();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkeys();
            ApplyWindowFrame();
        }

        /// <summary>
        /// 无边框窗口仍然保留了一圈 WS_THICKFRAME 的系统边框(拖边缘改大小要用),
        /// DWM 会给这圈边框描一条 1px 的浅色线——在深色界面上就是标题栏上方那道白边。
        /// 这里把边框色改成与背景一致(Win11 22000+),更老的系统退化为深色沉浸模式。
        /// </summary>
        private void ApplyWindowFrame()
        {
            if (!IsHandleCreated) return;
            try
            {
                int dark = Theme.Dark ? 1 : 0;
                if (Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4) != 0)
                    Native.DwmSetWindowAttribute(Handle, 19, ref dark, 4);

                Color bg = Theme.Bg;
                int colorRef = bg.R | (bg.G << 8) | (bg.B << 16);
                // 系统边框直接关掉:DWMWA_COLOR_NONE 比"把边框涂成背景色"更彻底,
                // 不会在圆角外侧留一道由系统强调色描出来的细线。
                int none = unchecked((int)0xFFFFFFFE);
                if (Native.DwmSetWindowAttribute(Handle, 34, ref none, 4) != 0)
                    Native.DwmSetWindowAttribute(Handle, 34, ref colorRef, 4);

                // 客户区铺满后窗口是直角,配上系统阴影看着像一张硬纸板。
                // Win11 让 DWM 按系统圆角裁剪(属性 33,1=直角 2=圆角),最大化时系统会自动改回直角。
                int corner = 2;
                Native.DwmSetWindowAttribute(Handle, 33, ref corner, 4);
            }
            catch { }
        }

        private void OnTick()
        {
            SysSample s = Mon.Current;
            if (s == null) return;

            // 换了游戏或全屏状态变了都要重算:独占全屏走 gamma LUT,窗口/无边框全屏走放大镜,
            // 只跟着 Fullscreen 判断的话,换成一个窗口化游戏会一直停在 LUT 模式。
            if (s.Fullscreen != lastFullscreen || s.GamePid != lastFilterPid)
            {
                lastFullscreen = s.Fullscreen;
                lastFilterPid = s.GamePid;
                SyncOverlay();
                ApplyFilter();
            }

            // 独占全屏的交换链绕过 DWM,再置顶的叠加层也会被游戏盖住。
            // 检测到就提示一次(每局一次),把"看不到数据"变成可操作的提示,
            // 而不是让用户以为叠加层坏了。
            if (s.Fullscreen && St.OverlayOn && s.GamePid > 0 && exclusiveWarnPid != s.GamePid
                && Monitor.IsExclusiveFullscreen(s.GamePid))
            {
                exclusiveWarnPid = s.GamePid;
                Toast(I18n.T("toast.overlay.exclusive"));
            }
            else if (!s.Fullscreen || !St.OverlayOn)
            {
                exclusiveWarnPid = -1;
            }

            // 只要游戏进程还在就保持采集。GameRecent 在切窗口、加载、UAC 弹窗时会短暂变假,
            // 跟着它反复 Stop/Start 会让帧率断流好几秒,赛后记录也会被拆成两段。
            if (s.GamePid > 0) Fps.Track(s.GamePid);
            else Fps.Stop();

            Ov.Feed(s, Fps.Fps, Fps.Low1);
            Tracker.Feed(s, Fps.Fps, Fps.Low1);
            Perf.Feed(Boost != null && Boost.Active, Fps.Fps, Fps.Low1);
            UpdateTrayTip(s);

            CheckBoostAlive();
            MaybeAutoBoost(s);
            // PCBoost 第二层:加速期间每 2 分钟重新把游戏主线程软引导到最空闲的核心。
            if (Boost != null && Boost.Active) { try { Boost.SteerIfDue(); } catch { } }
            AutoTrim(s);

            // 帧率每秒在变,加速页(已合并监测)跟着 200ms 刷新;其它页面没有实时数据,1 秒一次就够,
            // 免得整窗高频重绘白占 CPU。窗口收进托盘或最小化时没人看,直接跳过;
            // 重新显示时会补刷一次,不会看到过期数据。
            DateTime now = DateTime.UtcNow;
            double gap = activePage == 0 ? 0.2 : 1.0;
            if (Visible && WindowState != FormWindowState.Minimized &&
                (now - lastPageRefresh).TotalSeconds >= gap)
            {
                lastPageRefresh = now;
                RefreshActive();
            }
        }

        private void RefreshActive()
        {
            if (pages == null || Mon == null || pages[activePage] == null) return;
            try { pages[activePage].Refresh(Mon.Current); }
            catch (Exception ex) { Log.Write("refresh: " + ex.Message); }
        }

        private void ShowPage(int i)
        {
            if (pages == null) return;
            if (i < 0 || i >= pages.Length) i = 0;
            // 切语言/主题时只重建了当前页,其余页在第一次切过去时补建,避免白建 5 页。
            if (pageDirty != null && pageDirty[i]) RebuildPage(i);
            activePage = i;
            for (int k = 0; k < pages.Length; k++)
                if (pages[k] != null) pages[k].Visible = k == i;
            if (toast != null) toast.BringToFront();
            if (nav.Selected != i) nav.SetSilent(i);
            RefreshActive();
        }

        /// <summary>顶部导航的选中色跟随加速模式:野兽模式整窗变红。</summary>
        public void SetNavAccent(Color c)
        {
            if (nav == null) return;
            nav.AccentColor = c;
            nav.Invalidate();
        }

        /// <summary>
        /// 按加速模式切换全局强调色(0 蓝 / 1 红 / 2 紫):所有页面共用,
        /// 已上屏的控件全部重绘,滑杆/开关/图表不用逐个改。
        /// </summary>
        public void ApplyModeTheme(int mode)
        {
            Theme.SetMode(mode);
            if (nav != null)
            {
                nav.AccentColor = Theme.Accent;
                nav.Invalidate();
            }
            if (pages != null)
            {
                // 页面重建时数组里会短暂留一个空位,跳过它。
                for (int i = 0; i < pages.Length; i++)
                    if (pages[i] != null) pages[i].Invalidate(true);
            }
            if (toast != null) toast.Invalidate();
            Invalidate(true);
        }

        // ---------------- 布局与绘制 ----------------

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutAll();
        }

        private void LayoutAll()
        {
            if (pages == null || toast == null) return;
            int th = TitleH();
            int bh = Ui.Px(34);
            int by = (th - bh) / 2;
            int gap = Ui.Px(2);
            int x = Width - Ui.Px(12) - bh;

            btnClose.SetBounds(x, by, bh, bh); x -= bh + gap;
            btnMin.SetBounds(x, by, bh, bh); x -= bh + gap;
            btnTheme.SetBounds(x, by, bh, bh); x -= bh + gap;
            btnLang.SetBounds(x, by, bh, bh);

            int navW = Ui.Px(I18n.IsEn ? 566 : 454);
            int limit = Width - Ui.Px(480);
            if (navW > limit) navW = limit;
            if (navW < Ui.Px(220)) navW = Ui.Px(220);
            nav.SetBounds((Width - navW) / 2, by, navW, bh);

            // 页面的外边距和卡片内边距统一到 20:切页时内容左边缘不会左右跳。
            int pad = Ui.Px(20);
            int top = th + Ui.Px(10);
            int pw = Width - pad * 2;
            int ph = Height - top - Ui.Px(20);
            for (int i = 0; i < pages.Length; i++)
                if (pages[i] != null) pages[i].SetBounds(pad, top, pw, ph);

            int tw = Math.Min(Ui.Px(420), Width - Ui.Px(80));
            toast.SetBounds((Width - tw) / 2, Height - Ui.Px(88), tw, Ui.Px(40));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

            int th = TitleH();
            int nameW = Math.Max(Ui.Px(120), nav.Left - Ui.Px(44));
            int nameH = Ui.Px(26);
            TextRenderer.DrawText(g, "FrameForge", Theme.Bold(14f),
                new Rectangle(Ui.Px(22), (th - nameH) / 2, nameW, nameH), Theme.Text, LeftLine());

            using (var pen = new Pen(Theme.Border, 1f))
                g.DrawLine(pen, 0, th - 1, Width, th - 1);
        }

        private static TextFormatFlags LeftLine()
        {
            return TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                 | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        }

        /// <summary>跨屏拖动后按新 DPI 重建界面,并按比例调整窗口尺寸。</summary>
        private void OnDpiChangedMsg(Message m)
        {
            int dpi = (int)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            if (dpi < 96) return;
            float s = dpi / 96f;
            if (Math.Abs(s - Ui.S) < 0.01f) return;

            int cw = (int)Math.Round(ClientSize.Width * s / Ui.S);
            int ch = (int)Math.Round(ClientSize.Height * s / Ui.S);
            Ui.S = s;
            ClientSize = new Size(Math.Max(Ui.Px(840), cw), Math.Max(Ui.Px(600), ch));
            Defer(RebuildUiCore);
        }

        protected override void WndProc(ref Message m)
        {
            // 这层窗口带着 WS_THICKFRAME(为了系统阴影和边缘缩放),DefWindowProc 收到
            // WM_NCPAINT / WM_NCACTIVATE 时会按"标准窗口"去画系统标题栏和边框。
            // 切主题时整窗重绘偶尔会把那条蓝灰标题栏画到内容上面,而且再也不会消失。
            // 非客户区已经被 WM_NCCALCSIZE 收成 0,这两个消息直接吞掉,系统就没机会画。
            if (m.Msg == 0x0085) { m.Result = IntPtr.Zero; return; }   // WM_NCPAINT
            if (m.Msg == 0x0086) { m.Result = (IntPtr)1; return; }     // WM_NCACTIVATE

            if (m.Msg == 0x02E0) { OnDpiChangedMsg(m); return; }
            if (m.Msg == Native.WM_HOTKEY) { HandleHotkey(m.WParam.ToInt32()); return; }
            if (m.Msg == Native.WM_SHOWME) { RestoreFromTray(); return; }

            if (m.Msg == WM_NCCALCSIZE)
            {
                // wParam != 0:客户区铺满整个窗口矩形。WS_THICKFRAME 留下的那一圈边框会被 DWM
                // 填成系统边框色:浅色主题下是 #F3F3F3,在深色界面上就是标题栏上方那道白标。
                if (m.WParam != IntPtr.Zero)
                {
                    if (WindowState == FormWindowState.Maximized)
                    {
                        Native.RECT r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                        Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
                        r.Left = wa.Left; r.Top = wa.Top; r.Right = wa.Right; r.Bottom = wa.Bottom;
                        Marshal.StructureToPtr(r, m.LParam, false);
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }

                // wParam == 0:系统在问"客户区应该是多少"。原样放给 DefWindowProc 的话,
                // 它会按标准窗口算出一圈非客户区(标题栏高度 + 边框),那 29px 的系统标题栏
                // 就会盖到自绘标题上——只有切成别的窗口再切回来才会重算出来,所以表现为
                // "切几次主题就冒出一条系统标题栏"。这里让客户区仍然等于整个窗口。
                m.Result = IntPtr.Zero;
                return;
            }

            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                int lp = m.LParam.ToInt32();
                short sx = unchecked((short)(lp & 0xFFFF));
                short sy = unchecked((short)((lp >> 16) & 0xFFFF));
                Point p = PointToClient(new Point(sx, sy));

                // 边框被吃掉后系统不再返回 HTLEFT/HTRIGHT…,四边改大小的热区得自己算。
                if (WindowState == FormWindowState.Normal)
                {
                    int b = Ui.Px(5);
                    bool left = p.X < b, right = p.X >= ClientSize.Width - b;
                    bool top = p.Y < b, bottom = p.Y >= ClientSize.Height - b;
                    if (top && left) { m.Result = (IntPtr)Native.HTTOPLEFT; return; }
                    if (top && right) { m.Result = (IntPtr)Native.HTTOPRIGHT; return; }
                    if (bottom && left) { m.Result = (IntPtr)Native.HTBOTTOMLEFT; return; }
                    if (bottom && right) { m.Result = (IntPtr)Native.HTBOTTOMRIGHT; return; }
                    if (left) { m.Result = (IntPtr)Native.HTLEFT; return; }
                    if (right) { m.Result = (IntPtr)Native.HTRIGHT; return; }
                    if (top) { m.Result = (IntPtr)Native.HTTOP; return; }
                    if (bottom) { m.Result = (IntPtr)Native.HTBOTTOM; return; }
                }

                if ((int)m.Result == Native.HTCLIENT)
                {
                    if (p.Y >= 0 && p.Y < TitleH()) m.Result = (IntPtr)Native.HTCAPTION;
                }
                return;
            }
            base.WndProc(ref m);
        }

        // ---------------- 托盘与关闭 ----------------

        private void CloseRequested()
        {
            if (St.CloseToTray) { HideToTray(true); return; }
            Close();
        }

        private void HideToTray(bool hint)
        {
            Hide();
            if (hint && !trayHinted && tray != null)
            {
                trayHinted = true;
                try { tray.ShowBalloonTip(1600, "FrameForge", I18n.T("tray.hint"), ToolTipIcon.None); }
                catch { }
            }
        }

        private void RestoreFromTray()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            // 隐藏期间跳过了页面刷新,这里补一次,免得恢复后先看到几秒前的旧数据。
            RefreshActive();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (ui != null) ui.Stop();
            if (saveTimer != null) saveTimer.Stop();
            if (captureTimer != null) captureTimer.Stop();
            // 退出时正在玩的对局也要留下记录,否则关掉软件这局的成绩就丢了。
            try { if (Tracker != null) Tracker.Flush(); } catch { }
            try { if (St != null) St.Save(); } catch { }

            UnregisterHotkeys();
            try { if (Fps != null) { Fps.Stop(); Fps.Dispose(); } } catch { }
            try { if (Mon != null) Mon.Dispose(); } catch { }
            try { if (Ov != null) { Ov.Stop(); Ov.Dispose(); } } catch { }
            try { if (Boost != null) { Boost.Restore(); Boost.Dispose(); } } catch { }
            try { Sys.Filter.Shutdown(); } catch { }
            try { Sys.Display.Shutdown(); } catch { }

            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            base.OnFormClosing(e);
        }

        /// <summary>
        /// 优先用编译进 exe 的多尺寸 app.ico,任务栏/标题栏在任意 DPI 下都能拿到清晰尺寸;
        /// 资源缺失时才退回运行时绘制的图标。
        /// </summary>
        private static Icon LoadIcon(int size)
        {
            try
            {
                using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
                {
                    if (s != null) return new Icon(s, size, size);
                }
            }
            catch { }
            return MakeIcon();
        }

        private static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (GraphicsPath p = Ui.Round(new RectangleF(0.5f, 0.5f, 31, 31), 7.5f))
                    using (var b = new LinearGradientBrush(new RectangleF(0, 0, 32, 32),
                        Theme.Hex("#5C9BFF"), Theme.Hex("#2B62D9"), 55f))
                        g.FillPath(b, p);
                    // 闪电:与 app.ico 保持同一套视觉
                    var bolt = new PointF[]
                    {
                        new PointF(18.1f, 2.4f), new PointF(7.8f, 17.8f), new PointF(14.6f, 17.8f),
                        new PointF(13.0f, 29.6f), new PointF(24.2f, 14.2f), new PointF(17.1f, 14.2f)
                    };
                    using (var b = new SolidBrush(Color.White)) g.FillPolygon(b, bolt);
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
                }
                finally { Native.DestroyIcon(h); }
            }
        }
    }
}