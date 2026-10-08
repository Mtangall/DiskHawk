using System;
using System.Collections.Generic;

namespace DiskHawk.Core
{
    public static class Analysis
    {
        /// <summary>
        /// "Hotspot": descends from the root into the largest subfolder, then keeps descending while the largest child
        /// holds at least <paramref name="ratio"/> of its parent.
        /// The result is the folder where the space really concentrates (e.g. C:\Users\ann\AppData\Local\Microsoft\Outlook).
        /// </summary>
        public static DirNode FindHotspot(DirNode root, Func<DirNode, long> metric, double ratio = 0.5)
        {
            if (root == null) return null;
            if (!root.HasChildren) return root;
            var cur = MaxChild(root, metric);
            if (cur == null || metric(cur) <= 0) return root;
            while (cur.HasChildren)
            {
                var m = MaxChild(cur, metric);
                if (m == null || metric(m) < metric(cur) * ratio) break;
                cur = m;
            }
            return cur;
        }

        private static DirNode MaxChild(DirNode n, Func<DirNode, long> metric)
        {
            DirNode best = null;
            long bestV = -1;
            foreach (var c in n.Children)
            {
                var v = metric(c);
                if (v > bestV) { bestV = v; best = c; }
            }
            return best;
        }

        public static ReportSummary BuildSummary(ScanReport r)
        {
            var s = new ReportSummary
            {
                Machine = r.Machine,
                ScanTimeUtc = r.ScanTimeUtc,
                ScannerVersion = r.ScannerVersion,
                OsVersion = r.OsVersion,
                UserContext = r.UserContext
            };
            foreach (var d in r.Drives)
            {
                var ds = new DriveSummary
                {
                    RootPath = d.RootPath,
                    FileSystem = d.FileSystem,
                    TotalBytes = d.TotalBytes,
                    FreeBytes = d.FreeBytes,
                    ScannedBytes = d.ScannedBytes,
                    ScannedFiles = d.ScannedFiles,
                    ScannedDirs = d.ScannedDirs,
                    Errors = d.Errors,
                    CloudOnlyBytes = d.CloudOnlyBytes,
                    DurationMs = d.DurationMs,
                    Engine = d.Engine ?? "",
                    EngineNote = d.EngineNote ?? ""
                };
                var hs = FindHotspot(d.Root, n => n.Size);
                if (hs != null) { ds.HotspotPath = hs.FullPath; ds.HotspotSize = hs.Size; }
                var fh = FindHotspot(d.Root, n => n.Files);
                if (fh != null) { ds.FileHotspotPath = fh.FullPath; ds.FileHotspotCount = fh.Files; }
                if (d.LargestFiles.Count > 0)
                {
                    ds.LargestFilePath = d.LargestFiles[0].Path;
                    ds.LargestFileSize = d.LargestFiles[0].Size;
                }
                s.Drives.Add(ds);
            }
            return s;
        }

        /// <summary>Finds a tree node by full path (case-insensitive). Returns the nearest ancestor if not found.</summary>
        public static DirNode FindNode(DirNode root, string fullPath, out bool exact)
        {
            exact = false;
            if (root == null || string.IsNullOrEmpty(fullPath)) return root;
            var rootPath = root.Name.TrimEnd('\\');
            if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return root;
            var rest = fullPath.Substring(rootPath.Length).Trim('\\');
            var cur = root;
            if (rest.Length == 0) { exact = true; return cur; }
            foreach (var part in rest.Split('\\'))
            {
                if (part.Length == 0) continue;
                DirNode next = null;
                if (cur.Children != null)
                    foreach (var c in cur.Children)
                        if (string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
                if (next == null) return cur;
                cur = next;
            }
            exact = true;
            return cur;
        }

        /// <summary>Flattens the tree (for CSV) with depth and minimum size filters.</summary>
        public static IEnumerable<DirNode> Flatten(DirNode root, int maxDepth, long minSize)
        {
            var stack = new Stack<DirNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.Size < minSize) continue;
                yield return n;
                if (n.Children != null && n.Depth < maxDepth)
                    for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }
    }
}
