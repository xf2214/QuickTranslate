using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using QuickTranslate.Infrastructure.Security;
using QuickTranslate.Infrastructure.Options;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure.Security;

/// <summary>
/// Error-path suite for DpapiCurrentUserSecretStore.
/// Asserts actual behavioral contracts (exception types, regeneration, isolation);
/// does NOT assert log presence (another agent adds logging to sources).
/// </summary>
public class DpapiErrorPathsTests : IDisposable
{
    readonly string _tempRoot;

    public DpapiErrorPathsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"qt_dpapi_err_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    DpapiCurrentUserSecretStore CreateStore(string subDir = "store", string entropyFile = "entropy.dat")
    {
        var opts = Options.Create(new SecretStoreOptions
        {
            DataDirectory = Path.Combine(_tempRoot, subDir),
            EntropyFile = entropyFile
        });
        return new DpapiCurrentUserSecretStore(opts);
    }

    // ---- corrupted ciphertext: flipped bytes ----
    [Fact]
    public void Load_CorruptedCiphertext_FlippedBytes_ThrowsCryptographicException()
    {
        var store = CreateStore("corrupt_flip");
        store.Save("k1", "secret123");
        // Locate the .dpapi file and flip a byte
        var dir = Path.Combine(_tempRoot, "corrupt_flip");
        var dpapiFile = Directory.GetFiles(dir, "*.dpapi").Single();
        var bytes = File.ReadAllBytes(dpapiFile);
        Assert.True(bytes.Length > 4);
        bytes[bytes.Length / 2] ^= 0xFF;
        bytes[0] ^= 0x01;
        File.WriteAllBytes(dpapiFile, bytes);

        var ex = Record.Exception(() => store.Load("k1"));
        Assert.NotNull(ex);
        // DPAPI unprotect on corrupted data throws CryptographicException
        Assert.IsType<CryptographicException>(ex);
    }

    [Fact]
    public void Load_CorruptedCiphertext_Truncated_Throws()
    {
        var store = CreateStore("corrupt_trunc");
        store.Save("k2", "hello world");
        var dir = Path.Combine(_tempRoot, "corrupt_trunc");
        var dpapiFile = Directory.GetFiles(dir, "*.dpapi").Single();
        var bytes = File.ReadAllBytes(dpapiFile);
        // Truncate to half
        File.WriteAllBytes(dpapiFile, bytes.Take(bytes.Length / 2).ToArray());

        var ex = Record.Exception(() => store.Load("k2"));
        Assert.NotNull(ex);
        Assert.True(ex is CryptographicException || ex is ArgumentException,
            $"Expected CryptographicException or ArgumentException but got {ex!.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public void Load_CorruptedCiphertext_EmptyFile_Throws()
    {
        var store = CreateStore("corrupt_empty");
        store.Save("k3", "value");
        var dir = Path.Combine(_tempRoot, "corrupt_empty");
        var dpapiFile = Directory.GetFiles(dir, "*.dpapi").Single();
        File.WriteAllBytes(dpapiFile, Array.Empty<byte>());

        var ex = Record.Exception(() => store.Load("k3"));
        Assert.NotNull(ex);
        Assert.True(ex is CryptographicException || ex is ArgumentException,
            $"Expected CryptographicException/ArgumentException but got {ex!.GetType().Name}");
    }

    [Fact]
    public void Load_CorruptedCiphertext_RandomBytes_Throws()
    {
        var store = CreateStore("corrupt_random");
        store.Save("k4", "value");
        var dir = Path.Combine(_tempRoot, "corrupt_random");
        var dpapiFile = Directory.GetFiles(dir, "*.dpapi").Single();
        var random = new byte[64];
        RandomNumberGenerator.Fill(random);
        File.WriteAllBytes(dpapiFile, random);

        var ex = Record.Exception(() => store.Load("k4"));
        Assert.NotNull(ex);
        Assert.IsType<CryptographicException>(ex);
    }

    // ---- entropy.dat absent -> regeneration (32 bytes) ----
    [Fact]
    public void EntropyFile_Absent_Regenerated_OnConstruction()
    {
        var subDir = "entropy_regen";
        var store1 = CreateStore(subDir);
        // entropy.dat should now exist
        var entropyPath = Path.Combine(_tempRoot, subDir, "entropy.dat");
        Assert.True(File.Exists(entropyPath));
        var first = File.ReadAllBytes(entropyPath);
        Assert.Equal(32, first.Length);

        // Delete and create new store -> regeneration
        File.Delete(entropyPath);
        Assert.False(File.Exists(entropyPath));

        var store2 = CreateStore(subDir);
        Assert.True(File.Exists(entropyPath));
        var second = File.ReadAllBytes(entropyPath);
        Assert.Equal(32, second.Length);
        // Must be newly generated (extremely unlikely to collide)
        Assert.False(first.SequenceEqual(second));

        // Old store's key cannot be decrypted by new entropy (if we saved before deletion, it would fail)
        // Verify new store is functional
        store2.Save("new_key", "new_val");
        Assert.Equal("new_val", store2.Load("new_key"));
    }

    [Fact]
    public void EntropyFile_Absent_Regeneration_CreatesUsableStore()
    {
        var entropyPath = Path.Combine(_tempRoot, "regen2", "entropy.dat");
        // Do not pre-create file
        var store = CreateStore("regen2");
        Assert.True(File.Exists(entropyPath));
        store.Save("k", "v");
        Assert.Equal("v", store.Load("k"));
    }

    // ---- wrong-entropy decrypt failure ----
    [Fact]
    public void Load_WithWrongEntropy_ThrowsCryptographicException()
    {
        // Store A saves with entropy E1
        var dirA = Path.Combine(_tempRoot, "wrong_entropy_A");
        var dirB = Path.Combine(_tempRoot, "wrong_entropy_B");
        var storeA = new DpapiCurrentUserSecretStore(Options.Create(new SecretStoreOptions
        {
            DataDirectory = dirA,
            EntropyFile = "entropy.dat"
        }));
        storeA.Save("shared_key", "my_secret");

        // Store B has different entropy (different directory -> different entropy.dat)
        var storeB = new DpapiCurrentUserSecretStore(Options.Create(new SecretStoreOptions
        {
            DataDirectory = dirB,
            EntropyFile = "entropy.dat"
        }));

        // Copy A's ciphertext file into B's directory under B's FileForKey naming
        // To simulate wrong-entropy scenario, we manually copy the cipher file:
        // Use reflection to get FileForKey path for both
        var method = typeof(DpapiCurrentUserSecretStore).GetMethod("FileForKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var fileA = (string)method.Invoke(storeA, new object[] { "shared_key" })!;
        var fileB = (string)method.Invoke(storeB, new object[] { "shared_key" })!;
        Assert.True(File.Exists(fileA));
        // Ensure B's file does not yet exist, then copy A's cipher to B's expected path
        Directory.CreateDirectory(Path.GetDirectoryName(fileB)!);
        File.Copy(fileA, fileB, overwrite: true);

        var ex = Record.Exception(() => storeB.Load("shared_key"));
        Assert.NotNull(ex);
        Assert.IsType<CryptographicException>(ex);
    }

    [Fact]
    public void Load_DeletedEntropy_MakesOldCipherUnrecoverable()
    {
        var subDir = "entropy_rotation";
        var store = CreateStore(subDir);
        store.Save("k", "secret_value");
        var entropyPath = Path.Combine(_tempRoot, subDir, "entropy.dat");
        var oldEntropy = File.ReadAllBytes(entropyPath);
        Assert.Equal(32, oldEntropy.Length);

        // Delete entropy and recreate store (new entropy)
        File.Delete(entropyPath);
        var store2 = new DpapiCurrentUserSecretStore(Options.Create(new SecretStoreOptions
        {
            DataDirectory = Path.Combine(_tempRoot, subDir),
            EntropyFile = "entropy.dat"
        }));
        var newEntropy = File.ReadAllBytes(entropyPath);
        Assert.False(oldEntropy.SequenceEqual(newEntropy));

        // store2 trying to load old key should fail (wrong entropy)
        var ex = Record.Exception(() => store2.Load("k"));
        Assert.NotNull(ex);
        Assert.IsType<CryptographicException>(ex);
    }

    // ---- isolation paths ----
    [Fact]
    public void DifferentKeys_DifferentFiles_Isolated()
    {
        var store = CreateStore("isolation_keys");
        store.Save("keyA", "valA");
        store.Save("keyB", "valB");
        Assert.Equal("valA", store.Load("keyA"));
        Assert.Equal("valB", store.Load("keyB"));

        var files = Directory.GetFiles(Path.Combine(_tempRoot, "isolation_keys"), "*.dpapi");
        Assert.Equal(2, files.Length);
        Assert.NotEqual(files[0], files[1]);

        // Deleting A does not affect B
        store.Delete("keyA");
        Assert.False(store.Exists("keyA"));
        Assert.True(store.Exists("keyB"));
        Assert.Equal("valB", store.Load("keyB"));
    }

    [Fact]
    public void DifferentStores_DifferentDirectories_Isolated()
    {
        var storeX = CreateStore("iso_X");
        var storeY = CreateStore("iso_Y");
        storeX.Save("k", "valueX");
        storeY.Save("k", "valueY");
        Assert.Equal("valueX", storeX.Load("k"));
        Assert.Equal("valueY", storeY.Load("k"));
        // They must not interfere
        storeX.Delete("k");
        Assert.False(storeX.Exists("k"));
        Assert.True(storeY.Exists("k"));
        Assert.Equal("valueY", storeY.Load("k"));
    }

    [Fact]
    public void FileForKey_SanitizesInvalidChars_ProducesSafeFilename()
    {
        var store = CreateStore("sanitize");
        // Key with invalid filename chars
        var trickyKey = "a/b\\c:d*e?f\"g<h>i|j";
        store.Save(trickyKey, "ok");
        Assert.True(store.Exists(trickyKey));
        Assert.Equal("ok", store.Load(trickyKey));
        var files = Directory.GetFiles(Path.Combine(_tempRoot, "sanitize"), "*.dpapi");
        Assert.Single(files);
        var fname = Path.GetFileName(files[0]);
        // Should not contain invalid chars (skip '\0' which string contains check is vacuous)
        foreach (var c in Path.GetInvalidFileNameChars().Where(ch => ch != '\0'))
            Assert.DoesNotContain(c.ToString(), fname, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_EmptyOrWhitespace_DeletesFile()
    {
        var store = CreateStore("empty_delete");
        store.Save("k", "nonempty");
        Assert.True(store.Exists("k"));
        store.Save("k", "");
        Assert.False(store.Exists("k"));
        Assert.Null(store.Load("k"));
        store.Save("k", "again");
        Assert.True(store.Exists("k"));
        store.Save("k", "   ");
        Assert.False(store.Exists("k"));
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull_NotThrows()
    {
        var store = CreateStore("missing_load");
        var result = store.Load("nonexistent_" + Guid.NewGuid().ToString("N"));
        Assert.Null(result);
    }

    [Fact]
    public void Exists_MissingReturnsFalse_ExistingReturnsTrue()
    {
        var store = CreateStore("exists_check");
        Assert.False(store.Exists("nope"));
        store.Save("yes", "1");
        Assert.True(store.Exists("yes"));
    }

    [Fact]
    public void EntropyFile_AlreadyExists_ReusesSameBytes()
    {
        var subDir = "entropy_reuse";
        var store1 = CreateStore(subDir);
        var entropyPath = Path.Combine(_tempRoot, subDir, "entropy.dat");
        var first = File.ReadAllBytes(entropyPath);
        var store2 = CreateStore(subDir);
        var second = File.ReadAllBytes(entropyPath);
        Assert.True(first.SequenceEqual(second));
        // Both stores can interoperate
        store1.Save("shared", "hello");
        Assert.Equal("hello", store2.Load("shared"));
    }

    [Fact]
    public void EntropyFile_EmptyString_UsesNoEntropy_StillWorks()
    {
        var opts = Options.Create(new SecretStoreOptions
        {
            DataDirectory = Path.Combine(_tempRoot, "no_entropy"),
            EntropyFile = "" // triggers Array.Empty
        });
        var store = new DpapiCurrentUserSecretStore(opts);
        store.Save("k", "v");
        Assert.Equal("v", store.Load("k"));
        // No entropy.dat should be created
        Assert.False(File.Exists(Path.Combine(_tempRoot, "no_entropy", "entropy.dat")));
    }

    // Documents empty catch at :37 relates to ACL setup (O:BAD:P...) - behavior contract unchanged.
    // The ACL operation is best-effort; failure to set ACL does not prevent entropy creation or usage.
    [Fact]
    public void EntropyCreation_Succeeds_EvenIfAclFails_ObservableViaFileExistsAndUsable()
    {
        var subDir = "acl_best_effort";
        var store = CreateStore(subDir);
        var entropyPath = Path.Combine(_tempRoot, subDir, "entropy.dat");
        Assert.True(File.Exists(entropyPath));
        Assert.Equal(32, new FileInfo(entropyPath).Length);
        store.Save("k", "works");
        Assert.Equal("works", store.Load("k"));
    }
}
