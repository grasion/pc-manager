using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PCManagerCS
{
    /// <summary>색상·글꼴·DPI 공통 정의</summary>
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(243, 244, 246);
        public static readonly Color Surface = Color.White;
        public static readonly Color Border = Color.FromArgb(226, 229, 234);
        public static readonly Color Text = Color.FromArgb(17, 24, 39);
        public static readonly Color SubText = Color.FromArgb(107, 114, 128);
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentSoft = Color.FromArgb(235, 242, 255);
        public static readonly Color Hover = Color.FromArgb(243, 246, 250);
        public static readonly Color Success = Color.FromArgb(22, 163, 74);
        public static readonly Color Warn = Color.FromArgb(217, 119, 6);
        public static readonly Color LogBg = Color.FromArgb(30, 32, 36);
        public static readonly Color LogText = Color.FromArgb(214, 218, 224);

        public const string FontName = "Malgun Gothic";
        public static readonly Font Regular = new Font(FontName, 9f);
        public static readonly Font Small = new Font(FontName, 8.5f);
        public static readonly Font Bold = new Font(FontName, 9f, FontStyle.Bold);
        public static readonly Font Title = new Font(FontName, 11.5f, FontStyle.Bold);
        public static readonly Font Section = new Font(FontName, 10f, FontStyle.Bold);
        public static readonly Font Search = new Font(FontName, 11.5f);
        public static readonly Font Mono = new Font("Consolas", 9f);

        public static readonly string IconFontName = DetectIconFont();
        private static readonly System.Collections.Generic.Dictionary<float, Font> iconFonts =
            new System.Collections.Generic.Dictionary<float, Font>();

        /// <summary>아이콘 글꼴 (캐시됨 — Dispose 하지 말 것)</summary>
        public static Font Icon(float size)
        {
            if (IconFontName == null) return null;
            if (!iconFonts.TryGetValue(size, out var f)) iconFonts[size] = f = new Font(IconFontName, size);
            return f;
        }

        public static readonly float Dpi = GetDpi();
        public static int S(int px) => (int)Math.Round(px * Dpi / 96f);

        // Segoe MDL2 Assets 글리프
        public const string GSearch = "", GTools = "", GRepair = "", GDelete = "",
            GDoc = "", GGlobe = "", GDisk = "", GInfo = "", GBattery = "",
            GNetwork = "", GShield = "", GRefresh = "", GSettings = "",
            GPrint = "", GStartup = "", GBrowser = "", GClock = "", GSync = "";

        private static float GetDpi()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX; }
            catch { return 96f; }
        }

        private static string DetectIconFont()
        {
            using (var fonts = new InstalledFontCollection())
            {
                foreach (var f in fonts.Families)
                    if (f.Name == "Segoe Fluent Icons") return f.Name;
                foreach (var f in fonts.Families)
                    if (f.Name == "Segoe MDL2 Assets") return f.Name;
            }
            return null;
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Button FlatButton(string text, bool primary = false)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Font = Regular,
                BackColor = primary ? Accent : Surface,
                ForeColor = primary ? Color.White : Text,
                Cursor = Cursors.Hand,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(S(8), S(2), S(8), S(2)),
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = primary ? Accent : Border;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(29, 78, 216) : Hover;
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(30, 64, 175) : Border;
            return b;
        }
    }

    /// <summary>상단 탭 버튼</summary>
    internal sealed class NavTab : Control
    {
        private bool hover, selected;
        public string Glyph { get; set; }

        public bool Selected
        {
            get => selected;
            set { selected = value; Invalidate(); }
        }

        public NavTab(string text, string glyph)
        {
            Text = text;
            Glyph = glyph;
            Font = Theme.Bold;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(Theme.S(120), Theme.S(52));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Surface);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            if (hover && !selected)
                using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, 0, Theme.S(8), Width, Height - Theme.S(16));

            Color fg = selected ? Theme.Accent : hover ? Theme.Text : Theme.SubText;
            int x = Theme.S(14);
            var iconFont = Theme.Icon(11f);
            if (iconFont != null && !string.IsNullOrEmpty(Glyph))
            {
                var sz = TextRenderer.MeasureText(g, Glyph, iconFont, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Glyph, iconFont, new Rectangle(x, 0, sz.Width, Height), fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += sz.Width + Theme.S(7);
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (selected)
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, Theme.S(10), Height - Theme.S(3), Width - Theme.S(20), Theme.S(3));
        }
    }

    /// <summary>PC 관리 탭의 기능 카드</summary>
    internal sealed class ActionCard : Control
    {
        private bool hover, busy;
        public string Glyph { get; set; }
        public string Description { get; set; }
        public Color GlyphColor { get; set; } = Theme.Accent;

        public bool Busy
        {
            get => busy;
            set { busy = value; Cursor = value ? Cursors.WaitCursor : Cursors.Hand; Invalidate(); }
        }

        public ActionCard(string title, string description, string glyph)
        {
            Text = title;
            Description = description;
            Glyph = glyph;
            Font = Theme.Bold;
            Cursor = Cursors.Hand;
            Height = Theme.S(76);
            Margin = new Padding(0, 0, Theme.S(10), Theme.S(10));
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            if (busy) return;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Round(r, Theme.S(8)))
            {
                using (var b = new SolidBrush(hover && !busy ? Theme.AccentSoft : Theme.Surface)) g.FillPath(b, path);
                using (var p = new Pen(hover && !busy ? Theme.Accent : Theme.Border)) g.DrawPath(p, path);
            }

            int pad = Theme.S(14);
            int icon = Theme.S(38);
            var ir = new Rectangle(pad, (Height - icon) / 2, icon, icon);
            using (var path = Theme.Round(ir, Theme.S(8)))
            using (var b = new SolidBrush(Color.FromArgb(28, GlyphColor)))
                g.FillPath(b, path);
            var iconFont = Theme.Icon(14f);
            if (iconFont != null)
                TextRenderer.DrawText(g, Glyph, iconFont, ir, GlyphColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int tx = ir.Right + Theme.S(12);
            int tw = Width - tx - pad;
            int mid = Height / 2;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, mid - Theme.S(20), tw, Theme.S(20)), Theme.Text,
                TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, busy ? "실행 중…" : Description, Theme.Small,
                new Rectangle(tx, mid + Theme.S(2), tw, Theme.S(20)), busy ? Theme.Warn : Theme.SubText,
                TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>둥근 테두리 + 포커스 강조가 있는 검색 입력창</summary>
    internal sealed class SearchBox : Panel
    {
        public readonly TextBox Box;
        private readonly Label icon;

        public SearchBox(string placeholder)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Height = Theme.S(38);

            icon = new Label
            {
                Text = Theme.IconFontName != null ? Theme.GSearch : "",
                Font = Theme.Icon(11f) ?? Theme.Regular,
                ForeColor = Theme.SubText,
                BackColor = Theme.Surface,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter
            };
            Box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Font = Theme.Search,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text
            };
            Controls.Add(icon);
            Controls.Add(Box);
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Box.HandleCreated += (s, e) => Native.SendMessage(Box.Handle, Native.EM_SETCUEBANNER, (IntPtr)1, placeholder);
            icon.Click += (s, e) => Box.Focus();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Box == null || icon == null) return; // 생성자에서 Height 설정 시 먼저 호출됨
            int pad = Theme.S(2);
            int iw = Theme.S(34);
            icon.SetBounds(pad, pad, iw, Height - pad * 2);
            Box.SetBounds(iw + pad, (Height - Box.Height) / 2, Width - iw - Theme.S(12), Box.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Round(r, Theme.S(6)))
            {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                bool focus = Box.Focused;
                using (var p = new Pen(focus ? Theme.Accent : Theme.Border, focus ? Theme.S(2) : 1)) g.DrawPath(p, path);
            }
        }
    }
}
