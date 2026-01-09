using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace XpressCache.Tests;

/// <summary>
/// Additional edge case and advanced scenario tests for <see cref="CacheStore"/>.
/// </summary>
public class AdvancedScenariosTests
{
    private readonly NullLogger<CacheStore> _logger = new();

    private CacheStore CreateCache(CacheStoreOptions? options = null)
    {
        options ??= new CacheStoreOptions();
        return new CacheStore(_logger, Options.Create(options));
    }

    [Fact]
    public async Task LoadItem_WithNullEntityId_ReturnsDefault()
    {
        // Arrange
        var cache = CreateCache();
        var called = false;

        // Act
        var result = await cache.LoadItem<TestEntity>(
            entityId: null,
            subject: "test",
            cacheMissRecovery: _ =>
            {
                called = true;
                return Task.FromResult(new TestEntity());
            }
        );

        // Assert
        Assert.Null(result);
        Assert.False(called, "Recovery should not be called for null entity ID");
    }

    [Fact]
    public async Task LoadItem_WithEmptyGuid_ReturnsDefault()
    {
        // Arrange
        var cache = CreateCache();
        var called = false;

        // Act
        var result = await cache.LoadItem<TestEntity>(
            entityId: Guid.Empty,
            subject: "test",
            cacheMissRecovery: _ =>
            {
                called = true;
                return Task.FromResult(new TestEntity());
            }
        );

        // Assert
        Assert.Null(result);
        Assert.False(called, "Recovery should not be called for empty GUID");
    }

    [Fact]
    public async Task LoadItem_WithNullRecovery_ReturnsCachedOrNull()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();

        // Act - First call with null recovery (nothing in cache)
        var result1 = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: null
        );

        // Set item in cache
        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Cached" });

        // Second call with null recovery (now in cache)
        var result2 = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: null
        );

        // Assert
        Assert.Null(result1);
        Assert.NotNull(result2);
        Assert.Equal("Cached", result2.Name);
    }

    [Fact]
    public async Task LoadItem_NullSubject_NormalizedToEmpty()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();

        // Act - Set with null subject
        await cache.SetItem(entityId, null, new TestEntity { Id = entityId, Name = "Test" });

        // Load with empty string subject
        var result = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: string.Empty,
            cacheMissRecovery: null
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test", result.Name);
    }

    [Fact]
    public async Task LoadItem_DifferentSubjects_StoredSeparately()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();

        var entity1 = new TestEntity { Id = entityId, Name = "Subject1" };
        var entity2 = new TestEntity { Id = entityId, Name = "Subject2" };

        // Act
        await cache.SetItem(entityId, "subject1", entity1);
        await cache.SetItem(entityId, "subject2", entity2);

        var result1 = await cache.LoadItem<TestEntity>(entityId, "subject1", null);
        var result2 = await cache.LoadItem<TestEntity>(entityId, "subject2", null);

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal("Subject1", result1.Name);
        Assert.Equal("Subject2", result2.Name);
    }

    [Fact]
    public async Task LoadItem_DifferentTypes_StoredSeparately()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();

        var testEntity = new TestEntity { Id = entityId, Name = "Entity" };
        var otherEntity = new OtherEntity { Id = entityId, Value = 42 };

        // Act
        await cache.SetItem(entityId, "test", testEntity);
        await cache.SetItem(entityId, "test", otherEntity);

        var result1 = await cache.LoadItem<TestEntity>(entityId, "test", null);
        var result2 = await cache.LoadItem<OtherEntity>(entityId, "test", null);

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal("Entity", result1.Name);
        Assert.Equal(42, result2.Value);
    }

    [Fact]
    public async Task EnableCache_SetToFalse_DisablesAllOperations()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCallCount = 0;

        // First, add item to cache
        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId });

        // Act - Disable cache
        cache.EnableCache = false;

        // Try to load (should call recovery even though item is cached)
        var result = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: async (id) =>
            {
                recoveryCallCount++;
                return new TestEntity { Id = id, Name = "Fresh" };
            }
        );

        // Assert
        Assert.Equal(1, recoveryCallCount);
        Assert.Equal("Fresh", result.Name);
    }

    [Fact]
    public async Task EnableCache_SetToTrue_ClearsCache()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Original" });

        // Act - Disable then re-enable
        cache.EnableCache = false;
        cache.EnableCache = true;

        // Try to load
        var result = await cache.LoadItem<TestEntity>(entityId, "test", null);

        // Assert
        Assert.Null(result); // Cache was cleared when re-enabled
    }

    [Fact]
    public async Task SetItem_WhenCacheDisabled_DoesNothing()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();

        cache.EnableCache = false;

        // Act
        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId });

        // Re-enable and check
        cache.EnableCache = true;
        var result = await cache.LoadItem<TestEntity>(entityId, "test", null);

        // Assert
        Assert.Null(result); // Nothing was cached
    }

    [Fact]
    public void RemoveItem_NonExistentItem_ReturnsFalse()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();

        // Act
        var removed = cache.RemoveItem<TestEntity>(entityId, "test");

        // Assert
        Assert.False(removed);
    }

    [Fact]
    public async Task RemoveItem_ExistingItem_ReturnsTrue()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId });

        // Act
        var removed = cache.RemoveItem<TestEntity>(entityId, "test");

        // Assert
        Assert.True(removed);

        // Verify it's gone
        var result = await cache.LoadItem<TestEntity>(entityId, "test", null);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedItems_WhenCacheDisabled_ReturnsNull()
    {
        // Arrange
        var cache = CreateCache();
        cache.EnableCache = false;

        // Act
        var items = cache.GetCachedItems<TestEntity>("test");

        // Assert
        Assert.Null(items);
    }

    [Fact]
    public async Task GetCachedItems_FiltersCorrectly()
    {
        // Arrange
        var cache = CreateCache();

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        await cache.SetItem(id1, "subject1", new TestEntity { Id = id1, Name = "A" });
        await cache.SetItem(id2, "subject1", new TestEntity { Id = id2, Name = "B" });
        await cache.SetItem(id3, "subject2", new TestEntity { Id = id3, Name = "C" });
        await cache.SetItem(id1, "subject1", new OtherEntity { Id = id1, Value = 1 });

        // Act
        var subject1Items = cache.GetCachedItems<TestEntity>("subject1");
        var subject2Items = cache.GetCachedItems<TestEntity>("subject2");
        var otherItems = cache.GetCachedItems<OtherEntity>("subject1");

        // Assert
        Assert.Equal(2, subject1Items.Count);
        Assert.Contains(subject1Items, e => e.Name == "A");
        Assert.Contains(subject1Items, e => e.Name == "B");

        Assert.Single(subject2Items);
        Assert.Equal("C", subject2Items[0].Name);

        Assert.Single(otherItems);
        Assert.Equal(1, otherItems[0].Value);
    }

    [Fact]
    public async Task CleanupCache_RemovesExpiredItems()
    {
        // Arrange
        var cache = CreateCache(new CacheStoreOptions
        {
            DefaultTtlMs = 100 // Very short TTL
        });

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        await cache.SetItem(id1, "test", new TestEntity { Id = id1, Name = "Will Expire" });

        // Wait for expiration
        await Task.Delay(150);

        await cache.SetItem(id2, "test", new TestEntity { Id = id2, Name = "Fresh" });

        // Act
        cache.CleanupCache();

        var items = cache.GetCachedItems<TestEntity>("test");

        // Assert
        Assert.Single(items);
        Assert.Equal("Fresh", items[0].Name);
    }

    [Fact]
    public async Task Clear_RemovesAllItems()
    {
        // Arrange
        var cache = CreateCache();

        await cache.SetItem(Guid.NewGuid(), "test", new TestEntity());
        await cache.SetItem(Guid.NewGuid(), "test", new TestEntity());
        await cache.SetItem(Guid.NewGuid(), "other", new TestEntity());

        // Act
        cache.Clear();

        // Assert
        var testItems = cache.GetCachedItems<TestEntity>("test");
        var otherItems = cache.GetCachedItems<TestEntity>("other");

        Assert.Empty(testItems);
        Assert.Empty(otherItems);
    }

    [Fact]
    public async Task LoadItem_RecoveryReturnsNull_CachesNull()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var callCount = 0;

        // Act
        var result1 = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: _ =>
            {
                callCount++;
                return Task.FromResult<TestEntity>(null);
            }
        );

        // Second call - should hit cache
        var result2 = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: _ =>
            {
                callCount++;
                return Task.FromResult<TestEntity>(null);
            }
        );

        // Assert
        Assert.Null(result1);
        Assert.Null(result2);
        Assert.Equal(1, callCount); // Should only call recovery once
    }

    /// <summary>
    /// Tests for custom validation behavior.
    /// NOTE: These tests document KNOWN ISSUE with custom validation (see Known-Issues.md).
    /// Custom validation currently bypasses the fast cache-hit path and may trigger
    /// unnecessary recovery calls. This is tracked for fix in v1.0.1.
    /// </summary>
    [Fact]
    [Trait("KnownIssue", "CustomValidation")]
    public async Task LoadItem_CustomValidate_CurrentBehavior_DocumentedLimitation()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Cached" });

        // Act - Current behavior: custom validation triggers full async path
        var result = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: async (id) =>
            {
                recoveryCount++;
                return new TestEntity { Id = id, Name = "Recovery" };
            },
            customValidate: async (item) =>
            {
                await Task.CompletedTask;
                // ISSUE: This validation is called on the RECOVERED item, not cached item
                return true;
            }
        );

        // Assert - Current (buggy) behavior
        // TODO: In v1.0.1, this should get "Cached" and recoveryCount should be 0
        Assert.NotNull(result);
        // Current behavior: recovery is called even though item is cached
        Assert.True(recoveryCount >= 1, "KNOWN ISSUE: Recovery called unnecessarily");
    }

    [Fact]
    public async Task LoadItem_CustomValidate_WithoutCache_Works()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var validateCount = 0;

        // Act - No cached item, recovery is expected
        var result = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: async (id) => new TestEntity { Id = id, Name = "Fresh" },
            customValidate: async (item) =>
            {
                validateCount++;
                await Task.CompletedTask;
                Assert.Equal("Fresh", item?.Name);
                return true;
            }
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Fresh", result.Name);
        Assert.Equal(1, validateCount);
    }

    [Fact]
    public async Task LoadItem_CustomValidate_FailureTriggersRecovery()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Old" });

        // Act - Validation fails, should trigger recovery
        var result = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: async (id) =>
            {
                recoveryCount++;
                return new TestEntity { Id = id, Name = "New" };
            },
            customValidate: async (item) =>
            {
                await Task.CompletedTask;
                return false; // Always fail validation
            }
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal("New", result.Name);
        Assert.True(recoveryCount > 0);
    }

    [Fact]
    public async Task LoadItem_SlidingExpiration_ExtendsLifetime()
    {
        // Arrange
        var cache = CreateCache(new CacheStoreOptions
        {
            DefaultTtlMs = 200 // 200ms TTL
        });

        var entityId = Guid.NewGuid();
        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Test" });

        // Act - Access every 100ms (before 200ms expiry)
        await Task.Delay(100);
        var result1 = await cache.LoadItem<TestEntity>(entityId, "test", null);
        
        await Task.Delay(100);
        var result2 = await cache.LoadItem<TestEntity>(entityId, "test", null);
        
        await Task.Delay(100);
        var result3 = await cache.LoadItem<TestEntity>(entityId, "test", null);

        // Assert - Should still be cached due to sliding expiration
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.NotNull(result3);
    }

    [Fact]
    public async Task LoadItem_AfterExpiration_TriggersRecovery()
    {
        // Arrange
        var cache = CreateCache(new CacheStoreOptions
        {
            DefaultTtlMs = 100 // Very short TTL
        });

        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Old" });

        // Wait for expiration
        await Task.Delay(150);

        // Act
        var result = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: async (id) =>
            {
                recoveryCount++;
                return new TestEntity { Id = id, Name = "New" };
            }
        );

        // Assert
        Assert.Equal("New", result.Name);
        Assert.Equal(1, recoveryCount);
    }

    [Fact]
    public async Task LoadItem_ConcurrentDifferentSubjects_ExecuteInParallel()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var allStarted = new System.Threading.CountdownEvent(2);
        var allowComplete = new TaskCompletionSource<bool>();

        async Task<TestEntity> Recovery(Guid id, string name)
        {
            allStarted.Signal();
            await allowComplete.Task;
            return new TestEntity { Id = id, Name = name };
        }

        // Act
        var task1 = cache.LoadItem<TestEntity>(
            entityId, "subject1",
            async id => await Recovery(id, "A")
        ).AsTask();

        var task2 = cache.LoadItem<TestEntity>(
            entityId, "subject2",
            async id => await Recovery(id, "B")
        ).AsTask();

        // Both should start (different subjects)
        var started = allStarted.Wait(TimeSpan.FromSeconds(5));
        allowComplete.SetResult(true);

        var results = await Task.WhenAll(task1, task2);

        // Assert
        Assert.True(started);
        Assert.Equal(2, results.Length);
    }

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
}
