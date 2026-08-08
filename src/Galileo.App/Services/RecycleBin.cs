using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Galileo.Models;

namespace Galileo.Services;

public sealed class RecycleEntry
{
    public string Id { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; }
    public long Size { get; set; }
    public DateTime DeletedUtc { get; set; }
}

/// <summary>
/// Galileo's own recycle bin (independent of the Windows Recycle Bin). Deleted items are moved into
/// %LocalAppData%\Galileo\RecycleBin\store and tracked in index.json so they can be restored;
/// emptying / permanent-delete uses <see cref="SecureWipe"/> with the user's chosen method.
/// </summary>
public sealed class RecycleBin
{
    public const string Location = "bin:::"; // sentinel used as the explorer's _currentFolder

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo", "RecycleBin");
    private static string StoreDir => Path.Combine(Root, "store");
    private static string IndexPath => Path.Combine(Root, "index.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _lock = new();
    private const string MutexName = "Galileo.RecycleBin";

    // Cross-process guard for load → mutate → save sequences on index.json (two Galileo instances
    // share the same bin). Throws TimeoutException if another process wedges the bin.
    private static IDisposable AcquireIndexMutex()
    {
        var m = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            if (!m.WaitOne(TimeSpan.FromSeconds(10))) { m.Dispose(); throw new TimeoutException("Recycle bin index is busy."); }
        }
        catch (AbandonedMutexException) { /* previous holder died — the mutex is ours now */ }
        return new MutexReleaser(m);
    }

    private sealed class MutexReleaser : IDisposable
    {
        private readonly Mutex _m;
        public MutexReleaser(Mutex m) => _m = m;
        public void Dispose() { try { _m.ReleaseMutex(); } catch { } _m.Dispose(); }
    }

    /// <summary>Stored file path: GUID + original extension (so previews/open by extension still work).</summary>
    public string StorePathOf(RecycleEntry e) =>
        Path.Combine(StoreDir, e.Id + (e.IsFolder ? "" : Path.GetExtension(e.Name)));

    public int Count => Load().Count;

    /// <summary>Store paths of every entry (files or folders) — used to wipe the whole bin with progress.</summary>
    public List<string> StorePaths() => Load().Select(StorePathOf).ToList();

    /// <summary>Drops index entries whose stored item no longer exists (e.g. after a wipe), and tidies
    /// the store directory. Lets an external wipe handle the bytes while the index stays consistent.</summary>
    public void RemoveMissing()
    {
        lock (_lock)
        {
            try
            {
                using var gate = AcquireIndexMutex();
                var list = Load();
                list.RemoveAll(e => { var p = StorePathOf(e); return !File.Exists(p) && !Directory.Exists(p); });
                Save(list);
                // Store files with no index record are left alone — another process may be mid-add,
                // so purging "orphans" here could destroy a just-binned item.
            }
            catch { }
        }
    }

    public List<RecycleEntry> Load()
    {
        try { if (File.Exists(IndexPath)) return JsonSerializer.Deserialize<List<RecycleEntry>>(File.ReadAllText(IndexPath)) ?? new(); }
        catch { }
        return new();
    }

    // Atomic index write: serialize to index.json.tmp, then swap it into place. Throws on failure so
    // callers can react (a silently lost index would orphan store files).
    private void Save(List<RecycleEntry> entries)
    {
        Directory.CreateDirectory(Root);
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Json));
        if (File.Exists(IndexPath)) File.Replace(tmp, IndexPath, destinationBackupFileName: null);
        else File.Move(tmp, IndexPath);
    }

    /// <summary>Moves a file/folder into the bin (recoverable). Returns false if the path is gone
    /// or the bin could not record it.</summary>
    public bool MoveToBin(string path)
    {
        lock (_lock)
        {
            try
            {
                using var gate = AcquireIndexMutex();
                var isDir = Directory.Exists(path);
                if (!isDir && !File.Exists(path)) return false;
                Directory.CreateDirectory(StoreDir);

                var name = Path.GetFileName(path.TrimEnd('\\', '/'));
                var id = Guid.NewGuid().ToString("N");
                var dest = Path.Combine(StoreDir, id + (isDir ? "" : Path.GetExtension(name)));
                long size = 0;
                try { size = isDir ? DirSize(path) : new FileInfo(path).Length; } catch { }

                // Record the entry BEFORE moving: if we crash mid-move the index at worst points at
                // a missing store file (RemoveMissing tidies that), never at an untracked orphan.
                var list = Load();
                list.Add(new RecycleEntry { Id = id, OriginalPath = path, Name = name, IsFolder = isDir, Size = size, DeletedUtc = DateTime.UtcNow });
                Save(list);
                try { MoveAny(path, dest, isDir); }
                catch
                {
                    list.RemoveAll(x => x.Id == id);
                    try { Save(list); } catch { }
                    return false;
                }
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>The bin's contents as explorer items (newest first; store files keep their extension).</summary>
    public List<ExplorerItem> ListItems()
    {
        var items = new List<ExplorerItem>();
        foreach (var e in Load().OrderByDescending(x => x.DeletedUtc))
        {
            var kind = e.IsFolder ? ExplorerItemKind.Folder : ExplorerItemKind.File;
            var type = e.IsFolder ? "Folder" : TypeName(Path.GetExtension(e.Name));
            items.Add(new ExplorerItem(StorePathOf(e), kind, e.Size, e.DeletedUtc.ToLocalTime(), type, displayName: e.Name));
        }
        return items;
    }

    /// <summary>Restores an item to its original location (conflict-renamed). Returns the restored path.</summary>
    public bool Restore(string storePath, out string restoredTo)
    {
        restoredTo = "";
        lock (_lock)
        {
            try
            {
                using var gate = AcquireIndexMutex();
                var list = Load();
                var e = list.FirstOrDefault(x => string.Equals(StorePathOf(x), storePath, StringComparison.OrdinalIgnoreCase));
                if (e is null) return false;
                try
                {
                    var dest = UniquePath(e.OriginalPath, e.IsFolder);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    MoveAny(storePath, dest, e.IsFolder);
                    restoredTo = dest;
                }
                catch { return false; }
                list.Remove(e);
                try { Save(list); } catch { /* stale entry; RemoveMissing will drop it */ }
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>Permanently removes one entry, secure-wiping its bytes first.</summary>
    public async Task DeleteEntryAsync(string storePath, WipeMethod method)
    {
        RecycleEntry? e;
        lock (_lock) { e = Load().FirstOrDefault(x => string.Equals(StorePathOf(x), storePath, StringComparison.OrdinalIgnoreCase)); }
        if (e is null) return;
        await SecureWipe.WipePathAsync(storePath, method);
        lock (_lock)
        {
            try { using var gate = AcquireIndexMutex(); var list = Load(); list.RemoveAll(x => x.Id == e.Id); Save(list); }
            catch { }
        }
    }

    /// <summary>Empties the bin, secure-wiping every item with the chosen method.</summary>
    public async Task EmptyAsync(WipeMethod method, IProgress<string>? progress = null)
    {
        List<RecycleEntry> list;
        lock (_lock) { list = Load(); }
        foreach (var e in list) await SecureWipe.WipePathAsync(StorePathOf(e), method, progress);
        lock (_lock)
        {
            try
            {
                using var gate = AcquireIndexMutex();
                Save(new List<RecycleEntry>());
                try { foreach (var f in Directory.EnumerateFileSystemEntries(StoreDir)) TryRemove(f); } catch { }
            }
            catch { }
        }
    }

    // ---- helpers ----

    private static void TryRemove(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); } catch { }
    }

    private static void MoveAny(string src, string dest, bool isDir)
    {
        try { if (isDir) Directory.Move(src, dest); else File.Move(src, dest); return; }
        catch (IOException) { /* cross-volume → copy + delete */ }
        if (isDir) { CopyDir(src, dest); Directory.Delete(src, true); }
        else { File.Copy(src, dest, overwrite: true); File.Delete(src); }
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try { foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) { try { total += new FileInfo(f).Length; } catch { } } }
        catch { }
        return total;
    }

    private static string UniquePath(string path, bool isDir)
    {
        if (isDir ? !Directory.Exists(path) : !File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (isDir ? !Directory.Exists(candidate) : !File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private static string TypeName(string ext) =>
        string.IsNullOrEmpty(ext) ? "File" : $"{ext.TrimStart('.').ToUpperInvariant()} File";
}
