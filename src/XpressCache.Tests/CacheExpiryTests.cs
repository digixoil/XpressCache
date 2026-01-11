using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace XpressCache.Tests;

/// <summary>
/// Unit tests for <see cref="CacheStore"/> expiration and renewal behavior.
/// </summary>
public class CacheExpiryTests
{
    private readonly ILogger<CacheStore> _logger;

    public CacheExpiryTests()
    {
        _logger = new NullLogger<CacheStore>();
    }

    private CacheStore CreateCacheWithTtl(long ttlMs)
    {
        var options = new CacheStoreOptions
        {
            DefaultTtlMs = ttlMs,
            ProbabilisticCleanupThreshold = 10000 // Disable probabilistic cleanup for tests
        };
        return new CacheStore(_logger, Options.Create(options));
    }

    [Fact]
    public async Task LoadItem_ExpiredEntry_ExecutesRecovery()
    {
        // Arrange
        var cache = CreateCacheWithTtl(50); // 50ms TTL
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        // First call - populates cache
        await cache.LoadItem<TestEntity>(entityId, "subject", _ =>
        {
            recoveryCount++;
            return Task.FromResult(new TestEntity { Name = "First" });
        });

        // Wait for expiry
        await Task.Delay(100);

        // Act - Second call after expiry
        var result = await cache.LoadItem<TestEntity>(entityId, "subject", _ =>
        {
            recoveryCount++;
            return Task.FromResult(new TestEntity { Name = "Second" });
        });

        // Assert
        Assert.Equal(2, recoveryCount); // Recovery called again after expiry
        Assert.Equal("Second", result?.Name ?? string.Empty);
    }

    [Fact]
    public async Task LoadItem_AccessBeforeExpiry_ReturnsFromCache()
    {
        // Arrange
        var cache = CreateCacheWithTtl(500); // 500ms TTL
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        // First call - populates cache
        await cache.LoadItem<TestEntity>(entityId, "subject", _ =>
        {
            recoveryCount++;
            return Task.FromResult(new TestEntity { Name = "Cached" });
        });

        // Wait but not long enough to expire
        await Task.Delay(50);

        // Act - Second call before expiry
        var result = await cache.LoadItem<TestEntity>(entityId, "subject", _ =>
        {
            recoveryCount++;
            return Task.FromResult(new TestEntity { Name = "Should not be returned" });
        });

        // Assert
        Assert.Equal(1, recoveryCount); // Recovery called only once
        Assert.Equal("Cached", result?.Name ?? string.Empty);
    }

    [Fact]
    public async Task LoadItem_SlidingExpiration_RenewsOnAccess()
    {
        // Arrange
        var cache = CreateCacheWithTtl(150); // 150ms TTL
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        // First call - populates cache
        await cache.LoadItem<TestEntity>(entityId, "subject", _ =>
        {
            recoveryCount++;
            return Task.FromResult(new TestEntity { Name = "Cached" });
        });

        // Wait 100ms (less than TTL), then access to renew
        await Task.Delay(100);
        await cache.LoadItem<TestEntity>(entityId, "subject", _ =>
        {
            recoveryCount++;
            return Task.FromResult(new TestEntity { Name = "Not used" });
        });

        // Wait another 100ms (would have expired without renewal)
        await Task.Delay(100);

        // Act - Third call should still hit cache due to renewal
        var result = await cache.LoadItem<TestEntity>(entityId, "subject", _ =>
        {
            recoveryCount++;
            return Task.FromResult(new TestEntity { Name = "Should not be returned" });
        });

        // Assert
        Assert.Equal(1, recoveryCount); // Only first call did recovery
        Assert.Equal("Cached", result?.Name ?? string.Empty);
    }

    [Fact]
    public async Task CleanupCache_RemovesExpiredEntries()
    {
        // Arrange
        var cache = CreateCacheWithTtl(50); // 50ms TTL
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        await cache.SetItem(id1, "subject", new TestEntity { Name = "Item1" });
        await cache.SetItem(id2, "subject", new TestEntity { Name = "Item2" });

        // Wait for expiry
        await Task.Delay(100);

        // Act
        cache.CleanupCache();

        // Now try to get items - should require recovery
        var recoveryCount = 0;
        await cache.LoadItem<TestEntity>(id1, "subject", _ => { recoveryCount++; return Task.FromResult(new TestEntity()); });
        await cache.LoadItem<TestEntity>(id2, "subject", _ => { recoveryCount++; return Task.FromResult(new TestEntity()); });

        // Assert
        Assert.Equal(2, recoveryCount); // Both items were expired and cleaned up
    }

    [Fact]
    public async Task CleanupCache_PreservesNonExpiredEntries()
    {
        // Arrange
        var cache = CreateCacheWithTtl(10000); // 10 second TTL
        var entityId = Guid.NewGuid();
        
        await cache.SetItem(entityId, "subject", new TestEntity { Name = "NotExpired" });

        // Act
        cache.CleanupCache();

        // Try to get item - should hit cache
        var recoveryCount = 0;
        var result = await cache.LoadItem<TestEntity>(entityId, "subject", _ =>
        {
            recoveryCount++;
            return Task.FromResult(new TestEntity { Name = "Recovered" });
        });

        // Assert
        Assert.Equal(0, recoveryCount); // No recovery needed
        Assert.Equal("NotExpired", result?.Name ?? string.Empty);
    }

    [Fact]
    public async Task GetCachedItems_RemovesExpiredDuringEnumeration()
    {
        // Arrange
        var cache = CreateCacheWithTtl(50); // 50ms TTL
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        await cache.SetItem(id1, "subject", new TestEntity { Name = "Item1" });
        await Task.Delay(100); // Wait for id1 to expire
        await cache.SetItem(id2, "subject", new TestEntity { Name = "Item2" }); // Fresh item

        // Act
        var items = cache.GetCachedItems<TestEntity>("subject");

        // Assert
        Assert.NotNull(items);
        Assert.Single(items); // Only non-expired item
        Assert.Equal("Item2", items[0].Name);
    }

    [Fact]
    public async Task Options_CustomTtl_UsesConfiguredValue()
    {
        // Arrange
        var cache = CreateCacheWithTtl(100);
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        // Act - First call
        await cache.LoadItem<TestEntity>(entityId, "subject", _ =>
        {
            recoveryCount++;
            return Task.FromResult(new TestEntity { Name = "Cached" });
        });

        // Wait less than TTL
        await Task.Delay(50);
        await cache.LoadItem<TestEntity>(entityId, "subject", _ =>
        {
            recoveryCount++;
            return Task.FromResult(new TestEntity());
        });

        // Wait for full expiry
        await Task.Delay(150);
        await cache.LoadItem<TestEntity>(entityId, "subject", _ =>
        {
            recoveryCount++;
            return Task.FromResult(new TestEntity());
        });

        // Assert
        Assert.Equal(2, recoveryCount); // Initial + after expiry
    }

    private class TestEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
