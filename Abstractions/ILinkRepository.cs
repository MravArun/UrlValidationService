using UrlValidationService.Models;

namespace UrlValidationService.Abstractions;

/// <summary>
/// Repository for link persistence and querying.
/// Design Decision: Separate from validation results - links are the primary entity,
/// validation updates them in place.
/// </summary>
public interface ILinkRepository
{
    /// <summary>
    /// Adds a new link to the store.
    /// </summary>
    Task<Link> AddAsync(Link link, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk insert links (more efficient for batch imports).
    /// </summary>
    Task<int> AddBatchAsync(IEnumerable<Link> links, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a link by ID.
    /// </summary>
    Task<Link?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all stored links.
    /// </summary>
    Task<List<Link>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all links that need validation (not yet validated or stale).
    /// </summary>
    Task<List<Link>> GetPendingValidationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all broken links with their failure details.
    /// </summary>
    Task<List<Link>> GetBrokenLinksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a link's validation result.
    /// </summary>
    Task UpdateValidationResultAsync(
        string id,
        LinkStatus status,
        int? httpStatusCode,
        string? failureReason,
        long responseTimeMs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks links as currently being validated (prevents duplicate processing).
    /// Returns the IDs that were successfully marked.
    /// </summary>
    Task<List<string>> MarkAsValidatingAsync(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets total link count.
    /// </summary>
    Task<long> GetCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures indexes exist for optimal query performance.
    /// </summary>
    Task EnsureIndexesAsync(CancellationToken cancellationToken = default);
}
