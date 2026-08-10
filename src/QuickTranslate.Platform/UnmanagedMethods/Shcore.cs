using System.Runtime.InteropServices;

namespace QuickTranslate.Platform.UnmanagedMethods;

public static partial class Shcore
{
    private const string DllName = "shcore.dll";

    [LibraryImport(DllName)]
    public static partial int GetDpiForMonitor(IntPtr hmonitor, uint dpiType, out uint dpiX, out uint dpiY);

    public const uint MDT_EFFECTIVE_DPI = 0;
    public const uint MDT_ANGULAR_DPI = 1;
    public const uint MDT_RAW_DPI = 2;
    public const uint MDT_DEFAULT = MDT_EFFECTIVE_DPI;

    public const int S_OK = 0;
}
