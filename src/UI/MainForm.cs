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
        private int activePage;
        private BoostPage boostPage;
        private SettingsPage setPage;

        private Timer ui;
        private Timer saveTimer;
        private NotifyIcon tray;
        private Icon appIcon;
        private bool trayHinted;

        private bool ovShown;
        private bool lastFullscreen;
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

            MinimumSize = new Size(Ui.Px(840), Ui.Px(600));
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
                + " admin=" + (MemEngine.IsAdmin ? 1 : 0));

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

        private void BuildPages()
        {
            boostPage = new BoostPage(this);
            setPage = new SettingsPage(this);
            pages = new Page[]
            {
                boostPage, new MonitorPage(this), new FilterPage(this), new OverlayPage(this), setPage
            };
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Visible = false;
                Controls.Add(pages[i]);
            }
            ShowPage(0);
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
            var menu = new ContextMenuStrip();
            menu.Items.Add(I18n.T("tray.open"), null, delegate { RestoreFromTray(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(I18n.T("tray.exit"), null, delegate { Close(); });

            tray = new NotifyIcon();
            tray.Icon = appIcon;
            tray.Text = "FrameForge";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { RestoreFromTray(); };
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
        }

        private string[] NavItems()
        {
            return new string[]
            {
                I18n.T("nav.boost"), I18n.T("nav.monitor"), I18n.T("nav.filter"),
                I18n.T("nav.overlay"), I18n.T("nav.settings")
            };
        }

        // ---------------- 运行 ----------------

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Mon.Start();
            ui.Start();
            if (setPage != null) setPage.UpdateStatus();
            SyncOverlay();
            Sys.Filter.Init();
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

            if (s.Fullscreen != lastFullscreen)
            {
                lastFullscreen = s.Fullscreen;
                SyncOverlay();
                ApplyFilter();
            }

            // 切出游戏(看状态/看网页)的短时间内继续跟踪,否则一看数据帧率就被清零。
            if (s.GameRecent && s.GamePid > 0) Fps.Track(s.GamePid);
            else Fps.Stop();

            Ov.Feed(s, Fps.Fps, Fps.Low1);

            CheckBoostAlive();
            MaybeAutoBoost(s);
            AutoTrim(s);

            // 帧率每秒在变,监测页跟着 200ms 刷新;其它页面没有实时数据,1 秒一次就够,
            // 免得整窗高频重绘白占 CPU。
            DateTime now = DateTime.UtcNow;
            double gap = activePage == 1 ? 0.2 : 1.0;
            if ((now - lastPageRefresh).TotalSeconds >= gap)
            {
                lastPageRefresh = now;
                RefreshActive();
            }
        }

        private void RefreshActive()
        {
            if (pages == null || Mon == null) return;
            try { pages[activePage].Refresh(Mon.Current); }
            catch (Exception ex) { Log.Write("refresh: " + ex.Message); }
        }

        private void ShowPage(int i)
        {
            if (pages == null) return;
            if (i < 0 || i >= pages.Length) i = 0;
            activePage = i;
            for (int k = 0; k < pages.Length; k++) pages[k].Visible = k == i;
            if (toast != null) toast.BringToFront();
            if (nav.Selected != i) nav.SetSilent(i);
            RefreshActive();
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

            int navW = Ui.Px(I18n.IsEn ? 468 : 372);
            int limit = Width - Ui.Px(480);
            if (navW > limit) navW = limit;
            if (navW < Ui.Px(220)) navW = Ui.Px(220);
            nav.SetBounds((Width - navW) / 2, by, navW, bh);

            int pad = Ui.Px(18);
            int top = th + Ui.Px(10);
            int pw = Width - pad * 2;
            int ph = Height - top - Ui.Px(16);
            for (int i = 0; i < pages.Length; i++) pages[i].SetBounds(pad, top, pw, ph);

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
            TextRenderer.DrawText(g, "FrameForge", Theme.Bold(14f),
                new Rectangle(Ui.Px(22), Ui.Px(4), nameW, Ui.Px(26)), Theme.Text, LeftLine());
            TextRenderer.DrawText(g, I18n.T("app.sub"), Theme.Regular(8.3f),
                new Rectangle(Ui.Px(23), Ui.Px(30), nameW, Ui.Px(18)), Theme.Sub, LeftLine());

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
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (ui != null) ui.Stop();
            if (saveTimer != null) saveTimer.Stop();
            try { if (St != null) St.Save(); } catch { }

            UnregisterHotkeys();
            try { if (Fps != null) { Fps.Stop(); Fps.Dispose(); } } catch { }
            try { if (Mon != null) Mon.Dispose(); } catch { }
            try { if (Ov != null) { Ov.Stop(); Ov.Dispose(); } } catch { }
            try { if (Boost != null) { Boost.Restore(); Boost.Dispose(); } } catch { }
            try { Sys.Filter.Shutdown(); } catch { }

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