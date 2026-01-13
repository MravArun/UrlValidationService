using Microsoft.Extensions.Options;
using MongoDB.Driver;
using UrlValidationService.Abstractions;
using UrlValidationService.Models;

namespace UrlValidationService.Repositories;

/// <summary>
/// MongoDB repository for stored links.
/// Design Decision: Links are the primary entity - validation updates them in place.
/// </summary>
public class LinkRepository : ILinkRepository
{
    private readonly IMongoCollection<Link> _collection;
    private readonly ILogger<LinkRepository> _logger;

    public LinkRepository(
        IOptions<MongoSettings> settings,
        ILogger<LinkRepository> logger)
    {
        var client = new MongoClient(settings.Value.ConnectionString);
        var database = client.GetDatabase(settings.Value.DatabaseName);
        _collection = database.GetCollection<Link>("links");
        _logger = logger;
    }

    public async Task<Link> AddAsync(Link link, CancellationToken cancellationToken = default)
    {
        await _collection.InsertOneAsync(link, cancellationToken: cancellationToken);
        _logger.LogDebug("Added link: {Url}", link.Url);
        return link;
    }

    /// <summary>
    /// Bulk insert for efficient batch imports.
    /// Interview Note: InsertMany is significantly more efficient than individual inserts.
    /// </summary>
    public async Task<int> AddBatchAsync(IEnumerable<Link> links, CancellationToken cancellationToken = default)
    {
        var linkList = links.ToList();
        if (linkList.Count == 0) return 0;

        await _collection.InsertManyAsync(linkList, cancellationToken: cancellationToken);
        _logger.LogInformation("Added {Count} links in batch", linkList.Count);
        return linkList.Count;
    }

    public async Task<Link?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _collection
            .Find(l => l.Id == id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<Link>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _collection
            .Find(_ => true)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets links that haven't been validated yet or aren't currently being validated.
    /// </summary>
    public async Task<List<Link>> GetPendingValidationAsync(CancellationToken cancellationToken = default)
    {
        var filter = Builders<Link>.Filter.Or(
            Builders<Link>.Filter.Eq(l => l.Status, null),
            Builders<Link>.Filter.Ne(l => l.Status, LinkStatus.Validating)
        );

        return await _collection
            .Find(filter)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets all broken links with full details.
    /// Interview Note: This query benefits from the index on status field.
    /// </summary>
    public async Task<List<Link>> GetBrokenLinksAsync(CancellationToken cancellationToken = default)
    {
        return await _collection
            .Find(l => l.Status == LinkStatus.Broken)
            .SortByDescending(l => l.LastValidatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Updates validation result for a single link.
    /// </summary>
    public async Task UpdateValidationResultAsync(
        string id,
        LinkStatus status,
        int? httpStatusCode,
        string? failureReason,
        long responseTimeMs,
        CancellationToken cancellationToken = default)
    {
        var update = Builders<Link>.Update
            .Set(l => l.Status, status)
            .Set(l => l.HttpStatusCode, httpStatusCode)
            .Set(l => l.FailureReason, failureReason)
            .Set(l => l.ResponseTimeMs, responseTimeMs)
            .Set(l => l.LastValidatedAt, DateTime.UtcNow);

        await _collection.UpdateOneAsync(
            l => l.Id == id,
            update,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Atomically marks links as being validated to prevent duplicate processing.
    /// Uses updateMany with a filter to only update links not already being validated.
    /// </summary>
    public async Task<List<string>> MarkAsValidatingAsync(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return new List<string>();

        // Filter: ID in list AND not currently validating
        var filter = Builders<Link>.Filter.And(
            Builders<Link>.Filter.In(l => l.Id, idList),
            Builders<Link>.Filter.Ne(l => l.Status, LinkStatus.Validating)
        );

        var update = Builders<Link>.Update
            .Set(l => l.Status, LinkStatus.Validating);

        // Update all matching and return which ones were updated
        await _collection.UpdateManyAsync(filter, update, cancellationToken: cancellationToken);

        // Return the IDs that are now in Validating status
        var validating = await _collection
            .Find(Builders<Link>.Filter.And(
                Builders<Link>.Filter.In(l => l.Id, idList),
                Builders<Link>.Filter.Eq(l => l.Status, LinkStatus.Validating)))
            .Project(l => l.Id!)
            .ToListAsync(cancellationToken);

        return validating;
    }

    public async Task<long> GetCountAsync(CancellationToken cancellationToken = default)
    {
        return await _collection.CountDocumentsAsync(_ => true, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Creates indexes for efficient querying.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var indexModels = new List<CreateIndexModel<Link>>
            {
                // Index for broken links query
                new(Builders<Link>.IndexKeys.Ascending(l => l.Status)),

                // Index for finding unvalidated links
                new(Builders<Link>.IndexKeys
                    .Ascending(l => l.Status)
                    .Ascending(l => l.LastValidatedAt)),

                // Index for URL lookups (deduplication)
                new(Builders<Link>.IndexKeys.Ascending(l => l.NormalizedUrl))
            };

            await _collection.Indexes.CreateManyAsync(indexModels, cancellationToken);
            _logger.LogInformation("Ensured MongoDB indexes for links collection");
        }
        catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
        {
            // Index already exists (possibly with different name) - that's fine
            _logger.LogDebug("Index already exists for links: {Message}", ex.Message);
        }
    }
}
