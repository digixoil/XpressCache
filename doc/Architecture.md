# XpressCache Architecture Guide

This document explains the internal architecture, design decisions, and implementation details of XpressCache.

## Table of Contents

- [Overview](#overview)
- [Core Components](#core-components)
- [Design Principles](#design-principles)
- [Data Structures](#data-structures)
- [Threading Model](#threading-model)
- [Cache Stampede Prevention](#cache-stampede-prevention)
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

---

## Core Components

### Component Diagram

```
???????????????????????????????????????????????
?           ICacheStore Interface             ?
?  (Public API for cache operations)          ?
???????????????????????????????????????????????
                 ? implements
                 ?
???????????????????????????????????????????????
?            CacheStore Class                 ?
?  ????????????????????????????????????????   ?
?  ?  ConcurrentDictionary<CacheKey,      ?   ?
?  ?                       CacheEntry>    ?   ?
?  ?  (Main cache storage)                ?   ?
?  ????????????????????????????????????????   ?
?  ????????????????????????????????????????   ?
?  ?  KeyedAsyncLock<CacheKey>            ?   ?
?  ?  (Per-key locking)                   ?   ?
?  ????????????????????????????????????????   ?
???????????????????????????????????????????????
         ?                    ?
         ?                    ?
         ?                    ?
???????????????????  ????????????????????
?   CacheKey      ?  ?   CacheEntry     ?
?   (struct)      ?  ?   (sealed class) ?
?   - EntityId    ?  ?   - Data         ?
?   - Type        ?  ?   - ExpiryTicks  ?
?   - Subject     ?  ????????????????????
???????????????????
```

### CacheStore

The main implementation of `ICacheStore`. Coordinates all cache operations.

**Responsibilities:**
- Manage cache entries in a `ConcurrentDictionary`
- Coordinate single-flight locking via `KeyedAsyncLock`
- Handle expiration and renewal
- Trigger cleanup operations

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
    readonly object Data;
}
```

**Why immutable?**
- Thread-safe reads without locking
- Atomic replacement via `ConcurrentDictionary.TryUpdate()`
- Simplifies reasoning about concurrency

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

### 3. Value-Type Keys

**Principle:** Avoid allocations for cache key structures.

**Implementation:**
- `CacheKey` is a `readonly struct`
- Passed by reference in `ConcurrentDictionary`

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

**Benefit:** Amortized cleanup cost, no dedicated cleanup thread needed

---

## Data Structures

### ConcurrentDictionary Layout

```
ConcurrentDictionary<CacheKey, CacheEntry>

Key Structure (value type - no heap allocation):
????????????????????????????????????????
? EntityId (Guid - 16 bytes)           ?
? Type (reference - 8 bytes)           ?
? Subject (string reference - 8 bytes) ?
????????????????????????????????????????
Total: 32 bytes on stack

Value Structure (reference type):
???????????????????????????????????
? CacheEntry (sealed class)       ?
? ?? Object header (16 bytes)     ?
? ?? ExpiryTicks (8 bytes)        ?
? ?? Data (8 bytes reference)     ?
???????????????????????????????????
Total: ~32 bytes + data size

Dictionary overhead: ~16 bytes per entry
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
48 (overhead) + 24 (User object) + Name string ? 80+ bytes
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
3. TryUpdate() for renewal - lock-free (may fail, that's OK)
4. Return data
```

**Write Path (Cache Miss with Stampede Prevention):**
```
1. ConcurrentDictionary.TryGetValue() - lock-free
2. Cache miss detected
3. Acquire per-key lock (KeyedAsyncLock)
4. Double-check cache - lock-free
5. If still miss, execute recovery
6. ConcurrentDictionary.AddOrUpdate() - lock-free
7. Release lock
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
Time  ?  T0    T1    T2    T3    T4
         ?     ?     ?     ?     ?
Req 1 ??????????????????????????????? DB Query
Req 2 ??????????????????????????????? DB Query (duplicate!)
Req 3 ??????????????????????????????? DB Query (duplicate!)
Req 4 ??????????????????????????????? DB Query (duplicate!)

All requests miss cache, all query database!
```

### The Solution: Single-Flight Pattern

```
Time  ?  T0    T1    T2    T3    T4
         ?     ?     ?     ?     ?
Req 1 ????? LOCK ???? DB Query ?????? Return
Req 2 ????? WAIT ?????????? GET CACHED ??? Return
Req 3 ????? WAIT ?????????? GET CACHED ??? Return
Req 4 ????? WAIT ?????????? GET CACHED ??? Return

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

## Expiration Strategy

### Sliding Expiration

Every cache hit extends the expiration time (best-effort).

```
Initial:   [Entry created] ????????????? [Expires in 10 min]
                                      
Access 1:  [Entry hit at T+5] ??????????? [Expires in T+5+10 = 15 min]

Access 2:  [Entry hit at T+12] ?????????? [Expires in T+12+10 = 22 min]
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
Memory ? (48 bytes + data size) × active entries
```

**Recommendation:**
- Set `ProbabilisticCleanupThreshold` to ~2× expected cache size
- Optionally run manual cleanup every 5-10 minutes

---

## Performance Optimizations

### 1. ValueTask for Cache Hits

```csharp
public ValueTask<T> LoadItem<T>(...)
```

**Why?**
- Cache hits return synchronously (data already in memory)
- `ValueTask<T>` avoids Task allocation
- Falls back to `Task<T>` only for cache misses

**Benchmark:**
```
Cache Hit with Task<T>:      45 ns/op, 40 B allocated
Cache Hit with ValueTask<T>: 12 ns/op,  0 B allocated
```

### 2. Struct-Based Cache Keys

```csharp
readonly struct CacheKey
```

**Why?**
- No heap allocation for key object
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

**Why?**
- Eliminates method call overhead
- Enables further JIT optimizations
- Critical for hot paths

### 4. Immutable Cache Entries

```csharp
sealed class CacheEntry
{
    readonly long ExpiryTicks;
    readonly object Data;
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
| Stampede prevention | ? Built-in | ? Manual |
| Thread-safe | ? Yes | ? Yes |
| Async operations | ? Full support | ?? Limited |
| Per-key locking | ? Yes | ? No |
| Value-type keys | ? Yes | ? No |
| Dependency injection | ? Yes | ? Yes |

### vs. ConcurrentDictionary

| Feature | XpressCache | ConcurrentDictionary |
|---------|-------------|---------------------|
| Expiration | ? Automatic | ? Manual |
| Stampede prevention | ? Built-in | ? Manual |
| Cleanup | ? Automatic | ? Manual |
| Async operations | ? Native | ? Sync only |
| Memory bounds | ? Configurable | ?? Unbounded |

### vs. Redis (Distributed Cache)

| Feature | XpressCache | Redis |
|---------|-------------|-------|
| Latency | ~10-100 ns | ~1-5 ms |
| Network | ? Not required | ? Required |
| Persistence | ? In-memory only | ? Yes |
| Distributed | ? No | ? Yes |
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
