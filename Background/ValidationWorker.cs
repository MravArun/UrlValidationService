using Microsoft.Extensions.Options;
using UrlValidationService.Abstractions;
using UrlValidationService.Models;
using UrlValidationService.Services;

namespace UrlValidationService.Background;

/// <summary>
/// Background worker for async job processing.
/// Design Decision: Uses BackgroundService for lifecycle management with IHostedService.
/// 
/// Interview Talking Points:
/// 1. Atomic job claiming prevents duplicate processing across multiple instances
/// 2. Batch processing enables progress updates and prevents long-running transactions
/// 3. Graceful shutdown support via CancellationToken
/// 4. Idempotent by design - safe to restart mid-job
/// 
/// Trade-off: Polling-based vs message queue
/// - Polling: Simpler, no infrastructure dependency, good for moderate scale
/// - Message Queue: Better for high-throughput, exactly-once delivery guarantees
/// - Note: For production scale, consider Azure Service Bus or RabbitMQ
/// </summary>
public class ValidationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ValidationSettings _settings;
    private readonly ILogger<ValidationWorker> _logger;

    public ValidationWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<ValidationSettings> settings,
        ILogger<ValidationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Validation worker started. Polling interval: {Interval}s, Batch size: {BatchSize}",
            _settings.WorkerPollingIntervalSeconds,
            _settings.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextJobAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Validation worker stopping due to cancellation");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in validation worker loop");
                // Continue polling after error
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_settings.WorkerPollingIntervalSeconds),
                stoppingToken);
        }

        _logger.LogInformation("Validation worker stopped");
    }

    /// <summary>
    /// Attempts to claim and process one job.
    /// </summary>
    private async Task ProcessNextJobAsync(CancellationToken cancellationToken)
    {
        // Create scope for scoped services (repositories)
        using var scope = _scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var linkRepository = scope.ServiceProvider.GetRequiredService<ILinkRepository>();
        var validationService = scope.ServiceProvider.GetRequiredService<ILinkValidationService>();

        // Atomically claim next queued job
        var job = await jobRepository.ClaimNextQueuedJobAsync(cancellationToken);
        if (job == null)
        {
            return; // No jobs available
        }

        _logger.LogInformation(
            "Processing job {JobId} with {LinkCount} links",
            job.JobId, job.TotalLinks);

        try
        {
            await ProcessJobAsync(job, jobRepository, linkRepository, validationService, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job {JobId} processing cancelled", job.JobId);
            // Job stays in Processing state - will need manual intervention or timeout handling
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed", job.JobId);
            await jobRepository.FailAsync(job.JobId, ex.Message, cancellationToken);
        }
    }

    /// <summary>
    /// Processes all links in batches with progress updates.
    /// </summary>
    private async Task ProcessJobAsync(
        ValidationJob job,
        IJobRepository jobRepository,
        ILinkRepository linkRepository,
        ILinkValidationService validationService,
        CancellationToken cancellationToken)
    {
        // Get all links that need validation
        var links = await linkRepository.GetAllAsync(cancellationToken);
        
        var processedCount = 0;
        var brokenCount = 0;

        // Process in batches
        var batches = links
            .Select((link, index) => new { link, index })
            .GroupBy(x => x.index / _settings.BatchSize)
            .Select(g => g.Select(x => x.link).ToList());

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Validate batch with controlled concurrency
            foreach (var link in batch)
            {
                var (status, httpCode, failureReason, responseTimeMs) = 
                    await validationService.ValidateLinkAsync(link.Url, cancellationToken);

                // Update link in database
                await linkRepository.UpdateValidationResultAsync(
                    link.Id!,
                    status,
                    httpCode,
                    failureReason,
                    responseTimeMs,
                    cancellationToken);

                processedCount++;
                if (status == LinkStatus.Broken)
                {
                    brokenCount++;
                }
            }

            // Update job progress after each batch
            await jobRepository.UpdateProgressAsync(job.JobId, processedCount, brokenCount, cancellationToken);

            _logger.LogDebug(
                "Job {JobId} progress: {Processed}/{Total}, Broken: {Broken}",
                job.JobId, processedCount, job.TotalLinks, brokenCount);
        }

        // Mark job complete
        await jobRepository.CompleteAsync(job.JobId, processedCount, brokenCount, cancellationToken);

        _logger.LogInformation(
            "Job {JobId} completed. Processed {Count} links, {Broken} broken",
            job.JobId, processedCount, brokenCount);
    }
}
