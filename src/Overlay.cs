using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge
{
    /// <summary>一套叠加层配色（字段角色 → 颜色）。</summary>
    public sealed class OverlaySkin
    {
        public string Name = "";
        public Color Accent;
        public Color Fps;
        public Color Low;
        public Color Frame;
        public Color Label;
        public Color Ambient;
        public Color Value;
        public Color Warn;
        public Color Hot;

        /// <summary>温度按阈值分档：低于 warn 用主数值色，warn..hot 用暖色，超过 hot 用告警色。</summary>
        public Color Temp(double celsius, int warn, int hot)
        {
            if (celsius < 0) return Value;
            if (hot > 0 && celsius > hot) return Hot;
            if (warn > 0 && celsius >= warn) return Warn;
            return Value;
        }
    }

    /// <summary>
    /// 叠加层统一配色：五套电竞主题，自绘 HUD、RTSS 与设置页预览共用同一套色板。
    /// </summary>
    public static class OverlayPalette
    {
        public static readonly string[] ThemeKeys = { "cinnabar", "green", "pink", "cyan", "mono" };

        /// <summary>水墨朱砂（默认）：朱砂主视觉 + 暖金帧时间 + 月白数值，契合 FrameForge 整体风格。</summary>
        private static readonly OverlaySkin Cinnabar = new OverlaySkin
        {
            Name = "cinnabar",
            Accent = Color.FromArgb(255, 224, 79, 69),     // #E04F45
            Fps = Color.FromArgb(255, 255, 107, 91),       // #FF6B5B
            Low = Color.FromArgb(255, 255, 180, 168),      // #FFB4A8
            Frame = Color.FromArgb(255, 244, 201, 122),    // #F4C97A
            Label = Color.FromArgb(228, 198, 203, 212),    // #C6CBD4
            Ambient = Color.FromArgb(220, 142, 150, 164),  // #8E96A4
            Value = Color.FromArgb(255, 247, 249, 252),    // #F7F9FC
            Warn = Color.FromArgb(255, 255, 209, 102),     // #FFD166
            Hot = Color.FromArgb(255, 255, 82, 82)         // #FF5252
        };

        /// <summary>游戏加加绿：高对比翠绿帧率 + 青蓝帧时间，经典竞技护眼。</summary>
        private static readonly OverlaySkin Green = new OverlaySkin
        {
            Name = "green",
            Accent = Color.FromArgb(255, 60, 232, 122),    // #3CE87A
            Fps = Color.FromArgb(255, 93, 255, 142),       // #5DFF8E
            Low = Color.FromArgb(255, 183, 255, 202),      // #B7FFCA
            Frame = Color.FromArgb(255, 86, 217, 255),     // #56D9FF
            Label = Color.FromArgb(228, 203, 224, 213),    // #CBE0D5
            Ambient = Color.FromArgb(220, 140, 161, 150),  // #8CA196
            Value = Color.White,
            Warn = Color.FromArgb(255, 255, 179, 71),      // #FFB347
            Hot = Color.FromArgb(255, 255, 92, 92)         // #FF5C5C
        };

        /// <summary>小飞机粉（Afterburner）：霓虹粉紫帧率 + 冰青帧时间。</summary>
        private static readonly OverlaySkin Pink = new OverlaySkin
        {
            Name = "pink",
            Accent = Color.FromArgb(255, 255, 79, 216),    // #FF4FD8
            Fps = Color.FromArgb(255, 255, 79, 216),       // #FF4FD8
            Low = Color.FromArgb(255, 255, 199, 238),      // #FFC7EE
            Frame = Color.FromArgb(255, 79, 216, 232),     // #4FD8E8
            Label = Color.FromArgb(228, 206, 214, 226),    // #CED6E2
            Ambient = Color.FromArgb(220, 154, 166, 184),  // #9AA6B8
            Value = Color.White,
            Warn = Color.FromArgb(255, 255, 209, 102),     // #FFD166
            Hot = Color.FromArgb(255, 255, 107, 107)       // #FF6B6B
        };

        /// <summary>赛博青蓝：电光青帧率 + 淡紫帧时间，冷峻科技感。</summary>
        private static readonly OverlaySkin Cyan = new OverlaySkin
        {
            Name = "cyan",
            Accent = Color.FromArgb(255, 46, 230, 255),    // #2EE6FF
            Fps = Color.FromArgb(255, 76, 238, 255),       // #4CEEFF
            Low = Color.FromArgb(255, 179, 246, 255),      // #B3F6FF
            Frame = Color.FromArgb(255, 167, 139, 250),    // #A78BFA
            Label = Color.FromArgb(228, 194, 212, 230),    // #C2D4E6
            Ambient = Color.FromArgb(220, 130, 153, 176),  // #8299B0
            Value = Color.FromArgb(255, 245, 250, 255),    // #F5FAFF
            Warn = Color.FromArgb(255, 255, 209, 102),     // #FFD166
            Hot = Color.FromArgb(255, 255, 94, 126)        // #FF5E7E
        };

        /// <summary>极简白：黑白灰低干扰，仅高温告警着色。</summary>
        private static readonly OverlaySkin Mono = new OverlaySkin
        {
            Name = "mono",
            Accent = Color.FromArgb(255, 226, 232, 240),   // #E2E8F0
            Fps = Color.White,
            Low = Color.FromArgb(255, 203, 213, 225),      // #CBD5E1
            Frame = Color.FromArgb(255, 148, 163, 184),    // #94A3B8
            Label = Color.FromArgb(228, 156, 168, 184),    // #9CA8B8
            Ambient = Color.FromArgb(215, 118, 130, 148),  // #768294
            Value = Color.FromArgb(255, 248, 250, 252),    // #F8FAFC
            Warn = Color.FromArgb(255, 251, 191, 36),      // #FBBF24
            Hot = Color.FromArgb(255, 248, 113, 113)       // #F87171
        };

        /// <summary>按主题键返回配色方案；未知值回落水墨朱砂。</summary>
        public static OverlaySkin For(string theme)
        {
            if (string.Equals(theme, "green", StringComparison.OrdinalIgnoreCase)) return Green;
            if (string.Equals(theme, "pink", StringComparison.OrdinalIgnoreCase)) return Pink;
            if (string.Equals(theme, "cyan", StringComparison.OrdinalIgnoreCase)) return Cyan;
            if (string.Equals(theme, "mono", StringComparison.OrdinalIgnoreCase)) return Mono;
            return Cinnabar;
        }

        /// <summary>兼容旧签名：按是否 RTSS 返回默认配色。</summary>
        public static OverlaySkin For(bool rtss)
        {
            return rtss ? Pink : Green;
        }

        /// <summary>RTSS 内嵌颜色标签要 AARRGGBB；这里只取 RGB，alpha 固定 FF。</summary>
        public static string RgbHex(Color c)
        {
            return c.R.ToString("X2", CultureInfo.InvariantCulture)
                 + c.G.ToString("X2", CultureInfo.InvariantCulture)
                 + c.B.ToString("X2", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 游戏内叠加层:分层置顶窗口 + UpdateLayeredWindow 逐像素透明。
    /// 不注入游戏,不接管输入;锁定后完全鼠标穿透。
    /// </summary>
    public sealed class Overlay : Form
    {
        private readonly Settings st;
        private readonly Timer timer;
        private SysSample last = new SysSample();
        private FpsEngine fpsEngine;
        private double fps = -1;
        private double low = -1;
        private double low01 = -1;
        private double frameTime = -1;
        private DateTime upFrom = DateTime.Now;
        private readonly float[] graphBuf = new float[90];
        private int graphCount;
        private float graphAvgMs;

        private bool toastActive;
        private string toastTitle = "";
        private string toastDetail = "";
        private Color toastAccent = Color.Empty;
        private DateTime toastUntil = DateTime.MinValue;

        private bool moving;
        // 用户是否打开了叠加层。窗口被隐藏(内容清空、被游戏压掉)后靠它自愈显示,
        // 否则 Hide 之后没有任何东西会把窗口叫回来,开关看着还是"开"的却永远不出现。
        private bool wanted;
        private DateTime nextTopmost = DateTime.MinValue;
        private Rectangle lastWa = Rectangle.Empty;
        // 上次画上去的内容。数值没变就不重画:UpdateLayeredWindow 每次都会让 DWM
        // 重新合成一层,游戏里叠加层本来就在和游戏抢合成,能少一次是一次。
        private string lastKey = "";

        // 字体池：同时供实机和设置页预览复用，避免每帧 new Font 泄漏 GDI+ 句柄。
        private static readonly object FontLock = new object();
        private static readonly Dictionary<string, Font> FontPool = new Dictionary<string, Font>();
        // 排版用的是"无额外内边距"的格式,测出来多宽就画多宽,右对齐不会漂。
        private static readonly StringFormat Typo = NewTypo();

        private IntPtr memDc = IntPtr.Zero;
        private IntPtr dib = IntPtr.Zero;
        private IntPtr dibBits = IntPtr.Zero;
        private IntPtr oldObj = IntPtr.Zero;
        private int bmpW, bmpH;

        // 预乘 alpha 的临时缓冲。每秒重画 5 次,每次都 new 一块几百 KB 的数组会一直给 GC
        // 添乱(叠加层和游戏在抢合成),按需扩到最大尺寸后复用即可。
        private byte[] premul = new byte[0];
        // 画布同理:尺寸不变就复用同一张 Bitmap,不必每帧新建/释放一块 32bpp 位图。
        private Bitmap canvas;
        private int canvasW, canvasH;
        // 1x1 测量探针:惰性创建并复用,避免每次内容变化都分配/释放 Bitmap(1, 1)。
        private Bitmap probeBmp;
        private Graphics probeGfx;
        // 渲染异常按分钟记一次日志:真出问题时不至于每 200ms 写一次盘。
        private DateTime lastErrLog = DateTime.MinValue;

        public Overlay(Settings settings) : this(settings, null)
        {
        }

        public Overlay(Settings settings, FpsEngine engine)
        {
            st = settings;
            fpsEngine = engine;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            Text = "FrameForge overlay";

            timer = new Timer();
            timer.Interval = 200;
            timer.Tick += delegate { Render(); };
        }

        public void AttachFps(FpsEngine engine)
        {
            fpsEngine = engine;
        }

        public bool Locked { get { return st.OverlayLocked; } }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                if (st != null && st.OverlayLocked) cp.ExStyle |= Native.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public void Start()
        {
            wanted = true;
            if (!Visible) { upFrom = DateTime.Now; lastKey = ""; Show(); }
            ApplyInputMode();
            Render();
            timer.Start();
        }

        public void Stop()
        {
            wanted = false;
            timer.Stop();
            // 自己关了叠加层,RTSS 那边的文字也要撤掉,别在游戏里留一行孤儿数据。
            Rtss.Clear();
            if (Visible) Hide();
        }

        /// <summary>叠加层开关/样式/大小/锁定变化后调用。</summary>
        public void ApplySettings()
        {
            ApplyInputMode();
            Render();
        }

        public void Feed(SysSample s, double currentFps, double low1, double frameTimeMs)
        {
            Feed(s, currentFps, low1, fpsEngine != null ? fpsEngine.Low01 : -1, frameTimeMs);
        }

        public void Feed(SysSample s, double currentFps, double low1, double low01Val, double frameTimeMs)
        {
            last = s ?? new SysSample();
            fps = currentFps;
            low = low1;
            low01 = low01Val;
            frameTime = frameTimeMs;
        }

        /// <summary>在游戏顶部角落弹出短暂的电竞风格 OSD 提示（不抢焦点、鼠标穿透）。</summary>
        public void ShowOsdToast(string title)
        {
            ShowOsdToast(title, "", Color.Empty, 2200);
        }

        /// <summary>在游戏顶部角落弹出短暂的电竞风格 OSD 提示（不抢焦点、鼠标穿透）。</summary>
        public void ShowOsdToast(string title, string detail, Color accent, int durationMs)
        {
            if (st == null || !st.InGameOsdToast || IsDisposed) return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((Action)delegate { ShowOsdToast(title, detail, accent, durationMs); });
                }
                catch { }
                return;
            }

            int dur = durationMs > 0 ? durationMs : 2200;
            if (dur < 600) dur = 600;
            if (dur > 10000) dur = 10000;

            toastTitle = title ?? "";
            toastDetail = detail ?? "";
            toastAccent = accent.A > 0 ? accent : OverlayPalette.For(st.OverlayTheme).Accent;
            toastUntil = DateTime.UtcNow.AddMilliseconds(dur);
            toastActive = true;
            lastKey = "";

            if (!Visible)
            {
                try { Show(); }
                catch { }
            }
            if (IsHandleCreated)
            {
                int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TRANSPARENT) == 0)
                    Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex | Native.WS_EX_TRANSPARENT);
            }
            Render();
            if (!timer.Enabled) timer.Start();
        }

        private void ApplyInputMode()
        {
            if (!IsHandleCreated) return;
            int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            if (st.OverlayLocked) ex |= Native.WS_EX_TRANSPARENT;
            else ex &= ~Native.WS_EX_TRANSPARENT;
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);
        }

        /// <summary>
        /// 摆位:选了屏幕四角就固定贴角(忽略拖动保存的坐标);
        /// 选"自动"时,横向居中、竖向靠左(纵向居中)、简洁居中。
        /// 用户拖动过(OverlayX/Y 已保存)就沿用他的位置;切换样式时设置页会清掉坐标,
        /// 重新按新样式摆放。
        /// </summary>
        private void PlaceIfNeeded(int w, int h, float k)
        {
            Rectangle wa = TargetBounds();
            string corner = st.OverlayCorner;
            if (corner != null && corner != "auto")
            {
                int m = (int)Math.Round(12 * k);
                int cx = (corner == "tl" || corner == "bl") ? wa.Left + m : wa.Right - w - m;
                int cy = (corner == "tl" || corner == "tr") ? wa.Top + m : wa.Bottom - h - m;
                Location = new Point(cx, cy);
                return;
            }

            if (st.OverlayX >= 0 && st.OverlayY >= 0)
            {
                var saved = new Rectangle(st.OverlayX, st.OverlayY, w, h);
                if (wa.IntersectsWith(saved))
                {
                    Location = new Point(st.OverlayX, st.OverlayY);
                    return;
                }
            }

            int margin = (int)Math.Round(20 * k);
            int x, y;
            if (st.OverlayStyle == "v")
            {
                x = wa.Left + margin;
                y = wa.Top + (wa.Height - h) / 2;
            }
            else
            {
                x = wa.Left + (wa.Width - w) / 2;
                y = wa.Top + margin;
            }
            Location = new Point(x, y);
        }

        /// <summary>
        /// 叠加层跟着游戏窗口走:只要识别到了游戏,就直接用游戏窗口的边界(DWM 扩展边界),
        /// 不管它是不是前台、在哪块屏、窗口化还是全屏,都贴在游戏上面。
        /// 没有游戏时才退回前台窗口所在显示器,配置界面里也能正常预览。
        /// </summary>
        private Rectangle TargetBounds()
        {
            IntPtr target = IntPtr.Zero;
            if (last != null && last.GamePid > 0 && last.GameHwnd != IntPtr.Zero
                && Native.IsWindow(last.GameHwnd) && !Native.IsIconic(last.GameHwnd))
                target = last.GameHwnd;

            bool game = target != IntPtr.Zero;
            if (!game) target = Native.GetForegroundWindow();

            if (target != IntPtr.Zero)
            {
                Rectangle screen;
                try { screen = Screen.FromHandle(target).Bounds; }
                catch { screen = Screen.PrimaryScreen.Bounds; }

                if (game)
                {
                    Native.RECT r;
                    if (Monitor.WindowBounds(target, out r))
                    {
                        Rectangle win = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                        if (win.Width >= screen.Width - 8 && win.Height >= screen.Height - 8)
                            return screen;
                        if (win.Width >= 120 && win.Height >= 90) return win;
                    }
                }
                try { return Screen.FromHandle(target).WorkingArea; }
                catch { }
            }
            return Screen.PrimaryScreen.WorkingArea;
        }

        /// <summary>
        /// 有些游戏会反复把自己的窗口提到最前,把第三方置顶窗口压到后面。
        /// 每秒恢复一次 TOPMOST(不移动、不缩放、不抢焦点),被压下去后能自己回来。
        /// </summary>
        private void KeepTopmost()
        {
            if (!IsHandleCreated) return;
            DateTime now = DateTime.UtcNow;
            if (now < nextTopmost) return;
            nextTopmost = now.AddSeconds(1);
            try
            {
                uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
                if (wanted || toastActive) flags |= Native.SWP_SHOWWINDOW;
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST && !st.OverlayLocked)
            {
                m.Result = (IntPtr)Native.HTCAPTION;
                return;
            }
            if (m.Msg == 0x0231 && !st.OverlayLocked) moving = true;  // WM_ENTERSIZEMOVE
            if (m.Msg == 0x0232 && !st.OverlayLocked)                 // WM_EXITSIZEMOVE
            {
                moving = false;
                st.OverlayX = Left;
                st.OverlayY = Top;
                st.OverlayCorner = "auto";
                st.Save();
            }
            base.WndProc(ref m);
        }

        // ---------------- 数据模型 ----------------

        /// <summary>叠加层的一行:一个设备名 + 它的若干项数值(占用 / 温度 / 时钟……)。</summary>
        private sealed class Row
        {
            public string Label = "";
            public readonly List<string> Cells = new List<string>();
            public readonly List<Color> Colors = new List<Color>();
            /// <summary>是否为 FPS 核心主行（大号 FPS + 1% Low / 帧时间）。</summary>
            public bool IsHero;
            /// <summary>环境信息（时间/时长）整行降为次级灰。</summary>
            public bool Ambient;
            /// <summary>需要以紧凑徽章样式绘制的单元格索引（如 THRM / PWR）。</summary>
            public int BadgeCell = -1;

            public void Add(string text, Color color)
            {
                Cells.Add(text);
                Colors.Add(color);
            }

            public Color ColorAt(int i)
            {
                return i >= 0 && i < Colors.Count ? Colors[i] : Color.White;
            }
        }

        /// <summary>按设备分组生成行：FPS 为独立主视觉行，设备与环境分层。</summary>
        private static List<Row> BuildRows(Settings st, OverlaySkin skin, SysSample s,
            double fpsVal, double lowVal, double low01Val, double ftVal, DateTime now, DateTime upFrom)
        {
            var rows = new List<Row>();

            if (st.ShowFps || st.ShowLow || st.ShowFrameTime)
            {
                Row r = new Row();
                r.Label = "FPS";
                r.IsHero = st.ShowFps;
                if (st.ShowFps) r.Add(fpsVal > 0 ? fpsVal.ToString("0") : "--", skin.Fps);
                if (st.ShowLow)
                {
                    r.Add("1% " + (lowVal > 0 ? lowVal.ToString("0") : "--"), skin.Low);
                    if (low01Val > 0)
                        r.Add("0.1% " + low01Val.ToString("0"), skin.Low);
                }
                if (st.ShowFrameTime) r.Add(ftVal > 0 ? ftVal.ToString("0.0") + "ms" : "--ms", skin.Frame);
                rows.Add(r);
            }
            if (st.ShowCpu)
            {
                Row r = new Row();
                r.Label = "CPU";
                r.Add(s.Cpu >= 0 ? s.Cpu.ToString("0") + "%" : "--", skin.Value);
                rows.Add(r);
            }
            if (st.ShowGpu || st.ShowGpuTemp)
            {
                Row r = new Row();
                r.Label = "GPU";
                if (st.ShowGpu) r.Add(s.Gpu >= 0 ? s.Gpu.ToString("0") + "%" : "--", skin.Value);
                if (st.ShowGpuTemp)
                    r.Add(s.GpuTemp >= 0 ? s.GpuTemp.ToString("0") + "°C" : "--°C",
                        skin.Temp(s.GpuTemp, st.OverlayTempWarn, st.OverlayTempHot));
                if (!string.IsNullOrEmpty(s.GpuThrottle))
                {
                    Color tc = string.Equals(s.GpuThrottle, "THRM", StringComparison.OrdinalIgnoreCase)
                        ? skin.Hot : skin.Warn;
                    r.BadgeCell = r.Cells.Count;
                    r.Add(s.GpuThrottle, tc);
                }
                rows.Add(r);
            }
            if (st.ShowRam || st.ShowRamPct)
            {
                Row r = new Row();
                r.Label = I18n.IsEn ? "RAM" : "内存";
                if (st.ShowRam) r.Add(s.RamUsed > 0 ? ((double)s.RamUsed / 1073741824.0).ToString("0.0") + "G" : "--", skin.Value);
                if (st.ShowRamPct) r.Add(s.RamPct >= 0 ? s.RamPct.ToString("0") + "%" : "--", skin.Value);
                rows.Add(r);
            }
            if (st.ShowClock)
            {
                Row r = new Row();
                r.Label = I18n.IsEn ? "TIME" : "时间";
                r.Ambient = true;
                r.Add(now.ToString("HH:mm:ss"), skin.Ambient);
                rows.Add(r);
            }
            if (st.ShowSession)
            {
                TimeSpan t = now - upFrom;
                if (t.TotalSeconds < 0) t = TimeSpan.Zero;
                Row r = new Row();
                r.Label = I18n.IsEn ? "UPTIME" : "时长";
                r.Ambient = true;
                r.Add(((int)t.TotalMinutes).ToString() + ":" + t.Seconds.ToString("00"), skin.Ambient);
                rows.Add(r);
            }
            return rows;
        }

        // ---------------- 渲染 ----------------

        private void Render()
        {
            if (!IsHandleCreated) return;
            try
            {
                double dpi = 96;
                try { uint d = Native.GetDpiForWindow(Handle); if (d >= 96) dpi = d; } catch { }
                float k = (float)(st.OverlayScale * (dpi / 96.0));
                if (k < 0.6f) k = 0.6f;
                if (k > 2.4f) k = 2.4f;

                if (toastActive)
                {
                    if (DateTime.UtcNow >= toastUntil)
                    {
                        toastActive = false;
                        lastKey = "";
                        ApplyInputMode();
                        if (!wanted)
                        {
                            if (Visible) Hide();
                            if (!st.RtssOsd) timer.Stop();
                            return;
                        }
                    }
                    else
                    {
                        RenderToast(k);
                        return;
                    }
                }

                if (fpsEngine != null)
                {
                    fpsEngine.CopyFrameGraph(graphBuf, out graphCount, out graphAvgMs);
                    if (low01 <= 0 && fpsEngine.Low01 > 0) low01 = fpsEngine.Low01;
                }
                else
                {
                    graphCount = 0;
                    graphAvgMs = 0f;
                }

                string style = st.OverlayStyle;
                OverlaySkin skin = OverlayPalette.For(st.OverlayTheme);
                List<Row> rows = BuildRows(st, skin, last, fps, low, low01, frameTime, DateTime.Now, upFrom);

                bool miniData = st.ShowFps || st.ShowLow || st.ShowGpuTemp;
                bool hasData = style == "mini" ? miniData : rows.Count > 0;

                if (st.RtssOsd && Rtss.Running)
                {
                    Rtss.Push(rows.Count > 0 ? RtssText(st, rows, skin) : "");
                    lastKey = "";
                    if (Visible) Hide();
                    return;
                }

                if (!hasData) { lastKey = ""; if (Visible) Hide(); return; }

                if (wanted && !Visible)
                {
                    lastKey = "";
                    try { Show(); }
                    catch { }
                }
                if (!Visible) return;

                // 帧边界安全回收字体池，避免单帧绘制中途释放仍在使用的 GDI+ Font。
                TrimFontPool();

                Rectangle wa = TargetBounds();
                if (wa != lastWa) { lastWa = wa; lastKey = ""; }

                string key = style + "|" + st.OverlayScale.ToString("0.00", CultureInfo.InvariantCulture)
                           + "|" + k.ToString("0.00", CultureInfo.InvariantCulture)
                           + "|" + st.OverlayCorner
                           + "|" + skin.Name
                           + "|" + (st.OverlayBackdrop ?? "hud")
                           + "|" + (st.OverlayLocked ? "L" : "U")
                           + "|" + st.OverlayTempWarn.ToString(CultureInfo.InvariantCulture)
                           + "|" + st.OverlayTempHot.ToString(CultureInfo.InvariantCulture)
                           + "|" + (st.ShowFrameGraph ? "G" : "")
                           + "|" + (style == "v" && st.ShowFrameGraph ? GraphKey(graphBuf, graphCount).ToString(CultureInfo.InvariantCulture) : "")
                           + "|" + (style == "mini" ? ((st.ShowFps ? "F" : "") + (st.ShowLow ? "L" : "") + (st.ShowGpuTemp ? "T" : "")) : "")
                           + "|" + RowsKey(rows);
                if (key == lastKey)
                {
                    if (!moving) KeepTopmost();
                    return;
                }
                lastKey = key;

                if (probeBmp == null)
                {
                    probeBmp = new Bitmap(1, 1);
                    probeGfx = Graphics.FromImage(probeBmp);
                    probeGfx.TextRenderingHint = TextRenderingHint.AntiAlias;
                }
                HudLayout layout = MeasureHud(probeGfx, st, rows, skin, k, fps, low, last.GpuTemp, graphCount);

                int w = layout.W;
                int h = layout.H;
                if (w < 44) w = 44;
                if (h < 24) h = 24;
                if (w > 4096) w = 4096;
                if (h > 2048) h = 2048;

                if (!moving) PlaceIfNeeded(w, h, k);
                KeepTopmost();

                Bitmap bmp = Canvas(w, h);
                if (bmp != null)
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = TextRenderingHint.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        DrawHud(g, st, rows, skin, layout, 0f, 0f, k, fps, low, last.GpuTemp, !st.OverlayLocked, graphBuf, graphCount, graphAvgMs);
                    }
                    Blit(bmp, w, h);
                }
            }
            catch (Exception ex)
            {
                if ((DateTime.Now - lastErrLog).TotalSeconds >= 60)
                {
                    lastErrLog = DateTime.Now;
                    Log.Write("overlay render: " + ex.Message);
                }
            }
        }

        private void RenderToast(float k)
        {
            TrimFontPool();
            Rectangle wa = TargetBounds();
            if (wa != lastWa) { lastWa = wa; lastKey = ""; }

            string key = "toast|" + toastTitle + "|" + toastDetail
                       + "|" + toastAccent.ToArgb().ToString(CultureInfo.InvariantCulture)
                       + "|" + k.ToString("0.00", CultureInfo.InvariantCulture)
                       + "|" + st.OverlayCorner;
            if (key == lastKey)
            {
                KeepTopmost();
                return;
            }
            lastKey = key;

            if (probeBmp == null)
            {
                probeBmp = new Bitmap(1, 1);
                probeGfx = Graphics.FromImage(probeBmp);
                probeGfx.TextRenderingHint = TextRenderingHint.AntiAlias;
            }

            Font tf = GetOverlayFont(10.5f * k, FontStyle.Bold);
            Font df = GetOverlayFont(8.8f * k, FontStyle.Regular);
            SizeF ts = probeGfx.MeasureString(toastTitle, tf, int.MaxValue, Typo);
            bool hasDetail = !string.IsNullOrEmpty(toastDetail);
            SizeF ds = hasDetail ? probeGfx.MeasureString(toastDetail, df, int.MaxValue, Typo) : SizeF.Empty;

            float padL = 13f * k;
            float padR = 13f * k;
            float padY = 8f * k;
            float gap = hasDetail ? 2.5f * k : 0f;
            float bodyW = Math.Max(ts.Width, ds.Width);
            int w = (int)Math.Ceiling(Math.Max(140f * k, bodyW + padL + padR));
            int h = (int)Math.Ceiling(ts.Height + ds.Height + gap + padY * 2f);
            if (w > 640) w = 640;
            if (h > 160) h = 160;

            int margin = (int)Math.Round(16 * k);
            bool useLeft = string.Equals(st.OverlayCorner, "tr", StringComparison.OrdinalIgnoreCase);
            int tx = useLeft ? wa.Left + margin : wa.Right - w - margin;
            int ty = wa.Top + margin;
            Location = new Point(tx, ty);
            KeepTopmost();

            Bitmap bmp = Canvas(w, h);
            if (bmp != null)
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    float rad = 7f * k;
                    var rect = new RectangleF(0.5f, 0.5f, w - 1f, h - 1f);
                    using (GraphicsPath path = RoundRect(rect, rad))
                    {
                        using (var grad = new LinearGradientBrush(rect,
                            Color.FromArgb(228, 16, 19, 27),
                            Color.FromArgb(238, 10, 12, 18),
                            LinearGradientMode.Vertical))
                            g.FillPath(grad, path);
                        using (var sheen = new Pen(Color.FromArgb(30, 255, 255, 255), 1f))
                            g.DrawLine(sheen, rad, 1.2f, w - rad, 1.2f);
                        using (var border = new Pen(Color.FromArgb(56, 255, 255, 255), 1f))
                            g.DrawPath(border, path);
                    }

                    float barW = Math.Max(2.5f, 3f * k);
                    float barInset = Math.Min(h * 0.22f, 6f * k);
                    var barRect = new RectangleF(5f * k, barInset, barW, Math.Max(6f, h - barInset * 2f));
                    using (GraphicsPath bp = RoundRect(barRect, barW / 2f))
                    using (var ab = new SolidBrush(Color.FromArgb(245, toastAccent)))
                        g.FillPath(ab, bp);

                    using (var tb = new SolidBrush(Color.FromArgb(255, 247, 249, 252)))
                        ShadowString(g, toastTitle, tf, tb, padL, padY, k, true);
                    if (hasDetail)
                    {
                        using (var db = new SolidBrush(Color.FromArgb(228, 198, 203, 212)))
                            ShadowString(g, toastDetail, df, db, padL, padY + ts.Height + gap, k, true);
                    }
                }
                Blit(bmp, w, h);
            }
        }

        private static int GraphKey(float[] buf, int count)
        {
            if (buf == null || count <= 0) return 0;
            int h = count;
            int n = Math.Min(count, buf.Length);
            for (int i = 0; i < n; i++)
            {
                int q = (int)(buf[i] * 10f);
                h = (h * 31) ^ q;
            }
            return h;
        }

        /// <summary>
        /// 设置页预览直接复用与实机完全一致的测量与绘制管线，保证所见即所得。
        /// </summary>
        public static void PaintPreview(Graphics g, Settings st, Rectangle area, bool rtssMode)
        {
            if (g == null || st == null || area.Width < 60 || area.Height < 40) return;

            TrimFontPool();
            float dpiScale = g.DpiX >= 96f ? (g.DpiX / 96f) : 1f;
            if (dpiScale > 2.25f) dpiScale = 2.25f;

            OverlaySkin skin = OverlayPalette.For(st.OverlayTheme);
            var sample = new SysSample();
            sample.Cpu = 38;
            sample.Gpu = 76;
            sample.GpuTemp = 64;
            sample.RamUsed = (ulong)(9.8 * 1073741824.0);
            sample.RamPct = 62;

            DateTime fakeNow = new DateTime(2026, 11, 25, 21, 40, 36);
            DateTime fakeUp = fakeNow.AddMinutes(-32).AddSeconds(-8);
            List<Row> rows = BuildRows(st, skin, sample, 144, 112, 86, 6.9, fakeNow, fakeUp);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            if (rtssMode)
            {
                PaintRtssPreview(g, st, rows, skin, area, dpiScale);
                return;
            }

            string style = st.OverlayStyle;
            bool miniData = st.ShowFps || st.ShowLow || st.ShowGpuTemp;
            bool hasData = style == "mini" ? miniData : rows.Count > 0;
            if (!hasData)
            {
                Font ef = GetOverlayFont(12f * dpiScale, FontStyle.Regular);
                using (var b = new SolidBrush(skin.Label))
                    ShadowString(g, I18n.T("ov.preview.none"), ef, b, area.Left + 16f * dpiScale, area.Top + area.Height / 2f - 8f * dpiScale, dpiScale, false);
                return;
            }

            float k = (float)st.OverlayScale;
            if (k < 0.78f) k = 0.78f;
            if (k > 1.32f) k = 1.32f;
            k *= dpiScale;

            float[] previewGraph = BuildPreviewGraph();
            int previewCount = st.ShowFrameGraph && style == "v" ? previewGraph.Length : 0;
            HudLayout layout = MeasureHud(g, st, rows, skin, k, 144, 112, 64, previewCount);

            // 如果横向过宽撑出预览卡片，自动等比微缩以完整展示。
            float maxW = area.Width - 24f * dpiScale;
            if (layout.W > maxW && maxW > 80f)
            {
                k = k * (maxW / layout.W);
                if (k < 0.55f) k = 0.55f;
                layout = MeasureHud(g, st, rows, skin, k, 144, 112, 64, previewCount);
            }

            string corner = st.OverlayCorner;
            bool fixedCorner = corner != null && corner != "auto";
            bool left = corner == "tl" || corner == "bl";
            bool bottom = corner == "bl" || corner == "br";
            float pad = 12f * dpiScale;

            float ox, oy;
            if (fixedCorner)
            {
                ox = left ? area.Left + pad : area.Right - pad - layout.W;
                oy = bottom ? area.Bottom - pad - layout.H : area.Top + pad;
            }
            else if (style == "v")
            {
                ox = area.Left + pad;
                oy = area.Top + (area.Height - layout.H) / 2f;
            }
            else
            {
                ox = area.Left + (area.Width - layout.W) / 2f;
                oy = area.Top + pad;
            }

            if (ox < area.Left + 6f) ox = area.Left + 6f;
            if (oy < area.Top + 6f) oy = area.Top + 6f;

            DrawHud(g, st, rows, skin, layout, ox, oy, k, 144, 112, 64, false, previewGraph, previewCount, 6.9f);
        }

        private static float[] BuildPreviewGraph()
        {
            var buf = new float[90];
            for (int i = 0; i < 90; i++)
            {
                float v = 6.9f + (float)Math.Sin(i * 0.35) * 0.7f;
                if (i == 28 || i == 64) v = 16.5f;
                else if (i == 47) v = 22.0f;
                buf[i] = v;
            }
            return buf;
        }

        private static void PaintRtssPreview(Graphics g, Settings st, List<Row> rows, OverlaySkin skin, Rectangle area, float dpiScale)
        {
            if (rows.Count == 0)
            {
                Font ef = GetOverlayFont(12f * dpiScale, FontStyle.Regular);
                using (var b = new SolidBrush(skin.Label))
                    ShadowString(g, I18n.T("ov.preview.none"), ef, b, area.Left + 16f * dpiScale, area.Top + area.Height / 2f - 8f * dpiScale, dpiScale, false);
                return;
            }

            int scale = st.RtssScale;
            if (scale < 50) scale = 50;
            if (scale > 200) scale = 200;
            float basePx = Math.Max(9f, 13.5f * scale / 100f) * dpiScale;
            if (basePx > 20f * dpiScale) basePx = 20f * dpiScale;

            Font fHero = GetNumericFont(basePx * 1.2f, FontStyle.Bold);
            Font fUnit = GetNumericFont(basePx * 0.82f, FontStyle.Bold);
            Font fLbl = GetNumericFont(basePx * 0.90f, FontStyle.Bold);
            Font fVal = GetNumericFont(basePx, FontStyle.Bold);
            Font fSub = GetNumericFont(basePx * 0.92f, FontStyle.Bold);

            float x0 = area.Left + 14f;
            float y = area.Top + 14f;
            float x = x0;
            float lineH = fVal.Height + 3f;
            float heroH = fHero.Height + 4f;
            Color sepColor = Color.FromArgb(180, 110, 122, 138);

            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (!st.RtssStacked && i > 0)
                {
                    using (var sb = new SolidBrush(sepColor))
                        ShadowString(g, " | ", fLbl, sb, x, y + (fHero.Height - fLbl.Height) * 0.5f, 1f, false);
                    x += g.MeasureString(" | ", fLbl, int.MaxValue, Typo).Width;
                }

                float rowY = y;
                float lx = st.RtssStacked ? x0 : x;

                if (r.IsHero && r.Cells.Count > 0)
                {
                    float baseTop = rowY;
                    using (var b = new SolidBrush(r.ColorAt(0)))
                        ShadowString(g, r.Cells[0], fHero, b, lx, baseTop, 1f, false);
                    lx += g.MeasureString(r.Cells[0], fHero, int.MaxValue, Typo).Width + 3f;

                    float unitY = baseTop + (fHero.Height - fUnit.Height) * 0.78f;
                    using (var b = new SolidBrush(skin.Label))
                        ShadowString(g, "FPS", fUnit, b, lx, unitY, 1f, false);
                    lx += g.MeasureString("FPS", fUnit, int.MaxValue, Typo).Width;

                    float subY = baseTop + (fHero.Height - fSub.Height) * 0.74f;
                    for (int c = 1; c < r.Cells.Count; c++)
                    {
                        lx += 8f;
                        using (var b = new SolidBrush(r.ColorAt(c)))
                            ShadowString(g, r.Cells[c], fSub, b, lx, subY, 1f, false);
                        lx += g.MeasureString(r.Cells[c], fSub, int.MaxValue, Typo).Width;
                    }
                    if (st.RtssStacked) y += heroH;
                    else x = lx;
                }
                else
                {
                    string lbl = RtssLabel(r.Label);
                    if (st.RtssStacked) lbl = lbl.PadRight(4, ' ');
                    Color lc = r.Ambient ? skin.Ambient : skin.Label;
                    float topOff = !st.RtssStacked && rows[0].IsHero ? (fHero.Height - fVal.Height) * 0.72f : 0f;
                    float lblY = rowY + topOff + (fVal.Height - fLbl.Height) * 0.6f;

                    using (var b = new SolidBrush(lc))
                        ShadowString(g, lbl, fLbl, b, lx, lblY, 1f, false);
                    lx += g.MeasureString(lbl, fLbl, int.MaxValue, Typo).Width + 5f;

                    for (int c = 0; c < r.Cells.Count; c++)
                    {
                        if (c > 0) lx += 7f;
                        using (var b = new SolidBrush(r.ColorAt(c)))
                            ShadowString(g, r.Cells[c], fVal, b, lx, rowY + topOff, 1f, false);
                        lx += g.MeasureString(r.Cells[c], fVal, int.MaxValue, Typo).Width;
                    }
                    if (st.RtssStacked) y += lineH;
                    else x = lx;
                }
            }
        }

        // ---------------- 统一测量与绘制核心 ----------------

        private sealed class HudLayout
        {
            public int W, H;
            public float PadL, PadR, PadY;
            public float LabelMax;
            public float[] ColW = new float[0];
            public float HeroH, RowH, AmbH, DivH, GraphH;
            public float Gap1, GapV, GapC, GapGrp;
            public bool HasHero;
            public bool HasBelowHero;
            public bool IsHud;
            public bool HasBackdrop;
        }

        private static HudLayout MeasureHud(Graphics g, Settings st, List<Row> rows,
            OverlaySkin skin, float k, double fpsVal, double lowVal, double gpuTemp, int graphCount)
        {
            var m = new HudLayout();
            string bd = st.OverlayBackdrop ?? "hud";
            m.IsHud = string.Equals(bd, "hud", StringComparison.OrdinalIgnoreCase);
            m.HasBackdrop = m.IsHud || string.Equals(bd, "subtle", StringComparison.OrdinalIgnoreCase);

            if (m.IsHud)
            {
                m.PadL = 13f * k;
                m.PadR = 11f * k;
                m.PadY = 8f * k;
            }
            else if (m.HasBackdrop)
            {
                m.PadL = 10f * k;
                m.PadR = 10f * k;
                m.PadY = 7f * k;
            }
            else
            {
                m.PadL = 6f * k;
                m.PadR = 6f * k;
                m.PadY = 5f * k;
            }

            m.Gap1 = 4.5f * k;
            m.GapV = 11f * k;
            m.GapC = 9f * k;
            m.GapGrp = 14f * k;
            m.HeroH = (float)Math.Round(22f * k);
            m.RowH = (float)Math.Round(18f * k);
            m.AmbH = (float)Math.Round(16f * k);
            m.DivH = (float)Math.Round((m.HasBackdrop ? 7f : 4f) * k);

            string style = st.OverlayStyle;
            Font lf = GetOverlayFont(8.8f * k, FontStyle.Regular);
            Font vf = GetNumericFont(11.4f * k, FontStyle.Bold);
            Font af = GetNumericFont(9.8f * k, FontStyle.Regular);
            Font hf = GetNumericFont(17.5f * k, FontStyle.Bold);
            Font huf = GetOverlayFont(8.5f * k, FontStyle.Bold);
            Font hsf = GetNumericFont(10.2f * k, FontStyle.Bold);

            if (style == "mini")
            {
                float w = 0f;
                bool hasLeft = false;
                if (st.ShowFps)
                {
                    string fpsNum = fpsVal > 0 ? fpsVal.ToString("0") : "--";
                    w += g.MeasureString(fpsNum, hf, int.MaxValue, Typo).Width
                       + 3f * k
                       + g.MeasureString("FPS", huf, int.MaxValue, Typo).Width;
                    hasLeft = true;
                }
                if (st.ShowLow)
                {
                    if (hasLeft) w += 8f * k;
                    string lowStr = "1% " + (lowVal > 0 ? lowVal.ToString("0") : "--");
                    w += g.MeasureString(lowStr, hsf, int.MaxValue, Typo).Width;
                    hasLeft = true;
                }
                if (st.ShowGpuTemp)
                {
                    if (hasLeft) w += 18f * k;
                    Font tf = GetNumericFont(12.8f * k, FontStyle.Bold);
                    string ts = gpuTemp >= 0 ? gpuTemp.ToString("0") + "°C" : "--°C";
                    w += g.MeasureString(ts, tf, int.MaxValue, Typo).Width;
                }
                m.W = (int)Math.Ceiling(w + m.PadL + m.PadR);
                m.H = (int)Math.Ceiling(m.HeroH + m.PadY * 2f);
                return m;
            }

            if (style == "v")
            {
                int digitW = (int)Math.Ceiling(g.MeasureString("0", vf, int.MaxValue, Typo).Width);
                float heroW = 0f;
                int cols = 0;

                float ambMaxW = 0f;
                for (int i = 0; i < rows.Count; i++)
                {
                    Row r = rows[i];
                    if (r.IsHero && r.Cells.Count > 0)
                    {
                        m.HasHero = true;
                        float hw = Math.Max(g.MeasureString(r.Cells[0], hf, int.MaxValue, Typo).Width,
                                            g.MeasureString("000", hf, int.MaxValue, Typo).Width);
                        hw += 3f * k + g.MeasureString("FPS", huf, int.MaxValue, Typo).Width;
                        for (int c = 1; c < r.Cells.Count; c++)
                        {
                            hw += 8f * k + g.MeasureString(r.Cells[c], hsf, int.MaxValue, Typo).Width;
                        }
                        heroW = hw;
                    }
                    else if (r.Ambient)
                    {
                        m.HasBelowHero = true;
                        Font rowLf = GetOverlayFont(8.2f * k, FontStyle.Regular);
                        float lw = g.MeasureString(r.Label, rowLf, int.MaxValue, Typo).Width;
                        float vw = r.Cells.Count > 0 ? g.MeasureString(r.Cells[0], af, int.MaxValue, Typo).Width : 0f;
                        float aw = lw + m.GapV + vw;
                        if (aw > ambMaxW) ambMaxW = aw;
                    }
                    else
                    {
                        m.HasBelowHero = true;
                        float lw = g.MeasureString(r.Label, lf, int.MaxValue, Typo).Width;
                        if (lw > m.LabelMax) m.LabelMax = lw;
                        if (r.Cells.Count > cols) cols = r.Cells.Count;
                    }
                }

                if (cols > 0)
                {
                    m.ColW = new float[cols];
                    for (int i = 0; i < rows.Count; i++)
                    {
                        Row r = rows[i];
                        if (r.IsHero || r.Ambient) continue;
                        for (int c = 0; c < r.Cells.Count; c++)
                        {
                            float cw;
                            if (c == r.BadgeCell)
                            {
                                Font bf = GetOverlayFont(7.6f * k, FontStyle.Bold);
                                cw = g.MeasureString(r.Cells[c], bf, int.MaxValue, Typo).Width + 8f * k;
                            }
                            else
                            {
                                cw = g.MeasureString(r.Cells[c], vf, int.MaxValue, Typo).Width;
                                float fw = digitW * r.Cells[c].Length;
                                if (fw > cw) cw = fw;
                            }
                            if (cw > m.ColW[c]) m.ColW[c] = cw;
                        }
                    }
                }

                float tableW = 0f;
                if (cols > 0)
                {
                    tableW = m.LabelMax + m.GapV;
                    for (int c = 0; c < cols; c++)
                        tableW += m.ColW[c] + (c + 1 < cols ? m.GapC : 0f);
                }

                m.GraphH = (st.ShowFrameGraph && graphCount >= 2) ? (float)Math.Round(24f * k) : 0f;
                float bodyW = Math.Max(Math.Max(heroW, tableW), ambMaxW);
                if (m.GraphH > 0f && bodyW < 130f * k) bodyW = 130f * k;

                float totalH = m.PadY * 2f;
                bool sawDevice = false;
                bool sawAmbGap = false;
                for (int i = 0; i < rows.Count; i++)
                {
                    Row r = rows[i];
                    if (r.IsHero)
                    {
                        totalH += m.HeroH;
                        if (m.HasBelowHero) totalH += m.DivH;
                    }
                    else if (r.Ambient)
                    {
                        if ((m.HasHero || sawDevice) && !sawAmbGap)
                        {
                            totalH += 3f * k;
                            sawAmbGap = true;
                        }
                        totalH += m.AmbH;
                    }
                    else
                    {
                        sawDevice = true;
                        totalH += m.RowH;
                    }
                }
                if (m.GraphH > 0f)
                {
                    if (m.HasHero && !m.HasBelowHero) totalH += m.DivH;
                    totalH += m.GraphH + 4f * k;
                }

                m.W = (int)Math.Ceiling(bodyW + m.PadL + m.PadR);
                m.H = (int)Math.Ceiling(totalH);
                return m;
            }

            // 横向 HUD ("h")
            Font hFps = GetNumericFont(14.2f * k, FontStyle.Bold);
            Font hUnit = GetOverlayFont(8.2f * k, FontStyle.Bold);
            float totalW = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) totalW += m.GapGrp;
                Row r = rows[i];
                if (r.IsHero && r.Cells.Count > 0)
                {
                    totalW += g.MeasureString(r.Cells[0], hFps, int.MaxValue, Typo).Width
                            + 2.5f * k
                            + g.MeasureString("FPS", hUnit, int.MaxValue, Typo).Width;
                    for (int c = 1; c < r.Cells.Count; c++)
                        totalW += 7f * k + g.MeasureString(r.Cells[c], hsf, int.MaxValue, Typo).Width;
                }
                else
                {
                    Font rowLf = r.Ambient ? GetOverlayFont(8.2f * k, FontStyle.Regular) : lf;
                    Font rowVf = r.Ambient ? af : vf;
                    totalW += g.MeasureString(r.Label, rowLf, int.MaxValue, Typo).Width + m.Gap1;
                    for (int c = 0; c < r.Cells.Count; c++)
                    {
                        if (c > 0) totalW += 5.5f * k;
                        totalW += g.MeasureString(r.Cells[c], rowVf, int.MaxValue, Typo).Width;
                    }
                }
            }

            m.W = (int)Math.Ceiling(totalW + m.PadL + m.PadR);
            m.H = (int)Math.Ceiling(22f * k + m.PadY * 2f);
            return m;
        }

        private static void DrawHud(Graphics g, Settings st, List<Row> rows, OverlaySkin skin,
            HudLayout m, float ox, float oy, float k, double fpsVal, double lowVal, double gpuTemp, bool unlockedHint,
            float[] graphBuf, int graphCount, float graphAvgMs)
        {
            DrawBackdrop(g, ox, oy, m.W, m.H, k, st.OverlayBackdrop ?? "hud", skin, unlockedHint);

            string style = st.OverlayStyle;
            Font lf = GetOverlayFont(8.8f * k, FontStyle.Regular);
            Font vf = GetNumericFont(11.4f * k, FontStyle.Bold);
            Font af = GetNumericFont(9.8f * k, FontStyle.Regular);
            Font hf = GetNumericFont(17.5f * k, FontStyle.Bold);
            Font huf = GetOverlayFont(8.5f * k, FontStyle.Bold);
            Font hsf = GetNumericFont(10.2f * k, FontStyle.Bold);

            if (style == "mini")
            {
                float x = ox + m.PadL;
                float cy = oy + m.H / 2f;
                bool hasLeft = false;
                float refBottom = cy + m.HeroH / 2f - 2f * k;

                if (st.ShowFps)
                {
                    string fpsNum = fpsVal > 0 ? fpsVal.ToString("0") : "--";
                    SizeF ns = g.MeasureString(fpsNum, hf, int.MaxValue, Typo);
                    SizeF us = g.MeasureString("FPS", huf, int.MaxValue, Typo);
                    float numY = cy - ns.Height / 2f;
                    refBottom = numY + ns.Height;
                    using (var b = new SolidBrush(skin.Fps))
                        ShadowString(g, fpsNum, hf, b, x, numY, k, m.HasBackdrop);
                    x += ns.Width + 3f * k;

                    float unitY = refBottom - us.Height - 2.2f * k;
                    using (var b = new SolidBrush(skin.Label))
                        ShadowString(g, "FPS", huf, b, x, unitY, k, m.HasBackdrop);
                    x += us.Width;
                    hasLeft = true;
                }

                if (st.ShowLow)
                {
                    if (hasLeft) x += 8f * k;
                    string lowStr = "1% " + (lowVal > 0 ? lowVal.ToString("0") : "--");
                    SizeF ls = g.MeasureString(lowStr, hsf, int.MaxValue, Typo);
                    float ly = st.ShowFps ? (refBottom - ls.Height - 1.5f * k) : (cy - ls.Height / 2f);
                    using (var b = new SolidBrush(skin.Low))
                        ShadowString(g, lowStr, hsf, b, x, ly, k, m.HasBackdrop);
                    x += ls.Width;
                    hasLeft = true;
                }

                if (st.ShowGpuTemp)
                {
                    if (hasLeft)
                    {
                        float divX = x + 9f * k;
                        using (var pen = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
                            g.DrawLine(pen, divX, oy + m.H * 0.24f, divX, oy + m.H * 0.76f);
                        x = divX + 9f * k;
                    }
                    Font tf = GetNumericFont(12.8f * k, FontStyle.Bold);
                    string ts = gpuTemp >= 0 ? gpuTemp.ToString("0") + "°C" : "--°C";
                    SizeF tsz = g.MeasureString(ts, tf, int.MaxValue, Typo);
                    using (var b = new SolidBrush(skin.Temp(gpuTemp, st.OverlayTempWarn, st.OverlayTempHot)))
                        ShadowString(g, ts, tf, b, x, cy - tsz.Height / 2f, k, m.HasBackdrop);
                }
                return;
            }

            if (style == "v")
            {
                float y = oy + m.PadY;
                bool sawDevice = false;
                bool sawAmbGap = false;
                bool graphDrawn = false;

                for (int i = 0; i < rows.Count; i++)
                {
                    Row r = rows[i];
                    if (r.IsHero && r.Cells.Count > 0)
                    {
                        float x = ox + m.PadL;
                        SizeF ns = g.MeasureString(r.Cells[0], hf, int.MaxValue, Typo);
                        SizeF us = g.MeasureString("FPS", huf, int.MaxValue, Typo);
                        float ny = y + (m.HeroH - ns.Height) / 2f;

                        using (var b = new SolidBrush(r.ColorAt(0)))
                            ShadowString(g, r.Cells[0], hf, b, x, ny, k, m.HasBackdrop);
                        x += Math.Max(ns.Width, g.MeasureString("000", hf, int.MaxValue, Typo).Width) + 3f * k;

                        float uy = ny + ns.Height - us.Height - 2.2f * k;
                        using (var b = new SolidBrush(skin.Label))
                            ShadowString(g, "FPS", huf, b, x, uy, k, m.HasBackdrop);
                        x += us.Width;

                        for (int c = 1; c < r.Cells.Count; c++)
                        {
                            x += 8f * k;
                            SizeF ss = g.MeasureString(r.Cells[c], hsf, int.MaxValue, Typo);
                            float sy = ny + ns.Height - ss.Height - 1.5f * k;
                            using (var b = new SolidBrush(r.ColorAt(c)))
                                ShadowString(g, r.Cells[c], hsf, b, x, sy, k, m.HasBackdrop);
                            x += ss.Width;
                        }

                        y += m.HeroH;
                        if (m.HasBelowHero || m.GraphH > 0f)
                        {
                            if (m.HasBackdrop)
                            {
                                float dy = y + m.DivH / 2f;
                                using (var pen = new Pen(Color.FromArgb(28, 255, 255, 255), 1f))
                                    g.DrawLine(pen, ox + m.PadL, dy, ox + m.W - m.PadR, dy);
                            }
                            y += m.DivH;
                        }
                        if (m.GraphH > 0f && !graphDrawn)
                        {
                            DrawFrameGraph(g, graphBuf, graphCount, graphAvgMs,
                                ox + m.PadL, y, m.W - m.PadL - m.PadR, m.GraphH, k, skin, m.HasBackdrop);
                            y += m.GraphH + 4f * k;
                            graphDrawn = true;
                        }
                        continue;
                    }

                    if (m.GraphH > 0f && !graphDrawn)
                    {
                        DrawFrameGraph(g, graphBuf, graphCount, graphAvgMs,
                            ox + m.PadL, y, m.W - m.PadL - m.PadR, m.GraphH, k, skin, m.HasBackdrop);
                        y += m.GraphH + 4f * k;
                        graphDrawn = true;
                    }

                    if (r.Ambient && (m.HasHero || sawDevice) && !sawAmbGap)
                    {
                        y += 3f * k;
                        sawAmbGap = true;
                    }
                    if (!r.Ambient) sawDevice = true;

                    float rh = r.Ambient ? m.AmbH : m.RowH;
                    Font rowLf = r.Ambient ? GetOverlayFont(8.2f * k, FontStyle.Regular) : lf;
                    Font rowVf = r.Ambient ? af : vf;

                    SizeF ls = g.MeasureString(r.Label, rowLf, int.MaxValue, Typo);
                    using (var b = new SolidBrush(r.Ambient ? skin.Ambient : skin.Label))
                        ShadowString(g, r.Label, rowLf, b, ox + m.PadL, y + (rh - ls.Height) / 2f, k, m.HasBackdrop);

                    if (r.Ambient)
                    {
                        if (r.Cells.Count > 0)
                        {
                            SizeF cs = g.MeasureString(r.Cells[0], rowVf, int.MaxValue, Typo);
                            using (var b = new SolidBrush(r.ColorAt(0)))
                                ShadowString(g, r.Cells[0], rowVf, b, ox + m.W - m.PadR - cs.Width,
                                    y + (rh - cs.Height) / 2f, k, m.HasBackdrop);
                        }
                    }
                    else
                    {
                        float cx = ox + m.PadL + m.LabelMax + m.GapV;
                        for (int c = 0; c < r.Cells.Count && c < m.ColW.Length; c++)
                        {
                            if (c == r.BadgeCell)
                            {
                                Font bf = GetOverlayFont(7.6f * k, FontStyle.Bold);
                                SizeF bs = g.MeasureString(r.Cells[c], bf, int.MaxValue, Typo);
                                float bw = bs.Width + 7f * k;
                                float bh = bs.Height + 2.5f * k;
                                float bx = cx + m.ColW[c] - bw;
                                float by = y + (rh - bh) / 2f;
                                Color bc = r.ColorAt(c);
                                var brRect = new RectangleF(bx, by, bw, bh);
                                using (GraphicsPath bp = RoundRect(brRect, 3f * k))
                                {
                                    using (var bg = new SolidBrush(Color.FromArgb(46, bc)))
                                        g.FillPath(bg, bp);
                                    using (var pen = new Pen(Color.FromArgb(145, bc), 1f))
                                        g.DrawPath(pen, bp);
                                }
                                using (var b = new SolidBrush(bc))
                                    g.DrawString(r.Cells[c], bf, b, bx + (bw - bs.Width) / 2f, by + (bh - bs.Height) / 2f, Typo);
                            }
                            else
                            {
                                SizeF cs = g.MeasureString(r.Cells[c], rowVf, int.MaxValue, Typo);
                                using (var b = new SolidBrush(r.ColorAt(c)))
                                    ShadowString(g, r.Cells[c], rowVf, b, cx + m.ColW[c] - cs.Width,
                                        y + (rh - cs.Height) / 2f, k, m.HasBackdrop);
                            }
                            cx += m.ColW[c] + m.GapC;
                        }
                    }
                    y += rh;
                }
                return;
            }

            // 横向 HUD ("h")
            {
                Font hFps = GetNumericFont(14.2f * k, FontStyle.Bold);
                Font hUnit = GetOverlayFont(8.2f * k, FontStyle.Bold);
                float x = ox + m.PadL;
                float cy = oy + m.H / 2f;

                for (int i = 0; i < rows.Count; i++)
                {
                    if (i > 0)
                    {
                        float divX = x + m.GapGrp / 2f;
                        using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), 1f))
                            g.DrawLine(pen, divX, oy + m.H * 0.26f, divX, oy + m.H * 0.74f);
                        x += m.GapGrp;
                    }

                    Row r = rows[i];
                    if (r.IsHero && r.Cells.Count > 0)
                    {
                        SizeF ns = g.MeasureString(r.Cells[0], hFps, int.MaxValue, Typo);
                        SizeF us = g.MeasureString("FPS", hUnit, int.MaxValue, Typo);
                        float ny = cy - ns.Height / 2f;
                        using (var b = new SolidBrush(r.ColorAt(0)))
                            ShadowString(g, r.Cells[0], hFps, b, x, ny, k, m.HasBackdrop);
                        x += ns.Width + 2.5f * k;

                        float uy = ny + ns.Height - us.Height - 1.6f * k;
                        using (var b = new SolidBrush(skin.Label))
                            ShadowString(g, "FPS", hUnit, b, x, uy, k, m.HasBackdrop);
                        x += us.Width;

                        for (int c = 1; c < r.Cells.Count; c++)
                        {
                            x += 7f * k;
                            SizeF ss = g.MeasureString(r.Cells[c], hsf, int.MaxValue, Typo);
                            using (var b = new SolidBrush(r.ColorAt(c)))
                                ShadowString(g, r.Cells[c], hsf, b, x, cy - ss.Height / 2f + 0.5f * k, k, m.HasBackdrop);
                            x += ss.Width;
                        }
                    }
                    else
                    {
                        Font rowLf = r.Ambient ? GetOverlayFont(8.2f * k, FontStyle.Regular) : lf;
                        Font rowVf = r.Ambient ? af : vf;
                        SizeF ls = g.MeasureString(r.Label, rowLf, int.MaxValue, Typo);
                        using (var b = new SolidBrush(r.Ambient ? skin.Ambient : skin.Label))
                            ShadowString(g, r.Label, rowLf, b, x, cy - ls.Height / 2f + 0.5f * k, k, m.HasBackdrop);
                        x += ls.Width + m.Gap1;

                        for (int c = 0; c < r.Cells.Count; c++)
                        {
                            if (c > 0) x += 5.5f * k;
                            SizeF cs = g.MeasureString(r.Cells[c], rowVf, int.MaxValue, Typo);
                            using (var b = new SolidBrush(r.ColorAt(c)))
                                ShadowString(g, r.Cells[c], rowVf, b, x, cy - cs.Height / 2f, k, m.HasBackdrop);
                            x += cs.Width;
                        }
                    }
                }
            }
        }

        /// <summary>绘制 90 帧实时帧时间折线图（尖刺着色，零每帧 Pen/Brush 泄漏）。</summary>
        private static void DrawFrameGraph(Graphics g, float[] buf, int count, float avgMs,
            float gx, float gy, float gw, float gh, float k, OverlaySkin skin, bool hasBackdrop)
        {
            if (buf == null || count < 2 || gw < 16f || gh < 8f) return;
            int n = Math.Min(count, Math.Min(buf.Length, 90));

            if (hasBackdrop)
            {
                var bgRect = new RectangleF(gx, gy, gw, gh);
                using (GraphicsPath bgPath = RoundRect(bgRect, 3f * k))
                using (var bgBrush = new SolidBrush(Color.FromArgb(52, 0, 0, 0)))
                    g.FillPath(bgBrush, bgPath);
            }

            float peak = 0f;
            for (int i = 0; i < n; i++)
                if (buf[i] > peak) peak = buf[i];

            float baseAvg = avgMs > 0.5f ? avgMs : 8.3f;
            float spikeThresh = Math.Max(baseAvg * 1.8f, baseAvg + 8f);
            float severeThresh = spikeThresh * 1.35f;
            float yMax = Math.Max(peak * 1.08f, Math.Max(baseAvg * 2.2f, 16.7f));
            if (yMax < 4f) yMax = 4f;

            float pad = 2f * k;
            float innerH = Math.Max(4f, gh - pad * 2f);
            float bottom = gy + gh - pad;

            float avgY = bottom - Math.Min(1f, baseAvg / yMax) * innerH;
            using (var avgPen = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
                g.DrawLine(avgPen, gx + 1f, avgY, gx + gw - 1f, avgY);

            float step = (gw - 2f) / 89f;
            int offset = 90 - n;
            float lineW = Math.Max(1.1f, 1.35f * k);

            using (var normPen = new Pen(Color.FromArgb(230, skin.Frame), lineW))
            using (var amberPen = new Pen(Color.FromArgb(245, 255, 179, 71), lineW * 1.1f))
            using (var spikePen = new Pen(Color.FromArgb(250, 224, 79, 69), lineW * 1.15f))
            {
                normPen.StartCap = LineCap.Round;
                normPen.EndCap = LineCap.Round;
                amberPen.StartCap = LineCap.Round;
                amberPen.EndCap = LineCap.Round;
                spikePen.StartCap = LineCap.Round;
                spikePen.EndCap = LineCap.Round;

                float prevX = gx + 1f + offset * step;
                float prevV = buf[0] > 0f ? buf[0] : 0f;
                float prevY = bottom - Math.Min(1f, prevV / yMax) * innerH;

                for (int i = 1; i < n; i++)
                {
                    float curX = gx + 1f + (offset + i) * step;
                    float curV = buf[i] > 0f ? buf[i] : 0f;
                    float curY = bottom - Math.Min(1f, curV / yMax) * innerH;

                    float segMax = Math.Max(prevV, curV);
                    Pen pen = segMax > severeThresh ? spikePen : (segMax > spikeThresh ? amberPen : normPen);
                    g.DrawLine(pen, prevX, prevY, curX, curY);

                    prevX = curX;
                    prevY = curY;
                    prevV = curV;
                }
            }
        }

        /// <summary>绘制现代 HUD 毛玻璃暗底 / 轻透底 / 无底板。</summary>
        private static void DrawBackdrop(Graphics g, float x, float y, float w, float h,
            float k, string mode, OverlaySkin skin, bool unlockedHint)
        {
            if (string.Equals(mode, "hud", StringComparison.OrdinalIgnoreCase))
            {
                float rad = 8f * k;
                var rect = new RectangleF(x + 0.5f, y + 0.5f, w - 1f, h - 1f);
                using (GraphicsPath path = RoundRect(rect, rad))
                {
                    using (var grad = new LinearGradientBrush(rect,
                        Color.FromArgb(196, 17, 20, 28),
                        Color.FromArgb(218, 10, 12, 18),
                        LinearGradientMode.Vertical))
                        g.FillPath(grad, path);

                    // 顶部微弱高光，增加玻璃层次感
                    using (var sheen = new Pen(Color.FromArgb(26, 255, 255, 255), 1f))
                        g.DrawLine(sheen, x + rad, y + 1.2f, x + w - rad, y + 1.2f);

                    using (var border = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
                        g.DrawPath(border, path);
                }

                // 左侧主题色竖条点缀
                float barW = Math.Max(2.2f, 2.8f * k);
                float barInsetY = Math.Min(h * 0.25f, 6.5f * k);
                var barRect = new RectangleF(x + 4.5f * k, y + barInsetY, barW, Math.Max(6f, h - barInsetY * 2f));
                using (GraphicsPath bp = RoundRect(barRect, barW / 2f))
                using (var br = new SolidBrush(Color.FromArgb(235, skin.Accent)))
                    g.FillPath(br, bp);
            }
            else if (string.Equals(mode, "subtle", StringComparison.OrdinalIgnoreCase))
            {
                float rad = 6f * k;
                var rect = new RectangleF(x + 0.5f, y + 0.5f, w - 1f, h - 1f);
                using (GraphicsPath path = RoundRect(rect, rad))
                {
                    using (var bg = new SolidBrush(Color.FromArgb(118, 10, 12, 17)))
                        g.FillPath(bg, path);
                    using (var border = new Pen(Color.FromArgb(26, 255, 255, 255), 1f))
                        g.DrawPath(border, path);
                }
            }

            if (unlockedHint)
            {
                var dr = new RectangleF(x + 1f, y + 1f, w - 2f, h - 2f);
                using (var pen = new Pen(Color.FromArgb(150, 255, 255, 255), 1f))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(pen, dr.X, dr.Y, dr.Width, dr.Height);
                }
            }
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static StringFormat NewTypo()
        {
            StringFormat f = StringFormat.GenericTypographic;
            f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            return f;
        }

        /// <summary>有底板时用利落单层投影，无底板时用干净四向描边托字。</summary>
        private static void ShadowString(Graphics g, string text, Font font, Brush brush,
            float x, float y, float k, bool hasBackdrop)
        {
            if (hasBackdrop)
            {
                float o = k >= 1.5f ? 1.5f : 1f;
                using (var sh = new SolidBrush(Color.FromArgb(135, 0, 0, 0)))
                    g.DrawString(text, font, sh, x + o, y + o, Typo);
            }
            else
            {
                float o = k >= 1.4f ? 1.5f : 1f;
                using (var sh = new SolidBrush(Color.FromArgb(165, 0, 0, 0)))
                {
                    g.DrawString(text, font, sh, x + o, y, Typo);
                    g.DrawString(text, font, sh, x - o, y, Typo);
                    g.DrawString(text, font, sh, x, y - o, Typo);
                    g.DrawString(text, font, sh, x, y + o, Typo);
                }
                using (var ds = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                    g.DrawString(text, font, ds, x + o, y + o * 1.6f, Typo);
            }
            g.DrawString(text, font, brush, x, y, Typo);
        }

        private static string RtssLabel(string label)
        {
            if (label == "内存") return "RAM";
            if (label == "时间") return "TIME";
            if (label == "时长") return "UP";
            return label;
        }

        private static int ClampRtssScale(int s)
        {
            if (s < 50) return 50;
            if (s > 200) return 200;
            return s;
        }

        /// <summary>
        /// 交给 RTSS 的文字：复用统一主题色板，FPS 放大主视觉，设备标签等宽对齐，单行加竖线分隔。
        /// 每个颜色标签严格以 &lt;C&gt; 闭合，避免污染 RTSS 后续槽位。
        /// </summary>
        private static string RtssText(Settings st, List<Row> rows, OverlaySkin skin)
        {
            const string ResetC = "<C>";
            int baseS = ClampRtssScale(st.RtssScale);
            int heroS = ClampRtssScale((int)Math.Round(baseS * 1.18));
            int unitS = ClampRtssScale((int)Math.Round(baseS * 0.82));
            int lblS = ClampRtssScale((int)Math.Round(baseS * 0.90));
            int subS = ClampRtssScale((int)Math.Round(baseS * 0.92));

            var sb = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                {
                    if (st.RtssStacked) sb.Append('\n');
                    else sb.Append("<S=").Append(lblS.ToString(CultureInfo.InvariantCulture))
                           .Append("><C=FF6E7A8A> | ").Append(ResetC);
                }

                Row r = rows[i];
                if (r.IsHero && r.Cells.Count > 0)
                {
                    sb.Append("<S=").Append(heroS.ToString(CultureInfo.InvariantCulture))
                      .Append("><C=FF").Append(OverlayPalette.RgbHex(r.ColorAt(0))).Append('>')
                      .Append(r.Cells[0]).Append(ResetC)
                      .Append("<S=").Append(unitS.ToString(CultureInfo.InvariantCulture))
                      .Append("><C=FF").Append(OverlayPalette.RgbHex(skin.Label)).Append("> FPS").Append(ResetC);

                    if (r.Cells.Count > 1)
                    {
                        sb.Append("<S=").Append(subS.ToString(CultureInfo.InvariantCulture)).Append('>');
                        for (int c = 1; c < r.Cells.Count; c++)
                        {
                            sb.Append("  <C=FF").Append(OverlayPalette.RgbHex(r.ColorAt(c))).Append('>')
                              .Append(r.Cells[c]).Append(ResetC);
                        }
                    }
                }
                else
                {
                    string lbl = RtssLabel(r.Label);
                    if (st.RtssStacked) lbl = lbl.PadRight(4, ' ');
                    Color lc = r.Ambient ? skin.Ambient : skin.Label;

                    sb.Append("<S=").Append(lblS.ToString(CultureInfo.InvariantCulture))
                      .Append("><C=FF").Append(OverlayPalette.RgbHex(lc)).Append('>')
                      .Append(lbl).Append(ResetC)
                      .Append("<S=").Append(baseS.ToString(CultureInfo.InvariantCulture)).Append('>');

                    for (int c = 0; c < r.Cells.Count; c++)
                    {
                        sb.Append(' ');
                        if (c == r.BadgeCell)
                            sb.Append(Rtss.ThrottleBadge(r.Cells[c]));
                        else
                            sb.Append("<C=FF").Append(OverlayPalette.RgbHex(r.ColorAt(c))).Append('>')
                              .Append(r.Cells[c]).Append(ResetC);
                    }
                }
            }
            sb.Append("<S>");
            return sb.ToString();
        }

        /// <summary>供 --selftest 校验主题、底板与 RTSS 标签闭合。</summary>
        public static bool SelfTestVisuals(out string note)
        {
            note = "";
            var st = new Settings();
            st.ShowFps = true; st.ShowLow = true; st.ShowFrameTime = true;
            st.ShowCpu = true; st.ShowGpu = true; st.ShowGpuTemp = true;
            st.ShowRam = true; st.ShowRamPct = true; st.ShowClock = true; st.ShowSession = true;
            st.ShowFrameGraph = true;

            var s = new SysSample { Cpu = 42, Gpu = 88, GpuTemp = 76, GpuThrottle = "THRM", RamUsed = 10737418240UL, RamPct = 62 };
            DateTime now = DateTime.Now;

            for (int t = 0; t < OverlayPalette.ThemeKeys.Length; t++)
            {
                st.OverlayTheme = OverlayPalette.ThemeKeys[t];
                OverlaySkin skin = OverlayPalette.For(st.OverlayTheme);
                List<Row> rows = BuildRows(st, skin, s, 144, 112, 86, 6.9, now, now.AddMinutes(-12));

                st.RtssStacked = true;
                string txt1 = RtssText(st, rows, skin);
                st.RtssStacked = false;
                string txt2 = RtssText(st, rows, skin);

                if (txt1.Length == 0 || txt2.Length == 0) { note = "empty rtss text"; return false; }
                if (Encoding.Default.GetByteCount(txt1) >= 4096 || Encoding.Default.GetByteCount(txt2) >= 4096)
                {
                    note = "rtss text exceeds 4096 bytes";
                    return false;
                }
                if (!TagsBalanced(txt1) || !TagsBalanced(txt2))
                {
                    note = "unclosed <C> tag in " + st.OverlayTheme;
                    return false;
                }
            }

            // 验证三种样式 + 三种底板能无异常完成预览绘制
            string[] styles = { "v", "h", "mini" };
            string[] bds = { "hud", "subtle", "none" };
            using (var bmp = new Bitmap(420, 240, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                for (int i = 0; i < styles.Length; i++)
                {
                    st.OverlayStyle = styles[i];
                    for (int j = 0; j < bds.Length; j++)
                    {
                        st.OverlayBackdrop = bds[j];
                        PaintPreview(g, st, new Rectangle(0, 0, 420, 240), false);
                    }
                }
                PaintPreview(g, st, new Rectangle(0, 0, 420, 240), true);
            }

            note = "5 themes, 3 backdrops, 3 styles, rtss balanced";
            return true;
        }

        private static bool TagsBalanced(string text)
        {
            int open = 0, close = 0, idx = 0;
            while ((idx = text.IndexOf("<C=", idx, StringComparison.Ordinal)) >= 0) { open++; idx += 3; }
            idx = 0;
            while ((idx = text.IndexOf("<C>", idx, StringComparison.Ordinal)) >= 0) { close++; idx += 3; }
            return open > 0 && open == close;
        }

        /// <summary>内容签名:只有文字或排布变了才重新量尺寸、重画。</summary>
        private static string RowsKey(List<Row> rows)
        {
            string s = "";
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) s += "|";
                s += (rows[i].IsHero ? "*" : "") + rows[i].Label + ":";
                for (int c = 0; c < rows[i].Cells.Count; c++)
                {
                    if (c > 0) s += ",";
                    s += rows[i].Cells[c];
                }
            }
            return s;
        }

        /// <summary>在帧边界（不持有任何 Font 引用时）按上限安全回收字体池。</summary>
        private static void TrimFontPool()
        {
            lock (FontLock)
            {
                if (FontPool.Count <= 48) return;
                foreach (Font old in FontPool.Values) { try { old.Dispose(); } catch { } }
                FontPool.Clear();
            }
        }

        private static Font GetOverlayFont(float px, FontStyle style)
        {
            if (px < 6f) px = 6f;
            string family = I18n.IsEn ? "Segoe UI" : "Microsoft YaHei UI";
            string key = "t|" + family + "|" + px.ToString("0.0", CultureInfo.InvariantCulture) + "|" + ((int)style).ToString();
            lock (FontLock)
            {
                Font f;
                if (FontPool.TryGetValue(key, out f)) return f;
                try { f = new Font(family, px, style, GraphicsUnit.Pixel); }
                catch { f = new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel); }
                FontPool[key] = f;
                return f;
            }
        }

        private static Font GetNumericFont(float px, FontStyle style)
        {
            if (px < 6f) px = 6f;
            string key = "n|" + px.ToString("0.0", CultureInfo.InvariantCulture) + "|" + ((int)style).ToString();
            lock (FontLock)
            {
                Font f;
                if (FontPool.TryGetValue(key, out f)) return f;
                try { f = new Font("Consolas", px, style, GraphicsUnit.Pixel); }
                catch
                {
                    try { f = new Font(FontFamily.GenericMonospace, px, style, GraphicsUnit.Pixel); }
                    catch { f = new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel); }
                }
                FontPool[key] = f;
                return f;
            }
        }

        /// <summary>复用的绘制画布:尺寸没变就不重新分配,尺寸变了换一张。</summary>
        private Bitmap Canvas(int w, int h)
        {
            if (canvas != null && canvasW == w && canvasH == h) return canvas;
            ReleaseCanvas();
            try
            {
                canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                canvasW = w;
                canvasH = h;
            }
            catch
            {
                canvas = null;
                canvasW = 0;
                canvasH = 0;
            }
            return canvas;
        }

        private void ReleaseCanvas()
        {
            if (canvas != null) { try { canvas.Dispose(); } catch { } }
            canvas = null;
            canvasW = 0;
            canvasH = 0;
        }

        /// <summary>把 GDI+ 绘制的 32bpp 位图预乘 alpha 后交给 UpdateLayeredWindow。</summary>
        private void Blit(Bitmap bmp, int w, int h)
        {
            if (bmp == null || w <= 0 || h <= 0) return;

            if (memDc == IntPtr.Zero || bmpW != w || bmpH != h)
            {
                ReleaseDib();
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) return;
                memDc = Native.CreateCompatibleDC(screen);
                Native.ReleaseDC(IntPtr.Zero, screen);
                if (memDc == IntPtr.Zero) return;

                var header = new Native.BITMAPINFOHEADER();
                header.biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
                header.biWidth = w;
                header.biHeight = -h;         // top-down
                header.biPlanes = 1;
                header.biBitCount = 32;
                header.biCompression = 0;     // BI_RGB
                dib = Native.CreateDIBSection(memDc, ref header, 0, out dibBits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || dibBits == IntPtr.Zero)
                {
                    ReleaseDib();
                    return;
                }
                oldObj = Native.SelectObject(memDc, dib);
                bmpW = w; bmpH = h;
            }

            if (dibBits == IntPtr.Zero) return;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { Premultiply(data.Scan0, dibBits, w * h); }
            finally { bmp.UnlockBits(data); }

            var dst = new Native.POINT { X = Left, Y = Top };
            var size = new Native.SIZE { cx = w, cy = h };
            var src = new Native.POINT { X = 0, Y = 0 };
            var blend = new Native.BLENDFUNCTION();
            blend.BlendOp = Native.AC_SRC_OVER;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = Native.AC_SRC_ALPHA;
            if (!Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA))
            {
                return;
            }
        }

        /// <summary>UpdateLayeredWindow 的 AC_SRC_ALPHA 要求颜色分量已按 alpha 预乘。</summary>
        private void Premultiply(IntPtr src, IntPtr dst, int pixels)
        {
            int bytes = pixels * 4;
            if (bytes <= 0) return;
            if (premul.Length < bytes) premul = new byte[bytes];
            Marshal.Copy(src, premul, 0, bytes);
            for (int i = 0; i < bytes; i += 4)
            {
                int a = premul[i + 3];
                if (a == 255) continue;
                if (a == 0) { premul[i] = 0; premul[i + 1] = 0; premul[i + 2] = 0; continue; }
                premul[i] = (byte)(premul[i] * a / 255);
                premul[i + 1] = (byte)(premul[i + 1] * a / 255);
                premul[i + 2] = (byte)(premul[i + 2] * a / 255);
            }
            Marshal.Copy(premul, 0, dst, bytes);
        }

        private void ReleaseDib()
        {
            try
            {
                if (memDc != IntPtr.Zero && oldObj != IntPtr.Zero) Native.SelectObject(memDc, oldObj);
                if (dib != IntPtr.Zero) Native.DeleteObject(dib);
                if (memDc != IntPtr.Zero) Native.DeleteDC(memDc);
            }
            catch { }
            memDc = IntPtr.Zero; dib = IntPtr.Zero; dibBits = IntPtr.Zero; oldObj = IntPtr.Zero;
            bmpW = 0; bmpH = 0;
        }

        private void ReleaseProbe()
        {
            if (probeGfx != null) { try { probeGfx.Dispose(); } catch { } probeGfx = null; }
            if (probeBmp != null) { try { probeBmp.Dispose(); } catch { } probeBmp = null; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            timer.Stop();
            ReleaseDib();
            ReleaseCanvas();
            ReleaseProbe();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { timer.Stop(); timer.Dispose(); } catch { }
                ReleaseDib();
                ReleaseCanvas();
                ReleaseProbe();
                premul = new byte[0];
            }
            base.Dispose(disposing);
        }
    }
}
