using System.Text;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure.Options;
using QuickTranslate.Infrastructure.Persistence;
using QuickTranslate.Infrastructure.Security;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

public class SecretStoreTests : IDisposable
{
    readonly string _testDir;

    public SecretStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"qt_secrets_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    DpapiCurrentUserSecretStore CreateStore(string subDir = "store1", string entropyFile = "entropy.dat")
    {
        var opts = Options.Create(new SecretStoreOptions
        {
            DataDirectory = Path.Combine(_testDir, subDir),
            EntropyFile = entropyFile
        });
        return new DpapiCurrentUserSecretStore(opts);
    }

    [Fact]
    public void SaveThenLoad_SameValue()
    {
        var store = CreateStore();
        store.Save("k", "v123");
        var result = store.Load("k");
        Assert.Equal("v123", result);
    }

    [Fact]
    public void SaveEmpty_Deletes()
    {
        var store = CreateStore();
        store.Save("k", "v123");
        Assert.True(store.Exists("k"));
        store.Save("k", "");
        Assert.False(store.Exists("k"));
        Assert.Null(store.Load("k"));
    }

    [Fact]
    public void DeleteMissing_NoThrow()
    {
        var store = CreateStore();
        var ex = Record.Exception(() => store.Delete("nonexistent_key_xyz"));
        Assert.Null(ex);
    }

    [Fact]
    public void Exists_Miss_ReturnsFalse()
    {
        var store = CreateStore();
        Assert.False(store.Exists("missing_key_123"));
    }

    [Fact]
    public void Exists_Hit_True()
    {
        var store = CreateStore();
        store.Save("hit_key", "val");
        Assert.True(store.Exists("hit_key"));
    }

    [Fact]
    public void EntropyFile_Persists()
    {
        var sharedDir = "shared_store";
        var store1 = CreateStore(sharedDir);
        store1.Save("persist_key", "secret_value_456");

        var opts2 = Options.Create(new SecretStoreOptions
        {
            DataDirectory = Path.Combine(_testDir, sharedDir),
            EntropyFile = "entropy.dat"
        });
        var store2 = new DpapiCurrentUserSecretStore(opts2);
        var loaded = store2.Load("persist_key");
        Assert.Equal("secret_value_456", loaded);
    }

    [Fact]
    public void Save_NoPlaintextInFile()
    {
        var apiKey = "sk-qwen-test-api-key-abc123xyz789";
        var store = CreateStore("plaintext_check");
        store.Save("qwen.api.key", apiKey);

        var storeDir = Path.Combine(_testDir, "plaintext_check");
        var dpapiFiles = Directory.GetFiles(storeDir, "*.dpapi");
        Assert.NotEmpty(dpapiFiles);

        foreach (var f in dpapiFiles)
        {
            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(f));
            Assert.DoesNotContain(apiKey, raw);
        }

        var settingsDir = Path.Combine(_testDir, "settings_snap");
        Directory.CreateDirectory(settingsDir);
        var tempStoreOpts = Options.Create(new SecretStoreOptions
        {
            DataDirectory = Path.Combine(_testDir, "settings_snap_store"),
            EntropyFile = "entropy.dat"
        });
        var tempStore = new DpapiCurrentUserSecretStore(tempStoreOpts);
        var sm = new SettingsManager(settingsDir, tempStore);
        var settings = new AppSettings();
        sm.SetApiKey(apiKey);
        sm.SaveAsync(settings).GetAwaiter().GetResult();

        var settingsJson = File.ReadAllText(Path.Combine(settingsDir, "settings.json"));
        Assert.DoesNotContain("qwen.api.key", settingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, settingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", settingsJson, StringComparison.OrdinalIgnoreCase);
    }
}
