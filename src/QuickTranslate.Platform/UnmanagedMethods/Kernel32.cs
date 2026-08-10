using System.Runtime.InteropServices;

namespace QuickTranslate.Platform.UnmanagedMethods;

public static partial class Kernel32
{
    private const string DllName = "kernel32.dll";

    [LibraryImport(DllName, EntryPoint = "GetModuleHandleW", SetLastError = true)]
    public static partial IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);
}
