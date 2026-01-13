using Microsoft.Extensions.Options;
using MongoDB.Driver;
using UrlValidationService.Abstractions;
using UrlValidationService.Models;

namespace UrlValidationService.Repositories;

/// <summary>
/// MongoDB repository for validation jobs.
/// Design Decision: Uses atomic findAndModify for job claiming to prevent race conditions.
/// 
/// Interview Talking Point: The ClaimNextQueuedJobAsync method is key for horizontal scaling.
/// Multiple workers can safely compete for jobs without coordination.
/// </summary>
public class JobRepository : IJobRepository
{
    private readonly IMongoCollection<ValidationJob> _collection;
    private readonly ILogger<JobRepository> _logger;

    public JobRepository(
        IOptions<MongoSettings> settings,
        ILogger<JobRepository> logger)
    {
        var client = new MongoClient(settings.Value.ConnectionString);
        var database = client.GetDatabase(settings.Value.DatabaseName);
        _collection = database.GetCollection<ValidationJob>("validation_jobs");
        _logger = logger;
    }

    public async Task<ValidationJob> CreateAsync(ValidationJob job, CancellationToken cancellationToken = default)
    {
        await _collection.InsertOneAsync(job, cancellationToken: cancellationToken);
        _logger.LogInformation("Created job {JobId} for {LinkCount} links", job.JobId, job.TotalLinks);
        return job;
    }

    public async Task<ValidationJob?> GetByIdAsync(string jobId, CancellationToken cancellationToken = default)
    {
        return await _collection
            .Find(j => j.JobId == jobId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Atomically claims the next queued job.
    /// Uses findAndModify to prevent race conditions between multiple workers.
    /// 
    /// Interview Note: This pattern is essential for distributed job processing.
    /// Without atomic claim, two workers could pick up the same job.
    /// </summary>
    public async Task<ValidationJob?> ClaimNextQueuedJobAsync(CancellationToken cancellationToken = default)
    {
        var filter = Builders<ValidationJob>.Filter.Eq(j => j.Status, JobStatus.Queued);
        var update = Builders<ValidationJob>.Update
            .Set(j => j.Status, JobStatus.Processing)
            .Set(j => j.StartedAt, DateTime.UtcNow);
        var options = new FindOneAndUpdateOptions<ValidationJob>
        {
            ReturnDocument = ReturnDocument.After,
            Sort = Builders<ValidationJob>.Sort.Ascending(j => j.CreatedAt) // FIFO
        };

        var job = await _collection.FindOneAndUpdateAsync(
            filter, update, options, cancellationToken);

        if (job != null)
        {
            _logger.LogInformation("Claimed job {JobId} for processing", job.JobId);
        }

        return job;
    }

    public async Task UpdateProgressAsync(string jobId, int processedCount, int brokenCount, CancellationToken cancellationToken = default)
    {
        var update = Builders<ValidationJob>.Update
            .Set(j => j.ProcessedCount, processedCount)
            .Set(j => j.BrokenCount, brokenCount);

        await _collection.UpdateOneAsync(
            j => j.JobId == jobId,
            update,
            cancellationToken: cancellationToken);
    }

    public async Task CompleteAsync(string jobId, int totalProcessed, int totalBroken, CancellationToken cancellationToken = default)
    {
        var update = Builders<ValidationJob>.Update
            .Set(j => j.Status, JobStatus.Completed)
            .Set(j => j.ProcessedCount, totalProcessed)
            .Set(j => j.BrokenCount, totalBroken)
            .Set(j => j.CompletedAt, DateTime.UtcNow);

        await _collection.UpdateOneAsync(
            j => j.JobId == jobId,
            update,
            cancellationToken: cancellationToken);

        _logger.LogInformation("Job {JobId} completed. Processed: {Processed}, Broken: {Broken}", 
            jobId, totalProcessed, totalBroken);
    }

    public async Task FailAsync(string jobId, string errorMessage, CancellationToken cancellationToken = default)
    {
        var update = Builders<ValidationJob>.Update
            .Set(j => j.Status, JobStatus.Failed)
            .Set(j => j.ErrorMessage, errorMessage)
            .Set(j => j.CompletedAt, DateTime.UtcNow);

        await _collection.UpdateOneAsync(
            j => j.JobId == jobId,
            update,
            cancellationToken: cancellationToken);

        _logger.LogError("Job {JobId} failed: {Error}", jobId, errorMessage);
    }

    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var indexModels = new List<CreateIndexModel<ValidationJob>>
            {
                // Index for job claiming (status + createdAt for FIFO)
                new(Builders<ValidationJob>.IndexKeys
                    .Ascending(j => j.Status)
                    .Ascending(j => j.CreatedAt))
            };

            await _collection.Indexes.CreateManyAsync(indexModels, cancellationToken);
            _logger.LogInformation("Ensured MongoDB indexes for validation jobs");
        }
        catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
        {
            // Index already exists (possibly with different name) - that's fine
            _logger.LogDebug("Index already exists for validation jobs: {Message}", ex.Message);
        }
    }
}
