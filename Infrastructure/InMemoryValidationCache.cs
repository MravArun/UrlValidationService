using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using UrlValidationService.Abstractions;
using UrlValidationService.Models;

namespace UrlValidationService.Infrastructure;

/// <summary>
/// In-memory cache implementation with TTL and LRU eviction.
/// Design Decision: Uses ConcurrentDictionary for thread-safe access without locks.
/// 
/// Trade-offs:
/// - Pros: Fast, no external dependencies, good for single-instance deployments
/// - Cons: Not shared across instances, memory-bound, lost on restart
/// 
/// Interview Note: For distributed deployments, swap this for Redis implementation
/// via the IValidationCache interface. No business logic changes required.
/// </summary>
public class InMemoryValidationCache : IValidationCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly CacheSettings _settings;
    private readonly ILogger<InMemoryValidationCache> _logger;
    
    private long _hitCount;
    private long _missCount;

    public InMemoryValidationCache(
        IOptions<CacheSettings> settings, 
        ILogger<InMemoryValidationCache> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public ValidationResult? Get(string normalizedUrl)
    {
        if (_cache.TryGetValue(normalizedUrl, out var entry))
        {
            // Check TTL expiration
            if (DateTime.UtcNow - entry.CreatedAt < TimeSpan.FromMinutes(_settings.TtlMinutes))
            {
                Interlocked.Increment(ref _hitCount);
                entry.LastAccessed = DateTime.UtcNow; // Update for LRU tracking
                
                // Return a copy marked as cached
                return CloneWithCachedStatus(entry.Result);
            }
            
            // Expired - remove and treat as miss
            _cache.TryRemove(normalizedUrl, out _);
        }

        Interlocked.Increment(ref _missCount);
        return null;
    }

    public void Set(string normalizedUrl, ValidationResult result)
    {
        // Enforce max entries with LRU eviction
        if (_cache.Count >= _settings.MaxEntries)
        {
            EvictOldestEntries(_settings.MaxEntries / 10); // Evict 10% to avoid frequent evictions
        }

        var entry = new CacheEntry
        {
            Result = result,
            CreatedAt = DateTime.UtcNow,
            LastAccessed = DateTime.UtcNow
        };

        _cache.AddOrUpdate(normalizedUrl, entry, (_, _) => entry);
        
        _logger.LogDebug("Cached validation result for {Url}", normalizedUrl);
    }

    public void Remove(string normalizedUrl)
    {
        _cache.TryRemove(normalizedUrl, out _);
    }

    public void Clear()
    {
        _cache.Clear();
        _hitCount = 0;
        _missCount = 0;
        _logger.LogInformation("Cache cleared");
    }

    public CacheStats GetStats()
    {
        return new CacheStats
        {
            EntryCount = _cache.Count,
            HitCount = _hitCount,
            MissCount = _missCount
        };
    }

    /// <summary>
    /// LRU eviction - removes oldest accessed entries.
    /// </summary>
    private void EvictOldestEntries(int count)
    {
        var toEvict = _cache
            .OrderBy(kvp => kvp.Value.LastAccessed)
            .Take(count)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toEvict)
        {
            _cache.TryRemove(key, out _);
        }

        _logger.LogDebug("Evicted {Count} cache entries (LRU)", toEvict.Count);
    }

    /// <summary>
    /// Creates a copy of the result with Cached status to indicate source.
    /// </summary>
    private static ValidationResult CloneWithCachedStatus(ValidationResult original)
    {
        return new ValidationResult
        {
            Url = original.Url,
            OriginalUrl = original.OriginalUrl,
            Status = UrlStatus.Cached,
            HttpStatus = original.HttpStatus,
            ResponseTimeMs = 0, // Cached response is instant
            ErrorReason = original.ErrorReason,
            CreatedAt = original.CreatedAt
        };
    }

    private class CacheEntry
    {
        public ValidationResult Result { get; set; } = null!;
        public DateTime CreatedAt { get; set; }
        public DateTime LastAccessed { get; set; }
    }
}
