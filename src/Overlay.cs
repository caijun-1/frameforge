using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge
{
    /// <summary>
    /// 游戏内叠加层:分层置顶窗口 + UpdateLayeredWindow 逐像素透明。
    /// 不注入游戏,不接管输入;锁定后完全鼠标穿透。
    /// </summary>
    public sealed class Overlay : Form
    {
        private readonly Settings st;
        private readonly Timer timer;
        private SysSample last = new SysSample();
        private double fps = -1;
        private double low = -1;
        private DateTime upFrom = DateTime.Now;

        private bool moving;
        // 用户是否打开了叠加层。窗口被隐藏(内容清空、被游戏压掉)后靠它自愈显示,
        // 否则 Hide 之后没有任何东西会把窗口叫回来,开关看着还是"开"的却永远不出现。
        private bool wanted;
        private Rectangle lastWa = Rectangle.Empty;
        // 上次画上去的内容。数值没变就不重画:UpdateLayeredWindow 每次都会让 DWM
        // 重新合成一层,游戏里叠加层本来就在和游戏抢合成,能少一次是一次。
        private string lastKey = "";

        // 每 200ms 重画一次,字体不能每次都 new:Font 带 GDI+ 句柄,不释放会一直涨。
        private readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        // 排版用的是"无额外内边距"的格式,测出来多宽就画多宽,右对齐不会漂。
        private static readonly StringFormat Typo = NewTypo();

        // 帧率数值用"小飞机"(MSI Afterburner / RTSS)那套粉色,一眼就能和别的数值分开。
        private static readonly Color FpsColor = Color.FromArgb(255, 255, 79, 216);

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
        // 渲染异常按分钟记一次日志:真出问题时不至于每 200ms 写一次盘。
        private DateTime lastErrLog = DateTime.MinValue;

        public Overlay(Settings settings)
        {
            st = settings;
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
            if (Visible) Hide();
        }

        /// <summary>叠加层开关/样式/大小/锁定变化后调用。</summary>
        public void ApplySettings()
        {
            ApplyInputMode();
            Render();
        }

        public void Feed(SysSample s, double currentFps, double low1)
        {
            last = s ?? new SysSample();
            fps = currentFps;
            low = low1;
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
                // 小飞机那种 OSD 是贴着屏幕角的:离边只留一点点,越靠边越不挡画面。
                // 边距跟着缩放同比放大,大字号时也不会压到屏幕边。
                int m = (int)Math.Round(10 * k);
                int cx = (corner == "tl" || corner == "bl") ? wa.Left + m : wa.Right - w - m;
                int cy = (corner == "tl" || corner == "tr") ? wa.Top + m : wa.Bottom - h - m;
                Location = new Point(cx, cy);
                return;
            }

            if (st.OverlayX >= 0 && st.OverlayY >= 0)
            {
                // 用户拖动过就沿用他的位置;但拔掉副屏、改分辨率、把游戏挪到另一块屏之后,
                // 旧坐标可能整个落在目标屏外——只在整个虚拟桌面里判断不够,
                // 那样窗口会停在用户压根看不到的地方。落在目标屏外就按默认摆位重来。
                var saved = new Rectangle(st.OverlayX, st.OverlayY, w, h);
                if (wa.IntersectsWith(saved))
                {
                    Location = new Point(st.OverlayX, st.OverlayY);
                    return;
                }
            }

            int margin = (int)Math.Round(24 * k);
            int x, y;
            if (st.OverlayStyle == "v")
            {
                // 竖向:贴左侧,纵向居中
                x = wa.Left + margin;
                y = wa.Top + (wa.Height - h) / 2;
            }
            else
            {
                // 横向 / 简洁:水平居中,顶部留一点边距
                x = wa.Left + (wa.Width - w) / 2;
                y = wa.Top + margin;
            }
            Location = new Point(x, y);
        }

        /// <summary>
        /// 叠加层跟着前台窗口所在的显示器走:双屏时游戏在副屏,数据就摆在副屏,
        /// 不会被固定摆到主屏上。配置界面在前台时,自然摆到主窗口那一块屏。
        /// </summary>
        private static Rectangle TargetBounds()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                try { return Screen.FromHandle(fg).WorkingArea; }
                catch { }
            }
            return Screen.PrimaryScreen.WorkingArea;
        }

        /// <summary>
        /// 有些游戏会反复把自己的窗口提到最前,把第三方置顶窗口压到后面。
        /// 每帧重新声明一次 TOPMOST(不移动、不缩放、不抢焦点),被压下去后能自己回来。
        /// </summary>
        private void KeepTopmost()
        {
            if (!IsHandleCreated) return;
            try
            {
                uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
                // 游戏切全屏/切分辨率时可能把窗口顺手收起来,带上 SHOWWINDOW 再声明一次,
                // 内容没变也能重新上屏。
                if (wanted) flags |= Native.SWP_SHOWWINDOW;
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
                // 拖动就是对"四角"的否定:位置改回自动,否则下一帧又被拉回角上。
                st.OverlayCorner = "auto";
                st.Save();
            }
            base.WndProc(ref m);
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

                string style = st.OverlayStyle;
                List<Row> rows = BuildRows();
                // 一个显示项都没勾时窗口收起,但这只是"暂时没内容",开关并没有关。
                // 简洁样式只画帧率/低帧/温度,别的项即使勾了也不会出现在那一行里,
                // 所以它的"有内容"和堆叠样式分开判断,免得挂一条"-- FPS"在屏幕上。
                // 这里不 Show,否则会和下面的 Hide 每 200ms 来回切,画面一直闪。
                bool miniData = st.ShowFps || st.ShowLow || st.ShowGpuTemp;
                bool hasData = style == "mini" ? miniData : rows.Count > 0;
                if (!hasData) { lastKey = ""; if (Visible) Hide(); return; }

                // 开关还开着却看不见窗口(内容清空过、或被游戏切全屏时系统收走了),
                // 先自愈:重新 Show 并清掉缓存,下一帧全量重画。
                if (wanted && !Visible)
                {
                    lastKey = "";
                    try { Show(); }
                    catch { }
                }
                if (!Visible) return;

                // 前台换到另一块屏、或者游戏改了分辨率:工作区变了就强制重新排版,
                // 否则内容没变时会一直沿用旧位置,用户看到的可能是屏外的一条边。
                Rectangle wa = TargetBounds();
                if (wa != lastWa) { lastWa = wa; lastKey = ""; }

                string key = style + "|" + st.OverlayScale.ToString("0.00", CultureInfo.InvariantCulture)
                           + "|" + k.ToString("0.00", CultureInfo.InvariantCulture)
                           + "|" + st.OverlayCorner + "|" + RowsKey(rows);
                if (key == lastKey)
                {
                    // 位置可能被游戏顶掉,内容没变也要重新声明置顶。
                    if (!moving) KeepTopmost();
                    return;
                }
                lastKey = key;
                float labelPt = 9.2f * k;
                float valuePt = 12f * k;
                // 内边距按样式分开给:堆叠 OSD 是贴角的窄条,上下左右只留几个像素,
                // 行距也压紧,整块面积比原来小一圈,挡住的画面更少。
                int padX = (int)Math.Round(8 * k);
                int padY = (int)Math.Round(6 * k);

                // 每块占的宽度必须和绘制时的推进量一字不差地对应,否则误差会随列数累加,
                // 最后一两列被挤出画布,看起来就是"后面的字显示不全"。
                float gap1 = 4 * k;    // 同一组内:标签与数值、数值与数值之间
                float gap2 = 10 * k;   // 横向样式:两组数据之间
                float gapV = 12 * k;   // 竖向:设备名与第一列数值之间
                float gapC = 10 * k;   // 竖向:数值列之间
                float rowH = (float)Math.Round(19 * k);   // 竖向行距(小飞机那种紧凑行距)

                int w = 0, h = 0;
                int labelMax = 0;
                int[] colW = new int[0];
                using (Bitmap probe = new Bitmap(1, 1))
                using (Graphics pg = Graphics.FromImage(probe))
                {
                    pg.TextRenderingHint = TextRenderingHint.AntiAlias;
                    Font lf = OverlayFont(labelPt, FontStyle.Regular);
                    Font vf = OverlayFont(valuePt, FontStyle.Bold);

                    // 分组排布:同一台设备的数值并到一行(CPU 只写一次),
                    // 同一列在所有行里按最宽的那一项对齐。
                    int cols = 0;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        Row r = rows[i];
                        int lw = (int)Math.Ceiling(pg.MeasureString(r.Label, lf, int.MaxValue, Typo).Width);
                        if (lw > labelMax) labelMax = lw;
                        if (r.Cells.Count > cols) cols = r.Cells.Count;
                    }
                    if (cols < 1) cols = 1;
                    colW = new int[cols];
                    for (int i = 0; i < rows.Count; i++)
                    {
                        Row r = rows[i];
                        for (int c = 0; c < r.Cells.Count; c++)
                        {
                            int cw = (int)Math.Ceiling(pg.MeasureString(r.Cells[c], vf, int.MaxValue, Typo).Width);
                            if (cw > colW[c]) colW[c] = cw;
                        }
                    }

                    if (style == "v")
                    {
                        int body = labelMax + (int)Math.Ceiling(gapV);
                        for (int c = 0; c < cols; c++)
                            body += colW[c] + (c + 1 < cols ? (int)Math.Ceiling(gapC) : 0);
                        w = body + padX * 2;
                        h = (int)Math.Round(rows.Count * rowH) + padY * 2;
                    }
                    else if (style == "mini")
                    {
                        w = (int)Math.Ceiling(pg.MeasureString(MiniText(), OverlayFont(valuePt * 1.35f, FontStyle.Bold), int.MaxValue, Typo).Width) + padX * 2;
                        h = (int)Math.Round(28 * k) + padY * 2;
                    }
                    else
                    {
                        // 横向:每组"设备名 + 若干数值"占一块,块与块之间留 gap2。
                        int width = 0;
                        for (int i = 0; i < rows.Count; i++)
                        {
                            Row r = rows[i];
                            width += (int)Math.Ceiling(pg.MeasureString(r.Label, lf, int.MaxValue, Typo).Width + gap1);
                            for (int c = 0; c < r.Cells.Count; c++)
                                width += (int)Math.Ceiling(pg.MeasureString(r.Cells[c], vf, int.MaxValue, Typo).Width + gap1);
                            width += (int)Math.Ceiling(gap2);
                        }
                        w = width + padX * 2;
                        h = (int)Math.Round(24 * k) + padY * 2;
                    }
                }
                if (w < 40) w = 40;
                if (h < 24) h = 24;
                // 尺寸上限:读数异常(比如某传感器返回了超长字符串)时也不去申请一张
                // 几万像素宽的位图,避免一次性吃光 GDI 内存。
                if (w > 4096) w = 4096;
                if (h > 2048) h = 2048;

                // 尺寸算出来后再摆放:居中要按实际宽高算。
                // 用户正在拖动时不能改 Location,否则每 200ms 会被拉回原位置。
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

                        // 整块只留文字:不要圆角底,也不要描边(用户明确要求)。
                        // 没有底色后白字在雪地/亮天空里会糊,所以先压一层深色阴影再画字,
                        // 相当于数字自带描边,背景仍然完全透明。
                        if (style == "v")
                        {
                            // 小飞机那种分组排布:左边设备名只出现一次,右边是这台设备的各项数值。
                            // 同一列的数值右对齐,读数长短变化时列不会左右抖。
                            float y = padY;
                            Font lf = OverlayFont(labelPt, FontStyle.Regular);
                            Font vf = OverlayFont(valuePt, FontStyle.Bold);
                            for (int i = 0; i < rows.Count; i++)
                            {
                                Row r = rows[i];
                                SizeF ls = g.MeasureString(r.Label, lf, int.MaxValue, Typo);
                                using (var b = new SolidBrush(Color.FromArgb(225, 206, 214, 226)))
                                    ShadowString(g, r.Label, lf, b, padX, y + (rowH - ls.Height) / 2f, k);

                                float cx = padX + labelMax + gapV;
                                for (int c = 0; c < r.Cells.Count; c++)
                                {
                                    SizeF cs = g.MeasureString(r.Cells[c], vf, int.MaxValue, Typo);
                                    using (var b = new SolidBrush(r.FpsCell && c == 0 ? FpsColor : Color.White))
                                        ShadowString(g, r.Cells[c], vf, b, cx + colW[c] - cs.Width,
                                            y + (rowH - cs.Height) / 2f, k);
                                    cx += colW[c] + gapC;
                                }
                                y += rowH;
                            }
                        }
                        else if (style == "mini")
                        {
                            // 帧率数字用粉色,后面的温度保持白色,和小飞机那种"只高亮帧率"一致。
                            Font f = OverlayFont(valuePt * 1.35f, FontStyle.Bold);
                            string text = MiniText();
                            int cut = text.IndexOf("   ", StringComparison.Ordinal);
                            string head = cut > 0 ? text.Substring(0, cut) : text;
                            string tail = cut > 0 ? text.Substring(cut).Trim() : "";
                            SizeF hs = g.MeasureString(head, f, int.MaxValue, Typo);
                            SizeF ts = tail.Length > 0 ? g.MeasureString(tail, f, int.MaxValue, Typo) : SizeF.Empty;
                            float totalW = hs.Width + (tail.Length > 0 ? 12 * k + ts.Width : 0);
                            float mx = (w - totalW) / 2f;
                            float my = (h - hs.Height) / 2f;
                            using (var b = new SolidBrush(FpsColor))
                                ShadowString(g, head, f, b, mx, my, k);
                            if (tail.Length > 0)
                                using (var b = new SolidBrush(Color.White))
                                    ShadowString(g, tail, f, b, mx + hs.Width + 12 * k, my, k);
                        }
                        else
                        {
                            float x = padX;
                            float cy = h / 2f;
                            Font lf = OverlayFont(labelPt, FontStyle.Regular);
                            Font vf = OverlayFont(valuePt, FontStyle.Bold);
                            for (int i = 0; i < rows.Count; i++)
                            {
                                Row r = rows[i];
                                SizeF ls = g.MeasureString(r.Label, lf, int.MaxValue, Typo);
                                using (var b = new SolidBrush(Color.FromArgb(220, 196, 206, 220)))
                                    ShadowString(g, r.Label, lf, b, x, cy - ls.Height / 2f, k);
                                x += ls.Width + gap1;
                                for (int c = 0; c < r.Cells.Count; c++)
                                {
                                    SizeF cs = g.MeasureString(r.Cells[c], vf, int.MaxValue, Typo);
                                    using (var b = new SolidBrush(r.FpsCell && c == 0 ? FpsColor : Color.White))
                                        ShadowString(g, r.Cells[c], vf, b, x, cy - cs.Height / 2f, k);
                                    x += cs.Width + gap1;
                                }
                                x += gap2;
                            }
                        }
                    }

                    Blit(bmp, w, h);
                }
            }
            catch (Exception ex)
            {
                // 渲染失败不能把整个程序带走,也不能每 200ms 写一次日志。一分钟记一条就够定位。
                if ((DateTime.Now - lastErrLog).TotalSeconds >= 60)
                {
                    lastErrLog = DateTime.Now;
                    Log.Write("overlay render: " + ex.Message);
                }
            }
        }

        private static StringFormat NewTypo()
        {
            StringFormat f = StringFormat.GenericTypographic;
            f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            return f;
        }

        /// <summary>先压一圈深色阴影再画字:透明背景下在亮场景也能看清。</summary>
        private static void ShadowString(Graphics g, string text, Font font, Brush brush, float x, float y, float k)
        {
            float o = k >= 1.4f ? 2f : 1f;
            using (var sh = new SolidBrush(Color.FromArgb(195, 0, 0, 0)))
            {
                g.DrawString(text, font, sh, x + o, y, Typo);
                g.DrawString(text, font, sh, x - o, y, Typo);
                g.DrawString(text, font, sh, x, y - o, Typo);
                g.DrawString(text, font, sh, x, y + o, Typo);
                g.DrawString(text, font, sh, x + o, y + o * 2, Typo);
            }
            g.DrawString(text, font, brush, x, y, Typo);
        }

        /// <summary>叠加层的一行:一个设备名 + 它的若干项数值(占用 / 温度 / 时钟……)。</summary>
        private sealed class Row
        {
            public string Label = "";
            public readonly List<string> Cells = new List<string>();
            public bool FpsCell;
        }

        /// <summary>
        /// 按设备分组生成行,和小飞机的 OSD 一样:CPU 只写一次,占用和温度并排放在后面,
        /// 而不是每项都带一遍"CPU"前缀。行顺序即显示顺序。
        /// </summary>
        private List<Row> BuildRows()
        {
            var rows = new List<Row>();

            if (st.ShowFps || st.ShowLow)
            {
                Row r = new Row();
                r.Label = "FPS";
                r.FpsCell = st.ShowFps;
                if (st.ShowFps) r.Cells.Add(fps > 0 ? fps.ToString("0") : "--");
                if (st.ShowLow) r.Cells.Add("1% " + (low > 0 ? low.ToString("0") : "--"));
                rows.Add(r);
            }
            if (st.ShowCpu)
            {
                Row r = new Row();
                r.Label = "CPU";
                r.Cells.Add(last.Cpu >= 0 ? last.Cpu.ToString("0") + "%" : "--");
                rows.Add(r);
            }
            if (st.ShowGpu || st.ShowGpuTemp)
            {
                Row r = new Row();
                r.Label = "GPU";
                if (st.ShowGpu) r.Cells.Add(last.Gpu >= 0 ? last.Gpu.ToString("0") + "%" : "--");
                if (st.ShowGpuTemp) r.Cells.Add(last.GpuTemp >= 0 ? last.GpuTemp.ToString("0") + "°" : "--");
                rows.Add(r);
            }
            if (st.ShowRam || st.ShowRamPct)
            {
                Row r = new Row();
                r.Label = I18n.IsEn ? "RAM" : "内存";
                if (st.ShowRam) r.Cells.Add(last.RamUsed > 0 ? ((double)last.RamUsed / 1073741824.0).ToString("0.0") + "G" : "--");
                if (st.ShowRamPct) r.Cells.Add(last.RamPct >= 0 ? last.RamPct.ToString("0") + "%" : "--");
                rows.Add(r);
            }
            if (st.ShowClock)
            {
                Row r = new Row();
                r.Label = I18n.IsEn ? "TIME" : "时间";
                r.Cells.Add(DateTime.Now.ToString("HH:mm:ss"));
                rows.Add(r);
            }
            if (st.ShowSession)
            {
                TimeSpan t = DateTime.Now - upFrom;
                if (t.TotalSeconds < 0) t = TimeSpan.Zero;
                Row r = new Row();
                r.Label = I18n.IsEn ? "UPTIME" : "时长";
                r.Cells.Add(((int)t.TotalMinutes).ToString() + ":" + t.Seconds.ToString("00"));
                rows.Add(r);
            }
            return rows;
        }

        /// <summary>简洁样式:一行"帧率 + 温度",用三空格分隔,绘制时拆成两段上色。</summary>
        private string MiniText()
        {
            string text = (fps > 0 ? fps.ToString("0") : "--") + " FPS";
            if (st.ShowGpuTemp && last.GpuTemp >= 0) text += "   " + last.GpuTemp.ToString("0") + "°";
            return text;
        }

        /// <summary>内容签名:只有文字或排布变了才重新量尺寸、重画。</summary>
        private static string RowsKey(List<Row> rows)
        {
            string s = "";
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) s += "|";
                s += rows[i].Label + ":";
                for (int c = 0; c < rows[i].Cells.Count; c++)
                {
                    if (c > 0) s += ",";
                    s += rows[i].Cells[c];
                }
            }
            return s;
        }

        private Font OverlayFont(float px, FontStyle style)
        {
            string key = px.ToString("0.0", CultureInfo.InvariantCulture) + "/" + ((int)style).ToString();
            Font f;
            if (fonts.TryGetValue(key, out f)) return f;

            // 缩放在滑杆上是 1% 一档的,每动一下就是一组新字号。缓存留够两档 DPI 的余量,
            // 拖滑杆时不会走到"清空缓存→重新建字体"的抖动里。
            if (fonts.Count > 24)
            {
                foreach (Font old in fonts.Values) { try { old.Dispose(); } catch { } }
                fonts.Clear();
            }

            string family = I18n.IsEn ? "Segoe UI" : "Microsoft YaHei UI";
            try { f = new Font(family, px, style, GraphicsUnit.Pixel); }
            catch { f = new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel); }
            fonts[key] = f;
            return f;
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
                // 内存紧张时这一帧放弃绘制,下一帧再试;不要把异常抛到定时器外面。
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
                    // 建 DIB 失败时绝不能继续往 dibBits 里写:那不是托管异常,
                    // 而是访问冲突,整个进程会被直接终止(游戏还开着的话就成了"优化器把游戏带崩")。
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
                // 失败通常只是这一帧没上屏(窗口正在重建/切全屏)。DIB 本身仍然有效,
                // 保留它下一帧直接重试,反复重建反而会一直分配 GDI 内存。
                return;
            }
        }

        /// <summary>UpdateLayeredWindow 的 AC_SRC_ALPHA 要求颜色分量已按 alpha 预乘。</summary>
        private void Premultiply(IntPtr src, IntPtr dst, int pixels)
        {
            int bytes = pixels * 4;
            if (bytes <= 0) return;
            // 缓冲按最大尺寸留一次,之后每帧复用,避免 5 次/秒的几百 KB 临时分配。
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            timer.Stop();
            ReleaseDib();
            ReleaseCanvas();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { timer.Stop(); timer.Dispose(); } catch { }
                ReleaseDib();
                ReleaseCanvas();
                foreach (Font f in fonts.Values) { try { f.Dispose(); } catch { } }
                fonts.Clear();
                premul = new byte[0];
            }
            base.Dispose(disposing);
        }
    }
}