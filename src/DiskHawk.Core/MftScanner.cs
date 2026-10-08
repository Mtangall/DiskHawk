using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DiskHawk.Core
{
    /// <summary>Raw disk reader (a file for tests, a \\.\C: volume handle on Windows).</summary>
    public interface IVolumeReader : IDisposable
    {
        /// <summary>Reads exactly count bytes from offset (offset/count must be sector aligned).</summary>
        void Read(long offset, byte[] buffer, int bufferOffset, int count);
    }

    /// <summary>Reads from a disk image file (tests / image analysis).</summary>
    public sealed class FileVolumeReader : IVolumeReader
    {
        private readonly FileStream _fs;
        public FileVolumeReader(string path) { _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16); }

        public void Read(long offset, byte[] buffer, int bufferOffset, int count)
        {
            _fs.Position = offset;
            int done = 0;
            while (done < count)
            {
                int n = _fs.Read(buffer, bufferOffset + done, count - done);
                if (n <= 0) { Array.Clear(buffer, bufferOffset + done, count - done); return; }
                done += n;
            }
        }

        public void Dispose() { _fs.Dispose(); }
    }

    /// <summary>Raw reads through a Windows volume handle. Requires administrator rights.</summary>
    public sealed class Win32VolumeReader : IVolumeReader
    {
        private readonly IntPtr _h;

        public Win32VolumeReader(char driveLetter)
        {
            _h = CreateFileW(@"\\.\" + char.ToUpperInvariant(driveLetter) + ":", GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (_h == INVALID_HANDLE_VALUE)
                throw new IOException(L.T("Volume açılamadı (") + Marshal.GetLastWin32Error() + L.T(") - yönetici yetkisi gerekli"));
        }

        public void Read(long offset, byte[] buffer, int bufferOffset, int count)
        {
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                long newPos;
                if (!SetFilePointerEx(_h, offset, out newPos, 0))
                    throw new IOException(L.T("Konumlanamadı (") + Marshal.GetLastWin32Error() + ")");
                int done = 0;
                IntPtr basePtr = pin.AddrOfPinnedObject();
                while (done < count)
                {
                    int read;
                    if (!ReadFile(_h, IntPtr.Add(basePtr, bufferOffset + done), count - done, out read, IntPtr.Zero))
                        throw new IOException(L.T("Okuma hatası (") + Marshal.GetLastWin32Error() + ")");
                    if (read == 0) { Array.Clear(buffer, bufferOffset + done, count - done); return; }
                    done += read;
                }
            }
            finally { pin.Free(); }
        }

        public void Dispose() { if (_h != INVALID_HANDLE_VALUE) CloseHandle(_h); }

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
        private const uint OPEN_EXISTING = 3;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(IntPtr h, IntPtr buffer, int toRead, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFilePointerEx(IntPtr h, long distance, out long newPos, uint method);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr h);
    }

    /// <summary>
    /// Scan engine that reads the raw $MFT (the WizTree approach). Extracts every file record on the drive
    /// by reading the $MFT sequentially instead of walking directories. Works only on an NTFS drive root with
    /// administrator rights; otherwise DiskScanner falls back to the classic engine.
    /// </summary>
    public static class MftScanner
    {
        private const uint AttrStandardInfo = 0x10;
        private const uint AttrAttributeList = 0x20;
        private const uint AttrFileName = 0x30;
        private const uint AttrData = 0x80;
        private const uint AttrReparse = 0xC0;
        private const uint AttrEnd = 0xFFFFFFFF;
        private const int RootRecord = 5;
        private const uint NameSurrogateBit = 0x20000000;

        private const byte FInUse = 1, FDir = 2, FName = 4, FNameWin32 = 8;

        private struct Extent
        {
            public long Lcn;   // -1 = sparse
            public long Len;   // cluster
        }

        /// <summary>Tries to scan a drive root with the MFT engine on Windows.</summary>
        public static bool TryScan(string rootPath, ScanOptions opt, ScanProgress progress, CancellationToken ct,
                                   out DriveResult result, out string whyNot)
        {
            result = null;
            whyNot = null;
            if (!Native.IsWindows) { whyNot = L.T("Windows değil"); return false; }
            if (rootPath == null || rootPath.Length != 3 || rootPath[1] != ':' || rootPath[2] != '\\') { whyNot = L.T("Sürücü kökü değil"); return false; }
            try
            {
                var di = new DriveInfo(rootPath.Substring(0, 1));
                if (!string.Equals(di.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) { whyNot = L.T("NTFS değil (") + di.DriveFormat + ")"; return false; }
            }
            catch (Exception ex) { whyNot = ex.Message; return false; }

            try
            {
                char letter = rootPath[0];
                result = ScanVolume(() => new Win32VolumeReader(letter), rootPath, opt, progress, ct);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                whyNot = ex.Message;
                return false;
            }
        }

        // ================================================================== main scan

        /// <param name="openReader">Opens a new reader on every call (for parallel reads every thread uses its own handle).</param>
        public static DriveResult ScanVolume(Func<IVolumeReader> openReader, string rootName, ScanOptions opt, ScanProgress progress, CancellationToken ct)
        {
            var swAll = Stopwatch.StartNew();
            int bps, recordSize;
            long clusterSize, mftDataSize;
            List<Extent> runs;
            byte[] bitmap;

            using (var vol = openReader())
            {
                // --- boot sector
                var boot = new byte[65536];
                vol.Read(0, boot, 0, boot.Length);
                if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ") throw new InvalidDataException(L.T("NTFS boot sektörü değil"));
                bps = U16(boot, 0x0B);
                int spcRaw = boot[0x0D];
                long spc = spcRaw <= 0x80 ? spcRaw : 1L << (256 - spcRaw);
                clusterSize = bps * spc;
                long mftLcn = I64(boot, 0x30);
                int cpr = (sbyte)boot[0x40];
                recordSize = cpr > 0 ? (int)(cpr * clusterSize) : 1 << (-cpr);
                long volSectors = I64(boot, 0x28);
                // NTFS: sector 256..4096 (power of two), cluster at most 2 MB; the $MFT location must be inside the volume
                if (bps < 256 || bps > 4096 || (bps & (bps - 1)) != 0 || (spcRaw > 0x80 && 256 - spcRaw > 12) ||
                    clusterSize <= 0 || clusterSize > (2L << 20) || recordSize < 256 || recordSize > 65536 ||
                    mftLcn <= 0 || volSectors <= 0 || mftLcn > volSectors * bps / clusterSize)
                    throw new InvalidDataException(L.T("NTFS geometrisi geçersiz"));

                // --- the $MFT's own $DATA extents + usage bitmap (record 0)
                runs = ReadMftRuns(vol, mftLcn, clusterSize, recordSize, bps, out mftDataSize, out bitmap);
                long volBytes = I64(boot, 0x28) * bps;
                if (volBytes > 0 && mftDataSize > volBytes) throw new InvalidDataException(L.T("$MFT boyutu birim boyutundan büyük (bozuk birim)"));
            }
            long total = mftDataSize / recordSize;
            if (total <= RootRecord || total > int.MaxValue) throw new InvalidDataException(L.T("$MFT boyutu geçersiz"));
            int n = (int)total;

            // --- compact per-record arrays
            var parent = new int[n];
            var size = new long[n];
            var lw = new long[n];
            var attr = new uint[n];
            var tag = new uint[n];
            var flags = new byte[n];
            var nameOff = new int[n];
            var nameLen = new ushort[n];
            var seq = new ushort[n];        // sequence number of the record (to detect reuse)
            var pseq = new ushort[n];       // sequence number in the parent reference
            var pool = new NamePool((int)Math.Min((long)n * 12, 1 << 27));   // avoid n*12 int overflow; the pool grows if needed
            for (int i = 0; i < n; i++) parent[i] = -1;

            // --- read plan: 4 MB chunks; chunks without any used record (per bitmap) are skipped
            const int Chunk = 4 << 20;
            int recsPerChunk = Chunk / recordSize;
            long mftBytes = (long)n * recordSize;
            var chunks = new List<int>();
            int chunkCount = (int)((n + (long)recsPerChunk - 1) / recsPerChunk);
            for (int c = 0; c < chunkCount; c++)
            {
                int r0 = c * recsPerChunk, r1 = (int)Math.Min(n, (long)r0 + recsPerChunk);
                if (bitmap == null || AnySet(bitmap, r0, r1)) chunks.Add(c);
            }

            // --- parallel reads (each reader has its own handle), parsing on one thread (overlapping with reads)
            int readers = Math.Max(1, Math.Min(opt.MftReaders, chunks.Count));
            var queue = new BlockingCollection<KeyValuePair<int, byte[]>>(readers * 2);
            var free = new ConcurrentBag<byte[]>();
            int next = -1, alive = readers;
            Exception readErr = null;
            long bytesRead = 0;
            progress.CurrentPath = rootName + "$MFT";

            var threads = new Thread[readers];
            for (int t = 0; t < readers; t++)
            {
                threads[t] = new Thread(() =>
                {
                    try
                    {
                        using (var v = openReader())
                        {
                            while (!ct.IsCancellationRequested && Volatile.Read(ref readErr) == null)
                            {
                                int ci = Interlocked.Increment(ref next);
                                if (ci >= chunks.Count) break;
                                byte[] b;
                                if (!free.TryTake(out b)) b = new byte[Chunk];
                                int c = chunks[ci];
                                long pos = (long)c * Chunk;
                                int want = (int)Math.Min(Chunk, mftBytes - pos);
                                int aligned = (int)((want + bps - 1) / bps * bps);
                                ReadVirtual(v, runs, clusterSize, pos, b, aligned);
                                Interlocked.Add(ref bytesRead, aligned);
                                queue.Add(new KeyValuePair<int, byte[]>(c, b), ct);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Interlocked.CompareExchange(ref readErr, ex, null); }
                    finally
                    {
                        if (Interlocked.Decrement(ref alive) == 0) queue.CompleteAdding();
                    }
                }) { IsBackground = true, Name = "mft-read-" + t };
                threads[t].Start();
            }

            long parsed = 0;
            try
            {
                foreach (var item in queue.GetConsumingEnumerable(ct))
                {
                    int c = item.Key;
                    var b = item.Value;
                    int baseIdx = c * recsPerChunk;
                    int cnt = (int)Math.Min(recsPerChunk, n - (long)baseIdx);
                    for (int k = 0; k < cnt; k++)
                        ParseRecord(b, k * recordSize, recordSize, baseIdx + k, n,
                            parent, size, lw, attr, tag, flags, nameOff, nameLen, seq, pseq, pool);
                    free.Add(b);
                    parsed += cnt;
                    Interlocked.Exchange(ref progress.Files, parsed);
                }
            }
            finally
            {
                foreach (var th in threads) th.Join();
            }
            ct.ThrowIfCancellationRequested();
            if (readErr != null) throw new IOException(L.T("$MFT okunamadı: ") + readErr.Message, readErr);
            long msRead = swAll.ElapsedMilliseconds;

            // --- tree
            // treat as orphan if the parent reference points to a reused record (deleted folder)
            for (int i = 0; i < n; i++)
            {
                int p = parent[i];
                if (p >= 0 && p < n && i != RootRecord && seq[p] != 0 && pseq[i] != 0 && seq[p] != pseq[i]) parent[i] = -1;
            }
            var result = BuildTree(rootName, opt, progress, ct, n, parent, size, lw, attr, tag, flags, nameOff, nameLen, pool);
            long msTree = swAll.ElapsedMilliseconds - msRead;
            result.EngineNote = string.Format(L.T("$MFT {0} MB, {1:N0} kayıt; okunan {2} MB ({3}/{4} blok), {5} okuyucu; okuma+işleme {6} ms, ağaç {7} ms"),
                mftBytes >> 20, n, bytesRead >> 20, chunks.Count, chunkCount, readers, msRead, msTree);
            return result;
        }

        private static bool AnySet(byte[] bm, int r0, int r1)
        {
            int b0 = r0 >> 3, b1 = Math.Min(bm.Length, (r1 + 7) >> 3);
            for (int i = b0; i < b1; i++) if (bm[i] != 0) return true;
            return false;
        }

        // ================================================================== $MFT extents

        private const uint AttrBitmap = 0xB0;
        private const long MaxBitmapBytes = 1L << 28;       // enough even for 2^31 records
        private const long MaxAttrListBytes = 16L << 20;

        private static List<Extent> ReadMftRuns(IVolumeReader vol, long mftLcn, long clusterSize, int recordSize, int bps, out long dataSize, out byte[] bitmap)
        {
            bitmap = null;
            int readLen = (int)Math.Max(clusterSize, recordSize);
            readLen = (readLen + bps - 1) / bps * bps;
            var rec = new byte[readLen];
            vol.Read(mftLcn * clusterSize, rec, 0, readLen);
            if (!IsFile(rec, 0) || !ApplyFixup(rec, 0, recordSize)) throw new InvalidDataException(L.T("$MFT kaydı okunamadı"));

            dataSize = 0;
            var runs = new List<Extent>();
            byte[] attrList = null;
            int attrListLen = 0;

            foreach (var a in Attributes(rec, 0, recordSize))
            {
                uint type = U32(rec, a);
                if (type == AttrData && rec[a + 9] == 0 && rec[a + 8] != 0 && I64(rec, a + 0x10) == 0)
                {
                    dataSize = I64(rec, a + 0x30);
                    DecodeRuns(rec, a + U16(rec, a + 0x20), a + (int)U32(rec, a + 4), runs);
                }
                else if (type == AttrBitmap && rec[a + 9] == 0)
                {
                    try
                    {
                        if (rec[a + 8] == 0)
                        {
                            int co = a + U16(rec, a + 0x14);
                            long len = U32(rec, a + 0x10);
                            if (len <= 0 || co + len > recordSize) throw new InvalidDataException();
                            bitmap = new byte[len];
                            Buffer.BlockCopy(rec, co, bitmap, 0, (int)len);
                        }
                        else
                        {
                            var br = new List<Extent>();
                            DecodeRuns(rec, a + U16(rec, a + 0x20), a + (int)U32(rec, a + 4), br);
                            long bmSize = I64(rec, a + 0x30);
                            if (bmSize <= 0 || bmSize > MaxBitmapBytes) throw new InvalidDataException();
                            int aligned = (int)((bmSize + bps - 1) / bps * bps);
                            var tmp = new byte[aligned];
                            ReadVirtual(vol, br, clusterSize, 0, tmp, aligned);
                            bitmap = new byte[bmSize];
                            Buffer.BlockCopy(tmp, 0, bitmap, 0, (int)bmSize);
                        }
                    }
                    catch { bitmap = null; } // if the bitmap cannot be read the whole $MFT is read
                }
                else if (type == AttrAttributeList)
                {
                    if (rec[a + 8] == 0)
                    {
                        int co = a + U16(rec, a + 0x14);
                        long al = U32(rec, a + 0x10);
                        if (al < 0 || co + al > recordSize) throw new InvalidDataException(L.T("$MFT attribute list bozuk"));
                        attrListLen = (int)al;
                        attrList = new byte[attrListLen];
                        Buffer.BlockCopy(rec, co, attrList, 0, attrListLen);
                    }
                    else
                    {
                        var lr = new List<Extent>();
                        DecodeRuns(rec, a + U16(rec, a + 0x20), a + (int)U32(rec, a + 4), lr);
                        long dataLen = I64(rec, a + 0x30);
                        long alloc = I64(rec, a + 0x28);
                        // real attribute lists are a few hundred KB; huge values mean a corrupt/crafted volume
                        if (dataLen < 0 || alloc < dataLen || alloc > MaxAttrListBytes) throw new InvalidDataException(L.T("$MFT attribute list bozuk"));
                        attrListLen = (int)dataLen;
                        attrList = new byte[(int)((alloc + bps - 1) / bps * bps) + bps];
                        ReadVirtual(vol, lr, clusterSize, 0, attrList, (int)((alloc + bps - 1) / bps * bps));
                    }
                }
            }
            if (runs.Count == 0 || dataSize <= 0) throw new InvalidDataException(L.T("$MFT $DATA bulunamadı"));

            // fragmented $MFT: the rest of $DATA lives in extension records (attribute list)
            if (attrList != null)
            {
                var ext = new List<KeyValuePair<long, long>>(); // (startVcn, recordNo)
                int p = 0;
                while (p + 0x1A <= attrListLen)
                {
                    uint t = U32(attrList, p);
                    int len = U16(attrList, p + 4);
                    if (len <= 0) break;
                    long startVcn = I64(attrList, p + 8);
                    long recNo = I64(attrList, p + 0x10) & 0xFFFFFFFFFFFFL;
                    if (t == AttrData && attrList[p + 6] == 0 && recNo != 0 && startVcn > 0) ext.Add(new KeyValuePair<long, long>(startVcn, recNo));
                    p += len;
                }
                ext.Sort((x, y) => x.Key.CompareTo(y.Key));
                var er = new byte[(recordSize + bps - 1) / bps * bps];
                foreach (var e in ext)
                {
                    ReadVirtual(vol, runs, clusterSize, e.Value * recordSize, er, er.Length);
                    if (!IsFile(er, 0) || !ApplyFixup(er, 0, recordSize)) continue;
                    foreach (var a in Attributes(er, 0, recordSize))
                    {
                        if (U32(er, a) == AttrData && er[a + 9] == 0 && er[a + 8] != 0 && I64(er, a + 0x10) == e.Key)
                        {
                            // continuation extents are decoded from their own start, not relative to the previous last LCN
                            DecodeRuns(er, a + U16(er, a + 0x20), a + (int)U32(er, a + 4), runs);
                        }
                    }
                }
            }
            return runs;
        }

        /// <summary>NTFS runlist decoding. Each attribute's runlist starts at LCN=0 on its own.</summary>
        private static void DecodeRuns(byte[] b, int p, int end, List<Extent> runs)
        {
            long lcn = 0;
            while (p < end && b[p] != 0)
            {
                int h = b[p++];
                int lenBytes = h & 0x0F, offBytes = h >> 4;
                if (lenBytes == 0 || lenBytes > 8 || offBytes > 8 || p + lenBytes + offBytes > end) break;
                long len = 0;
                for (int i = 0; i < lenBytes; i++) len |= (long)b[p + i] << (8 * i);
                p += lenBytes;
                if (offBytes == 0)
                {
                    runs.Add(new Extent { Lcn = -1, Len = len });
                }
                else
                {
                    long delta = 0;
                    for (int i = 0; i < offBytes; i++) delta |= (long)b[p + i] << (8 * i);
                    if ((b[p + offBytes - 1] & 0x80) != 0 && offBytes < 8) delta |= -1L << (8 * offBytes);
                    p += offBytes;
                    lcn += delta;
                    runs.Add(new Extent { Lcn = lcn, Len = len });
                }
            }
        }

        /// <summary>Reads from a virtual $MFT offset (VCN based); splits at extent boundaries.</summary>
        private static void ReadVirtual(IVolumeReader vol, List<Extent> runs, long clusterSize, long vpos, byte[] buf, int count)
        {
            int done = 0;
            long extStart = 0;
            foreach (var r in runs)
            {
                long extBytes = r.Len * clusterSize;
                long extEnd = extStart + extBytes;
                while (done < count && vpos + done >= extStart && vpos + done < extEnd)
                {
                    long inExt = vpos + done - extStart;
                    int piece = (int)Math.Min(count - done, extEnd - (vpos + done));
                    if (r.Lcn < 0) Array.Clear(buf, done, piece);
                    else
                    {
                        long at = r.Lcn * clusterSize + inExt;
                        if (at < 0) throw new InvalidDataException(L.T("$MFT extent listesi bozuk"));
                        vol.Read(at, buf, done, piece);
                    }
                    done += piece;
                }
                if (done >= count) return;
                extStart = extEnd;
            }
            if (done < count) Array.Clear(buf, done, count - done);
        }

        // ================================================================== record parsing

        private static bool IsFile(byte[] b, int off)
        {
            return b[off] == (byte)'F' && b[off + 1] == (byte)'I' && b[off + 2] == (byte)'L' && b[off + 3] == (byte)'E';
        }

        /// <summary>Update Sequence Array fixup (NTFS checks the end of every 512-byte block).</summary>
        private static bool ApplyFixup(byte[] b, int off, int recordSize)
        {
            int usaOff = U16(b, off + 4);
            int usaCount = U16(b, off + 6);
            if (usaOff < 0x28 || usaCount < 2 || usaOff + usaCount * 2 > recordSize) return false;
            int usn = U16(b, off + usaOff);
            for (int i = 1; i < usaCount; i++)
            {
                int pos = off + i * 512 - 2;
                if (pos + 2 > off + recordSize) break;
                if (U16(b, pos) != usn) return false;
                b[pos] = b[off + usaOff + i * 2];
                b[pos + 1] = b[off + usaOff + i * 2 + 1];
            }
            return true;
        }

        private static IEnumerable<int> Attributes(byte[] b, int off, int recordSize)
        {
            int a = off + U16(b, off + 0x14);
            int used = (int)Math.Min(U32(b, off + 0x18), (uint)recordSize);
            int end = off + used;
            while (a + 16 <= end)
            {
                uint type = U32(b, a);
                if (type == AttrEnd) yield break;
                int len = (int)U32(b, a + 4);
                if (len < 16 || a + len > end) yield break;
                yield return a;
                a += len;
            }
        }

        private static void ParseRecord(byte[] b, int off, int recordSize, int idx, int n,
            int[] parent, long[] size, long[] lw, uint[] attr, uint[] tag, byte[] flags,
            int[] nameOff, ushort[] nameLen, ushort[] seq, ushort[] pseq, NamePool pool)
        {
            if (!IsFile(b, off)) return;
            int rf = U16(b, off + 0x16);
            if ((rf & 1) == 0) return;                         // unused (deleted) record
            if (!ApplyFixup(b, off, recordSize)) return;

            long baseRefFull = I64(b, off + 0x20);
            long baseRef = baseRefFull & 0xFFFFFFFFFFFFL;
            int t;
            if (baseRef == 0)
            {
                t = idx;
                seq[t] = (ushort)U16(b, off + 0x10);
                flags[t] |= FInUse;
                if ((rf & 2) != 0) flags[t] |= FDir;
            }
            else
            {
                if (baseRef >= n) return;
                t = (int)baseRef;                               // extension record: data belongs to the base record
            }

            int a = off + U16(b, off + 0x14);
            int used = (int)Math.Min(U32(b, off + 0x18), (uint)recordSize);
            int end = off + used;
            while (a + 16 <= end)
            {
                uint type = U32(b, a);
                if (type == AttrEnd) break;
                int len = (int)U32(b, a + 4);
                if (len < 16 || a + len > end) break;
                bool nonRes = b[a + 8] != 0;
                int attrNameLen = b[a + 9];

                if (type == AttrStandardInfo && !nonRes)
                {
                    int co = a + U16(b, a + 0x14);
                    if (co + 0x24 <= end)
                    {
                        lw[t] = I64(b, co + 0x08);
                        attr[t] = U32(b, co + 0x20);
                    }
                }
                else if (type == AttrFileName && !nonRes)
                {
                    int co = a + U16(b, a + 0x14);
                    if (co + 0x42 <= end)
                    {
                        int nl = b[co + 0x40];
                        int ns = b[co + 0x41];
                        if (co + 0x42 + nl * 2 <= end && nl > 0)
                        {
                            bool have = (flags[t] & FName) != 0;
                            bool haveWin32 = (flags[t] & FNameWin32) != 0;
                            bool isDosOnly = ns == 2;
                            // take the first Win32/POSIX name; use the DOS name only if there is nothing else. Other names (hard links) are not counted.
                            if (!have || (!haveWin32 && !isDosOnly))
                            {
                                long pref = I64(b, co);
                                parent[t] = (int)(pref & 0xFFFFFFFFFFFFL);
                                pseq[t] = (ushort)((ulong)pref >> 48);
                                nameOff[t] = pool.Add(b, co + 0x42, nl);
                                nameLen[t] = (ushort)nl;
                                flags[t] |= FName;
                                if (!isDosOnly) flags[t] |= FNameWin32;
                                uint fnAttr = U32(b, co + 0x38);
                                if ((fnAttr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && tag[t] == 0) tag[t] = U32(b, co + 0x3C);
                            }
                        }
                    }
                }
                else if (type == AttrData && attrNameLen == 0)
                {
                    if (!nonRes) size[t] = U32(b, a + 0x10);
                    else if (I64(b, a + 0x10) == 0) size[t] = I64(b, a + 0x30);
                }
                else if (type == AttrReparse && !nonRes)
                {
                    int co = a + U16(b, a + 0x14);
                    if (co + 4 <= end) tag[t] = U32(b, co);
                }
                a += len;
            }
        }

        // ================================================================== tree

        private static DriveResult BuildTree(string rootName, ScanOptions opt, ScanProgress progress, CancellationToken ct, int n,
            int[] parent, long[] size, long[] lw, uint[] attr, uint[] tag, byte[] flags, int[] nameOff, ushort[] nameLen, NamePool pool)
        {
            ct.ThrowIfCancellationRequested();
            var result = new DriveResult { RootPath = rootName };
            var root = new DirNode { Name = rootName, Depth = 0 };
            result.Root = root;
            var nodes = new DirNode[n];
            nodes[RootRecord] = root;

            // 1) folder nodes
            for (int i = 0; i < n; i++)
            {
                if (i == RootRecord) continue;
                var f = flags[i];
                if ((f & (FInUse | FDir | FName)) != (FInUse | FDir | FName)) continue;
                // junction / symlink / mount point: the target is counted elsewhere (same as the classic engine)
                if ((attr[i] & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && (tag[i] & NameSurrogateBit) != 0) continue;
                nodes[i] = new DirNode { Name = PathText.SafeName(pool.Get(nameOff[i], nameLen[i])), Depth = -1 };
            }

            // 2) parent links
            for (int i = 0; i < n; i++)
            {
                var d = nodes[i];
                if (d == null || i == RootRecord) continue;
                int p = parent[i];
                if (p < 0 || p >= n || p == i) continue;
                var pn = nodes[p];
                if (pn == null) continue;
                d.Parent = pn;
                if (pn.Children == null) pn.Children = new List<DirNode>();
                pn.Children.Add(d);
            }

            // 3) mark what is reachable from the root (depth); the rest is orphaned / corrupt -> skipped
            var q = new Queue<DirNode>();
            q.Enqueue(root);
            long dirCount = 0;
            while (q.Count > 0)
            {
                var d = q.Dequeue();
                dirCount++;
                if (d.Children == null) continue;
                foreach (var c in d.Children)
                {
                    if (c.Depth >= 0) continue;          // loop protection
                    c.Depth = (short)Math.Min(short.MaxValue, d.Depth + 1);
                    q.Enqueue(c);
                }
            }
            Interlocked.Exchange(ref progress.Dirs, dirCount);

            // 4) files
            var ext = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
            var largeIdx = new List<int>();
            long largeMin = opt.LargeFileThreshold;
            long bytes = 0, files = 0;
            for (int i = 0; i < n; i++)
            {
                var f = flags[i];
                if ((f & (FInUse | FName)) != (FInUse | FName) || (f & FDir) != 0) continue;
                int p = parent[i];
                if (p < 0 || p >= n) continue;
                var d = nodes[p];
                if (d == null || d.Depth < 0) continue;

                long s = size[i];
                long w = lw[i] > 0 && lw[i] <= DirNode.MaxFileTime ? lw[i] : 0;
                if (w > d.LastWrite) d.LastWrite = w;

                if ((attr[i] & (Native.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Native.FILE_ATTRIBUTE_OFFLINE)) != 0)
                {
                    result.CloudOnlyBytes += s;
                    result.CloudOnlyFiles++;
                    continue;
                }
                d.OwnSize += s;
                d.OwnFiles++;
                bytes += s;
                files++;

                // extension
                int nl = nameLen[i], no = nameOff[i];
                int dot = pool.LastDot(no, nl);
                string e = (dot > 0 && dot < nl - 1 && nl - dot <= 16) ? pool.Get(no + dot + 1, nl - dot - 1) : "";
                ExtStat st;
                if (!ext.TryGetValue(e, out st)) { st = new ExtStat { Ext = e.Length == 0 ? "" : PathText.SafeName(e.ToLowerInvariant()) }; ext[e] = st; }
                st.Size += s;
                st.Count++;

                // NTFS metafiles in the root ($MFT, $LogFile...) appear in the tree but not in the "largest files" list
                bool meta = p == RootRecord && nl > 0 && pool.CharAt(no) == '$';
                if (s >= largeMin && !meta) largeIdx.Add(i);
            }
            Interlocked.Exchange(ref progress.Bytes, bytes);
            Interlocked.Exchange(ref progress.Files, files);

            // 5) folder totals
            for (int i = 0; i < n; i++)
            {
                var d = nodes[i];
                if (d == null || d.Depth < 0) continue;
                d.Size = d.OwnSize;
                d.Files = d.OwnFiles;
                if (d.Children != null)
                {
                    d.Children.RemoveAll(c => c.Depth < 0);
                    d.Dirs = d.Children.Count;
                }
            }
            DiskScanner.Aggregate(root);

            // 6) largest files
            largeIdx.Sort((x, y) => size[y].CompareTo(size[x]));
            int take = Math.Min(largeIdx.Count, opt.MaxLargeFiles);
            for (int k = 0; k < take; k++)
            {
                int i = largeIdx[k];
                var d = nodes[parent[i]];
                string dirPath = d.FullPath;
                string fname = PathText.SafeName(pool.Get(nameOff[i], nameLen[i]));
                string path = dirPath.EndsWith("\\") ? dirPath + fname : dirPath + "\\" + fname;
                long w = lw[i] > 0 && lw[i] <= DirNode.MaxFileTime ? lw[i] : 0;
                result.LargestFiles.Add(new FileEntry { Path = path, Size = size[i], LastWrite = w });
            }

            result.Extensions = new List<ExtStat>(ext.Values);
            result.Extensions.Sort((a, b) => b.Size.CompareTo(a.Size));
            result.ScannedBytes = root.Size;
            result.ScannedFiles = root.Files;
            result.ScannedDirs = root.Dirs + 1;
            return result;
        }

        // ================================================================== helpers

        private sealed class NamePool
        {
            private char[] _c;
            private int _len;

            public NamePool(int initial) { _c = new char[Math.Max(1024, initial)]; }

            public int Add(byte[] b, int off, int chars)
            {
                if (_len + chars > _c.Length)
                {
                    long ns = Math.Max((long)_c.Length * 2, (long)_len + chars + 1024);
                    if (ns > int.MaxValue - 64) ns = int.MaxValue - 64;
                    Array.Resize(ref _c, (int)ns);
                }
                int start = _len;
                for (int i = 0; i < chars; i++) _c[_len++] = (char)(b[off + 2 * i] | (b[off + 2 * i + 1] << 8));
                return start;
            }

            public string Get(int off, int len) { return new string(_c, off, len); }

            public char CharAt(int off) { return _c[off]; }

            public int LastDot(int off, int len)
            {
                for (int i = len - 1; i >= 0; i--) if (_c[off + i] == '.') return i;
                return -1;
            }
        }

        private static int U16(byte[] b, int o) { return b[o] | (b[o + 1] << 8); }
        private static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
        private static long I64(byte[] b, int o) { return (long)U32(b, o) | ((long)U32(b, o + 4) << 32); }
    }
}
