namespace QuickTranslate.Core.Geometry;

public readonly struct MonitorId : IEquatable<MonitorId>
{
    public IntPtr Handle { get; }
    public string DeviceName { get; }

    public static MonitorId Empty => new(IntPtr.Zero, string.Empty);

    public MonitorId(IntPtr handle, string deviceName)
    {
        Handle = handle;
        DeviceName = deviceName ?? string.Empty;
    }

    public bool IsEmpty => Handle == IntPtr.Zero;

    public bool Equals(MonitorId other) =>
        Handle == other.Handle &&
        string.Equals(DeviceName, other.DeviceName, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is MonitorId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Handle, DeviceName);

    public override string ToString() =>
        string.IsNullOrEmpty(DeviceName) ? $"Monitor(0x{Handle.ToInt64():X16})" : DeviceName;

    public static bool operator ==(MonitorId left, MonitorId right) => left.Equals(right);

    public static bool operator !=(MonitorId left, MonitorId right) => !left.Equals(right);
}
