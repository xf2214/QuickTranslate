using System.Runtime.InteropServices;

namespace QuickTranslate.Platform.UnmanagedMethods;

public static partial class Gdi32
{
    private const string DllName = "gdi32.dll";

    [LibraryImport(DllName)]
    public static partial int GetDeviceCaps(IntPtr hdc, int nIndex);

    public const int LOGPIXELSX = 88;
    public const int LOGPIXELSY = 90;

    [LibraryImport(DllName)]
    public static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport(DllName)]
    public static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [LibraryImport(DllName)]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport(DllName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(IntPtr hdc, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    public const uint SRCCOPY = 0x00CC0020;

    [LibraryImport(DllName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport(DllName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr ho);
}
