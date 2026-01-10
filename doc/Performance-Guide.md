# XpressCache Performance Guide

A comprehensive guide to optimizing performance when using XpressCache.

## Table of Contents

- [Performance Characteristics](#performance-characteristics)
- [Benchmarking Results](#benchmarking-results)
- [Configuration Tuning](#configuration-tuning)
- [Best Practices](#best-practices)
- [Validation Performance](#validation-performance)
- [Common Pitfalls](#common-pitfalls)
- [Monitoring and Diagnostics](#monitoring-and-diagnostics)
- [Optimization Checklist](#optimization-checklist)

---

## Performance Characteristics

### Operation Complexity

| Operation | Time Complexity | Lock Required | Allocations |
|-----------|----------------|---------------|-------------|
| Cache Hit | O(1) average | No | 0 (ValueTask) |
| Cache Hit + sync validation | O(1) average | No | 0 (context on stack) |
| Cache Hit + async validation | O(1) + validation | Per-key | Task allocation |
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
Cache Hit (found):                    10-50 ns
Cache Hit + sync validation:          15-60 ns (context creation ~5ns)
Cache Hit + async validation:         200-500 ns (includes lock)
Cache Hit (expired):                  50-100 ns (includes removal)
Cache Miss (single-flight):           Lock overhead ~200-500 ns + recovery time
Cache Miss (parallel):                ~100 ns + recovery time
SetItem:                              50-150 ns
RemoveItem:                           50-100 ns
GetCachedItems (100 items):           5-10 µs
CleanupCache (1000 items):            50-200 µs
```

**Factors affecting latency:**
- CPU speed and cache hierarchy
- Memory contention
- GC pressure
- Lock contention (for cache misses)
- Validation function complexity

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

### Cache Hit with Sync Validation

```csharp
[Benchmark]
public async ValueTask<User> CacheHitWithSyncValidation()
{
    return await _cache.LoadItem<User>(
        _userId, "users",
        cacheMissRecovery: null,
        syncValidate: (u) => u.IsActive
    );
}
```

**Results:**
```
Method                     | Mean     | Allocated |
---------------------------|----------|-----------|
CacheHitWithSyncValidation | 15.1 ns  | 0 B       |
```

### Cache Hit with Timing Context Validation

```csharp
[Benchmark]
public async ValueTask<User> CacheHitWithContextValidation()
{
    return await _cache.LoadItem<User>(
        _userId, "users",
        cacheMissRecovery: null,
        syncValidateWithContext: (u, ctx) => ctx.ExpiryProgress < 0.75
    );
}
```

**Results:**
```
Method                         | Mean     | Allocated |
-------------------------------|----------|-----------|
CacheHitWithContextValidation  | 18.2 ns  | 0 B       |
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

### PreventCacheStampedeByDefault

**Impact:** Controls default single-flight behavior

**Recommendations:**

```csharp
// Primarily expensive operations (database, APIs)
PreventCacheStampedeByDefault = true  // Default

// Primarily cheap, idempotent operations
PreventCacheStampedeByDefault = false
```

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
var hash = await cache.LoadItem<HashResult>(
    dataId, "hashes",
    async id => ComputeHash(data),
    behavior: CacheLoadBehavior.AllowParallelLoad
);
```

### 2. Prefer Sync Validation Over Async

**Good (fast path):**

```csharp
var user = await cache.LoadItem<User>(
    userId, "users", LoadUserAsync,
    syncValidate: (u) => u.IsActive  // No async overhead
);
```

**Avoid when possible:**

```csharp
// Only use async when truly needed (database checks, API calls)
var user = await cache.LoadItem<User>(
    userId, "users", LoadUserAsync,
    asyncValidate: async (u) => await IsUserValidAsync(u)  // Requires lock
);
```

### 3. Use Timing Context for Proactive Refresh

**Proactive refresh pattern:**

```csharp
var data = await cache.LoadItem<Data>(
    dataId, "data", LoadDataAsync,
    syncValidateWithContext: (data, ctx) =>
    {
        // Refresh when 80% of TTL has elapsed
        // This prevents mass expiration and spreads load
        return ctx.ExpiryProgress < 0.8;
    }
);
```

**Benefits:**
- Prevents thundering herd on TTL expiry
- Spreads refresh load over time
- Keeps cache warm proactively

### 4. Use Appropriate Subject Granularity

**Good:**

```csharp
// Logical grouping
await cache.LoadItem<User>(userId, "users", ...);
await cache.LoadItem<Product>(productId, "products", ...);
await cache.LoadItem<Order>(orderId, "orders", ...);
```

**Bad:**

```csharp
// Too granular
await cache.LoadItem<User>(userId, $"user-{userId}", ...);

// Too broad
await cache.LoadItem<User>(userId, "entities", ...);
```

### 5. Avoid GetCachedItems in Hot Paths

**Bad:**

```csharp
// Called on every request - O(n) scan each time!
public async Task<ActionResult> GetUsers()
{
    var users = cache.GetCachedItems<User>("users");
    return Ok(users);
}
```

**Good:**

```csharp
// Called only when needed
public async Task InvalidateUserCache()
{
    var users = cache.GetCachedItems<User>("users");
    foreach (var user in users ?? [])
    {
        cache.RemoveItem<User>(user.Id, "users");
    }
}
```

### 6. Handle Null Returns

```csharp
var user = await cache.LoadItem<User>(userId, "users", ...);
if (user == null)
{
    return NotFound();
}
```

---

## Validation Performance

### Sync vs Async Validation

| Validation Type | Lock Required | Fast Path | Use When |
|-----------------|---------------|-----------|----------|
| None | No | ✅ Yes | Simple caching, no staleness concerns |
| syncValidate | No | ✅ Yes | Simple property checks, computed validation |
| syncValidateWithContext | No | ✅ Yes | Time-based validation, proactive refresh |
| asyncValidate | Yes | ❌ No | Database/API checks required |
| asyncValidateWithContext | Yes | ❌ No | Time-based + external validation |

### Timing Context Overhead

The `CacheValidationContext` is a `readonly struct` allocated on the stack:

```
Context creation:    ~2-5 ns
Property access:     ~1-2 ns each
Total overhead:      ~5-10 ns per validation
```

This is negligible compared to cache hit time (~12 ns).

### Validation Best Practices

**1. Keep sync validation simple:**

```csharp
// Good - simple property check
syncValidate: (item) => item.IsValid

// Good - computed check
syncValidate: (item) => item.UpdatedAt > DateTime.UtcNow.AddMinutes(-5)

// Avoid - complex logic
syncValidate: (item) => ExpensiveValidation(item)  // Move to async if expensive
```

**2. Use timing context effectively:**

```csharp
// Proactive refresh at 75%
syncValidateWithContext: (item, ctx) => ctx.ExpiryProgress < 0.75

// Conditional async validation
asyncValidateWithContext: async (item, ctx) =>
{
    // Only perform expensive check near expiry
    if (ctx.ExpiryProgress > 0.9)
        return await ExpensiveCheckAsync(item);
    return true;
}
```

**3. Combine sync and async validation:**

```csharp
// Sync check first (fast), then async if needed
var item = await cache.LoadItem<Item>(
    id, "items", LoadItemAsync,
    syncValidateWithContext: (item, ctx) => 
        item.IsValid && ctx.ExpiryProgress < 0.9,  // Fast check
    asyncValidateWithContext: async (item, ctx) =>
        await ValidateWithExternalServiceAsync(item)  // Only if sync passes
);
```

---

## Common Pitfalls

### Pitfall 1: Using Async Validation Unnecessarily

**Problem:**

```csharp
// Forces lock acquisition even for simple validation
asyncValidate: async (item) =>
{
    await Task.CompletedTask;
    return item.IsValid;  // This is synchronous!
}
```

**Solution:**

```csharp
// Use sync validation for synchronous checks
syncValidate: (item) => item.IsValid
```

### Pitfall 2: Expensive Sync Validation

**Problem:**

```csharp
// Blocking I/O in sync validation - very bad!
syncValidate: (item) =>
{
    var result = httpClient.GetAsync(...).Result;  // DEADLOCK RISK!
    return result.IsValid;
}
```

**Solution:**

```csharp
// Use async validation for I/O
asyncValidate: async (item) =>
{
    var result = await httpClient.GetAsync(...);
    return result.IsValid;
}
```

### Pitfall 3: Ignoring Timing Context Opportunities

**Problem:**

```csharp
// Fixed validation, no proactive refresh
syncValidate: (item) => item.IsValid

// Results in thundering herd when multiple items expire together
```

**Solution:**

```csharp
// Spread refresh load with timing context
syncValidateWithContext: (item, ctx) =>
    item.IsValid && ctx.ExpiryProgress < 0.8
```

### Pitfall 4: Not Handling Validation Failures Gracefully

**Problem:**

```csharp
asyncValidate: async (item) =>
{
    // If external service is down, all cache reads fail!
    return await externalService.ValidateAsync(item);
}
```

**Solution:**

```csharp
asyncValidateWithContext: async (item, ctx) =>
{
    try
    {
        // Only check with external service if item is old
        if (ctx.ExpiryProgress > 0.9)
            return await externalService.ValidateAsync(item);
        return true;
    }
    catch (Exception)
    {
        // Graceful degradation - use cached value
        return ctx.ExpiryProgress < 0.95;
    }
}
```

### Pitfall 5: Over-validating Fresh Items

**Problem:**

```csharp
// Every cache hit does expensive validation
asyncValidate: async (item) =>
    await ExpensiveValidationAsync(item)  // Called on every hit!
```

**Solution:**

```csharp
// Only validate older items
asyncValidateWithContext: async (item, ctx) =>
{
    // Skip validation for fresh items
    if (ctx.ExpiryProgress < 0.5)
        return true;
    
    // Validate items in second half of TTL
    return await ExpensiveValidationAsync(item);
}
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
```

**Target:** > 80% for frequently accessed data

**2. Validation Rate**

Track how often validation fails to trigger refresh:

```csharp
var validationFailures = 0;

var item = await cache.LoadItem<Item>(
    id, "items", LoadItemAsync,
    syncValidateWithContext: (item, ctx) =>
    {
        var valid = ctx.ExpiryProgress < 0.8;
        if (!valid) Interlocked.Increment(ref validationFailures);
        return valid;
    });
```

**3. Average Load Time**

```csharp
var sw = Stopwatch.StartNew();
var item = await cache.LoadItem<Item>(id, "items", LoadItemAsync);
sw.Stop();
_metrics.RecordLoadTime(sw.ElapsedMilliseconds);
```

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

---

## Optimization Checklist

### Configuration

- [ ] Set `DefaultTtlMs` appropriate for data volatility
- [ ] Configure `InitialCapacity` to ~1.5× expected cache size
- [ ] Set `ProbabilisticCleanupThreshold` to ~2× typical cache size
- [ ] Enable `PreventCacheStampedeByDefault` for expensive operations

### Validation

- [ ] Use `syncValidate` instead of `asyncValidate` when possible
- [ ] Use `syncValidateWithContext` for time-based validation
- [ ] Implement proactive refresh (ExpiryProgress < 0.75-0.8)
- [ ] Handle validation failures gracefully
- [ ] Avoid expensive operations in sync validation

### Code

- [ ] Use `CacheLoadBehavior.PreventStampede` for database/API calls
- [ ] Use `CacheLoadBehavior.AllowParallelLoad` for cheap operations
- [ ] Avoid `GetCachedItems` in hot paths
- [ ] Handle null returns from `LoadItem`
- [ ] Use consistent subject strings

### Memory

- [ ] Avoid caching very large objects (> 10 MB)
- [ ] Set up periodic manual cleanup if needed
- [ ] Monitor cache size metrics

### Monitoring

- [ ] Track hit/miss rates
- [ ] Track validation failure rates
- [ ] Measure average load times
- [ ] Set up alerts for low hit rates

---

## Advanced Optimization Techniques

### 1. Tiered Validation

```csharp
public async Task<Data?> GetDataWithTieredValidationAsync(Guid dataId)
{
    return await _cache.LoadItem<Data>(
        dataId, "data", LoadDataAsync,
        syncValidateWithContext: (data, ctx) =>
        {
            // Tier 1: Always accept very fresh items
            if (ctx.ExpiryProgress < 0.25) return true;
            
            // Tier 2: Basic validation for moderately fresh items
            if (ctx.ExpiryProgress < 0.75) return data.IsValid;
            
            // Tier 3: Strict validation for older items (forces refresh)
            return false;
        });
}
```

### 2. Cache Warming with Timing

```csharp
public async Task WarmCacheAsync()
{
    var items = await GetFrequentlyAccessedItemsAsync();
    
    foreach (var item in items)
    {
        // Load with validation that only refreshes old items
        await _cache.LoadItem<Data>(
            item.Id, "data",
            async id => await LoadDataAsync(id),
            syncValidateWithContext: (data, ctx) =>
                ctx.Age.TotalMinutes < 5  // Keep items accessed within 5 min
        );
    }
}
```

### 3. Graceful Degradation

```csharp
public async Task<Data?> GetDataWithDegradationAsync(Guid dataId)
{
    return await _cache.LoadItem<Data>(
        dataId, "data",
        async id =>
        {
            try
            {
                return await LoadDataFromPrimaryAsync(id);
            }
            catch
            {
                // Fallback to secondary source
                return await LoadDataFromFallbackAsync(id);
            }
        },
        asyncValidateWithContext: async (data, ctx) =>
        {
            try
            {
                // Validate with external service
                if (ctx.ExpiryProgress > 0.8)
                    return await ValidateExternallyAsync(data);
                return true;
            }
            catch
            {
                // On validation failure, keep using cached data if not too old
                return ctx.ExpiryProgress < 0.95;
            }
        });
}
```

---

## See Also

- [API Reference](API-Reference.md)
- [Architecture Guide](Architecture.md)
- [Getting Started Tutorial](Getting-Started.md)
- [Testing Guide](Testing-Guide.md)
