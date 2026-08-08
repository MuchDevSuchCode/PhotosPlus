using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace Galileo.Services;

/// <summary>A vault backup found in Google Drive (folder named by the vault id).</summary>
public sealed record RemoteVault(string Id, string FolderId, int FileCount);

/// <summary>
/// Backs up / restores the *encrypted* vault store to Google Drive over OAuth. Only the opaque,
/// already-encrypted files are uploaded — blobs (random GUID names), the encrypted index, and a
/// name-stripped copy of the manifest — so Google never sees plaintext, real names, or the key
/// (which stays wrapped behind the passphrase/Hello). Uses the drive.file scope, so the app can
/// only ever touch files it created.
/// </summary>
public sealed class GoogleDriveBackup
{
    private const string RootFolderName = "Galileo Vault Backups";
    private const string FolderMime = "application/vnd.google-apps.folder";

    private static string AppData =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo");

    public static string OAuthConfigPath => Path.Combine(AppData, "google-oauth.json");
    private static string TokenDir => Path.Combine(AppData, "gdrive-token");

    // OAuth "Desktop app" client. To keep the secret OUT of source control (GitHub push protection),
    // it's shipped as a gitignored JSON next to the app (Assets\google-oauth.json, copied to output),
    // with a per-user override at %LocalAppData%\Galileo\google-oauth.json. The bundled file makes
    // "Sign in with Google" work out of the box for end users without exposing the secret in the repo.
    private static string BundledConfigPath => Path.Combine(AppContext.BaseDirectory, "Assets", "google-oauth.json");

    /// <summary>The client config to use: the per-user override wins, else the shipped bundled file.</summary>
    private static string? ClientConfigFile =>
        File.Exists(OAuthConfigPath) ? OAuthConfigPath
        : File.Exists(BundledConfigPath) ? BundledConfigPath
        : null;

    /// <summary>True when an OAuth client config is available (bundled or per-user file).</summary>
    public static bool IsConfigured => ClientConfigFile is not null;

    private static bool HasStoredToken => Directory.Exists(TokenDir) && Directory.EnumerateFiles(TokenDir).Any();

    private DriveService? _service;
    private string? _rootId;

    public bool IsConnected => _service is not null;

    /// <summary>The signed-in Google account's email, once connected (null otherwise).</summary>
    public string? ConnectedEmail { get; private set; }

    // ---------- Connect / disconnect ----------

    public async Task<bool> ConnectAsync(bool forcePrompt = false, CancellationToken ct = default)
    {
        if (!IsConfigured) return false;

        // Force the browser sign-in by clearing any cached token first — otherwise the Google
        // library silently reuses a stored token and no browser ever appears.
        if (forcePrompt)
        {
            try { if (Directory.Exists(TokenDir)) Directory.Delete(TokenDir, recursive: true); } catch { /* ignore */ }
        }

        // Don't hang forever if the user closes the browser without finishing.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            await GetClientSecretsAsync(),
            new[] { DriveService.Scope.DriveFile },
            "user",
            linked.Token,
            new DpapiDataStore(TokenDir));

        _service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Galileo",
        });
        _rootId = null;
        await FetchAccountAsync();
        return true;
    }

    /// <summary>Silently reconnects from a previously stored token (refreshing it) without opening a
    /// browser — used at startup so the user stays signed in across launches.</summary>
    public async Task<bool> TryReconnectAsync()
    {
        if (_service is not null) return true;
        if (!IsConfigured || !HasStoredToken) return false;
        try { return await ConnectAsync(); }
        catch { return false; }
    }

    private static async Task<ClientSecrets> GetClientSecretsAsync()
    {
        var path = ClientConfigFile ?? throw new InvalidOperationException("No Google OAuth client configured.");
        using var s = File.OpenRead(path);
        return (await GoogleClientSecrets.FromStreamAsync(s)).Secrets;
    }

    private async Task FetchAccountAsync()
    {
        try
        {
            var req = _service!.About.Get();
            req.Fields = "user";
            var about = await req.ExecuteAsync();
            ConnectedEmail = about.User?.EmailAddress;
        }
        catch { ConnectedEmail = null; }
    }

    public Task DisconnectAsync()
    {
        _service?.Dispose();
        _service = null;
        _rootId = null;
        ConnectedEmail = null;
        try { if (Directory.Exists(TokenDir)) Directory.Delete(TokenDir, recursive: true); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    // ---------- Backup ----------

    public async Task BackupVaultAsync(Vault v, IProgress<string>? progress = null)
    {
        EnsureConnected();
        var folderId = await FindFolderAsync(v.Id, await EnsureRootAsync()) ?? await CreateFolderAsync(v.Id, await EnsureRootAsync());

        // A concurrent vault flush can delete/replace blobs after we snapshot the list. If a blob
        // vanishes mid-upload, retry the whole snapshot+upload once from scratch; and because the
        // index is only uploaded after every blob made it up, an aborted pass never publishes an
        // index that references blobs missing from the backup.
        try
        {
            await BackupOnceAsync(v, folderId, progress);
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            await BackupOnceAsync(v, folderId, progress);
        }
    }

    private async Task BackupOnceAsync(Vault v, string folderId, IProgress<string>? progress)
    {
        var remote = await ListChildrenAsync(folderId);

        var blobsDir = BlobsDir(v);
        var localBlobs = Directory.Exists(blobsDir) ? Directory.GetFiles(blobsDir, "*.blob") : Array.Empty<string>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index.enc", "vault.json" };

        var i = 0;
        foreach (var blob in localBlobs)
        {
            var name = Path.GetFileName(blob);
            keep.Add(name);
            progress?.Report($"Uploading files… {++i}/{localBlobs.Length}");
            if (remote.ContainsKey(name)) continue; // blobs are immutable — already backed up
            using var s = File.OpenRead(blob); // FileNotFoundException here → caller retries once
            await UploadAsync(folderId, name, s, existingId: null);
        }

        // The index/manifest go up LAST, only after every blob upload succeeded — a failed blob
        // upload throws out of the loop above and leaves the previous remote index intact.
        progress?.Report("Uploading index…");
        using (var s = File.OpenRead(IndexPath(v)))
            await UploadAsync(folderId, "index.enc", s, remote.GetValueOrDefault("index.enc"));

        progress?.Report("Uploading manifest…");
        using (var ms = new MemoryStream(SanitizedManifestBytes(v)))
            await UploadAsync(folderId, "vault.json", ms, remote.GetValueOrDefault("vault.json"));

        // Reflect local deletions: remove remote blobs no longer present locally.
        foreach (var (name, id) in remote)
            if (name.EndsWith(".blob", StringComparison.OrdinalIgnoreCase) && !keep.Contains(name))
                await _service!.Files.Delete(id).ExecuteAsync();

        progress?.Report("Backup complete.");
    }

    /// <summary>Manifest copy with the display name removed (keeps salt + wrapped key so it restores).</summary>
    private static byte[] SanitizedManifestBytes(Vault v)
    {
        var manifest = JsonSerializer.Deserialize<VaultManifest>(File.ReadAllText(ManifestPath(v))) ?? new VaultManifest();
        manifest.Name = "";
        return JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    // ---------- List / restore ----------

    public async Task<IReadOnlyList<RemoteVault>> ListBackupsAsync()
    {
        EnsureConnected();
        var root = await EnsureRootAsync();
        var folders = new List<RemoteVault>();
        string? page = null;
        do
        {
            var req = _service!.Files.List();
            req.Q = $"'{root}' in parents and mimeType='{FolderMime}' and trashed=false";
            req.Fields = "nextPageToken, files(id,name)";
            req.PageSize = 1000;
            req.PageToken = page;
            var res = await req.ExecuteAsync();
            foreach (var f in res.Files)
            {
                var count = (await ListChildrenAsync(f.Id)).Count;
                folders.Add(new RemoteVault(f.Name, f.Id, count));
            }
            page = res.NextPageToken;
        } while (page is not null);
        return folders;
    }

    public async Task RestoreVaultAsync(string vaultId, IProgress<string>? progress = null)
    {
        EnsureConnected();
        var folderId = await FindFolderAsync(vaultId, await EnsureRootAsync())
            ?? throw new InvalidOperationException("No backup found for this vault.");
        var children = await ListChildrenAsync(folderId);

        var dest = Path.Combine(VaultManager.VaultsRoot, vaultId);
        Directory.CreateDirectory(Path.Combine(dest, "blobs"));

        var i = 0;
        foreach (var (name, id) in children)
        {
            // Remote names come from Drive and could be tampered with — never let one escape the
            // vault folder (path separators, drive colons, or '..' traversal are rejected).
            if (!IsSafeRemoteName(name)) continue;
            progress?.Report($"Downloading files… {++i}/{children.Count}");
            var path = name.EndsWith(".blob", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(dest, "blobs", name)
                : Path.Combine(dest, name);
            using var fs = File.Create(path);
            await _service!.Files.Get(id).DownloadAsync(fs);
        }

        // Restored manifests have a blank name (sanitized on upload) — give it a placeholder.
        var manifestPath = Path.Combine(dest, "vault.json");
        if (File.Exists(manifestPath))
        {
            var m = JsonSerializer.Deserialize<VaultManifest>(File.ReadAllText(manifestPath));
            if (m is not null && string.IsNullOrWhiteSpace(m.Name))
            {
                m.Name = "Restored vault";
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        progress?.Report("Restore complete.");
    }

    // ---------- Drive helpers ----------

    private void EnsureConnected()
    {
        if (_service is null) throw new InvalidOperationException("Not connected to Google Drive.");
    }

    private async Task<string> EnsureRootAsync()
    {
        _rootId ??= await FindFolderAsync(RootFolderName, null) ?? await CreateFolderAsync(RootFolderName, null);
        return _rootId;
    }

    private async Task<string?> FindFolderAsync(string name, string? parentId)
    {
        var q = $"mimeType='{FolderMime}' and name='{Escape(name)}' and trashed=false";
        if (parentId is not null) q += $" and '{parentId}' in parents";
        var req = _service!.Files.List();
        req.Q = q;
        req.Spaces = "drive";
        req.Fields = "files(id,name)";
        var res = await req.ExecuteAsync();
        return res.Files.FirstOrDefault()?.Id;
    }

    private async Task<string> CreateFolderAsync(string name, string? parentId)
    {
        var meta = new DriveFile { Name = name, MimeType = FolderMime };
        if (parentId is not null) meta.Parents = new[] { parentId };
        var created = await _service!.Files.Create(meta).ExecuteAsync();
        return created.Id;
    }

    private async Task<Dictionary<string, string>> ListChildrenAsync(string folderId)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? page = null;
        do
        {
            var req = _service!.Files.List();
            req.Q = $"'{folderId}' in parents and trashed=false";
            req.Fields = "nextPageToken, files(id,name)";
            req.PageSize = 1000;
            req.PageToken = page;
            var res = await req.ExecuteAsync();
            foreach (var f in res.Files) map[f.Name] = f.Id;
            page = res.NextPageToken;
        } while (page is not null);
        return map;
    }

    private async Task UploadAsync(string folderId, string name, Stream content, string? existingId)
    {
        IUploadProgress result;
        if (existingId is null)
        {
            var meta = new DriveFile { Name = name, Parents = new[] { folderId } };
            var req = _service!.Files.Create(meta, content, "application/octet-stream");
            req.Fields = "id";
            result = await req.UploadAsync();
        }
        else
        {
            var meta = new DriveFile { Name = name };
            var req = _service!.Files.Update(meta, existingId, content, "application/octet-stream");
            result = await req.UploadAsync();
        }
        if (result.Status == UploadStatus.Failed)
            throw result.Exception ?? new Exception($"Upload of {name} failed.");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>True when a remote file name is safe to combine into a local path.</summary>
    private static bool IsSafeRemoteName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0
        && !name.Contains("..");

    // Vault store layout (keyed off the public Root).
    private static string ManifestPath(Vault v) => Path.Combine(v.Root, "vault.json");
    private static string IndexPath(Vault v) => Path.Combine(v.Root, "index.enc");
    private static string BlobsDir(Vault v) => Path.Combine(v.Root, "blobs");
}
