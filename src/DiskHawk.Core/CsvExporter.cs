using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DiskHawk.Core
{
    /// <summary>
    /// CSV that opens directly in Excel with European regional settings (';' separator, UTF-8 BOM, system decimal mark).
    /// </summary>
    public sealed class CsvWriter : IDisposable
    {
        private readonly StreamWriter _w;
        private readonly CultureInfo _c = CultureInfo.CurrentCulture;
        private bool _first = true;
        public const char Sep = ';';

        public CsvWriter(string path)
        {
            _w = new StreamWriter(path, false, new UTF8Encoding(true));
        }

        /// <summary>Text cell. Scan data (file/folder names) is untrusted: neutralized so it cannot run as a formula.</summary>
        public CsvWriter Cell(string s)
        {
            return Raw(PathText.CsvSafe(s ?? ""));
        }

        // numbers and dates are our own values: not neutralized (no ' in front of negative numbers)
        public CsvWriter Cell(long v) { return Raw(v.ToString(_c)); }
        public CsvWriter Cell(double v) { return Raw(v.ToString(_c)); }
        public CsvWriter Cell(DateTime? v) { return Raw(v.HasValue ? v.Value.ToString("yyyy-MM-dd HH:mm", _c) : ""); }

        private CsvWriter Raw(string s)
        {
            if (!_first) _w.Write(Sep);
            _first = false;
            if (s.IndexOfAny(new[] { Sep, '"', '\n', '\r' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            _w.Write(s);
            return this;
        }

        public void EndRow()
        {
            _w.WriteLine();
            _first = true;
        }

        public void Row(params string[] cells)
        {
            foreach (var c in cells) Cell(c);
            EndRow();
        }

        public void Dispose() { _w.Dispose(); }
    }

    public static class CsvExporter
    {
        /// <summary>Writes folder / file / extension CSVs for all drives of a report. Returns the created file paths.</summary>
        public static List<string> ExportReport(ScanReport rep, string dir, int maxDepth = 6, long minFolderSize = 10L * 1024 * 1024)
        {
            Directory.CreateDirectory(dir);
            var files = new List<string>();
            string baseName = Safe(rep.Machine) + "_" + rep.ScanTimeUtc.ToLocalTime().ToString("yyyyMMdd_HHmm");

            var fFolders = Path.Combine(dir, baseName + L.T("_klasorler.csv"));
            using (var w = new CsvWriter(fFolders))
            {
                w.Row(L.T("Makine"), L.T("Klasör"), L.T("Derinlik"), L.T("Boyut (MB)"), L.T("Dosya"), L.T("Alt klasör"), L.T("Klasördeki dosyalar (MB)"), L.T("Son değişiklik"), L.T("Not"));
                foreach (var d in rep.Drives)
                {
                    if (d.Root == null) continue;
                    foreach (var n in Analysis.Flatten(d.Root, maxDepth, minFolderSize))
                    {
                        w.Cell(rep.Machine).Cell(n.FullPath).Cell(n.Depth).Cell(Fmt.Mb(n.Size)).Cell(n.Files)
                         .Cell(n.Dirs).Cell(Fmt.Mb(n.OwnSize)).Cell(n.LastWriteLocal)
                         .Cell(n.IsAccessDenied ? L.T("Erişim yok") : "");
                        w.EndRow();
                    }
                }
            }
            files.Add(fFolders);

            var fFiles = Path.Combine(dir, baseName + L.T("_dosyalar.csv"));
            using (var w = new CsvWriter(fFiles))
            {
                w.Row(L.T("Makine"), L.T("Dosya"), L.T("Klasör"), L.T("Boyut (MB)"), L.T("Son değişiklik"));
                foreach (var d in rep.Drives)
                    foreach (var f in d.LargestFiles)
                    {
                        w.Cell(rep.Machine).Cell(f.FileName).Cell(f.Directory).Cell(Fmt.Mb(f.Size)).Cell(f.LastWriteLocal);
                        w.EndRow();
                    }
            }
            files.Add(fFiles);

            var fExt = Path.Combine(dir, baseName + L.T("_uzantilar.csv"));
            using (var w = new CsvWriter(fExt))
            {
                w.Row(L.T("Makine"), L.T("Sürücü"), L.T("Uzantı"), L.T("Boyut (MB)"), L.T("Dosya sayısı"));
                foreach (var d in rep.Drives)
                    foreach (var e in d.Extensions)
                    {
                        w.Cell(rep.Machine).Cell(d.RootPath).Cell(e.Ext.Length == 0 ? L.T("(uzantısız)") : e.Ext).Cell(Fmt.Mb(e.Size)).Cell(e.Count);
                        w.EndRow();
                    }
            }
            files.Add(fExt);
            return files;
        }

        public static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return L.T("rapor");
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
