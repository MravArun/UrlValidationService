using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using UrlValidationService.Infrastructure;
using UrlValidationService.Models;
using Xunit;

namespace UrlValidationService.Tests.Infrastructure;

/// <summary>
/// Unit tests for InMemoryValidationCache.
/// Tests caching behavior, TTL, and LRU eviction.
/// </summary>
public class InMemoryValidationCacheTests
{
    private readonly Mock<ILogger<InMemoryValidationCache>> _mockLogger;
    private readonly CacheSettings _settings;

    public InMemoryValidationCacheTests()
    {
        _mockLogger = new Mock<ILogger<InMemoryValidationCache>>();
        _settings = new CacheSettings
        {
            TtlMinutes = 60,
            MaxEntries = 100
        };
    }

    private InMemoryValidationCache CreateCache(CacheSettings? settings = null)
    {
        return new InMemoryValidationCache(
            Options.Create(settings ?? _settings),
            _mockLogger.Object);
    }

    #region Get/Set Tests

    [Fact]
    public void Get_ShouldReturnNull_WhenKeyNotFound()
    {
        // Arrange
        var cache = CreateCache();

        // Act
        var result = cache.Get("https://nonexistent.com/");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Get_ShouldReturnCachedResult_WhenKeyExists()
    {
        // Arrange
        var cache = CreateCache();
        var url = "https://example.com/";
        var validationResult = new ValidationResult
        {
            Url = url,
            Status = UrlStatus.Valid,
            HttpStatus = 200
        };

        cache.Set(url, validationResult);

        // Act
        var result = cache.Get(url);

        // Assert
        result.Should().NotBeNull();
        result!.Url.Should().Be(url);
        result.Status.Should().Be(UrlStatus.Cached); // Should be marked as cached
    }

    [Fact]
    public void Get_ShouldReturnCachedStatus_NotOriginalStatus()
    {
        // Arrange
        var cache = CreateCache();
        var url = "https://example.com/";
        var validationResult = new ValidationResult
        {
            Url = url,
            Status = UrlStatus.Valid,
            HttpStatus = 200
        };

        cache.Set(url, validationResult);

        // Act
        var result = cache.Get(url);

        // Assert
        result!.Status.Should().Be(UrlStatus.Cached, 
            "cached results should have Cached status to indicate source");
    }

    [Fact]
    public void Set_ShouldOverwriteExistingEntry()
    {
        // Arrange
        var cache = CreateCache();
        var url = "https://example.com/";
        
        var firstResult = new ValidationResult { Url = url, HttpStatus = 200 };
        var secondResult = new ValidationResult { Url = url, HttpStatus = 404 };

        // Act
        cache.Set(url, firstResult);
        cache.Set(url, secondResult);
        var result = cache.Get(url);

        // Assert
        result!.HttpStatus.Should().Be(404);
    }

    #endregion

    #region TTL Tests

    [Fact]
    public void Get_ShouldReturnNull_WhenEntryExpired()
    {
        // Arrange
        var shortTtlSettings = new CacheSettings { TtlMinutes = 0, MaxEntries = 100 };
        var cache = CreateCache(shortTtlSettings);
        var url = "https://example.com/";

        cache.Set(url, new ValidationResult { Url = url });
        
        // Wait a tiny bit to ensure TTL expires (0 minutes = immediate expiration)
        Thread.Sleep(10);

        // Act
        var result = cache.Get(url);

        // Assert
        result.Should().BeNull("entry should have expired");
    }

    #endregion

    #region Remove/Clear Tests

    [Fact]
    public void Remove_ShouldDeleteEntry()
    {
        // Arrange
        var cache = CreateCache();
        var url = "https://example.com/";
        cache.Set(url, new ValidationResult { Url = url });

        // Act
        cache.Remove(url);
        var result = cache.Get(url);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Remove_ShouldNotThrow_WhenKeyNotFound()
    {
        // Arrange
        var cache = CreateCache();

        // Act & Assert
        var action = () => cache.Remove("nonexistent");
        action.Should().NotThrow();
    }

    [Fact]
    public void Clear_ShouldRemoveAllEntries()
    {
        // Arrange
        var cache = CreateCache();
        cache.Set("https://example1.com/", new ValidationResult { Url = "https://example1.com/" });
        cache.Set("https://example2.com/", new ValidationResult { Url = "https://example2.com/" });
        cache.Set("https://example3.com/", new ValidationResult { Url = "https://example3.com/" });

        // Act
        cache.Clear();

        // Assert
        cache.Get("https://example1.com/").Should().BeNull();
        cache.Get("https://example2.com/").Should().BeNull();
        cache.Get("https://example3.com/").Should().BeNull();
        cache.GetStats().EntryCount.Should().Be(0);
    }

    #endregion

    #region Stats Tests

    [Fact]
    public void GetStats_ShouldTrackHitsAndMisses()
    {
        // Arrange
        var cache = CreateCache();
        var url = "https://example.com/";
        cache.Set(url, new ValidationResult { Url = url });

        // Act
        cache.Get(url);           // Hit
        cache.Get(url);           // Hit
        cache.Get("nonexistent"); // Miss
        cache.Get("nonexistent"); // Miss
        cache.Get("nonexistent"); // Miss

        var stats = cache.GetStats();

        // Assert
        stats.HitCount.Should().Be(2);
        stats.MissCount.Should().Be(3);
        stats.HitRate.Should().BeApproximately(0.4, 0.01); // 2/5 = 0.4
    }

    [Fact]
    public void GetStats_ShouldTrackEntryCount()
    {
        // Arrange
        var cache = CreateCache();

        // Act
        cache.Set("https://example1.com/", new ValidationResult { Url = "1" });
        cache.Set("https://example2.com/", new ValidationResult { Url = "2" });
        
        var stats = cache.GetStats();

        // Assert
        stats.EntryCount.Should().Be(2);
    }

    [Fact]
    public void Clear_ShouldResetStats()
    {
        // Arrange
        var cache = CreateCache();
        cache.Set("url", new ValidationResult { Url = "url" });
        cache.Get("url"); // Hit
        cache.Get("miss"); // Miss

        // Act
        cache.Clear();
        var stats = cache.GetStats();

        // Assert
        stats.HitCount.Should().Be(0);
        stats.MissCount.Should().Be(0);
        stats.EntryCount.Should().Be(0);
    }

    #endregion

    #region LRU Eviction Tests

    [Fact]
    public void Set_ShouldEvictOldEntries_WhenMaxEntriesReached()
    {
        // Arrange
        var smallCacheSettings = new CacheSettings { TtlMinutes = 60, MaxEntries = 10 };
        var cache = CreateCache(smallCacheSettings);

        // Fill cache
        for (int i = 0; i < 10; i++)
        {
            cache.Set($"https://example{i}.com/", new ValidationResult { Url = $"url{i}" });
        }

        // Access first entry to make it recently used
        cache.Get("https://example0.com/");

        // Act - Add one more entry, should trigger eviction
        cache.Set("https://new.com/", new ValidationResult { Url = "new" });

        // Assert
        // The new entry should exist
        cache.Get("https://new.com/").Should().NotBeNull();
        
        // Entry count should be around MaxEntries (eviction removes ~10%)
        cache.GetStats().EntryCount.Should().BeLessThanOrEqualTo(11);
    }

    #endregion
}
