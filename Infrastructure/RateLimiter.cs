using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using UrlValidationService.Models;

namespace UrlValidationService.Infrastructure;

/// <summary>
/// Per-host rate limiter to prevent abuse and avoid getting banned.
/// Design Decision: Token bucket algorithm per host - simple and effective.
/// 
/// Trade-off: In-memory state means rate limits don't work across instances.
/// For true distributed rate limiting, use Redis with sliding window.
/// 
/// Interview Note: This is a courtesy rate limiter to avoid overwhelming target servers.
/// It's not a security feature - external rate limiting should be handled by API gateway.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Waits until a request slot is available for the given host.
    /// </summary>
    Task WaitForSlotAsync(string host, CancellationToken cancellationToken = default);
}

public class PerHostRateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostSemaphores = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastRequestTimes = new();
    private readonly ResilienceSettings _settings;
    private readonly ILogger<PerHostRateLimiter> _logger;

    public PerHostRateLimiter(
        IOptions<ResilienceSettings> settings,
        ILogger<PerHostRateLimiter> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task WaitForSlotAsync(string host, CancellationToken cancellationToken = default)
    {
        // Get or create semaphore for this host (limits concurrent requests)
        var semaphore = _hostSemaphores.GetOrAdd(
            host.ToLowerInvariant(),
            _ => new SemaphoreSlim(_settings.MaxRequestsPerSecondPerHost, _settings.MaxRequestsPerSecondPerHost));

        await semaphore.WaitAsync(cancellationToken);

        try
        {
            // Enforce minimum time between requests to this host
            var minInterval = TimeSpan.FromMilliseconds(1000.0 / _settings.MaxRequestsPerSecondPerHost);
            
            if (_lastRequestTimes.TryGetValue(host, out var lastRequest))
            {
                var elapsed = DateTime.UtcNow - lastRequest;
                if (elapsed < minInterval)
                {
                    var delay = minInterval - elapsed;
                    _logger.LogDebug("Rate limiting: waiting {Delay}ms for {Host}", delay.TotalMilliseconds, host);
                    await Task.Delay(delay, cancellationToken);
                }
            }

            _lastRequestTimes[host] = DateTime.UtcNow;
        }
        finally
        {
            semaphore.Release();
        }
    }
}
