using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Galileo.Services;

public enum WipeMethod { None, Zero, Random, Dod3, Dod7, Gutmann35 }

/// <summary>
/// Best-effort secure file wipe: overwrites a file's bytes with one or more passes before deleting,
/// then renames it to defeat name-based recovery. Methods mirror fileshredder.org (Zero / Random /
/// DoD 5220.22-M / DoD ECE / Gutmann 35-pass). NOTE: on SSDs/NVMe and copy-on-write filesystems
/// overwriting is not a guarantee (wear-leveling/TRIM) — this is best-effort, not forensic.
/// </summary>
public static class SecureWipe
{
    private const int Chunk = 1 << 20;

    public static WipeMethod Parse(string? s) => s switch
    {
        "Zero" => WipeMethod.Zero,
        "Random" => WipeMethod.Random,
        "Dod3" or "DoD3" => WipeMethod.Dod3,
        "Dod7" or "DoD7" => WipeMethod.Dod7,
        "Gutmann35" or "Gutmann" => WipeMethod.Gutmann35,
        _ => WipeMethod.None,
    };

    /// <summary>Overwrites + deletes a file, or recursively a folder, on a background thread.</summary>
    public static Task WipePathAsync(string path, WipeMethod method, IProgress<string>? progress = null) => Task.Run(() =>
    {
        if (Directory.Exists(path))
        {
            if (IsReparsePoint(path)) { try { Directory.Delete(path, recursive: false); } catch { } return; }
            foreach (var f in SafeFiles(path)) WipeFile(f, method, progress);
            try { Directory.Delete(path, recursive: true); } catch { }
        }
        else
        {
            if (IsReparsePoint(path)) { try { File.Delete(path); } catch { } return; }
            WipeFile(path, method, progress);
        }
    });

    // Walks the tree manually so directory junctions/symlinks are never followed — a wipe must not
    // overwrite a link's TARGET. Reparse points are deleted as bare links, not recursed into.
    private static IEnumerable<string> SafeFiles(string dir)
    {
        var files = new List<string>();
        Collect(dir, files);
        return files;
    }

    private static void Collect(string dir, List<string> files)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (IsReparsePoint(f)) { try { File.Delete(f); } catch { } }
                else files.Add(f);
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                if (IsReparsePoint(d)) { try { Directory.Delete(d, recursive: false); } catch { } }
                else Collect(d, files);
            }
        }
        catch { /* access denied etc. — wipe what we can */ }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return true; } // can't inspect → treat as a link so we never overwrite a target
    }

    /// <summary>Number of overwrite passes for a method (0 for None / plain delete).</summary>
    public static int PassCount(WipeMethod m) => m == WipeMethod.None ? 0 : Passes(m).Count;

    /// <summary>
    /// Wipes a batch of paths (files and/or folders) on a background thread, reporting aggregate
    /// progress (bytes overwritten across all passes, plus file counts) and honoring cancellation.
    /// Drives the floating progress card for Empty Recycle Bin / shred / Shift+Delete.
    /// </summary>
    public static Task WipePathsAsync(IReadOnlyList<string> paths, WipeMethod method,
        IProgress<TransferProgress>? progress, CancellationToken ct = default) => Task.Run(() =>
    {
        var files = new List<string>();
        var dirs = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                if (IsReparsePoint(p)) { try { Directory.Delete(p, recursive: false); } catch { } continue; }
                files.AddRange(SafeFiles(p)); dirs.Add(p);
            }
            else if (File.Exists(p))
            {
                if (IsReparsePoint(p)) { try { File.Delete(p); } catch { } continue; }
                files.Add(p);
            }
        }

        var passes = PassCount(method);
        long bytesTotal = 0;
        if (passes > 0)
            foreach (var f in files) { try { bytesTotal += new FileInfo(f).Length; } catch { } }
        bytesTotal *= passes; // total bytes we'll actually overwrite (0 for plain delete → progress by file count)

        long bytesDone = 0;
        var filesDone = 0;
        var filesTotal = files.Count;
        var clock = Stopwatch.StartNew();
        long lastReportMs = -1000, rateBytesMark = 0, rateMsMark = 0;
        double ema = 0;

        void Report(string name, bool force)
        {
            var ms = clock.ElapsedMilliseconds;
            if (!force && ms - lastReportMs < 33) return;
            var dt = (ms - rateMsMark) / 1000.0;
            if (dt >= 0.25)
            {
                var inst = (bytesDone - rateBytesMark) / dt;
                ema = ema <= 0 ? inst : ema * 0.75 + inst * 0.25;
                rateBytesMark = bytesDone; rateMsMark = ms;
            }
            lastReportMs = ms;
            progress?.Report(new TransferProgress
            {
                CurrentFile = name, BytesDone = bytesDone, BytesTotal = bytesTotal,
                FilesDone = filesDone, FilesTotal = filesTotal, BytesPerSecond = ema,
            });
        }

        Report("", true);
        foreach (var f in files)
        {
            if (ct.IsCancellationRequested) break;
            if (WipeFileCore(f, method, ref bytesDone, ct, name => Report(name, false))) filesDone++;
            Report(Path.GetFileName(f), true);
        }
        if (!ct.IsCancellationRequested)
            foreach (var d in dirs) { try { Directory.Delete(d, recursive: true); } catch { } }
    });

    // Overwrite a single file pass-by-pass, reporting bytes and honoring cancellation, then delete it.
    // On cancellation after even one byte was overwritten the file is already destroyed, so it is still
    // renamed + deleted (leaving it with its original name/size but garbage contents would look intact);
    // only files not yet touched survive a cancel. Returns true when the file was removed.
    private static bool WipeFileCore(string path, WipeMethod method, ref long bytesDone, CancellationToken ct, Action<string>? tick)
    {
        try
        {
            if (!File.Exists(path)) return true;
            var fi = new FileInfo(path);
            if (fi.Attributes.HasFlag(FileAttributes.ReadOnly)) fi.Attributes = FileAttributes.Normal;
            long len = fi.Length;

            if (method != WipeMethod.None && len > 0)
            {
                if (ct.IsCancellationRequested) return false; // untouched — safe to leave as-is
                var passes = Passes(method);
                var touched = false;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[(int)Math.Min(len, Chunk)];
                    for (var p = 0; p < passes.Count && !ct.IsCancellationRequested; p++)
                    {
                        fs.Position = 0;
                        long written = 0;
                        while (written < len && !ct.IsCancellationRequested)
                        {
                            var n = (int)Math.Min(buf.Length, len - written);
                            Fill(buf, n, passes[p], written);
                            fs.Write(buf, 0, n);
                            written += n; bytesDone += n;
                            touched = true;
                            tick?.Invoke(fi.Name);
                        }
                        fs.Flush(flushToDisk: true);
                    }
                    if (!ct.IsCancellationRequested)
                    {
                        fs.SetLength(0);
                        fs.Flush(flushToDisk: true);
                    }
                }
                if (ct.IsCancellationRequested && !touched) return false; // nothing overwritten — leave intact
            }

            var scrambled = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N"));
            try { File.Move(path, scrambled); File.Delete(scrambled); }
            catch { File.Delete(path); }
            return true;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            return !File.Exists(path);
        }
    }

    private static void WipeFile(string path, WipeMethod method, IProgress<string>? progress)
    {
        try
        {
            if (!File.Exists(path)) return;
            var fi = new FileInfo(path);
            if (fi.Attributes.HasFlag(FileAttributes.ReadOnly)) fi.Attributes = FileAttributes.Normal;
            long len = fi.Length;

            if (method != WipeMethod.None && len > 0)
            {
                var passes = Passes(method);
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                var buf = new byte[(int)Math.Min(len, Chunk)];
                for (var p = 0; p < passes.Count; p++)
                {
                    progress?.Report($"Wiping {fi.Name} — pass {p + 1}/{passes.Count}");
                    fs.Position = 0;
                    long written = 0;
                    while (written < len)
                    {
                        var n = (int)Math.Min(buf.Length, len - written);
                        Fill(buf, n, passes[p], written);
                        fs.Write(buf, 0, n);
                        written += n;
                    }
                    fs.Flush(flushToDisk: true);
                }
                fs.SetLength(0);
                fs.Flush(flushToDisk: true);
            }

            // Rename to a random name before deleting (defeats name-based recovery of the directory entry).
            var scrambled = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N"));
            try { File.Move(path, scrambled); File.Delete(scrambled); }
            catch { File.Delete(path); }
        }
        catch
        {
            try { File.Delete(path); } catch { /* give up */ }
        }
    }

    // pattern == null → cryptographic random; otherwise tile the pattern bytes (phase-continuous across chunks).
    private static void Fill(byte[] buf, int count, byte[]? pattern, long absoluteOffset)
    {
        if (pattern is null) { RandomNumberGenerator.Fill(buf.AsSpan(0, count)); return; }
        for (var i = 0; i < count; i++) buf[i] = pattern[(int)((absoluteOffset + i) % pattern.Length)];
    }

    private static List<byte[]?> Passes(WipeMethod m) => m switch
    {
        WipeMethod.Zero => new() { new byte[] { 0x00 } },
        WipeMethod.Random => new() { null },
        WipeMethod.Dod3 => new() { new byte[] { 0x00 }, new byte[] { 0xFF }, null },
        WipeMethod.Dod7 => new() { null, new byte[] { 0x00 }, new byte[] { 0xFF }, null, new byte[] { 0x00 }, new byte[] { 0xFF }, null },
        WipeMethod.Gutmann35 => Gutmann(),
        _ => new(),
    };

    // 4 random + the 27 canonical Gutmann patterns + 4 random = 35 passes.
    private static List<byte[]?> Gutmann()
    {
        var list = new List<byte[]?>();
        for (var i = 0; i < 4; i++) list.Add(null);
        byte[][] patterns =
        {
            new byte[]{0x55}, new byte[]{0xAA},
            new byte[]{0x92,0x49,0x24}, new byte[]{0x49,0x24,0x92}, new byte[]{0x24,0x92,0x49},
            new byte[]{0x00}, new byte[]{0x11}, new byte[]{0x22}, new byte[]{0x33}, new byte[]{0x44},
            new byte[]{0x55}, new byte[]{0x66}, new byte[]{0x77}, new byte[]{0x88}, new byte[]{0x99},
            new byte[]{0xAA}, new byte[]{0xBB}, new byte[]{0xCC}, new byte[]{0xDD}, new byte[]{0xEE}, new byte[]{0xFF},
            new byte[]{0x92,0x49,0x24}, new byte[]{0x49,0x24,0x92}, new byte[]{0x24,0x92,0x49},
            new byte[]{0x6D,0xB6,0xDB}, new byte[]{0xB6,0xDB,0x6D}, new byte[]{0xDB,0x6D,0xB6},
        };
        foreach (var p in patterns) list.Add(p);
        for (var i = 0; i < 4; i++) list.Add(null);
        return list;
    }
}
