# XpressCache

A high-performance, thread-safe in-memory cache for .NET with built-in cache stampede (thundering herd) prevention using per-key single-flight locking.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![NuGet](https://img.shields.io/nuget/v/XpressCache.svg)](https://www.nuget.org/packages/XpressCache/)

## Features

- 🚀 **High Performance**: Lock-free read operations with value-type keys for zero-allocation lookups
- 🧵 **Thread-Safe**: All operations are thread-safe with concurrent dictionary and atomic operations
- 🛡️ **Cache Stampede Prevention**: Per-key single-flight locking prevents thundering herd problems
- ⏳ **Sliding Expiration**: Automatic TTL renewal on access with configurable expiry policies
- ⚙️ **Flexible Configuration**: Control stampede prevention globally or per-call
- 🎯 **Multi-Targeting**: Supports .NET 6.0, .NET 7.0, .NET 8.0, .NET 9.0, and .NET 10.0
- 🧹 **Automatic Cleanup**: Probabilistic and manual cleanup of expired entries
- 🧪 **Custom Validation**: Support for custom validation functions on cached items
- 📚 **Fully Documented**: Comprehensive XML documentation for all public APIs

## What is Cache Stampede?

Cache stampede (also known as thundering herd) occurs when:
1. A cached item expires
2. Multiple concurrent requests try to access it simultaneously
3. All requests miss the cache and trigger expensive recovery operations
4. Resources are wasted computing the same data multiple times

**XpressCache solves this** by ensuring only one caller executes the recovery function while others wait for and share the result.

## Installation

Install via NuGet Package Manager:

```bash
dotnet add package XpressCache
```

Or via Package Manager Console:

```powershell
Install-Package XpressCache
```

## Quick Start

### Basic Usage

```csharp
using XpressCache;
using Microsoft.Extensions.Logger;
using Microsoft.Extensions.DependencyInjection;

// Setup with dependency injection
var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<ICacheStore, CacheStore>();

var provider = services.BuildServiceProvider();
var cache = provider.GetRequiredService<ICacheStore>();

// Load an item (with cache-miss recovery)
var userId = Guid.NewGuid();
var user = await cache.LoadItem<User>(
    entityId: userId,
    subject: "users",
    cacheMissRecovery: async (id) => 
    {
        // This expensive operation runs only once for concurrent requests
        return await database.GetUserAsync(id);
    }
);
```

### Configuration Options

```csharp
using Microsoft.Extensions.Options;

// Configure cache behavior
var options = Options.Create(new CacheStoreOptions
{
    PreventCacheStampedeByDefault = true,  // Enable single-flight (default: true)
    DefaultTtlMs = 10 * 60 * 1000,         // 10 minutes (default)
    InitialCapacity = 512,                  // Initial dictionary capacity
    ProbabilisticCleanupThreshold = 1000    // Trigger cleanup above this size
});

var cache = new CacheStore(logger, options);
```

### Per-Call Behavior Control

```csharp
// Force stampede prevention for expensive operations
var data = await cache.LoadItem<ExpensiveData>(
    entityId: id,
    subject: "reports",
    cacheMissRecovery: GenerateExpensiveReportAsync,
    behavior: CacheLoadBehavior.PreventStampede
);

// Allow parallel loads for cheap, idempotent operations
var config = await cache.LoadItem<Config>(
    entityId: id,
    subject: "config",
    cacheMissRecovery: LoadConfigAsync,
    behavior: CacheLoadBehavior.AllowParallelLoad
);

// Use default behavior from CacheStoreOptions
var item = await cache.LoadItem<Item>(
    entityId: id,
    subject: "items",
    cacheMissRecovery: LoadItemAsync,
    behavior: CacheLoadBehavior.Default  // This is also the default parameter
);
```

## Advanced Usage

### Custom Validation

Implement custom validation to invalidate cache entries based on business logic:

```csharp
var product = await cache.LoadItem<Product>(
    entityId: productId,
    subject: "products",
    cacheMissRecovery: async (id) => await database.GetProductAsync(id),
    customValidate: async (cachedProduct) =>
    {
        // Invalidate if price changed in database
        var currentPrice = await database.GetProductPriceAsync(cachedProduct.Id);
        return cachedProduct.Price == currentPrice;
    }
);
```

### Manual Cache Operations

```csharp
// Explicitly set an item in cache
await cache.SetItem(userId, "users", userObject);

// Remove a specific item
bool removed = cache.RemoveItem<User>(userId, "users");

// Get all cached items of a type
List<User> allCachedUsers = cache.GetCachedItems<User>("users");

// Clear entire cache
cache.Clear();

// Manual cleanup of expired entries
cache.CleanupCache();

// Enable/disable caching at runtime
cache.EnableCache = false;  // All operations become no-ops
cache.EnableCache = true;   // Re-enable caching (clears existing cache)
```

### Dependency Injection with ASP.NET Core

```csharp
// In Program.cs or Startup.cs
builder.Services.Configure<CacheStoreOptions>(options =>
{
    options.PreventCacheStampedeByDefault = true;
    options.DefaultTtlMs = 15 * 60 * 1000;  // 15 minutes
    options.InitialCapacity = 1024;
});

builder.Services.AddSingleton<ICacheStore, CacheStore>();

// Usage in controllers or services
public class ProductService
{
    private readonly ICacheStore _cache;
    private readonly IProductRepository _repository;

    public ProductService(ICacheStore cache, IProductRepository repository)
    {
        _cache = cache;
        _repository = repository;
    }

    public async Task<Product> GetProductAsync(Guid productId)
    {
        return await _cache.LoadItem<Product>(
            entityId: productId,
            subject: "products",
            cacheMissRecovery: _repository.GetByIdAsync
        );
    }
}
```

## How It Works

### Cache Key Structure

XpressCache uses a composite key consisting of:
- **Entity ID** (`Guid`): Unique identifier for the entity
- **Type** (`Type`): The .NET type of the cached item
- **Subject** (`string`): Optional categorization (e.g., "users", "products")

This allows you to cache different types with the same ID without conflicts.

### Single-Flight Pattern

When stampede prevention is enabled:

1. **First Request** (cache miss):
   - Acquires per-key lock
   - Executes cache-miss recovery function
   - Stores result in cache
   - Releases lock
   - Returns result

2. **Concurrent Requests** (for same key):
   - Wait for per-key lock
   - After lock released, check cache again (double-check pattern)
   - Find cached result from first request
   - Return cached result without executing recovery

3. **Different Keys**:
   - Execute in parallel (locks are per-key, not global)

### Expiration and Renewal

- Entries have a sliding expiration window (TTL)
- Accessing an entry extends its expiration (best-effort)
- Expired entries are removed lazily during access
- Probabilistic cleanup prevents unbounded growth
- Manual cleanup available via `CleanupCache()`

## Thread Safety Guarantees

All operations are thread-safe:
- 🧵 `LoadItem` - Multiple concurrent calls are safe
- ✍️ `SetItem` - Atomic updates
- ❌ `RemoveItem` - Safe concurrent removal
- 📋 `GetCachedItems` - Returns consistent snapshot
- 🧹 `Clear` - Safe to call anytime
- 🧹 `CleanupCache` - Can run concurrently with other operations
- 🔒 `EnableCache` setter - Thread-safe property

## Performance Characteristics

| Operation | Complexity | Notes |
|-----------|------------|-------|
| Cache Hit | O(1) | Lock-free read, no allocations for key |
| Cache Miss (single-flight) | O(1) + recovery time | Per-key lock acquisition |
| Cache Miss (parallel) | O(1) + recovery time | No locking overhead |
| Remove | O(1) | Concurrent dictionary removal |
| GetCachedItems | O(n) | Full cache scan, use sparingly |
| Clear | O(n) | Clears all entries |
| Memory per entry | ~48 bytes | Overhead excluding cached data |

## Best Practices

### ✅ DO

- Use stampede prevention for expensive operations (database, API calls)
- Set appropriate TTL based on data volatility
- Use `subject` parameter to categorize related items
- Handle null returns from `LoadItem` when recovery returns null
- Consider custom validation for critical data freshness
- Use `AllowParallelLoad` for cheap, idempotent operations
- Monitor cache size and adjust `ProbabilisticCleanupThreshold`

### 🚫 DON'T

- Don't cache very large objects (hundreds of MB per entry)
- Don't use for persistent storage (in-memory only)
- Don't call `GetCachedItems` in hot paths (O(n) operation)
- Don't forget to handle exceptions in recovery functions
- Don't disable stampede prevention for expensive operations
- Don't use excessively short TTLs (defeats caching purpose)

## Migration from Other Caches

### From IMemoryCache

```csharp
// Before (IMemoryCache)
var user = await cache.GetOrCreateAsync(userId, async entry =>
{
    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
    return await database.GetUserAsync(userId);
});

// After (XpressCache)
var user = await cache.LoadItem<User>(
    entityId: userId,
    subject: "users",
    cacheMissRecovery: database.GetUserAsync
);
```

### From ConcurrentDictionary

```csharp
// Before (manual ConcurrentDictionary)
var dict = new ConcurrentDictionary<Guid, User>();
var user = dict.GetOrAdd(userId, id => LoadUser(id)); // Synchronous only

// After (XpressCache)
var user = await cache.LoadItem<User>(
    entityId: userId,
    subject: "users",
    cacheMissRecovery: LoadUserAsync  // Async support
);
```

## Troubleshooting

### High Memory Usage

1. Check cache size: monitor entry count
2. Reduce `DefaultTtlMs` for faster expiration
3. Lower `ProbabilisticCleanupThreshold`
4. Call `CleanupCache()` periodically
5. Consider caching smaller objects

### Cache Stampede Still Occurring

1. Verify `PreventCacheStampedeByDefault = true`
2. Check you're not using `CacheLoadBehavior.AllowParallelLoad`
3. Ensure same key (entityId + type + subject) for related requests

### Performance Issues

1. Avoid `GetCachedItems` in hot paths
2. Use appropriate `InitialCapacity` to reduce rehashing
3. Consider if recovery function is the bottleneck
4. Check if TTL is too short causing frequent reloads

## Examples

See the [documentation folder](./doc) for more examples:
- [API Reference](./doc/API-Reference.md)
- [Architecture Guide](./doc/Architecture.md)
- [Performance Optimization](./doc/Performance-Guide.md)
- [Getting Started Tutorial](./doc/Getting-Started.md)

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Author

**Russlan Kafri**  
Company: [Digixoil](https://www.digixoil.se)

## Support

For issues, questions, or suggestions:
- Open an issue on GitHub
- Check existing documentation in the `/doc` folder
- Review XML documentation in source code

---

Made with ❤️ by Digixoil