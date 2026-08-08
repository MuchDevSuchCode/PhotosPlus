using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Galileo.Services;

/// <summary>A snapshot of a running transfer, reported to the UI.</summary>
public sealed class TransferProgress
{
    public string CurrentFile { get; init; } = "";
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public int FilesDone { get; init; }
    public int FilesTotal { get; init; }
    public double BytesPerSecond { get; init; }
    public bool Paused { get; init; }

    public double Fraction => BytesTotal > 0 ? (double)BytesDone / BytesTotal : (FilesTotal > 0 ? (double)FilesDone / FilesTotal : 0);
}

public sealed class TransferResult
{
    public int FilesCompleted { get; init; }
    public int Skipped { get; init; }
    public bool Canceled { get; init; }
    public int Errors { get; init; }
}

public enum ConflictAction { Overwrite, Skip, KeepBoth, Cancel }

/// <summary>Details of a name collision, handed to the UI so the user can choose what to do.</summary>
public sealed class ConflictInfo
{
    public string Name { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string DestPath { get; init; } = "";
    public long SourceSize { get; init; }
    public long DestSize { get; init; }
    public DateTime SourceModified { get; init; }
    public DateTime DestModified { get; init; }
    /// <summary>True when both files have identical contents (verified by SHA-256).</summary>
    public bool Identical { get; init; }
    /// <summary>How many further conflicts remain after this one (for an "apply to all" option).</summary>
    public int RemainingConflicts { get; init; }
}

public sealed class ConflictChoice
{
    public ConflictAction Action { get; init; }
    public bool ApplyToAll { get; init; }
}

/// <summary>
/// Copies or moves files/folders into a destination directory with granular progress, and supports
/// <see cref="Pause"/> / <see cref="Resume"/> / <see cref="Cancel"/> while running. Same-volume moves
/// use an instant rename; everything else streams in chunks. Name collisions raise a conflict callback
/// so the user can Overwrite / Skip / Keep both (with "apply to all"); identical files are detected by
/// hashing both sides first so the choice is informed. Existing folders are merged (per-file conflicts).
/// </summary>
public sealed class FileTransfer
{
    private const int Chunk = 1 << 20; // 1 MiB

    private readonly ManualResetEventSlim _gate = new(initialState: true); // set = running, reset = paused
    private readonly CancellationTokenSource _cts = new();

    public bool IsPaused { get; private set; }
    public bool IsCanceled => _cts.IsCancellationRequested;

    public void Pause() { if (IsCanceled) return; IsPaused = true; _gate.Reset(); }
    public void Resume() { if (IsCanceled) return; IsPaused = false; _gate.Set(); }
    public void TogglePause() { if (IsPaused) Resume(); else Pause(); }
    public void Cancel() { _cts.Cancel(); _gate.Set(); } // release the gate so a paused copy can observe the cancel

    private sealed class CopyOp { public string Src = ""; public string Dest = ""; public long Size; }

    public Task<TransferResult> RunAsync(string destDir, IReadOnlyList<string> paths, bool move,
        IProgress<TransferProgress>? progress, Func<ConflictInfo, Task<ConflictChoice>>? onConflict = null)
        => Task.Run(() => Run(destDir, paths, move, progress, onConflict));

    private TransferResult Run(string destDir, IReadOnlyList<string> paths, bool move,
        IProgress<TransferProgress>? progress, Func<ConflictInfo, Task<ConflictChoice>>? onConflict)
    {
        var copies = new List<CopyOp>();      // files to stream-copy
        var dirsToCreate = new List<string>(); // destination dirs (preserves empty subdirs / merges)
        var fastMoves = new List<(string src, string dest, bool isDir)>(); // instant same-volume renames
        var moveDirSources = new List<string>(); // top-level dirs that were merged on a move (empty-dir cleanup)
        var claimedDests = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // dests taken by fast moves + resolved copies

        // ---- Plan ----
        foreach (var src in paths)
        {
            try
            {
                var name = Path.GetFileName(src.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) continue;

                if (Directory.Exists(src))
                {
                    var srcParent = Path.GetDirectoryName(src.TrimEnd('\\', '/'));
                    if (move && string.Equals(srcParent, destDir, StringComparison.OrdinalIgnoreCase)) continue; // no-op
                    var destBase = Path.Combine(destDir, name);
                    if (IsSubPath(src, destBase)) continue; // can't move a folder into itself

                    if (!Directory.Exists(destBase) && move && SameVolume(src, destBase))
                    {
                        // Dest occupied by a file, or already claimed earlier in this batch → auto-rename
                        // (Keep both) like the copy path does, then rename in place.
                        if (Occupied(destBase) || claimedDests.Contains(destBase))
                            destBase = UniquePath(destBase, claimedDests);
                        claimedDests.Add(destBase);
                        fastMoves.Add((src, destBase, true)); // brand-new dir on the same volume → rename
                    }
                    else
                    {
                        // dest exists → merge into it; otherwise create it. Inner files conflict-check individually.
                        PlanDirectory(src, destBase, copies, dirsToCreate);
                        if (move) moveDirSources.Add(src);
                    }
                }
                else if (File.Exists(src))
                {
                    var srcParent = Path.GetDirectoryName(src);
                    if (move && string.Equals(srcParent, destDir, StringComparison.OrdinalIgnoreCase)) continue; // no-op
                    var destPath = Path.Combine(destDir, name);

                    if (!File.Exists(destPath) && move && SameVolume(src, destPath))
                    {
                        // Dest occupied by a folder, or already claimed earlier in this batch → auto-rename
                        // (Keep both) like the copy path does, then rename in place.
                        if (Occupied(destPath) || claimedDests.Contains(destPath))
                            destPath = UniquePath(destPath, claimedDests);
                        claimedDests.Add(destPath);
                        fastMoves.Add((src, destPath, false));
                    }
                    else
                        copies.Add(NewOp(src, destPath));
                }
            }
            catch { /* skip the offending item, keep planning the rest */ }
        }

        // ---- Resolve conflicts (files whose destination already exists) ----
        var skipped = 0;
        ConflictChoice? batch = null;
        var totalConflicts = copies.Count(o => Occupied(o.Dest));
        var conflictsSeen = 0;
        var resolved = new List<CopyOp>(copies.Count);
        foreach (var op in copies)
        {
            if (IsCanceled) return new TransferResult { Canceled = true, Skipped = skipped };

            // Two ops resolving to the same destination (including a fast-move's) would silently
            // overwrite each other — treat a path already claimed earlier in this batch as a
            // conflict and auto-rename (Keep both).
            if (!Occupied(op.Dest))
            {
                if (claimedDests.Add(op.Dest)) { resolved.Add(op); continue; }
                op.Dest = UniquePath(op.Dest, claimedDests);
                claimedDests.Add(op.Dest);
                resolved.Add(op);
                continue;
            }

            ConflictAction action;
            if (batch is not null) action = batch.Action;
            else if (onConflict is null) action = ConflictAction.KeepBoth; // no UI hook → old auto-rename behavior
            else
            {
                var info = BuildConflictInfo(op, totalConflicts - conflictsSeen - 1);
                ConflictChoice choice;
                try { choice = onConflict(info).GetAwaiter().GetResult(); }
                catch { choice = new ConflictChoice { Action = ConflictAction.KeepBoth }; }
                action = choice.Action;
                if (choice.ApplyToAll) batch = choice;
            }
            conflictsSeen++;

            switch (action)
            {
                case ConflictAction.Overwrite: claimedDests.Add(op.Dest); resolved.Add(op); break; // Create truncates
                case ConflictAction.KeepBoth: op.Dest = UniquePath(op.Dest, claimedDests); claimedDests.Add(op.Dest); resolved.Add(op); break;
                case ConflictAction.Skip: skipped++; break;                                        // drop it
                case ConflictAction.Cancel: return new TransferResult { Canceled = true, Skipped = skipped };
            }
        }
        copies = resolved;

        // ---- Totals (after conflict resolution) ----
        long bytesTotal = copies.Sum(o => o.Size);
        var filesTotal = copies.Count + fastMoves.Count;
        var clock = Stopwatch.StartNew();
        long bytesDone = 0;
        var filesDone = 0;
        var errors = 0;
        var canceled = false;

        long lastReportMs = -1000, rateBytesMark = 0, rateMsMark = 0;
        double ema = 0;

        void Report(string file, bool force)
        {
            var ms = clock.ElapsedMilliseconds;
            if (!force && ms - lastReportMs < 33) return; // ~30 fps
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
                CurrentFile = file, BytesDone = bytesDone, BytesTotal = bytesTotal,
                FilesDone = filesDone, FilesTotal = filesTotal, BytesPerSecond = ema, Paused = IsPaused,
            });
        }

        Report("", force: true);

        // ---- Instant same-volume renames first (before the copy list is finalized, so a failed
        // rename can be demoted to a streamed copy below) ----
        foreach (var (src, dest, isDir) in fastMoves)
        {
            _gate.Wait();
            if (IsCanceled) { canceled = true; break; }
            try { if (isDir) Directory.Move(src, dest); else File.Move(src, dest); filesDone++; Report(Path.GetFileName(dest), true); }
            catch
            {
                // Rename failed (locked file, sharing violation, dest appeared meanwhile…) —
                // fall back to the streamed-copy path instead of dropping the item.
                if (isDir)
                {
                    var before = copies.Count;
                    PlanDirectory(src, dest, copies, dirsToCreate);
                    if (move) moveDirSources.Add(src);
                    for (var k = before; k < copies.Count; k++) bytesTotal += copies[k].Size;
                    filesTotal += copies.Count - before - 1; // the dir itself was counted as one "file"
                }
                else
                {
                    var op = NewOp(src, dest);
                    copies.Add(op);
                    bytesTotal += op.Size;
                }
            }
        }

        // ---- Streamed copies ----
        // For a move, defer deleting sources until the whole copy succeeds, so cancelling leaves the
        // originals intact (the dest may have partial copies, but no source data is lost).
        var copiedSources = new List<string>();
        if (!canceled)
        {
            foreach (var dir in dirsToCreate) { try { Directory.CreateDirectory(dir); } catch { } }

            foreach (var op in copies)
            {
                _gate.Wait();
                if (IsCanceled) { canceled = true; break; }
                try
                {
                    CopyFile(op, ref bytesDone, Report);
                    if (move) copiedSources.Add(op.Src);
                    filesDone++;
                    Report(Path.GetFileName(op.Dest), true);
                }
                catch (OperationCanceledException) { canceled = true; break; }
                catch { errors++; }
            }
        }

        // ---- Finish a move (only on a clean run): delete copied sources, then prune emptied folders ----
        if (move && !canceled)
        {
            foreach (var src in copiedSources) { try { File.Delete(src); } catch { } }
            foreach (var dir in moveDirSources) RemoveEmptyDirs(dir);
        }

        return new TransferResult { FilesCompleted = filesDone, Skipped = skipped, Canceled = canceled, Errors = errors };
    }

    private static CopyOp NewOp(string src, string dest)
    {
        long size = 0; try { size = new FileInfo(src).Length; } catch { }
        return new CopyOp { Src = src, Dest = dest, Size = size };
    }

    private static ConflictInfo BuildConflictInfo(CopyOp op, int remaining)
    {
        long ds = 0; DateTime dm = default, sm = default;
        try { var fi = new FileInfo(op.Dest); ds = fi.Length; dm = fi.LastWriteTime; } catch { }
        try { sm = new FileInfo(op.Src).LastWriteTime; } catch { }
        return new ConflictInfo
        {
            Name = Path.GetFileName(op.Dest),
            SourcePath = op.Src, DestPath = op.Dest,
            SourceSize = op.Size, DestSize = ds,
            SourceModified = sm, DestModified = dm,
            Identical = FilesIdentical(op.Src, op.Dest),
            RemainingConflicts = Math.Max(0, remaining),
        };
    }

    /// <summary>Equal length + equal bytes → identical contents. Streams a chunk-by-chunk compare with an
    /// early-out (cheaper than hashing the whole file twice). Only called on a real collision.</summary>
    private static bool FilesIdentical(string a, string b)
    {
        try
        {
            var la = new FileInfo(a).Length;
            if (la != new FileInfo(b).Length) return false;
            using var sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, FileOptions.SequentialScan);
            using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, FileOptions.SequentialScan);
            var ba = new byte[Chunk];
            var bb = new byte[Chunk];
            while (true)
            {
                var na = ReadBlock(sa, ba);
                var nb = ReadBlock(sb, bb);
                if (na != nb) return false;
                if (na == 0) return true;
                if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            }
        }
        catch { return false; }
    }

    // Reads up to buf.Length bytes, looping until the buffer is full or EOF (a single Read may return less).
    private static int ReadBlock(Stream s, byte[] buf)
    {
        var total = 0;
        int n;
        while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
        return total;
    }

    private void CopyFile(CopyOp op, ref long bytesDone, Action<string, bool> report)
    {
        var name = Path.GetFileName(op.Dest);
        Directory.CreateDirectory(Path.GetDirectoryName(op.Dest)!);
        var buffer = new byte[Chunk];
        var partial = false;
        try
        {
            using var src = new FileStream(op.Src, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, FileOptions.SequentialScan);
            using var dst = new FileStream(op.Dest, FileMode.Create, FileAccess.Write, FileShare.None, Chunk, FileOptions.SequentialScan);
            partial = true;
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                _gate.Wait(); // pause mid-file
                if (IsCanceled) throw new OperationCanceledException();
                dst.Write(buffer, 0, read);
                bytesDone += read;
                report(name, false);
            }
            partial = false;
        }
        catch (OperationCanceledException) { TryDeletePartial(op.Dest); throw; }
        catch { if (partial) TryDeletePartial(op.Dest); throw; }
        try { File.SetLastWriteTimeUtc(op.Dest, File.GetLastWriteTimeUtc(op.Src)); } catch { }
    }

    private static void TryDeletePartial(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static void PlanDirectory(string srcDir, string destDir, List<CopyOp> copies, List<string> dirsToCreate)
    {
        dirsToCreate.Add(destDir);
        try
        {
            foreach (var file in Directory.EnumerateFiles(srcDir))
                copies.Add(NewOp(file, Path.Combine(destDir, Path.GetFileName(file))));
            foreach (var sub in Directory.EnumerateDirectories(srcDir))
                PlanDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)), copies, dirsToCreate);
        }
        catch { /* access denied etc. — copy what we can */ }
    }

    /// <summary>Removes empty directories bottom-up (after a merged move; skipped files leave dirs intact).</summary>
    private static void RemoveEmptyDirs(string dir)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir)) RemoveEmptyDirs(sub);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    private static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)),
                                 Path.GetPathRoot(Path.GetFullPath(b)),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>A destination path counts as taken whether a FILE or a FOLDER occupies it.</summary>
    private static bool Occupied(string path) => File.Exists(path) || Directory.Exists(path);

    private static string UniquePath(string path, HashSet<string>? avoid = null)
    {
        bool Taken(string p) => Occupied(p) || (avoid is not null && avoid.Contains(p));
        if (!Taken(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!Taken(candidate)) return candidate;
        }
        // Last resort: a unique suffix so we never return a colliding path (which would overwrite).
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    private static bool IsSubPath(string parent, string child)
    {
        var p = Path.GetFullPath(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(child).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }
}
