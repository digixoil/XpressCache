using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace XpressCache.Tests;

/// <summary>
/// Unit tests for <see cref="CacheValidationContext"/> and validation with timing context.
/// </summary>
public class ValidationContextTests
{
    private readonly ILogger<CacheStore> _logger;

    public ValidationContextTests()
    {
        _logger = new NullLogger<CacheStore>();
    }

    private CacheStore CreateCache(long ttlMs = 600_000)
    {
        var options = new CacheStoreOptions
        {
            DefaultTtlMs = ttlMs,
            ProbabilisticCleanupThreshold = 100000
        };
        return new CacheStore(_logger, Options.Create(options));
    }

    #region CacheValidationContext Unit Tests

    [Fact]
    public void CacheValidationContext_TimeToExpiry_ReturnsCorrectValue()
    {
        // Arrange
        var currentTicks = 1000L;
        var ttlMs = 600_000L;
        var expiryTicks = currentTicks + ttlMs;
        var context = new CacheValidationContext(expiryTicks, currentTicks, ttlMs);

        // Act
        var timeToExpiry = context.TimeToExpiry;

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(ttlMs), timeToExpiry);
    }

    [Fact]
    public void CacheValidationContext_TimeToExpiry_WhenExpired_ReturnsZero()
    {
        // Arrange
        var ttlMs = 600_000L;
        var expiryTicks = 1000L;
        var currentTicks = expiryTicks + 1000; // After expiry
        var context = new CacheValidationContext(expiryTicks, currentTicks, ttlMs);

        // Act
        var timeToExpiry = context.TimeToExpiry;

        // Assert
        Assert.Equal(TimeSpan.Zero, timeToExpiry);
    }

    [Fact]
    public void CacheValidationContext_Age_ReturnsCorrectValue()
    {
        // Arrange
        var ttlMs = 600_000L;
        var createdAtTicks = 1000L;
        var expiryTicks = createdAtTicks + ttlMs;
        var elapsedMs = 100_000L; // 100 seconds
        var currentTicks = createdAtTicks + elapsedMs;
        var context = new CacheValidationContext(expiryTicks, currentTicks, ttlMs);

        // Act
        var age = context.Age;

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(elapsedMs), age);
    }

    [Fact]
    public void CacheValidationContext_Age_WhenJustCreated_ReturnsZero()
    {
        // Arrange
        var ttlMs = 600_000L;
        var currentTicks = 1000L;
        var expiryTicks = currentTicks + ttlMs;
        var context = new CacheValidationContext(expiryTicks, currentTicks, ttlMs);

        // Act
        var age = context.Age;

        // Assert
        Assert.Equal(TimeSpan.Zero, age);
    }

    [Fact]
    public void CacheValidationContext_ExpiryProgress_AtStart_ReturnsZero()
    {
        // Arrange
        var ttlMs = 600_000L;
        var currentTicks = 1000L;
        var expiryTicks = currentTicks + ttlMs;
        var context = new CacheValidationContext(expiryTicks, currentTicks, ttlMs);

        // Act
        var progress = context.ExpiryProgress;

        // Assert
        Assert.Equal(0.0, progress, precision: 2);
    }

    [Fact]
    public void CacheValidationContext_ExpiryProgress_AtMidpoint_ReturnsHalf()
    {
        // Arrange
        var ttlMs = 600_000L;
        var startTicks = 1000L;
        var expiryTicks = startTicks + ttlMs;
        var currentTicks = startTicks + (ttlMs / 2); // Halfway
        var context = new CacheValidationContext(expiryTicks, currentTicks, ttlMs);

        // Act
        var progress = context.ExpiryProgress;

        // Assert
        Assert.Equal(0.5, progress, precision: 2);
    }

    [Fact]
    public void CacheValidationContext_ExpiryProgress_AtExpiry_ReturnsOne()
    {
        // Arrange
        var ttlMs = 600_000L;
        var startTicks = 1000L;
        var expiryTicks = startTicks + ttlMs;
        var currentTicks = expiryTicks; // At expiry
        var context = new CacheValidationContext(expiryTicks, currentTicks, ttlMs);

        // Act
        var progress = context.ExpiryProgress;

        // Assert
        Assert.Equal(1.0, progress, precision: 2);
    }

    [Fact]
    public void CacheValidationContext_ExpiryProgress_AfterExpiry_ClampedToOne()
    {
        // Arrange
        var ttlMs = 600_000L;
        var startTicks = 1000L;
        var expiryTicks = startTicks + ttlMs;
        var currentTicks = expiryTicks + 100_000; // Well after expiry
        var context = new CacheValidationContext(expiryTicks, currentTicks, ttlMs);

        // Act
        var progress = context.ExpiryProgress;

        // Assert
        Assert.Equal(1.0, progress, precision: 2);
    }

    [Fact]
    public void CacheValidationContext_ToString_ReturnsFormattedString()
    {
        // Arrange
        var ttlMs = 600_000L;
        var currentTicks = 1000L;
        var expiryTicks = currentTicks + ttlMs;
        var context = new CacheValidationContext(expiryTicks, currentTicks, ttlMs);

        // Act
        var result = context.ToString();

        // Assert
        Assert.Contains("CacheValidationContext", result);
        Assert.Contains("Age=", result);
        Assert.Contains("TimeToExpiry=", result);
        Assert.Contains("ExpiryProgress=", result);
    }

    #endregion

    #region LoadItem with SyncValidateWithContext Tests

    [Fact]
    public async Task LoadItem_SyncValidateWithContext_ReceivesValidContext()
    {
        // Arrange
        var cache = CreateCache(ttlMs: 10_000); // 10 second TTL
        var entityId = Guid.NewGuid();
        CacheValidationContext? capturedContext = null;

        // Populate cache
        await cache.SetItem(entityId, "test", new TestEntity { Name = "Test" });

        // Act
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => Task.FromResult(new TestEntity { Name = "Recovered" }),
            syncValidateWithContext: (item, ctx) =>
            {
                capturedContext = ctx;
                return true;
            });

        // Assert
        Assert.NotNull(capturedContext);
        Assert.True(capturedContext.Value.TtlMs > 0, "TTL should be positive");
        Assert.True(capturedContext.Value.ExpiryTicks > 0, "ExpiryTicks should be positive");
        Assert.True(capturedContext.Value.CurrentTicks > 0, "CurrentTicks should be positive");
        Assert.True(capturedContext.Value.TimeToExpiry > TimeSpan.Zero, "TimeToExpiry should be positive");
    }

    [Fact]
    public async Task LoadItem_SyncValidateWithContext_PassesValidation_UsesCached()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Name = "Cached" });

        // Act - validation passes
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "Recovered" }); },
            syncValidateWithContext: (item, ctx) => ctx.ExpiryProgress < 0.9 // Should pass
        );

        // Assert
        Assert.Equal("Cached", result?.Name);
        Assert.Equal(0, recoveryCount);
    }

    [Fact]
    public async Task LoadItem_SyncValidateWithContext_FailsValidation_ExecutesRecovery()
    {
        // Arrange
        var cache = CreateCache(ttlMs: 100); // 100ms TTL
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Name = "Cached" });

        // Wait for partial expiry
        await Task.Delay(60);

        // Act - validation fails (progress > 0.5)
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "Recovered" }); },
            syncValidateWithContext: (item, ctx) => ctx.ExpiryProgress < 0.5
        );

        // Assert
        Assert.Equal("Recovered", result?.Name);
        Assert.Equal(1, recoveryCount);
    }

    [Fact]
    public async Task LoadItem_SyncValidateWithContext_BasedOnAge_Works()
    {
        // Arrange
        var cache = CreateCache(ttlMs: 10_000); // 10 second TTL
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Name = "Cached" });

        // Wait for item to age
        await Task.Delay(100);

        // Act - invalidate items older than 50ms
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "Recovered" }); },
            syncValidateWithContext: (item, ctx) => ctx.Age.TotalMilliseconds < 50
        );

        // Assert
        Assert.Equal("Recovered", result?.Name);
        Assert.Equal(1, recoveryCount);
    }

    [Fact]
    public async Task LoadItem_SyncValidateWithContext_BasedOnTimeToExpiry_Works()
    {
        // Arrange
        var cache = CreateCache(ttlMs: 200); // 200ms TTL
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Name = "Cached" });

        // Wait until close to expiry
        await Task.Delay(150);

        // Act - refresh items with less than 100ms remaining
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "Recovered" }); },
            syncValidateWithContext: (item, ctx) => ctx.TimeToExpiry.TotalMilliseconds > 100
        );

        // Assert
        Assert.Equal("Recovered", result?.Name);
        Assert.Equal(1, recoveryCount);
    }

    #endregion

    #region LoadItem with AsyncValidateWithContext Tests

    [Fact]
    public async Task LoadItem_AsyncValidateWithContext_ReceivesValidContext()
    {
        // Arrange
        var cache = CreateCache(ttlMs: 10_000);
        var entityId = Guid.NewGuid();
        CacheValidationContext? capturedContext = null;

        await cache.SetItem(entityId, "test", new TestEntity { Name = "Test" });

        // Act
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => Task.FromResult(new TestEntity { Name = "Recovered" }),
            asyncValidateWithContext: async (item, ctx) =>
            {
                capturedContext = ctx;
                await Task.CompletedTask;
                return true;
            });

        // Assert
        Assert.NotNull(capturedContext);
        Assert.True(capturedContext.Value.TtlMs > 0);
    }

    [Fact]
    public async Task LoadItem_AsyncValidateWithContext_PassesValidation_UsesCached()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Name = "Cached" });

        // Act
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "Recovered" }); },
            asyncValidateWithContext: async (item, ctx) =>
            {
                await Task.CompletedTask;
                return ctx.ExpiryProgress < 0.9;
            });

        // Assert
        Assert.Equal("Cached", result?.Name);
        Assert.Equal(0, recoveryCount);
    }

    [Fact]
    public async Task LoadItem_AsyncValidateWithContext_FailsValidation_ExecutesRecovery()
    {
        // Arrange
        var cache = CreateCache(ttlMs: 100);
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Name = "Cached" });
        await Task.Delay(60);

        // Act
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "Recovered" }); },
            asyncValidateWithContext: async (item, ctx) =>
            {
                await Task.CompletedTask;
                return ctx.ExpiryProgress < 0.5;
            });

        // Assert
        Assert.Equal("Recovered", result?.Name);
        Assert.Equal(1, recoveryCount);
    }

    #endregion

    #region Combined Sync and Async Validation with Context Tests

    [Fact]
    public async Task LoadItem_BothValidationsWithContext_SyncFailsFirst_AsyncNotCalled()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var syncCount = 0;
        var asyncCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Name = "Cached" });

        // Act - sync fails immediately
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => Task.FromResult(new TestEntity { Name = "Recovered" }),
            syncValidateWithContext: (item, ctx) => { syncCount++; return false; },
            asyncValidateWithContext: async (item, ctx) => { asyncCount++; await Task.CompletedTask; return true; });

        // Assert
        Assert.Equal("Recovered", result?.Name);
        Assert.Equal(1, syncCount);
        Assert.Equal(0, asyncCount);
    }

    [Fact]
    public async Task LoadItem_BothValidationsWithContext_BothPass_UsesCached()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var syncCount = 0;
        var asyncCount = 0;
        var recoveryCount = 0;

        await cache.SetItem(entityId, "test", new TestEntity { Name = "Cached" });

        // Act
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "Recovered" }); },
            syncValidateWithContext: (item, ctx) => { syncCount++; return true; },
            asyncValidateWithContext: async (item, ctx) => { asyncCount++; await Task.CompletedTask; return true; });

        // Assert
        Assert.Equal("Cached", result?.Name);
        Assert.Equal(1, syncCount);
        Assert.Equal(1, asyncCount);
        Assert.Equal(0, recoveryCount);
    }

    #endregion

    #region Proactive Refresh Pattern Tests

    [Fact]
    public async Task ProactiveRefresh_WhenEntryAt75Percent_RefreshesData()
    {
        // Arrange
        var cache = CreateCache(ttlMs: 400); // 400ms TTL
        var entityId = Guid.NewGuid();
        var loadCount = 0;

        // Initial load
        await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => { loadCount++; return Task.FromResult(new TestEntity { Name = $"Data_{loadCount}" }); },
            syncValidateWithContext: (item, ctx) => ctx.ExpiryProgress < 0.75);

        Assert.Equal(1, loadCount);

        // Access immediately (should use cache)
        await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => { loadCount++; return Task.FromResult(new TestEntity { Name = $"Data_{loadCount}" }); },
            syncValidateWithContext: (item, ctx) => ctx.ExpiryProgress < 0.75);

        Assert.Equal(1, loadCount);

        // Wait until 75%+ progress (300ms+)
        await Task.Delay(320);

        // Access again (should trigger refresh)
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => { loadCount++; return Task.FromResult(new TestEntity { Name = $"Data_{loadCount}" }); },
            syncValidateWithContext: (item, ctx) => ctx.ExpiryProgress < 0.75);

        // Assert
        Assert.Equal(2, loadCount);
        Assert.Equal("Data_2", result?.Name);
    }

    #endregion

    #region Cache Miss Scenario Tests

    [Fact]
    public async Task LoadItem_CacheMiss_WithContextValidation_StillWorks()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var validationCount = 0;

        // No item in cache

        // Act
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "New" }); },
            syncValidateWithContext: (item, ctx) => { validationCount++; return true; });

        // Assert
        Assert.Equal("New", result?.Name);
        Assert.Equal(1, recoveryCount);
        Assert.Equal(0, validationCount); // Validation not called on miss
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task LoadItem_NullRecovery_WithContextValidation_ReturnsNull()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();

        // Act
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            null,
            syncValidateWithContext: (item, ctx) => true);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadItem_EmptyGuid_WithContextValidation_ReturnsNull()
    {
        // Arrange
        var cache = CreateCache();

        // Act
        var result = await cache.LoadItem<TestEntity>(
            Guid.Empty,
            "test",
            _ => Task.FromResult(new TestEntity { Name = "Test" }),
            syncValidateWithContext: (item, ctx) => true);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadItem_CacheDisabled_WithContextValidation_CallsRecovery()
    {
        // Arrange
        var cache = CreateCache();
        cache.EnableCache = false;
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var validationCount = 0;

        // Act
        var result = await cache.LoadItem<TestEntity>(
            entityId,
            "test",
            _ => { recoveryCount++; return Task.FromResult(new TestEntity { Name = "Direct" }); },
            syncValidateWithContext: (item, ctx) => { validationCount++; return true; });

        // Assert
        Assert.Equal("Direct", result?.Name);
        Assert.Equal(1, recoveryCount);
        Assert.Equal(0, validationCount);
    }

    #endregion

    private class TestEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
