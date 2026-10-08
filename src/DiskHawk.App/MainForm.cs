using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DiskHawk.Core;

namespace DiskHawk.App
{
    /// <summary>Central console: machine list, parallel remote scanning, fleet summary.</summary>
    internal sealed class MainForm : DarkForm
    {
        // --- data
        private readonly List<MachineEntry> _all = new List<MachineEntry>();
        private readonly Dictionary<string, MachineEntry> _byName = new Dictionary<string, MachineEntry>(StringComparer.OrdinalIgnoreCase);
        private List<MachineEntry> _view = new List<MachineEntry>();
        private readonly ConcurrentQueue<MachineEntry> _queue = new ConcurrentQueue<MachineEntry>();
        private readonly ConcurrentQueue<string> _logQ = new ConcurrentQueue<string>();
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private int _running;
        private volatile bool _dirty, _viewDirty;
        private int _sortCol;
        private bool _sortDesc;

        // --- paths
        private readonly string _dataDir, _resultsDir, _logFile, _scannerExe;
        private readonly bool _isAdmin;

        // --- local scan
        private DiskScanner _localScanner;

        // --- UI
        private DarkListView _list;
        private TextBox _addBox, _searchBox, _logBox;
        private ComboBox _stateFilter;
        private Label _status, _localStatus;
        private StatCard _cardAll, _cardScanned, _cardCritical, _cardWarn, _cardFailed, _cardActive;
        private readonly List<StatCard> _cards = new List<StatCard>();
        private SplitContainer _split;
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 400 };

        public MainForm()
        {

            _dataDir = AppPaths.DataDir;
            _resultsDir = AppPaths.ResultsDir;
            Directory.CreateDirectory(_resultsDir);
            _logFile = AppPaths.LogFile;
            _scannerExe = AppPaths.ScannerExe;
            _isAdmin = IsAdmin();

            Text = Brand.Name + " - " + Brand.Tagline + "  v" + DiskScanner.Version + (_isAdmin ? "" : "   " + L.T("(yönetici değil)"));
            Size = Dpi.Fit(1500, 900);
            MinimumSize = Dpi.Fit(900, 560);
            if (Size.Width >= Screen.PrimaryScreen.WorkingArea.Width - Dpi.S(40)) WindowState = FormWindowState.Maximized;
            KeyPreview = true;

            BuildUi();
            LoadMachineList();

            _timer.Tick += (s, e) => OnTick();
            _timer.Start();

            Log(L.T("Hazır. Veri klasörü: ") + _dataDir);
            // integrity check: scanner hash and application folder permissions (warn once if there is a problem)
            Task.Run(() => AppIntegrity.StartupCheck()).ContinueWith(t =>
            {
                if (t.IsFaulted || t.Result.Count == 0) return;
                foreach (var p in t.Result) Log(L.T("GÜVENLİK UYARISI: ") + p.Replace("\n", " "));
                Ui.Error(this, L.T("Güvenlik uyarısı:\n\n") + string.Join("\n\n", t.Result));
            }, TaskScheduler.FromCurrentSynchronizationContext());
            if (!File.Exists(_scannerExe)) Log(L.T("UYARI: ") + _scannerExe + L.T(" bulunamadı - uzak tarama yapılamaz."));
            if (!_isAdmin) Log(L.T("UYARI: Uygulama yönetici olarak çalışmıyor; yerel taramada bazı klasörler okunamayabilir."));
        }

        // ================================================================== UI

        private void BuildUi()
        {
            // --- header (brand)
            var header = new AppHeader(Brand.Tagline);
            header.Actions.Controls.Add(new IconButton(Glyph.Settings, L.T("Ayarlar"), (s, e) => ShowSettings()));
            header.Actions.Controls.Add(new IconButton(Glyph.Info, "", (s, e) => { using (var f = new AboutForm()) f.ShowDialog(this); }, false, L.T("Hakkında")));

            // --- actions
            var bar = Theme.Toolbar();
            bar.Padding = new Padding(Dpi.S(8), Dpi.S(4), Dpi.S(8), Dpi.S(4));
            _addBox = Theme.TextBox(250);
            _addBox.Margin = new Padding(Dpi.S(3), Dpi.S(8), Dpi.S(3), Dpi.S(4));
            _addBox.KeyDown += AddBox_KeyDown;
            Theme.SetCue(_addBox, L.T("Makine adı yazın ya da liste yapıştırın"));
            bar.Controls.Add(_addBox);
            bar.Controls.Add(new IconButton(Glyph.Add, L.T("Ekle"), (s, e) => AddFromTextBox()));
            bar.Controls.Add(new IconButton(Glyph.Import, L.T("İçe aktar"), (s, e) => LoadListFile(), false, L.T("TXT / CSV dosyasından makine listesi")));
            bar.Controls.Add(new IconButton(Glyph.People, "Active Directory", (s, e) => AddFromAd(), false, L.T("Active Directory'den makine getir")));
            bar.Controls.Add(Theme.Separator());
            bar.Controls.Add(new IconButton(Glyph.Play, L.T("Seçilileri tara"), (s, e) => Enqueue(SelectedEntries()), true, "F5"));
            bar.Controls.Add(new IconButton(Glyph.Play, L.T("Görünenleri tara"), (s, e) => Enqueue(_view)));
            bar.Controls.Add(new IconButton(Glyph.Refresh, L.T("Hatalıları tekrar dene"), (s, e) => Enqueue(_all.Where(m => m.State == MachineState.Failed || m.State == MachineState.Cancelled))));
            bar.Controls.Add(new IconButton(Glyph.Stop, L.T("Durdur"), (s, e) => StopAll()));
            bar.Controls.Add(Theme.Separator());
            bar.Controls.Add(new IconButton(Glyph.Computer, L.T("Bu bilgisayar"), (s, e) => ScanLocal(), false, L.T("Bu bilgisayarı tara")));
            bar.Controls.Add(new IconButton(Glyph.Document, L.T("Rapor aç"), (s, e) => OpenReportDialog()));
            bar.Controls.Add(new IconButton(Glyph.Save, L.T("Özet CSV"), (s, e) => ExportFleetCsv(), false, L.T("Görünen makinelerin özetini CSV olarak kaydet")));

            // --- summary cards (click to filter)
            var cards = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                BackColor = Theme.Back,
                Padding = new Padding(Dpi.S(8), Dpi.S(4), Dpi.S(8), 0)
            };
            _cardAll = Card(cards, L.T("Makineler"), Theme.Accent, 0);
            _cardScanned = Card(cards, L.T("Raporu olan"), Theme.Good, 1);
            _cardCritical = Card(cards, L.T("Kritik (%90+ dolu)"), Theme.Bad, 5);
            _cardWarn = Card(cards, L.T("Uyarı (%80+ dolu)"), Theme.Warn, 6);
            _cardFailed = Card(cards, L.T("Başarısız"), Color.FromArgb(200, 110, 210), 2);
            _cardActive = Card(cards, L.T("Çalışan / kuyrukta"), RunColor, 3);

            // --- search and filter
            var fbar = Theme.Toolbar();
            fbar.BackColor = Theme.Back;
            fbar.WrapContents = false;
            fbar.Padding = new Padding(Dpi.S(10), Dpi.S(2), Dpi.S(8), Dpi.S(2));
            if (Glyph.Font != null)
                fbar.Controls.Add(new Label { Text = Glyph.Search, Font = Glyph.Font, ForeColor = Theme.Dim, AutoSize = true, Margin = new Padding(0, Dpi.S(8), Dpi.S(2), 0) });
            _searchBox = Theme.TextBox(300);
            Theme.SetCue(_searchBox, L.T("Makine, mesaj ya da sıcak nokta ara (Ctrl+F)"));
            _searchBox.TextChanged += (s, e) => _viewDirty = true;
            fbar.Controls.Add(_searchBox);
            fbar.Controls.Add(Theme.Lbl(L.T("Durum:"), Theme.Dim));
            _stateFilter = Theme.Combo(170, L.T("Tümü"), L.T("Tamam"), L.T("Hata"), L.T("Çalışan / kuyrukta"), L.T("Taranmamış"), L.T("%90+ dolu"), L.T("%80+ dolu"));
            _stateFilter.SelectedIndexChanged += (s, e) => { _viewDirty = true; SyncCards(); };
            fbar.Controls.Add(_stateFilter);

            // --- list
            _list = new DarkListView { Dock = DockStyle.Fill, VirtualMode = true };
            _list.AddColumn(L.T("Makine"), 150);
            _list.AddColumn(L.T("Durum"), 230);
            _list.AddColumn(L.T("Sürücü"), 60);
            _list.AddColumn(L.T("Boyut"), 80, HorizontalAlignment.Right);
            _list.AddColumn(L.T("Boş"), 80, HorizontalAlignment.Right);
            _list.AddColumn(L.T("Doluluk"), 120);
            _list.AddColumn(L.T("Sıcak nokta (yerin yoğunlaştığı klasör)"), 300);
            _list.AddColumn(L.T("S.N. boyut"), 100, HorizontalAlignment.Right);
            _list.AddColumn(L.T("En büyük dosya"), 220);
            _list.AddColumn(L.T("Son tarama"), 115);
            _list.AddColumn(L.T("Mesaj"), 200);
            _list.SortColumn = 0;
            _list.SortDescending = false;
            _list.RetrieveVirtualItem += List_RetrieveVirtualItem;
            _list.Bars = ListBars;
            _list.Colors = ListColors;
            _list.ColumnClick += (s, e) =>
            {
                if (_sortCol == e.Column) _sortDesc = !_sortDesc;
                else { _sortCol = e.Column; _sortDesc = e.Column >= 3 && e.Column != 6 && e.Column != 10; }
                _list.SortColumn = _sortCol;
                _list.SortDescending = _sortDesc;
                RebuildView();
            };
            _list.DoubleClick += (s, e) => OpenSelectedResult();
            _list.KeyDown += List_KeyDown;

            var menu = Theme.Menu();
            menu.Item(L.T("Sonucu aç"), (s, e) => OpenSelectedResult());
            menu.Item(L.T("Tara"), (s, e) => Enqueue(SelectedEntries()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Item(L.T("C$ paylaşımını aç"), (s, e) => { var m = SelectedEntries().FirstOrDefault(); if (m != null) Ui.OpenExplorer(@"\\" + m.Name + @"\C$"); });
            menu.Item(L.T("Sonuç klasörünü aç"), (s, e) =>
            {
                var m = SelectedEntries().FirstOrDefault();
                if (m == null) return;
                var dir = Path.Combine(_resultsDir, CsvExporter.Safe(m.Name.ToUpperInvariant()));
                Ui.OpenExplorer(Directory.Exists(dir) ? dir : _resultsDir);
            });
            menu.Item(L.T("Makine adlarını kopyala"), (s, e) => Ui.CopyText(string.Join(Environment.NewLine, SelectedEntries().Select(m => m.Name))));
            menu.Item(L.T("Mesajı kopyala"), (s, e) => Ui.CopyText(string.Join(Environment.NewLine, SelectedEntries().Select(m => m.Name + "\t" + m.Message))));
            menu.Items.Add(new ToolStripSeparator());
            menu.Item(L.T("Listeden kaldır"), (s, e) => RemoveSelected());
            _list.ContextMenuStrip = menu;

            // --- log
            _logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.Panel,
                ForeColor = Theme.Dim,
                BorderStyle = BorderStyle.None,
                Font = Theme.MonoFont,
                WordWrap = false
            };
            _logBox.HandleCreated += (s, e) => Theme.DarkScrollbars(_logBox);

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = Theme.Border,
                SplitterWidth = Dpi.S(4),
                FixedPanel = FixedPanel.Panel2
            };
            _split.Panel1.BackColor = Theme.Back;
            _split.Panel2.BackColor = Theme.Panel;
            _split.Panel1.Controls.Add(_list);
            _split.Panel2.Controls.Add(_logBox);

            // --- status bar
            var statusBar = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(30), BackColor = AppHeader.HeaderBack, Padding = new Padding(Dpi.S(4), Dpi.S(2), Dpi.S(8), Dpi.S(2)) };
            var logToggle = new IconButton(Glyph.History, L.T("Etkinlik günlüğü"), (s, e) => _split.Panel2Collapsed = !_split.Panel2Collapsed);
            logToggle.Dock = DockStyle.Left;
            logToggle.Margin = new Padding(0);
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Dim, Font = Theme.UiFont, Padding = new Padding(Dpi.S(10), 0, 0, 0) };
            _localStatus = new Label { Dock = DockStyle.Right, AutoSize = false, Width = Dpi.S(560), TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.AccentHover, Font = Theme.UiFont };
            statusBar.Controls.Add(_status);
            statusBar.Controls.Add(_localStatus);
            statusBar.Controls.Add(logToggle);

            Controls.Add(_split);
            Controls.Add(fbar);
            Controls.Add(cards);
            Controls.Add(bar);
            Controls.Add(header);
            Controls.Add(statusBar);
            SyncCards();
        }

        private StatCard Card(FlowLayoutPanel host, string title, Color accent, int filter)
        {
            var c = new StatCard(title, accent) { Tag = filter };
            c.Click += (s, e) =>
            {
                int f = (int)c.Tag;
                _stateFilter.SelectedIndex = _stateFilter.SelectedIndex == f ? 0 : f;
            };
            host.Controls.Add(c);
            _cards.Add(c);
            return c;
        }

        private void SyncCards()
        {
            foreach (var c in _cards) c.Selected = (int)c.Tag == _stateFilter.SelectedIndex;
        }

        private void ShowSettings()
        {
            using (var f = new SettingsForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                Log(L.T("Ayarlar kaydedildi."));
                if (f.RestartRequested) Application.Restart();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { _split.SplitterDistance = Math.Max(Dpi.S(200), _split.Height - Dpi.S(150)); } catch { }
            _list.FillLastColumn();
            _addBox.Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.KeyCode == Keys.F) { _searchBox.Focus(); _searchBox.SelectAll(); e.Handled = true; }
        }

        private bool _closing, _closeReady;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closeReady && (_running > 0 || !_queue.IsEmpty))
            {
                e.Cancel = true;
                if (_closing) return;
                var r = MessageBox.Show(this, L.T("Devam eden taramalar var. İptal edilip çıkılsın mı?"), "DiskHawk",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                // wait for running tasks so they can stop remote processes and clean up temporary files
                _closing = true;
                StopAll();
                Text += L.T("   - kapatılıyor, uzak taramalar sonlandırılıyor...");
                UseWaitCursor = true;
                Task.Run(() => SpinWait.SpinUntil(() => Volatile.Read(ref _running) == 0, 60000))
                    .ContinueWith(t => { _closeReady = true; Close(); }, TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }
            SaveMachineList();
            FlushLog();
            base.OnFormClosing(e);
        }

        // ================================================================== list

        private void List_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _view.Count) { e.Item = new ListViewItem(new string[11]); return; }
            var m = _view[e.ItemIndex];
            var s = m.Summary;
            var p = s != null ? s.Primary : null;
            string drive = p == null ? "" : p.RootPath + (s.Drives.Count > 1 ? " +" + (s.Drives.Count - 1) : "");
            string largest = p == null || p.LargestFileSize <= 0 ? "" : Path.GetFileName(p.LargestFilePath) + "  (" + Fmt.Size(p.LargestFileSize) + ")";
            e.Item = new ListViewItem(new[]
            {
                m.Name,
                m.StateText,
                drive,
                p != null && p.TotalBytes > 0 ? Fmt.Size(p.TotalBytes) : "",
                p != null && p.TotalBytes > 0 ? Fmt.Size(p.FreeBytes) : "",
                p != null && p.TotalBytes > 0 ? Fmt.Percent(p.UsedPercent) : "",
                p != null ? p.HotspotPath : "",
                p != null ? Fmt.Size(p.HotspotSize) : "",
                largest,
                s != null ? Fmt.Date(s.ScanTimeUtc.ToLocalTime()) : "",
                m.Message
            });
        }

        private bool ListBars(int i, int col, out double v, out Color c)
        {
            v = 0;
            c = Theme.Good;
            if (col != 5 || i < 0 || i >= _view.Count) return false;
            var s = _view[i].Summary;
            var p = s != null ? s.Primary : null;
            if (p == null || p.TotalBytes <= 0) return false;
            v = p.UsedPercent / 100.0;
            c = Color.FromArgb(160, Theme.UsageColor(p.UsedPercent));
            return true;
        }

        private static readonly Color RunColor = Color.FromArgb(86, 156, 214);

        private Color? ListColors(int i, int col)
        {
            if (i < 0 || i >= _view.Count) return null;
            var m = _view[i];
            if (col == 1)
            {
                switch (m.State)
                {
                    case MachineState.Done: return Theme.Good;
                    case MachineState.Failed: return Theme.Bad;
                    case MachineState.Running: return RunColor;
                    case MachineState.Cancelled: return Theme.Warn;
                    default: return Theme.Dim;
                }
            }
            if (col == 10) return m.State == MachineState.Failed ? Theme.Bad : Theme.Dim;
            if (col == 2 || col == 9 || col == 8) return Theme.Dim;
            return null;
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { OpenSelectedResult(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { Enqueue(SelectedEntries()); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.C) { Ui.CopyText(string.Join(Environment.NewLine, SelectedEntries().Select(m => m.Name))); e.Handled = true; }
        }

        private List<MachineEntry> SelectedEntries()
        {
            var res = new List<MachineEntry>();
            foreach (int i in _list.SelectedIndices)
                if (i >= 0 && i < _view.Count) res.Add(_view[i]);
            return res;
        }

        private void RebuildView()
        {
            var selected = new HashSet<MachineEntry>(SelectedEntries());
            string q = _searchBox.Text.Trim();
            int f = _stateFilter.SelectedIndex;
            // sort a snapshot so ordering stays consistent while worker threads change State/Summary
            var keys = _all.Where(m => Match(m, q, f)).Select(m => new SortKey(m)).ToList();
            Comparison<SortKey> cmp = Comparer(_sortCol);
            bool desc = _sortDesc;
            keys.Sort((a, b) =>
            {
                int r = desc ? cmp(b, a) : cmp(a, b);
                return r != 0 ? r : string.Compare(a.M.Name, b.M.Name, StringComparison.OrdinalIgnoreCase);
            });
            var v = keys.Select(k => k.M).ToList();

            _view = v;
            _list.BeginUpdate();
            _list.SelectedIndices.Clear();
            _list.VirtualListSize = v.Count;
            if (selected.Count > 0 && selected.Count < 2000)
                for (int i = 0; i < v.Count; i++)
                    if (selected.Contains(v[i])) _list.SelectedIndices.Add(i);
            _list.EndUpdate();
            _list.Invalidate();
            _viewDirty = false;
        }

        private static bool Match(MachineEntry m, string q, int filter)
        {
            var p = m.Summary != null ? m.Summary.Primary : null;
            if (q.Length > 0)
            {
                bool hit = m.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                           || (m.Message ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                           || (p != null && (p.HotspotPath ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!hit) return false;
            }
            switch (filter)
            {
                case 1: return m.Summary != null && m.State != MachineState.Failed && m.State != MachineState.Running;
                case 2: return m.State == MachineState.Failed;
                case 3: return m.State == MachineState.Running || m.State == MachineState.Queued;
                case 4: return m.Summary == null && m.State != MachineState.Running && m.State != MachineState.Queued;
                case 5: return p != null && p.UsedPercent >= 90;
                case 6: return p != null && p.UsedPercent >= 80;
                default: return true;
            }
        }

        private sealed class SortKey
        {
            public readonly MachineEntry M;
            public readonly int State;
            public readonly ReportSummary S;
            public readonly DriveSummary P;
            public readonly string Msg;

            public SortKey(MachineEntry m)
            {
                M = m;
                State = (int)m.State;
                S = m.Summary;
                P = S != null ? S.Primary : null;
                Msg = m.Message ?? "";
            }
        }

        private static Comparison<SortKey> Comparer(int col)
        {
            const StringComparison ic = StringComparison.OrdinalIgnoreCase;
            switch (col)
            {
                case 1: return (a, b) => a.State.CompareTo(b.State);
                case 2: return (a, b) => string.Compare(a.P != null ? a.P.RootPath : "", b.P != null ? b.P.RootPath : "", ic);
                case 3: return (a, b) => (a.P != null ? a.P.TotalBytes : -1).CompareTo(b.P != null ? b.P.TotalBytes : -1);
                case 4: return (a, b) => (a.P != null ? a.P.FreeBytes : -1).CompareTo(b.P != null ? b.P.FreeBytes : -1);
                case 5: return (a, b) => (a.P != null ? a.P.UsedPercent : -1).CompareTo(b.P != null ? b.P.UsedPercent : -1);
                case 6: return (a, b) => string.Compare(a.P != null ? a.P.HotspotPath : "", b.P != null ? b.P.HotspotPath : "", ic);
                case 7: return (a, b) => (a.P != null ? a.P.HotspotSize : -1).CompareTo(b.P != null ? b.P.HotspotSize : -1);
                case 8: return (a, b) => (a.P != null ? a.P.LargestFileSize : -1).CompareTo(b.P != null ? b.P.LargestFileSize : -1);
                case 9: return (a, b) => (a.S != null ? a.S.ScanTimeUtc : DateTime.MinValue).CompareTo(b.S != null ? b.S.ScanTimeUtc : DateTime.MinValue);
                case 10: return (a, b) => string.Compare(a.Msg, b.Msg, ic);
                default: return (a, b) => string.Compare(a.M.Name, b.M.Name, ic);
            }
        }

        // ================================================================== adding / removing machines

        private static readonly char[] Splitters = { ' ', '\t', '\r', '\n', ',', ';' };
        private static readonly HashSet<string> HeaderWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "name", "makine", "computername", "computer", "hostname", "bilgisayar" };

        private void AddBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { AddFromTextBox(); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.V)
            {
                // multi-line paste: add directly to the list
                string clip = null;
                try { clip = Clipboard.GetText(); } catch { }
                if (clip != null && clip.IndexOfAny(new[] { '\n', '\r' }) >= 0)
                {
                    int n = AddMachines(clip.Split(Splitters, StringSplitOptions.RemoveEmptyEntries));
                    Log(L.T("Panodan ") + n + L.T(" makine eklendi."));
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            }
        }

        private void AddFromTextBox()
        {
            var parts = _addBox.Text.Split(Splitters, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            int n = AddMachines(parts);
            _addBox.Clear();
            if (n > 1) Log(n + L.T(" makine eklendi."));
        }

        private int AddMachines(IEnumerable<string> names)
        {
            var added = new List<MachineEntry>();
            var invalid = new List<string>();
            foreach (var raw in names)
            {
                var name = raw.Trim().Trim('"', '\'').TrimStart('\\').ToUpperInvariant();
                if (name.Length == 0 || HeaderWords.Contains(name) || _byName.ContainsKey(name)) continue;
                // the machine name is used in UNC paths, the WMI address and the report folder name: valid names / IPv4 only
                if (!PathText.IsValidMachineName(name))
                {
                    if (invalid.Count < 5) invalid.Add(PathText.SafeText(name, 60));
                    else if (invalid.Count == 5) invalid.Add("...");
                    continue;
                }
                var m = new MachineEntry { Name = name };
                _all.Add(m);
                _byName[name] = m;
                added.Add(m);
            }
            if (invalid.Count > 0)
                Log(L.T("Geçersiz makine adları atlandı (boşluk veya \\ / : * ? \" < > | ; , içeremez): ") + string.Join(", ", invalid));
            if (added.Count > 0)
            {
                _viewDirty = true;
                Task.Run(() =>
                {
                    foreach (var m in added) LoadLatestSummary(m);
                    _viewDirty = true;
                });
            }
            return added.Count;
        }

        private void LoadListFile()
        {
            using (var dlg = new OpenFileDialog { Filter = L.T("Metin / CSV (*.txt;*.csv)|*.txt;*.csv|Tüm dosyalar (*.*)|*.*"), Title = L.T("Makine listesi") })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var names = File.ReadAllLines(dlg.FileName)
                        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
                        .Select(l => l.Split(Splitters, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                        .Where(x => x != null);
                    int n = AddMachines(names);
                    Log(Path.GetFileName(dlg.FileName) + ": " + n + L.T(" makine eklendi."));
                }
                catch (Exception ex) { Ui.Error(this, ex.Message); }
            }
        }

        private void AddFromAd()
        {
            using (var f = new AdForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                int n = AddMachines(f.Names);
                Log(L.T("AD: ") + f.Names.Count + L.T(" makine bulundu, ") + n + L.T(" yeni eklendi."));
            }
        }

        private void RemoveSelected()
        {
            var sel = SelectedEntries().Where(m => m.State != MachineState.Running && m.State != MachineState.Queued).ToList();
            if (sel.Count == 0) return;
            if (sel.Count > 1 && MessageBox.Show(this, sel.Count + L.T(" makine listeden kaldırılsın mı? (rapor dosyaları silinmez)"), "DiskHawk",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var set = new HashSet<MachineEntry>(sel);
            _all.RemoveAll(set.Contains);
            foreach (var m in sel) _byName.Remove(m.Name);
            RebuildView();
        }

        private void LoadLatestSummary(MachineEntry m)
        {
            try
            {
                var dir = Path.Combine(_resultsDir, CsvExporter.Safe(m.Name.ToUpperInvariant()));
                if (!Directory.Exists(dir)) return;
                var di = new DirectoryInfo(dir);
                var latest = di.GetFiles("*" + ReportSerializer.Extension)
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (latest == null) return;
                m.Summary = ReportSerializer.ReadSummary(latest.FullName);
                m.ResultFile = latest.FullName;
            }
            catch { /* corrupt file: ignore */ }
        }

        // ================================================================== scan queue

        private void Enqueue(IEnumerable<MachineEntry> entries)
        {
            if (!File.Exists(_scannerExe))
            {
                Ui.Error(this, L.T("Tarayıcı bulunamadı:\n") + _scannerExe + L.T("\n\nDiskHawk.Scanner.exe bu uygulamayla aynı klasörde olmalı."));
                return;
            }
            if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
            int n = 0;
            foreach (var m in entries.ToList())
            {
                if (m.State == MachineState.Running || m.State == MachineState.Queued) continue;
                m.State = MachineState.Queued;
                m.Attempts = 0;
                m.Message = "";
                _queue.Enqueue(m);
                n++;
            }
            if (n > 0)
            {
                Log(n + L.T(" makine kuyruğa alındı."));
                _dirty = true;
                if (_stateFilter.SelectedIndex != 0 || _sortCol == 1) _viewDirty = true;
            }
        }

        private void StopAll()
        {
            _cts.Cancel();
            int n = 0;
            MachineEntry m;
            while (_queue.TryDequeue(out m))
            {
                if (m.State == MachineState.Queued) { m.State = MachineState.Cancelled; m.Message = L.T("İptal edildi"); n++; }
            }
            Log(L.T("Durduruluyor... (") + n + L.T(" kuyruktaki iş iptal, çalışanlar sonlandırılıyor)"));
            _dirty = true;
            _viewDirty = true;
        }

        private void Dispatch()
        {
            if (_closing) return;
            int limit = AppPaths.Settings.Parallel;
            MachineEntry m;
            while (Volatile.Read(ref _running) < limit && _queue.TryDequeue(out m))
            {
                if (m.State != MachineState.Queued) continue;
                StartOne(m);
            }
        }

        private void StartOne(MachineEntry m)
        {
            Interlocked.Increment(ref _running);
            m.State = MachineState.Running;
            m.Phase = L.T("Başlıyor");
            m.StartedAt = DateTime.Now;
            m.Attempts++;
            m.Message = "";

            var settings = AppPaths.RemoteSettings();
            var token = _cts.Token;

            Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    new RemoteScanner(settings, Log).Scan(m, token);
                    m.State = MachineState.Done;
                    m.Message = "";
                }
                catch (OperationCanceledException)
                {
                    m.State = MachineState.Cancelled;
                    m.Message = L.T("İptal edildi");
                }
                catch (ScanFailure f)
                {
                    if (f.Retryable && m.Attempts <= AppPaths.Settings.Retries && !token.IsCancellationRequested)
                    {
                        m.State = MachineState.Queued;
                        m.Message = f.Message + L.T("  (tekrar denenecek)");
                        Log(m.Name + ": " + f.Message + L.T(" - tekrar denenecek"));
                        _queue.Enqueue(m);
                    }
                    else
                    {
                        m.State = MachineState.Failed;
                        m.Message = f.Message;
                        Log(m.Name + L.T(": HATA - ") + f.Message);
                    }
                }
                catch (Exception ex)
                {
                    m.State = MachineState.Failed;
                    m.Message = ex.Message;
                    Log(m.Name + L.T(": HATA - ") + ex);
                }
                finally
                {
                    m.LastRunMs = sw.ElapsedMilliseconds;
                    m.Phase = "";
                    Interlocked.Decrement(ref _running);
                    _dirty = true;
                    _viewDirty = true;
                }
            });
        }

        // ================================================================== local scan

        private void ScanLocal()
        {
            if (_localScanner != null) { Ui.Info(this, L.T("Yerel tarama zaten çalışıyor.")); return; }
            var roots = DiskScanner.GetFixedDrives();
            if (roots.Count == 0) { Ui.Error(this, L.T("Sabit disk bulunamadı.")); return; }

            var scanner = new DiskScanner(new ScanOptions { LargeFileThreshold = (long)AppPaths.Settings.MinFileMb * 1024 * 1024 });
            _localScanner = scanner;
            Log(L.T("Yerel tarama başladı: ") + string.Join(", ", roots));
            var name = Environment.MachineName.ToUpperInvariant();

            Task.Run(() =>
            {
                var rep = scanner.ScanAll(roots, CancellationToken.None);
                var dir = Path.Combine(_resultsDir, CsvExporter.Safe(name));
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, CsvExporter.Safe(name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ReportSerializer.Extension);
                ReportSerializer.Save(rep, file);
                return Tuple.Create(rep, file);
            }).ContinueWith(t =>
            {
                _localScanner = null;
                _localStatus.Text = "";
                if (t.IsFaulted)
                {
                    var msg = t.Exception.GetBaseException().Message;
                    Log(L.T("Yerel tarama hatası: ") + msg);
                    Ui.Error(this, L.T("Yerel tarama başarısız:\n") + msg);
                    return;
                }
                var rep = t.Result.Item1;
                var file = t.Result.Item2;
                foreach (var d in rep.Drives)
                    Log(string.Format(L.T("  {0}: {1} motor, {2}{3}"), d.RootPath, L.T(d.Engine), Fmt.Duration(d.DurationMs),
                        string.IsNullOrEmpty(d.EngineNote) ? "" : d.Engine == "MFT" ? "  [" + d.EngineNote + "]" : L.T("  (MFT kullanılamadı: ") + d.EngineNote + ")"));
                AddMachines(new[] { name });
                MachineEntry m;
                if (_byName.TryGetValue(name, out m))
                {
                    m.Summary = Analysis.BuildSummary(rep);
                    m.ResultFile = file;
                    m.State = MachineState.Done;
                    m.Message = "";
                }
                var p = m != null ? m.Summary.Primary : null;
                Log(L.T("Yerel tarama bitti: ") + (p != null ? p.RootPath + L.T(" sıcak nokta ") + p.HotspotPath + " (" + Fmt.Size(p.HotspotSize) + ")" : ""));
                _viewDirty = true;
                new ResultForm(rep, file, name, OnReportChanged, Log).Show();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ================================================================== reports

        private void OpenSelectedResult()
        {
            var m = SelectedEntries().FirstOrDefault();
            if (m == null) return;
            if (string.IsNullOrEmpty(m.ResultFile) || !File.Exists(m.ResultFile))
            {
                Ui.Info(this, m.Name + L.T(" için henüz rapor yok. Önce tarayın."));
                return;
            }
            ResultForm.OpenFile(this, m.ResultFile, Log, m.Name, OnReportChanged);
        }

        private void OpenReportDialog()
        {
            using (var dlg = new OpenFileDialog { Filter = "DiskHawk (*.dhr)|*.dhr", InitialDirectory = _resultsDir })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) ResultForm.OpenFile(this, dlg.FileName, Log, null, OnReportChanged);
            }
        }

        private void ExportFleetCsv()
        {
            using (var dlg = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv",
                FileName = L.T("disk_ozet_") + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    int rows = 0;
                    using (var w = new CsvWriter(dlg.FileName))
                    {
                        w.Row(L.T("Makine"), L.T("Durum"), L.T("Sürücü"), L.T("Toplam (GB)"), L.T("Boş (GB)"), L.T("Doluluk (%)"), L.T("Taranan (GB)"), L.T("Dosya sayısı"),
                              L.T("Sıcak nokta"), L.T("Sıcak nokta (GB)"), L.T("En çok dosya olan klasör"), L.T("Dosyalar"), L.T("En büyük dosya"), L.T("En büyük dosya (MB)"),
                              L.T("Bulutta (GB)"), L.T("Erişilemeyen klasör"), L.T("Son tarama"), L.T("Mesaj"));
                        foreach (var m in _view)
                        {
                            if (m.Summary == null || m.Summary.Drives.Count == 0)
                            {
                                w.Cell(m.Name).Cell(m.StateText);
                                for (int i = 0; i < 15; i++) w.Cell("");
                                w.Cell(m.Message).EndRow();
                                rows++;
                                continue;
                            }
                            foreach (var d in m.Summary.Drives)
                            {
                                w.Cell(m.Name).Cell(m.StateText).Cell(d.RootPath)
                                 .Cell(Fmt.Gb(d.TotalBytes)).Cell(Fmt.Gb(d.FreeBytes)).Cell(Math.Round(d.UsedPercent, 1))
                                 .Cell(Fmt.Gb(d.ScannedBytes)).Cell(d.ScannedFiles)
                                 .Cell(d.HotspotPath).Cell(Fmt.Gb(d.HotspotSize))
                                 .Cell(d.FileHotspotPath).Cell(d.FileHotspotCount)
                                 .Cell(d.LargestFilePath).Cell(Fmt.Mb(d.LargestFileSize))
                                 .Cell(Fmt.Gb(d.CloudOnlyBytes)).Cell(d.Errors)
                                 .Cell((DateTime?)m.Summary.ScanTimeUtc.ToLocalTime())
                                 .Cell(m.Message).EndRow();
                                rows++;
                            }
                        }
                    }
                    Log(L.T("Özet CSV yazıldı: ") + dlg.FileName + " (" + rows + L.T(" satır)"));
                    Ui.OpenExplorer(dlg.FileName, true);
                }
                catch (Exception ex) { Ui.Error(this, ex.Message); }
            }
        }

        // ================================================================== timer / log

        /// <summary>A deletion in the result window updates the report file; refresh the summary in the list.</summary>
        private void OnReportChanged(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            foreach (var m in _all)
            {
                if (!string.Equals(m.ResultFile, file, StringComparison.OrdinalIgnoreCase)) continue;
                try { m.Summary = ReportSerializer.ReadSummary(file); } catch { }
            }
            _viewDirty = true;
        }

        private void OnTick()
        {
            FlushLog();
            Dispatch();

            if (_viewDirty) RebuildView();
            else if (_dirty || _running > 0) _list.Invalidate();
            _dirty = false;

            int done = 0, failed = 0, queued = 0, withData = 0, critical = 0, warn = 0;
            foreach (var m in _all)
            {
                switch (m.State)
                {
                    case MachineState.Done: done++; break;
                    case MachineState.Failed: failed++; break;
                    case MachineState.Queued: queued++; break;
                }
                var sm = m.Summary;
                if (sm != null)
                {
                    withData++;
                    var p = sm.Primary;
                    if (p != null && p.TotalBytes > 0)
                    {
                        if (p.UsedPercent >= 90) critical++;
                        if (p.UsedPercent >= 80) warn++;
                    }
                }
            }
            _cardAll.Value = Fmt.Count(_all.Count);
            _cardScanned.Value = Fmt.Count(withData);
            _cardCritical.Value = Fmt.Count(critical);
            _cardWarn.Value = Fmt.Count(warn);
            _cardFailed.Value = Fmt.Count(failed);
            _cardActive.Value = _running + (queued > 0 ? "  /  " + Fmt.Count(queued) : "");
            var st = AppPaths.Settings;
            _status.Text = L.F("{0} / {1} makine gösteriliyor   |   Bu oturumda: {2} tamam, {3} hata   |   Paralel {4}, zaman aşımı {5} dk",
                Fmt.Count(_view.Count), Fmt.Count(_all.Count), done, failed, st.Parallel, st.TimeoutMinutes);

            var ls = _localScanner;
            if (ls != null)
            {
                var p = ls.Progress;
                _localStatus.Text = string.Format(L.T("Yerel tarama: {0}  {1} dosya  {2}  {3}"),
                    p.RootPath, Fmt.Count(Interlocked.Read(ref p.Files)), Fmt.Size(Interlocked.Read(ref p.Bytes)), Fmt.Duration(p.Watch.ElapsedMilliseconds));
            }
        }

        private void Log(string msg)
        {
            _logQ.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + msg);
        }

        private void FlushLog()
        {
            if (_logQ.IsEmpty) return;
            var sb = new StringBuilder();
            string line;
            while (_logQ.TryDequeue(out line)) sb.AppendLine(line);
            var text = sb.ToString();
            if (_logBox.TextLength > 300000) _logBox.Text = _logBox.Text.Substring(_logBox.TextLength - 150000);
            _logBox.AppendText(text);
            AppPaths.AppendLog(_logFile, text);
        }

        // ================================================================== settings

        private static bool IsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private string ListFile { get { return Path.Combine(_dataDir, "machines.txt"); } }

        private void LoadMachineList()
        {
            try
            {
                if (File.Exists(ListFile)) AddMachines(File.ReadAllLines(ListFile));
            }
            catch { }
        }

        private void SaveMachineList()
        {
            try { File.WriteAllLines(ListFile, _all.Select(m => m.Name)); } catch { }
        }
    }
}
