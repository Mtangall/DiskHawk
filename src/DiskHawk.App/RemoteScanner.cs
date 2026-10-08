using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using DiskHawk.Core;

namespace DiskHawk.App
{
    internal enum MachineState { Idle, Queued, Running, Done, Failed, Cancelled }

    internal sealed class MachineEntry
    {
        public string Name;
        public volatile MachineState State;
        public volatile string Phase = "";
        public volatile string Message = "";
        public ReportSummary Summary;
        public string ResultFile;
        public DateTime? StartedAt;
        public long LastRunMs;
        public int Attempts;

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case MachineState.Queued: return L.T("Kuyrukta");
                    case MachineState.Running: return string.IsNullOrEmpty(Phase) ? L.T("Çalışıyor") : Phase;
                    case MachineState.Done: return L.T("Tamam");
                    case MachineState.Failed: return L.T("Hata");
                    case MachineState.Cancelled: return L.T("İptal");
                    default: return Summary != null ? L.T("Önceki sonuç") : "";
                }
            }
        }
    }

    internal sealed class RemoteScanSettings
    {
        public string ScannerExe;
        public string ResultsDir;
        public bool PingFirst = true;
        public int TimeoutMinutes = 30;
        public int MinFileMb = 10;
        public bool LowIo;
    }

    /// <summary>Error with a known cause, shown to the user.</summary>
    internal sealed class ScanFailure : Exception
    {
        public readonly bool Retryable;
        /// <summary>The remote process was started but its result could not be retrieved (deletion may be running / finished).</summary>
        public bool OutcomeUnknown;
        public ScanFailure(string msg, bool retryable = true, Exception inner = null) : base(msg, inner) { Retryable = retryable; }
    }

    /// <summary>
    /// Remote scan: copies the scanner via admin$, starts it at low priority via WMI (Win32_Process.Create),
    /// pulls the report back to the console when done and cleans up the remote folder.
    /// No WinRM needed; RPC/DCOM + SMB are enough.
    ///
    /// Security: the working folder is C:\Windows\DiskHawk\&lt;random&gt;. Standard users cannot create folders
    /// under C:\Windows (unlike C:\Windows\Temp); in addition the parent folder ACL is reset to
    /// SYSTEM + Administrators only and verified. Every run uses its own new folder; the scanner is
    /// copied every time and verified with SHA-256.
    /// </summary>
    internal sealed class RemoteScanner
    {
        private const string RemoteFolder = "DiskHawk";
        private const string ExeName = "DiskHawk.Scanner.exe";
        /// <summary>First line of the deletion list file: "#DHLIST1 &lt;count&gt;". The scanner verifies the count.</summary>
        public const string ListHeader = "#DHLIST1 ";

        private readonly RemoteScanSettings _s;
        private readonly Action<string> _log;

        public RemoteScanner(RemoteScanSettings s, Action<string> log)
        {
            _s = s;
            _log = log ?? (x => { });
        }

        public void Scan(MachineEntry m, CancellationToken ct)
        {
            string pc = m.Name;
            var total = Stopwatch.StartNew();

            // 1) ping
            if (_s.PingFirst)
            {
                m.Phase = "Ping";
                // also try port 445 for machines with ICMP blocked but SMB open
                if (!PingOk(pc) && !TcpOk(pc, 445, 2000))
                    throw new ScanFailure(L.T("Ping ve SMB (445) yanıt vermedi (kapalı / ağda değil)"), false);
            }
            ct.ThrowIfCancellationRequested();

            // 2) WMI connection
            m.Phase = L.T("WMI bağlanıyor");
            ManagementScope scope;
            string winDir;
            try
            {
                scope = Connect(pc, ct);
                winDir = QueryWindowsDir(scope);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw Translate(ex, "WMI"); }
            ct.ThrowIfCancellationRequested();

            string runId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string slot = "scan_" + runId + "_" + Guid.NewGuid().ToString("N");
            string uncBase = @"\\" + pc + @"\admin$\" + RemoteFolder;
            string remoteDir = winDir.TrimEnd('\\') + @"\" + RemoteFolder + @"\" + slot;
            string uncDir = uncBase + @"\" + slot;
            string remoteExe = remoteDir + @"\" + ExeName;
            string outName = "scan_" + runId + ReportSerializer.Extension;
            string progName = "scan_" + runId + ".progress";
            string uncOut = uncDir + @"\" + outName;
            string uncErr = uncOut + ".err";
            string uncProg = uncDir + @"\" + progName;

            // 3) copy the scanner
            m.Phase = L.T("Kopyalanıyor");
            try
            {
                PrepareRemoteDir(uncBase, uncDir);
                CopyScanner(uncDir + @"\" + ExeName);
            }
            catch (Exception ex)
            {
                TryDeleteDir(uncDir);   // do not leave a half-prepared run folder behind
                if (ex is ScanFailure) throw;
                throw Translate(ex, "admin$");
            }
            ct.ThrowIfCancellationRequested();

            // 4) start
            m.Phase = L.T("Başlatılıyor");
            string cmd = string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" -q --below-normal --lang " + L.Lang + " --min-file-mb {1} -o \"{2}\" --progress-file \"{3}\"{4}",
                remoteExe, _s.MinFileMb, remoteDir + @"\" + outName, remoteDir + @"\" + progName, _s.LowIo ? " --low-io" : "");
            uint pid;
            try { pid = StartProcess(scope, cmd, remoteDir); }
            catch (ScanFailure) { throw; }
            catch (Exception ex) { throw Translate(ex, "WMI"); }
            _log(pc + L.T(": tarayıcı başlatıldı (PID ") + pid + ")");

            // 5) wait
            var sw = Stopwatch.StartNew();
            int loops = 0, missing = 0;
            try
            {
                while (true)
                {
                    if (ct.WaitHandle.WaitOne(1500))
                    {
                        TryTerminate(scope, pid);
                        throw new OperationCanceledException(ct);
                    }
                    if (File.Exists(uncOut)) break;
                    if (File.Exists(uncErr))
                    {
                        string err = SafeRead(uncErr);
                        throw new ScanFailure(L.T("Tarayıcı hatası: ") + FirstLine(err), true);
                    }
                    UpdatePhase(m, uncProg, sw.Elapsed);

                    if (++loops % 4 == 0)
                    {
                        if (!ProcessAlive(scope, pid))
                        {
                            if (++missing >= 2 && !File.Exists(uncOut))
                                throw new ScanFailure(L.T("Tarayıcı beklenmedik şekilde kapandı (AV/AppLocker engellemiş olabilir)"), true);
                        }
                        else missing = 0;
                    }
                    if (sw.Elapsed.TotalMinutes > _s.TimeoutMinutes)
                    {
                        TryTerminate(scope, pid);
                        throw new ScanFailure(L.T("Zaman aşımı (") + _s.TimeoutMinutes + L.T(" dk)"), true);
                    }
                }

                // 6) fetch the report
                m.Phase = L.T("Rapor alınıyor");
                string localDir = Path.Combine(_s.ResultsDir, CsvExporter.Safe(pc.ToUpperInvariant()));
                Directory.CreateDirectory(localDir);
                string local = Path.Combine(localDir, CsvExporter.Safe(pc.ToUpperInvariant()) + "_" + runId + ReportSerializer.Extension);
                long reportBytes = new FileInfo(uncOut).Length;
                if (reportBytes > MaxReportBytes)
                    throw new ScanFailure(string.Format(L.T("Uzak rapor dosyası beklenmedik kadar büyük ({0}); alınmadı."), Fmt.Size(reportBytes)), false);
                CopyWithRetry(uncOut, local);

                var summary = ReportSerializer.ReadSummary(local);
                m.Summary = summary;
                m.ResultFile = local;
                var p = summary.Primary;
                _log(string.Format(L.T("{0}: tamam - {1} ({2})"), pc,
                    p != null ? p.RootPath + " " + Fmt.Percent(p.UsedPercent) + L.T(" dolu, sıcak nokta ") + p.HotspotPath + " " + Fmt.Size(p.HotspotSize) + L.T(", motor ") + L.T(p.Engine) + (string.IsNullOrEmpty(p.EngineNote) ? "" : " [" + p.EngineNote + "]") : L.T("sürücü yok"),
                    Fmt.Duration(total.ElapsedMilliseconds)));
            }
            finally
            {
                // 7) clean up (give the scanner a moment to exit)
                WaitExit(scope, pid, 10);
                TryDeleteDir(uncDir);
            }
        }

        /// <summary>
        /// Remote deletion: the list of paths is dropped via admin$ and the scanner runs on the target in --delete mode
        /// via WMI. The target machine itself performs the deletion (no C$ needed).
        /// </summary>
        public List<DeleteResult> DeleteRemote(string pc, IList<string> paths, bool allowCritical, CancellationToken ct, Action<string> phase)
        {
            phase = phase ?? (x => { });
            if (_s.PingFirst)
            {
                phase("Ping");
                if (!PingOk(pc) && !TcpOk(pc, 445, 2000))
                    throw new ScanFailure(L.T("Ping ve SMB (445) yanıt vermedi (kapalı / ağda değil)"), false);
            }

            phase(L.T("WMI bağlanıyor"));
            ManagementScope scope;
            string winDir;
            try
            {
                scope = Connect(pc, ct);
                winDir = QueryWindowsDir(scope);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw Translate(ex, "WMI"); }

            // separate folder so it does not clash with a scan
            // every deletion has its own subfolder: concurrent deletions on the same machine do not touch each other's files
            foreach (var pth in paths)
                if (string.IsNullOrEmpty(pth) || PathText.HasControlChars(pth))
                    throw new ScanFailure(L.T("Silme listesinde geçersiz karakter içeren yol var; işlem yapılmadı."), false);

            string runId = "del_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N");
            string uncBase = @"\\" + pc + @"\admin$\" + RemoteFolder;
            string remoteDir = winDir.TrimEnd('\\') + @"\" + RemoteFolder + @"\" + runId;
            string uncDir = uncBase + @"\" + runId;
            string listName = runId + ".txt", resName = runId + ".result", progName = runId + ".progress";
            string uncRes = uncDir + @"\" + resName;
            string uncErr = uncRes + ".err";
            string uncProg = uncDir + @"\" + progName;

            phase(L.T("Kopyalanıyor"));
            try
            {
                PrepareRemoteDir(uncBase, uncDir);
                CopyScanner(uncDir + @"\" + ExeName);
                var lines = new List<string>(paths.Count + 1) { ListHeader + paths.Count.ToString(CultureInfo.InvariantCulture) };
                lines.AddRange(paths);
                File.WriteAllLines(uncDir + @"\" + listName, lines, new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                TryDeleteDir(uncDir);   // the deletion has not started yet; the folder can be removed safely
                if (ex is ScanFailure) throw;
                throw Translate(ex, "admin$");
            }

            phase(L.T("Siliniyor"));
            string cmd = string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" -q --lang " + L.Lang + " --delete \"{1}\" --delete-log \"{2}\" --progress-file \"{3}\"{4}",
                remoteDir + @"\" + ExeName, remoteDir + @"\" + listName, remoteDir + @"\" + resName, remoteDir + @"\" + progName,
                allowCritical ? " --allow-critical" : "");
            uint pid;
            try { pid = StartProcess(scope, cmd, remoteDir); }
            catch (ScanFailure) { throw; }
            catch (Exception ex) { throw Translate(ex, "WMI"); }
            _log(pc + L.T(": silme başlatıldı (PID ") + pid + ", " + paths.Count + L.T(" öğe)"));

            var sw = Stopwatch.StartNew();          // time since the last progress update
            string lastProg = null;
            int loops = 0, missing = 0;
            bool alive = true;
            try
            {
                while (true)
                {
                    // an interrupted deletion stays half done, so there is no cancel: the remote process runs to the end.
                    Thread.Sleep(1000);
                    if (File.Exists(uncRes)) break;
                    if (File.Exists(uncErr)) throw new ScanFailure(L.T("Silme hatası: ") + FirstLine(SafeRead(uncErr)), false);
                    try
                    {
                        if (File.Exists(uncProg))
                        {
                            var raw = ReadSmall(uncProg, MaxSmallFileBytes);
                            if (raw != lastProg) { lastProg = raw; sw.Restart(); }
                            var pr = raw.Split('|');
                            if (pr.Length >= 6)
                                phase(string.Format(L.T("Siliniyor: {0} dosya, {1} boşaldı"),
                                    Fmt.Count(long.Parse(pr[0], CultureInfo.InvariantCulture)),
                                    Fmt.Size(long.Parse(pr[2], CultureInfo.InvariantCulture))));
                        }
                    }
                    catch { }
                    if (++loops % 5 == 0)
                    {
                        alive = ProcessAlive(scope, pid);
                        if (!alive)
                        {
                            if (++missing >= 2 && !File.Exists(uncRes))
                                throw new ScanFailure(L.T("Tarayıcı beklenmedik şekilde kapandı; silme kısmen yapılmış olabilir, makineyi yeniden tarayın"), false) { OutcomeUnknown = true };
                        }
                        else missing = 0;
                    }
                    if (sw.Elapsed.TotalMinutes > Math.Max(_s.TimeoutMinutes, 30))
                        throw new ScanFailure(L.T("Silme ") + Math.Max(_s.TimeoutMinutes, 30) + L.T(" dk boyunca ilerleme bildirmedi; uzak işlem hâlâ çalışıyor olabilir. Sonucu görmek için makineyi yeniden tarayın."), false) { OutcomeUnknown = true };
                }
                return Deleter.ReadResults(uncRes);
            }
            finally
            {
                WaitExit(scope, pid, 10);
                // do not touch the folder while the remote process is still running (exe locked, result written later)
                if (!ProcessAlive(scope, pid))
                {
                    TryDeleteDir(uncDir);
                }
            }
        }

        // ------------------------------------------------------------------ steps

        private static bool PingOk(string pc)
        {
            try
            {
                using (var p = new Ping())
                {
                    for (int i = 0; i < 2; i++)
                        if (p.Send(pc, 1500).Status == IPStatus.Success) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool TcpOk(string pc, int port, int timeoutMs)
        {
            try
            {
                using (var c = new System.Net.Sockets.TcpClient())
                {
                    var t = c.ConnectAsync(pc, port);
                    return t.Wait(timeoutMs) && c.Connected;
                }
            }
            catch { return false; }
        }

        /// <summary>ConnectionOptions.Timeout does not bound scope.Connect(); wait in a separate task and apply cancel/timeout.</summary>
        private static ManagementScope Connect(string pc, CancellationToken ct)
        {
            ManagementScope scope = null;
            var task = System.Threading.Tasks.Task.Run(() => { scope = ConnectCore(pc); });
            bool finished;
            try { finished = task.Wait(45000, ct); }
            catch (AggregateException ae) { throw ae.GetBaseException(); }
            if (!finished) throw new ScanFailure(L.T("WMI bağlantısı zaman aşımına uğradı (RPC/firewall engelli olabilir)"), true);
            return scope;
        }

        private static ManagementScope ConnectCore(string pc)
        {
            var co = new ConnectionOptions
            {
                Impersonation = ImpersonationLevel.Impersonate,
                Authentication = AuthenticationLevel.PacketPrivacy,
                EnablePrivileges = true,
                Timeout = TimeSpan.FromSeconds(30)
            };
            var scope = new ManagementScope(@"\\" + pc + @"\root\cimv2", co);
            scope.Connect();
            return scope;
        }

        private static string QueryWindowsDir(ManagementScope scope)
        {
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT WindowsDirectory FROM Win32_OperatingSystem")))
            using (var col = s.Get())
            {
                foreach (ManagementObject mo in col)
                    using (mo)
                    {
                        var v = mo["WindowsDirectory"] as string;
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
            }
            return @"C:\Windows";
        }

        // ------------------------------------------------------------------ secure remote folder

        /// <summary>
        /// Prepares the parent folder (C:\Windows\DiskHawk): creates it if missing, resets its ACL to SYSTEM + Administrators,
        /// verifies nobody else can write to it, then creates a new subfolder for this run.
        /// If verification fails nothing is done (the scanner is never run from an untrusted folder).
        /// </summary>
        private void PrepareRemoteDir(string uncBase, string uncDir)
        {
            var bi = new DirectoryInfo(uncBase);
            if (!bi.Exists) Directory.CreateDirectory(uncBase);
            bi.Refresh();
            if ((bi.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ScanFailure(L.T("Uzak çalışma klasörü bir bağlantı (junction/symlink); güvenli değil: ") + uncBase, false);
            try { bi.SetAccessControl(AdminOnlySecurity()); }
            catch (Exception ex) { _log(uncBase + L.T(": izinler ayarlanamadı - ") + ex.Message); }
            if (!OnlyAdminsCanWrite(uncBase))
                throw new ScanFailure(L.T("Uzak çalışma klasörü güvenli değil (yönetici olmayanların yazma izni var): ") + uncBase, false);

            CleanupStale(uncBase);

            if (Directory.Exists(uncDir)) throw new ScanFailure(L.T("Uzak çalışma klasörü zaten var (beklenmedik)"), true);
            Directory.CreateDirectory(uncDir);
            if (!OnlyAdminsCanWrite(uncDir))
                throw new ScanFailure(L.T("Uzak çalışma klasörü güvenli değil (yönetici olmayanların yazma izni var): ") + uncDir, false);
        }

        private static DirectorySecurity AdminOnlySecurity()
        {
            var ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);   // do not inherit permissions from the parent folder
            const InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            return ds;
        }

        /// <summary>Can nobody but SYSTEM, Administrators, TrustedInstaller and CREATOR OWNER write to the folder?</summary>
        private static bool OnlyAdminsCanWrite(string dir)
        {
            return FolderSecurity.OnlyAdminsCanWrite(dir);
        }

        /// <summary>Removes stale run folders left behind (older than 1 day).</summary>
        private static void CleanupStale(string uncBase)
        {
            try
            {
                foreach (var d in new DirectoryInfo(uncBase).GetDirectories())
                {
                    if (!(d.Name.StartsWith("scan_", StringComparison.OrdinalIgnoreCase) || d.Name.StartsWith("del_", StringComparison.OrdinalIgnoreCase))) continue;
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (DateTime.UtcNow - d.CreationTimeUtc > TimeSpan.FromDays(1)) TryDeleteDir(d.FullName);
                }
            }
            catch { }
        }

        private static readonly object HashLock = new object();
        private static string _hashFile;
        private static DateTime _hashTime;
        private static byte[] _hash;

        private static byte[] ScannerHash(FileInfo src)
        {
            lock (HashLock)
            {
                if (_hash == null || _hashFile != src.FullName || _hashTime != src.LastWriteTimeUtc)
                {
                    var h = AppIntegrity.Sha256(src.FullName);
                    // the scanner is deployed to every machine as administrator: it must match the hash embedded at build time
                    var problem = AppIntegrity.CheckScannerHash(h);
                    if (problem != null) throw new ScanFailure(problem, false);
                    _hash = h;
                    _hashFile = src.FullName;
                    _hashTime = src.LastWriteTimeUtc;
                }
                return _hash;
            }
        }

        /// <summary>Copies the scanner into the new (empty) run folder and verifies the copy with SHA-256.</summary>
        private void CopyScanner(string target)
        {
            var src = new FileInfo(_s.ScannerExe);
            if (!src.Exists) throw new ScanFailure(L.T("Tarayıcı exe bulunamadı: ") + _s.ScannerExe, false);
            var expected = ScannerHash(src);
            try
            {
                File.Copy(src.FullName, target, false);
            }
            catch (IOException ex)
            {
                throw new ScanFailure(L.T("Tarayıcı kopyalanamadı: ") + ex.Message, true);
            }
            // remove the Mark of the Web from the remote copy so AV/ASR/AppLocker do not block our own tool
            try { DeleteFileW(target + ":Zone.Identifier"); } catch { }

            byte[] actual;
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                actual = sha.ComputeHash(fs);
            if (!StructuralEquals(expected, actual))
                throw new ScanFailure(L.T("Uzak makinedeki tarayıcı kopyası doğrulanamadı (dosya değiştirilmiş; güvenlik yazılımı müdahale etmiş olabilir)"), false);
        }

        private static bool StructuralEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteFileW(string path);

        private static uint StartProcess(ManagementScope scope, string cmd, string workDir)
        {
            using (var proc = new ManagementClass(scope, new ManagementPath("Win32_Process"), new ObjectGetOptions()))
            using (var startupCls = new ManagementClass(scope, new ManagementPath("Win32_ProcessStartup"), new ObjectGetOptions()))
            using (var startup = startupCls.CreateInstance())
            {
                startup["ShowWindow"] = (ushort)0;          // SW_HIDE
                startup["PriorityClass"] = (uint)16384;     // BELOW_NORMAL_PRIORITY_CLASS
                using (var inp = proc.GetMethodParameters("Create"))
                {
                    inp["CommandLine"] = cmd;
                    inp["CurrentDirectory"] = workDir;
                    inp["ProcessStartupInformation"] = startup;
                    using (var outp = proc.InvokeMethod("Create", inp, null))
                    {
                        uint rv = Convert.ToUInt32(outp["ReturnValue"]);
                        if (rv != 0) throw new ScanFailure(L.T("Uzak işlem başlatılamadı: ") + CreateError(rv), rv != 2 && rv != 3);
                        return Convert.ToUInt32(outp["ProcessId"]);
                    }
                }
            }
        }

        private static string CreateError(uint rv)
        {
            switch (rv)
            {
                case 2: return L.T("erişim reddedildi (2)");
                case 3: return L.T("yetersiz yetki (3)");
                case 8: return L.T("bilinmeyen hata (8)");
                case 9: return L.T("yol bulunamadı (9)");
                case 21: return L.T("geçersiz parametre (21)");
                default: return L.T("kod ") + rv;
            }
        }

        private static bool ProcessAlive(ManagementScope scope, uint pid)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT ProcessId FROM Win32_Process WHERE ProcessId = " + pid)))
                using (var col = s.Get())
                    return col.Count > 0;
            }
            catch { return true; } // if unsure, keep waiting
        }

        private static void WaitExit(ManagementScope scope, uint pid, int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                if (!ProcessAlive(scope, pid)) return;
                Thread.Sleep(1000);
            }
        }

        private void TryTerminate(ManagementScope scope, uint pid)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_Process WHERE ProcessId = " + pid)))
                using (var col = s.Get())
                    foreach (ManagementObject mo in col)
                        using (mo) mo.InvokeMethod("Terminate", new object[] { (uint)0 });
            }
            catch (Exception ex) { _log(L.T("Uzak işlem sonlandırılamadı: ") + ex.Message); }
        }

        private static void UpdatePhase(MachineEntry m, string progFile, TimeSpan elapsed)
        {
            try
            {
                if (!File.Exists(progFile)) { m.Phase = L.T("Taranıyor (") + (int)elapsed.TotalSeconds + L.T(" sn)"); return; }
                var parts = ReadSmall(progFile, MaxSmallFileBytes).Split('|');
                if (parts.Length < 6) return;
                long files = long.Parse(parts[0], CultureInfo.InvariantCulture);
                long bytes = long.Parse(parts[2], CultureInfo.InvariantCulture);
                m.Phase = string.Format(L.T("Taranıyor {0}  {1} dosya  {2}"), parts[5], Fmt.Count(files), Fmt.Size(bytes));
            }
            catch { /* the file may be being written */ }
        }

        private static void CopyWithRetry(string src, string dst)
        {
            for (int i = 0; ; i++)
            {
                try { File.Copy(src, dst, true); return; }
                catch (IOException)
                {
                    if (i >= 5) throw;
                    Thread.Sleep(1000);
                }
            }
        }

        private static void TryDeleteDir(string dir)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    return;
                }
                catch { Thread.Sleep(1500); }
            }
        }

        // upper limits for helper files read from the remote machine (memory / disk must not fill up)
        private const int MaxSmallFileBytes = 64 * 1024;          // .err, .progress
        private const long MaxReportBytes = 2L * 1024 * 1024 * 1024; // .dhr

        private static string SafeRead(string f)
        {
            try { return ReadSmall(f, MaxSmallFileBytes); } catch { return ""; }
        }

        /// <summary>Reads at most the first maxBytes bytes of a file as UTF-8 text.</summary>
        private static string ReadSmall(string f, int maxBytes)
        {
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var buf = new byte[(int)Math.Min(maxBytes, Math.Max(0, fs.Length))];
                int n = 0, r;
                while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
                return System.Text.Encoding.UTF8.GetString(buf, 0, n);
            }
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return L.T("(detay yok)");
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            return i > 0 ? s.Substring(0, i) : s;
        }

        private static ScanFailure Translate(Exception ex, string stage)
        {
            if (ex is ScanFailure) return (ScanFailure)ex;
            if (ex is UnauthorizedAccessException)
                return new ScanFailure(stage + L.T(": erişim reddedildi (yetkili hesapla çalıştırın)"), false, ex);
            var com = ex as COMException;
            if (com != null)
            {
                switch ((uint)com.ErrorCode)
                {
                    case 0x800706BA: return new ScanFailure(L.T("RPC sunucusu yok (makine kapalı, firewall veya WMI engelli)"), true, ex);
                    case 0x80070005: return new ScanFailure(stage + L.T(": erişim reddedildi"), false, ex);
                    case 0x800706BE: return new ScanFailure(L.T("RPC çağrısı başarısız"), true, ex);
                }
                return new ScanFailure(stage + ": " + com.Message.Trim() + " (0x" + com.ErrorCode.ToString("X8") + ")", true, ex);
            }
            var io = ex as IOException;
            if (io != null) return new ScanFailure(stage + ": " + io.Message.Trim(), true, ex);
            return new ScanFailure(stage + ": " + ex.Message.Trim(), true, ex);
        }
    }
}
