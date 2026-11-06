using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;
using FrameForge.Sys;

namespace FrameForge.UI
{
    /// <summary>
    /// 发现新版本时的提示框。新版带 exe 与 .sha256 时可以直接下载安装:
    /// 校验通过后退出,由临时脚本替换并重启;没有资产时退回打开发布页。
    /// </summary>
    public sealed class UpdateDialog : Form
    {
        private readonly string current;
        private readonly string latest;
        private readonly bool canInstall;
        private FlatButton btnInstall, btnLater;
        private string error = "";
        private bool downloading;

        /// <summary>true 表示用户点了下载安装,主窗体随后退出让脚本接管。</summary>
        public bool InstallRequested;

        public UpdateDialog(string currentVersion, string latestVersion)
        {
            current = currentVersion;
            latest = latestVersion;
            canInstall = UpdateCheck.ExeUrl.Length > 0 && UpdateCheck.ShaUrl.Length > 0;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Card;
            DoubleBuffered = true;
            KeyPreview = true;

            float s = Ui.S;
            ClientSize = new Size((int)(460 * s), (int)(242 * s));

            btnLater = new FlatButton();
            btnLater.Primary = false;
            btnLater.Text = I18n.T("upd.later");
            btnLater.FontPt = 9.5f;
            btnLater.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(btnLater);

            btnInstall = new FlatButton();
            btnInstall.Primary = true;
            btnInstall.Text = I18n.T(canInstall ? "upd.install" : "upd.open");
            btnInstall.FontPt = 9.5f;
            btnInstall.Click += delegate
            {
                if (!canInstall)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
                StartInstall();
            };
            Controls.Add(btnInstall);

            LayoutButtons();
            MouseDown += OnDrag;
            KeyDown += delegate(object o, KeyEventArgs k)
            {
                if (downloading) return;
                if (k.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                if (k.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
            };
        }

        private void StartInstall()
        {
            downloading = true;
            error = "";
            btnInstall.Busy = true;
            btnInstall.Enabled = false;
            btnLater.Enabled = false;
            btnInstall.Text = I18n.T("upd.downloading", 0);

            Thread t = new Thread(delegate()
            {
                string err = UpdateCheck.DownloadAndInstall(delegate(int pct)
                {
                    SetButtonText(I18n.T("upd.downloading", pct));
                });
                Finish(err);
            });
            t.IsBackground = true;
            t.Name = "update-install";
            t.Start();
        }

        private void SetButtonText(string text)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed) btnInstall.Text = text;
                });
            }
            catch (Exception) { }
        }

        private void Finish(string err)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    downloading = false;
                    if (err.Length == 0)
                    {
                        InstallRequested = true;
                        DialogResult = DialogResult.OK;
                        Close();
                        return;
                    }
                    error = I18n.T("upd.failed", err);
                    btnInstall.Busy = false;
                    btnInstall.Enabled = true;
                    btnLater.Enabled = true;
                    btnInstall.Text = I18n.T("upd.retry");
                    Invalidate();
                });
            }
            catch (Exception) { }
        }

        private void LayoutButtons()
        {
            int pad = Ui.Px(24);
            int bh = Ui.Px(36), bwOpen = Ui.Px(132), bwLater = Ui.Px(92);
            int bottom = Height - pad - bh;
            btnInstall.SetBounds(Width - pad - bwOpen, bottom, bwOpen, bh);
            btnLater.SetBounds(Width - pad - bwOpen - Ui.Px(10) - bwLater, bottom, bwLater, bh);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 跨屏拖动时 DPI 会变,按钮位置按新尺寸重排。
            if (btnInstall != null) LayoutButtons();
        }

        private void OnDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int pref = 2;
            if (Native.DwmSetWindowAttribute(Handle, 33, ref pref, 4) != 0)
            {
                using (GraphicsPath p = Ui.Round(new RectangleF(0, 0, Width, Height), Ui.Fp(12)))
                    Region = new Region(p);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (GraphicsPath p = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.Fp(12)))
            using (Pen pen = new Pen(Theme.Border, 1f))
                g.DrawPath(pen, p);

            int pad = Ui.Px(24);

            Rectangle titleRect = new Rectangle(pad, Ui.Px(22), Width - pad * 2, Ui.Px(26));
            TextRenderer.DrawText(g, I18n.T("upd.title"), Theme.F(11.5f, FontStyle.Bold),
                titleRect, Theme.Text, Ui.Flags(ContentAlignment.MiddleLeft, false));

            Rectangle bodyRect = new Rectangle(pad, Ui.Px(58), Width - pad * 2, Ui.Px(24));
            TextRenderer.DrawText(g, I18n.T("upd.body", current, latest), Theme.F(9.5f, FontStyle.Regular),
                bodyRect, Theme.Sub, Ui.Flags(ContentAlignment.MiddleLeft, false));

            Rectangle noteRect = new Rectangle(pad, Ui.Px(88), Width - pad * 2, Ui.Px(44));
            TextRenderer.DrawText(g, I18n.T(canInstall ? "upd.note" : "upd.note.web"), Theme.F(8.6f, FontStyle.Regular),
                noteRect, Theme.Faint, Ui.Flags(ContentAlignment.TopLeft, true));

            if (error.Length > 0)
            {
                Rectangle errRect = new Rectangle(pad, Ui.Px(134), Width - pad * 2, Ui.Px(40));
                TextRenderer.DrawText(g, error, Theme.F(8.6f, FontStyle.Regular),
                    errRect, Theme.Danger, Ui.Flags(ContentAlignment.TopLeft, true));
            }
        }
    }
}