using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DiskHawk.Core;

namespace DiskHawk.App
{
    /// <summary>Window showing one machine's scan result: tree + list + treemap, largest files, extensions.</summary>
    internal sealed class ResultForm : DarkForm
    {
        private sealed class FolderRow
        {
            public DirNode Node;
            public bool IsFiles;
            public long Size { get { return IsFiles ? Node.OwnSize : Node.Size; } }
            public int Files { get { return IsFiles ? Node.OwnFiles : Node.Files; } }
            public string Name { get { return IsFiles ? L.T("[bu klasördeki dosyalar]") : Node.Name; } }
        }

        private static readonly Color BarBlue = Color.FromArgb(52, 120, 198);

        private readonly ScanReport _rep;
        private readonly string _file;
        private readonly string _target;             // target machine name for delete / Explorer (null = unknown)
        private readonly bool _targetFromReport;     // target name came from the report file (not from the machine list)
        private bool _targetConfirmed;
        private readonly Action<string> _onChanged;  // notify the main window when the report changes (after a deletion)
        private readonly Action<string> _log;
        private bool _busy;
        private DriveResult _drive;
        private DirNode _current;
        private bool _syncing;

        private ComboBox _driveCombo;
        private Label _stats;
        private readonly ToolTip _statsTip = new ToolTip { AutoPopDelay = 20000 };
        private UsageBar _usage;
        private DarkTabs _tabs;

        private SplitContainer _splitMain, _splitRight;
        private TreeView _tree;
        private DarkListView _list;
        private TreemapControl _map;
        private TextBox _pathBox;
        private Button _upBtn;
        private readonly List<FolderRow> _rows = new List<FolderRow>();
        private int _rowSort = 1;
        private bool _rowDesc = true;

        private DarkListView _fileList;
        private TextBox _fileFilter;
        private Label _fileCount;
        private List<FileEntry> _fileRows = new List<FileEntry>();
        private int _fileSort = 1;
        private bool _fileDesc = true;

        private DarkListView _extList;
        private List<ExtStat> _extRows = new List<ExtStat>();
        private int _extSort = 1;
        private bool _extDesc = true;

        public ResultForm(ScanReport rep, string file, string target = null, Action<string> onChanged = null, Action<string> log = null)
        {
            _rep = rep;
            _file = file;
            // for a report opened from a file the target name comes from the report (untrusted): remote actions are off if it is invalid,
            // otherwise the user is asked to confirm on the first remote action (Explorer / delete).
            _targetFromReport = string.IsNullOrEmpty(target);
            _target = !_targetFromReport ? target : (PathText.IsValidMachineName(rep.Machine) ? rep.Machine : null);
            _onChanged = onChanged;
            _log = log;
            Text = "DiskHawk - " + rep.Machine;
            Size = Dpi.Fit(1320, 860);
            MinimumSize = Dpi.Fit(800, 500);
            KeyPreview = true;

            _tabs = new DarkTabs { Dock = DockStyle.Fill };
            _tabs.RightArea.Controls.Add(new IconButton(Glyph.Save, L.T("CSV'ye aktar"), (s, e) => ExportCsv()));
            if (!string.IsNullOrEmpty(_file))
                _tabs.RightArea.Controls.Add(new IconButton(Glyph.Folder, L.T("Rapor dosyasını göster"), (s, e) => Ui.OpenExplorer(_file, true)));
            _tabs.AddPage(L.T("Klasörler"), BuildFoldersPage());
            _tabs.AddPage(L.T("En büyük dosyalar"), BuildFilesPage());
            _tabs.AddPage(L.T("Uzantılar"), BuildExtPage());

            Controls.Add(_tabs);
            Controls.Add(BuildHeader());

            for (int i = 0; i < _rep.Drives.Count; i++)
            {
                var d = _rep.Drives[i];
                _driveCombo.Items.Add(string.Format("{0}   {1}  {2}", d.RootPath, d.FileSystem, d.TotalBytes > 0 ? Fmt.Size(d.TotalBytes) : ""));
            }
            _driveCombo.SelectedIndexChanged += (s, e) => LoadDrive(_driveCombo.SelectedIndex);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                _splitMain.SplitterDistance = Dpi.S(330);
                _splitRight.SplitterDistance = (int)(_splitRight.Height * 0.48);
            }
            catch { }
            if (_driveCombo.Items.Count > 0) _driveCombo.SelectedIndex = 0;
            _list.FillLastColumn();
            _fileList.FillLastColumn();
            _extList.FillLastColumn();
            _list.Focus();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                Ui.Info(this, L.T("Silme sürüyor, bitince pencereyi kapatabilirsiniz."));
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Alt && e.KeyCode == Keys.Up) { GoUp(); e.Handled = true; }
            if (e.KeyCode == Keys.F5 && _current != null) { Navigate(_current, null); e.Handled = true; }
        }

        // ------------------------------------------------------------------ header

        private Control BuildHeader()
        {
            var header = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Theme.Panel,
                Padding = new Padding(Dpi.S(10), Dpi.S(6), Dpi.S(10), Dpi.S(4))
            };

            var line1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Panel, Margin = new Padding(0) };
            line1.Controls.Add(Theme.Lbl(_rep.Machine, Theme.Text, Theme.TitleFont));
            var sub = Theme.Lbl(string.Format(L.T("Tarama: {0}   |   Tarayıcı v{1}   |   {2}   |   {3}"),
                Fmt.Date(_rep.ScanTimeUtc.ToLocalTime()), _rep.ScannerVersion, _rep.UserContext, _rep.OsVersion), Theme.Dim);
            sub.Margin = new Padding(Dpi.S(10), Dpi.S(10), 0, 0);
            line1.Controls.Add(sub);

            var line2 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Panel, Margin = new Padding(0) };
            line2.Controls.Add(Theme.Lbl(L.T("Sürücü:"), Theme.Dim));
            _driveCombo = Theme.Combo(200);
            line2.Controls.Add(_driveCombo);
            _usage = new UsageBar { Width = Dpi.S(170), Height = Dpi.S(16), Margin = new Padding(Dpi.S(10), Dpi.S(8), Dpi.S(6), 0) };
            line2.Controls.Add(_usage);
            _stats = Theme.Lbl("", Theme.Text);
            line2.Controls.Add(_stats);

            header.Controls.Add(line1);
            header.Controls.Add(line2);
            return header;
        }

        private void LoadDrive(int idx)
        {
            if (idx < 0 || idx >= _rep.Drives.Count) return;
            _drive = _rep.Drives[idx];
            var d = _drive;

            UpdateStats();

            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            if (d.Root != null)
            {
                var rn = MakeTreeNode(d.Root);
                _tree.Nodes.Add(rn);
                rn.Expand();
            }
            _tree.EndUpdate();

            if (d.Root != null) Navigate(d.Root, null);
            else
            {
                _current = null;
                _rows.Clear();
                _list.VirtualListSize = 0;
                _list.Invalidate();
                _map.Root = null;
                _pathBox.Text = d.RootPath;
            }
            RefreshFiles();
            RefreshExt();
        }

        private void UpdateStats()
        {
            var d = _drive;
            if (d == null) return;
            _stats.ForeColor = Theme.Text;
            var parts = new List<string>();
            if (d.TotalBytes > 0)
                parts.Add(string.Format(L.T("Toplam {0}  |  Boş {1}  ({2} dolu)"), Fmt.Size(d.TotalBytes), Fmt.Size(d.FreeBytes), Fmt.Percent(d.UsedPercent)));
            parts.Add(string.Format(L.T("Taranan {0}, {1} dosya, {2} klasör  |  {3}{4}"),
                Fmt.Size(d.ScannedBytes), Fmt.Count(d.ScannedFiles), Fmt.Count(d.ScannedDirs), Fmt.Duration(d.DurationMs),
                string.IsNullOrEmpty(d.Engine) ? "" : " (" + L.T(d.Engine) + ")"));
            if (d.Errors > 0) parts.Add(L.T("Erişilemeyen ") + Fmt.Count(d.Errors) + L.T(" klasör"));
            if (d.CloudOnlyBytes > 0) parts.Add(L.T("Bulutta (diskte değil) ") + Fmt.Size(d.CloudOnlyBytes));
            _stats.Text = string.Join("   |   ", parts);
            _statsTip.SetToolTip(_stats, string.IsNullOrEmpty(d.EngineNote) ? null : L.T(d.Engine) + ": " + d.EngineNote);
            _usage.Percent = d.UsedPercent;
            _usage.Visible = d.TotalBytes > 0;
        }

        // ------------------------------------------------------------------ folders tab

        private Control BuildFoldersPage()
        {
            var page = new Panel { BackColor = Theme.Back };

            var nav = new Panel { Dock = DockStyle.Top, Height = Dpi.S(36), BackColor = Theme.Back, Padding = new Padding(Dpi.S(4), Dpi.S(5), Dpi.S(8), Dpi.S(4)) };
            var navBtns = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Theme.Back, Margin = new Padding(0) };
            _upBtn = new IconButton(Glyph.Up, L.T("Üst klasör"), (s, e) => GoUp(), false, "Alt+↑ / Backspace");
            _upBtn.Margin = new Padding(Dpi.S(2), 0, Dpi.S(2), 0);
            var rootBtn = new IconButton(Glyph.Home, L.T("Kök"), (s, e) => { if (_drive != null) Navigate(_drive.Root, null); });
            rootBtn.Margin = new Padding(Dpi.S(2), 0, Dpi.S(6), 0);
            navBtns.Controls.Add(_upBtn);
            navBtns.Controls.Add(rootBtn);
            _pathBox = Theme.TextBox(400);
            _pathBox.Dock = DockStyle.Fill;
            _pathBox.ReadOnly = true;
            _pathBox.BackColor = Theme.Panel;
            nav.Controls.Add(_pathBox);
            nav.Controls.Add(navBtns);

            _splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = Theme.Border,
                SplitterWidth = Dpi.S(4),
                FixedPanel = FixedPanel.Panel1
            };
            _splitMain.Panel1.BackColor = Theme.Back;
            _splitMain.Panel2.BackColor = Theme.Back;

            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Back,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
                LineColor = Theme.Border,
                HideSelection = false,
                FullRowSelect = true,
                ShowLines = false,
                DrawMode = TreeViewDrawMode.OwnerDrawText,
                ItemHeight = Dpi.S(22),
                Font = Theme.UiFont
            };
            _tree.HandleCreated += (s, e) => Theme.DarkScrollbars(_tree);
            _tree.BeforeExpand += Tree_BeforeExpand;
            _tree.AfterSelect += (s, e) =>
            {
                if (_syncing) return;
                var n = e.Node != null ? e.Node.Tag as DirNode : null;
                if (n != null) Navigate(n, null, true);
            };
            _tree.DrawNode += Tree_DrawNode;
            _splitMain.Panel1.Controls.Add(_tree);

            _splitRight = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = Theme.Border,
                SplitterWidth = Dpi.S(4)
            };
            _splitRight.Panel1.BackColor = Theme.Back;
            _splitRight.Panel2.BackColor = Theme.Back;

            _list = new DarkListView { Dock = DockStyle.Fill, VirtualMode = true, MultiSelect = true };
            _list.AddColumn(L.T("Ad"), 300);
            _list.AddColumn(L.T("Boyut"), 95, HorizontalAlignment.Right);
            _list.AddColumn(L.T("Pay"), 150);
            _list.AddColumn(L.T("Dosyalar"), 90, HorizontalAlignment.Right);
            _list.AddColumn(L.T("Alt klasörler"), 80, HorizontalAlignment.Right);
            _list.AddColumn(L.T("Son değişiklik"), 125);
            _list.AddColumn(L.T("Not"), 120);
            _list.SortColumn = 1;
            _list.RetrieveVirtualItem += List_RetrieveVirtualItem;
            _list.Bars = FolderBars;
            _list.Colors = FolderColors;
            _list.DoubleClick += (s, e) => OpenSelectedRow();
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { OpenSelectedRow(); e.Handled = true; }
                else if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
                else if (e.KeyCode == Keys.Delete) { DeleteFromFolderList(); e.Handled = true; }
            };
            _list.SelectedIndexChanged += (s, e) =>
            {
                var r = SelectedRow();
                _map.Selected = r != null && !r.IsFiles ? r.Node : null;
            };
            _list.ColumnClick += (s, e) =>
            {
                if (_rowSort == e.Column) _rowDesc = !_rowDesc;
                else { _rowSort = e.Column; _rowDesc = e.Column != 0 && e.Column != 6; }
                _list.SortColumn = _rowSort;
                _list.SortDescending = _rowDesc;
                var sel = SelectedRow();
                SortRows();
                ReselectRow(sel);
                _list.Invalidate();
            };
            var menu = Theme.Menu();
            menu.Item(L.T("Aç"), (s, e) => OpenSelectedRow());
            menu.Item(L.T("Yolu kopyala"), (s, e) => { var r = SelectedRow(); if (r != null) Ui.CopyText(r.Node.FullPath); });
            menu.Item(L.T("Explorer'da aç"), (s, e) => { var r = SelectedRow(); if (r != null) OpenPath(r.Node.FullPath, false); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Item(L.T("Kalıcı olarak sil..."), (s, e) => DeleteFromFolderList()).ForeColor = Theme.Bad;
            _list.ContextMenuStrip = menu;
            _splitRight.Panel1.Controls.Add(_list);

            _map = new TreemapControl { Dock = DockStyle.Fill };
            _map.NodeClicked += Map_NodeClicked;
            _map.NodeDoubleClicked += n =>
            {
                if (n.HasChildren) Navigate(n, null);
                else if (n.Parent != null) Navigate(n.Parent, n);
            };
            _splitRight.Panel2.Controls.Add(_map);

            _splitMain.Panel2.Controls.Add(_splitRight);

            page.Controls.Add(_splitMain);
            page.Controls.Add(nav);
            return page;
        }

        private TreeNode MakeTreeNode(DirNode n)
        {
            var tn = new TreeNode(n.Name + "     " + Fmt.Size(n.Size)) { Tag = n };
            if (n.HasChildren) tn.Nodes.Add(new TreeNode("...") { Tag = null });
            return tn;
        }

        private void Tree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            var tn = e.Node;
            if (tn.Nodes.Count == 1 && tn.Nodes[0].Tag == null)
            {
                var n = (DirNode)tn.Tag;
                _tree.BeginUpdate();
                tn.Nodes.Clear();
                foreach (var c in n.Children) tn.Nodes.Add(MakeTreeNode(c));
                _tree.EndUpdate();
            }
        }

        private void Tree_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            var n = e.Node.Tag as DirNode;
            var b = e.Bounds;
            if (n == null || b.Width <= 0 || b.Height <= 0) { e.DrawDefault = n == null; return; }
            var g = e.Graphics;
            bool sel = (e.State & TreeNodeStates.Selected) != 0;
            var row = new Rectangle(b.X, b.Y, Math.Max(b.Width, _tree.ClientSize.Width - b.X), b.Height);
            using (var br = new SolidBrush(sel ? (_tree.Focused ? Theme.Select : Theme.SelectInactive) : Theme.Back))
                g.FillRectangle(br, row);

            const TextFormatFlags f = TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            var nameSize = TextRenderer.MeasureText(g, n.Name, _tree.Font, new Size(int.MaxValue, b.Height), f);
            TextRenderer.DrawText(g, n.Name, _tree.Font, new Rectangle(b.X + 2, b.Y, nameSize.Width + 2, b.Height),
                n.IsAccessDenied ? Theme.Warn : Theme.Text, f);

            // share of parent: small bar + size
            int x = b.X + nameSize.Width + Dpi.S(10);
            if (n.Parent != null && n.Parent.Size > 0)
            {
                double pct = (double)n.Size / n.Parent.Size;
                var bar = new Rectangle(x, b.Y + b.Height / 2 - Dpi.S(3), Dpi.S(36), Dpi.S(6));
                using (var br = new SolidBrush(Theme.Surface)) g.FillRectangle(br, bar);
                using (var br = new SolidBrush(BarBlue)) g.FillRectangle(br, bar.X, bar.Y, (int)(bar.Width * pct), bar.Height);
                x += bar.Width + Dpi.S(6);
            }
            TextRenderer.DrawText(g, Fmt.Size(n.Size), Theme.SmallFont, new Rectangle(x, b.Y, Dpi.S(90), b.Height), Theme.Dim, f);
        }

        private void Navigate(DirNode node, DirNode select, bool fromTree = false)
        {
            if (node == null) return;
            _current = node;
            _rows.Clear();
            if (node.Children != null)
                foreach (var c in node.Children) _rows.Add(new FolderRow { Node = c });
            if (node.OwnFiles > 0) _rows.Add(new FolderRow { Node = node, IsFiles = true });
            SortRows();

            _list.SelectedIndices.Clear();
            _list.VirtualListSize = _rows.Count;
            _list.Invalidate();
            _pathBox.Text = node.FullPath;
            _upBtn.Enabled = node.Parent != null;
            _map.Root = node;
            _map.Selected = select;

            if (!fromTree) SelectTreeNode(node);
            if (select != null) ReselectRow(new FolderRow { Node = select });
            else if (_rows.Count > 0) _list.EnsureVisible(0);
        }

        private void GoUp()
        {
            if (_current != null && _current.Parent != null) Navigate(_current.Parent, _current);
        }

        private void OpenSelectedRow()
        {
            var r = SelectedRow();
            if (r == null || r.IsFiles) return;
            if (r.Node.HasChildren) Navigate(r.Node, null);
        }

        private FolderRow SelectedRow()
        {
            if (_list.SelectedIndices.Count == 0) return null;
            int i = _list.SelectedIndices[0];
            return i >= 0 && i < _rows.Count ? _rows[i] : null;
        }

        private void ReselectRow(FolderRow r)
        {
            if (r == null) return;
            int idx = _rows.FindIndex(x => x.Node == r.Node && x.IsFiles == r.IsFiles);
            if (idx < 0) return;
            _list.SelectedIndices.Clear();
            _list.SelectedIndices.Add(idx);
            _list.EnsureVisible(idx);
        }

        private void SelectTreeNode(DirNode n)
        {
            var chain = new List<DirNode>();
            for (var x = n; x != null; x = x.Parent) chain.Insert(0, x);
            TreeNodeCollection col = _tree.Nodes;
            TreeNode found = null;
            _tree.BeginUpdate();
            try
            {
                foreach (var a in chain)
                {
                    found = null;
                    foreach (TreeNode tn in col)
                        if (tn.Tag == a) { found = tn; break; }
                    if (found == null) break;
                    if (a != n) { found.Expand(); col = found.Nodes; }
                }
            }
            finally { _tree.EndUpdate(); }
            if (found != null)
            {
                _syncing = true;
                _tree.SelectedNode = found;
                found.EnsureVisible();
                _syncing = false;
            }
        }

        private void SortRows()
        {
            Comparison<FolderRow> cmp;
            switch (_rowSort)
            {
                case 0: cmp = (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); break;
                case 3: cmp = (a, b) => a.Files.CompareTo(b.Files); break;
                case 4: cmp = (a, b) => (a.IsFiles ? 0 : a.Node.Dirs).CompareTo(b.IsFiles ? 0 : b.Node.Dirs); break;
                case 5: cmp = (a, b) => a.Node.LastWrite.CompareTo(b.Node.LastWrite); break;
                case 6: cmp = (a, b) => a.Node.Flags.CompareTo(b.Node.Flags); break;
                default: cmp = (a, b) => a.Size.CompareTo(b.Size); break;
            }
            if (_rowDesc) _rows.Sort((a, b) => cmp(b, a));
            else _rows.Sort(cmp);
        }

        private void List_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count) { e.Item = new ListViewItem(new string[7]); return; }
            var r = _rows[e.ItemIndex];
            double pct = _current != null && _current.Size > 0 ? r.Size * 100.0 / _current.Size : 0;
            string note = r.IsFiles ? "" : r.Node.IsAccessDenied ? L.T("Erişim yok") : (r.Node.Flags & DirNode.FlagError) != 0 ? L.T("Okuma hatası") : "";
            e.Item = new ListViewItem(new[]
            {
                r.Name,
                Fmt.Size(r.Size),
                Fmt.Percent(pct),
                Fmt.Count(r.Files),
                r.IsFiles ? "" : Fmt.Count(r.Node.Dirs),
                r.IsFiles ? "" : Fmt.Date(r.Node.LastWriteLocal),
                note
            });
        }

        private bool FolderBars(int i, int col, out double v, out Color c)
        {
            v = 0;
            c = BarBlue;
            if (col != 2 || i < 0 || i >= _rows.Count || _current == null || _current.Size <= 0) return false;
            v = (double)_rows[i].Size / _current.Size;
            if (_rows[i].IsFiles) c = Color.FromArgb(100, 100, 100);
            return true;
        }

        private Color? FolderColors(int i, int col)
        {
            if (i < 0 || i >= _rows.Count) return null;
            var r = _rows[i];
            if (r.IsFiles) return Theme.Dim;
            if (col == 6 || (col == 0 && r.Node.IsAccessDenied)) return Theme.Warn;
            if (col >= 3) return Theme.Dim;
            return null;
        }

        private void Map_NodeClicked(DirNode n)
        {
            if (_current == null || n == null) return;
            if (n == _current) { ReselectRow(new FolderRow { Node = n, IsFiles = true }); return; }
            // select in the list the ancestor of the clicked box that is a direct child of the current folder
            var x = n;
            while (x.Parent != null && x.Parent != _current) x = x.Parent;
            if (x.Parent == _current) ReselectRow(new FolderRow { Node = x });
            _map.Selected = n;
        }

        // ------------------------------------------------------------------ files tab

        private Control BuildFilesPage()
        {
            var page = new Panel { BackColor = Theme.Back };
            var bar = Theme.Toolbar();
            bar.WrapContents = false;
            bar.Controls.Add(Theme.Lbl(L.T("Filtre:"), Theme.Dim));
            _fileFilter = Theme.TextBox(320);
            _fileFilter.TextChanged += (s, e) => RefreshFiles();
            bar.Controls.Add(_fileFilter);
            _fileCount = Theme.Lbl("", Theme.Dim);
            bar.Controls.Add(_fileCount);

            _fileList = new DarkListView { Dock = DockStyle.Fill, VirtualMode = true };
            _fileList.AddColumn(L.T("Dosya"), 320);
            _fileList.AddColumn(L.T("Boyut"), 95, HorizontalAlignment.Right);
            _fileList.AddColumn(L.T("Son değişiklik"), 125);
            _fileList.AddColumn(L.T("Klasör"), 600);
            _fileList.SortColumn = 1;
            _fileList.RetrieveVirtualItem += (s, e) =>
            {
                if (e.ItemIndex < 0 || e.ItemIndex >= _fileRows.Count) { e.Item = new ListViewItem(new string[4]); return; }
                var f = _fileRows[e.ItemIndex];
                e.Item = new ListViewItem(new[] { f.FileName, Fmt.Size(f.Size), Fmt.Date(f.LastWriteLocal), f.Directory });
            };
            _fileList.Colors = (i, col) => col >= 2 ? (Color?)Theme.Dim : null;
            _fileList.ColumnClick += (s, e) =>
            {
                if (_fileSort == e.Column) _fileDesc = !_fileDesc;
                else { _fileSort = e.Column; _fileDesc = e.Column == 1 || e.Column == 2; }
                _fileList.SortColumn = _fileSort;
                _fileList.SortDescending = _fileDesc;
                RefreshFiles();
            };
            _fileList.DoubleClick += (s, e) => GoToSelectedFileFolder();

            var menu = Theme.Menu();
            menu.Item(L.T("Klasöre git"), (s, e) => GoToSelectedFileFolder());
            menu.Item(L.T("Yolu kopyala"), (s, e) =>
            {
                var sel = SelectedFiles();
                if (sel.Count > 0) Ui.CopyText(string.Join(Environment.NewLine, sel.Select(f => f.Path)));
            });
            menu.Item(L.T("Explorer'da göster"), (s, e) =>
            {
                var sel = SelectedFiles();
                if (sel.Count > 0) OpenPath(sel[0].Path, true);
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Item(L.T("Kalıcı olarak sil..."), (s, e) => DeleteFromFileList()).ForeColor = Theme.Bad;
            _fileList.ContextMenuStrip = menu;
            _fileList.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) { DeleteFromFileList(); e.Handled = true; } };

            page.Controls.Add(_fileList);
            page.Controls.Add(bar);
            return page;
        }

        private void RefreshFiles()
        {
            if (_drive == null) return;
            IEnumerable<FileEntry> q = _drive.LargestFiles;
            var filter = _fileFilter.Text.Trim();
            if (filter.Length > 0) q = q.Where(f => f.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            var list = q.ToList();
            Comparison<FileEntry> cmp;
            switch (_fileSort)
            {
                case 0: cmp = (a, b) => string.Compare(a.FileName, b.FileName, StringComparison.CurrentCultureIgnoreCase); break;
                case 2: cmp = (a, b) => a.LastWrite.CompareTo(b.LastWrite); break;
                case 3: cmp = (a, b) => string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase); break;
                default: cmp = (a, b) => a.Size.CompareTo(b.Size); break;
            }
            if (_fileDesc) list.Sort((a, b) => cmp(b, a)); else list.Sort(cmp);
            _fileRows = list;
            _fileList.SelectedIndices.Clear();
            _fileList.VirtualListSize = list.Count;
            _fileList.Invalidate();
            long total = 0;
            foreach (var f in list) total += f.Size;
            _fileCount.Text = string.Format(L.T("{0} dosya, toplam {1}   (rapora {2} üstü en büyük dosyalar alınır)"),
                Fmt.Count(list.Count), Fmt.Size(total), _drive.LargestFiles.Count > 0 ? Fmt.Size(_drive.LargestFiles[_drive.LargestFiles.Count - 1].Size) : "-");
        }

        private List<FileEntry> SelectedFiles()
        {
            var res = new List<FileEntry>();
            foreach (int i in _fileList.SelectedIndices)
                if (i >= 0 && i < _fileRows.Count) res.Add(_fileRows[i]);
            return res;
        }

        private void GoToSelectedFileFolder()
        {
            var sel = SelectedFiles();
            if (sel.Count == 0 || _drive == null) return;
            bool exact;
            var node = Analysis.FindNode(_drive.Root, sel[0].Directory, out exact);
            _tabs.SelectedIndex = 0;
            if (node != null) Navigate(node, null);
        }

        // ------------------------------------------------------------------ extensions tab

        private Control BuildExtPage()
        {
            _extList = new DarkListView { Dock = DockStyle.Fill, VirtualMode = true };
            _extList.AddColumn(L.T("Uzantı"), 140);
            _extList.AddColumn(L.T("Boyut"), 100, HorizontalAlignment.Right);
            _extList.AddColumn(L.T("Pay"), 200);
            _extList.AddColumn(L.T("Dosya sayısı"), 110, HorizontalAlignment.Right);
            _extList.AddColumn(L.T("Ortalama boyut"), 120, HorizontalAlignment.Right);
            _extList.SortColumn = 1;
            _extList.RetrieveVirtualItem += (s, e) =>
            {
                if (e.ItemIndex < 0 || e.ItemIndex >= _extRows.Count) { e.Item = new ListViewItem(new string[5]); return; }
                var x = _extRows[e.ItemIndex];
                double pct = _drive != null && _drive.ScannedBytes > 0 ? x.Size * 100.0 / _drive.ScannedBytes : 0;
                e.Item = new ListViewItem(new[]
                {
                    x.Ext.Length == 0 ? L.T("(uzantısız)") : "." + x.Ext,
                    Fmt.Size(x.Size),
                    Fmt.Percent(pct),
                    Fmt.Count(x.Count),
                    Fmt.Size(x.Count > 0 ? x.Size / x.Count : 0)
                });
            };
            _extList.Bars = (int i, int col, out double v, out Color c) =>
            {
                v = 0; c = BarBlue;
                if (col != 2 || i < 0 || i >= _extRows.Count || _drive == null || _drive.ScannedBytes <= 0) return false;
                v = (double)_extRows[i].Size / _drive.ScannedBytes;
                return true;
            };
            _extList.Colors = (i, col) => col >= 3 ? (Color?)Theme.Dim : null;
            _extList.ColumnClick += (s, e) =>
            {
                if (_extSort == e.Column) _extDesc = !_extDesc;
                else { _extSort = e.Column; _extDesc = e.Column != 0; }
                _extList.SortColumn = _extSort;
                _extList.SortDescending = _extDesc;
                RefreshExt();
            };
            var menu = Theme.Menu();
            menu.Item(L.T("Bu uzantıdaki en büyük dosyaları göster"), (s, e) =>
            {
                if (_extList.SelectedIndices.Count == 0) return;
                var x = _extRows[_extList.SelectedIndices[0]];
                if (x.Ext.Length == 0) return;
                _tabs.SelectedIndex = 1;
                _fileFilter.Text = "." + x.Ext;
            });
            _extList.ContextMenuStrip = menu;
            return _extList;
        }

        private void RefreshExt()
        {
            if (_drive == null) return;
            var list = new List<ExtStat>(_drive.Extensions);
            Comparison<ExtStat> cmp;
            switch (_extSort)
            {
                case 0: cmp = (a, b) => string.Compare(a.Ext, b.Ext, StringComparison.OrdinalIgnoreCase); break;
                case 3: cmp = (a, b) => a.Count.CompareTo(b.Count); break;
                case 4: cmp = (a, b) => (a.Count > 0 ? a.Size / a.Count : 0).CompareTo(b.Count > 0 ? b.Size / b.Count : 0); break;
                default: cmp = (a, b) => a.Size.CompareTo(b.Size); break;
            }
            if (_extDesc) list.Sort((a, b) => cmp(b, a)); else list.Sort(cmp);
            _extRows = list;
            _extList.SelectedIndices.Clear();
            _extList.VirtualListSize = list.Count;
            _extList.Invalidate();
        }

        // ------------------------------------------------------------------ delete

        private void DeleteFromFolderList()
        {
            if (_busy) return;
            var items = new List<DeleteItem>();
            bool pseudo = false;
            foreach (int i in _list.SelectedIndices)
            {
                if (i < 0 || i >= _rows.Count) continue;
                var r = _rows[i];
                if (r.IsFiles) { pseudo = true; continue; }
                items.Add(new DeleteItem { Path = r.Node.FullPath, IsDir = true, Size = r.Node.Size, Files = r.Node.Files });
            }
            if (items.Count == 0)
            {
                if (pseudo) Ui.Info(this, L.T("[bu klasördeki dosyalar] satırı toplu silinemez.\nDosyaları 'En büyük dosyalar' sekmesinden seçip silebilirsiniz."));
                return;
            }
            RunDelete(items);
        }

        private void DeleteFromFileList()
        {
            if (_busy) return;
            var items = SelectedFiles().Select(f => new DeleteItem { Path = f.Path, IsDir = false, Size = f.Size, Files = 1 }).ToList();
            if (items.Count > 0) RunDelete(items);
        }

        private void OpenPath(string path, bool select)
        {
            // "\\server\..." paths need no target machine (OpenReportPath asks for confirmation itself)
            if (!PathText.IsUncPath(path) && !IsLocalTarget() && !ConfirmReportTarget()) return;
            Ui.OpenReportPath(this, _target, path, select);
        }

        /// <summary>If the report was opened from a file, confirm once before connecting to the machine named in it.</summary>
        private bool ConfirmReportTarget()
        {
            if (_target == null)
            {
                Ui.Error(this, L.T("Rapordaki makine adı geçersiz; bu rapor üzerinden uzak işlem yapılamaz."));
                return false;
            }
            if (!_targetFromReport || _targetConfirmed) return true;
            var r = MessageBox.Show(this, string.Format(L.T("Bu rapor bir dosyadan açıldı; hedef makine adı rapor dosyasından alınıyor: {0}\n\nRapor güvenilir bir kaynaktan geldiyse devam edin. Bu makineye bağlanılsın mı?"), _target),
                Brand.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            _targetConfirmed = r == DialogResult.Yes;
            return _targetConfirmed;
        }

        private bool IsLocalTarget()
        {
            if (_target == null) return false;
            return string.Equals(_target, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(_target, "localhost", StringComparison.OrdinalIgnoreCase) || _target == ".";
        }

        private void SetBusyText(string t)
        {
            _stats.ForeColor = Theme.Warn;
            _stats.Text = L.T("Siliniyor (") + _target + "): " + t;
        }

        private void RunDelete(List<DeleteItem> items)
        {
            // for a report opened from a file, confirm the target before deleting (even if it matches the local computer name)
            if ((_targetFromReport || !IsLocalTarget()) && !ConfirmReportTarget()) return;
            // de-duplicate paths selected twice; drop items below a selected folder
            items = items.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var dirs = items.Where(i => i.IsDir).Select(i => i.Path.TrimEnd('\\') + "\\").ToList();
            items = items.Where(i => !dirs.Any(d => i.Path.StartsWith(d, StringComparison.OrdinalIgnoreCase))).ToList();

            foreach (var i in items)
            {
                string reason;
                i.Level = Deleter.Classify(i.Path, out reason);
                i.Reason = reason;
            }

            bool allowCritical;
            using (var f = new DeleteConfirmForm(_target, items))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                items = f.ToDelete;
                allowCritical = f.IncludeCritical;
            }
            if (items.Count == 0) return;

            var paths = items.Select(i => i.Path).ToList();
            var byPath = items.ToDictionary(i => i.Path, i => i, StringComparer.OrdinalIgnoreCase);
            string target = _target;
            bool local = IsLocalTarget();

            _busy = true;
            _tabs.Enabled = false;
            _driveCombo.Enabled = false;
            UseWaitCursor = true;
            SetBusyText(local ? L.T("yerel") : L.T("başlıyor"));
            if (_log != null) _log(target + ": " + items.Count + L.T(" öğe için kalıcı silme başlatıldı"));

            Action<string> phase = t =>
            {
                try { if (IsHandleCreated) BeginInvoke((Action)(() => SetBusyText(t))); } catch { }
            };

            Task.Run(() => local
                    ? new Deleter { AllowCritical = allowCritical }.DeleteAll(paths, CancellationToken.None)
                    : new RemoteScanner(AppPaths.RemoteSettings(), _log).DeleteRemote(target, paths, allowCritical, CancellationToken.None, phase))
                .ContinueWith(t =>
                {
                    _busy = false;
                    _tabs.Enabled = true;
                    _driveCombo.Enabled = true;
                    UseWaitCursor = false;

                    if (t.IsFaulted)
                    {
                        var baseEx = t.Exception.GetBaseException();
                        var msg = baseEx.Message;
                        var sf = baseEx as ScanFailure;
                        var st = sf != null && sf.OutcomeUnknown ? DeleteStatus.Unknown : DeleteStatus.Failed;
                        foreach (var i in items)
                            AppPaths.Audit(target, new DeleteResult { Path = i.Path, IsDirectory = i.IsDir, Status = st, Error = msg }, i.Size);
                        if (_log != null) _log(target + L.T(": silme yapılamadı - ") + msg);
                        UpdateStats();
                        Ui.Error(this, L.T("Silme yapılamadı:\n") + msg);
                        return;
                    }

                    // the remote result file is untrusted: only results for requested paths are accepted, one per path
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var results = t.Result.Where(r => r.Path != null && byPath.ContainsKey(r.Path) && seen.Add(r.Path)).ToList();
                    if (results.Count != t.Result.Count && _log != null)
                        _log(target + L.T(": sonuç dosyasında istenmeyen yollar vardı, yok sayıldı"));
                    // every request without a result is also written to the audit log and result window as "result unknown"
                    foreach (var i in items)
                        if (!seen.Contains(i.Path))
                            results.Add(new DeleteResult { Path = i.Path, IsDirectory = i.IsDir, Status = DeleteStatus.Unknown,
                                Error = L.T("Sonuç dosyasında bu yol için kayıt yok; makineyi yeniden tarayın") });
                    long freed = 0;
                    foreach (var r in results)
                    {
                        DeleteItem it;
                        long expected = 0;
                        if (byPath.TryGetValue(r.Path, out it)) { expected = it.Size; if (it.IsDir) r.IsDirectory = true; }
                        freed += r.FreedBytes;
                        AppPaths.Audit(target, r, expected);
                    }
                    if (_log != null) _log(string.Format(L.T("{0}: silme bitti - {1} öğe, {2} boşaldı"), target, results.Count, Fmt.Size(freed)));

                    ApplyDeletions(results, byPath);
                    if (!string.IsNullOrEmpty(_file))
                    {
                        try { ReportSerializer.Save(_rep, _file); }
                        catch (Exception ex) { if (_log != null) _log(L.T("Rapor güncellenemedi: ") + ex.Message); }
                    }
                    if (_onChanged != null) _onChanged(_file);

                    new DeleteResultForm(target, results).ShowDialog(this);
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Removes deleted items from the in-memory tree and updates parent totals and lists.</summary>
        private void ApplyDeletions(List<DeleteResult> results, Dictionary<string, DeleteItem> byPath)
        {
            var d = _drive;
            if (d == null || d.Root == null) return;
            DirNode navTo = null;

            foreach (var r in results)
            {
                bool removed = r.Status == DeleteStatus.Deleted || r.Status == DeleteStatus.NotFound;
                if (!removed && r.Status != DeleteStatus.Partial) continue;
                DeleteItem it;
                byPath.TryGetValue(r.Path, out it);
                bool exact = false;

                if (r.IsDirectory)
                {
                    var node = Analysis.FindNode(d.Root, r.Path, out exact);
                    if (!exact || node == null || node.Parent == null) continue;
                    if (removed)
                    {
                        if (IsSameOrAncestor(node, _current)) navTo = node.Parent;
                        var p = node.Parent;
                        p.Children.Remove(node);
                        for (var a = p; a != null; a = a.Parent)
                        {
                            a.Size -= node.Size;
                            a.Files -= node.Files;
                            a.Dirs -= node.Dirs + 1;
                        }
                        Resort(p);
                        var prefix = r.Path.TrimEnd('\\') + "\\";
                        d.LargestFiles.RemoveAll(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                        d.ScannedBytes -= node.Size;
                        d.ScannedFiles -= node.Files;
                        d.ScannedDirs -= node.Dirs + 1;
                    }
                    else
                    {
                        for (var a = node; a != null; a = a.Parent)
                        {
                            a.Size = Math.Max(0, a.Size - r.FreedBytes);
                            a.Files = Math.Max(0, a.Files - r.FilesDeleted);
                        }
                        Resort(node.Parent);
                        d.ScannedBytes -= r.FreedBytes;
                        d.ScannedFiles -= r.FilesDeleted;
                    }
                }
                else if (removed)
                {
                    long size = it != null ? it.Size : r.FreedBytes;
                    int cut = r.Path.LastIndexOf('\\');
                    var parent = cut > 0 ? Analysis.FindNode(d.Root, r.Path.Substring(0, cut), out exact) : null;
                    if (parent != null && exact)
                    {
                        parent.OwnSize = Math.Max(0, parent.OwnSize - size);
                        parent.OwnFiles = Math.Max(0, parent.OwnFiles - 1);
                        for (var a = parent; a != null; a = a.Parent)
                        {
                            a.Size = Math.Max(0, a.Size - size);
                            a.Files = Math.Max(0, a.Files - 1);
                        }
                        Resort(parent.Parent);
                    }
                    d.LargestFiles.RemoveAll(f => string.Equals(f.Path, r.Path, StringComparison.OrdinalIgnoreCase));
                    var name = r.Path.Substring(cut + 1);
                    int dot = name.LastIndexOf('.');
                    string ext = dot > 0 && dot < name.Length - 1 ? name.Substring(dot + 1) : "";
                    var es = d.Extensions.FirstOrDefault(x => string.Equals(x.Ext, ext, StringComparison.OrdinalIgnoreCase));
                    if (es != null)
                    {
                        es.Size = Math.Max(0, es.Size - size);
                        es.Count--;
                        if (es.Count <= 0) d.Extensions.Remove(es);
                    }
                    d.ScannedBytes -= size;
                    d.ScannedFiles -= 1;
                }

                if (r.FreedBytes > 0 && d.TotalBytes > 0) d.FreeBytes = Math.Min(d.TotalBytes, d.FreeBytes + r.FreedBytes);
            }

            // refresh the view
            var target = navTo ?? (_current != null && IsAttached(_current) ? _current : d.Root);
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            var rn = MakeTreeNode(d.Root);
            _tree.Nodes.Add(rn);
            rn.Expand();
            _tree.EndUpdate();
            Navigate(target, null);
            RefreshFiles();
            RefreshExt();
            UpdateStats();
        }

        private static bool IsSameOrAncestor(DirNode a, DirNode n)
        {
            for (var x = n; x != null; x = x.Parent) if (x == a) return true;
            return false;
        }

        private bool IsAttached(DirNode n)
        {
            var x = n;
            while (x.Parent != null)
            {
                if (x.Parent.Children == null || !x.Parent.Children.Contains(x)) return false;
                x = x.Parent;
            }
            return _drive != null && x == _drive.Root;
        }

        private static void Resort(DirNode n)
        {
            for (var a = n; a != null; a = a.Parent)
                if (a.Children != null) a.Children.Sort((x, y) => y.Size.CompareTo(x.Size));
        }

        // ------------------------------------------------------------------ export

        private void ExportCsv()
        {
            using (var dlg = new FolderBrowserDialog { Description = L.T("CSV dosyalarının yazılacağı klasör"), ShowNewFolderButton = true })
            {
                dlg.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var dir = dlg.SelectedPath;
                UseWaitCursor = true;
                Task.Run(() => CsvExporter.ExportReport(_rep, dir)).ContinueWith(t =>
                {
                    UseWaitCursor = false;
                    if (t.IsFaulted) { Ui.Error(this, L.T("CSV yazılamadı:\n") + t.Exception.GetBaseException().Message); return; }
                    Ui.OpenExplorer(t.Result[0], true);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        /// <summary>Loads a report file in the background and opens the window.</summary>
        public static void OpenFile(IWin32Window owner, string file, Action<string> log = null, string target = null, Action<string> onChanged = null)
        {
            var ctl = owner as Control;
            if (ctl != null) ctl.UseWaitCursor = true;
            Task.Run(() => ReportSerializer.Load(file)).ContinueWith(t =>
            {
                if (ctl != null) ctl.UseWaitCursor = false;
                if (t.IsFaulted)
                {
                    var msg = t.Exception.GetBaseException().Message;
                    if (log != null) log(L.T("Rapor açılamadı: ") + msg);
                    Ui.Error(owner, L.T("Rapor açılamadı:\n") + msg);
                    return;
                }
                new ResultForm(t.Result, file, target, onChanged, log).Show();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>Usage bar.</summary>
    internal sealed class UsageBar : Control
    {
        private double _pct;

        public UsageBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public double Percent
        {
            get { return _pct; }
            set { _pct = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, r);
            int w = (int)(r.Width * Math.Max(0, Math.Min(100, _pct)) / 100.0);
            using (var b = new SolidBrush(Theme.UsageColor(_pct))) g.FillRectangle(b, 0, 0, w, r.Height + 1);
            using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, r);
        }
    }
}
