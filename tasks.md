# Galileo (internally "Galileo") — Task Breakdown

Phased plan to reach Windows Photos parity plus the **eye hide/un-hide toggle** and **slideshow** features.

Legend: `[ ]` todo · `[~]` in progress · `[x]` done

---

## ✅ Build status (current)

**The app compiles cleanly** (`dotnet build`, 0 warnings / 0 errors) and runs as an unpackaged WinUI 3 desktop app with a modern, redesigned UI. Implemented and working:

**Open & gallery**
- Open **file**, **folder**, or **drag-and-drop** (single image / multiple images / folder) → virtualized gallery grid with async thumbnails, favorite ★ + hidden badges.
- Reopens last folder on launch; **Close gallery** clears it.

**Viewer**
- **Fit-to-screen by default** — any photo scaled up/down to be fully visible; re-fits on resize.
- **Mouse-wheel zoom toward the cursor** (transform-based viewer — no ScrollViewer fighting the wheel), plus +/- buttons, double-tap, and **drag-to-pan** when zoomed.
- **Rotate** that auto-re-fits and re-centres (90°/270° scaled down so the image stays fully visible).
- Next / prev / full screen.

**Headline features**
- **Eye toggle** (`H` / eye icon): covers the current photo with a **solid black** curtain; flyout → permanent hide into a gated **Hidden album**.
- **Slideshow** (`F5`): full screen, crossfade / Ken Burns, timing, shuffle, loop, auto-hiding controls, caption, skips hidden photos.
- **Collage**: fit-to-screen auto-layout with **Justified / Grid / Hero** presets; pick photos via gallery multi-select or **drag-and-drop into the collage**; shuffle, count stepper, save-to-PNG, tap a tile to open it.

**Settings & UI**
- **Settings panel** — slideshow seconds (2–30), shuffle, loop, transition; persisted.
- **Modern UI** — Mica backdrop, extended title bar (no command bar), floating auto-hiding control pills, rounded thumbnails, hero empty-state.

**Other** — favorites + filter, metadata panel, delete-to-Recycle-Bin, reveal in Explorer; JSON persistence in `%LocalAppData%\Galileo`.

**Right-click context menu** (viewer + gallery) — copy image / copy as file / copy path, Open with…, Print…, set as desktop background, favorite, hide, rename, show in Explorer, delete, native Windows Properties dialog.

**Crash logging** — global exception handlers write to `%LocalAppData%\Galileo\logs\error.log`; viewer caps decode size (8000px) to avoid GPU-texture-limit crashes on huge images.

**File Explorer (home, phase 1)** — Win11-style sidebar (This PC / Quick access / drives), back/fwd/up + breadcrumb + editable address, Large/Medium/Small icon views with a size slider + Details (name/date/type/size), shell thumbnails for all file types, open files/folders, New folder / Copy / Copy path / Paste / Rename / Delete / Properties, Slideshow/Collage on the current folder. **Hide-folder**: app-hidden folders appear empty when opened, excluded/dimmed in parent, Show-app-hidden toggle, reversible, disk untouched (`AppState.HiddenFolders`). _Next: cut/move, drag-drop between folders, Details sorting, search, expandable folder tree._

**Build notes (csproj / project):**
- `<WindowsSdkPackageVersion>10.0.19041.38</WindowsSdkPackageVersion>` — WindowsAppSDK 1.6 needs SDK.NET.Ref ≥ `.38`; .NET 8.0.300 ships `.31`.
- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` — CsWinRT AOT generator emits unsafe code for generic WinRT calls (drag-drop's `GetStorageItemsAsync`).
- Shared XAML styles live in **`App.xaml`**, not `Window.Resources` — the WinUI 1.6 markup compiler hard-crashes on a `Style` in `Window.Resources`.
- Close the running app before rebuilding (the `.exe` is locked while running → `MSB3021`).

Still TODO from the roadmap below: editing (Phase 5), shell/default-app registration (Phase 6), video (Phase 7), MSIX packaging & DI host (Phases 0/8), `.Core`/`.Tests` split, and automated tests.

---

## Phase 0 — Project setup

- [~] WinUI 3 (.NET 8) app project (single `Galileo.App`; `.Core`/`.Tests` split still TODO).
- [~] Packages: `CommunityToolkit.Mvvm` added (DI host / Win2D / SQLite not used — JSON state instead).
- [~] MVVM + app theme: observable models + **Mica / dark-light** done; no DI host.
- [ ] Set up CI (build + test) and linting/formatting (`dotnet format`).
- [ ] MSIX packaging project with photo file-type associations declared.

## Phase 1 — Core viewer (parity)

- [x] Image decode (JPEG, PNG, GIF, BMP, TIFF, WEBP, HEIC, AVIF, RAW via platform codecs).
- [x] Viewer control: zoom (wheel/+/-, double-tap), pan, fit-to-window. _(1:1 via zoom)_
- [x] Rotate (auto-fit). _(flip: TODO)_
- [x] Animated GIF playback.
- [x] Next/previous navigation within a folder. _(neighbor preload: TODO)_
- [x] Open single file and open folder (+ drag-and-drop). _(file association / "Open with": TODO)_
- [x] Full-screen mode (F11/F).

## Phase 2 — Library & collections (parity)

- [x] Folder/file picker + persisted last folder.
- [x] Async thumbnail pipeline (virtualized, on-realization) with an on-disk cache (raw-BGRA, mtime-keyed, 256 MB LRU; vault/bin paths excluded).
- [x] Gallery grid with virtualization for large folders. _(adjustable thumbnail size: TODO)_
- [x] Favorites (★) stored in local state + "Favorites only" filter.
- [ ] "Recent" and "All photos" virtual collections.
- [ ] Search by filename; filters by date and folder.
- [x] Metadata panel (dimensions, size, dates, camera).

## Phase 3 — ⭐ Eye toggle: hide / un-hide current photo

- [x] Add **eye icon** to the viewer command bar with stateful glyph (eye ⇄ eye-off) and tooltip.
- [x] **Obscure mode:** toggle replaces the displayed image with a "hidden" privacy overlay; toggle again to reveal. Bound to shortcut **H**.
- [x] Ensure obscure is view-only — never moves, edits, or deletes the original file.
- [x] **Persistent hide:** "Hide permanently" flags the image in the local index; excluded from gallery.
- [x] **Hidden album** view that lists hidden images; itself gated behind the eye toggle.
- [ ] Optional **Windows Hello** gate before revealing the Hidden album.
- [ ] Hidden-count badge on the eye icon.
- [x] Persist hidden state across sessions via sidecar index (no original-file mutation).
- [ ] Unit tests: toggle state machine, persistence round-trip, gallery/search exclusion.

## Phase 4 — ⭐ Slideshow

- [x] Slideshow service: ordered/shuffled queue from current folder/album/selection.
- [x] Full-screen slideshow shell on the active monitor.
- [x] Per-slide duration (2–30 s) and loop toggle.
- [x] Transitions: none, crossfade, **Ken Burns** (slow zoom/pan). _(slide: TODO)_
- [x] Controls overlay (auto-hide): play/pause (Space), prev/next (←/→), speed, exit (Esc).
- [x] Caption (filename + position).
- [ ] Optional background music (audio file/playlist).
- [x] **Skip hidden images** in slideshows (cooperate with Phase 3).
- [x] Launch entry points: command bar button + **F5**.
- [ ] Unit/UI tests: queue ordering, shuffle, hidden-skip, timing.

## Phase 5 — Editing (parity)

- [ ] Non-destructive edit pipeline with "Save a copy".
- [ ] Crop & straighten; aspect-ratio presets.
- [ ] Adjustments: brightness, contrast, exposure, saturation, warmth, auto-enhance.
- [ ] Filters.
- [ ] Red-eye and spot fix.
- [ ] Markup / ink draw.

## Phase 6 — Actions & shell integration (parity)

- [ ] Print.
- [ ] Share via Windows share sheet.
- [ ] Copy to clipboard.
- [ ] Set as background / lock screen.
- [x] Delete to Recycle Bin; reveal in Explorer. _(rename: TODO)_
- [x] **File activation** — opens a file/folder passed on the command line ("Open with" / default app).
- [x] **Register as default photo handler** — per-user registry scripts (`tools\register-default.ps1` / `unregister-default.ps1`) + publish to `%LocalAppData%\Galileo\app`. _(user sets the actual default in Settings; jump list: TODO; single-instance: TODO)_

## Phase 7 — Video (parity)

- [ ] Play/pause/scrub common video formats.
- [ ] Basic trim; export frame.

## Phase 8 — Polish & release

- [~] Settings panel: **slideshow seconds / shuffle / loop / transition** done. _(theme, default folder, Hello gate, eye behavior: TODO)_
- [x] Modern UI redesign: Mica, extended title bar, floating auto-hiding controls, removed command bar.
- [~] Keyboard shortcuts implemented. _(full accessibility pass: narrator/high-contrast/keyboard nav TODO)_
- [~] Performance pass: neighbor preload + huge-folder icon release + disk thumb cache done; cold-open timing logged (further tuning open).
- [ ] Localization scaffolding.
- [ ] App icon, store assets.
- [ ] Package & sign MSIX; release notes.

---

## Milestones

1. **M1 – Viewable:** Phases 0–1 (open and view any supported image).
2. **M2 – Library + Eye:** Phases 2–3 (gallery, favorites, **eye hide/un-hide**).
3. **M3 – Slideshow:** Phase 4.
4. **M4 – Editor + Actions:** Phases 5–6.
5. **M5 – 1.0:** Phases 7–8.

## Open questions / decisions

- ~~"Hide" default behavior~~ → **Resolved:** eye = solid black-out by default; permanent hide via flyout.
- ~~Storage engine~~ → **Resolved:** JSON app-state for now (SQLite/LiteDB only if the library grows large).
- RAW/HEIC codec coverage — currently relies on the OS-installed codec; bundle/guide install of Microsoft Raw/HEIF extensions?
- Editing scope for Phase 5 — minimal (crop/rotate/adjust) vs full parity (filters/markup/red-eye)?
- License choice (README suggests MIT).
