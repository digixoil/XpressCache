using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace XpressCache.Tests;

/// <summary>
/// Unit tests for <see cref="CacheStore"/> cache hit/miss behavior.
/// </summary>
public class CacheStoreTests
{
    private readonly ILogger<CacheStore> _logger;
    private readonly CacheStore _cache;

    public CacheStoreTests()
    {
        _logger = new NullLogger<CacheStore>();
        _cache = new CacheStore(_logger);
    }

    private static CacheStore CreateCache(ILogger<CacheStore> logger, CacheStoreOptions? options = null)
    {
        return new CacheStore(logger, Options.Create(options ?? new CacheStoreOptions()));
    }

    #region Cache Hit/Miss Tests

    [Fact]
    public async Task LoadItem_CacheMiss_ExecutesRecoveryFunction()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var expectedData = new TestEntity { Id = entityId, Name = "Test" };
        var recoveryCallCount = 0;

        // Act
        var result = await _cache.LoadItem<TestEntity>(
            entityId,
            "test-subject",
            async id =>
            {
                recoveryCallCount++;
                return expectedData;
            });

        // Assert
        Assert.Equal(1, recoveryCallCount);
        Assert.NotNull(result);
        Assert.Equal(expectedData.Id, result.Id);
        Assert.Equal(expectedData.Name, result.Name);
    }

    [Fact]
    public async Task LoadItem_CacheHit_ReturnsFromCacheWithoutRecovery()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var expectedData = new TestEntity { Id = entityId, Name = "Cached" };
        var recoveryCallCount = 0;

        // First call - populates cache
        await _cache.LoadItem<TestEntity>(
            entityId,
            "test-subject",
            async id =>
            {
                recoveryCallCount++;
                return expectedData;
            });

        // Act - Second call should hit cache
        var result = await _cache.LoadItem<TestEntity>(
            entityId,
            "test-subject",
            async id =>
            {
                recoveryCallCount++;
                return new TestEntity { Id = id, Name = "Should not be returned" };
            });

        // Assert
        Assert.Equal(1, recoveryCallCount); // Recovery was called only once
        Assert.NotNull(result);
        Assert.Equal(expectedData.Name, result.Name);
    }

    [Fact]
    public async Task LoadItem_DifferentSubjects_CachesSeparately()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var subject1Data = new TestEntity { Id = entityId, Name = "Subject1" };
        var subject2Data = new TestEntity { Id = entityId, Name = "Subject2" };

        // Act
        var result1 = await _cache.LoadItem<TestEntity>(entityId, "subject1", _ => Task.FromResult(subject1Data));
        var result2 = await _cache.LoadItem<TestEntity>(entityId, "subject2", _ => Task.FromResult(subject2Data));

        // Assert
        Assert.Equal("Subject1", result1?.Name);
        Assert.Equal("Subject2", result2?.Name);
    }

    [Fact]
    public async Task LoadItem_DifferentTypes_CachesSeparately()
    {
        // Arrange
        var entityId = Guid.NewGuid();

        // Act
        var entity = await _cache.LoadItem<TestEntity>(entityId, "subject", _ => Task.FromResult(new TestEntity { Id = entityId, Name = "Entity" }));
        var other = await _cache.LoadItem<OtherEntity>(entityId, "subject", _ => Task.FromResult(new OtherEntity { Id = entityId, Value = 42 }));

        // Assert
        Assert.Equal("Entity", entity?.Name);
        Assert.Equal(42, other?.Value);
    }

    [Fact]
    public async Task LoadItem_NullEntityId_ReturnsDefault()
    {
        // Act
        var result = await _cache.LoadItem<TestEntity>(null, "subject", _ => Task.FromResult(new TestEntity { Name = "Should not be called" }));

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadItem_EmptyGuidEntityId_ReturnsDefault()
    {
        // Act
        var result = await _cache.LoadItem<TestEntity>(Guid.Empty, "subject", _ => Task.FromResult(new TestEntity { Name = "Should not be called" }));

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadItem_NullRecoveryFunction_ReturnsDefault()
    {
        // Arrange
        var entityId = Guid.NewGuid();

        // Act
        var result = await _cache.LoadItem<TestEntity>(entityId, "subject", null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadItem_NullSubject_NormalizesToEmptyString()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var data = new TestEntity { Id = entityId, Name = "Test" };
        var recoveryCount = 0;

        // Act - First call with null subject
        await _cache.LoadItem<TestEntity>(entityId, null!, _ => { recoveryCount++; return Task.FromResult(data); });
        
        // Second call with empty string subject
        await _cache.LoadItem<TestEntity>(entityId, "", _ => { recoveryCount++; return Task.FromResult(data); });

        // Assert - Should be same cache key
        Assert.Equal(1, recoveryCount);
    }

    #endregion

    #region SetItem Tests

    [Fact]
    public async Task SetItem_StoresItemInCache()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var item = new TestEntity { Id = entityId, Name = "SetItem Test" };

        // Act
        await _cache.SetItem(entityId, "subject", item);
        var recoveryCount = 0;
        var result = await _cache.LoadItem<TestEntity>(entityId, "subject", _ => { recoveryCount++; return Task.FromResult(new TestEntity()); });

        // Assert
        Assert.Equal(0, recoveryCount); // Should hit cache
        Assert.Equal(item.Name, result?.Name);
    }

    [Fact]
    public async Task SetItem_ReplacesExistingItem()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var originalItem = new TestEntity { Id = entityId, Name = "Original" };
        var updatedItem = new TestEntity { Id = entityId, Name = "Updated" };

        // Act
        await _cache.SetItem(entityId, "subject", originalItem);
        await _cache.SetItem(entityId, "subject", updatedItem);
        var result = await _cache.LoadItem<TestEntity>(entityId, "subject", null);

        // Assert
        Assert.Equal("Updated", result?.Name);
    }

    [Fact]
    public async Task SetItem_NullEntityId_DoesNothing()
    {
        // Arrange & Act (should not throw)
        await _cache.SetItem<TestEntity>(null, "subject", new TestEntity());

        // Assert - no exception thrown
        Assert.True(true);
    }

    #endregion

    #region RemoveItem Tests

    [Fact]
    public async Task RemoveItem_RemovesCachedItem()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        await _cache.SetItem(entityId, "subject", new TestEntity { Name = "ToRemove" });

        // Act
        var removed = _cache.RemoveItem<TestEntity>(entityId, "subject");
        var recoveryCount = 0;
        var result = await _cache.LoadItem<TestEntity>(entityId, "subject", _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "Recovered" }); });

        // Assert
        Assert.True(removed);
        Assert.Equal(1, recoveryCount); // Had to call recovery
        Assert.Equal("Recovered", result?.Name);
    }

    [Fact]
    public void RemoveItem_NonExistentItem_ReturnsFalse()
    {
        // Act
        var removed = _cache.RemoveItem<TestEntity>(Guid.NewGuid(), "subject");

        // Assert
        Assert.False(removed);
    }

    #endregion

    #region Clear Tests

    [Fact]
    public async Task Clear_RemovesAllCachedItems()
    {
        // Arrange
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        await _cache.SetItem(id1, "subject", new TestEntity { Name = "Item1" });
        await _cache.SetItem(id2, "subject", new TestEntity { Name = "Item2" });

        // Act
        _cache.Clear();
        var recoveryCount = 0;
        await _cache.LoadItem<TestEntity>(id1, "subject", _ => { recoveryCount++; return Task.FromResult(new TestEntity()); });
        await _cache.LoadItem<TestEntity>(id2, "subject", _ => { recoveryCount++; return Task.FromResult(new TestEntity()); });

        // Assert
        Assert.Equal(2, recoveryCount); // Both had to call recovery
    }

    #endregion

    #region EnableCache Tests

    [Fact]
    public async Task EnableCache_WhenDisabled_AlwaysCallsRecovery()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        _cache.EnableCache = false;

        // Act
        await _cache.LoadItem<TestEntity>(entityId, "subject", _ => { recoveryCount++; return Task.FromResult(new TestEntity()); });
        await _cache.LoadItem<TestEntity>(entityId, "subject", _ => { recoveryCount++; return Task.FromResult(new TestEntity()); });

        // Assert
        Assert.Equal(2, recoveryCount); // Recovery called every time
    }

    [Fact]
    public async Task EnableCache_WhenDisabled_SetItemDoesNothing()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        _cache.EnableCache = false;

        // Act
        await _cache.SetItem(entityId, "subject", new TestEntity { Name = "Test" });
        _cache.EnableCache = true;
        
        var recoveryCount = 0;
        await _cache.LoadItem<TestEntity>(entityId, "subject", _ => { recoveryCount++; return Task.FromResult(new TestEntity()); });

        // Assert
        Assert.Equal(1, recoveryCount); // Item was not cached
    }

    [Fact]
    public void EnableCache_WhenDisabled_GetCachedItemsReturnsNull()
    {
        // Arrange
        _cache.EnableCache = false;

        // Act
        var result = _cache.GetCachedItems<TestEntity>("subject");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task EnableCache_TogglingClearsCache()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        await _cache.SetItem(entityId, "subject", new TestEntity { Name = "Cached" });

        // Act - Disable and re-enable
        _cache.EnableCache = false;
        _cache.EnableCache = true;

        var recoveryCount = 0;
        await _cache.LoadItem<TestEntity>(entityId, "subject", _ => { recoveryCount++; return Task.FromResult(new TestEntity()); });

        // Assert
        Assert.Equal(1, recoveryCount); // Cache was cleared
    }

    #endregion

    #region GetCachedItems Tests

    [Fact]
    public async Task GetCachedItems_ReturnsAllItemsOfTypeAndSubject()
    {
        // Arrange
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        
        await _cache.SetItem(id1, "subject-a", new TestEntity { Name = "Item1" });
        await _cache.SetItem(id2, "subject-a", new TestEntity { Name = "Item2" });
        await _cache.SetItem(id3, "subject-b", new TestEntity { Name = "Item3" }); // Different subject

        // Act
        var results = _cache.GetCachedItems<TestEntity>("subject-a");

        // Assert
        Assert.NotNull(results);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, x => x.Name == "Item1");
        Assert.Contains(results, x => x.Name == "Item2");
    }

    [Fact]
    public async Task GetCachedItems_ReturnsEmptyListWhenNoMatch()
    {
        // Arrange
        await _cache.SetItem(Guid.NewGuid(), "subject-a", new TestEntity { Name = "Item" });

        // Act
        var results = _cache.GetCachedItems<TestEntity>("subject-b");

        // Assert
        Assert.NotNull(results);
        Assert.Empty(results);
    }

    #endregion

    #region Test Entities

    private class TestEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private class OtherEntity
    {
        public Guid Id { get; set; }
        public int Value { get; set; }
    }

    #endregion
}
