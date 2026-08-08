using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Galileo.Models;

namespace Galileo.Services;

/// <summary>Enumerates the filesystem for the explorer: drives, quick-access roots and folders.</summary>
public sealed class FileSystemService
{
    private readonly AppState _state;
    public FileSystemService(AppState state) => _state = state;

    /// <summary>Drives shown under "This PC".</summary>
    public List<ExplorerItem> GetDrives()
    {
        var items = new List<ExplorerItem>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) { items.Add(Drive(d.Name, d.Name.TrimEnd('\\'), 0, 0)); continue; }
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                items.Add(Drive(d.Name, $"{label} ({d.Name.TrimEnd('\\')})", SafeTotal(d), SafeFree(d)));
            }
            catch { /* skip flaky drives */ }
        }
        return items;
    }

    /// <summary>Common user folders for the sidebar / home.</summary>
    public List<ExplorerItem> GetQuickAccess()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new[]
        {
            profile,
            Path.Combine(profile, "Desktop"),
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        var items = new List<ExplorerItem>();
        foreach (var p in paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            items.Add(new ExplorerItem(p, ExplorerItemKind.Folder, 0, SafeWriteTime(p), "Folder"));
        return items;
    }

    /// <summary>Lists a directory: folders first, then files, alphabetically.</summary>
    public List<ExplorerItem> List(string path, bool showWindowsHidden, bool showAppHidden)
    {
        var folders = new List<ExplorerItem>();
        var files = new List<ExplorerItem>();
        try
        {
            var di = new DirectoryInfo(path);

            foreach (var d in SafeEnumerate(() => di.EnumerateDirectories()))
            {
                if (IsWindowsHidden(d.Attributes) && !showWindowsHidden) continue;
                var appHidden = _state.HiddenFolders.Contains(d.FullName);
                if (appHidden && !showAppHidden) continue;
                folders.Add(new ExplorerItem(d.FullName, ExplorerItemKind.Folder, 0, SafeWrite(d), "Folder")
                {
                    IsAppHidden = appHidden
                });
            }

            foreach (var f in SafeEnumerate(() => di.EnumerateFiles()))
            {
                if (IsWindowsHidden(f.Attributes) && !showWindowsHidden) continue;
                files.Add(new ExplorerItem(f.FullName, ExplorerItemKind.File, SafeLength(f), SafeWrite(f), TypeName(f.Extension)));
            }
        }
        catch
        {
            // Inaccessible directory — return whatever we managed to read.
        }

        folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        folders.AddRange(files);
        return folders;
    }

    /// <summary>Recursively finds items under <paramref name="root"/> whose name contains the query.
    /// Walks manually (not RecurseSubdirectories) so app-hidden folders can be PRUNED — their contents
    /// must never surface in results, or search would defeat the hidden-folder privacy feature. Honors
    /// the same hidden-item toggles as <see cref="List"/> and never descends into reparse points.</summary>
    public List<ExplorerItem> Search(string root, string query, bool showWindowsHidden, bool showAppHidden, int max = 4000)
    {
        var results = new List<ExplorerItem>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        void Walk(DirectoryInfo dir)
        {
            if (results.Count >= max) return;
            IEnumerable<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos().ToList(); }
            catch { return; } // access denied etc. — keep what we have
            foreach (var info in entries)
            {
                if (results.Count >= max) return;
                if (IsWindowsHidden(info.Attributes) && !showWindowsHidden) continue;
                if (info is DirectoryInfo d)
                {
                    var appHidden = _state.HiddenFolders.Contains(d.FullName);
                    if (appHidden && !showAppHidden) continue;                      // prune the whole branch
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue; // junction cycles / foreign targets
                    if (d.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        results.Add(new ExplorerItem(d.FullName, ExplorerItemKind.Folder, 0, SafeWrite(d), "Folder") { IsAppHidden = appHidden });
                    Walk(d);
                }
                else if (info is FileInfo f && f.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    results.Add(new ExplorerItem(f.FullName, ExplorerItemKind.File, SafeLength(f), SafeWrite(f), TypeName(f.Extension)));
            }
        }
        try { Walk(new DirectoryInfo(root)); } catch { }
        return results;
    }

    private static ExplorerItem Drive(string root, string name, long total, long free) =>
        new(root, ExplorerItemKind.Drive, 0, default, "Drive", name, totalBytes: total, freeBytes: free);

    private static long SafeTotal(DriveInfo d) { try { return d.TotalSize; } catch { return 0; } }
    private static long SafeFree(DriveInfo d) { try { return d.TotalFreeSpace; } catch { return 0; } }

    private static bool IsWindowsHidden(FileAttributes a) =>
        a.HasFlag(FileAttributes.Hidden) || a.HasFlag(FileAttributes.System);

    internal static string TypeName(string ext) =>
        string.IsNullOrEmpty(ext) ? "File" : $"{ext.TrimStart('.').ToUpperInvariant()} File";

    private static IEnumerable<T> SafeEnumerate<T>(Func<IEnumerable<T>> source)
    {
        try { return source().ToList(); }
        catch { return Enumerable.Empty<T>(); }
    }

    private static long SafeLength(FileInfo f) { try { return f.Length; } catch { return 0; } }
    private static DateTime SafeWrite(FileSystemInfo i) { try { return i.LastWriteTime; } catch { return default; } }
    private static DateTime SafeWriteTime(string p) { try { return Directory.GetLastWriteTime(p); } catch { return default; } }
}
