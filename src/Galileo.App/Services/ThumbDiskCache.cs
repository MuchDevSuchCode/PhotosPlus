using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Galileo.Services;

/// <summary>
/// On-disk thumbnail cache: raw BGRA pixels under LocalAppData\Galileo\thumbcache, one file per
/// (path, mtime, pixel size). Raw pixels rather than PNG because encoding/decoding would drag
/// WinRT imaging objects onto worker threads — the exact finalizer/UI-thread stall
/// <see cref="Galileo.Models.ExplorerItem.LoadIconAsync"/> exists to avoid — and a raw hit is a
/// single memcpy into the bitmap. A 96px icon is ~36 KB; the sweep keeps the total bounded.
///
/// Everything here is synchronous and swallows IO errors: a cache that throws is worse than no
/// cache. Callers invoke it from worker threads only.
/// </summary>
public static class ThumbDiskCache
{
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    // "GAL1" little-endian. Bump if the header/pixel layout ever changes so old files read as corrupt.
    private const int Magic = 0x314C4147;
    private const int HeaderBytes = 12; // magic, width, height
    private const int MaxDim = 4096;    // sanity bound — anything larger is a corrupt header, not a thumbnail

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo", "thumbcache");

    // Vault working folders decrypt under ...\Galileo\.work and the recycle-bin store lives under
    // ...\Galileo\RecycleBin — a cached thumbnail of either would leak vault plaintext / deleted-file
    // content to disk in the clear. Nothing under the app's own data folder is ever cached.
    private static readonly string ExcludedRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo");

    /// <summary>Whether a thumbnail for this path may ever touch the disk. TryGet/Store no-op when false.</summary>
    public static bool Cacheable(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!path.StartsWith(ExcludedRoot, StringComparison.OrdinalIgnoreCase)) return true;
        // Prefix alone would also exclude innocent siblings like "...\GalileoNotes".
        return path.Length > ExcludedRoot.Length && path[ExcludedRoot.Length] is not ('\\' or '/');
    }

    public static (byte[] Pixels, int Width, int Height)? TryGet(string path, DateTime mtimeUtc, int px)
    {
        if (!Cacheable(path)) return null;
        var file = Path.Combine(Root, FileNameFor(HashOf(path), mtimeUtc.Ticks, px));
        if (!File.Exists(file)) return null;
        try
        {
            (byte[] Pixels, int Width, int Height)? result = null;
            using (var fs = File.OpenRead(file))
            {
                Span<byte> header = stackalloc byte[HeaderBytes];
                fs.ReadExactly(header);
                var w = BitConverter.ToInt32(header.Slice(4, 4));
                var h = BitConverter.ToInt32(header.Slice(8, 4));
                if (BitConverter.ToInt32(header) == Magic
                    && w > 0 && h > 0 && w <= MaxDim && h <= MaxDim
                    && fs.Length == HeaderBytes + (long)w * h * 4)
                {
                    var pixels = new byte[w * h * 4];
                    fs.ReadExactly(pixels);
                    result = (pixels, w, h);
                }
            }
            if (result is { } r)
            {
                // Sweep evicts by last access, and NTFS often defers or disables access-time
                // updates — stamp it ourselves so hot entries survive the sweep.
                try { File.SetLastAccessTimeUtc(file, DateTime.UtcNow); } catch { }
                return r;
            }
        }
        catch { /* unreadable counts as corrupt */ }
        TryDelete(file); // exists but misshapen — it will never validate, reclaim it now
        return null;
    }

    public static void Store(string path, DateTime mtimeUtc, int px, byte[] bgra, int width, int height)
    {
        if (!Cacheable(path) || bgra is null || width <= 0 || height <= 0) return;
        if (bgra.Length != width * height * 4) return; // pooled buffers carry slack — callers must pass a right-sized copy
        var hash = HashOf(path);
        var final = Path.Combine(Root, FileNameFor(hash, mtimeUtc.Ticks, px));
        // Write-then-move so a crash mid-write can't leave a half file under the real name. The
        // ".tmp" suffix keeps strays out of the "*.bin" stale-entry match below.
        var tmp = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Root);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Span<byte> header = stackalloc byte[HeaderBytes];
                BitConverter.TryWriteBytes(header, Magic);
                BitConverter.TryWriteBytes(header.Slice(4, 4), width);
                BitConverter.TryWriteBytes(header.Slice(8, 4), height);
                fs.Write(header);
                fs.Write(bgra, 0, bgra.Length);
            }
            File.Move(tmp, final, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            return;
        }
        // Entries for the same path+size at another mtime are dead (the source changed) — reclaim
        // them now instead of letting them sit until the size cap forces a sweep.
        try
        {
            var keep = Path.GetFileName(final);
            foreach (var f in Directory.EnumerateFiles(Root, hash + "_*_" + px + ".bin"))
                if (!string.Equals(Path.GetFileName(f), keep, StringComparison.OrdinalIgnoreCase))
                    TryDelete(f);
        }
        catch { }
    }

    /// <summary>Drops every cached size/mtime for a path. For invalidations the mtime can't see —
    /// e.g. a folder's user-chosen thumbnail changing, which lives in app state, not on disk.</summary>
    public static void Invalidate(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, HashOf(path) + "_*"))
                TryDelete(f);
        }
        catch { }
    }

    /// <summary>Evicts oldest-by-last-access entries until the cache fits the cap. Meant to run once
    /// at startup on a background thread; also creates the cache directory so first-run Stores are
    /// a plain write.</summary>
    public static void Sweep(long maxBytes = DefaultMaxBytes)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var files = new DirectoryInfo(Root).GetFiles();
            long total = 0;
            foreach (var f in files) total += f.Length;
            if (total <= maxBytes) return;
            foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= maxBytes) break;
                try { var len = f.Length; f.Delete(); total -= len; } catch { }
            }
        }
        catch { }
    }

    // 20 hex chars (80 bits) of SHA-256 over the lowercased path: collision-safe at cache scale,
    // short enough to keep the whole filename well under MAX_PATH.
    private static string HashOf(string path)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())).AsSpan(0, 10));

    private static string FileNameFor(string hash, long mtimeUtcTicks, int px)
        => $"{hash}_{mtimeUtcTicks}_{px}.bin";

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { }
    }
}
