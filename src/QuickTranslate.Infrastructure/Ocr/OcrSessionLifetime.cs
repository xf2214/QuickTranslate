namespace QuickTranslate.Infrastructure.Ocr;

internal static class OcrSessionLifetime
{
    internal static async Task DisposeWhenReadyAsync<T>(Lazy<Task<T>>? initialization, Action<T> dispose)
    {
        if (initialization == null || !initialization.IsValueCreated) return;
        T resource;
        try
        {
            resource = await initialization.Value.ConfigureAwait(false);
        }
        catch
        {
            // Failed creation has no published resource. The initializer owns
            // partial construction cleanup and reports its original error.
            return;
        }
        dispose(resource);
    }
}
