namespace QuickTranslate.Infrastructure.Options;

public class SecretStoreOptions
{
    public string DataDirectory { get; set; } = ".data/secrets";
    public string EntropyFile { get; set; } = "entropy.dat";
}
