namespace UrlValidationService.Models;

/// <summary>
/// Validation service configuration.
/// Design Decision: All tunables in one place enables easy environment-specific overrides
/// via appsettings.{Environment}.json or environment variables.
/// </summary>
public class ValidationSettings
{
    public const string SectionName = "Validation";

    /// <summary>
    /// URL count threshold for sync vs async processing.
    /// Below/equal: synchronous. Above: asynchronous job.
    /// Trade-off: Lower value = faster individual responses, higher load on background worker.
    /// </summary>
    public int SyncThreshold { get; set; } = 10;

    /// <summary>
    /// HTTP request timeout per URL in seconds.
    /// Trade-off: Too low = false negatives on slow servers. Too high = slow batch processing.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum redirects to follow before flagging as redirect loop.
    /// </summary>
    public int MaxRedirects { get; set; } = 5;

    /// <summary>
    /// Concurrent HTTP requests during batch processing.
    /// Trade-off: Higher = faster processing but more memory/connections. Consider target server limits.
    /// </summary>
    public int MaxConcurrency { get; set; } = 20;

    /// <summary>
    /// URLs processed per batch in background worker.
    /// Enables progress updates and prevents long-running transactions.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Background worker polling interval in seconds.
    /// Trade-off: Lower = faster job pickup, higher DB load.
    /// </summary>
    public int WorkerPollingIntervalSeconds { get; set; } = 5;
}

/// <summary>
/// Cache configuration.
/// </summary>
public class CacheSettings
{
    public const string SectionName = "Cache";

    /// <summary>
    /// How long to cache validation results in minutes.
    /// Trade-off: Longer = fewer HTTP requests, staler data.
    /// </summary>
    public int TtlMinutes { get; set; } = 60;

    /// <summary>
    /// Maximum cache entries (LRU eviction when exceeded).
    /// </summary>
    public int MaxEntries { get; set; } = 10000;
}

/// <summary>
/// Resilience configuration for HTTP client.
/// </summary>
public class ResilienceSettings
{
    public const string SectionName = "Resilience";

    /// <summary>
    /// Circuit breaker failure threshold before opening.
    /// </summary>
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>
    /// Circuit breaker open duration in seconds.
    /// </summary>
    public int CircuitBreakerDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Max retry attempts for transient failures (async mode only).
    /// </summary>
    public int RetryAttempts { get; set; } = 2;

    /// <summary>
    /// Base delay between retries in milliseconds.
    /// Actual delay uses exponential backoff: baseDelay * 2^attempt.
    /// </summary>
    public int RetryBaseDelayMs { get; set; } = 500;

    /// <summary>
    /// Max requests per second per host (rate limiting).
    /// Prevents getting banned by target servers.
    /// </summary>
    public int MaxRequestsPerSecondPerHost { get; set; } = 10;
}

/// <summary>
/// MongoDB configuration.
/// </summary>
public class MongoSettings
{
    public const string SectionName = "MongoDB";

    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string DatabaseName { get; set; } = "url_validation";
    public string JobsCollection { get; set; } = "validation_jobs";
    public string ResultsCollection { get; set; } = "validation_results";

    /// <summary>
    /// TTL in hours for validation results (MongoDB auto-cleanup).
    /// </summary>
    public int ResultsTtlHours { get; set; } = 24;
}
