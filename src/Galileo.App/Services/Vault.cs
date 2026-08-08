using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Galileo.Services;

/// <summary>On-disk vault manifest (vault.json). Holds only public parameters and the wrapped
/// (encrypted) data-encryption key — safe to store at rest.</summary>
public sealed class VaultManifest
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    // Argon2id KDF parameters + salt for the passphrase keyslot.
    public string Kdf { get; set; } = "argon2id";
    public int MemoryKib { get; set; } = VaultCrypto.Argon2MemoryKib;
    public int Iterations { get; set; } = VaultCrypto.Argon2Iterations;
    public int Parallelism { get; set; } = VaultCrypto.Argon2Parallelism;
    public byte[] Salt { get; set; } = Array.Empty<byte>();

    /// <summary>DEK wrapped by the passphrase-derived key (VaultCrypto.Encrypt output).</summary>
    public byte[] PassphraseWrap { get; set; } = Array.Empty<byte>();

    // Optional Windows Hello keyslot (added in Phase B).
    public bool HasHello { get; set; }
    public string? HelloKeyName { get; set; }
    public byte[]? HelloChallenge { get; set; }
    public byte[]? HelloWrap { get; set; }

    /// <summary>Per-vault idle-lock override in seconds; null = use the global setting.</summary>
    public int? IdleSecondsOverride { get; set; }

    /// <summary>Consecutive wrong-passphrase attempts since the last successful unlock.</summary>
    public int FailedAttempts { get; set; }
}

public sealed class VaultIndex
{
    public List<VaultEntry> Entries { get; set; } = new();
}

public sealed class VaultEntry
{
    public string RelPath { get; set; } = "";   // forward-slash relative path within the vault
    public string BlobId { get; set; } = "";     // <BlobId>.blob in the blobs folder
    public long Size { get; set; }
    public long ModifiedUtcTicks { get; set; }
}

/// <summary>
/// A single secure vault: an app-managed store of AES-256-GCM-encrypted blobs with an encrypted
/// index. While unlocked it decrypts its contents into an ACL-restricted working folder so the rest
/// of the app can use them as ordinary files; locking re-encrypts changes and securely wipes that
/// folder.
/// </summary>
public sealed class Vault
{
    public string Root { get; }
    public VaultManifest Manifest { get; private set; }

    public string Id => Manifest.Id;
    public string Name => Manifest.Name;
    public bool HasHello => Manifest.HasHello;
    public bool IsUnlocked => _dek is not null;
    public string? WorkingDir { get; private set; }

    private byte[]? _dek;
    private VaultIndex _index = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1); // serialize commits (periodic flush vs lock)

    private string ManifestPath => Path.Combine(Root, "vault.json");
    private string IndexPath => Path.Combine(Root, "index.enc");
    private string BlobsDir => Path.Combine(Root, "blobs");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string AppData =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo");

    /// <summary>Root of all unlocked working folders (one subfolder per vault id).</summary>
    public static string WorkRoot => Path.Combine(AppData, ".work");

    private Vault(string root, VaultManifest manifest)
    {
        Root = root;
        Manifest = manifest;
    }

    public static Vault Load(string root)
    {
        var manifest = JsonSerializer.Deserialize<VaultManifest>(File.ReadAllText(ManifestPathOf(root)), JsonOpts)
                       ?? throw new InvalidDataException("Invalid vault manifest.");
        return new Vault(root, manifest);
    }

    private static string ManifestPathOf(string root) => Path.Combine(root, "vault.json");

    /// <summary>Creates a new, empty vault and returns it already unlocked (DEK in memory) so the
    /// caller can immediately import content before locking.</summary>
    public static Vault Create(string vaultsRoot, string name, string passphrase)
    {
        var id = Guid.NewGuid().ToString("N");
        var root = Path.Combine(vaultsRoot, id);
        Directory.CreateDirectory(Path.Combine(root, "blobs"));

        var salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSize);
        var dek = VaultCrypto.RandomBytes(VaultCrypto.KeySize);
        byte[]? kek = null;
        try
        {
            kek = VaultCrypto.DeriveKey(passphrase, salt);
            var wrap = VaultCrypto.Encrypt(kek, dek);
            var manifest = new VaultManifest { Id = id, Name = name, Salt = salt, PassphraseWrap = wrap };
            var v = new Vault(root, manifest) { _dek = dek };
            v.SaveManifest();
            v.SaveIndex();
            return v;
        }
        catch
        {
            VaultCrypto.Wipe(dek);
            throw;
        }
        finally
        {
            VaultCrypto.Wipe(kek);
        }
    }

    public void SaveManifest() => File.WriteAllText(ManifestPath, JsonSerializer.Serialize(Manifest, JsonOpts));

    /// <summary>Renames the vault (display name only — the on-disk store is keyed by id, so no files
    /// move). Works whether the vault is locked or unlocked.</summary>
    public void Rename(string newName)
    {
        Manifest.Name = newName;
        SaveManifest();
    }

    public int FailedAttempts => Manifest.FailedAttempts;

    /// <summary>Records a wrong-passphrase attempt and returns the new running total.</summary>
    public int RecordFailedAttempt()
    {
        Manifest.FailedAttempts++;
        SaveManifest();
        return Manifest.FailedAttempts;
    }

    public void ResetFailedAttempts()
    {
        if (Manifest.FailedAttempts == 0) return;
        Manifest.FailedAttempts = 0;
        SaveManifest();
    }

    // ---------- Unlock / lock ----------

    public async Task UnlockWithPassphraseAsync(string passphrase)
    {
        if (_dek is not null) return;
        var dek = await Task.Run(() =>
        {
            byte[]? kek = null;
            try
            {
                kek = VaultCrypto.DeriveKey(passphrase, Manifest.Salt,
                    Manifest.MemoryKib, Manifest.Iterations, Manifest.Parallelism);
                return VaultCrypto.Decrypt(kek, Manifest.PassphraseWrap); // throws CryptographicException if wrong
            }
            finally { VaultCrypto.Wipe(kek); }
        });
        await OpenWithDekAsync(dek);
    }

    /// <summary>Opens the vault with an already-recovered DEK (used by the Hello keyslot too).</summary>
    internal async Task OpenWithDekAsync(byte[] dek)
    {
        _dek = dek;
        try { _index = LoadIndex(); }
        catch { VaultCrypto.Wipe(_dek); _dek = null; throw; } // don't stay half-open on a bad index
        await DecryptAllToWorkingAsync();
    }

    /// <summary>Adds (or replaces) a Windows Hello keyslot that unwraps the same DEK. Requires the
    /// vault to be unlocked. Returns false if Hello is unavailable or the user cancels.</summary>
    public async Task<bool> EnableHelloAsync()
    {
        if (_dek is null) return false;
        var keyName = "Galileo.Vault." + Id;
        var challenge = VaultCrypto.RandomBytes(32);
        var kek = await HelloKey.EnrollAndDeriveAsync(keyName, challenge);
        if (kek is null) return false;
        try
        {
            Manifest.HelloWrap = VaultCrypto.Encrypt(kek, _dek);
            Manifest.HelloChallenge = challenge;
            Manifest.HelloKeyName = keyName;
            Manifest.HasHello = true;
            SaveManifest();
            return true;
        }
        finally { VaultCrypto.Wipe(kek); }
    }

    public async Task DisableHelloAsync()
    {
        if (Manifest.HelloKeyName is not null) await HelloKey.DeleteAsync(Manifest.HelloKeyName);
        Manifest.HasHello = false;
        Manifest.HelloKeyName = null;
        Manifest.HelloChallenge = null;
        Manifest.HelloWrap = null;
        SaveManifest();
    }

    public async Task<bool> UnlockWithHelloAsync()
    {
        if (_dek is not null) return true;
        if (!Manifest.HasHello || Manifest.HelloKeyName is null
            || Manifest.HelloChallenge is null || Manifest.HelloWrap is null) return false;

        var kek = await HelloKey.OpenAndDeriveAsync(Manifest.HelloKeyName, Manifest.HelloChallenge);
        if (kek is null) return false;

        byte[] dek;
        try { dek = VaultCrypto.Decrypt(kek, Manifest.HelloWrap); }
        catch (CryptographicException) { return false; }
        finally { VaultCrypto.Wipe(kek); }

        await OpenWithDekAsync(dek);
        return true;
    }

    public async Task LockAsync()
    {
        if (_dek is null) return;
        await _syncGate.WaitAsync();
        try
        {
            // Same safety net as FlushAsync: if the working folder transiently enumerates empty while
            // the index still has entries, committing would wipe every blob — skip the sync and just
            // tear down (the encrypted store already holds everything since the last flush).
            var transientlyEmpty = _index.Entries.Count > 0 && WorkingDir is not null
                && Directory.Exists(WorkingDir)
                && !Directory.EnumerateFiles(WorkingDir, "*", SearchOption.AllDirectories).Any();
            if (transientlyEmpty)
                App.LogInfo("Vault lock: working folder empty but index is not; skipping commit to avoid wiping the index.");
            else
                await SyncWorkingToBlobsAsync();
        }
        finally { _syncGate.Release(); }

        // Tear down only after a successful commit — if the sync threw, the working copy and DEK stay
        // intact so the caller can retry or warn instead of losing everything since the last flush.
        if (WorkingDir is not null) { VaultCrypto.WipeDirectory(WorkingDir); WorkingDir = null; }
        VaultCrypto.Wipe(_dek);
        _dek = null;
    }

    /// <summary>Commits the working folder back to the encrypted blobs/index <b>without</b> locking — so
    /// changes are durable even if the app is force-killed before a graceful lock. Safe to call often;
    /// unchanged files keep their existing blob (no re-encryption).</summary>
    public async Task FlushAsync()
    {
        if (_dek is null || WorkingDir is null) return;
        await _syncGate.WaitAsync();
        try
        {
            // Safety net: never let a commit wipe the whole index because the working folder
            // momentarily appears empty (a transient/bug is far likelier than the user deleting
            // everything). The lock path applies the same guard.
            if (_index.Entries.Count > 0 && Directory.Exists(WorkingDir)
                && !Directory.EnumerateFiles(WorkingDir, "*", SearchOption.AllDirectories).Any())
                return;
            await SyncWorkingToBlobsAsync();
        }
        finally { _syncGate.Release(); }
    }

    /// <summary>Ensures the decrypted working copy exists; re-materializes it from the encrypted blobs if it
    /// went missing or empty (so re-entering an unlocked vault always shows its contents, no re-mount needed).
    /// Only acts when empty, so it never clobbers not-yet-committed additions.</summary>
    public async Task EnsureWorkingAsync()
    {
        if (_dek is null) return;
        await _syncGate.WaitAsync();
        try
        {
            var empty = WorkingDir is null || !Directory.Exists(WorkingDir)
                        || !Directory.EnumerateFileSystemEntries(WorkingDir).Any();
            if (empty && _index.Entries.Count > 0) await DecryptAllToWorkingAsync();
        }
        finally { _syncGate.Release(); }
    }

    // ---------- Import (create / move-to-vault) ----------

    /// <summary>Encrypts the given files/folders straight into the blob store and (optionally)
    /// securely deletes the originals. Requires the vault to be unlocked.</summary>
    public async Task<int> ImportPathsAsync(IEnumerable<string> paths, bool deleteOriginals)
    {
        if (_dek is null) throw new InvalidOperationException("Vault is locked.");
        var sources = paths.ToList();
        var imported = new List<string>(); // only fully-imported sources are eligible for deletion
        int count = 0;

        foreach (var p in sources)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    var baseName = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    {
                        var rel = baseName + "/" + Path.GetRelativePath(p, f).Replace(Path.DirectorySeparatorChar, '/');
                        await ImportSingleAsync(f, rel);
                        count++;
                    }
                    imported.Add(p);
                }
                else if (File.Exists(p))
                {
                    await ImportSingleAsync(p, Path.GetFileName(p));
                    count++;
                    imported.Add(p);
                }
            }
            catch { /* skip this source; continue with the rest */ }
        }

        SaveIndex();

        if (deleteOriginals)
        {
            // Delete only sources whose import fully succeeded — never wipe an original that
            // isn't safely in the vault.
            foreach (var p in imported)
            {
                if (Directory.Exists(p)) VaultCrypto.WipeDirectory(p);
                else if (File.Exists(p)) VaultCrypto.OverwriteAndDelete(p);
            }
        }
        return count;
    }

    private async Task ImportSingleAsync(string srcFile, string rel)
    {
        rel = UniqueRel(rel);
        var blobId = Guid.NewGuid().ToString("N");
        var fi = new FileInfo(srcFile);
        using (var inp = File.OpenRead(srcFile))
        using (var outp = File.Create(Path.Combine(BlobsDir, blobId + ".blob")))
            await VaultCrypto.EncryptStreamAsync(_dek!, inp, outp, VaultCrypto.BlobContext(blobId));

        _index.Entries.Add(new VaultEntry
        {
            RelPath = rel,
            BlobId = blobId,
            Size = fi.Length,
            ModifiedUtcTicks = fi.LastWriteTimeUtc.Ticks,
        });
    }

    /// <summary>Adds files/folders into this <b>already-unlocked</b> vault: each is encrypted into a
    /// durable blob now (crash-safe), mirrored into the working folder so it shows immediately, and
    /// — when <paramref name="deleteOriginals"/> — the source is securely wiped from clear space.</summary>
    public async Task<int> AddToOpenVaultAsync(IEnumerable<string> paths, bool deleteOriginals)
    {
        if (_dek is null || WorkingDir is null) throw new InvalidOperationException("Vault is not unlocked.");
        var sources = paths.ToList();
        var imported = new List<string>(); // only fully-imported sources are eligible for deletion
        var added = 0;

        await _syncGate.WaitAsync(); // don't race a concurrent flush mutating/iterating the index
        try
        {
            foreach (var p in sources)
            {
                try
                {
                    if (Directory.Exists(p))
                    {
                        var baseName = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                        {
                            var rel = baseName + "/" + Path.GetRelativePath(p, f).Replace(Path.DirectorySeparatorChar, '/');
                            await AddOneToOpenAsync(f, rel);
                            added++;
                        }
                        imported.Add(p);
                    }
                    else if (File.Exists(p))
                    {
                        await AddOneToOpenAsync(p, Path.GetFileName(p));
                        added++;
                        imported.Add(p);
                    }
                }
                catch { /* skip this source; continue with the rest */ }
            }

            SaveIndex();
        }
        finally { _syncGate.Release(); }

        if (deleteOriginals)
        {
            // Delete only sources whose import fully succeeded — never wipe an original that
            // isn't safely in the vault.
            foreach (var p in imported)
            {
                if (Directory.Exists(p)) VaultCrypto.WipeDirectory(p);
                else if (File.Exists(p)) VaultCrypto.OverwriteAndDelete(p);
            }
        }
        return added;
    }

    private async Task AddOneToOpenAsync(string srcFile, string rel)
    {
        rel = UniqueRel(rel);
        var dest = Path.Combine(WorkingDir!, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        // UniqueRel dedupes only against the index; a same-named file may still sit in the live
        // working folder (not yet committed), which would make the mirror copy throw and abort the
        // import. Pick a unique on-disk name and keep rel in agreement with it.
        var unique = UniqueFsPath(dest);
        if (!string.Equals(unique, dest, StringComparison.Ordinal))
        {
            dest = unique;
            rel = Path.GetRelativePath(WorkingDir!, dest).Replace(Path.DirectorySeparatorChar, '/');
        }

        // Mirror into the working folder (preserves the source timestamp so the lock-time sync sees it
        // as unchanged and keeps the blob we write below rather than re-encrypting).
        File.Copy(srcFile, dest, overwrite: false);
        var fi = new FileInfo(dest);

        var blobId = Guid.NewGuid().ToString("N");
        using (var inp = File.OpenRead(dest))
        using (var outp = File.Create(Path.Combine(BlobsDir, blobId + ".blob")))
            await VaultCrypto.EncryptStreamAsync(_dek!, inp, outp, VaultCrypto.BlobContext(blobId));

        _index.Entries.Add(new VaultEntry
        {
            RelPath = rel,
            BlobId = blobId,
            Size = fi.Length,
            ModifiedUtcTicks = fi.LastWriteTimeUtc.Ticks,
        });
    }

    // ---------- Working-folder decrypt / re-encrypt ----------

    private async Task DecryptAllToWorkingAsync()
    {
        var work = Path.Combine(WorkRoot, Id);
        VaultCrypto.WipeDirectory(work); // clear any stale copy first
        Directory.CreateDirectory(work);
        SetRestrictiveAcl(work);
        WorkingDir = work; // own the folder immediately so a mid-decrypt failure can't orphan plaintext

        try
        {
            foreach (var e in _index.Entries)
            {
                var dest = Path.Combine(work, e.RelPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var blob = Path.Combine(BlobsDir, e.BlobId + ".blob");
                if (!File.Exists(blob)) continue;
                using (var inp = File.OpenRead(blob))
                using (var outp = File.Create(dest))
                    await VaultCrypto.DecryptStreamAsync(_dek!, inp, outp, VaultCrypto.BlobContext(e.BlobId));
                try { File.SetLastWriteTimeUtc(dest, new DateTime(e.ModifiedUtcTicks, DateTimeKind.Utc)); } catch { }
            }
        }
        catch (Exception ex)
        {
            // Never leave plaintext behind or the vault half-open after a failed decrypt.
            VaultCrypto.WipeDirectory(work);
            WorkingDir = null;
            VaultCrypto.Wipe(_dek);
            _dek = null;
            // A corrupt content blob is NOT a wrong passphrase (the keyslot already unwrapped) —
            // surface it distinctly so callers don't count it as a failed unlock attempt.
            if (ex is CryptographicException)
                throw new InvalidDataException("A vault file failed to decrypt (corrupt or tampered blob).", ex);
            throw;
        }
    }

    private async Task SyncWorkingToBlobsAsync()
    {
        if (WorkingDir is null || !Directory.Exists(WorkingDir)) return;
        var work = WorkingDir;
        var existing = _index.Entries.ToDictionary(e => e.RelPath, StringComparer.OrdinalIgnoreCase);
        var newIndex = new VaultIndex();
        var keptBlobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(work, file).Replace(Path.DirectorySeparatorChar, '/');
            var fi = new FileInfo(file);
            var ticks = fi.LastWriteTimeUtc.Ticks;

            if (existing.TryGetValue(rel, out var prev) && prev.Size == fi.Length && prev.ModifiedUtcTicks == ticks)
            {
                newIndex.Entries.Add(prev);            // unchanged → keep its blob
                keptBlobs.Add(prev.BlobId);
                continue;
            }

            var blobId = Guid.NewGuid().ToString("N");  // new or changed → fresh blob
            using (var inp = File.OpenRead(file))
            using (var outp = File.Create(Path.Combine(BlobsDir, blobId + ".blob")))
                await VaultCrypto.EncryptStreamAsync(_dek!, inp, outp, VaultCrypto.BlobContext(blobId));
            newIndex.Entries.Add(new VaultEntry { RelPath = rel, BlobId = blobId, Size = fi.Length, ModifiedUtcTicks = ticks });
            keptBlobs.Add(blobId);
        }

        // Remove blobs no longer referenced (deleted or replaced files).
        foreach (var e in _index.Entries)
            if (!keptBlobs.Contains(e.BlobId))
                VaultCrypto.OverwriteAndDelete(Path.Combine(BlobsDir, e.BlobId + ".blob"));

        _index = newIndex;
        SaveIndex();
    }

    // ---------- Index persistence ----------

    private void SaveIndex()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(_index, JsonOpts);
        try { File.WriteAllBytes(IndexPath, VaultCrypto.Encrypt(_dek!, json)); }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    private VaultIndex LoadIndex()
    {
        if (!File.Exists(IndexPath)) return new VaultIndex();
        var json = VaultCrypto.Decrypt(_dek!, File.ReadAllBytes(IndexPath));
        try { return JsonSerializer.Deserialize<VaultIndex>(json, JsonOpts) ?? new VaultIndex(); }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    private static string UniqueFsPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    // ---------- Helpers ----------

    private string UniqueRel(string rel)
    {
        var set = new HashSet<string>(_index.Entries.Select(e => e.RelPath), StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(rel)) return rel;
        var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/');
        var name = Path.GetFileNameWithoutExtension(rel);
        var ext = Path.GetExtension(rel);
        for (var i = 2; ; i++)
        {
            var cand = (string.IsNullOrEmpty(dir) ? "" : dir + "/") + $"{name} ({i}){ext}";
            if (!set.Contains(cand)) return cand;
        }
    }

    /// <summary>The working folder lives under %LocalAppData%\Galileo\.work, which Windows already
    /// restricts to the current user account via the profile's inherited ACL. We mark it Hidden so it
    /// doesn't show up casually; it is securely wiped on lock.</summary>
    private static void SetRestrictiveAcl(string dir)
    {
        try { new DirectoryInfo(dir).Attributes |= FileAttributes.Hidden; }
        catch { /* best effort */ }
    }

    // ---------- Hello keyslot support (used in Phase B) ----------

    /// <summary>Returns a copy of the live DEK so a Hello keyslot can be added while unlocked.</summary>
    internal byte[]? ExportDekForKeyslot() => _dek is null ? null : (byte[])_dek.Clone();
}
