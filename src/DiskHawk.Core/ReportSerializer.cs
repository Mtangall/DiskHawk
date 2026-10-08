using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DiskHawk.Core
{
    /// <summary>
    /// Report file format (.dhr):
    ///   magic "ADSX" (4 bytes) + format version (int32) + GZip(
    ///       summary section  -> read quickly without loading the tree (machine list)
    ///       drive details    -> tree (pre-order), largest files, extension statistics
    ///   )
    /// </summary>
    public static class ReportSerializer
    {
        public const string Extension = ".dhr";          // DiskHawk Report
        private static readonly byte[] Magic = { (byte)'A', (byte)'D', (byte)'S', (byte)'X' };
        private const int FormatVersion = 3;   // v2: scan engine, v3: engine note (timing breakdown)

        // ------------------------------------------------------------------ writing

        public static void Save(ScanReport report, string path)
        {
            var summary = Analysis.BuildSummary(report);
            var tmp = path + ".tmp";
            try
            {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                fs.Write(Magic, 0, 4);
                fs.Write(BitConverter.GetBytes(FormatVersion), 0, 4);
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest, true))
                using (var bs = new BufferedStream(gz, 1 << 16))
                using (var w = new BinaryWriter(bs, Encoding.UTF8))
                {
                    WriteSummary(w, summary);
                    foreach (var d in report.Drives) WriteDrive(w, d);
                }
            }
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        private static void WriteSummary(BinaryWriter w, ReportSummary s)
        {
            w.Write(s.Machine ?? "");
            w.Write(s.ScanTimeUtc.ToBinary());
            w.Write(s.ScannerVersion ?? "");
            w.Write(s.OsVersion ?? "");
            w.Write(s.UserContext ?? "");
            w.Write(s.Drives.Count);
            foreach (var d in s.Drives)
            {
                w.Write(d.RootPath ?? "");
                w.Write(d.FileSystem ?? "");
                w.Write(d.TotalBytes);
                w.Write(d.FreeBytes);
                w.Write(d.ScannedBytes);
                w.Write(d.ScannedFiles);
                w.Write(d.ScannedDirs);
                w.Write(d.Errors);
                w.Write(d.CloudOnlyBytes);
                w.Write(d.DurationMs);
                w.Write(d.Engine ?? "");
                w.Write(d.EngineNote ?? "");
                w.Write(d.HotspotPath ?? "");
                w.Write(d.HotspotSize);
                w.Write(d.FileHotspotPath ?? "");
                w.Write(d.FileHotspotCount);
                w.Write(d.LargestFilePath ?? "");
                w.Write(d.LargestFileSize);
            }
        }

        private static void WriteDrive(BinaryWriter w, DriveResult d)
        {
            w.Write(d.RootPath ?? "");
            w.Write(d.FileSystem ?? "");
            w.Write(d.VolumeLabel ?? "");
            w.Write(d.TotalBytes);
            w.Write(d.FreeBytes);
            w.Write(d.ScannedBytes);
            w.Write(d.ScannedFiles);
            w.Write(d.ScannedDirs);
            w.Write(d.Errors);
            w.Write(d.CloudOnlyBytes);
            w.Write(d.CloudOnlyFiles);
            w.Write(d.DurationMs);
            w.Write(d.Engine ?? "");
            w.Write(d.EngineNote ?? "");

            // tree: iterative pre-order (no stack overflow on deep folder structures)
            w.Write(d.Root != null);
            if (d.Root != null)
            {
                var stack = new Stack<DirNode>();
                stack.Push(d.Root);
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    w.Write(n.Name ?? "");
                    w.Write(n.Size);
                    w.Write(n.OwnSize);
                    w.Write(n.Files);
                    w.Write(n.OwnFiles);
                    w.Write(n.Dirs);
                    w.Write(n.LastWrite);
                    w.Write(n.Flags);
                    int cc = n.Children != null ? n.Children.Count : 0;
                    w.Write(cc);
                    for (int i = cc - 1; i >= 0; i--) stack.Push(n.Children[i]);
                }
            }

            w.Write(d.LargestFiles.Count);
            foreach (var f in d.LargestFiles)
            {
                w.Write(f.Path ?? "");
                w.Write(f.Size);
                w.Write(f.LastWrite);
            }

            // many distinct extensions (e.g. x.000001 ... x.999999) must not bloat the report: only the largest KeepListItems are written
            var exts = d.Extensions.Count <= KeepListItems ? d.Extensions
                : d.Extensions.OrderByDescending(x => x.Size).Take(KeepListItems).ToList();
            w.Write(exts.Count);
            foreach (var e in exts)
            {
                w.Write(e.Ext ?? "");
                w.Write(e.Size);
                w.Write(e.Count);
            }
        }

        // ------------------------------------------------------------------ reading
        //
        // Report files are untrusted input (they come from remote machines and may be shared by e-mail).
        // The reader therefore limits counts, never pre-allocates capacity from counts in the file,
        // limits string lengths, sanitizes names and paths (PathText) and validates the root path.

        /// <summary>Maximum drives in one report.</summary>
        public const int MaxDrives = 64;
        /// <summary>Maximum folder nodes in one report (~1.5 GB memory ceiling).</summary>
        public const int MaxNodes = 15000000;
        /// <summary>Nodes per compressed byte (&lt; 2 in real reports; stops compression bombs).</summary>
        private const int MaxNodesPerByte = 16;
        private const int MinNodeBudget = 1000000;
        /// <summary>More list items than this means a corrupt file.</summary>
        public const int MaxListItems = 20000000;
        /// <summary>Maximum list items kept in memory (the rest is read and skipped; lists are sorted by size).</summary>
        public const int KeepListItems = 200000;
        private const int MaxDepth = short.MaxValue;
        private const int MaxStringBytes = 32767 * 3;   // longest Windows path, UTF-8

        public static ReportSummary ReadSummary(string path)
        {
            int ver;
            long len;
            using (var r = OpenReader(path, out ver, out len))
                return Guard(() => ReadSummary(r, ver));
        }

        public static ScanReport Load(string path)
        {
            int ver;
            long len;
            using (var r = OpenReader(path, out ver, out len))
            {
                return Guard(() =>
                {
                    var s = ReadSummary(r, ver);
                    var rep = new ScanReport
                    {
                        Machine = s.Machine,
                        ScanTimeUtc = s.ScanTimeUtc,
                        ScannerVersion = s.ScannerVersion,
                        OsVersion = s.OsVersion,
                        UserContext = s.UserContext
                    };
                    // budget per drive, so file servers with many large volumes can still be opened
                    long perDrive = Math.Min(MaxNodes, Math.Max(MinNodeBudget, len * MaxNodesPerByte));
                    for (int i = 0; i < s.Drives.Count; i++)
                    {
                        long budget = perDrive;
                        rep.Drives.Add(ReadDrive(r, ver, ref budget));
                    }
                    return rep;
                });
            }
        }

        /// <summary>Turns read errors on corrupt / truncated files into one clear error type.</summary>
        private static T Guard<T>(Func<T> f)
        {
            try { return f(); }
            catch (InvalidDataException) { throw; }
            catch (Exception ex) when (ex is EndOfStreamException || ex is IOException || ex is ArgumentException ||
                                       ex is OverflowException || ex is DecoderFallbackException || ex is OutOfMemoryException)
            {
                throw new InvalidDataException(L.T("Rapor dosyası bozuk veya geçersiz: ") + ex.Message, ex);
            }
        }

        private static BinaryReader OpenReader(string path, out int ver, out long length)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            try
            {
                length = fs.Length;
                var head = new byte[8];
                if (fs.Read(head, 0, 8) != 8 || head[0] != Magic[0] || head[1] != Magic[1] || head[2] != Magic[2] || head[3] != Magic[3])
                    throw new InvalidDataException(L.T("Geçerli bir DiskHawk raporu değil: ") + path);
                ver = BitConverter.ToInt32(head, 4);
                if (ver < 1 || ver > FormatVersion)
                    throw new InvalidDataException(L.T("Desteklenmeyen dosya sürümü: ") + ver);
                var gz = new GZipStream(fs, CompressionMode.Decompress, false);
                return new BinaryReader(new BufferedStream(gz, 1 << 16), Encoding.UTF8, false);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        // --- bounded read helpers

        /// <summary>BinaryWriter.Write(string) format (7-bit length + UTF-8), length limited.</summary>
        private static string Str(BinaryReader r, int maxBytes = MaxStringBytes)
        {
            int len = 0, shift = 0;
            while (true)
            {
                if (shift > 28) throw new InvalidDataException(L.T("Rapor dosyası bozuk (metin uzunluğu)"));
                byte b = r.ReadByte();
                len |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            if (len < 0 || len > maxBytes) throw new InvalidDataException(L.T("Rapor dosyası bozuk (metin çok uzun)"));
            if (len == 0) return "";
            var bytes = r.ReadBytes(len);
            if (bytes.Length != len) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(bytes);
        }

        private static string Text(BinaryReader r) { return PathText.SafeText(Str(r), 1024); }
        private static string PathStr(BinaryReader r) { return PathText.SafePath(Str(r)); }

        private static int Count(BinaryReader r, int max, string what)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > max) throw new InvalidDataException(string.Format(L.T("Rapor dosyası bozuk ({0} sayısı: {1})"), what, n));
            return n;
        }

        private static long NonNeg(long v) { return v < 0 ? 0 : v; }
        private static int NonNeg(int v) { return v < 0 ? 0 : v; }

        private static DateTime Time(BinaryReader r)
        {
            long v = r.ReadInt64();
            try { return DateTime.FromBinary(v); }
            catch (ArgumentException) { return DateTime.MinValue; }
        }

        /// <summary>Root path: must be "X:\..." (local drive) or "\\server\share...".</summary>
        private static string RootPath(string p)
        {
            var q = p != null && p.StartsWith(@"\\?\") ? p.Substring(4) : p;
            if (q != null && q.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) q = @"\\" + q.Substring(4);
            bool volumeGuid = q != null && q.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase);
            if (!PathText.IsDrivePath(q) && !PathText.IsUncPath(q) && !volumeGuid)
                throw new InvalidDataException(L.T("Rapor dosyası geçersiz kök yol içeriyor: ") + PathText.SafeText(p, 200));
            return p;
        }

        private static ReportSummary ReadSummary(BinaryReader r, int ver)
        {
            var s = new ReportSummary
            {
                Machine = PathText.SafeText(Str(r), 255),
                ScanTimeUtc = Time(r),
                ScannerVersion = Text(r),
                OsVersion = Text(r),
                UserContext = Text(r)
            };
            int n = Count(r, MaxDrives, L.T("sürücü"));
            for (int i = 0; i < n; i++)
            {
                var ds = new DriveSummary
                {
                    RootPath = RootPath(PathStr(r)),
                    FileSystem = Text(r),
                    TotalBytes = NonNeg(r.ReadInt64()),
                    FreeBytes = NonNeg(r.ReadInt64()),
                    ScannedBytes = NonNeg(r.ReadInt64()),
                    ScannedFiles = NonNeg(r.ReadInt64()),
                    ScannedDirs = NonNeg(r.ReadInt64()),
                    Errors = NonNeg(r.ReadInt32()),
                    CloudOnlyBytes = NonNeg(r.ReadInt64()),
                    DurationMs = NonNeg(r.ReadInt64())
                };
                ds.Engine = ver >= 2 ? Text(r) : "Klasik";
                ds.EngineNote = ver >= 3 ? Text(r) : "";
                ds.HotspotPath = PathStr(r);
                ds.HotspotSize = NonNeg(r.ReadInt64());
                ds.FileHotspotPath = PathStr(r);
                ds.FileHotspotCount = NonNeg(r.ReadInt32());
                ds.LargestFilePath = PathStr(r);
                ds.LargestFileSize = NonNeg(r.ReadInt64());
                s.Drives.Add(ds);
            }
            return s;
        }

        private sealed class Frame
        {
            public DirNode Node;
            public int Remaining;
        }

        private static DriveResult ReadDrive(BinaryReader r, int ver, ref long budget)
        {
            var d = new DriveResult
            {
                RootPath = RootPath(PathStr(r)),
                FileSystem = Text(r),
                VolumeLabel = Text(r),
                TotalBytes = NonNeg(r.ReadInt64()),
                FreeBytes = NonNeg(r.ReadInt64()),
                ScannedBytes = NonNeg(r.ReadInt64()),
                ScannedFiles = NonNeg(r.ReadInt64()),
                ScannedDirs = NonNeg(r.ReadInt64()),
                Errors = NonNeg(r.ReadInt32()),
                CloudOnlyBytes = NonNeg(r.ReadInt64()),
                CloudOnlyFiles = NonNeg(r.ReadInt32()),
                DurationMs = NonNeg(r.ReadInt64())
            };
            d.Engine = ver >= 2 ? Text(r) : "Klasik";
            d.EngineNote = ver >= 3 ? Text(r) : "";

            if (r.ReadBoolean())
            {
                int cc;
                var root = ReadNode(r, null, out cc);
                root.Name = RootPath(PathText.SafePath(root.Name));   // root node name = full path
                if (--budget < 0) throw TooMany();
                d.Root = root;
                var stack = new Stack<Frame>();
                long pending = 0;
                if (cc > 0)
                {
                    if ((pending += cc) > budget) throw TooMany();
                    root.Children = new List<DirNode>(Math.Min(cc, 1024));
                    stack.Push(new Frame { Node = root, Remaining = cc });
                }
                while (stack.Count > 0)
                {
                    var f = stack.Peek();
                    var child = ReadNode(r, f.Node, out cc);
                    child.Name = PathText.SafeName(child.Name);
                    budget--;
                    pending--;
                    f.Node.Children.Add(child);
                    if (--f.Remaining == 0) stack.Pop();
                    if (cc > 0)
                    {
                        // pending (not yet read) children also count against the budget: huge counts are rejected without allocating
                        if ((pending += cc) > budget) throw TooMany();
                        child.Children = new List<DirNode>(Math.Min(cc, 1024));
                        stack.Push(new Frame { Node = child, Remaining = cc });
                    }
                }
            }

            int nf = Count(r, MaxListItems, L.T("dosya"));
            d.LargestFiles = new List<FileEntry>(Math.Min(nf, 4096));
            for (int i = 0; i < nf; i++)
            {
                var fe = new FileEntry { Path = PathStr(r), Size = NonNeg(r.ReadInt64()), LastWrite = r.ReadInt64() };
                if (i < KeepListItems) d.LargestFiles.Add(fe);
            }

            int ne = Count(r, MaxListItems, L.T("uzantı"));
            d.Extensions = new List<ExtStat>(Math.Min(ne, 4096));
            for (int i = 0; i < ne; i++)
            {
                var es = new ExtStat { Ext = SafeExt(Str(r, 1024)), Size = NonNeg(r.ReadInt64()), Count = NonNeg(r.ReadInt32()) };
                if (i < KeepListItems) d.Extensions.Add(es);
            }

            return d;
        }

        private static string SafeExt(string e) { return e.Length == 0 ? "" : PathText.SafeName(e); }

        private static Exception TooMany()
        {
            return new InvalidDataException(L.T("Rapor dosyası çok fazla klasör içeriyor (bozuk veya güvenilmez dosya)"));
        }

        private static DirNode ReadNode(BinaryReader r, DirNode parent, out int childCount)
        {
            var n = new DirNode
            {
                Name = Str(r),
                Size = NonNeg(r.ReadInt64()),
                OwnSize = NonNeg(r.ReadInt64()),
                Files = NonNeg(r.ReadInt32()),
                OwnFiles = NonNeg(r.ReadInt32()),
                Dirs = NonNeg(r.ReadInt32()),
                LastWrite = r.ReadInt64(),
                Flags = r.ReadByte(),
                Parent = parent,
                Depth = parent == null ? (short)0 : (short)Math.Min(MaxDepth, parent.Depth + 1)   // a very deep tree is not rejected; the depth is clamped
            };
            childCount = r.ReadInt32();
            if (childCount < 0) throw new InvalidDataException(L.T("Rapor dosyası bozuk (negatif alt klasör sayısı)"));
            return n;
        }
    }
}
