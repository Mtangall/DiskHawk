using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using DiskHawk.Core;

namespace DiskHawk.App
{
    /// <summary>
    /// Folder / file permission check: finds accounts other than SYSTEM, Administrators, TrustedInstaller, CREATOR OWNER
    /// (and optionally the account running the console) that can write, delete or change permissions.
    /// </summary>
    internal static class FolderSecurity
    {
        private static readonly SecurityIdentifier TrustedInstallerSid =
            new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

        private const FileSystemRights WriteIsh = FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        private const int GenericWriteOrAll = 0x40000000 | 0x10000000;

        /// <summary>Names of untrusted accounts with write access (empty list = no problem).</summary>
        public static List<string> UntrustedWriters(FileSystemSecurity sec, SecurityIdentifier alsoTrusted)
        {
            var bad = new List<string>();
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & WriteIsh) == 0 && ((int)rule.FileSystemRights & GenericWriteOrAll) == 0) continue;
                var sid = rule.IdentityReference as SecurityIdentifier;
                if (sid != null && (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                    sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) || sid == TrustedInstallerSid || (alsoTrusted != null && sid == alsoTrusted)))
                    continue;
                string name = rule.IdentityReference.Value;
                try { if (sid != null) name = sid.Translate(typeof(NTAccount)).Value; } catch { }
                if (!bad.Contains(name)) bad.Add(name);
            }
            return bad;
        }

        public static bool OnlyAdminsCanWrite(string dir)
        {
            return UntrustedWriters(Directory.GetAccessControl(dir, AccessControlSections.Access), null).Count == 0;
        }
    }

    /// <summary>
    /// Console integrity. The scanner (DiskHawk.Scanner.exe) is deployed to every machine with an administrator account, so:
    /// (1) it is compared with the SHA-256 embedded at build time (BuildInfo.ScannerSha256, DiskHawk.App.csproj);
    /// (2) we check whether non-administrators can write to the application folder or the scanner file.
    /// Once a code-signing certificate is available, Authenticode (WinVerifyTrust) verification should be added here.
    /// </summary>
    internal static class AppIntegrity
    {
        public static byte[] Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                return sha.ComputeHash(fs);
        }

        public static string Hex(byte[] b)
        {
            var c = new char[b.Length * 2];
            const string d = "0123456789ABCDEF";
            for (int i = 0; i < b.Length; i++) { c[2 * i] = d[b[i] >> 4]; c[2 * i + 1] = d[b[i] & 15]; }
            return new string(c);
        }

        /// <summary>Does the hash match the value embedded at build time? If not, returns the error text for the user.</summary>
        public static string CheckScannerHash(byte[] actual)
        {
            var expected = BuildInfo.ScannerSha256 ?? "";
            if (expected.Length != 64)
                return L.T("Bu konsol derlemesinde tarayıcının beklenen özeti yok; çözümü (tüm projeleri) yeniden derleyin.");
            if (!string.Equals(expected, Hex(actual), StringComparison.OrdinalIgnoreCase))
                return L.T("DiskHawk.Scanner.exe bu konsolla birlikte derlenen sürüm değil (dosya değiştirilmiş olabilir). Güvenlik için dağıtılmadı; uygulamayı güvenilir kaynaktan yeniden kurun veya derleyin.");
            return null;
        }

        /// <summary>At startup: scanner hash and application folder permissions. Returns problem texts (empty = no problem).</summary>
        public static List<string> StartupCheck()
        {
            var problems = new List<string>();
            try
            {
                if (File.Exists(AppPaths.ScannerExe))
                {
                    var msg = CheckScannerHash(Sha256(AppPaths.ScannerExe));
                    if (msg != null) problems.Add(msg);
                }
            }
            catch (Exception ex) { problems.Add(L.T("Tarayıcı dosyası doğrulanamadı: ") + ex.Message); }

            if (!Native2.IsWindows) return problems;
            try
            {
                SecurityIdentifier me = null;
                try { using (var id = WindowsIdentity.GetCurrent()) me = id.User; } catch { }
                var writers = new List<string>();
                writers.AddRange(FolderSecurity.UntrustedWriters(Directory.GetAccessControl(AppPaths.AppDir.TrimEnd('\\'), AccessControlSections.Access), me));
                if (File.Exists(AppPaths.ScannerExe))
                    foreach (var w in FolderSecurity.UntrustedWriters(File.GetAccessControl(AppPaths.ScannerExe, AccessControlSections.Access), me))
                        if (!writers.Contains(w)) writers.Add(w);
                if (writers.Count > 0)
                    problems.Add(string.Format(L.T("Uygulama klasörüne yönetici olmayan hesaplar yazabiliyor ({0}): {1}\nBu hesaplar tarayıcıyı değiştirip tüm makinelere dağıtılmasına yol açabilir. DiskHawk'ı yalnızca yöneticilerin yazabildiği bir klasöre (örn. C:\\Program Files\\DiskHawk) taşıyın."),
                        writers.Count, string.Join(", ", writers)));
            }
            catch { /* permissions could not be read (network path etc.): no warning */ }
            return problems;
        }

        private static class Native2
        {
            public static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }
        }
    }
}
