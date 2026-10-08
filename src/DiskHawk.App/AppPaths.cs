using System;
using System.IO;
using System.Text;
using DiskHawk.Core;

namespace DiskHawk.App
{
    /// <summary>Application-wide paths and the deletion audit log.</summary>
    internal static class AppPaths
    {
        public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string DataDir = PickDataDir(AppDir);
        public static readonly string ResultsDir = Path.Combine(DataDir, "Reports");
        public static readonly string LogFile = Path.Combine(DataDir, "DiskHawk.log");
        public static readonly string AuditFile = Path.Combine(DataDir, "deletion_audit.csv");
        public static readonly string ErrorLog = Path.Combine(DataDir, "error.log");

        /// <summary>When a log file exceeds this size it is kept as .1 (single backup) and a new file is started.</summary>
        public const long MaxLogBytes = 10L * 1024 * 1024;

        private static readonly object LogLock = new object();

        /// <summary>Appends to a log; if the file exceeds MaxLogBytes it is first rotated to "&lt;name&gt;.1".</summary>
        public static void AppendLog(string file, string text)
        {
            lock (LogLock)
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > MaxLogBytes)
                    {
                        var old = file + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(file, old);
                    }
                    File.AppendAllText(file, text, new UTF8Encoding(false));
                }
                catch { /* the application keeps running if the log cannot be written */ }
            }
        }
        public static readonly string ScannerExe = Path.Combine(AppDir, "DiskHawk.Scanner.exe");

        public static readonly string SettingsFile = Path.Combine(DataDir, "settings.ini");

        /// <summary>Application settings (loaded by Program.Main at startup).</summary>
        public static AppSettings Settings = new AppSettings();

        /// <summary>
        /// Data folder (settings, machine list, reports, logs). Portable use: the application folder if it is writable.
        /// Installed under Program Files (MSI): always %LOCALAPPDATA%\DiskHawk, even when running elevated,
        /// so nothing is ever written into the installation folder.
        /// </summary>
        private static string PickDataDir(string appDir)
        {
            if (!IsUnderProgramFiles(appDir))
            {
                try
                {
                    var probe = Path.Combine(appDir, ".write_test");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                    return appDir;
                }
                catch { /* read-only folder: use the user profile */ }
            }
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskHawk");
            Directory.CreateDirectory(d);
            return d;
        }

        private static bool IsUnderProgramFiles(string dir)
        {
            foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var pf = Environment.GetFolderPath(sf);
                if (!string.IsNullOrEmpty(pf) && dir.StartsWith(pf.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static RemoteScanSettings RemoteSettings()
        {
            return new RemoteScanSettings
            {
                ScannerExe = ScannerExe,
                ResultsDir = ResultsDir,
                PingFirst = Settings.Ping,
                TimeoutMinutes = Settings.TimeoutMinutes,
                MinFileMb = Settings.MinFileMb,
                LowIo = Settings.LowIo
            };
        }

        private static readonly object AuditLock = new object();

        /// <summary>Writes every deletion attempt to the persistent audit log (who, when, where, what, result).</summary>
        public static void Audit(string machine, DeleteResult r, long expectedSize)
        {
            lock (AuditLock)
            {
                try
                {
                    bool isNew = !File.Exists(AuditFile);
                    using (var sw = new StreamWriter(AuditFile, true, new UTF8Encoding(true)))
                    {
                        if (isNew) sw.WriteLine(L.T("Zaman;Yapan;Konsol;Makine;Tür;Yol;Beklenen (MB);Sonuç;Boşalan (MB);Silinen dosya;Silinemeyen dosya;Açıklama"));
                        sw.WriteLine(string.Join(";", new[]
                        {
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            Environment.UserDomainName + "\\" + Environment.UserName,
                            Environment.MachineName,
                            Q(machine),
                            r.IsDirectory ? L.T("Klasör") : L.T("Dosya"),
                            Q(r.Path),
                            Fmt.Mb(expectedSize).ToString(),
                            r.StatusText,
                            Fmt.Mb(r.FreedBytes).ToString(),
                            r.FilesDeleted.ToString(),
                            r.FilesFailed.ToString(),
                            Q(r.Error)
                        }));
                    }
                }
                catch { /* if the audit log cannot be written the deletion result is still shown */ }
            }
        }

        /// <summary>Audit log cell: no fake rows via line breaks, no Excel formulas.</summary>
        private static string Q(string s)
        {
            s = PathText.CsvSafe(PathText.SafeText(s ?? "", 8192));
            return s.IndexOfAny(new[] { ';', '"' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }

    /// <summary>Persistent application settings (settings.ini, key=value).</summary>
    internal sealed class AppSettings
    {
        public int Parallel = 20;
        public int Retries = 1;
        public int TimeoutMinutes = 30;
        public int MinFileMb = 10;
        public bool Ping = true;
        public bool LowIo;
        public string Language = "en";

        public static AppSettings Load(string file)
        {
            var st = new AppSettings();
            try
            {
                if (!File.Exists(file)) return st;
                foreach (var line in File.ReadAllLines(file))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim().ToLowerInvariant(), v = line.Substring(i + 1).Trim();
                    int n;
                    bool isNum = int.TryParse(v, out n);
                    switch (k)
                    {
                        case "parallel": case "paralel": if (isNum) st.Parallel = Clamp(n, 1, 200); break;
                        case "retries": case "tekrar": if (isNum) st.Retries = Clamp(n, 0, 5); break;
                        case "timeout": case "zamanasimi": if (isNum) st.TimeoutMinutes = Clamp(n, 1, 240); break;
                        case "minfilemb": case "mindosyamb": if (isNum) st.MinFileMb = Clamp(n, 1, 100000); break;
                        case "ping": st.Ping = v == "1"; break;
                        case "lowio": case "dusukio": st.LowIo = v == "1"; break;
                        case "language": st.Language = v == "tr" ? "tr" : "en"; break;
                    }
                }
            }
            catch { }
            return st;
        }

        public void Save(string file)
        {
            try
            {
                File.WriteAllLines(file, new[]
                {
                    "language=" + Language,
                    "parallel=" + Parallel,
                    "retries=" + Retries,
                    "timeout=" + TimeoutMinutes,
                    "minfilemb=" + MinFileMb,
                    "ping=" + (Ping ? "1" : "0"),
                    "lowio=" + (LowIo ? "1" : "0")
                });
            }
            catch { }
        }

        private static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }
    }
}
