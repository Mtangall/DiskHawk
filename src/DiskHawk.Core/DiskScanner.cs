using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DiskHawk.Core
{
    public enum ScanEngine
    {
        Auto = 0,     // NTFS drive root + administrator -> MFT, otherwise classic
        Mft = 1,      // MFT only (error if not possible)
        Classic = 2   // directory walk with FindFirstFileEx
    }

    public sealed class ScanOptions
    {
        /// <summary>Scan engine selection.</summary>
        public ScanEngine Engine = ScanEngine.Auto;

        /// <summary>Number of concurrent readers in the MFT engine (for NVMe/SSD queue depth).</summary>
        public int MftReaders = 4;

        /// <summary>Number of threads reading directories in parallel.</summary>
        public int Threads = DefaultThreads;

        /// <summary>Files above this size are candidates for the "largest files" list.</summary>
        public long LargeFileThreshold = 10L * 1024 * 1024;

        /// <summary>Number of largest files written to the report.</summary>
        public int MaxLargeFiles = 1000;

        /// <summary>Puts worker threads into Windows "background mode" (low I/O priority).</summary>
        public bool LowIoPriority;

        public static int DefaultThreads
        {
            get { return Math.Max(4, Math.Min(Environment.ProcessorCount * 2, 16)); }
        }
    }

    /// <summary>Live progress. Fields may be read from other threads during the scan.</summary>
    public sealed class ScanProgress
    {
        public string RootPath = "";
        public long Files;
        public long Dirs;
        public long Bytes;
        public long Errors;
        public volatile string CurrentPath = "";
        public readonly Stopwatch Watch = new Stopwatch();
    }

    /// <summary>
    /// Multi-threaded directory scanner. On Windows it uses FindFirstFileExW + FIND_FIRST_EX_LARGE_FETCH
    /// (large block reads with one kernel transition per directory); file sizes come with the
    /// enumeration, so there is no extra call per file.
    /// </summary>
    public sealed class DiskScanner
    {
        public const string Version = "1.4.0";

        // reparse point types that are not followed (prevents double counting).
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003; // junction / mount
        private const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;
        private const uint NameSurrogateBit = 0x20000000;

        private static readonly string SepStr = Path.DirectorySeparatorChar.ToString();

        private readonly ScanOptions _opt;
        private volatile ScanProgress _progress = new ScanProgress();

        public DiskScanner(ScanOptions options)
        {
            _opt = options ?? new ScanOptions();
            if (_opt.Threads < 1) _opt.Threads = 1;
            if (_opt.MaxLargeFiles < 1) _opt.MaxLargeFiles = 1;
        }

        /// <summary>Progress of the drive currently being scanned.</summary>
        public ScanProgress Progress { get { return _progress; } }

        public static List<string> GetFixedDrives()
        {
            var list = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == DriveType.Fixed && d.IsReady) list.Add(d.RootDirectory.FullName);
                }
                catch { /* drive not ready */ }
            }
            return list;
        }

        public static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException(L.T("Boş yol"));
            root = root.Trim().Trim('"');
            // Windows does not normalize paths with the \\?\ prefix: relative paths, '/' and '..' must be resolved here
            if (!root.StartsWith(@"\\?\"))
            {
                try { root = Path.GetFullPath(root.Length == 2 && root[1] == ':' ? root + "\\" : root); } catch { }
            }
            if (root.Length == 2 && root[1] == ':') return root.ToUpperInvariant() + "\\";      // "c:" -> "C:\"
            if (root.Length == 3 && root[1] == ':' && (root[2] == '\\' || root[2] == '/'))
                return root.Substring(0, 2).ToUpperInvariant() + "\\";
            if (root.Length > 3 && root.EndsWith("\\")) root = root.TrimEnd('\\');
            return root;
        }

        public ScanReport ScanAll(IList<string> roots, CancellationToken ct, Action<string> onRootStart = null)
        {
            var report = new ScanReport
            {
                Machine = Environment.MachineName,
                ScanTimeUtc = DateTime.UtcNow,
                ScannerVersion = Version,
                OsVersion = Environment.OSVersion.VersionString,
                UserContext = Environment.UserDomainName + "\\" + Environment.UserName
            };
            foreach (var r in roots)
            {
                ct.ThrowIfCancellationRequested();
                if (onRootStart != null) onRootStart(r);
                report.Drives.Add(ScanRoot(r, ct));
            }
            return report;
        }

        // ------------------------------------------------------------------ scanning

        private struct WorkItem
        {
            public DirNode Node;
            public string Path;
        }

        private struct RawEntry
        {
            public string Name;
            public uint Attr;
            public uint ReparseTag;
            public long Size;
            public long LastWrite;
        }

        private sealed class WorkerState
        {
            public readonly List<RawEntry> Buffer = new List<RawEntry>(512);
            public readonly Dictionary<string, ExtStat> Ext = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
            public readonly List<FileEntry> Large = new List<FileEntry>();
            public long LargeMin;
            public long CloudBytes;
            public int CloudFiles;
        }

        private delegate bool Enumerator(string path, List<RawEntry> into, out int error);

        private ConcurrentStack<WorkItem> _work;
        private int _pending;
        private ManualResetEventSlim _done;
        private CancellationToken _ct;
        private Enumerator _enum;

        public DriveResult ScanRoot(string rootPath, CancellationToken ct)
        {
            rootPath = NormalizeRoot(rootPath);
            var progress = new ScanProgress { RootPath = rootPath };
            _progress = progress;
            progress.Watch.Start();

            // --- 1) raw $MFT engine (drive root, NTFS + administrator)
            string mftWhyNot = null;
            if (_opt.Engine != ScanEngine.Classic)
            {
                DriveResult mres;
                if (MftScanner.TryScan(rootPath, _opt, progress, ct, out mres, out mftWhyNot))
                {
                    FillDriveInfo(mres);
                    mres.Engine = "MFT";
                    progress.Watch.Stop();
                    mres.DurationMs = progress.Watch.ElapsedMilliseconds;
                    return mres;
                }
                if (_opt.Engine == ScanEngine.Mft)
                    throw new InvalidOperationException(L.T("MFT motoru kullanılamadı: ") + mftWhyNot);
            }

            // --- 2) classic engine
            var result = new DriveResult { RootPath = rootPath, Engine = "Klasik", EngineNote = mftWhyNot ?? "" };
            FillDriveInfo(result);

            var root = new DirNode { Name = rootPath, Depth = 0 };
            result.Root = root;

            _ct = ct;
            _enum = Native.IsWindows ? (Enumerator)EnumerateWin32 : EnumerateManaged;
            _work = new ConcurrentStack<WorkItem>();
            _work.Push(new WorkItem { Node = root, Path = rootPath });
            _pending = 1;
            _done = new ManualResetEventSlim(false);

            var states = new WorkerState[_opt.Threads];
            var threads = new Thread[_opt.Threads];
            for (int i = 0; i < threads.Length; i++)
            {
                states[i] = new WorkerState { LargeMin = _opt.LargeFileThreshold };
                threads[i] = new Thread(WorkerLoop, 1024 * 1024) { IsBackground = true, Name = "scan-" + i };
                threads[i].Start(states[i]);
            }
            foreach (var t in threads) t.Join();
            _done.Dispose();

            ct.ThrowIfCancellationRequested();

            // --- merge
            var ext = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
            var large = new List<FileEntry>();
            foreach (var s in states)
            {
                foreach (var kv in s.Ext)
                {
                    ExtStat e;
                    if (!ext.TryGetValue(kv.Key, out e)) { e = new ExtStat { Ext = kv.Value.Ext }; ext[kv.Key] = e; }
                    e.Size += kv.Value.Size;
                    e.Count += kv.Value.Count;
                }
                large.AddRange(s.Large);
                result.CloudOnlyBytes += s.CloudBytes;
                result.CloudOnlyFiles += s.CloudFiles;
            }
            large.Sort((a, b) => b.Size.CompareTo(a.Size));
            if (large.Count > _opt.MaxLargeFiles) large.RemoveRange(_opt.MaxLargeFiles, large.Count - _opt.MaxLargeFiles);
            result.LargestFiles = large;

            result.Extensions = new List<ExtStat>(ext.Values);
            result.Extensions.Sort((a, b) => b.Size.CompareTo(a.Size));

            Aggregate(root);

            result.ScannedBytes = root.Size;
            result.ScannedFiles = root.Files;
            result.ScannedDirs = root.Dirs + 1;
            result.Errors = (int)Interlocked.Read(ref progress.Errors);
            progress.Watch.Stop();
            result.DurationMs = progress.Watch.ElapsedMilliseconds;
            return result;
        }

        private void WorkerLoop(object state)
        {
            var ws = (WorkerState)state;
            bool bg = _opt.LowIoPriority && Native.IsWindows;
            if (bg) Native.SetThreadPriority(Native.GetCurrentThread(), Native.THREAD_MODE_BACKGROUND_BEGIN);
            try
            {
                var spin = new SpinWait();
                while (!_ct.IsCancellationRequested)
                {
                    WorkItem item;
                    if (_work.TryPop(out item))
                    {
                        spin.Reset();
                        try { ProcessDir(item, ws); }
                        catch
                        {
                            item.Node.Flags |= DirNode.FlagError;
                            Interlocked.Increment(ref _progress.Errors);
                        }
                        if (Interlocked.Decrement(ref _pending) == 0)
                        {
                            _done.Set();
                            return;
                        }
                    }
                    else
                    {
                        if (Volatile.Read(ref _pending) == 0) return;
                        if (spin.NextSpinWillYield)
                        {
                            if (_done.Wait(1)) return;
                        }
                        else spin.SpinOnce();
                    }
                }
            }
            finally
            {
                if (bg) Native.SetThreadPriority(Native.GetCurrentThread(), Native.THREAD_MODE_BACKGROUND_END);
            }
        }

        private void ProcessDir(WorkItem item, WorkerState ws)
        {
            var node = item.Node;
            var path = item.Path;
            var progress = _progress;
            progress.CurrentPath = path;

            var buf = ws.Buffer;
            buf.Clear();
            int err;
            if (!_enum(path, buf, out err))
            {
                node.Flags |= err == Native.ERROR_ACCESS_DENIED ? DirNode.FlagAccessDenied : DirNode.FlagError;
                Interlocked.Increment(ref progress.Errors);
                Interlocked.Increment(ref progress.Dirs);
                return;
            }

            string prefix = path.EndsWith(SepStr) ? path : path + SepStr;
            long own = 0, maxLw = 0;
            int files = 0;
            List<WorkItem> subs = null;

            for (int i = 0; i < buf.Count; i++)
            {
                var e = buf[i];
                if ((e.Attr & Native.FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    // junction / symlink / any link pointing elsewhere ("name surrogate" bit): the target is already
                    // counted elsewhere. Cloud folders such as OneDrive do not carry this bit and are scanned.
                    if ((e.Attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 &&
                        (e.ReparseTag == IO_REPARSE_TAG_MOUNT_POINT || e.ReparseTag == IO_REPARSE_TAG_SYMLINK ||
                         (e.ReparseTag & NameSurrogateBit) != 0))
                        continue;

                    // the displayed name is sanitized (control characters etc.); traversal uses the real name (Path)
                    var child = new DirNode { Name = PathText.SafeName(e.Name), Parent = node, Depth = (short)Math.Min(short.MaxValue, node.Depth + 1) };
                    if (node.Children == null) node.Children = new List<DirNode>();
                    node.Children.Add(child);
                    if (subs == null) subs = new List<WorkItem>();
                    subs.Add(new WorkItem { Node = child, Path = prefix + e.Name });
                }
                else
                {
                    if (e.LastWrite > maxLw) maxLw = e.LastWrite;

                    if ((e.Attr & (Native.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Native.FILE_ATTRIBUTE_OFFLINE)) != 0)
                    {
                        // OneDrive "online-only": size is visible but takes no disk space
                        ws.CloudBytes += e.Size;
                        ws.CloudFiles++;
                        continue;
                    }

                    own += e.Size;
                    files++;
                    AddExt(ws, e.Name, e.Size);
                    if (e.Size >= ws.LargeMin) AddLarge(ws, prefix + e.Name, e.Size, e.LastWrite);
                }
            }

            node.OwnSize = own;
            node.Size = own;
            node.OwnFiles = files;
            node.Files = files;
            node.LastWrite = maxLw;
            node.Dirs = node.Children != null ? node.Children.Count : 0;

            Interlocked.Add(ref progress.Files, files);
            Interlocked.Add(ref progress.Bytes, own);
            Interlocked.Increment(ref progress.Dirs);

            if (subs != null)
            {
                Interlocked.Add(ref _pending, subs.Count);
                _work.PushRange(subs.ToArray());
            }
        }

        private static void AddExt(WorkerState ws, string name, long size)
        {
            int dot = name.LastIndexOf('.');
            string ext = (dot > 0 && dot < name.Length - 1 && name.Length - dot <= 16) ? name.Substring(dot + 1) : "";
            ExtStat st;
            if (!ws.Ext.TryGetValue(ext, out st))
            {
                st = new ExtStat { Ext = ext.Length == 0 ? "" : PathText.SafeName(ext.ToLowerInvariant()) };
                ws.Ext[ext] = st;
            }
            st.Size += size;
            st.Count++;
        }

        private void AddLarge(WorkerState ws, string path, long size, long lw)
        {
            ws.Large.Add(new FileEntry { Path = PathText.SafePath(path), Size = size, LastWrite = lw });
            if (ws.Large.Count >= _opt.MaxLargeFiles * 2)
            {
                ws.Large.Sort((a, b) => b.Size.CompareTo(a.Size));
                ws.Large.RemoveRange(_opt.MaxLargeFiles, ws.Large.Count - _opt.MaxLargeFiles);
                ws.LargeMin = Math.Max(ws.LargeMin, ws.Large[ws.Large.Count - 1].Size);
            }
        }

        /// <summary>Sums subfolder values upwards and sorts children by size.</summary>
        public static void Aggregate(DirNode root)
        {
            // reverse of pre-order: every node is processed after all of its descendants
            var order = new List<DirNode>();
            var stack = new Stack<DirNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                order.Add(n);
                if (n.Children != null)
                    for (int i = 0; i < n.Children.Count; i++) stack.Push(n.Children[i]);
            }
            for (int i = order.Count - 1; i >= 0; i--)
            {
                var n = order[i];
                if (n.Children != null)
                {
                    n.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
                    n.Children.TrimExcess();
                }
                var p = n.Parent;
                if (p != null)
                {
                    p.Size += n.Size;
                    p.Files += n.Files;
                    p.Dirs += n.Dirs;
                    if (n.LastWrite > p.LastWrite) p.LastWrite = n.LastWrite;
                }
            }
        }

        private static void FillDriveInfo(DriveResult r)
        {
            try
            {
                if (r.RootPath.Length == 3 && r.RootPath[1] == ':')
                {
                    var di = new DriveInfo(r.RootPath.Substring(0, 1));
                    if (di.IsReady)
                    {
                        r.TotalBytes = di.TotalSize;
                        r.FreeBytes = di.TotalFreeSpace;
                        r.FileSystem = di.DriveFormat ?? "";
                        r.VolumeLabel = di.VolumeLabel ?? "";
                    }
                }
            }
            catch { /* drive info unavailable; the scan continues */ }
        }

        // ------------------------------------------------------------------ enumerators

        private static bool EnumerateWin32(string path, List<RawEntry> into, out int error)
        {
            string search;
            if (path.StartsWith(@"\\?\")) search = path;
            else if (path.StartsWith(@"\\")) search = @"\\?\UNC\" + path.Substring(2);
            else search = @"\\?\" + path;
            search = search.EndsWith("\\") ? search + "*" : search + "\\*";

            Native.WIN32_FIND_DATA fd;
            IntPtr h = Native.FindFirstFileExW(search, Native.FindExInfoBasic, out fd,
                Native.FindExSearchNameMatch, IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
            if (h == Native.INVALID_HANDLE_VALUE)
            {
                error = Marshal.GetLastWin32Error();
                if (error == Native.ERROR_FILE_NOT_FOUND || error == Native.ERROR_NO_MORE_FILES)
                {
                    error = 0;
                    return true; // empty folder
                }
                return false;
            }
            try
            {
                do
                {
                    var name = fd.cFileName;
                    if ((fd.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0 && (name == "." || name == ".."))
                        continue;
                    into.Add(new RawEntry
                    {
                        Name = name,
                        Attr = fd.dwFileAttributes,
                        ReparseTag = (fd.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? fd.dwReserved0 : 0,
                        Size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow,
                        LastWrite = ClampFt(((long)fd.ftLastWriteTimeHigh << 32) | fd.ftLastWriteTimeLow)
                    });
                } while (Native.FindNextFileW(h, out fd));
            }
            finally
            {
                Native.FindClose(h);
            }
            error = 0;
            return true;
        }

        // corrupt/extreme timestamps (NTFS accepts 0x7FFF...) must not break DateTime
        private static long ClampFt(long ft)
        {
            return ft > 0 && ft <= DirNode.MaxFileTime ? ft : 0;
        }

        /// <summary>Enumeration with the managed API for non-Windows environments (tests).</summary>
        private static bool EnumerateManaged(string path, List<RawEntry> into, out int error)
        {
            try
            {
                foreach (var fsi in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    var attr = (uint)fsi.Attributes;
                    var e = new RawEntry { Name = fsi.Name, Attr = attr };
                    if ((attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) e.ReparseTag = IO_REPARSE_TAG_SYMLINK;
                    var fi = fsi as FileInfo;
                    if (fi != null)
                    {
                        try { e.Size = fi.Length; } catch { }
                    }
                    try { e.LastWrite = fsi.LastWriteTimeUtc.ToFileTimeUtc(); } catch { }
                    into.Add(e);
                }
                error = 0;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                error = Native.ERROR_ACCESS_DENIED;
                return false;
            }
            catch (Exception)
            {
                error = 1;
                return false;
            }
        }
    }
}
