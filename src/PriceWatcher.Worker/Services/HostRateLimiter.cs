using System.Collections.Concurrent;

namespace PriceWatcher.Worker.Services;

public class HostRateLimiter
{
    private readonly ConcurrentDictionary<string, HostState> _hosts = new();
    private readonly int _minDelayMs;
    private readonly int _jitterMs;

    public HostRateLimiter(int minDelayMs = 2000, int jitterMs = 500)
    {
        _minDelayMs = minDelayMs;
        _jitterMs = jitterMs;
    }

    public async Task<IDisposable> AcquireAsync(string url, CancellationToken ct = default)
    {
        var host = GetHost(url);
        var state = _hosts.GetOrAdd(host, _ => new HostState());

        await state.Semaphore.WaitAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var timeSinceLast = now - state.LastRequestAt;
        var delay = _minDelayMs + Random.Shared.Next(_jitterMs);

        if (timeSinceLast.TotalMilliseconds < delay)
        {
            var waitTime = delay - (int)timeSinceLast.TotalMilliseconds;
            await Task.Delay(waitTime, ct);
        }

        state.LastRequestAt = DateTimeOffset.UtcNow;
        return new ReleaseHandle(state);
    }

    private static string GetHost(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host.ToLowerInvariant();
        return url;
    }

    private class HostState
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public DateTimeOffset LastRequestAt { get; set; } = DateTimeOffset.MinValue;
    }

    private class ReleaseHandle(HostState state) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            state.Semaphore.Release();
        }
    }
}
