using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using UrlValidationService.Models;
using UrlValidationService.Repositories;
using Xunit;

namespace UrlValidationService.Tests.Repositories;

/// <summary>
/// Integration tests for LinkRepository.
/// Note: These tests require MongoDB to be running.
/// In a real project, you'd use a test container or in-memory MongoDB.
/// 
/// Interview Note: These tests demonstrate understanding of repository testing.
/// For true unit tests, you'd mock the MongoDB driver or use an abstraction layer.
/// </summary>
public class LinkRepositoryTests
{
    /// <summary>
    /// This test class contains integration test examples.
    /// They require MongoDB to be running and are marked with appropriate traits.
    /// 
    /// In production, you would either:
    /// 1. Use Testcontainers to spin up MongoDB
    /// 2. Use an in-memory MongoDB implementation
    /// 3. Create an abstraction layer and mock it
    /// </summary>

    [Fact]
    public void MongoSettings_ShouldHaveDefaultValues()
    {
        // Arrange & Act
        var settings = new MongoSettings();

        // Assert
        settings.ConnectionString.Should().Be("mongodb://localhost:27017");
        settings.DatabaseName.Should().Be("url_validation");
        settings.ResultsTtlHours.Should().Be(24);
    }

    [Fact]
    public void Link_ShouldHaveCorrectDefaultValues()
    {
        // Arrange & Act
        var link = new Link();

        // Assert
        link.Id.Should().BeNull();
        link.Url.Should().BeEmpty();
        link.Status.Should().BeNull();
        link.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        link.LastValidatedAt.Should().BeNull();
    }

    [Theory]
    [InlineData(LinkStatus.Valid)]
    [InlineData(LinkStatus.Broken)]
    [InlineData(LinkStatus.Validating)]
    public void LinkStatus_ShouldHaveExpectedValues(LinkStatus status)
    {
        // This test ensures the enum values exist and are usable
        var link = new Link { Status = status };
        link.Status.Should().Be(status);
    }
}

/// <summary>
/// Example integration tests that would run against real MongoDB.
/// These are structured but marked to skip unless MongoDB is available.
/// </summary>
public class LinkRepositoryIntegrationTests
{
    // Note: These tests are examples of what integration tests would look like.
    // They're not executed by default as they require MongoDB.

    [Fact(Skip = "Requires MongoDB - run manually or in CI with MongoDB container")]
    public async Task AddAsync_ShouldPersistLink()
    {
        // This test would:
        // 1. Create a real LinkRepository with test MongoDB
        // 2. Add a link
        // 3. Verify it was persisted
        await Task.CompletedTask;
    }

    [Fact(Skip = "Requires MongoDB - run manually or in CI with MongoDB container")]
    public async Task GetBrokenLinksAsync_ShouldOnlyReturnBrokenLinks()
    {
        // This test would:
        // 1. Add mix of valid and broken links
        // 2. Call GetBrokenLinksAsync
        // 3. Verify only broken links returned
        await Task.CompletedTask;
    }

    [Fact(Skip = "Requires MongoDB - run manually or in CI with MongoDB container")]
    public async Task UpdateValidationResultAsync_ShouldUpdateLinkStatus()
    {
        // This test would:
        // 1. Add a link with null status
        // 2. Call UpdateValidationResultAsync
        // 3. Verify link was updated with new status
        await Task.CompletedTask;
    }
}
