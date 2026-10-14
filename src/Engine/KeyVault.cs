using System.Diagnostics;
using System.Security.Cryptography;

namespace Funnel.Engine;

/// <summary>
/// Encrypts API keys for the current user. Windows: DPAPI. macOS: AES-GCM under a random key kept
/// in the user's login Keychain (a 0600 file next to the config if the Keychain can't be reached).
/// </summary>
public static class KeyVault
{
    public static byte[] Protect(byte[] data, byte[] entropy)
    {
#if WINDOWS
        return ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);
#else
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(MasterKey.Value, 16)) aes.Encrypt(nonce, data, cipher, tag, entropy);
        return nonce.Concat(tag).Concat(cipher).ToArray();
#endif
    }

    public static byte[] Unprotect(byte[] blob, byte[] entropy)
    {
#if WINDOWS
        return ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser);
#else
        if (blob.Length < 28) throw new CryptographicException("Too short.");
        var plain = new byte[blob.Length - 28];
        using (var aes = new AesGcm(MasterKey.Value, 16)) aes.Decrypt(blob[..12], blob[28..], blob[12..28], plain, entropy);
        return plain;
#endif
    }

#if !WINDOWS
    private const string Service = "Funnel", Account = "api-keys";
    private static readonly Lazy<byte[]> MasterKey = new(LoadOrCreateKey);

    private static byte[] LoadOrCreateKey()
    {
        // A key file written while the Keychain was unreachable stays the key, so nothing saved is lost.
        var file = Path.Combine(AppPaths.Home, ".keyvault");
        if (TryRead(File.Exists(file) ? SafeRead(file) : null) is { } fromFile) return fromFile;

        if (TryRead(Security($"find-generic-password -s {Service} -a {Account} -w")) is { } found) return found;

        var key = RandomNumberGenerator.GetBytes(32);
        var b64 = Convert.ToBase64String(key);
        Security($"add-generic-password -U -s {Service} -a {Account} -l \"Funnel API keys\" -w {b64}");
        if (TryRead(Security($"find-generic-password -s {Service} -a {Account} -w")) is { } stored) return stored;

        File.WriteAllText(file, b64);
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        return key;
    }

    private static string? SafeRead(string path) { try { return File.ReadAllText(path); } catch { return null; } }

    private static byte[]? TryRead(string? b64)
    {
        try { var k = Convert.FromBase64String((b64 ?? "").Trim()); return k.Length == 32 ? k : null; }
        catch { return null; }
    }

    /// <summary>Runs /usr/bin/security; its standard output, or null if it failed.</summary>
    private static string? Security(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/security", args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(10000) && p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
#endif
}
