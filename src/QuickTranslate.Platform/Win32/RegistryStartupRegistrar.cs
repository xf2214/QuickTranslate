using System.Diagnostics;
using Microsoft.Win32;
using QuickTranslate.Core.Abstractions;

namespace QuickTranslate.Platform.Win32;

public class RegistryStartupRegistrar : IStartupRegistrar
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppValueName = "QuickTranslate";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var raw = key?.GetValue(AppValueName) as string;
            if (string.IsNullOrEmpty(raw))
                return false;
            var exePath = ExtractExePath(raw);
            var expected = GetExpectedExePathPrefix();
            return exePath.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    static string ExtractExePath(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end > 0)
                return s.Substring(1, end - 1);
            return s.Substring(1);
        }
        int space = s.IndexOf(' ');
        if (space > 0)
            return s.Substring(0, space);
        return s;
    }

    static string GetExpectedExePathPrefix() => Process.GetCurrentProcess().MainModule!.FileName;

    public void Enable(string? args = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        var path = GetExpectedExePathPrefix();
        key.SetValue(AppValueName, string.IsNullOrEmpty(args) ? $"\"{path}\"" : $"\"{path}\" {args}");
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(AppValueName, throwOnMissingValue: false);
    }

    public string? GetCommandLine()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(AppValueName) as string;
    }
}
