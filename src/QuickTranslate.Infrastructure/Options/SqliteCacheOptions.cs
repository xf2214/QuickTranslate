namespace QuickTranslate.Infrastructure.Options;

public class SqliteCacheOptions
{
    public string DataDirectory { get; set; } = ".data";

    public string FileName { get; set; } = "translate_cache_v1.db";

    public int MaxKeepRows { get; set; } = 10000;

    public bool EnsureCreated { get; set; } = true;
}
