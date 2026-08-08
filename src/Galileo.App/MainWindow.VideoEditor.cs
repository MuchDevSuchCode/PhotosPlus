using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Galileo.Services;
using Windows.Foundation;
using Windows.Media.Playback;
using Windows.Storage.Pickers;

namespace Galileo;

public sealed partial class MainWindow
{
    // ===================== FFmpeg-powered video editor =====================

    private string? _currentVideoPath;     // the real file path of the open video (for editing)
    private VideoInfo? _editVideoInfo;
    private int _editRotate;               // 0 / 90 / 180 / 270
    private double _editTrimStart;
    private double? _editTrimEnd;
    private readonly List<(double Start, double End)> _editSegments = new();
    private readonly List<string> _editCodecIds = new();
    private int _editorOpenGen; // bumped on every open/close so an in-flight probe can detect it was superseded

    private double CurrentVideoSeconds()
        => VideoPlayer.MediaPlayer?.PlaybackSession?.Position.TotalSeconds ?? 0;

    private async void VideoEdit_Click(object sender, RoutedEventArgs e)
    {
        try { await OpenVideoEditorAsync(); }
        catch (Exception ex) { EditorStatus.Text = "Couldn't open the editor: " + ex.Message; App.Log("OpenVideoEditor", ex); }
    }

    private async Task OpenVideoEditorAsync()
    {
        if (string.IsNullOrEmpty(_currentVideoPath) || !File.Exists(_currentVideoPath))
        {
            StatusText.Text = "Open a video to edit it.";
            return;
        }
        if (!FfmpegVideo.Available)
        {
            EditorStatus.Text = "FFmpeg isn't bundled with this build (Assets/ffmpeg).";
            VideoEditorPanel.Visibility = Visibility.Visible;
            return;
        }

        // Reset editor state.
        _editRotate = 0;
        _editTrimStart = 0;
        _editTrimEnd = null;
        _editSegments.Clear();
        EditRotateText.Text = "0°";
        EditFlipHSwitch.IsOn = EditFlipVSwitch.IsOn = false;
        EditDeinterlace.IsOn = EditDenoise.IsOn = EditSharpen.IsOn = EditStabilize.IsOn = false;
        EditCropL.Value = EditCropT.Value = EditCropR.Value = EditCropB.Value = 0;
        EditResizeW.Value = EditResizeH.Value = 0;
        EditBrightness.Value = 0; EditContrast.Value = 1; EditSaturation.Value = 1;
        EditSpeed.Value = 1; EditFps.Value = 0;
        EditAudioCombo.SelectedIndex = 0;
        EditContainerCombo.SelectedIndex = 0;
        EditCrf.Value = 21;
        EditPresetCombo.SelectedIndex = 2;
        UpdateTrimText();
        UpdateSegmentsText();

        VideoEditorPanel.Visibility = Visibility.Visible;
        EditorStatus.Text = "Probing…";

        var gen = ++_editorOpenGen; // detect close/reopen during the awaits below
        _editVideoInfo = await FfmpegVideo.ProbeAsync(_currentVideoPath);
        var encoders = await FfmpegVideo.DetectEncodersAsync();
        if (gen != _editorOpenGen) return; // editor was closed or another video opened while probing

        // Codec choices: software + copy + any detected GPU encoders.
        _editCodecIds.Clear();
        EditCodecCombo.Items.Clear();
        void AddCodec(string label, string id) { EditCodecCombo.Items.Add(new ComboBoxItem { Content = label }); _editCodecIds.Add(id); }
        AddCodec("H.264", "h264");
        AddCodec("H.265 (HEVC)", "h265");
        AddCodec("Copy (no re-encode)", "copy");
        foreach (var enc in encoders)
            AddCodec(enc switch
            {
                "h264_nvenc" => "H.264 — NVIDIA NVENC",
                "hevc_nvenc" => "H.265 — NVIDIA NVENC",
                "h264_qsv" => "H.264 — Intel Quick Sync",
                "hevc_qsv" => "H.265 — Intel Quick Sync",
                "h264_amf" => "H.264 — AMD",
                "hevc_amf" => "H.265 — AMD",
                _ => enc,
            }, enc);
        EditCodecCombo.SelectedIndex = 0;

        if (_editVideoInfo is { } info)
            EditorStatus.Text = $"{info.Width}×{info.Height} · {info.Fps:0.##} fps · {FormatClock(info.Duration)}";
        else
            EditorStatus.Text = "Couldn't probe the file; export may still work.";

        HookEditorPreviewHandlers();
        _editorReady = true;
        EditTimeline.Visibility = Visibility.Visible;
        SubscribePlayhead();
        StartLivePreview();
        _ = BuildTimelineAsync();
    }

    private void CloseVideoEditor_Click(object sender, RoutedEventArgs e) => CloseVideoEditor();

    /// <summary>Full teardown — stops the live preview (restoring normal playback) and hides editor chrome.</summary>
    private void CloseVideoEditor()
    {
        _editorOpenGen++; // supersede any in-flight OpenVideoEditorAsync probe
        _editorReady = false;
        StopLivePreview();
        UnsubscribePlayhead();
        VideoEditorPanel.Visibility = Visibility.Collapsed;
        EditTimeline.Visibility = Visibility.Collapsed;
        try { if (_thumbsDir is not null && Directory.Exists(_thumbsDir)) Directory.Delete(_thumbsDir, true); } catch { }
        _thumbsDir = null;
    }

    private void EditSetStart_Click(object sender, RoutedEventArgs e) { _editTrimStart = CurrentVideoSeconds(); UpdateTrimText(); }
    private void EditSetEnd_Click(object sender, RoutedEventArgs e)
    {
        // An end at/before the start would "successfully" export a near-zero-length clip.
        var pos = CurrentVideoSeconds();
        if (pos <= _editTrimStart) { EditorStatus.Text = "The end must be after the start."; return; }
        _editTrimEnd = pos;
        UpdateTrimText();
    }
    private void EditResetTrim_Click(object sender, RoutedEventArgs e) { _editTrimStart = 0; _editTrimEnd = null; UpdateTrimText(); }

    private void EditAddSegment_Click(object sender, RoutedEventArgs e)
    {
        var end = _editTrimEnd ?? (_editVideoInfo?.Duration ?? 0);
        if (end <= _editTrimStart) { EditorStatus.Text = "Set a start before the end first."; return; }
        _editSegments.Add((_editTrimStart, end));
        _editTrimStart = 0; _editTrimEnd = null;
        UpdateTrimText();
        UpdateSegmentsText();
    }

    private void EditClearSegments_Click(object sender, RoutedEventArgs e) { _editSegments.Clear(); UpdateSegmentsText(); }

    private void UpdateTrimText()
    {
        EditTrimText.Text = _editTrimStart <= 0 && _editTrimEnd is null
            ? "Whole clip"
            : $"{FormatClock(_editTrimStart)} → {(_editTrimEnd is { } e ? FormatClock(e) : "end")}";
        UpdateTrimMarks();
    }

    /// <summary>Positions the trim start/end marks (and the kept-region highlight) on the timeline.</summary>
    private void UpdateTrimMarks()
    {
        var dur = _editVideoInfo?.Duration ?? 0;
        var w = EditTimeline.ActualWidth;
        if (dur <= 0 || w <= 0 || EditTimeline.Visibility != Visibility.Visible)
        {
            EditStartMark.Visibility = EditEndMark.Visibility = EditKeptRegion.Visibility = Visibility.Collapsed;
            return;
        }
        var startFrac = Math.Clamp(_editTrimStart / dur, 0, 1);
        var endFrac = Math.Clamp((_editTrimEnd ?? dur) / dur, 0, 1);

        EditStartMark.Visibility = _editTrimStart > 0 ? Visibility.Visible : Visibility.Collapsed;
        EditStartShift.X = startFrac * w;
        EditEndMark.Visibility = _editTrimEnd is not null ? Visibility.Visible : Visibility.Collapsed;
        EditEndShift.X = Math.Max(0, endFrac * w - EditEndMark.Width);

        if ((_editTrimStart > 0 || _editTrimEnd is not null) && endFrac > startFrac)
        {
            EditKeptShift.X = startFrac * w;
            EditKeptRegion.Width = (endFrac - startFrac) * w;
            EditKeptRegion.Visibility = Visibility.Visible;
        }
        else EditKeptRegion.Visibility = Visibility.Collapsed;
    }

    private void EditTimeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTrimMarks();
        var dur = _editVideoInfo?.Duration ?? 0;
        var mp = VideoPlayer.MediaPlayer;
        if (dur > 0 && mp?.PlaybackSession is { } ps)
            EditPlayheadShift.X = Math.Clamp(ps.Position.TotalSeconds / dur, 0, 1) * EditTimeline.ActualWidth;
    }

    private void UpdateSegmentsText()
        => EditSegmentsText.Text = _editSegments.Count == 0
            ? ""
            : $"{_editSegments.Count} segment(s) will be stitched: " +
              string.Join(", ", _editSegments.ConvertAll(s => $"{FormatClock(s.Start)}–{FormatClock(s.End)}"));

    private void EditRotateLeft_Click(object sender, RoutedEventArgs e) { _editRotate = (_editRotate + 270) % 360; EditRotateText.Text = $"{_editRotate}°"; UpdatePreviewTransform(); }
    private void EditRotateRight_Click(object sender, RoutedEventArgs e) { _editRotate = (_editRotate + 90) % 360; EditRotateText.Text = $"{_editRotate}°"; UpdatePreviewTransform(); }

    private void EditContainer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // GIF ignores codec/CRF/preset/audio; dim them as a hint (export handles it regardless).
        var gif = EditContainerCombo.SelectedIndex == 3;
        if (EditCodecCombo is not null) EditCodecCombo.IsEnabled = !gif;
        if (EditCrf is not null) EditCrf.IsEnabled = !gif;
        if (EditPresetCombo is not null) EditPresetCombo.IsEnabled = !gif;
        if (EditAudioCombo is not null) EditAudioCombo.IsEnabled = !gif;
    }

    private VideoEditSettings BuildEditSettings()
    {
        double Nz(double v) => double.IsNaN(v) ? 0 : v;
        var s = new VideoEditSettings
        {
            InputPath = _currentVideoPath!,
            SourceDuration = _editVideoInfo?.Duration ?? 0,
            CropL = (int)Nz(EditCropL.Value), CropT = (int)Nz(EditCropT.Value),
            CropR = (int)Nz(EditCropR.Value), CropB = (int)Nz(EditCropB.Value),
            ResizeW = (int)Nz(EditResizeW.Value), ResizeH = (int)Nz(EditResizeH.Value),
            Rotate = _editRotate,
            FlipH = EditFlipHSwitch.IsOn, FlipV = EditFlipVSwitch.IsOn,
            Deinterlace = EditDeinterlace.IsOn, Denoise = EditDenoise.IsOn,
            Sharpen = EditSharpen.IsOn, Stabilize = EditStabilize.IsOn,
            Brightness = EditBrightness.Value, Contrast = EditContrast.Value, Saturation = EditSaturation.Value,
            Fps = Nz(EditFps.Value),
            Speed = EditSpeed.Value <= 0 ? 1 : EditSpeed.Value,
            AudioMode = EditAudioCombo.SelectedIndex switch { 1 => "aac", 2 => "mp3", 3 => "none", _ => "keep" },
            Container = EditContainerCombo.SelectedIndex switch { 1 => "mkv", 2 => "ts", 3 => "gif", _ => "mp4" },
            VideoCodec = _editCodecIds.Count > 0 && EditCodecCombo.SelectedIndex >= 0
                ? _editCodecIds[EditCodecCombo.SelectedIndex] : "h264",
            Crf = (int)EditCrf.Value,
            Preset = (EditPresetCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "medium",
        };
        s.ColorAdjust = s.Brightness != 0 || s.Contrast != 1 || s.Saturation != 1;
        if (_editSegments.Count > 0) s.Segments = new List<(double, double)>(_editSegments);
        else { s.TrimStart = _editTrimStart; s.TrimEnd = _editTrimEnd; }
        return s;
    }

    private async void EditExport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentVideoPath)) return;
        var s = BuildEditSettings();
        var ext = "." + s.Container;

        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var typeName = s.Container switch { "gif" => "Animated GIF", "mkv" => "Matroska", "ts" => "MPEG-TS", _ => "MP4 video" };
        picker.FileTypeChoices.Add(typeName, new List<string> { ext });
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_currentVideoPath) + "-edited";
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        await RunFfmpegExportAsync(s, file.Path, s.Container == "gif" ? "Exporting GIF" : "Exporting video");
    }

    private async void EditSaveFrame_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentVideoPath)) return;
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_currentVideoPath) + "-frame";
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        EditorStatus.Text = "Saving frame…";
        try { await FfmpegVideo.SnapshotAsync(_currentVideoPath, CurrentVideoSeconds(), file.Path); EditorStatus.Text = "Saved frame: " + file.Path; }
        catch (Exception ex) { EditorStatus.Text = "Save frame failed: " + ex.Message; App.Log("VideoSnapshot", ex); }
    }

    /// <summary>Runs an FFmpeg export behind the floating progress card (Cancel + Hide).</summary>
    private async Task RunFfmpegExportAsync(VideoEditSettings s, string outPath, string title)
    {
        _progressCancel?.Invoke();
        var cts = new CancellationTokenSource();
        var token = new object();
        BeginProgressOp(token, title, cancel: cts.Cancel, pauseToggle: null, isPaused: null, hideable: true);
        TransferFile.Text = System.IO.Path.GetFileName(outPath);
        ShowTransferPanel();

        var progress = new Progress<double>(pct =>
        {
            if (!ReferenceEquals(_activeOp, token)) return; // ignore stale reports if another op took over
            _transferFrac = Math.Clamp(pct / 100.0, 0, 1);
            TransferBar.Value = _transferFrac;
            TransferStats.Text = $"{pct:0}%";
            TransferEta.Text = "";
        });
        try
        {
            await FfmpegVideo.ExportAsync(s, outPath, progress, cts.Token);
            EditorStatus.Text = "Exported: " + outPath;
            StatusText.Text = "Video exported.";
        }
        catch (OperationCanceledException)
        {
            EditorStatus.Text = "Export canceled.";
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
        }
        catch (Exception ex) { EditorStatus.Text = "Export failed: " + ex.Message; App.Log("VideoExport", ex); }
        finally { EndProgressOp(token, null); }
    }

    // ---- Timeline (filmstrip + scrub) ----

    private string? _thumbsDir;
    private bool _scrubbing;
    private bool _playheadHooked;

    private async Task BuildTimelineAsync()
    {
        EditFilmstrip.Children.Clear();
        EditFilmstrip.ColumnDefinitions.Clear();
        EditPlayheadShift.X = 0;
        if (_currentVideoPath is null || _editVideoInfo is null || _editVideoInfo.Duration <= 0) return;
        var gen = _editorOpenGen; // detect close/reopen during generation
        try
        {
            var thumbs = await FfmpegVideo.GenerateThumbnailsAsync(_currentVideoPath, 16, _editVideoInfo.Duration);
            var dir = thumbs.Count > 0 ? Path.GetDirectoryName(thumbs[0]) : null;
            if (gen != _editorOpenGen || !_editorReady)
            {
                // Editor closed (or reopened) while generating — its cleanup already ran, so assigning
                // _thumbsDir now would just orphan these JPEGs; delete them instead.
                try { if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                return;
            }
            _thumbsDir = dir;
            for (var i = 0; i < thumbs.Count; i++)
            {
                EditFilmstrip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var img = new Image
                {
                    Source = new BitmapImage(new Uri(thumbs[i])),
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                };
                img.Clip = null;
                Grid.SetColumn(img, i);
                EditFilmstrip.Children.Add(img);
            }
        }
        catch (Exception ex) { App.Log("Timeline", ex); }
    }

    private void EditTimeline_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _scrubbing = true;
        EditTimeline.CapturePointer(e.Pointer);
        ScrubTo(e);
    }

    private void EditTimeline_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_scrubbing) ScrubTo(e);
    }

    private void EditTimeline_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _scrubbing = false;
        EditTimeline.ReleasePointerCapture(e.Pointer);
    }

    private void ScrubTo(PointerRoutedEventArgs e)
    {
        var w = EditTimeline.ActualWidth;
        var dur = _editVideoInfo?.Duration ?? 0;
        if (w <= 0 || dur <= 0) return;
        var frac = Math.Clamp(e.GetCurrentPoint(EditTimeline).Position.X / w, 0, 1);
        EditPlayheadShift.X = frac * w;
        var mp = VideoPlayer.MediaPlayer;
        if (mp?.PlaybackSession is { } ps) ps.Position = TimeSpan.FromSeconds(frac * dur);
        if (_previewOn) RenderFromLastFrame(); // refresh the still preview at the new spot
    }

    private void SubscribePlayhead()
    {
        var mp = VideoPlayer.MediaPlayer;
        if (mp?.PlaybackSession is { } ps && !_playheadHooked)
        {
            ps.PositionChanged += OnPlaybackPositionChanged;
            _playheadHooked = true;
        }
    }

    private void UnsubscribePlayhead()
    {
        var mp = VideoPlayer.MediaPlayer;
        if (mp?.PlaybackSession is { } ps && _playheadHooked)
        {
            ps.PositionChanged -= OnPlaybackPositionChanged;
            _playheadHooked = false;
        }
    }

    private void OnPlaybackPositionChanged(MediaPlaybackSession session, object args)
    {
        var pos = session.Position.TotalSeconds;
        var dur = _editVideoInfo?.Duration ?? 0;
        if (dur <= 0) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_scrubbing || EditTimeline.Visibility != Visibility.Visible) return;
            EditPlayheadShift.X = Math.Clamp(pos / dur, 0, 1) * EditTimeline.ActualWidth;
        });
    }

    // ---- Live preview (Win2D frame server) ----

    private CanvasImageSource? _previewSource;
    private CanvasRenderTarget? _lastFrame;
    private int _previewSrcW, _previewSrcH;
    private bool _previewOn;
    private bool _previewBusy;
    private bool _editorReady;
    private bool _editorHandlersHooked;
    private MediaPlayer? _previewPlayer;

    private void StartLivePreview()
    {
        var mp = VideoPlayer.MediaPlayer;
        var w = _editVideoInfo?.Width ?? 0;
        var h = _editVideoInfo?.Height ?? 0;
        if (mp is null || w <= 0 || h <= 0) return; // no probe info → keep the normal player
        try
        {
            // The frame server copies straight into this persistent render target (a CanvasRenderTarget is
            // an IDirect3DSurface) — no per-frame SoftwareBitmap or throwaway GPU texture.
            _lastFrame?.Dispose();
            _lastFrame = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), w, h, 96);
            _previewPlayer = mp;
            mp.IsVideoFrameServerEnabled = true;
            mp.VideoFrameAvailable += OnVideoFrameAvailable;
            _previewOn = true;
            EditPreviewImage.Visibility = Visibility.Visible;
            UpdatePreviewTransform();
        }
        catch (Exception ex)
        {
            App.Log("LivePreviewStart", ex);
            StopLivePreview();
            EditorStatus.Text = "Live preview unavailable on this video; edits still export.";
        }
    }

    private void StopLivePreview()
    {
        _previewOn = false;
        if (_previewPlayer is not null)
        {
            try { _previewPlayer.VideoFrameAvailable -= OnVideoFrameAvailable; _previewPlayer.IsVideoFrameServerEnabled = false; } catch { }
            _previewPlayer = null;
        }
        EditPreviewImage.Visibility = Visibility.Collapsed;
        EditPreviewImage.Source = null;
        _previewSource = null; _previewSrcW = _previewSrcH = 0;
        try { _lastFrame?.Dispose(); } catch { }
        _lastFrame = null;
        if (EditPreviewTransform is not null) { EditPreviewTransform.Rotation = 0; EditPreviewTransform.ScaleX = EditPreviewTransform.ScaleY = 1; }
    }

    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (!_previewOn || _previewBusy) return;
        _previewBusy = true;
        var queued = DispatcherQueue.TryEnqueue(() =>
        {
            try { GrabAndRenderFrame(sender); }
            catch (Exception ex) { App.Log("PreviewFrame", ex); }
            finally { _previewBusy = false; }
        });
        if (!queued) _previewBusy = false; // never latch if the dispatch was dropped
    }

    private void GrabAndRenderFrame(MediaPlayer mp)
    {
        if (!_previewOn || _lastFrame is null) return;
        mp.CopyFrameToVideoSurface(_lastFrame);
        RenderPreview(_lastFrame, _lastFrame.Size.Width, _lastFrame.Size.Height);
    }

    private void RenderFromLastFrame()
    {
        if (!_previewOn || _lastFrame is null) return;
        try { RenderPreview(_lastFrame, _lastFrame.Size.Width, _lastFrame.Size.Height); }
        catch (Exception ex) { App.Log("PreviewRedraw", ex); }
    }

    private void RenderPreview(ICanvasImage frame, double w, double h)
    {
        if (!_previewOn) return;
        double Nz(double v) => double.IsNaN(v) ? 0 : v;
        var l = (int)Math.Clamp(Nz(EditCropL.Value), 0, w - 2);
        var t = (int)Math.Clamp(Nz(EditCropT.Value), 0, h - 2);
        var r = (int)Math.Clamp(Nz(EditCropR.Value), 0, w - 2 - l);
        var b = (int)Math.Clamp(Nz(EditCropB.Value), 0, h - 2 - t);
        var cw = Math.Max(2, (int)w - l - r);
        var ch = Math.Max(2, (int)h - t - b);

        var img = BuildPreviewColor(frame);

        var device = CanvasDevice.GetSharedDevice();
        if (_previewSource is null || _previewSrcW != cw || _previewSrcH != ch)
        {
            _previewSrcW = cw; _previewSrcH = ch;
            _previewSource = new CanvasImageSource(device, cw, ch, 96);
            EditPreviewImage.Source = _previewSource;
        }
        using (var ds = _previewSource.CreateDrawingSession(Microsoft.UI.Colors.Black))
            ds.DrawImage(img, new Rect(0, 0, cw, ch), new Rect(l, t, cw, ch));

        UpdatePreviewTransform();
    }

    // Color/sharpen only (crop is applied by the source rect, rotate/flip by the Image transform).
    private ICanvasImage BuildPreviewColor(ICanvasImage src)
    {
        var img = src;
        double br = EditBrightness.Value, co = EditContrast.Value, sa = EditSaturation.Value;
        if (br != 0) img = new LinearTransferEffect { Source = img, RedOffset = (float)br, GreenOffset = (float)br, BlueOffset = (float)br };
        if (co != 1) img = new ContrastEffect { Source = img, Contrast = (float)Math.Clamp(co - 1, -1, 1) };
        if (sa != 1) img = new ColorMatrixEffect { Source = img, ColorMatrix = SaturationMatrix((float)sa) };
        if (EditSharpen.IsOn) img = new SharpenEffect { Source = img, Amount = 4f, Threshold = 0 };
        return img;
    }

    private void UpdatePreviewTransform()
    {
        if (EditPreviewTransform is null) return;
        // For 90/270 the rotated image's bounding box swaps W/H, so a uniform-stretched Image overflows.
        // Apply a compensating scale so the rotated preview re-fits its area (matches the export aspect).
        double k = 1;
        if ((_editRotate == 90 || _editRotate == 270) && _previewSrcW > 0 && _previewSrcH > 0)
        {
            double aw = EditPreviewImage.ActualWidth, ah = EditPreviewImage.ActualHeight;
            if (aw > 0 && ah > 0)
            {
                var scale0 = Math.Min(aw / _previewSrcW, ah / _previewSrcH); // Uniform fit before rotation
                double rw = _previewSrcW * scale0, rh = _previewSrcH * scale0;
                if (rh > 0 && rw > 0) k = Math.Min(aw / rh, ah / rw); // re-fit the rotated box
            }
        }
        EditPreviewTransform.Rotation = _editRotate;
        EditPreviewTransform.ScaleX = (EditFlipHSwitch.IsOn ? -1 : 1) * k;
        EditPreviewTransform.ScaleY = (EditFlipVSwitch.IsOn ? -1 : 1) * k;
    }

    // Re-render the still preview when a tool changes (so paused edits update immediately).
    private void OnEditPreviewChanged() { if (_editorReady) RenderFromLastFrame(); }

    private void HookEditorPreviewHandlers()
    {
        if (_editorHandlersHooked) return;
        _editorHandlersHooked = true;
        EditBrightness.ValueChanged += (_, _) => OnEditPreviewChanged();
        EditContrast.ValueChanged += (_, _) => OnEditPreviewChanged();
        EditSaturation.ValueChanged += (_, _) => OnEditPreviewChanged();
        EditSharpen.Toggled += (_, _) => OnEditPreviewChanged();
        EditDenoise.Toggled += (_, _) => OnEditPreviewChanged();
        EditFlipHSwitch.Toggled += (_, _) => { if (_editorReady) UpdatePreviewTransform(); };
        EditFlipVSwitch.Toggled += (_, _) => { if (_editorReady) UpdatePreviewTransform(); };
        EditCropL.ValueChanged += (_, _) => OnEditPreviewChanged();
        EditCropT.ValueChanged += (_, _) => OnEditPreviewChanged();
        EditCropR.ValueChanged += (_, _) => OnEditPreviewChanged();
        EditCropB.ValueChanged += (_, _) => OnEditPreviewChanged();
    }

    private static Matrix5x4 SaturationMatrix(float sat)
    {
        const float lr = 0.2125f, lg = 0.7154f, lb = 0.0721f;
        float ir = (1 - sat) * lr, ig = (1 - sat) * lg, ib = (1 - sat) * lb;
        return new Matrix5x4
        {
            M11 = ir + sat, M12 = ir, M13 = ir, M14 = 0,
            M21 = ig, M22 = ig + sat, M23 = ig, M24 = 0,
            M31 = ib, M32 = ib, M33 = ib + sat, M34 = 0,
            M41 = 0, M42 = 0, M43 = 0, M44 = 1,
            M51 = 0, M52 = 0, M53 = 0, M54 = 0,
        };
    }

    private static string FormatClock(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
