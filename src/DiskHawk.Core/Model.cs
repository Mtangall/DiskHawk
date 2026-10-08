using System;
using System.Collections.Generic;
using System.Text;

namespace DiskHawk.Core
{
    /// <summary>A folder in the scan tree.</summary>
    public sealed class DirNode
    {
        /// <summary>The largest FILETIME a DateTime can represent (year 9999).</summary>
        public const long MaxFileTime = 2650467743999999999;

        public static DateTime? FileTimeToLocal(long ft)
        {
            return ft > 0 && ft <= MaxFileTime ? (DateTime?)DateTime.FromFileTimeUtc(ft).ToLocalTime() : null;
        }

        public const byte FlagAccessDenied = 1;
        public const byte FlagError = 2;

        public string Name;
        public DirNode Parent;
        public List<DirNode> Children;   // null = no subfolders

        public long Size;       // total size including subfolders (bytes)
        public long OwnSize;    // files directly in this folder only
        public int Files;       // file count including subfolders
        public int OwnFiles;    // files directly in this folder only
        public int Dirs;        // number of all subfolders (including descendants)
        public long LastWrite;  // newest file in the subtree (FILETIME UTC), 0 = none
        public short Depth;
        public byte Flags;

        public bool HasChildren { get { return Children != null && Children.Count > 0; } }
        public bool IsAccessDenied { get { return (Flags & FlagAccessDenied) != 0; } }

        public string FullPath
        {
            get
            {
                if (Parent == null) return Name;
                var parts = new List<string>();
                for (var n = this; n != null; n = n.Parent) parts.Add(n.Name);
                var sb = new StringBuilder();
                for (int i = parts.Count - 1; i >= 0; i--)
                {
                    sb.Append(parts[i]);
                    if (i > 0 && sb[sb.Length - 1] != '\\') sb.Append('\\');
                }
                return sb.ToString();
            }
        }

        public DateTime? LastWriteLocal
        {
            get { return FileTimeToLocal(LastWrite); }
        }

        public override string ToString() { return Name; }
    }

    public sealed class FileEntry
    {
        public string Path;
        public long Size;
        public long LastWrite; // FILETIME UTC

        public string FileName
        {
            get { int i = Path.LastIndexOf('\\'); return i >= 0 ? Path.Substring(i + 1) : Path; }
        }

        public string Directory
        {
            get { int i = Path.LastIndexOf('\\'); return i >= 0 ? Path.Substring(0, i) : ""; }
        }

        public DateTime? LastWriteLocal
        {
            get { return DirNode.FileTimeToLocal(LastWrite); }
        }
    }

    public sealed class ExtStat
    {
        public string Ext;   // lower case, without dot; "" = no extension
        public long Size;
        public int Count;
    }

    /// <summary>Scan result of one drive / root folder.</summary>
    public sealed class DriveResult
    {
        public string RootPath;
        public string FileSystem = "";
        public string VolumeLabel = "";
        public long TotalBytes;
        public long FreeBytes;

        public DirNode Root;
        public List<FileEntry> LargestFiles = new List<FileEntry>();
        public List<ExtStat> Extensions = new List<ExtStat>();

        public long ScannedBytes;
        public long ScannedFiles;
        public long ScannedDirs;
        public int Errors;
        public long CloudOnlyBytes;   // OneDrive etc. "online-only" files (take no disk space)
        public int CloudOnlyFiles;
        public long DurationMs;
        public string Engine = "";      // "MFT" or "Klasik" (classic)
        public string EngineNote = "";  // MFT: timing breakdown; classic: why MFT could not be used

        public long UsedBytes { get { return Math.Max(0, TotalBytes - FreeBytes); } }
        public double UsedPercent { get { return TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0; } }
    }

    /// <summary>A machine's full report (.dhr file).</summary>
    public sealed class ScanReport
    {
        public string Machine = "";
        public DateTime ScanTimeUtc;
        public string ScannerVersion = "";
        public string OsVersion = "";
        public string UserContext = "";
        public List<DriveResult> Drives = new List<DriveResult>();
    }

    /// <summary>Lightweight summary at the start of the file; readable without loading the tree.</summary>
    public sealed class ReportSummary
    {
        public string Machine = "";
        public DateTime ScanTimeUtc;
        public string ScannerVersion = "";
        public string OsVersion = "";
        public string UserContext = "";
        public List<DriveSummary> Drives = new List<DriveSummary>();

        /// <summary>The fullest drive (shown in the list view).</summary>
        public DriveSummary Primary
        {
            get
            {
                DriveSummary best = null;
                foreach (var d in Drives)
                    if (best == null || d.UsedPercent > best.UsedPercent) best = d;
                return best;
            }
        }
    }

    public sealed class DriveSummary
    {
        public string RootPath = "";
        public string FileSystem = "";
        public long TotalBytes;
        public long FreeBytes;
        public long ScannedBytes;
        public long ScannedFiles;
        public long ScannedDirs;
        public int Errors;
        public long CloudOnlyBytes;
        public long DurationMs;
        public string Engine = "";
        public string EngineNote = "";

        public string HotspotPath = "";      // folder where the space concentrates
        public long HotspotSize;
        public string FileHotspotPath = "";  // folder where the file count concentrates
        public int FileHotspotCount;
        public string LargestFilePath = "";
        public long LargestFileSize;

        public long UsedBytes { get { return Math.Max(0, TotalBytes - FreeBytes); } }
        public double UsedPercent { get { return TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0; } }
    }
}
