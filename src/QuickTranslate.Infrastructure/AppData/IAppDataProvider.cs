namespace QuickTranslate.Infrastructure.AppData;

public interface IAppDataProvider
{
    string GetAppDataDirectory();
    string GetLogDirectory();
}

public class DefaultAppDataProvider : IAppDataProvider
{
    public string GetAppDataDirectory()
    {
        var env = Environment.GetEnvironmentVariable("QUICKTRANSLATE_APPDATA");
        var dir = string.IsNullOrEmpty(env)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickTranslate")
            : env;
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetLogDirectory()
    {
        var env = Environment.GetEnvironmentVariable("QUICKTRANSLATE_LOGDIR");
        var dir = string.IsNullOrEmpty(env)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickTranslate", "Logs")
            : env;
        Directory.CreateDirectory(dir);
        return dir;
    }
}
