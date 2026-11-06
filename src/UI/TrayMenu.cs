using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FrameForge.UI
{
    /// <summary>
    /// 托盘右键菜单:只放最常用的两三条动作,文字统一从左侧内缩起排,不加分隔线。
    /// </summary>
    public sealed class TrayMenu : ContextMenuStrip
    {
        /// <summary>左侧内缩:菜单没有图标列,文字直接从留白处开始。</summary>
        private const int Gutter = 16;
        /// <summary>右侧内缩。</summary>
        private const int Inset = 14;
        /// <summary>行内上下留白,上下对称,文字自然落在行中线上。</summary>
        private const int RowPadY = 6;

        public TrayMenu()
        {
            Renderer = new Skin();
            ShowImageMargin = false;
            ShowCheckMargin = false;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            Font = Theme.Regular(9.5f);
            DropShadowEnabled = true;
            // 宽度和高度都由 FitSize 一次算好:交给系统自动撑开时,
            // 条目宽度会按自己的 Padding 算得比菜单还宽,文字就会被切在右边界外。
            AutoSize = false;
            Opened += delegate { ApplyCorners(); };
        }

        /// <summary>加一条普通菜单项。</summary>
        public ToolStripMenuItem Add(string text, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text);
            Style(item);
            if (onClick != null) item.Click += onClick;
            Items.Add(item);
            return item;
        }

        /// <summary>行内边距和外边距统一:外侧归零,左右留白完全交给 Gutter 和文字矩形控制。</summary>
        private static void Style(ToolStripItem item)
        {
            item.Padding = new Padding(0, Ui.Px(RowPadY), 0, Ui.Px(RowPadY));
            item.Margin = Padding.Empty;
        }

        /// <summary>
        /// 按最长的文字算一次宽高,并把每条都设成同样宽:
        /// 条目宽度交给系统算时,文字会贴着菜单左边缘,右边留一大片空白;
        /// 固定成同宽后,勾选号列和文字列每次都在同一个位置。
        /// </summary>
        private void FitSize()
        {
            int textWidth = 0;
            foreach (ToolStripItem item in Items)
            {
                Size t = TextRenderer.MeasureText(item.Text, item.Font);
                if (t.Width > textWidth) textWidth = t.Width;
            }

            int width = textWidth + Ui.Px(Gutter + Inset);
            int min = Ui.Px(120);
            if (width < min) width = min;

            int height = Padding.Top + Padding.Bottom;
            foreach (ToolStripItem item in Items)
            {
                int row = TextRenderer.MeasureText(item.Text, item.Font).Height + item.Padding.Vertical;
                // 关掉自动尺寸,系统就不会把条目按自己的内容宽度重新摆一遍。
                item.AutoSize = false;
                item.Size = new Size(width, row);
                height += row;
            }

            Size = new Size(width, height);
        }

        protected override void OnOpening(CancelEventArgs e)
        {
            FitSize();
            base.OnOpening(e);
        }

        /// <summary>Win11 用系统圆角(自带正确阴影),旧系统退回到自绘圆角区域。</summary>
        private void ApplyCorners()
        {
            int pref = 2;   // DWMWCP_ROUND
            if (Native.DwmSetWindowAttribute(Handle, 33, ref pref, 4) != 0)
            {
                using (GraphicsPath p = Ui.Round(new RectangleF(0, 0, Width, Height), Ui.Fp(8)))
                    Region = new Region(p);
            }
        }

        private sealed class Skin : ToolStripProfessionalRenderer
        {
            public Skin() : base(new Palette()) { RoundedEdges = false; }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var b = new SolidBrush(Theme.Card))
                    e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // 圆角半径和 Win11 的系统窗口圆角一致,免得自绘边框比系统裁切还大一圈。
                using (GraphicsPath p = Ui.Round(
                           new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1f, e.ToolStrip.Height - 1f), Ui.Fp(8)))
                using (var pen = new Pen(Theme.Border, 1f))
                    g.DrawPath(pen, p);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (e.Item.Selected && e.Item.Enabled)
                {
                    Graphics g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    // 悬停反馈用一层很淡的底色就够,不用彩色大色块。
                    var r = new Rectangle(Ui.Px(4), Ui.Px(1),
                        e.Item.Width - Ui.Px(8), e.Item.Height - Ui.Px(2));
                    using (GraphicsPath p = Ui.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), Ui.Fp(6)))
                    using (var b = new SolidBrush(Theme.Hover))
                        g.FillPath(b, p);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                // 菜单项的 Tag 可以放一个颜色(退出项用危险色),放过就优先用。
                if (e.Item.Tag is Color) e.TextColor = (Color)e.Item.Tag;
                else if (!e.Item.Enabled) e.TextColor = Theme.Faint;
                else e.TextColor = Theme.Text;

                // 所有文字从同一列起排,右侧留一点呼吸。
                e.TextRectangle = new Rectangle(Ui.Px(Gutter), 0,
                    e.Item.Width - Ui.Px(Gutter + Inset), e.Item.Height);
                e.TextFormat = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
                base.OnRenderItemText(e);
            }
        }

        /// <summary>把系统菜单默认的白色/蓝色替换成主题色,避免自绘漏掉的地方露馅。</summary>
        private sealed class Palette : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color MenuItemSelected { get { return Color.Transparent; } }
            public override Color MenuItemSelectedGradientBegin { get { return Color.Transparent; } }
            public override Color MenuItemSelectedGradientEnd { get { return Color.Transparent; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Card; } }
        }
    }
}