namespace UrlValidationService.Models;

/// <summary>
/// Internal validation result for caching purposes.
/// Design Decision: Used by cache layer to store validation outcomes.
/// </summary>
public class ValidationResult
{
    public string Url { get; set; } = string.Empty;
    public string OriginalUrl { get; set; } = string.Empty;
    public UrlStatus Status { get; set; }
    public int? HttpStatus { get; set; }
    public long ResponseTimeMs { get; set; }
    public string? ErrorReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// URL validation outcome for caching.
/// </summary>
public enum UrlStatus
{
    Valid,
    Invalid,
    ServerError,
    Timeout,
    DnsFailure,
    ConnectionFailed,
    RedirectLoop,
    Unreachable,
    Cached
}
