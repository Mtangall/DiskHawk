using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DiskHawk.Core;

namespace DiskHawk.App
{
    internal sealed class DeleteItem
    {
        public string Path;
        public bool IsDir;
        public long Size;
        public int Files;
        public PathLevel Level;
        public string Reason = "";
    }

    /// <summary>
    /// Permanent deletion confirmation. Critical paths (Windows, Program Files, profile folders, OneDrive...)
    /// are marked red and deleted only if a separate box is ticked. Drive roots are never deleted.
    /// </summary>
    internal sealed class DeleteConfirmForm : DarkForm
    {
        private readonly List<DeleteItem> _items;
        private readonly CheckBox _critCheck;

        public bool IncludeCritical { get { return _critCheck != null && _critCheck.Checked; } }

        public List<DeleteItem> ToDelete
        {
            get
            {
                return _items.Where(i => i.Level == PathLevel.Normal || (i.Level == PathLevel.Critical && IncludeCritical)).ToList();
            }
        }

        public DeleteConfirmForm(string machine, List<DeleteItem> items)
        {
            _items = items;
            int critCount = items.Count(i => i.Level == PathLevel.Critical);
            int blocked = items.Count(i => i.Level == PathLevel.Blocked);

            Text = L.T("Kalıcı silme onayı - ") + machine;
            Size = Dpi.Fit(1000, 580);
            MinimumSize = Dpi.Fit(700, 400);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;

            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Theme.Panel,
                Padding = new Padding(Dpi.S(12), Dpi.S(10), Dpi.S(12), Dpi.S(8))
            };
            top.Controls.Add(Theme.Lbl("\u26A0  " + machine + L.T(" üzerinde KALICI silme"), Theme.Bad, Theme.TitleFont));
            top.Controls.Add(Theme.Lbl(L.T("Aşağıdaki öğeler hedef makinenin kendisinde silinecek. Geri Dönüşüm Kutusu kullanılmaz, geri alınamaz.\n") +
                                       L.T("Açık / kilitli dosyalar atlanır ve sonuçta raporlanır. Her işlem denetim kaydına yazılır."), Theme.Text));

            var list = new DarkListView { Dock = DockStyle.Fill, MultiSelect = true };
            list.AddColumn(L.T("Durum"), 110);
            list.AddColumn(L.T("Tür"), 60);
            list.AddColumn(L.T("Boyut"), 90, HorizontalAlignment.Right);
            list.AddColumn(L.T("Dosyalar"), 80, HorizontalAlignment.Right);
            list.AddColumn(L.T("Yol"), 460);
            list.AddColumn(L.T("Not"), 220);
            foreach (var i in items)
            {
                list.Items.Add(new ListViewItem(new[]
                {
                    i.Level == PathLevel.Blocked ? L.T("Silinmez") : i.Level == PathLevel.Critical ? L.T("KRİTİK") : L.T("Silinecek"),
                    i.IsDir ? L.T("Klasör") : L.T("Dosya"),
                    Fmt.Size(i.Size),
                    Fmt.Count(i.Files),
                    i.Path,
                    i.Reason
                }));
            }
            list.Colors = (idx, col) =>
            {
                if (idx < 0 || idx >= _items.Count) return null;
                var it = _items[idx];
                if (it.Level == PathLevel.Blocked) return Theme.Dim;
                if (it.Level == PathLevel.Critical) return col == 0 || col == 5 ? Theme.Bad : (Color?)Theme.Warn;
                return col == 0 ? (Color?)Theme.Warn : null;
            };

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = true,
                BackColor = Theme.Panel,
                Padding = new Padding(Dpi.S(8))
            };
            var cancel = new IconButton(null, L.T("Vazgeç"), (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
            var ok = new IconButton(null, "", (s, e) => { DialogResult = DialogResult.OK; Close(); }, true);
            ok.BackColor = Theme.Bad;
            ok.FlatAppearance.BorderColor = Theme.Bad;
            ok.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 110, 100);
            var check = Theme.Check(L.T("Bu işlemin geri alınamayacağını anlıyorum"), false);
            var summary = Theme.Lbl("", Theme.Text, Theme.BoldFont);

            if (critCount > 0)
            {
                _critCheck = Theme.Check(critCount + L.T(" KRİTİK sistem/profil yolunu da sil (makine açılmayabilir / kullanıcı verisi gider)"), false);
                _critCheck.ForeColor = Theme.Bad;
            }

            Action refresh = () =>
            {
                var del = ToDelete;
                long total = del.Sum(x => x.Size);
                int skipped = items.Count - del.Count;
                ok.Text = string.Format(L.T("Kalıcı olarak sil  ({0} öğe, {1})"), del.Count, Fmt.Size(total));
                ok.Enabled = check.Checked && del.Count > 0;
                summary.Text = string.Format(L.T("Silinecek: {0} öğe, {1}, ~{2} dosya{3}"), del.Count, Fmt.Size(total),
                    Fmt.Count(del.Sum(x => x.Files)), skipped > 0 ? "   |   " + skipped + L.T(" öğe atlanacak") : "");
            };
            check.CheckedChanged += (s, e) => refresh();
            if (_critCheck != null) _critCheck.CheckedChanged += (s, e) => refresh();
            refresh();

            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);
            bottom.Controls.Add(check);
            if (_critCheck != null) bottom.Controls.Add(_critCheck);
            bottom.Controls.Add(summary);
            if (blocked > 0) bottom.Controls.Add(Theme.Lbl(blocked + L.T(" öğe (sürücü kökü / geçersiz yol) hiçbir durumda silinmez"), Theme.Dim));

            Controls.Add(list);
            Controls.Add(top);
            Controls.Add(bottom);
            CancelButton = cancel;
            Shown += (s, e) => { list.FillLastColumn(); cancel.Focus(); };
        }
    }

    /// <summary>Deletion results.</summary>
    internal sealed class DeleteResultForm : DarkForm
    {
        public DeleteResultForm(string machine, List<DeleteResult> results)
        {
            Text = L.T("Silme sonucu - ") + machine;
            Size = Dpi.Fit(1000, 520);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;

            long freed = results.Sum(r => r.FreedBytes);
            Func<DeleteStatus, int> cnt = st => results.Count(r => r.Status == st);

            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Theme.Panel,
                Padding = new Padding(Dpi.S(12), Dpi.S(10), Dpi.S(12), Dpi.S(8))
            };
            top.Controls.Add(Theme.Lbl(L.T("Boşalan alan: ") + Fmt.Size(freed), Theme.Good, Theme.TitleFont));
            top.Controls.Add(Theme.Lbl(string.Format(L.T("Silindi: {0}   |   Kısmen: {1}   |   Silinemedi: {2}   |   Korunuyor: {3}   |   Zaten yok: {4}"),
                cnt(DeleteStatus.Deleted), cnt(DeleteStatus.Partial), cnt(DeleteStatus.Failed), cnt(DeleteStatus.Protected), cnt(DeleteStatus.NotFound)), Theme.Text));

            var list = new DarkListView { Dock = DockStyle.Fill, MultiSelect = true };
            list.AddColumn(L.T("Sonuç"), 110);
            list.AddColumn(L.T("Boşalan"), 90, HorizontalAlignment.Right);
            list.AddColumn(L.T("Silinen"), 75, HorizontalAlignment.Right);
            list.AddColumn(L.T("Silinemeyen"), 110, HorizontalAlignment.Right);
            list.AddColumn(L.T("Yol"), 420);
            list.AddColumn(L.T("Açıklama"), 250);
            foreach (var r in results)
                list.Items.Add(new ListViewItem(new[]
                {
                    r.StatusText, Fmt.Size(r.FreedBytes), Fmt.Count(r.FilesDeleted), Fmt.Count(r.FilesFailed), r.Path, r.Error
                }));
            list.Colors = (idx, col) =>
            {
                if (idx < 0 || idx >= results.Count || col != 0) return null;
                switch (results[idx].Status)
                {
                    case DeleteStatus.Deleted: return Theme.Good;
                    case DeleteStatus.Partial: return Theme.Warn;
                    case DeleteStatus.NotFound: return Theme.Dim;
                    default: return Theme.Bad;
                }
            };
            var menu = Theme.Menu();
            menu.Item(L.T("Kopyala"), (s, e) =>
            {
                var lines = new List<string>();
                foreach (int i in list.SelectedIndices)
                {
                    var r = results[i];
                    lines.Add(r.StatusText + "\t" + Fmt.Size(r.FreedBytes) + "\t" + r.Path + "\t" + r.Error);
                }
                Ui.CopyText(string.Join(Environment.NewLine, lines));
            });
            list.ContextMenuStrip = menu;

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = Theme.Panel,
                Padding = new Padding(Dpi.S(8))
            };
            var close = new IconButton(null, L.T("Kapat"), (s, e) => Close(), true);
            bottom.Controls.Add(close);
            bottom.Controls.Add(new IconButton(null, L.T("Denetim kaydını göster"), (s, e) => Ui.OpenExplorer(AppPaths.AuditFile, true)));

            Controls.Add(list);
            Controls.Add(top);
            Controls.Add(bottom);
            AcceptButton = close;
            CancelButton = close;
            Shown += (s, e) => list.FillLastColumn();
        }
    }
}
