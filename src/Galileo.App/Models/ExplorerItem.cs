using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Galileo.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Galileo.Models;

public enum ExplorerItemKind { Folder, File, Drive }

/// <summary>A folder, file, or drive shown in the file-explorer list.</summary>
public partial class ExplorerItem : ObservableObject
{
    /// <summary>Whether folder icons get a content-preview overlay (set from settings).</summary>
    public static bool ShowFolderPreviews = true;

    /// <summary>Whether file extensions are shown in the displayed name (set from settings).</summary>
    public static bool ShowExtensions = true;

    /// <summary>User-chosen folder thumbnails (folder path → image path), shared from app state.</summary>
    public static IReadOnlyDictionary<string, string>? FolderThumbnails;

    public string Path { get; private set; }
    public string Name { get; private set; }

    /// <summary>Shell parsing name for items that live in the shell namespace (MTP / portable
    /// devices) and have no filesystem path; null for ordinary filesystem items.</summary>
    public string? ShellId { get; }
    public bool IsShellItem => ShellId is not null;

    /// <summary>Name shown in the UI — drops the extension for files when extensions are hidden.</summary>
    public string DisplayName => Kind == ExplorerItemKind.File && !ShowExtensions
        ? System.IO.Path.GetFileNameWithoutExtension(Path)
        : Name;
    public ExplorerItemKind Kind { get; }
    public bool IsFolder => Kind != ExplorerItemKind.File;
    public long Size { get; }
    public DateTime Modified { get; }
    public string TypeName { get; private set; }

    [ObservableProperty] private bool _isAppHidden;
    [ObservableProperty] private ImageSource? _icon;

    private bool _iconRequested;
    private CancellationTokenSource? _iconCts;

    /// <summary>Total / free bytes for drives (0 for other items).</summary>
    public long TotalBytes { get; }
    public long FreeBytes { get; }

    /// <summary>Show the capacity bar + free/total text (drives with a known size).</summary>
    public bool ShowCapacity => Kind == ExplorerItemKind.Drive && TotalBytes > 0;
    public double UsedPercent => TotalBytes > 0 ? (double)(TotalBytes - FreeBytes) / TotalBytes * 100 : 0;
    public string CapacityText => TotalBytes > 0 ? $"{FormatSize(FreeBytes)} free of {FormatSize(TotalBytes)}" : "";
    public Visibility CapacityVisibility => ShowCapacity ? Visibility.Visible : Visibility.Collapsed;

    public ExplorerItem(string path, ExplorerItemKind kind, long size, DateTime modified, string typeName, string? displayName = null, string? shellId = null, long totalBytes = 0, long freeBytes = 0)
    {
        Path = path;
        ShellId = shellId;
        Kind = kind;
        Size = size;
        Modified = modified;
        TypeName = typeName;
        TotalBytes = totalBytes;
        FreeBytes = freeBytes;
        Name = displayName ?? (kind == ExplorerItemKind.Drive ? path : System.IO.Path.GetFileName(path));
        if (string.IsNullOrEmpty(Name)) Name = path;
    }

    /// <summary>Adopts the item's new path after an on-disk rename, updating the displayed name in
    /// place — the object (and its loaded icon) survives, so the list needn't reload or flicker.
    /// An extension change is a TYPE change: the type name and icon are refreshed then.</summary>
    public void Rename(string newPath)
    {
        var extChanged = Kind == ExplorerItemKind.File && !string.Equals(
            System.IO.Path.GetExtension(Path), System.IO.Path.GetExtension(newPath), StringComparison.OrdinalIgnoreCase);
        Path = newPath;
        Name = Kind == ExplorerItemKind.Drive ? newPath : System.IO.Path.GetFileName(newPath);
        if (string.IsNullOrEmpty(Name)) Name = newPath;
        if (extChanged)
        {
            TypeName = FileSystemService.TypeName(System.IO.Path.GetExtension(newPath));
            OnPropertyChanged(nameof(TypeName));
            ResetIcon(); // the old glyph/thumbnail belongs to the old type
        }
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DisplayName));
    }

    /// <summary>A fresh copy with no icon loaded. Views with different icon sizes must not share
    /// instances: <see cref="LoadIconAsync"/> keeps whichever bitmap loaded first, so a 32px sidebar
    /// icon would get stretched blurry in the large This PC grid.</summary>
    public ExplorerItem Clone() => new(Path, Kind, Size, Modified, TypeName, Name, ShellId, TotalBytes, FreeBytes);

    public bool IsImage => Kind == ExplorerItemKind.File && PhotoLibrary.IsSupported(IsShellItem ? Name : Path);

    public string SizeText => Kind == ExplorerItemKind.File ? FormatSize(Size) : "";
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

    // Dim app-hidden folders when the user has chosen to reveal them.
    public double ItemOpacity => IsAppHidden ? 0.45 : 1.0;
    partial void OnIsAppHiddenChanged(bool value) => OnPropertyChanged(nameof(ItemOpacity));

    /// <summary>
    /// Loads the file/folder/drive icon with proper transparency via the shell (IShellItemImageFactory),
    /// falling back to GetThumbnailAsync only if that fails.
    /// </summary>
    /// <summary>Clears the cached icon so the next <see cref="LoadIconAsync"/> regenerates it
    /// (e.g. after the folder's chosen thumbnail changes).</summary>
    public void ResetIcon()
    {
        _iconRequested = false;
        Icon = null;
    }

    /// <summary>Cancels a queued/in-flight icon load when the item scrolls off-screen (its list
    /// container is recycled), so a fast scroll doesn't leave hundreds of dead decodes flooding the
    /// pipeline. Already-loaded icons are kept; the load simply re-runs when the item reappears.</summary>
    public void CancelIconLoad()
    {
        if (Icon is not null) return; // finished loading — keep it
        try { _iconCts?.Cancel(); } catch (ObjectDisposedException) { }
        _iconRequested = false;
    }

    public async Task LoadIconAsync(uint size = 96)
    {
        if (_iconRequested) return;
        _iconRequested = true;
        _iconCts?.Dispose();
        _iconCts = new CancellationTokenSource();
        var ct = _iconCts.Token;

        var path = Path;
        var px = (int)Math.Clamp(size, 32u, 256u);

        // Throttle concurrent decodes — a fast scroll through hundreds of media files otherwise
        // floods the decode pipeline and crashes the render thread. The token lets a decode that's
        // still waiting for a slot bail out the moment its item scrolls off-screen.
        try
        {
        await DecodeThrottle.RunAsync(async () =>
        {
        try
        {
            // Shell-namespace items (MTP/portable devices) have no filesystem path: get the thumbnail
            // straight from the shell by parsing name (photos thumbnail; folders get the folder icon).
            if (IsShellItem)
            {
                using var shell = await Task.Run(() =>
                    ShellImaging.GetImage(ShellId!, px, iconOnly: Kind != ExplorerItemKind.File));
                if (shell.IsValid)
                {
                    var wb = new WriteableBitmap(shell.Width, shell.Height);
                    using (var s = wb.PixelBuffer.AsStream()) s.Write(shell.Pixels!, 0, shell.ByteCount);
                    Icon = wb;
                }
                else _iconRequested = false; // transient device/thumbnail miss → allow a retry instead of a permanent blank
                return;
            }

            // Folders & drives: Galileo's own flat icon (not the Windows shell icon). Folders overlay
            // a content preview (the first image inside), composited ourselves so it stays upright.
            if (Kind != ExplorerItemKind.File)
            {
                var folderKind = Kind == ExplorerItemKind.Folder ? IconFactory.FolderKindFor(path) : FolderKind.Normal;
                var pixels = Kind == ExplorerItemKind.Drive
                    ? await Task.Run(() => IconFactory.RenderDrive(px))
                    : await Task.Run(() => IconFactory.RenderFolder(px, folderKind));
                // Themed media folders keep their glyph; only plain folders get a content preview overlay.
                if (Kind == ExplorerItemKind.Folder && ShowFolderPreviews && folderKind == FolderKind.Normal)
                    await TryOverlayFirstImageAsync(path, pixels, px, px, px);

                var wb = new WriteableBitmap(px, px);
                using (var s = wb.PixelBuffer.AsStream()) s.Write(pixels, 0, pixels.Length);
                Icon = wb;
                return;
            }

            // Files.
            //
            // This deliberately does NOT use StorageFile.GetThumbnailAsync / BitmapImage. Those create WinRT
            // objects that are only reclaimed by the finalizer, and finalizing them has to marshal back to
            // the STA (UI) thread. Realizing a folder of photos queued hundreds of them: the finalizer thread
            // starved waiting on the UI thread, the GC blocked waiting on finalizers, and the UI thread
            // stalled for 10-15s waiting on the GC — which is what made opening a 256 KB photo take 15
            // seconds. IShellItemImageFactory does the same job (it IS what Explorer uses, honours EXIF
            // orientation, and reads the same thumbnail cache), runs entirely on a worker thread, and
            // releases its COM explicitly. The only UI-thread cost left is one WriteableBitmap memcpy.
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var appIcon = ext is ".exe" or ".lnk" or ".url" or ".msi" or ".ico" or ".scr" or ".cpl";

            // Only ask the shell for the kinds of file that actually have a preview. Everything else got a
            // shell call per file whose result was then thrown away in favour of Galileo's own icon.
            if (appIcon || IsImage || PhotoLibrary.IsMedia(path) || DocThumbExts.Contains(ext))
            {
                // Pooled: the shell hands back its cached 256x256 thumbnail (256 KB), which is 3x over the
                // Large Object Heap threshold. Allocating one per file drove a gen2 collection storm and
                // multi-second UI stalls; renting keeps it out of the GC entirely.
                using var img = await Task.Run(() => ShellImaging.GetImage(path, px, iconOnly: false));
                using var fallback = img.IsValid
                    ? default
                    : await Task.Run(() => ShellImaging.GetImage(path, px, iconOnly: true));
                var shot = img.IsValid ? img : fallback;

                if (ct.IsCancellationRequested) return;
                if (shot.IsValid)
                {
                    var wbApp = new WriteableBitmap(shot.Width, shot.Height);
                    using (var s = wbApp.PixelBuffer.AsStream()) s.Write(shot.Pixels!, 0, shot.ByteCount);
                    Icon = wbApp;
                    return;
                }
            }

            var fp = await Task.Run(() => IconFactory.RenderFile(px, ext));
            if (ct.IsCancellationRequested) return;
            var wbFile = new WriteableBitmap(px, px);
            using (var s = wbFile.PixelBuffer.AsStream()) s.Write(fp, 0, fp.Length);
            Icon = wbFile;
        }
        catch
        {
            _iconRequested = false; // allow a retry later
        }
        }, ct);
        }
        catch (OperationCanceledException)
        {
            _iconRequested = false; // scrolled off before its decode slot opened — reload when realized again
        }
    }

    private static async Task TryOverlayFirstImageAsync(string folderPath, byte[] folderPixels, int fw, int fh, int px)
    {
        // Prefer the user's chosen thumbnail for this folder; fall back to the first image inside.
        var imgPath = ChosenThumbnail(folderPath) ?? await Task.Run(() => FindFirstImage(folderPath));
        if (imgPath is null) return;
        try
        {
            // Same reason as the file thumbnails: StorageFile/BitmapDecoder/SoftwareBitmap are WinRT objects
            // whose finalizers have to marshal to the UI thread, and this ran once PER FOLDER. The shell
            // path does the whole thing on a worker thread and frees its COM immediately.
            using var photo = await Task.Run(() =>
                ShellImaging.GetImage(imgPath, Math.Max(48, px * 3 / 5), iconOnly: false));
            if (!photo.IsValid) return;
            // OverlayPhoto bounds itself by iw/ih, so the pooled buffer's slack is harmless.
            ImageCompositor.OverlayPhoto(folderPixels, fw, fh, photo.Pixels!, photo.Width, photo.Height);
        }
        catch { /* leave the plain folder icon */ }
    }

    /// <summary>The user-chosen thumbnail image for a folder, if one is set and still exists.</summary>
    private static string? ChosenThumbnail(string folderPath)
    {
        if (FolderThumbnails is null) return null;
        if (!FolderThumbnails.TryGetValue(folderPath, out var imgPath)) return null;
        return File.Exists(imgPath) && PhotoLibrary.IsSupported(imgPath) ? imgPath : null;
    }

    /// <summary>Non-media file types that still have a real shell preview worth fetching. Anything not in
    /// here (and not an image/video/app) gets Galileo's own icon without troubling the shell at all.</summary>
    private static readonly HashSet<string> DocThumbExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".svg", ".psd", ".ai", ".eps", ".rtf",
    };

    /// <summary>First supported image in a folder (bounded scan), or null.</summary>
    private static string? FindFirstImage(string folderPath)
    {
        try
        {
            var n = 0;
            foreach (var f in Directory.EnumerateFiles(folderPath))
            {
                if (PhotoLibrary.IsSupported(f)) return f;
                if (++n > 300) break; // don't scan huge non-image folders forever
            }
        }
        catch { /* access denied etc. */ }
        return null;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double v = bytes;
        var u = -1;
        do { v /= 1024; u++; } while (v >= 1024 && u < units.Length - 1);
        return $"{v:0.#} {units[u]}";
    }
}
