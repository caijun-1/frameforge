using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
            if (!Visible) Show();
            ApplyInputMode();
            PlaceIfNeeded();
            Render();
            timer.Start();
        }

        public void Stop()
        {
            timer.Stop();
            if (Visible) Hide();
        }

        /// <summary>叠加层开关/样式/大小/锁定变化后调用。</summary>
        public void ApplySettings()
        {
            ApplyInputMode();
            PlaceIfNeeded();
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

        private void PlaceIfNeeded()
        {
            if (st.OverlayX >= 0 && st.OverlayY >= 0)
            {
                Location = new Point(st.OverlayX, st.OverlayY);
                return;
            }
            Screen sc = Screen.PrimaryScreen;
            Location = new Point(sc.WorkingArea.Right - 380, sc.WorkingArea.Top + 24);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST && !st.OverlayLocked)
            {
                m.Result = (IntPtr)Native.HTCAPTION;
                return;
            }
            if (m.Msg == 0x0232 && !st.OverlayLocked) // WM_EXITSIZEMOVE
            {
                st.OverlayX = Left;
                st.OverlayY = Top;
                st.Save();
            }
            base.WndProc(ref m);
        }

        // ---------------- 渲染 ----------------

        private void Render()
        {
            if (!IsHandleCreated || !Visible) return;
            try
            {
                double dpi = 96;
                try { uint d = Native.GetDpiForWindow(Handle); if (d >= 96) dpi = d; } catch { }
                float k = (float)(st.OverlayScale * (dpi / 96.0));
                if (k < 0.6f) k = 0.6f;
                if (k > 2.4f) k = 2.4f;

                string[] labels = BuildLabels();
                string[] values = BuildValues();
                if (labels.Length == 0) { Hide(); return; }

                string style = st.OverlayStyle;
                int pad = (int)Math.Round(12 * k);
                float labelPt = 8.2f * k;
                float valuePt = 10.5f * k;

                int w = 0, h = 0;
                int[] colW = new int[labels.Length];
                using (Bitmap probe = new Bitmap(1, 1))
                using (Graphics pg = Graphics.FromImage(probe))
                {
                    pg.TextRenderingHint = TextRenderingHint.AntiAlias;
                    for (int i = 0; i < labels.Length; i++)
                    {
                        SizeF a = pg.MeasureString(labels[i], OverlayFont(labelPt, FontStyle.Regular));
                        SizeF b = pg.MeasureString(values[i], OverlayFont(valuePt, FontStyle.Bold));
                        colW[i] = (int)Math.Ceiling(a.Width + b.Width + 10 * k);
                    }
                    if (style == "v")
                    {
                        int labelMax = 0, valueMax = 0;
                        for (int i = 0; i < labels.Length; i++)
                        {
                            int lw = (int)Math.Ceiling(pg.MeasureString(labels[i], OverlayFont(labelPt, FontStyle.Regular)).Width);
                            int vw = (int)Math.Ceiling(pg.MeasureString(values[i], OverlayFont(valuePt, FontStyle.Bold)).Width);
                            if (lw > labelMax) labelMax = lw;
                            if (vw > valueMax) valueMax = vw;
                        }
                        w = labelMax + valueMax + pad * 3;
                        h = labels.Length * (int)Math.Round(21 * k) + pad * 2;
                    }
                    else if (style == "mini")
                    {
                        w = (int)Math.Ceiling(pg.MeasureString(values[0], OverlayFont(valuePt * 1.35f, FontStyle.Bold)).Width) + pad * 2;
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

                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = TextRenderingHint.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                        float rad = 9 * k;
                        var rect = new RectangleF(0.5f, 0.5f, w - 1.5f, h - 1.5f);
                        using (GraphicsPath p = Round(rect, rad))
                        using (var b = new SolidBrush(Color.FromArgb(150, 10, 12, 16)))
                            g.FillPath(b, p);
                        using (GraphicsPath p = Round(rect, rad))
                        using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1))
                            g.DrawPath(pen, p);

                        if (style == "v")
                        {
                            int y = pad;
                            int rowH = (int)Math.Round(21 * k);
                            for (int i = 0; i < labels.Length; i++)
                            {
                                using (var b = new SolidBrush(Color.FromArgb(190, 200, 208, 220)))
                                    g.DrawString(labels[i], OverlayFont(labelPt, FontStyle.Regular), b, pad, y + 2 * k);
                                using (var b = new SolidBrush(Color.White))
                                {
                                    var fmt = new StringFormat();
                                    fmt.Alignment = StringAlignment.Far;
                                    g.DrawString(values[i], OverlayFont(valuePt, FontStyle.Bold), b,
                                        new RectangleF(pad, y, w - pad * 2, rowH), fmt);
                                }
                                y += rowH;
                            }
                        }
                        else if (style == "mini")
                        {
                            using (var b = new SolidBrush(Color.White))
                                g.DrawString(values[0], OverlayFont(valuePt * 1.35f, FontStyle.Bold), b,
                                    new RectangleF(0, 0, w, h), CenterFormat());
                        }
                        else
                        {
                            float x = pad;
                            float cy = h / 2f;
                            for (int i = 0; i < labels.Length; i++)
                            {
                                SizeF ls = g.MeasureString(labels[i], OverlayFont(labelPt, FontStyle.Regular));
                                using (var b = new SolidBrush(Color.FromArgb(180, 190, 200, 214)))
                                    g.DrawString(labels[i], OverlayFont(labelPt, FontStyle.Regular), b,
                                        x, cy - ls.Height / 2f - 1);
                                x += ls.Width + 4 * k;
                                SizeF vs = g.MeasureString(values[i], OverlayFont(valuePt, FontStyle.Bold));
                                using (var b = new SolidBrush(Color.White))
                                    g.DrawString(values[i], OverlayFont(valuePt, FontStyle.Bold), b,
                                        x, cy - vs.Height / 2f - 1);
                                x += vs.Width + 12 * k;
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

        private static StringFormat CenterFormat()
        {
            var f = new StringFormat();
            f.Alignment = StringAlignment.Center;
            f.LineAlignment = StringAlignment.Center;
            return f;
        }

        private string[] BuildLabels()
        {
            var l = new System.Collections.Generic.List<string>();
            if (st.ShowFps) l.Add("FPS");
            if (st.ShowCpu) l.Add("CPU");
            if (st.ShowGpu) l.Add("GPU");
            if (st.ShowTemp) l.Add(I18n.IsEn ? "TEMP" : "温度");
            if (st.ShowRam) l.Add(I18n.IsEn ? "RAM" : "内存");
            if (st.OverlayStyle == "mini") { l.Clear(); l.Add("FPS"); }
            return l.ToArray();
        }

        private string[] BuildValues()
        {
            var v = new System.Collections.Generic.List<string>();
            string fpsText = fps > 0 ? fps.ToString("0") : "--";
            if (st.ShowFps) v.Add(fpsText);
            if (st.ShowCpu) v.Add(last.Cpu >= 0 ? last.Cpu.ToString("0") + "%" : "--");
            if (st.ShowGpu) v.Add(last.Gpu >= 0 ? last.Gpu.ToString("0") + "%" : "--");
            if (st.ShowTemp) v.Add(last.GpuTemp >= 0 ? last.GpuTemp.ToString("0") + "°" : "--");
            if (st.ShowRam) v.Add(last.RamUsed > 0 ? ((double)last.RamUsed / 1073741824.0).ToString("0.0") + "G" : "--");
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
            string family = I18n.IsEn ? "Segoe UI" : "Microsoft YaHei UI";
            try { return new Font(family, px, style, GraphicsUnit.Pixel); }
            catch { return new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel); }
        }

        private GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
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
            if (disposing) ReleaseDib();
            base.Dispose(disposing);
        }
    }
}