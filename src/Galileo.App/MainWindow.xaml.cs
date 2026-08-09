using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Galileo.Models;
using Galileo.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;

namespace Galileo;

public sealed partial class MainWindow : Window
{
    // Segoe Fluent Icons glyphs for the eye toggle.
    private const string GlyphEyeOpen = "";  // View
    private const string GlyphEyeOff = "";   // Hide

    private readonly AppState _state = App.State;
    private readonly PhotoLibrary _library;
    private readonly AppWindow _appWindow;

    private readonly List<PhotoItem> _allPhotos = new();
    private readonly ObservableCollection<PhotoItem> _view = new();

    // Paths currently "obscured" in-session by the eye toggle (privacy curtain).
    private readonly HashSet<string> _obscured = new(StringComparer.OrdinalIgnoreCase);

    private int _currentIndex = -1;
    private int _loadToken;           // bumped per LoadCurrentAsync; lets a stale decode bail out
    private double _rotation;
    private bool _isFullScreen;
    private bool _showHiddenAlbum;
    private bool _favoritesOnly;

    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _loadingSettings;
    private AppState? _settingsSnapshot;   // pre-edit copy, restored on Cancel

    // Spacebar Peek (Quick Look) state.
    private ExplorerItem? _peekItem;
    private int _peekToken;            // bumped per preview load so a fast nav cancels a stale decode

    // Rubber-band (marquee) selection state for the icon view.
    private bool _marqueeActive;
    private Windows.Foundation.Point _marqueeStart;

    // Open archives: maps a zip's extracted temp dir -> (zip path, display name) for breadcrumb labels.
    private readonly Dictionary<string, (string ZipPath, string Name)> _openZips = new(StringComparer.OrdinalIgnoreCase);

    // Shell-namespace browser for MTP / portable devices (phones, cameras) — no filesystem paths.
    private readonly ShellBrowser _shell = new();

    // Galileo's own self-contained recycle bin (independent of the Windows Recycle Bin).
    private readonly RecycleBin _bin = new();

    // One-shot: suppress "always open in new window" for the next programmatic open (startup / shell hand-off).
    private bool _bypassAlwaysNewWindow;
    private bool _windowActive = true;      // tracked via Activated; gates the live folder refresh
    private bool _pendingWatchRefresh;      // a folder change arrived while inactive — refresh on activation

    // In-app file clipboard — a reliable fallback for cut/copy/paste. The system clipboard's StorageItems
    // round-trip and its RequestedOperation (cut vs copy) flag are unreliable in unpackaged apps, so we
    // also remember the paths + move intent here and prefer them when they match.
    private (List<string> Paths, bool Move)? _fileClip;

    /// <summary>The secure-wipe method chosen in Settings (used for Empty / shred / Shift+Delete).</summary>
    private WipeMethod CurrentWipeMethod => SecureWipe.Parse(_state.WipeMethod);

    // Live folder refresh: auto-show files added/removed/renamed outside the app.
    private FileSystemWatcher? _folderWatcher;
    private string? _watchedPath;
    private int _watchErrorCount;
    // Locations whose watcher fired Error repeatedly (network / WSL / 9P shares) — never re-arm a watcher
    // there, or it floods the UI thread with rebuild→error loops. Live refresh is off there; F5 still works.
    private readonly HashSet<string> _watchUnsupported = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _watchDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };

    // Developer-mode embedded terminal.
    private TerminalSession? _term;
    private bool _termWebReady;
    private short _termCols, _termRows;
    private readonly List<(string Label, string Exe)> _shells = new();

    // Secure vault state.
    private readonly VaultManager _vaults = App.Vaults; // process-wide — see App.Vaults

    // Live sort/group of THIS window. Deliberately window-local: _state.SortBy is process-wide and
    // shared by every window, so using it as the live value let one window's navigation rewrite
    // another's sort (and poison its next per-folder save). _state.* keeps only the last-used
    // default that seeds new windows and folders without a remembered pref.
    private string _sortBy = App.State.SortBy;
    private bool _sortDescending = App.State.SortDescending;
    private string _groupBy = App.State.GroupBy;
    private readonly GoogleDriveBackup _drive = new();

    // Scheduled vault backups: a periodic check runs a backup when one is overdue (see AppState.BackupSchedule).
    private readonly DispatcherTimer _backupTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly ObservableCollection<Models.VaultInfo> _vaultList = new();
    private readonly DispatcherTimer _vaultIdleTimer = new();
    // Commit the unlocked vault's working folder to its encrypted store continuously, so a non-graceful
    // exit can't lose changes. Backstop (periodic) + a short debounce fired when the working folder changes.
    private readonly DispatcherTimer _vaultFlushTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _vaultFlushDebounce = new() { Interval = TimeSpan.FromMilliseconds(2500) };
    private bool _closingForVaultLock;  // guards the re-entrant AppWindow.Closing lock flow
    // Push: watch the unlocked vault's working folder so we can tell active viewers the instant it changes
    // (so they re-list without waiting for their poll). Debounced to coalesce bursts (e.g. multi-file adds).

    // Polls for mounted/removed drives so the sidebar and This PC view stay current
    // (WinUI 3 doesn't surface WM_DEVICECHANGE directly).
    private readonly DispatcherTimer _driveWatcher = new() { Interval = TimeSpan.FromSeconds(2) };
    private HashSet<string> _knownDrives = new(StringComparer.OrdinalIgnoreCase);

    // File-explorer state
    private FileSystemService _fs = null!;
    private readonly ObservableCollection<ExplorerItem> _explorerItems = new();
    private readonly List<string?> _navHistory = new();
    private List<ExplorerItem> _explorerRaw = new();
    private int _navIndex = -1;
    private string? _currentFolder; // null = home (This PC)
    private bool _showAppHidden;
    // Show Windows-hidden (OS hidden attribute) files/folders. Session-only — never persisted, resets on launch.
    private bool _showWindowsHidden;
    private string _explorerViewMode = "Large";
    private double _iconSize = 110;
    private ExplorerItem? _explorerContextItem;

    // Search
    private string _searchQuery = "";
    private bool _searchRecursive;
    private List<ExplorerItem> _searchResults = new();
    private bool _suppressSearchEvent;

    // Tabs
    private bool _switchingTabs;

    // Privacy gate (unlocked once per session after a successful Hello check)
    private bool _helloUnlocked;


    // False until the file-manager half of the window is built. A window opened to show a single photo
    // skips it entirely and only pays for it if the user actually navigates to the explorer.
    private bool _fileManagerReady;

    /// <summary>
    /// True for an additional window opened by the running instance ("open in new window"). Such a window
    /// is a guest: it must not create a tray icon and must not run any once-per-process crash recovery.
    ///
    /// This cannot be inferred from the command line. "--new-window" used to spawn a whole new process, so
    /// checking Environment.GetCommandLineArgs() worked; those windows are now created in-process by the
    /// primary (App.OnRedirected), whose own command line has no such argument. The checks that relied on
    /// it were therefore silently doing nothing — leaving a tray icon per opened photo, and, far worse,
    /// letting a guest window run vault crash-recovery that would wipe the live vault's working folder out
    /// from under the primary.
    /// </summary>
    private readonly bool _secondaryWindow;

    // Collage mode state
    private readonly Random _rng = new();
    private List<PhotoItem> _collageSource = new();
    private List<PhotoItem> _collageItems = new();
    private int _collageCount;
    private CollagePreset _collagePreset = CollagePreset.Justified;

    public MainWindow(string? initialPath = null, bool secondaryWindow = false)
    {
        _secondaryWindow = secondaryWindow;
        InitializeComponent();
        _library = new PhotoLibrary(_state);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.Title = "Galileo";
        try { _appWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "galileo.ico")); } catch { }
        try { TitleLogo.Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "galileo.png"))); } catch { }

        // A photo window ("open in new window") re-opens at its last position/size — e.g. the monitor
        // the user dragged the previous one to. Validated against the current displays so a spot saved
        // on a since-unplugged monitor snaps to the nearest one instead of opening off-screen.
        if ((_secondaryWindow || LaunchedNewWindow()) && _state.PhotoWinW > 0 && _state.PhotoWinH > 0)
        {
            try
            {
                var rect = new Windows.Graphics.RectInt32(_state.PhotoWinX, _state.PhotoWinY, _state.PhotoWinW, _state.PhotoWinH);
                var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest).WorkArea;
                // Size first: a rect remembered on a 4K monitor restored onto a laptop panel must
                // shrink, or the caption buttons land off-screen and the window can't be closed.
                rect.Width = Math.Min(rect.Width, area.Width);
                rect.Height = Math.Min(rect.Height, area.Height);
                rect.X = Math.Clamp(rect.X, area.X, Math.Max(area.X, area.X + area.Width - rect.Width));
                rect.Y = Math.Clamp(rect.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - rect.Height));
                _appWindow.MoveAndResize(rect);
            }
            catch { /* display topology quirk — fall back to default placement */ }
        }

        // Mica backdrop for a modern translucent window (cached; reused across theme changes).
        EnsureMica();

        // Seamless modern chrome: draw our own content up into the title bar.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var tb = _appWindow.TitleBar;
            tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }

        _chromeTimer.Tick += (_, _) =>
        {
            _chromeTimer.Stop();
            ViewerChrome.Opacity = 0;
            ViewerChrome.IsHitTestVisible = false;
            // Invisible controls must leave the TAB ORDER too — Opacity=0 alone left keyboard users
            // walking 15 transparent buttons with no visible focus.
            ViewerChrome.Visibility = Visibility.Collapsed;
            if (InVideo)
            {
                // The video pills fade with the same timer instead of sitting over the frame forever.
                VideoBackBar.Visibility = Visibility.Collapsed;
                VideoControlsBar.Visibility = Visibility.Collapsed;
            }
        };

        // File explorer is the home view.
        _fs = new FileSystemService(_state);
        ExplorerIconsView.ItemsSource = _explorerItems;
        ExplorerDetailsList.ItemsSource = _explorerItems;
        ExplorerIconsView.ItemTemplate = (DataTemplate)Application.Current.Resources["ExplorerIconTemplate"];
        _iconSize = _state.IconSize is > 0 and <= 240 ? _state.IconSize : 110;
        _explorerViewMode = _state.ExplorerViewMode is "Large" or "Medium" or "Small" or "Details" ? _state.ExplorerViewMode : "Medium";
        _collagePreset = ParseCollagePreset(_state.CollagePreset);
        ExplorerItem.ShowFolderPreviews = _state.FolderPreviews;
        ExplorerItem.ShowExtensions = _state.ShowExtensions;
        ExplorerItem.FolderThumbnails = _state.FolderThumbnails;

        _vaultIdleTimer.Tick += VaultIdle_Tick;
        _vaultFlushTimer.Tick += (_, _) => FlushVaultSoon();
        _vaultFlushDebounce.Tick += (_, _) => { _vaultFlushDebounce.Stop(); FlushVaultSoon(); };
        _volSaveDebounce.Tick += (_, _) => { _volSaveDebounce.Stop(); _state.Save(); };

        // A drag that leaves without dropping must not poison the NEXT drag's move/copy decision.
        ExplorerIconsView.DragLeave += (_, _) => ResetDragSource();
        ExplorerDetailsList.DragLeave += (_, _) => ResetDragSource();

        RootGrid.SizeChanged += RootGrid_SizeChanged; // keep the Settings card inside a shrinking window
        // Overlays trap Tab inside their card (their scrims only LOOK modal).
        SettingsCard.TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Cycle;
        PeekCard.TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Cycle;

        // Hovering a chrome control must hold it open — the auto-hide timer only reset on pointer
        // MOVE, so pausing 3s over a button faded it away under the cursor mid-click.
        ViewerChrome.PointerEntered += (_, _) => _chromeTimer.Stop();
        ViewerChrome.PointerExited += (_, _) => { _chromeTimer.Stop(); _chromeTimer.Start(); };
        VideoControlsBar.PointerEntered += (_, _) => _chromeTimer.Stop();
        VideoControlsBar.PointerExited += (_, _) => { _chromeTimer.Stop(); _chromeTimer.Start(); };
        VideoBackBar.PointerEntered += (_, _) => _chromeTimer.Stop();
        VideoBackBar.PointerExited += (_, _) => { _chromeTimer.Stop(); _chromeTimer.Start(); };

        // Track activation: privacy re-hide, catch-up of deferred folder refreshes, and a diagnostic
        // trail (CodeActivated on a window the user didn't click = something programmatic stole focus).
        Activated += (_, e) =>
        {
            App.LogInfo($"win {(_secondaryWindow ? "photo" : "main")}#{GetHashCode():x8}: {e.WindowActivationState}{(InEditor ? " [editor]" : "")}");
            _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
            if (_windowActive && _pendingWatchRefresh && ExplorerView.Visibility == Visibility.Visible)
            {
                _pendingWatchRefresh = false;
                RefreshFolderIncremental();
            }
            if (!_windowActive) ReHideOnBackground();
        };
        // When the clipboard changes from OUTSIDE Galileo (another app, or a text/image copy), drop our
        // in-app file clip so a later paste uses the new content — not a stale earlier file copy.
        // Compare CONTENT rather than counting events: SetContent can raise zero or several
        // ContentChanged notifications, so a one-shot suppress flag either swallowed a real external
        // copy (stale paste) or nulled our own clip (cut degraded to copy).
        Clipboard.ContentChanged += async (_, _) =>
        {
            if (_fileClip is not { } fc) return;
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await content.GetStorageItemsAsync();
                    var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                    if (SamePaths(paths, fc.Paths)) return; // still our clip — our own SetContent echoing
                }
            }
            catch { return; } // clipboard busy/inaccessible — keep the in-app clip rather than guess
            _fileClip = null;
        };
        _appWindow.Closing += AppWindow_Closing;

        // Catch Ctrl+C/X/V/A even if the explorer list marks them handled first (handledEventsToo).
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(ExplorerClipboard_KeyDown), handledEventsToo: true);

        ApplyDeveloperMode(); // show/hide the Terminal button per the saved setting
        SetResizeCursor(TerminalSplitter); // ↔ cursor on the explorer/terminal divider
        SetResizeCursor(SidebarSplitter);  // ↔ cursor on the sidebar/file-pane divider
        SidebarCol.Width = new GridLength(Math.Clamp(_state.SidebarWidth is > 0 ? _state.SidebarWidth : 240, 160, 560));

        IconSizeSlider.Value = _iconSize;
        ApplyIconSize();
        ApplyTheme();
        ApplyClickMode();
        SyncSortGroupRadios();
        UpdateSortHeaders();

        // Debounced reload when the current folder changes on disk (downloads, other apps, etc.).
        _watchDebounce.Tick += (_, _) =>
        {
            _watchDebounce.Stop();
            if (ExplorerView.Visibility == Visibility.Visible
                && string.IsNullOrEmpty(_searchQuery)
                && string.Equals(_currentFolder, _watchedPath, StringComparison.OrdinalIgnoreCase))
            {
                // Refreshing reshuffles the list (grouped views even rebuild the ItemsSource), which can
                // move focus — and moving focus in an INACTIVE window of this process pulls the foreground
                // away from the window the user is actually working in (e.g. saving in the editor made the
                // explorer behind it refresh and steal focus). Defer until this window is next activated.
                if (_windowActive) { App.LogInfo($"watch: refresh {_currentFolder}"); RefreshFolderIncremental(); }
                else { App.LogInfo($"watch: refresh deferred (window inactive) {_currentFolder}"); _pendingWatchRefresh = true; }
            }
            // If the change was inside the unlocked vault, commit it to the encrypted store promptly.
            if (_vaults.Current?.WorkingDir is { } wd && _currentFolder is { } cf
                && cf.StartsWith(wd, StringComparison.OrdinalIgnoreCase))
                ScheduleVaultFlush();
        };

        InitBackground(); // tray icon + start-hidden if launched with --background (cheap; always needed)

        // Windows may launch us with a file (default app) or folder to open.
        // Opening a single photo does NOT build the file manager: that path used to enumerate the
        // photo's whole containing folder and generate a thumbnail for every file in it, then look the
        // photo up in that list — so the viewer couldn't show the image until the entire folder had
        // loaded. On a folder of a few hundred images that is several seconds; and because WinUI runs
        // every window in a process on ONE UI thread, opening a second and third photo stacked more
        // full file managers onto the same thread and it stalled for 15+ seconds. See EnsureFileManager.
        if (!string.IsNullOrEmpty(initialPath) && System.IO.File.Exists(initialPath)
            && (PhotoLibrary.IsSupported(initialPath) || PhotoLibrary.IsMedia(initialPath)))
        {
            OpenViewerDirect(initialPath);
        }
        else if (!string.IsNullOrEmpty(initialPath) && System.IO.Directory.Exists(initialPath))
        {
            EnsureFileManager();
            NewTab(initialPath);
        }
        else
        {
            EnsureFileManager();
            NewTab(null); // This PC / home
        }

        // Cold-start baseline in the diagnostics log — lets perf work show its receipts.
        try
        {
            var up = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
            App.LogInfo($"startup: window constructed {up.TotalMilliseconds:0} ms after process start ({(_secondaryWindow ? "photo" : "main")})");
        }
        catch { }
    }

    /// <summary>
    /// Builds the file-manager half of the window: sidebar, pinned items, devices, vault list, drive
    /// watcher, tray icon and background services. Deferred so a window opened purely to show one photo
    /// pays for none of it (see the constructor). Runs at most once; safe to call repeatedly.
    /// </summary>
    private void EnsureFileManager()
    {
        if (_fileManagerReady) return;
        _fileManagerReady = true;

        PopulateSidebar();
        PopulatePinned();
        PopulateDevices();

        // Secure vault: wipe any decrypted working folder left by a crash, list vaults, and arm the
        // idle auto-lock + app-exit lock. An "open in new window" window belongs to an already-running
        // primary (which may have a vault unlocked) — it must NOT run crash recovery, or it would wipe the
        // live vault's working folder out from under the primary. See _secondaryWindow: this deliberately
        // does not consult the command line, which is the primary's and says nothing about this window.
        if (!_secondaryWindow && !LaunchedNewWindow())
        {
            _shell.WipeTemp();            // device temp copies are process-wide — a guest must not wipe the primary's
            _vaults.WipeOrphanWorkDirs();
            ArchiveService.WipeOrphans(); // clear any leftover extracted-zip temp dirs from a prior run
            RecoverRenameJournal();       // restore names stranded by a crash mid bulk-rename
            _ = Task.Run(() => ThumbDiskCache.Sweep()); // keep the thumbnail cache under its size cap
        }
        // Guest photo windows get no vault affordances at all: vault lifecycle (unlock/lock/share)
        // belongs to the primary window, and offering it here invites concurrent unlocks of the same
        // vault working folder.
        if (_secondaryWindow || LaunchedNewWindow())
        {
            VaultsLockedEntry.Visibility = Visibility.Collapsed;
            VaultsSection.Visibility = Visibility.Collapsed;
        }
        else
        {
            VaultsList.ItemsSource = _vaultList;
            RefreshVaults();
        }

        // Watch for drives being mounted/removed and keep the UI in sync.
        _knownDrives = CurrentDriveSignature();
        _driveWatcher.Tick += DriveWatcher_Tick;
        _driveWatcher.Start();

        // Stay signed in to Google Drive across launches (silent token refresh; no browser).
        if (GoogleDriveBackup.IsConfigured) _ = SilentReconnectDriveAsync();

        // Scheduled vault backups: re-check periodically while the app is open (launch check runs
        // once the silent reconnect above completes).
        _backupTimer.Tick += async (_, _) => await MaybeRunScheduledBackupAsync();
        _backupTimer.Start();
    }

    /// <summary>
    /// Shows one photo immediately, without touching the explorer. The folder's other images are pulled
    /// in afterwards (off-thread, paths only — no thumbnails) so arrow-key / swipe navigation still works;
    /// they just don't block the image the user actually asked for.
    /// </summary>
    /// <summary>Bumped whenever the photo pipeline (_allPhotos) is rebuilt, so a slow sibling backfill
    /// from an EARLIER open can't clobber the pipeline of the photo now on screen.</summary>
    private int _pipelineGen;

    private async void OpenViewerDirect(string path)
    {
        try
        {
            // Videos/audio aren't photos — LoadFiles below would filter them out and leave an EMPTY
            // viewer. Route them straight to the embedded media player instead.
            if (PhotoLibrary.IsMedia(path))
            {
                var fi = new System.IO.FileInfo(path);
                OpenVideoFromExplorer(new ExplorerItem(path, ExplorerItemKind.File, fi.Length, fi.LastWriteTime, fi.Extension));
                return;
            }

            var gen = ++_pipelineGen;
            _allPhotos.Clear();
            foreach (var p in _library.LoadFiles(new[] { path })) _allPhotos.Add(p);
            // A photo in the Hidden album must open AS ITSELF: with the normal filter it would be
            // excluded from _view and the viewer would silently show a different image (index 0).
            _showHiddenAlbum = _allPhotos.Count > 0 && _allPhotos[0].IsHidden;
            _favoritesOnly = false;
            RefreshView();
            _currentIndex = 0;

            ShowViewer();
            await LoadCurrentAsync(); // the photo is on screen from here on

            // Now backfill the siblings for next/previous.
            var dir = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;

            // Order the siblings the way the EXPLORER would show this folder — its remembered
            // per-folder sort AND grouping, else the current global ones — so the viewer's arrow keys
            // walk the same sequence the user sees in the file list (this window may never have shown
            // it). Grouped views display group-by-group, so the same grouping is applied here: a
            // stable re-order by group rank/key that keeps the sort within each group.
            var (sortBy, sortDesc, groupBy) = _state.FolderSorts.TryGetValue(dir, out var pref)
                ? (pref.SortBy, pref.SortDescending, pref.GroupBy)
                : (_sortBy, _sortDescending, _groupBy);
            var siblings = await Task.Run(() =>
            {
                try
                {
                    long SafeLen(System.IO.FileInfo f) { try { return f.Length; } catch { return 0; } }
                    var files = new System.IO.DirectoryInfo(dir).EnumerateFiles()
                        .Where(f => PhotoLibrary.IsSupported(f.FullName))
                        .Select(f => new ExplorerItem(f.FullName, ExplorerItemKind.File, SafeLen(f),
                                                      f.LastWriteTime, FileSystemService.TypeName(f.Extension)))
                        .ToList();
                    var sorted = SortItems(files, sortBy, sortDesc);
                    if (groupBy != "None")
                        sorted = sorted
                            .OrderBy(i => GroupKeyRank(i, groupBy).Rank)
                            .ThenBy(i => GroupKeyRank(i, groupBy).Key, StringComparer.OrdinalIgnoreCase)
                            .ToList(); // OrderBy is stable — within-group order stays the user's sort
                    return sorted.Select(i => i.Path).ToList();
                }
                catch { return new List<string>(); }
            });

            if (siblings.Count <= 1) return;
            if (!InViewer) return;          // user already navigated away
            if (gen != _pipelineGen) return; // a different open rebuilt the pipeline while we listed

            var keep = Current?.Path ?? path;
            // LoadFiles RE-SORTS BY NAME — feed its items back in the explorer order computed above,
            // or the arrow keys walk alphabetically regardless of the folder's sort/grouping (the same
            // trap PopulatePhotoPipelineFromCurrent works around the same way).
            var byPath = _library.LoadFiles(siblings).ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
            _allPhotos.Clear();
            foreach (var s in siblings)
                if (byPath.TryGetValue(s, out var item)) _allPhotos.Add(item);
            RefreshView();

            // Re-anchor on the photo actually being shown — RefreshView rebuilt _view underneath us.
            var idx = _view.ToList().FindIndex(p => string.Equals(p.Path, keep, StringComparison.OrdinalIgnoreCase));
            _currentIndex = Math.Max(0, idx);
        }
        catch (Exception ex) { App.Log("OpenViewerDirect", ex); }
    }

    /// <summary>Opens a file path that lives in the current folder (image → viewer, video → player, else default app).</summary>
    private void OpenPathInCurrentTab(string path)
    {
        var match = _explorerItems.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (match is not null) { _bypassAlwaysNewWindow = true; OpenExplorerItem(match); }
    }

    /// <summary>Opens a file/folder handed to us by another (redirected) instance, in a new tab.</summary>
    public void OpenExternalPath(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) NewTab(path);
            else if (System.IO.File.Exists(path))
            {
                NewTab(System.IO.Path.GetDirectoryName(path));
                OpenPathInCurrentTab(path);
            }
        }
        catch (Exception ex) { App.Log("OpenExternalPath", ex); }
    }

    // 'new' intentionally hides the (unused) Window.Current; this is the currently viewed photo.
    private new PhotoItem? Current =>
        _currentIndex >= 0 && _currentIndex < _view.Count ? _view[_currentIndex] : null;

    private bool InViewer => ViewerView.Visibility == Visibility.Visible;

    /// <summary>True while the embedded video player is on screen.</summary>
    private bool InVideo => VideoPlayer.Visibility == Visibility.Visible;

    // ===================== Folder loading =====================

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            _state.LastFolder = folder.Path;
            _state.Save();
            await LoadFolderAsync(folder.Path);
        }
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail
        };
        foreach (var ext in PhotoLibrary.SupportedExtensions) picker.FileTypeFilter.Add(ext);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is not null) await OpenSinglePhotoAsync(file.Path);
    }

    /// <summary>Opens one image: loads its containing folder as the gallery and jumps to it.</summary>
    /// <summary>Opens a local file in the right in-app surface: image → viewer, video/audio → player,
    /// anything else → its default app. Used for vault files that must stay in-process.</summary>
    private async Task OpenLocalFileInViewerAsync(string path)
    {
        try
        {
            if (PhotoLibrary.IsSupported(path)) await OpenSinglePhotoAsync(path);
            else if (PhotoLibrary.IsMedia(path))
            {
                var fi = new FileInfo(path);
                var item = new ExplorerItem(path, ExplorerItemKind.File, fi.Length, fi.LastWriteTime, fi.Extension);
                OpenVideoFromExplorer(item);
            }
            else
            {
                try { ShellOps.AllowForeground(); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
                catch (Exception ex) { StatusText.Text = ex.Message; }
            }
        }
        catch (Exception ex) { App.Log("OpenLocalFile", ex); }
    }

    private async Task OpenSinglePhotoAsync(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path);
        if (folder is null) return;

        _state.LastFolder = folder;
        _state.Save();
        await LoadFolderAsync(folder);

        var match = _view.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            _currentIndex = _view.IndexOf(match);
            ShowViewer();
            await LoadCurrentAsync();
        }
    }

    /// <summary>Builds a gallery from an explicit list of image files (multi-file drop).</summary>
    private async Task LoadPathsAsync(System.Collections.Generic.List<string> paths)
    {
        StatusText.Text = "Loading…";
        ShowExplorer();

        _pipelineGen++; // invalidate any in-flight sibling backfill from an earlier direct open
        var items = await Task.Run(() => _library.LoadFiles(paths));
        _allPhotos.Clear();
        _allPhotos.AddRange(items);
        RefreshView();

        StatusText.Text = $"{_allPhotos.Count} photo(s)";
    }

    // ===================== Drag & drop =====================

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = "Open in Galileo";
                e.DragUIOverride.IsContentVisible = true;
            }
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        try
        {
            var dropped = await e.DataView.GetStorageItemsAsync();

            // Dropping onto an open collage adds the images to it.
            if (InCollage)
            {
                var dropFiles = dropped.OfType<StorageFile>()
                    .Select(f => f.Path).Where(PhotoLibrary.IsSupported).ToList();
                if (dropFiles.Count > 0) await AddToCollageAsync(dropFiles);
                else StatusText.Text = "No supported images in the dropped items.";
                return;
            }

            // A dropped folder wins: load it as the gallery.
            var folder = dropped.OfType<StorageFolder>().FirstOrDefault();
            if (folder is not null)
            {
                _state.LastFolder = folder.Path;
                _state.Save();
                await LoadFolderAsync(folder.Path);
                return;
            }

            var files = dropped.OfType<StorageFile>()
                .Select(f => f.Path)
                .Where(PhotoLibrary.IsSupported)
                .ToList();

            if (files.Count == 1) await OpenSinglePhotoAsync(files[0]);
            else if (files.Count > 1) await LoadPathsAsync(files);
            else StatusText.Text = "No supported images in the dropped items.";
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task LoadFolderAsync(string folder)
    {
        StatusText.Text = "Loading…";
        ShowExplorer();

        var items = await Task.Run(() => _library.Load(folder));
        _allPhotos.Clear();
        _allPhotos.AddRange(items);
        RefreshView();

        StatusText.Text = $"{_allPhotos.Count} photo(s) in {folder}";
    }

    /// <summary>Rebuilds the visible collection from filters (hidden album, favorites).</summary>
    private void RefreshView()
    {
        IEnumerable<PhotoItem> q = _allPhotos;
        q = _showHiddenAlbum ? q.Where(p => p.IsHidden) : q.Where(p => !p.IsHidden);
        if (_favoritesOnly) q = q.Where(p => p.IsFavorite);

        _view.Clear();
        foreach (var p in q) _view.Add(p);
    }

    // ===================== Viewer =====================

    private void BackToGallery_Click(object sender, RoutedEventArgs e) => ShowExplorer();

    private void ShowViewer()
    {
        ExplorerView.Visibility = Visibility.Collapsed;
        CollageView.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Visible;
        UpdateChromeForDarkSurface();
        ShowChrome();
    }

    /// <summary>Returns to the file-explorer home (the photo viewer / collage live on top of it).</summary>
    private void ShowExplorer()
    {
        // A window that opened straight into the viewer has no file manager yet — build it now, on
        // first use, and give it a tab on the folder the photo came from.
        EnsureFileManager();
        if (ExplorerTabs.TabItems.Count == 0)
        {
            // In video/audio mode Current (a PhotoItem) is null — the playing file's path is the seed,
            // so backing out of a directly-opened video lands in ITS folder, not This PC.
            var from = Current?.Path ?? _currentVideoPath;
            NewTab(string.IsNullOrEmpty(from) ? null : System.IO.Path.GetDirectoryName(from));
            return; // NewTab re-enters here with a tab in place and finishes the transition
        }

        if (VideoEditorPanel.Visibility == Visibility.Visible || EditTimeline.Visibility == Visibility.Visible) CloseVideoEditor();
        StopVideo();
        ViewerView.Visibility = Visibility.Collapsed;
        CollageView.Visibility = Visibility.Collapsed;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
        ExplorerView.Visibility = Visibility.Visible;
        UpdateChromeForDarkSurface();
        ModeLabel.Text = ""; // the title-bar label is always visible — clear the viewed file's name on the way out
        _chromeTimer.Stop();
        // Re-assert the icon-grid cell size: when the explorer is re-shown after the viewer, the
        // ItemsWrapGrid can come back without ItemWidth/Height and render a thumbnail at full size.
        // Apply now and again after layout (the panel may not be realized yet on the first pass).
        ApplyIconSize();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyIconSize);

        // A folder change that arrived while the viewer was up (window active, explorer hidden) was
        // deferred with nowhere to land — the activation hook only fires on focus changes. Catch up now.
        if (_pendingWatchRefresh) { _pendingWatchRefresh = false; RefreshFolderIncremental(); }
    }

    // --- Viewer chrome auto-hide ---

    private void ShowChrome()
    {
        if (InVideo)
        {
            VideoBackBar.Visibility = Visibility.Visible;
            VideoControlsBar.Visibility = Visibility.Visible;
        }
        else if (InViewer) ViewerChrome.Visibility = Visibility.Visible; // undo the timer's collapse
        ViewerChrome.Opacity = 1;
        ViewerChrome.IsHitTestVisible = true;
        _chromeTimer.Stop();
        _chromeTimer.Start();
    }

    private void ViewerView_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowChrome();

    /// <summary>Decoded image ready for the viewer. Cap the decoded size on the longer side:
    /// full-resolution decode can exceed the GPU's max texture size on large images
    /// (panoramas/huge screenshots) and crash the render thread — a failure try/catch can't see.
    /// 8000px is well under the ~16384 D3D limit and still sharp for screen + zoom.</summary>
    private static async Task<BitmapImage> DecodeViewerImageAsync(string path)
    {
        const int maxSide = 8000;
        var file = await StorageFile.GetFileFromPathAsync(path);
        var props = await file.Properties.GetImagePropertiesAsync();
        uint w = props.Width, h = props.Height;
        using var stream = await file.OpenReadAsync();
        var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
        if (w > 0 && h > 0 && Math.Max(w, h) > maxSide)
        {
            if (w >= h) bmp.DecodePixelWidth = maxSide;
            else bmp.DecodePixelHeight = maxSide;
        }
        await bmp.SetSourceAsync(stream);
        return bmp;
    }

    // Neighbor preload: arrows feel instant because the next/previous photo is usually decoded by
    // the time it's asked for. Two entries max (±1) so a giant image can pin at most two decodes.
    // Entries carry the file's mtime and are revalidated at use, so an edit/overwrite on disk can
    // never serve a stale preloaded frame.
    private readonly Dictionary<string, (BitmapImage Bmp, DateTime MtimeUtc)> _preloaded = new(StringComparer.OrdinalIgnoreCase);
    private int _preloadGen;

    private bool TryTakePreloaded(string path, out BitmapImage bmp)
    {
        bmp = null!;
        if (!_preloaded.TryGetValue(path, out var e)) return false;
        try { if (File.GetLastWriteTimeUtc(path) != e.MtimeUtc) { _preloaded.Remove(path); return false; } }
        catch { _preloaded.Remove(path); return false; }
        bmp = e.Bmp;
        return true;
    }

    private async void PreloadNeighborsAsync()
    {
        var gen = ++_preloadGen;
        var wanted = new List<string>();
        foreach (var d in new[] { 1, -1 })
        {
            var i = _currentIndex + d;
            if (i >= 0 && i < _view.Count && PhotoLibrary.IsSupported(_view[i].Path)) wanted.Add(_view[i].Path);
        }
        // Drop entries that are no longer neighbors (keeps the cache at ≤2 decoded images).
        foreach (var k in _preloaded.Keys.Where(k => !wanted.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            _preloaded.Remove(k);
        foreach (var p in wanted)
        {
            if (_preloaded.ContainsKey(p)) continue;
            try
            {
                var mtime = File.GetLastWriteTimeUtc(p);
                var bmp = await DecodeViewerImageAsync(p);
                if (gen != _preloadGen) return;      // navigation moved on — neighbors changed
                _preloaded[p] = (bmp, mtime);
            }
            catch { /* preload is best-effort; the real load reports errors */ }
        }
    }

    private async Task LoadCurrentAsync()
    {
        var item = Current;
        if (item is null)
        {
            ShowExplorer();
            return;
        }

        EnterImageMode();
        _rotation = 0;
        _bmpW = _bmpH = 0;

        // Generation token: if the user flips to the next photo while this one is still
        // decoding, the older (possibly slower) decode must not overwrite the newer image.
        var token = ++_loadToken;

        try
        {
            if (!TryTakePreloaded(item.Path, out var bmp))
            {
                bmp = await DecodeViewerImageAsync(item.Path);
                if (token != _loadToken) return; // a newer photo won the race — drop this one
            }
            ViewerImage.Source = bmp;
            _bmpW = bmp.PixelWidth;
            _bmpH = bmp.PixelHeight;
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            App.Log("LoadCurrent", ex);
            StatusText.Text = $"Could not open {item.FileName}: {ex.Message}";
            ViewerImage.Source = null;
        }

        ResetView();
        UpdateFavoriteIcon();
        UpdateEyeState();
        ModeLabel.Text = $"{item.FileName}   ({_currentIndex + 1}/{_view.Count})";
        PreloadNeighborsAsync(); // fire-and-forget: warm ±1 for instant arrows
        if (InfoPanel.Visibility == Visibility.Visible) await PopulateInfoAsync();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(+1);

    private void Navigate(int delta)
    {
        if (!InViewer || _view.Count == 0) return;
        _currentIndex = (_currentIndex + delta + _view.Count) % _view.Count;
        _ = LoadCurrentAsync();
    }

    // ===================== Zoom / pan / rotate =====================
    //
    // The Image fills ImageHost and uses Stretch=Uniform, so at scale 1 the photo is
    // fully visible (scaled up or down to fit). We apply zoom (scale), pan (translate)
    // and rotation through a single CompositeTransform that we drive directly — no
    // ScrollViewer, so the mouse wheel zooms instead of scrolling.

    private const double MaxScale = 8.0;

    private double _scale = 1.0;
    private double _minScale = 1.0;   // the fit scale for the current rotation (1.0 unless rotated 90/270)
    private double _tx;
    private double _ty;
    private double _bmpW;             // source pixel size of the current photo
    private double _bmpH;

    private bool _panning;
    private Windows.Foundation.Point _panStart;
    private double _panStartTx;
    private double _panStartTy;

    private void ApplyTransform()
    {
        ViewerTransform.ScaleX = _scale;
        ViewerTransform.ScaleY = _scale;
        ViewerTransform.TranslateX = _tx;
        ViewerTransform.TranslateY = _ty;
        ViewerTransform.Rotation = _rotation;
    }

    /// <summary>
    /// Scale that makes the photo fully fit the host at the current rotation. 1.0 for
    /// 0°/180°; for 90°/270° the width/height swap, so the image is scaled down to fit.
    /// </summary>
    private double FitScaleForRotation()
    {
        double W = ImageHost.ActualWidth, H = ImageHost.ActualHeight;
        if (_bmpW <= 0 || _bmpH <= 0 || W <= 0 || H <= 0) return 1.0;

        // The Image fills the host with Uniform stretch, so at transform-scale 1 the photo is
        // already magnified by `uniform`. We want the base view magnified by `m` instead:
        //   m = min(1.0, fitMagnification)  → fit large photos, but never upscale small ones.
        var uniform = Math.Min(W / _bmpW, H / _bmpH);
        var quarterTurn = _rotation % 180 != 0;
        var fitMagnification = quarterTurn ? Math.Min(W / _bmpH, H / _bmpW) : uniform;
        var m = Math.Min(1.0, fitMagnification);
        return m / uniform; // transform scale that yields magnification m
    }

    /// <summary>Resets to the centered fit for the current rotation (zoom/pan cleared).</summary>
    private void ResetView()
    {
        _minScale = FitScaleForRotation();
        _scale = _minScale;
        _tx = 0;
        _ty = 0;
        ApplyTransform();
    }

    /// <summary>Zoom about a focal point (in ImageHost coordinates) so it stays put.</summary>
    private void ZoomAt(double factor, Windows.Foundation.Point focus)
    {
        var newScale = Math.Clamp(_scale * factor, _minScale, MaxScale);
        var ratio = newScale / _scale;
        // Pivot is the host centre (RenderTransformOrigin = 0.5,0.5), so anchor relative to it.
        var ux = focus.X - ImageHost.ActualWidth / 2;
        var uy = focus.Y - ImageHost.ActualHeight / 2;
        _tx = ux - ratio * (ux - _tx);
        _ty = uy - ratio * (uy - _ty);
        _scale = newScale;
        if (_scale <= _minScale + 0.001) { _tx = 0; _ty = 0; } // snap back to centered fit
        ApplyTransform();
    }

    private Windows.Foundation.Point HostCenter() =>
        new(ImageHost.ActualWidth / 2, ImageHost.ActualHeight / 2);

    private bool IsAtFit => _scale <= _minScale + 0.001;

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAt(1.25, HostCenter());
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAt(0.8, HostCenter());
    private void Fit_Click(object sender, RoutedEventArgs e) => ResetView();

    private void ImageHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!IsAtFit) ResetView();
        else ZoomAt(2.5, e.GetPosition(ImageHost));
    }

    /// <summary>Mouse wheel zooms in/out toward the cursor (no modifier needed).</summary>
    private void ImageHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageHost);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        ZoomAt(delta > 0 ? 1.15 : 1.0 / 1.15, point.Position);
        e.Handled = true;
    }

    // --- Drag to pan when zoomed in ---

    private void ImageHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsAtFit) return; // nothing to pan at fit
        var point = e.GetCurrentPoint(ImageHost);
        if (!point.Properties.IsLeftButtonPressed) return;

        _panning = true;
        _panStart = point.Position;
        _panStartTx = _tx;
        _panStartTy = _ty;
        ImageHost.CapturePointer(e.Pointer);
    }

    private void ImageHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetCurrentPoint(ImageHost).Position;
        _tx = _panStartTx + (p.X - _panStart.X);
        _ty = _panStartTy + (p.Y - _panStart.Y);
        ApplyTransform();
    }

    private void ImageHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ImageHost.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>Keeps the image clipped to the host so a zoomed photo can't overlap the chrome.</summary>
    private void ImageHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ImageHost.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, ImageHost.ActualWidth, ImageHost.ActualHeight)
        };
        if (IsAtFit) ResetView(); // keep the photo fitted as the window resizes
    }

    private void ViewerImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        App.Log("ImageFailed", new Exception(e.ErrorMessage));
        var name = Current?.FileName ?? "image";
        StatusText.Text = $"Could not display {name}: {e.ErrorMessage}";
    }

    /// <summary>Once decoded we know the true pixel size; re-fit if the user hasn't zoomed.</summary>
    private void ViewerImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (ViewerImage.Source is BitmapImage b && b.PixelWidth > 0)
        {
            _bmpW = b.PixelWidth;
            _bmpH = b.PixelHeight;
            if (IsAtFit) ResetView();
        }
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        _rotation = (_rotation + 90) % 360;
        ResetView(); // re-fit (and re-centre) so the rotated image stays fully in view
    }

    // ===================== Favorites =====================

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } item) FavoriteItem(item);
    }

    private void UpdateFavoriteIcon()
    {
        var fav = Current?.IsFavorite == true;
        FavoriteIcon.Glyph = fav ? "" : ""; // filled / outline star // filled / outline star
        FavoriteIcon.Foreground = fav
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gold)
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        ToolTipService.SetToolTip(FavoriteButton, fav ? "Unfavorite" : "Favorite");
    }

    // ===================== Eye toggle (headline feature) =====================

    /// <summary>Default eye click = toggle the in-view privacy curtain for the current photo.</summary>
    private void Eye_Click(object sender, RoutedEventArgs e) => ToggleObscure();

    private void EyeObscure_Click(object sender, RoutedEventArgs e) => ToggleObscure();

    private void ToggleObscure()
    {
        var item = Current;
        if (item is null) return;

        if (_obscured.Contains(item.Path)) _obscured.Remove(item.Path);
        else _obscured.Add(item.Path);

        UpdateEyeState();
    }

    private void UpdateEyeState()
    {
        var obscured = Current is not null && _obscured.Contains(Current.Path);
        ObscureOverlay.Visibility = obscured ? Visibility.Visible : Visibility.Collapsed;
        // Eye glyph reflects state: open eye = visible, eye-off = hidden.
        EyeIcon.Glyph = obscured ? GlyphEyeOff : GlyphEyeOpen;
        ToolTipService.SetToolTip(EyeButton, obscured ? "Reveal (H)" : "Hide (H)");

        // The curtain must cover METADATA too: the filename in the title bar and the info panel
        // (name/folder/EXIF) render above the black overlay and would identify the hidden photo.
        if (obscured)
        {
            ModeLabel.Text = "";
            if (InfoPanel.Visibility == Visibility.Visible)
            {
                _infoHiddenByObscure = true;
                InfoPanel.Visibility = Visibility.Collapsed;
            }
        }
        else if (Current is { } cur)
        {
            ModeLabel.Text = $"{cur.FileName}   ({_currentIndex + 1}/{_view.Count})";
            if (_infoHiddenByObscure)
            {
                _infoHiddenByObscure = false;
                InfoPanel.Visibility = Visibility.Visible;
                _ = PopulateInfoAsync();
            }
        }
    }

    private bool _infoHiddenByObscure; // info panel was open when the curtain dropped — restore on reveal

    /// <summary>Permanently flag the current photo as hidden (Hidden album); never deletes the file.</summary>
    private void EyeHidePermanent_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } item) HideItemPermanently(item);
    }

    // ===================== Info / Reveal / Delete =====================

    private async void Info_Click(object sender, RoutedEventArgs e)
    {
        InfoPanel.Visibility = InfoPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        if (InfoPanel.Visibility == Visibility.Visible) await PopulateInfoAsync();
    }

    private async Task PopulateInfoAsync()
    {
        var item = Current;
        if (item is null) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            var basic = await file.GetBasicPropertiesAsync();
            var img = await file.Properties.GetImagePropertiesAsync();

            var sizeMb = basic.Size / 1024d / 1024d;
            var lines = new List<string>
            {
                $"Name: {item.FileName}",
                $"Folder: {System.IO.Path.GetDirectoryName(item.Path)}",
                $"Dimensions: {img.Width} × {img.Height}",
                $"Size: {sizeMb:0.00} MB",
                $"Modified: {basic.DateModified.LocalDateTime}",
            };
            if (img.DateTaken.Year > 1601) lines.Add($"Taken: {img.DateTaken.LocalDateTime}");
            if (!string.IsNullOrWhiteSpace(img.CameraManufacturer) || !string.IsNullOrWhiteSpace(img.CameraModel))
                lines.Add($"Camera: {img.CameraManufacturer} {img.CameraModel}".Trim());
            lines.Add($"Favorite: {(item.IsFavorite ? "" : "")}");
            lines.Add($"Hidden: {(item.IsHidden ? "yes" : "no")}");

            InfoText.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            InfoText.Text = $"Metadata unavailable: {ex.Message}";
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var item = Current ?? (_view.Count > 0 ? _view[0] : null);
        if (item is not null) RevealItem(item);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } item) await DeleteItemAsync(item);
    }

    // ===================== Full screen =====================

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        _appWindow.SetPresenter(_isFullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);
    }

    // ===================== Slideshow =====================

    private void Slideshow_Click(object sender, RoutedEventArgs e) => StartSlideshow();

    private void StartSlideshow()
    {
        // Slideshow always skips hidden photos, regardless of current filter.
        var photos = _view.Where(p => !p.IsHidden).ToList();
        if (photos.Count == 0)
        {
            StatusText.Text = "Nothing to show — no visible photos.";
            return;
        }
        var start = Current is not null ? Math.Max(0, photos.IndexOf(Current)) : 0;
        var slideshow = new SlideshowWindow(photos, start, _state);
        slideshow.Activate();
    }

    // ===================== Collage =====================

    private bool InCollage => CollageView.Visibility == Visibility.Visible;

    private async void Collage_Click(object sender, RoutedEventArgs e)
    {
        // All visible (non-hidden) photos in the current pipeline.
        var pool = _view.Where(p => !p.IsHidden).ToList();

        if (pool.Count == 0)
        {
            StatusText.Text = "No photos to make a collage.";
            return;
        }
        _collageSource = pool;
        _collageCount = Math.Min(pool.Count, 12); // sample a screen-friendly number

        ExplorerView.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
        CollageView.Visibility = Visibility.Visible;
        UpdateChromeForDarkSurface();
        ModeLabel.Text = "Collage";

        // Reflect the default layout (from settings) in the in-collage picker.
        PresetJustified.IsChecked = _collagePreset == CollagePreset.Justified;
        PresetGrid.IsChecked = _collagePreset == CollagePreset.Grid;
        PresetHero.IsChecked = _collagePreset == CollagePreset.Hero;

        await RebuildCollageAsync(reshuffle: true);
    }

    private async System.Threading.Tasks.Task RebuildCollageAsync(bool reshuffle)
    {
        if (_collageSource.Count == 0) return;
        _collageCount = Math.Clamp(_collageCount, 1, _collageSource.Count);

        if (reshuffle || _collageItems.Count != _collageCount)
            _collageItems = _collageSource.OrderBy(_ => _rng.Next()).Take(_collageCount).ToList();

        CollageCountText.Text = $"{_collageItems.Count} photo{(_collageItems.Count == 1 ? "" : "s")}";

        await System.Threading.Tasks.Task.WhenAll(_collageItems.Select(i => i.EnsureAspectAsync()));
        LayoutCollage();
    }

    private void LayoutCollage()
    {
        CollageCanvas.Children.Clear();
        if (_collageItems.Count == 0) return;

        var tiles = CollageLayout.Compute(_collageItems, CollageCanvas.ActualWidth, CollageCanvas.ActualHeight, 6, _collagePreset);
        foreach (var tile in tiles)
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            var border = new Border
            {
                Width = tile.Width,
                Height = tile.Height,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
                Child = image
            };
            var item = tile.Item;
            border.Tapped += (_, _) => OpenFromCollage(item);
            Canvas.SetLeft(border, tile.X);
            Canvas.SetTop(border, tile.Y);
            CollageCanvas.Children.Add(border);
            _ = LoadTileAsync(image, item, (int)Math.Ceiling(tile.Width));
        }
    }

    private static async System.Threading.Tasks.Task LoadTileAsync(Image image, PhotoItem item, int decodeWidth)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var stream = await file.OpenReadAsync();
            var bmp = new BitmapImage();
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            await bmp.SetSourceAsync(stream);
            image.Source = bmp;
        }
        catch
        {
            // Skip unreadable tiles.
        }
    }

    private void OpenFromCollage(PhotoItem item)
    {
        var idx = _view.IndexOf(item);
        if (idx < 0) return;
        _currentIndex = idx;
        ShowViewer();
        _ = LoadCurrentAsync();
    }

    private void CollageCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (InCollage && _collageItems.Count > 0) LayoutCollage();
    }

    private void CollageBack_Click(object sender, RoutedEventArgs e) => ShowExplorer();
    private async void CollageShuffle_Click(object sender, RoutedEventArgs e) => await RebuildCollageAsync(reshuffle: true);

    private void CollagePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item && Enum.TryParse<CollagePreset>(item.Tag as string, out var preset))
        {
            _collagePreset = preset;
            LayoutCollage();
        }
    }

    /// <summary>Adds dropped image files to the open collage and re-lays it out.</summary>
    private async System.Threading.Tasks.Task AddToCollageAsync(List<string> paths)
    {
        var existing = new HashSet<string>(_collageSource.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
        var added = paths
            .Where(p => !existing.Contains(p))
            .Select(p => new PhotoItem(p)
            {
                IsFavorite = _state.FavoritePaths.Contains(p),
                IsHidden = _state.HiddenPaths.Contains(p)
            })
            .ToList();
        if (added.Count == 0) return;

        _collageSource.AddRange(added);
        _collageItems.AddRange(added);
        _collageCount = _collageItems.Count;
        CollageCountText.Text = $"{_collageItems.Count} photo{(_collageItems.Count == 1 ? "" : "s")}";

        await System.Threading.Tasks.Task.WhenAll(added.Select(i => i.EnsureAspectAsync()));
        LayoutCollage();
        StatusText.Text = $"Added {added.Count} photo(s) to the collage";
    }

    private async void CollageFewer_Click(object sender, RoutedEventArgs e)
    {
        _collageCount = Math.Max(1, _collageCount - 1);
        await RebuildCollageAsync(reshuffle: true);
    }

    private async void CollageMore_Click(object sender, RoutedEventArgs e)
    {
        _collageCount = Math.Min(_collageSource.Count, _collageCount + 1);
        await RebuildCollageAsync(reshuffle: true);
    }

    private async void CollageSave_Click(object sender, RoutedEventArgs e)
    {
        if (_collageItems.Count == 0) return;
        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(CollageCanvas);
            var pixels = await rtb.GetPixelsAsync();

            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            picker.SuggestedFileName = "collage";
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
            StatusText.Text = $"Collage saved to {file.Path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    // ===================== File Explorer =====================

    private void PopulateSidebar()
    {
        // Quick Access is local profile folders — safe on the UI thread. Drive enumeration is NOT:
        // DriveInfo.IsReady on a sleeping/disconnected network drive blocks for the SMB timeout
        // (measured: 21s UI freeze at startup), so all DriveInfo touches happen off-thread and the
        // sidebar/This PC fill in when the data lands.
        var quick = _fs.GetQuickAccess();
        QuickAccessList.ItemsSource = quick;
        foreach (var i in quick) _ = i.LoadIconAsync(32);
        ExplorerIconsView.Loaded += (_, _) => ApplyIconSize();

        _ = RefreshDrivesAsync();

        // Ctrl + mouse wheel resizes the thumbnails (handledEventsToo so it fires even though
        // the list scrolls the wheel internally).
        ExplorerIconsView.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Explorer_PointerWheelChanged), true);
        ExplorerDetailsList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Explorer_PointerWheelChanged), true);

        // Spacebar Peek: handledEventsToo so we still see Space/arrows after the list consumes them
        // for selection — lets Space open the preview and arrows drive it while it's open.
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Peek_KeyDown), true);

        // Any pointer/key activity resets the vault idle auto-lock countdown.
        RootGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, _) => ResetVaultIdle()), true);

        // Rubber-band selection on the icon view (handledEventsToo so it fires even though the
        // GridView handles pointer events for its own scrolling/selection).
        ExplorerIconsView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ExplorerIcons_PointerPressed), true);
        ExplorerIconsView.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ExplorerIcons_PointerMoved), true);
        ExplorerIconsView.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ExplorerIcons_PointerReleased), true);
        ExplorerIconsView.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ExplorerIcons_PointerCaptureLost), true);

        // Middle-click an image to open it in a new window (both views).
        ExplorerIconsView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Explorer_MiddleClick), true);
        ExplorerDetailsList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Explorer_MiddleClick), true);
    }

    /// <summary>Last known drive list. This PC paints from this instantly (never blocking on DriveInfo)
    /// while <see cref="RefreshDrivesAsync"/> fetches fresh data in the background.</summary>
    private List<ExplorerItem> _driveCache = new();

    /// <summary>Enumerates drives off the UI thread (IsReady/size on a network drive can block for the
    /// SMB timeout), then updates the sidebar, the drive cache, and — if we're on This PC — the listing.</summary>
    // ===================== Sidebar folder tree =====================

    /// <summary>Payload for a folder-tree node; ToString feeds the TreeView's default template.</summary>
    private sealed class FolderTreeNode
    {
        public string Name = "", Path = "";
        public override string ToString() => Name;
    }

    /// <summary>(Re)builds the tree's drive roots. Skipped when the drive set is unchanged so the
    /// user's expansion state survives the periodic drive refresh.</summary>
    private void PopulateFolderTree(List<ExplorerItem> drives)
    {
        var wanted = drives.Select(d => d.Path).ToList();
        var current = FolderTree.RootNodes.Select(n => ((FolderTreeNode)n.Content).Path).ToList();
        if (wanted.SequenceEqual(current, StringComparer.OrdinalIgnoreCase)) return;
        FolderTree.RootNodes.Clear();
        foreach (var d in drives)
            FolderTree.RootNodes.Add(new TreeViewNode
            {
                Content = new FolderTreeNode { Name = d.Name, Path = d.Path },
                HasUnrealizedChildren = true,
            });
    }

    private async void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        var node = args.Node;
        if (!node.HasUnrealizedChildren || node.Content is not FolderTreeNode f) return;
        node.HasUnrealizedChildren = false;
        List<ExplorerItem> subs;
        try
        {
            // Same visibility rules as the main listing (Windows-hidden + app-hidden folders).
            subs = await Task.Run(() => _fs.List(f.Path, showWindowsHidden: _showWindowsHidden, _showAppHidden)
                .Where(i => i.Kind == ExplorerItemKind.Folder).ToList());
        }
        catch { return; } // access denied / device gone — leave the node empty
        foreach (var s in subs)
            node.Children.Add(new TreeViewNode
            {
                Content = new FolderTreeNode { Name = s.Name, Path = s.Path },
                // Chevron up front for every folder: probing each for subfolders would cost an extra
                // enumeration per node (painful on network shares). An empty expand just yields nothing.
                HasUnrealizedChildren = true,
            });
    }

    private void FolderTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        // Drop the subtree so the next expand re-reads the disk (fresh contents, and memory back).
        args.Node.Children.Clear();
        args.Node.HasUnrealizedChildren = true;
    }

    private void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        var content = (args.InvokedItem as TreeViewNode)?.Content ?? args.InvokedItem;
        if (content is FolderTreeNode f) NavigateTo(f.Path);
    }

    private async Task RefreshDrivesAsync()
    {
        List<ExplorerItem> drives;
        HashSet<string> sig;
        try
        {
            drives = await Task.Run(() => _fs.GetDrives());
            sig = await Task.Run(CurrentDriveSignature);
        }
        catch { return; }

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _driveCache = drives;
                _knownDrives = sig;
                DrivesList.ItemsSource = drives;
                PopulateFolderTree(drives);
                foreach (var i in drives) _ = i.LoadIconAsync(32);
                if (_currentFolder is null)
                {
                    // Keep any async-appended devices that are already in the listing. Clone the drives —
                    // these instances go to the 32px sidebar, and the grid needs its own icons at grid size.
                    var devices = _explorerRaw.Where(x => x.ShellId is not null).ToList();
                    _explorerRaw = _fs.GetQuickAccess().Concat(drives.Select(d => d.Clone())).Concat(devices).ToList();
                    ApplySortAndGroup();
                    ApplyViewMode();
                }
            }
            catch (Exception ex) { App.Log("RefreshDrives", ex); }
        });
    }

    /// <summary>A cheap fingerprint of the current drives (letter + ready state) to detect changes.</summary>
    private static HashSet<string> CurrentDriveSignature()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in System.IO.DriveInfo.GetDrives())
            {
                bool ready;
                try { ready = d.IsReady; } catch { ready = false; }
                set.Add(d.Name + (ready ? "+" : "-"));
            }
        }
        catch { /* enumeration can momentarily fail while a device settles */ }
        return set;
    }

    /// <summary>Polls for drive arrival/removal and refreshes the sidebar + This PC view on change.</summary>
    private async void DriveWatcher_Tick(object? sender, object e)
    {
        HashSet<string> sig;
        List<ExplorerItem> drives;
        try
        {
            sig = await Task.Run(CurrentDriveSignature); // IsReady can block, so poll off the UI thread
            if (sig.SetEquals(_knownDrives)) return;     // nothing changed
            drives = await Task.Run(() => _fs.GetDrives());
        }
        catch { return; }

        // Marshal the UI updates explicitly onto the UI thread. Relying on the await-captured
        // context is unsafe here (a cross-thread ItemsSource assignment hard-crashes the XAML core).
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _knownDrives = sig;
                _driveCache = drives;   // keep the This PC fast-paint cache current
                DrivesList.ItemsSource = drives;
                foreach (var i in drives) _ = i.LoadIconAsync(32);

                // If This PC is on screen (not searching), refresh it so the drive shows there too.
                if (_currentFolder is null && ExplorerView.Visibility == Visibility.Visible && string.IsNullOrEmpty(_searchQuery))
                    LoadCurrentFolder();

                StatusText.Text = "Drives updated";
            }
            catch (Exception ex) { App.Log("DriveWatcher", ex); }
        });
    }

    private void Explorer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
        var delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
        if (delta == 0) return;

        if (_explorerViewMode == "Details") { _explorerViewMode = "Large"; ApplyViewMode(); }
        _iconSize = Math.Clamp(_iconSize + (delta > 0 ? 16 : -16), 48, 240);
        IconSizeSlider.Value = _iconSize;
        ApplyIconSize();
        e.Handled = true;
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) { ShowExplorer(); NavigateTo(null); }

    private void Sidebar_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ExplorerItem item) { ShowExplorer(); NavigateTo(item.Path); }
    }

    // ---- Pinned sidebar locations (local folders, UNC shares, WSL paths) ----

    private void PopulatePinned()
    {
        var items = _state.PinnedPaths
            .Select(p => new ExplorerItem(p, ExplorerItemKind.Folder, 0, default, "Folder", FriendlyPinName(p)))
            .ToList();
        PinnedList.ItemsSource = items;
        foreach (var i in items) _ = i.LoadIconAsync(32);
    }

    /// <summary>Connected portable devices (phones/cameras) as navigable shell-location items.</summary>
    private static List<ExplorerItem> MapDevices(List<(string Name, string ParsingName)> devices) =>
        devices.Select(d => new ExplorerItem(ShellLoc.Wrap(d.ParsingName), ExplorerItemKind.Folder, 0, default,
                                             "Portable device", displayName: d.Name, shellId: d.ParsingName)).ToList();

    /// <summary>Enumerates portable devices off the UI thread (STA worker for COM) and refreshes the
    /// sidebar's Devices section + the This PC list when it returns.</summary>
    private async Task LoadDevicesAsync()
    {
        List<(string Name, string ParsingName)> devices;
        try { devices = await StaTask.RunAsync(() => _shell.GetPortableDevices()); }
        catch (Exception ex) { App.Log("Devices", ex); devices = new(); }

        var items = MapDevices(devices);
        DevicesSection.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DevicesList.ItemsSource = items;
        foreach (var i in items) _ = i.LoadIconAsync(32);

        // If still on This PC, append the devices into the listing (drives/folders were shown immediately).
        if (_currentFolder is null && items.Count > 0)
        {
            _explorerRaw = _explorerRaw.Concat(MapDevices(devices)).ToList();
            ApplySortAndGroup();
            ApplyViewMode();
        }
    }

    /// <summary>Refreshes the sidebar's Devices section (async; hidden when nothing is connected).</summary>
    private void PopulateDevices() => _ = LoadDevicesAsync();

    private async Task LoadShellFolderAsync(string shellFolder)
    {
        List<ExplorerItem> items;
        try { items = await StaTask.RunAsync(() => _shell.List(ShellLoc.Unwrap(shellFolder))); }
        catch (Exception ex) { App.Log("ShellList", ex); items = new(); }
        if (_currentFolder != shellFolder) return; // user navigated away while loading

        _explorerRaw = items;
        ApplySortAndGroup();
        ApplyViewMode();
        UpdateHideFolderButton();
        StatusText.Text = $"{_explorerRaw.Count} item(s)";
    }

    private static string FriendlyPinName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private async void AddLocation_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = @"e.g. \\server\share  or  \\wsl.localhost\Ubuntu\home" };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var note = new TextBlock
        {
            Text = "Pin a local folder, a network share, or a WSL path. Paste a full path.",
            Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(note);
        panel.Children.Add(box);

        var dlg = new ContentDialog
        {
            Title = "Add location",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var path = box.Text.Trim().Trim('"');
        if (!string.IsNullOrEmpty(path)) AddPinnedPath(path);
    }

    private void AddPinnedPath(string path)
    {
        path = path.TrimEnd();
        if (_state.PinnedPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "That location is already pinned.";
        }
        else
        {
            _state.PinnedPaths.Add(path);
            _state.Save();
            PopulatePinned();
            StatusText.Text = $"Pinned {FriendlyPinName(path)}";
        }
        // Jump to it if it's reachable right now (network/WSL may be offline — still pinned).
        if (Directory.Exists(path)) { ShowExplorer(); NavigateTo(path); }
    }

    private void PinnedList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ExplorerItem item) return;
        var menu = new MenuFlyout();
        var remove = new MenuFlyoutItem { Text = "Remove from sidebar", Icon = new SymbolIcon(Symbol.UnPin) };
        remove.Click += (_, _) => RemovePinnedPath(item.Path);
        menu.Items.Add(remove);
        var target = (FrameworkElement)sender;
        menu.ShowAt(target, new FlyoutShowOptions { Position = e.GetPosition(target) });
        e.Handled = true;
    }

    private void RemovePinnedPath(string path)
    {
        _state.PinnedPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _state.Save();
        PopulatePinned();
        StatusText.Text = "Removed from sidebar.";
    }

    private void NavigateTo(string? path, bool addHistory = true)
    {
        ClearSearch();
        _currentFolder = path;
        if (addHistory)
        {
            if (_navIndex < _navHistory.Count - 1)
                _navHistory.RemoveRange(_navIndex + 1, _navHistory.Count - _navIndex - 1);
            _navHistory.Add(path);
            _navIndex = _navHistory.Count - 1;
        }
        LoadCurrentFolder();
        UpdateNavButtons();
        BuildBreadcrumb();
        SyncActiveTab();
    }

    private void LoadCurrentFolder()
    {
        HiddenFolderPlaceholder.Visibility = Visibility.Collapsed;
        ExplorerEmpty.Visibility = Visibility.Collapsed;
        UpdateFolderWatch();
        LoadSortPrefsForCurrentFolder(); // apply this folder's remembered sort/group

        // Galileo's own recycle bin — list the moved-in items (real files, so previews work).
        if (_currentFolder == RecycleBin.Location)
        {
            _explorerRaw = _bin.ListItems();
            ApplySortAndGroup();
            ApplyViewMode();
            UpdateHideFolderButton();
            StatusText.Text = $"Recycle Bin — {_explorerRaw.Count} item(s)";
            return;
        }

        // Shell-namespace location (MTP / portable device) — enumerate via the shell, no filesystem.
        if (ShellLoc.IsShell(_currentFolder))
        {
            // MTP/portable-device enumeration can stall over USB — do it off the UI thread (on an STA
            // worker for COM) so the window stays responsive; fill the list in when it returns.
            _explorerRaw = new List<ExplorerItem>();
            ApplySortAndGroup();
            ApplyViewMode();
            UpdateHideFolderButton();
            StatusText.Text = "Loading device…";
            _ = LoadShellFolderAsync(_currentFolder!);
            return;
        }

        if (_currentFolder is null)
        {
            // Paint instantly from the drive cache — DriveInfo (IsReady/size) can block for the SMB
            // timeout on a sleeping network drive, so it must never run here on the UI thread. The
            // cache refreshes off-thread and the listing updates in place if anything changed.
            // Clone the cached items: the cache's own instances live in the sidebar with 32px icons,
            // and sharing them here would show those small icons stretched blurry at grid size.
            _explorerRaw = _fs.GetQuickAccess().Concat(_driveCache.Select(d => d.Clone())).ToList();
            ApplySortAndGroup();
            ApplyViewMode();
            UpdateHideFolderButton();
            StatusText.Text = "This PC";
            _ = RefreshDrivesAsync();
            _ = LoadDevicesAsync(); // appends devices to the list + refreshes the sidebar when ready
            return;
        }

        // App-hidden folder: present it as an ordinary empty folder — never reveal that it's hidden.
        if (_state.HiddenFolders.Contains(_currentFolder) && !_showAppHidden)
        {
            _explorerRaw = new List<ExplorerItem>();
            ApplySortAndGroup();
            ApplyViewMode();
            UpdateHideFolderButton();
            StatusText.Text = "0 item(s)";
            return;
        }

        _explorerRaw = _fs.List(_currentFolder, showWindowsHidden: _showWindowsHidden, _showAppHidden);
        if (_vaults.Current?.WorkingDir is { } vwd && _currentFolder.StartsWith(vwd, StringComparison.OrdinalIgnoreCase))
            App.LogInfo($"vault folder load: {_explorerRaw.Count} item(s) at {_currentFolder}");
        ApplySortAndGroup();
        ApplyViewMode();
        UpdateHideFolderButton();
        StatusText.Text = $"{_explorerRaw.Count} item(s)";
    }

    // ---- Live folder refresh ----

    /// <summary>Watches the current real folder for outside changes; debounced reload keeps the
    /// listing (and sort order) current as files are downloaded/added/removed.</summary>
    private void UpdateFolderWatch()
    {
        var path = _currentFolder is not null
                   && string.IsNullOrEmpty(_searchQuery)
                   && !(_state.HiddenFolders.Contains(_currentFolder) && !_showAppHidden)
                   && Directory.Exists(_currentFolder)
            ? _currentFolder
            : null;

        // Never arm a watcher on a location already known to be unwatchable (avoids the error storm).
        if (path is not null && _watchUnsupported.Contains(path))
        {
            StopFolderWatch();
            _watchedPath = path;
            return;
        }

        if (string.Equals(path, _watchedPath, StringComparison.OrdinalIgnoreCase) && _folderWatcher is not null)
            return; // already watching it

        StopFolderWatch();
        _watchedPath = path;
        _watchErrorCount = 0;
        if (path is null) return;

        try
        {
            _folderWatcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            void Bump(object? _, FileSystemEventArgs __) => DispatcherQueue.TryEnqueue(RestartWatchDebounce);
            _folderWatcher.Created += Bump;
            _folderWatcher.Deleted += Bump;
            _folderWatcher.Changed += Bump;
            _folderWatcher.Renamed += (_, _) => DispatcherQueue.TryEnqueue(RestartWatchDebounce);
            _folderWatcher.Error += (_, _) => DispatcherQueue.TryEnqueue(() => OnWatchError(path));
            _folderWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // EnableRaisingEvents throws on shares that can't be watched → mark unsupported, don't retry.
            App.Log("FolderWatch", ex);
            StopFolderWatch();
            _watchUnsupported.Add(path);
        }
    }

    /// <summary>Handles a watcher Error. A real buffer overflow on a local folder is recovered by rebuilding
    /// once; a location that keeps erroring (network/WSL/9P share) is given up on so it can't loop and
    /// freeze the UI — F5 still refreshes it manually.</summary>
    private void OnWatchError(string path)
    {
        if (!string.Equals(path, _watchedPath, StringComparison.OrdinalIgnoreCase)) return; // navigated away
        StopFolderWatch();
        if (++_watchErrorCount > 2)
        {
            _watchUnsupported.Add(path);
            App.Log("FolderWatch", new InvalidOperationException($"Live refresh disabled for '{path}' (repeated watcher errors)."));
            return; // keep _watchedPath = path, leave _folderWatcher null → won't re-arm
        }
        _watchedPath = null;
        UpdateFolderWatch();      // one rebuild attempt for a genuine overflow
        RestartWatchDebounce();
    }

    private void RestartWatchDebounce() { _watchDebounce.Stop(); _watchDebounce.Start(); }

    private void StopFolderWatch()
    {
        if (_folderWatcher is null) return;
        try { _folderWatcher.EnableRaisingEvents = false; _folderWatcher.Dispose(); } catch { }
        _folderWatcher = null;
    }

    /// <summary>Reloads the current folder while preserving the current selection (by path).</summary>
    private void ReloadKeepingSelection()
    {
        var selected = ActiveExplorerList().SelectedItems.OfType<ExplorerItem>()
            .Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        LoadCurrentFolder();
        if (selected.Count == 0) return;
        var list = ActiveExplorerList();
        foreach (var it in _explorerItems)
            if (selected.Contains(it.Path)) list.SelectedItems.Add(it);
    }

    /// <summary>Applies on-disk changes to the live list in place — inserting new items at their
    /// sorted position and removing deleted ones — so scroll position and selection are kept.
    /// Falls back to a full reload for grouped/search views.</summary>
    /// <summary>The current folder vanished from disk — climb to the nearest surviving ancestor
    /// (This PC as the last resort) instead of showing a dead listing forever. The watcher tears
    /// itself down when its folder disappears, so nothing would ever refresh the stale view.</summary>
    private void NavigateToNearestExisting()
    {
        var p = _currentFolder;
        while (!string.IsNullOrEmpty(p) && !Directory.Exists(p)) p = System.IO.Path.GetDirectoryName(p);
        NavigateTo(string.IsNullOrEmpty(p) ? null : p);
        StatusText.Text = "That folder no longer exists.";
    }

    private void RefreshFolderIncremental()
    {
        if (_currentFolder is null) return;
        if (!Directory.Exists(_currentFolder)) { NavigateToNearestExisting(); return; }

        // Grouped or search views: patching grouped sources in place is fiddly — reload (keeps
        // selection). But first compare against what's already shown: when the watcher is just
        // echoing a change the UI applied in place (e.g. a rename), the listing matches and the
        // reload — with its thumbnail flicker and scroll reset — is skipped entirely.
        if (_groupBy != "None" || !string.IsNullOrEmpty(_searchQuery))
        {
            var fresh = SortItems(_fs.List(_currentFolder, showWindowsHidden: _showWindowsHidden, _showAppHidden));
            var same = SameSequence(fresh, _explorerItems);
            // Same flat order isn't enough when grouped: fresh metadata can move an item to another
            // group without reordering (e.g. an extension change under Name sort, or a new Modified
            // date under Date grouping) — compare group keys too before declaring it a no-op.
            if (same && _groupBy != "None")
                for (var i = 0; i < fresh.Count && same; i++)
                    same = string.Equals(GroupKeyRank(fresh[i], _groupBy).Key,
                                         GroupKeyRank(_explorerItems[i], _groupBy).Key,
                                         StringComparison.OrdinalIgnoreCase);
            if (!same) RefreshFolderInPlace();
            return;
        }

        // Adopt already-shown objects for surviving paths (same trick as RefreshFolderInPlace): taking
        // the fresh listing verbatim desyncs _explorerRaw from _explorerItems — a later in-place rename
        // would then reconcile against stale objects and visibly revert.
        var listed = _fs.List(_currentFolder, showWindowsHidden: _showWindowsHidden, _showAppHidden);
        var current = new Dictionary<string, ExplorerItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in _explorerItems) current[it.Path] = it;
        _explorerRaw = listed.Select(f => current.TryGetValue(f.Path, out var old) ? old : f).ToList();
        var target = SortItems(_explorerRaw);
        ReconcileExplorerItems(target);

        if (ActiveExplorerList().SelectedItems.Count == 0)
            StatusText.Text = $"{_explorerRaw.Count} item(s)";
        UpdateExplorerEmptyState();
    }

    /// <summary>True when both lists hold the same paths in the same order.</summary>
    private static bool SameSequence(List<ExplorerItem> a, IList<ExplorerItem> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].Path, b[i].Path, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Mutates <see cref="_explorerItems"/> to match <paramref name="target"/> (ordered by the
    /// current sort) using minimal insert/move/remove ops, keeping existing item objects so their loaded
    /// icons and selection survive.</summary>
    private void ReconcileExplorerItems(List<ExplorerItem> target)
    {
        var coll = _explorerItems;
        var targetPaths = new HashSet<string>(target.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);

        // Remove items that are gone.
        for (var i = coll.Count - 1; i >= 0; i--)
            if (!targetPaths.Contains(coll[i].Path)) coll.RemoveAt(i);

        // Index the survivors by path.
        var existing = new Dictionary<string, ExplorerItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in coll) existing[it.Path] = it;

        // Walk the target order, fixing position i each step.
        for (var i = 0; i < target.Count; i++)
        {
            var path = target[i].Path;
            if (i < coll.Count && string.Equals(coll[i].Path, path, StringComparison.OrdinalIgnoreCase))
                continue;

            if (existing.TryGetValue(path, out var item))
            {
                var from = coll.IndexOf(item);
                if (from != i) coll.Move(from, i);
            }
            else
            {
                coll.Insert(i, target[i]); // new item — its icon loads lazily when realized
                existing[path] = target[i];
            }
        }
    }

    // ---- Sort & group ----

    private void ApplySortAndGroup()
    {
        var basis = SearchBasis();
        var sorted = SortItems(basis);

        // Keep the flat collection current (used by image/collage/slideshow code).
        _explorerItems.Clear();
        foreach (var it in sorted) _explorerItems.Add(it);

        // This PC: always group Drives (incl. devices) first, then Folders — regardless of sort/group.
        if (_currentFolder is null && string.IsNullOrEmpty(_searchQuery))
        {
            var pcGroups = BuildThisPcGroups(sorted);
            ExplorerIconsView.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = pcGroups }.View;
            ExplorerDetailsList.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = pcGroups }.View;
            UpdateSortHeaders();
            UpdateExplorerEmptyState();
            return;
        }

        if (_groupBy == "None")
        {
            ExplorerIconsView.ItemsSource = _explorerItems;
            ExplorerDetailsList.ItemsSource = _explorerItems;
        }
        else
        {
            var groups = BuildGroups(sorted);
            ExplorerIconsView.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
            ExplorerDetailsList.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
        }

        UpdateSortHeaders();
        UpdateExplorerEmptyState();
    }

    /// <summary>The items to display: the current folder, or search results when a query is active.</summary>
    private List<ExplorerItem> SearchBasis()
    {
        if (string.IsNullOrEmpty(_searchQuery)) return _explorerRaw;
        return _searchRecursive
            ? _searchResults
            : _explorerRaw.Where(i => i.Name.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    private void UpdateExplorerEmptyState()
    {
        // The hidden-folder placeholder, when shown, owns the empty area instead.
        if (HiddenFolderPlaceholder.Visibility == Visibility.Visible)
        {
            ExplorerEmpty.Visibility = Visibility.Collapsed;
            return;
        }
        var searching = !string.IsNullOrEmpty(_searchQuery);
        // A no-match search must say so even on This PC (null folder), not show a silent blank pane.
        var empty = _explorerItems.Count == 0 && (_currentFolder is not null || searching);
        ExplorerEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        if (searching)
        {
            ExplorerEmptyTitle.Text = "No matches";
            ExplorerEmptySubtitle.Text = $"Nothing here matches “{_searchQuery}”.";
        }
        else
        {
            ExplorerEmptyTitle.Text = "This folder is empty";
            ExplorerEmptySubtitle.Text = "Drop files here, or use New folder to get started.";
        }
    }

    private List<ExplorerItem> SortItems(List<ExplorerItem> items) =>
        SortItems(items, _sortBy, _sortDescending);

    private static List<ExplorerItem> SortItems(List<ExplorerItem> items, string sortBy, bool sortDescending)
    {
        var dir = sortDescending ? -1 : 1;
        int ByName(ExplorerItem a, ExplorerItem b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        int Primary(ExplorerItem a, ExplorerItem b) => sortBy switch
        {
            "Date" => a.Modified.CompareTo(b.Modified),
            "Type" => string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase) is var c && c != 0 ? c : ByName(a, b),
            "Size" => a.Size.CompareTo(b.Size),
            _ => ByName(a, b)
        };

        var sorted = new List<ExplorerItem>(items);
        sorted.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1; // folders first, always
            var c = Primary(a, b) * dir;
            return c != 0 ? c : ByName(a, b) * dir;
        });
        return sorted;
    }

    /// <summary>Group section keys the user has collapsed this session (remembered across refreshes).</summary>
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Expand/collapse a group section when its header is clicked, remembering the choice.</summary>
    private void GroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExplorerGroup g) return;
        g.Toggle();
        if (g.IsExpanded) _collapsedGroups.Remove(g.Key); else _collapsedGroups.Add(g.Key);
    }

    /// <summary>This PC sections: "Drives" (drives + portable devices) first, then "Folders".</summary>
    private List<ExplorerGroup> BuildThisPcGroups(List<ExplorerItem> sorted)
    {
        ExplorerGroup Make(string key, double rank, IEnumerable<ExplorerItem> items)
        {
            var g = new ExplorerGroup { Key = key, Rank = rank };
            foreach (var i in items) g.AddItem(i);
            g.SetExpanded(!_collapsedGroups.Contains(key));
            g.Finish();
            return g;
        }

        // Drives by drive letter (root path), then portable devices by name.
        var drives = sorted.Where(i => i.Kind == ExplorerItemKind.Drive).OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Concat(sorted.Where(i => i.IsShellItem).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var folders = sorted.Where(i => i.Kind != ExplorerItemKind.Drive && !i.IsShellItem).ToList();

        var groups = new List<ExplorerGroup>();
        if (drives.Count > 0) groups.Add(Make("Drives", 0, drives));
        if (folders.Count > 0) groups.Add(Make("Folders", 1, folders));
        return groups;
    }

    private List<ExplorerGroup> BuildGroups(List<ExplorerItem> sorted)
    {
        var map = new Dictionary<string, ExplorerGroup>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<ExplorerGroup>();
        foreach (var it in sorted)
        {
            var (key, rank) = GroupKeyRank(it, _groupBy);
            if (!map.TryGetValue(key, out var g))
            {
                g = new ExplorerGroup { Key = key, Rank = rank };
                map[key] = g;
                groups.Add(g);
            }
            g.AddItem(it);
        }
        groups.Sort((a, b) => a.Rank.CompareTo(b.Rank) is var c && c != 0 ? c
            : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        foreach (var g in groups)
        {
            g.SetExpanded(!_collapsedGroups.Contains(g.Key)); // restore remembered collapse state
            g.Finish();                                       // populate visible items now that the group is complete
        }
        return groups;
    }

    private static (string Key, double Rank) GroupKeyRank(ExplorerItem it, string groupBy)
    {
        switch (groupBy)
        {
            case "Type":
                return it.IsFolder ? ("File folder", -1) : (it.TypeName, 1);

            case "Size":
                if (it.IsFolder) return ("—", -1);
                var s = it.Size;
                if (s == 0) return ("Empty", 0);
                if (s < 16 * 1024) return ("Tiny (0–16 KB)", 1);
                if (s < 1024 * 1024) return ("Small (16 KB–1 MB)", 2);
                if (s < 128L * 1024 * 1024) return ("Medium (1–128 MB)", 3);
                if (s < 1024L * 1024 * 1024) return ("Large (128 MB–1 GB)", 4);
                return ("Huge (> 1 GB)", 5);

            case "Date":
                var d = it.Modified;
                if (d == default) return ("Unknown", 100);
                var today = DateTime.Now.Date;
                var dd = d.Date;
                if (dd == today) return ("Today", 0);
                if (dd == today.AddDays(-1)) return ("Yesterday", 1);
                if (dd > today.AddDays(-7)) return ("Earlier this week", 2);
                if (dd > today.AddDays(-14)) return ("Last week", 3);
                if (d.Year == today.Year && d.Month == today.Month) return ("Earlier this month", 4);
                if (d.Year == today.Year) return ("Earlier this year", 5);
                return ("A long time ago", 6);

            default: // Name
                var ch = it.Name.Length > 0 ? char.ToUpperInvariant(it.Name[0]) : '#';
                if (ch is >= 'A' and <= 'Z') return (ch.ToString(), ch - 'A');
                if (ch is >= '0' and <= '9') return ("0–9", 26);
                return ("#", 27);
        }
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item) { _sortBy = item.Tag as string ?? "Name"; _state.SortBy = _sortBy; SaveSortPrefsForCurrentFolder(); _state.Save(); ApplySortAndGroup(); }
    }

    private void SortDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item) { _sortDescending = (item.Tag as string) == "Desc"; _state.SortDescending = _sortDescending; SaveSortPrefsForCurrentFolder(); _state.Save(); ApplySortAndGroup(); }
    }

    private void Group_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item) { _groupBy = item.Tag as string ?? "None"; _state.GroupBy = _groupBy; SaveSortPrefsForCurrentFolder(); _state.Save(); ApplySortAndGroup(); }
    }

    /// <summary>Applies the current folder's saved sort/group (if any) into the live state and
    /// refreshes the menu/header UI. New folders inherit the last-used sort.</summary>
    private void LoadSortPrefsForCurrentFolder()
    {
        if (_currentFolder is not null && _state.FolderSorts.TryGetValue(_currentFolder, out var p))
        {
            _sortBy = p.SortBy;
            _sortDescending = p.SortDescending;
            _groupBy = p.GroupBy;
        }
        SyncSortGroupRadios();
        UpdateSortHeaders();
    }

    /// <summary>Remembers the current sort/group for the current folder.</summary>
    private void SaveSortPrefsForCurrentFolder()
    {
        // Don't persist per-folder sort for sentinel/virtual locations (recycle bin, MTP/shell) — they
        // aren't real paths and would accumulate junk entries in state.json.
        if (_currentFolder is null || _currentFolder == RecycleBin.Location || ShellLoc.IsShell(_currentFolder)) return;
        _state.FolderSorts[_currentFolder] = new FolderSortPref
        {
            SortBy = _sortBy,
            SortDescending = _sortDescending,
            GroupBy = _groupBy,
        };
    }

    private void SyncSortGroupRadios()
    {
        SortName.IsChecked = _sortBy == "Name";
        SortDate.IsChecked = _sortBy == "Date";
        SortType.IsChecked = _sortBy == "Type";
        SortSize.IsChecked = _sortBy == "Size";
        SortAsc.IsChecked = !_sortDescending;
        SortDesc.IsChecked = _sortDescending;
        GroupNone.IsChecked = _groupBy == "None";
        GroupName.IsChecked = _groupBy == "Name";
        GroupDate.IsChecked = _groupBy == "Date";
        GroupType.IsChecked = _groupBy == "Type";
        GroupSize.IsChecked = _groupBy == "Size";
    }

    private void ApplyViewMode()
    {
        var details = _explorerViewMode == "Details";
        // Icons and Details are two independent list controls over the same items — carry the
        // selection across so switching views doesn't silently drop it.
        var from = details ? (ListViewBase)ExplorerIconsView : ExplorerDetailsList;
        var to = details ? (ListViewBase)ExplorerDetailsList : ExplorerIconsView;
        if (!ReferenceEquals(from, to) && from.SelectedItems.Count > 0)
        {
            var carry = from.SelectedItems.OfType<ExplorerItem>().ToList();
            to.SelectedItems.Clear();
            foreach (var it in carry) to.SelectedItems.Add(it);
        }
        ExplorerIconsView.Visibility = details ? Visibility.Collapsed : Visibility.Visible;
        ExplorerDetailsView.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
        if (!details) ApplyIconSize();

        // Keep the View flyout's checkmark in sync with the active mode (incl. the restored one on launch).
        ViewLarge.IsChecked = _explorerViewMode == "Large";
        ViewMedium.IsChecked = _explorerViewMode == "Medium";
        ViewSmall.IsChecked = _explorerViewMode == "Small";
        ViewDetails.IsChecked = details;
    }

    private void ApplyIconSize()
    {
        if (ExplorerIconsView.ItemsPanelRoot is ItemsWrapGrid wg)
        {
            wg.ItemWidth = _iconSize;
            wg.ItemHeight = _iconSize + 28;
        }
    }

    private void UpdateNavButtons()
    {
        BackNav.IsEnabled = _navIndex > 0;
        FwdNav.IsEnabled = _navIndex < _navHistory.Count - 1;
        UpNav.IsEnabled = _currentFolder is not null;
    }

    private void BuildBreadcrumb()
    {
        Breadcrumb.Children.Clear();

        // Galileo's own recycle bin: This PC › Recycle Bin.
        if (_currentFolder == RecycleBin.Location)
        {
            AddCrumb("This PC", null);
            Breadcrumb.Children.Add(new TextBlock { Text = "›", Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
            AddCrumb("Recycle Bin", RecycleBin.Location);
            BreadcrumbScroller.UpdateLayout();
            BreadcrumbScroller.ChangeView(BreadcrumbScroller.ScrollableWidth, null, null, true);
            return;
        }

        // Shell-namespace location (MTP device): build the trail from the shell parent chain.
        if (ShellLoc.IsShell(_currentFolder))
        {
            AddCrumb("This PC", null);
            var chain = new List<(string Name, string Loc)>();
            string? pn = ShellLoc.Unwrap(_currentFolder!);
            var guard = 0;
            while (pn is not null && guard++ < 64)
            {
                chain.Insert(0, (_shell.DisplayName(pn), ShellLoc.Wrap(pn)));
                pn = _shell.GetParentParsingName(pn);
            }
            foreach (var (name, loc) in chain)
            {
                Breadcrumb.Children.Add(new TextBlock { Text = "›", Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
                AddCrumb(name, loc);
            }
            BreadcrumbScroller.UpdateLayout();
            BreadcrumbScroller.ChangeView(BreadcrumbScroller.ScrollableWidth, null, null, true);
            return;
        }

        // Inside a vault or an opened zip, root the trail at the friendly name and hide the temp path.
        if (_currentFolder is not null && SpecialRootFor(_currentFolder) is { } sr)
        {
            var work = sr.root;
            AddCrumb(sr.label, work);
            var rel = System.IO.Path.GetRelativePath(work, _currentFolder);
            if (rel != ".")
            {
                var acc = work;
                foreach (var part in rel.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
                {
                    if (string.IsNullOrEmpty(part)) continue;
                    acc = System.IO.Path.Combine(acc, part);
                    Breadcrumb.Children.Add(new TextBlock { Text = "›", Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
                    AddCrumb(part, acc);
                }
            }
            BreadcrumbScroller.UpdateLayout();
            BreadcrumbScroller.ChangeView(BreadcrumbScroller.ScrollableWidth, null, null, true);
            return;
        }

        AddCrumb("This PC", null);
        if (_currentFolder is not null)
        {
            var chain = new List<(string Name, string Path)>();
            var di = new DirectoryInfo(_currentFolder);
            while (di is not null) { chain.Insert(0, (string.IsNullOrEmpty(di.Name) ? di.FullName : di.Name, di.FullName)); di = di.Parent; }
            foreach (var (name, path) in chain)
            {
                Breadcrumb.Children.Add(new TextBlock { Text = "›", Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
                AddCrumb(name, path);
            }
        }

        // Keep the current (right-most) folder visible when the path is long.
        BreadcrumbScroller.UpdateLayout();
        BreadcrumbScroller.ChangeView(BreadcrumbScroller.ScrollableWidth, null, null, true);
    }

    private void PathBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => EditPath_Click(sender, null!);

    private void AddCrumb(string text, string? path)
    {
        var btn = new Button
        {
            Content = text,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 13
        };
        btn.Click += (_, _) => NavigateTo(path);
        Breadcrumb.Children.Add(btn);
    }

    private void NavBack_Click(object sender, RoutedEventArgs e)
    {
        if (_navIndex > 0) { _navIndex--; NavigateTo(_navHistory[_navIndex], addHistory: false); }
    }

    private void NavForward_Click(object sender, RoutedEventArgs e)
    {
        if (_navIndex < _navHistory.Count - 1) { _navIndex++; NavigateTo(_navHistory[_navIndex], addHistory: false); }
    }

    private void NavUp_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is null) return;
        if (_currentFolder == RecycleBin.Location) { NavigateTo(null); return; }
        if (ShellLoc.IsShell(_currentFolder))
        {
            var parent = _shell.GetParentParsingName(ShellLoc.Unwrap(_currentFolder));
            NavigateTo(parent is null ? null : ShellLoc.Wrap(parent));
            return;
        }
        NavigateTo(Directory.GetParent(_currentFolder)?.FullName);
    }

    private void EditPath_Click(object sender, RoutedEventArgs e)
    {
        AddressBox.Text = _currentFolder ?? "";
        BreadcrumbScroller.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Focus(FocusState.Programmatic);
        AddressBox.SelectAll();
    }

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var path = AddressBox.Text.Trim();
            EndEditPath();
            if (Directory.Exists(path)) NavigateTo(path);
            else if (File.Exists(path) && PhotoLibrary.IsSupported(path))
            {
                NavigateTo(Directory.GetParent(path)?.FullName);
                var m = _explorerItems.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
                if (m is not null) OpenImageFromExplorer(m);
            }
            else StatusText.Text = "Path not found.";
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape) { EndEditPath(); e.Handled = true; }
    }

    private void EndEditPath()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        BreadcrumbScroller.Visibility = Visibility.Visible;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadCurrentFolder();

    private async void ExplorerIcons_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ExplorerItem it) return;
        // Recycled out (scrolled off) — drop any queued decode so a fast scroll can't flood the pipeline.
        if (args.InRecycleQueue)
        {
            it.CancelIconLoad();
            // Huge folders: thumbnails scrolled past would otherwise stay in memory for the folder's
            // lifetime (10k photos ≈ hundreds of MB). Re-loading on scroll-back is cheap now that
            // icons come from the on-disk cache, so above this size recycled icons are released.
            if (_explorerItems.Count > 2000) it.ResetIcon();
            return;
        }
        if (it.Icon is null)
            await it.LoadIconAsync((uint)Math.Clamp(_iconSize, 48, 256));
    }

    private void ExplorerItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ExplorerItem item) return;
        if (IsAltDown() && item.IsImage) { OpenInNewWindow(item.Path); return; }
        OpenExplorerItem(item);
    }

    /// <summary>True when a path lives inside the currently-unlocked vault's working folder.</summary>
    private bool IsInCurrentVault(string? path) =>
        _vaults.Current?.WorkingDir is { } wd && !string.IsNullOrEmpty(path)
        && path.StartsWith(wd, StringComparison.OrdinalIgnoreCase);

    /// <summary>Launches a fresh Galileo instance to open the path in its own window (works even in
    /// single-instance mode via the --new-window flag). Vault files open in-process instead — a second
    /// instance would wipe the vault working folder and read decrypted files outside the vault session.</summary>
    private void OpenInNewWindow(string path)
    {
        if (IsInCurrentVault(path)) { _ = OpenLocalFileInViewerAsync(path); return; }
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            ShellOps.AllowForeground();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                ArgumentList = { "--new-window", path },
            });
        }
        catch (Exception ex) { StatusText.Text = "Couldn't open a new window: " + ex.Message; App.Log("NewWindow", ex); }
    }

    /// <summary>Lets the user drag files/folders out to Explorer, terminals, chat apps, etc.</summary>
    private void Explorer_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var picked = e.Items.OfType<ExplorerItem>().Select(i => (i.Path, i.IsFolder)).ToList();
        if (picked.Count == 0) { e.Cancel = true; return; }

        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        // Path text fallback (terminals/editors that accept text).
        e.Data.SetText(string.Join(" ", picked.Select(p => $"\"{p.Path}\"")));

        // Real file drop (CF_HDROP) provided on demand so we can resolve StorageItems async.
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (var (path, isFolder) in picked)
                {
                    try
                    {
                        items.Add(isFolder
                            ? await StorageFolder.GetFolderFromPathAsync(path)
                            : await StorageFile.GetFileFromPathAsync(path));
                    }
                    catch { /* skip unreadable */ }
                }
                request.SetData(items);
            }
            finally { deferral.Complete(); }
        });
    }

    /// <summary>Extracts a .zip to a temp folder and navigates into it (browse like a folder).</summary>
    private async Task OpenArchiveAsync(ExplorerItem item)
    {
        StatusText.Text = $"Opening {item.Name}…";
        string tmp;
        try { tmp = await ArchiveService.ExtractToTempAsync(item.Path); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        _openZips[tmp] = (item.Path, item.Name);
        ShowExplorer();
        NavigateTo(tmp);
        StatusText.Text = item.Name;
    }

    private async Task ExtractArchiveHereAsync(ExplorerItem item)
    {
        var parent = System.IO.Path.GetDirectoryName(item.Path);
        if (parent is null) return;
        var dest = UniquePath(System.IO.Path.Combine(parent, System.IO.Path.GetFileNameWithoutExtension(item.Path)), isDir: true);
        StatusText.Text = $"Extracting {item.Name}…";
        try { await ArchiveService.ExtractToFolderAsync(item.Path, dest); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        LoadCurrentFolder();
        StatusText.Text = $"Extracted to {System.IO.Path.GetFileName(dest)}";
    }

    private async Task ExtractArchiveToAsync(ExplorerItem item)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var dest = UniquePath(System.IO.Path.Combine(folder.Path, System.IO.Path.GetFileNameWithoutExtension(item.Path)), isDir: true);
        StatusText.Text = $"Extracting {item.Name}…";
        try { await ArchiveService.ExtractToFolderAsync(item.Path, dest); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        if (string.Equals(folder.Path, _currentFolder, StringComparison.OrdinalIgnoreCase)) LoadCurrentFolder();
        StatusText.Text = $"Extracted to {dest}";
    }

    /// <summary>Streams a device file to a temp copy, then opens it with the right viewer.</summary>
    private async System.Threading.Tasks.Task OpenDeviceFileAsync(ExplorerItem item)
    {
        StatusText.Text = $"Opening {item.Name}…";
        string temp;
        try { temp = await _shell.CopyToTempAsync(item.ShellId!, item.Name); }
        catch (Exception ex) { StatusText.Text = "Couldn't open from device: " + ex.Message; App.Log("MtpOpen", ex); return; }

        if (PhotoLibrary.IsSupported(temp)) OpenSingleImage(temp);
        else if (PhotoLibrary.IsMedia(temp)) OpenVideoFromExplorer(new ExplorerItem(temp, ExplorerItemKind.File, 0, default, "Media"));
        else
        {
            try { ShellOps.AllowForeground(); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = temp, UseShellExecute = true }); }
            catch (Exception ex) { StatusText.Text = ex.Message; App.Log("MtpOpenDefault", ex); }
        }
    }

    /// <summary>Opens a single local image file in the viewer (one-item pipeline).</summary>
    private void OpenSingleImage(string path)
    {
        _allPhotos.Clear();
        _allPhotos.AddRange(_library.LoadFiles(new[] { path }));
        _showHiddenAlbum = false;
        _favoritesOnly = false;
        RefreshView();
        _currentIndex = 0;
        ShowViewer();
        _ = LoadCurrentAsync();
    }

    // ---- device write operations (IFileOperation, shell progress UI) ----

    private List<ExplorerItem> SelectedDeviceItems(ExplorerItem clicked)
    {
        var sel = SelectedExplorerItems().Where(i => i.IsShellItem).ToList();
        return sel.Any(s => s == clicked) ? sel : new List<ExplorerItem> { clicked };
    }

    private async System.Threading.Tasks.Task DeviceCopyToPcAsync(ExplorerItem clicked)
    {
        var sel = SelectedDeviceItems(clicked);
        var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        try
        {
            StatusText.Text = $"Copying {sel.Count} item(s) to {folder.Name}…";
            _shell.Download(sel.Select(s => s.ShellId!), folder.Path, WinRT.Interop.WindowNative.GetWindowHandle(this));
            StatusText.Text = "Copied to " + folder.Path;
        }
        catch (Exception ex) { StatusText.Text = "Copy failed: " + ex.Message; App.Log("MtpDownload", ex); }
    }

    private async System.Threading.Tasks.Task DeviceUploadAsync()
    {
        if (!ShellLoc.IsShell(_currentFolder)) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;
        try
        {
            StatusText.Text = $"Uploading {files.Count} file(s)…";
            _shell.Upload(files.Select(f => f.Path), ShellLoc.Unwrap(_currentFolder!), WinRT.Interop.WindowNative.GetWindowHandle(this));
            StatusText.Text = "Uploaded.";
            LoadCurrentFolder();
        }
        catch (Exception ex) { StatusText.Text = "Upload failed: " + ex.Message; App.Log("MtpUpload", ex); }
    }

    private async System.Threading.Tasks.Task DeviceNewFolderAsync()
    {
        if (!ShellLoc.IsShell(_currentFolder)) return;
        var box = new TextBox { Text = "New folder" };
        box.Loaded += (_, _) => box.SelectAll();
        var dlg = new ContentDialog { Title = "New folder", Content = box, PrimaryButtonText = "Create", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var name = box.Text.Trim();
        if (name.Length == 0) return;
        try { _shell.NewFolder(ShellLoc.Unwrap(_currentFolder!), name, WinRT.Interop.WindowNative.GetWindowHandle(this)); LoadCurrentFolder(); }
        catch (Exception ex) { StatusText.Text = "Couldn't create folder: " + ex.Message; App.Log("MtpNewFolder", ex); }
    }

    private async System.Threading.Tasks.Task DeviceRenameAsync(ExplorerItem item)
    {
        var box = new TextBox { Text = item.Name };
        box.Loaded += (_, _) =>
        {
            var ext = item.IsFolder ? "" : System.IO.Path.GetExtension(item.Name);
            var baseLen = item.Name.Length - ext.Length;
            if (baseLen > 0) box.Select(0, baseLen); else box.SelectAll();
        };
        var dlg = new ContentDialog { Title = "Rename", Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var name = box.Text.Trim();
        if (name.Length == 0 || name == item.Name) return;
        try { _shell.Rename(item.ShellId!, name, WinRT.Interop.WindowNative.GetWindowHandle(this)); LoadCurrentFolder(); }
        catch (Exception ex) { StatusText.Text = "Rename failed: " + ex.Message; App.Log("MtpRename", ex); }
    }

    private async System.Threading.Tasks.Task DeviceDeleteAsync(ExplorerItem clicked)
    {
        var sel = SelectedDeviceItems(clicked);
        var dlg = new ContentDialog
        {
            Title = sel.Count == 1 ? $"Delete “{sel[0].Name}”?" : $"Delete {sel.Count} items?",
            Content = "This permanently deletes from the device — there is no Recycle Bin.",
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try { _shell.Delete(sel.Select(s => s.ShellId!), WinRT.Interop.WindowNative.GetWindowHandle(this)); LoadCurrentFolder(); StatusText.Text = "Deleted."; }
        catch (Exception ex) { StatusText.Text = "Delete failed: " + ex.Message; App.Log("MtpDelete", ex); }
    }

    private void OpenExplorerItem(ExplorerItem item)
    {
        // One-shot bypass so programmatic opens (startup / shell hand-off) don't recurse into a new window.
        var bypassNewWindow = _bypassAlwaysNewWindow;
        _bypassAlwaysNewWindow = false;

        if (item.IsShellItem)
        {
            if (item.IsFolder) NavigateTo(ShellLoc.Wrap(item.ShellId!));
            else _ = OpenDeviceFileAsync(item); // stream to temp, then open (Phase 2)
            return;
        }

        // "Always open photos & videos in a new window": route real media files to a separate window.
        // Never spawn a second instance for files inside the unlocked vault — a new process would wipe the
        // vault's working folder (crash recovery) and read decrypted files outside the vault session.
        if (!bypassNewWindow && _state.AlwaysOpenMediaInNewWindow && !item.IsFolder
            && (item.IsImage || PhotoLibrary.IsMedia(item.Path))
            && !IsInCurrentVault(item.Path))
        {
            OpenInNewWindow(item.Path);
            return;
        }

        if (item.IsFolder) NavigateTo(item.Path);
        else if (item.IsImage) OpenImageFromExplorer(item);
        else if (PhotoLibrary.IsMedia(item.Path)) OpenVideoFromExplorer(item);
        else if (ArchiveService.IsArchive(item.Path)) _ = OpenArchiveAsync(item);
        else if (string.Equals(item.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            // Never shell-launch our own executable (defense against any relaunch loop).
            StatusText.Text = "That's Galileo itself.";
        }
        else
        {
            try
            {
                ShellOps.AllowForeground(); // let the opened app come to the front, not stay behind us
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = item.Path, UseShellExecute = true });
            }
            catch (Exception ex) { StatusText.Text = ex.Message; App.Log("OpenDefault", ex); }
        }
    }

    private void ExplorerItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_state.SingleClickToOpen) return; // single-click already opened it
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ExplorerItem item)
        {
            if (IsAltDown() && item.IsImage) { OpenInNewWindow(item.Path); return; }
            OpenExplorerItem(item);
        }
    }

    // ---- Embedded video player ----

    private bool _videoMuted;
    private bool _videoRepeat;

    private async void OpenVideoFromExplorer(ExplorerItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            ShowViewer();
            EnterVideoMode();
            var isAudio = PhotoLibrary.IsAudio(item.Path);
            AudioOverlay.Visibility = isAudio ? Visibility.Visible : Visibility.Collapsed;
            AudioTitle.Text = isAudio ? item.Name : "";
            if (isAudio) _ = LoadAlbumArtAsync(file);

            // Track the real file for the FFmpeg editor; offer Edit for real video files only.
            CloseVideoEditor(); // tear down any prior editor/preview (re-enables normal playback)
            _currentVideoPath = item.IsShellItem ? null : item.Path;
            VideoEditBtn.Visibility = (!isAudio && !item.IsShellItem && FfmpegVideo.Available)
                ? Visibility.Visible : Visibility.Collapsed;
            VideoPlayer.Source = MediaSource.CreateFromStorageFile(file);
            var mp = VideoPlayer.MediaPlayer;
            if (mp is not null)
            {
                // Movie category → full multichannel output (no stereo downmix); Windows' spatial
                // engine (Dolby Atmos / DTS:X / Windows Sonic) on the output device renders surround.
                mp.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Movie;
                // Restore the remembered audio state: muted stays muted, otherwise the last volume.
                // Snapshot BEFORE touching the slider — setting Value fires SliderChanged SYNCHRONOUSLY,
                // which derives mute from the volume and would clobber the remembered state under us.
                var remMuted = _state.VideoMuted;
                var remVol = Math.Clamp(_state.VideoVolume, 0, 100);
                if (Math.Abs(VideoVolumeSlider.Value - remVol) > 0.5) VideoVolumeSlider.Value = remVol; // fires SliderChanged
                // "Start videos muted" (opt-in) forces the START muted for video, but must not poison the
                // remembered preference — persisting it would leave audio files muted forever after.
                _videoMuted = remMuted || (!isAudio && _state.StartVideoMuted);
                _state.VideoMuted = remMuted;
                mp.IsMuted = _videoMuted;
                mp.Volume = VideoVolumeSlider.Value / 100.0;
                mp.IsLoopingEnabled = _videoRepeat;
                mp.Play();
            }
            UpdateVideoToggleIcons();
            ModeLabel.Text = item.Name;
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't play video: {ex.Message}"; App.Log("OpenVideo", ex); }
    }

    // Debounces state.json writes while the volume slider is being dragged.
    private readonly DispatcherTimer _volSaveDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };

    /// <summary>Speaker icon: toggle mute/unmute.</summary>
    private void VideoVolume_Click(object sender, RoutedEventArgs e)
    {
        _videoMuted = !_videoMuted;
        if (VideoPlayer.MediaPlayer is not null) VideoPlayer.MediaPlayer.IsMuted = _videoMuted;
        _state.VideoMuted = _videoMuted;   // remembered for the next video
        _state.Save();
        UpdateVideoToggleIcons();
    }

    private void VideoVolume_SliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (VideoPlayer.MediaPlayer is not null) VideoPlayer.MediaPlayer.Volume = e.NewValue / 100.0;
        _videoMuted = e.NewValue <= 0; // dragging to 0 mutes; above 0 unmutes
        if (VideoPlayer.MediaPlayer is not null) VideoPlayer.MediaPlayer.IsMuted = _videoMuted;
        if (InVideo)   // ignore the slider's initial XAML-load tick — don't clobber the remembered state
        {
            _state.VideoVolume = e.NewValue;
            _state.VideoMuted = _videoMuted;
            _volSaveDebounce.Stop();
            _volSaveDebounce.Start();
        }
        UpdateVideoToggleIcons();
    }

    private void VideoRepeat_Click(object sender, RoutedEventArgs e)
    {
        _videoRepeat = !_videoRepeat;
        if (VideoPlayer.MediaPlayer is not null) VideoPlayer.MediaPlayer.IsLoopingEnabled = _videoRepeat;
        UpdateVideoToggleIcons();
    }

    private void UpdateVideoToggleIcons()
    {
        // The volume slider's Value="100" raises ValueChanged during XAML load, before the repeat
        // button is built — guard against the not-yet-created controls.
        if (VideoVolumeIcon is null || VideoRepeatIcon is null) return;
        var vol = VideoVolumeSlider?.Value ?? 100;
        VideoVolumeIcon.Glyph = ((char)((_videoMuted || vol <= 0) ? 0xE74F : vol <= 33 ? 0xE993 : vol <= 66 ? 0xE994 : 0xE995)).ToString();
        VideoRepeatIcon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            _videoRepeat ? Microsoft.UI.Colors.Gold : Microsoft.UI.Colors.White);
    }

    private int _audioArtToken;

    /// <summary>Shows embedded album art for an audio file (falls back to the music glyph).</summary>
    private async Task LoadAlbumArtAsync(StorageFile file)
    {
        var token = ++_audioArtToken;
        AudioArt.Source = null;
        AudioArtHost.Visibility = Visibility.Collapsed;
        AudioGlyph.Visibility = Visibility.Visible;
        if (!_state.ShowAlbumArt) return;

        try
        {
            using var thumb = await file.GetThumbnailAsync(
                Windows.Storage.FileProperties.ThumbnailMode.MusicView, 480,
                Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);
            if (token != _audioArtToken) return;
            if (thumb is null || thumb.Type == Windows.Storage.FileProperties.ThumbnailType.Icon) return; // no embedded art

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(thumb);
            if (token != _audioArtToken) return;

            AudioArt.Source = bmp;
            AudioArtHost.Visibility = Visibility.Visible;
            AudioGlyph.Visibility = Visibility.Collapsed;
        }
        catch { /* keep the music glyph */ }
    }

    private void EnterVideoMode()
    {
        ImageHost.Visibility = Visibility.Collapsed;
        ObscureOverlay.Visibility = Visibility.Collapsed;
        ViewerChrome.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Visible;
        VideoBackBar.Visibility = Visibility.Visible;
        VideoControlsBar.Visibility = Visibility.Visible;
        AudioOverlay.Visibility = Visibility.Collapsed; // set by the caller when the file is audio
    }

    private void EnterImageMode()
    {
        StopVideo();
        // The video editor must not survive into image mode: its panel/filmstrip would overlay the
        // photo and its Export would run FFmpeg against a stale (possibly deleted) _currentVideoPath.
        if (VideoEditorPanel.Visibility == Visibility.Visible || EditTimeline.Visibility == Visibility.Visible)
            CloseVideoEditor();
        _currentVideoPath = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
        VideoBackBar.Visibility = Visibility.Collapsed;
        VideoControlsBar.Visibility = Visibility.Collapsed;
        AudioOverlay.Visibility = Visibility.Collapsed;
        ImageHost.Visibility = Visibility.Visible;
        ViewerChrome.Visibility = Visibility.Visible;
    }

    private void StopVideo()
    {
        try
        {
            VideoPlayer.MediaPlayer?.Pause();
            // CreateFromStorageFile hands us a MediaSource we own; the element won't dispose it,
            // so release it here or we leak one native source per video opened.
            var previous = VideoPlayer.Source as MediaSource;
            VideoPlayer.Source = null;
            previous?.Dispose();
        }
        catch { /* ignore */ }
    }

    private void OpenImageFromExplorer(ExplorerItem item)
    {
        PopulatePhotoPipelineFromCurrent();
        int IndexOfTarget() => _view.ToList().FindIndex(p => string.Equals(p.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        var idx = IndexOfTarget();
        if (idx < 0)
        {
            // The clicked photo is in the Hidden album (filtered out of _view) — switch to the hidden
            // set so it opens AS ITSELF instead of silently showing a different image (index 0).
            var target = _allPhotos.FirstOrDefault(p => string.Equals(p.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            if (target?.IsHidden == true) { _showHiddenAlbum = true; RefreshView(); idx = IndexOfTarget(); }
        }
        if (idx < 0) { StatusText.Text = $"Couldn't open {item.Name}."; return; } // never open the WRONG photo
        _currentIndex = idx;
        ShowViewer();
        _ = LoadCurrentAsync();
    }

    private void PopulatePhotoPipelineFromCurrent()
    {
        _pipelineGen++; // invalidate any in-flight sibling backfill from an earlier direct open
        // Follow the DISPLAYED order — the sequence the user actually sees (and Peek walks). The list
        // control flattens grouped views group-by-group; _explorerItems only holds the flat sort and
        // diverges from the screen whenever grouping is on.
        var displayed = ActiveExplorerList().Items.OfType<ExplorerItem>().ToList();
        if (displayed.Count == 0) displayed = _explorerItems.ToList();
        var paths = displayed.Where(i => i.IsImage).Select(i => i.Path).ToList();
        var byPath = _library.LoadFiles(paths).ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        _allPhotos.Clear();
        foreach (var path in paths)
            if (byPath.TryGetValue(path, out var item)) _allPhotos.Add(item);
        _showHiddenAlbum = false;
        _favoritesOnly = false;
        RefreshView();
    }

    // ---- View controls ----

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item) return;
        _explorerViewMode = item.Tag as string ?? "Large";
        if (_explorerViewMode != "Details")
        {
            _iconSize = _explorerViewMode switch { "Large" => 160, "Medium" => 110, _ => 72 };
            IconSizeSlider.Value = _iconSize;
        }
        _state.ExplorerViewMode = _explorerViewMode;
        _state.Save();
        ApplyViewMode();
    }

    private void IconSize_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_fs is null) return;
        _iconSize = e.NewValue;
        if (_explorerViewMode == "Details") { _explorerViewMode = "Large"; ApplyViewMode(); }
        ApplyIconSize();
        _state.IconSize = _iconSize;
        _state.ExplorerViewMode = _explorerViewMode;
        _state.Save();
    }

    private async void ShowAppHidden_Click(object sender, RoutedEventArgs e)
    {
        if (ShowHiddenToggle.IsChecked == true && !await EnsureHiddenUnlockedAsync())
        {
            ShowHiddenToggle.IsChecked = false;
            return;
        }
        _showAppHidden = ShowHiddenToggle.IsChecked == true;
        LoadCurrentFolder();
    }

    /// <summary>Privacy (opt-in): when Galileo goes to the background, collapse any revealed app-hidden folders
    /// so they look empty again. The user re-reveals them with the Show app-hidden toggle (Hello/passphrase
    /// gated) once they return. Setting IsChecked here doesn't raise Click, so it won't re-prompt.</summary>
    private void ReHideOnBackground()
    {
        if (!_state.HideOnBackground || !_showAppHidden) return;
        _showAppHidden = false;
        if (ShowHiddenToggle.IsChecked == true) ShowHiddenToggle.IsChecked = false;
        if (ExplorerView.Visibility == Visibility.Visible) LoadCurrentFolder();
    }

    /// <summary>Toggles showing Windows-hidden (OS hidden-attribute) items. Session-only: not saved,
    /// so it reverts to off on the next launch.</summary>
    private void ShowWindowsHidden_Click(object sender, RoutedEventArgs e)
    {
        _showWindowsHidden = ShowWindowsHiddenToggle.IsChecked == true;
        if (ExplorerView.Visibility == Visibility.Visible) LoadCurrentFolder();
    }

    // ---- Hide folder feature ----

    private void HideFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is null) return;
        ToggleFolderHidden(_currentFolder);
        LoadCurrentFolder();
    }

    private void ToggleFolderHidden(string folderPath)
    {
        if (_state.HiddenFolders.Contains(folderPath)) _state.HiddenFolders.Remove(folderPath);
        else _state.HiddenFolders.Add(folderPath);
        _state.Save();
    }

    private void UnhideCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is null) return;
        _state.HiddenFolders.Remove(_currentFolder);
        _state.Save();
        LoadCurrentFolder();
    }

    private void UpdateHideFolderButton()
    {
        var canHide = _currentFolder is not null;
        HideFolderBtn.IsEnabled = canHide;
        var hidden = canHide && _state.HiddenFolders.Contains(_currentFolder!);
        HideFolderText.Text = hidden ? "Unhide folder" : "Hide folder";
        HideFolderIcon.Glyph = hidden ? GlyphEyeOpen : GlyphEyeOff;
        // The tooltip must flip with the label — a stale "Hide this folder" over an "Unhide folder"
        // button tells the user it does the opposite of what it does.
        ToolTipService.SetToolTip(HideFolderBtn, hidden
            ? "Show this folder again"
            : "Hide this folder (appears empty)");
    }

    // ---- File operations ----

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is null) { StatusText.Text = "Pick a folder first."; return; }
        try
        {
            var name = "New folder";
            var n = 2;
            while (Directory.Exists(Path.Combine(_currentFolder, name))) name = $"New folder ({n++})";
            var full = Path.Combine(_currentFolder, name);
            Directory.CreateDirectory(full);
            LoadCurrentFolder();

            // Immediately prompt to name it, like Explorer's inline rename.
            var item = _explorerItems.FirstOrDefault(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase));
            if (item is not null) await RenameExplorerAsync(item);
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't create folder: {ex.Message}"; }
    }

    /// <summary>Simple OK dialog for error/info messages.</summary>
    private async Task MessageAsync(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title, Content = message, CloseButtonText = "OK", XamlRoot = RootGrid.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    /// <summary>Ctrl+Alt+V: the discreet way in — opens the vault picker (unlock an existing vault or
    /// create one) without any visible vault UI.</summary>
    private async void OpenVaultShortcutAsync() => await ShowVaultPickerAsync();

    private void ExplorerSlideshow_Click(object sender, RoutedEventArgs e)
    {
        PopulatePhotoPipelineFromCurrent();
        StartSlideshow();
    }

    private void ExplorerCollage_Click(object sender, RoutedEventArgs e)
    {
        PopulatePhotoPipelineFromCurrent();
        if (_view.Count == 0) { StatusText.Text = "No images in this folder."; return; }
        Collage_Click(this, new RoutedEventArgs());
    }

    // ---- Explorer context menu ----

    private void ExplorerView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as ExplorerItem;
        _explorerContextItem = item;
        var target = (FrameworkElement)sender;
        ShowExplorerMenu(item, target, e.GetPosition(target));
        e.Handled = true;
    }

    private void ShowExplorerMenu(ExplorerItem? item, FrameworkElement target, Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem SMI(string text, Symbol? sym, RoutedEventHandler click)
        {
            var i = new MenuFlyoutItem { Text = text };
            if (sym.HasValue) i.Icon = new SymbolIcon(sym.Value);
            i.Click += click;
            return i;
        }

        // Recycle Bin view: Restore / Delete permanently (shred). No move/cut/paste here.
        if (_currentFolder == RecycleBin.Location)
        {
            if (item is not null)
            {
                List<ExplorerItem> BinSel()
                {
                    var sel = SelectedExplorerItems();
                    return sel.Any(s => s == item) ? sel : new List<ExplorerItem> { item };
                }
                if (!item.IsFolder)
                    menu.Items.Add(SMI("Open", Symbol.OpenFile, (_, _) => OpenExplorerItem(item)));
                menu.Items.Add(SMI("Restore", Symbol.Undo, (_, _) => RestoreBinEntries(BinSel())));
                menu.Items.Add(SMI("Delete permanently", Symbol.Delete, async (_, _) => await ShredBinEntriesAsync(BinSel())));
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            menu.Items.Add(SMI("Empty Recycle Bin", null, EmptyRecycleBin_Click));
            menu.Items.Add(SMI("Refresh", Symbol.Refresh, (_, _) => LoadCurrentFolder()));
            menu.ShowAt(target, new FlyoutShowOptions { Position = position });
            return;
        }

        if (item is not null)
        {
            if (item.IsShellItem)
            {
                menu.Items.Add(SMI("Open", Symbol.OpenFile, (_, _) => OpenExplorerItem(item)));
                menu.Items.Add(SMI("Copy to PC…", null, async (_, _) => await DeviceCopyToPcAsync(item)));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(SMI("Rename…", Symbol.Rename, async (_, _) => await DeviceRenameAsync(item)));
                menu.Items.Add(SMI("Delete", Symbol.Delete, async (_, _) => await DeviceDeleteAsync(item)));
                menu.ShowAt(target, new FlyoutShowOptions { Position = position });
                return;
            }
            menu.Items.Add(SMI(item.IsFolder ? "Open" : "Open", Symbol.OpenFile, (_, _) => OpenExplorerItem(item)));
            if (item.IsImage)
                menu.Items.Add(SMI("Open in new window", null, (_, _) => OpenInNewWindow(item.Path)));
            if (!item.IsFolder)
                menu.Items.Add(SMI("Open with…", null, (_, _) => OpenWithItem2(item.Path)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(SMI("Cut", Symbol.Cut, async (_, _) =>
            {
                var sel = SelectedExplorerItems();
                if (sel.All(s => s != item)) sel = new List<ExplorerItem> { item };
                await CopyItemsToClipboardAsync(sel, cut: true);
            }));
            menu.Items.Add(SMI("Copy", Symbol.Copy, async (_, _) =>
            {
                var sel = SelectedExplorerItems();
                if (sel.All(s => s != item)) sel = new List<ExplorerItem> { item };
                await CopyItemsToClipboardAsync(sel, cut: false);
            }));
            menu.Items.Add(SMI("Copy path", Symbol.Link, (_, _) => CopyTextToClipboard(item.Path)));
            menu.Items.Add(SMI("Paste", Symbol.Paste, async (_, _) => await PasteIntoCurrentAsync()));
            if (item.IsImage)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                var srcExt = System.IO.Path.GetExtension(item.Path).ToLowerInvariant();
                MenuFlyoutItem Conv(string label, string targetExt)
                {
                    var mi = new MenuFlyoutItem { Text = label };
                    mi.Click += async (_, _) =>
                    {
                        var sel = SelectedExplorerItems();
                        if (sel.All(s => s != item)) sel = new List<ExplorerItem> { item };
                        await ConvertImagesAsync(sel, targetExt);
                    };
                    return mi;
                }
                var convert = new MenuFlyoutSubItem { Text = "Convert" };
                if (srcExt is ".jpg" or ".jpeg") convert.Items.Add(Conv("JPG to PNG", ".png"));
                else if (srcExt == ".png") convert.Items.Add(Conv("PNG to JPG", ".jpg"));
                if (convert.Items.Count > 0) menu.Items.Add(convert);
                menu.Items.Add(SMI("Set as desktop background", null, (_, _) => SetWallpaperPath(item.Path)));
                menu.Items.Add(SMI("Set as lock screen", null, async (_, _) => await SetLockScreenAsync(item.Path)));
                menu.Items.Add(SMI("Set as Thumbnail", Symbol.Pictures, (_, _) => SetFolderThumbnail(item.Path)));
            }
            if (!item.IsFolder && ArchiveService.IsArchive(item.Path))
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(SMI("Extract Here", null, async (_, _) => await ExtractArchiveHereAsync(item)));
                menu.Items.Add(SMI("Extract All…", null, async (_, _) => await ExtractArchiveToAsync(item)));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            if (_vaults.IsAnyUnlocked && !ItemInsideOpenVault(item))
            {
                menu.Items.Add(SMI("Send to Vault", null, async (_, _) =>
                {
                    var sel = SelectedExplorerItems();
                    if (sel.All(s => s != item)) sel = new List<ExplorerItem> { item };
                    await SendToVaultAsync(sel);
                }));
            }
            menu.Items.Add(SMI("Move to new vault…", null, async (_, _) =>
            {
                var sel = SelectedExplorerItems();
                if (sel.All(s => s != item)) sel = new List<ExplorerItem> { item };
                await MoveToNewVaultAsync(sel);
            }));
            menu.Items.Add(new MenuFlyoutSeparator());
            if (item.IsFolder)
            {
                var hidden = _state.HiddenFolders.Contains(item.Path);
                menu.Items.Add(SMI(hidden ? "Unhide folder" : "Hide folder", null, (_, _) => { ToggleFolderHidden(item.Path); LoadCurrentFolder(); }));
                menu.Items.Add(SMI("Pin to sidebar", Symbol.Pin, (_, _) => AddPinnedPath(item.Path)));
                if (_state.DeveloperMode)
                    menu.Items.Add(SMI("Open terminal here", null, async (_, _) => await OpenTerminalHereAsync(item.Path)));
            }
            menu.Items.Add(SMI("Rename…", Symbol.Rename, async (_, _) =>
            {
                var sel = SelectedExplorerItems();
                if (sel.Count > 1 && sel.Any(s => s == item)) await BulkRenameExplorerAsync(item, sel);
                else await RenameExplorerAsync(item);
            }));
            menu.Items.Add(SMI("Delete", Symbol.Delete, async (_, _) => await DeleteExplorerAsync(item)));
            menu.Items.Add(SMI("Secure delete (shred)…", null, async (_, _) =>
            {
                var sel = SelectedExplorerItems();
                if (sel.All(s => s != item)) sel = new List<ExplorerItem> { item };
                await SecureShredAsync(sel);
            }));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(SMI("Properties", null, (_, _) => { var h = WinRT.Interop.WindowNative.GetWindowHandle(this); ShellOps.ShowProperties(h, item.Path); }));
        }
        else if (ShellLoc.IsShell(_currentFolder))
        {
            menu.Items.Add(SMI("New folder", Symbol.NewFolder, async (_, _) => await DeviceNewFolderAsync()));
            menu.Items.Add(SMI("Upload files…", Symbol.Upload, async (_, _) => await DeviceUploadAsync()));
            menu.Items.Add(SMI("Refresh", Symbol.Refresh, (_, _) => LoadCurrentFolder()));
        }
        else
        {
            menu.Items.Add(SMI("New folder", Symbol.NewFolder, NewFolder_Click));
            menu.Items.Add(SMI("Paste", Symbol.Paste, async (_, _) => await PasteIntoCurrentAsync()));
            menu.Items.Add(SMI("Refresh", Symbol.Refresh, (_, _) => LoadCurrentFolder()));
        }

        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
    }

    private void OpenWithItem2(string path)
    {
        try { ShellOps.OpenWith(path); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    /// <summary>Re-encodes the selected images to <paramref name="targetExt"/> (.png/.jpg) next to the
    /// originals. EXIF orientation is baked in (the new container may not carry it). JPEG has no alpha, so
    /// transparent areas flatten against the premultiplied background. Reads/writes via plain FileStream
    /// (not StorageFile) so it also works inside a vault's hidden working folder. When the user opts to
    /// remove the original: a normal file goes to the Recycle Bin (recoverable); a file inside the open
    /// vault is securely wiped in place instead — moving it to the global bin would leak its plaintext
    /// outside the vault.</summary>
    private async Task ConvertImagesAsync(IReadOnlyList<ExplorerItem> items, string targetExt)
    {
        var jpeg = targetExt is ".jpg" or ".jpeg";
        var encoderId = jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId;
        var alpha = jpeg ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied;

        int ok = 0, skipped = 0; string? lastPath = null; string? lastError = null;
        foreach (var it in items)
        {
            if (it.IsFolder || !it.IsImage) continue;
            // Don't pointlessly re-encode a file that's already in the target format.
            if (System.IO.Path.GetExtension(it.Path).ToLowerInvariant() is var e &&
                (e == targetExt || (jpeg && e is ".jpg" or ".jpeg"))) { skipped++; continue; }
            try
            {
                byte[] pix; uint ow, oh; double dx, dy;
                using (var inFs = new FileStream(it.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var inRas = inFs.AsRandomAccessStream())
                {
                    var decoder = await BitmapDecoder.CreateAsync(inRas);
                    var provider = await decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8, alpha, new BitmapTransform(),
                        ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
                    pix = provider.DetachPixelData();
                    ow = decoder.OrientedPixelWidth; oh = decoder.OrientedPixelHeight;
                    dx = decoder.DpiX > 0 ? decoder.DpiX : 96; dy = decoder.DpiY > 0 ? decoder.DpiY : 96;
                }

                var dir = System.IO.Path.GetDirectoryName(it.Path)!;
                var baseName = System.IO.Path.GetFileNameWithoutExtension(it.Path);
                var dest = UniquePath(System.IO.Path.Combine(dir, baseName + targetExt), isDir: false);

                using (var outFs = new FileStream(dest, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var outRas = outFs.AsRandomAccessStream())
                {
                    var encoder = await BitmapEncoder.CreateAsync(encoderId, outRas);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, alpha, ow, oh, dx, dy, pix);
                    await encoder.FlushAsync();
                }
                ok++; lastPath = dest;

                if (_state.ConvertRemovesOriginal)
                {
                    try
                    {
                        if (IsInCurrentVault(it.Path))
                        {
                            // Keep vault plaintext from escaping to the global Recycle Bin: shred in place.
                            var m = SecureWipe.Parse(_state.WipeMethod);
                            await SecureWipe.WipePathAsync(it.Path, m == WipeMethod.None ? WipeMethod.Random : m);
                        }
                        else _bin.MoveToBin(it.Path);
                    }
                    catch (Exception ex) { App.Log("Convert", ex); }
                }
            }
            catch (Exception ex) { lastError = ex.Message; App.Log("Convert", ex); }
        }

        if (ok > 0) LoadCurrentFolder();
        StatusText.Text = ok switch
        {
            0 when lastError is not null => $"Convert failed: {lastError}",
            0 when skipped > 0 => "Already in that format.",
            0 => "Nothing to convert.",
            1 => $"Converted to {System.IO.Path.GetFileName(lastPath)}",
            _ => $"Converted {ok} images to {targetExt.TrimStart('.').ToUpperInvariant()}",
        };
    }

    private void CopyTextToClipboard(string text)
    {
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetText(text);
        Clipboard.SetContent(data);
        StatusText.Text = "Path copied";
    }

    private async System.Threading.Tasks.Task PasteIntoCurrentAsync()
    {
        if (_currentFolder is null) { StatusText.Text = "Pick a folder first."; return; }
        if (_currentFolder == RecycleBin.Location || ShellLoc.IsShell(_currentFolder)) { StatusText.Text = "Can't paste here."; return; }
        try
        {
            // Read the system clipboard (so files copied in Explorer paste too).
            var clip = new List<string>();
            var clipMove = false;
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                try
                {
                    var items = await content.GetStorageItemsAsync();
                    clip = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                }
                catch { /* clipboard round-trip can drop items in unpackaged apps — handled below */ }
                clipMove = content.RequestedOperation.HasFlag(DataPackageOperation.Move);
            }

            // The in-app clip is the authoritative source/intent for anything copied inside Galileo — it is
            // kept current on each copy/cut and cleared whenever the clipboard changes externally. Trust it
            // over the system clipboard, whose unpackaged round-trip can return stale or partial paths (which
            // caused a second in-app copy to paste the FIRST file). Fall back to the system clipboard only
            // when we have no in-app clip (a fresh external copy from Explorer).
            List<string> paths;
            bool move;
            if (_fileClip is { } fc)
            {
                paths = fc.Paths.ToList();
                move = fc.Move;
            }
            else { paths = clip; move = clipMove; }

            // Only transfer items that still exist; tell the user if some vanished.
            var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            var missing = paths.Count - existing.Count;
            if (existing.Count == 0) { StatusText.Text = "Nothing to paste."; return; }


            var result = await RunTransferWithUiAsync(_currentFolder, existing, move);
            if (move && !result.Canceled && result.Errors == 0) _fileClip = null; // a cut is consumed only on a clean paste
            RefreshFolderInPlace(); // slot the pasted files in without losing scroll/selection/thumbnails
            var msg = DescribeTransfer(result, move);
            if (missing > 0) msg += $" ({missing} source(s) no longer existed)";
            StatusText.Text = msg;
        }
        catch (Exception ex) { StatusText.Text = $"Paste failed: {ex.Message}"; App.Log("Paste", ex); }
    }

    private static bool SamePaths(List<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        var set = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        return a.All(set.Contains);
    }

    private async System.Threading.Tasks.Task RenameExplorerAsync(ExplorerItem item)
    {
        var box = new TextBox { Text = item.Name };
        box.Loaded += (_, _) =>
        {
            // Preselect only the base name so the extension is kept by default (Explorer behavior).
            var ext = item.IsFolder ? "" : System.IO.Path.GetExtension(item.Name);
            var baseLen = item.Name.Length - ext.Length;
            if (baseLen > 0) box.Select(0, baseLen); else box.SelectAll();
        };
        var dialog = new ContentDialog
        {
            Title = "Rename",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var newName = box.Text.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.Name) return;
        try
        {
            if (item.IsFolder)
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(item.Path);
                await folder.RenameAsync(newName, NameCollisionOption.FailIfExists);
            }
            else
            {
                var file = await StorageFile.GetFileFromPathAsync(item.Path);
                await file.RenameAsync(newName, NameCollisionOption.FailIfExists);
            }
            // No folder reload: adopt the new name on the SAME item (loaded thumbnails, scroll and
            // selection all survive) and just move it to its new sorted position.
            var newPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path) ?? "", newName);
            _state.RepathEntry(item.Path, newPath); // hidden/favorite/thumbnail/sort/pin flags follow the rename
            item.Rename(newPath);
            ResortExplorerInPlace(item);
        }
        catch (Exception ex) { StatusText.Text = $"Rename failed: {ex.Message}"; }
    }

    /// <summary>Refreshes the current folder from disk while KEEPING existing item objects (their
    /// loaded thumbnails), the selection, and the scroll position — unlike LoadCurrentFolder, which
    /// rebuilds everything. New files slot in at their sorted spots; removed ones disappear. Grouped
    /// views rebuild their lightweight group wrappers around the same objects and restore scroll.</summary>
    private void RefreshFolderInPlace()
    {
        if (_currentFolder is null || _currentFolder == RecycleBin.Location || ShellLoc.IsShell(_currentFolder))
        { LoadCurrentFolder(); return; }
        if (!Directory.Exists(_currentFolder)) { NavigateToNearestExisting(); return; }
        if (!string.IsNullOrEmpty(_searchQuery)) { ReloadKeepingSelection(); return; } // search results: old path

        var fresh = _fs.List(_currentFolder, showWindowsHidden: _showWindowsHidden, _showAppHidden);

        // Adopt the already-shown object for every path that still exists (keeps its loaded icon);
        // only genuinely new files use the freshly-listed object.
        var shown = new Dictionary<string, ExplorerItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in _explorerItems) shown[it.Path] = it;
        _explorerRaw = fresh.Select(f => shown.TryGetValue(f.Path, out var old) ? old : f).ToList();

        if (_groupBy == "None")
        {
            ReconcileExplorerItems(SortItems(_explorerRaw));
            UpdateExplorerEmptyState();
            return;
        }

        // Grouped: the ItemsSource swap would drop selection and scroll — save and restore both.
        var list = ActiveExplorerList();
        var selected = list.SelectedItems.OfType<ExplorerItem>().ToHashSet();
        var scroll = FindScrollViewer(list)?.VerticalOffset ?? 0;
        ApplySortAndGroup();
        foreach (var it in _explorerItems) if (selected.Contains(it)) list.SelectedItems.Add(it);
        if (scroll > 0)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FindScrollViewer(ActiveExplorerList())?.ChangeView(null, scroll, null, disableAnimation: true));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is ScrollViewer sv) return sv;
            if (FindScrollViewer(c) is { } found) return found;
        }
        return null;
    }

    /// <summary>Re-sorts the visible listing after an in-place change (rename) WITHOUT reloading the
    /// folder: the same item objects stay in the list — their loaded thumbnails survive — and only
    /// positions change. Grouped views rebuild their group wrappers around the same objects.</summary>
    private void ResortExplorerInPlace(ExplorerItem? focus = null)
    {
        if (_groupBy == "None" && string.IsNullOrEmpty(_searchQuery) && _currentFolder is not null)
            ReconcileExplorerItems(SortItems(_explorerRaw));
        else
            ApplySortAndGroup();
        if (focus is not null)
        {
            var list = ActiveExplorerList();
            list.SelectedItem = focus;
            list.ScrollIntoView(focus);
        }
    }

    private static readonly string RenameJournalPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo", "rename-journal.json");

    /// <summary>Crash recovery: restores original names for files stranded mid bulk-rename (the
    /// journal survives a crash between the temp-rename and final-rename phases).</summary>
    private static void RecoverRenameJournal()
    {
        try
        {
            if (!File.Exists(RenameJournalPath)) return;
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RenameJournalPath));
            if (map is not null)
                foreach (var (temp, original) in map)
                {
                    try
                    {
                        if (!File.Exists(temp) && !Directory.Exists(temp)) continue;
                        var dest = original;
                        if (File.Exists(dest) || Directory.Exists(dest))
                            dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(original)!,
                                System.IO.Path.GetFileNameWithoutExtension(original) + " (restored)" + System.IO.Path.GetExtension(original));
                        if (Directory.Exists(temp)) Directory.Move(temp, dest); else File.Move(temp, dest);
                    }
                    catch { /* leave that one for manual recovery rather than fail the launch */ }
                }
            File.Delete(RenameJournalPath);
        }
        catch { /* recovery is best-effort */ }
    }

    /// <summary>Bulk-renames a multi-selection like Explorer, but with dash numbering: the primary
    /// item becomes "name", the rest "name-1", "name-2", … (each keeping its own extension).</summary>
    private async System.Threading.Tasks.Task BulkRenameExplorerAsync(ExplorerItem primary, List<ExplorerItem> selection)
    {
        var items = selection.Where(i => i.Kind != ExplorerItemKind.Drive).ToList();
        if (items.Count <= 1) { await RenameExplorerAsync(primary); return; }
        var dir = _currentFolder;
        if (string.IsNullOrEmpty(dir)) return;

        // Primary first, then the rest in their current order.
        items = new[] { primary }.Concat(items.Where(i => i != primary)).ToList();

        var box = new TextBox { Text = primary.Name };
        box.Loaded += (_, _) =>
        {
            // Show the extension but preselect only the base name.
            var ext = primary.IsFolder ? "" : System.IO.Path.GetExtension(primary.Name);
            var baseLen = primary.Name.Length - ext.Length;
            if (baseLen > 0) box.Select(0, baseLen); else box.SelectAll();
        };
        var dlg = new ContentDialog
        {
            Title = $"Rename {items.Count} items",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "They'll be named “name”, “name-1”, “name-2”, … keeping each file's extension (or the one you type).",
                                    Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    box,
                },
            },
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var typed = box.Text.Trim();
        if (string.IsNullOrEmpty(typed)) return;
        var typedExt = System.IO.Path.GetExtension(typed); // if the user typed an extension, apply it to all
        var baseName = System.IO.Path.GetFileNameWithoutExtension(typed);
        if (string.IsNullOrEmpty(baseName)) baseName = typed;

        // Resolve storage items + each one's extension.
        var resolved = new List<(ExplorerItem it, IStorageItem si, string ext)>();
        foreach (var it in items)
        {
            try
            {
                IStorageItem si = it.IsFolder
                    ? await StorageFolder.GetFolderFromPathAsync(it.Path)
                    : await StorageFile.GetFileFromPathAsync(it.Path);
                resolved.Add((it, si, it.IsFolder ? "" : System.IO.Path.GetExtension(it.Name)));
            }
            catch { /* skip unreadable */ }
        }

        // Phase 1: move everything to temp names so target names can't collide with current ones.
        // The journal is written BEFORE any rename: a crash (or phase-2 failure) between the phases
        // would otherwise strand files as extension-less "__galileo_…" names with no way back.
        var journal = new List<(string Temp, string Original)>();
        foreach (var (it, _, _) in resolved)
            journal.Add((System.IO.Path.Combine(dir, "__galileo_" + Guid.NewGuid().ToString("N")), it.Path));
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RenameJournalPath)!);
            File.WriteAllText(RenameJournalPath, System.Text.Json.JsonSerializer.Serialize(
                journal.ToDictionary(j => j.Temp, j => j.Original)));
        }
        catch { /* best-effort — worst case is the old stranding behavior */ }
        for (var i = 0; i < resolved.Count; i++)
        {
            try { await resolved[i].si.RenameAsync(System.IO.Path.GetFileName(journal[i].Temp), NameCollisionOption.GenerateUniqueName); }
            catch { }
        }

        // Phase 2: assign final names with a monotonic counter, skipping names already on disk.
        var counter = 0;
        var ok = 0;
        for (var i = 0; i < resolved.Count; i++)
        {
            var (it, si, ext) = resolved[i];
            var useExt = string.IsNullOrEmpty(typedExt) ? ext : typedExt; // typed ext wins, else keep own
            string name;
            while (true)
            {
                name = (counter == 0 ? baseName : $"{baseName}-{counter}") + useExt;
                counter++;
                var full = System.IO.Path.Combine(dir, name);
                if (!File.Exists(full) && !Directory.Exists(full)) break;
            }
            try { await si.RenameAsync(name, NameCollisionOption.FailIfExists); ok++; }
            catch (Exception ex)
            {
                StatusText.Text = $"Rename failed: {ex.Message}";
                // Give the file its REAL name back rather than leaving the meaningless temp name.
                try { await si.RenameAsync(System.IO.Path.GetFileName(journal[i].Original), NameCollisionOption.GenerateUniqueName); }
                catch { }
            }
            _state.RepathEntry(it.Path, si.Path); // hidden/favorite/thumbnail/sort/pin flags follow the rename
            it.Rename(si.Path);   // whatever landed on disk (final name, restored original, or the temp)
        }
        try { File.Delete(RenameJournalPath); } catch { } // clean run — nothing to recover

        // One in-place re-sort instead of a full reload — same objects, thumbnails intact.
        ResortExplorerInPlace();
        StatusText.Text = $"Renamed {ok} item(s)";
    }

    /// <summary>True while Shift is held — used to bypass the Recycle Bin (permanent delete).</summary>
    private static bool IsShiftDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    /// <summary>True while Alt is held — used for the "open in new window" gesture (Shift/Ctrl are taken by multi-select).</summary>
    private static bool IsAltDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    /// <summary>True while Ctrl is held — used to detect clipboard / select-all shortcuts.</summary>
    private static bool IsCtrlDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    /// <summary>True when keyboard focus is in a text field, so explorer shortcuts (Ctrl+A/C/V…)
    /// don't hijack normal text editing in the address bar or a rename box.</summary>
    private bool IsTextInputFocused() =>
        FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox or RichEditBox;

    /// <summary>The explorer list currently on screen (icon grid or details list).</summary>
    private ListViewBase ActiveExplorerList() =>
        ExplorerIconsView.Visibility == Visibility.Visible ? ExplorerIconsView : ExplorerDetailsList;

    /// <summary>The ExplorerItems currently selected in the active explorer list.</summary>
    private List<ExplorerItem> SelectedExplorerItems() =>
        ActiveExplorerList().SelectedItems.OfType<ExplorerItem>().ToList();

    /// <summary>Shows the selection count (and total size) in the status bar; falls back to the item
    /// count when nothing is selected.</summary>
    private void ExplorerSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListViewBase list) return;
        var sel = list.SelectedItems.OfType<ExplorerItem>().ToList();
        if (sel.Count == 0)
        {
            StatusText.Text = list.Items.Count > 0 ? $"{list.Items.Count} item{(list.Items.Count == 1 ? "" : "s")}" : "Ready";
            return;
        }
        var bytes = sel.Where(i => i.Kind == ExplorerItemKind.File).Sum(i => i.Size);
        StatusText.Text = bytes > 0
            ? $"{sel.Count} selected · {FormatBytes(bytes)}"
            : $"{sel.Count} selected";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }

    /// <summary>Copies (or cuts) the selected explorer items to the clipboard as files/folders,
    /// so they can be pasted here or into Windows Explorer.</summary>
    private async System.Threading.Tasks.Task CopySelectedExplorerAsync(bool cut)
        => await CopyItemsToClipboardAsync(SelectedExplorerItems(), cut);

    /// <summary>Copies (or cuts) the given items to both the in-app clip and the system clipboard.
    /// The in-app clip is recorded first so pasting inside Galileo works for the FULL set even when
    /// the WinRT storage-item round-trip below is slow or fails for some items (large selections,
    /// network shares); items WinRT can't wrap are simply left out of the system clipboard.</summary>
    private async System.Threading.Tasks.Task CopyItemsToClipboardAsync(List<ExplorerItem> selection, bool cut)
    {
        // Bin entries are GUID store files — cutting/copying them out corrupts the bin's index.
        if (_currentFolder == RecycleBin.Location) return;
        // Drives can't be copied/moved — only real files and folders.
        selection = selection.Where(i => i.Kind != ExplorerItemKind.Drive).ToList();
        if (selection.Count == 0) return;

        _fileClip = (selection.Select(s => s.Path).ToList(), cut);
        StatusText.Text = $"{(cut ? "Cut" : "Copied")} {selection.Count} item(s)";

        // Resolve concurrently: sequential WinRT lookups take seconds for hundreds of files, during
        // which a paste in Explorer would still see the OLD clipboard.
        var resolved = await System.Threading.Tasks.Task.WhenAll(selection.Select(async it =>
        {
            try
            {
                return it.IsFolder
                    ? (IStorageItem)await StorageFolder.GetFolderFromPathAsync(it.Path)
                    : await StorageFile.GetFileFromPathAsync(it.Path);
            }
            catch { return null; } // not visible to WinRT (locked, gone, odd path) — the in-app clip still has it
        }));
        var items = resolved.Where(i => i is not null).Cast<IStorageItem>().ToList();
        if (items.Count == 0) return; // in-app paste still works via _fileClip

        try
        {
            var data = new DataPackage { RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy };
            data.SetStorageItems(items);
            Clipboard.SetContent(data); // ContentChanged compares content against _fileClip — no flag needed
        }
        catch { /* clipboard busy — in-app paste still works via _fileClip */ }
    }

    /// <summary>Drives and volume roots must never be deletable as a unit — recycling "E:\" would
    /// copy the whole drive into the bin store and then erase it; shredding it would wipe the volume.</summary>
    private static bool IsUndeletableRoot(ExplorerItem i)
    {
        if (i.Kind == ExplorerItemKind.Drive) return true;
        if (i.IsShellItem || string.IsNullOrEmpty(i.Path)) return false;
        try
        {
            var full = System.IO.Path.GetFullPath(i.Path);
            return string.Equals(System.IO.Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Removes one non-bin, non-remote item: vault plaintext is shredded in place (it must
    /// never reach the global Recycle Bin), everything else goes to the bin.</summary>
    private async System.Threading.Tasks.Task<bool> BinOrShredVaultAwareAsync(string path)
    {
        if (IsInCurrentVault(path))
        {
            var m = SecureWipe.Parse(_state.WipeMethod);
            await SecureWipe.WipePathAsync(path, m == WipeMethod.None ? WipeMethod.Random : m);
            return true;
        }
        return _bin.MoveToBin(path);
    }

    private async System.Threading.Tasks.Task DeleteExplorerAsync(ExplorerItem item)
    {
        // In the bin view, "delete" means permanently shred that entry.
        if (_currentFolder == RecycleBin.Location) { await ShredBinEntriesAsync(new() { item }); return; }

        if (IsUndeletableRoot(item)) { StatusText.Text = "Drives can't be deleted."; return; }

        var permanent = IsShiftDown();
        var dialog = new ContentDialog
        {
            Title = permanent ? "Securely delete" : "Delete",
            Content = permanent
                ? $"Securely erase \"{item.Name}\" with overwrites? This can't be undone."
                : $"Move \"{item.Name}\" to the Recycle Bin?",
            PrimaryButtonText = permanent ? "Erase" : "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            if (permanent)
                await RunWipeWithUiAsync(new[] { item.Path }, CurrentWipeMethod, "Securely deleting 1 item");
            else if (!await BinOrShredVaultAwareAsync(item.Path))
                StatusText.Text = "Delete failed: item not found.";
            LoadCurrentFolder();
        }
        catch (Exception ex) { StatusText.Text = $"Delete failed: {ex.Message}"; }
    }

    /// <summary>Deletes the explorer selection (Del key). Shift = permanent.</summary>
    private async System.Threading.Tasks.Task DeleteSelectedExplorerAsync()
    {
        var active = ExplorerIconsView.Visibility == Visibility.Visible
            ? (ListViewBase)ExplorerIconsView : ExplorerDetailsList;
        var selection = active.SelectedItems.OfType<ExplorerItem>().ToList();
        selection = selection.Where(i => !IsUndeletableRoot(i)).ToList();
        if (selection.Count == 0) return;

        // In the bin view, "delete" means permanently shred the selected entries.
        if (_currentFolder == RecycleBin.Location) { await ShredBinEntriesAsync(selection); return; }

        var permanent = IsShiftDown();
        var dialog = new ContentDialog
        {
            Title = permanent ? "Securely delete" : "Delete",
            Content = permanent
                ? $"Securely erase {selection.Count} item(s) with overwrites? This can't be undone."
                : $"Move {selection.Count} item(s) to the Recycle Bin?",
            PrimaryButtonText = permanent ? "Erase" : "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (permanent)
        {
            var paths = selection.Select(i => i.Path).ToList();
            try { await RunWipeWithUiAsync(paths, CurrentWipeMethod, selection.Count == 1 ? "Securely deleting 1 item" : $"Securely deleting {selection.Count} items"); }
            catch (Exception ex) { StatusText.Text = $"Delete failed: {ex.Message}"; }
        }
        else
        {
            foreach (var item in selection)
            {
                try { await BinOrShredVaultAwareAsync(item.Path); }
                catch (Exception ex) { StatusText.Text = $"Delete failed: {ex.Message}"; }
            }
            StatusText.Text = $"Moved {selection.Count} item(s) to the Recycle Bin.";
        }
        LoadCurrentFolder();
    }

    /// <summary>Permanently shreds bin entries (secure overwrite), used by the bin view's Delete.</summary>
    private async System.Threading.Tasks.Task ShredBinEntriesAsync(List<ExplorerItem> selection)
    {
        if (selection.Count == 0) return;
        var dialog = new ContentDialog
        {
            Title = "Delete permanently",
            Content = $"Permanently erase {selection.Count} item(s) from the Recycle Bin with overwrites? This can't be undone.",
            PrimaryButtonText = "Erase",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var paths = selection.Select(i => i.Path).ToList(); // bin items' Path = their store path
        try { await RunWipeWithUiAsync(paths, CurrentWipeMethod, "Deleting permanently"); }
        catch (Exception ex) { StatusText.Text = $"Erase failed: {ex.Message}"; }
        _bin.RemoveMissing();
        LoadCurrentFolder();
    }

    /// <summary>Right-click "Secure delete (shred)" — overwrites the files immediately, bypassing the bin.</summary>
    private async System.Threading.Tasks.Task SecureShredAsync(List<ExplorerItem> selection)
    {
        selection = selection.Where(s => !s.IsShellItem && !IsUndeletableRoot(s)).ToList();
        if (selection.Count == 0) return;
        var effective = CurrentWipeMethod == WipeMethod.None ? WipeMethod.Random : CurrentWipeMethod; // shred always overwrites
        var what = selection.Count == 1 ? $"\"{selection[0].Name}\"" : $"{selection.Count} item(s)";
        var dialog = new ContentDialog
        {
            Title = "Secure delete (shred)",
            Content = $"Securely erase {what} with overwrites ({WipeMethodLabel(effective)})? This bypasses the Recycle Bin and can't be undone.",
            PrimaryButtonText = "Shred",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var paths = selection.Select(i => i.Path).ToList();
        try { await RunWipeWithUiAsync(paths, effective, selection.Count == 1 ? "Securely deleting 1 item" : $"Securely deleting {selection.Count} items"); }
        catch (Exception ex) { StatusText.Text = $"Shred failed: {ex.Message}"; }
        LoadCurrentFolder();
    }

    /// <summary>Restores bin entries to their original locations.</summary>
    private void RestoreBinEntries(List<ExplorerItem> selection)
    {
        if (selection.Count == 0) return;
        var restored = 0;
        foreach (var item in selection)
            if (_bin.Restore(item.Path, out _)) restored++;
        StatusText.Text = restored == 1 ? "Restored 1 item." : $"Restored {restored} item(s).";
        LoadCurrentFolder();
    }

    // ===================== Right-click context menu =====================

    private PhotoItem? _contextItem;

    private void ImageHost_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Current is null) return;
        _contextItem = Current;
        ShowImageMenu(ImageHost, e.GetPosition(ImageHost));
        e.Handled = true;
    }


    private void ShowImageMenu(FrameworkElement target, Windows.Foundation.Point position)
    {
        var item = _contextItem;
        if (item is null) return;

        var seg = new FontFamily("Segoe Fluent Icons");
        MenuFlyoutItem MI(string text, string glyph, RoutedEventHandler click)
        {
            var i = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph, FontFamily = seg } };
            i.Click += click;
            return i;
        }

        var menu = new MenuFlyout();
        menu.Items.Add(MI("Copy", "", async (_, _) => await CopyImageAsync(item)));
        menu.Items.Add(MI("Copy as file", "", async (_, _) => await CopyFileAsync(item)));
        menu.Items.Add(MI("Copy file path", "", (_, _) => CopyPath(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MI("Open with…", "", (_, _) => OpenWithItem(item)));
        menu.Items.Add(MI("Print…", "", (_, _) => RunVerb(item, "print")));
        menu.Items.Add(MI("Set as desktop background", "", (_, _) => SetWallpaper(item)));
        menu.Items.Add(MI("Set as lock screen", "", async (_, _) => await SetLockScreenAsync(item.Path)));
        menu.Items.Add(MI("Set as Thumbnail", "", (_, _) => SetFolderThumbnail(item.Path)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MI(item.IsFavorite ? "" : "", item.IsFavorite ? "" : "", (_, _) => FavoriteItem(item)));
        if (!item.IsHidden)
            menu.Items.Add(MI("Hide (Hidden album)", "", (_, _) => HideItemPermanently(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MI("Rename…", "", async (_, _) => await RenameItemAsync(item)));
        menu.Items.Add(MI("Show in Explorer", "", (_, _) => RevealItem(item)));
        menu.Items.Add(MI("Edit…", "", async (_, _) => await EnterEditModeAsync(item)));
        menu.Items.Add(MI("Delete", "", async (_, _) => await DeleteItemAsync(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MI("Properties", "", (_, _) => ShowProperties(item)));

        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
    }

    // ---- Core operations (shared by toolbar buttons and the context menu) ----

    private async System.Threading.Tasks.Task CopyImageAsync(PhotoItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            Clipboard.SetContent(data);
            StatusText.Text = "Image copied to clipboard";
        }
        catch (Exception ex) { StatusText.Text = $"Copy failed: {ex.Message}"; App.Log("CopyImage", ex); }
    }

    private async System.Threading.Tasks.Task CopyFileAsync(PhotoItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetStorageItems(new IStorageItem[] { file });
            Clipboard.SetContent(data);
            StatusText.Text = "File copied to clipboard";
        }
        catch (Exception ex) { StatusText.Text = $"Copy failed: {ex.Message}"; }
    }

    private void CopyPath(PhotoItem item)
    {
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetText(item.Path);
        Clipboard.SetContent(data);
        StatusText.Text = "Path copied";
    }

    private void RunVerb(PhotoItem item, string verb)
    {
        try { ShellOps.InvokeVerb(item.Path, verb); }
        catch (Exception ex) { StatusText.Text = ex.Message; App.Log("Verb:" + verb, ex); }
    }

    private void OpenWithItem(PhotoItem item)
    {
        try { ShellOps.OpenWith(item.Path); }
        catch (Exception ex) { StatusText.Text = $"Open with failed: {ex.Message}"; App.Log("OpenWith", ex); }
    }

    private void SetWallpaper(PhotoItem item) => SetWallpaperPath(item.Path);

    private void SetWallpaperPath(string path)
    {
        StatusText.Text = ShellOps.SetWallpaper(path)
            ? "Set as desktop background"
            : "Couldn't set the desktop background for this image.";
    }

    /// <summary>Sets the current user's lock-screen image (WinRT UserProfile API).</summary>
    private async Task SetLockScreenAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
            StatusText.Text = "Set as lock screen";
        }
        catch (Exception ex) { StatusText.Text = "Couldn't set the lock screen: " + ex.Message; App.Log("LockScreen", ex); }
    }

    private void ShowProperties(PhotoItem item)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShellOps.ShowProperties(hwnd, item.Path);
    }

    /// <summary>Makes <paramref name="imagePath"/> the preview thumbnail for its parent folder.</summary>
    private void SetFolderThumbnail(string imagePath)
    {
        var folder = System.IO.Path.GetDirectoryName(imagePath);
        if (string.IsNullOrEmpty(folder)) { StatusText.Text = "Couldn't set the folder thumbnail."; return; }
        _state.FolderThumbnails[folder] = imagePath;
        _state.Save();
        // The chosen thumbnail lives in app state — the folder's mtime doesn't change, so the disk
        // cache would keep serving the old composed icon without an explicit invalidation.
        ThumbDiskCache.Invalidate(folder);
        RefreshFolderIcon(folder);
        StatusText.Text = $"Folder thumbnail set to {System.IO.Path.GetFileName(imagePath)}";
    }

    /// <summary>Regenerates a folder's icon in the current listing (if it's visible) so a new
    /// thumbnail shows immediately.</summary>
    private void RefreshFolderIcon(string folderPath)
    {
        var match = _explorerItems.FirstOrDefault(i =>
            i.IsFolder && string.Equals(i.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        if (match is null) return;
        match.ResetIcon();
        _ = match.LoadIconAsync((uint)Math.Clamp(_iconSize, 48, 256));
    }

    private void FavoriteItem(PhotoItem item)
    {
        item.IsFavorite = !item.IsFavorite;
        // A file you're browsing from a friend's share lives in a temp folder that's wiped when you leave, so
        if (item.IsFavorite) _state.FavoritePaths.Add(item.Path);
        else _state.FavoritePaths.Remove(item.Path);
        _state.Save();
        if (ReferenceEquals(item, Current)) UpdateFavoriteIcon();
        if (_favoritesOnly) RefreshView();
    }

    private void HideItemPermanently(PhotoItem item)
    {
        item.IsHidden = true;
        _state.HiddenPaths.Add(item.Path);
        _state.Save();
        _obscured.Remove(item.Path);
        StatusText.Text = $"{item.FileName} moved to Hidden album";

        if (_showHiddenAlbum) return;
        var wasCurrent = ReferenceEquals(item, Current);
        RefreshView();
        if (InViewer && wasCurrent)
        {
            if (_view.Count == 0) { ShowExplorer(); return; }
            _currentIndex = Math.Min(_currentIndex, _view.Count - 1);
            _ = LoadCurrentAsync();
        }
    }

    private void RevealItem(PhotoItem item)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Path}\""); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async System.Threading.Tasks.Task DeleteItemAsync(PhotoItem item)
    {
        var permanent = IsShiftDown();
        var dialog = new ContentDialog
        {
            Title = permanent ? "Securely delete" : "Delete photo",
            Content = permanent
                ? $"Securely erase \"{item.FileName}\" with overwrites? This can't be undone."
                : $"Move \"{item.FileName}\" to the Recycle Bin?",
            PrimaryButtonText = permanent ? "Erase" : "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            if (permanent) await RunWipeWithUiAsync(new[] { item.Path }, CurrentWipeMethod, "Securely deleting 1 item");
            else if (!_bin.MoveToBin(item.Path)) { StatusText.Text = "Delete failed: item not found."; return; }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Delete failed: {ex.Message}";
            return;
        }

        _state.HiddenPaths.Remove(item.Path);
        _state.FavoritePaths.Remove(item.Path);
        _state.Save();
        var wasCurrent = ReferenceEquals(item, Current);
        _allPhotos.Remove(item);
        RefreshView();

        if (InViewer && wasCurrent)
        {
            if (_view.Count == 0) { ShowExplorer(); return; }
            _currentIndex = Math.Min(_currentIndex, _view.Count - 1);
            await LoadCurrentAsync();
        }
    }

    private async System.Threading.Tasks.Task RenameItemAsync(PhotoItem item)
    {
        var box = new TextBox { Text = item.FileName };
        box.Loaded += (_, _) =>
        {
            var ext = System.IO.Path.GetExtension(item.FileName);
            var baseLen = item.FileName.Length - ext.Length;
            if (baseLen > 0) box.Select(0, baseLen); else box.SelectAll();
        };
        var dialog = new ContentDialog
        {
            Title = "Rename",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = box.Text.Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, item.FileName, StringComparison.Ordinal)) return;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            await file.RenameAsync(newName, NameCollisionOption.FailIfExists);
            _state.RepathEntry(item.Path, file.Path); // hidden/favorite flags follow the rename
            StatusText.Text = $"Renamed to {newName}";
            var dir = System.IO.Path.GetDirectoryName(item.Path);
            if (dir is not null) await LoadFolderAsync(dir); // reload so paths refresh
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Rename failed: {ex.Message}";
        }
    }

    // ===================== Sortable column headers (Details) =====================

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string key) return;
        if (_sortBy == key) _sortDescending = !_sortDescending;
        else { _sortBy = key; _sortDescending = false; }
        _state.SortBy = _sortBy; _state.SortDescending = _sortDescending; // last-used default
        SaveSortPrefsForCurrentFolder();
        _state.Save();
        ApplySortAndGroup();
        SyncSortGroupRadios();
    }

    private void UpdateSortHeaders()
    {
        void Set(Button b, string label, string key)
        {
            var arrow = _sortDescending ? " ▾" : " ▴"; // ▾ / ▴
            b.Content = _sortBy == key ? label + arrow : label;
        }
        Set(HdrName, "Name", "Name");
        Set(HdrDate, "Date modified", "Date");
        Set(HdrType, "Type", "Type");
        Set(HdrSize, "Size", "Size");
    }

    // ===================== Search =====================

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_suppressSearchEvent) return;
        _searchQuery = sender.Text?.Trim() ?? "";
        _ = RunSearchAsync();
    }

    private void SearchRecursive_Click(object sender, RoutedEventArgs e)
    {
        _searchRecursive = SearchRecursiveToggle.IsChecked == true;
        if (!string.IsNullOrEmpty(_searchQuery)) _ = RunSearchAsync();
    }

    /// <summary>Clears the search box quietly (no reload); callers reload as needed.</summary>
    private void ClearSearch()
    {
        _searchResults = new();
        if (string.IsNullOrEmpty(_searchQuery) && (SearchBox is null || SearchBox.Text.Length == 0)) return;
        _searchQuery = "";
        if (SearchBox is not null && SearchBox.Text.Length > 0)
        {
            _suppressSearchEvent = true;
            SearchBox.Text = "";
            _suppressSearchEvent = false;
        }
    }

    private async Task RunSearchAsync()
    {
        if (string.IsNullOrEmpty(_searchQuery))
        {
            _searchResults = new();
            ApplySortAndGroup();
            ApplyViewMode();
            StatusText.Text = _currentFolder is null ? "This PC" : $"{_explorerRaw.Count} item(s)";
            return;
        }

        if (_searchRecursive && _currentFolder is not null && _currentFolder != RecycleBin.Location && !ShellLoc.IsShell(_currentFolder))
        {
            var q = _searchQuery;
            var root = _currentFolder;
            StatusText.Text = $"Searching {System.IO.Path.GetFileName(root.TrimEnd('\\'))}…";
            var results = await Task.Run(() => _fs.Search(root, q, _showWindowsHidden, _showAppHidden));
            if (q != _searchQuery || root != _currentFolder) return; // a newer query/folder superseded us
            _searchResults = results;
        }

        ApplySortAndGroup();
        ApplyViewMode();
        StatusText.Text = $"{_explorerItems.Count} result(s) for “{_searchQuery}”";
    }

    // ===================== Folder tabs =====================

    private sealed class ExplorerTab
    {
        public List<string?> History { get; } = new();
        public int Index { get; set; } = -1;
        public string? Current => Index >= 0 && Index < History.Count ? History[Index] : null;
    }

    private void NewTab(string? path)
    {
        var tvi = new TabViewItem { Tag = new ExplorerTab(), Header = "This PC", IconSource = new SymbolIconSource { Symbol = Symbol.Folder } };
        _switchingTabs = true;
        ExplorerTabs.TabItems.Add(tvi);
        ExplorerTabs.SelectedItem = tvi;
        _switchingTabs = false;
        // The TabView raises SelectionChanged for this add asynchronously, after the guard above has
        // been reset — suppress that one echo so the new tab doesn't load twice. Tab-scoped (not a
        // blind one-shot): if the echo never arrives, a bare flag would swallow the next REAL click.
        _suppressSelectionFor = tvi;

        // Fresh navigation state for the new tab, then go.
        _navHistory.Clear();
        _navIndex = -1;
        _currentFolder = null;
        ShowExplorer();
        NavigateTo(path);
    }

    private TabViewItem? _suppressSelectionFor;

    private void SyncActiveTab()
    {
        if (ExplorerTabs?.SelectedItem is not TabViewItem tvi || tvi.Tag is not ExplorerTab tab) return;
        tab.History.Clear();
        tab.History.AddRange(_navHistory);
        tab.Index = _navIndex;
        tvi.Header = TabHeaderFor(_currentFolder);
    }

    private string TabHeaderFor(string? folder)
    {
        if (folder is null) return "This PC";
        if (folder == RecycleBin.Location) return "Recycle Bin";
        if (ShellLoc.IsShell(folder)) return _shell.DisplayName(ShellLoc.Unwrap(folder));
        // Inside a vault or an opened zip, show the friendly name at its root (not the temp GUID path).
        if (SpecialRootFor(folder) is { } sr)
        {
            return string.Equals(folder.TrimEnd('\\'), sr.root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? sr.label
                : System.IO.Path.GetFileName(folder.TrimEnd('\\'));
        }
        var name = System.IO.Path.GetFileName(folder.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    /// <summary>If the path is inside a special root (unlocked vault working dir or an opened zip's
    /// temp dir), returns the friendly label + that root path; otherwise null.</summary>
    private (string label, string root)? SpecialRootFor(string? path)
    {
        if (path is null) return null;
        if (_vaults.Current?.WorkingDir is { } w && path.StartsWith(w, StringComparison.OrdinalIgnoreCase))
            return (_vaults.Current.Name, w);
        foreach (var kv in _openZips)
            if (path.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                return (kv.Value.Name, kv.Key);
        return null;
    }

    private void ExplorerTabs_AddClick(TabView sender, object args) => NewTab(null);

    private async void ExplorerTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingTabs) return;
        if (_suppressSelectionFor is { } sup)
        {
            _suppressSelectionFor = null;
            if (ReferenceEquals(ExplorerTabs.SelectedItem, sup)) return; // NewTab's async echo
            // Not the echo — a real switch the user made; handle it normally.
        }
        if (ExplorerTabs.SelectedItem is not TabViewItem tvi || tvi.Tag is not ExplorerTab tab) return;

        // Returning to an unlocked vault tab: re-materialize the working copy if it went missing/empty
        // (e.g. after viewing media in another tab) so it doesn't show empty until a manual re-open.
        if (_vaults.Current?.WorkingDir is { } wd && tab.Current is { } tc
            && tc.StartsWith(wd, StringComparison.OrdinalIgnoreCase))
            try { await _vaults.EnsureCurrentWorkingAsync(); } catch (Exception ex) { App.Log("VaultEnsure", ex); }

        // Load the selected tab's navigation state into the live fields.
        _navHistory.Clear();
        _navHistory.AddRange(tab.History);
        _navIndex = tab.Index;
        _currentFolder = tab.Current;
        ClearSearch();
        ShowExplorer();
        LoadCurrentFolder();
        UpdateNavButtons();
        BuildBreadcrumb();

        // Force one more list/render pass after layout settles — the grid can come back blank after a
        // view transition (viewer/gallery → explorer) even though the collection is populated.
        var folder = _currentFolder;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ExplorerView.Visibility == Visibility.Visible
                && string.Equals(_currentFolder, folder, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(_searchQuery))
                RefreshFolderIncremental();
        });
    }

    private void ExplorerTabs_CloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (sender.TabItems.Count <= 1) return; // always keep one tab
        sender.TabItems.Remove(args.Tab);       // removal re-selects a neighbour → SelectionChanged loads it
    }

    // ===================== Rubber-band (marquee) selection =====================

    private static GridViewItem? FindAncestorGridViewItem(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is GridViewItem gvi) return gvi;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>Middle-clicking an image opens it in a new window (like a browser's open-in-new-tab).</summary>
    private void Explorer_MiddleClick(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsMiddleButtonPressed) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ExplorerItem item && item.IsImage)
        {
            OpenInNewWindow(item.Path);
            e.Handled = true;
        }
    }

    private void ExplorerIcons_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(ExplorerIconsView);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !pt.Properties.IsLeftButtonPressed)
            return;
        // A press on an item belongs to selection/drag; only empty space starts a marquee.
        if (FindAncestorGridViewItem(e.OriginalSource as DependencyObject) is not null) return;

        _marqueeActive = true;
        _marqueeStart = e.GetCurrentPoint(ExplorerContentArea).Position;
        if (!IsCtrlDown()) ExplorerIconsView.SelectedItems.Clear();
        // Ctrl+marquee is ADDITIVE: the selection made before the drag is the baseline the box adds
        // to — without it, the first pointer move deselected everything outside the box.
        _marqueeBaseline.Clear();
        if (IsCtrlDown())
            foreach (var s in ExplorerIconsView.SelectedItems.OfType<ExplorerItem>()) _marqueeBaseline.Add(s);
        ExplorerIconsView.CapturePointer(e.Pointer);
        UpdateMarquee(_marqueeStart);
        MarqueeRect.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void ExplorerIcons_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeActive) return;
        UpdateMarquee(e.GetCurrentPoint(ExplorerContentArea).Position);
        SelectWithinMarquee();
        e.Handled = true;
    }

    private void ExplorerIcons_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeActive) return;
        EndMarquee(e.Pointer);
        e.Handled = true;
    }

    private void ExplorerIcons_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeActive) EndMarquee(null);
    }

    private void EndMarquee(Pointer? pointer)
    {
        _marqueeActive = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
        if (pointer is not null) ExplorerIconsView.ReleasePointerCapture(pointer);
    }

    private void UpdateMarquee(Windows.Foundation.Point cur)
    {
        var x = Math.Min(_marqueeStart.X, cur.X);
        var y = Math.Min(_marqueeStart.Y, cur.Y);
        MarqueeRect.Margin = new Thickness(x, y, 0, 0);
        MarqueeRect.Width = Math.Abs(cur.X - _marqueeStart.X);
        MarqueeRect.Height = Math.Abs(cur.Y - _marqueeStart.Y);
    }

    /// <summary>Pre-marquee selection kept selected during a Ctrl+drag (additive marquee).</summary>
    private readonly HashSet<ExplorerItem> _marqueeBaseline = new();

    private void SelectWithinMarquee()
    {
        var box = new Windows.Foundation.Rect(MarqueeRect.Margin.Left, MarqueeRect.Margin.Top, MarqueeRect.Width, MarqueeRect.Height);
        foreach (var item in _explorerItems)
        {
            if (ExplorerIconsView.ContainerFromItem(item) is not GridViewItem c) continue; // realized containers only
            var b = c.TransformToVisual(ExplorerContentArea).TransformBounds(new Windows.Foundation.Rect(0, 0, c.ActualWidth, c.ActualHeight));
            var hit = !(b.Right < box.Left || b.Left > box.Right || b.Bottom < box.Top || b.Top > box.Bottom)
                      || _marqueeBaseline.Contains(item);
            var selected = ExplorerIconsView.SelectedItems.Contains(item);
            if (hit && !selected) ExplorerIconsView.SelectedItems.Add(item);
            else if (!hit && selected) ExplorerIconsView.SelectedItems.Remove(item);
        }
    }

    /// <summary>Finds the folder/drive whose item sits under the drop point (the drag event's
    /// OriginalSource is the list itself, not the row, so we hit-test by position instead). Falls
    /// back to the current folder when the drop isn't over a folder.</summary>
    private string? DropTargetFolder(DragEventArgs e)
    {
        var view = ExplorerIconsView.Visibility == Visibility.Visible
            ? (ItemsControl)ExplorerIconsView : ExplorerDetailsList;
        var pos = e.GetPosition((UIElement)view);
        foreach (var item in _explorerItems)
        {
            if (!item.IsFolder) continue;
            if (view.ContainerFromItem(item) is not FrameworkElement c) continue; // realized rows only
            var tl = c.TransformToVisual((UIElement)view).TransformPoint(new Windows.Foundation.Point(0, 0));
            if (pos.X >= tl.X && pos.X <= tl.X + c.ActualWidth && pos.Y >= tl.Y && pos.Y <= tl.Y + c.ActualHeight)
                return item.Path; // dropped onto this folder/drive
        }
        return _currentFolder;
    }

    // Volume root of the first dragged item, resolved async on the first DragOver frame. Windows
    // convention: a same-volume drag MOVES, a cross-volume drag (USB stick, network share) COPIES —
    // defaulting cross-volume to move deleted originals off the user's removable media.
    private string? _dragSourceRoot;
    private bool _dragSourceRootPending;

    private void ResetDragSource() { _dragSourceRoot = null; _dragSourceRootPending = false; }

    private static string? VolumeRootOf(string path)
    {
        try { return System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path)); } catch { return null; }
    }

    private async System.Threading.Tasks.Task CacheDragSourceRootAsync(DataPackageView view)
    {
        try
        {
            var items = await view.GetStorageItemsAsync();
            var first = items.Select(i => i.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            _dragSourceRoot = first is null ? null : VolumeRootOf(first);
        }
        catch { _dragSourceRoot = null; }
    }

    /// <summary>Move or copy for this drag: Shift forces move, Ctrl forces copy, otherwise Windows'
    /// rule — move within a volume, copy across volumes (Copy until the source volume is known).</summary>
    private bool DragWantsMove(string target)
    {
        if (IsCtrlDown()) return false;
        if (IsShiftDown()) return true;
        return _dragSourceRoot is { } src && string.Equals(src, VolumeRootOf(target), StringComparison.OrdinalIgnoreCase);
    }

    private void ExplorerList_DragOver(object sender, DragEventArgs e)
    {
        var target = DropTargetFolder(e);
        if (target is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        if (!_dragSourceRootPending)
        {
            _dragSourceRootPending = true;
            _ = CacheDragSourceRootAsync(e.DataView);
        }
        var move = DragWantsMove(target);
        e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
        if (e.DragUIOverride is not null)
        {
            e.DragUIOverride.Caption = move ? "Move here" : "Copy here";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        e.Handled = true; // keep RootGrid's "open" drop from also firing
    }

    private async void ExplorerList_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var target = DropTargetFolder(e);
        if (target is null) return;
        var move = DragWantsMove(target); // same rule the DragOver caption promised
        ResetDragSource();
        e.Handled = true;

        var deferral = e.GetDeferral();
        List<string> paths;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        }
        catch (Exception ex) { StatusText.Text = $"Drop failed: {ex.Message}"; App.Log("Drop", ex); return; }
        finally { deferral.Complete(); } // release the source app once we've read the dropped paths

        paths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList(); // only transfer what's actually there
        if (paths.Count == 0) return;
        // MTP/portable-device targets have no filesystem path — the copy engine would fail every file
        // ("Copied 0 item(s), N failed"). Route through the shell uploader like the Upload button does.
        if (ShellLoc.IsShell(target))
        {
            try { _shell.Upload(paths, ShellLoc.Unwrap(target), WinRT.Interop.WindowNative.GetWindowHandle(this)); }
            catch (Exception ex) { StatusText.Text = $"Upload failed: {ex.Message}"; }
            return;
        }
        try
        {
            var result = await RunTransferWithUiAsync(target, paths, move);
            RefreshFolderInPlace(); // a move out of this folder removes items in place; no full reload
            StatusText.Text = DescribeTransfer(result, move, TabHeaderFor(target));
        }
        catch (Exception ex) { StatusText.Text = $"Drop failed: {ex.Message}"; App.Log("Drop", ex); }
    }

    /// <summary>Human-readable transfer outcome that never hides skipped/failed/cancelled items.</summary>
    private static string DescribeTransfer(TransferResult r, bool move, string? dest = null)
    {
        var verb = move ? "Moved" : "Copied";
        var to = dest is null ? "" : $" to {dest}";
        var extra = "";
        if (r.Skipped > 0) extra += $", skipped {r.Skipped}";
        if (r.Errors > 0) extra += $", {r.Errors} failed";
        if (r.Canceled) extra += " — cancelled";
        return $"{verb} {r.FilesCompleted} item(s){to}{extra}.";
    }

    // ===================== File-transfer progress (Apple-style panel) =====================

    private bool _transferPanelShown;
    private bool _progressHidden;              // user dismissed the card; the op keeps running in the background
    private bool _progressHideable;            // show the Hide button (wipes)
    private double _transferFrac;
    private object? _activeOp;                 // token identifying the operation that owns the card
    private Action? _progressCancel;           // cancels the active operation
    private Action? _progressPauseToggle;      // null → the operation can't pause (hides the Pause button)
    private Func<bool>? _progressIsPaused;

    /// <summary>Runs a copy/move through the cancellable/pausable engine, showing the floating progress
    /// card (after a short delay so instant operations don't flash it). Returns the full result so the
    /// caller can report skipped/failed items instead of silently dropping them.</summary>
    private async System.Threading.Tasks.Task<TransferResult> RunTransferWithUiAsync(string destDir, List<string> paths, bool move)
    {
        _progressCancel?.Invoke(); // only one panel at a time
        var transfer = new FileTransfer();
        var token = new object();
        BeginProgressOp(token,
            title: (move ? "Moving " : "Copying ") + (paths.Count == 1 ? "1 item" : $"{paths.Count} items"),
            cancel: transfer.Cancel, pauseToggle: transfer.TogglePause, isPaused: () => transfer.IsPaused, hideable: false);

        var revealCts = ScheduleReveal(token); // copies/moves only flash the card if they take a moment
        var progress = new Progress<TransferProgress>(p => { if (ReferenceEquals(_activeOp, token)) UpdateTransferUi(p); });
        TransferResult result;
        try { result = await transfer.RunAsync(destDir, paths, move, progress, ResolveConflictAsync); }
        finally { EndProgressOp(token, revealCts); }

        return result;
    }

    /// <summary>Runs a secure wipe of the given paths behind the floating progress card. The card is shown
    /// immediately (wipes can be long) with Cancel and a Hide button (keeps running in the background);
    /// wiping can't pause. Used by Empty Recycle Bin, right-click shred, and Shift+Delete.</summary>
    private async System.Threading.Tasks.Task RunWipeWithUiAsync(IReadOnlyList<string> paths, WipeMethod method, string title)
    {
        _progressCancel?.Invoke();
        var cts = new System.Threading.CancellationTokenSource();
        var token = new object();
        BeginProgressOp(token, title, cancel: cts.Cancel, pauseToggle: null, isPaused: null, hideable: true);
        ShowTransferPanel(); // always show for wipes

        var progress = new Progress<TransferProgress>(p => { if (ReferenceEquals(_activeOp, token)) UpdateTransferUi(p); });
        try { await SecureWipe.WipePathsAsync(paths, method, progress, cts.Token); }
        finally { EndProgressOp(token, null); }
    }

    private void BeginProgressOp(object token, string title, Action cancel, Action? pauseToggle, Func<bool>? isPaused, bool hideable)
    {
        _activeOp = token;
        _progressCancel = cancel;
        _progressPauseToggle = pauseToggle;
        _progressIsPaused = isPaused;
        _progressHideable = hideable;
        _progressHidden = false;
        _transferPanelShown = false;
        _transferFrac = 0;
        TransferTitle.Text = title;
        TransferFile.Text = "Preparing…";
        TransferStats.Text = "";
        TransferEta.Text = "";
        TransferBar.Value = 0;
        SetTransferPaused(false);
    }

    private System.Threading.CancellationTokenSource ScheduleReveal(object token)
    {
        // Delayed reveal: skip the panel entirely for operations that finish in <350ms.
        var revealCts = new System.Threading.CancellationTokenSource();
        _ = System.Threading.Tasks.Task.Delay(350, revealCts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            DispatcherQueue.TryEnqueue(() => { if (ReferenceEquals(_activeOp, token)) ShowTransferPanel(); });
        }, System.Threading.Tasks.TaskScheduler.Default);
        return revealCts;
    }

    private void EndProgressOp(object token, System.Threading.CancellationTokenSource? revealCts)
    {
        revealCts?.Cancel();
        if (!ReferenceEquals(_activeOp, token)) return;
        _activeOp = null;
        _progressCancel = null;
        _progressPauseToggle = null;
        _progressIsPaused = null;
        _progressHideable = false;
        HideTransferPanel();
    }

    /// <summary>Conflict callback for <see cref="FileTransfer"/>: marshals to the UI thread and shows the
    /// Overwrite / Skip / Keep-both dialog (with file details + an identical-contents check).</summary>
    private Task<ConflictChoice> ResolveConflictAsync(ConflictInfo info)
    {
        var tcs = new TaskCompletionSource<ConflictChoice>();
        // On any UI failure (dialog collision, dispatch dropped while closing), default to KEEP BOTH so a
        // conflicting file is never silently dropped — renaming is always non-destructive.
        var queued = DispatcherQueue.TryEnqueue(async () =>
        {
            try { tcs.TrySetResult(await ShowConflictDialogAsync(info)); }
            catch { tcs.TrySetResult(new ConflictChoice { Action = ConflictAction.KeepBoth }); }
        });
        if (!queued) tcs.TrySetResult(new ConflictChoice { Action = ConflictAction.KeepBoth });
        return tcs.Task;
    }

    private async Task<ConflictChoice> ShowConflictDialogAsync(ConflictInfo info)
    {
        TextBlock Line(string label, string value) => new()
        {
            FontSize = 12,
            Inlines =
            {
                new Microsoft.UI.Xaml.Documents.Run { Text = label + "  ", Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
                new Microsoft.UI.Xaml.Documents.Run { Text = value },
            },
        };

        Border Card(string heading, long size, DateTime modified, bool accent) => new()
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = accent
                ? new SolidColorBrush(Color.FromArgb(255, 0x6E, 0xA8, 0xFF))
                : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(accent ? 1.5 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = heading, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 },
                    Line("Size", FormatBytes(size)),
                    Line("Modified", modified == default ? "—" : modified.ToString("yyyy-MM-dd HH:mm")),
                },
            },
        };

        var body = new StackPanel { Spacing = 12, MinWidth = 360 };
        body.Children.Add(new TextBlock
        {
            Text = info.Identical
                ? "These files are identical — same size and contents (verified by hash)."
                : "A different file with this name is already here. Compare and choose:",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = info.Identical
                ? new SolidColorBrush(Color.FromArgb(255, 0x5A, 0xD1, 0x9A))
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        body.Children.Add(Card("Replace with this file", info.SourceSize, info.SourceModified, accent: true));
        body.Children.Add(Card("Keep the existing file", info.DestSize, info.DestModified, accent: false));

        CheckBox? applyAll = null;
        if (info.RemainingConflicts > 0)
        {
            applyAll = new CheckBox { Content = $"Do this for all {info.RemainingConflicts + 1} conflicts", FontSize = 12.5 };
            body.Children.Add(applyAll);
        }

        var canceled = false;
        var cancelLink = new HyperlinkButton { Content = "Cancel the whole transfer", FontSize = 12, Padding = new Thickness(0) };

        var dialog = new ContentDialog
        {
            Title = $"“{info.Name}” already exists",
            Content = new StackPanel { Spacing = 12, Children = { body, cancelLink } },
            PrimaryButtonText = "Replace",
            SecondaryButtonText = "Keep both",
            CloseButtonText = "Skip",
            DefaultButton = info.Identical ? ContentDialogButton.Close : ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        cancelLink.Click += (_, _) => { canceled = true; dialog.Hide(); };

        var result = await dialog.ShowAsync();
        var action = canceled ? ConflictAction.Cancel : result switch
        {
            ContentDialogResult.Primary => ConflictAction.Overwrite,
            ContentDialogResult.Secondary => ConflictAction.KeepBoth,
            _ => ConflictAction.Skip,
        };
        return new ConflictChoice { Action = action, ApplyToAll = applyAll?.IsChecked == true };
    }

    private void UpdateTransferUi(TransferProgress p)
    {
        if (!_transferPanelShown && p.BytesTotal > 8L * 1024 * 1024) ShowTransferPanel(); // big op → show now
        if (!string.IsNullOrEmpty(p.CurrentFile)) TransferFile.Text = p.CurrentFile;

        _transferFrac = Math.Clamp(p.Fraction, 0, 1);
        TransferBar.Value = _transferFrac;

        var pct = (int)Math.Round(_transferFrac * 100);
        TransferStats.Text = p.BytesTotal > 0
            ? $"{FormatBytes(p.BytesDone)} of {FormatBytes(p.BytesTotal)}  ·  {pct}%"
            : $"{p.FilesDone} of {p.FilesTotal}";
        TransferEta.Text = p.Paused ? "Paused" : FormatEta(p);
        SetTransferPaused(p.Paused);
    }


    private void ShowTransferPanel()
    {
        if (_transferPanelShown || _progressHidden) return; // don't re-show once the user dismissed it
        _transferPanelShown = true;
        TransferPauseBtn.Visibility = _progressPauseToggle is null ? Visibility.Collapsed : Visibility.Visible;
        TransferHideBtn.Visibility = _progressHideable ? Visibility.Visible : Visibility.Collapsed;
        TransferPanel.Visibility = Visibility.Visible;
        try
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var sb = new Storyboard();
            void Add(string path, double from, double to)
            {
                var a = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(280), EasingFunction = ease };
                Storyboard.SetTarget(a, TransferPanel);
                Storyboard.SetTargetProperty(a, path);
                sb.Children.Add(a);
            }
            Add("Opacity", 0, 1);
            Add("(UIElement.RenderTransform).(TranslateTransform.Y)", 24, 0);
            sb.Begin();
        }
        catch { TransferPanel.Opacity = 1; TransferPanelShift.Y = 0; }
    }

    private void HideTransferPanel()
    {
        if (!_transferPanelShown) { TransferPanel.Visibility = Visibility.Collapsed; return; }
        _transferPanelShown = false;
        try
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var sb = new Storyboard();
            void Add(string path, double from, double to)
            {
                var a = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(200), EasingFunction = ease };
                Storyboard.SetTarget(a, TransferPanel);
                Storyboard.SetTargetProperty(a, path);
                sb.Children.Add(a);
            }
            Add("Opacity", TransferPanel.Opacity, 0);
            Add("(UIElement.RenderTransform).(TranslateTransform.Y)", 0, 16);
            sb.Completed += (_, _) => { TransferPanel.Visibility = Visibility.Collapsed; TransferPanelShift.Y = 24; };
            sb.Begin();
        }
        catch { TransferPanel.Visibility = Visibility.Collapsed; }
    }

    private void SetTransferPaused(bool paused)
    {
        TransferPauseIcon.Glyph = ((char)(paused ? 0xE768 : 0xE769)).ToString(); // Play : Pause
        ToolTipService.SetToolTip(TransferPauseBtn, paused ? "Resume" : "Pause");
    }

    private void TransferPause_Click(object sender, RoutedEventArgs e)
    {
        if (_progressPauseToggle is null) return;
        _progressPauseToggle();
        var paused = _progressIsPaused?.Invoke() ?? false;
        SetTransferPaused(paused);
        if (paused) TransferEta.Text = "Paused";
    }

    private void TransferCancel_Click(object sender, RoutedEventArgs e)
    {
        _progressCancel?.Invoke();
        TransferFile.Text = "Cancelling…";
    }

    /// <summary>Dismisses the card but lets the operation finish in the background.</summary>
    private void TransferHide_Click(object sender, RoutedEventArgs e)
    {
        _progressHidden = true;
        HideTransferPanel();
        StatusText.Text = "Working in the background…";
    }

    private static string FormatEta(TransferProgress p)
    {
        if (p.BytesPerSecond < 1 || p.BytesTotal <= 0) return "";
        var remain = p.BytesTotal - p.BytesDone;
        if (remain <= 0) return "Finishing…";
        var secs = remain / p.BytesPerSecond;
        if (secs < 1) return "Less than a second left";
        if (secs < 60) return $"About {Math.Round(secs)} seconds left";
        if (secs < 3600) return $"About {Math.Round(secs / 60)} min left";
        return $"About {Math.Round(secs / 3600, 1)} hr left";
    }

    private static string UniquePath(string path, bool isDir)
    {
        if (isDir ? !Directory.Exists(path) : !File.Exists(path)) return path;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var stem = isDir ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : System.IO.Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
            if (isDir ? !Directory.Exists(candidate) : !File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDir(dir, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(dir)));
    }

    private static bool IsSubPath(string parent, string child)
    {
        var p = System.IO.Path.GetFullPath(parent).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        var c = System.IO.Path.GetFullPath(child).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    // ===================== Privacy gate (Windows Hello) =====================

    /// <summary>
    /// Returns true if the Hidden album / app-hidden folders may be revealed. When the lock is on,
    /// prompts Windows Hello (falling back to a confirmation if Hello isn't available).
    /// </summary>
    private async Task<bool> EnsureHiddenUnlockedAsync()
    {
        if (!_state.LockHiddenAlbum || _helloUnlocked) return true;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var hello = await HelloAuth.VerifyAsync(hwnd, "Verify your identity to reveal hidden items");
        bool ok;
        if (hello.HasValue) ok = hello.Value;
        else
        {
            // No Hello on this device — fall back to an explicit confirmation.
            var dialog = new ContentDialog
            {
                Title = "Reveal hidden items?",
                Content = "Windows Hello isn't set up, so identity can't be verified. Reveal hidden items anyway?",
                PrimaryButtonText = "Reveal",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };
            ok = await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        if (ok) _helloUnlocked = true;
        return ok;
    }

    // ===================== Entrance animations =====================

    private void AnimateSettingsIn()
    {
        // Guaranteed final state first, so the card is always visible even if the animation no-ops.
        SettingsCard.Opacity = 1;
        SettingsCardTransform.ScaleX = SettingsCardTransform.ScaleY = 1;
        SettingsCardTransform.TranslateY = 0;

        try
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var sb = new Storyboard();

            // Animations must target the element with a full property path; targeting a bare
            // CompositeTransform object does not resolve in WinUI 3 (and can failfast the render thread).
            void Add(string path, double from, double to)
            {
                var anim = new DoubleAnimation
                {
                    From = from,
                    To = to,
                    Duration = TimeSpan.FromMilliseconds(180),
                    EasingFunction = ease
                };
                Storyboard.SetTarget(anim, SettingsCard);
                Storyboard.SetTargetProperty(anim, path);
                sb.Children.Add(anim);
            }
            Add("Opacity", 0, 1);
            Add("(UIElement.RenderTransform).(CompositeTransform.ScaleX)", 0.97, 1);
            Add("(UIElement.RenderTransform).(CompositeTransform.ScaleY)", 0.97, 1);
            Add("(UIElement.RenderTransform).(CompositeTransform.TranslateY)", 14, 0);
            sb.Begin();
        }
        catch (Exception ex)
        {
            App.Log("AnimateSettingsIn", ex); // final state is already applied above
        }
    }

    // ===================== Google Drive backup =====================

    private void UpdateBackupUi()
    {
        var configured = GoogleDriveBackup.IsConfigured;
        var connected = _drive.IsConnected;
        BackupStatusText.Text = !configured ? "Sign-in unavailable — no Google OAuth client configured"
            : connected
                ? (string.IsNullOrEmpty(_drive.ConnectedEmail) ? "Signed in" : $"Signed in as {_drive.ConnectedEmail}")
                : "Not signed in";
        BackupConnectBtn.Content = connected ? "Sign out" : "Sign in with Google";
        BackupNowBtn.IsEnabled = connected;
        BackupRestoreBtn.IsEnabled = connected;
        LastBackupText.Text = _state.LastVaultBackupUtcTicks > 0
            ? $"Last backup: {new DateTime(_state.LastVaultBackupUtcTicks, DateTimeKind.Utc).ToLocalTime():yyyy-MM-dd HH:mm}"
            : "";
    }

    private async Task SilentReconnectDriveAsync()
    {
        try { await _drive.TryReconnectAsync(); }
        catch (Exception ex) { App.Log("DriveReconnect", ex); }
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (SettingsOverlay.Visibility == Visibility.Visible) UpdateBackupUi();
            await MaybeRunScheduledBackupAsync(); // launch-time backup if overdue and now connected
        });
    }

    private async void BackupConnect_Click(object sender, RoutedEventArgs e)
    {
        if (!GoogleDriveBackup.IsConfigured) { await ShowBackupSetupHelpAsync(); return; }
        if (_drive.IsConnected) { await _drive.DisconnectAsync(); UpdateBackupUi(); return; }

        BackupStatusText.Text = "Opening your browser to sign in…";
        try { await _drive.ConnectAsync(forcePrompt: true); }
        catch (OperationCanceledException) { BackupStatusText.Text = "Sign-in canceled or timed out."; }
        catch (Exception ex) { BackupStatusText.Text = "Connect failed: " + ex.Message; App.Log("DriveConnect", ex); }
        UpdateBackupUi();
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e) => await BackupAllVaultsAsync(silent: false);

    private bool _backupRunning;

    /// <summary>Backs up every vault to Google Drive. <paramref name="silent"/> = a scheduled run
    /// (no button toggling; quiet status), otherwise the user pressed "Back up all vaults now".</summary>
    private async Task BackupAllVaultsAsync(bool silent)
    {
        if (_backupRunning || !_drive.IsConnected) return;
        var vaults = _vaults.List();
        if (vaults.Count == 0) { if (!silent) BackupStatusText.Text = "No vaults to back up."; return; }

        _backupRunning = true;
        if (!silent) BackupNowBtn.IsEnabled = false;
        var progress = new Progress<string>(m => { if (!silent) BackupStatusText.Text = m; });
        var ok = 0; var failed = 0;
        try
        {
            var n = 0;
            foreach (var v in vaults)
            {
                if (!silent) BackupStatusText.Text = $"Backing up “{v.Name}” ({++n}/{vaults.Count})…";
                // Isolate each vault so one failure doesn't abort the rest or strand the timestamp.
                try { await _drive.BackupVaultAsync(v, progress); ok++; }
                catch (Exception ex) { failed++; App.Log("DriveBackup", ex); }
            }
            if (ok > 0) { _state.LastVaultBackupUtcTicks = DateTime.UtcNow.Ticks; ForceSaveState(); }
            var msg = failed == 0 ? $"Backed up {ok} vault(s)." : $"Backed up {ok}, {failed} failed.";
            if (silent) StatusText.Text = "Scheduled backup: " + msg;
            else BackupStatusText.Text = msg;
            if (SettingsOverlay.Visibility == Visibility.Visible) UpdateBackupUi();
        }
        finally { _backupRunning = false; if (!silent) BackupNowBtn.IsEnabled = _drive.IsConnected; }
    }

    /// <summary>Runs a scheduled backup if one is due (cadence elapsed, connected, vaults present).</summary>
    private async Task MaybeRunScheduledBackupAsync()
    {
        if (_backupRunning || !_drive.IsConnected) return;
        if (_vaults.IsAnyUnlocked) return; // don't snapshot a vault mid-edit/mid-sync — wait until it's locked
        var interval = _state.BackupSchedule switch
        {
            "Daily" => TimeSpan.FromDays(1),
            "Weekly" => TimeSpan.FromDays(7),
            _ => TimeSpan.Zero, // Off
        };
        if (interval == TimeSpan.Zero) return;
        var last = _state.LastVaultBackupUtcTicks > 0
            ? new DateTime(_state.LastVaultBackupUtcTicks, DateTimeKind.Utc)
            : DateTime.MinValue;
        if (DateTime.UtcNow - last < interval) return; // not due yet
        await BackupAllVaultsAsync(silent: true);
    }

    private async void BackupRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!_drive.IsConnected) return;

        BackupStatusText.Text = "Listing backups…";
        IReadOnlyList<RemoteVault> backups;
        try { backups = await _drive.ListBackupsAsync(); }
        catch (Exception ex) { BackupStatusText.Text = "Couldn't list backups: " + ex.Message; return; }
        if (backups.Count == 0) { BackupStatusText.Text = "No backups found in Drive."; return; }

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300 };
        foreach (var b in backups)
        {
            var local = Directory.Exists(System.IO.Path.Combine(VaultManager.VaultsRoot, b.Id));
            list.Items.Add(new TextBlock { Text = $"{b.Id}  ·  {b.FileCount} files{(local ? "  (already on this PC)" : "")}" });
        }
        list.SelectedIndex = 0;

        var dlg = new ContentDialog
        {
            Title = "Restore vault from Drive",
            Content = list,
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || list.SelectedIndex < 0) return;

        var chosen = backups[list.SelectedIndex];
        BackupStatusText.Text = "Restoring…";
        try
        {
            await _drive.RestoreVaultAsync(chosen.Id, new Progress<string>(m => BackupStatusText.Text = m));
            RefreshVaults();
            BackupStatusText.Text = "Restored — unlock it from the sidebar.";
        }
        catch (Exception ex) { BackupStatusText.Text = "Restore failed: " + ex.Message; App.Log("DriveRestore", ex); }
    }

    private async Task BackupSingleVaultAsync(string vaultId)
    {
        Vault v;
        try
        {
            v = _vaults.Current?.Id == vaultId ? _vaults.Current
                : Vault.Load(System.IO.Path.Combine(VaultManager.VaultsRoot, vaultId));
        }
        catch (Exception ex) { StatusText.Text = "Backup failed: " + ex.Message; return; }

        if (!_drive.IsConnected)
        {
            if (!GoogleDriveBackup.IsConfigured) { await ShowBackupSetupHelpAsync(); return; }
            StatusText.Text = "Connecting to Google Drive…";
            try { await _drive.ConnectAsync(); }
            catch (Exception ex) { StatusText.Text = "Connect failed: " + ex.Message; return; }
        }

        if (_backupRunning) { StatusText.Text = "A backup is already running…"; return; }
        _backupRunning = true; // don't collide with a scheduled BackupAllVaultsAsync on the same Drive folder
        StatusText.Text = $"Backing up “{v.Name}”…";
        try
        {
            await _drive.BackupVaultAsync(v, new Progress<string>(m => StatusText.Text = m));
            _state.LastVaultBackupUtcTicks = DateTime.UtcNow.Ticks;
            ForceSaveState();
            StatusText.Text = $"Backed up “{v.Name}” to Google Drive.";
        }
        catch (Exception ex) { StatusText.Text = "Backup failed: " + ex.Message; App.Log("DriveBackup", ex); }
        finally { _backupRunning = false; }
    }

    /// <summary>Persists state even while the Settings dialog has Save suppressed (for the backup timestamp).</summary>
    private void ForceSaveState()
    {
        var prev = _state.SuppressSave;
        _state.SuppressSave = false;
        _state.Save();
        _state.SuppressSave = prev;
    }

    private async Task ShowBackupSetupHelpAsync()
    {
        var msg = "Sign-in needs a Google OAuth client. Galileo ships one in Assets\\google-oauth.json; " +
                  "to use your own instead:\n\n" +
                  "1. Create a project at console.cloud.google.com\n" +
                  "2. Enable the Google Drive API\n" +
                  "3. Create an OAuth client ID of type “Desktop app”\n" +
                  "4. Download its JSON and save it as:\n" +
                  GoogleDriveBackup.OAuthConfigPath + "\n\n" +
                  "Then reopen Settings and click Sign in with Google.";
        await new ContentDialog
        {
            Title = "Set up Google Drive sign-in",
            Content = new TextBlock { Text = msg, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot,
        }.ShowAsync();
    }

    // ===================== Developer-mode terminal =====================

    private void ApplyDeveloperMode()
    {
        TerminalBtn.Visibility = _state.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_state.DeveloperMode)
        {
            HideTerminal();
            try { _term?.Dispose(); } catch { }
            _term = null;
        }
    }

    private void DeveloperModeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.DeveloperMode = DeveloperModeSwitch.IsOn;
        _state.Save();
        ApplyDeveloperMode();
    }

    private async void TerminalToggle_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalPane.Visibility == Visibility.Visible) HideTerminal();
        else await ShowTerminalAsync();
    }

    private void TerminalClose_Click(object sender, RoutedEventArgs e) => HideTerminal();

    private async Task OpenTerminalHereAsync(string folder)
    {
        NavigateTo(folder);
        await ShowTerminalAsync();
        if (_termWebReady) StartTerminalSession(_termCols, _termRows); // (re)start the shell in this folder
    }

    private void HideTerminal()
    {
        TerminalPane.Visibility = Visibility.Collapsed;
        TerminalSplitter.Visibility = Visibility.Collapsed;
        TerminalCol.Width = new GridLength(0);
    }

    private async Task ShowTerminalAsync()
    {
        TerminalCol.Width = new GridLength(Math.Clamp(ExplorerView.ActualWidth * 0.4, 280, 640));
        TerminalSplitter.Visibility = Visibility.Visible;
        TerminalPane.Visibility = Visibility.Visible;

        if (ShellCombo.Items.Count == 0) PopulateShells();
        if (_termWebReady) return; // the session keeps running across hide/show

        try
        {
            await TerminalWeb.EnsureCoreWebView2Async();
            var assets = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
            TerminalWeb.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "galileo.terminal", assets, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            TerminalWeb.CoreWebView2.WebMessageReceived += Terminal_WebMessageReceived;
            TerminalWeb.CoreWebView2.Navigate("https://galileo.terminal/index.html");
            _termWebReady = true;
        }
        catch (Exception ex) { StatusText.Text = "Terminal failed to start: " + ex.Message; App.Log("Terminal", ex); }
    }

    private void Terminal_WebMessageReceived(Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
    {
        string msg;
        try { msg = args.TryGetWebMessageAsString(); } catch { return; }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(msg);
            var root = doc.RootElement;
            switch (root.GetProperty("t").GetString())
            {
                case "in":
                    var b64 = root.GetProperty("d").GetString();
                    if (!string.IsNullOrEmpty(b64)) _term?.Write(Convert.FromBase64String(b64));
                    break;
                case "size":
                    _termCols = (short)root.GetProperty("cols").GetInt32();
                    _termRows = (short)root.GetProperty("rows").GetInt32();
                    if (_term is null) StartTerminalSession(_termCols, _termRows);
                    else _term.Resize(_termCols, _termRows);
                    break;
            }
        }
        catch { /* ignore malformed messages */ }
    }

    private void StartTerminalSession(short cols, short rows)
    {
        if (cols <= 0) cols = 80;
        if (rows <= 0) rows = 24;
        try { _term?.Dispose(); } catch { }
        _term = null;

        var exe = ShellCombo.SelectedIndex >= 0 && ShellCombo.SelectedIndex < _shells.Count
            ? _shells[ShellCombo.SelectedIndex].Exe
            : Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var cwd = _currentFolder is not null && Directory.Exists(_currentFolder)
            ? _currentFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var session = new TerminalSession();
        session.Output += OnTerminalOutput;
        try { session.Start(exe, null, cwd, cols, rows); _term = session; }
        catch (Exception ex) { StatusText.Text = "Couldn't start the shell: " + ex.Message; App.Log("Terminal", ex); session.Dispose(); }
    }

    private void OnTerminalOutput(byte[] data)
    {
        var b64 = Convert.ToBase64String(data);
        DispatcherQueue.TryEnqueue(() =>
        {
            try { TerminalWeb.CoreWebView2?.PostWebMessageAsString("{\"t\":\"out\",\"d\":\"" + b64 + "\"}"); }
            catch { }
        });
    }

    private void ShellCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShellCombo.SelectedIndex < 0 || ShellCombo.SelectedIndex >= _shells.Count) return;
        _state.TerminalShell = LabelToKey(_shells[ShellCombo.SelectedIndex].Label);
        _state.Save();
        if (_term is not null) StartTerminalSession(_termCols, _termRows); // restart in the chosen shell
    }

    private void WipeMethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.WipeMethod = WipeMethodCombo.SelectedIndex switch
        {
            0 => "Zero",
            2 => "Dod3",
            3 => "Dod7",
            4 => "Gutmann35",
            _ => "Random",
        };
        _state.Save();
    }

    private void SecureEmptySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SecureDeleteOnEmpty = SecureEmptySwitch.IsOn;
        _state.Save();
    }

    private void ConvertRemovesOriginalSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.ConvertRemovesOriginal = ConvertRemovesOriginalSwitch.IsOn;
        _state.Save();
    }

    private async void BackupScheduleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.BackupSchedule = BackupScheduleCombo.SelectedIndex switch { 1 => "Daily", 2 => "Weekly", _ => "Off" };
        _state.Save();
        if (_state.BackupSchedule != "Off")
        {
            if (!_drive.IsConnected)
                BackupStatusText.Text = "Sign in to Google Drive to enable scheduled backups.";
            else
                await MaybeRunScheduledBackupAsync(); // back up now if already overdue
        }
    }

    private void PopulateShells()
    {
        _shells.Clear();
        _shells.Add(("Command Prompt", Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"));
        var ps = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
        if (ps is not null) _shells.Add(("PowerShell", ps));
        var wsl = FindOnPath("wsl.exe");
        if (wsl is not null) _shells.Add(("WSL", wsl));

        ShellCombo.Items.Clear();
        foreach (var s in _shells) ShellCombo.Items.Add(s.Label);
        var want = SavedShellLabel();
        var idx = _shells.FindIndex(s => s.Label == want);
        ShellCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private string SavedShellLabel() => _state.TerminalShell switch
    {
        "powershell" => "PowerShell",
        "wsl" => "WSL",
        _ => "Command Prompt",
    };

    private static string LabelToKey(string label) => label switch
    {
        "PowerShell" => "powershell",
        "WSL" => "wsl",
        _ => "cmd",
    };

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator))
        {
            try { var full = System.IO.Path.Combine(dir.Trim(), exe); if (File.Exists(full)) return full; }
            catch { }
        }
        return null;
    }

    /// <summary>Gives an element the ↔ resize cursor. (WinUI's ProtectedCursor is protected and
    /// Border is sealed, so we set it via reflection.)</summary>
    private static void SetResizeCursor(UIElement element)
    {
        try
        {
            var prop = typeof(UIElement).GetProperty("ProtectedCursor",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            prop?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast));
        }
        catch { /* cosmetic only */ }
    }

    private void TerminalSplitter_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        var w = TerminalCol.ActualWidth - e.Delta.Translation.X; // drag left → wider terminal
        var max = Math.Max(280, ExplorerView.ActualWidth - 360);
        TerminalCol.Width = new GridLength(Math.Clamp(w, 240, max));
    }

    private void SidebarSplitter_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        var w = SidebarCol.ActualWidth + e.Delta.Translation.X; // drag right → wider sidebar
        SidebarCol.Width = new GridLength(Math.Clamp(w, 160, 560));
    }

    private void SidebarSplitter_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        _state.SidebarWidth = SidebarCol.ActualWidth;
        _state.Save();
    }

    // ===================== Settings =====================

    private void OpenRecycleBin_Click(object sender, RoutedEventArgs e) => NavigateTo(RecycleBin.Location);

    private async void EmptyRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        var count = _bin.Count;
        if (count == 0) { StatusText.Text = "The Recycle Bin is already empty."; return; }

        // The empty-bin overwrite is opt-in (the toggle); right-click shred always overwrites.
        var method = _state.SecureDeleteOnEmpty ? CurrentWipeMethod : WipeMethod.None;
        var dlg = new ContentDialog
        {
            Title = "Empty Recycle Bin?",
            Content = method == WipeMethod.None
                ? $"Permanently delete all {count} item(s) in the Recycle Bin? This can't be undone."
                : $"Securely erase all {count} item(s) in the Recycle Bin with overwrites ({WipeMethodLabel(method)})? This can't be undone.",
            PrimaryButtonText = "Empty",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var paths = _bin.StorePaths();
        try { await RunWipeWithUiAsync(paths, method, "Emptying Recycle Bin"); }
        catch (Exception ex) { StatusText.Text = $"Empty failed: {ex.Message}"; }
        _bin.RemoveMissing();
        StatusText.Text = $"{_bin.Count} item(s) in Recycle Bin.";
        if (_currentFolder == RecycleBin.Location) LoadCurrentFolder();
    }

    private static string WipeMethodLabel(WipeMethod m) => m switch
    {
        WipeMethod.Zero => "Zero, 1 pass",
        WipeMethod.Random => "Random, 1 pass",
        WipeMethod.Dod3 => "DoD 5220.22-M, 3 passes",
        WipeMethod.Dod7 => "DoD ECE, 7 passes",
        WipeMethod.Gutmann35 => "Gutmann, 35 passes",
        _ => "no overwrite",
    };

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // Snapshot current state and suppress writes; edits apply live for preview but only persist
        // when the user clicks Save (Cancel reverts to this snapshot).
        _settingsSnapshot = _state.Clone();
        _state.SuppressSave = true;

        _loadingSettings = true;
        ThemeCombo.SelectedIndex = _state.Theme switch { "Light" => 1, "Dark" => 2, "Terminal" => 3, "Gray" => 4, _ => 0 };
        OpenModeCombo.SelectedIndex = _state.SingleClickToOpen ? 1 : 0;
        IconSizeCombo.SelectedIndex = _iconSize <= 85 ? 0 : _iconSize >= 140 ? 2 : 1;
        FolderPreviewSwitch.IsOn = _state.FolderPreviews;
        ShowExtensionsSwitch.IsOn = _state.ShowExtensions;
        PeekSwitch.IsOn = _state.PeekEnabled;
        AlbumArtSwitch.IsOn = _state.ShowAlbumArt;
        StartMutedSwitch.IsOn = _state.StartVideoMuted;
        SingleInstanceSwitch.IsOn = _state.SingleInstance;
        AlwaysNewWindowSwitch.IsOn = _state.AlwaysOpenMediaInNewWindow;
        CloseToBackSwitch.IsOn = _state.CloseToViewerBack;
        LockHiddenSwitch.IsOn = _state.LockHiddenAlbum;
        HideOnBackgroundSwitch.IsOn = _state.HideOnBackground;
        VaultIdleCombo.SelectedIndex = _state.VaultIdleSeconds <= 0 ? 5
            : _state.VaultIdleSeconds <= 300 ? 0
            : _state.VaultIdleSeconds <= 600 ? 1
            : _state.VaultIdleSeconds <= 900 ? 2
            : _state.VaultIdleSeconds <= 1800 ? 3 : 4;
        VaultHelloSwitch.IsOn = _state.VaultDefaultUseHello;
        VaultWipeSwitch.IsOn = _state.VaultWipeOnFailure;
        VaultWipeCountBox.Value = _state.VaultWipeAfterAttempts;
        VaultWipeCountRow.Visibility = _state.VaultWipeOnFailure ? Visibility.Visible : Visibility.Collapsed;
        HideVaultSwitch.IsOn = _state.HideVaultEntry;
        DeveloperModeSwitch.IsOn = _state.DeveloperMode;
        WipeMethodCombo.SelectedIndex = CurrentWipeMethod switch
        {
            WipeMethod.Zero => 0,
            WipeMethod.Dod3 => 2,
            WipeMethod.Dod7 => 3,
            WipeMethod.Gutmann35 => 4,
            _ => 1, // Random
        };
        SecureEmptySwitch.IsOn = _state.SecureDeleteOnEmpty;
        ConvertRemovesOriginalSwitch.IsOn = _state.ConvertRemovesOriginal;
        CollageLayoutCombo.SelectedIndex = (int)_collagePreset;
        BackupScheduleCombo.SelectedIndex = _state.BackupSchedule switch { "Daily" => 1, "Weekly" => 2, _ => 0 };
        RunInBackgroundSwitch.IsOn = _state.RunInBackground;
        StartWithWindowsSwitch.IsOn = _state.StartWithWindows;
        UpdateBackupUi();
        SlideshowSecondsSlider.Value = Math.Clamp(_state.SlideshowSeconds, 2, 30);
        SlideshowSecondsValue.Text = $"{_state.SlideshowSeconds}s";
        ShuffleSwitch.IsOn = _state.SlideshowShuffle;
        LoopSwitch.IsOn = _state.SlideshowLoop;
        TransitionCombo.SelectedIndex = (int)_state.SlideshowTransition;
        _loadingSettings = false;

        // Cap the card to the current window height (so it scrolls on short windows) using a
        // known-laid-out element — ActualHeight bindings don't update reliably in WinUI.
        SettingsCard.MaxHeight = Math.Max(320, RootGrid.ActualHeight - 40);
        SettingsOverlay.Visibility = Visibility.Visible;
        // Focus-modal: the card's TabFocusNavigation=Cycle traps Tab, but only once focus is INSIDE —
        // otherwise Tab keeps walking the dimmed UI behind the scrim (address bar included).
        ThemeCombo.Focus(FocusState.Programmatic);
        AnimateSettingsIn();
    }

    /// <summary>Keeps the Settings card within the window as it is resized WHILE open — a one-shot cap
    /// left Save/Cancel unreachable below the bottom edge after shrinking the window.</summary>
    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible)
            SettingsCard.MaxHeight = Math.Max(320, RootGrid.ActualHeight - 40);
    }

    private void SingleInstanceSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SingleInstance = SingleInstanceSwitch.IsOn;
        _state.Save();
    }

    private void AlwaysNewWindowSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.AlwaysOpenMediaInNewWindow = AlwaysNewWindowSwitch.IsOn;
        _state.Save();
    }

    private void CloseToBackSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.CloseToViewerBack = CloseToBackSwitch.IsOn;
        _state.Save();
    }

    private void LockHiddenSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.LockHiddenAlbum = LockHiddenSwitch.IsOn;
        if (!_state.LockHiddenAlbum) _helloUnlocked = false; // re-arm the gate when turned off
        _state.Save();
    }

    private void HideOnBackgroundSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.HideOnBackground = HideOnBackgroundSwitch.IsOn;
        _state.Save();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.Theme = ThemeCombo.SelectedIndex switch { 1 => "Light", 2 => "Dark", 3 => "Terminal", 4 => "Gray", _ => "System" };
        ApplyTheme();
        _state.Save();
    }

    private void OpenModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SingleClickToOpen = OpenModeCombo.SelectedIndex == 1;
        ApplyClickMode();
        _state.Save();
    }

    private static readonly string[] CustomThemeKeys =
    {
        "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush",
        "LayerFillColorAltBrush", "LayerFillColorDefaultBrush",
        "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush",
        "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush",
        "AcrylicInAppFillColorDefaultBrush", "AcrylicBackgroundFillColorDefaultBrush",
        "SolidBackgroundFillColorBaseBrush", "CardStrokeColorDefaultBrush", "SubtleFillColorSecondaryBrush"
    };

    private Microsoft.UI.Xaml.Media.MicaBackdrop? _micaBackdrop;

    // Reuse one Mica backdrop instead of allocating a new controller on every theme apply (avoids a flash).
    // Mica needs Windows 11 (22000+); on Windows 10 / RDP / transparency-off it silently fails to apply,
    // and with RootGrid.Background = null the window would have NO base surface at all — fall back to
    // Acrylic, then to a solid theme brush.
    private void EnsureMica()
    {
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            _micaBackdrop ??= new Microsoft.UI.Xaml.Media.MicaBackdrop();
            if (!ReferenceEquals(SystemBackdrop, _micaBackdrop)) SystemBackdrop = _micaBackdrop;
        }
        else if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
        {
            if (SystemBackdrop is not Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop)
                SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        }
        else
        {
            SystemBackdrop = null;
            RootGrid.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        }
    }

    /// <summary>True when no translucent backdrop is available — RootGrid must keep a solid background.</summary>
    private static bool BackdropAvailable =>
        Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()
        || Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported();

    private void ApplyTheme()
    {
        var res = Application.Current.Resources;
        foreach (var k in CustomThemeKeys) res.Remove(k);

        switch (_state.Theme)
        {
            case "Light":
                EnsureMica();
                if (BackdropAvailable) RootGrid.Background = null;
                SetElementTheme(ElementTheme.Light);
                break;
            case "Dark":
                EnsureMica();
                if (BackdropAvailable) RootGrid.Background = null;
                SetElementTheme(ElementTheme.Dark);
                break;
            case "Terminal":
                ApplyCustomTheme(Rgb(255, 4, 10, 4), Rgb(255, 13, 24, 13), Rgb(255, 90, 255, 130), Rgb(130, 60, 210, 110));
                break;
            case "Gray":
                ApplyCustomTheme(Rgb(255, 46, 48, 50), Rgb(255, 64, 66, 68), Rgb(255, 230, 230, 232), Rgb(120, 150, 154, 158));
                break;
            default:
                EnsureMica();
                if (BackdropAvailable) RootGrid.Background = null;
                SetElementTheme(ElementTheme.Default);
                break;
        }

        // Caption buttons (min/max/close) need a matching foreground or they vanish on the backdrop.
        ApplyCaptionColorsForTheme();
        UpdateChromeForDarkSurface(); // theme switched mid-viewer/editor: keep the chrome readable
    }

    private void ApplyCaptionColorsForTheme() => SetCaptionColors(_state.Theme switch
    {
        "Light" => Rgb(255, 30, 30, 30),
        "System" => (Windows.UI.Color?)null,   // let the system decide
        _ => Rgb(255, 235, 235, 235)           // Dark / Terminal / Gray
    });

    /// <summary>Viewer/editor/collage paint a hardcoded dark surface up behind the title bar — in
    /// Light theme the theme-colored brand/filename/status text and caption glyphs would be
    /// near-black on black. Force dark-theme (light-on-dark) chrome while any of them is visible.</summary>
    private void UpdateChromeForDarkSurface()
    {
        var dark = ViewerView.Visibility == Visibility.Visible
                || EditorView.Visibility == Visibility.Visible
                || CollageView.Visibility == Visibility.Visible;
        AppTitleBar.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Default;
        StatusText.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Default;
        if (dark) SetCaptionColors(Rgb(255, 235, 235, 235));
        else ApplyCaptionColorsForTheme();
    }

    private void SetCaptionColors(Windows.UI.Color? fg)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var tb = _appWindow.TitleBar;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = fg.HasValue ? Rgb(160, fg.Value.R, fg.Value.G, fg.Value.B) : null;
        tb.ButtonHoverBackgroundColor = fg.HasValue ? Rgb(40, fg.Value.R, fg.Value.G, fg.Value.B) : null;
    }

    private void ApplyCustomTheme(Windows.UI.Color bg, Windows.UI.Color panel, Windows.UI.Color fg, Windows.UI.Color stroke)
    {
        SystemBackdrop = null;
        RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(bg);

        var res = Application.Current.Resources;
        Microsoft.UI.Xaml.Media.SolidColorBrush B(Windows.UI.Color c) => new(c);
        res["TextFillColorPrimaryBrush"] = B(fg);
        res["TextFillColorSecondaryBrush"] = B(Rgb(200, fg.R, fg.G, fg.B));
        res["TextFillColorTertiaryBrush"] = B(Rgb(150, fg.R, fg.G, fg.B));
        res["LayerFillColorAltBrush"] = B(panel);
        res["LayerFillColorDefaultBrush"] = B(panel);
        res["CardBackgroundFillColorDefaultBrush"] = B(panel);
        res["CardBackgroundFillColorSecondaryBrush"] = B(panel);
        res["ControlFillColorDefaultBrush"] = B(panel);
        res["ControlFillColorSecondaryBrush"] = B(panel);
        res["AcrylicInAppFillColorDefaultBrush"] = B(panel);
        res["AcrylicBackgroundFillColorDefaultBrush"] = B(panel);
        res["SolidBackgroundFillColorBaseBrush"] = B(bg);
        res["CardStrokeColorDefaultBrush"] = B(stroke);
        res["SubtleFillColorSecondaryBrush"] = B(panel);

        SetElementTheme(ElementTheme.Dark);
    }

    private void SetElementTheme(ElementTheme theme)
    {
        // Toggle to force ThemeResource references to re-resolve against the current resources.
        RootGrid.RequestedTheme = ElementTheme.Light;
        RootGrid.RequestedTheme = ElementTheme.Dark;
        RootGrid.RequestedTheme = theme;
    }

    private static Windows.UI.Color Rgb(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };

    private void ApplyClickMode()
    {
        // Single-click → ItemClick opens; double-click (default) → items select, double-tap opens.
        ExplorerIconsView.IsItemClickEnabled = _state.SingleClickToOpen;
        ExplorerDetailsList.IsItemClickEnabled = _state.SingleClickToOpen;
    }

    private void CloseSettings() => SettingsOverlay.Visibility = Visibility.Collapsed;

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        _state.SuppressSave = false;
        _state.Save();
        _settingsSnapshot = null;
        CloseSettings();
    }

    private void SettingsCancel_Click(object sender, RoutedEventArgs e) => CancelSettings();

    /// <summary>Reverts any live-applied edits to the pre-open snapshot and closes without saving.</summary>
    private void CancelSettings()
    {
        if (_settingsSnapshot is not null)
        {
            _state.CopySettingsFrom(_settingsSnapshot);
            _settingsSnapshot = null;
            ReapplyAllSettings();   // push the reverted values back to the live UI
        }
        _state.SuppressSave = false;
        CloseSettings();
    }

    /// <summary>Pushes the current <see cref="_state"/> values into the live app (theme, icon size,
    /// explorer flags, idle timer). Used to revert on Cancel.</summary>
    private void ReapplyAllSettings()
    {
        _iconSize = _state.IconSize is > 0 and <= 240 ? _state.IconSize : 110;
        _explorerViewMode = _state.ExplorerViewMode is "Large" or "Medium" or "Small" or "Details" ? _state.ExplorerViewMode : "Medium";
        _collagePreset = ParseCollagePreset(_state.CollagePreset);
        ExplorerItem.ShowFolderPreviews = _state.FolderPreviews;
        ExplorerItem.ShowExtensions = _state.ShowExtensions;
        ApplyTheme();
        ApplyClickMode();
        IconSizeSlider.Value = _iconSize;
        ApplyViewMode(); // restores Details vs icon view (and the View flyout check) on Cancel
        ResetVaultIdle();
        ApplyDeveloperMode();
        if (ExplorerView.Visibility == Visibility.Visible) LoadCurrentFolder();
    }

    // The X and the dim scrim both cancel (discard edits); a tap inside the card is swallowed.
    private void SettingsClose_Click(object sender, RoutedEventArgs e) => CancelSettings();
    private void SettingsScrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // A stray click 2px outside the card must not silently throw away pending edits — once
        // something changed, Save/Cancel (or Esc) are the explicit exits.
        if (_settingsSnapshot is not null && _state.Fingerprint() != _settingsSnapshot.Fingerprint()) return;
        CancelSettings();
    }

    private void SettingsCard_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void SlideshowSecondsSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SlideshowSeconds = (int)Math.Round(e.NewValue);
        SlideshowSecondsValue.Text = $"{_state.SlideshowSeconds}s";
        _state.Save();
    }

    private void ShuffleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SlideshowShuffle = ShuffleSwitch.IsOn;
        _state.Save();
    }

    private void LoopSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SlideshowLoop = LoopSwitch.IsOn;
        _state.Save();
    }

    private void TransitionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SlideshowTransition = (SlideshowTransition)Math.Max(0, TransitionCombo.SelectedIndex);
        _state.Save();
    }

    private void IconSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _iconSize = IconSizeCombo.SelectedIndex switch { 0 => 72, 2 => 160, _ => 110 };
        if (_explorerViewMode == "Details") { _explorerViewMode = "Large"; ApplyViewMode(); }
        IconSizeSlider.Value = _iconSize; // also updates _state.IconSize via the slider handler
        ApplyIconSize();
    }

    private void FolderPreviewSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.FolderPreviews = FolderPreviewSwitch.IsOn;
        ExplorerItem.ShowFolderPreviews = _state.FolderPreviews;
        _state.Save();
        LoadCurrentFolder(); // re-render icons with/without previews
    }

    private void ShowExtensionsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.ShowExtensions = ShowExtensionsSwitch.IsOn;
        ExplorerItem.ShowExtensions = _state.ShowExtensions;
        _state.Save();
        LoadCurrentFolder(); // re-render names
    }

    private void PeekSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.PeekEnabled = PeekSwitch.IsOn;
        _state.Save();
        if (!_state.PeekEnabled) ClosePeek();
    }

    private void AlbumArtSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.ShowAlbumArt = AlbumArtSwitch.IsOn;
        _state.Save();
    }

    private void StartMutedSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.StartVideoMuted = StartMutedSwitch.IsOn;
        _state.Save();
    }

    private void VaultIdleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.VaultIdleSeconds = VaultIdleCombo.SelectedIndex switch { 0 => 300, 1 => 600, 2 => 900, 3 => 1800, 4 => 3600, _ => 0 };
        _state.Save();
        ResetVaultIdle(); // apply immediately to an open vault
    }


    private void VaultHelloSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.VaultDefaultUseHello = VaultHelloSwitch.IsOn;
        _state.Save();
    }

    private void VaultWipeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        VaultWipeCountRow.Visibility = VaultWipeSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (_loadingSettings) return;
        _state.VaultWipeOnFailure = VaultWipeSwitch.IsOn;
        _state.Save();
    }

    private void VaultWipeCount_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingSettings || double.IsNaN(args.NewValue)) return;
        _state.VaultWipeAfterAttempts = Math.Clamp((int)args.NewValue, 1, 50);
        _state.Save();
    }

    private void HideVaultSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.HideVaultEntry = HideVaultSwitch.IsOn;
        _state.Save();
        RefreshVaults();
    }

    private void CollageLayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _collagePreset = (CollagePreset)Math.Max(0, CollageLayoutCombo.SelectedIndex);
        _state.CollagePreset = _collagePreset.ToString();
        _state.Save();
    }

    private static CollagePreset ParseCollagePreset(string? s) =>
        Enum.TryParse<CollagePreset>(s, out var p) ? p : CollagePreset.Justified;

    // ===================== Keyboard =====================

    /// <summary>Spacebar toggles video play/pause.</summary>
    private void ToggleVideoPlayPause()
    {
        var mp = VideoPlayer.MediaPlayer;
        if (mp is null) return;
        if (mp.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing) mp.Pause();
        else mp.Play();
    }

    /// <summary>Copies the current video frame (the on-screen video region) to the clipboard.</summary>
    private async Task CopyVideoFrameAsync()
    {
        try
        {
            var scale = VideoPlayer.XamlRoot?.RasterizationScale ?? 1.0;
            var pos = VideoPlayer.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            double w = VideoPlayer.ActualWidth, h = VideoPlayer.ActualHeight;
            if (w < 1 || h < 1) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var stream = await ScreenCapture.CaptureClientRectToPngStreamAsync(hwnd, pos.X, pos.Y, w, h, scale);
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(data);
            StatusText.Text = "Frame copied to clipboard";
        }
        catch (Exception ex) { StatusText.Text = "Copy frame failed: " + ex.Message; App.Log("CopyFrame", ex); }
    }

    /// <summary>Saves a screenshot to %USERPROFILE%\Pictures\Galileo. In the viewer it captures just the
    /// media (video frame or image) with the chrome/controls hidden; elsewhere the whole window.</summary>
    private async Task SaveScreenshotAsync()
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures", "Galileo");
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"Galileo_{DateTimeOffset.Now:yyyy-MM-dd_HH-mm-ss}.png");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // Video: decode the exact frame at the current position from the file — native resolution,
            // true aspect ratio, and none of the player chrome (screen-grabbing the element would catch
            // the transport controls and the letterbox bars).
            if (InVideo && !string.IsNullOrEmpty(_currentVideoPath) && File.Exists(_currentVideoPath)
                && !PhotoLibrary.IsAudio(_currentVideoPath) && FfmpegVideo.Available)
            {
                try
                {
                    await FfmpegVideo.SnapshotAsync(_currentVideoPath, CurrentVideoSeconds(), path);
                    StatusText.Text = "Frame saved: " + path;
                    FlashScreenshot();
                    return;
                }
                catch (Exception ex) { App.Log("Screenshot", ex); /* fall through to a region/window grab */ }
            }

            // Image in the viewer: just the image region (chrome hidden). Otherwise the whole window.
            FrameworkElement? media = (InViewer && !InVideo && ViewerImage.Source is not null) ? ViewerImage : null;
            var saved = media is { ActualWidth: >= 1, ActualHeight: >= 1 }
                ? await CaptureMediaOnlyAsync(hwnd, media, path)
                : await ScreenCapture.CaptureWindowAsync(hwnd, path);
            StatusText.Text = "Screenshot saved: " + saved;
            FlashScreenshot();
        }
        catch (Exception ex) { StatusText.Text = "Screenshot failed: " + ex.Message; App.Log("Screenshot", ex); }
    }

    /// <summary>A quick white edge flash confirming a screenshot was captured (like a camera).</summary>
    private void FlashScreenshot()
    {
        try
        {
            ScreenshotFlash.Visibility = Visibility.Visible;
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
            anim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(70), Value = 1 });
            anim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(450), Value = 0 });
            Storyboard.SetTarget(anim, ScreenshotFlash);
            Storyboard.SetTargetProperty(anim, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Completed += (_, _) => ScreenshotFlash.Visibility = Visibility.Collapsed;
            sb.Begin();
        }
        catch { ScreenshotFlash.Visibility = Visibility.Collapsed; }
    }

    /// <summary>Grabs only the media element's region, with the floating chrome and the player's transport
    /// bar hidden for the capture, then restores them.</summary>
    private async Task<string> CaptureMediaOnlyAsync(IntPtr hwnd, FrameworkElement media, string path)
    {
        var prevControls = VideoControlsBar.Visibility;
        var prevBack = VideoBackBar.Visibility;
        var prevChrome = ViewerChrome.Visibility;
        VideoControlsBar.Visibility = Visibility.Collapsed;
        VideoBackBar.Visibility = Visibility.Collapsed;
        ViewerChrome.Visibility = Visibility.Collapsed;
        try { VideoPlayer.TransportControls?.Hide(); } catch { }
        try
        {
            await WaitForRenderAsync();           // let the compositor present a frame without the chrome
            var scale = media.XamlRoot?.RasterizationScale ?? 1.0;
            var pos = media.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            return await ScreenCapture.CaptureClientRectToPngFileAsync(
                hwnd, pos.X, pos.Y, media.ActualWidth, media.ActualHeight, scale, path);
        }
        finally
        {
            VideoControlsBar.Visibility = prevControls;
            VideoBackBar.Visibility = prevBack;
            ViewerChrome.Visibility = prevChrome;
        }
    }

    /// <summary>Completes after <paramref name="frames"/> composition frames have rendered.</summary>
    private Task WaitForRenderAsync(int frames = 2)
    {
        var tcs = new TaskCompletionSource();
        var count = 0;
        void OnRendering(object? s, object e)
        {
            if (++count < frames) return;
            CompositionTarget.Rendering -= OnRendering;
            tcs.TrySetResult();
        }
        CompositionTarget.Rendering += OnRendering;
        return tcs.Task;
    }

    /// <summary>Explorer Ctrl+C/X/V/A. Registered with handledEventsToo=true so the GridView/ListView
    /// can't swallow these keys before our main KeyDown handler (which only sees unhandled events) runs.</summary>
    private void ExplorerClipboard_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (PeekOverlay.Visibility == Visibility.Visible) return;
        if (ExplorerView.Visibility != Visibility.Visible) return;
        // Let Ctrl+Alt+V (open vault) fall through to RootGrid_KeyDown — don't treat it as paste.
        if (!IsCtrlDown() || IsAltDown() || IsTextInputFocused()) return;
        switch (e.Key)
        {
            case VirtualKey.C: _ = CopySelectedExplorerAsync(cut: false); e.Handled = true; break;
            case VirtualKey.X: _ = CopySelectedExplorerAsync(cut: true); e.Handled = true; break;
            case VirtualKey.V: _ = PasteIntoCurrentAsync(); e.Handled = true; break;
            case VirtualKey.A: ActiveExplorerList().SelectAll(); e.Handled = true; break;
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // While the Peek overlay is open it owns the keyboard (handled in Peek_KeyDown) — don't let
        // the explorer/viewer shortcuts below also fire (e.g. Enter opening the item a second time).
        if (PeekOverlay.Visibility == Visibility.Visible) return;

        switch (e.Key)
        {
            // Ctrl+Alt+V: open a local vault, or browse what friends share with you.
            case VirtualKey.V when IsCtrlDown() && IsAltDown() && !IsTextInputFocused():
                OpenVaultShortcutAsync(); e.Handled = true; break;

            case VirtualKey.F5:
                if (InVideo) break; // a slideshow over a still-playing video would double the audio
                if (ExplorerView.Visibility != Visibility.Visible) StartSlideshow();
                else LoadCurrentFolder();
                e.Handled = true; break;

            // Shift+S: save a screenshot of the window (skip while typing).
            case VirtualKey.S when IsShiftDown() && !IsTextInputFocused():
                _ = SaveScreenshotAsync(); e.Handled = true; break;

            // ---- Video playback: space = play/pause, arrows = frame step (must precede the viewer cases) ----
            case VirtualKey.C when InVideo && IsCtrlDown():
                _ = CopyVideoFrameAsync(); e.Handled = true; break;
            case VirtualKey.Space when InVideo:
                ToggleVideoPlayPause(); e.Handled = true; break;
            case VirtualKey.Left when InVideo:
                VideoPlayer.MediaPlayer?.StepBackwardOneFrame(); e.Handled = true; break;
            case VirtualKey.Right when InVideo:
                VideoPlayer.MediaPlayer?.StepForwardOneFrame(); e.Handled = true; break;
            case VirtualKey.Delete when ExplorerView.Visibility == Visibility.Visible:
                _ = DeleteSelectedExplorerAsync(); e.Handled = true; break;

            // Explorer clipboard / select-all (Ctrl+C/X/V/A) are handled by ExplorerClipboard_KeyDown,
            // which is registered with handledEventsToo so the list control can't swallow them first.
            // Never in the bin view: renaming a GUID store file orphans its index entry (Restore can't
            // find it and the orphan sweep eventually deletes it) — the context menu already refuses.
            case VirtualKey.F2 when ExplorerView.Visibility == Visibility.Visible && !IsTextInputFocused()
                    && _currentFolder != RecycleBin.Location:
            {
                var sel = SelectedExplorerItems();
                var primary = FocusedExplorerItem() ?? sel.FirstOrDefault();
                if (sel.Count > 1 && primary is not null) _ = BulkRenameExplorerAsync(primary, sel);
                else if (sel.Count > 0) _ = RenameExplorerAsync(sel[0]);
                e.Handled = true; break;
            }
            case VirtualKey.Enter when ExplorerView.Visibility == Visibility.Visible && !IsTextInputFocused():
            {
                var sel = SelectedExplorerItems();
                if (sel.Count > 0) OpenExplorerItem(sel[0]);
                e.Handled = true; break;
            }

            case VirtualKey.Back when ExplorerView.Visibility == Visibility.Visible
                    && FocusManager.GetFocusedElement(RootGrid.XamlRoot) is not TextBox:
                NavBack_Click(sender, e); e.Handled = true; break;
            case VirtualKey.H when InViewer:
                ToggleObscure(); e.Handled = true; break;
            case VirtualKey.Left when InViewer:
                Navigate(-1); e.Handled = true; break;
            case VirtualKey.Right when InViewer:
                Navigate(+1); e.Handled = true; break;
            case VirtualKey.Escape when SettingsOverlay.Visibility == Visibility.Visible:
                CancelSettings(); e.Handled = true; break;
            case VirtualKey.D when InEditor && IsCtrlDown() && !IsTextInputFocused():
                ClearSelection(); e.Handled = true; break;      // Photoshop's Deselect
            case VirtualKey.Escape when InEditor:
                EditCancel_Click(this, new RoutedEventArgs());  // same unsaved-changes guard as Cancel
                e.Handled = true; break;
            case VirtualKey.Escape when InCollage:
                ShowExplorer(); e.Handled = true; break;
            case VirtualKey.Escape when InViewer:
                if (_isFullScreen) ToggleFullScreen();
                // A photo window ("open in new window", either in-process or via --new-window) is a
                // viewer, not a file manager: Esc closes it (unless "Close button returns to files"
                // says viewers should go back to the explorer instead).
                else if ((_secondaryWindow || LaunchedNewWindow()) && !_state.CloseToViewerBack) Close();
                else ShowExplorer();
                e.Handled = true; break;
            case VirtualKey.F11:
                ToggleFullScreen(); e.Handled = true; break;
            case VirtualKey.F when InViewer: // 'f' elsewhere is just typing (e.g. the address bar)
                ToggleFullScreen(); e.Handled = true; break;
            case VirtualKey.Add when InViewer:
            case (VirtualKey)187 when InViewer: // '='/'+'
                ZoomAt(1.25, HostCenter()); e.Handled = true; break;
            case VirtualKey.Subtract when InViewer:
            case (VirtualKey)189 when InViewer: // '-'
                ZoomAt(0.8, HostCenter()); e.Handled = true; break;
            case VirtualKey.Number0 when InViewer:
                ResetView(); e.Handled = true; break;
            case VirtualKey.R when InViewer:
                Rotate_Click(sender, e); e.Handled = true; break;
            case VirtualKey.Delete when InViewer:
                Delete_Click(sender, e); e.Handled = true; break;
        }
    }

    // ===================== Peek (Quick Look) =====================

    /// <summary>Extensions previewed as plain text/code in the Peek overlay.</summary>
    private static readonly HashSet<string> PeekTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml",
        ".ini", ".cfg", ".conf", ".config", ".toml", ".env", ".gitignore", ".gitattributes",
        ".cs", ".xaml", ".js", ".ts", ".jsx", ".tsx", ".html", ".htm", ".css", ".scss",
        ".py", ".java", ".c", ".cpp", ".h", ".hpp", ".go", ".rs", ".rb", ".php", ".sql",
        ".sh", ".bat", ".cmd", ".ps1", ".psm1", ".bib", ".tex"
    };

    private static bool IsTextPreviewable(string path) =>
        PeekTextExtensions.Contains(System.IO.Path.GetExtension(path));

    /// <summary>The explorer item under keyboard focus (the row the user is "on"), or the
    /// selected item if focus can't be resolved.</summary>
    private ExplorerItem? FocusedExplorerItem()
    {
        var node = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: ExplorerItem item }) return item;
            node = VisualTreeHelper.GetParent(node);
        }
        return SelectedExplorerItems().FirstOrDefault();
    }

    /// <summary>Global key hook (handledEventsToo): Space opens Peek; while open, Space/Esc close
    /// it and the arrows step through the folder.</summary>
    private void Peek_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        ResetVaultIdle(); // keyboard counts as activity for the vault idle timer

        if (PeekOverlay.Visibility == Visibility.Visible)
        {
            switch (e.Key)
            {
                case VirtualKey.Space:
                case VirtualKey.Escape:
                    ClosePeek(); e.Handled = true; break;
                case VirtualKey.Left:
                case VirtualKey.Up:
                    PeekNavigate(-1); e.Handled = true; break;
                case VirtualKey.Right:
                case VirtualKey.Down:
                    PeekNavigate(+1); e.Handled = true; break;
                case VirtualKey.Enter:
                {
                    var cur = _peekItem;
                    ClosePeek();
                    if (cur is not null) OpenExplorerItem(cur);
                    e.Handled = true; break;
                }
            }
            return;
        }

        // The OPEN gesture must respect e.Handled: this hook runs handledEventsToo, and Space on a
        // focused Button both invokes the button AND opened Peek on whatever row was selected.
        if (e.Handled) return;
        if (e.Key == VirtualKey.Space
            && ExplorerView.Visibility == Visibility.Visible
            && _state.PeekEnabled
            && !IsTextInputFocused()
            && FocusManager.GetFocusedElement(RootGrid.XamlRoot) is not Button and not ToggleButton)
        {
            var item = FocusedExplorerItem();
            if (item is not null && item.Kind != ExplorerItemKind.Drive)
            {
                OpenPeek(item);
                e.Handled = true;
            }
        }
    }

    private void OpenPeek(ExplorerItem item)
    {
        // Anchor the selection so arrow navigation has a starting index, then take focus off the
        // list (onto the overlay) so arrows drive Peek rather than moving the list underneath.
        ActiveExplorerList().SelectedItem = item;
        PeekOverlay.Visibility = Visibility.Visible;
        PeekOverlay.Focus(FocusState.Programmatic); // + TabFocusNavigation=Cycle keeps Tab inside
        ShowPeekFor(item);
    }

    private void ClosePeek()
    {
        if (PeekOverlay.Visibility != Visibility.Visible) return;
        _peekToken++;            // cancel any in-flight load
        StopPeekVideo();
        PeekImage.Source = null;
        PeekOverlay.Visibility = Visibility.Collapsed;
        _peekItem = null;
        ActiveExplorerList().Focus(FocusState.Programmatic); // hand focus back for continued nav
    }

    private void PeekNavigate(int delta)
    {
        var list = ActiveExplorerList();
        var count = list.Items.Count;
        if (count == 0) return;
        var cur = list.SelectedIndex;
        if (cur < 0) cur = _peekItem is not null ? list.Items.IndexOf(_peekItem) : 0;
        // Step over items Peek refuses to open (drives in This PC) instead of landing on them.
        var next = cur;
        do { next += Math.Sign(delta); }
        while (next >= 0 && next < count && list.Items[next] is ExplorerItem { Kind: ExplorerItemKind.Drive });
        next = Math.Clamp(next, 0, count - 1);
        if (list.Items[next] is ExplorerItem { Kind: ExplorerItemKind.Drive }) return; // nothing peekable that way
        if (next == cur && list.Items[next] == _peekItem) return;
        list.SelectedIndex = next;
        list.ScrollIntoView(list.Items[next]);
        if (list.Items[next] is ExplorerItem it) ShowPeekFor(it);
    }

    private async void ShowPeekFor(ExplorerItem item)
    {
        var token = ++_peekToken;
        _peekItem = item;
        PeekTitle.Text = item.Name;
        PeekInfo.Text = BuildPeekInfo(item);

        // Reset every content surface and release any playing video before loading the next item.
        StopPeekVideo();
        PeekImage.Source = null;
        PeekImage.Visibility = Visibility.Collapsed;
        PeekVideo.Visibility = Visibility.Collapsed;
        PeekTextScroller.Visibility = Visibility.Collapsed;
        PeekFallback.Visibility = Visibility.Collapsed;

        try
        {
            if (item.Kind == ExplorerItemKind.File && PhotoLibrary.IsSupported(item.Path))
            {
                var bmp = new BitmapImage();
                using (var s = await (await StorageFile.GetFileFromPathAsync(item.Path)).OpenReadAsync())
                {
                    if (token != _peekToken) return;
                    await bmp.SetSourceAsync(s);
                }
                if (token != _peekToken) return;
                PeekImage.Source = bmp;
                PeekImage.Visibility = Visibility.Visible;
            }
            else if (item.Kind == ExplorerItemKind.File && PhotoLibrary.IsMedia(item.Path))
            {
                var file = await StorageFile.GetFileFromPathAsync(item.Path);
                if (token != _peekToken) return;
                PeekVideo.Source = MediaSource.CreateFromStorageFile(file);
                PeekVideo.Visibility = Visibility.Visible;
                if (PeekVideo.MediaPlayer is { } peekMp)
                {
                    peekMp.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Movie;
                    // Honor the same remembered mute/volume as the main player — a peek must not
                    // blast full volume at someone who keeps videos muted.
                    peekMp.IsMuted = _state.VideoMuted || (!PhotoLibrary.IsAudio(item.Path) && _state.StartVideoMuted);
                    peekMp.Volume = Math.Clamp(_state.VideoVolume, 0, 100) / 100.0;
                }
                PeekVideo.MediaPlayer?.Play();
            }
            else if (item.Kind == ExplorerItemKind.File && IsTextPreviewable(item.Path))
            {
                var text = await ReadTextPreviewAsync(item.Path);
                if (token != _peekToken) return;
                PeekText.Text = text;
                PeekTextScroller.ChangeView(0, 0, null, true);
                PeekTextScroller.Visibility = Visibility.Visible;
            }
            else
            {
                await ShowPeekFallbackAsync(item, token);
            }
        }
        catch (Exception ex)
        {
            if (token != _peekToken) return;
            PeekFallbackImage.Source = null;
            PeekFallbackText.Text = $"Can't preview this file.\n{ex.Message}";
            PeekFallback.Visibility = Visibility.Visible;
        }
    }

    private async Task ShowPeekFallbackAsync(ExplorerItem item, int token)
    {
        PeekFallbackText.Text = string.IsNullOrEmpty(item.TypeName) ? "No preview available" : item.TypeName;
        PeekFallback.Visibility = Visibility.Visible;

        var (pixels, w, h) = await Task.Run(() => ShellImaging.GetPixels(item.Path, 256, iconOnly: false));
        if (pixels is null) (pixels, w, h) = await Task.Run(() => ShellImaging.GetPixels(item.Path, 256, iconOnly: true));
        if (token != _peekToken || pixels is null || w <= 0 || h <= 0) return;

        var wb = new WriteableBitmap(w, h);
        using (var st = wb.PixelBuffer.AsStream()) st.Write(pixels, 0, pixels.Length);
        PeekFallbackImage.Source = wb;
        PeekFallbackImage.Width = Math.Min(256, w);
        PeekFallbackImage.Height = Math.Min(256, h);
    }

    private static async Task<string> ReadTextPreviewAsync(string path)
    {
        const int maxBytes = 256 * 1024; // cap so a huge log doesn't freeze the UI
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenStreamForReadAsync();
        var len = (int)Math.Min(stream.Length, maxBytes);
        var buf = new byte[len];
        var read = await stream.ReadAsync(buf, 0, len);
        var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
        if (stream.Length > maxBytes) text += "\n\n… (truncated preview)";
        return text;
    }

    private static string BuildPeekInfo(ExplorerItem item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(item.TypeName)) parts.Add(item.TypeName);
        if (item.Kind == ExplorerItemKind.File && !string.IsNullOrEmpty(item.SizeText)) parts.Add(item.SizeText);
        if (!string.IsNullOrEmpty(item.ModifiedText)) parts.Add(item.ModifiedText);
        return string.Join("   ·   ", parts);
    }

    private void StopPeekVideo()
    {
        try
        {
            PeekVideo.MediaPlayer?.Pause();
            var previous = PeekVideo.Source as MediaSource;
            PeekVideo.Source = null;
            previous?.Dispose();
        }
        catch { /* ignore */ }
    }

    private void PeekScrim_Tapped(object sender, TappedRoutedEventArgs e) => ClosePeek();
    private void PeekCard_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true; // keep clicks on the card from closing
    private void PeekClose_Click(object sender, RoutedEventArgs e) => ClosePeek();

    private void PeekOpen_Click(object sender, RoutedEventArgs e)
    {
        var cur = _peekItem;
        ClosePeek();
        if (cur is not null) OpenExplorerItem(cur);
    }

    // ===================== Secure vault =====================

    private void RefreshVaults()
    {
        _vaultList.Clear();
        foreach (var v in _vaults.List())
            _vaultList.Add(new Models.VaultInfo(v.Id, v.Name, _vaults.Current?.Id == v.Id));

        // The vault list only appears once something is unlocked; otherwise a single discreet entry.
        // When "Hide vault from the sidebar" is on, NOTHING is shown — not even the currently-open vault;
        // the user reaches it with Ctrl+Alt+V (the command-strip Lock button still works to lock it).
        var open = _vaults.IsAnyUnlocked;
        VaultsSection.Visibility = (open && !_state.HideVaultEntry) ? Visibility.Visible : Visibility.Collapsed;
        VaultsLockedEntry.Visibility = (open || _state.HideVaultEntry) ? Visibility.Collapsed : Visibility.Visible;
        UpdateVaultLockButton();
    }

    private async void VaultsLockedEntry_Click(object sender, RoutedEventArgs e) => await ShowVaultPickerAsync();

    /// <summary>Locked-state entry point: lists vaults to unlock (or create one) without revealing
    /// them in the sidebar.</summary>
    private async Task ShowVaultPickerAsync()
    {
        var vaults = _vaults.List();
        var panel = new StackPanel { Spacing = 10, MinWidth = 280 };

        ListView? list = null;
        if (vaults.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No vaults yet. Create one to get started.",
                Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300, ItemsSource = vaults, DisplayMemberPath = "Name" };
            list.SelectedIndex = 0;
            panel.Children.Add(list);
        }

        var dlg = new ContentDialog
        {
            Title = "Vaults",
            Content = panel,
            PrimaryButtonText = vaults.Count > 0 ? "Unlock" : null,
            SecondaryButtonText = "New vault…",
            CloseButtonText = "Cancel",
            DefaultButton = vaults.Count > 0 ? ContentDialogButton.Primary : ContentDialogButton.Secondary,
            XamlRoot = RootGrid.XamlRoot,
        };

        Vault? chosen = null;
        if (list is not null)
        {
            list.DoubleTapped += (_, _) => { if (list.SelectedItem is Vault) { chosen = (Vault)list.SelectedItem; dlg.Hide(); } };
            dlg.PrimaryButtonClick += (_, args) =>
            {
                if (list.SelectedItem is Vault v) chosen = v; else args.Cancel = true;
            };
        }

        var result = await dlg.ShowAsync();
        if (chosen is not null) { await TryUnlockVaultAsync(chosen); return; }
        if (result == ContentDialogResult.Secondary) await CreateVaultDialogAsync(null);
    }

    private async void VaultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Models.VaultInfo vi) return;

        // Already unlocked → browse its decrypted working folder. Re-materialize it first if it went
        // missing/empty (otherwise it shows empty until a manual re-mount), then reload fresh.
        if (_vaults.Current?.Id == vi.Id && _vaults.Current.WorkingDir is not null)
        {
            ShowExplorer();
            try { await _vaults.EnsureCurrentWorkingAsync(); } catch (Exception ex) { App.Log("VaultEnsure", ex); }
            NavigateTo(_vaults.Current.WorkingDir);
            return;
        }

        Vault v;
        try { v = Vault.Load(System.IO.Path.Combine(VaultManager.VaultsRoot, vi.Id)); }
        catch (Exception ex) { StatusText.Text = "Couldn't open vault: " + ex.Message; return; }
        await TryUnlockVaultAsync(v);
    }

    private async void NewVault_Click(object sender, RoutedEventArgs e) => await CreateVaultDialogAsync(null);

    private void VaultsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Models.VaultInfo vi) return;
        var menu = new MenuFlyout();

        if (_vaults.Current?.Id == vi.Id)
        {
            var lockItem = new MenuFlyoutItem
            {
                Text = "Lock",
                Icon = new FontIcon { Glyph = char.ConvertFromUtf32(0xE72E), FontFamily = new FontFamily("Segoe Fluent Icons") },
            };
            lockItem.Click += (_, _) => _ = LockActiveVaultAsync();
            menu.Items.Add(lockItem);
        }

        var rename = new MenuFlyoutItem { Text = "Rename…", Icon = new SymbolIcon(Symbol.Rename) };
        rename.Click += async (_, _) => await RenameVaultAsync(vi);
        menu.Items.Add(rename);

        if (GoogleDriveBackup.IsConfigured)
        {
            var backup = new MenuFlyoutItem
            {
                Text = "Back up to Google Drive",
                Icon = new FontIcon { Glyph = char.ConvertFromUtf32(0xE753), FontFamily = new FontFamily("Segoe Fluent Icons") },
            };
            backup.Click += async (_, _) => await BackupSingleVaultAsync(vi.Id);
            menu.Items.Add(backup);
        }

        var target = (FrameworkElement)sender;
        menu.ShowAt(target, new FlyoutShowOptions { Position = e.GetPosition(target) });
        e.Handled = true;
    }

    private async Task RenameVaultAsync(Models.VaultInfo vi)
    {
        var box = new TextBox { Text = vi.Name, PlaceholderText = "Vault name" };
        box.Loaded += (_, _) => box.SelectAll();
        var dlg = new ContentDialog
        {
            Title = "Rename vault",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        dlg.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(box.Text)) args.Cancel = true; };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = box.Text.Trim();
        if (newName == vi.Name) return;
        try
        {
            // Use the live unlocked instance if it's the same vault, so its in-memory name updates too.
            var v = _vaults.Current?.Id == vi.Id
                ? _vaults.Current
                : Vault.Load(System.IO.Path.Combine(VaultManager.VaultsRoot, vi.Id));
            v.Rename(newName);
        }
        catch (Exception ex) { StatusText.Text = "Rename failed: " + ex.Message; return; }

        RefreshVaults();
        UpdateVaultLockButton();
        StatusText.Text = $"Vault renamed to “{newName}”.";
    }

    private async Task TryUnlockVaultAsync(Vault v)
    {
        var pw = new PasswordBox { PlaceholderText = "Passphrase" };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = $"Unlock “{v.Name}”",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(pw);

        var dlg = new ContentDialog
        {
            Title = "Unlock vault",
            Content = panel,
            PrimaryButtonText = "Unlock",
            SecondaryButtonText = v.HasHello ? "Windows Hello" : null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };

        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            StatusText.Text = "Unlocking…";
            VaultUnlockOutcome outcome;
            try { outcome = await _vaults.UnlockWithPassphraseAsync(v, pw.Password, _state.VaultWipeOnFailure, _state.VaultWipeAfterAttempts); }
            catch (Exception ex) { StatusText.Text = "Unlock failed: " + ex.Message; return; }

            switch (outcome)
            {
                case VaultUnlockOutcome.Success:
                    OnVaultOpened(v);
                    break;
                case VaultUnlockOutcome.Wiped:
                    RefreshVaults();
                    await new ContentDialog
                    {
                        Title = "Vault wiped",
                        Content = $"“{v.Name}” was permanently erased after {_state.VaultWipeAfterAttempts} incorrect attempts.",
                        CloseButtonText = "OK",
                        XamlRoot = RootGrid.XamlRoot,
                    }.ShowAsync();
                    break;
                default:
                    if (_state.VaultWipeOnFailure)
                    {
                        var left = Math.Max(0, _state.VaultWipeAfterAttempts - v.FailedAttempts);
                        StatusText.Text = $"Wrong passphrase — {left} attempt(s) left before this vault is wiped.";
                    }
                    else StatusText.Text = "Wrong passphrase.";
                    break;
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            StatusText.Text = "Waiting for Windows Hello…";
            bool ok;
            try { ok = await _vaults.UnlockWithHelloAsync(v); } catch { ok = false; }
            if (ok) OnVaultOpened(v); else StatusText.Text = "Windows Hello unlock failed.";
        }
    }

    private void OnVaultOpened(Vault v)
    {
        RefreshVaults();
        ResetVaultIdle();
        StartVaultFlush(); // commit working-folder changes continuously, not only on lock
        ShowExplorer();
        NavigateTo(v.WorkingDir);
        StatusText.Text = $"Vault “{v.Name}” unlocked";
    }

    private async Task CreateVaultDialogAsync(IList<string>? importPaths)
    {
        var suggested = importPaths is { Count: 1 }
            ? System.IO.Path.GetFileName(importPaths[0].TrimEnd('\\', '/'))
            : "";
        var name = new TextBox { PlaceholderText = "Vault name", Text = suggested };
        var pw = new PasswordBox { PlaceholderText = "Passphrase (min 8 characters)" };
        var pw2 = new PasswordBox { PlaceholderText = "Confirm passphrase" };
        var hello = new CheckBox { Content = "Also unlock with Windows Hello", IsChecked = _state.VaultDefaultUseHello };
        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
            Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, FontSize = 12,
        };
        var hint = new TextBlock
        {
            Text = "Your passphrase is the only recovery key — there is no reset. Hello is an optional convenience.",
            Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        // Passphrase strength meter (red → green bars + label), live as the user types.
        var bars = new Border[4];
        var barRow = new Grid { Height = 6, Margin = new Thickness(0, 2, 0, 0) };
        Brush Neutral() => new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.25 };
        for (var i = 0; i < 4; i++)
        {
            barRow.ColumnDefinitions.Add(new ColumnDefinition());
            var b = new Border { CornerRadius = new CornerRadius(3), Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0), Background = Neutral() };
            Grid.SetColumn(b, i);
            barRow.Children.Add(b);
            bars[i] = b;
        }
        var strengthLabel = new TextBlock { FontSize = 12, Opacity = 0.85 };
        void UpdateStrength()
        {
            if (pw.Password.Length == 0)
            {
                for (var i = 0; i < 4; i++) bars[i].Background = Neutral();
                strengthLabel.Text = "";
                return;
            }
            var (score, label, color) = EvaluatePassphrase(pw.Password);
            var brush = new SolidColorBrush(color);
            for (var i = 0; i < 4; i++) bars[i].Background = i < score ? brush : Neutral();
            strengthLabel.Text = label;
            strengthLabel.Foreground = brush;
        }
        pw.PasswordChanged += (_, _) => UpdateStrength();
        UpdateStrength();

        var panel = new StackPanel { Spacing = 10 };
        foreach (var c in new UIElement[] { name, pw, barRow, strengthLabel, pw2, hello, hint, error }) panel.Children.Add(c);

        var dlg = new ContentDialog
        {
            Title = importPaths is null ? "New vault" : "Move to new vault",
            Content = panel,
            PrimaryButtonText = importPaths is null ? "Create" : "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        dlg.PrimaryButtonClick += (_, args) =>
        {
            void Fail(string m) { error.Text = m; error.Visibility = Visibility.Visible; args.Cancel = true; }
            if (string.IsNullOrWhiteSpace(name.Text)) { Fail("Enter a vault name."); return; }
            if (pw.Password.Length < 8) { Fail("Use a passphrase of at least 8 characters."); return; }
            if (pw.Password != pw2.Password) { Fail("Passphrases don't match."); return; }
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        StatusText.Text = "Encrypting… this can take a moment for large folders.";
        try
        {
            await _vaults.CreateAsync(name.Text.Trim(), pw.Password, hello.IsChecked == true, importPaths);
        }
        catch (Exception ex) { StatusText.Text = "Vault creation failed: " + ex.Message; App.Log("VaultCreate", ex); return; }

        RefreshVaults();
        if (importPaths is not null) LoadCurrentFolder(); // originals were removed
        StatusText.Text = "Vault created.";
    }

    private bool ItemInsideOpenVault(ExplorerItem item) =>
        _vaults.Current?.WorkingDir is { } w
        && item.Path.StartsWith(w, StringComparison.OrdinalIgnoreCase);

    /// <summary>Encrypts the selected items into the open vault now (durable blob + visible in the
    /// working folder) and securely wipes the clear-space originals.</summary>
    private async Task SendToVaultAsync(IList<ExplorerItem> items)
    {
        var cur = _vaults.Current;
        if (cur?.WorkingDir is not { } work) { StatusText.Text = "Unlock a vault first."; return; }

        var paths = items.Select(i => i.Path)
            .Where(p => !string.IsNullOrEmpty(p) && !p.StartsWith(work, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (paths.Count == 0) return;

        StatusText.Text = "Encrypting into vault…";
        int n;
        try { n = await _vaults.AddToCurrentAsync(paths); }
        catch (Exception ex) { StatusText.Text = "Send to vault failed: " + ex.Message; App.Log("SendToVault", ex); return; }

        LoadCurrentFolder(); // originals are gone; new items appear if browsing the vault
        ResetVaultIdle();
        StatusText.Text = $"Sent {n} item(s) to vault “{cur.Name}” — encrypted, originals wiped.";
    }

    /// <summary>Scores a passphrase 1–4 from length + character-class variety, with a label and a
    /// red→green colour for the strength meter.</summary>
    private static (int score, string label, Windows.UI.Color color) EvaluatePassphrase(string pw)
    {
        var score = 0;
        if (pw.Length >= 8) score++;
        if (pw.Length >= 12) score++;
        if (pw.Length >= 16) score++;
        var classes = 0;
        if (pw.Any(char.IsLower)) classes++;
        if (pw.Any(char.IsUpper)) classes++;
        if (pw.Any(char.IsDigit)) classes++;
        if (pw.Any(c => !char.IsLetterOrDigit(c))) classes++;
        if (classes >= 2) score++;
        if (classes >= 3) score++;
        score = Math.Clamp(score, 0, 4);

        static Windows.UI.Color C(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
        return score switch
        {
            <= 1 => (1, "Weak", C(0xE7, 0x4C, 0x3C)),   // red
            2 => (2, "Fair", C(0xE6, 0x7E, 0x22)),       // orange
            3 => (3, "Good", C(0xF1, 0xC4, 0x0F)),       // amber
            _ => (4, "Strong", C(0x2E, 0xCC, 0x71)),     // green
        };
    }

    /// <summary>Creates a new vault from the selected items (encrypt + securely remove originals).</summary>
    private async Task MoveToNewVaultAsync(IList<ExplorerItem> items)
    {
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0) return;
        await CreateVaultDialogAsync(paths);
    }

    private void VaultLock_Click(object sender, RoutedEventArgs e) => _ = LockActiveVaultAsync();

    private async Task LockActiveVaultAsync()
    {
        var work = _vaults.Current?.WorkingDir;
        StopVaultIdle();
        StopVaultFlush();
        try { await _vaults.LockCurrentAsync(); }
        catch (Exception ex)
        {
            // A transient lock failure must not leave the vault unlocked with the idle timer stopped —
            // re-arm so it retries instead of staying open indefinitely.
            StatusText.Text = "Lock failed: " + ex.Message; App.Log("VaultLock", ex);
            ResetVaultIdle();
            return;
        }
        RefreshVaults();

        // If we were browsing/viewing inside the vault, leave it (its folder is now wiped).
        if (work is not null && (_currentFolder?.StartsWith(work, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            if (InViewer || InCollage) ShowExplorer();
            NavigateTo(null);
        }
        StatusText.Text = "Vault locked.";
    }

    private void UpdateVaultLockButton()
    {
        var open = _vaults.IsAnyUnlocked;
        VaultLockBtn.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        VaultLockText.Text = open && _vaults.Current is not null ? $"Lock “{_vaults.Current.Name}”" : "Lock vault";
    }

    // ---- Idle auto-lock ----

    private void ResetVaultIdle()
    {
        if (!_vaults.IsAnyUnlocked) return;
        var secs = _state.VaultIdleSeconds;
        _vaultIdleTimer.Stop();
        if (secs <= 0) return; // 0 = never auto-lock
        _vaultIdleTimer.Interval = TimeSpan.FromSeconds(secs);
        _vaultIdleTimer.Start();
    }

    private void StopVaultIdle() => _vaultIdleTimer.Stop();

    /// <summary>Begin continuous commits for a freshly-unlocked vault.</summary>
    private void StartVaultFlush() => _vaultFlushTimer.Start();

    private void StopVaultFlush() { _vaultFlushTimer.Stop(); _vaultFlushDebounce.Stop(); }

    /// <summary>Schedule a commit shortly after a working-folder change (coalesces bursts).</summary>
    private void ScheduleVaultFlush() { _vaultFlushDebounce.Stop(); _vaultFlushDebounce.Start(); }

    private void FlushVaultSoon()
    {
        if (_vaults.IsAnyUnlocked) _ = SafeFlushVaultAsync();
    }

    private async Task SafeFlushVaultAsync()
    {
        try { await _vaults.FlushCurrentAsync(); }
        catch (Exception ex) { App.Log("VaultFlush", ex); }
    }

    private void VaultIdle_Tick(object? sender, object e)
    {
        _vaultIdleTimer.Stop();
        if (!_vaults.IsAnyUnlocked) return;
        _ = LockActiveVaultAsync();
    }

    // ---- App-exit lock (re-encrypt + wipe before the window closes) ----

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // A volume change within the debounce window must not be lost to the close.
        if (_volSaveDebounce.IsEnabled) { _volSaveDebounce.Stop(); _state.Save(); }

        // Remember where the user keeps photo windows so the next one opens on the same spot/monitor.
        // Only a normal (restored) window — a maximized/fullscreen rect would be wrong to re-apply.
        if ((_secondaryWindow || LaunchedNewWindow()) && !_isFullScreen
            && (_appWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.State
                == Microsoft.UI.Windowing.OverlappedPresenterState.Restored)
        {
            _state.PhotoWinX = _appWindow.Position.X;
            _state.PhotoWinY = _appWindow.Position.Y;
            _state.PhotoWinW = _appWindow.Size.Width;
            _state.PhotoWinH = _appWindow.Size.Height;
            _state.Save();
        }

        // Unsaved edits (including AI, which rewrites pixels) must never be lost to the X. Cancel the close
        // synchronously — it's too late once we've awaited — then ask, and only re-close if they let us.
        // Tray → Exit must never be re-routed into a dialog on a hidden window (invisible modal = exit
        // blocked forever) — a deliberate exit wins over the ask.
        if (!_closingForVaultLock && !_exitingFromTray && InEditor && HasUnsavedEdits)
        {
            args.Cancel = true;
            if (await ConfirmLeaveEditorAsync())
            {
                ExitEditMode(reloadViewer: false, activateWindow: false);   // leaves the editor, so this branch won't re-trigger
                Close();
            }
            return;
        }

        // "Close button returns to files": while viewing a single photo/video, X acts like Back, not quit.
        // (Never on a tray Exit — that would swallow the exit on a hidden window.)
        if (!_closingForVaultLock && !_exitingFromTray && _state.CloseToViewerBack && InViewer)
        {
            args.Cancel = true;
            ShowExplorer();
            return;
        }

        // "Run in background": closing the window hides it to the tray and keeps the process (and the
        // secure-sharing host) alive. Real exit comes from the tray menu (sets _exitingFromTray).
        if (!_closingForVaultLock && !_exitingFromTray && _state.RunInBackground && _tray is not null)
        {
            args.Cancel = true;
            try { await _vaults.FlushCurrentAsync(); } catch { } // commit vault changes before going to the tray
            try { _appWindow.Hide(); } catch { }
            return;
        }

        if (_closingForVaultLock) return;       // second pass: cleanup already ran; let the close proceed
        try { _term?.Dispose(); _term = null; } catch { } // kill any terminal shell on close
        try { VideoPlayer.MediaPlayer?.Pause(); } catch { } // don't keep audio playing during a deferred close
        StopFolderWatch();
        RemoveTray();
        _backupTimer.Stop(); _driveWatcher.Stop();

        // Everything above is this window's own state. What follows is process-wide, and a guest window
        // ("open in new window", in-process OR spawned via --new-window) closing is not the app exiting —
        // the primary is still running. Wiping the shared temp root or locking the vault here would pull
        // them out from under it.
        if (_secondaryWindow || LaunchedNewWindow()) return;
        if (!_vaults.IsAnyUnlocked) return;
        args.Cancel = true;                      // defer close until the vault is secured
        try { await _vaults.LockCurrentAsync(); } catch (Exception ex) { App.Log("VaultCloseLock", ex); }
        _closingForVaultLock = true;
        Close();
    }
}
