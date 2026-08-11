namespace QuickTranslate.Core.Abstractions;

public interface ISecretStore
{
    void Save(string key, string value);
    string? Load(string key);
    void Delete(string key);
    bool Exists(string key);
}
