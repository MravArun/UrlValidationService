using System.ComponentModel.DataAnnotations;

namespace UrlValidationService.Models;

// =============================================================================
// REQUEST DTOs
// =============================================================================

/// <summary>
/// Request DTO for adding links.
/// POST /api/links
/// </summary>
public class AddLinksRequest
{
    /// <summary>
    /// List of URLs to store. Max 10,000 per request.
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one URL is required")]
    [MaxLength(10000, ErrorMessage = "Maximum 10,000 URLs per request")]
    public List<string> Urls { get; set; } = new();
}

// =============================================================================
// RESPONSE DTOs
// =============================================================================

/// <summary>
/// Response for adding links.
/// </summary>
public class AddLinksResponse
{
    public int AddedCount { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for triggering validation.
/// </summary>
public class ValidationTriggerResponse
{
    /// <summary>
    /// True if validation completed synchronously.
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Job ID for async tracking (null if sync).
    /// </summary>
    public string? JobId { get; set; }

    /// <summary>
    /// Total links being validated.
    /// </summary>
    public int TotalLinks { get; set; }

    /// <summary>
    /// Links validated so far (for async progress).
    /// </summary>
    public int ValidatedCount { get; set; }

    /// <summary>
    /// Links found to be broken so far.
    /// </summary>
    public int BrokenCount { get; set; }

    /// <summary>
    /// Human-readable message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for broken links query.
/// GET /api/links/broken
/// </summary>
public class BrokenLinksResponse
{
    public int TotalBroken { get; set; }
    public List<BrokenLinkDto> Links { get; set; } = new();
}

/// <summary>
/// Individual broken link details.
/// </summary>
public class BrokenLinkDto
{
    /// <summary>
    /// The original URL that was stored.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// HTTP status code if available (e.g., 404, 500).
    /// </summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>
    /// Human-readable failure reason.
    /// Examples: "404 Not Found", "Connection timeout", "DNS resolution failed"
    /// </summary>
    public string FailureReason { get; set; } = string.Empty;

    /// <summary>
    /// Response time in milliseconds (time until failure).
    /// </summary>
    public long ResponseTimeMs { get; set; }

    /// <summary>
    /// When this link was last validated.
    /// </summary>
    public DateTime? LastValidatedAt { get; set; }

    /// <summary>
    /// When this link was first added to the system.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Link summary DTO for general queries.
/// </summary>
public class LinkDto
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Status { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? FailureReason { get; set; }
    public DateTime? LastValidatedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Standardized error response.
/// </summary>
public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? TraceId { get; set; }
}

// =============================================================================
// LEGACY DTOs (kept for background job system)
// =============================================================================

/// <summary>
/// Progress information for async job polling.
/// </summary>
public class ProgressInfo
{
    public int TotalUrls { get; set; }
    public int ProcessedCount { get; set; }
    public int PercentComplete => TotalUrls > 0 ? (ProcessedCount * 100) / TotalUrls : 0;
}
