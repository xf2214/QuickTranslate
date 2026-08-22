using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Infrastructure.Options;

namespace QuickTranslate.Infrastructure.Security;

public class DpapiCurrentUserSecretStore : ISecretStore
{
    readonly string _storeDir;
    readonly byte[] _entropy;
    readonly ILogger<DpapiCurrentUserSecretStore> _logger;

    public DpapiCurrentUserSecretStore(IOptions<SecretStoreOptions> opts, ILogger<DpapiCurrentUserSecretStore>? logger = null)
    {
        _logger = logger ?? NullLogger<DpapiCurrentUserSecretStore>.Instance;
        var dir = Path.IsPathRooted(opts.Value.DataDirectory) ? opts.Value.DataDirectory
                    : Path.Combine(AppContext.BaseDirectory, opts.Value.DataDirectory);
        Directory.CreateDirectory(dir);
        _storeDir = dir;
        _entropy = !string.IsNullOrEmpty(opts.Value.EntropyFile)
                   ? LoadOrCreateEntropyFile(Path.Combine(dir, opts.Value.EntropyFile))
                   : Array.Empty<byte>();
    }

    byte[] LoadOrCreateEntropyFile(string path)
    {
        if (File.Exists(path)) return File.ReadAllBytes(path);
        var b = new byte[32];
        RandomNumberGenerator.Fill(b);
        File.WriteAllBytes(path, b);
        try
        {
            var fi = new FileInfo(path);
            var fs = fi.GetAccessControl();
            fs.SetSecurityDescriptorSddlForm("O:BAD:P(A;;FA;;;WD)");
        }
        catch (Exception ex)
        {
            // SECURITY-relevant: ACL hardening failed — entropy file may be more broadly accessible than intended.
            // Never log key material or file content; only exception type/message and error code.
            _logger.LogWarning(ex, "[SecretStore.EntropyAcl] Failed to set restrictive ACL on entropy file [ErrorCode=SECRETSTORE_ENTROPY_ACL_FAIL] {ExType}: {Message}", ex.GetType().Name, ex.Message);
        }
        return b;
    }

    string FileForKey(string key)
    {
        var safe = string.Join("_", key.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_storeDir, $"{safe}_{hash.Substring(0, 12)}.dpapi");
    }

    public void Save(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { Delete(key); return; }
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = ProtectedData.Protect(plain, _entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FileForKey(key), cipher);
    }

    public string? Load(string key)
    {
        var f = FileForKey(key);
        if (!File.Exists(f)) return null;
        var cipher = File.ReadAllBytes(f);
        var plain = ProtectedData.Unprotect(cipher, _entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public void Delete(string key)
    {
        var f = FileForKey(key);
        if (File.Exists(f)) File.Delete(f);
    }

    public bool Exists(string key) => File.Exists(FileForKey(key));
}
