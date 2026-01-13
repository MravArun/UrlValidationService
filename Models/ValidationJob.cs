using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace UrlValidationService.Models;

/// <summary>
/// Represents a batch validation job stored in MongoDB.
/// Design Decision: Separate job tracking from link updates allows:
/// - Efficient status polling without loading all links
/// - Multiple concurrent validation jobs
/// - Job history and audit trail
/// 
/// Interview Talking Point: For millions of URLs, synchronous validation would timeout.
/// Background jobs enable scalable processing while keeping API responsive.
/// </summary>
public class ValidationJob
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string JobId { get; set; } = Guid.NewGuid().ToString();

    [BsonElement("status")]
    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>
    /// Total links to validate in this job.
    /// </summary>
    [BsonElement("totalLinks")]
    public int TotalLinks { get; set; }

    /// <summary>
    /// Links validated so far.
    /// </summary>
    [BsonElement("processedCount")]
    public int ProcessedCount { get; set; }

    /// <summary>
    /// Broken links found so far.
    /// </summary>
    [BsonElement("brokenCount")]
    public int BrokenCount { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("startedAt")]
    public DateTime? StartedAt { get; set; }

    [BsonElement("completedAt")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Error message if job failed during processing.
    /// </summary>
    [BsonElement("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Job lifecycle states.
/// Design Decision: Using enum for type safety and preventing invalid states.
/// </summary>
public enum JobStatus
{
    Queued,      // Job created, waiting for worker pickup
    Processing,  // Worker is actively processing links
    Completed,   // All links validated successfully
    Failed       // Unrecoverable error occurred
}
