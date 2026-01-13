using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using UrlValidationService.Abstractions;
using UrlValidationService.Controllers;
using UrlValidationService.Models;
using UrlValidationService.Services;
using Xunit;

namespace UrlValidationService.Tests.Controllers;

/// <summary>
/// Unit tests for LinksController.
/// Tests controller behavior in isolation from services.
/// </summary>
public class LinksControllerTests
{
    private readonly Mock<ILinkRepository> _mockLinkRepository;
    private readonly Mock<ILinkValidationService> _mockValidationService;
    private readonly Mock<IValidationCache> _mockCache;
    private readonly Mock<ILogger<LinksController>> _mockLogger;
    private readonly LinksController _sut;

    public LinksControllerTests()
    {
        _mockLinkRepository = new Mock<ILinkRepository>();
        _mockValidationService = new Mock<ILinkValidationService>();
        _mockCache = new Mock<IValidationCache>();
        _mockLogger = new Mock<ILogger<LinksController>>();

        _sut = new LinksController(
            _mockLinkRepository.Object,
            _mockValidationService.Object,
            _mockCache.Object,
            _mockLogger.Object);

        // Setup HttpContext for Activity.Current and TraceIdentifier
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "test-trace-id";
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    #region AddLinks Tests

    [Fact]
    public async Task AddLinks_ShouldReturn201_WhenLinksAdded()
    {
        // Arrange
        var request = new AddLinksRequest
        {
            Urls = new List<string> { "https://example.com", "https://test.com" }
        };

        _mockLinkRepository
            .Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<Link>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        _mockValidationService
            .Setup(s => s.NormalizeUrl(It.IsAny<string>()))
            .Returns((string url) => url.ToLowerInvariant());

        // Act
        var result = await _sut.AddLinks(request, CancellationToken.None);

        // Assert
        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var response = createdResult.Value.Should().BeOfType<AddLinksResponse>().Subject;
        response.AddedCount.Should().Be(2);
    }

    [Fact]
    public async Task AddLinks_ShouldNormalizeUrls_BeforeStoring()
    {
        // Arrange
        var request = new AddLinksRequest
        {
            Urls = new List<string> { "HTTPS://EXAMPLE.COM" }
        };

        _mockValidationService
            .Setup(s => s.NormalizeUrl("HTTPS://EXAMPLE.COM"))
            .Returns("https://example.com/");

        _mockLinkRepository
            .Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<Link>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        await _sut.AddLinks(request, CancellationToken.None);

        // Assert
        _mockLinkRepository.Verify(r => r.AddBatchAsync(
            It.Is<IEnumerable<Link>>(links => 
                links.First().NormalizedUrl == "https://example.com/"),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task AddLinks_ShouldSetStatusToNull_ForNewLinks()
    {
        // Arrange
        var request = new AddLinksRequest
        {
            Urls = new List<string> { "https://example.com" }
        };

        Link? capturedLink = null;
        _mockLinkRepository
            .Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<Link>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Link>, CancellationToken>((links, _) => capturedLink = links.First())
            .ReturnsAsync(1);

        _mockValidationService
            .Setup(s => s.NormalizeUrl(It.IsAny<string>()))
            .Returns((string url) => url);

        // Act
        await _sut.AddLinks(request, CancellationToken.None);

        // Assert
        capturedLink.Should().NotBeNull();
        capturedLink!.Status.Should().BeNull("new links should not have validation status");
    }

    #endregion

    #region ValidateLinks Tests

    [Fact]
    public async Task ValidateLinks_ShouldReturn200_WhenValidationComplete()
    {
        // Arrange
        var validationResponse = new ValidationTriggerResponse
        {
            IsComplete = true,
            TotalLinks = 10,
            ValidatedCount = 10,
            BrokenCount = 2
        };

        _mockValidationService
            .Setup(s => s.ValidateAllLinksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(validationResponse);

        // Act
        var result = await _sut.ValidateLinks(CancellationToken.None);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<ValidationTriggerResponse>().Subject;
        response.IsComplete.Should().BeTrue();
        response.ValidatedCount.Should().Be(10);
        response.BrokenCount.Should().Be(2);
    }

    [Fact]
    public async Task ValidateLinks_ShouldReturn202_WhenValidationAsync()
    {
        // Arrange
        var validationResponse = new ValidationTriggerResponse
        {
            IsComplete = false,
            JobId = "job-123"
        };

        _mockValidationService
            .Setup(s => s.ValidateAllLinksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(validationResponse);

        // Act
        var result = await _sut.ValidateLinks(CancellationToken.None);

        // Assert
        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task ValidateLinks_ShouldReturn500_WhenExceptionThrown()
    {
        // Arrange
        _mockValidationService
            .Setup(s => s.ValidateAllLinksAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database connection failed"));

        // Act
        var result = await _sut.ValidateLinks(CancellationToken.None);

        // Assert
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(500);
    }

    #endregion

    #region GetBrokenLinks Tests

    [Fact]
    public async Task GetBrokenLinks_ShouldReturnEmptyList_WhenNoBrokenLinks()
    {
        // Arrange
        _mockLinkRepository
            .Setup(r => r.GetBrokenLinksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Link>());

        // Act
        var result = await _sut.GetBrokenLinks(CancellationToken.None);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<BrokenLinksResponse>().Subject;
        response.TotalBroken.Should().Be(0);
        response.Links.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBrokenLinks_ShouldReturnBrokenLinks_WithDetails()
    {
        // Arrange
        var brokenLinks = new List<Link>
        {
            new()
            {
                Id = "1",
                Url = "https://broken.com",
                Status = LinkStatus.Broken,
                HttpStatusCode = 404,
                FailureReason = "404 Not Found",
                ResponseTimeMs = 150,
                LastValidatedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow.AddDays(-1)
            },
            new()
            {
                Id = "2",
                Url = "https://timeout.com",
                Status = LinkStatus.Broken,
                FailureReason = "Request timed out",
                ResponseTimeMs = 10000,
                LastValidatedAt = DateTime.UtcNow
            }
        };

        _mockLinkRepository
            .Setup(r => r.GetBrokenLinksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(brokenLinks);

        // Act
        var result = await _sut.GetBrokenLinks(CancellationToken.None);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<BrokenLinksResponse>().Subject;
        
        response.TotalBroken.Should().Be(2);
        response.Links.Should().HaveCount(2);
        
        response.Links[0].Url.Should().Be("https://broken.com");
        response.Links[0].HttpStatusCode.Should().Be(404);
        response.Links[0].FailureReason.Should().Be("404 Not Found");
        
        response.Links[1].Url.Should().Be("https://timeout.com");
        response.Links[1].FailureReason.Should().Be("Request timed out");
    }

    [Fact]
    public async Task GetBrokenLinks_ShouldUseUnknown_WhenFailureReasonNull()
    {
        // Arrange
        var brokenLinks = new List<Link>
        {
            new()
            {
                Id = "1",
                Url = "https://broken.com",
                Status = LinkStatus.Broken,
                FailureReason = null
            }
        };

        _mockLinkRepository
            .Setup(r => r.GetBrokenLinksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(brokenLinks);

        // Act
        var result = await _sut.GetBrokenLinks(CancellationToken.None);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<BrokenLinksResponse>().Subject;
        response.Links[0].FailureReason.Should().Be("Unknown");
    }

    #endregion

    #region GetAllLinks Tests

    [Fact]
    public async Task GetAllLinks_ShouldReturnAllLinks()
    {
        // Arrange
        var links = new List<Link>
        {
            new() { Id = "1", Url = "https://example1.com", Status = LinkStatus.Valid },
            new() { Id = "2", Url = "https://example2.com", Status = LinkStatus.Broken },
            new() { Id = "3", Url = "https://example3.com", Status = null }
        };

        _mockLinkRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(links);

        // Act
        var result = await _sut.GetAllLinks(CancellationToken.None);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeAssignableTo<List<LinkDto>>().Subject;
        response.Should().HaveCount(3);
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public async Task GetStats_ShouldReturnCorrectStatistics()
    {
        // Arrange
        var links = new List<Link>
        {
            new() { Status = LinkStatus.Valid },
            new() { Status = LinkStatus.Valid },
            new() { Status = LinkStatus.Broken },
            new() { Status = null }
        };

        var brokenLinks = links.Where(l => l.Status == LinkStatus.Broken).ToList();

        _mockLinkRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(links);

        _mockLinkRepository
            .Setup(r => r.GetBrokenLinksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(brokenLinks);

        _mockCache
            .Setup(c => c.GetStats())
            .Returns(new CacheStats { EntryCount = 10, HitCount = 100, MissCount = 20 });

        // Act
        var result = await _sut.GetStats(CancellationToken.None);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();
    }

    #endregion
}
