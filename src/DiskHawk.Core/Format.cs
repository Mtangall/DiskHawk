using System;
using System.Globalization;

namespace DiskHawk.Core
{
    public static class Fmt
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>1536 -> "1.5 KB" (according to the system culture).</summary>
        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.CurrentCulture) + " B";
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
            string f = v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00";
            return v.ToString(f, CultureInfo.CurrentCulture) + " " + Units[u];
        }

        public static string Count(long n)
        {
            return n.ToString("N0", CultureInfo.CurrentCulture);
        }

        public static string Percent(double p)
        {
            return p.ToString("0.0", CultureInfo.CurrentCulture) + " %";
        }

        public static string Duration(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            if (t.TotalSeconds < 60) return t.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture) + L.T(" sn");
            if (t.TotalMinutes < 60) return string.Format(L.T("{0} dk {1} sn"), (int)t.TotalMinutes, t.Seconds);
            return string.Format(L.T("{0} sa {1} dk"), (int)t.TotalHours, t.Minutes);
        }

        public static string Date(DateTime? dt)
        {
            return dt.HasValue ? dt.Value.ToString(L.T("dd.MM.yyyy HH:mm"), CultureInfo.CurrentCulture) : "";
        }

        public static double Gb(long bytes)
        {
            return Math.Round(bytes / 1073741824.0, 2);
        }

        public static double Mb(long bytes)
        {
            return Math.Round(bytes / 1048576.0, 1);
        }
    }
}
