using System.Runtime.InteropServices;

namespace QuickTranslate.Platform.UnmanagedMethods;

public static partial class Gdi32
{
    private const string DllName = "gdi32.dll";

    [LibraryImport(DllName)]
    public static partial int GetDeviceCaps(IntPtr hdc, int nIndex);

    public const int LOGPIXELSX = 88;
    public const int LOGPIXELSY = 90;
}
