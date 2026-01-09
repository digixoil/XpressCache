# Getting Started with XpressCache

A step-by-step tutorial for getting started with XpressCache.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Your First Cache](#your-first-cache)
- [Working with ASP.NET Core](#working-with-aspnet-core)
- [Common Scenarios](#common-scenarios)
- [Next Steps](#next-steps)

---

## Prerequisites

- .NET 6.0, 7.0, or 8.0 SDK
- Basic understanding of async/await in C#
- (Optional) ASP.NET Core for web application examples

---

## Installation

### Step 1: Install the NuGet Package

**Using .NET CLI:**

```bash
dotnet add package XpressCache
```

**Using Package Manager Console:**

```powershell
Install-Package XpressCache
```

**Using Visual Studio:**
1. Right-click on your project
2. Select "Manage NuGet Packages"
3. Search for "XpressCache"
4. Click "Install"

### Step 2: Verify Installation

Check that the package is referenced in your `.csproj` file:

```xml
<ItemGroup>
  <PackageReference Include="XpressCache" Version="1.0.0" />
</ItemGroup>
```

---

## Your First Cache

### Example 1: Simple Console Application

Create a new console application and add this code:

```csharp
using XpressCache;
using Microsoft.Extensions.Logging;

// Create a logger (for diagnostics)
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

var logger = loggerFactory.CreateLogger<CacheStore>();

// Create the cache
var cache = new CacheStore(logger);

// Define a sample data class
public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

// Simulate loading a product from a database
async Task<Product> LoadProductFromDatabaseAsync(Guid productId)
{
    Console.WriteLine($"Loading product {productId} from database...");
    await Task.Delay(1000); // Simulate database latency
    
    return new Product
    {
        Id = productId,
        Name = "Sample Product",
        Price = 99.99m
    };
}

// Load product with caching
var productId = Guid.NewGuid();

Console.WriteLine("First call (cache miss):");
var product1 = await cache.LoadItem<Product>(
    entityId: productId,
    subject: "products",
    cacheMissRecovery: LoadProductFromDatabaseAsync
);
Console.WriteLine($"Product: {product1.Name}, Price: ${product1.Price}");

Console.WriteLine("\nSecond call (cache hit):");
var product2 = await cache.LoadItem<Product>(
    entityId: productId,
    subject: "products",
    cacheMissRecovery: LoadProductFromDatabaseAsync
);
Console.WriteLine($"Product: {product2.Name}, Price: ${product2.Price}");

Console.WriteLine("\nNotice: Database was only queried once!");
```

**Output:**

```
First call (cache miss):
Loading product 123e4567-e89b-12d3-a456-426614174000 from database...
Product: Sample Product, Price: $99.99

Second call (cache hit):
Product: Sample Product, Price: $99.99

Notice: Database was only queried once!
```

### Example 2: Testing Cache Stampede Prevention

```csharp
using XpressCache;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

// Setup cache
var cache = new CacheStore(
    loggerFactory.CreateLogger<CacheStore>()
);

var productId = Guid.NewGuid();
var callCount = 0;

async Task<Product> SlowDatabaseLoad(Guid id)
{
    var count = Interlocked.Increment(ref callCount);
    Console.WriteLine($"Database call #{count} started at {DateTime.Now:HH:mm:ss.fff}");
    
    await Task.Delay(2000); // Simulate slow database
    
    Console.WriteLine($"Database call #{count} completed");
    
    return new Product { Id = id, Name = "Product", Price = 49.99m };
}

// Simulate 10 concurrent requests for the same product
Console.WriteLine("Simulating 10 concurrent requests...\n");

var sw = Stopwatch.StartNew();
var tasks = Enumerable.Range(1, 10).Select(_ =>
    cache.LoadItem<Product>(
        productId,
        "products",
        SlowDatabaseLoad
    ).AsTask()
).ToArray();

var results = await Task.WhenAll(tasks);
sw.Stop();

Console.WriteLine($"\nAll requests completed in {sw.ElapsedMilliseconds}ms");
Console.WriteLine($"Database was called {callCount} time(s)");
Console.WriteLine("Result: Only one database call despite 10 concurrent requests!");
```

**Output:**

```
Simulating 10 concurrent requests...

Database call #1 started at 14:23:45.123
Database call #1 completed

All requests completed in 2015ms
Database was called 1 time(s)
Result: Only one database call despite 10 concurrent requests!
```

---

## Working with ASP.NET Core

### Step 1: Configure Dependency Injection

In your `Program.cs` or `Startup.cs`:

```csharp
using XpressCache;

var builder = WebApplication.CreateBuilder(args);

// Configure cache options
builder.Services.Configure<CacheStoreOptions>(options =>
{
    options.DefaultTtlMs = 10 * 60 * 1000;  // 10 minutes
    options.PreventCacheStampedeByDefault = true;
    options.InitialCapacity = 512;
});

// Register cache as singleton
builder.Services.AddSingleton<ICacheStore, CacheStore>();

// Add controllers, etc.
builder.Services.AddControllers();

var app = builder.Build();

// Configure middleware
app.MapControllers();

app.Run();
```

### Step 2: Use in Controllers

```csharp
using Microsoft.AspNetCore.Mvc;
using XpressCache;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly ICacheStore _cache;
    private readonly IProductRepository _repository;

    public ProductsController(
        ICacheStore cache,
        IProductRepository repository)
    {
        _cache = cache;
        _repository = repository;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Product>> GetProduct(Guid id)
    {
        var product = await _cache.LoadItem<Product>(
            entityId: id,
            subject: "products",
            cacheMissRecovery: _repository.GetByIdAsync
        );

        if (product == null)
            return NotFound();

        return Ok(product);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> UpdateProduct(
        Guid id, 
        [FromBody] Product product)
    {
        await _repository.UpdateAsync(product);

        // Invalidate cache
        _cache.RemoveItem<Product>(id, "products");

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteProduct(Guid id)
    {
        await _repository.DeleteAsync(id);

        // Invalidate cache
        _cache.RemoveItem<Product>(id, "products");

        return NoContent();
    }
}
```

### Step 3: Use in Services

```csharp
public class UserService : IUserService
{
    private readonly ICacheStore _cache;
    private readonly IUserRepository _repository;
    private readonly ILogger<UserService> _logger;

    public UserService(
        ICacheStore cache,
        IUserRepository repository,
        ILogger<UserService> logger)
    {
        _cache = cache;
        _repository = repository;
        _logger = logger;
    }

    public async Task<User> GetUserAsync(Guid userId)
    {
        return await _cache.LoadItem<User>(
            entityId: userId,
            subject: "users",
            cacheMissRecovery: async (id) =>
            {
                _logger.LogInformation("Cache miss for user {UserId}", id);
                return await _repository.GetByIdAsync(id);
            }
        );
    }

    public async Task UpdateUserAsync(User user)
    {
        await _repository.UpdateAsync(user);

        // Important: Invalidate cache after update
        _cache.RemoveItem<User>(user.Id, "users");
        
        _logger.LogInformation("Updated and invalidated cache for user {UserId}", user.Id);
    }
}
```

---

## Common Scenarios

### Scenario 1: Caching Database Queries

**Problem:** Database queries are slow and frequently repeated.

**Solution:**

```csharp
public class OrderService
{
    private readonly ICacheStore _cache;
    private readonly IDbContext _db;

    public async Task<Order> GetOrderAsync(Guid orderId)
    {
        return await _cache.LoadItem<Order>(
            entityId: orderId,
            subject: "orders",
            cacheMissRecovery: async (id) =>
            {
                return await _db.Orders
                    .Include(o => o.Items)
                    .FirstOrDefaultAsync(o => o.Id == id);
            }
        );
    }
}
```

### Scenario 2: Caching External API Calls

**Problem:** External API calls are expensive and rate-limited.

**Solution:**

```csharp
public class WeatherService
{
    private readonly ICacheStore _cache;
    private readonly HttpClient _httpClient;

    public async Task<WeatherData> GetWeatherAsync(string city)
    {
        // Use city name as "entity ID" by hashing to Guid
        var cityId = GenerateGuidFromString(city);

        return await _cache.LoadItem<WeatherData>(
            entityId: cityId,
            subject: "weather",
            cacheMissRecovery: async (id) =>
            {
                var response = await _httpClient.GetAsync(
                    $"https://api.weather.com/v1/current?city={city}"
                );
                
                return await response.Content.ReadFromJsonAsync<WeatherData>();
            },
            // Force stampede prevention for rate-limited API
            behavior: CacheLoadBehavior.PreventStampede
        );
    }

    private static Guid GenerateGuidFromString(string input)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}
```

### Scenario 3: Caching with Custom Validation

**Problem:** Cached data might become stale based on business rules.

**Solution:**

```csharp
public class PriceService
{
    private readonly ICacheStore _cache;
    private readonly IPriceRepository _repository;

    public async Task<ProductPrice> GetPriceAsync(Guid productId)
    {
        return await _cache.LoadItem<ProductPrice>(
            entityId: productId,
            subject: "prices",
            cacheMissRecovery: _repository.GetPriceAsync,
            customValidate: async (cachedPrice) =>
            {
                // Check if price is still valid
                var currentPrice = await _repository.GetCurrentPriceAsync(productId);
                
                // Invalidate if price changed by more than 5%
                var priceChange = Math.Abs(
                    (currentPrice - cachedPrice.Amount) / cachedPrice.Amount
                );
                
                return priceChange < 0.05m;
            }
        );
    }
}
```

### Scenario 4: Repository Pattern with Caching

**Problem:** Want to add caching to existing repository pattern.

**Solution:**

```csharp
// Original repository (no caching)
public class UserRepository : IUserRepository
{
    private readonly IDbContext _db;

    public async Task<User> GetByIdAsync(Guid userId)
    {
        return await _db.Users.FindAsync(userId);
    }

    public async Task UpdateAsync(User user)
    {
        _db.Users.Update(user);
        await _db.SaveChangesAsync();
    }
}

// Cached repository decorator
public class CachedUserRepository : IUserRepository
{
    private readonly IUserRepository _inner;
    private readonly ICacheStore _cache;

    public CachedUserRepository(
        IUserRepository inner,
        ICacheStore cache)
    {
        _inner = inner;
        _cache = cache;
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
        
        // Invalidate cache after update
        _cache.RemoveItem<User>(user.Id, "users");
    }
}

// Register in DI
services.AddScoped<UserRepository>(); // Inner repository
services.Decorate<IUserRepository, CachedUserRepository>(); // Add caching
```

### Scenario 5: Batch Cache Invalidation

**Problem:** Need to invalidate multiple related cache entries.

**Solution:**

```csharp
public class CategoryService
{
    private readonly ICacheStore _cache;
    private readonly ICategoryRepository _repository;

    public async Task UpdateCategoryAsync(Category category)
    {
        await _repository.UpdateAsync(category);

        // Invalidate the category itself
        _cache.RemoveItem<Category>(category.Id, "categories");

        // Invalidate all products in this category
        var products = _cache.GetCachedItems<Product>("products");
        if (products != null)
        {
            foreach (var product in products.Where(p => p.CategoryId == category.Id))
            {
                _cache.RemoveItem<Product>(product.Id, "products");
            }
        }
    }
}
```

### Scenario 6: Cache Warming on Startup

**Problem:** First requests after startup are slow due to cold cache.

**Solution:**

```csharp
public class CacheWarmer : IHostedService
{
    private readonly ICacheStore _cache;
    private readonly IProductRepository _repository;

    public async Task StartAsync(CancellationToken ct)
    {
        // Load frequently accessed products into cache
        var popularProductIds = await _repository.GetPopularProductIdsAsync();

        var tasks = popularProductIds.Select(id =>
            _cache.LoadItem<Product>(
                id,
                "products",
                _repository.GetByIdAsync
            )
        );

        await Task.WhenAll(tasks);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

// Register in DI
services.AddHostedService<CacheWarmer>();
```

---

## Next Steps

### Learn More

1. **Read the Architecture Guide** - Understand how XpressCache works internally
   - [Architecture.md](Architecture.md)

2. **Optimize Performance** - Tune configuration and apply best practices
   - [Performance-Guide.md](Performance-Guide.md)

3. **Explore the API** - Complete reference for all types and methods
   - [API-Reference.md](API-Reference.md)

### Common Questions

**Q: How do I know if caching is working?**

A: Enable debug logging and look for cache cleanup messages:

```csharp
builder.Logging.SetMinimumLevel(LogLevel.Debug);
```

**Q: Should I use a singleton or scoped lifetime?**

A: Always use **singleton**. The cache is designed to be shared across all requests.

```csharp
services.AddSingleton<ICacheStore, CacheStore>();
```

**Q: How do I clear the entire cache?**

A: Call `Clear()`:

```csharp
_cache.Clear();
```

**Q: Can I cache null values?**

A: No, the generic constraint requires `where T : class` and null is not cached. Recovery function will be called.

**Q: What happens if the recovery function throws an exception?**

A: The exception propagates to the caller. Nothing is cached. Subsequent calls retry the recovery function.

**Q: How do I use different TTLs for different items?**

A: Currently, all items use the same `DefaultTtlMs` from options. For varying TTLs, you could create multiple cache instances.

### Example Projects

Check out these example projects (in the GitHub repository):

- **Examples/ConsoleApp** - Basic console application
- **Examples/WebApi** - ASP.NET Core Web API with caching
- **Examples/RepositoryPattern** - Repository pattern with caching decorator

### Getting Help

- **Documentation:** Check the `/doc` folder for detailed guides
- **Issues:** Open an issue on GitHub for bugs or feature requests
- **Examples:** See the `/examples` folder for more code samples

---

## Summary

You've learned how to:

? Install XpressCache via NuGet  
? Create and use a cache in a console application  
? Test cache stampede prevention  
? Integrate with ASP.NET Core using DI  
? Use caching in controllers and services  
? Handle common scenarios (database, APIs, validation)  
? Implement cache invalidation strategies  

**Next:** Explore the [API Reference](API-Reference.md) for detailed method documentation.

---

## Quick Reference

### Basic Usage

```csharp
// Load with caching
var item = await cache.LoadItem<T>(
    entityId: id,
    subject: "items",
    cacheMissRecovery: LoadFromSourceAsync
);
```

### Set Item

```csharp
await cache.SetItem(id, "items", item);
```

### Remove Item

```csharp
cache.RemoveItem<T>(id, "items");
```

### Clear Cache

```csharp
cache.Clear();
```

### Cleanup Expired Items

```csharp
cache.CleanupCache();
```

### Configure Options

```csharp
services.Configure<CacheStoreOptions>(options =>
{
    options.DefaultTtlMs = 10 * 60 * 1000;
    options.PreventCacheStampedeByDefault = true;
});
```

---

Happy caching! ??
