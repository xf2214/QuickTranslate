using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace QuickTranslate.Infrastructure.SingleInstance;

public class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _createdNew;
    private bool _disposed;

    public bool IsPrimaryInstance => _createdNew;

    public SingleInstanceGuard() : this(GenerateDefaultMutexName()) { }

    public SingleInstanceGuard(string mutexName)
    {
        _mutex = new Mutex(true, mutexName, out _createdNew);
    }

    public bool TryEnsureSingle()
    {
        return _createdNew;
    }

    private static string GenerateDefaultMutexName()
    {
        var appId = "QuickTranslate";
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(appId));
        var hash = Convert.ToBase64String(hashBytes)
            .Replace('/', '_')
            .Replace('+', '-')
            .TrimEnd('=');
        return $"QuickTranslate-{hash}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_createdNew)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ObjectDisposedException) { }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
        GC.SuppressFinalize(this);
    }
}
