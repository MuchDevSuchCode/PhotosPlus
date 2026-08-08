using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Galileo.Models;
using Galileo.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;

namespace Galileo;

/// <summary>Image editor overlay: a Win2D live preview driven by an <see cref="EditState"/>, with
/// transform/adjust/filter/markup tools and copy/as/overwrite saving. Originals stay untouched
/// unless the user explicitly overwrites.</summary>
public sealed partial class MainWindow
{
    private readonly ImageEditor _editor = new();
    private CanvasControl? _editCanvas;
    private EditState _edit = new();
    private string? _editPath;
    /// <summary>One undo entry. Slider/filter/crop edits only need the parameters; markup is its own list,
    /// and the AI models rewrite pixels, so those entries also carry the bitmap that was there before —
    /// otherwise Undo would restore the old settings onto the new pixels and appear to do nothing.</summary>
    private sealed record EditSnapshot(EditState State, List<MarkupItem> Markup, byte[]? Pixels, int W, int H);

    private readonly List<EditSnapshot> _editUndo = new();   // list, not Stack: we prune old pixel snapshots
    private readonly List<EditSnapshot> _editRedo = new();

    /// <summary>A full-resolution snapshot is ~4 bytes/px (100 MB+ on a big photo), so only the most recent
    /// few AI steps keep theirs; older entries degrade to parameters-only rather than pinning gigabytes.</summary>
    private const int MaxPixelSnapshots = 3;
    private readonly List<MarkupItem> _markup = new();

    private bool _editLoading;
    private long _lastEditChangeTick;
    private int _editLoadToken;                          // supersedes an in-flight open so the wrong image can't appear
    private readonly SemaphoreSlim _editLoadGate = new(1, 1); // serialize loads into the shared editor

    private Rect _editFitRect;      // where the image is drawn in the canvas (display space)
    private double _editFitScale = 1;
    private double _orientedW, _orientedH;

    private bool _cropMode;
    // Preview zoom: a multiplier on the fit-to-window scale (1 = fit), plus a display-space pan offset.
    private double _editZoom = 1.0;
    private double _editPanX, _editPanY;
    private bool _editPanning;
    private Point _editPanStart;
    private double _editPanStartX, _editPanStartY;
    // Before/after comparison: "off" | "split" (draggable divider) | "side" | "original".
    private string _compareMode = "off";
    private double _compareSplit = 0.5;
    private bool _compareDragging;
    private string _markupTool = "";
    // Lasso selection (oriented-image space). Freehand while dragging; closed on release.
    private bool _lassoMode;
    private readonly List<Point> _lasso = new();
    private bool _lassoDrawing;
    // Photoshop-style combine mode, captured from the modifier keys when the drag STARTS:
    // plain = new selection, Shift = add, Alt = subtract, Shift+Alt = intersect.
    private string _lassoCombine = "new";

    // The actual selection: a mask in RAW SOURCE pixels. Both the lasso and "Select text" produce one, so
    // Fill has a single input regardless of how the selection was made. _selOverlay is just its visual.
    private byte[]? _selMask;
    private CanvasBitmap? _selOverlay;

    /// <summary>Adopts a source-space selection mask and builds the tinted overlay shown on the canvas.</summary>
    private void SetSelection(byte[]? mask, int w, int h)
    {
        _selMask = mask;
        try { _selOverlay?.Dispose(); } catch { }
        _selOverlay = null;

        if (mask is not null && _editCanvas is not null)
        {
            var px = new byte[w * h * 4];
            for (var i = 0; i < mask.Length; i++)
            {
                if (mask[i] == 0) continue;
                var p = i * 4;
                px[p] = 255; px[p + 1] = 40; px[p + 2] = 190;   // BGRA — magenta
                px[p + 3] = 90;                                  // translucent
            }
            try
            {
                _selOverlay = CanvasBitmap.CreateFromBytes(_editCanvas.Device, px, w, h,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            }
            catch (Exception ex) { App.Log("SelOverlay", ex); }
        }
        UpdateLassoUi();
        _editCanvas?.Invalidate();
    }

    private void ClearSelection()
    {
        _lasso.Clear();
        _lassoDrawing = false;
        SetSelection(null, 0, 0);
    }
    private double _cropAspect;     // 0 = free
    private bool _dragging;
    private Point _dragStart;       // oriented-image space
    private Rect? _pendingCrop;
    private string _cropDrag = "";   // "", new, move, nw/ne/sw/se (corners), n/s/e/w (edges)
    private Rect _cropAtDragStart;
    private Rect _viewSrc;          // oriented-space region currently shown on the canvas
    private MarkupItem? _pendingShape;

    // Cached render of the (expensive) color+orientation pipeline. Crop/markup dragging blits this instead
    // of re-running the full-resolution effect graph every pointer frame — without it, dragging a crop on a
    // large photo re-renders the whole image dozens of times a second and the tool appears to freeze.
    private CanvasRenderTarget? _editCache;
    private bool _editCacheDirty = true;

    /// <summary>A pixel-affecting edit (adjust/filter/rotate/flip/straighten/undo/reset) changed — drop the
    /// cached render and repaint. Crop and markup don't change pixels, so they must NOT call this.</summary>
    private void InvalidateEditImage() { _editCacheDirty = true; _editCanvas?.Invalidate(); }

    // ---- enter / exit ----

    private async void Edit_Click(object sender, RoutedEventArgs e) => await EnterEditModeAsync();

    private async Task EnterEditModeAsync(PhotoItem? item = null)
    {
        item ??= Current ?? _contextItem;
        if (item is null || !PhotoLibrary.IsSupported(item.Path)) { StatusText.Text = "This file can't be edited."; return; }

        var token = ++_editLoadToken;   // any earlier open is now stale
        var path = item.Path;

        // Reset ALL editor state up front so nothing carries over from a previous image — including any AI
        // job still running on the PREVIOUS photo, whose result must not land on this one.
        AbortAiWork();
        _editPath = path;
        _editLoading = true;
        _edit = new EditState();
        _editUndo.Clear();
        _editRedo.Clear();
        _markup.Clear();
        _orientedW = _orientedH = 0; _editFitScale = 1; _editFitRect = default; _viewSrc = default;
        _dragging = false; _pendingCrop = null; _cropDrag = ""; _cropAtDragStart = default; _pendingShape = null;
        _cropAspect = 0;
        _compareMode = "off"; _compareSplit = 0.5; _compareDragging = false;
        _editZoom = 1.0; _editPanX = _editPanY = 0; _editPanning = false;
        if (EditZoomLabel is not null) EditZoomLabel.Content = "Fit";
        _lassoMode = false;
        ClearSelection();
        InvalidateLiveDenoise();
        try { _editCache?.Dispose(); } catch { } _editCache = null; _editCacheDirty = true; // fresh image → fresh cache
        ResetEditSliders();
        _editLoading = true;
        if (CropAspectCombo.Items.Count > 0) CropAspectCombo.SelectedIndex = 0;
        if (CompareCombo.Items.Count > 0) CompareCombo.SelectedIndex = 0;
        AiStatus.Text = "";
        _lastEditChangeTick = 0;   // a fresh image starts a fresh debounce window
        _editLoading = false;
        SetCanvasMode("none");

        if (_editCanvas is null)
        {
            // Created in code (not XAML) and sharing the editor's device so effects/bitmaps match.
            _editCanvas = new CanvasControl { UseSharedDevice = true };
            _editCanvas.Draw += EditCanvas_Draw;
            EditCanvasHost.Children.Insert(0, _editCanvas);
        }

        // Serialize loads into the shared editor; a newer open (token) skips this one entirely so it can
        // never clobber the bitmap or pop the wrong image into view.
        await _editLoadGate.WaitAsync();
        try
        {
            if (token != _editLoadToken) return;
            try { await _editor.LoadAsync(path); }
            catch (Exception ex)
            {
                if (token == _editLoadToken) { _editLoading = false; StatusText.Text = "Couldn't open for editing: " + ex.Message; }
                App.Log("Editor", ex);
                return;
            }
            if (token != _editLoadToken) return; // superseded while loading

            ViewerView.Visibility = Visibility.Collapsed;
            EditorView.Visibility = Visibility.Visible;
            UpdateChromeForDarkSurface();
            _editLoading = false;
            _editCanvas?.Invalidate();
        }
        finally { _editLoadGate.Release(); }
    }

    private void ExitEditMode(bool reloadViewer, bool activateWindow = true)
    {
        EditorView.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Visible;
        UpdateChromeForDarkSurface();
        try { _editCache?.Dispose(); } catch { } _editCache = null; _editCacheDirty = true;

        // Stop any in-flight AI first and invalidate its result — otherwise disposing a session later would
        // pull it out from under a worker thread (a native AccessViolation, not a catchable exception), and
        // a late-finishing job would write its pixels into a closed editor.
        AbortAiWork();

        // Don't drop the models immediately: hopping out to the viewer and back would then reload CodeFormer
        // (360 MB) every time. They're released after a spell of not using AI at all, so the memory still
        // comes back once you've genuinely moved on.
        ScheduleModelRelease();
        _editUndo.Clear();   // undo entries can hold full-resolution bitmaps
        _editRedo.Clear();
        InvalidateLiveDenoise();   // the pair holds two full-resolution buffers
        try { _selOverlay?.Dispose(); } catch { }
        _selOverlay = null;
        _selMask = null;
        try { _editor.Unload(); } catch { }
        // The big buffers (undo pixels, denoise pair, editor bitmaps) are already unrooted above. A
        // background hint reclaims them without blocking the UI thread — WaitForPendingFinalizers here
        // can deadlock against WinRT finalizers that must marshal back to this STA thread.
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);

        if (reloadViewer) _ = LoadCurrentAsync();

        // Leaving the editor must land the user back on THIS window's viewer. Saving over the original
        // reshuffles the main explorer window's list (same process, same UI thread), which can pull the
        // foreground to it — re-assert activation so the viewer the user was working in stays on top.
        // Skipped when the window is closing (activating a dying window just flashes it), and skipped
        // whenever the foreground belongs to ANOTHER process — the user Alt-Tabbed away, and reclaiming
        // focus from a different app would be focus stealing, not restoration.
        if (activateWindow)
        {
            if (ForegroundIsThisProcess()) { try { Activate(); } catch { } }
            // The explorer refresh is debounced (~400ms after the file lands) and can steal focus AFTER
            // we exit — re-assert once more after it has had its turn.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (Visible && ForegroundIsThisProcess()) { try { Activate(); } catch { } }
            };
            t.Start();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>True when the current foreground window belongs to this process — the only case where
    /// re-asserting activation is shuffling focus between our own windows rather than stealing it.</summary>
    private static bool ForegroundIsThisProcess()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            _ = GetWindowThreadProcessId(fg, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch { return false; }
    }

    /// <summary>True when the editor holds work that isn't on disk: any adjustment/crop/filter, any markup,
    /// or an AI operation (which rewrites the source pixels and so isn't visible in the EditState).</summary>
    private bool HasUnsavedEdits => !_edit.IsNeutral || _markup.Count > 0 || _editor.SourceModified;

    /// <summary>True while the editor is on screen.</summary>
    private bool InEditor => EditorView.Visibility == Visibility.Visible;

    /// <summary>
    /// Asks about unsaved work before leaving the editor. Returns true if the caller may proceed (the work
    /// was saved, or the user chose to discard it) and false to stay put.
    ///
    /// Every exit route must go through this — the Cancel button, the window's X, and Esc. Guarding only the
    /// Cancel button silently threw away AI edits when the window was closed instead.
    /// </summary>
    /// <summary>Guards against a second ContentDialog while one is already up — showing two throws
    /// InvalidOperationException out of an async void handler, which takes the process down. Reachable by
    /// pressing Esc twice quickly, since Esc and the Cancel button share this path.</summary>
    private bool _leaveDialogOpen;

    private async Task<bool> ConfirmLeaveEditorAsync()
    {
        if (!InEditor || !HasUnsavedEdits) return true;
        if (_leaveDialogOpen) return false;   // already asking

        // Don't ask on top of a running job — cancel it first so the answer can't be raced by a late result.
        if (_aiBusy) AbortAiWork();

        var dialog = new ContentDialog
        {
            Title = "Save your changes?",
            Content = new TextBlock
            {
                Text = $"You've made changes to {System.IO.Path.GetFileName(_editPath) ?? "this image"} that "
                     + "haven't been saved. Saving writes a copy next to the original — the original file is "
                     + "never modified.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Save a copy",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Keep editing",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };

        _leaveDialogOpen = true;
        try
        {
            return await dialog.ShowAsync() switch
            {
                // Only leave if the file actually made it to disk — a failed export must not lose the work.
                ContentDialogResult.Primary => await SaveCopyAsync(),
                ContentDialogResult.Secondary => true,   // discard
                _ => false,                              // keep editing
            };
        }
        finally { _leaveDialogOpen = false; }
    }

    private async void EditCancel_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmLeaveEditorAsync()) ExitEditMode(reloadViewer: false);
    }

    // ---- canvas drawing ----

    private void EditCanvasHost_SizeChanged(object sender, SizeChangedEventArgs e) => _editCanvas?.Invalidate();

    private void EditCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_editor.Source is null) return;
        var ds = args.DrawingSession;

        var oriented = _editor.BuildOriented(_edit, out var bounds);
        double ow = bounds.Width, oh = bounds.Height;
        _orientedW = ow; _orientedH = oh;
        double cw = sender.Size.Width, ch = sender.Size.Height;
        if (ow <= 0 || oh <= 0 || cw <= 0 || ch <= 0) return;

        // Show the cropped region once a crop is applied (not actively cropping); else the whole image.
        var src = !_cropMode && _edit.Crop is Rect cr ? cr : new Rect(0, 0, ow, oh);
        _viewSrc = src;

        // Zoom is a multiplier on the fit-to-window scale, with a pan offset in display space. Everything
        // else (crop, markup, the compare divider) maps through _editFitRect/_editFitScale, so it follows.
        double fit = Math.Min(cw / src.Width, ch / src.Height);
        double scale = fit * _editZoom;
        double dw = src.Width * scale, dh = src.Height * scale;
        double ox = (cw - dw) / 2 + _editPanX, oy = (ch - dh) / 2 + _editPanY;
        _editFitRect = new Rect(ox, oy, dw, dh);
        _editFitScale = scale;

        // ---- Before / after comparison (Topaz-style) ----
        if (_compareMode != "off")
        {
            DrawCompare(ds, oriented, src, ox, oy, dw, dh, cw, ch, scale);
            return;
        }

        // While interactively cropping or marking up (lots of pointer-driven redraws), draw from a cached
        // render of the pipeline so each frame is a cheap bitmap blit, not a full effect re-render. During
        // adjustments (no interaction loop) draw the effect graph directly so the live preview is exact.
        ICanvasImage shown = oriented;
        if (_cropMode || _markupTool.Length > 0)
        {
            var sizeOk = _editCache is not null
                         && Math.Abs(_editCache.Size.Width - ow) < 0.5 && Math.Abs(_editCache.Size.Height - oh) < 0.5;
            if (_editCacheDirty || !sizeOk)
            {
                try
                {
                    if (!sizeOk) { _editCache?.Dispose(); _editCache = new CanvasRenderTarget(sender.Device, (float)ow, (float)oh, 96); }
                    using (var cds = _editCache!.CreateDrawingSession())
                    {
                        cds.Clear(Microsoft.UI.Colors.Transparent);
                        cds.DrawImage(oriented);
                    }
                    _editCacheDirty = false;
                }
                catch (Exception ex) { App.Log("EditCache", ex); _editCache = null; }
            }
            if (_editCache is not null) shown = _editCache;
        }
        else
        {
            _editCacheDirty = true; // adjustments happen here; force a rebuild next time we crop/mark up
        }

        ds.DrawImage(shown, new Rect(ox, oy, dw, dh), src);

        // Crop overlay (dim outside + bright border) — only while actively cropping the whole image.
        if (_cropMode && (_pendingCrop ?? _edit.Crop) is Rect c && c.Width > 0 && c.Height > 0)
        {
            var disp = new Rect(ox + c.X * scale, oy + c.Y * scale, c.Width * scale, c.Height * scale);
            var shade = Color.FromArgb(120, 0, 0, 0);
            ds.FillRectangle(new Rect(ox, oy, dw, disp.Y - oy), shade);
            ds.FillRectangle(new Rect(ox, disp.Y + disp.Height, dw, oy + dh - (disp.Y + disp.Height)), shade);
            ds.FillRectangle(new Rect(ox, disp.Y, disp.X - ox, disp.Height), shade);
            ds.FillRectangle(new Rect(disp.X + disp.Width, disp.Y, ox + dw - (disp.X + disp.Width), disp.Height), shade);
            ds.DrawRectangle(disp, Microsoft.UI.Colors.White, 2);
        }

        // Markup is in oriented space; map it through the currently-shown source region.
        var mx = ox - src.X * scale;
        var my = oy - src.Y * scale;
        foreach (var m in _markup) DrawShape(ds, m, mx, my, scale);
        if (_pendingShape is MarkupItem ps) DrawShape(ds, ps, mx, my, scale);

        // The committed selection (from the lasso or from text detection) lives in source-pixel space, so
        // it's put through the same geometry as the image to line up on the rotated/flipped preview.
        if (_selOverlay is not null)
        {
            try
            {
                var selImg = _editor.BuildOrientedOverlay(_edit, _selOverlay, out _);
                ds.DrawImage(selImg, new Rect(ox, oy, dw, dh), src);
            }
            catch (Exception ex) { App.Log("SelDraw", ex); }
        }

        // Lasso selection (oriented space, same mapping). Drawn as a dark/light dashed pair so it stays
        // visible over any image content.
        if (_lasso.Count >= 2)
        {
            using var path = new CanvasPathBuilder(ds.Device);
            path.BeginFigure((float)(mx + _lasso[0].X * scale), (float)(my + _lasso[0].Y * scale));
            for (var i = 1; i < _lasso.Count; i++)
                path.AddLine((float)(mx + _lasso[i].X * scale), (float)(my + _lasso[i].Y * scale));
            path.EndFigure(_lassoDrawing ? CanvasFigureLoop.Open : CanvasFigureLoop.Closed);
            using var geo = CanvasGeometry.CreatePath(path);

            if (!_lassoDrawing) ds.FillGeometry(geo, Color.FromArgb(40, 110, 168, 255));
            ds.DrawGeometry(geo, Color.FromArgb(200, 0, 0, 0), 3f);
            using var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            // While drawing, the dash tells the user which combine mode this stroke is in:
            // green = add (Shift), red = subtract (Alt), amber = intersect (Shift+Alt).
            var dashColor = !_lassoDrawing ? Microsoft.UI.Colors.White : _lassoCombine switch
            {
                "add" => Color.FromArgb(255, 90, 220, 90),
                "subtract" => Color.FromArgb(255, 255, 110, 110),
                "intersect" => Color.FromArgb(255, 255, 210, 80),
                _ => Microsoft.UI.Colors.White,
            };
            ds.DrawGeometry(geo, dashColor, 1.5f, dash);
        }
    }

    // Draw runs at pointer-move frequency; allocating (and leaking — they're IDisposable) a text format
    // per frame churns the GPU device. Shared instances, mutated only on the UI thread that draws.
    private static readonly CanvasTextFormat CompareLabelFormat = new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private static readonly CanvasTextFormat MarkupTextFormat = new();

    /// <summary>Draws the pristine image against the edited one. "before" is put through the same geometry
    /// (and scaled to the current source size) so the two line up even after an AI upscale.</summary>
    private void DrawCompare(CanvasDrawingSession ds, ICanvasImage after, Rect src,
        double ox, double oy, double dw, double dh, double cw, double ch, double scale)
    {
        ICanvasImage before;
        try { before = _editor.BuildBeforeOriented(_edit, out _); }
        catch { ds.DrawImage(after, new Rect(ox, oy, dw, dh), src); return; }

        var label = CompareLabelFormat;

        void Tag(string text, double x, double y)
        {
            var box = new Rect(x, y, 68, 22);
            ds.FillRectangle(box, Color.FromArgb(150, 0, 0, 0));
            ds.DrawText(text, new Vector2((float)(x + 8), (float)(y + 2)), Microsoft.UI.Colors.White, label);
        }

        switch (_compareMode)
        {
            case "original":
                ds.DrawImage(before, new Rect(ox, oy, dw, dh), src);
                Tag("Before", ox + 8, oy + 8);
                break;

            case "side":
            {
                var hw = cw / 2;
                var s2 = Math.Min(hw / src.Width, ch / src.Height) * _editZoom;
                double dw2 = src.Width * s2, dh2 = src.Height * s2;
                var oy2 = (ch - dh2) / 2 + _editPanY;

                // Fits in a half → butt the images against the centre line so they sit next to each other.
                // Zoomed past the half → centre both on the same source region (plus pan), so the two sides
                // show the SAME part of the image for inspection. Each half clips its own image.
                double oxL, oxR;
                if (dw2 <= hw) { oxL = hw - dw2 + _editPanX; oxR = hw + _editPanX; }
                else { oxL = (hw - dw2) / 2 + _editPanX; oxR = hw + (hw - dw2) / 2 + _editPanX; }

                using (ds.CreateLayer(1f, new Rect(0, 0, hw, ch)))
                    ds.DrawImage(before, new Rect(oxL, oy2, dw2, dh2), src);
                using (ds.CreateLayer(1f, new Rect(hw, 0, hw, ch)))
                    ds.DrawImage(after, new Rect(oxR, oy2, dw2, dh2), src);
                ds.DrawLine((float)hw, 0, (float)hw, (float)ch, Microsoft.UI.Colors.White, 1);
                Tag("Before", 8, 8);
                Tag("After", hw + 8, 8);
                break;
            }

            default: // "split" — after underneath, before revealed left of a viewport-anchored divider
            {
                ds.DrawImage(after, new Rect(ox, oy, dw, dh), src);

                // The divider lives at a fraction of the CANVAS, so it (and its grip) can never be panned
                // or zoomed out of view; the revealed portion is whatever part of the image lies left of it.
                var lineX = cw * Math.Clamp(_compareSplit, 0, 1);
                var f = Math.Clamp((lineX - ox) / Math.Max(1e-6, dw), 0, 1);
                if (f > 0.001)
                {
                    var destL = new Rect(ox, oy, dw * f, dh);
                    var srcL = new Rect(src.X, src.Y, src.Width * f, src.Height);
                    ds.DrawImage(before, destL, srcL);
                }
                ds.DrawLine((float)lineX, 0, (float)lineX, (float)ch, Microsoft.UI.Colors.White, 2);
                // Grip stays centred in the viewport (not the image), so it's always grabbable.
                ds.FillCircle((float)lineX, (float)(ch / 2), 9, Color.FromArgb(220, 255, 255, 255));
                ds.FillCircle((float)lineX, (float)(ch / 2), 7, Color.FromArgb(220, 30, 30, 30));
                Tag("Before", 8, 8);
                Tag("After", cw - 76, 8);
                break;
            }
        }
    }

    private static void DrawShape(CanvasDrawingSession ds, MarkupItem m, double offX, double offY, double sc)
    {
        float sx = (float)(offX + m.Start.X * sc), sy = (float)(offY + m.Start.Y * sc);
        float ex = (float)(offX + m.End.X * sc), ey = (float)(offY + m.End.Y * sc);
        float th = (float)Math.Max(1, m.Thickness * sc);
        var col = m.Color;
        switch (m.Kind)
        {
            case MarkupKind.Pen:
                for (var i = 1; i < m.Points.Count; i++)
                {
                    var a = m.Points[i - 1]; var b = m.Points[i];
                    ds.DrawLine((float)(offX + a.X * sc), (float)(offY + a.Y * sc),
                                (float)(offX + b.X * sc), (float)(offY + b.Y * sc), col, th);
                }
                break;
            case MarkupKind.Rectangle:
                ds.DrawRectangle(new Rect(Math.Min(sx, ex), Math.Min(sy, ey), Math.Abs(ex - sx), Math.Abs(ey - sy)), col, th);
                break;
            case MarkupKind.Ellipse:
                ds.DrawEllipse((sx + ex) / 2, (sy + ey) / 2, Math.Abs(ex - sx) / 2, Math.Abs(ey - sy) / 2, col, th);
                break;
            case MarkupKind.Line:
                ds.DrawLine(sx, sy, ex, ey, col, th);
                break;
            case MarkupKind.Arrow:
                ds.DrawLine(sx, sy, ex, ey, col, th);
                var ang = Math.Atan2(ey - sy, ex - sx);
                float head = Math.Max(10, th * 4);
                ds.DrawLine(ex, ey, (float)(ex - head * Math.Cos(ang - 0.5)), (float)(ey - head * Math.Sin(ang - 0.5)), col, th);
                ds.DrawLine(ex, ey, (float)(ex - head * Math.Cos(ang + 0.5)), (float)(ey - head * Math.Sin(ang + 0.5)), col, th);
                break;
            case MarkupKind.Text:
                MarkupTextFormat.FontSize = (float)Math.Max(6, m.FontSize * sc);
                ds.DrawText(m.Text, sx, sy, col, MarkupTextFormat);
                break;
        }
    }

    // ---- canvas modes & pointer ----

    private void SetCanvasMode(string mode)
    {
        // Engaging a tool leaves compare mode. While comparing, the canvas draws two images and the
        // fit-rect no longer describes where the image is — a crop or lasso drag would map to the wrong
        // place and wasn't even drawn, so you could silently crop a photo you never saw a rectangle on.
        if (mode is "crop" or "shape" && _compareMode != "off")
        {
            _compareMode = "off";
            _editLoading = true;
            if (CompareCombo.Items.Count > 0) CompareCombo.SelectedIndex = 0;
            _editLoading = false;
        }

        _cropMode = mode == "crop";
        if (mode != "shape") _markupTool = "";
        // Crop/markup and the lasso are mutually exclusive tools.
        if (mode is "crop" or "shape") { _lassoMode = false; UpdateLassoUi(); }
        // Clear any half-finished drag so a previously stuck pointer state can't break the new mode
        // (e.g. a lost pointer-capture that never fired PointerReleased).
        _dragging = false; _pendingCrop = null; _pendingShape = null; _cropDrag = "";
        UpdateOverlayHitTest();
    }

    /// <summary>The overlay only needs pointer events when something is actually draggable: a crop, a markup
    /// shape, the compare view (divider handle and pan/zoom inspection), or panning a zoomed-in preview.</summary>
    private void UpdateOverlayHitTest() =>
        OverlayCanvas.IsHitTestVisible =
            _cropMode || _markupTool.Length > 0 || _lassoMode || _compareMode != "off" || _editZoom > 1.0001;

    // ---- lasso selection ----

    private void LassoTool_Click(object sender, RoutedEventArgs e)
    {
        _lassoMode = !_lassoMode;
        if (_lassoMode)
        {
            SetCanvasMode("none");   // clears crop/markup tools
            _lassoMode = true;       // (SetCanvasMode doesn't know about the lasso)
            // Same reason as crop/markup: while comparing, a drag maps to the wrong place.
            if (_compareMode != "off")
            {
                _compareMode = "off";
                _editLoading = true;
                if (CompareCombo.Items.Count > 0) CompareCombo.SelectedIndex = 0;
                _editLoading = false;
            }
            AiSay("Lasso: drag to select · Shift adds · Alt subtracts · Shift+Alt intersects · Ctrl+D deselects");
        }
        else AiSay(null);
        UpdateLassoUi();
        UpdateOverlayHitTest();
        _editCanvas?.Invalidate();
    }

    private void LassoClear_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void UpdateLassoUi()
    {
        if (LassoBtn is not null)
            LassoBtn.Content = _lassoMode ? "Lasso (on)" : "Lasso";
        if (FillBtn is not null)
            FillBtn.IsEnabled = _selMask is not null && !_aiBusy;
    }

    /// <summary>True when the pointer is on the split divider's grab zone (the line/handle ±20px).</summary>
    private bool NearSplitDivider(Point pos)
    {
        var cw = EditCanvasHost.ActualWidth;
        if (cw <= 0) return false;
        var lineX = cw * Math.Clamp(_compareSplit, 0, 1);
        return Math.Abs(pos.X - lineX) <= 20;
    }

    // ---- preview zoom (scroll wheel + buttons) ----

    private void EditCanvas_Wheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_editor.Source is null) return;
        var pt = e.GetCurrentPoint(EditCanvasHost);
        var delta = pt.Properties.MouseWheelDelta;
        if (delta == 0) return;
        ZoomAt(pt.Position, delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private void EditZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAt(CanvasCentre(), 1.25);
    private void EditZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAt(CanvasCentre(), 1 / 1.25);

    private void EditZoomFit_Click(object sender, RoutedEventArgs e)
    {
        _editZoom = 1.0;
        _editPanX = _editPanY = 0;
        AfterZoomChanged();
    }

    private Point CanvasCentre() => new(EditCanvasHost.ActualWidth / 2, EditCanvasHost.ActualHeight / 2);

    /// <summary>Zooms about <paramref name="anchor"/> (the cursor, or the canvas centre for the buttons) so
    /// whatever is under it stays put — otherwise zooming appears to drift.</summary>
    private void ZoomAt(Point anchor, double factor)
    {
        var old = _editZoom;
        var z = Math.Clamp(old * factor, 0.2, 8.0);
        if (Math.Abs(z - old) < 1e-6) return;

        if (_editFitRect.Width > 0 && _viewSrc.Width > 0)
        {
            // Offset of the anchor from the image origin, at the old scale.
            var relX = anchor.X - _editFitRect.X;
            var relY = anchor.Y - _editFitRect.Y;
            var k = z / old;

            var fit = _editFitScale / old;                 // scale at zoom = 1
            var newW = _viewSrc.Width * fit * z;
            var newH = _viewSrc.Height * fit * z;
            var cw = EditCanvasHost.ActualWidth;
            var ch = EditCanvasHost.ActualHeight;

            // Solve for the pan that keeps anchor fixed: anchor - newOrigin == rel * k.
            _editPanX = anchor.X - relX * k - (cw - newW) / 2;
            _editPanY = anchor.Y - relY * k - (ch - newH) / 2;
        }

        _editZoom = z;
        if (Math.Abs(_editZoom - 1.0) < 1e-6) { _editPanX = 0; _editPanY = 0; } // snap back to a clean fit
        AfterZoomChanged();
    }

    private void AfterZoomChanged()
    {
        if (EditZoomLabel is not null)
            EditZoomLabel.Content = Math.Abs(_editZoom - 1.0) < 1e-6 ? "Fit" : $"{_editZoom * 100:0}%";
        UpdateOverlayHitTest();
        _editCanvas?.Invalidate();
    }

    // The split is a fraction of the CANVAS, not the image — so the divider/handle always stays in the
    // viewport even when the image is zoomed or panned out from under it.
    private void SetSplitFrom(double displayX)
    {
        var cw = EditCanvasHost.ActualWidth;
        if (cw <= 0) return;
        _compareSplit = Math.Clamp(displayX / cw, 0, 1);
        _editCanvas?.Invalidate();
    }

    private Point DisplayToOriented(Point p)
    {
        var ox = Math.Clamp(_viewSrc.X + (p.X - _editFitRect.X) / _editFitScale, 0, _orientedW);
        var oy = Math.Clamp(_viewSrc.Y + (p.Y - _editFitRect.Y) / _editFitScale, 0, _orientedH);
        return new Point(ox, oy);
    }

    private void Overlay_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Split mode: only a grab on the divider handle drags it — everywhere else the drag pans, so the
        // user can zoom in and inspect matching areas on both sides.
        if (_compareMode == "split" && NearSplitDivider(e.GetCurrentPoint(OverlayCanvas).Position))
        {
            _compareDragging = true;
            SetSplitFrom(e.GetCurrentPoint(OverlayCanvas).Position.X);
            OverlayCanvas.CapturePointer(e.Pointer);
            return;
        }

        // Lasso: start a freehand stroke. The modifiers decide how it combines with the existing
        // selection on release (Photoshop keys) — read HERE at drag start, like Photoshop does.
        if (_lassoMode)
        {
            _lassoCombine = IsShiftDown() && IsAltDown() ? "intersect"
                          : IsShiftDown() ? "add"
                          : IsAltDown() ? "subtract"
                          : "new";
            _lasso.Clear();
            _lassoDrawing = true;
            _lasso.Add(DisplayToOriented(e.GetCurrentPoint(OverlayCanvas).Position));
            OverlayCanvas.CapturePointer(e.Pointer);
            _editCanvas?.Invalidate();
            return;
        }

        // Zoomed in with no tool active (including compare views) → drag to pan around the image.
        if (!_cropMode && _markupTool.Length == 0 && _editZoom > 1.0001)
        {
            _editPanning = true;
            _editPanStart = e.GetCurrentPoint(OverlayCanvas).Position;
            _editPanStartX = _editPanX;
            _editPanStartY = _editPanY;
            OverlayCanvas.CapturePointer(e.Pointer);
            return;
        }

        var p = DisplayToOriented(e.GetCurrentPoint(OverlayCanvas).Position);
        if (_cropMode)
        {
            _dragging = true; _dragStart = p;
            // Decide what the drag does: resize a handle/edge, move, or draw a new rectangle.
            _cropDrag = _edit.Crop is Rect ec ? CropDragMode(ec, p) : "new";
            _cropAtDragStart = _edit.Crop ?? new Rect(p.X, p.Y, 0, 0);
            _pendingCrop = _cropDrag == "new" ? new Rect(p.X, p.Y, 0, 0) : _cropAtDragStart;
            OverlayCanvas.CapturePointer(e.Pointer);
        }
        else if (_markupTool == "Text")
        {
            _ = AddTextAsync(p);
        }
        else if (_markupTool.Length > 0)
        {
            _dragging = true; _dragStart = p;
            _pendingShape = new MarkupItem
            {
                Kind = Enum.Parse<MarkupKind>(_markupTool),
                Start = p, End = p, Color = SelectedMarkupColor(),
                // Stored in image space, so the default must come from the image's own size — deriving it
                // from the current zoom would bake that zoom into the shape permanently.
                Thickness = Math.Max(2.0, _orientedW / 400.0),
            };
            if (_pendingShape.Kind == MarkupKind.Pen) _pendingShape.Points.Add(p);
            OverlayCanvas.CapturePointer(e.Pointer);
        }
    }

    private void Overlay_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_compareDragging) { SetSplitFrom(e.GetCurrentPoint(OverlayCanvas).Position.X); return; }
        if (_lassoDrawing)
        {
            var lp = DisplayToOriented(e.GetCurrentPoint(OverlayCanvas).Position);
            // Skip near-duplicate points: a lasso of thousands of points makes the scanline fill crawl.
            if (_lasso.Count == 0 || Math.Abs(lp.X - _lasso[^1].X) + Math.Abs(lp.Y - _lasso[^1].Y) > 1.5)
                _lasso.Add(lp);
            _editCanvas?.Invalidate();
            return;
        }
        if (_editPanning)
        {
            var cur = e.GetCurrentPoint(OverlayCanvas).Position;
            _editPanX = _editPanStartX + (cur.X - _editPanStart.X);
            _editPanY = _editPanStartY + (cur.Y - _editPanStart.Y);
            _editCanvas?.Invalidate();
            return;
        }
        if (!_dragging) return;
        var p = DisplayToOriented(e.GetCurrentPoint(OverlayCanvas).Position);
        if (_cropMode)
        {
            _pendingCrop = _cropDrag switch
            {
                "new" => MakeCropRect(_dragStart, p),
                "move" => MoveCrop(_cropAtDragStart, p),
                _ => ResizeCrop(_cropDrag, _cropAtDragStart, p),
            };
            _editCanvas?.Invalidate();
        }
        else if (_pendingShape is not null)
        {
            _pendingShape.End = p;
            if (_pendingShape.Kind == MarkupKind.Pen) _pendingShape.Points.Add(p);
            _editCanvas?.Invalidate();
        }
    }

    private void Overlay_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try { OverlayCanvas.ReleasePointerCapture(e.Pointer); } catch { }
        if (_compareDragging) { _compareDragging = false; return; }
        if (_lassoDrawing)
        {
            _lassoDrawing = false;
            if (_lasso.Count < 3) { ClearSelection(); return; }   // a stray click isn't a selection
            BuildSelectionFromLasso();
            return;
        }
        if (_editPanning) { _editPanning = false; return; }
        FinishOverlayDrag();
    }

    /// <summary>Compare-mode picker. Comparing is a view mode, so it drops any active crop/markup tool —
    /// their pointer mapping doesn't apply while two images are on screen.</summary>
    private void Compare_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_editLoading || CompareCombo is null) return;
        _compareMode = (CompareCombo.SelectedIndex switch
        {
            1 => "split",
            2 => "side",
            3 => "original",
            _ => "off",
        });
        if (_compareMode != "off") SetCanvasMode("none");
        UpdateOverlayHitTest();
        _editCanvas?.Invalidate();
    }

    // If the pointer capture is lost (pointer leaves the window, another control steals it), PointerReleased
    // may never fire — finalize here too so the drag can't get stuck and force a close/reopen.
    private void Overlay_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => FinishOverlayDrag();

    private void FinishOverlayDrag()
    {
        // Also reached via PointerCaptureLost, so make sure a lost capture can't leave a drag stuck on.
        // _lassoDrawing especially: leaving it set meant the lasso kept trailing the cursor with no button
        // held, and carried on drawing over whatever tool was selected next.
        _editPanning = false;
        _compareDragging = false;
        if (_lassoDrawing)
        {
            _lassoDrawing = false;
            if (_lasso.Count >= 3) BuildSelectionFromLasso(); else ClearSelection();
        }
        if (!_dragging) return;
        _dragging = false;
        if (_cropMode)
        {
            if (_pendingCrop is Rect c && c.Width > 8 && c.Height > 8) { PushUndo(); _edit.Crop = c; }
            _pendingCrop = null; _cropDrag = "";
        }
        else if (_pendingShape is MarkupItem ps)
        {
            var keep = ps.Kind == MarkupKind.Pen
                ? ps.Points.Count > 1
                : Math.Abs(ps.End.X - ps.Start.X) > 3 || Math.Abs(ps.End.Y - ps.Start.Y) > 3;
            if (keep) { PushUndo(); _markup.Add(ps); }   // markup is undoable like any other edit
            _pendingShape = null;
        }
        _editCanvas?.Invalidate();
    }

    private Rect MakeCropRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(b.X - a.X), h = Math.Abs(b.Y - a.Y);
        if (_cropAspect > 0) h = w / _cropAspect;
        w = Math.Min(w, _orientedW - x);
        h = Math.Min(h, _orientedH - y);
        return new Rect(x, y, w, h);
    }

    /// <summary>Classifies a press over an existing crop: a corner/edge handle, inside (move), or new.</summary>
    private string CropDragMode(Rect ec, Point p)
    {
        var tol = 12 / Math.Max(0.0001, _editFitScale);
        double l = ec.X, t = ec.Y, r = ec.X + ec.Width, b = ec.Y + ec.Height;
        bool nl = Math.Abs(p.X - l) <= tol, nr = Math.Abs(p.X - r) <= tol;
        bool nt = Math.Abs(p.Y - t) <= tol, nb = Math.Abs(p.Y - b) <= tol;
        if (p.X >= l - tol && p.X <= r + tol && p.Y >= t - tol && p.Y <= b + tol)
        {
            if (nl && nt) return "nw";
            if (nr && nt) return "ne";
            if (nl && nb) return "sw";
            if (nr && nb) return "se";
            if (nl) return "w";
            if (nr) return "e";
            if (nt) return "n";
            if (nb) return "s";
            if (p.X >= l && p.X <= r && p.Y >= t && p.Y <= b) return "move";
        }
        return "new";
    }

    private Rect MoveCrop(Rect orig, Point p)
    {
        var nx = Math.Clamp(orig.X + (p.X - _dragStart.X), 0, _orientedW - orig.Width);
        var ny = Math.Clamp(orig.Y + (p.Y - _dragStart.Y), 0, _orientedH - orig.Height);
        return new Rect(nx, ny, orig.Width, orig.Height);
    }

    /// <summary>Resizes the crop by a corner or edge. Corners keep the chosen aspect (proportionate).</summary>
    private Rect ResizeCrop(string mode, Rect orig, Point p)
    {
        const double min = 10;
        double l = orig.X, t = orig.Y, r = orig.X + orig.Width, b = orig.Y + orig.Height;
        bool left = mode is "nw" or "sw" or "w";
        bool right = mode is "ne" or "se" or "e";
        bool top = mode is "nw" or "ne" or "n";
        bool bottom = mode is "sw" or "se" or "s";

        if (left) l = Math.Clamp(p.X, 0, r - min);
        if (right) r = Math.Clamp(p.X, l + min, _orientedW);
        if (top) t = Math.Clamp(p.Y, 0, b - min);
        if (bottom) b = Math.Clamp(p.Y, t + min, _orientedH);

        if (mode is "nw" or "ne" or "sw" or "se" && _cropAspect > 0)
        {
            var w = r - l;
            var h = w / _cropAspect;
            if (top) t = b - h; else b = t + h;
            if (t < 0) { t = 0; h = b - t; w = h * _cropAspect; if (left) l = r - w; else r = l + w; }
            if (b > _orientedH) { b = _orientedH; h = b - t; w = h * _cropAspect; if (left) l = r - w; else r = l + w; }
        }
        return new Rect(l, t, Math.Max(min, r - l), Math.Max(min, b - t));
    }

    private void Overlay_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (_cropMode) CropApply_Click(sender, e);
    }

    private async Task AddTextAsync(Point at)
    {
        var box = new TextBox { PlaceholderText = "Text", AcceptsReturn = false, MinWidth = 240 };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var dlg = new ContentDialog
        {
            Title = "Add text", Content = box, PrimaryButtonText = "Add", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrEmpty(box.Text)) return;
        PushUndo();
        _markup.Add(new MarkupItem
        {
            Kind = MarkupKind.Text, Start = at, End = at, Text = box.Text,
            // Image-space size derived from the image, not the current zoom (see _pendingShape.Thickness).
            Color = SelectedMarkupColor(), FontSize = Math.Max(14.0, _orientedW / 80.0),
        });
        _editCanvas?.Invalidate();
    }

    private Color SelectedMarkupColor()
    {
        var name = (MarkupColorCombo.SelectedItem as ComboBoxItem)?.Content as string;
        return name switch
        {
            "Yellow" => Microsoft.UI.Colors.Yellow,
            "Lime" => Microsoft.UI.Colors.Lime,
            "White" => Microsoft.UI.Colors.White,
            "Black" => Microsoft.UI.Colors.Black,
            _ => Microsoft.UI.Colors.Red,
        };
    }

    // ---- transform handlers ----

    private void RotateCw_Click(object sender, RoutedEventArgs e) { PushUndo(); _edit.Quarter = (_edit.Quarter + 1) % 4; _edit.Crop = null; InvalidateEditImage(); }
    private void RotateCcw_Click(object sender, RoutedEventArgs e) { PushUndo(); _edit.Quarter = (_edit.Quarter + 3) % 4; _edit.Crop = null; InvalidateEditImage(); }
    private void FlipH_Click(object sender, RoutedEventArgs e) { PushUndo(); _edit.FlipH = !_edit.FlipH; _edit.Crop = null; InvalidateEditImage(); }
    private void FlipV_Click(object sender, RoutedEventArgs e) { PushUndo(); _edit.FlipV = !_edit.FlipV; _edit.Crop = null; InvalidateEditImage(); }

    private void Straighten_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_editCanvas is null || _editLoading) return;
        DebounceUndo();
        _edit.StraightenDeg = StraightenSlider.Value;
        _edit.Crop = null;
        InvalidateEditImage();
    }

    private void CropAspect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_editCanvas is null || _editLoading) return; // ignore events fired during XAML load
        _cropAspect = ((CropAspectCombo.SelectedItem as ComboBoxItem)?.Content as string) switch
        {
            "Original" => _orientedH > 0 ? _orientedW / _orientedH : 0,
            "1:1" => 1.0,
            "4:3" => 4.0 / 3,
            "3:2" => 3.0 / 2,
            "16:9" => 16.0 / 9,
            _ => 0,
        };
        SetCanvasMode("crop");
        StatusText.Text = "Drag on the image to set the crop.";
    }

    private void CropReset_Click(object sender, RoutedEventArgs e) { PushUndo(); _edit.Crop = null; _editCanvas?.Invalidate(); }

    private void CropEnter_Click(object sender, RoutedEventArgs e)
    {
        if (_editCanvas is null) return;
        SetCanvasMode("crop");
        _editCanvas.Invalidate();
        StatusText.Text = "Drag to draw a crop; drag inside it to move. Then Apply.";
    }

    private void CropApply_Click(object sender, RoutedEventArgs e)
    {
        if (_editCanvas is null) return;
        // Commit a still-in-progress selection so Apply works even if the pointer is mid-drag / never released.
        if (_pendingCrop is Rect pc && pc.Width > 8 && pc.Height > 8) { PushUndo(); _edit.Crop = pc; }
        _pendingCrop = null; _cropDrag = ""; _dragging = false;
        if (_edit.Crop is null) { StatusText.Text = "Drag a box on the image first, then Apply."; return; }
        SetCanvasMode("none");
        _editCanvas.Invalidate();
        StatusText.Text = "Crop applied (preview). Use Crop to adjust, Reset to clear.";
    }

    // ---- adjustments & filters ----

    private void Adjust_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_editCanvas is null || _editLoading) return;
        DebounceUndo();
        _edit.Exposure = ExposureSlider.Value / 50.0;     // -2..2
        _edit.Brightness = BrightnessSlider.Value / 100.0;
        _edit.Contrast = ContrastSlider.Value / 100.0;
        _edit.Saturation = SaturationSlider.Value / 100.0;
        _edit.Temperature = TemperatureSlider.Value / 100.0;
        _edit.Tint = TintSlider.Value / 100.0;
        _edit.Sharpness = SharpnessSlider.Value / 100.0;
        InvalidateEditImage();
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && Enum.TryParse<ImageFilter>(fe.Tag as string, out var f))
        {
            PushUndo();
            _edit.Filter = f;
            InvalidateEditImage();
        }
    }

    private void ResetEditSliders()
    {
        _editLoading = true;
        ExposureSlider.Value = 0; BrightnessSlider.Value = 0; ContrastSlider.Value = 0;
        SaturationSlider.Value = 0; TemperatureSlider.Value = 0; TintSlider.Value = 0; SharpnessSlider.Value = 0;
        StraightenSlider.Value = 0;
        _editLoading = false;
    }

    private void SyncSlidersFromState()
    {
        _editLoading = true;
        ExposureSlider.Value = _edit.Exposure * 50;
        BrightnessSlider.Value = _edit.Brightness * 100;
        ContrastSlider.Value = _edit.Contrast * 100;
        SaturationSlider.Value = _edit.Saturation * 100;
        TemperatureSlider.Value = _edit.Temperature * 100;
        TintSlider.Value = _edit.Tint * 100;
        SharpnessSlider.Value = _edit.Sharpness * 100;
        StraightenSlider.Value = _edit.StraightenDeg;
        _editLoading = false;
    }

    // ---- markup tool buttons ----

    private void MarkupTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) { _markupTool = fe.Tag as string ?? ""; SetCanvasMode("shape"); }
    }

    private void MarkupNone_Click(object sender, RoutedEventArgs e) => SetCanvasMode("none");

    private void MarkupClear_Click(object sender, RoutedEventArgs e)
    {
        if (_markup.Count == 0) return;
        PushUndo();          // clearing every annotation was previously unrecoverable
        _markup.Clear();
        _editCanvas?.Invalidate();
    }

    // ---- undo / redo / reset ----

    private void PushUndo(byte[]? pixels = null, int w = 0, int h = 0)
    {
        _editUndo.Add(new EditSnapshot(_edit.Clone(), MarkupSnapshot(), pixels, w, h));
        _editRedo.Clear();
        PrunePixelSnapshots(_editUndo);
    }

    private List<MarkupItem> MarkupSnapshot() => _markup.Select(m => m.Clone()).ToList();

    private void RestoreMarkup(List<MarkupItem> items)
    {
        _markup.Clear();
        _markup.AddRange(items.Select(m => m.Clone()));
    }

    /// <summary>Records the current pixels as well — used before an AI model rewrites them.</summary>
    private void PushUndoPixels()
    {
        if (_editor.Source is null) { PushUndo(); return; }
        var px = _editor.GetSourcePixels(0, out var w, out var h);
        PushUndo(px, w, h);
    }

    /// <summary>Only the newest few entries keep their bitmap; older ones degrade to parameters-only so the
    /// history can't quietly pin hundreds of megabytes.</summary>
    private static void PrunePixelSnapshots(List<EditSnapshot> history)
    {
        var kept = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Pixels is null) continue;
            if (++kept > MaxPixelSnapshots) history[i] = history[i] with { Pixels = null, W = 0, H = 0 };
        }
    }

    private void DebounceUndo()
    {
        var now = Environment.TickCount64;
        if (now - _lastEditChangeTick > 500) PushUndo();
        _lastEditChangeTick = now;
    }

    private void EditUndo_Click(object sender, RoutedEventArgs e) => StepHistory(_editUndo, _editRedo);
    private void EditRedo_Click(object sender, RoutedEventArgs e) => StepHistory(_editRedo, _editUndo);

    /// <summary>Moves one step between the undo and redo histories. An entry that carries pixels restores the
    /// bitmap as well as the parameters — that's what makes Undo actually reverse an AI action.</summary>
    private void StepHistory(List<EditSnapshot> from, List<EditSnapshot> to)
    {
        // Stepping history while an AI job is in flight would restore pixels the finishing job then
        // overwrites — the result would look like undo "didn't work" (or worse, mixed states).
        if (_aiBusy) return;
        if (from.Count == 0) return;
        var entry = from[^1];
        from.RemoveAt(from.Count - 1);

        // Capture where we are now so the step is reversible. If we're about to restore pixels, the current
        // pixels have to be recorded too, or stepping back the other way would land on the wrong bitmap.
        byte[]? nowPixels = null;
        int nowW = 0, nowH = 0;
        if (entry.Pixels is not null && _editor.Source is not null)
            nowPixels = _editor.GetSourcePixels(0, out nowW, out nowH);
        to.Add(new EditSnapshot(_edit.Clone(), MarkupSnapshot(), nowPixels, nowW, nowH));
        PrunePixelSnapshots(to);

        _edit = entry.State;
        RestoreMarkup(entry.Markup);
        if (entry.Pixels is not null)
        {
            _editor.ReplaceSource(entry.Pixels, entry.W, entry.H);
            InvalidateLiveDenoise();
            ClearSelection();   // a mask sized to the other bitmap can't be reused
        }

        SyncSlidersFromState();
        InvalidateEditImage();
    }

    private void EditReset_Click(object sender, RoutedEventArgs e)
    {
        // The AI works from a snapshot of the pixels taken when it started, so a reset mid-run would be
        // silently overwritten when the job lands — it would look like the reset undid itself.
        if (_aiBusy) { StatusText.Text = "Wait for the AI operation to finish."; return; }

        // Reset means "back to the file as it was", so it has to undo AI pixel changes too — not just the
        // sliders. The pixel snapshot (100MB+ on a big photo) is only taken when AI actually rewrote the
        // source; a plain slider reset stays cheap.
        if (_editor.SourceModified) PushUndoPixels(); else PushUndo();
        _editor.RevertToOriginal();
        InvalidateLiveDenoise();
        ClearSelection();   // reverting an AI upscale changes the pixel size — a kept mask would be stale
        _edit = new EditState();
        _markup.Clear();
        ResetEditSliders();
        SetCanvasMode("none");
        InvalidateEditImage();
    }

    // ---- save ----

    private void BakeOverlay(CanvasDrawingSession ds, Rect crop)
    {
        // Markup is stored in oriented-image space; shift by the crop origin to land in the render target.
        foreach (var m in _markup) DrawShape(ds, m, -crop.X, -crop.Y, 1.0);
    }

    private async Task<bool> ExportToAsync(string destPath)
    {
        try
        {
            var quality = 0.92f;
            await _editor.ExportAsync(_edit, destPath, quality, BakeOverlay);
            return true;
        }
        catch (Exception ex) { StatusText.Text = "Save failed: " + ex.Message; App.Log("EditorSave", ex); return false; }
    }

    /// <summary>Writes the edit to a new file next to the original. True if it landed on disk.</summary>
    private async Task<bool> SaveCopyAsync()
    {
        if (_editPath is null) return false;
        var dir = System.IO.Path.GetDirectoryName(_editPath)!;
        var ext = System.IO.Path.GetExtension(_editPath);
        var baseName = System.IO.Path.GetFileNameWithoutExtension(_editPath);
        var dest = UniquePath(System.IO.Path.Combine(dir, baseName + "-edited" + ext), isDir: false);
        if (!await ExportToAsync(dest)) return false;
        StatusText.Text = "Saved " + System.IO.Path.GetFileName(dest);
        return true;
    }

    /// <summary>Serializes the save handlers. Double-clicking "Save a copy" otherwise wrote both
    /// `-edited` AND `-edited-2` and called ExitEditMode twice.</summary>
    private bool _saving;

    private async void SaveCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _aiBusy) return;
        _saving = true;
        try { if (await SaveCopyAsync()) ExitEditMode(reloadViewer: false); }
        finally { _saving = false; }
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_editPath is null || _saving || _aiBusy) return;
        _saving = true;
        try { await SaveAsCoreAsync(); }
        finally { _saving = false; }
    }

    private async Task SaveAsCoreAsync()
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("JPEG image", new List<string> { ".jpg" });
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_editPath!) + "-edited";
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        if (await ExportToAsync(file.Path))
        {
            StatusText.Text = "Saved " + file.Name;
            ExitEditMode(reloadViewer: false);
        }
    }

    private async void SaveOverwrite_Click(object sender, RoutedEventArgs e)
    {
        if (_editPath is null || _saving || _aiBusy) return;
        _saving = true;
        try { await SaveOverwriteCoreAsync(); }
        finally { _saving = false; }
    }

    private async Task SaveOverwriteCoreAsync()
    {
        var confirm = new ContentDialog
        {
            Title = "Overwrite original?",
            Content = "This replaces the original file on disk. This can't be undone.",
            PrimaryButtonText = "Overwrite", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = RootGrid.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        // The photo viewer's BitmapImage keeps the original file memory-mapped, so writing over it
        // directly fails with ERROR_USER_MAPPED_FILE. Release that hold, render to a sibling temp file,
        // then atomically replace the original (with a short retry while the mapping is released).
        var path = _editPath!;
        ViewerImage.Source = null;

        var dir = System.IO.Path.GetDirectoryName(path)!;
        var ext = System.IO.Path.GetExtension(path);
        var tmp = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(path) + ".galileo-tmp" + ext);
        if (!await ExportToAsync(tmp))
        {
            // A failed export can leave a partial temp file behind — with its real extension, it would
            // list as a broken photo.
            try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
            // The viewer's image was released to free the file lock; a failed export must put it back or the
            // viewer behind the editor stays permanently blank.
            _ = LoadCurrentAsync();
            return;
        }

        if (await ReplaceWithRetryAsync(tmp, path))
        {
            StatusText.Text = "Saved " + System.IO.Path.GetFileName(path);
            ExitEditMode(reloadViewer: true);
        }
        else
        {
            try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
            ExitEditMode(reloadViewer: true);
        }
    }

    /// <summary>Replaces <paramref name="dest"/> with <paramref name="tmp"/>, retrying briefly while the
    /// file is still mapped/locked (the viewer's bitmap releases shortly after its source is cleared).</summary>
    private async Task<bool> ReplaceWithRetryAsync(string tmp, string dest)
    {
        for (var i = 0; i < 15; i++)
        {
            try
            {
                if (System.IO.File.Exists(dest)) System.IO.File.Replace(tmp, dest, null);
                else System.IO.File.Move(tmp, dest);
                return true;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                await Task.Delay(80); // file still in use — wait for the mapping to drop
            }
            catch (Exception ex)
            {
                StatusText.Text = "Save failed: " + ex.Message; App.Log("EditorOverwrite", ex);
                return false;
            }
        }
        StatusText.Text = "Save failed: the file is still in use.";
        return false;
    }
}
