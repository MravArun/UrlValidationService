using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using UrlValidationService.Abstractions;
using UrlValidationService.Infrastructure;
using UrlValidationService.Models;

namespace UrlValidationService.Services;

/// <summary>
/// Service for validating stored links with hybrid sync/async support.
/// Design Decision: 
/// - Small batches (≤ threshold): Synchronous for immediate response
/// - Large batches (> threshold): Creates async job for background processing
/// 
/// Interview Talking Points:
/// 1. Hybrid approach optimizes for common case (small batches) while scaling for heavy workloads
/// 2. Same validation logic used in both sync and async paths
/// 3. Caching prevents redundant HTTP calls across both paths
/// </summary>
public interface ILinkValidationService
{
    /// <summary>
    /// Validates all stored links. Uses sync or async based on count threshold.
    /// </summary>
    Task<ValidationTriggerResponse> ValidateAllLinksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a single link and returns the result.
    /// </summary>
    Task<(LinkStatus Status, int? HttpCode, string? FailureReason, long ResponseTimeMs)> ValidateLinkAsync(
        string url,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets job status for async validation.
    /// </summary>
    Task<ValidationTriggerResponse> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Normalizes URL for consistent comparison.
    /// </summary>
    string? NormalizeUrl(string url);
}

public class LinkValidationService : ILinkValidationService
{
    private readonly ILinkRepository _linkRepository;
    private readonly IJobRepository _jobRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IValidationCache _cache;
    private readonly IRateLimiter _rateLimiter;
    private readonly ValidationSettings _settings;
    private readonly ILogger<LinkValidationService> _logger;

    public LinkValidationService(
        ILinkRepository linkRepository,
        IJobRepository jobRepository,
        IHttpClientFactory httpClientFactory,
        IValidationCache cache,
        IRateLimiter rateLimiter,
        IOptions<ValidationSettings> settings,
        ILogger<LinkValidationService> logger)
    {
        _linkRepository = linkRepository;
        _jobRepository = jobRepository;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _rateLimiter = rateLimiter;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Validates all stored links using hybrid sync/async approach.
    /// Interview Note: This is the key decision point for scalability.
    /// </summary>
    public async Task<ValidationTriggerResponse> ValidateAllLinksAsync(CancellationToken cancellationToken = default)
    {
        var linkCount = await _linkRepository.GetCountAsync(cancellationToken);
        
        if (linkCount == 0)
        {
            return new ValidationTriggerResponse
            {
                IsComplete = true,
                TotalLinks = 0,
                ValidatedCount = 0,
                BrokenCount = 0,
                Message = "No links to validate"
            };
        }

        _logger.LogInformation(
            "Validation requested for {Count} links (threshold: {Threshold})",
            linkCount, _settings.SyncThreshold);

        // Decision point: sync vs async
        if (linkCount <= _settings.SyncThreshold)
        {
            return await ValidateSynchronouslyAsync(cancellationToken);
        }
        else
        {
            return await CreateAsyncJobAsync((int)linkCount, cancellationToken);
        }
    }

    /// <summary>
    /// Synchronous validation for small batches.
    /// User waits for immediate results.
    /// </summary>
    private async Task<ValidationTriggerResponse> ValidateSynchronouslyAsync(CancellationToken cancellationToken)
    {
        var links = await _linkRepository.GetAllAsync(cancellationToken);
        
        _logger.LogInformation("Processing {Count} links synchronously", links.Count);

        var processedCount = 0;
        var brokenCount = 0;

        // Process with controlled concurrency
        using var semaphore = new SemaphoreSlim(_settings.MaxConcurrency);
        
        var tasks = links.Select(async link =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var (status, httpCode, failureReason, responseTimeMs) = 
                    await ValidateLinkAsync(link.Url, cancellationToken);

                // Update link in database
                await _linkRepository.UpdateValidationResultAsync(
                    link.Id!,
                    status,
                    httpCode,
                    failureReason,
                    responseTimeMs,
                    cancellationToken);

                Interlocked.Increment(ref processedCount);
                if (status == LinkStatus.Broken)
                {
                    Interlocked.Increment(ref brokenCount);
                }

                return status;
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation(
            "Sync validation complete. Total: {Total}, Broken: {Broken}",
            processedCount, brokenCount);

        return new ValidationTriggerResponse
        {
            IsComplete = true,
            TotalLinks = links.Count,
            ValidatedCount = processedCount,
            BrokenCount = brokenCount,
            Message = $"Validated {processedCount} links synchronously. Found {brokenCount} broken."
        };
    }

    /// <summary>
    /// Creates async job for large batches.
    /// Returns immediately with job ID for polling.
    /// </summary>
    private async Task<ValidationTriggerResponse> CreateAsyncJobAsync(int linkCount, CancellationToken cancellationToken)
    {
        var job = new ValidationJob
        {
            TotalLinks = linkCount,
            Status = JobStatus.Queued
        };

        await _jobRepository.CreateAsync(job, cancellationToken);

        _logger.LogInformation(
            "Created async job {JobId} for {LinkCount} links",
            job.JobId, linkCount);

        return new ValidationTriggerResponse
        {
            IsComplete = false,
            JobId = job.JobId,
            TotalLinks = linkCount,
            ValidatedCount = 0,
            BrokenCount = 0,
            Message = $"Processing {linkCount} links asynchronously. Poll GET /api/links/jobs/{job.JobId} for status."
        };
    }

    /// <summary>
    /// Gets async job status for polling.
    /// </summary>
    public async Task<ValidationTriggerResponse> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = await _jobRepository.GetByIdAsync(jobId, cancellationToken);
        
        if (job == null)
        {
            return new ValidationTriggerResponse
            {
                IsComplete = false,
                Message = "Job not found"
            };
        }

        var response = new ValidationTriggerResponse
        {
            IsComplete = job.Status == JobStatus.Completed,
            JobId = job.JobId,
            TotalLinks = job.TotalLinks,
            ValidatedCount = job.ProcessedCount,
            BrokenCount = job.BrokenCount
        };

        response.Message = job.Status switch
        {
            JobStatus.Queued => "Job queued, waiting for worker",
            JobStatus.Processing => $"Processing: {job.ProcessedCount}/{job.TotalLinks} ({(job.TotalLinks > 0 ? job.ProcessedCount * 100 / job.TotalLinks : 0)}%)",
            JobStatus.Completed => $"Completed. Validated {job.ProcessedCount} links, found {job.BrokenCount} broken.",
            JobStatus.Failed => $"Failed: {job.ErrorMessage}",
            _ => "Unknown status"
        };

        return response;
    }

    /// <summary>
    /// Validates a single URL and returns the result.
    /// </summary>
    public async Task<(LinkStatus Status, int? HttpCode, string? FailureReason, long ResponseTimeMs)> ValidateLinkAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var normalizedUrl = NormalizeUrl(url);
        if (normalizedUrl == null)
        {
            return (LinkStatus.Broken, null, "Invalid URL format", 0);
        }

        // Check cache first
        var cached = _cache.Get(normalizedUrl);
        if (cached != null)
        {
            _logger.LogDebug("Cache hit for {Url}", normalizedUrl);
            var cachedStatus = cached.Status == UrlStatus.Valid ? LinkStatus.Valid : LinkStatus.Broken;
            return (cachedStatus, cached.HttpStatus, cached.ErrorReason, 0);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var uri = new Uri(normalizedUrl);
            
            // Rate limiting per host
            await _rateLimiter.WaitForSlotAsync(uri.Host, cancellationToken);

            var client = _httpClientFactory.CreateClient(HttpClientConfiguration.ValidationClientName);

            // Use HEAD request first (less bandwidth)
            using var request = new HttpRequestMessage(HttpMethod.Head, normalizedUrl);
            using var response = await client.SendAsync(request, cancellationToken);

            stopwatch.Stop();
            var responseTimeMs = stopwatch.ElapsedMilliseconds;

            var statusCode = (int)response.StatusCode;
            var isValid = statusCode >= 200 && statusCode < 400;

            // Cache the result
            CacheResult(normalizedUrl, isValid, statusCode, null, responseTimeMs);

            if (isValid)
            {
                return (LinkStatus.Valid, statusCode, null, responseTimeMs);
            }
            else
            {
                var reason = $"{statusCode} {response.ReasonPhrase}";
                return (LinkStatus.Broken, statusCode, reason, responseTimeMs);
            }
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
        {
            var reason = $"Connection failed: {ex.InnerException.Message}";
            CacheResult(normalizedUrl, false, null, reason, stopwatch.ElapsedMilliseconds);
            return (LinkStatus.Broken, null, reason, stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("DNS", StringComparison.OrdinalIgnoreCase) ||
                                               ex.Message.Contains("host", StringComparison.OrdinalIgnoreCase))
        {
            const string reason = "DNS resolution failed";
            CacheResult(normalizedUrl, false, null, reason, stopwatch.ElapsedMilliseconds);
            return (LinkStatus.Broken, null, reason, stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase))
        {
            const string reason = "Too many redirects";
            CacheResult(normalizedUrl, false, null, reason, stopwatch.ElapsedMilliseconds);
            return (LinkStatus.Broken, null, reason, stopwatch.ElapsedMilliseconds);
        }
        catch (TimeoutRejectedException)
        {
            var reason = $"Request timed out after {_settings.RequestTimeoutSeconds}s";
            // Don't cache timeouts - might be transient
            return (LinkStatus.Broken, null, reason, stopwatch.ElapsedMilliseconds);
        }
        catch (BrokenCircuitException)
        {
            const string reason = "Circuit breaker open - too many recent failures";
            return (LinkStatus.Broken, null, reason, stopwatch.ElapsedMilliseconds);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            const string reason = "Request timed out";
            return (LinkStatus.Broken, null, reason, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unexpected error validating {Url}", normalizedUrl);
            var reason = $"Validation failed: {ex.Message}";
            return (LinkStatus.Broken, null, reason, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Normalizes URL for consistent caching and comparison.
    /// </summary>
    public string? NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            // Add scheme if missing
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            var uri = new Uri(url);
            
            var normalized = new UriBuilder
            {
                Scheme = uri.Scheme.ToLowerInvariant(),
                Host = uri.Host.ToLowerInvariant(),
                Port = uri.IsDefaultPort ? -1 : uri.Port,
                Path = uri.AbsolutePath,
                Query = uri.Query
            };

            var result = normalized.Uri.ToString().TrimEnd('/');
            if (string.IsNullOrEmpty(new Uri(result).AbsolutePath) || 
                new Uri(result).AbsolutePath == "/")
            {
                if (!result.EndsWith("/"))
                    result += "/";
            }

            return result;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private void CacheResult(string normalizedUrl, bool isValid, int? httpStatus, string? errorReason, long responseTimeMs)
    {
        var result = new ValidationResult
        {
            Url = normalizedUrl,
            OriginalUrl = normalizedUrl,
            Status = isValid ? UrlStatus.Valid : UrlStatus.Invalid,
            HttpStatus = httpStatus,
            ErrorReason = errorReason,
            ResponseTimeMs = responseTimeMs
        };
        _cache.Set(normalizedUrl, result);
    }
}
