using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using DiskHawk.Core;

namespace DiskHawk.App
{
    /// <summary>Brand: name, tagline, logo drawing.</summary>
    internal static class Brand
    {
        public const string Name = "DiskHawk";
        public const string Website = "github.com/Mtangall/DiskHawk";   // project home page (shown in About)
        public static string Tagline { get { return L.T("Filo disk analizi"); } }

        public static readonly Color Blue1 = Color.FromArgb(30, 144, 255);
        public static readonly Color Blue2 = Color.FromArgb(8, 62, 140);

        /// <summary>Logo: rounded square (gradient) + disk ring + hawk eye. Same geometry as the icon file.</summary>
        public static void DrawLogo(Graphics g, RectangleF r)
        {
            var oldSmooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = r.Width / 256f;
            using (var path = RoundRect(r, 56 * s))
            using (var br = new LinearGradientBrush(r, Blue1, Blue2, 45f))
                g.FillPath(br, path);

            float cx = r.X + 128 * s, cy = r.Y + 128 * s;
            float ring = 86 * s;
            using (var pen = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(1f, 14 * s)))
                g.DrawEllipse(pen, cx - ring, cy - ring, ring * 2, ring * 2);

            // almond eye
            var pts = new PointF[82];
            float L0 = r.X + 62 * s, R0 = r.X + 194 * s, H = 40 * s;
            for (int i = 0; i <= 40; i++)
            {
                double t = i / 40.0;
                pts[i] = new PointF((float)(L0 + (R0 - L0) * t), (float)(cy - H * Math.Pow(Math.Sin(Math.PI * t), 0.9)));
                pts[81 - i] = new PointF((float)(L0 + (R0 - L0) * t), (float)(cy + H * Math.Pow(Math.Sin(Math.PI * t), 0.9)));
            }
            g.FillPolygon(Brushes.White, pts);

            float pr = 22 * s;
            using (var b = new SolidBrush(Blue2)) g.FillEllipse(b, cx - pr, cy - pr, pr * 2, pr * 2);
            float hr = 7 * s;
            g.FillEllipse(Brushes.White, cx + 6 * s - hr, cy - 8 * s - hr, hr * 2, hr * 2);
            g.SmoothingMode = oldSmooth;
        }

        private static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Segoe MDL2 / Fluent icon glyphs.</summary>
    internal static class Glyph
    {
        public const string Add = "\uE710";
        public const string Import = "\uE8E5";
        public const string People = "\uE716";
        public const string Play = "\uE768";
        public const string Refresh = "\uE72C";
        public const string Stop = "\uE71A";
        public const string Computer = "\uE770";
        public const string Document = "\uE8A5";
        public const string Save = "\uE74E";
        public const string Settings = "\uE713";
        public const string Info = "\uE946";
        public const string Search = "\uE721";
        public const string Delete = "\uE74D";
        public const string Folder = "\uE8B7";
        public const string Up = "\uE74A";
        public const string Home = "\uE80F";
        public const string History = "\uE81C";
        public const string Filter = "\uE71C";

        private static Font _font;
        private static bool _probed;

        /// <summary>Windows 11: Segoe Fluent Icons, Windows 10: Segoe MDL2 Assets. null if missing (no icons drawn).</summary>
        public static Font Font
        {
            get
            {
                if (_probed) return _font;
                _probed = true;
                try
                {
                    using (var fonts = new InstalledFontCollection())
                    {
                        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                            foreach (var f in fonts.Families)
                                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                                    return _font = new Font(name, 10.5F);
                    }
                }
                catch { }
                return null;
            }
        }
    }

    /// <summary>Flat button with icon + text.</summary>
    internal sealed class IconButton : Button
    {
        private readonly string _glyph;
        private readonly bool _primary;
        private bool _hover, _down;

        public IconButton(string glyph, string text, EventHandler click, bool primary = false, string tooltip = null)
        {
            _glyph = Glyph.Font != null ? glyph : null;
            _primary = primary;
            // without an icon font (older Windows) icon-only buttons show their tooltip text
            if (Glyph.Font == null && string.IsNullOrEmpty(text)) text = tooltip ?? "?";
            Text = text ?? "";
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            Font = Theme.UiFont;
            ForeColor = Theme.Text;
            BackColor = primary ? Theme.Accent : Theme.Surface;
            Margin = new Padding(Dpi.S(3), Dpi.S(4), Dpi.S(3), Dpi.S(4));
            Height = Dpi.S(30);
            UpdateWidth();
            if (click != null) Click += click;
            if (!string.IsNullOrEmpty(tooltip) || Text.Length == 0)
                new ToolTip().SetToolTip(this, tooltip ?? Text);
        }

        private void UpdateWidth()
        {
            int tw = Text.Length > 0 ? TextRenderer.MeasureText(Text, Font).Width : 0;
            int gw = _glyph != null ? Dpi.S(20) : 0;
            Width = Text.Length == 0 ? Dpi.S(34) : Dpi.S(12) + gw + (gw > 0 ? Dpi.S(4) : 0) + tw + Dpi.S(8);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (Dock == DockStyle.None) UpdateWidth();
            Invalidate();
        }

        private static Color Shade(Color c, double f)
        {
            Color t = f >= 0 ? Color.White : Color.Black;
            f = Math.Abs(f);
            return Color.FromArgb((int)(c.R + (t.R - c.R) * f), (int)(c.G + (t.G - c.G) * f), (int)(c.B + (t.B - c.B) * f));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color bg;
            if (!Enabled) bg = Theme.Panel;
            else if (_primary) bg = _down ? Shade(BackColor, -0.15) : _hover ? Shade(BackColor, 0.15) : BackColor;
            else bg = _down ? Theme.Border : _hover ? Theme.SurfaceHover : BackColor;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Back)) g.FillRectangle(b, ClientRectangle);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, 0, Width, Height);
            if (!_primary)
                using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            var fg = Enabled ? (_primary ? Color.White : ForeColor) : Theme.Dim;
            int x = Text.Length == 0 ? 0 : Dpi.S(10);
            const TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            if (_glyph != null)
            {
                int gw = Text.Length == 0 ? Width : Dpi.S(20);
                TextRenderer.DrawText(g, _glyph, Glyph.Font, new Rectangle(x, 0, gw, Height), _primary ? Color.White : (Enabled ? Theme.AccentHover : Theme.Dim), f | TextFormatFlags.HorizontalCenter);
                x += gw + Dpi.S(4);
            }
            if (Text.Length > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x - Dpi.S(4), Height), fg, f | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
                using (var p = new Pen(Theme.AccentHover)) g.DrawRectangle(p, 1, 1, Width - 3, Height - 3);
        }
    }

    /// <summary>Summary card (title + large value). Clickable; the selected card is highlighted.</summary>
    internal sealed class StatCard : Control
    {
        private string _value = "0";
        private bool _selected, _hover;
        public readonly Color Accent;
        public string Title;
        public static readonly Font ValueFont = new Font("Segoe UI Semibold", 17F);

        public StatCard(string title, Color accent)
        {
            Title = title;
            Accent = accent;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(Dpi.S(176), Dpi.S(66));
            Margin = new Padding(Dpi.S(4), Dpi.S(6), Dpi.S(4), Dpi.S(6));
            Cursor = Cursors.Hand;
            BackColor = Theme.Panel;
        }

        public string Value
        {
            get { return _value; }
            set { if (_value != value) { _value = value; Invalidate(); } }
        }

        public bool Selected
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(_hover ? Theme.Surface : Theme.Panel)) g.FillRectangle(b, ClientRectangle);
            using (var b = new SolidBrush(Accent)) g.FillRectangle(b, 0, 0, Dpi.S(4), Height);
            if (_selected)
                using (var p = new Pen(Accent, Dpi.S(2f))) g.DrawRectangle(p, 1, 1, Width - 2, Height - 2);
            else
                using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(g, Title, Theme.SmallFont, new Rectangle(Dpi.S(14), Dpi.S(8), Width - Dpi.S(18), Dpi.S(16)), Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, _value, ValueFont, new Rectangle(Dpi.S(12), Dpi.S(24), Width - Dpi.S(16), Height - Dpi.S(26)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Application header: logo + product name + tagline, icon buttons on the right.</summary>
    internal sealed class AppHeader : Panel
    {
        public static readonly Color HeaderBack = Color.FromArgb(22, 22, 24);
        private static readonly Font BrandFont = new Font("Segoe UI Semibold", 14F);
        public readonly FlowLayoutPanel Actions;
        private readonly string _subtitle;

        public AppHeader(string subtitle)
        {
            _subtitle = subtitle;
            Dock = DockStyle.Top;
            Height = Dpi.S(52);
            BackColor = HeaderBack;
            DoubleBuffered = true;
            Actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = HeaderBack,
                Padding = new Padding(0, Dpi.S(7), Dpi.S(8), 0)
            };
            Controls.Add(Actions);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            int logo = Dpi.S(32);
            Brand.DrawLogo(g, new RectangleF(Dpi.S(14), (Height - logo) / 2f, logo, logo));
            int x = Dpi.S(14) + logo + Dpi.S(10);
            var nameSize = TextRenderer.MeasureText(g, Brand.Name, BrandFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Brand.Name, BrandFont, new Point(x, (Height - nameSize.Height) / 2), Theme.Text, TextFormatFlags.NoPadding);
            x += nameSize.Width + Dpi.S(12);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, x, Dpi.S(16), x, Height - Dpi.S(16));
            x += Dpi.S(12);
            var subSize = TextRenderer.MeasureText(g, _subtitle, Theme.UiFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _subtitle, Theme.UiFont, new Point(x, (Height - subSize.Height) / 2), Theme.Dim, TextFormatFlags.NoPadding);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>Small control that draws the logo (About dialog).</summary>
    internal sealed class LogoBox : Control
    {
        public LogoBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Back)) e.Graphics.FillRectangle(b, ClientRectangle);
            Brand.DrawLogo(e.Graphics, new RectangleF(0, 0, Math.Min(Width, Height), Math.Min(Width, Height)));
        }
    }
}
