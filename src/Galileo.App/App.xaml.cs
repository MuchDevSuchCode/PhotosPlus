using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Galileo.Services;

namespace Galileo;

public partial class App : Application
{
    /// <summary>Process-wide persistent state (hidden/favorite flags, settings).</summary>
    public static AppState State { get; } = AppState.Load();

    /// <summary>Process-wide vault manager. One instance for ALL windows: a per-window manager let a
    /// guest photo window unlock a vault the primary already had open, which re-decrypted (wiping) the
    /// live working folder, and let the process exit thinking no vault was unlocked.</summary>
    public static VaultManager Vaults { get; } = new();

    /// <summary>Crash/error log path: %LocalAppData%\Galileo\logs\error.log.</summary>
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Galileo", "logs", "error.log");

    /// <summary>Diagnostic trail of every thrown exception — the last entry before a hard crash
    /// (0xc000027b XAML failfast) is the real culprit, since those bypass the handlers above.</summary>
    public static string FirstChancePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Galileo", "logs", "firstchance.log");

    private static readonly object _fcLock = new();

    /// <summary>True once this process is registered as the single-instance key holder and its Activated
    /// handler is wired to <see cref="OnRedirected"/> (by Program.Main or, mid-session, by the tray).</summary>
    internal static bool SingleInstanceHooked;

    private Window? _window;

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            Log("UI", e.Exception);
            e.Handled = true; // keep the app alive so the error is logged and visible
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("Task", e.Exception); e.SetObserved(); };

        // Capture every first-chance exception. WinUI render/dispatcher failfasts (0xc000027b)
        // skip the handlers above, but the underlying managed exception is still thrown first —
        // so the tail of this file pinpoints the crash. Best-effort; must never throw.
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            try
            {
                var ex = e.Exception;
                var line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {ex.GetType().FullName} (0x{ex.HResult:X8}): {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}";
                lock (_fcLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FirstChancePath)!);
                    File.AppendAllText(FirstChancePath, line);
                }
            }
            catch { /* diagnostics must never crash the app */ }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        LogInfo($"OnLaunched args=[{string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}]");
        _window = new MainWindow(GetInitialMediaPath());
        _window.Activate();
    }

    /// <summary>
    /// Called (on a background thread) by the single-instance host when another launch is redirected
    /// here. Opens the handed-off file/folder in the existing window and brings it forward.
    /// </summary>
    public void OnRedirected(AppActivationArguments e)
    {
        var path = PathFromActivation(e);
        var newWindow = WantsNewWindow(e);
        LogInfo($"OnRedirected kind={e.Kind} newWindow={newWindow} path={path ?? "(none)"}");
        var window = _window;
        if (window is null) return;
        window.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (newWindow)
                {
                    // "Open in new window": an additional in-process window, instantly — instead of a
                    // separate process paying a full cold start of the self-contained app each time.
                    // secondaryWindow: it's a guest of this process — no tray icon, no crash recovery.
                    var extra = new MainWindow(path, secondaryWindow: true);
                    extra.Activate();
                    return;
                }
                if (window is MainWindow mw)
                {
                    if (!string.IsNullOrEmpty(path)) mw.OpenExternalPath(path!);
                    mw.RestoreFromBackground(); // un-hide if it was minimized to the tray, then bring to front
                }
                else window.Activate();
            }
            catch (Exception ex) { Log("Redirected", ex); }
        });
    }

    /// <summary>True when the redirected launch asked for its own window (`--new-window`).</summary>
    private static bool WantsNewWindow(AppActivationArguments e)
    {
        try
        {
            if (e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs la)
                return la.Arguments.Contains("--new-window", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* ignore */ }
        return false;
    }

    private static string? PathFromActivation(AppActivationArguments e)
    {
        try
        {
            if (e.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fa && fa.Files.Count > 0)
                return fa.Files[0].Path;
            if (e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs la)
                return FirstExistingPath(SplitArgs(la.Arguments));
        }
        catch { /* ignore */ }
        return null;
    }

    private static string? FirstExistingPath(IEnumerable<string> args)
    {
        // Only ever open a real folder or media file the user passed — never a flag (e.g. --background) and
        // never our own executable. The activation command line always starts with the exe path; opening it
        // navigated to the app folder and then "opened" the exe, relaunching Galileo in a fork-bomb loop.
        var self = Environment.ProcessPath;
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a) || a.StartsWith('-')) continue;
            if (!string.IsNullOrEmpty(self) && string.Equals(a, self, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(a)) return a;
            if (File.Exists(a) && (PhotoLibrary.IsSupported(a) || PhotoLibrary.IsMedia(a))) return a;
        }
        return null;
    }

    private static IEnumerable<string> SplitArgs(string commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) yield break;
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ' ' && !inQuotes)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>Appends an exception (with stack trace) to the error log. Never throws.</summary>
    public static void Log(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    /// <summary>Diagnostic info log: %LocalAppData%\Galileo\logs\app.log (lifecycle, sharing, tray — not errors).</summary>
    public static readonly string InfoLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo", "logs", "app.log");

    public static void LogInfo(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InfoLogPath)!);
            File.AppendAllText(InfoLogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* logging must never crash the app */ }
    }

    private static string? GetInitialMediaPath()
    {
        try
        {
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;
                if (Directory.Exists(arg)) return arg;
                if (File.Exists(arg) && (PhotoLibrary.IsSupported(arg) || PhotoLibrary.IsMedia(arg))) return arg;
            }
        }
        catch
        {
            // Ignore malformed arguments — fall back to a normal launch.
        }
        return null;
    }
}
