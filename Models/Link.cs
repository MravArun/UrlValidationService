using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace UrlValidationService.Models;

/// <summary>
/// Represents a stored link in MongoDB.
/// Design Decision: Links are stored first, then validated separately.
/// This decouples ingestion from validation, allowing batch processing.
/// </summary>
public class Link
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>
    /// Original URL as submitted by the user.
    /// </summary>
    [BsonElement("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Normalized URL for consistent comparison and deduplication.
    /// </summary>
    [BsonElement("normalizedUrl")]
    public string? NormalizedUrl { get; set; }

    /// <summary>
    /// Validation status - null means not yet validated.
    /// </summary>
    [BsonElement("status")]
    public LinkStatus? Status { get; set; }

    /// <summary>
    /// HTTP status code from last validation (null if not validated or DNS/connection failed).
    /// </summary>
    [BsonElement("httpStatusCode")]
    public int? HttpStatusCode { get; set; }

    /// <summary>
    /// Human-readable failure reason for broken links.
    /// </summary>
    [BsonElement("failureReason")]
    public string? FailureReason { get; set; }

    /// <summary>
    /// Response time in milliseconds from last validation.
    /// </summary>
    [BsonElement("responseTimeMs")]
    public long? ResponseTimeMs { get; set; }

    /// <summary>
    /// When the link was first added.
    /// </summary>
    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the link was last validated.
    /// </summary>
    [BsonElement("lastValidatedAt")]
    public DateTime? LastValidatedAt { get; set; }
}

/// <summary>
/// Link validation status.
/// Design Decision: Clear enum values for easy filtering and reporting.
/// </summary>
public enum LinkStatus
{
    /// <summary>
    /// Link returned 2xx or 3xx response - working correctly.
    /// </summary>
    Valid,

    /// <summary>
    /// Link is broken - 4xx, 5xx, timeout, DNS failure, etc.
    /// </summary>
    Broken,

    /// <summary>
    /// Validation is currently in progress.
    /// </summary>
    Validating
}
