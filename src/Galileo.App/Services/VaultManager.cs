using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Galileo.Services;

public enum VaultUnlockOutcome { Success, WrongPassphrase, Wiped }

/// <summary>
/// Owns the set of vaults on disk and the single currently-unlocked vault. Locking is centralized
/// here so the idle timer, the app-exit hook, and manual lock all go through one path.
/// </summary>
public sealed class VaultManager
{
    public static string AppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo");

    public static string VaultsRoot => Path.Combine(AppDataRoot, "Vaults");

    /// <summary>The currently unlocked vault, or null when everything is locked.</summary>
    public Vault? Current { get; private set; }

    public bool IsAnyUnlocked => Current is not null;

    public IReadOnlyList<Vault> List()
    {
        var list = new List<Vault>();
        if (!Directory.Exists(VaultsRoot)) return list;
        foreach (var dir in Directory.EnumerateDirectories(VaultsRoot))
        {
            try { list.Add(Vault.Load(dir)); }
            catch { /* skip an unreadable/partial vault */ }
        }
        return list.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Creates a vault, optionally enrolls Windows Hello, optionally moves the given
    /// files/folders into it (originals securely removed), then locks it.</summary>
    public async Task<Vault> CreateAsync(string name, string passphrase, bool useHello, IEnumerable<string>? importPaths)
    {
        Directory.CreateDirectory(VaultsRoot);
        var v = Vault.Create(VaultsRoot, name, passphrase); // returns unlocked
        try
        {
            if (useHello) await v.EnableHelloAsync();
            if (importPaths is not null) await v.ImportPathsAsync(importPaths, deleteOriginals: true);
        }
        finally { await v.LockAsync(); }
        return v;
    }

    public async Task<VaultUnlockOutcome> UnlockWithPassphraseAsync(Vault v, string passphrase, bool wipeEnabled, int wipeAfter)
    {
        // Already the active unlocked vault: never re-open it — OpenWithDekAsync starts by wiping
        // the live working folder, which would destroy not-yet-committed changes.
        if (Current is not null && Current.Id == v.Id && Current.IsUnlocked) return VaultUnlockOutcome.Success;
        if (Current is not null && Current.Id != v.Id) await LockCurrentAsync();
        try
        {
            await v.UnlockWithPassphraseAsync(passphrase);
            v.ResetFailedAttempts();
            Current = v;
            return VaultUnlockOutcome.Success;
        }
        // Only a keyslot-unwrap/index-decrypt failure means a wrong passphrase; a corrupt content
        // blob surfaces from Vault as InvalidDataException and must not count as a failed attempt.
        catch (System.Security.Cryptography.CryptographicException)
        {
            var attempts = v.RecordFailedAttempt();
            if (wipeEnabled && wipeAfter > 0 && attempts >= wipeAfter)
            {
                await WipeVaultAsync(v);
                return VaultUnlockOutcome.Wiped;
            }
            return VaultUnlockOutcome.WrongPassphrase;
        }
    }

    /// <summary>Permanently and securely destroys a vault (blobs, index, manifest, working copy, and
    /// any Windows Hello credential). Irreversible.</summary>
    public async Task WipeVaultAsync(Vault v)
    {
        if (Current?.Id == v.Id) Current = null;
        try { if (v.HasHello) await v.DisableHelloAsync(); } catch { /* ignore */ }
        VaultCrypto.WipeDirectory(Path.Combine(Vault.WorkRoot, v.Id));
        VaultCrypto.WipeDirectory(v.Root);
    }

    /// <summary>Encrypts the given paths into the currently-unlocked vault and securely wipes the
    /// originals. Returns the number of files added (0 if nothing is unlocked).</summary>
    public Task<int> AddToCurrentAsync(IEnumerable<string> paths) =>
        Current is null ? Task.FromResult(0) : Current.AddToOpenVaultAsync(paths, deleteOriginals: true);

    public async Task<bool> UnlockWithHelloAsync(Vault v)
    {
        // Already the active unlocked vault: never re-open it (see UnlockWithPassphraseAsync).
        if (Current is not null && Current.Id == v.Id && Current.IsUnlocked) return true;
        if (Current is not null && Current.Id != v.Id) await LockCurrentAsync();
        var ok = await v.UnlockWithHelloAsync();
        if (ok) Current = v;
        return ok;
    }

    /// <summary>Marks a vault as the active unlocked one after it was opened via another keyslot (Hello).</summary>
    public void SetCurrent(Vault v) => Current = v;

    public async Task LockCurrentAsync()
    {
        var c = Current;
        if (c is null) return;
        // LockAsync can throw with the vault still unlocked (commit failed) — keep Current set in
        // that case so the vault isn't orphaned in an unlocked state the app no longer tracks.
        await c.LockAsync();
        Current = null;
    }

    /// <summary>Commits the unlocked vault's working folder to its encrypted store without locking, so
    /// changes survive a non-graceful exit. No-op when nothing is unlocked.</summary>
    public Task FlushCurrentAsync() => Current?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>Re-materializes the unlocked vault's working copy if it went missing/empty.</summary>
    public Task EnsureCurrentWorkingAsync() => Current?.EnsureWorkingAsync() ?? Task.CompletedTask;

    /// <summary>Crash recovery: at startup, securely wipe any leftover decrypted working folders.</summary>
    public void WipeOrphanWorkDirs()
    {
        var work = Vault.WorkRoot;
        if (!Directory.Exists(work)) return;
        foreach (var d in Directory.EnumerateDirectories(work))
            VaultCrypto.WipeDirectory(d);
        try { Directory.Delete(work, true); } catch { /* ignore */ }
    }
}
