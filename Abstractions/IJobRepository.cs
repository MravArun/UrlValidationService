using UrlValidationService.Models;

namespace UrlValidationService.Abstractions;

/// <summary>
/// Repository for validation job persistence.
/// Design Decision: Repository pattern isolates MongoDB details from business logic,
/// enabling unit testing with in-memory implementations.
/// </summary>
public interface IJobRepository
{
    /// <summary>
    /// Creates a new validation job.
    /// </summary>
    Task<ValidationJob> CreateAsync(ValidationJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves job by ID.
    /// </summary>
    Task<ValidationJob?> GetByIdAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims next queued job for processing.
    /// Uses findAndModify to prevent race conditions between multiple workers.
    /// 
    /// Interview Talking Point: This is critical for horizontal scaling - multiple workers
    /// can safely poll for jobs without duplicate processing.
    /// </summary>
    Task<ValidationJob?> ClaimNextQueuedJobAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates job progress during processing.
    /// </summary>
    Task UpdateProgressAsync(string jobId, int processedCount, int brokenCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks job as completed.
    /// </summary>
    Task CompleteAsync(string jobId, int totalProcessed, int totalBroken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks job as failed with error message.
    /// </summary>
    Task FailAsync(string jobId, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures indexes exist for optimal query performance.
    /// </summary>
    Task EnsureIndexesAsync(CancellationToken cancellationToken = default);
}
