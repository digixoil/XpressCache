using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace XpressCache.Tests;

/// <summary>
/// Unit tests for <see cref="CacheStore"/> custom validation and edge cases.
/// </summary>
public class ValidationAndEdgeCaseTests
{
    private readonly ILogger<CacheStore> _logger;
    private readonly CacheStore _cache;

    public ValidationAndEdgeCaseTests()
    {
        _logger = new NullLogger<CacheStore>();
        _cache = new CacheStore(_logger);
    }

    #region Custom Validation Tests (with CacheValidationContext)

    [Fact]
    public async Task LoadItem_SyncValidationPasses_UsesCachedItem()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        // First call - populate cache
        await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { IsValid = true, Name = "First" }); });

        // Act - Second call with sync validation that passes (using CacheValidationContext)
        var result = await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { IsValid = true, Name = "Second" }); },
            syncValidateWithContext: (entity, ctx) => entity.IsValid);

        // Assert
        Assert.Equal(1, recoveryCount); // Only first call did recovery
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Equal("First", result.Name); // Uses cached item
    }

    [Fact]
    public async Task LoadItem_SyncValidationFails_ExecutesRecovery()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        // First call - populate cache with invalid item
        await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { IsValid = false }); });

        // Act - Second call with validation that fails (using CacheValidationContext)
        var result = await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { IsValid = true, Name = "Fresh" }); },
            syncValidateWithContext: (entity, ctx) => entity.IsValid);

        // Assert
        Assert.Equal(2, recoveryCount); // Recovery called again
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Equal("Fresh", result.Name);
    }

    [Fact]
    public async Task LoadItem_AsyncValidationPasses_UsesCachedItem()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        // First call - populate cache
        await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { IsValid = true, Name = "Cached" }); });

        // Act - Second call with async validation that passes (using CacheValidationContext)
        var result = await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { IsValid = true, Name = "New" }); },
            asyncValidateWithContext: async (entity, ctx) =>
            {
                await Task.CompletedTask;
                return entity.IsValid;
            });

        // Assert
        Assert.Equal(1, recoveryCount); // Only first call did recovery
        Assert.NotNull(result);
        Assert.Equal("Cached", result.Name); // Uses cached item
    }

    [Fact]
    public async Task LoadItem_AsyncValidationFails_ExecutesRecovery()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        // First call - populate cache with invalid item
        await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { IsValid = false }); });

        // Act - Second call with async validation that fails (using CacheValidationContext)
        var result = await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { IsValid = true, Name = "Fresh" }); },
            asyncValidateWithContext: async (entity, ctx) =>
            {
                await Task.CompletedTask;
                return entity.IsValid;
            });

        // Assert
        Assert.Equal(2, recoveryCount); // Recovery called again
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Equal("Fresh", result.Name);
    }

    [Fact]
    public async Task LoadItem_NullValidation_SkipsValidation()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "Cached" }); });

        // Act - No validation, uses fast path and hits cache
        var result = await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "New" }); },
            syncValidateWithContext: null,
            asyncValidateWithContext: null);

        // Assert
        Assert.Equal(1, recoveryCount); // Only first call did recovery
        Assert.NotNull(result);
        Assert.Equal("Cached", result.Name);
    }

    [Fact]
    public async Task LoadItem_ValidationWithAsyncOperation_ExecutesValidation()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var validationCount = 0;
        var recoveryCount = 0;

        // First call populates cache
        await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "Cached", IsValid = true }); });

        // Second call with async validation that passes (using CacheValidationContext)
        var result = await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "Recovered", IsValid = true }); },
            asyncValidateWithContext: async (entity, ctx) =>
            {
                validationCount++;
                await Task.Delay(10); // Simulate async validation
                return entity.IsValid;
            });

        // Assert
        Assert.Equal(1, recoveryCount); // Only first call does recovery
        Assert.NotNull(result);
        Assert.Equal("Cached", result.Name); // Uses cached value
        Assert.Equal(1, validationCount); // Validation was called
    }

    [Fact]
    public async Task LoadItem_ValidationCanAccessExpiryProgress()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        double? capturedProgress = null;

        await _cache.SetItem(entityId, "subject", new TestEntity { Name = "Test" });

        // Act - Validation accesses the context
        var result = await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => Task.FromResult(new TestEntity { Name = "Recovered" }),
            syncValidateWithContext: (entity, ctx) =>
            {
                capturedProgress = ctx.ExpiryProgress;
                return true;
            });

        // Assert
        Assert.NotNull(capturedProgress);
        Assert.True(capturedProgress >= 0.0 && capturedProgress <= 1.0);
        Assert.NotNull(result);
        Assert.Equal("Test", result.Name);
    }

    [Fact]
    public async Task LoadItem_ValidationCanAccessTimeToExpiry()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        TimeSpan? capturedTimeToExpiry = null;

        await _cache.SetItem(entityId, "subject", new TestEntity { Name = "Test" });

        // Act
        var result = await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => Task.FromResult(new TestEntity { Name = "Recovered" }),
            syncValidateWithContext: (entity, ctx) =>
            {
                capturedTimeToExpiry = ctx.TimeToExpiry;
                return true;
            });

        // Assert
        Assert.NotNull(capturedTimeToExpiry);
        Assert.True(capturedTimeToExpiry.Value > TimeSpan.Zero);
    }

    [Fact]
    public async Task LoadItem_ValidationCanAccessAge()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        TimeSpan? capturedAge = null;

        await _cache.SetItem(entityId, "subject", new TestEntity { Name = "Test" });
        
        // Wait a bit for the item to age
        await Task.Delay(50);

        // Act
        var result = await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => Task.FromResult(new TestEntity { Name = "Recovered" }),
            syncValidateWithContext: (entity, ctx) =>
            {
                capturedAge = ctx.Age;
                return true;
            });

        // Assert
        Assert.NotNull(capturedAge);
        Assert.True(capturedAge.Value >= TimeSpan.FromMilliseconds(40)); // Allow some tolerance
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task LoadItem_RecoveryReturnsNull_CachesNull()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        // Act
        var result1 = await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult<TestEntity>(null!); });

        var result2 = await _cache.LoadItem<TestEntity>(
            entityId,
            "subject",
            _ => { recoveryCount++; return Task.FromResult<TestEntity>(null!); });

        // Assert
        Assert.Null(result1);
        Assert.Null(result2);
        Assert.Equal(1, recoveryCount); // Null was cached
    }

    [Fact]
    public async Task LoadItem_VeryLongSubject_WorksCorrectly()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var longSubject = new string('x', 10000);

        // Act
        await _cache.SetItem(entityId, longSubject, new TestEntity { Name = "Test" });
        var result = await _cache.LoadItem<TestEntity>(entityId, longSubject, null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test", result.Name);
    }

    [Fact]
    public async Task LoadItem_SpecialCharactersInSubject_WorksCorrectly()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var specialSubject = "test/with\\special:chars?and=symbols&more<>\"'";

        // Act
        await _cache.SetItem(entityId, specialSubject, new TestEntity { Name = "Special" });
        var result = await _cache.LoadItem<TestEntity>(entityId, specialSubject, null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Special", result.Name);
    }

    [Fact]
    public async Task LoadItem_UnicodeSubject_WorksCorrectly()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var unicodeSubject = "??? ?? ???????";

        // Act
        await _cache.SetItem(entityId, unicodeSubject, new TestEntity { Name = "Unicode" });
        var result = await _cache.LoadItem<TestEntity>(entityId, unicodeSubject, null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Unicode", result.Name);
    }

    [Fact]
    public async Task LoadItem_ManyDifferentKeys_HandlesCorrectly()
    {
        // Arrange
        var items = Enumerable.Range(0, 100)
            .Select(i => (Id: Guid.NewGuid(), Name: $"Item{i}"))
            .ToList();

        // Act - Store all items
        foreach (var item in items)
        {
            await _cache.SetItem(item.Id, "subject", new TestEntity { Id = item.Id, Name = item.Name });
        }

        // Verify all items
        var allFound = true;
        foreach (var item in items)
        {
            var result = await _cache.LoadItem<TestEntity>(item.Id, "subject", null);
            if (result?.Name != item.Name)
            {
                allFound = false;
                break;
            }
        }

        // Assert
        Assert.True(allFound);
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new CacheStore(null!));
    }

    [Fact]
    public void Constructor_NullOptions_UsesDefaults()
    {
        // Act
        var cache = new CacheStore(_logger, null!);

        // Assert - Should not throw and should work
        Assert.True(cache.EnableCache);
    }

    [Fact]
    public async Task SetItem_NullItem_StoresNull()
    {
        // Arrange
        var entityId = Guid.NewGuid();

        // Act
        await _cache.SetItem<TestEntity>(entityId, "subject", null!);
        var result = await _cache.LoadItem<TestEntity>(entityId, "subject", null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void RemoveItem_NullEntityId_ReturnsFalse()
    {
        // Act
        var result = _cache.RemoveItem<TestEntity>(null, "subject");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CleanupCache_WhenCacheDisabled_DoesNotThrow()
    {
        // Arrange
        _cache.EnableCache = false;

        // Act & Assert (should not throw)
        _cache.CleanupCache();
    }

    [Fact]
    public async Task Cache_TypeMismatch_LogsWarningAndReturnsDefault()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        
        // Store as one type
        await _cache.SetItem(entityId, "subject", new TestEntity { Name = "Test" });

        // Act - Try to retrieve as different type (internal type mismatch handling)
        // This tests the type safety of the cache key
        var result = await _cache.LoadItem<OtherEntity>(entityId, "subject", null);

        // Assert - Should return null (different types have different cache keys)
        Assert.Null(result);
    }

    #endregion

    #region Options Tests

    [Fact]
    public void CacheStoreOptions_DefaultValues_AreCorrect()
    {
        // Act
        var options = new CacheStoreOptions();

        // Assert
        Assert.True(options.PreventCacheStampedeByDefault);
        Assert.Equal(600000, options.DefaultTtlMs); // 10 minutes
        Assert.Equal(256, options.InitialCapacity);
        Assert.Equal(1000, options.ProbabilisticCleanupThreshold);
    }

    [Fact]
    public async Task CacheStore_WithCustomOptions_UsesProvidedValues()
    {
        // Arrange
        var options = new CacheStoreOptions
        {
            PreventCacheStampedeByDefault = false,
            DefaultTtlMs = 1000,
            InitialCapacity = 512,
            ProbabilisticCleanupThreshold = 500
        };
        var cache = new CacheStore(_logger, Options.Create(options));

        // Act - Verify cache works with custom options
        var entityId = Guid.NewGuid();
        await cache.SetItem(entityId, "subject", new TestEntity { Name = "Custom" });
        var result = await cache.LoadItem<TestEntity>(entityId, "subject", null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Custom", result.Name);
    }

    #endregion

    #region Test Entities

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

    #endregion
}
