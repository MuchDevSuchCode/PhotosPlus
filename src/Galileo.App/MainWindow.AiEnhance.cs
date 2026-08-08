using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Galileo.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Galileo;

/// <summary>
/// AI restoration in the image editor — Real-ESRGAN (upscale / detail / denoise), CodeFormer face recovery,
/// and an Autopilot that measures the image and picks the steps for you. Everything runs locally on the GPU
/// through DirectML; nothing is uploaded.
///
/// These models rewrite pixels, so unlike the sliders/filters (a non-destructive Win2D effect graph) each
/// action replaces the editor's source bitmap — hence the dedicated one-deep Undo.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Built on first actual use — never just because the editor was opened. Constructing it would
    /// pull in the ONNX Runtime assembly (and, through it, the native runtime), which has no business being
    /// loaded for someone who only wants to crop a photo.</summary>
    private AiEngine? _ai;
    private AiEngine Ai => _ai ??= new AiEngine();

    private bool _aiBusy;
    private CancellationTokenSource? _aiCts;

    /// <summary>Separate from <see cref="_aiCts"/>: Autopilot downloads models *between* its stages, so a
    /// download must not clobber the in-flight job's token source (it would null it out mid-run).</summary>
    private CancellationTokenSource? _downloadCts;

    /// <summary>Bumped whenever the editor loads a different image or closes. A job captures it and refuses
    /// to apply its result if it no longer matches — otherwise a slow upscale of photo A could land on
    /// photo B, or write into an editor that has already been unloaded.</summary>
    private int _aiGeneration;

    /// <summary>
    /// Models are cached per-model and are NOT dropped when you switch between them. But they were being
    /// dropped every time the editor closed, so hopping back to the viewer and returning meant reloading
    /// CodeFormer (360 MB) from scratch.
    ///
    /// Instead, keep them warm and release only after a spell of not using AI at all — you get the memory
    /// back when you've moved on, without paying a reload for every trip through the viewer.
    /// </summary>
    private static readonly TimeSpan AiIdleRelease = TimeSpan.FromMinutes(5);

    private Microsoft.UI.Xaml.DispatcherTimer? _aiIdleTimer;

    /// <summary>Cancels any pending release — an AI operation is starting or running.</summary>
    private void KeepModelsWarm() => _aiIdleTimer?.Stop();

    /// <summary>Arms the idle release (called when the editor closes).</summary>
    private void ScheduleModelRelease()
    {
        if (_ai is null) return;   // nothing loaded — don't even create the timer
        if (_aiIdleTimer is null)
        {
            _aiIdleTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = AiIdleRelease };
            _aiIdleTimer.Tick += (_, _) =>
            {
                _aiIdleTimer!.Stop();
                if (_aiBusy || InEditor) return;   // still in use — try again next time we leave
                try { _ai?.ReleaseSessions(); } catch { }
                App.LogInfo("AI: released cached models after idle");
                GC.Collect();
            };
        }
        _aiIdleTimer.Stop();
        _aiIdleTimer.Start();
    }

    /// <summary>Cancels any in-flight AI work and invalidates its result. Called when the editor loads a new
    /// image or closes.</summary>
    private void AbortAiWork()
    {
        _aiGeneration++;
        try { _aiCts?.Cancel(); } catch { }
        try { _downloadCts?.Cancel(); } catch { }
        _aiBusy = false;
        if (AiProgress is not null) AiProgress.Visibility = Visibility.Collapsed;
        if (AiCancelBtn is not null) AiCancelBtn.Visibility = Visibility.Collapsed;
    }

    // Live denoise: the network runs once at full strength; the strength slider then re-blends the
    // original against that processed result instantly, always re-rendering from the original rather
    // than compounding passes. Invalidated whenever any other operation rewrites the pixels.
    private byte[]? _denoiseBase;
    private byte[]? _denoiseProcessed;
    private int _denoiseW, _denoiseH;

    internal void InvalidateLiveDenoise() { _denoiseBase = null; _denoiseProcessed = null; }

    private double Strength => (AiStrength?.Value ?? 40) / 100.0;
    private double Fidelity => (AiFidelity?.Value ?? 70) / 100.0;

    private async void AiEnhance_Click(object sender, RoutedEventArgs e) => await RunAiAsync(AiJob.Enhance);
    private async void AiUpscale_Click(object sender, RoutedEventArgs e) => await RunAiAsync(AiJob.Upscale);
    private async void AiDenoise_Click(object sender, RoutedEventArgs e) => await RunAiAsync(AiJob.Denoise);
    private async void AiFaces_Click(object sender, RoutedEventArgs e) => await RunAiAsync(AiJob.Faces);
    private async void AiAuto_Click(object sender, RoutedEventArgs e) => await RunAutopilotAsync();

    private enum AiJob { Enhance, Upscale, Denoise, Faces }

    /// <summary>Turns the drawn lasso into the source-space selection mask, combining it with any
    /// existing selection per the Photoshop-style mode captured at drag start (add/subtract/intersect).</summary>
    private void BuildSelectionFromLasso()
    {
        if (_editor.Source is null || _lasso.Count < 3) { ClearSelection(); return; }
        int w = (int)_editor.PixelWidth, h = (int)_editor.PixelHeight;

        // The lasso is drawn on the oriented preview; map it back to raw source pixels.
        var poly = new List<Point>(_lasso.Count);
        foreach (var p in _lasso)
            if (_editor.TryOrientedToSource(_edit, p, out var sp)) poly.Add(sp);

        if (poly.Count < 3) { ClearSelection(); return; }
        var shape = RasterizePolygon(poly, w, h);
        _lasso.Clear();   // committed — from here the tinted mask overlay IS the selection's visual

        var combine = _lassoCombine;
        var old = _selMask;
        if (old is not null && old.Length != shape.Length)
        {
            // The kept mask was sized to different pixels (an upscale/undo changed them since) — combining
            // with it would corrupt, so drop it, say so, and let this stroke start a fresh selection.
            ClearSelection();
            AiSay("The previous selection no longer matched the image — starting a new selection.");
            old = null;
            combine = "new";
        }
        switch (combine)
        {
            case "add" when old is not null:
                for (var i = 0; i < shape.Length; i++) if (old[i] != 0) shape[i] = 255;
                break;
            case "subtract":
                if (old is null) { ClearSelection(); AiSay("Nothing to subtract from."); return; }
                var cut = (byte[])old.Clone();
                for (var i = 0; i < shape.Length; i++) if (shape[i] != 0) cut[i] = 0;
                shape = cut;
                break;
            case "intersect":
                // Photoshop semantics: intersecting with no prior selection yields nothing, not a new one.
                if (old is null) { ClearSelection(); AiSay("No pixels selected."); return; }
                for (var i = 0; i < shape.Length; i++) shape[i] = (byte)(old[i] != 0 && shape[i] != 0 ? 255 : 0);
                break;
        }

        // Subtract/intersect can leave nothing — that's "no selection", not an invisible one.
        var any = false;
        for (var i = 0; i < shape.Length; i++) if (shape[i] != 0) { any = true; break; }
        if (!any) { ClearSelection(); AiSay("No pixels selected."); return; }

        SetSelection(shape, w, h);
    }

    /// <summary>Finds text (watermarks, captions, timestamps) and selects it — no drawing required.</summary>
    private async void AiSelectText_Click(object sender, RoutedEventArgs e)
    {
        if (_aiBusy || _editor.Source is null) return;
        if (!await EnsureModelAsync(AiModel.TextDetect)) return;

        SetAiBusy(true);
        _aiCts = new CancellationTokenSource();
        var gen = _aiGeneration;
        try
        {
            var pixels = _editor.GetSourcePixels(0, out var w, out var h);
            AiSay("Looking for text…");

            var engine = Ai;
            var ct = _aiCts.Token;
            var regions = 0;
            var mask = await Task.Run(() => TextDetect.DetectMask(engine, pixels, w, h, out regions, ct), ct);

            // The editor loaded a different photo, or closed, while detection ran — installing this mask
            // would select photo A's text on photo B.
            if (gen != _aiGeneration || _editor.Source is null) return;

            if (mask is null)
            {
                ClearSelection();
                AiSay("No text found in this image.");
                return;
            }
            _lasso.Clear();          // a detected selection isn't a polygon
            SetSelection(mask, w, h);
            AiSay($"Selected {regions} text region{(regions == 1 ? "" : "s")} — press Fill to remove.");
        }
        catch (OperationCanceledException) { AiSay("Cancelled."); }
        catch (Exception ex)
        {
            App.Log("AiSelectText", ex);
            await MessageAsync("Select text", "Text detection failed.\n\n" + ex.Message);
            AiSay(null);
        }
        finally
        {
            _aiCts?.Dispose();
            _aiCts = null;
            SetAiBusy(false);
        }
    }

    /// <summary>Content-aware fill: LaMa paints out whatever is selected (lasso or detected text).</summary>
    private async void AiFill_Click(object sender, RoutedEventArgs e)
    {
        if (_aiBusy || _editor.Source is null || _selMask is null) return;
        if (!await EnsureModelAsync(AiModel.Inpaint)) return;

        SetAiBusy(true);
        _aiCts = new CancellationTokenSource();
        var gen = _aiGeneration;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var before = _editor.GetSourcePixels(0, out var w, out var h);
            var mask = _selMask;
            if (mask.Length != w * h) { AiSay("The selection no longer matches the image."); return; }

            AiSay("Filling the selection…");
            var p2 = AiProgressReporter();
            var ct = _aiCts.Token;
            var engine = Ai;
            var result = await Task.Run(() => Inpaint.Fill(engine, before, w, h, mask, p2, ct), ct);

            if (!ApplyAiResult(gen, before, w, h, result, w, h)) return;
            ClearSelection();
            sw.Stop();
            AiSay($"Filled the selection  ·  {engine.Provider}  ·  {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) { AiSay("Cancelled."); }
        catch (Exception ex)
        {
            App.Log("AiFill", ex);
            await MessageAsync("Content-aware fill", "The fill failed.\n\n" + ex.Message);
            AiSay(null);
        }
        finally
        {
            _aiCts?.Dispose();
            _aiCts = null;
            SetAiBusy(false);
            UpdateLassoUi();
        }
    }

    /// <summary>Fills a closed polygon into a byte mask (255 inside) by scanline — testing every pixel
    /// against every edge would crawl on a lasso with hundreds of points.</summary>
    private static byte[] RasterizePolygon(List<Point> poly, int w, int h)
    {
        var mask = new byte[w * h];
        var n = poly.Count;
        var xs = new List<double>(8);

        var minY = Math.Max(0, (int)Math.Floor(poly.Min(p => p.Y)));
        var maxY = Math.Min(h - 1, (int)Math.Ceiling(poly.Max(p => p.Y)));

        for (var y = minY; y <= maxY; y++)
        {
            var cy = y + 0.5;
            xs.Clear();
            for (var i = 0; i < n; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % n];           // closing edge included
                if (a.Y == b.Y) continue;
                // Half-open rule: counts each crossing once, so shared vertices can't double-count.
                if (cy < Math.Min(a.Y, b.Y) || cy >= Math.Max(a.Y, b.Y)) continue;
                xs.Add(a.X + (cy - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            for (var i = 0; i + 1 < xs.Count; i += 2)
            {
                var x0 = Math.Max(0, (int)Math.Ceiling(xs[i] - 0.5));
                var x1 = Math.Min(w - 1, (int)Math.Floor(xs[i + 1] - 0.5));
                for (var x = x0; x <= x1; x++) mask[y * w + x] = 255;
            }
        }
        return mask;
    }

    /// <summary>Stops the running AI job. The models honour the token between tiles/faces, so this takes
    /// effect at the next checkpoint rather than instantly.</summary>
    private void AiCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _aiCts?.Cancel(); } catch { }
        try { _downloadCts?.Cancel(); } catch { }
        AiSay("Cancelling…");
    }

    private void SetAiBusy(bool busy)
    {
        _aiBusy = busy;
        if (busy) KeepModelsWarm();   // an idle release must not fire mid-operation
        foreach (var b in new[] { AiEnhanceBtn, AiUpscaleBtn, AiDenoiseBtn, AiFacesBtn, AiAutoBtn, SelectTextBtn })
            if (b is not null) b.IsEnabled = !busy;
        if (AiCancelBtn is not null) AiCancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateLassoUi();   // Fill depends on both the busy state and whether a selection exists
        if (AiProgress is not null)
        {
            AiProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            // Indeterminate until the first real progress tick: the gap covers loading the model into the
            // GPU (a few seconds on first use, since AI loads only when actually used), so the bar visibly
            // works instead of sitting at 0.
            AiProgress.IsIndeterminate = busy;
            AiProgress.Value = 0;
        }
    }

    private void AiSay(string? text) { if (AiStatus is not null) AiStatus.Text = text ?? ""; }

    private IProgress<double> AiProgressReporter() =>
        new Progress<double>(v =>
        {
            if (AiProgress is null) return;
            AiProgress.IsIndeterminate = false;
            AiProgress.Value = v;
        });

    /// <summary>Downloads a model once, with confirmation (they range from 0.2 MB to 360 MB).</summary>
    private async Task<bool> EnsureModelAsync(AiModel model)
    {
        if (AiEngine.IsReady(model)) return true;
        var spec = AiEngine.Catalog[model];

        var confirm = new ContentDialog
        {
            Title = "Download the AI model?",
            Content = new TextBlock
            {
                Text = $"This needs {spec.Label}. It downloads once, is kept on this PC, and then runs "
                     + "locally on your GPU — images are never uploaded anywhere.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return false;

        SetAiBusy(true);
        AiSay($"Downloading {spec.Label}…");
        // Its own token so Cancel works during the download too — CodeFormer is 360 MB, and being unable to
        // stop that is exactly when you'd want to.
        _downloadCts = new CancellationTokenSource();
        try
        {
            await AiEngine.DownloadAsync(model, AiProgressReporter(), _downloadCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            AiSay("Download cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            App.Log("AiModelDownload", ex);
            await MessageAsync("AI", "Couldn't download the model.\n\n" + ex.Message);
            return false;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            // Don't clear the busy state if a job is still running around us (Autopilot downloads
            // mid-flow); the job's own finally will do it.
            if (_aiCts is null) SetAiBusy(false);
        }
    }

    /// <summary>Called when the live-denoise slider moves: re-blend original vs processed and redisplay.
    /// This always starts from the original pixels, so dragging never compounds the effect.</summary>
    private void AiStrength_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_aiBusy || _denoiseBase is null || _denoiseProcessed is null) return;
        var blended = AiEngine.Blend(_denoiseBase, _denoiseProcessed, Strength);
        _editor.ReplaceSource(blended, _denoiseW, _denoiseH);
        AiSay($"Denoise strength {Strength:P0}");
        InvalidateEditImage();
    }

    /// <summary>Pushes the pre-AI pixels onto the shared undo history, then swaps in the result. Pushed only
    /// on success, so a failed or cancelled run doesn't leave a bogus entry to "undo".
    /// Returns false when the result is stale (the editor moved on) and was discarded.</summary>
    private bool ApplyAiResult(int generation, byte[] before, int beforeW, int beforeH,
        byte[] result, int outW, int outH)
    {
        // The editor loaded a different photo, or closed, while this ran — dropping the result is the whole
        // point of the generation check.
        if (generation != _aiGeneration || _editor.Source is null) return false;

        InvalidateLiveDenoise();   // any pixel rewrite obsoletes the live-blend pair (denoise re-arms after)
        PushUndo(before, beforeW, beforeH);
        var factor = _editor.ReplaceSource(result, outW, outH);

        if (Math.Abs(factor - 1.0) > 0.001)
        {
            // Everything held in oriented/source coordinates has to follow the resize.
            if (_edit.Crop is { } c)
                _edit.Crop = new Rect(c.X * factor, c.Y * factor, c.Width * factor, c.Height * factor);
            foreach (var m in _markup) m.Scale(factor);
            // The selection mask is sized to the old pixels; it can't be reinterpreted, so drop it rather
            // than leave a stale overlay that draws in the wrong place and a Fill button that can't work.
            ClearSelection();
        }
        InvalidateEditImage();
        return true;
    }

    private static AiModel ModelFor(AiJob job) => job switch
    {
        AiJob.Enhance or AiJob.Upscale => AiModel.Upscale,
        AiJob.Denoise => AiModel.General,
        _ => AiModel.Face,
    };

    private async Task RunAiAsync(AiJob job)
    {
        if (_aiBusy || _editor.Source is null) return;
        if (!await EnsureModelAsync(ModelFor(job))) return;
        if (job == AiJob.Faces && !await EnsureModelAsync(AiModel.FaceDetect)) return;

        SetAiBusy(true);
        _aiCts = new CancellationTokenSource();
        var gen = _aiGeneration;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Pixels as they are now — pushed onto the undo history only if the run succeeds.
            var before = _editor.GetSourcePixels(0, out var bw, out var bh);

            // Only a kept 4x result needs the input capped (its output has 16x the pixels); the others keep
            // the original size, so they can stream a full-resolution photo.
            var cap = job == AiJob.Upscale ? AiEngine.MaxUpscaleInputEdge : 0;
            var pixels = _editor.GetSourcePixels(cap, out var w, out var h);

            AiSay(job switch
            {
                AiJob.Upscale => $"Upscaling {w}×{h} → {w * 4}×{h * 4}…",
                AiJob.Denoise => $"Denoising {w}×{h}…",
                AiJob.Faces => "Finding and restoring faces…",
                _ => $"Enhancing {w}×{h}…",
            });

            var p = AiProgressReporter();
            var ct = _aiCts.Token;
            var strength = Strength;
            var fidelity = Fidelity;

            var engine = Ai;
            int outW = w, outH = h, faces = 0;
            byte[]? denoisedFull = null;   // pure processed result, kept so the strength slider can re-blend live
            var result = await Task.Run(() =>
            {
                switch (job)
                {
                    case AiJob.Upscale: return engine.Enhance(pixels, w, h, true, out outW, out outH, p, ct);
                    case AiJob.Enhance: return engine.Enhance(pixels, w, h, false, out outW, out outH, p, ct);
                    case AiJob.Denoise:
                        // Run the network once at 100%; the displayed result is a blend, and the slider can
                        // then re-blend from the original instantly without another inference pass.
                        denoisedFull = engine.Denoise(pixels, w, h, 1.0, p, ct);
                        return AiEngine.Blend(pixels, denoisedFull, strength);
                    default: return FaceRestore.Run(engine, pixels, w, h, fidelity, out faces, p, ct);
                }
            }, ct);

            if (job == AiJob.Faces && faces == 0)
            {
                AiSay("No faces found in this image.");
                return;
            }

            if (!ApplyAiResult(gen, before, bw, bh, result, outW, outH)) return;
            if (job == AiJob.Denoise)
            {
                // Arm the live slider (after ApplyAiResult, which clears any previous pair).
                _denoiseBase = pixels; _denoiseProcessed = denoisedFull; _denoiseW = w; _denoiseH = h;
            }
            sw.Stop();
            AiSay(job switch
            {
                AiJob.Upscale => $"Upscaled → {outW}×{outH}",
                AiJob.Denoise => $"Denoised (strength {strength:P0}) — drag the slider to fine-tune live",
                AiJob.Faces => $"Restored {faces} face{(faces == 1 ? "" : "s")}",
                _ => $"Enhanced → {outW}×{outH}",
            } + $"  ·  {engine.Provider}  ·  {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) { AiSay("Cancelled."); }
        catch (Exception ex)
        {
            App.Log("AiEnhance", ex);
            await MessageAsync("AI", "AI processing failed.\n\n" + ex.Message);
            AiSay(null);
        }
        finally
        {
            _aiCts?.Dispose();
            _aiCts = null;
            SetAiBusy(false);
        }
    }

    /// <summary>
    /// Autopilot: measure the image, then apply only what it actually needs.
    /// Blur is the variance of the Laplacian (low = soft), noise is the mean deviation from a local mean
    /// (high = grainy), and faces come from the detector. Cheap to compute and far more useful than
    /// blindly running every model.
    /// </summary>
    private async Task RunAutopilotAsync()
    {
        if (_aiBusy || _editor.Source is null) return;
        // Face restoration redraws faces, so Autopilot only considers it when explicitly opted in;
        // with the toggle off we don't even need (or download) the face-detect model.
        var allowFaces = AiAutoFaces?.IsChecked == true;
        if (allowFaces && !await EnsureModelAsync(AiModel.FaceDetect)) return;

        SetAiBusy(true);
        _aiCts = new CancellationTokenSource();
        var gen = _aiGeneration;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var probe = _editor.GetSourcePixels(0, out var pw, out var ph);
            AiSay("Analysing…");

            var engine = Ai;
            var (blur, noise) = await Task.Run(() => AiEngine.Analyze(probe, pw, ph));
            var faceCount = allowFaces
                ? await Task.Run(() => FaceRestore.DetectRestorable(engine, probe, pw, ph, _aiCts.Token).Count)
                : 0;

            var wantDenoise = noise > 2.0;
            var wantDetail = blur < 150;
            var wantFaces = faceCount > 0;

            var plan = new List<string>();
            if (wantDenoise) plan.Add("denoise");
            if (wantDetail) plan.Add("enhance");
            if (wantFaces) plan.Add($"restore {faceCount} face{(faceCount == 1 ? "" : "s")}");

            if (plan.Count == 0)
            {
                AiSay($"Autopilot: this image already looks clean and sharp (blur {blur:0}, noise {noise:0.0}). Nothing to do.");
                return;
            }

            // Make sure everything the plan needs is present before touching the image.
            if (wantDenoise && !await EnsureModelAsync(AiModel.General)) return;
            if (wantDetail && !await EnsureModelAsync(AiModel.Upscale)) return;
            if (wantFaces && !await EnsureModelAsync(AiModel.Face)) return;

            SetAiBusy(true);
            AiSay($"Autopilot: {string.Join(" + ", plan)}…");

            var p = AiProgressReporter();
            var ct = _aiCts.Token;
            // Scale denoise with how noisy it actually is, rather than using a fixed amount.
            var denoiseStrength = Math.Clamp((noise - 1.5) / 6.0, 0.35, 1.0);
            var fidelity = Fidelity;

            // Each stage's output size feeds the next. Enhance keeps the size today, but hard-coding the
            // INPUT size into ReplaceSource would silently hand it a mismatched buffer the day it doesn't.
            int outW = pw, outH = ph, facesDone = 0;
            var result = await Task.Run(() =>
            {
                var cur = probe;
                if (wantDenoise) cur = engine.Denoise(cur, outW, outH, denoiseStrength, p, ct);
                if (wantDetail) cur = engine.Enhance(cur, outW, outH, false, out outW, out outH, p, ct);
                if (wantFaces) cur = FaceRestore.Run(engine, cur, outW, outH, fidelity, out facesDone, p, ct);
                return cur;
            }, ct);

            if (!ApplyAiResult(gen, probe, pw, ph, result, outW, outH)) return;
            sw.Stop();
            if (wantFaces && facesDone != faceCount)
                plan[plan.Count - 1] = $"restore {facesDone} face{(facesDone == 1 ? "" : "s")}";
            AiSay($"Autopilot: {string.Join(" + ", plan)}  ·  {engine.Provider}  ·  {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) { AiSay("Cancelled."); }
        catch (Exception ex)
        {
            App.Log("AiAutopilot", ex);
            await MessageAsync("AI", "Autopilot failed.\n\n" + ex.Message);
            AiSay(null);
        }
        finally
        {
            _aiCts?.Dispose();
            _aiCts = null;
            SetAiBusy(false);
        }
    }
}
