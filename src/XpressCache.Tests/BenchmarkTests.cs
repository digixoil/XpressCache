using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace XpressCache.Tests;

/// <summary>
/// Performance and concurrency benchmark tests for <see cref="CacheStore"/>.
/// These tests verify performance characteristics and scalability.
/// Run separately from unit tests to avoid resource contention.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Baseline Values:</strong>
/// </para>
/// <para>
/// The baseline values in these tests are designed to be achievable on a wide range of hardware,
/// from typical CI servers to developer workstations. They are intentionally conservative to:
/// </para>
/// <list type="bullet">
///   <item>Prevent false positives in CI environments with shared resources</item>
///   <item>Account for garbage collection pauses</item>
///   <item>Allow for slower cloud VMs</item>
///   <item>Handle debug vs release builds gracefully</item>
/// </list>
/// <para>
/// For accurate performance measurements, use BenchmarkDotNet in a dedicated project.
/// </para>
/// </remarks>
[Trait("Category", "Performance")]
[Trait("Category", "Benchmark")]
public class BenchmarkTests
{
    private readonly ITestOutputHelper _output;
    private readonly NullLogger<CacheStore> _logger = new();

    public BenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private CacheStore CreateCache(CacheStoreOptions? options = null)
    {
        options ??= new CacheStoreOptions
        {
            ProbabilisticCleanupThreshold = 10000 // High threshold for benchmarks
        };
        return new CacheStore(_logger, Options.Create(options));
    }

    [Fact]
    [Trait("Speed", "Fast")]
    public async Task Benchmark_CacheHit_Performance()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        await cache.SetItem(entityId, "test", new BenchmarkEntity { Id = entityId });

        const int iterations = 10000;
        var sw = Stopwatch.StartNew();

        // Act
        for (int i = 0; i < iterations; i++)
        {
            var result = await cache.LoadItem<BenchmarkEntity>(entityId, "test", null);
            Assert.NotNull(result);
        }

        sw.Stop();

        // Report
        var avgNs = sw.Elapsed.TotalNanoseconds / iterations;
        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;

        _output.WriteLine($"Cache Hit Performance:");
        _output.WriteLine($"  Iterations: {iterations:N0}");
        _output.WriteLine($"  Total Time: {sw.ElapsedMilliseconds:N0} ms");
        _output.WriteLine($"  Avg Time: {avgNs:N1} ns/op");
        _output.WriteLine($"  Throughput: {opsPerSec:N0} ops/sec");

        // Assert - Should be fast (< 10 microseconds on any reasonable hardware)
        // Baseline is very conservative to handle CI environments and debug builds
        Assert.True(avgNs < 10_000, $"Cache hit too slow: {avgNs:N1} ns (expected < 10,000 ns)");
    }

    [Fact]
    [Trait("Speed", "Medium")]
    public async Task Benchmark_CacheMiss_WithStampedePrevention()
    {
        // Arrange
        var cache = CreateCache(new CacheStoreOptions
        {
            PreventCacheStampedeByDefault = true
        });

        const int iterations = 1000;
        var sw = Stopwatch.StartNew();

        // Act
        for (int i = 0; i < iterations; i++)
        {
            var id = Guid.NewGuid();
            var result = await cache.LoadItem<BenchmarkEntity>(
                id, "test",
                async entityId => new BenchmarkEntity { Id = entityId }
            );
            Assert.NotNull(result);
        }

        sw.Stop();

        // Report
        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;

        _output.WriteLine($"Cache Miss (with stampede prevention) Performance:");
        _output.WriteLine($"  Iterations: {iterations:N0}");
        _output.WriteLine($"  Total Time: {sw.ElapsedMilliseconds:N0} ms");
        _output.WriteLine($"  Avg Time: {avgMs:N3} ms/op");
        _output.WriteLine($"  Throughput: {opsPerSec:N0} ops/sec");

        // Assert - reasonable throughput (> 100 ops/sec)
        Assert.True(opsPerSec > 100, $"Cache miss throughput too low: {opsPerSec:N0} ops/sec");
    }

    [Fact]
    [Trait("Speed", "Medium")]
    public async Task Benchmark_ConcurrentCacheHits()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        await cache.SetItem(entityId, "test", new BenchmarkEntity { Id = entityId });

        const int threadCount = 10;
        const int iterationsPerThread = 1000;
        var sw = Stopwatch.StartNew();

        // Act
        var tasks = Enumerable.Range(0, threadCount).Select(async _ =>
        {
            for (int i = 0; i < iterationsPerThread; i++)
            {
                var result = await cache.LoadItem<BenchmarkEntity>(entityId, "test", null);
                Assert.NotNull(result);
            }
        });

        await Task.WhenAll(tasks);
        sw.Stop();

        // Report
        var totalOps = threadCount * iterationsPerThread;
        var opsPerSec = totalOps / sw.Elapsed.TotalSeconds;

        _output.WriteLine($"Concurrent Cache Hit Performance:");
        _output.WriteLine($"  Threads: {threadCount}");
        _output.WriteLine($"  Ops per Thread: {iterationsPerThread:N0}");
        _output.WriteLine($"  Total Ops: {totalOps:N0}");
        _output.WriteLine($"  Total Time: {sw.ElapsedMilliseconds:N0} ms");
        _output.WriteLine($"  Throughput: {opsPerSec:N0} ops/sec");

        // Assert - Should achieve at least 10,000 ops/sec even on slow CI
        // Conservative baseline to handle various CI environments
        Assert.True(opsPerSec > 10_000, $"Concurrent throughput too low: {opsPerSec:N0} ops/sec (expected > 10,000)");
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public async Task Benchmark_StampedePrevention_Effectiveness()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCallCount = 0;
        var recoveryDelay = TimeSpan.FromMilliseconds(50);

        async Task<BenchmarkEntity> SlowRecovery(Guid id)
        {
            Interlocked.Increment(ref recoveryCallCount);
            await Task.Delay(recoveryDelay);
            return new BenchmarkEntity { Id = id };
        }

        const int concurrentRequests = 50;
        var sw = Stopwatch.StartNew();

        // Act
        var tasks = Enumerable.Range(0, concurrentRequests).Select(_ =>
            cache.LoadItem<BenchmarkEntity>(
                entityId, "test", SlowRecovery,
                behavior: CacheLoadBehavior.PreventStampede
            ).AsTask()
        );

        await Task.WhenAll(tasks);
        sw.Stop();

        // Report
        _output.WriteLine($"Stampede Prevention Effectiveness:");
        _output.WriteLine($"  Concurrent Requests: {concurrentRequests}");
        _output.WriteLine($"  Recovery Calls: {recoveryCallCount}");
        _output.WriteLine($"  Total Time: {sw.ElapsedMilliseconds:N0} ms");
        _output.WriteLine($"  Expected Time (no prevention): ~{recoveryDelay.TotalMilliseconds * concurrentRequests:N0} ms");
        _output.WriteLine($"  Actual Time: {sw.ElapsedMilliseconds:N0} ms");

        // Assert - Single-flight should result in exactly 1 recovery call
        Assert.Equal(1, recoveryCallCount);
        
        // Should complete within reasonable time (3x recovery time for overhead)
        Assert.True(sw.ElapsedMilliseconds < recoveryDelay.TotalMilliseconds * 3,
            $"Took too long: {sw.ElapsedMilliseconds}ms (expected < {recoveryDelay.TotalMilliseconds * 3}ms)");
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public async Task Benchmark_ParallelLoad_Comparison()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCallCount = 0;

        async Task<BenchmarkEntity> Recovery(Guid id)
        {
            Interlocked.Increment(ref recoveryCallCount);
            await Task.Delay(10);
            return new BenchmarkEntity { Id = id };
        }

        const int concurrentRequests = 20;
        var sw = Stopwatch.StartNew();

        // Act
        var tasks = Enumerable.Range(0, concurrentRequests).Select(_ =>
            cache.LoadItem<BenchmarkEntity>(
                entityId, "test", Recovery,
                behavior: CacheLoadBehavior.AllowParallelLoad
            ).AsTask()
        );

        await Task.WhenAll(tasks);
        sw.Stop();

        // Report
        _output.WriteLine($"Parallel Load Performance:");
        _output.WriteLine($"  Concurrent Requests: {concurrentRequests}");
        _output.WriteLine($"  Recovery Calls: {recoveryCallCount}");
        _output.WriteLine($"  Total Time: {sw.ElapsedMilliseconds:N0} ms");

        // Assert
        Assert.Equal(concurrentRequests, recoveryCallCount);
        
        // With parallel execution, should complete within reasonable time
        // Allow very generous timeout (2 minutes) for CI/slow environments with high contention
        // In practice this should complete in ~10-100ms, but under extreme load it could take longer
        Assert.True(sw.ElapsedMilliseconds < 120_000,
            $"Parallel execution took too long: {sw.ElapsedMilliseconds} ms (expected < 120,000 ms)");
    }

    [Fact]
    [Trait("Speed", "Fast")]
    public async Task Benchmark_SetItem_Throughput()
    {
        // Arrange
        var cache = CreateCache();
        const int iterations = 10000;
        var sw = Stopwatch.StartNew();

        // Act
        for (int i = 0; i < iterations; i++)
        {
            var id = Guid.NewGuid();
            await cache.SetItem(id, "test", new BenchmarkEntity { Id = id });
        }

        sw.Stop();

        // Report
        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        var avgNs = sw.Elapsed.TotalNanoseconds / iterations;

        _output.WriteLine($"SetItem Throughput:");
        _output.WriteLine($"  Iterations: {iterations:N0}");
        _output.WriteLine($"  Total Time: {sw.ElapsedMilliseconds:N0} ms");
        _output.WriteLine($"  Avg Time: {avgNs:N1} ns/op");
        _output.WriteLine($"  Throughput: {opsPerSec:N0} ops/sec");

        // Assert - Should achieve at least 1,000 ops/sec
        Assert.True(opsPerSec > 1_000, $"SetItem throughput too low: {opsPerSec:N0} ops/sec");
    }

    [Fact]
    [Trait("Speed", "Fast")]
    public async Task Benchmark_RemoveItem_Throughput()
    {
        // Arrange
        var cache = CreateCache();
        const int iterations = 10000;
        var ids = new List<Guid>();

        // Pre-populate cache
        for (int i = 0; i < iterations; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await cache.SetItem(id, "test", new BenchmarkEntity { Id = id });
        }

        var sw = Stopwatch.StartNew();

        // Act
        foreach (var id in ids)
        {
            cache.RemoveItem<BenchmarkEntity>(id, "test");
        }

        sw.Stop();

        // Report
        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        var avgNs = sw.Elapsed.TotalNanoseconds / iterations;

        _output.WriteLine($"RemoveItem Throughput:");
        _output.WriteLine($"  Iterations: {iterations:N0}");
        _output.WriteLine($"  Total Time: {sw.ElapsedMilliseconds:N0} ms");
        _output.WriteLine($"  Avg Time: {avgNs:N1} ns/op");
        _output.WriteLine($"  Throughput: {opsPerSec:N0} ops/sec");

        // Assert - Should achieve at least 1,000 ops/sec
        Assert.True(opsPerSec > 1_000, $"RemoveItem throughput too low: {opsPerSec:N0} ops/sec");
    }

    [Fact]
    [Trait("Speed", "Medium")]
    public void Benchmark_CleanupCache_Performance()
    {
        // Arrange
        var cache = CreateCache(new CacheStoreOptions
        {
            DefaultTtlMs = 10 // Very short TTL for testing
        });

        const int itemCount = 10000;

        // Populate cache with items that will expire
        for (int i = 0; i < itemCount / 2; i++)
        {
            cache.SetItem(Guid.NewGuid(), "test",
                new BenchmarkEntity { Id = Guid.NewGuid() }).Wait();
        }

        // Wait for expiration
        Thread.Sleep(20);

        // Add fresh items
        for (int i = 0; i < itemCount / 2; i++)
        {
            cache.SetItem(Guid.NewGuid(), "test",
                new BenchmarkEntity { Id = Guid.NewGuid() }).Wait();
        }

        var sw = Stopwatch.StartNew();

        // Act
        cache.CleanupCache();

        sw.Stop();

        // Report
        _output.WriteLine($"CleanupCache Performance:");
        _output.WriteLine($"  Total Items: {itemCount:N0}");
        _output.WriteLine($"  Cleanup Time: {sw.ElapsedMilliseconds:N0} ms");
        _output.WriteLine($"  Time per Item: {sw.Elapsed.TotalMicroseconds / itemCount:N2} µs");

        // Verify cleanup worked
        var remainingItems = cache.GetCachedItems<BenchmarkEntity>("test");
        _output.WriteLine($"  Remaining Items: {remainingItems?.Count ?? 0}");

        Assert.NotNull(remainingItems);
        // Allow some margin for timing variations
        Assert.True(remainingItems.Count <= itemCount / 2 + 500,
            $"Should have removed most expired items, but {remainingItems.Count} remain");
        
        // Cleanup should complete within 5 seconds
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Cleanup took too long: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    [Trait("Speed", "Medium")]
    public async Task Benchmark_GetCachedItems_Performance()
    {
        // Arrange
        var cache = CreateCache();
        const int itemCount = 1000;

        // Populate cache
        for (int i = 0; i < itemCount; i++)
        {
            await cache.SetItem(Guid.NewGuid(), "test",
                new BenchmarkEntity { Id = Guid.NewGuid() });
        }

        var sw = Stopwatch.StartNew();

        // Act
        var items = cache.GetCachedItems<BenchmarkEntity>("test");

        sw.Stop();

        // Report
        _output.WriteLine($"GetCachedItems Performance:");
        _output.WriteLine($"  Cache Size: {itemCount:N0}");
        _output.WriteLine($"  Retrieved: {items?.Count ?? 0:N0}");
        _output.WriteLine($"  Total Time: {sw.ElapsedMilliseconds:N0} ms");
        _output.WriteLine($"  Time per Item: {sw.Elapsed.TotalMicroseconds / itemCount:N2} µs");

        // Assert
        Assert.NotNull(items);
        Assert.Equal(itemCount, items.Count);
        
        // Should complete within 1 second for 1000 items
        Assert.True(sw.ElapsedMilliseconds < 1_000,
            $"GetCachedItems took too long: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    [Trait("Speed", "Slow")]
    [Trait("Category", "Stress")]
    public async Task Benchmark_MixedWorkload()
    {
        // Arrange
        var cache = CreateCache();
        const int duration = 5000; // 5 seconds
        const int threadCount = 4;

        var operations = new long[threadCount];
        var cts = new CancellationTokenSource(duration);

        // Act - Mixed read/write workload
        var tasks = Enumerable.Range(0, threadCount).Select(async threadId =>
        {
            var rnd = new Random(threadId);
            var localOps = 0L;

            while (!cts.Token.IsCancellationRequested)
            {
                var id = Guid.NewGuid();
                var operation = rnd.Next(100);

                try
                {
                    if (operation < 70) // 70% reads
                    {
                        await cache.LoadItem<BenchmarkEntity>(
                            id, "test",
                            async _ => new BenchmarkEntity { Id = id }
                        );
                    }
                    else if (operation < 95) // 25% writes
                    {
                        await cache.SetItem(id, "test", new BenchmarkEntity { Id = id });
                    }
                    else // 5% deletes
                    {
                        cache.RemoveItem<BenchmarkEntity>(id, "test");
                    }

                    localOps++;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            operations[threadId] = localOps;
        });

        await Task.WhenAll(tasks);

        // Report
        var totalOps = operations.Sum();
        var opsPerSec = totalOps / (duration / 1000.0);

        _output.WriteLine($"Mixed Workload Performance:");
        _output.WriteLine($"  Duration: {duration:N0} ms");
        _output.WriteLine($"  Threads: {threadCount}");
        _output.WriteLine($"  Total Operations: {totalOps:N0}");
        _output.WriteLine($"  Throughput: {opsPerSec:N0} ops/sec");
        _output.WriteLine($"  Operations per thread:");
        for (int i = 0; i < threadCount; i++)
        {
            _output.WriteLine($"    Thread {i}: {operations[i]:N0}");
        }

        // Assert - Should achieve at least 100 ops/sec (very conservative)
        Assert.True(opsPerSec > 100, $"Mixed workload throughput too low: {opsPerSec:N0} ops/sec");
    }

    [Fact]
    [Trait("Speed", "Medium")]
    [Trait("Category", "Stress")]
    public async Task Benchmark_MemoryPressure_LargeCache()
    {
        // Arrange
        var cache = CreateCache();
        const int itemCount = 50000;

        var sw = Stopwatch.StartNew();
        var beforeMem = GC.GetTotalMemory(true);

        // Act - Populate large cache
        for (int i = 0; i < itemCount; i++)
        {
            await cache.SetItem(
                Guid.NewGuid(),
                "test",
                new BenchmarkEntity
                {
                    Id = Guid.NewGuid(),
                    Name = $"Entity_{i}",
                    Data = new byte[100] // Add some data
                }
            );
        }

        var afterMem = GC.GetTotalMemory(false);
        sw.Stop();

        // Report
        var memoryUsed = (afterMem - beforeMem) / (1024.0 * 1024.0);
        var memoryPerItem = (afterMem - beforeMem) / (double)itemCount;

        _output.WriteLine($"Large Cache Memory Pressure:");
        _output.WriteLine($"  Items: {itemCount:N0}");
        _output.WriteLine($"  Population Time: {sw.ElapsedMilliseconds:N0} ms");
        _output.WriteLine($"  Memory Used: {memoryUsed:N2} MB");
        _output.WriteLine($"  Memory per Item: {memoryPerItem:N0} bytes");

        // Assert - Should complete within 30 seconds
        Assert.True(sw.ElapsedMilliseconds < 30_000,
            $"Population took too long: {sw.ElapsedMilliseconds}ms");

        // Cleanup
        cache.CleanupCache();
        GC.Collect();
    }

    [Fact]
    [Trait("Speed", "Slow")]
    [Trait("Category", "Stress")]
    public async Task Stress_ThousandConcurrentMissesSameKey()
    {
        // Arrange
        var cache = CreateCache();
        var entityId = Guid.NewGuid();
        var recoveryCount = 0;
        var barrier = new TaskCompletionSource<bool>();

        async Task<BenchmarkEntity> Recovery(Guid id)
        {
            Interlocked.Increment(ref recoveryCount);
            await barrier.Task;
            return new BenchmarkEntity { Id = id };
        }

        const int concurrentCount = 1000;
        var sw = Stopwatch.StartNew();

        // Act - Fire off all requests
        var tasks = Enumerable.Range(0, concurrentCount).Select(_ =>
            cache.LoadItem<BenchmarkEntity>(
                entityId, "test", Recovery,
                behavior: CacheLoadBehavior.PreventStampede
            ).AsTask()
        ).ToArray();

        // Let first request acquire lock
        await Task.Delay(100);

        // Release all
        barrier.SetResult(true);

        var results = await Task.WhenAll(tasks);
        sw.Stop();

        // Report
        _output.WriteLine($"Stress Test - 1000 Concurrent Requests:");
        _output.WriteLine($"  Recovery Calls: {recoveryCount}");
        _output.WriteLine($"  Total Time: {sw.ElapsedMilliseconds:N0} ms");
        _output.WriteLine($"  All Results Valid: {results.All(r => r != null)}");

        // Assert
        Assert.Equal(1, recoveryCount);
        Assert.All(results, r => Assert.NotNull(r));
        
        // Should complete within 10 seconds
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"Stress test took too long: {sw.ElapsedMilliseconds}ms");
    }

    private class BenchmarkEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public byte[]? Data { get; set; }
    }
}
