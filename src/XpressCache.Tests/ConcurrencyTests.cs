using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

namespace XpressCache.Tests;

/// <summary>
/// Concurrency and stress tests for <see cref="CacheStore"/>.
/// </summary>
public class ConcurrencyTests
{
    private readonly ILogger<CacheStore> _logger;

    public ConcurrencyTests()
    {
        _logger = new NullLogger<CacheStore>();
    }

    private CacheStore CreateCache(CacheStoreOptions? options = null)
    {
        return new CacheStore(_logger, Options.Create(options ?? new CacheStoreOptions
        {
            ProbabilisticCleanupThreshold = 100000 // Disable probabilistic cleanup
        }));
    }

    [Fact]
    public async Task ConcurrentLoads_SameKey_SingleRecoveryExecution()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryExecutions = 0;
        var concurrentCallers = 50;
        var barrier = new Barrier(concurrentCallers);

        async Task<TestEntity> Recovery(Guid id)
        {
            Interlocked.Increment(ref recoveryExecutions);
            await Task.Delay(10); // Simulate work
            return new TestEntity { Id = id, Name = "Recovered" };
        }

        // Act - All callers start simultaneously
        var tasks = Enumerable.Range(0, concurrentCallers).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait(); // Synchronize start
            return await cache.LoadItem<TestEntity>(entityId, "subject", Recovery);
        })).ToList();

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, recoveryExecutions); // Only one recovery
        Assert.All(results, r => Assert.Equal("Recovered", r?.Name ?? string.Empty));
    }

    [Fact]
    public async Task ConcurrentLoads_DifferentKeys_ParallelRecovery()
    {
        // Arrange
        var cache = CreateCache();
        var keyCount = 20;
        var callsPerKey = 5;
        var recoveryByKey = new ConcurrentDictionary<Guid, int>();
        var barrier = new Barrier(keyCount * callsPerKey);

        var entityIds = Enumerable.Range(0, keyCount).Select(_ => Guid.NewGuid()).ToList();

        async Task<TestEntity> Recovery(Guid id)
        {
            recoveryByKey.AddOrUpdate(id, 1, (_, count) => count + 1);
            await Task.Delay(5);
            return new TestEntity { Id = id };
        }

        // Act
        var tasks = new List<Task<TestEntity?>>();
        foreach (var entityId in entityIds)
        {
            for (int i = 0; i < callsPerKey; i++)
            {
                var id = entityId;
                tasks.Add(Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    return await cache.LoadItem<TestEntity>(id, "subject", Recovery);
                }));
            }
        }

        await Task.WhenAll(tasks);

        // Assert - Each key should have exactly 1 recovery (single-flight)
        Assert.Equal(keyCount, recoveryByKey.Count);
        Assert.All(recoveryByKey.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task ConcurrentSetAndGet_NoDataCorruption()
    {
        // Arrange
        var cache = CreateCache();
        var iterations = 100;
        var entityId = Guid.NewGuid();

        // Act - Concurrent sets and gets
        var setTasks = Enumerable.Range(0, iterations).Select(i => Task.Run(async () =>
        {
            await cache.SetItem(entityId, "subject", new TestEntity { Name = $"Value{i}" });
        }));

        var getTasks = Enumerable.Range(0, iterations).Select(_ => Task.Run(async () =>
        {
            return await cache.LoadItem<TestEntity>(entityId, "subject", null);
        }));

        await Task.WhenAll(setTasks.Concat(getTasks));

        // Assert - Final value should be valid
        var finalResult = await cache.LoadItem<TestEntity>(entityId, "subject", null);
        Assert.NotNull(finalResult);
        Assert.StartsWith("Value", finalResult.Name);
    }

    [Fact]
    public async Task ConcurrentRemoveAndLoad_HandlesRaceCondition()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var iterations = 50;

        await cache.SetItem(entityId, "subject", new TestEntity { Name = "Initial" });

        // Act - Concurrent removes and loads
        var tasks = new List<Task>();

        for (int i = 0; i < iterations; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                cache.RemoveItem<TestEntity>(entityId, "subject");
            }));

            tasks.Add(Task.Run(async () =>
            {
                await cache.LoadItem<TestEntity>(entityId, "subject", 
                    _ => Task.FromResult(new TestEntity { Name = "Recovered" }));
            }));
        }

        // Should complete without exception
        await Task.WhenAll(tasks);

        // Assert - Cache should be in valid state
        var result = await cache.LoadItem<TestEntity>(entityId, "subject", null);
        // Result could be null or have a value, but no corruption
        Assert.True(result == null || result.Name != null);
    }

    [Fact]
    public async Task ConcurrentClear_WhileLoading_NoException()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();

        // Act
        var loadTask = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                await cache.LoadItem<TestEntity>(entityId, "subject",
                    _ => Task.FromResult(new TestEntity { Name = "Loaded" }));
                await Task.Delay(1);
            }
        });

        var clearTask = Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                cache.Clear();
                Thread.Sleep(5);
            }
        });

        // Should complete without exception
        await Task.WhenAll(loadTask, clearTask);

        // Assert - Just verify it completed
        Assert.True(true);
    }

    [Fact]
    public async Task StressTest_HighVolume_MaintainsPerformance()
    {
        // Arrange
        var cache = CreateCache();
        var operationCount = 1000;
        var keyCount = 50;
        var entityIds = Enumerable.Range(0, keyCount).Select(_ => Guid.NewGuid()).ToList();

        // Act
        var stopwatch = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, operationCount).Select(i => Task.Run(async () =>
        {
            var entityId = entityIds[i % keyCount];
            
            if (i % 3 == 0)
            {
                await cache.SetItem(entityId, "subject", new TestEntity { Name = $"Item{i}" });
            }
            else
            {
                await cache.LoadItem<TestEntity>(entityId, "subject",
                    _ => Task.FromResult(new TestEntity { Name = $"Recovered{i}" }));
            }
        }));

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        // Assert - Should complete in reasonable time (< 10 seconds)
        Assert.True(stopwatch.ElapsedMilliseconds < 10000, 
            $"Stress test took too long: {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ConcurrentCleanup_WhileOperating_NoException()
    {
        // Arrange
        var options = new CacheStoreOptions { DefaultTtlMs = 50 };
        var cache = CreateCache(options);
        var entityIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();

        // Act
        var operationTask = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                var id = entityIds[i % entityIds.Count];
                await cache.SetItem(id, "subject", new TestEntity { Name = $"Item{i}" });
                await Task.Delay(5);
            }
        });

        var cleanupTask = Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                cache.CleanupCache();
                Thread.Sleep(10);
            }
        });

        await Task.WhenAll(operationTask, cleanupTask);

        // Assert
        Assert.True(true); // Completed without exception
    }

    [Fact]
    public async Task SingleFlight_RecoveryException_DoesNotBlockOtherCallers()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var attemptCount = 0;

        async Task<TestEntity> Recovery(Guid id)
        {
            var attempt = Interlocked.Increment(ref attemptCount);
            if (attempt == 1)
            {
                await Task.Delay(10);
                throw new InvalidOperationException("First attempt failed");
            }
            return new TestEntity { Id = id, Name = "Success" };
        }

        // Act - First caller will fail
        var firstTask = cache.LoadItem<TestEntity>(entityId, "subject", Recovery);
        
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstTask.AsTask());

        // Second caller should be able to proceed
        var result = await cache.LoadItem<TestEntity>(entityId, "subject", Recovery);

        // Assert
        Assert.Equal("Success", result?.Name ?? string.Empty);
        Assert.Equal(2, attemptCount);
    }

    [Fact]
    public async Task MultipleSubjects_ConcurrentAccess_IsolatedCorrectly()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var subjects = new[] { "subject-a", "subject-b", "subject-c" };
        var resultsBySubject = new ConcurrentDictionary<string, string>();

        // Act
        var tasks = subjects.SelectMany(subject =>
            Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
            {
                var result = await cache.LoadItem<TestEntity>(entityId, subject,
                    _ => Task.FromResult(new TestEntity { Name = $"Value-{subject}" }));
                resultsBySubject.TryAdd($"{subject}-{Guid.NewGuid()}", result?.Name ?? string.Empty);
                return result;
            }))
        );

        var results = await Task.WhenAll(tasks);

        // Assert - Each subject should have consistent values
        foreach (var subject in subjects)
        {
            var subjectResults = results.Where(r => r?.Name == $"Value-{subject}").ToList();
            Assert.Equal(10, subjectResults.Count);
        }
    }

    private class TestEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
