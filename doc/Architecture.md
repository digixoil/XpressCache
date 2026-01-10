# XpressCache Architecture Guide

This document explains the internal architecture, design decisions, and implementation details of XpressCache.

## Table of Contents

- [Overview](#overview)
- [Core Components](#core-components)
- [Design Principles](#design-principles)
- [Data Structures](#data-structures)
- [Threading Model](#threading-model)
- [Cache Stampede Prevention](#cache-stampede-prevention)
- [Validation with Timing Context](#validation-with-timing-context)
- [Expiration Strategy](#expiration-strategy)
- [Memory Management](#memory-management)
- [Performance Optimizations](#performance-optimizations)

---

## Overview

XpressCache is a high-performance, thread-safe in-memory cache designed to solve the cache stampede (thundering herd) problem while maintaining excellent performance for concurrent workloads.

### Key Design Goals

1. **Zero-allocation cache hits** - Fast path should not allocate objects
2. **Thread-safe without global locks** - Per-key locking for scalability
3. **Prevent cache stampede** - Single-flight pattern for expensive operations
4. **Sliding expiration** - Keep frequently accessed items cached
5. **Automatic cleanup** - Prevent unbounded memory growth
6. **Timing-aware validation** - Enable sophisticated refresh strategies

---

## Core Components

### Component Diagram

```
┌───────────────────────────────────────────────┐
│           ICacheStore Interface               │
│  (Public API for cache operations)            │
└───────────────────────────────────────────────┘
                 │ implements
                 ▼
┌───────────────────────────────────────────────┐
│            CacheStore Class                   │
│  ┌────────────────────────────────────────┐   │
│  │  ConcurrentDictionary<CacheKey,        │   │
│  │                       CacheEntry>      │   │
│  │  (Main cache storage)                  │   │
│  └────────────────────────────────────────┘   │
│  ┌────────────────────────────────────────┐   │
│  │  KeyedAsyncLock<CacheKey>              │   │
│  │  (Per-key locking)                     │   │
│  └────────────────────────────────────────┘   │
└───────────────────────────────────────────────┘
         │                    │
         ▼                    ▼
┌───────────────────┐  ┌────────────────────┐
│   CacheKey        │  │   CacheEntry       │
│   (struct)        │  │   (sealed class)   │
│   - EntityId      │  │   - Data           │
│   - Type          │  │   - ExpiryTicks    │
│   - Subject       │  └────────────────────┘
└───────────────────┘           │
                                ▼
                   ┌────────────────────────┐
                   │ CacheValidationContext │
                   │   (readonly struct)    │
                   │   - ExpiryTicks        │
                   │   - CurrentTicks       │
                   │   - TtlMs              │
                   │   - TimeToExpiry       │
                   │   - Age                │
                   │   - ExpiryProgress     │
                   └────────────────────────┘
```

### CacheStore

The main implementation of `ICacheStore`. Coordinates all cache operations.

**Responsibilities:**
- Manage cache entries in a `ConcurrentDictionary`
- Coordinate single-flight locking via `KeyedAsyncLock`
- Handle expiration and renewal
- Trigger cleanup operations
- Provide timing context for validation

### CacheKey (struct)

Value-type composite key for cache lookups.

**Design:**
```csharp
readonly struct CacheKey : IEquatable<CacheKey>
{
    readonly Guid EntityId;
    readonly Type Type;
    readonly string Subject;
}
```

**Why struct?**
- No heap allocation for the key structure
- Better cache locality in `ConcurrentDictionary`
- Implements `IEquatable<T>` for fast equality checks
- Custom `GetHashCode()` for optimized hashing

**Type Identity:**
Uses direct `Type` reference rather than hash code to guarantee correct type identity and eliminate hash collision risks.

### CacheEntry (sealed class)

Immutable container for cached data with expiration time.

**Design:**
```csharp
sealed class CacheEntry
{
    readonly long ExpiryTicks;
    readonly object? Data;
}
```

**Why immutable?**
- Thread-safe reads without locking
- Atomic replacement via `ConcurrentDictionary.TryUpdate()`
- Simplifies reasoning about concurrency

### CacheValidationContext (readonly struct)

Value-type container for timing information during validation.

**Design:**
```csharp
readonly struct CacheValidationContext
{
    long ExpiryTicks;      // When the entry expires
    long CurrentTicks;     // Current time reference
    long TtlMs;            // Configured TTL
    TimeSpan TimeToExpiry; // Computed: time remaining
    TimeSpan Age;          // Computed: approximate age
    double ExpiryProgress; // Computed: 0.0 to 1.0
}
```

**Why provided to validation?**
- Enables proactive refresh strategies
- Allows time-based business logic
- Supports graceful cache warming patterns
- No additional lookups required during validation

### KeyedAsyncLock&lt;TKey&gt;

Provides per-key async locking for single-flight pattern.

**Design:**
- `ConcurrentDictionary<TKey, SemaphoreSlim>` for lock storage
- `SemaphoreSlim` for async-compatible waiting
- Automatic cleanup of unused semaphores

**Example flow:**
```
Thread A: Acquires lock for Key1
Thread B: Acquires lock for Key2 (parallel - different key)
Thread C: Waits for lock on Key1 (same key as A)

After A releases:
Thread C: Acquires lock for Key1
```

---

## Design Principles

### 1. Lock-Free Read Operations

**Principle:** Cache hits should not require locking.

**Implementation:**
- `ConcurrentDictionary` for lock-free reads
- Immutable `CacheEntry` values
- Atomic replacement on updates

**Benefit:** Maximum throughput for cache hits (common case)

### 2. Per-Key Locking (Not Global)

**Principle:** Only serialize operations for the same key.

**Implementation:**
- `KeyedAsyncLock` provides independent locks per cache key
- Operations on different keys proceed in parallel

**Benefit:** Horizontal scalability - locks don't become bottleneck

### 3. Value-Type Keys and Context

**Principle:** Avoid allocations for frequently-used structures.

**Implementation:**
- `CacheKey` is a `readonly struct`
- `CacheValidationContext` is a `readonly struct`
- Passed by value, no heap allocation

**Benefit:** Reduced GC pressure, better memory locality

### 4. Efficient Time Handling

**Principle:** Minimize overhead of time-based operations.

**Implementation:**
- `Environment.TickCount64` instead of `DateTime.Now`
- Millisecond precision sufficient for cache TTL
- No timezone/DST concerns

**Benefit:** Faster expiry checks, no syscalls

### 5. Lazy Cleanup

**Principle:** Don't pay cleanup cost unless necessary.

**Implementation:**
- Expired entries removed during access
- Probabilistic cleanup when size threshold exceeded
- Manual cleanup available but optional

**Benefit:** Amortized cleanup cost, no dedicated cleanup thread

### 6. Timing-Aware Validation

**Principle:** Provide rich context for validation decisions.

**Implementation:**
- `CacheValidationContext` with computed timing properties
- Enables proactive refresh patterns
- Supports complex staleness policies

**Benefit:** Fine-grained control over cache freshness

---

## Data Structures

### ConcurrentDictionary Layout

```
ConcurrentDictionary<CacheKey, CacheEntry>

Key Structure (value type - no heap allocation):
┌──────────────────────────────────────────┐
│ EntityId (Guid - 16 bytes)               │
│ Type (reference - 8 bytes)               │
│ Subject (string reference - 8 bytes)     │
└──────────────────────────────────────────┘
Total: 32 bytes on stack

Value Structure (reference type):
┌─────────────────────────────────────┐
│ CacheEntry (sealed class)           │
│ ├─ Object header (16 bytes)         │
│ ├─ ExpiryTicks (8 bytes)            │
│ └─ Data (8 bytes reference)         │
└─────────────────────────────────────┘
Total: ~32 bytes + data size

Dictionary overhead: ~16 bytes per entry
```

### CacheValidationContext Layout

```
CacheValidationContext (value type - stack allocated):
┌──────────────────────────────────────────┐
│ ExpiryTicks (long - 8 bytes)             │
│ CurrentTicks (long - 8 bytes)            │
│ TtlMs (long - 8 bytes)                   │
└──────────────────────────────────────────┘
Total: 24 bytes on stack

Computed properties (no storage):
- TimeToExpiry: calculated from ExpiryTicks - CurrentTicks
- Age: calculated from TtlMs - (ExpiryTicks - CurrentTicks)
- ExpiryProgress: calculated as Age / TtlMs
```

### Memory per Entry

**Base overhead:** ~48 bytes
- ConcurrentDictionary entry: ~16 bytes
- CacheEntry object: ~32 bytes

**Plus:**
- Cached data object size
- Subject string (if not empty)

**Example:**
```csharp
class User 
{
    public Guid Id;        // 16 bytes
    public string Name;    // 8 bytes (ref) + string data
}

Total cache memory for one User entry:
48 (overhead) + 24 (User object) + Name string ≈ 80+ bytes
```

---

## Threading Model

### Thread-Safety Guarantees

All public methods are thread-safe and can be called concurrently.

### Synchronization Strategy

**Read Path (Cache Hit):**
```
1. ConcurrentDictionary.TryGetValue() - lock-free
2. Check expiration - lock-free
3. Create CacheValidationContext - stack allocation only
4. Execute validation (sync) - lock-free
5. TryUpdate() for renewal - lock-free (may fail, that's OK)
6. Return data
```

**Write Path (Cache Miss with Stampede Prevention):**
```
1. ConcurrentDictionary.TryGetValue() - lock-free
2. Cache miss detected
3. Acquire per-key lock (KeyedAsyncLock)
4. Double-check cache - lock-free
5. If found, execute validation with context
6. If still miss, execute recovery
7. ConcurrentDictionary.AddOrUpdate() - lock-free
8. Release lock
```

**Write Path (Cache Miss without Stampede Prevention):**
```
1. ConcurrentDictionary.TryGetValue() - lock-free
2. Cache miss detected
3. Execute recovery (parallel with other callers)
4. ConcurrentDictionary.AddOrUpdate() - lock-free
```

### Lock Granularity

| Operation | Lock Level | Contention |
|-----------|------------|------------|
| Cache hit | None | None |
| Cache hit with sync validation | None | None |
| Cache hit with async validation | Per-key | Serialized per key |
| Cache miss (different keys) | Per-key | None |
| Cache miss (same key, stampede on) | Per-key | Serialized per key |
| Cache miss (same key, stampede off) | None | Parallel execution |
| SetItem | None | None (atomic update) |
| RemoveItem | None | None (atomic remove) |
| Clear | None | None (dictionary clear) |
| GetCachedItems | None | None (snapshot iteration) |

---

## Cache Stampede Prevention

### The Problem

```
Time  →  T0    T1    T2    T3    T4
         │     │     │     │     │
Req 1 ───┼─────────────────┼─────── DB Query
Req 2 ───┼─────────────────┼─────── DB Query (duplicate!)
Req 3 ───┼─────────────────┼─────── DB Query (duplicate!)
Req 4 ───┼─────────────────┼─────── DB Query (duplicate!)

All requests miss cache, all query database!
```

### The Solution: Single-Flight Pattern

```
Time  →  T0     T1    T2    T3    T4
         │      │     │     │     │
Req 1 ───┼ LOCK ├──── DB Query ───┼── Return
Req 2 ───┼ WAIT ├───────────┼─────┼── GET CACHED ── Return
Req 3 ───┼ WAIT ├───────────┼─────┼── GET CACHED ── Return
Req 4 ───┼ WAIT ├───────────┼─────┼── GET CACHED ── Return

Only Req 1 queries database!
```

### Implementation

**Double-Check Locking Pattern:**

```csharp
// 1. Fast check (no lock)
if (cache.TryGetValue(key, out entry))
    return entry.Data;

// 2. Acquire per-key lock
using (await keyedLock.AcquireAsync(key))
{
    // 3. Double-check (another thread may have populated)
    if (cache.TryGetValue(key, out entry))
        return entry.Data;
    
    // 4. Still miss - execute recovery
    var data = await recovery(id);
    
    // 5. Store and return
    cache.AddOrUpdate(key, new CacheEntry(data, ...));
    return data;
}
```

**Why double-check?**

Without the second check, all waiting threads would execute recovery after acquiring the lock, defeating the purpose!

### Per-Key Lock Cleanup

```csharp
// KeyedAsyncLock releases semaphore and cleans up
if (semaphore.CurrentCount == 1) // No waiters
{
    locks.TryRemove(key, out _); // Remove from dictionary
}
```

This prevents unbounded growth of the lock dictionary.

---

## Validation with Timing Context

### Purpose

The `CacheValidationContext` enables sophisticated validation strategies that consider not just the cached data, but also its temporal characteristics.

### Available Context

```csharp
public readonly struct CacheValidationContext
{
    // Raw timing data
    public long ExpiryTicks { get; }    // Absolute expiry time
    public long CurrentTicks { get; }   // Current time reference
    public long TtlMs { get; }          // Configured TTL
    
    // Computed helpers
    public TimeSpan TimeToExpiry { get; } // Time until expiry
    public TimeSpan Age { get; }          // Time since creation/renewal
    public double ExpiryProgress { get; } // 0.0 (fresh) to 1.0 (expired)
}
```

### Common Validation Patterns

**1. Proactive Refresh (Percentage-Based)**
```csharp
// Refresh when 75% of TTL has elapsed
syncValidateWithContext: (item, ctx) => ctx.ExpiryProgress < 0.75
```

**2. Proactive Refresh (Time-Based)**
```csharp
// Refresh items with less than 30 seconds remaining
syncValidateWithContext: (item, ctx) => ctx.TimeToExpiry.TotalSeconds > 30
```

**3. Age-Based Invalidation**
```csharp
// Refresh items older than 2 minutes regardless of TTL
syncValidateWithContext: (item, ctx) => ctx.Age.TotalMinutes < 2
```

**4. Conditional Async Validation**
```csharp
// Only perform expensive validation when near expiry
asyncValidateWithContext: async (item, ctx) =>
{
    if (ctx.ExpiryProgress > 0.8)
        return await ExpensiveValidationAsync(item);
    return true;
}
```

### Validation Flow

```
┌───────────────────────────────────────────────────┐
│               Cache Hit Detected                  │
└───────────────────────────────────────────────────┘
                        │
                        ▼
        ┌────────────────────────────────┐
        │  Create CacheValidationContext │
        │  (stack allocation, no heap)   │
        └────────────────────────────────┘
                        │
                        ▼
        ┌───────────────────────────────┐
        │   syncValidate(item, ctx)?    │───No──▶ Cache Hit, Return
        └───────────────────────────────┘
                        │ Yes (with context)
                        ▼
        ┌───────────────────────────────┐
        │     Validation Passes?        │
        └───────────────────────────────┘
               │                │
              Yes              No
               │                │
               ▼                ▼
        Cache Hit,      Remove from cache,
        Return          Execute recovery
```

---

## Expiration Strategy

### Sliding Expiration

Every cache hit extends the expiration time (best-effort).

```
Initial:   [Entry created] ──────────────▶ [Expires in 10 min]
                                      
Access 1:  [Entry hit at T+5] ───────────▶ [Expires in T+5+10 = 15 min]

Access 2:  [Entry hit at T+12] ──────────▶ [Expires in T+12+10 = 22 min]
```

### Best-Effort Renewal

Renewal uses optimistic concurrency:

```csharp
var renewedEntry = entry.Renew(currentTicks, ttl);
cache.TryUpdate(key, renewedEntry, entry); // May fail - that's OK!
```

**Why best-effort?**
- Under high contention, renewal may fail
- Entry expires slightly earlier than expected
- Acceptable for cache semantics
- Avoids expensive retry loops

### Expiration Check

```csharp
public bool IsExpired(long currentTicks)
    => currentTicks >= ExpiryTicks;
```

**Using `Environment.TickCount64`:**
- Much faster than `DateTime.Now` (no syscalls)
- Monotonic (doesn't go backwards)
- Millisecond precision (adequate for caching)
- No timezone/DST issues

---

## Memory Management

### Cleanup Strategies

#### 1. Lazy Cleanup (Access-Time)

Expired entries removed when accessed:

```csharp
if (entry.IsExpired(currentTicks))
{
    cache.TryRemove(key, out _);
    // Fall through to cache miss path
}
```

**Advantage:** Zero overhead if entries are accessed
**Limitation:** Entries never accessed stay in memory

#### 2. Probabilistic Cleanup (Read-Time)

Triggered when cache grows beyond threshold:

```csharp
if (cache.Count > threshold && Random(1, 100) == 1)
{
    ThreadPool.QueueUserWorkItem(_ => CleanupExpiredEntries());
}
```

**Advantage:** Amortized cleanup cost across many operations
**Configuration:** `ProbabilisticCleanupThreshold` option

#### 3. Manual Cleanup (Explicit)

Call `CleanupCache()` on a timer or reminder:

```csharp
public void CleanupCache()
{
    foreach (var kvp in cache)
    {
        if (kvp.Value.IsExpired(currentTicks))
            cache.TryRemove(kvp.Key, out _);
    }
}
```

**Advantage:** Predictable memory reclamation
**Cost:** O(n) scan of entire cache

### Memory Growth Bounds

**Without cleanup:**
```
Memory = (48 bytes + data size) × total entries ever cached
```

**With probabilistic cleanup:**
```
Memory ≈ (48 bytes + data size) × active entries
```

**Recommendation:**
- Set `ProbabilisticCleanupThreshold` to ~2× expected cache size
- Optionally run manual cleanup every 5-10 minutes

---

## Performance Optimizations

### 1. ValueTask for Cache Hits

```csharp
public ValueTask<T?> LoadItem<T>(...)
```

**Why?**
- Cache hits return synchronously (data already in memory)
- `ValueTask<T?>` avoids Task allocation
- Falls back to `Task<T>` only for cache misses

**Benchmark:**
```
Cache Hit with Task<T>:      45 ns/op, 40 B allocated
Cache Hit with ValueTask<T>: 12 ns/op,  0 B allocated
```

### 2. Struct-Based Keys and Context

```csharp
readonly struct CacheKey
readonly struct CacheValidationContext
```

**Why?**
- No heap allocation for these structures
- Better CPU cache locality
- Custom equality and hash code

**Benchmark:**
```
Tuple<Guid,Type,string> key: 72 B heap allocation per lookup
CacheKey struct:              0 B heap allocation per lookup
```

### 3. AggressiveInlining

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public bool IsExpired(long currentTicks)
```

**Applied to:**
- `CacheEntry.IsExpired()`
- `CacheEntry.Renew()`
- `CacheKey.Equals()`
- Cleanup trigger logic
- `CacheValidationContext` property getters

**Why?**
- Eliminates method call overhead
- Enables further JIT optimizations
- Critical for hot paths

### 4. Immutable Cache Entries

```csharp
sealed class CacheEntry
{
    readonly long ExpiryTicks;
    readonly object? Data;
}
```

**Why?**
- No locking needed for reads
- CPU can optimize reads (no memory barriers)
- Simplifies reasoning

### 5. ConcurrentDictionary Configuration

```csharp
new ConcurrentDictionary<CacheKey, CacheEntry>(
    concurrencyLevel: Environment.ProcessorCount * 2,
    capacity: InitialCapacity
)
```

**Why?**
- `concurrencyLevel` matches CPU cores
- `capacity` avoids initial rehashing
- Reduces lock contention in dictionary

---

## Scalability Characteristics

### Horizontal Scalability

**Reads (Cache Hits):**
- Linear scalability with CPU cores
- No locks, no contention
- Memory bandwidth is limit

**Writes (Cache Misses):**
- Scales with number of distinct keys
- Per-key serialization with stampede prevention
- Parallel execution for different keys

### Vertical Scalability

**Memory:**
- Bounded by `ProbabilisticCleanupThreshold`
- Cleanup prevents unbounded growth
- Linear with number of cached entries

**CPU:**
- Cleanup cost amortized across operations
- Lock acquisition/release minimal overhead
- No dedicated cleanup threads

---

## Comparison with Alternatives

### vs. IMemoryCache

| Feature | XpressCache | IMemoryCache |
|---------|-------------|--------------|
| Stampede prevention | ✅ Built-in | ❌ Manual |
| Thread-safe | ✅ Yes | ✅ Yes |
| Async operations | ✅ Full support | ⚠️ Limited |
| Per-key locking | ✅ Yes | ❌ No |
| Value-type keys | ✅ Yes | ❌ No |
| Validation with timing | ✅ Yes | ❌ No |
| Dependency injection | ✅ Yes | ✅ Yes |

### vs. ConcurrentDictionary

| Feature | XpressCache | ConcurrentDictionary |
|---------|-------------|---------------------|
| Expiration | ✅ Automatic | ❌ Manual |
| Stampede prevention | ✅ Built-in | ❌ Manual |
| Cleanup | ✅ Automatic | ❌ Manual |
| Async operations | ✅ Native | ❌ Sync only |
| Memory bounds | ✅ Configurable | ⚠️ Unbounded |
| Validation hooks | ✅ Yes | ❌ No |

### vs. Redis (Distributed Cache)

| Feature | XpressCache | Redis |
|---------|-------------|-------|
| Latency | ~10-100 ns | ~1-5 ms |
| Network | ✅ Not required | ❌ Required |
| Persistence | ❌ In-memory only | ✅ Yes |
| Distributed | ❌ No | ✅ Yes |
| Complexity | Low | High |

**Use XpressCache when:** You need in-process caching with stampede prevention  
**Use Redis when:** You need distributed caching across multiple servers

---

## Design Trade-offs

### Trade-off 1: Best-Effort Renewal

**Decision:** Entry renewal may fail under contention

**Rationale:**
- Simplifies code (no retry loops)
- Minimal impact (entry expires slightly earlier)
- Better performance (no lock contention)

**Alternative:** Lock-based guaranteed renewal
**Cost:** Requires locking on every cache hit

### Trade-off 2: Type-Based Keys

**Decision:** Cache key includes .NET `Type`

**Rationale:**
- Natural separation of different types
- Avoids key collisions between types
- Type-safe API

**Alternative:** String-based type names
**Cost:** String comparison slower than reference equality

### Trade-off 3: No Eviction Policy

**Decision:** Only time-based expiration, no LRU/LFU

**Rationale:**
- Simpler implementation
- Predictable behavior
- Lower overhead

**Alternative:** LRU eviction
**Cost:** Additional data structures, complexity, overhead

**Mitigation:** Use probabilistic cleanup and appropriate TTL

### Trade-off 4: Sync vs Async Validation

**Decision:** Separate sync and async validation overloads

**Rationale:**
- Sync validation stays in lock-free fast path
- Async validation only when truly needed
- Maximum performance for common cases

**Alternative:** Single async-only validation
**Cost:** Forces async machinery even for simple checks

---

## Future Enhancements

Potential improvements (not currently planned):

1. **Eviction Policies:**
   - LRU (Least Recently Used)
   - LFU (Least Frequently Used)
   - Size-based eviction

2. **Statistics/Metrics:**
   - Hit/miss ratio
   - Average load time
   - Cache size trending

3. **Partitioning:**
   - Multiple independent cache instances
   - Reduce contention on `ConcurrentDictionary`

4. **Serialization Support:**
   - Optional disk persistence
   - Snapshot/restore

5. **Conditional Updates:**
   - CAS (Compare-And-Swap) semantics
   - Optimistic concurrency for business logic

---

## See Also

- [API Reference](API-Reference.md)
- [Performance Guide](Performance-Guide.md)
- [Getting Started Tutorial](Getting-Started.md)
- [Testing Guide](Testing-Guide.md)
