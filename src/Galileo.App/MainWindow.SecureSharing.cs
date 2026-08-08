using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Galileo.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace Galileo;

public sealed partial class MainWindow
{
    // ===================== Secure peer-to-peer sharing (UI) =====================
    // Crypto/transport live in the Services layer (PeerIdentity, SecureChannel, ShareProtocol,
    // SecureSharing). This is the WinUI glue, dialog-driven (no new XAML panel) to avoid XAML-load risk
    // and keep the surface deniable. Model: a mutual friend list (request → accept), per-vault grants
    // either side can set/revoke, and B browses shares via Ctrl+Alt+V after entering their passphrase.

    private SecureSharing? _sharing;
    private bool _accessLogRevealed;          // access-log entry hidden until Ctrl+Alt+L
    private bool _sharingEventsAttached;
    private Window? _auditWindow;             // the access log opens in its own resizable window (single instance)

    private void RelayUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SecureRelayUrl = RelayUrlBox.Text.Trim();
        _state.Save();
    }

    // Entry points -----------------------------------------------------------

    private async void ManageSharing_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureIdentityAsync()) return;
            await EnsureOnlineAsync();
            await ShowSharingHubAsync();
        }
        catch (Exception ex) { await MessageAsync("Secure sharing", ex.Message); App.Log("Sharing", ex); }
    }

    /// <summary>Ctrl+Alt+V: resume an already-open vault, open a local vault, or browse friends' shares.</summary>
    private async void OpenVaultShortcutAsync()
    {
        try
        {
            // If a vault is already unlocked, just resume it — jump straight back in with no dialog and no
            // re-auth. This is the only way back when the sidebar entry is hidden, and it never touches the
            // sharing connection (so pressing Ctrl+Alt+V can't drop an online session or force a re-login).
            if (_vaults.Current?.WorkingDir is { } wd && Directory.Exists(wd))
            {
                NavigateTo(wd);
                return;
            }

            // No vault is open: offer to unlock one, or browse what friends are sharing.
            if (!SecureSharing.Exists()) { await ShowVaultPickerAsync(); return; }
            var dlg = new ContentDialog
            {
                Title = "Open",
                Content = new TextBlock { Text = "Open one of your local vaults, or browse what friends are sharing with you.", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Shared with me",
                SecondaryButtonText = "Local vault",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
            };
            var r = await dlg.ShowAsync();
            if (r == ContentDialogResult.Primary) await OpenSharesAsync();
            else if (r == ContentDialogResult.Secondary) await ShowVaultPickerAsync();
        }
        catch (Exception ex) { await MessageAsync("Secure sharing", ex.Message); App.Log("Sharing", ex); }
    }

    /// <summary>Command-strip "Share" button (visible inside an unlocked vault): pick which friends get it.</summary>
    private async void VaultShare_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vaults.Current is null) { await MessageAsync("Share", "Unlock a vault first."); return; }
            if (!await EnsureIdentityAsync()) return;
            await EnsureOnlineAsync();
            await ShareCurrentVaultAsync();
        }
        catch (Exception ex) { await MessageAsync("Share", ex.Message); App.Log("Sharing", ex); }
    }

    /// <summary>Unlocks the existing identity (passphrase) or runs first-time setup. Returns true if ready.</summary>
    private async Task<bool> EnsureIdentityAsync()
    {
        if (_sharing is not null) return true;
        if (SecureSharing.Exists())
        {
            var pass = await PromptPassphraseAsync("Secure sharing", "Enter your sharing passphrase.", "Unlock");
            if (pass is null) return false;
            try { _sharing = SecureSharing.Open(pass); }
            catch (CryptographicException) { await MessageAsync("Secure sharing", "Wrong passphrase."); return false; }
        }
        else
        {
            await FirstRunSetupAsync();
            if (_sharing is null) return false;
        }
        AttachSharingEvents();
        return true;
    }

    private void AttachSharingEvents()
    {
        if (_sharing is null || _sharingEventsAttached) return;
        _sharingEventsAttached = true;
        // Friend requests surface in the hub's friends list (Accept there); just log here.
        _sharing.FriendRequestReceived += f => App.Log("Sharing", new Exception($"friend request from {f.Alias} ({f.Uuid})"));
        // Owner locked/revoked while we were browsing them → tear our copy down immediately.
        _sharing.ShareRevokedByOwner += peer => RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            if (_remoteBrowse?.Session.PeerUuid != peer) return;
            var dir = _remoteBrowse.Dir;
            if (string.Equals(_currentFolder, dir, StringComparison.OrdinalIgnoreCase)
                || (_currentFolder?.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ?? false))
                NavigateTo(null); // leaving triggers CleanupRemoteBrowse (secure wipe)
            else
                CleanupRemoteBrowse();
            StatusText.Text = "The owner stopped sharing — the shared files were removed.";
        });
        // Push: the owner we're browsing changed their vault → re-list now instead of waiting for the poll.
        _sharing.VaultChangedByOwner += peer => RootGrid.DispatcherQueue.TryEnqueue(() => OnRemoteVaultChanged(peer));
    }

    /// <summary>A friend we're actively browsing pushed a "vault changed" signal — sync their share at once.</summary>
    private void OnRemoteVaultChanged(Guid peer)
    {
        var rb = _remoteBrowse;
        if (rb is null || rb.Session.PeerUuid != peer) return;
        if (rb.Gate.CurrentCount == 0) return; // a sync is already running; it'll pick up the change
        _ = Task.Run(() => SyncRemoteBrowseAsync(rb, null));
    }

    // First-run: create or recover --------------------------------------------

    private async Task FirstRunSetupAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "Set up secure sharing",
            Content = new TextBlock
            {
                Text = "Create a new identity (you'll get a recovery phrase to back up), or recover one from its "
                     + "phrase. You'll choose a display name friends will see, and a passphrase that protects the "
                     + "identity on this device.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Create new",
            SecondaryButtonText = "Recover",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        var choice = await dlg.ShowAsync();
        if (choice == ContentDialogResult.Primary) await CreateIdentityAsync();
        else if (choice == ContentDialogResult.Secondary) await RecoverIdentityAsync();
    }

    private async Task CreateIdentityAsync()
    {
        var (alias, pass) = await PromptAliasAndPassphraseAsync(null);
        if (alias is null || pass is null) return;
        var (sharing, seed) = SecureSharing.CreateNew(pass, alias);
        _sharing = sharing;
        AttachSharingEvents();
        await ShowRecoveryPhraseAsync(seed);
    }

    private async Task RecoverIdentityAsync()
    {
        var phraseBox = new TextBox { PlaceholderText = "twelve words…", TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = "Enter your recovery phrase.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(phraseBox);
        var dlg = new ContentDialog
        {
            Title = "Recover identity", Content = panel, PrimaryButtonText = "Next",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (!PeerIdentity.ValidateSeedPhrase(phraseBox.Text))
        {
            await MessageAsync("Recover identity", "That doesn't look like a valid recovery phrase.");
            return;
        }
        var (alias, pass) = await PromptAliasAndPassphraseAsync(null);
        if (alias is null || pass is null) return;
        _sharing = SecureSharing.Recover(phraseBox.Text, pass, alias);
        AttachSharingEvents();
    }

    private async Task ShowRecoveryPhraseAsync(string seed)
    {
        var box = new TextBox
        {
            Text = seed, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        var copy = new Button { Content = "Copy phrase" };
        copy.Click += (_, _) => SetClipboard(seed);
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "Write down this recovery phrase and keep it offline. It's the only backup of your identity.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        panel.Children.Add(copy);
        await new ContentDialog { Title = "Your recovery phrase", Content = panel, CloseButtonText = "I've saved it", XamlRoot = RootGrid.XamlRoot }.ShowAsync();
    }

    // The hub ----------------------------------------------------------------

    private async Task ShowSharingHubAsync()
    {
        _accessLogRevealed = false;
        while (_sharing is not null)
        {
            string? action = null;
            var dlg = new ContentDialog { Title = "Secure sharing", CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot };
            var panel = new StackPanel { Spacing = 8, MinWidth = 360 };

            panel.Children.Add(new TextBlock
            {
                Text = (_sharing.IsOnline ? "● Online" : "○ Offline (relay unreachable)") + $"   ·   You are \"{_sharing.Alias}\"",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(_sharing.IsOnline ? Microsoft.UI.Colors.LimeGreen : Microsoft.UI.Colors.Gray),
                FontWeight = FontWeights.SemiBold,
            });

            Button Hub(string text, string act)
            {
                var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
                b.Click += (_, _) => { action = act; dlg.Hide(); };
                return b;
            }

            panel.Children.Add(Hub("Show my ID & fingerprint", "show"));
            panel.Children.Add(Hub("Add a friend…", "addfriend"));
            if (_vaults.Current is not null)
                panel.Children.Add(Hub($"Share \"{_vaults.Current.Name}\" with friends…", "grant"));
            panel.Children.Add(Hub("Refresh", "refresh"));

            // Friends
            var friends = _sharing.Friends.ToList();
            if (friends.Count > 0)
            {
                panel.Children.Add(new TextBlock { Text = "Friends", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
                foreach (var f in friends)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    var label = string.IsNullOrWhiteSpace(f.Alias) ? f.Uuid[..8] : f.Alias;
                    if (f.IsLinked)
                    {
                        row.Children.Add(new TextBlock { Text = "● " + label, VerticalAlignment = VerticalAlignment.Center, MinWidth = 140 });
                        row.Children.Add(Hub("Browse", "browse:" + f.Uuid));
                        row.Children.Add(Hub("Unfriend", "unfriend:" + f.Uuid));
                    }
                    else if (f.Status == "pending_in")
                    {
                        row.Children.Add(new TextBlock { Text = $"{label} wants to connect", VerticalAlignment = VerticalAlignment.Center, MinWidth = 140 });
                        row.Children.Add(Hub("Accept", "accept:" + f.Uuid));
                        row.Children.Add(Hub("Decline", "unfriend:" + f.Uuid));
                    }
                    else // pending_out
                    {
                        row.Children.Add(new TextBlock { Text = $"{label} — request sent", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
                        row.Children.Add(Hub("Cancel", "unfriend:" + f.Uuid));
                    }
                    panel.Children.Add(row);
                }
            }

            if (_accessLogRevealed) panel.Children.Add(Hub("View access log", "audit"));

            var regen = Hub("Delete identity & regenerate…", "regen");
            regen.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            regen.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(regen);

            panel.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.L && IsCtrlDown() && IsAltDown() && !_accessLogRevealed)
                { _accessLogRevealed = true; action = "refresh"; dlg.Hide(); }
            };

            dlg.Content = new ScrollViewer { Content = panel, MaxHeight = 520 };
            await dlg.ShowAsync();

            if (action is null) break;
            if (action == "show") await ShowMyIdentityAsync();
            else if (action == "addfriend") await AddFriendAsync();
            else if (action == "grant") await ShareCurrentVaultAsync();
            else if (action == "audit") await ShowAuditAsync();
            else if (action == "regen") await RegenerateIdentityAsync();
            else if (action == "refresh") { }
            else if (action.StartsWith("accept:") && Guid.TryParse(action[7..], out var au)) await DoAsync(() => _sharing!.AcceptFriendAsync(au), "Accept");
            else if (action.StartsWith("unfriend:") && Guid.TryParse(action[9..], out var uu)) await DoAsync(() => _sharing!.UnfriendAsync(uu), "Unfriend");
            else if (action.StartsWith("browse:") && Guid.TryParse(action[7..], out var bu)) await BrowsePeerAsync(bu);

            if (_sharing is null) break;
        }
    }

    private async Task DoAsync(Func<Task> op, string title)
    {
        try { await op(); } catch (Exception ex) { await MessageAsync(title, ex.Message); }
    }

    private async Task ShowMyIdentityAsync()
    {
        if (_sharing is null) return;
        var uuid = _sharing.Identity.Uuid.ToString();
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock { Text = $"Display name: {_sharing.Alias}" });
        panel.Children.Add(new TextBlock { Text = "Your ID (share this so friends can add you):" });
        panel.Children.Add(new TextBox { Text = uuid, IsReadOnly = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
        panel.Children.Add(new TextBlock { Text = "Safety number (verify out-of-band it matches on both devices):", Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(new TextBox { Text = _sharing.Identity.Fingerprint, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
        var copy = new Button { Content = "Copy ID" };
        copy.Click += (_, _) => SetClipboard(uuid);
        panel.Children.Add(copy);
        await new ContentDialog { Title = "My identity", Content = panel, CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot }.ShowAsync();
    }

    private async Task AddFriendAsync()
    {
        if (_sharing is null) return;
        if (!_sharing.IsOnline) { await MessageAsync("Add a friend", "You're offline — check the relay URL in Settings."); return; }
        var idBox = new TextBox { PlaceholderText = "friend's ID (a UUID)" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = "Send a friend request by their ID. They must accept before you're linked." });
        panel.Children.Add(idBox);
        var dlg = new ContentDialog
        {
            Title = "Add a friend", Content = panel, PrimaryButtonText = "Send request",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (!Guid.TryParse(idBox.Text.Trim(), out var to)) { await MessageAsync("Add a friend", "That's not a valid ID."); return; }
        try { await _sharing.SendFriendRequestAsync(to); await MessageAsync("Add a friend", "Request sent. They'll appear as linked once they accept."); }
        catch (Exception ex) { await MessageAsync("Add a friend", ex.Message); }
    }

    private async Task ShareCurrentVaultAsync()
    {
        if (_sharing is null || _vaults.Current is null) { await MessageAsync("Share", "Unlock a vault first."); return; }
        var vaultId = _vaults.Current.Id;

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock { Text = $"Share “{_vaults.Current.Name}” with:", TextWrapping = TextWrapping.Wrap });
        var rowsHost = new StackPanel { Spacing = 8 };
        panel.Children.Add(rowsHost);

        void AddAccessRow(string label, Guid id, bool direct)
        {
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var combo = new ComboBox { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
            combo.Items.Add(new ComboBoxItem { Content = "No access" });
            combo.Items.Add(new ComboBoxItem { Content = "Read only" });
            combo.Items.Add(new ComboBoxItem { Content = "Read & write" });
            combo.SelectedIndex = (int)_sharing!.AccessFor(vaultId, id); // None=0, Read=1, Write=2
            combo.SelectionChanged += (_, _) =>
            {
                try
                {
                    if (direct) _sharing!.SetGrantById(vaultId, id, (ShareAccess)combo.SelectedIndex);
                    else _sharing!.SetGrant(vaultId, id, (ShareAccess)combo.SelectedIndex);
                }
                catch { }
            };
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            rowsHost.Children.Add(row);
        }

        void Rebuild()
        {
            rowsHost.Children.Clear();
            foreach (var f in _sharing!.LinkedFriends)
                AddAccessRow(string.IsNullOrWhiteSpace(f.Alias) ? f.Uuid[..8] : f.Alias, Guid.Parse(f.Uuid), false);
            foreach (var idStr in _sharing!.DirectGrantIds(vaultId))
                if (Guid.TryParse(idStr, out var g)) AddAccessRow($"Device {idStr[..8]}…", g, true);
            if (rowsHost.Children.Count == 0)
                rowsHost.Children.Add(new TextBlock { Text = "No one yet — add a friend, or grant a device by ID below.", Opacity = 0.6, FontSize = 12 });
        }
        Rebuild();

        // Grant another of your own devices (e.g. your phone) directly by its ID — no friend link needed.
        panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Margin = new Thickness(0, 4, 0, 4) });
        panel.Children.Add(new TextBlock { Text = "Grant a device by ID:", FontWeight = FontWeights.SemiBold });
        var idBox = new TextBox { PlaceholderText = "Paste the device's ID (UUID)" };
        var idCombo = new ComboBox { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
        idCombo.Items.Add(new ComboBoxItem { Content = "Read only" });
        idCombo.Items.Add(new ComboBoxItem { Content = "Read & write" });
        idCombo.SelectedIndex = 0;
        var addErr = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed), Visibility = Visibility.Collapsed, FontSize = 12 };
        var addBtn = new Button { Content = "Grant" };
        addBtn.Click += (_, _) =>
        {
            addErr.Visibility = Visibility.Collapsed;
            if (!Guid.TryParse(idBox.Text.Trim(), out var g)) { addErr.Text = "Enter a valid device ID."; addErr.Visibility = Visibility.Visible; return; }
            try
            {
                _sharing!.SetGrantById(vaultId, g, idCombo.SelectedIndex == 1 ? ShareAccess.Write : ShareAccess.Read);
                idBox.Text = "";
                Rebuild();
            }
            catch (Exception ex) { addErr.Text = ex.Message; addErr.Visibility = Visibility.Visible; }
        };
        var addRow = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } }, ColumnSpacing = 8 };
        Grid.SetColumn(idCombo, 0); Grid.SetColumn(addBtn, 1);
        addRow.Children.Add(idCombo); addRow.Children.Add(addBtn);
        panel.Children.Add(idBox);
        panel.Children.Add(addRow);
        panel.Children.Add(addErr);

        panel.Children.Add(new TextBlock { Text = "Anyone here can browse this vault live while it's unlocked and you're online. Read & write also lets them add files/folders. Grant-by-ID is for your own devices. Revoke any time (set to No access).", Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        await new ContentDialog { Title = "Share vault", Content = new ScrollViewer { Content = panel, MaxHeight = 560 }, CloseButtonText = "Done", XamlRoot = RootGrid.XamlRoot }.ShowAsync();
    }

    // Browsing — present a friend's shared vault in the real file explorer ----
    // Files stream into a temp working folder (read-only copy, wiped on the next browse / next launch);
    // the explorer then shows them with thumbnails, gallery, viewer — exactly like any other folder.

    private sealed class RemoteBrowse
    {
        public required SecureSession Session { get; set; } // replaced if the session dies and we reconnect
        public required string Dir { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        // full temp path (lowercased) -> shared object id, so opening a temp file maps back to the share.
        public required Dictionary<string, string> PathToId { get; init; }
        // Serializes list/download so an initial sync and a refresh can't race the session's request/response.
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
    private RemoteBrowse? _remoteBrowse;
    private string? _currentRemoteViewId; // the shared file currently open in the viewer (for view/close audit)
    private readonly DispatcherTimer _remoteSyncTimer = new() { Interval = TimeSpan.FromSeconds(8) }; // live auto-sync
    // Auto-disconnect a remote browse after a configurable idle period (B's privacy/safety setting).
    private readonly DispatcherTimer _remoteIdleTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private DateTimeOffset _lastRemoteActivity;

    /// <summary>Disconnect the open shared browse once it's been idle for the user's configured timeout.</summary>
    private void RemoteIdleTick()
    {
        if (_remoteBrowse is null) { _remoteIdleTimer.Stop(); return; }
        var mins = _state.RemoteIdleDisconnectMinutes;
        if (mins <= 0) { _remoteIdleTimer.Stop(); return; }
        if ((DateTimeOffset.Now - _lastRemoteActivity).TotalMinutes < mins) return;
        _remoteIdleTimer.Stop();
        StatusText.Text = "Disconnected from the shared vault after being idle.";
        NavigateTo(null); // tears down the browse (signals the owner) and securely wipes the downloaded copies
    }

    /// <summary>Securely wipe any leftover remote-browse temp folders (called at startup and on exit).</summary>
    private static void WipeShareTempDirs()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "GalileoShare");
            if (Directory.Exists(root)) VaultCrypto.WipeDirectory(root);
        }
        catch { /* best effort */ }
    }

    /// <summary>If we're navigating out of the current shared-browse folder, tear it down and securely wipe
    /// the downloaded copies — decrypted shared files never linger once you leave.</summary>
    private void CheckLeftRemoteBrowse(string? target)
    {
        var rb = _remoteBrowse;
        if (rb is null) return;
        var inside = target is not null &&
            (string.Equals(target, rb.Dir, StringComparison.OrdinalIgnoreCase)
             || target.StartsWith(rb.Dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (!inside) CleanupRemoteBrowse();
    }

    // Hub "Browse <friend>" → open that friend's shared vault in the explorer.
    private async Task BrowsePeerAsync(Guid peer)
    {
        if (_sharing is null) return;
        if (!_sharing.IsOnline) { await MessageAsync("Browse", "You're offline."); return; }
        SecureSession? session = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            session = await _sharing.ConnectToPeerAsync(peer, cts.Token);
            // Announce we're the Windows client (before the first list) so the owner's access log tags us.
            await ShareProtocol.ClientHelloAsync(_sharing.Relay, session, "windows", cts.Token);
            var listing = await ShareProtocol.ListAsync(_sharing.Relay, session, cts.Token);
            if (listing.Items.Count == 0) { session.Dispose(); await MessageAsync("Browse", "That friend isn't sharing anything right now."); return; }
            StartRemoteBrowse(session, listing);
            session = null; // ownership handed to _remoteBrowse
        }
        catch (Exception ex) { await MessageAsync("Browse", "Couldn't reach that friend (online + sharing?). " + ex.Message); }
        finally { session?.Dispose(); }
    }

    /// <summary>Ctrl+Alt+V → "Shared with me": find which friends are sharing, pick one, open it in the explorer.</summary>
    private async Task OpenSharesAsync()
    {
        if (!await EnsureIdentityAsync()) return;
        await EnsureOnlineAsync();
        if (_sharing is null) return;
        if (!_sharing.IsOnline) { await MessageAsync("Shared with me", "You're offline — check the relay URL in Settings."); return; }

        var found = new List<(Friend f, SecureSession s, SharedListing l)>();
        foreach (var f in _sharing.LinkedFriends)
        {
            if (!Guid.TryParse(f.Uuid, out var fid)) continue;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var s = await _sharing.ConnectToPeerAsync(fid, cts.Token);
                // Announce we're the Windows client (before the first list) so the owner's access log tags us.
                await ShareProtocol.ClientHelloAsync(_sharing.Relay, s, "windows", cts.Token);
                var l = await ShareProtocol.ListAsync(_sharing.Relay, s, cts.Token);
                if (l.Items.Count == 0) { s.Dispose(); continue; }
                found.Add((f, s, l));
            }
            catch { /* friend offline or not sharing — skip */ }
        }

        if (found.Count == 0) { await MessageAsync("Shared with me", "No friends are sharing anything with you right now."); return; }

        var pick = found.Count == 1 ? 0 : await ChooseShareAsync(found);
        if (pick < 0) { foreach (var x in found) x.s.Dispose(); return; }
        for (var i = 0; i < found.Count; i++) if (i != pick) found[i].s.Dispose();
        StartRemoteBrowse(found[pick].s, found[pick].l);
    }

    private async Task<int> ChooseShareAsync(List<(Friend f, SecureSession s, SharedListing l)> found)
    {
        var result = -1;
        var dlg = new ContentDialog { Title = "Shared with me", CloseButtonText = "Cancel", XamlRoot = RootGrid.XamlRoot };
        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = "Open a shared vault:" });
        for (var i = 0; i < found.Count; i++)
        {
            var idx = i;
            var who = string.IsNullOrWhiteSpace(found[i].f.Alias) ? found[i].f.Uuid[..8] : found[i].f.Alias;
            var b = new Button { Content = $"{who} — {found[i].l.VaultName}  ({found[i].l.Items.Count} files)", HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += (_, _) => { result = idx; dlg.Hide(); };
            panel.Children.Add(b);
        }
        dlg.Content = panel;
        await dlg.ShowAsync();
        return result;
    }

    // Open a friend's shared vault in the explorer. Files stream into a temp folder; refreshing (F5)
    // re-lists against the live vault so adds/deletes/changes on the owner's side show up.
    private void StartRemoteBrowse(SecureSession session, SharedListing listing)
    {
        CleanupRemoteBrowse(); // tear down any previous browse
        var dir = Path.Combine(Path.GetTempPath(), "GalileoShare", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _remoteBrowse = new RemoteBrowse
        {
            Session = session, Dir = dir, Cts = new CancellationTokenSource(),
            PathToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

        ShowExplorer();
        NavigateTo(dir);
        // (The "windows" client hello is sent during connect, before the first list — see BrowsePeerAsync.)
        _ = Task.Run(() => SyncRemoteBrowseAsync(_remoteBrowse, listing));
        _remoteSyncTimer.Start(); // keep it live: auto-sync the owner's adds/deletes every few seconds
        _lastRemoteActivity = DateTimeOffset.Now;
        if (_state.RemoteIdleDisconnectMinutes > 0) _remoteIdleTimer.Start(); // idle auto-disconnect, if enabled
    }

    /// <summary>Re-sync the open shared folder against the owner's live vault (F5): pull new/changed files,
    /// remove ones the owner deleted, drop stale partials.</summary>
    private void RefreshRemoteBrowse()
    {
        var rb = _remoteBrowse;
        if (rb is null) return;
        StatusText.Text = "Refreshing shared vault…";
        _ = Task.Run(() => SyncRemoteBrowseAsync(rb, null)); // null → re-list from the owner
    }

    /// <summary>Periodic auto-sync so the owner's adds/deletes show up without a manual refresh.</summary>
    private void RemoteSyncTick()
    {
        var rb = _remoteBrowse;
        if (rb is null) { _remoteSyncTimer.Stop(); return; }
        if (rb.Gate.CurrentCount == 0) return; // a sync is already in progress
        _ = Task.Run(() => SyncRemoteBrowseAsync(rb, null));
    }

    private async Task SyncRemoteBrowseAsync(RemoteBrowse rb, SharedListing? listing)
    {
        var relay = _sharing?.Relay;
        if (relay is null) return;
        await rb.Gate.WaitAsync();
        try
        {
            // Re-list from the owner unless we were handed a fresh listing (initial open). If the session
            // has died (owner reconnected / a relay drop), re-establish it once and retry — otherwise a
            // refresh would silently keep stale files.
            if (listing is null)
            {
                try
                {
                    using var lcts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    listing = await ShareProtocol.ListAsync(relay, rb.Session, lcts.Token);
                }
                catch
                {
                    try
                    {
                        using var rcts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                        var fresh = await _sharing!.ConnectToPeerAsync(rb.Session.PeerUuid, rcts.Token);
                        var old = rb.Session; rb.Session = fresh; try { old.Dispose(); } catch { }
                        listing = await ShareProtocol.ListAsync(relay, fresh, rcts.Token);
                    }
                    catch { return; } // owner offline — keep what we have
                }
            }
            App.LogInfo($"remote sync: owner lists {listing.Items.Count} item(s) for {listing.VaultName}");
            var dir = rb.Dir;
            var sep = Path.DirectorySeparatorChar;

            // Item names come off the wire from the OWNER's machine — never trust them as paths. A
            // hostile/compromised peer sending "..\..\Startup\evil.exe" or a rooted/drive path would
            // otherwise write outside our temp browse folder (arbitrary file write). Resolve each name
            // and accept it only if it lands strictly inside the browse dir.
            string? SafeDest(string wireName)
            {
                if (string.IsNullOrEmpty(wireName) || wireName.Contains(':')) return null;
                try
                {
                    var full = Path.GetFullPath(Path.Combine(dir, wireName.Replace('/', sep)));
                    return full.StartsWith(dir.TrimEnd(sep) + sep, StringComparison.OrdinalIgnoreCase) ? full : null;
                }
                catch { return null; }
            }

            // Desired state: dest path -> object id.
            var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in listing.Items)
                if (SafeDest(it.Name) is { } safe) desired[safe] = it.Id;

            // Remove files the owner deleted, and any leftover partials.
            var removed = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || !desired.ContainsKey(f))
                        try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); removed++; } catch { }
            }
            catch { }
            if (removed > 0) App.LogInfo($"remote sync: removed {removed} file(s) the owner deleted");

            rb.PathToId.Clear();
            foreach (var kv in desired) rb.PathToId[kv.Key] = kv.Value;

            var items = listing.Items;
            var name = listing.VaultName;
            var done = 0;
            foreach (var it in items)
            {
                if (rb.Cts.IsCancellationRequested) return;
                if (SafeDest(it.Name) is not { } dest) continue; // hostile/malformed name — never write it
                // Download only what we don't already have at the right size (handles adds + changes).
                if (!File.Exists(dest) || new FileInfo(dest).Length != it.Size)
                {
                    var part = dest + ".part";
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        using (var fs = File.Create(part))
                        {
                            try { File.SetAttributes(part, FileAttributes.Hidden); } catch { } // hide the partial from the view
                            await ShareProtocol.FetchAsync(relay, rb.Session, it.Id, it.Size, fs, null, rb.Cts.Token);
                        }
                        File.Move(part, dest, overwrite: true);
                        try { File.SetAttributes(dest, FileAttributes.Normal); } catch { }
                    }
                    catch { try { File.Delete(part); } catch { } } // clear the partial if the file is gone / failed
                }
                done++;
                var d = done; var total = items.Count;
                RootGrid.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_remoteBrowse?.Dir != dir) return; // a newer browse replaced us
                    if (string.Equals(_currentFolder, dir, StringComparison.OrdinalIgnoreCase)) RefreshFolderIncremental();
                    StatusText.Text = d < total ? $"{name}: syncing {d}/{total}…" : $"{name} — {total} file(s) (shared, read-only)";
                });
            }
            // Final refresh so deletions disappear even if nothing was downloaded.
            RootGrid.DispatcherQueue.TryEnqueue(() =>
            {
                if (_remoteBrowse?.Dir == dir && string.Equals(_currentFolder, dir, StringComparison.OrdinalIgnoreCase))
                    RefreshFolderIncremental();
            });
        }
        catch (Exception ex) { App.Log("RemoteSync", ex); }
        finally { rb.Gate.Release(); }
    }

    private void CleanupRemoteBrowse()
    {
        _remoteSyncTimer.Stop();
        _remoteIdleTimer.Stop();
        NoteRemoteView(null); // close out any in-viewer access first
        var rb = _remoteBrowse;
        _remoteBrowse = null;
        if (rb is null) return;
        try { rb.Cts.Cancel(); } catch { }
        _ = FinishRemoteBrowseAsync(rb); // signal the owner we left, then dispose + securely wipe
    }

    private async Task FinishRemoteBrowseAsync(RemoteBrowse rb)
    {
        // Tell the host we've closed the folder (logged in their access log) before tearing the session down.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ShareProtocol.EndBrowseAsync(_sharing!.Relay, rb.Session, cts.Token);
        }
        catch { /* best effort */ }
        try { rb.Session.Dispose(); } catch { }
        rb.Cts.Dispose();
        rb.Gate.Dispose();
        try { if (Directory.Exists(rb.Dir)) VaultCrypto.WipeDirectory(rb.Dir); } catch { }
    }

    /// <summary>Call when the actively-viewed file changes (image load / video open), or null when leaving
    /// the viewer. If the file belongs to the current remote browse, signal the owner so their access log
    /// records what was actually viewed (open) and when it was closed (duration).</summary>
    private void NoteRemoteView(string? path)
    {
        var rb = _remoteBrowse;
        string? newId = null;
        if (rb is not null && path is not null && rb.PathToId.TryGetValue(path, out var id)) newId = id;
        if (newId == _currentRemoteViewId) return; // unchanged

        var prev = _currentRemoteViewId;
        _currentRemoteViewId = newId;
        if (rb is null) { _currentRemoteViewId = null; return; }

        if (prev is not null) _ = SafeSignalAsync(rb.Session, prev, open: false);
        if (newId is not null) _ = SafeSignalAsync(rb.Session, newId, open: true);
    }

    private async Task SafeSignalAsync(SecureSession session, string id, bool open)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (open) await ShareProtocol.ViewAsync(_sharing!.Relay, session, id, cts.Token);
            else await ShareProtocol.CloseAsync(_sharing!.Relay, session, id, cts.Token);
        }
        catch { /* best effort */ }
    }

    // ---- viewer write actions (need a read+write grant on the owner; the owner enforces it) ------------

    private static string RemoteRel(RemoteBrowse rb, string folder)
    {
        if (string.Equals(folder, rb.Dir, StringComparison.OrdinalIgnoreCase)) return "";
        var rel = Path.GetRelativePath(rb.Dir, folder).Replace(Path.DirectorySeparatorChar, '/');
        return rel is "." ? "" : rel;
    }

    private static string JoinRel(string a, string b) => string.IsNullOrEmpty(a) ? b : a + "/" + b;

    private static string FriendlyWriteError(Exception ex) =>
        ex.Message.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
            ? "you have read-only access to this share." : ex.Message;

    /// <summary>Create a folder in the shared vault we're browsing. Throws on rejection (e.g. read-only).</summary>
    private async Task RemoteCreateFolderAsync(string parentFolder, string name)
    {
        var rb = _remoteBrowse ?? throw new InvalidOperationException("Not browsing a share.");
        if (_sharing is null) throw new InvalidOperationException("You're offline.");
        var path = JoinRel(RemoteRel(rb, parentFolder), name);
        await rb.Gate.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ShareProtocol.CreateFolderAsync(_sharing.Relay, rb.Session, path, cts.Token);
        }
        finally { rb.Gate.Release(); }
    }

    /// <summary>Upload local files/folders into the current remote-browse folder (recurses folders; the owner
    /// auto-creates intermediate directories). Refreshes the view when done.</summary>
    private async Task RemoteUploadItemsAsync(string parentFolder, IReadOnlyList<string> localPaths)
    {
        var rb = _remoteBrowse; if (rb is null || _sharing is null) return;
        var parentRel = RemoteRel(rb, parentFolder);
        var work = new List<(string vaultPath, string local)>();
        foreach (var p in localPaths)
        {
            if (File.Exists(p)) work.Add((JoinRel(parentRel, Path.GetFileName(p)), p));
            else if (Directory.Exists(p))
            {
                var baseName = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                {
                    var within = Path.GetRelativePath(p, f).Replace(Path.DirectorySeparatorChar, '/');
                    work.Add((JoinRel(parentRel, baseName + "/" + within), f));
                }
            }
        }
        if (work.Count == 0) return;

        await rb.Gate.WaitAsync();
        var ok = false;
        try
        {
            var n = 0;
            foreach (var (vp, lf) in work)
            {
                StatusText.Text = $"Uploading {Path.GetFileName(lf)} ({++n}/{work.Count})…";
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                using var fs = File.OpenRead(lf);
                await ShareProtocol.UploadAsync(_sharing.Relay, rb.Session, vp, fs, null, cts.Token);
            }
            ok = true;
            StatusText.Text = work.Count == 1 ? "Uploaded 1 file to the share." : $"Uploaded {work.Count} files to the share.";
        }
        catch (Exception ex) { StatusText.Text = "Upload failed: " + FriendlyWriteError(ex); }
        finally { rb.Gate.Release(); }
        if (ok) RefreshRemoteBrowse();
    }

    /// <summary>Delete shared entries (by their full temp paths) from the owner's vault, then refresh.</summary>
    private async Task RemoteDeleteAsync(IReadOnlyList<string> fullPaths)
    {
        var rb = _remoteBrowse; if (rb is null || _sharing is null) return;
        var ids = fullPaths.Select(p => rb.PathToId.TryGetValue(p, out var id) ? id : null).Where(id => id is not null).ToList();
        if (ids.Count == 0) return;
        await rb.Gate.WaitAsync();
        var ok = false;
        try
        {
            foreach (var id in ids)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await ShareProtocol.DeleteRemoteAsync(_sharing.Relay, rb.Session, id!, cts.Token);
            }
            ok = true;
            StatusText.Text = ids.Count == 1 ? "Deleted 1 item from the share." : $"Deleted {ids.Count} items from the share.";
        }
        catch (Exception ex) { StatusText.Text = "Delete failed: " + FriendlyWriteError(ex); }
        finally { rb.Gate.Release(); }
        if (ok) RefreshRemoteBrowse();
    }

    /// <summary>The viewer (un)favorited a file from the share — tell the owner so it lands in their access
    /// log. Fire-and-forget; only fires for files that belong to the current remote browse.</summary>
    private void NoteRemoteFavorite(string path, bool fav)
    {
        var rb = _remoteBrowse;
        if (rb is null || _sharing is null || !rb.PathToId.TryGetValue(path, out var id)) return;
        var session = rb.Session;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await ShareProtocol.FavoriteAsync(_sharing.Relay, session, id, fav, cts.Token);
            }
            catch { /* best effort */ }
        });
    }

    // Online + regenerate ----------------------------------------------------

    private bool _sharingOnlineDeclined; // skip re-prompting after the user cancels once this session

    /// <summary>Called when a vault is unlocked: bring sharing online so friends can reach it. Silent if the
    /// identity is already loaded; otherwise a single passphrase prompt (skipped if declined this session,
    /// or if no sharing identity exists — preserving deniability).</summary>
    private async Task MaybeBringSharingOnlineAsync()
    {
        try
        {
            if (_sharing is not null) { await EnsureOnlineAsync(); return; }
            if (_sharingOnlineDeclined || !SecureSharing.Exists()) return;

            var pass = await PromptPassphraseAsync("Secure sharing",
                "Bring secure sharing online so friends can access the vaults you share with them? "
                + "Enter your sharing passphrase, or cancel to skip.", "Go online");
            if (pass is null) { _sharingOnlineDeclined = true; return; }
            try { _sharing = SecureSharing.Open(pass); }
            catch (CryptographicException) { await MessageAsync("Secure sharing", "Wrong passphrase."); return; }
            AttachSharingEvents();
            await EnsureOnlineAsync();
        }
        catch (Exception ex) { App.Log("Sharing", ex); }
    }

    private async Task EnsureOnlineAsync()
    {
        if (_sharing is null || _sharing.IsOnline) return;
        var url = _state.SecureRelayUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        // Serve the currently-unlocked vault to a friend only while it's granted to them (checked live).
        Func<Guid, IShareSource?> shareForPeer = peer =>
            new GrantGatedSource(
                () => _vaults.Current is not null && _sharing is not null && _sharing.IsGranted(_vaults.Current.Id, peer),
                () => _vaults.Current is not null && _sharing is not null && _sharing.CanWrite(_vaults.Current.Id, peer),
                new LiveCurrentVaultSource(this));
        App.LogInfo($"sharing: going online to {url}");
        try { await _sharing.GoOnlineAsync(url, shareForPeer); App.LogInfo("sharing: online"); }
        catch (Exception ex) { App.LogInfo("sharing: go-online failed: " + ex.Message); }
    }

    private async Task RegenerateIdentityAsync()
    {
        if (_sharing is null) return;
        var confirm = new ContentDialog
        {
            Title = "Delete identity & regenerate",
            Content = new TextBlock
            {
                Text = "This permanently erases your current identity, alias, friends and shares from this device "
                     + "and creates a brand-new identity with a new ID. Friends will no longer recognise you. This "
                     + "can't be undone unless you backed up the old recovery phrase.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Delete & regenerate",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        await _sharing.GoOfflineAsync();
        _sharing.Dispose();
        _sharing = null;
        _sharingEventsAttached = false;
        SecureSharing.DeleteStore();

        await CreateIdentityAsync();
        await EnsureOnlineAsync();
    }

    // Access log -------------------------------------------------------------

    private async Task ShowAuditAsync()
    {
        if (_sharing is null) return;
        var url = _state.SecureRelayUrl;
        if (string.IsNullOrWhiteSpace(url)) { await MessageAsync("Access log", "Set a relay server URL in Settings first."); return; }

        const string TimeFmt = "yyyy-MM-dd HH:mm:ss";

        // Turn a fresh batch of audit records into display rows. Names/paths are re-resolved on every call
        // so the log tracks whichever vault is currently unlocked. Newest-first.
        List<(DateTimeOffset t, string title, string detail, string? path)> BuildRows(IReadOnlyList<RelayClient.AuditRecord> records)
        {
            var names = new Dictionary<string, string>();
            if (_vaults.Current is not null)
                foreach (var en in _vaults.Current.ShareEntries()) names[en.BlobId] = en.RelPath;

            string PeerName(Guid v)
            {
                var n = _sharing!.Friends.FirstOrDefault(p => p.Uuid == v.ToString())?.Alias;
                return string.IsNullOrWhiteSpace(n) ? v.ToString()[..8] : n!;
            }
            string FileName(string id) => names.TryGetValue(id, out var f) ? f : "(item no longer shared)";
            string? PathFor(string objectId)
            {
                var wd = _vaults.Current?.WorkingDir;
                if (wd is null || !names.TryGetValue(objectId, out var rel)) return null;
                var p = Path.Combine(wd, rel.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(p) ? p : null;
            }

            var asc = records.OrderBy(r => r.Time).ToList();
            var openAt = new Dictionary<string, DateTimeOffset>();
            var rows = new List<(DateTimeOffset t, string title, string detail, string? path)>();
            // Per-viewer client app, learned from the "client" hello sent at the start of each session; every
            // later row from that viewer is tagged literally with "(Windows)" / "(Android)". Defaults to (Windows).
            var appByViewer = new Dictionary<Guid, string>();
            string AppTag(Guid v) => " (" + (appByViewer.TryGetValue(v, out var a) ? a : "Windows") + ")";
            foreach (var r in asc)
            {
                var who = PeerName(r.Viewer);
                var key = r.Viewer + "|" + r.ObjectId;
                switch (r.Action)
                {
                    case "list":
                        rows.Add((r.Time, "Opened your shared vault", $"by {who}{AppTag(r.Viewer)}   ·   {r.Time.LocalDateTime.ToString(TimeFmt)}", null));
                        break;
                    case "browse_end":
                        rows.Add((r.Time, "Closed your shared vault", $"by {who}{AppTag(r.Viewer)}   ·   {r.Time.LocalDateTime.ToString(TimeFmt)}", null));
                        break;
                    case "client":
                    {
                        var app = r.ObjectId.Length > 0 ? char.ToUpperInvariant(r.ObjectId[0]) + r.ObjectId[1..] : "Windows";
                        appByViewer[r.Viewer] = app;
                        rows.Add((r.Time, $"Connected from the {app} app", $"by {who}   ·   {r.Time.LocalDateTime.ToString(TimeFmt)}", null));
                        break;
                    }
                    case "fetch":
                        rows.Add((r.Time, FileName(r.ObjectId), $"downloaded by {who}{AppTag(r.Viewer)}   ·   {r.Time.LocalDateTime.ToString(TimeFmt)}", PathFor(r.ObjectId)));
                        break;
                    case "favorite":
                        rows.Add((r.Time, FileName(r.ObjectId), $"★ favorited by {who}{AppTag(r.Viewer)}   ·   {r.Time.LocalDateTime.ToString(TimeFmt)}", PathFor(r.ObjectId)));
                        break;
                    case "unfavorite":
                        rows.Add((r.Time, FileName(r.ObjectId), $"☆ unfavorited by {who}{AppTag(r.Viewer)}   ·   {r.Time.LocalDateTime.ToString(TimeFmt)}", PathFor(r.ObjectId)));
                        break;
                    case "open":
                        openAt[key] = r.Time;
                        break;
                    case "close" when openAt.TryGetValue(key, out var t):
                        rows.Add((t, FileName(r.ObjectId),
                            $"viewed by {who}{AppTag(r.Viewer)}   ·   {t.LocalDateTime.ToString(TimeFmt)} → {r.Time.LocalDateTime:HH:mm:ss}  ({FormatDuration(r.Time - t)})", PathFor(r.ObjectId)));
                        openAt.Remove(key);
                        break;
                }
            }
            foreach (var kv in openAt) // opens still without a close
            {
                var parts = kv.Key.Split('|', 2);
                Guid.TryParse(parts[0], out var v);
                rows.Add((kv.Value, FileName(parts[1]), $"viewing by {PeerName(v)}{AppTag(v)}   ·   {kv.Value.LocalDateTime.ToString(TimeFmt)}  (still open)", PathFor(parts[1])));
            }
            rows.Sort((a, b) => b.t.CompareTo(a.t)); // newest first
            return rows;
        }

        // Open in its own resizable window (not a height-capped dialog), so the full log scrolls and the
        // window can be moved / resized / maximized / minimized like any other. One instance only.
        if (_auditWindow is not null) { try { _auditWindow.Activate(); return; } catch { _auditWindow = null; } } // stale/dead → reopen

        var list = new StackPanel { Spacing = 12 };
        var scroller = new ScrollViewer
        {
            Content = list,
            Padding = new Thickness(20, 12, 20, 16),
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroller, 1);

        var search = new TextBox { PlaceholderText = "Filter the log (file, friend, action, date…)", HorizontalAlignment = HorizontalAlignment.Stretch };
        var exportBtn = new Button { Content = "Export…" };
        var clearBtn = new Button { Content = "Clear log" };
        var bar = new Grid { Padding = new Thickness(20, 14, 20, 10), ColumnSpacing = 10 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(search, 0); Grid.SetColumn(exportBtn, 1); Grid.SetColumn(clearBtn, 2);
        bar.Children.Add(search); bar.Children.Add(exportBtn); bar.Children.Add(clearBtn);
        Grid.SetRow(bar, 0);

        var root = new Grid { Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(bar);
        root.Children.Add(scroller);
        if ((this.Content as FrameworkElement)?.RequestedTheme is { } th) root.RequestedTheme = th; // match the app's theme

        var win = new Window { Content = root };
        _auditWindow = win;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(win);
            var appWin = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            appWin.Title = "Galileo — Access log";
            try { appWin.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "galileo.ico")); } catch { }
            try { appWin.Resize(new Windows.Graphics.SizeInt32(980, 760)); } catch { }
        }
        catch (Exception ex) { App.Log("AccessLogWindow", ex); }

        // allRows = everything from the relay; currentRows = what's currently shown (after the filter), used by Export.
        var allRows = new List<(DateTimeOffset t, string title, string detail, string? path)>();
        var currentRows = new List<(DateTimeOffset t, string title, string detail, string? path)>();
        var filter = "";

        void Populate(List<(DateTimeOffset t, string title, string detail, string? path)> rows)
        {
            currentRows = rows;
            list.Children.Clear();
            if (rows.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(filter) ? "No file access recorded yet." : "No entries match your filter.",
                    Opacity = 0.7,
                });
                return;
            }
            foreach (var (_, title, detail, path) in rows)
            {
                var card = new StackPanel { Spacing = 1 };
                if (path is not null)
                {
                    // Clickable link: open the file in the main window's viewer (its vault is unlocked) and
                    // bring that window forward; the log window stays open.
                    var link = new HyperlinkButton { Content = title, Padding = new Thickness(0), FontWeight = FontWeights.SemiBold };
                    var p = path;
                    link.Click += (_, _) => { _ = OpenLocalFileInViewerAsync(p); try { this.Activate(); } catch { } };
                    if (PhotoLibrary.IsSupported(p)) AttachImageHoverPreview(link, p); // hover thumbnail for images
                    card.Children.Add(link);
                }
                else
                {
                    card.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                }
                card.Children.Add(new TextBlock { Text = detail, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                list.Children.Add(card);
            }
        }

        // Apply the current filter to allRows and repaint. A blank filter shows everything; otherwise we
        // match the search text (case-insensitive) against each row's title and detail line.
        void Render()
        {
            var f = filter.Trim();
            var rows = string.IsNullOrEmpty(f)
                ? allRows
                : allRows.Where(r => r.title.Contains(f, StringComparison.OrdinalIgnoreCase)
                                     || r.detail.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            Populate(rows);
        }

        search.TextChanged += (_, _) => { filter = search.Text; Render(); };

        // Re-query the relay and refresh the list in place; auto-scroll to the top (newest) when new
        // entries have arrived, so the log "tails" live while it's open.
        var refreshing = false;
        var lastCount = -1;
        async Task RefreshAsync()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                var records = await _sharing!.QueryAuditAsync(url);
                allRows = BuildRows(records);
                Render();
                if (records.Count != lastCount)
                {
                    var grew = records.Count > lastCount && lastCount >= 0;
                    lastCount = records.Count;
                    if (grew) { scroller.UpdateLayout(); scroller.ChangeView(null, 0, null, true); }
                }
            }
            catch { /* transient relay hiccup — keep the last view, try again next tick */ }
            finally { refreshing = false; }
        }

        async Task ExportAsync()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Galileo access log — exported {DateTimeOffset.Now.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();
                if (currentRows.Count == 0) sb.AppendLine("No file access recorded yet.");
                foreach (var (_, title, detail, _) in currentRows) { sb.AppendLine(title); sb.AppendLine("    " + detail); sb.AppendLine(); }

                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "galileo-access-log" };
                picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;
                await File.WriteAllTextAsync(file.Path, sb.ToString());
                StatusText.Text = $"Access log exported to {file.Path}";
            }
            catch (Exception ex) { StatusText.Text = "Access log export failed: " + ex.Message; }
        }

        clearBtn.Click += async (_, _) =>
        {
            try { await _sharing!.ClearAuditAsync(url); lastCount = -1; await RefreshAsync(); } catch { }
        };
        exportBtn.Click += async (_, _) => await ExportAsync();

        // Initial load, then poll the relay every few seconds for live updates while the window is open.
        await RefreshAsync();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4);
        timer.Tick += (_, _) => _ = RefreshAsync();
        timer.Start();
        win.Closed += (_, _) => { timer.Stop(); _auditWindow = null; };

        win.Activate();
    }

    /// <summary>Shows a large image thumbnail near the pointer while it hovers over <paramref name="target"/>.
    /// Uses a Popup anchored to the element's own XamlRoot (a plain ToolTip doesn't position correctly in a
    /// secondary window). The bitmap is decoded once, lazily, so a long log doesn't read every file up front.</summary>
    private void AttachImageHoverPreview(FrameworkElement target, string path)
    {
        var img = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxWidth = 720, MaxHeight = 720 };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Child = img,
            IsHitTestVisible = false, // never steal the hover from the link
        };
        var popup = new Microsoft.UI.Xaml.Controls.Primitives.Popup { Child = border, IsLightDismissEnabled = false };
        var loaded = false;

        // async void event handler — wrap the WHOLE body so a throw (e.g. the window is mid-close) can't
        // bubble out as an unhandled exception and crash the app.
        target.PointerEntered += async (_, e) =>
        {
            try
            {
                if (target.XamlRoot is null) return;
                popup.XamlRoot = target.XamlRoot;
                var p = e.GetCurrentPoint(null).Position;       // relative to the window's XamlRoot
                var size = target.XamlRoot.Size;
                popup.HorizontalOffset = Math.Min(p.X + 16, Math.Max(0, size.Width - 744));
                popup.VerticalOffset = Math.Min(p.Y + 16, Math.Max(0, size.Height - 744));
                popup.IsOpen = true;
                if (!loaded)
                {
                    loaded = true;
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = 720 };
                    await bmp.SetSourceAsync(fs.AsRandomAccessStream());
                    img.Source = bmp;
                }
            }
            catch { try { popup.IsOpen = false; } catch { } }
        };
        target.PointerExited += (_, _) => { try { popup.IsOpen = false; } catch { } };
    }

    /// <summary>Opens a local file in THIS window (image → viewer, video/audio → player, else default app).
    /// Used by access-log links and for vault files (which must never spawn a second instance).</summary>
    private async Task OpenLocalFileInViewerAsync(string path)
    {
        try
        {
            if (PhotoLibrary.IsSupported(path)) await OpenSinglePhotoAsync(path);
            else if (PhotoLibrary.IsMedia(path))
            {
                var fi = new FileInfo(path);
                var item = new Models.ExplorerItem(path, Models.ExplorerItemKind.File, fi.Length, fi.LastWriteTime, fi.Extension);
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

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 1) return "<1s";
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m {d.Seconds}s";
        return $"{(int)d.TotalHours}h {d.Minutes}m";
    }

    // Small dialog helpers ---------------------------------------------------

    private async Task<string?> PromptPassphraseAsync(string title, string label, string primary)
    {
        var pw = new PasswordBox { PlaceholderText = "Passphrase" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(pw);
        var dlg = new ContentDialog
        {
            Title = title, Content = panel, PrimaryButtonText = primary, CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary && pw.Password.Length > 0 ? pw.Password : null;
    }

    private async Task<(string? alias, string? pass)> PromptAliasAndPassphraseAsync(string? existingAlias)
    {
        var aliasBox = new TextBox { PlaceholderText = "display name (friends see this)", Text = existingAlias ?? "" };
        var pw1 = new PasswordBox { PlaceholderText = "Passphrase" };
        var pw2 = new PasswordBox { PlaceholderText = "Confirm passphrase" };
        var err = new TextBlock { Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = "Choose a display name and a passphrase to protect this identity on this device.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(aliasBox);
        panel.Children.Add(pw1);
        panel.Children.Add(pw2);
        panel.Children.Add(err);
        var dlg = new ContentDialog
        {
            Title = "New identity", Content = panel, PrimaryButtonText = "OK", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
        };
        dlg.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(aliasBox.Text)) { err.Text = "Enter a display name."; err.Visibility = Visibility.Visible; args.Cancel = true; }
            else if (pw1.Password.Length < 6) { err.Text = "Passphrase: at least 6 characters."; err.Visibility = Visibility.Visible; args.Cancel = true; }
            else if (pw1.Password != pw2.Password) { err.Text = "Passphrases don't match."; err.Visibility = Visibility.Visible; args.Cancel = true; }
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? (aliasBox.Text.Trim(), pw1.Password) : (null, null);
    }

    private Task MessageAsync(string title, string message) =>
        new ContentDialog
        {
            Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK", XamlRoot = RootGrid.XamlRoot,
        }.ShowAsync().AsTask();

    private static void SetClipboard(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    /// <summary>An IShareSource that always reflects the currently-unlocked vault (so unlocking a different
    /// vault changes what's served without reconnecting).</summary>
    private sealed class LiveCurrentVaultSource : IShareSource
    {
        private readonly MainWindow _mw;
        public LiveCurrentVaultSource(MainWindow mw) => _mw = mw;
        public string ShareName => _mw._vaults.Current?.ShareName ?? "";
        public IReadOnlyList<VaultEntry> ShareEntries() => _mw._vaults.Current?.ShareEntries() ?? Array.Empty<VaultEntry>();
        public Stream OpenSharedEntry(string blobId) =>
            _mw._vaults.Current?.OpenSharedEntry(blobId) ?? throw new FileNotFoundException("Not shared.");

        public bool CanWrite => _mw._vaults.Current?.CanWrite ?? false;
        public void CreateFolder(string relPath) => (_mw._vaults.Current ?? throw new InvalidOperationException("No vault.")).CreateFolder(relPath);
        public Stream BeginUpload(string relPath) => (_mw._vaults.Current ?? throw new InvalidOperationException("No vault.")).BeginUpload(relPath);
        public void CommitUpload(string relPath) => _mw._vaults.Current?.CommitUpload(relPath);
        public void AbortUpload(string relPath) => _mw._vaults.Current?.AbortUpload(relPath);
        public void DeleteEntry(string id) => (_mw._vaults.Current ?? throw new InvalidOperationException("No vault.")).DeleteEntry(id);
    }
}
