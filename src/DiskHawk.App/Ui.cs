using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DiskHawk.Core;

namespace DiskHawk.App
{
    /// <summary>Dark theme colors and control factories.</summary>
    internal static class Theme
    {
        public static readonly Color Back = Color.FromArgb(30, 30, 30);
        public static readonly Color BackAlt = Color.FromArgb(34, 34, 35);
        public static readonly Color Panel = Color.FromArgb(37, 37, 38);
        public static readonly Color Surface = Color.FromArgb(51, 51, 55);
        public static readonly Color SurfaceHover = Color.FromArgb(66, 66, 72);
        public static readonly Color Border = Color.FromArgb(63, 63, 70);
        public static readonly Color Text = Color.FromArgb(230, 230, 230);
        public static readonly Color Dim = Color.FromArgb(150, 150, 150);
        public static readonly Color Accent = Color.FromArgb(14, 122, 216);
        public static readonly Color AccentHover = Color.FromArgb(28, 142, 240);
        public static readonly Color Select = Color.FromArgb(9, 71, 113);
        public static readonly Color SelectInactive = Color.FromArgb(55, 55, 61);
        public static readonly Color Good = Color.FromArgb(63, 185, 80);
        public static readonly Color Warn = Color.FromArgb(210, 153, 34);
        public static readonly Color Bad = Color.FromArgb(248, 81, 73);
        public static readonly Color Bar = Color.FromArgb(38, 79, 120);

        public static readonly Font UiFont = new Font("Segoe UI", 9F);
        public static readonly Font BoldFont = new Font("Segoe UI", 9F, FontStyle.Bold);
        public static readonly Font TitleFont = new Font("Segoe UI Semibold", 13F);
        public static readonly Font SmallFont = new Font("Segoe UI", 8F);
        public static readonly Font MonoFont = new Font("Consolas", 9F);

        public static Color UsageColor(double pct)
        {
            if (pct >= 90) return Bad;
            if (pct >= 80) return Warn;
            return Good;
        }

        public static Button Btn(string text, EventHandler onClick, bool primary = false)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = primary ? Accent : Surface,
                ForeColor = Text,
                Font = UiFont,
                Margin = new Padding(Dpi.S(2), Dpi.S(3), Dpi.S(2), Dpi.S(3)),
                Padding = new Padding(Dpi.S(6), Dpi.S(1), Dpi.S(6), Dpi.S(1)),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                TextImageRelation = TextImageRelation.ImageBeforeText
            };
            b.FlatAppearance.BorderColor = primary ? Accent : Border;
            b.FlatAppearance.MouseOverBackColor = primary ? AccentHover : SurfaceHover;
            b.FlatAppearance.MouseDownBackColor = primary ? Accent : Border;
            if (onClick != null) b.Click += onClick;
            return b;
        }

        public static Label Lbl(string text, Color? color = null, Font font = null)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = color ?? Text,
                Font = font ?? UiFont,
                Margin = new Padding(Dpi.S(4), Dpi.S(7), Dpi.S(2), Dpi.S(3)),
                BackColor = Color.Transparent
            };
        }

        public static TextBox TextBox(int width)
        {
            return new TextBox
            {
                Width = Dpi.S(width),
                BackColor = Surface,
                ForeColor = Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = UiFont,
                Margin = new Padding(Dpi.S(2), Dpi.S(4), Dpi.S(2), Dpi.S(3))
            };
        }

        public static NumericUpDown Num(int min, int max, int value, int width = 50)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                Width = Dpi.S(width),
                BackColor = Surface,
                ForeColor = Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = UiFont,
                Margin = new Padding(Dpi.S(2), Dpi.S(4), Dpi.S(2), Dpi.S(3))
            };
        }

        public static CheckBox Check(string text, bool value)
        {
            return new CheckBox
            {
                Text = text,
                Checked = value,
                AutoSize = true,
                ForeColor = Text,
                Font = UiFont,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(Dpi.S(6), Dpi.S(6), Dpi.S(2), Dpi.S(3))
            };
        }

        public static ComboBox Combo(int width, params object[] items)
        {
            var c = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Surface,
                ForeColor = Text,
                Font = UiFont,
                Width = Dpi.S(width),
                Margin = new Padding(Dpi.S(2), Dpi.S(4), Dpi.S(2), Dpi.S(3))
            };
            c.Items.AddRange(items);
            if (c.Items.Count > 0) c.SelectedIndex = 0;
            return c;
        }

        public static Control Separator()
        {
            return new Panel
            {
                Width = Math.Max(1, Dpi.S(1)),
                Height = Dpi.S(22),
                BackColor = Border,
                Margin = new Padding(Dpi.S(6), Dpi.S(5), Dpi.S(6), Dpi.S(3))
            };
        }

        public static FlowLayoutPanel Toolbar()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                BackColor = Panel,
                Padding = new Padding(Dpi.S(4), Dpi.S(2), Dpi.S(4), Dpi.S(2))
            };
        }

        public static ContextMenuStrip Menu()
        {
            return new ContextMenuStrip { Renderer = new DarkMenuRenderer(), BackColor = Panel, ForeColor = Text, Font = UiFont, ShowImageMargin = false };
        }

        public static ToolStripMenuItem Item(this ContextMenuStrip m, string text, EventHandler click, Keys keys = Keys.None)
        {
            var it = new ToolStripMenuItem(text, null, click) { ForeColor = Text };
            if (keys != Keys.None) it.ShortcutKeys = keys;
            m.Items.Add(it);
            return it;
        }

        // --- Windows 10/11 dark title bar and scroll bars
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);

        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
            }
            catch { /* older Windows */ }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>Grey cue banner text for a TextBox (EM_SETCUEBANNER).</summary>
        public static void SetCue(TextBox box, string cue)
        {
            EventHandler apply = (s, e) => { try { SendMessage(box.Handle, 0x1501, new IntPtr(1), cue); } catch { } };
            if (box.IsHandleCreated) apply(box, EventArgs.Empty);
            else box.HandleCreated += apply;
        }

        public static void DarkScrollbars(Control c)
        {
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }
    }

    internal static class Dpi
    {
        /// <summary>Fits the requested size into the working area.</summary>
        public static Size Fit(int w, int h)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            return new Size(Math.Min(S(w), wa.Width), Math.Min(S(h), wa.Height));
        }

        private static float _scale = -1;

        public static float Scale
        {
            get
            {
                if (_scale < 0)
                {
                    try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) _scale = g.DpiX / 96f; }
                    catch { _scale = 1f; }
                }
                return _scale;
            }
        }

        public static int S(int px) { return (int)Math.Round(px * Scale); }
        public static float S(float px) { return px * Scale; }
    }

    /// <summary>Dark theme base form.</summary>
    internal class DarkForm : Form
    {
        public DarkForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.UiFont;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Dim;
            base.OnRenderItemText(e);
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemBorder { get { return Theme.Select; } }
            public override Color MenuItemSelected { get { return Theme.Select; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Select; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Select; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Border; } }
        }
    }

    /// <summary>
    /// Owner-drawn, dark themed ListView suitable for virtual mode.
    /// Supports bar columns and per-cell colors.
    /// </summary>
    internal sealed class DarkListView : ListView
    {
        public delegate bool BarProvider(int itemIndex, int column, out double value, out Color color);
        public delegate Color? ColorProvider(int itemIndex, int column);

        public BarProvider Bars;
        public ColorProvider Colors;
        public int SortColumn = -1;
        public bool SortDescending = true;

        public DarkListView()
        {
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            OwnerDraw = true;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.UiFont;
            HeaderStyle = ColumnHeaderStyle.Clickable;
            DoubleBuffered = true;
            SmallImageList = new ImageList { ImageSize = new Size(1, Dpi.S(22)) };
        }

        public ColumnHeader AddColumn(string text, int width, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var c = Columns.Add(text, Dpi.S(width), align);
            return c;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkScrollbars(this);
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Theme.Border))
            {
                g.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
                g.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            }
            var r = Rectangle.Inflate(e.Bounds, -Dpi.S(6), 0);
            string text = e.Header.Text;
            if (e.ColumnIndex == SortColumn) text = (e.Header.TextAlign == HorizontalAlignment.Right ? (SortDescending ? "\u25BE " : "\u25B4 ") + text : text + (SortDescending ? " \u25BE" : " \u25B4"));
            TextRenderer.DrawText(g, text, Theme.BoldFont, r, Theme.Dim, Flags(e.Header.TextAlign));
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            e.DrawDefault = false;
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            var g = e.Graphics;
            bool sel = IsItemSelected(e.ItemIndex);
            Color bg = sel ? (Focused ? Theme.Select : Theme.SelectInactive) : (e.ItemIndex % 2 == 0 ? Theme.Back : Theme.BackAlt);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);

            double v;
            Color barColor;
            if (Bars != null && Bars(e.ItemIndex, e.ColumnIndex, out v, out barColor))
            {
                v = Math.Max(0, Math.Min(1, v));
                var br = Rectangle.Inflate(e.Bounds, -Dpi.S(4), -Dpi.S(4));
                using (var b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, br);
                var fill = new Rectangle(br.X, br.Y, (int)Math.Round(br.Width * v), br.Height);
                if (fill.Width > 0) using (var b = new SolidBrush(barColor)) g.FillRectangle(b, fill);
            }

            Color fg = Theme.Text;
            if (Colors != null)
            {
                var c = Colors(e.ItemIndex, e.ColumnIndex);
                if (c.HasValue) fg = c.Value;
            }
            var tr = Rectangle.Inflate(e.Bounds, -Dpi.S(6), 0);
            var align = Columns[e.ColumnIndex].TextAlign;
            TextRenderer.DrawText(g, e.SubItem != null ? e.SubItem.Text : "", Font, tr, fg, Flags(align));
        }

        private static TextFormatFlags Flags(HorizontalAlignment a)
        {
            var f = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            if (a == HorizontalAlignment.Right) f |= TextFormatFlags.Right;
            else if (a == HorizontalAlignment.Center) f |= TextFormatFlags.HorizontalCenter;
            return f;
        }

        /// <summary>Stretches the last column over the remaining width (no white area right of the header).</summary>
        public void FillLastColumn()
        {
            if (Columns.Count == 0 || !IsHandleCreated) return;
            int used = 0;
            for (int i = 0; i < Columns.Count - 1; i++) used += Columns[i].Width;
            int w = ClientSize.Width - used;
            var last = Columns[Columns.Count - 1];
            if (w > Dpi.S(80) && last.Width != w) last.Width = w;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            FillLastColumn();
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            FillLastColumn();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && SmallImageList != null) { var il = SmallImageList; SmallImageList = null; il.Dispose(); }
            base.Dispose(disposing);
        }

        // --- "select all" in virtual mode
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LVITEM
        {
            public int mask, iItem, iSubItem, state, stateMask;
            public IntPtr pszText;
            public int cchTextMax, iImage;
            public IntPtr lParam;
            public int iIndent, iGroupId, cColumns;
            public IntPtr puColumns, piColFmt;
            public int iGroup;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVITEM lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private bool _nativeSelOk = true;

        /// <summary>Queries selection state in O(1) (SelectedIndices.Contains walks the whole selection in virtual mode).</summary>
        public bool IsItemSelected(int index)
        {
            if (_nativeSelOk && IsHandleCreated)
            {
                try { return SendMessage(Handle, 0x1000 + 44 /* LVM_GETITEMSTATE */, new IntPtr(index), new IntPtr(2)) != IntPtr.Zero; }
                catch { _nativeSelOk = false; }
            }
            return SelectedIndices.Contains(index);
        }

        // workaround for owner-draw + FullRowSelect repainting only the first cell on mouse move
        private int _hotIndex = -1;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var it = GetItemAt(e.X, e.Y);
            int idx = it != null ? it.Index : -1;
            if (idx == _hotIndex) return;
            if (_hotIndex >= 0 && _hotIndex < (VirtualMode ? VirtualListSize : Items.Count))
                try { Invalidate(GetItemRect(_hotIndex)); } catch { }
            if (it != null) Invalidate(it.Bounds);
            _hotIndex = idx;
        }

        public void SelectAllItems()
        {
            if (!VirtualMode)
            {
                BeginUpdate();
                foreach (ListViewItem it in Items) it.Selected = true;
                EndUpdate();
                return;
            }
            try
            {
                var lv = new LVITEM { mask = 8, state = 2, stateMask = 2 };
                SendMessage(Handle, 0x1000 + 43, new IntPtr(-1), ref lv);
            }
            catch { }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A) { SelectAllItems(); e.Handled = true; }
            base.OnKeyDown(e);
        }
    }

    /// <summary>Simple dark tab control (TabControl headers cannot be darkened).</summary>
    internal sealed class DarkTabs : Panel
    {
        private readonly FlowLayoutPanel _left;
        private readonly List<Button> _buttons = new List<Button>();
        private readonly List<Control> _pages = new List<Control>();
        private readonly Panel _body;
        private int _selected = -1;

        public readonly FlowLayoutPanel RightArea;
        public event EventHandler SelectedIndexChanged;

        public DarkTabs()
        {
            BackColor = Theme.Back;
            var header = new Panel { Dock = DockStyle.Top, Height = Dpi.S(34), BackColor = Theme.Panel };
            _left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Theme.Panel, Padding = new Padding(Dpi.S(4), 0, 0, 0) };
            RightArea = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Theme.Panel };
            header.Controls.Add(_left);
            header.Controls.Add(RightArea);
            _body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Back };
            Controls.Add(_body);
            Controls.Add(header);
        }

        public void AddPage(string title, Control content)
        {
            int idx = _pages.Count;
            var b = new Button
            {
                Text = title,
                FlatStyle = FlatStyle.Flat,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = Dpi.S(34),
                Margin = new Padding(0),
                Padding = new Padding(Dpi.S(10), Dpi.S(5), Dpi.S(10), Dpi.S(5)),
                BackColor = Theme.Panel,
                ForeColor = Theme.Dim,
                Font = Theme.UiFont,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.Surface;
            b.Click += (s, e) => SelectedIndex = idx;
            b.Paint += (s, e) =>
            {
                if (_selected == idx)
                    using (var br = new SolidBrush(Theme.Accent))
                        e.Graphics.FillRectangle(br, 0, b.Height - Dpi.S(2), b.Width, Dpi.S(2));
            };
            _buttons.Add(b);
            _left.Controls.Add(b);

            content.Dock = DockStyle.Fill;
            content.Visible = false;
            _pages.Add(content);
            _body.Controls.Add(content);
            if (_selected < 0) SelectedIndex = 0;
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (value < 0 || value >= _pages.Count || value == _selected) return;
                _selected = value;
                for (int i = 0; i < _pages.Count; i++)
                {
                    _pages[i].Visible = i == value;
                    _buttons[i].ForeColor = i == value ? Theme.Text : Theme.Dim;
                    _buttons[i].BackColor = i == value ? Theme.Back : Theme.Panel;
                    _buttons[i].Invalidate();
                }
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }
    }

    internal static class Ui
    {
        public static void Info(IWin32Window owner, string msg)
        {
            MessageBox.Show(owner, msg, "DiskHawk", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void Error(IWin32Window owner, string msg)
        {
            MessageBox.Show(owner, msg, "DiskHawk", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public static void CopyText(string s)
        {
            try { if (!string.IsNullOrEmpty(s)) Clipboard.SetText(s); } catch { }
        }

        public static void OpenExplorer(string path, bool select = false)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || path.IndexOf('"') >= 0 || PathText.HasControlChars(path)) return;
                // Explorer RUNS / opens a file path it is given: files are only ever shown selected.
                // An existence check on network paths can freeze the UI; a trailing '\' makes the path open only as a folder.
                if (!select)
                {
                    if (PathText.IsUncPath(path)) path = path.TrimEnd('\\') + "\\";
                    else if (File.Exists(path)) select = true;
                }
                if (select) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        /// <summary>
        /// Opens an (untrusted) path from a report in Explorer. Only "X:\..." paths are accepted and are
        /// converted to the target machine's C$ share; "\\server\..." paths require explicit confirmation first
        /// (otherwise a forged report could make Windows send credentials to another server).
        /// </summary>
        public static void OpenReportPath(IWin32Window owner, string machine, string path, bool select)
        {
            if (string.IsNullOrEmpty(path) || PathText.HasControlChars(path) || path.IndexOfAny(new[] { '"', '?', '*', '<', '>', '|' }) >= 0)
            {
                Error(owner, L.T("Bu yol açılamıyor (geçersiz karakter içeriyor)."));
                return;
            }
            string target;
            if (PathText.IsDrivePath(path))
            {
                bool local = !string.IsNullOrEmpty(machine) && string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
                if (!local && (string.IsNullOrEmpty(machine) || !PathText.IsValidMachineName(machine)))
                {
                    Error(owner, L.T("Hedef makine adı bilinmiyor veya geçersiz; yol açılamıyor."));
                    return;
                }
                target = ToAdminShare(machine, path);
            }
            else if (PathText.IsUncPath(path))
            {
                var server = path.Substring(2).Split('\\')[0];
                if (MessageBox.Show(owner, string.Format(L.T("Bu yol başka bir sunucuya bağlanır: \\\\{0}\n\nWindows bu sunucuya kimlik bilgilerinizle bağlanır. Yalnızca güvendiğiniz sunucular için devam edin. Açılsın mı?"), server),
                        Brand.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                target = path;
            }
            else
            {
                Error(owner, L.T("Bu yol açılamıyor (yerel sürücü yolu değil)."));
                return;
            }
            OpenExplorer(target, select);
        }

        /// <summary>"C:\Users\x" -> "\\PC\C$\Users\x" (administrative share for a remote machine).</summary>
        public static string ToAdminShare(string machine, string localPath)
        {
            if (string.IsNullOrEmpty(localPath) || localPath.Length < 2 || localPath[1] != ':') return localPath;
            if (string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return localPath;
            return @"\\" + machine + @"\" + localPath[0] + "$" + localPath.Substring(2);
        }
    }
}
