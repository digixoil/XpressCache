# XpressCache API Reference

Complete reference for all public types, methods, and properties in XpressCache.

## Table of Contents

- [ICacheStore Interface](#icachestore-interface)
- [CacheStore Class](#cachestore-class)
- [CacheStoreOptions Class](#cachestoreoptions-class)
- [CacheLoadBehavior Enum](#cacheloadbehavior-enum)

---

## ICacheStore Interface

The primary interface for cache operations.

**Namespace:** `XpressCache`

### Properties

#### EnableCache

```csharp
bool EnableCache { get; set; }
```

Gets or sets whether the cache is enabled.

**Remarks:**
- When `false`, all cache operations become no-ops
- `LoadItem` will always invoke the recovery function
- `SetItem` and other operations will do nothing
- Changing this property clears all cached items

**Example:**

```csharp
// Temporarily disable caching
cache.EnableCache = false;
var result = await cache.LoadItem(...); // Always loads from source

// Re-enable
cache.EnableCache = true; // Clears cache first
```

---

### Methods

#### LoadItem&lt;T&gt;

```csharp
ValueTask<T> LoadItem<T>(
    Guid? entityId,
    string subject,
    Func<Guid, Task<T>> cacheMissRecovery,
    Func<T, Task<bool>> customValidate = null,
    CacheLoadBehavior behavior = CacheLoadBehavior.Default
) where T : class
```

Loads an item from the cache, or retrieves it using the recovery function if not cached.

**Type Parameters:**
- `T` - The type of the cached item (must be a reference type)

**Parameters:**
- `entityId` - The unique identifier of the entity (required, cannot be `Guid.Empty`)
- `subject` - Optional subject for categorization (null normalized to empty string)
- `cacheMissRecovery` - Function to retrieve the item if not in cache (can be null for cache-only checks)
- `customValidate` - Optional validation function for cached items (returns false to invalidate)
- `behavior` - Cache loading behavior for stampede prevention (default: uses store-wide setting)

**Returns:**
- The cached item, recovered item, or `default(T)` if not found and no recovery function

**Remarks:**
- Returns `ValueTask<T>` to avoid Task allocation on cache hits
- Implements double-check locking when stampede prevention is active
- Expired entries are automatically removed
- Entry expiration is renewed on cache hit (best-effort)

**Example:**

```csharp
// Basic usage
var user = await cache.LoadItem<User>(
    entityId: userId,
    subject: "users",
    cacheMissRecovery: async (id) => await database.GetUserAsync(id)
);

// With custom validation
var product = await cache.LoadItem<Product>(
    entityId: productId,
    subject: "products",
    cacheMissRecovery: LoadProductAsync,
    customValidate: async (p) => await IsProductStillValidAsync(p)
);

// Force stampede prevention
var report = await cache.LoadItem<Report>(
    entityId: reportId,
    subject: "reports",
    cacheMissRecovery: GenerateReportAsync,
    behavior: CacheLoadBehavior.PreventStampede
);

// Cache-only check (no recovery)
var cached = await cache.LoadItem<User>(userId, "users", cacheMissRecovery: null);
if (cached == null)
{
    // Not in cache
}
```

---

#### SetItem&lt;T&gt;

```csharp
Task SetItem<T>(Guid? entityId, string subject, T item) where T : class
```

Stores an item in the cache.

**Type Parameters:**
- `T` - The type of the item to cache

**Parameters:**
- `entityId` - The unique identifier of the entity
- `subject` - Optional subject for categorization
- `item` - The item to cache

**Returns:**
- A completed task

**Remarks:**
- If an item with the same key exists, it is replaced
- Does nothing if `EnableCache` is false
- Does nothing if `entityId` is null or `Guid.Empty`

**Example:**

```csharp
var user = new User { Id = userId, Name = "John" };
await cache.SetItem(userId, "users", user);
```

---

#### RemoveItem&lt;T&gt;

```csharp
bool RemoveItem<T>(Guid? entityId, string subject)
```

Removes a specific item from the cache.

**Type Parameters:**
- `T` - The type of the item to remove

**Parameters:**
- `entityId` - The unique identifier of the entity
- `subject` - The subject used when storing the item

**Returns:**
- `true` if the item was found and removed; otherwise `false`

**Example:**

```csharp
bool removed = cache.RemoveItem<User>(userId, "users");
if (removed)
{
    Console.WriteLine("User removed from cache");
}
```

---

#### GetCachedItems&lt;T&gt;

```csharp
List<T> GetCachedItems<T>(string subject) where T : class
```

Gets all cached items of a specific type and subject.

**Type Parameters:**
- `T` - The type of items to retrieve

**Parameters:**
- `subject` - The subject to filter by

**Returns:**
- A list of all matching cached items, or `null` if cache is disabled

**Remarks:**
- **Performance:** O(n) operation where n is the total cache size
- Expired entries encountered during scan are removed
- Use sparingly in performance-critical code
- Consider secondary indices for frequent use

**Example:**

```csharp
// Get all cached users
var allUsers = cache.GetCachedItems<User>("users");
if (allUsers != null)
{
    Console.WriteLine($"Found {allUsers.Count} cached users");
}
```

---

#### Clear

```csharp
void Clear()
```

Removes all cached items.

**Example:**

```csharp
cache.Clear();
Console.WriteLine("Cache cleared");
```

---

#### CleanupCache

```csharp
void CleanupCache()
```

Removes expired items from the cache.

**Remarks:**
- Performs a full scan of the cache
- Should be called periodically (e.g., via background timer)
- The cache also performs lazy cleanup during access operations
- Not strictly necessary to call frequently due to lazy cleanup

**Example:**

```csharp
// Setup periodic cleanup
var timer = new Timer(_ => cache.CleanupCache(), null, 
    TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
```

---

## CacheStore Class

The default implementation of `ICacheStore`.

**Namespace:** `XpressCache`

### Constructors

#### CacheStore(ILogger&lt;CacheStore&gt;)

```csharp
public CacheStore(ILogger<CacheStore> logger)
```

Initializes a new instance with default options.

**Parameters:**
- `logger` - Logger for diagnostic output (required)

**Example:**

```csharp
var cache = new CacheStore(loggerFactory.CreateLogger<CacheStore>());
```

---

#### CacheStore(ILogger&lt;CacheStore&gt;, IOptions&lt;CacheStoreOptions&gt;)

```csharp
public CacheStore(
    ILogger<CacheStore> logger,
    IOptions<CacheStoreOptions> options
)
```

Initializes a new instance with configuration from dependency injection.

**Parameters:**
- `logger` - Logger for diagnostic output (required)
- `options` - Configuration options wrapper

**Example:**

```csharp
// In DI container
services.Configure<CacheStoreOptions>(config =>
{
    config.DefaultTtlMs = 15 * 60 * 1000;
});
services.AddSingleton<ICacheStore, CacheStore>();
```

---

## CacheStoreOptions Class

Configuration options for the cache store.

**Namespace:** `XpressCache`

### Properties

#### PreventCacheStampedeByDefault

```csharp
public bool PreventCacheStampedeByDefault { get; init; } = true;
```

Gets or sets whether cache stampede prevention is enabled by default.

**Default:** `true`

**Remarks:**
- When `true`, uses per-key single-flight locking
- Can be overridden per-call with `CacheLoadBehavior`
- Set to `false` to allow parallel cache-miss recovery by default

**Example:**

```csharp
var options = new CacheStoreOptions
{
    PreventCacheStampedeByDefault = true
};
```

---

#### DefaultTtlMs

```csharp
public long DefaultTtlMs { get; init; } = 10 * 60 * 1000;
```

Gets or sets the default time-to-live for cache entries in milliseconds.

**Default:** 600000 (10 minutes)

**Remarks:**
- Entries automatically expire after this duration
- Accessing an entry renews its TTL (sliding expiration)
- Must be positive

**Example:**

```csharp
var options = new CacheStoreOptions
{
    DefaultTtlMs = 30 * 60 * 1000  // 30 minutes
};
```

---

#### InitialCapacity

```csharp
public int InitialCapacity { get; init; } = 256;
```

Gets or sets the initial capacity of the cache dictionary.

**Default:** 256

**Remarks:**
- Higher capacity reduces rehashing but uses more memory upfront
- Set based on expected cache size
- Dictionary grows automatically if exceeded

**Example:**

```csharp
var options = new CacheStoreOptions
{
    InitialCapacity = 1024  // Expect ~1000 cached items
};
```

---

#### ProbabilisticCleanupThreshold

```csharp
public int ProbabilisticCleanupThreshold { get; init; } = 1000;
```

Gets or sets the threshold for triggering probabilistic cleanup during read operations.

**Default:** 1000

**Remarks:**
- When cache size exceeds this threshold, cleanup may trigger on reads
- Prevents unbounded cache growth
- Set higher for larger expected cache sizes

**Example:**

```csharp
var options = new CacheStoreOptions
{
    ProbabilisticCleanupThreshold = 5000
};
```

---

## CacheLoadBehavior Enum

Specifies cache loading behavior for stampede prevention control.

**Namespace:** `XpressCache`

### Values

#### Default

```csharp
Default = 0
```

Uses the store-wide default behavior from `CacheStoreOptions.PreventCacheStampedeByDefault`.

**Recommended for:** Most use cases

**Example:**

```csharp
var item = await cache.LoadItem<Item>(
    entityId: id,
    subject: "items",
    cacheMissRecovery: LoadItemAsync,
    behavior: CacheLoadBehavior.Default  // Optional, this is the default
);
```

---

#### PreventStampede

```csharp
PreventStampede = 1
```

Forces per-key single-flight locking regardless of store-wide default.

**Use for:**
- Database queries
- External API calls
- Complex computations
- Rate-limited operations

**Example:**

```csharp
var report = await cache.LoadItem<Report>(
    entityId: reportId,
    subject: "reports",
    cacheMissRecovery: async (id) =>
    {
        // Expensive operation - ensure only one execution per key
        return await GenerateComplexReportAsync(id);
    },
    behavior: CacheLoadBehavior.PreventStampede
);
```

---

#### AllowParallelLoad

```csharp
AllowParallelLoad = 2
```

Allows parallel cache-miss recovery executions regardless of store-wide default.

**Use for:**
- In-memory operations
- Idempotent functions
- Cheap operations where lock overhead is unnecessary

**Example:**

```csharp
var config = await cache.LoadItem<Config>(
    entityId: configId,
    subject: "config",
    cacheMissRecovery: async (id) =>
    {
        // Cheap, idempotent operation
        return await LoadConfigFromMemoryAsync(id);
    },
    behavior: CacheLoadBehavior.AllowParallelLoad
);
```

---

## Common Usage Patterns

### Pattern 1: Repository Pattern with Caching

```csharp
public class CachedUserRepository : IUserRepository
{
    private readonly ICacheStore _cache;
    private readonly IUserRepository _inner;

    public CachedUserRepository(ICacheStore cache, IUserRepository inner)
    {
        _cache = cache;
        _inner = inner;
    }

    public async Task<User> GetByIdAsync(Guid userId)
    {
        return await _cache.LoadItem<User>(
            entityId: userId,
            subject: "users",
            cacheMissRecovery: _inner.GetByIdAsync
        );
    }

    public async Task UpdateAsync(User user)
    {
        await _inner.UpdateAsync(user);
        
        // Invalidate cache
        _cache.RemoveItem<User>(user.Id, "users");
    }
}
```

### Pattern 2: Cache-Aside with Validation

```csharp
public async Task<Product> GetProductWithPriceValidationAsync(Guid productId)
{
    return await _cache.LoadItem<Product>(
        entityId: productId,
        subject: "products",
        cacheMissRecovery: LoadProductFromDatabaseAsync,
        customValidate: async (cached) =>
        {
            // Validate price hasn't changed
            var currentPrice = await GetCurrentPriceAsync(cached.Id);
            return cached.Price == currentPrice;
        }
    );
}
```

### Pattern 3: Multi-Level Caching

```csharp
public class MultiLevelCache
{
    private readonly ICacheStore _l1Cache;
    private readonly ICacheStore _l2Cache;

    public async Task<T> GetAsync<T>(Guid id, Func<Guid, Task<T>> source) 
        where T : class
    {
        // Try L1
        var item = await _l1Cache.LoadItem<T>(
            id, "l1", 
            cacheMissRecovery: null
        );

        if (item != null) return item;

        // Try L2, fallback to source
        item = await _l2Cache.LoadItem<T>(
            id, "l2",
            cacheMissRecovery: source
        );

        // Populate L1
        if (item != null)
        {
            await _l1Cache.SetItem(id, "l1", item);
        }

        return item;
    }
}
```

---

## See Also

- [Architecture Guide](Architecture.md)
- [Performance Guide](Performance-Guide.md)
- [Getting Started Tutorial](Getting-Started.md)
