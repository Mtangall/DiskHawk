using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

using DiskHawk.Core;

namespace DiskHawk.App
{
    /// <summary>Dialog that imports a computer list from Active Directory.</summary>
    internal sealed class AdForm : DarkForm
    {
        private readonly TextBox _pattern, _ou;
        private readonly CheckBox _enabledOnly;
        private readonly NumericUpDown _days;
        private readonly Label _result;
        private readonly Button _ok, _query;
        private List<string> _names = new List<string>();

        public List<string> Names { get { return _names; } }

        public AdForm()
        {
            Text = L.T("Active Directory'den makine getir");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Dpi.S(560), Dpi.S(250));

            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(Dpi.S(12)),
                BackColor = Theme.Back
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _pattern = Theme.TextBox(380);
            _pattern.Text = "*";
            _ou = Theme.TextBox(380);
            _enabledOnly = Theme.Check(L.T("Devre dışı hesapları atla"), true);
            _days = Theme.Num(0, 3650, 30, 70);
            _result = Theme.Lbl("", Theme.Dim);

            t.Controls.Add(Theme.Lbl(L.T("İsim filtresi:")), 0, 0);
            t.Controls.Add(_pattern, 1, 0);
            t.Controls.Add(Theme.Lbl(L.T("OU (isteğe bağlı):")), 0, 1);
            t.Controls.Add(_ou, 1, 1);
            var dayRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Back, Margin = new Padding(0) };
            dayRow.Controls.Add(_days);
            dayRow.Controls.Add(Theme.Lbl(L.T("gün içinde oturum açmış olanlar (0 = hepsi)"), Theme.Dim));
            t.Controls.Add(Theme.Lbl(L.T("Son aktiflik:")), 0, 2);
            t.Controls.Add(dayRow, 1, 2);
            t.Controls.Add(new Label(), 0, 3);
            t.Controls.Add(_enabledOnly, 1, 3);
            t.Controls.Add(new Label(), 0, 4);
            t.Controls.Add(Theme.Lbl(L.T("Örnek filtre: IST-*  |  OU örneği: OU=Istanbul,OU=Computers,DC=contoso,DC=local"), Theme.Dim, Theme.SmallFont), 1, 4);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Panel, Padding = new Padding(Dpi.S(8)) };
            var cancel = new IconButton(null, L.T("Vazgeç"), (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
            _ok = new IconButton(null, L.T("Listeye ekle"), (s, e) => { DialogResult = DialogResult.OK; Close(); }, true);
            _ok.Enabled = false;
            _query = new IconButton(null, L.T("Sorgula"), (s, e) => RunQuery());
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_ok);
            buttons.Controls.Add(_query);
            buttons.Controls.Add(_result);

            Controls.Add(t);
            Controls.Add(buttons);
            AcceptButton = _query;
            CancelButton = cancel;
        }

        private void RunQuery()
        {
            string pattern = string.IsNullOrWhiteSpace(_pattern.Text) ? "*" : _pattern.Text.Trim();
            string ou = _ou.Text.Trim();
            if (ou.StartsWith("LDAP://", StringComparison.OrdinalIgnoreCase)) ou = ou.Substring(7);
            // distinguished name only: with "server/..." the query and credentials would go to another server
            if (ou.Length > 0 && !IsDistinguishedName(ou))
            {
                Ui.Error(this, L.T("OU bir LDAP ayırt edici adı olmalı (örn. OU=Istanbul,OU=Computers,DC=contoso,DC=local); sunucu adı veya kaçışsız '/' içeremez."));
                return;
            }
            bool enabledOnly = _enabledOnly.Checked;
            int days = (int)_days.Value;

            _query.Enabled = false;
            _ok.Enabled = false;
            _result.Text = L.T("Sorgulanıyor...");
            UseWaitCursor = true;

            Task.Run(() => Query(pattern, ou, enabledOnly, days)).ContinueWith(t =>
            {
                UseWaitCursor = false;
                _query.Enabled = true;
                if (t.IsFaulted)
                {
                    _result.Text = "";
                    Ui.Error(this, L.T("AD sorgusu başarısız:\n") + t.Exception.GetBaseException().Message);
                    return;
                }
                _names = t.Result;
                _result.Text = _names.Count + L.T(" makine bulundu");
                _ok.Enabled = _names.Count > 0;
                if (_ok.Enabled) AcceptButton = _ok;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static List<string> Query(string pattern, string ou, bool enabledOnly, int days)
        {
            var filter = "(&(objectCategory=computer)(name=" + Escape(pattern) + ")";
            if (enabledOnly) filter += "(!(userAccountControl:1.2.840.113556.1.4.803:=2))";
            if (days > 0) filter += "(lastLogonTimestamp>=" + DateTime.UtcNow.AddDays(-days).ToFileTimeUtc() + ")";
            filter += ")";

            var names = new List<string>();
            using (var root = string.IsNullOrEmpty(ou) ? new DirectoryEntry() : new DirectoryEntry("LDAP://" + ou))
            using (var ds = new DirectorySearcher(root, filter, new[] { "name" }))
            {
                ds.PageSize = 1000;
                ds.SearchScope = SearchScope.Subtree;
                using (var res = ds.FindAll())
                {
                    foreach (SearchResult r in res)
                        if (r.Properties["name"].Count > 0) names.Add(r.Properties["name"][0].ToString());
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>
        /// Basic DN check: must contain at least one "name=value", no control characters and no unescaped '/'
        /// ('/' separates the server in an ADsPath; a '/' inside a DN is written as "\/"). DN escapes such as "\," are allowed.
        /// </summary>
        private static bool IsDistinguishedName(string dn)
        {
            if (dn.IndexOf('=') <= 0 || PathText.HasControlChars(dn)) return false;
            for (int i = 0; i < dn.Length; i++)
                if (dn[i] == '/' && (i == 0 || dn[i - 1] != '\\')) return false;
            return true;
        }

        /// <summary>LDAP filter escaping; '*' is kept as a wildcard.</summary>
        private static string Escape(string s)
        {
            return s.Replace("\\", "\\5c").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
        }
    }
}
