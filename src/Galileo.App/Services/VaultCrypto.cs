using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Konscious.Security.Cryptography;

namespace Galileo.Services;

/// <summary>
/// Low-level cryptographic primitives for the secure vault:
///  - Argon2id key derivation from a passphrase,
///  - AES-256-GCM authenticated encryption (single-shot for keys/index, chunked for files),
///  - best-effort secure deletion.
/// This class never persists key material; callers own the lifetime of returned keys and
/// should zero them when done (see <see cref="Wipe"/>).
/// </summary>
public static class VaultCrypto
{
    public const int KeySize = 32;    // AES-256
    public const int NonceSize = 12;  // AES-GCM nonce
    public const int TagSize = 16;    // AES-GCM tag
    public const int SaltSize = 16;

    // Argon2id parameters — memory-hard; tuned for a few hundred ms on a typical desktop.
    public const int Argon2MemoryKib = 256 * 1024; // 256 MiB
    public const int Argon2Iterations = 3;
    public const int Argon2Parallelism = 4;

    private const int ChunkSize = 1024 * 1024; // 1 MiB of plaintext per chunk
    private static readonly byte[] FileMagic = { (byte)'G', (byte)'V', (byte)'B', 1 }; // Galileo Vault Blob v1

    public static byte[] RandomBytes(int count)
    {
        var b = new byte[count];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    public static void Wipe(byte[]? key)
    {
        if (key is not null) CryptographicOperations.ZeroMemory(key);
    }

    // Minimum KDF cost accepted at unlock time. The manifest's Argon2 params are plaintext on disk, so
    // an attacker could lower them to make an offline brute-force cheap; clamp to this floor regardless.
    private const int MinMemoryKib = 64 * 1024; // 64 MiB
    private const int MinIterations = 2;
    private const int MinParallelism = 1;

    /// <summary>Derives a 256-bit key-encryption key from a passphrase with Argon2id.</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt,
        int memoryKib = Argon2MemoryKib, int iterations = Argon2Iterations, int parallelism = Argon2Parallelism)
    {
        // Never derive with weaker-than-floor params, even if the manifest asks for them (downgrade guard).
        memoryKib = Math.Max(memoryKib, MinMemoryKib);
        iterations = Math.Max(iterations, MinIterations);
        parallelism = Math.Max(parallelism, MinParallelism);

        var pw = System.Text.Encoding.UTF8.GetBytes(passphrase);
        try
        {
            var argon = new Argon2id(pw)
            {
                Salt = salt,
                MemorySize = memoryKib,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
            };
            return argon.GetBytes(KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
        }
    }

    // ---------- Single-shot AEAD (DEK wrap, index, small payloads) ----------

    /// <summary>AES-256-GCM encrypt. Output layout: nonce(12) || tag(16) || ciphertext.</summary>
    public static byte[] Encrypt(byte[] key, byte[] plaintext, byte[]? aad = null)
    {
        var nonce = RandomBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, plaintext, cipher, tag, aad);

        var output = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, output, NonceSize + TagSize, cipher.Length);
        return output;
    }

    /// <summary>Decrypts data produced by <see cref="Encrypt"/>. Throws on tag mismatch (tampering / wrong key).</summary>
    public static byte[] Decrypt(byte[] key, byte[] data, byte[]? aad = null)
    {
        if (data.Length < NonceSize + TagSize) throw new CryptographicException("Ciphertext too short.");
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipher = new byte[data.Length - NonceSize - TagSize];
        Buffer.BlockCopy(data, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(data, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(data, NonceSize + TagSize, cipher, 0, cipher.Length);

        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, cipher, tag, plain, aad); // CryptographicException on failure
        return plain;
    }

    // ---------- Chunked AEAD stream (file contents, including multi-GB videos) ----------
    // Format: FileMagic(4) || baseNonce(12) || repeated [ len:int32-BE | tag(16) | ciphertext(len) ].
    // Per chunk: nonce = baseNonce XOR counter, AAD = counter(8-BE) || isFinal(1) || context. The final
    // flag makes truncating/dropping trailing chunks fail; the counter prevents reordering; the context
    // (the blob's identity, e.g. its BlobId) binds the bytes to that blob so two blobs can't be swapped
    // — a blob encrypted as A won't verify when the index says it should be B. Plaintext <= ChunkSize.

    public static async Task EncryptStreamAsync(byte[] key, Stream input, Stream output, byte[]? context = null)
    {
        var baseNonce = RandomBytes(NonceSize);
        await output.WriteAsync(FileMagic);
        await output.WriteAsync(baseNonce);

        using var gcm = new AesGcm(key, TagSize);
        var plain = new byte[ChunkSize];
        var tag = new byte[TagSize];
        var lenBuf = new byte[4];
        long counter = 0;

        while (true)
        {
            int read = await ReadFullAsync(input, plain, ChunkSize);
            bool isFinal = read < ChunkSize || input.Position >= input.Length;

            var nonce = ChunkNonce(baseNonce, counter);
            var aad = ChunkAad(counter, isFinal, context);
            var cipher = new byte[read];
            gcm.Encrypt(nonce, plain.AsSpan(0, read), cipher, tag, aad);

            BinaryPrimitives.WriteInt32BigEndian(lenBuf, read);
            await output.WriteAsync(lenBuf);
            await output.WriteAsync(tag);
            await output.WriteAsync(cipher.AsMemory(0, read));

            counter++;
            if (isFinal) break;
        }
    }

    public static async Task DecryptStreamAsync(byte[] key, Stream input, Stream output, byte[]? context = null)
    {
        var magic = new byte[FileMagic.Length];
        if (await ReadFullAsync(input, magic, magic.Length) != magic.Length
            || !((ReadOnlySpan<byte>)magic).SequenceEqual(FileMagic))
            throw new CryptographicException("Not a Galileo vault blob.");

        var baseNonce = new byte[NonceSize];
        if (await ReadFullAsync(input, baseNonce, NonceSize) != NonceSize)
            throw new CryptographicException("Truncated blob header.");

        using var gcm = new AesGcm(key, TagSize);
        var lenBuf = new byte[4];
        var tag = new byte[TagSize];
        long counter = 0;

        while (true)
        {
            int got = await ReadFullAsync(input, lenBuf, 4);
            if (got == 0) break; // clean EOF
            if (got != 4) throw new CryptographicException("Truncated chunk length.");

            int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
            if (len < 0 || len > ChunkSize) throw new CryptographicException("Corrupt chunk length.");
            if (await ReadFullAsync(input, tag, TagSize) != TagSize) throw new CryptographicException("Truncated tag.");

            var cipher = new byte[len];
            if (await ReadFullAsync(input, cipher, len) != len) throw new CryptographicException("Truncated chunk.");

            bool isFinal = input.Position >= input.Length;
            var nonce = ChunkNonce(baseNonce, counter);
            var aad = ChunkAad(counter, isFinal, context);
            var plain = new byte[len];
            gcm.Decrypt(nonce, cipher, tag, plain, aad); // throws on mismatch / truncation / reorder / swap
            await output.WriteAsync(plain.AsMemory(0, len));

            counter++;
            if (isFinal) break;
        }
    }

    /// <summary>The per-blob AAD context that binds ciphertext to its identity (defeats blob swapping).</summary>
    public static byte[] BlobContext(string blobId) => System.Text.Encoding.UTF8.GetBytes("blob:" + blobId);

    private static byte[] ChunkNonce(byte[] baseNonce, long counter)
    {
        var n = (byte[])baseNonce.Clone();
        for (int i = 0; i < 8; i++)
            n[NonceSize - 1 - i] ^= (byte)(counter >> (8 * i));
        return n;
    }

    private static byte[] ChunkAad(long counter, bool isFinal, byte[]? context)
    {
        var ctxLen = context?.Length ?? 0;
        var aad = new byte[9 + ctxLen];
        BinaryPrimitives.WriteInt64BigEndian(aad, counter);
        aad[8] = (byte)(isFinal ? 1 : 0);
        if (ctxLen > 0) Buffer.BlockCopy(context!, 0, aad, 9, ctxLen);
        return aad;
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = await s.ReadAsync(buffer.AsMemory(total, count - total));
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // ---------- Best-effort secure deletion ----------

    /// <summary>Overwrites a file's bytes with random data, then deletes it. Best-effort: on SSDs
    /// (wear-levelling / TRIM) the original blocks may persist; this is not a forensic guarantee.</summary>
    public static void OverwriteAndDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var fi = new FileInfo(path);
            if (fi.Attributes.HasFlag(FileAttributes.ReadOnly)) fi.Attributes = FileAttributes.Normal;
            long len = fi.Length;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[(int)Math.Min(len, ChunkSize)];
                if (buf.Length > 0)
                {
                    long written = 0;
                    while (written < len)
                    {
                        RandomNumberGenerator.Fill(buf);
                        int chunk = (int)Math.Min(buf.Length, len - written);
                        fs.Write(buf, 0, chunk);
                        written += chunk;
                    }
                    fs.Flush(true);
                }
            }
            File.Delete(path);
        }
        catch
        {
            try { File.Delete(path); } catch { /* give up */ }
        }
    }

    /// <summary>Securely deletes every file under <paramref name="dir"/>, then removes the tree.</summary>
    public static void WipeDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        if (HasReparsePoint(dir)) { try { Directory.Delete(dir, false); } catch { /* ignore */ } return; }
        WipeDirectoryCore(dir);
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }

    // Walks the tree without following reparse points (junctions/symlinks), so a link inside the
    // vault can never cause its TARGET to be overwritten; links are deleted as bare links instead.
    private static void WipeDirectoryCore(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (HasReparsePoint(f)) { try { File.Delete(f); } catch { /* ignore */ } }
                else OverwriteAndDelete(f);
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                if (HasReparsePoint(d)) { try { Directory.Delete(d, false); } catch { /* ignore */ } }
                else WipeDirectoryCore(d);
            }
        }
        catch { /* continue to the directory removal */ }
    }

    private static bool HasReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return true; } // can't inspect → treat as a link so we never overwrite a target
    }
}
