namespace Revi;

internal class RateLimiter : IDisposable
{
    private readonly int _delayBetweenRequestsMs;
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly object _lock = new();

    public RateLimiter(int delayBetweenRequestsMs)
    {
        _delayBetweenRequestsMs = delayBetweenRequestsMs;
    }

    public async Task EnsureRateLimit(CancellationToken token = default)
    {
        if (_delayBetweenRequestsMs <= 0)
            return;

        TimeSpan delayNeeded;
        
        lock (_lock)
        {
            var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
            var minimumDelay = TimeSpan.FromMilliseconds(_delayBetweenRequestsMs);
            
            if (timeSinceLastRequest < minimumDelay)
            {
                delayNeeded = minimumDelay - timeSinceLastRequest;
            }
            else
            {
                delayNeeded = TimeSpan.Zero;
            }
            
            _lastRequestTime = DateTime.UtcNow + delayNeeded;
        }

        if (delayNeeded > TimeSpan.Zero)
        {
            await Task.Delay(delayNeeded, token);
        }
    }
    public void Dispose()
    {
    }
}