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

        private IntPtr memDc = IntPtr.Zero;
        private IntPtr dib = IntPtr.Zero;
        private IntPtr dibBits = IntPtr.Zero;
        private IntPtr oldObj = IntPtr.Zero;
        private int bmpW, bmpH;

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
        /// 默认摆位:横向居中、竖向靠左(纵向居中)、简洁居中。
        /// 用户拖动过(OverlayX/Y 已保存)就沿用他的位置;切换样式时设置页会清掉坐标,
        /// 重新按新样式摆放。
        /// </summary>
        private void PlaceIfNeeded(int w, int h, float k)
        {
            Rectangle wa = TargetBounds();
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

                string[] labels = BuildLabels();
                string[] values = BuildValues();
                // 一个显示项都没勾时窗口收起,但这只是"暂时没内容",开关并没有关。
                // 这里不 Show,否则会和下面的 Hide 每 200ms 来回切,画面一直闪。
                if (labels.Length == 0) { lastKey = ""; if (Visible) Hide(); return; }

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

                string style = st.OverlayStyle;
                string key = style + "|" + st.OverlayScale.ToString("0.00", CultureInfo.InvariantCulture)
                           + "|" + k.ToString("0.00", CultureInfo.InvariantCulture)
                           + "|" + string.Join(",", labels) + "|" + string.Join(",", values);
                if (key == lastKey)
                {
                    // 位置可能被游戏顶掉,内容没变也要重新声明置顶。
                    if (!moving) KeepTopmost();
                    return;
                }
                lastKey = key;
                int pad = (int)Math.Round(12 * k);
                float labelPt = 9.2f * k;
                float valuePt = 12f * k;

                // 横向样式:每块占的宽度必须和绘制时的推进量一字不差地对应,
                // 否则误差会随列数累加,最后一两列被挤出画布,看起来就是"后面的字显示不全"。
                float gap1 = 4 * k;    // 标签与数值之间
                float gap2 = 12 * k;   // 两组数据之间

                int w = 0, h = 0;
                int[] colW = new int[labels.Length];
                using (Bitmap probe = new Bitmap(1, 1))
                using (Graphics pg = Graphics.FromImage(probe))
                {
                    pg.TextRenderingHint = TextRenderingHint.AntiAlias;
                    for (int i = 0; i < labels.Length; i++)
                    {
                        SizeF a = pg.MeasureString(labels[i], OverlayFont(labelPt, FontStyle.Regular), int.MaxValue, Typo);
                        SizeF b = pg.MeasureString(values[i], OverlayFont(valuePt, FontStyle.Bold), int.MaxValue, Typo);
                        colW[i] = (int)Math.Ceiling(a.Width + gap1 + b.Width + gap2);
                    }
                    if (style == "v")
                    {
                        int labelMax = 0, valueMax = 0;
                        for (int i = 0; i < labels.Length; i++)
                        {
                            int lw = (int)Math.Ceiling(pg.MeasureString(labels[i], OverlayFont(labelPt, FontStyle.Regular), int.MaxValue, Typo).Width);
                            int vw = (int)Math.Ceiling(pg.MeasureString(values[i], OverlayFont(valuePt, FontStyle.Bold), int.MaxValue, Typo).Width);
                            if (lw > labelMax) labelMax = lw;
                            if (vw > valueMax) valueMax = vw;
                        }
                        w = labelMax + valueMax + pad * 3;
                        h = labels.Length * (int)Math.Round(21 * k) + pad * 2;
                    }
                    else if (style == "mini")
                    {
                        w = (int)Math.Ceiling(pg.MeasureString(values[0], OverlayFont(valuePt * 1.35f, FontStyle.Bold), int.MaxValue, Typo).Width) + pad * 2;
                        h = (int)Math.Round(34 * k) + pad;
                    }
                    else
                    {
                        for (int i = 0; i < labels.Length; i++) w += colW[i];
                        w += pad * 2;
                        h = (int)Math.Round(34 * k) + pad;
                    }
                }
                if (w < 40) w = 40;
                if (h < 24) h = 24;

                // 尺寸算出来后再摆放:居中要按实际宽高算。
                // 用户正在拖动时不能改 Location,否则每 200ms 会被拉回原位置。
                if (!moving) PlaceIfNeeded(w, h, k);
                KeepTopmost();

                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = TextRenderingHint.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                        // 整块只留文字:不要圆角底,也不要描边(用户明确要求)。
                        // 没有底色后白字在雪地/亮天空里会糊,所以先压一层深色阴影再画字,
                        // 相当于数字自带描边,背景仍然完全透明。
                        if (style == "v")
                        {
                            int y = pad;
                            int rowH = (int)Math.Round(21 * k);
                            Font lf = OverlayFont(labelPt, FontStyle.Regular);
                            Font vf = OverlayFont(valuePt, FontStyle.Bold);
                            for (int i = 0; i < labels.Length; i++)
                            {
                                using (var b = new SolidBrush(Color.FromArgb(225, 206, 214, 226)))
                                    ShadowString(g, labels[i], lf, b, pad, y + 2 * k, k);
                                float vw = g.MeasureString(values[i], vf, int.MaxValue, Typo).Width;
                                using (var b = new SolidBrush(Color.White))
                                    ShadowString(g, values[i], vf, b, w - pad - vw, y + 1 * k, k);
                                y += rowH;
                            }
                        }
                        else if (style == "mini")
                        {
                            Font f = OverlayFont(valuePt * 1.35f, FontStyle.Bold);
                            SizeF vs = g.MeasureString(values[0], f, int.MaxValue, Typo);
                            using (var b = new SolidBrush(Color.White))
                                ShadowString(g, values[0], f, b, (w - vs.Width) / 2f, (h - vs.Height) / 2f, k);
                        }
                        else
                        {
                            float x = pad;
                            float cy = h / 2f;
                            Font lf = OverlayFont(labelPt, FontStyle.Regular);
                            Font vf = OverlayFont(valuePt, FontStyle.Bold);
                            for (int i = 0; i < labels.Length; i++)
                            {
                                SizeF ls = g.MeasureString(labels[i], lf, int.MaxValue, Typo);
                                using (var b = new SolidBrush(Color.FromArgb(220, 196, 206, 220)))
                                    ShadowString(g, labels[i], lf, b, x, cy - ls.Height / 2f, k);
                                x += ls.Width + gap1;
                                SizeF vs = g.MeasureString(values[i], vf, int.MaxValue, Typo);
                                using (var b = new SolidBrush(Color.White))
                                    ShadowString(g, values[i], vf, b, x, cy - vs.Height / 2f, k);
                                x += vs.Width + gap2;
                            }
                        }
                    }

                    Blit(bmp, w, h);
                }
            }
            catch (Exception ex)
            {
                Log.Write("overlay render: " + ex.Message);
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

        private string[] BuildLabels()
        {
            var l = new System.Collections.Generic.List<string>();
            if (st.ShowFps) l.Add("FPS");
            if (st.ShowLow) l.Add("1% LOW");
            if (st.ShowCpu) l.Add("CPU");
            if (st.ShowGpu) l.Add("GPU");
            if (st.ShowTemp) l.Add(I18n.IsEn ? "TEMP" : "温度");
            if (st.ShowRam) l.Add(I18n.IsEn ? "RAM" : "内存");
            if (st.ShowClock) l.Add(I18n.IsEn ? "TIME" : "时间");
            if (st.ShowSession) l.Add(I18n.IsEn ? "UPTIME" : "时长");
            if (st.OverlayStyle == "mini") { l.Clear(); l.Add("FPS"); }
            return l.ToArray();
        }

        private string[] BuildValues()
        {
            var v = new System.Collections.Generic.List<string>();
            string fpsText = fps > 0 ? fps.ToString("0") : "--";
            if (st.ShowFps) v.Add(fpsText);
            if (st.ShowLow) v.Add(low > 0 ? low.ToString("0") : "--");
            if (st.ShowCpu) v.Add(last.Cpu >= 0 ? last.Cpu.ToString("0") + "%" : "--");
            if (st.ShowGpu) v.Add(last.Gpu >= 0 ? last.Gpu.ToString("0") + "%" : "--");
            if (st.ShowTemp) v.Add(last.GpuTemp >= 0 ? last.GpuTemp.ToString("0") + "°" : "--");
            if (st.ShowRam) v.Add(last.RamUsed > 0 ? ((double)last.RamUsed / 1073741824.0).ToString("0.0") + "G" : "--");
            if (st.ShowClock) v.Add(DateTime.Now.ToString("HH:mm"));
            if (st.ShowSession)
            {
                TimeSpan t = DateTime.Now - upFrom;
                if (t.TotalSeconds < 0) t = TimeSpan.Zero;
                v.Add(((int)t.TotalMinutes).ToString() + ":" + t.Seconds.ToString("00"));
            }
            if (st.OverlayStyle == "mini")
            {
                v.Clear();
                string text = fpsText + " FPS";
                if (st.ShowTemp && last.GpuTemp >= 0) text += "   " + last.GpuTemp.ToString("0") + "°";
                v.Add(text);
            }
            return v.ToArray();
        }

        private Font OverlayFont(float px, FontStyle style)
        {
            string key = px.ToString("0.0", CultureInfo.InvariantCulture) + "/" + ((int)style).ToString();
            Font f;
            if (fonts.TryGetValue(key, out f)) return f;

            if (fonts.Count > 12)
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

        /// <summary>把 GDI+ 绘制的 32bpp 位图预乘 alpha 后交给 UpdateLayeredWindow。</summary>
        private void Blit(Bitmap bmp, int w, int h)
        {
            if (memDc == IntPtr.Zero || bmpW != w || bmpH != h)
            {
                ReleaseDib();
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                memDc = Native.CreateCompatibleDC(screen);
                Native.ReleaseDC(IntPtr.Zero, screen);

                var header = new Native.BITMAPINFOHEADER();
                header.biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
                header.biWidth = w;
                header.biHeight = -h;         // top-down
                header.biPlanes = 1;
                header.biBitCount = 32;
                header.biCompression = 0;     // BI_RGB
                dib = Native.CreateDIBSection(memDc, ref header, 0, out dibBits, IntPtr.Zero, 0);
                oldObj = Native.SelectObject(memDc, dib);
                bmpW = w; bmpH = h;
            }

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
            Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }

        /// <summary>UpdateLayeredWindow 的 AC_SRC_ALPHA 要求颜色分量已按 alpha 预乘。</summary>
        private static void Premultiply(IntPtr src, IntPtr dst, int pixels)
        {
            int bytes = pixels * 4;
            var row = new byte[bytes];
            Marshal.Copy(src, row, 0, bytes);
            for (int i = 0; i < bytes; i += 4)
            {
                int a = row[i + 3];
                if (a == 255) continue;
                if (a == 0) { row[i] = 0; row[i + 1] = 0; row[i + 2] = 0; continue; }
                row[i] = (byte)(row[i] * a / 255);
                row[i + 1] = (byte)(row[i + 1] * a / 255);
                row[i + 2] = (byte)(row[i + 2] * a / 255);
            }
            Marshal.Copy(row, 0, dst, bytes);
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
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseDib();
                foreach (Font f in fonts.Values) { try { f.Dispose(); } catch { } }
                fonts.Clear();
            }
            base.Dispose(disposing);
        }
    }
}