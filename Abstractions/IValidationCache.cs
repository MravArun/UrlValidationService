using UrlValidationService.Models;

namespace UrlValidationService.Abstractions;

/// <summary>
/// Cache abstraction for validation results.
/// Design Decision: Interface allows swapping implementations (in-memory → Redis → hybrid)
/// without changing validation logic. Currently uses in-memory for simplicity.
/// 
/// Interview Note: In production, this could be backed by Redis for distributed caching
/// across multiple API instances. The interface makes that migration trivial.
/// </summary>
public interface IValidationCache
{
    /// <summary>
    /// Attempts to retrieve cached validation result.
    /// </summary>
    /// <param name="normalizedUrl">Normalized URL as cache key</param>
    /// <returns>Cached result or null if not found/expired</returns>
    ValidationResult? Get(string normalizedUrl);

    /// <summary>
    /// Stores validation result in cache.
    /// </summary>
    /// <param name="normalizedUrl">Normalized URL as cache key</param>
    /// <param name="result">Result to cache</param>
    void Set(string normalizedUrl, ValidationResult result);

    /// <summary>
    /// Removes entry from cache (e.g., after manual re-validation request).
    /// </summary>
    /// <param name="normalizedUrl">Normalized URL key</param>
    void Remove(string normalizedUrl);

    /// <summary>
    /// Clears entire cache (admin operation).
    /// </summary>
    void Clear();

    /// <summary>
    /// Current cache statistics for monitoring.
    /// </summary>
    CacheStats GetStats();
}

/// <summary>
/// Cache health metrics.
/// </summary>
public class CacheStats
{
    public int EntryCount { get; set; }
    public long HitCount { get; set; }
    public long MissCount { get; set; }
    public double HitRate => HitCount + MissCount > 0 
        ? (double)HitCount / (HitCount + MissCount) 
        : 0;
}
