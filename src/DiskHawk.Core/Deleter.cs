using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DiskHawk.Core
{
    public enum DeleteStatus
    {
        Deleted = 0,    // fully deleted
        Partial = 1,    // part of the folder was deleted (locked / inaccessible files remain)
        Failed = 2,     // nothing could be deleted
        Protected = 3,  // protected path, not touched
        NotFound = 4,   // already gone
        Unknown = 5     // no result received (remote process timeout etc.)
    }

    public enum PathLevel
    {
        Normal = 0,
        Critical = 1,   // system / profile path: deletable only with explicit confirmation
        Blocked = 2     // invalid path or an entire drive root: never
    }

    public sealed class DeleteResult
    {
        public string Path = "";
        public bool IsDirectory;
        public DeleteStatus Status;
        public long FreedBytes;
        public int FilesDeleted;
        public int FilesFailed;
        public string Error = "";

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case DeleteStatus.Deleted: return L.T("Silindi");
                    case DeleteStatus.Partial: return L.T("Kısmen silindi");
                    case DeleteStatus.Protected: return L.T("Korunuyor");
                    case DeleteStatus.NotFound: return L.T("Zaten yok");
                    case DeleteStatus.Unknown: return L.T("Sonuç bilinmiyor");
                    default: return L.T("Silinemedi");
                }
            }
        }
    }

    /// <summary>
    /// File/folder deletion engine. Runs on the target machine (locally). On Windows deletion is handle based
    /// (see DeleteWin32): supports long paths (\\?\),
    /// read-only files, junctions/symlinks (only the link is removed, the target is not entered) and
    /// locked files (skipped and reported). Protected paths are never touched.
    /// </summary>
    public sealed class Deleter
    {
        public long FilesDeleted;
        public long BytesFreed;
        public long Failed;
        public volatile string Current = "";

        /// <summary>Allow deleting critical (system/profile) paths. Drive roots are still never deleted.</summary>
        public bool AllowCritical;

        public List<DeleteResult> DeleteAll(IEnumerable<string> paths, CancellationToken ct)
        {
            var res = new List<DeleteResult>();
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (ct.IsCancellationRequested) break;
                DeleteResult r;
                // NO Trim: NTFS names may end with spaces ("Backup ") - trimming would delete a different folder
                var path = p.TrimEnd('\r', '\n');
                try { r = DeleteOne(path); }
                catch (Exception ex) { r = new DeleteResult { Path = path, Status = DeleteStatus.Failed, Error = ex.Message }; }
                res.Add(r);
            }
            return res;
        }

        public DeleteResult DeleteOne(string path)
        {
            var r = new DeleteResult { Path = path };
            string reason;
            var lvl = Classify(path, out reason);
            if (lvl == PathLevel.Blocked || (lvl == PathLevel.Critical && !AllowCritical))
            {
                r.Status = DeleteStatus.Protected;
                r.Error = lvl == PathLevel.Critical ? reason + L.T(" (kritik yol onayı verilmedi)") : reason;
                return r;
            }
            if (Native.IsWindows && path.IndexOf('~') >= 0)
            {
                // 8.3 short names (C:\PROGRA~1) must not slip past the protected list: expand and check again
                var sb = new StringBuilder(32768);
                uint n = GetLongPathNameW(LongPath(path.TrimEnd('\\')), sb, (uint)sb.Capacity);
                if (n > 0 && n < sb.Capacity)
                {
                    var longPath = sb.ToString();
                    if (longPath.StartsWith(@"\\?\")) longPath = longPath.Substring(4);
                    var l2 = Classify(longPath, out reason);
                    if (l2 == PathLevel.Blocked || (l2 == PathLevel.Critical && !AllowCritical))
                    {
                        r.Status = DeleteStatus.Protected;
                        r.Error = reason;
                        return r;
                    }
                }
            }
            Current = path;
            return Native.IsWindows ? DeleteWin32(path, r) : DeleteManaged(path, r);
        }

        // ================================================================== protected paths

        private static readonly string[] WholeTreeTop =
        {
            "Windows", "Program Files", "Program Files (x86)", "System Volume Information", "$Recycle.Bin",
            "Boot", "Recovery", "EFI", "$WinREAgent", "Config.Msi", "PerfLogs", "$SysReset", "$Windows.~BT", "$Windows.~WS"
        };

        private static readonly string[] NtfsMetaFiles =
        {
            "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "$Bitmap", "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend"
        };

        private static readonly string[] RootSystemFiles =
        {
            "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "DumpStack.log", "DumpStack.log.tmp"
        };

        private static readonly string[] ProfileSystemFolders =
        {
            "AppData", "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music", "Favorites",
            "Contacts", "Links", "Saved Games", "Searches", "3D Objects"
        };

        private static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        private static bool In(string s, string[] set)
        {
            foreach (var x in set) if (Eq(s, x)) return true;
            return false;
        }

        /// <summary>
        /// Path class: Normal (deleted directly), Critical (system/profile path; deleted only with AllowCritical),
        /// Blocked (invalid path or the drive root itself; never deleted). Checked both in the console and in the scanner on the target.
        /// The CONTENTS of a folder can be deleted, but the following folders themselves cannot:
        /// drive root, Users, profile root, system folders in a profile (Downloads, AppData\Local ...).
        /// Fully protected trees: Windows, Program Files, ProgramData\Microsoft, boot/recovery folders, Users\Default.
        /// </summary>
        public static PathLevel Classify(string path, out string reason)
        {
            reason = "";
            if (string.IsNullOrWhiteSpace(path)) { reason = L.T("Boş yol"); return PathLevel.Blocked; }
            // control characters such as line breaks could split one path into two in the deletion list
            if (PathText.HasControlChars(path)) { reason = L.T("Yol geçersiz (kontrol karakteri içeriyor)"); return PathLevel.Blocked; }
            var p = path;
            if (p.StartsWith(@"\\?\")) p = p.Substring(4);
            if (p.Length < 3 || p[1] != ':' || p[2] != '\\' || p.IndexOf('/') >= 0 ||
                p.Contains("\\..\\") || p.EndsWith("\\..") || p.Contains("\\.\\") || p.EndsWith("\\."))
            {
                reason = L.T("Tam yerel yol değil (X:\\... bekleniyor)");
                return PathLevel.Blocked;
            }
            if (p.IndexOf(':', 2) >= 0 || p.Substring(2).Contains("\\\\") || p.IndexOfAny(new[] { '*', '?', '<', '>', '|', '"' }) >= 0)
            {
                reason = L.T("Geçersiz yol");
                return PathLevel.Blocked;
            }
            var parts = p.TrimEnd('\\').Split('\\');
            if (parts.Length == 1) { reason = L.T("Sürücü kökü"); return PathLevel.Blocked; }

            string top = parts[1];
            if (In(top, NtfsMetaFiles)) { reason = L.T("NTFS iç dosyası"); return PathLevel.Blocked; }
            if (In(top, WholeTreeTop)) { reason = top + L.T(" sistem klasörü"); return PathLevel.Critical; }

            // in case the target machine's real Windows folder is somewhere else
            try
            {
                var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(win) && Native.IsWindows &&
                    (Eq(p.TrimEnd('\\'), win.TrimEnd('\\')) || p.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    reason = L.T("Windows sistem klasörü");
                    return PathLevel.Critical;
                }
            }
            catch { }

            if (parts.Length == 2 && In(top, RootSystemFiles)) { reason = L.T("Sistem dosyası"); return PathLevel.Critical; }

            if (Eq(top, "ProgramData"))
            {
                if (parts.Length == 2) { reason = L.T("ProgramData kökü"); return PathLevel.Critical; }
                if (Eq(parts[2], "Microsoft")) { reason = "ProgramData\\Microsoft"; return PathLevel.Critical; }
            }

            if (Eq(top, "Users"))
            {
                if (parts.Length == 2) { reason = L.T("Users kökü"); return PathLevel.Critical; }
                if (Eq(parts[2], "Default") || Eq(parts[2], "Default User") || Eq(parts[2], "All Users"))
                {
                    reason = L.T("Varsayılan profil");
                    return PathLevel.Critical;
                }
                if (parts.Length == 3) { reason = L.T("Kullanıcı profil klasörü (içindekiler silinebilir)"); return PathLevel.Critical; }
                if (parts.Length == 4)
                {
                    if (In(parts[3], ProfileSystemFolders))
                    {
                        reason = L.T("Profilin sistem klasörü (içindekiler silinebilir)");
                        return PathLevel.Critical;
                    }
                    if (parts[3].StartsWith("ntuser", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = L.T("Kullanıcı kayıt defteri dosyası");
                        return PathLevel.Critical;
                    }
                }
                if (parts.Length >= 4 && parts[3].StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase))
                {
                    reason = L.T("OneDrive klasörü: silme buluta da yansır (OneDrive'da 'Alan boşalt' kullanın)");
                    return PathLevel.Critical;
                }
                if (parts.Length == 5 && Eq(parts[3], "AppData") && (Eq(parts[4], "Local") || Eq(parts[4], "Roaming") || Eq(parts[4], "LocalLow")))
                {
                    reason = "AppData\\" + parts[4] + L.T(" klasörünün kendisi (içindekiler silinebilir)");
                    return PathLevel.Critical;
                }
            }
            return PathLevel.Normal;
        }

        /// <summary>Backward compatibility: everything other than Normal counts as "protected".</summary>
        public static bool IsProtected(string path, out string reason)
        {
            return Classify(path, out reason) != PathLevel.Normal;
        }

        // ================================================================== Windows (handle based)
        //
        // Classify() works on the path text. Path-based APIs (DeleteFileW / RemoveDirectoryW / FindFirstFileW)
        // resolve every component again, so a parent folder that is changed into a junction/symlink after the
        // check would make them operate on a different tree.
        //
        // Approach:
        //  1) The target is opened with CreateFileW + FILE_FLAG_OPEN_REPARSE_POINT. If the last component is a link,
        //     the link ITSELF is opened. NOTE: this flag applies only to the LAST component; junctions in parent
        //     folders are still followed by the kernel, which is why step 2 is the main protection.
        //  2) The real (resolved) path is read from the open handle with GetFinalPathNameByHandleW. If it is not
        //     identical to the requested path (junction in a parent, SUBST drive, 8.3 short name etc.) the item is REFUSED.
        //     Otherwise Classify() runs again on the verified path.
        //  3) From here on no I/O resolves path text: children are listed through the open folder handle
        //     (GetFileInformationByHandleEx / FileFullDirectoryInfo), opened with NtCreateFile(RootDirectory = parent handle,
        //     single-component name, FILE_OPEN_REPARSE_POINT) and deleted with SetFileInformationByHandle
        //     (FileDispositionInfoEx / FileDispositionInfo). If an item became a link in the meantime the link itself
        //     is opened; before descending into a subfolder its reparse state is checked on the handle.
        //  4) Folder handles are opened without FILE_SHARE_DELETE when possible, so other processes cannot
        //     rename / move these folders while the operation runs (extra hardening).
        //
        // Path strings (DirFrame.Path etc.) are kept ONLY for progress and error messages from here on.

        private static string LongPath(string p)
        {
            if (p.StartsWith(@"\\?\")) return p;
            if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
            return @"\\?\" + p;
        }

        /// <summary>Removes the \\?\ and \\?\UNC\ prefixes and trailing '\' (comparison form).</summary>
        private static string StripLongPrefix(string p)
        {
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p.Substring(8);
            else if (p.StartsWith(@"\\?\")) p = p.Substring(4);
            return p.TrimEnd('\\');
        }

        private sealed class DirFrame
        {
            public SafeFileHandle Handle;
            public string Path;                 // display / error text only; NOT used for I/O
            public List<string> SubDirs;
            public int Next;
        }

        private struct DirEntry
        {
            public string Name;
            public uint Attributes;
            public uint ReparseTag;
            public long Size;
        }

        private DeleteResult DeleteWin32(string path, DeleteResult r)
        {
            path = StripLongPrefix(path);

            // ---- 1) open without following the link
            int err;
            SafeFileHandle root = OpenRoot(path, out err);
            if (root == null)
            {
                if (err == Native.ERROR_FILE_NOT_FOUND || err == Native.ERROR_PATH_NOT_FOUND) { r.Status = DeleteStatus.NotFound; return r; }
                r.Status = DeleteStatus.Failed;
                r.Error = ErrText(err);
                return r;
            }

            IntPtr nameBuf = IntPtr.Zero, dirBuf = IntPtr.Zero;
            try
            {
                // ---- 2) verify the real path
                string finalPath;
                err = GetFinalPath(root, out finalPath);
                if (err != 0)
                {
                    r.Status = DeleteStatus.Failed;
                    r.Error = L.T("Gerçek yol doğrulanamadı: ") + ErrText(err);
                    return r;
                }
                if (!string.Equals(finalPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    r.Status = DeleteStatus.Protected;
                    r.Error = L.T("Yol reddedildi: gerçek konum farklı (junction / bağlantı / kısa ad) → ") + finalPath;
                    return r;
                }
                string reason;
                var lvl = Classify(finalPath, out reason);
                if (lvl == PathLevel.Blocked || (lvl == PathLevel.Critical && !AllowCritical))
                {
                    r.Status = DeleteStatus.Protected;
                    r.Error = lvl == PathLevel.Critical ? reason + L.T(" (kritik yol onayı verilmedi)") : reason;
                    return r;
                }

                // ---- type information: from the handle, not the path
                uint attr, tag;
                err = QueryAttrTag(root, out attr, out tag);
                if (err != 0) { r.Status = DeleteStatus.Failed; r.Error = ErrText(err); return r; }

                bool isDir = (attr & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                bool isLink = IsLinkTag(attr, tag);
                r.IsDirectory = isDir;

                if (IsCloud(attr, tag) || (isDir && (attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && !isLink))
                {
                    // OneDrive etc. cloud item: deleting removes it from the cloud too and frees no disk space
                    r.Status = DeleteStatus.Failed;
                    r.Error = L.T("Bulut / özel öğe atlandı (OneDrive vb.)");
                    return r;
                }

                if (!isDir)
                {
                    long size = 0;
                    FILE_STANDARD_INFO si;
                    if (GetFileInformationByHandleEx(root, FileStandardInfo, out si, (uint)Marshal.SizeOf(typeof(FILE_STANDARD_INFO))))
                        size = si.EndOfFile;
                    int e = MarkForDelete(root);
                    if (e == 0)
                    {
                        r.Status = DeleteStatus.Deleted;
                        r.FilesDeleted = 1;
                        r.FreedBytes = size;
                        Interlocked.Increment(ref FilesDeleted);
                        Interlocked.Add(ref BytesFreed, size);
                    }
                    else
                    {
                        r.Status = DeleteStatus.Failed;
                        r.FilesFailed = 1;
                        r.Error = ErrText(e);
                        Interlocked.Increment(ref Failed);
                    }
                    return r;
                }

                if (isLink)
                {
                    // junction / symlink: the handle belongs to the link itself (OPEN_REPARSE_POINT) -> only the link is removed
                    int e = MarkForDelete(root);
                    r.Status = e == 0 ? DeleteStatus.Deleted : DeleteStatus.Failed;
                    if (e != 0) r.Error = ErrText(e);
                    return r;
                }

                // ---- 3) handle-based tree deletion
                nameBuf = Marshal.AllocHGlobal(NameBufBytes);
                dirBuf = Marshal.AllocHGlobal(DirBufBytes);
                DeleteTree(root, path, r, nameBuf, dirBuf);
                return r;
            }
            finally
            {
                // with classic (non-POSIX) deletion the entry disappears when the handle is closed.
                root.Dispose();
                if (nameBuf != IntPtr.Zero) Marshal.FreeHGlobal(nameBuf);
                if (dirBuf != IntPtr.Zero) Marshal.FreeHGlobal(dirBuf);
            }
        }

        /// <summary>
        /// Iterative DFS. Each folder is listed via its handle, files/links are deleted immediately, subfolders
        /// are opened relative to the parent handle and pushed; an emptied folder is deleted via its own handle.
        /// Open handles at any time = depth.
        /// </summary>
        private void DeleteTree(SafeFileHandle rootHandle, string rootPath, DeleteResult r, IntPtr nameBuf, IntPtr dirBuf)
        {
            var stack = new Stack<DirFrame>();
            string firstErr = null;
            bool rootRemoved = false;
            try
            {
                var rootFrame = new DirFrame { Handle = rootHandle, Path = rootPath };
                ProcessDirectory(rootFrame, r, nameBuf, dirBuf, ref firstErr);
                stack.Push(rootFrame);

                while (stack.Count > 0)
                {
                    var top = stack.Peek();
                    if (top.Next < top.SubDirs.Count)
                    {
                        var name = top.SubDirs[top.Next++];
                        var child = OpenChildDirectory(top, name, r, nameBuf, ref firstErr);
                        if (child != null)
                        {
                            stack.Push(child); // push first: if an exception occurs, finally closes it
                            ProcessDirectory(child, r, nameBuf, dirBuf, ref firstErr);
                        }
                        continue;
                    }

                    // all children processed: delete the folder via its own handle
                    stack.Pop();
                    bool isRoot = ReferenceEquals(top.Handle, rootHandle);
                    Current = top.Path;
                    int e = MarkForDelete(top.Handle);
                    if (!isRoot) top.Handle.Dispose(); // the caller closes the root handle
                    if (e == 0) { if (isRoot) rootRemoved = true; }
                    else if (r.FilesFailed == 0 && firstErr == null) firstErr = top.Path + ": " + ErrText(e);
                }
            }
            finally
            {
                while (stack.Count > 0)
                {
                    var f = stack.Pop();
                    if (!ReferenceEquals(f.Handle, rootHandle)) f.Handle.Dispose();
                }
            }

            if (rootRemoved) r.Status = DeleteStatus.Deleted;
            else if (r.FilesDeleted > 0) r.Status = DeleteStatus.Partial;
            else r.Status = DeleteStatus.Failed;
            if (r.Status != DeleteStatus.Deleted)
                r.Error = (r.FilesFailed > 0 ? r.FilesFailed + L.T(" dosya silinemedi. ") : "") + (firstErr ?? "");
        }

        /// <summary>Lists a folder via its handle; deletes files and links, adds real subfolders to f.SubDirs.</summary>
        private void ProcessDirectory(DirFrame f, DeleteResult r, IntPtr nameBuf, IntPtr dirBuf, ref string firstErr)
        {
            Current = f.Path;
            f.SubDirs = new List<string>();
            f.Next = 0;

            // full listing first: listing while deleting can skip entries on NTFS
            var entries = new List<DirEntry>();
            int e = EnumerateDirectory(f.Handle, dirBuf, entries);
            if (e != 0)
            {
                r.FilesFailed++;
                if (firstErr == null) firstErr = f.Path + ": " + ErrText(e);
            }

            foreach (var en in entries)
            {
                var display = f.Path + "\\" + en.Name;
                bool d = (en.Attributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                bool rp = (en.Attributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                bool link = IsLinkTag(en.Attributes, en.ReparseTag);

                if (IsCloud(en.Attributes, en.ReparseTag) || (d && rp && !link))
                {
                    // cloud / special reparse item: do not touch (deleting would remove it from the cloud too)
                    r.FilesFailed++;
                    if (firstErr == null) firstErr = display + L.T(": bulut / özel öğe atlandı");
                    continue;
                }

                if (d && !link)
                {
                    f.SubDirs.Add(en.Name); // OpenChildDirectory re-verifies via the handle before descending
                    continue;
                }

                // file or link (file/folder): open relative to the parent handle without following the link, then delete
                e = DeleteChild(f.Handle, en.Name, d, nameBuf);
                if (e == 0)
                {
                    if (!d)
                    {
                        r.FilesDeleted++;
                        r.FreedBytes += en.Size;
                        Interlocked.Increment(ref FilesDeleted);
                        Interlocked.Add(ref BytesFreed, en.Size);
                    }
                }
                else if (e != Native.ERROR_FILE_NOT_FOUND && e != Native.ERROR_PATH_NOT_FOUND) // fine if it was deleted in the meantime
                {
                    r.FilesFailed++;
                    if (!d) Interlocked.Increment(ref Failed);
                    if (firstErr == null) firstErr = display + ": " + ErrText(e);
                }
            }
        }

        /// <summary>
        /// Opens a subfolder relative to the parent handle and verifies it via the handle. If it became a link,
        /// it is not entered: only the link is removed and null is returned. Returns a new frame for a real folder.
        /// </summary>
        private DirFrame OpenChildDirectory(DirFrame parent, string name, DeleteResult r, IntPtr nameBuf, ref string firstErr)
        {
            var display = parent.Path + "\\" + name;
            SafeFileHandle h;
            int e = OpenRelative(parent.Handle, name, true, true, nameBuf, out h);
            if (e != 0)
            {
                if (e != Native.ERROR_FILE_NOT_FOUND && e != Native.ERROR_PATH_NOT_FOUND)
                {
                    r.FilesFailed++;
                    if (firstErr == null) firstErr = display + ": " + ErrText(e);
                }
                return null;
            }

            uint attr, tag;
            e = QueryAttrTag(h, out attr, out tag);
            if (e == 0 && (attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                if (IsLinkTag(attr, tag) && !IsCloud(attr, tag))
                {
                    // turned into a junction between listing and opening: do NOT enter the target, remove the link
                    e = MarkForDelete(h);
                    h.Dispose();
                    if (e != 0) { r.FilesFailed++; if (firstErr == null) firstErr = display + ": " + ErrText(e); }
                    return null;
                }
                h.Dispose();
                r.FilesFailed++;
                if (firstErr == null) firstErr = display + L.T(": bulut / özel öğe atlandı");
                return null;
            }
            if (e != 0 || (attr & Native.FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                h.Dispose();
                r.FilesFailed++;
                if (firstErr == null) firstErr = display + ": " + ErrText(e != 0 ? e : ERROR_DIRECTORY);
                return null;
            }
            return new DirFrame { Handle = h, Path = display };
        }

        /// <summary>Opens a file or link relative to the parent handle (OPEN_REPARSE_POINT) and deletes it. 0 = success, otherwise a Win32 error code.</summary>
        private int DeleteChild(SafeFileHandle parent, string name, bool isDirLink, IntPtr nameBuf)
        {
            SafeFileHandle h;
            int e = OpenRelative(parent, name, isDirLink, false, nameBuf, out h);
            if (e != 0) return e;
            try { return MarkForDelete(h); }
            finally { h.Dispose(); } // the deletion is committed on close
        }

        // ------------------------------------------------------------------ opening

        private static SafeFileHandle OpenRoot(string path, out int err)
        {
            var lp = LongPath(path);
            const uint flags = FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT;
            // start with the widest access and back off (for a file, FILE_LIST_DIRECTORY = FILE_READ_DATA may be denied)
            uint[] accesses =
            {
                DELETE | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE,
                DELETE | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE,   // keeps listing access when write-attributes is not granted
                DELETE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE,
                DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE
            };
            // first without FILE_SHARE_DELETE (prevents rename/move during the operation)
            uint[] shares = { FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE };
            err = 0;
            foreach (var access in accesses)
            {
                foreach (var share in shares)
                {
                    var h = CreateFileW(lp, access, share, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
                    if (!h.IsInvalid) return h;
                    err = Marshal.GetLastWin32Error();
                    h.Dispose();
                    if (err != ERROR_SHARING_VIOLATION && err != Native.ERROR_ACCESS_DENIED) return null;
                }
            }
            return null;
        }

        /// <summary>
        /// Opens a single-component name relative to a parent folder handle (NtCreateFile + RootDirectory). The name
        /// cannot contain separators; links are not followed (FILE_OPEN_REPARSE_POINT). NO path text is resolved.
        /// </summary>
        private static int OpenRelative(SafeFileHandle parent, string name, bool directory, bool forTraversal, IntPtr nameBuf, out SafeFileHandle h)
        {
            h = null;
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameChars || name == "." || name == ".." ||
                name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0 || name.IndexOf('\0') >= 0 ||
                name.IndexOf(':') >= 0)   // "name:stream" would open an alternate data stream; a single-component name cannot contain ':'
                return ERROR_INVALID_NAME;

            // UNICODE_STRING + characters in one block: [Length:2][MaxLength:2][pad][Buffer:ptr][WCHAR...]
            IntPtr chars = IntPtr.Add(nameBuf, 2 * IntPtr.Size);
            Marshal.Copy(name.ToCharArray(), 0, chars, name.Length);
            Marshal.WriteInt16(nameBuf, 0, unchecked((short)(name.Length * 2)));
            Marshal.WriteInt16(nameBuf, 2, unchecked((short)(name.Length * 2)));
            Marshal.WriteIntPtr(nameBuf, IntPtr.Size, chars);

            uint options = FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT | FILE_OPEN_FOR_BACKUP_INTENT |
                           (directory ? FILE_DIRECTORY_FILE : FILE_NON_DIRECTORY_FILE);

            uint[] accesses = forTraversal
                ? new[]
                {
                    DELETE | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE,
                    DELETE | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE
                }
                : new[]
                {
                    DELETE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE,
                    DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE
                };
            // folders to descend into are opened without FILE_SHARE_DELETE (cannot be moved meanwhile); not needed for files/links
            uint[] shares = forTraversal
                ? new[] { FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE }
                : new[] { FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE };

            bool addRef = false;
            try
            {
                parent.DangerousAddRef(ref addRef);
                var oa = new OBJECT_ATTRIBUTES
                {
                    Length = Marshal.SizeOf(typeof(OBJECT_ATTRIBUTES)),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = nameBuf,
                    Attributes = 0,                 // the name comes verbatim from the listing: open case-sensitively
                    SecurityDescriptor = IntPtr.Zero,
                    SecurityQualityOfService = IntPtr.Zero
                };
                int err = 0;
                foreach (var access in accesses)
                {
                    foreach (var share in shares)
                    {
                        IO_STATUS_BLOCK iosb;
                        SafeFileHandle t;
                        int st = NtCreateFile(out t, access, ref oa, out iosb, IntPtr.Zero, 0, share, FILE_OPEN, options, IntPtr.Zero, 0);
                        if (st >= 0 && t != null && !t.IsInvalid) { h = t; return 0; }
                        if (t != null) t.Dispose();
                        err = (int)RtlNtStatusToDosError(st);
                        if (err != ERROR_SHARING_VIOLATION && err != Native.ERROR_ACCESS_DENIED) return err;
                    }
                }
                return err;
            }
            finally
            {
                if (addRef) parent.DangerousRelease();
            }
        }

        // ------------------------------------------------------------------ handle queries

        private static int GetFinalPath(SafeFileHandle h, out string finalPath)
        {
            finalPath = null;
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
            if (n >= sb.Capacity)
            {
                sb = new StringBuilder((int)n + 1);
                n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
            }
            if (n == 0) return Marshal.GetLastWin32Error();
            if (n >= sb.Capacity) return ERROR_INSUFFICIENT_BUFFER;
            finalPath = StripLongPrefix(sb.ToString());
            return 0;
        }

        private static int QueryAttrTag(SafeFileHandle h, out uint attr, out uint tag)
        {
            FILE_ATTRIBUTE_TAG_INFO ti;
            if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, out ti, (uint)Marshal.SizeOf(typeof(FILE_ATTRIBUTE_TAG_INFO))))
            {
                attr = 0; tag = 0;
                return Marshal.GetLastWin32Error();
            }
            attr = ti.FileAttributes;
            tag = (attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? ti.ReparseTag : 0;
            return 0;
        }

        /// <summary>
        /// Folder listing via a handle (FILE_FULL_DIR_INFO). For reparse items the EaSize field carries the reparse
        /// tag (MS-FSCC 2.4.14; this is where FindFirstFile's dwReserved0 comes from).
        /// </summary>
        private static int EnumerateDirectory(SafeFileHandle h, IntPtr buf, List<DirEntry> list)
        {
            int cls = FileFullDirectoryRestartInfo;
            while (true)
            {
                if (!GetFileInformationByHandleEx(h, cls, buf, (uint)DirBufBytes))
                {
                    int e = Marshal.GetLastWin32Error();
                    return (e == Native.ERROR_NO_MORE_FILES || e == Native.ERROR_FILE_NOT_FOUND) ? 0 : e;
                }
                cls = FileFullDirectoryInfo;
                int off = 0;
                while (true)
                {
                    IntPtr p = IntPtr.Add(buf, off);
                    int next = Marshal.ReadInt32(p, 0);
                    long eof = Marshal.ReadInt64(p, 40);
                    uint attr = unchecked((uint)Marshal.ReadInt32(p, 56));
                    int nameBytes = Marshal.ReadInt32(p, 60);
                    uint ea = unchecked((uint)Marshal.ReadInt32(p, 64));
                    if (nameBytes > 0 && off + 68 + nameBytes <= DirBufBytes)
                    {
                        string name = Marshal.PtrToStringUni(IntPtr.Add(p, 68), nameBytes / 2);
                        if (name != "." && name != "..")
                        {
                            list.Add(new DirEntry
                            {
                                Name = name,
                                Attributes = attr,
                                ReparseTag = (attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? ea : 0,
                                Size = eof
                            });
                        }
                    }
                    if (next <= 0 || off + next >= DirBufBytes) break;
                    off += next;
                }
            }
        }

        // ------------------------------------------------------------------ deletion (via handle)

        // FileDispositionInfoEx support level (per Deleter instance, drops once):
        // 2 = DELETE|POSIX|IGNORE_READONLY (Win10 1809+), 1 = DELETE|POSIX (1709+), 0 = classic FileDispositionInfo
        private int _dispLevel = 2;

        /// <summary>Marks the item behind the handle for deletion. With POSIX semantics the name disappears immediately.</summary>
        private int MarkForDelete(SafeFileHandle h)
        {
            while (_dispLevel > 0)
            {
                var ex = new FILE_DISPOSITION_INFO_EX
                {
                    Flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS |
                            (_dispLevel == 2 ? FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE : 0)
                };
                if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref ex, (uint)Marshal.SizeOf(typeof(FILE_DISPOSITION_INFO_EX))))
                    return 0;
                int e = Marshal.GetLastWin32Error();
                if (e == ERROR_INVALID_PARAMETER) { _dispLevel--; continue; }     // the operating system does not know this flag
                if (e == ERROR_NOT_SUPPORTED || e == ERROR_INVALID_FUNCTION) break; // this file system (FAT etc.): fall back to the classic way
                if (e == Native.ERROR_ACCESS_DENIED && _dispLevel == 1) break;      // may be read-only: the classic way clears the attribute
                return e;
            }
            return MarkForDeleteLegacy(h);
        }

        private static int MarkForDeleteLegacy(SafeFileHandle h)
        {
            var d = new FILE_DISPOSITION_INFO { DeleteFile = 1 };
            uint dSize = (uint)Marshal.SizeOf(typeof(FILE_DISPOSITION_INFO));
            if (SetFileInformationByHandle(h, FileDispositionInfo, ref d, dSize)) return 0;
            int e = Marshal.GetLastWin32Error();
            if (e != Native.ERROR_ACCESS_DENIED) return e;

            // clear the read-only attribute through the same handle; restore the old attribute on failure
            uint bSize = (uint)Marshal.SizeOf(typeof(FILE_BASIC_INFO));
            FILE_BASIC_INFO bi;
            if (!GetFileInformationByHandleEx(h, FileBasicInfo, out bi, bSize) || (bi.FileAttributes & FILE_ATTRIBUTE_READONLY) == 0)
                return e;
            uint old = bi.FileAttributes;
            uint cleared = old & ~FILE_ATTRIBUTE_READONLY;
            var nb = new FILE_BASIC_INFO { FileAttributes = cleared == 0 ? FILE_ATTRIBUTE_NORMAL : cleared }; // time fields 0 = do not change
            if (!SetFileInformationByHandle(h, FileBasicInfo, ref nb, bSize)) return e;
            if (SetFileInformationByHandle(h, FileDispositionInfo, ref d, dSize)) return 0;
            e = Marshal.GetLastWin32Error();
            nb.FileAttributes = old;
            SetFileInformationByHandle(h, FileBasicInfo, ref nb, bSize);
            return e;
        }

        private const uint NameSurrogateBit = 0x20000000;

        /// <summary>Is this a link "pointing elsewhere" (junction / symlink / mount point)?</summary>
        private static bool IsLinkTag(uint attr, uint tag)
        {
            return (attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && (tag & NameSurrogateBit) != 0;
        }

        /// <summary>Is this a OneDrive/Cloud Files item (online-only, or a cloud reparse tag)?</summary>
        private static bool IsCloud(uint attr, uint tag)
        {
            if ((attr & (Native.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Native.FILE_ATTRIBUTE_RECALL_ON_OPEN | Native.FILE_ATTRIBUTE_OFFLINE)) != 0) return true;
            return (attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && (tag & 0xFFFF0FFF) == 0x9000001A; // IO_REPARSE_TAG_CLOUD_*
        }

        public static string ErrText(int e)
        {
            switch (e)
            {
                case 2: case 3: return L.T("Bulunamadı");
                case 5: return L.T("Erişim reddedildi");
                case 32: return L.T("Dosya kullanımda (açık)");
                case 33: return L.T("Dosyanın bir kısmı kilitli");
                case 145: return L.T("Klasör boş değil (silinemeyen dosyalar var)");
                case 206: return L.T("Yol çok uzun");
                default: return L.T("Win32 hata ") + e;
            }
        }

        // ------------------------------------------------------------------ P/Invoke

        private const int MaxNameChars = 32767;
        private static readonly int NameBufBytes = 2 * IntPtr.Size + MaxNameChars * 2;
        private const int DirBufBytes = 64 * 1024;

        // access rights
        private const uint DELETE = 0x00010000;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint FILE_LIST_DIRECTORY = 0x0001;
        private const uint FILE_READ_ATTRIBUTES = 0x0080;
        private const uint FILE_WRITE_ATTRIBUTES = 0x0100;

        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint FILE_SHARE_DELETE = 0x4;

        // CreateFileW
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

        // NtCreateFile
        private const uint FILE_OPEN = 0x1;
        private const uint FILE_DIRECTORY_FILE = 0x00000001;
        private const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x00000020;
        private const uint FILE_NON_DIRECTORY_FILE = 0x00000040;
        private const uint FILE_OPEN_FOR_BACKUP_INTENT = 0x00004000;
        private const uint FILE_OPEN_REPARSE_POINT = 0x00200000;

        // GetFinalPathNameByHandleW
        private const uint FILE_NAME_NORMALIZED = 0x0;
        private const uint VOLUME_NAME_DOS = 0x0;

        // FILE_INFO_BY_HANDLE_CLASS
        private const int FileBasicInfo = 0;
        private const int FileStandardInfo = 1;
        private const int FileDispositionInfo = 4;
        private const int FileAttributeTagInfo = 9;
        private const int FileFullDirectoryInfo = 14;
        private const int FileFullDirectoryRestartInfo = 15;
        private const int FileDispositionInfoEx = 21;

        private const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
        private const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2;
        private const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

        private const uint FILE_ATTRIBUTE_READONLY = 0x1;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        private const int ERROR_INVALID_FUNCTION = 1;
        private const int ERROR_SHARING_VIOLATION = 32;
        private const int ERROR_NOT_SUPPORTED = 50;
        private const int ERROR_INVALID_PARAMETER = 87;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const int ERROR_INVALID_NAME = 123;
        private const int ERROR_DIRECTORY = 267;

        [StructLayout(LayoutKind.Sequential)]
        private struct OBJECT_ATTRIBUTES
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;          // PUNICODE_STRING
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_STATUS_BLOCK
        {
            public IntPtr Status;
            public IntPtr Information;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_BASIC_INFO
        {
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public long ChangeTime;
            public uint FileAttributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_STANDARD_INFO
        {
            public long AllocationSize;
            public long EndOfFile;
            public uint NumberOfLinks;
            public byte DeletePending;
            public byte Directory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_ATTRIBUTE_TAG_INFO
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_DISPOSITION_INFO
        {
            public byte DeleteFile;            // BOOLEAN (1 byte)
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_DISPOSITION_INFO_EX
        {
            public uint Flags;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder filePath, uint cchFilePath, uint flags);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, IntPtr buffer, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FILE_ATTRIBUTE_TAG_INFO info, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FILE_STANDARD_INFO info, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FILE_BASIC_INFO info, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FILE_DISPOSITION_INFO info, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FILE_DISPOSITION_INFO_EX info, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FILE_BASIC_INFO info, uint bufferSize);

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtCreateFile(out SafeFileHandle fileHandle, uint desiredAccess, ref OBJECT_ATTRIBUTES objectAttributes,
            out IO_STATUS_BLOCK ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess,
            uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern uint RtlNtStatusToDosError(int status);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);

        // ================================================================== non-Windows (tests)

        private DeleteResult DeleteManaged(string path, DeleteResult r)
        {
            try
            {
                if (File.Exists(path))
                {
                    long size = new FileInfo(path).Length;
                    File.Delete(path);
                    r.Status = DeleteStatus.Deleted;
                    r.FilesDeleted = 1;
                    r.FreedBytes = size;
                    return r;
                }
                if (!Directory.Exists(path)) { r.Status = DeleteStatus.NotFound; return r; }
                r.IsDirectory = true;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { long s = new FileInfo(f).Length; File.Delete(f); r.FilesDeleted++; r.FreedBytes += s; }
                    catch (Exception ex) { r.FilesFailed++; if (r.Error.Length == 0) r.Error = f + ": " + ex.Message; }
                }
                try { Directory.Delete(path, true); r.Status = DeleteStatus.Deleted; }
                catch (Exception ex)
                {
                    r.Status = r.FilesDeleted > 0 ? DeleteStatus.Partial : DeleteStatus.Failed;
                    if (r.Error.Length == 0) r.Error = ex.Message;
                }
            }
            catch (Exception ex)
            {
                r.Status = DeleteStatus.Failed;
                r.Error = ex.Message;
            }
            return r;
        }

        // ================================================================== result file

        /// <summary>Result format between scanner and console (TSV, UTF-8). Written atomically via .tmp + rename.</summary>
        public static void WriteResults(IList<DeleteResult> results, string file)
        {
            var sb = new StringBuilder();
            foreach (var r in results)
            {
                sb.Append((int)r.Status).Append('\t')
                  .Append(r.IsDirectory ? "1" : "0").Append('\t')
                  .Append(r.FreedBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r.FilesDeleted.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r.FilesFailed.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(Clean(r.Path)).Append('\t')
                  .Append(Clean(r.Error)).Append('\n');
            }
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        /// <summary>Result file written by the remote scanner (untrusted input): corrupt lines are skipped, values clamped.</summary>
        private const long MaxResultFileBytes = 64L * 1024 * 1024;

        public static List<DeleteResult> ReadResults(string file)
        {
            var list = new List<DeleteResult>();
            if (new FileInfo(file).Length > MaxResultFileBytes)
                throw new InvalidDataException(L.T("Silme sonuç dosyası beklenmedik kadar büyük; okunmadı. Makineyi yeniden tarayın."));
            foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
            {
                var p = line.Split('\t');
                if (p.Length < 7) continue;
                int st, fd, ff;
                long freed;
                if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out st) ||
                    !long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out freed) ||
                    !int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out fd) ||
                    !int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out ff))
                    continue;
                if (st < 0 || st > (int)DeleteStatus.Unknown) st = (int)DeleteStatus.Unknown;
                list.Add(new DeleteResult
                {
                    Status = (DeleteStatus)st,
                    IsDirectory = p[1] == "1",
                    FreedBytes = Math.Max(0, freed),
                    FilesDeleted = Math.Max(0, fd),
                    FilesFailed = Math.Max(0, ff),
                    Path = p[5],
                    Error = PathText.SafeText(p[6], 2048)
                });
            }
            return list;
        }

        /// <summary>
        /// Reads the deletion list file. Lists written by the console start with "#DHLIST1 &lt;count&gt;" and the
        /// count is verified (nothing is deleted if the list is corrupt / split). Lists without a header (hand-made) are also accepted.
        /// </summary>
        public static List<string> ReadList(string file)
        {
            var lines = new List<string>(File.ReadAllLines(file, Encoding.UTF8));
            const string header = "#DHLIST1 ";
            if (lines.Count > 0 && lines[0].StartsWith(header, StringComparison.Ordinal))
            {
                int expected;
                if (!int.TryParse(lines[0].Substring(header.Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out expected))
                    throw new InvalidDataException(L.T("Silme listesi başlığı bozuk"));
                lines.RemoveAt(0);
                while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
                if (lines.Count != expected)
                    throw new InvalidDataException(string.Format(L.T("Silme listesi bozuk: {0} yol bekleniyordu, {1} satır var. Hiçbir şey silinmedi."), expected, lines.Count));
            }
            return lines;
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
