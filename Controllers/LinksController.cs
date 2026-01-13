using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using UrlValidationService.Abstractions;
using UrlValidationService.Models;
using UrlValidationService.Services;

namespace UrlValidationService.Controllers;

/// <summary>
/// URL Links API controller.
/// Design Decision: 
/// - POST /api/links - Store links
/// - POST /api/links/validate - Trigger validation (sync or async based on count)
/// - GET /api/links/jobs/{jobId} - Poll async job status
/// - GET /api/links/broken - Get all broken links
/// 
/// Interview Talking Points:
/// 1. Hybrid sync/async based on link count threshold
/// 2. Job-based polling for large-scale validation
/// 3. Consistent error handling
/// </summary>
[ApiController]
[Route("api/links")]
[Produces("application/json")]
public class LinksController : ControllerBase
{
    private readonly ILinkRepository _linkRepository;
    private readonly ILinkValidationService _validationService;
    private readonly IValidationCache _cache;
    private readonly ILogger<LinksController> _logger;

    public LinksController(
        ILinkRepository linkRepository,
        ILinkValidationService validationService,
        IValidationCache cache,
        ILogger<LinksController> logger)
    {
        _linkRepository = linkRepository;
        _validationService = validationService;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Stores links in MongoDB.
    /// Links are stored without validation - call /validate to check them.
    /// </summary>
    /// <param name="request">URLs to store</param>
    /// <returns>Count of added links</returns>
    /// <response code="201">Links stored successfully</response>
    /// <response code="400">Invalid request</response>
    [HttpPost]
    [ProducesResponseType(typeof(AddLinksResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddLinks(
        [FromBody] AddLinksRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Adding {Count} links. TraceId: {TraceId}",
            request.Urls.Count,
            Activity.Current?.Id ?? HttpContext.TraceIdentifier);

        try
        {
            // Create Link entities with normalized URLs
            var links = request.Urls.Select(url => new Link
            {
                Url = url,
                NormalizedUrl = _validationService.NormalizeUrl(url),
                Status = null, // Not validated yet
                CreatedAt = DateTime.UtcNow
            }).ToList();

            var addedCount = await _linkRepository.AddBatchAsync(links, cancellationToken);

            var response = new AddLinksResponse
            {
                AddedCount = addedCount,
                Message = $"Successfully stored {addedCount} links. Call POST /api/links/validate to check them."
            };

            return CreatedAtAction(nameof(GetBrokenLinks), response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding links");
            return StatusCode(500, new ErrorResponse
            {
                Error = "Failed to store links",
                Details = ex.Message,
                TraceId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
            });
        }
    }

    /// <summary>
    /// Triggers validation of all stored links.
    /// - Small batches (≤ threshold): Validates synchronously, returns immediate results
    /// - Large batches (> threshold): Creates async job, returns jobId for polling
    /// </summary>
    /// <returns>Validation results (sync) or job info (async)</returns>
    /// <response code="200">Validation completed synchronously</response>
    /// <response code="202">Async job created - poll /api/links/jobs/{jobId}</response>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(ValidationTriggerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationTriggerResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ValidateLinks(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Validation triggered. TraceId: {TraceId}",
            Activity.Current?.Id ?? HttpContext.TraceIdentifier);

        try
        {
            var result = await _validationService.ValidateAllLinksAsync(cancellationToken);

            if (result.IsComplete)
            {
                return Ok(result);
            }
            else
            {
                // Async job created - return 202 Accepted
                return Accepted(result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during validation");
            return StatusCode(500, new ErrorResponse
            {
                Error = "Validation failed",
                Details = ex.Message,
                TraceId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
            });
        }
    }

    /// <summary>
    /// Gets the status of an async validation job.
    /// Poll this endpoint to track progress of large batch validations.
    /// </summary>
    /// <param name="jobId">Job ID from validate endpoint</param>
    /// <returns>Job status and progress</returns>
    /// <response code="200">Job found</response>
    /// <response code="404">Job not found</response>
    [HttpGet("jobs/{jobId}")]
    [ProducesResponseType(typeof(ValidationTriggerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobStatus(string jobId, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Getting status for job {JobId}", jobId);

        var result = await _validationService.GetJobStatusAsync(jobId, cancellationToken);

        if (result.JobId == null && result.Message == "Job not found")
        {
            return NotFound(new ErrorResponse
            {
                Error = "Job not found",
                Details = $"No job exists with ID: {jobId}"
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Returns all links marked as broken.
    /// Includes failure reason, HTTP status code, and timestamp of last validation.
    /// </summary>
    /// <returns>List of broken links with details</returns>
    /// <response code="200">List of broken links</response>
    [HttpGet("broken")]
    [ProducesResponseType(typeof(BrokenLinksResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBrokenLinks(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching broken links");

        var brokenLinks = await _linkRepository.GetBrokenLinksAsync(cancellationToken);

        var response = new BrokenLinksResponse
        {
            TotalBroken = brokenLinks.Count,
            Links = brokenLinks.Select(link => new BrokenLinkDto
            {
                Url = link.Url,
                HttpStatusCode = link.HttpStatusCode,
                FailureReason = link.FailureReason ?? "Unknown",
                ResponseTimeMs = link.ResponseTimeMs ?? 0,
                LastValidatedAt = link.LastValidatedAt,
                CreatedAt = link.CreatedAt
            }).ToList()
        };

        return Ok(response);
    }

    /// <summary>
    /// Gets all stored links with their current status.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<LinkDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllLinks(CancellationToken cancellationToken)
    {
        var links = await _linkRepository.GetAllAsync(cancellationToken);

        var response = links.Select(link => new LinkDto
        {
            Id = link.Id ?? "",
            Url = link.Url,
            Status = link.Status?.ToString(),
            HttpStatusCode = link.HttpStatusCode,
            FailureReason = link.FailureReason,
            LastValidatedAt = link.LastValidatedAt,
            CreatedAt = link.CreatedAt
        }).ToList();

        return Ok(response);
    }

    /// <summary>
    /// Gets link count and validation statistics.
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        var allLinks = await _linkRepository.GetAllAsync(cancellationToken);
        var brokenLinks = await _linkRepository.GetBrokenLinksAsync(cancellationToken);
        var cacheStats = _cache.GetStats();

        return Ok(new
        {
            totalLinks = allLinks.Count,
            validatedLinks = allLinks.Count(l => l.Status != null),
            validLinks = allLinks.Count(l => l.Status == LinkStatus.Valid),
            brokenLinks = brokenLinks.Count,
            pendingValidation = allLinks.Count(l => l.Status == null),
            cache = cacheStats
        });
    }

    /// <summary>
    /// Health check endpoint.
    /// </summary>
    [HttpGet("/health")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var linkCount = await _linkRepository.GetCountAsync(cancellationToken);
        
        return Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            linkCount,
            cacheEntries = _cache.GetStats().EntryCount
        });
    }
}
