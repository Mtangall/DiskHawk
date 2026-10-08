using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using DiskHawk.Core;

namespace DiskHawk.App
{
    /// <summary>Settings dialog: language, scan and remote connection options.</summary>
    internal sealed class SettingsForm : DarkForm
    {
        private readonly ComboBox _lang;
        private readonly NumericUpDown _parallel, _retries, _timeout, _minFile;
        private readonly CheckBox _ping, _lowIo;

        /// <summary>The language changed and the user agreed to restart.</summary>
        public bool RestartRequested { get; private set; }

        public SettingsForm()
        {
            var st = AppPaths.Settings;
            Text = L.T("Ayarlar") + " - " + Brand.Name;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Dpi.S(560), Dpi.S(430));

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(Dpi.S(16), Dpi.S(12), Dpi.S(16), Dpi.S(8)), BackColor = Theme.Back };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;

            Action<string> section = title =>
            {
                var l = Theme.Lbl(title, Theme.AccentHover, Theme.BoldFont);
                l.Margin = new Padding(0, Dpi.S(row == 0 ? 2 : 12), 0, Dpi.S(2));
                t.Controls.Add(l, 0, row);
                t.SetColumnSpan(l, 2);
                row++;
            };
            Action<string, Control, string> field = (label, ctl, hint) =>
            {
                t.Controls.Add(Theme.Lbl(label), 0, row);
                if (hint != null)
                {
                    var fl = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Back, Margin = new Padding(0) };
                    fl.Controls.Add(ctl);
                    fl.Controls.Add(Theme.Lbl(hint, Theme.Dim, Theme.SmallFont));
                    t.Controls.Add(fl, 1, row);
                }
                else t.Controls.Add(ctl, 1, row);
                row++;
            };

            section(L.T("Genel"));
            _lang = Theme.Combo(160, "English", "Türkçe");
            _lang.SelectedIndex = st.Language == "tr" ? 1 : 0;
            field(L.T("Dil:"), _lang, null);

            section(L.T("Uzak tarama"));
            _parallel = Theme.Num(1, 200, st.Parallel, 60);
            field(L.T("Paralel tarama:"), _parallel, L.T("aynı anda taranan makine"));
            _retries = Theme.Num(0, 5, st.Retries, 60);
            field(L.T("Tekrar deneme:"), _retries, L.T("geçici hatalarda"));
            _timeout = Theme.Num(1, 240, st.TimeoutMinutes, 60);
            field(L.T("Zaman aşımı:"), _timeout, L.T("dakika / makine"));
            _ping = Theme.Check(L.T("Önce ping / SMB erişilebilirliğini kontrol et"), st.Ping);
            field("", _ping, null);
            _lowIo = Theme.Check(L.T("Düşük disk önceliği (kullanıcıyı en az etkiler, daha yavaş)"), st.LowIo);
            field("", _lowIo, null);

            section(L.T("Rapor"));
            _minFile = Theme.Num(1, 100000, st.MinFileMb, 70);
            field(L.T("En büyük dosyalar:"), _minFile, L.T("MB ve üstü dosyalar listelenir"));

            section(L.T("Veri klasörü"));
            var dataRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Back, Margin = new Padding(0) };
            var path = Theme.TextBox(360);
            path.Text = AppPaths.DataDir;
            path.ReadOnly = true;
            path.BackColor = Theme.Panel;
            dataRow.Controls.Add(path);
            dataRow.Controls.Add(new IconButton(Glyph.Folder, "", (s, e) => Ui.OpenExplorer(AppPaths.DataDir), false, L.T("Klasörü aç")));
            t.Controls.Add(dataRow, 0, row);
            t.SetColumnSpan(dataRow, 2);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Panel, Padding = new Padding(Dpi.S(8)) };
            var cancel = new IconButton(null, L.T("Vazgeç"), (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
            var ok = new IconButton(null, L.T("Kaydet"), (s, e) => SaveAndClose(), true);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            Controls.Add(t);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void SaveAndClose()
        {
            var st = AppPaths.Settings;
            string newLang = _lang.SelectedIndex == 1 ? "tr" : "en";
            bool langChanged = newLang != st.Language;
            st.Language = newLang;
            st.Parallel = (int)_parallel.Value;
            st.Retries = (int)_retries.Value;
            st.TimeoutMinutes = (int)_timeout.Value;
            st.MinFileMb = (int)_minFile.Value;
            st.Ping = _ping.Checked;
            st.LowIo = _lowIo.Checked;
            st.Save(AppPaths.SettingsFile);

            if (langChanged)
            {
                // ask the question in the new language
                var old = L.Lang;
                L.Lang = newLang;
                var r = MessageBox.Show(this, L.T("Dil değişikliği uygulamayı yeniden başlatınca geçerli olur. Şimdi yeniden başlatılsın mı?"),
                    Brand.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                L.Lang = old;
                RestartRequested = r == DialogResult.Yes;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>About dialog.</summary>
    internal sealed class AboutForm : DarkForm
    {
        public AboutForm()
        {
            Text = L.T("Hakkında") + " - " + Brand.Name;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Dpi.S(520), Dpi.S(330));

            var logo = new LogoBox { Location = new Point(Dpi.S(24), Dpi.S(26)), Size = new Size(Dpi.S(80), Dpi.S(80)) };
            Controls.Add(logo);

            var text = new FlowLayoutPanel
            {
                Location = new Point(Dpi.S(124), Dpi.S(20)),
                Size = new Size(Dpi.S(380), Dpi.S(250)),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Theme.Back
            };
            text.Controls.Add(Theme.Lbl(Brand.Name, Theme.Text, new Font("Segoe UI Semibold", 18F)));
            text.Controls.Add(Theme.Lbl(Brand.Tagline + "  ·  " + L.T("Sürüm") + " " + DiskScanner.Version, Theme.Dim));
            var desc = Theme.Lbl(L.T("Windows ağlarında disk kullanımını hızla analiz eder ve güvenle temizler.") + "\n\n" +
                                 "•  " + L.T("Ham NTFS $MFT motoru: sürücü başına saniyeler") + "\n" +
                                 "•  " + L.T("Ajan gerektirmez: WMI + SMB ile uzak tarama") + "\n" +
                                 "•  " + L.T("Korumalı uzak silme ve denetim kaydı") + "\n" +
                                 "•  " + L.T("Tek konsoldan binlerce makine"), Theme.Text);
            desc.MaximumSize = new Size(Dpi.S(370), 0);
            desc.Margin = new Padding(Dpi.S(4), Dpi.S(12), 0, Dpi.S(8));
            text.Controls.Add(desc);
            var link = new LinkLabel
            {
                Text = Brand.Website,
                AutoSize = true,
                LinkColor = Theme.AccentHover,
                ActiveLinkColor = Color.White,
                Font = Theme.UiFont,
                Margin = new Padding(Dpi.S(4), Dpi.S(4), 0, 0)
            };
            link.LinkClicked += (s, e) => { try { Process.Start("https://" + Brand.Website); } catch { } };
            text.Controls.Add(link);
            text.Controls.Add(Theme.Lbl("© 2026 " + Brand.Name + " contributors · MIT License", Theme.Dim, Theme.SmallFont));
            Controls.Add(text);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Panel, Padding = new Padding(Dpi.S(8)) };
            var close = new IconButton(null, L.T("Kapat"), (s, e) => Close(), true);
            buttons.Controls.Add(close);
            Controls.Add(buttons);
            AcceptButton = close;
            CancelButton = close;
        }
    }
}
