using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using DiskHawk.Core;

namespace DiskHawk.Scanner
{
    /// <summary>
    /// Standalone scanner that runs on the target machine. The console copies it via admin$ and
    /// starts it via WMI; it can also be used manually.
    /// </summary>
    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitError = 2;
        private const int ExitCancelled = 3;
        private const int ExitUsage = 64;

        private static int Main(string[] args)
        {
            Options o;
            try
            {
                o = Options.Parse(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                Console.Error.WriteLine();
                PrintHelp();
                return ExitUsage;
            }
            L.Lang = o.Lang;
            if (o.Help) { PrintHelp(); return ExitOk; }
            if (o.DeleteList != null) return RunDelete(o);

            string outFile = ResolveOutput(o.Output);
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            try
            {
                if (o.BelowNormal)
                {
                    try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                }

                var roots = o.Roots.Count > 0 ? o.Roots : DiskScanner.GetFixedDrives();
                if (roots.Count == 0) throw new InvalidOperationException("No drives to scan.");

                var scanner = new DiskScanner(new ScanOptions
                {
                    Threads = o.Threads > 0 ? o.Threads : ScanOptions.DefaultThreads,
                    LargeFileThreshold = o.MinFileMb * 1024L * 1024L,
                    MaxLargeFiles = o.MaxFiles,
                    LowIoPriority = o.LowIo,
                    Engine = o.Engine
                });

                Timer progressTimer = null;
                if (!o.Quiet || o.ProgressFile != null)
                {
                    progressTimer = new Timer(_ => ReportProgress(scanner.Progress, o), null, 500, o.Quiet ? 2000 : 500);
                }

                ScanReport report;
                try
                {
                    report = scanner.ScanAll(roots, cts.Token, r =>
                    {
                        if (!o.Quiet) Console.WriteLine("Scanning: " + r);
                    });
                }
                finally
                {
                    if (progressTimer != null) progressTimer.Dispose();
                }

                if (!o.Quiet) Console.WriteLine();

                ReportSerializer.Save(report, outFile);

                if (o.CsvDir != null)
                {
                    foreach (var f in CsvExporter.ExportReport(report, o.CsvDir))
                        if (!o.Quiet) Console.WriteLine("CSV: " + f);
                }

                if (!o.Quiet) PrintSummary(report, outFile);
                TryDelete(o.ProgressFile);
                return ExitOk;
            }
            catch (OperationCanceledException)
            {
                WriteError(outFile, "Scan cancelled.");
                return ExitCancelled;
            }
            catch (Exception ex)
            {
                WriteError(outFile, ex.ToString());
                if (!o.Quiet) Console.Error.WriteLine("Hata: " + ex.Message);
                return ExitError;
            }
        }

        /// <summary>Delete mode: deletes the listed paths on this machine and writes the result as TSV.</summary>
        private static int RunDelete(Options o)
        {
            string outFile = Path.GetFullPath(o.DeleteLog ?? (o.DeleteList + ".result"));
            try
            {
                var paths = Deleter.ReadList(o.DeleteList);
                var d = new Deleter { AllowCritical = o.AllowCritical };
                var sw = Stopwatch.StartNew();
                Timer t = null;
                if (o.ProgressFile != null)
                {
                    t = new Timer(_ =>
                    {
                        try
                        {
                            File.WriteAllText(o.ProgressFile, string.Join("|", new[]
                            {
                                Interlocked.Read(ref d.FilesDeleted).ToString(CultureInfo.InvariantCulture), "0",
                                Interlocked.Read(ref d.BytesFreed).ToString(CultureInfo.InvariantCulture),
                                Interlocked.Read(ref d.Failed).ToString(CultureInfo.InvariantCulture),
                                sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture), d.Current
                            }));
                        }
                        catch { }
                    }, null, 500, 1500);
                }
                List<DeleteResult> results;
                try { results = d.DeleteAll(paths, CancellationToken.None); }
                finally { if (t != null) t.Dispose(); }

                Deleter.WriteResults(results, outFile);
                TryDelete(o.ProgressFile);
                if (!o.Quiet)
                {
                    foreach (var r in results)
                        Console.WriteLine("{0,-14} {1,10}  {2}  {3}", r.StatusText, Fmt.Size(r.FreedBytes), r.Path, r.Error);
                    Console.WriteLine("Total freed: " + Fmt.Size(d.BytesFreed));
                }
                return ExitOk;
            }
            catch (Exception ex)
            {
                WriteError(outFile, ex.ToString());
                if (!o.Quiet) Console.Error.WriteLine("Hata: " + ex.Message);
                return ExitError;
            }
        }

        private static string ResolveOutput(string output)
        {
            string auto = Environment.MachineName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ReportSerializer.Extension;
            if (string.IsNullOrEmpty(output)) return Path.GetFullPath(auto);
            if (Directory.Exists(output) || output.EndsWith("\\") || output.EndsWith("/"))
            {
                Directory.CreateDirectory(output);
                return Path.GetFullPath(Path.Combine(output, auto));
            }
            var dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return Path.GetFullPath(output);
        }

        private static void ReportProgress(ScanProgress p, Options o)
        {
            long files = Interlocked.Read(ref p.Files);
            long dirs = Interlocked.Read(ref p.Dirs);
            long bytes = Interlocked.Read(ref p.Bytes);
            long errs = Interlocked.Read(ref p.Errors);
            long ms = p.Watch.ElapsedMilliseconds;

            if (o.ProgressFile != null)
            {
                try
                {
                    File.WriteAllText(o.ProgressFile, string.Join("|", new[]
                    {
                        files.ToString(CultureInfo.InvariantCulture),
                        dirs.ToString(CultureInfo.InvariantCulture),
                        bytes.ToString(CultureInfo.InvariantCulture),
                        errs.ToString(CultureInfo.InvariantCulture),
                        ms.ToString(CultureInfo.InvariantCulture),
                        p.RootPath
                    }));
                }
                catch { /* the reader may be holding the file */ }
            }

            if (!o.Quiet)
            {
                string line = string.Format("\r  {0}  {1} dosya  {2} klasor  {3}  hata:{4}  ",
                    p.RootPath, Fmt.Count(files), Fmt.Count(dirs), Fmt.Size(bytes), errs);
                try
                {
                    int w = Console.WindowWidth - 1;
                    if (w > 20 && line.Length > w) line = line.Substring(0, w);
                    Console.Write(line.PadRight(Math.Max(0, Math.Min(w, 120))));
                }
                catch { Console.Write(line); }
            }
        }

        private static void PrintSummary(ScanReport r, string outFile)
        {
            var s = Analysis.BuildSummary(r);
            Console.WriteLine();
            Console.WriteLine("Machine: " + r.Machine);
            foreach (var d in s.Drives)
            {
                Console.WriteLine();
                Console.WriteLine("  {0}  {1}  total {2}, free {3} ({4} used)", d.RootPath, d.FileSystem,
                    Fmt.Size(d.TotalBytes), Fmt.Size(d.FreeBytes), Fmt.Percent(d.UsedPercent));
                Console.WriteLine("    Scanned      : {0} / {1} files / {2} folders / {3} ({4})",
                    Fmt.Size(d.ScannedBytes), Fmt.Count(d.ScannedFiles), Fmt.Count(d.ScannedDirs), Fmt.Duration(d.DurationMs), d.Engine);
                if (!string.IsNullOrEmpty(d.EngineNote)) Console.WriteLine("    Engine notes : " + d.EngineNote);
                if (d.Errors > 0) Console.WriteLine("    Inaccessible : {0} folders", Fmt.Count(d.Errors));
                if (d.CloudOnlyBytes > 0) Console.WriteLine("    Cloud-only   : {0} (not stored on disk)", Fmt.Size(d.CloudOnlyBytes));
                Console.WriteLine("    Hotspot      : {0}  ({1})", d.HotspotPath, Fmt.Size(d.HotspotSize));
                Console.WriteLine("    Most files   : {0}  ({1} files)", d.FileHotspotPath, Fmt.Count(d.FileHotspotCount));
                if (d.LargestFileSize > 0)
                    Console.WriteLine("    Largest file : {0}  ({1})", d.LargestFilePath, Fmt.Size(d.LargestFileSize));
            }
            Console.WriteLine();
            Console.WriteLine("Report : " + outFile);
        }

        private static void WriteError(string outFile, string msg)
        {
            try { File.WriteAllText(outFile + ".err", msg); } catch { }
        }

        private static void TryDelete(string f)
        {
            if (f == null) return;
            try { File.Delete(f); } catch { }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("DiskHawk.Scanner " + DiskScanner.Version + " - fast disk usage scanner");
            Console.WriteLine();
            Console.WriteLine("Usage: DiskHawk.Scanner.exe [drive/folder ...] [options]");
            Console.WriteLine();
            Console.WriteLine("  (all fixed drives are scanned when no drive is given)");
            Console.WriteLine("  -o, --out <file|folder>   Report (.dhr) path. Default: MACHINE_timestamp.dhr");
            Console.WriteLine("  -t, --threads <n>         Parallel threads, classic engine (default " + ScanOptions.DefaultThreads + ")");
            Console.WriteLine("  --min-file-mb <n>         Minimum size for the largest-files list in MB (default 10)");
            Console.WriteLine("  --max-files <n>           Number of largest files kept in the report (default 1000)");
            Console.WriteLine("  --csv <folder>            Also write CSV reports");
            Console.WriteLine("  --engine auto|mft|classic Scan engine (default auto: raw MFT on NTFS as administrator)");
            Console.WriteLine("  --below-normal            Lower process priority (does not slow the user down)");
            Console.WriteLine("  --low-io                  Low I/O priority (slower, minimal impact)");
            Console.WriteLine("  --progress-file <file>    Write progress to a file (used by the console)");
            Console.WriteLine("  -q, --quiet               No console output");
            Console.WriteLine("  --delete <list.txt>       PERMANENTLY delete the listed paths instead of scanning");
            Console.WriteLine("  --delete-log <file>       Deletion result file (TSV)");
            Console.WriteLine("  --lang en|tr              Language of result texts (default en)");
            Console.WriteLine("  --allow-critical          Also delete critical paths (Windows, Program Files, profiles; never a drive root)");
            Console.WriteLine();
            Console.WriteLine("Example: DiskHawk.Scanner.exe C: --csv C:\\Temp\\report");
        }

        private sealed class Options
        {
            public readonly List<string> Roots = new List<string>();
            public string Output;
            public int Threads;
            public int MinFileMb = 10;
            public int MaxFiles = 1000;
            public string CsvDir;
            public bool BelowNormal;
            public bool LowIo;
            public string ProgressFile;
            public bool Quiet;
            public bool Help;
            public string DeleteList;
            public string DeleteLog;
            public bool AllowCritical;
            public ScanEngine Engine = ScanEngine.Auto;
            public string Lang = "en";

            public static Options Parse(string[] args)
            {
                var o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    switch (a.ToLowerInvariant())
                    {
                        case "-h": case "--help": case "/?": o.Help = true; break;
                        case "-o": case "--out": o.Output = Next(args, ref i, a); break;
                        case "-t": case "--threads": o.Threads = NextInt(args, ref i, a); break;
                        case "--min-file-mb": o.MinFileMb = NextInt(args, ref i, a); break;
                        case "--max-files": o.MaxFiles = NextInt(args, ref i, a); break;
                        case "--csv": o.CsvDir = Next(args, ref i, a); break;
                        case "--below-normal": o.BelowNormal = true; break;
                        case "--low-io": o.LowIo = true; break;
                        case "--progress-file": o.ProgressFile = Next(args, ref i, a); break;
                        case "-q": case "--quiet": o.Quiet = true; break;
                        case "--delete": o.DeleteList = Next(args, ref i, a); break;
                        case "--delete-log": o.DeleteLog = Next(args, ref i, a); break;
                        case "--allow-critical": o.AllowCritical = true; break;
                        case "--lang": o.Lang = Next(args, ref i, a).ToLowerInvariant() == "tr" ? "tr" : "en"; break;
                        case "--engine":
                            {
                                var v = Next(args, ref i, a).ToLowerInvariant();
                                if (v == "auto") o.Engine = ScanEngine.Auto;
                                else if (v == "mft") o.Engine = ScanEngine.Mft;
                                else if (v == "classic" || v == "klasik") o.Engine = ScanEngine.Classic;
                                else throw new ArgumentException("--engine auto|mft|classic");
                                break;
                            }
                        case "--all": break; // default behaviour
                        default:
                            if (a.StartsWith("-")) throw new ArgumentException("Unknown option: " + a);
                            o.Roots.Add(DiskScanner.NormalizeRoot(a));
                            break;
                    }
                }
                return o;
            }

            private static string Next(string[] a, ref int i, string name)
            {
                if (i + 1 >= a.Length) throw new ArgumentException(name + " expects a value");
                return a[++i];
            }

            private static int NextInt(string[] a, ref int i, string name)
            {
                int v;
                if (!int.TryParse(Next(a, ref i, name), out v) || v < 0) throw new ArgumentException(name + " expects a valid number");
                return v;
            }
        }
    }
}
