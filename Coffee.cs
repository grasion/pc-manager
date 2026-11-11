using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PCManagerCS
{
    /// <summary>헤더의 둥근 버튼 (커피 후원, 업데이트 알림)</summary>
    internal class PillButton : Control
    {
        private readonly Color back, backHover, edge, fore;
        private bool hover;

        public PillButton(string text, Color back, Color backHover, Color edge, Color fore, string tooltip)
        {
            this.back = back; this.backHover = backHover; this.edge = edge; this.fore = fore;
            Font = Theme.Small;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.PushButton;
            Text = text;
            if (tooltip != null) new ToolTip().SetToolTip(this, tooltip);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            AccessibleName = Text;
            var sz = TextRenderer.MeasureText(Text, Font);
            Size = new Size(sz.Width + Theme.S(20), Theme.S(30));
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Round(r, Theme.S(8)))
            using (var b = new SolidBrush(hover && Enabled ? backHover : back))
            using (var p = new Pen(edge))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text, Font, r, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>"개발자에게 커피 사주기" 버튼</summary>
    internal sealed class CoffeeButton : PillButton
    {
        public CoffeeButton()
            : base("☕  개발자에게 커피 사주기", Color.FromArgb(0xFF, 0xF7, 0xED), Color.FromArgb(0xFF, 0xED, 0xD5),
                   Color.FromArgb(0xFE, 0xD7, 0xAA), Color.FromArgb(168, 91, 19), "개발자 후원 QR 코드 보기")
        {
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            using (var f = new CoffeeForm()) f.ShowDialog(FindForm());
        }
    }

    /// <summary>후원 QR 코드 창 (다른 프로그램과 같은 카카오페이 QR)</summary>
    internal sealed class CoffeeForm : Form
    {
        private readonly Image qr;

        public CoffeeForm()
        {
            Text = "개발자에게 커피 사주기";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = Theme.Regular;
            ClientSize = new Size(Theme.S(360), Theme.S(470));

            try
            {
                using (var s = typeof(CoffeeForm).Assembly.GetManifestResourceStream("coffee_qr.png"))
                    if (s != null) qr = new Bitmap(Image.FromStream(s));
            }
            catch { }

            int w = ClientSize.Width;
            var title = new Label
            {
                Text = "☕ 개발자에게 커피 사주기",
                Font = new Font(Theme.FontName, 14f, FontStyle.Bold),
                ForeColor = Color.FromArgb(168, 91, 19),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(0, Theme.S(20), w, Theme.S(36))
            };
            int qs = Theme.S(260);
            var box = new Panel { Bounds = new Rectangle((w - qs) / 2, Theme.S(68), qs, qs), BackColor = Color.White };
            box.Paint += (s, e) =>
            {
                if (qr == null)
                {
                    TextRenderer.DrawText(e.Graphics, "QR 코드를 불러올 수 없습니다.", Font, box.ClientRectangle, Theme.SubText,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }
                // 작은 QR 이미지를 확대해도 흐려지지 않게 (픽셀 그대로 확대)
                e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                e.Graphics.DrawImage(qr, box.ClientRectangle);
            };
            var desc = new Label
            {
                Text = "QR 코드를 스캔하여 개발자에게\r\n커피 한 잔의 응원을 보내주세요.",
                ForeColor = Color.DimGray,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(0, box.Bottom + Theme.S(12), w, Theme.S(44))
            };
            var close = Theme.FlatButton("닫기");
            close.DialogResult = DialogResult.Cancel;
            close.MinimumSize = new Size(Theme.S(90), 0);
            CancelButton = close;
            AcceptButton = close;
            Controls.AddRange(new Control[] { title, box, desc, close });
            Layout += (s, e) => close.Location = new Point((ClientSize.Width - close.Width) / 2, desc.Bottom + Theme.S(10));
            ClientSize = new Size(w, desc.Bottom + Theme.S(10) + Theme.S(34) + Theme.S(20));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) qr?.Dispose();
            base.Dispose(disposing);
        }
    }
}
