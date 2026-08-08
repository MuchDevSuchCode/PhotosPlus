using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Galileo.Models;
using Galileo.Services;
using Windows.Storage;
using Windows.System;

namespace Galileo;

public sealed partial class SlideshowWindow : Window
{
    private readonly AppState _state;
    private readonly AppWindow _appWindow;
    private readonly List<PhotoItem> _photos;
    private List<int> _order;

    private readonly DispatcherTimer _advanceTimer = new();
    private readonly DispatcherTimer _hideControlsTimer = new();
    private readonly Random _rng = new();

    private int _pos;                 // index into _order
    private int _seconds;
    private bool _paused;
    private bool _showingA = true;    // which Image element is currently in front
    private int _slideToken;          // rapid navigation: only the newest load may install its bitmap

    public SlideshowWindow(List<PhotoItem> photos, int startIndex, AppState state)
    {
        InitializeComponent();
        _state = state;
        _photos = photos;
        _seconds = Math.Clamp(state.SlideshowSeconds, 2, 30);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.Title = "Galileo — Slideshow";
        try { _appWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "galileo.ico")); } catch { }
        _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        _order = Enumerable.Range(0, _photos.Count).ToList();
        ShuffleBtn.IsChecked = state.SlideshowShuffle;
        if (state.SlideshowShuffle) Shuffle();

        // Start on the photo the user launched from.
        _pos = Math.Max(0, _order.IndexOf(startIndex));

        _advanceTimer.Interval = TimeSpan.FromSeconds(_seconds);
        _advanceTimer.Tick += (_, _) => Advance(+1);

        _hideControlsTimer.Interval = TimeSpan.FromSeconds(3);
        _hideControlsTimer.Tick += (_, _) => { _hideControlsTimer.Stop(); SetChromeVisible(false); };

        // DispatcherTimers root themselves while running — left ticking, they keep the closed window alive.
        Closed += (_, _) => { _advanceTimer.Stop(); _hideControlsTimer.Stop(); };

        UpdateSpeedText();
        Root.Loaded += OnRootLoaded;
    }

    private bool _started;
    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        Root.Loaded -= OnRootLoaded; // one-shot

        await ShowAtAsync(_pos, animate: false); // show the first slide before timing starts
        _advanceTimer.Start();
        _hideControlsTimer.Start();
        Root.Focus(FocusState.Programmatic);
    }

    // ===================== Core navigation =====================

    private void Advance(int delta)
    {
        if (_photos.Count == 0) return;
        var next = _pos + delta;

        if (next >= _order.Count)
        {
            if (!_state.SlideshowLoop) { Exit(); return; }
            next = 0;
            if (_state.SlideshowShuffle) Shuffle(); // reshuffle each loop
        }
        else if (next < 0)
        {
            next = _order.Count - 1;
        }

        _pos = next;
        _ = ShowAtAsync(_pos, animate: true);
        RestartAdvanceTimer();
    }

    private async System.Threading.Tasks.Task ShowAtAsync(int pos, bool animate)
    {
        var t = ++_slideToken;
        var photo = _photos[_order[pos]];
        var bmp = await LoadAsync(photo);
        if (t != _slideToken) return; // a newer slide won the race — installing this one would flash backwards
        if (bmp is null) return;

        // Incoming goes to the back element; we then crossfade roles.
        var incoming = _showingA ? ImageB : ImageA;
        var outgoing = _showingA ? ImageA : ImageB;
        var incomingTransform = _showingA ? TransformB : TransformA;

        incoming.Source = bmp;
        ResetTransform(incomingTransform);

        Caption.Text = $"{photo.FileName}   ·   {pos + 1} / {_order.Count}";

        var transition = animate ? _state.SlideshowTransition : SlideshowTransition.None;

        if (transition == SlideshowTransition.None)
        {
            incoming.Opacity = 1;
            outgoing.Opacity = 0;
        }
        else
        {
            Crossfade(outgoing, incoming);
        }

        if (transition == SlideshowTransition.KenBurns)
            KenBurns(incomingTransform);

        _showingA = !_showingA;
    }

    private static async System.Threading.Tasks.Task<BitmapImage?> LoadAsync(PhotoItem photo)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(photo.Path);
            var bmp = new BitmapImage();
            // Same cap as the main viewer: decoding a panorama/huge screenshot at full resolution can
            // exceed the GPU's max texture size and crash the render thread. 8000px is well under the
            // ~16384 D3D limit and still sharp for a fullscreen slide.
            const uint maxSide = 8000;
            try
            {
                var props = await file.Properties.GetImagePropertiesAsync();
                uint w = props.Width, h = props.Height;
                if (w > 0 && h > 0 && Math.Max(w, h) > maxSide)
                {
                    bmp.DecodePixelType = DecodePixelType.Logical;
                    if (w >= h) bmp.DecodePixelWidth = (int)maxSide;
                    else bmp.DecodePixelHeight = (int)maxSide;
                }
            }
            catch { } // no properties (odd format) → decode as-is
            using var stream = await file.OpenReadAsync();
            await bmp.SetSourceAsync(stream);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ===================== Animations =====================

    private void Crossfade(UIElement outgoing, UIElement incoming)
    {
        var sb = new Storyboard();
        sb.Children.Add(MakeFade(outgoing, 1, 0, 600));
        sb.Children.Add(MakeFade(incoming, 0, 1, 600));
        sb.Begin();
    }

    private static DoubleAnimation MakeFade(UIElement target, double from, double to, int ms)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        return anim;
    }

    private void KenBurns(CompositeTransform transform)
    {
        var ms = _seconds * 1000 + 600;
        var sb = new Storyboard();
        sb.Children.Add(MakeTransformAnim(transform, "ScaleX", 1.0, 1.15, ms));
        sb.Children.Add(MakeTransformAnim(transform, "ScaleY", 1.0, 1.15, ms));
        sb.Children.Add(MakeTransformAnim(transform, "TranslateX", 0, _rng.Next(-40, 40), ms));
        sb.Children.Add(MakeTransformAnim(transform, "TranslateY", 0, _rng.Next(-30, 30), ms));
        sb.Begin();
    }

    private static DoubleAnimation MakeTransformAnim(CompositeTransform target, string prop, double from, double to, int ms)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, prop);
        return anim;
    }

    private static void ResetTransform(CompositeTransform t)
    {
        t.ScaleX = 1; t.ScaleY = 1; t.TranslateX = 0; t.TranslateY = 0;
    }

    // ===================== Controls =====================

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        _paused = !_paused;
        if (_paused) _advanceTimer.Stop(); else RestartAdvanceTimer();
        // Tooltip and accessible name must track the glyph, or the button announces the wrong action.
        var label = _paused ? "Play (Space)" : "Pause (Space)";
        ToolTipService.SetToolTip(PlayBtn, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayBtn, label);
        PlayIcon.Glyph = _paused ? "" : "";  // Play / Pause
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Advance(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Advance(+1);

    private void Slower_Click(object sender, RoutedEventArgs e) => ChangeSpeed(+1);
    private void Faster_Click(object sender, RoutedEventArgs e) => ChangeSpeed(-1);

    private void ChangeSpeed(int delta)
    {
        _seconds = Math.Clamp(_seconds + delta, 2, 30);
        _state.SlideshowSeconds = _seconds;
        _state.Save();
        UpdateSpeedText();
        RestartAdvanceTimer();
    }

    private void UpdateSpeedText() => SpeedText.Text = $"{_seconds}s";

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _state.SlideshowShuffle = ShuffleBtn.IsChecked == true;
        _state.Save();
        var currentPhoto = _order[_pos];
        if (_state.SlideshowShuffle) Shuffle();
        else _order = Enumerable.Range(0, _photos.Count).ToList();
        _pos = Math.Max(0, _order.IndexOf(currentPhoto));
    }

    private void Shuffle()
    {
        _order = Enumerable.Range(0, _photos.Count).OrderBy(_ => _rng.Next()).ToList();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Exit();

    private void Exit()
    {
        _advanceTimer.Stop();
        _hideControlsTimer.Stop();
        Close();
    }

    private void RestartAdvanceTimer()
    {
        _advanceTimer.Stop();
        if (!_paused)
        {
            _advanceTimer.Interval = TimeSpan.FromSeconds(_seconds);
            _advanceTimer.Start();
        }
    }

    // ===================== Chrome auto-hide =====================

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        SetChromeVisible(true);
        _hideControlsTimer.Stop();
        _hideControlsTimer.Start();
    }

    private void SetChromeVisible(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        Controls.Visibility = v;
        CaptionBar.Visibility = v;
    }

    // ===================== Keyboard =====================

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Exit(); e.Handled = true; break;
            case VirtualKey.Space:
                TogglePlay(); e.Handled = true; break;
            case VirtualKey.Left:
                Advance(-1); e.Handled = true; break;
            case VirtualKey.Right:
                Advance(+1); e.Handled = true; break;
            case VirtualKey.Up:
                ChangeSpeed(-1); e.Handled = true; break;
            case VirtualKey.Down:
                ChangeSpeed(+1); e.Handled = true; break;
        }
    }
}
