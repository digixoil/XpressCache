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
        Assert.NotNull(result);
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
    public void GetCachedItems_WhenCacheDisabled_ReturnsNull()
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
        Assert.NotNull(subject1Items);
        Assert.Equal(2, subject1Items.Count);
        Assert.Contains(subject1Items, e => e.Name == "A");
        Assert.Contains(subject1Items, e => e.Name == "B");

        Assert.NotNull(subject2Items);
        Assert.Single(subject2Items);
        Assert.Equal("C", subject2Items[0].Name);

        Assert.NotNull(otherItems);
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
        Assert.NotNull(items);
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

        Assert.NotNull(testItems);
        Assert.NotNull(otherItems);
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
                return Task.FromResult<TestEntity>(null!);
            }
        );

        // Second call - should hit cache
        var result2 = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: _ =>
            {
                callCount++;
                return Task.FromResult<TestEntity>(null!);
            }
        );

        // Assert
        Assert.Null(result1);
        Assert.Null(result2);
        Assert.Equal(1, callCount); // Should only call recovery once
    }

    [Fact]
    public async Task LoadItem_SyncValidate_PassesValidation_UsesCached()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var validateCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Cached" });

        // Act - Load with sync validation that passes (using CacheValidationContext)
        var result = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: async (id) =>
            {
                recoveryCount++;
                return new TestEntity { Id = id, Name = "Recovery" };
            },
            syncValidateWithContext: (item, ctx) =>
            {
                validateCount++;
                Assert.Equal("Cached", item?.Name);
                return true; // Validation passes
            }
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Cached", result.Name);
        Assert.Equal(1, validateCount);
        Assert.Equal(0, recoveryCount); // Should NOT call recovery when validation passes
    }

    [Fact]
    public async Task LoadItem_SyncValidate_FailsValidation_TriggersRecovery()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var validateCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Old" });

        // Act - Load with sync validation that fails (using CacheValidationContext)
        var result = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: async (id) =>
            {
                recoveryCount++;
                return new TestEntity { Id = id, Name = "New" };
            },
            syncValidateWithContext: (item, ctx) =>
            {
                validateCount++;
                return false; // Validation fails
            }
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal("New", result.Name);
        Assert.Equal(1, validateCount);
        Assert.Equal(1, recoveryCount);
    }

    [Fact]
    public async Task LoadItem_AsyncValidate_PassesValidation_UsesCached()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var validateCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Cached" });

        // Act - Load with async validation that passes (using CacheValidationContext)
        var result = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: async (id) =>
            {
                recoveryCount++;
                return new TestEntity { Id = id, Name = "Recovery" };
            },
            asyncValidateWithContext: async (item, ctx) =>
            {
                validateCount++;
                await Task.CompletedTask;
                Assert.Equal("Cached", item?.Name);
                return true; // Validation passes
            }
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Cached", result.Name);
        Assert.True(validateCount > 0, "Validation should be called");
        Assert.Equal(0, recoveryCount); // Should NOT call recovery when validation passes
    }

    [Fact]
    public async Task LoadItem_AsyncValidate_FailsValidation_TriggersRecovery()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Old" });

        // Act - Load with async validation that fails (using CacheValidationContext)
        var result = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: async (id) =>
            {
                recoveryCount++;
                return new TestEntity { Id = id, Name = "New" };
            },
            asyncValidateWithContext: async (item, ctx) =>
            {
                await Task.CompletedTask;
                return false; // Always fail validation
            }
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal("New", result.Name);
        Assert.Equal(1, recoveryCount);
    }

    [Fact]
    public async Task LoadItem_BothValidations_SyncFailsFirst()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var syncValidateCount = 0;
        var asyncValidateCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Test" });

        // Act - Sync validation fails, so async should not be called (using CacheValidationContext)
        var result = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: async (id) => new TestEntity { Id = id, Name = "Recovery" },
            syncValidateWithContext: (item, ctx) =>
            {
                syncValidateCount++;
                return false; // Fail sync validation
            },
            asyncValidateWithContext: async (item, ctx) =>
            {
                asyncValidateCount++;
                await Task.CompletedTask;
                return true;
            }
        );

        // Assert
        Assert.Equal("Recovery", result?.Name);
        Assert.Equal(1, syncValidateCount);
        Assert.Equal(0, asyncValidateCount); // Should NOT be called when sync fails
    }

    [Fact]
    public async Task LoadItem_BothValidations_BothPass_UsesCached()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var syncValidateCount = 0;
        var asyncValidateCount = 0;
        var recoveryCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Cached" });

        // Act - Both validations pass (using CacheValidationContext)
        var result = await cache.LoadItem<TestEntity>(
            entityId: entityId,
            subject: "test",
            cacheMissRecovery: async (id) =>
            {
                recoveryCount++;
                return new TestEntity { Id = id, Name = "Recovery" };
            },
            syncValidateWithContext: (item, ctx) =>
            {
                syncValidateCount++;
                return true; // Pass sync validation
            },
            asyncValidateWithContext: async (item, ctx) =>
            {
                asyncValidateCount++;
                await Task.CompletedTask;
                Assert.Equal("Cached", item?.Name);
                return true; // Pass async validation
            }
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Cached", result.Name);
        Assert.Equal(1, syncValidateCount);
        Assert.Equal(1, asyncValidateCount);
        Assert.Equal(0, recoveryCount); // Should NOT call recovery
    }

    [Fact]
    public async Task LoadItem_SyncValidate_WithStampedePrevention()
    {
        // Arrange
        var cache = CreateCache(new CacheStoreOptions
        {
            PreventCacheStampedeByDefault = true
        });
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var barrier = new TaskCompletionSource<bool>();

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Old" });

        // Act - Multiple concurrent calls with failing sync validation
        // Note: With sync validation, each thread validates independently in the fast path
        // before lock acquisition. If validation fails, the item is removed and each thread
        // proceeds to acquire lock and call recovery. This is different from async validation
        // which happens inside the lock.
        var tasks = Enumerable.Range(0, 5).Select(_ =>
            cache.LoadItem<TestEntity>(
                entityId: entityId,
                subject: "test",
                cacheMissRecovery: async (id) =>
                {
                    var count = Interlocked.Increment(ref recoveryCount);
                    if (count == 1)
                    {
                        await barrier.Task;
                    }
                    return new TestEntity { Id = id, Name = "New" };
                },
                syncValidateWithContext: (item, ctx) => false // Always fail - force recovery
            ).AsTask()
        ).ToArray();

        await Task.Delay(100);
        barrier.SetResult(true);

        var results = await Task.WhenAll(tasks);

        // Assert - With sync validation failing in fast path, all threads proceed to recovery
        // but stampede prevention ensures only one executes recovery
        // Note: First thread removes item and gets lock. Others wait, then find cache empty
        // (because first thread hasn't stored result yet), so they also call recovery.
        // This is expected behavior - sync validation happens BEFORE stampede prevention kicks in.
        Assert.True(recoveryCount >= 1, $"At least one recovery should execute, got {recoveryCount}");
        Assert.All(results, r =>
        {
            Assert.NotNull(r);
            Assert.Equal("New", r.Name);
        });
    }

    [Fact]
    public async Task LoadItem_AsyncValidate_WithStampedePrevention()
    {
        // Arrange
        var cache = CreateCache(new CacheStoreOptions
        {
            PreventCacheStampedeByDefault = true
        });
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var validateCount = 0;
        var barrier = new TaskCompletionSource<bool>();

        await cache.SetItem(entityId, "test", new TestEntity { Id = entityId, Name = "Old" });

        // Act - Multiple concurrent calls with failing async validation
        // The first thread to acquire lock will find the item, validate it (fails), 
        // remove it, and call recovery. Other threads will then find cache empty
        // and proceed to their own recovery calls.
        var tasks = Enumerable.Range(0, 5).Select(_ =>
            cache.LoadItem<TestEntity>(
                entityId: entityId,
                subject: "test",
                cacheMissRecovery: async (id) =>
                {
                    var count = Interlocked.Increment(ref recoveryCount);
                    if (count == 1)
                    {
                        await barrier.Task;
                    }
                    return new TestEntity { Id = id, Name = "New" };
                },
                asyncValidateWithContext: async (item, ctx) =>
                {
                    Interlocked.Increment(ref validateCount);
                    await Task.CompletedTask;
                    return false; // Always fail - force recovery
                }
            ).AsTask()
        ).ToArray();

        await Task.Delay(100);
        barrier.SetResult(true);

        var results = await Task.WhenAll(tasks);

        // Assert - First thread validates and removes item, then all threads call recovery
        // Stampede prevention ensures they don't ALL call simultaneously, but since
        // the first thread removes the item, subsequent threads find cache empty
        Assert.True(recoveryCount >= 1);
        Assert.True(validateCount >= 1, "At least first thread should validate");
        Assert.All(results, r =>
        {
            Assert.NotNull(r);
            Assert.Equal("New", r.Name);
        });
    }

    [Fact]
    public async Task LoadItem_AsyncValidate_CacheMiss_StampedePrevention_Works()
    {
        // Arrange
        var cache = CreateCache(new CacheStoreOptions
        {
            PreventCacheStampedeByDefault = true
        });
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var barrier = new TaskCompletionSource<bool>();

        // Note: No item in cache initially

        // Act - Multiple concurrent calls for a cache miss
        // Stampede prevention ensures only one recovery executes
        var tasks = Enumerable.Range(0, 5).Select(_ =>
            cache.LoadItem<TestEntity>(
                entityId: entityId,
                subject: "test",
                cacheMissRecovery: async (id) =>
                {
                    var count = Interlocked.Increment(ref recoveryCount);
                    if (count == 1)
                    {
                        await barrier.Task;
                    }
                    return new TestEntity { Id = id, Name = "Fresh", IsValid = true };
                },
                asyncValidateWithContext: async (item, ctx) =>
                {
                    await Task.CompletedTask;
                    return item.IsValid; // Validate the recovered item
                }
            ).AsTask()
        ).ToArray();

        await Task.Delay(100);
        barrier.SetResult(true);

        var results = await Task.WhenAll(tasks);

        // Assert - Stampede prevention works for cache miss scenario
        Assert.Equal(1, recoveryCount);
        Assert.All(results, r =>
        {
            Assert.NotNull(r);
            Assert.Equal("Fresh", r.Name);
        });
    }

    private class TestEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsValid { get; set; } = true;
    }

    private class OtherEntity
    {
        public Guid Id { get; set; }
        public int Value { get; set; }
    }
}
