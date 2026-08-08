# Polish review — 2026-08-08 (reviewed at `034e0bc`; burn-down completed same day)

**Status: 73 of 77 fixed.** Open: UI-5 (dead gallery — needs a product decision), P2-28 (won't fix — user
setting), P3-3 (deferred cosmetic), and the deferred halves of P1-27 (see its note).

Full functional + UI review. Five parallel review passes (explorer/file management, viewer/media/multi-window,
editor/AI, vault/sharing/security, UI/XAML); every finding below was verified against the code, and the
highest-severity items were independently re-verified. Ranked by severity; each entry has location, symptom,
and a fix sketch. Legend: `[ ]` open · `[~]` in progress · `[x]` fixed.

---

## P0 — Critical (data loss / security; fix first)

- [x] **P0-1 Drives are not excluded from Delete / Shift+Delete / Shred.** `MainWindow.xaml.cs`
  delete paths (`DeleteExplorerAsync` ~3596, `DeleteSelectedExplorerAsync` ~3630, `SecureShredAsync` ~3719) never
  filter `Kind == Drive` (copy at 3565 and bulk-rename at 3406 do). Selecting `E:\` in This PC and pressing
  Delete recycles-by-copy then **deletes the whole drive**; Shift+Delete/Shred secure-wipes it.
  *Fix:* filter drives (and any `Path.GetPathRoot(p) == p`) out of all delete/shred paths; guard again inside
  `RecycleBin.MoveToBin` and `SecureWipe`.

- [x] **P0-2 "Send to Vault" shreds originals even when their import FAILED.** `Vault.cs:355-398`
  (`AddToOpenVaultAsync`): per-source failures are swallowed (`catch { /* skip */ }` at 384), then the
  `deleteOriginals` loop wipes **all** sources (391-398) — including ones never encrypted. Disk-full or a locked
  source file ⇒ that file is securely erased and exists nowhere. Same pattern in `ImportPathsAsync` (296-350).
  *Fix:* collect successfully-imported sources inside the try; wipe only those; surface skipped ones.

- [x] **P0-3 Remote-browse path traversal (arbitrary file write).** `MainWindow.SecureSharing.cs:648,670`:
  `Path.Combine(dir, it.Name.Replace('/', sep))` uses wire-supplied names with no validation, then
  `Directory.CreateDirectory` + `File.Move(..., overwrite: true)`. A hostile/compromised peer can write
  outside the temp dir (e.g. Startup folder) ⇒ code execution. Host side validates (`Vault.SafeFull`); viewer
  side doesn't.
  *Fix:* reject any name whose `GetFullPath(Combine(dir, name))` doesn't start with `dir + separator`
  (also reject rooted names and `:`).

- [x] **P0-4 VaultManager is per-window ⇒ double-unlock wipes the live working folder.** `MainWindow.xaml.cs:104`
  (`private readonly VaultManager _vaults = new();`). A guest photo window can unlock the same vault again →
  `DecryptAllToWorkingAsync` starts by `WipeDirectory(work)` on the folder the primary is editing (uncommitted
  changes destroyed; two instances then fight over `index.enc`). Also: guest closes → no lock; primary closes
  seeing `IsAnyUnlocked == false` ⇒ process can exit leaving a decrypted working folder on disk.
  *Fix:* one process-wide manager (like `App.State`); hide vault UI in secondary windows; short-circuit
  re-unlock of an already-unlocked vault (see P1-16).

- [x] **P0-5 `Vault.LockAsync` wipes the working folder in `finally` even when the commit threw.**
  `Vault.cs:248-253`: if `SyncWorkingToBlobsAsync` fails (file open in another app, AV, disk full), the
  plaintext is wiped and the DEK zeroed anyway — everything since the last flush is unrecoverable.
  *Fix:* wipe only after a successful sync; on failure keep `WorkingDir`/`_dek` and propagate so the caller
  can retry/warn.

## P1 — Major functional

- [x] **P1-1 Video mute memory is erased before it's read** *(regression from `e4f5160`)*.
  `MainWindow.xaml.cs` `OpenVideoFromExplorer`: `VideoVolumeSlider.Value = remVol` fires `SliderChanged`
  **synchronously** (InVideo already true), which overwrites `_state.VideoMuted` from the volume before the
  next line reads it. Muted-at-40% is restored as unmuted-at-40%, and the mute memory is persisted away.
  *Fix:* snapshot `var remMuted = _state.VideoMuted;` before touching the slider; use `remMuted`.

- [x] **P1-2 "Start videos muted" contaminates the persisted state and mutes audio files**
  *(regression from `e4f5160`)*. The forced mute is folded into `_videoMuted` and then persisted into
  `_state.VideoMuted`, so after one video, MP3s start muted too.
  *Fix:* persist only the user's remembered mute; apply the forced mute at `mp.IsMuted` time only.

- [x] **P1-3 `RefreshFolderIncremental` desyncs `_explorerRaw` from `_explorerItems` ⇒ rename visibly reverts.**
  `MainWindow.xaml.cs:1864-1866` re-lists into new objects but `ReconcileExplorerItems` keeps the old ones —
  the two collections then hold different objects for the same paths. After any watcher tick/tab switch, F2
  rename → `ResortExplorerInPlace` reconciles against the stale raw list and **replaces the renamed item with a
  stale old-name entry**.
  *Fix:* adopt shown instances into `_explorerRaw` exactly as `RefreshFolderInPlace` does (~3352).

- [x] **P1-4 Recycle-bin index is non-atomic and unsynchronized.** `RecycleBin.cs:75-94`: store move happens
  before the read-modify-write of `index.json`; `Save()` swallows IO errors; per-instance lock only (second
  process = last-writer-wins); `RemoveMissing` hard-deletes orphans. Crash/error ⇒ file silently gone.
  *Fix:* journal entry before the move; cross-process `Mutex` + `File.Replace` for the index; never purge
  store files without an index record.

- [x] **P1-5 Deleting a vault file puts decrypted plaintext in the plain recycle bin.** Delete paths call
  `_bin.MoveToBin` with no `IsInCurrentVault` check (the convert path has one, ~3199). Plaintext survives in
  `RecycleBin\store` after the vault locks.
  *Fix:* same branch as convert: shred in place for vault paths.

- [x] **P1-6 Recursive search ignores `HiddenFolders` (and the hidden-items toggles).**
  `FileSystemService.Search` (90-118) doesn't take `showAppHidden`/`showWindowsHidden` and doesn't prune
  hidden branches ⇒ app-hidden ("privacy") folders and their files appear in search with working thumbnails,
  no Hello prompt.
  *Fix:* pass both flags into `Search`, prune `HiddenFolders` branches, mirror `IsWindowsHidden`.

- [x] **P1-7 Path-keyed state is never migrated on rename.** `HiddenFolders`, `FolderThumbnails`,
  `FolderSorts`, pins are keyed by path; `ExplorerItem.Rename` doesn't re-key them. Renaming a hidden folder
  **silently un-hides it** (and a future folder at the old path inherits the hidden flag).
  *Fix:* `AppState.RepathFolder(old, new)` re-keying all path-keyed sets (prefix-rewrite descendants), called
  from both rename paths.

- [x] **P1-8 FileTransfer fast-move: no in-batch destination guard, no fallback.** `FileTransfer.cs:109-129`:
  two same-named sources both plan `fastMove` to the same dest — second silently becomes `errors++` (no
  conflict dialog). `SameVolume` is `GetPathRoot`-based, so volume mount points/junctions plan a rename that
  always throws (no streamed-copy fallback).
  *Fix:* shared `plannedDests` for fast moves (auto-`UniquePath`); demote failed fast-moves into the copy list.

- [x] **P1-9 Clipboard `_suppressClipChange` is a fragile one-shot.** Zero `ContentChanged` events leaves the
  flag armed ⇒ the *next real external copy* is swallowed and Galileo pastes stale files; two events ⇒
  `_fileClip` nulled and a cut degrades to copy via the unreliable system-clipboard fallback.
  *Fix:* replace the bool with content matching — on `ContentChanged`, clear `_fileClip` only if the clipboard
  no longer holds the paths we put there (generation counter + compare).

- [x] **P1-10 Drag & drop defaults to MOVE even cross-volume.** `MainWindow.xaml.cs:4342`: Ctrl-only decision;
  dragging photos in from an SD card/USB **moves** them (deletes originals from the card), against the
  Windows convention (cross-volume drag = copy).
  *Fix:* default Copy when `!SameVolume(src, target)`, Move otherwise; Shift forces move, Ctrl forces copy.

- [x] **P1-11 Bulk rename can strand files as `__galileo_<guid>` with no recovery.** Phase-1 temp renames have
  no journal; any phase-2 failure (file open in viewer, ACL, crash) leaves files with meaningless
  extensionless names forever.
  *Fix:* journal `(temp → original)` before phase 1; restore on failure; startup sweep for leftover journals.

- [x] **P1-12 F2 / Ctrl+X inside the Recycle Bin corrupts the store.** Keyboard paths aren't gated on
  `RecycleBin.Location` (the context menu is): F2 renames the GUID store file so `Restore` can't find it and
  `RemoveMissing` eventually deletes it.
  *Fix:* guard F2/Ctrl+X/Ctrl+C on the bin view like the menu does.

- [x] **P1-13 `OpenViewerDirect` sibling backfill has no generation token.** Slow folder + quickly opening a
  second photo ⇒ the first backfill lands late, replaces `_allPhotos` with the wrong folder and resets
  `_currentIndex` to 0 (arrows navigate a different folder than the visible photo).
  *Fix:* generation token checked after the await (like `LoadCurrentAsync`).

- [x] **P1-14 Opening a Hidden-album photo shows the wrong photo (or an empty window).** Hidden photos still
  list in the explorer; both viewer entry paths reset `_showHiddenAlbum = false` and then `FindIndex == -1`
  → `Math.Max(0, -1)` shows **index 0** (a different photo); in a new window `_view` is empty → the photo
  window builds a file manager instead.
  *Fix:* force-include the requested path (or enable the hidden album) and never fall back to index 0 on a miss.

- [x] **P1-15 Slideshow: three defects.** `SlideshowWindow.xaml.cs` — (a) no generation token in `ShowAtAsync`
  ⇒ fast Next corrupts the A/B crossfade and skips slides; (b) timers never stopped on `Closed` (Alt+F4 ⇒
  ticking forever, decoding files, unobserved exceptions); (c) decodes full-resolution with no
  `DecodePixel*` cap ⇒ giant panoramas can crash the render thread (`0xc000027b`) — the main viewer caps at
  8000px for exactly this reason.
  *Fix:* token; `Closed += stop timers`; mirror the viewer's decode cap.

- [x] **P1-16 Re-unlocking an already-unlocked vault wipes its live working folder.** `VaultManager` (56)
  only locks when IDs differ; the picker lists fresh `Vault` objects incl. the unlocked one → re-unlock runs
  `WipeDirectory` on the live folder (uncommitted changes lost) and leaks the old DEK un-zeroed.
  *Fix:* short-circuit `Current?.Id == v.Id && IsUnlocked`; filter unlocked vaults from the picker.

- [x] **P1-17 Failed unlock leaves unowned plaintext + corrupt blob counts as a wrong passphrase.**
  `Vault.cs:183-188/447`: `WorkingDir` is only assigned at the END of decrypt-all, so a mid-decrypt failure
  leaves a plaintext folder nothing will wipe until next launch; a `CryptographicException` from a corrupt
  blob is misreported as a bad passphrase and **counts toward wipe-on-failure**.
  *Fix:* assign `WorkingDir` immediately; wipe + clear `_dek` on decrypt failure; distinguish KEK-unwrap
  failure from content-decrypt failure.

- [x] **P1-18 Secure wipe follows junctions/symlinks; cancel leaves zeroed files that look intact.**
  `SecureWipe.cs:50,134-139` (+ `VaultCrypto.WipeDirectory`): enumeration recurses through reparse points and
  overwrites the *targets*; cancelling mid-shred returns before the delete, leaving files with original
  name/size but destroyed contents.
  *Fix:* skip `ReparsePoint` entries; on cancel, delete any file with ≥1 overwritten byte and report it.

- [x] **P1-19 Backing up an unlocked vault can produce an unrestorable backup.** `GoogleDriveBackup.cs:144-170`:
  manual backup paths don't check `IsAnyUnlocked`; the 15s flush can delete/create blobs mid-upload; the
  uploaded `index.enc` can reference blobs never uploaded ⇒ restore silently loses files.
  *Fix:* flush + take the backup under `_syncGate` (or refuse while unlocked); upload `index.enc` last after
  verifying every referenced blob.

- [x] **P1-20 Tray "Exit" can be silently swallowed.** `AppWindow_Closing` `CloseToViewerBack` branch (~6716)
  and the unsaved-editor branch (~6704) don't check `_exitingFromTray`: exit from the tray while hidden ⇒
  close cancelled, invisible dialog can block exit forever, vault never locks.
  *Fix:* `&& !_exitingFromTray` on both; `Show()` before any closing confirm dialog.

- [x] **P1-21 Guest/`--new-window` windows still wipe shared state.** (a) `:6744` guards only
  `_secondaryWindow`, so an out-of-process `--new-window` viewer closing runs `WipeShareTempDirs()` —
  secure-wiping the primary's live remote-browse copies; (b) `_shell.WipeTemp()` at `:362` runs for guests
  too, deleting the primary's MTP temp copies.
  *Fix:* use `_secondaryWindow || LaunchedNewWindow()` for both (hoist an `IsPhotoWindow` property).

- [x] **P1-22 Process-global sort state bleeds between windows.** `_state.SortBy/SortDescending/GroupBy` act
  as "this window's live sort" but are process-wide: navigation in one window rewrites another window's
  next `SaveSortPrefsForCurrentFolder` with foreign values.
  *Fix:* live sort per window/tab; `_state` holds only the last-used default.

- [x] **P1-23 `state.json` writes are non-atomic and multi-process-unsafe.** `AppState.Save` is
  `File.WriteAllText`; with single-instance off, a second process's close overwrites the primary's changes
  (lost favorites), and a torn write makes `Load()` fall back to a **fresh default state** (everything gone).
  *Fix:* temp file + `File.Replace`, named mutex, reload-merge before write.

- [x] **P1-24 Editor: stale selection mask after Reset.** `EditReset_Click` (Editor.cs ~1176) doesn't
  `ClearSelection()` although Reset can change pixel dimensions (AI upscale revert) — overlay draws at the
  wrong scale, Fill errors, and the next lasso add/subtract silently degrades (mask length mismatch).
  *Fix:* `ClearSelection()` in Reset (and clear-with-notice on any dim change).

- [x] **P1-25 Video editor survives into image mode armed with a stale path.** `EnterImageMode` doesn't
  `CloseVideoEditor()`; `EditExport_Click` never checks `File.Exists(_currentVideoPath)` — the panel/filmstrip
  overlay the next photo and Export runs FFmpeg against a stale/deleted file.
  *Fix:* close the editor + null the path in `EnterImageMode`; `File.Exists` guards on export/save-frame.

- [x] **P1-26 Failed overwrite leaks `.galileo-tmp` that lists as a photo.** Editor.cs ~1290: export-failure
  branch never deletes the tmp; the doubled extension keeps `.jpg` so it appears in the gallery as a
  broken duplicate.
  *Fix:* delete tmp in that branch too (and/or use a non-image suffix + Hidden attribute).

- [~] **P1-27 Editor performance/memory cluster.** (a) Win2D effect graphs + `CanvasTextFormat` rebuilt and
  never disposed every Draw (slider drags leak native memory fast); (b) video frame server allocates a
  full-res GPU texture per frame (`CopyFrameToVideoSurface` could target `_lastFrame` directly);
  (c) undo+redo can pin ~6 full-res snapshots (~1 GB after AI upscales) — budget the two lists together;
  (d) `SetSelection` builds a full-resolution BGRA overlay (200 MB on 50 MP) on the UI thread per stroke —
  rasterize at display resolution; (e) `ExitEditMode` runs blocking
  `GC.Collect(); WaitForPendingFinalizers(); GC.Collect();` on the STA UI thread — freeze + deadlock hazard;
  drop it.
  **DONE:** (b) frame server reuses `_lastFrame`; (e) non-blocking GC; (a) partial — the leaked
  `CanvasTextFormat`s are hoisted to statics. **DEFERRED:** full effect-graph caching (a), the shared
  undo/redo byte budget (c), and display-resolution selection overlays (d) — invasive refactors of the
  render/undo pipeline, worth their own pass.

- [x] **P1-28 Editor re-activation timer can steal foreground from another app** *(from `9ed3db2`)*.
  The 700 ms `Activate()` re-assert fires even if the user Alt-Tabbed away (Visible ≠ foreground).
  *Fix:* only re-activate when the foreground window still belongs to this process
  (`GetForegroundWindow` vs our hwnds).

- [x] **P1-29 `AiSelectText_Click` lacks the `_aiGeneration` guard** — a slow text-detect finishing after the
  user switched photos installs photo A's mask on photo B. *Fix:* same generation check as `RunAiAsync`.

- [x] **P1-30 Deferred watcher refresh never consumed when returning via `ShowExplorer`** *(from `51ce10d`)*:
  the pending flag is only consumed on window activation with the explorer visible; re-entering the explorer
  in an already-active window misses it (stale listing until F5).
  *Fix:* consume `_pendingWatchRefresh` in `ShowExplorer()`; store the folder with the flag.

## P1 — Major UI

- [x] **UI-1 Light theme: title bar, filename/counter, status text and caption buttons are invisible in
  viewer/editor/collage.** Hardcoded dark backgrounds fill the window incl. behind the title bar, while text
  and caption glyphs follow the Light theme (near-black on black).
  *Fix:* force light-on-dark chrome colors while a dark full-bleed view is active; restore on `ShowExplorer`.

- [x] **UI-2 Privacy curtain leaks metadata.** The "Eye" black curtain renders *below* `ViewerChrome` and
  `InfoPanel`: filename stays in the title bar and the info panel keeps showing name/folder/EXIF while the
  image is "hidden".
  *Fix:* collapse InfoPanel + blank ModeLabel while obscured; reorder overlay above chrome.

- [x] **UI-3 Mica has no fallback** — no `MicaController.IsSupported()` check and `RootGrid.Background = null`;
  on Windows 10 / RDP / transparency-off the window has no base surface.
  *Fix:* fallback to `DesktopAcrylicBackdrop` or a solid theme brush.

- [x] **UI-4 Zero `AutomationProperties` app-wide** — ~50 icon-only buttons announce as "button", all sliders
  are unnamed (tooltips don't feed UIA Name). Screen-reader users can't use the app.
  *Fix:* `AutomationProperties.Name` mirroring tooltips; `Header=` on labelled sliders.

- [ ] **UI-5 `GalleryView` is dead UI.** `ShowGallery()` has no callers; Close-gallery's `EmptyState` line is a
  no-op; Select-mode/collage-from-selection are unreachable. Decide: delete it or give it an entry point.
  (Note: this moots the "editor exits to the wrong view when entered from the gallery" scenario — the only
  gallery entry to the editor is unreachable today.)
  **DEFERRED — needs a product decision** (delete the gallery, or give it an entry point). It's dead but
  harmless; removal touches connected animations and collage selection plumbing.

- [x] **UI-6 Explorer command strip drops commands at ordinary widths** — single non-wrapping StackPanel row;
  the `*` column (New folder/Slideshow/Collage/Share/Lock) collapses to zero first; no window MinWidth.
  *Fix:* `CommandBar` with overflow (or wrap + min size).

- [x] **UI-7 Editor command bar physically overlaps below ~660 px width** — three panels in one Grid cell
  aligned L/C/R; Save-As draws over Redo.
  *Fix:* real `Auto,*,Auto` columns.

- [x] **UI-8 Settings card: fixed 540 px width; height cap computed once at open** — shrink the window and
  Save/Cancel go off-screen (remaining exits discard changes, see UI-11).
  *Fix:* `MaxWidth` + re-cap on `SizeChanged`.

- [x] **UI-9 Viewer chrome fades under a stationary cursor and stays in tab order while invisible** —
  hover a button 3 s and it goes hit-test-invisible under you; keyboard focus walks 15 transparent buttons.
  *Fix:* hold the timer on `PointerEntered`; hide via `Visibility`/`IsTabStop`; `ShowChrome()` on focus.

- [x] **UI-10 Peek/Settings overlays aren't focus-modal** — Tab walks into the dimmed explorer behind the
  scrim (you can type into the address bar through the overlay).
  *Fix:* disable the underlying view while an overlay is up; `TabFocusNavigation="Cycle"` on the cards.

- [x] **UI-11 Scrim click discards all settings silently** — the card has explicit Save/Cancel, but a stray
  click 2 px outside reverts everything with no prompt.
  *Fix:* no-op (or confirm) on scrim tap when changes are pending.

## P2 — Minor

- [x] **P2-1 `ExplorerItem.Rename` doesn't refresh `TypeName`/icon** — rename `.txt`→`.jpg` keeps the TXT
  glyph and sorts/groups as TXT until a full reload. *Fix:* recompute TypeName + `ResetIcon()` when the
  extension changes.
- [x] **P2-2 Current folder deleted externally ⇒ permanently stale listing** (watcher tears itself down;
  incremental refresh returns early). *Fix:* navigate to nearest existing ancestor with a status note.
- [x] **P2-3 Ctrl+marquee wipes the pre-existing selection** — baseline the selection on press and don't
  remove items outside the marquee that were in the baseline.
- [x] **P2-4 Photo-window placement clamps X/Y but not W/H** — a 4K-sized window restored on a 1080p panel
  puts caption buttons off-screen. *Fix:* clamp W/H to the work area first. Also restore/save placement for
  out-of-process `--new-window` viewers (`_secondaryWindow || LaunchedNewWindow()`), matching the Esc rule.
- [x] **P2-5 Back from a directly-opened video lands on This PC** — `ShowExplorer` seeds from `Current?.Path`
  (null in video mode); use `_currentVideoPath` too.
- [x] **P2-6 F5 during video playback starts a slideshow on top of the playing audio** — guard `InVideo` or
  pause first.
- [x] **P2-7 Peek: Space on a focused button also opens Peek** (`handledEventsToo` ignores `e.Handled`);
  PeekNavigate steps onto drives it can't preview; PeekVideo ignores the remembered mute/volume.
- [x] **P2-8 Volume-save debounce not flushed on close** (change volume then close within 600 ms ⇒ lost).
- [x] **P2-9 Lasso combine semantics** *(from `7539983`)*: intersect with no prior selection should clear
  (it currently becomes a new selection); subtract-with-nothing silently deselects with no message; a mask
  length mismatch silently reinterprets the gesture. *Fix:* explicit per-mode null/mismatch handling + notice.
- [x] **P2-10 Filmstrip temp dir leaks when the video editor closes during thumbnail generation.**
- [x] **P2-11 Trim end not validated against start** ⇒ exports a 0.01 s clip as success.
- [x] **P2-12 Markup stroke width/text size baked from current zoom** — visually identical arrows export at
  wildly different weights depending on zoom at draw time. *Fix:* derive from image dimensions.
- [x] **P2-13 Remote vault deletes use plain `File.Delete`** (not `OverwriteAndDelete`); `_uploads` dictionary
  isn't thread-safe across peers; upload part names collide.
- [x] **P2-14 Drive restore silently overwrites a newer local vault; trusts remote names** — confirm
  overwrite; sanitize names.
- [x] **P2-15 Audit window: never closed by main close ⇒ headless process keeps polling relay; tray toggles
  from a guest window create a second tray icon.**
- [x] **P2-16 Drop onto MTP/device targets runs the filesystem engine** ("Copied 0, N failed") instead of
  routing to `_shell.Upload`.
- [x] **P2-17 FileTransfer conflict checks ignore directories at the destination** (`File.Exists` only) ⇒
  copy onto a same-named folder throws per-file instead of prompting.
- [x] **P2-18 Tab-switch suppression flag can desync TabView selection and history** (`_suppressNextTabSelection`
  armed after `_switchingTabs` cleared).
- [x] **P2-19 Search on This PC with no matches shows a blank pane** (empty-state requires a folder).
- [x] **P2-20 View-mode switch (Icons↔Details) drops the selection** — two ListViews, selection not copied over.
- [x] **P2-21 Truncated filenames/status have no tooltips anywhere** (StatusText is even hit-test-invisible).
- [x] **P2-22 Details columns: duplicated magic widths, no Name MinWidth** — name column collapses first.
- [x] **P2-23 Video chrome never auto-hides** (back/volume pills sit over the frame; photo chrome fades).
- [x] **P2-24 "Hide folder" tooltip goes stale** — says "Hide" while the button reads "Unhide".
- [x] **P2-25 Slideshow Prev/Play/Next lack tooltips; Play tooltip never flips; chrome hides via hard
  Collapse mid-hover.**
- [x] **P2-26 Transfer progress is a hand-drawn Border** — no automation RangeValue, hardcoded blue gradient
  ignores the Terminal/Gray themes. *Fix:* real `ProgressBar`.
- [x] **P2-27 Fixed pixel row heights clip at Windows text scaling ≥150%** (details rows, sidebar rows,
  icon captions). *Fix:* MinHeight + padding; measure captions.
- [ ] **P2-28 Hide-to-tray flushes but never locks the vault** — with idle-lock off, decrypted working folder
  stays on disk indefinitely while the app "looks closed".
  **WON'T FIX (by design):** idle-lock = 0 is an explicit user choice ("never auto-lock"), and locking on
  hide would break background sharing. The vault idle timer still applies when enabled.

## P3 — Polish

- [x] **P3-1 Icon vocabulary collisions** — same glyph for Collage and View-mode; the two adjacent
  hidden-items toggles use opposite eye metaphors; the Eye flyout inverts the app's own convention.
- [x] **P3-2 `FolderSorts` grows forever** (dead entries never pruned).
- [ ] **P3-3 Status/hint channels (StatusText vs AiSay) can overwrite each other in the editor.**
  **DEFERRED** — cosmetic; needs a small message-priority design, not a one-line fix.

---

### Verified-correct notes (checked, no action)
`LoadCurrentAsync`/`ShowPeekFor`/`LoadAlbumArtAsync` token races; `OpenViewerDirect` ordering logic (sort +
group + `byPath` re-order) matches the explorer exactly; `{Binding}` templates are OneWay (rename updates
propagate); all 41 ContentDialogs set `XamlRoot`; custom theme resource overrides apply and clean up;
`_editLoadToken`/`_editLoadGate` serialization; AI session use/release serialization; `StopVideo`/
`StopPeekVideo` dispose their MediaSources; guest windows correctly skip vault-orphan wipes on startup.
