using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace XpressCache.Tests;

/// <summary>
/// Unit tests for <see cref="CacheStore"/> single-flight behavior and <see cref="CacheLoadBehavior"/>.
/// </summary>
public class SingleFlightTests
{
    private readonly ILogger<CacheStore> _logger;

    public SingleFlightTests()
    {
        _logger = new NullLogger<CacheStore>();
    }

    private CacheStore CreateCache(bool preventStampedeByDefault = true)
    {
        var options = new CacheStoreOptions
        {
            PreventCacheStampedeByDefault = preventStampedeByDefault,
            ProbabilisticCleanupThreshold = 10000
        };
        return new CacheStore(_logger, Options.Create(options));
    }

    [Fact]
    public async Task LoadItem_ConcurrentRequests_ExecutesRecoveryOnlyOnce()
    {
        // Arrange
        var cache = CreateCache(preventStampedeByDefault: true);
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var recoveryStarted = new TaskCompletionSource<bool>();
        var allowRecoveryComplete = new TaskCompletionSource<bool>();

        async Task<TestEntity> SlowRecovery(Guid id)
        {
            Interlocked.Increment(ref recoveryCount);
            recoveryStarted.TrySetResult(true);
            await allowRecoveryComplete.Task;
            return new TestEntity { Id = id, Name = "Recovered" };
        }

        // Act - Start multiple concurrent requests
        var tasks = Enumerable.Range(0, 10).Select(_ =>
            cache.LoadItem<TestEntity>(entityId, "subject", SlowRecovery).AsTask()
        ).ToList();

        // Wait for first recovery to start
        await recoveryStarted.Task;
        
        // Small delay to ensure other tasks have attempted to acquire lock
        await Task.Delay(50);
        
        // Allow recovery to complete
        allowRecoveryComplete.SetResult(true);

        // Wait for all tasks
        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, recoveryCount); // Only one recovery executed
        Assert.All(results, r => Assert.Equal("Recovered", r?.Name ?? string.Empty));
    }

    [Fact]
    public async Task LoadItem_PreventStampedeExplicit_UsesLocking()
    {
        // Arrange - Default is OFF, but explicit override
        var cache = CreateCache(preventStampedeByDefault: false);
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var recoveryStarted = new TaskCompletionSource<bool>();
        var allowRecoveryComplete = new TaskCompletionSource<bool>();

        async Task<TestEntity> SlowRecovery(Guid id)
        {
            Interlocked.Increment(ref recoveryCount);
            recoveryStarted.TrySetResult(true);
            await allowRecoveryComplete.Task;
            return new TestEntity { Id = id, Name = "Recovered" };
        }

        // Act - Use explicit PreventStampede behavior
        var tasks = Enumerable.Range(0, 5).Select(_ =>
            cache.LoadItem<TestEntity>(
                entityId, 
                "subject", 
                SlowRecovery,
                behavior: CacheLoadBehavior.PreventStampede).AsTask()
        ).ToList();

        await recoveryStarted.Task;
        await Task.Delay(50);
        allowRecoveryComplete.SetResult(true);

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, recoveryCount); // Single-flight enforced
        Assert.All(results, r => Assert.Equal("Recovered", r?.Name ?? string.Empty));
    }

    [Fact]
    public async Task LoadItem_AllowParallelLoad_ExecutesMultipleRecoveries()
    {
        // Arrange
        var cache = CreateCache(preventStampedeByDefault: true);
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var allStarted = new CountdownEvent(5);
        var allowComplete = new TaskCompletionSource<bool>();

        async Task<TestEntity> SlowRecovery(Guid id)
        {
            Interlocked.Increment(ref recoveryCount);
            allStarted.Signal();
            await allowComplete.Task;
            return new TestEntity { Id = id, Name = "Recovered" };
        }

        // Act - Use AllowParallelLoad to bypass locking
        var tasks = Enumerable.Range(0, 5).Select(_ =>
            cache.LoadItem<TestEntity>(
                entityId,
                "subject",
                SlowRecovery,
                behavior: CacheLoadBehavior.AllowParallelLoad).AsTask()
        ).ToList();

        // Wait for all to start (proves parallel execution)
        allStarted.Wait(TimeSpan.FromSeconds(5));
        allowComplete.SetResult(true);

        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(5, recoveryCount); // All executed in parallel
    }

    [Fact]
    public async Task LoadItem_DefaultBehavior_UsesStoreWideSetting()
    {
        // Arrange - Store-wide prevention is ON
        var cache = CreateCache(preventStampedeByDefault: true);
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var barrier = new TaskCompletionSource<bool>();

        async Task<TestEntity> Recovery(Guid id)
        {
            Interlocked.Increment(ref recoveryCount);
            await barrier.Task;
            return new TestEntity { Id = id };
        }

        // Act - Use Default behavior (should inherit store setting)
        var task1 = cache.LoadItem<TestEntity>(entityId, "subject", Recovery, behavior: CacheLoadBehavior.Default).AsTask();
        await Task.Delay(50); // Let first acquire lock
        var task2 = cache.LoadItem<TestEntity>(entityId, "subject", Recovery, behavior: CacheLoadBehavior.Default).AsTask();
        
        barrier.SetResult(true);
        await Task.WhenAll(task1, task2);

        // Assert
        Assert.Equal(1, recoveryCount); // Single-flight used
    }

    [Fact]
    public async Task LoadItem_DefaultBehaviorWithPreventionOff_AllowsParallel()
    {
        // Arrange - Store-wide prevention is OFF
        var cache = CreateCache(preventStampedeByDefault: false);
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var allStarted = new CountdownEvent(3);
        var allowComplete = new TaskCompletionSource<bool>();

        async Task<TestEntity> Recovery(Guid id)
        {
            Interlocked.Increment(ref recoveryCount);
            allStarted.Signal();
            await allowComplete.Task;
            return new TestEntity { Id = id };
        }

        // Act
        var tasks = Enumerable.Range(0, 3).Select(_ =>
            cache.LoadItem<TestEntity>(entityId, "subject", Recovery, behavior: CacheLoadBehavior.Default).AsTask()
        ).ToList();

        allStarted.Wait(TimeSpan.FromSeconds(5));
        allowComplete.SetResult(true);

        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(3, recoveryCount); // Parallel execution
    }

    [Fact]
    public async Task LoadItem_DifferentKeys_ExecuteInParallel()
    {
        // Arrange
        var cache = CreateCache(preventStampedeByDefault: true);
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var allStarted = new CountdownEvent(2);
        var allowComplete = new TaskCompletionSource<bool>();

        async Task<TestEntity> Recovery(Guid id)
        {
            allStarted.Signal();
            await allowComplete.Task;
            return new TestEntity { Id = id, Name = id.ToString() };
        }

        // Act - Different keys should not block each other
        var task1 = cache.LoadItem<TestEntity>(id1, "subject", Recovery).AsTask();
        var task2 = cache.LoadItem<TestEntity>(id2, "subject", Recovery).AsTask();

        // Both should start (different keys)
        var started = allStarted.Wait(TimeSpan.FromSeconds(5));
        allowComplete.SetResult(true);

        var results = await Task.WhenAll(task1, task2);

        // Assert
        Assert.True(started); // Both started in parallel
        Assert.Equal(2, results.Length);
    }

    [Fact]
    public async Task LoadItem_SecondRequest_WaitsForFirstToComplete()
    {
        // Arrange
        var cache = CreateCache(preventStampedeByDefault: true);
        var entityId = Guid.NewGuid();
        var firstStarted = new TaskCompletionSource<bool>();
        var firstComplete = new TaskCompletionSource<bool>();
        var secondStarted = false;

        async Task<TestEntity> FirstRecovery(Guid id)
        {
            firstStarted.SetResult(true);
            await firstComplete.Task;
            return new TestEntity { Id = id, Name = "First" };
        }

        Task<TestEntity> SecondRecovery(Guid id)
        {
            secondStarted = true;
            return Task.FromResult(new TestEntity { Id = id, Name = "Second" });
        }

        // Act
        var task1 = cache.LoadItem<TestEntity>(entityId, "subject", FirstRecovery).AsTask();
        await firstStarted.Task;

        // Start second request while first is running
        var task2 = cache.LoadItem<TestEntity>(entityId, "subject", SecondRecovery).AsTask();
        
        await Task.Delay(50); // Give time for second to potentially start
        var secondStartedBeforeComplete = secondStarted;

        firstComplete.SetResult(true);
        
        var result1 = await task1;
        var result2 = await task2;

        // Assert
        Assert.False(secondStartedBeforeComplete); // Second didn't start until first completed
        Assert.Equal("First", result1?.Name ?? string.Empty);
        Assert.Equal("First", result2?.Name ?? string.Empty); // Second got cached result
    }

    [Fact]
    public async Task LoadItem_RecoveryThrows_PropagatesException()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.LoadItem<TestEntity>(entityId, "subject", _ =>
                throw new InvalidOperationException("Recovery failed"))
            .AsTask());
    }

    [Fact]
    public async Task LoadItem_RecoveryThrows_ReleasesLock()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var firstAttempt = true;

        async Task<TestEntity> Recovery(Guid id)
        {
            if (firstAttempt)
            {
                firstAttempt = false;
                throw new InvalidOperationException("First attempt failed");
            }
            return new TestEntity { Id = id, Name = "Success" };
        }

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.LoadItem<TestEntity>(entityId, "subject", Recovery).AsTask());

        // Second attempt should succeed (lock was released)
        var result = await cache.LoadItem<TestEntity>(entityId, "subject", Recovery);

        // Assert
        Assert.Equal("Success", result?.Name ?? string.Empty);
    }

    private class TestEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
