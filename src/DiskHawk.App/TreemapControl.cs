using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DiskHawk.Core;

namespace DiskHawk.App
{
    /// <summary>
    /// Squarified treemap (WizTree style). Draws the subfolders of the selected folder nested
    /// (up to MaxDepth levels). Raises navigation events on click / double-click.
    /// </summary>
    internal sealed class TreemapControl : Control
    {
        private sealed class TmRect
        {
            public RectangleF R;
            public DirNode Node;
            public bool IsFiles;  // pseudo box for "files in this folder"
            public int Depth;
        }

        private static readonly Color[] Palette =
        {
            Color.FromArgb(66, 133, 244), Color.FromArgb(219, 68, 55), Color.FromArgb(244, 180, 0),
            Color.FromArgb(15, 157, 88), Color.FromArgb(171, 71, 188), Color.FromArgb(0, 172, 193),
            Color.FromArgb(255, 112, 67), Color.FromArgb(158, 157, 36), Color.FromArgb(92, 107, 192),
            Color.FromArgb(240, 98, 146), Color.FromArgb(0, 121, 107), Color.FromArgb(141, 110, 99)
        };

        private DirNode _root;
        private Bitmap _cache;
        private readonly List<TmRect> _rects = new List<TmRect>();
        private TmRect _hover;
        private DirNode _selected;
        private readonly ToolTip _tip = new ToolTip { InitialDelay = 250, ReshowDelay = 50, AutoPopDelay = 15000 };

        public int MaxDepth = 3;
        public event Action<DirNode> NodeClicked;
        public event Action<DirNode> NodeDoubleClicked;

        public TreemapControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Back;
            Font = Theme.SmallFont;
        }

        public DirNode Root
        {
            get { return _root; }
            set { _root = value; _hover = null; DropCache(); Invalidate(); }
        }

        public DirNode Selected
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        private void DropCache()
        {
            if (_cache != null) { _cache.Dispose(); _cache = null; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            DropCache();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { DropCache(); _tip.Dispose(); }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 0 || Height <= 0) return;
            if (_cache == null || _cache.Width != Width || _cache.Height != Height) Render();
            e.Graphics.DrawImageUnscaled(_cache, 0, 0);

            if (_selected != null)
            {
                var r = FindRect(_selected);
                if (r != null)
                    using (var p = new Pen(Color.White, Dpi.S(2f)))
                        e.Graphics.DrawRectangle(p, r.R.X + 1, r.R.Y + 1, Math.Max(1, r.R.Width - 2), Math.Max(1, r.R.Height - 2));
            }
            if (_hover != null)
                using (var p = new Pen(Color.FromArgb(200, 255, 255, 255), 1f))
                    e.Graphics.DrawRectangle(p, _hover.R.X, _hover.R.Y, Math.Max(1, _hover.R.Width - 1), Math.Max(1, _hover.R.Height - 1));
        }

        private TmRect FindRect(DirNode n)
        {
            for (int i = 0; i < _rects.Count; i++)
                if (_rects[i].Node == n && !_rects[i].IsFiles) return _rects[i];
            return null;
        }

        private void Render()
        {
            DropCache();
            _rects.Clear();
            // 32bppRgb: GDI (TextRenderer) clears the alpha channel, so text would look like holes on an ARGB bitmap
            _cache = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(_cache))
            {
                g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.None;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                if (_root == null || _root.Size <= 0)
                {
                    TextRenderer.DrawText(g, _root == null ? "" : L.T("Bu klasörde veri yok"), Theme.UiFont, ClientRectangle, Theme.Dim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }
                LayoutLevel(g, _root, new RectangleF(1, 1, Width - 2, Height - 2), 1, Color.Empty);
            }
        }

        private void LayoutLevel(Graphics g, DirNode node, RectangleF rect, int depth, Color baseColor)
        {
            if (rect.Width < 2 || rect.Height < 2) return;

            // items: subfolders + (if any) the files in this folder
            var items = new List<DirNode>();
            var sizes = new List<long>();
            int filesIndex = -1;
            if (node.Children != null)
                foreach (var c in node.Children)
                    if (c.Size > 0) { items.Add(c); sizes.Add(c.Size); }
            if (node.OwnSize > 0)
            {
                // insert in the right place by size
                int pos = 0;
                while (pos < sizes.Count && sizes[pos] >= node.OwnSize) pos++;
                items.Insert(pos, node);
                sizes.Insert(pos, node.OwnSize);
                filesIndex = pos;
            }
            if (items.Count == 0) return;

            var rects = Squarify(sizes, rect);
            float header = Theme.SmallFont.Height + Dpi.S(2f);

            for (int i = 0; i < items.Count; i++)
            {
                var r = rects[i];
                if (r.Width < 1 || r.Height < 1) continue;
                bool isFiles = i == filesIndex;
                var n = items[i];

                Color c;
                if (depth == 1) c = isFiles ? Color.FromArgb(120, 120, 120) : Palette[i % Palette.Length];
                else c = isFiles ? Mix(baseColor, Color.Gray, 0.55) : Shade(baseColor, i % 2 == 0 ? 0.0 : -0.12);

                bool nest = !isFiles && depth < MaxDepth && n.HasChildren && r.Width > Dpi.S(36) && r.Height > Dpi.S(36);

                if (nest)
                {
                    var tr = new TmRect { R = r, Node = n, Depth = depth };
                    _rects.Add(tr);
                    using (var b = new SolidBrush(Shade(c, -0.45))) g.FillRectangle(b, r);
                    bool showHeader = r.Height > header * 2.2f && r.Width > Dpi.S(40);
                    if (showHeader)
                        DrawLabel(g, n.Name + "  " + Fmt.Size(n.Size), new RectangleF(r.X + 2, r.Y + 1, r.Width - 4, header), Color.White);
                    float top = showHeader ? header : Dpi.S(2f);
                    var inner = new RectangleF(r.X + Dpi.S(2f), r.Y + top, r.Width - Dpi.S(4f), r.Height - top - Dpi.S(2f));
                    LayoutLevel(g, n, inner, depth + 1, c);
                    using (var p = new Pen(Theme.Back)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                }
                else
                {
                    _rects.Add(new TmRect { R = r, Node = n, IsFiles = isFiles, Depth = depth });
                    FillCushion(g, r, c);
                    if (r.Width > Dpi.S(46) && r.Height > header + 2)
                    {
                        string label = isFiles ? L.T("[dosyalar]") : n.Name;
                        DrawLabel(g, label, new RectangleF(r.X + 2, r.Y + 1, r.Width - 4, header), Color.White);
                        if (r.Height > header * 2 + 2)
                            DrawLabel(g, Fmt.Size(isFiles ? n.OwnSize : n.Size), new RectangleF(r.X + 2, r.Y + header, r.Width - 4, header), Color.FromArgb(220, 255, 255, 255));
                    }
                }
            }
        }

        private void DrawLabel(Graphics g, string text, RectangleF r, Color color)
        {
            TextRenderer.DrawText(g, text, Font, Rectangle.Round(r), color,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        private static void FillCushion(Graphics g, RectangleF r, Color c)
        {
            if (r.Width >= 4 && r.Height >= 4)
            {
                using (var b = new LinearGradientBrush(new RectangleF(r.X, r.Y, r.Width, r.Height), Shade(c, 0.18), Shade(c, -0.28), 45f))
                    g.FillRectangle(b, r);
            }
            else
            {
                using (var b = new SolidBrush(c)) g.FillRectangle(b, r);
            }
            using (var p = new Pen(Color.FromArgb(90, 0, 0, 0))) g.DrawRectangle(p, r.X, r.Y, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1));
        }

        private static Color Shade(Color c, double f)
        {
            if (f >= 0) return Mix(c, Color.White, f);
            return Mix(c, Color.Black, -f);
        }

        private static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>Bruls, Huizing, van Wijk - squarified treemap. sizes must be in descending order.</summary>
        internal static RectangleF[] Squarify(IList<long> sizes, RectangleF rect)
        {
            int n = sizes.Count;
            var res = new RectangleF[n];
            double total = 0;
            for (int k = 0; k < n; k++) total += sizes[k];
            if (total <= 0 || rect.Width <= 0 || rect.Height <= 0) return res;

            double scale = (double)rect.Width * rect.Height / total;
            var areas = new double[n];
            for (int k = 0; k < n; k++) areas[k] = sizes[k] * scale;

            double x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
            int i = 0;
            while (i < n)
            {
                if (w <= 0.5 || h <= 0.5) break;
                double side = Math.Min(w, h);
                int end = i;
                double sum = 0, worst = double.MaxValue;
                while (end < n)
                {
                    double ns = sum + areas[end];
                    double wr = Worst(areas[i], areas[end], ns, side);
                    if (end > i && wr > worst) break;
                    worst = wr;
                    sum = ns;
                    end++;
                }

                if (w >= h)
                {
                    double cw = sum / h, yy = y;
                    for (int k = i; k < end; k++)
                    {
                        double kh = areas[k] / cw;
                        res[k] = new RectangleF((float)x, (float)yy, (float)cw, (float)kh);
                        yy += kh;
                    }
                    x += cw; w -= cw;
                }
                else
                {
                    double rh = sum / w, xx = x;
                    for (int k = i; k < end; k++)
                    {
                        double kw = areas[k] / rh;
                        res[k] = new RectangleF((float)xx, (float)y, (float)kw, (float)rh);
                        xx += kw;
                    }
                    y += rh; h -= rh;
                }
                i = end;
            }
            return res;
        }

        private static double Worst(double max, double min, double sum, double side)
        {
            if (min <= 0) return double.MaxValue;
            double s2 = side * side, sum2 = sum * sum;
            return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
        }

        // ------------------------------------------------------------------ mouse

        private TmRect HitTest(Point p)
        {
            for (int i = _rects.Count - 1; i >= 0; i--)
                if (_rects[i].R.Contains(p)) return _rects[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = HitTest(e.Location);
            if (h == _hover) return;
            _hover = h;
            if (h != null)
            {
                string path = h.Node.FullPath;
                string text = h.IsFiles
                    ? path + L.T("\n[bu klasördeki dosyalar]\n") + Fmt.Size(h.Node.OwnSize) + " - " + Fmt.Count(h.Node.OwnFiles) + L.T(" dosya")
                    : path + "\n" + Fmt.Size(h.Node.Size) + " - " + Fmt.Count(h.Node.Files) + L.T(" dosya, ") + Fmt.Count(h.Node.Dirs) + L.T(" klasör");
                _tip.SetToolTip(this, text);
            }
            else _tip.SetToolTip(this, null);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = null;
            _tip.SetToolTip(this, null);
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            var h = HitTest(e.Location);
            if (h != null && NodeClicked != null) NodeClicked(h.Node);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            var h = HitTest(e.Location);
            if (h != null && !h.IsFiles && NodeDoubleClicked != null) NodeDoubleClicked(h.Node);
        }
    }
}
