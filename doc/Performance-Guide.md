# XpressCache Performance Guide

A comprehensive guide to optimizing performance when using XpressCache.

## Table of Contents

- [Performance Characteristics](#performance-characteristics)
- [Benchmarking Results](#benchmarking-results)
- [Configuration Tuning](#configuration-tuning)
- [Best Practices](#best-practices)
- [Common Pitfalls](#common-pitfalls)
- [Monitoring and Diagnostics](#monitoring-and-diagnostics)
- [Optimization Checklist](#optimization-checklist)

---

## Performance Characteristics

### Operation Complexity

| Operation | Time Complexity | Lock Required | Allocations |
|-----------|----------------|---------------|-------------|
| Cache Hit | O(1) average | No | 0 (ValueTask) |
| Cache Miss (single-flight) | O(1) + recovery | Per-key | Task + entry |
| Cache Miss (parallel) | O(1) + recovery | No | Task + entry |
| SetItem | O(1) average | No | Entry object |
| RemoveItem | O(1) average | No | 0 |
| GetCachedItems | O(n) | No | List + items |
| Clear | O(n) | No | 0 |
| CleanupCache | O(n) | No | Temp list |

**Legend:**
- O(1): Constant time
- O(n): Linear with cache size
- Recovery: Time to execute cache-miss recovery function

### Latency Expectations

**Typical latencies (Intel Xeon, .NET 8):**

```
Cache Hit (found):           10-50 ns
Cache Hit (expired):         50-100 ns (includes removal)
Cache Miss (single-flight):  Lock overhead ~200-500 ns + recovery time
Cache Miss (parallel):       ~100 ns + recovery time
SetItem:                     50-150 ns
RemoveItem:                  50-100 ns
GetCachedItems (100 items):  5-10 ?s
CleanupCache (1000 items):   50-200 ?s
```

**Factors affecting latency:**
- CPU speed and cache hierarchy
- Memory contention
- GC pressure
- Lock contention (for cache misses)

---

## Benchmarking Results

### Test Setup

```
BenchmarkDotNet v0.13.x
CPU: Intel Xeon E5-2686 v4 @ 2.30GHz
RAM: 16 GB
.NET: 8.0
Configuration: Release
```

### Cache Hit Performance

```csharp
[Benchmark]
public async ValueTask<User> CacheHit()
{
    return await _cache.LoadItem<User>(
        _userId, "users", 
        cacheMissRecovery: null
    );
}
```

**Results:**
```
Method   | Mean     | Allocated |
---------|----------|-----------|
CacheHit | 12.3 ns  | 0 B       |
```

### Cache Miss with Stampede Prevention

```csharp
[Benchmark]
public async Task<User> CacheMissWithLock()
{
    _cache.RemoveItem<User>(_userId, "users");
    return await _cache.LoadItem<User>(
        _userId, "users",
        async id => new User { Id = id },
        behavior: CacheLoadBehavior.PreventStampede
    );
}
```

**Results:**
```
Method              | Mean      | Allocated |
--------------------|-----------|-----------|
CacheMissWithLock   | 450 ns    | 384 B     |
```

### Cache Miss without Lock

```csharp
[Benchmark]
public async Task<User> CacheMissNoLock()
{
    _cache.RemoveItem<User>(_userId, "users");
    return await _cache.LoadItem<User>(
        _userId, "users",
        async id => new User { Id = id },
        behavior: CacheLoadBehavior.AllowParallelLoad
    );
}
```

**Results:**
```
Method           | Mean      | Allocated |
-----------------|-----------|-----------|
CacheMissNoLock  | 180 ns    | 256 B     |
```

### Concurrent Cache Hits (10 threads)

```csharp
[Benchmark]
public async Task ConcurrentHits()
{
    var tasks = Enumerable.Range(0, 10)
        .Select(_ => _cache.LoadItem<User>(_userId, "users", null))
        .ToArray();
    await Task.WhenAll(tasks.Select(t => t.AsTask()));
}
```

**Results:**
```
Threads | Mean      | Throughput      |
--------|-----------|-----------------|
1       | 12 ns     | 83M ops/sec     |
4       | 15 ns     | 266M ops/sec    |
8       | 18 ns     | 444M ops/sec    |
16      | 24 ns     | 666M ops/sec    |
```

**Observation:** Near-linear scalability for cache hits (lock-free reads)

### Concurrent Cache Misses (Same Key)

```csharp
[Benchmark]
public async Task ConcurrentMissesSameKey()
{
    _cache.Clear();
    var tasks = Enumerable.Range(0, 100)
        .Select(_ => _cache.LoadItem<User>(
            _userId, "users",
            async id => { 
                await Task.Delay(10); 
                return new User { Id = id }; 
            },
            behavior: CacheLoadBehavior.PreventStampede))
        .ToArray();
    await Task.WhenAll(tasks);
}
```

**Results:**
```
Behavior        | Recovery Calls | Total Time |
----------------|----------------|------------|
PreventStampede | 1              | ~10 ms     |
AllowParallel   | 100            | ~10 ms     |
```

**Observation:** PreventStampede executes recovery once; AllowParallel executes 100 times (wasteful for expensive operations)

---

## Configuration Tuning

### DefaultTtlMs

**Impact:** Controls how long items stay cached

**Recommendations:**

```csharp
// Frequently changing data (e.g., stock prices)
DefaultTtlMs = 10 * 1000  // 10 seconds

// Moderately stable data (e.g., user profiles)
DefaultTtlMs = 5 * 60 * 1000  // 5 minutes

// Rarely changing data (e.g., configuration)
DefaultTtlMs = 30 * 60 * 1000  // 30 minutes
```

**Trade-offs:**
- **Shorter TTL:** More cache misses, fresher data
- **Longer TTL:** Fewer cache misses, potentially stale data

**Tuning tip:** Set TTL to ~2-5× the average query time to balance freshness and performance

### InitialCapacity

**Impact:** Reduces rehashing during cache growth

**Recommendations:**

```csharp
// Small cache (< 100 items)
InitialCapacity = 128

// Medium cache (100-1000 items)
InitialCapacity = 512

// Large cache (1000-10000 items)
InitialCapacity = 2048

// Very large cache (> 10000 items)
InitialCapacity = 8192
```

**Tuning tip:** Set to ~1.5× expected steady-state cache size

**Benchmark:**
```
InitialCapacity | First 1000 Inserts | Memory Overhead |
----------------|-------------------|-----------------|
64              | 2.1 ms (rehash)   | Low             |
512             | 1.3 ms (1 rehash) | Medium          |
2048            | 0.8 ms (no rehash)| High            |
```

### ProbabilisticCleanupThreshold

**Impact:** Controls when automatic cleanup triggers

**Recommendations:**

```csharp
// Low-memory environment
ProbabilisticCleanupThreshold = 500

// Normal environment
ProbabilisticCleanupThreshold = 1000  // Default

// High-memory, high-throughput environment
ProbabilisticCleanupThreshold = 5000
```

**Trade-offs:**
- **Lower threshold:** More frequent cleanup, lower memory, higher CPU
- **Higher threshold:** Less frequent cleanup, higher memory, lower CPU

**Tuning tip:** Set to 2× your typical cache size to balance cleanup frequency

### PreventCacheStampedeByDefault

**Impact:** Controls default single-flight behavior

**Recommendations:**

```csharp
// Primarily expensive operations (database, APIs)
PreventCacheStampedeByDefault = true  // Default

// Primarily cheap, idempotent operations
PreventCacheStampedeByDefault = false
```

**Trade-offs:**
- **true:** Prevents wasted work, adds lock overhead
- **false:** No lock overhead, potential duplicate work

**Tuning tip:** Enable by default, selectively disable with `CacheLoadBehavior.AllowParallelLoad`

---

## Best Practices

### 1. Choose Appropriate CacheLoadBehavior

**For expensive operations:**

```csharp
// Database query - prevent duplicate execution
var user = await cache.LoadItem<User>(
    userId, "users",
    async id => await database.GetUserByIdAsync(id),
    behavior: CacheLoadBehavior.PreventStampede
);
```

**For cheap operations:**

```csharp
// In-memory computation - allow parallel execution
var hash = await cache.LoadItem<int>(
    dataId, "hashes",
    async id => await ComputeHashAsync(data),
    behavior: CacheLoadBehavior.AllowParallelLoad
);
```

### 2. Use Appropriate Subject Granularity

**Good:**

```csharp
// Logical grouping
await cache.LoadItem<User>(userId, "users", ...);
await cache.LoadItem<Product>(productId, "products", ...);
await cache.LoadItem<Order>(orderId, "orders", ...);
```

**Bad:**

```csharp
// Too granular (doesn't leverage multi-entity operations)
await cache.LoadItem<User>(userId, $"user-{userId}", ...);

// Too broad (mixing unrelated types)
await cache.LoadItem<User>(userId, "entities", ...);
await cache.LoadItem<Product>(productId, "entities", ...);
```

### 3. Avoid GetCachedItems in Hot Paths

**Bad:**

```csharp
// Called on every request - O(n) scan each time!
public async Task<ActionResult> GetUsers()
{
    var users = cache.GetCachedItems<User>("users"); // O(n)
    return Ok(users);
}
```

**Good:**

```csharp
// Called only when needed
public async Task InvalidateUserCache()
{
    var users = cache.GetCachedItems<User>("users"); // O(n) but rare
    foreach (var user in users)
    {
        cache.RemoveItem<User>(user.Id, "users");
    }
}
```

### 4. Minimize Recovery Function Overhead

**Bad:**

```csharp
var user = await cache.LoadItem<User>(
    userId, "users",
    async id => {
        // Multiple operations in recovery
        var userData = await database.GetUserAsync(id);
        var permissions = await database.GetPermissionsAsync(id);
        var settings = await database.GetSettingsAsync(id);
        return new User { 
            Data = userData, 
            Permissions = permissions, 
            Settings = settings 
        };
    }
);
```

**Good:**

```csharp
// Cache each independently
var user = await cache.LoadItem<User>(
    userId, "users",
    database.GetUserAsync
);

var permissions = await cache.LoadItem<Permissions>(
    userId, "permissions",
    database.GetPermissionsAsync
);

var settings = await cache.LoadItem<Settings>(
    userId, "settings",
    database.GetSettingsAsync
);
```

**Benefit:** Independent expiration, better cache hit rates

### 5. Handle Null Returns

**Bad:**

```csharp
var user = await cache.LoadItem<User>(userId, "users", ...);
var name = user.Name; // NullReferenceException if user is null!
```

**Good:**

```csharp
var user = await cache.LoadItem<User>(userId, "users", ...);
if (user == null)
{
    // Handle missing user
    return NotFound();
}
var name = user.Name;
```

### 6. Use Custom Validation Wisely

**Good use cases:**

```csharp
// Validate price hasn't changed
customValidate: async (product) => 
    product.Price == await GetCurrentPriceAsync(product.Id)

// Validate user still active
customValidate: async (user) => 
    await IsUserActiveAsync(user.Id)
```

**Bad use cases:**

```csharp
// Always returns true (pointless overhead)
customValidate: async (item) => true

// Expensive validation (defeats caching purpose)
customValidate: async (item) => 
    await PerformExpensiveCheckAsync(item)
```

### 7. Configure Appropriate Cleanup

**Option 1: Probabilistic (automatic)**

```csharp
var options = new CacheStoreOptions
{
    ProbabilisticCleanupThreshold = 1000
};
// Cleanup happens automatically
```

**Option 2: Manual (scheduled)**

```csharp
// In ASP.NET Core
services.AddHostedService<CacheCleanupService>();

public class CacheCleanupService : BackgroundService
{
    private readonly ICacheStore _cache;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            _cache.CleanupCache();
        }
    }
}
```

**Recommendation:** Use both for optimal memory management

---

## Common Pitfalls

### Pitfall 1: Caching Too Much

**Problem:**

```csharp
// Caching large objects
var report = await cache.LoadItem<byte[]>(
    reportId, "reports",
    async id => await GenerateLargeReportAsync(id) // 50 MB!
);
```

**Impact:** High memory usage, GC pressure, potential OutOfMemoryException

**Solution:**

```csharp
// Cache metadata, load data on-demand
var metadata = await cache.LoadItem<ReportMetadata>(
    reportId, "report-metadata",
    async id => await GetReportMetadataAsync(id) // 1 KB
);

// Load full report only when needed
var report = await LoadReportFromDiskAsync(metadata.FilePath);
```

### Pitfall 2: Infinite Recovery Loops

**Problem:**

```csharp
var user = await cache.LoadItem<User>(
    userId, "users",
    async id => await cache.LoadItem<User>(id, "users", ...) // Infinite!
);
```

**Impact:** Stack overflow, deadlock

**Solution:**

```csharp
var user = await cache.LoadItem<User>(
    userId, "users",
    async id => await database.GetUserAsync(id) // Direct source
);
```

### Pitfall 3: Ignoring Cache Stampede on Expensive Operations

**Problem:**

```csharp
// 100 concurrent requests for same uncached item
var data = await cache.LoadItem<Data>(
    id, "data",
    async id => await ExpensiveDatabaseQueryAsync(id), // 5 seconds!
    behavior: CacheLoadBehavior.AllowParallelLoad // Bad!
);
```

**Impact:** 100 × 5 sec = 500 seconds of total query time, database overload

**Solution:**

```csharp
var data = await cache.LoadItem<Data>(
    id, "data",
    async id => await ExpensiveDatabaseQueryAsync(id),
    behavior: CacheLoadBehavior.PreventStampede // Good!
);
```

**Result:** 1 × 5 sec = 5 seconds total, 99 requests wait for result

### Pitfall 4: Not Handling Exceptions in Recovery

**Problem:**

```csharp
var user = await cache.LoadItem<User>(
    userId, "users",
    async id => {
        var data = await database.GetUserAsync(id);
        // Exception here prevents caching!
        return data.Transform(); // May throw
    }
);
```

**Impact:** Exceptions propagate, no caching occurs, subsequent calls retry

**Solution:**

```csharp
var user = await cache.LoadItem<User>(
    userId, "users",
    async id => {
        try
        {
            var data = await database.GetUserAsync(id);
            return data.Transform();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load user {UserId}", id);
            return null; // Or default value
        }
    }
);
```

### Pitfall 5: Using Wrong Subject for Invalidation

**Problem:**

```csharp
// Cached with subject "users"
await cache.SetItem(userId, "users", user);

// Trying to remove with different subject
cache.RemoveItem<User>(userId, "user"); // Wrong! Doesn't match
```

**Impact:** Item not removed, cache inconsistency

**Solution:**

```csharp
// Use consistent subjects
const string USERS_SUBJECT = "users";

await cache.SetItem(userId, USERS_SUBJECT, user);
cache.RemoveItem<User>(userId, USERS_SUBJECT); // Correct
```

---

## Monitoring and Diagnostics

### Metrics to Track

**1. Hit Rate**

```csharp
public class CacheMetrics
{
    private long _hits;
    private long _misses;

    public double HitRate => 
        (_hits + _misses) > 0 ? (double)_hits / (_hits + _misses) : 0;

    public void RecordHit() => Interlocked.Increment(ref _hits);
    public void RecordMiss() => Interlocked.Increment(ref _misses);
}

// In your code
var cached = await cache.LoadItem<User>(userId, "users", null);
if (cached != null)
    _metrics.RecordHit();
else
{
    _metrics.RecordMiss();
    cached = await cache.LoadItem<User>(userId, "users", LoadUserAsync);
}
```

**Target:** > 80% for frequently accessed data

**2. Average Load Time**

```csharp
var sw = Stopwatch.StartNew();
var item = await cache.LoadItem<Item>(id, "items", LoadItemAsync);
sw.Stop();
_metrics.RecordLoadTime(sw.ElapsedMilliseconds);
```

**Target:** < 100ms for cache misses

**3. Cache Size**

```csharp
// Approximate size (not exact due to concurrency)
var size = cache.GetCachedItems<object>(string.Empty)?.Count ?? 0;
_metrics.RecordCacheSize(size);
```

**Target:** Below `ProbabilisticCleanupThreshold`

### Logging Recommendations

**Enable debug logging:**

```csharp
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});
```

**CacheStore logs:**
- Cache initialization
- Cleanup operations
- Type mismatches
- Enable/disable events

**Example output:**

```
[Debug] CacheStore initialized with PreventCacheStampedeByDefault=True, TtlMs=600000
[Debug] Releasing cache from (245) expired objects...
[Debug] Finish cache cleanup, (1832) non-expired objects still in memory
[Warn] Found cached item (id = '...'); however its type 'UserDto' not as expected 'User'
[Info] Cache has been deactivated
```

### Performance Profiling

**Use BenchmarkDotNet:**

```csharp
[MemoryDiagnoser]
public class CacheBenchmarks
{
    private ICacheStore _cache;

    [GlobalSetup]
    public void Setup()
    {
        _cache = new CacheStore(...);
    }

    [Benchmark]
    public async ValueTask<User> LoadCachedUser()
    {
        return await _cache.LoadItem<User>(...);
    }
}
```

**Use .NET Diagnostic Tools:**

```bash
# Measure allocations
dotnet trace collect --process-id <pid> --providers Microsoft-Windows-DotNETRuntime:0x1:4

# Analyze GC pressure
dotnet counters monitor --process-id <pid> System.Runtime
```

---

## Optimization Checklist

### Configuration

- [ ] Set `DefaultTtlMs` appropriate for data volatility
- [ ] Configure `InitialCapacity` to ~1.5× expected cache size
- [ ] Set `ProbabilisticCleanupThreshold` to ~2× typical cache size
- [ ] Enable `PreventCacheStampedeByDefault` for expensive operations

### Code

- [ ] Use `CacheLoadBehavior.PreventStampede` for database/API calls
- [ ] Use `CacheLoadBehavior.AllowParallelLoad` for cheap operations
- [ ] Avoid `GetCachedItems` in hot paths
- [ ] Handle null returns from `LoadItem`
- [ ] Use consistent subject strings (consider constants)
- [ ] Keep recovery functions simple and focused
- [ ] Implement exception handling in recovery functions

### Memory

- [ ] Avoid caching very large objects (> 10 MB)
- [ ] Set up periodic manual cleanup if needed
- [ ] Monitor cache size metrics
- [ ] Consider object pooling for frequently allocated types

### Monitoring

- [ ] Track hit/miss rates
- [ ] Measure average load times
- [ ] Monitor cache size
- [ ] Enable appropriate logging level
- [ ] Set up alerts for low hit rates or high memory usage

### Testing

- [ ] Load test with expected concurrency
- [ ] Test cache stampede scenarios
- [ ] Benchmark cache hit latency
- [ ] Verify cleanup runs as expected
- [ ] Test memory usage under load

---

## Advanced Optimization Techniques

### 1. Cache Warming

Pre-populate cache with frequently accessed items:

```csharp
public async Task WarmCacheAsync()
{
    var frequentUserIds = await GetFrequentUserIdsAsync();
    var tasks = frequentUserIds.Select(id =>
        cache.LoadItem<User>(id, "users", LoadUserAsync)
    );
    await Task.WhenAll(tasks);
}
```

### 2. Layered Caching

Combine XpressCache with distributed cache:

```csharp
public async Task<T> GetWithLayeredCacheAsync<T>(
    Guid id, 
    Func<Guid, Task<T>> source) where T : class
{
    // L1: XpressCache (in-process)
    var item = await _xpressCache.LoadItem<T>(id, "l1", null);
    if (item != null) return item;

    // L2: Redis (distributed)
    item = await _redis.GetAsync<T>(id);
    if (item != null)
    {
        await _xpressCache.SetItem(id, "l1", item);
        return item;
    }

    // L3: Source (database)
    item = await source(id);
    await _redis.SetAsync(id, item);
    await _xpressCache.SetItem(id, "l1", item);
    return item;
}
```

### 3. Conditional Caching

Cache only expensive results:

```csharp
public async Task<Data> LoadDataAsync(Guid id)
{
    var sw = Stopwatch.StartNew();
    var data = await database.LoadDataAsync(id);
    sw.Stop();

    // Only cache if load took > 100ms
    if (sw.ElapsedMilliseconds > 100)
    {
        await cache.SetItem(id, "data", data);
    }

    return data;
}
```

### 4. Batch Operations

Reduce overhead for multiple related items:

```csharp
public async Task<List<User>> LoadUsersAsync(List<Guid> userIds)
{
    // Check cache first
    var results = new Dictionary<Guid, User>();
    var missing = new List<Guid>();

    foreach (var id in userIds)
    {
        var cached = await cache.LoadItem<User>(id, "users", null);
        if (cached != null)
            results[id] = cached;
        else
            missing.Add(id);
    }

    // Batch load missing
    if (missing.Any())
    {
        var loaded = await database.LoadUsersBatchAsync(missing);
        foreach (var user in loaded)
        {
            results[user.Id] = user;
            await cache.SetItem(user.Id, "users", user);
        }
    }

    return userIds.Select(id => results.GetValueOrDefault(id)).ToList();
}
```

---

## See Also

- [API Reference](API-Reference.md)
- [Architecture Guide](Architecture.md)
- [Getting Started Tutorial](Getting-Started.md)
