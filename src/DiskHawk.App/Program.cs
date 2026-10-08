using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

using DiskHawk.Core;

namespace DiskHawk.App
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            // same as dpiAware in the manifest; keeps the UI crisp in builds without a manifest
            try { SetProcessDPIAware(); } catch { }
            AppPaths.Settings = AppSettings.Load(AppPaths.SettingsFile);
            L.Lang = AppPaths.Settings.Language;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowCrash(e.ExceptionObject as Exception);

            // a report passed on the command line opens the report window directly (file association)
            if (args.Length == 1 && args[0].EndsWith(Core.ReportSerializer.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(args[0]))
            {
                Core.ScanReport rep;
                try { rep = Core.ReportSerializer.Load(args[0]); }
                catch (Exception ex)
                {
                    // corrupt / untrusted report: do not crash, tell the user why
                    MessageBox.Show(L.T("Rapor açılamadı:\n") + ex.Message, "DiskHawk", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Application.Run(new ResultForm(rep, args[0]));
                return;
            }
            Application.Run(new MainForm());
        }

        private static int _crashShown;

        private static void ShowCrash(Exception ex)
        {
            if (ex == null) return;
            try
            {
                AppPaths.AppendLog(AppPaths.ErrorLog,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
            if (Interlocked.Exchange(ref _crashShown, 1) == 1) return;
            MessageBox.Show(L.T("Beklenmeyen hata:\n\n") + ex.Message + L.T("\n\nDetay veri klasöründeki error.log dosyasına yazıldı."), "DiskHawk",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Interlocked.Exchange(ref _crashShown, 0);
        }
    }
}
