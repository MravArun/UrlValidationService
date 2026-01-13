using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using UrlValidationService.Abstractions;
using UrlValidationService.Infrastructure;
using UrlValidationService.Models;
using UrlValidationService.Repositories;
using UrlValidationService.Services;
using Xunit;

namespace UrlValidationService.Tests.Services;

/// <summary>
/// Unit tests for LinkValidationService.
/// Tests the core validation logic in isolation from external dependencies.
/// </summary>
public class LinkValidationServiceTests
{
    private readonly Mock<ILinkRepository> _mockLinkRepository;
    private readonly Mock<IJobRepository> _mockJobRepository;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<IValidationCache> _mockCache;
    private readonly Mock<IRateLimiter> _mockRateLimiter;
    private readonly Mock<ILogger<LinkValidationService>> _mockLogger;
    private readonly ValidationSettings _settings;
    private readonly LinkValidationService _sut; // System Under Test

    public LinkValidationServiceTests()
    {
        _mockLinkRepository = new Mock<ILinkRepository>();
        _mockJobRepository = new Mock<IJobRepository>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockCache = new Mock<IValidationCache>();
        _mockRateLimiter = new Mock<IRateLimiter>();
        _mockLogger = new Mock<ILogger<LinkValidationService>>();
        
        _settings = new ValidationSettings
        {
            RequestTimeoutSeconds = 10,
            MaxConcurrency = 5,
            MaxRedirects = 5,
            SyncThreshold = 10
        };

        // Setup rate limiter to not block
        _mockRateLimiter
            .Setup(r => r.WaitForSlotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new LinkValidationService(
            _mockLinkRepository.Object,
            _mockJobRepository.Object,
            _mockHttpClientFactory.Object,
            _mockCache.Object,
            _mockRateLimiter.Object,
            Options.Create(_settings),
            _mockLogger.Object);
    }

    #region NormalizeUrl Tests

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("http://EXAMPLE.COM", "http://example.com/")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("https://example.com/path", "https://example.com/path")]
    [InlineData("https://example.com/path/", "https://example.com/path")]
    [InlineData("HTTPS://Example.COM/Path", "https://example.com/Path")]
    public void NormalizeUrl_ShouldNormalizeCorrectly(string input, string expected)
    {
        // Act
        var result = _sut.NormalizeUrl(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("://missing-scheme.com")]
    public void NormalizeUrl_ShouldReturnNull_ForInvalidUrls(string? input)
    {
        // Act
        var result = _sut.NormalizeUrl(input!);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void NormalizeUrl_ShouldPreserveQueryString()
    {
        // Arrange
        var url = "https://example.com/search?q=test&page=1";

        // Act
        var result = _sut.NormalizeUrl(url);

        // Assert
        result.Should().Contain("?q=test&page=1");
    }

    [Fact]
    public void NormalizeUrl_ShouldHandleNonDefaultPort()
    {
        // Arrange
        var url = "https://example.com:8443/api";

        // Act
        var result = _sut.NormalizeUrl(url);

        // Assert
        result.Should().Contain(":8443");
    }

    [Fact]
    public void NormalizeUrl_ShouldAddScheme_ForBareHostname()
    {
        // Arrange - bare hostnames get https:// prefix
        var url = "example.com";

        // Act
        var result = _sut.NormalizeUrl(url);

        // Assert
        result.Should().StartWith("https://");
    }

    #endregion

    #region ValidateLinkAsync Tests

    [Fact]
    public async Task ValidateLinkAsync_ShouldReturnCachedResult_WhenCacheHit()
    {
        // Arrange
        var url = "https://example.com";
        var cachedResult = new ValidationResult
        {
            Url = "https://example.com/",
            Status = UrlStatus.Valid,
            HttpStatus = 200
        };

        _mockCache
            .Setup(c => c.Get(It.IsAny<string>()))
            .Returns(cachedResult);

        // Act
        var (status, httpCode, failureReason, responseTimeMs) = 
            await _sut.ValidateLinkAsync(url);

        // Assert
        status.Should().Be(LinkStatus.Valid);
        _mockHttpClientFactory.Verify(
            f => f.CreateClient(It.IsAny<string>()), 
            Times.Never,
            "Should not create HTTP client when cache hit");
    }

    [Fact]
    public async Task ValidateLinkAsync_ShouldReturnValid_For200Response()
    {
        // Arrange
        var url = "https://example.com";
        SetupHttpClient(HttpStatusCode.OK);
        _mockCache.Setup(c => c.Get(It.IsAny<string>())).Returns((ValidationResult?)null);

        // Act
        var (status, httpCode, failureReason, responseTimeMs) = 
            await _sut.ValidateLinkAsync(url);

        // Assert
        status.Should().Be(LinkStatus.Valid);
        httpCode.Should().Be(200);
        failureReason.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, LinkStatus.Valid)]
    [InlineData(HttpStatusCode.Created, LinkStatus.Valid)]
    [InlineData(HttpStatusCode.Redirect, LinkStatus.Valid)]
    [InlineData(HttpStatusCode.MovedPermanently, LinkStatus.Valid)]
    [InlineData(HttpStatusCode.NotFound, LinkStatus.Broken)]
    [InlineData(HttpStatusCode.InternalServerError, LinkStatus.Broken)]
    [InlineData(HttpStatusCode.BadGateway, LinkStatus.Broken)]
    [InlineData(HttpStatusCode.Forbidden, LinkStatus.Broken)]
    public async Task ValidateLinkAsync_ShouldMapHttpStatusCorrectly(
        HttpStatusCode httpStatus, 
        LinkStatus expectedStatus)
    {
        // Arrange
        var url = "https://example.com";
        SetupHttpClient(httpStatus);
        _mockCache.Setup(c => c.Get(It.IsAny<string>())).Returns((ValidationResult?)null);

        // Act
        var (status, httpCode, failureReason, responseTimeMs) = 
            await _sut.ValidateLinkAsync(url);

        // Assert
        status.Should().Be(expectedStatus);
        httpCode.Should().Be((int)httpStatus);
    }

    [Fact]
    public async Task ValidateLinkAsync_ShouldCacheResult_AfterSuccessfulValidation()
    {
        // Arrange
        var url = "https://example.com";
        SetupHttpClient(HttpStatusCode.OK);
        _mockCache.Setup(c => c.Get(It.IsAny<string>())).Returns((ValidationResult?)null);

        // Act
        await _sut.ValidateLinkAsync(url);

        // Assert
        _mockCache.Verify(
            c => c.Set(It.IsAny<string>(), It.IsAny<ValidationResult>()),
            Times.Once,
            "Should cache the validation result");
    }

    [Fact]
    public async Task ValidateLinkAsync_ShouldCallRateLimiter()
    {
        // Arrange
        var url = "https://example.com";
        SetupHttpClient(HttpStatusCode.OK);
        _mockCache.Setup(c => c.Get(It.IsAny<string>())).Returns((ValidationResult?)null);

        // Act
        await _sut.ValidateLinkAsync(url);

        // Assert
        _mockRateLimiter.Verify(
            r => r.WaitForSlotAsync("example.com", It.IsAny<CancellationToken>()),
            Times.Once,
            "Should apply rate limiting per host");
    }

    #endregion

    #region ValidateAllLinksAsync Tests

    [Fact]
    public async Task ValidateAllLinksAsync_ShouldReturnZeroCounts_WhenNoLinks()
    {
        // Arrange
        _mockLinkRepository
            .Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        var result = await _sut.ValidateAllLinksAsync();

        // Assert
        result.IsComplete.Should().BeTrue();
        result.TotalLinks.Should().Be(0);
        result.ValidatedCount.Should().Be(0);
        result.BrokenCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidateAllLinksAsync_ShouldValidateAllLinks_Synchronously()
    {
        // Arrange - 3 links is under sync threshold (10)
        var links = new List<Link>
        {
            new() { Id = "1", Url = "https://example1.com" },
            new() { Id = "2", Url = "https://example2.com" },
            new() { Id = "3", Url = "https://example3.com" }
        };

        _mockLinkRepository
            .Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        _mockLinkRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(links);

        SetupHttpClient(HttpStatusCode.OK);
        _mockCache.Setup(c => c.Get(It.IsAny<string>())).Returns((ValidationResult?)null);

        // Act
        var result = await _sut.ValidateAllLinksAsync();

        // Assert
        result.IsComplete.Should().BeTrue();
        result.TotalLinks.Should().Be(3);
        result.ValidatedCount.Should().Be(3);
    }

    [Fact]
    public async Task ValidateAllLinksAsync_ShouldCreateAsyncJob_WhenOverThreshold()
    {
        // Arrange - 100 links is over sync threshold (10)
        _mockLinkRepository
            .Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100);

        _mockJobRepository
            .Setup(r => r.CreateAsync(It.IsAny<ValidationJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ValidationJob job, CancellationToken _) => job);

        // Act
        var result = await _sut.ValidateAllLinksAsync();

        // Assert
        result.IsComplete.Should().BeFalse();
        result.JobId.Should().NotBeNullOrEmpty();
        result.TotalLinks.Should().Be(100);
        result.Message.Should().Contain("asynchronously");
    }

    [Fact]
    public async Task ValidateAllLinksAsync_ShouldUpdateEachLinkInRepository()
    {
        // Arrange
        var links = new List<Link>
        {
            new() { Id = "1", Url = "https://example.com" }
        };

        _mockLinkRepository
            .Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockLinkRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(links);

        SetupHttpClient(HttpStatusCode.NotFound);
        _mockCache.Setup(c => c.Get(It.IsAny<string>())).Returns((ValidationResult?)null);

        // Act
        await _sut.ValidateAllLinksAsync();

        // Assert
        _mockLinkRepository.Verify(
            r => r.UpdateValidationResultAsync(
                "1",
                LinkStatus.Broken,
                404,
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ValidateAllLinksAsync_ShouldCountBrokenLinksCorrectly()
    {
        // Arrange
        var links = new List<Link>
        {
            new() { Id = "1", Url = "https://valid.com" },
            new() { Id = "2", Url = "https://broken1.com" },
            new() { Id = "3", Url = "https://broken2.com" }
        };

        _mockLinkRepository
            .Setup(r => r.GetCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        _mockLinkRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(links);

        // Setup different responses per URL
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.Host.Contains("valid")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.Host.Contains("broken")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

        var httpClient = new HttpClient(mockHandler.Object);
        _mockHttpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        _mockCache.Setup(c => c.Get(It.IsAny<string>())).Returns((ValidationResult?)null);

        // Act
        var result = await _sut.ValidateAllLinksAsync();

        // Assert
        result.ValidatedCount.Should().Be(3);
        result.BrokenCount.Should().Be(2);
    }

    #endregion

    #region Helper Methods

    private void SetupHttpClient(HttpStatusCode statusCode)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode));

        var httpClient = new HttpClient(mockHandler.Object);
        _mockHttpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);
    }

    #endregion
}
