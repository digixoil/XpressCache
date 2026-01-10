using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace XpressCache;

/// <summary>
/// High-performance, thread-safe cache store for caching entities by ID, type, and subject.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Design Principles:</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <term>Lock-Free Read Operations</term>
///     <description>
///       Uses immutable cache entries with atomic replacement via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
///       Cache hits do not require locking.
///     </description>
///   </item>
///   <item>
///     <term>Per-Key Single-Flight Locking</term>
///     <description>
///       On cache miss, uses per-key async locks to prevent cache stampede (thundering herd).
///       Only one concurrent caller executes the recovery function per key.
///     </description>
///   </item>
///   <item>
///     <term>Value-Type Keys</term>
///     <description>
///       Uses a <c>readonly struct</c> (<see cref="CacheKey"/>) instead of <see cref="Tuple{T1,T2,T3}"/>
///       to avoid heap allocations for keys and improve hash code performance.
///     </description>
///   </item>
///   <item>
///     <term>Efficient Time Handling</term>
///     <description>
///       Uses <see cref="Environment.TickCount64"/> for expiry checks instead of <see cref="DateTime.Now"/>,

///       which is significantly faster and avoids timezone/DST issues.
///     </description>
///   </item>
///   <item>
///     <term>Lazy Expiry Cleanup</term>
///     <description>
///       Expired entries are removed lazily during access operations, reducing the need for
///       expensive periodic full-cache scans.
///     </description>
///   </item>
/// </list>
/// 
/// <para>
/// <strong>Cache Stampede Prevention:</strong>
/// </para>
/// <para>
/// This cache implements the single-flight pattern to prevent the classic cache stampede
/// (thundering herd) problem. When multiple concurrent callers request the same uncached key:
/// <list type="bullet">
///   <item>First caller acquires a per-key lock and executes the recovery function</item>
///   <item>Concurrent callers wait for the lock</item>
///   <item>After the first caller completes, waiting callers find the cached result</item>
///   <item>No redundant recovery executions occur</item>
/// </list>
/// This behavior is configurable via <see cref="CacheStoreOptions.PreventCacheStampedeByDefault"/>
/// and can be overridden per-call using <see cref="CacheLoadBehavior"/>.
/// </para>
/// 
/// <para>
/// <strong>Thread Safety:</strong>
/// </para>
/// <para>
/// All operations are thread-safe. The cache uses <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// with immutable entry values for the cache, and <see cref="KeyedAsyncLock{TKey}"/> for
/// per-key synchronization during cache-miss recovery.
/// </para>
/// 
/// <para>
/// <strong>Expiry and Renewal Semantics:</strong>
/// </para>
/// <para>
/// Cache entries have a sliding expiration window. When an entry is accessed, its expiry time
/// is extended. However, renewal is <strong>best-effort</strong>: under high contention, the
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/> operation may fail, and the entry's
/// expiry may not be extended. This is intentional to avoid the complexity and overhead of
/// stronger guarantees. For a cache, approximate expiration is acceptable - entries may expire
/// slightly earlier than expected under contention, but correctness is preserved since expired
/// entries are simply re-fetched from the source.
/// </para>
/// 
/// <para>
/// <strong>Validation with Timing Context:</strong>
/// </para>
/// <para>
/// Custom validation callbacks can receive a <see cref="CacheValidationContext"/> that provides:
/// <list type="bullet">
///   <item><see cref="CacheValidationContext.ExpiryTicks"/> - When the entry will expire</item>
///   <item><see cref="CacheValidationContext.CurrentTicks"/> - The current time reference</item>
///   <item><see cref="CacheValidationContext.TimeToExpiry"/> - Time remaining until expiration</item>
///   <item><see cref="CacheValidationContext.Age"/> - Approximate age of the entry</item>
///   <item><see cref="CacheValidationContext.ExpiryProgress"/> - Progress toward expiration (0.0-1.0)</item>
/// </list>
/// This enables sophisticated time-based validation logic such as proactive refresh.
/// </para>
/// 
/// <para>
/// <strong>Performance Characteristics:</strong>
/// </para>
/// <list type="bullet">
///   <item>Cache hit: O(1) average, no locking</item>
///   <item>Cache miss (single-flight): O(1) lock acquisition + recovery time</item>
///   <item>Memory: ~48 bytes per entry overhead (excluding cached data)</item>
/// </list>
/// </remarks>
/// <seealso cref="CacheStoreOptions"/>
/// <seealso cref="CacheLoadBehavior"/>
/// <seealso cref="CacheValidationContext"/>
/// <seealso cref="ICacheStore"/>
public sealed class CacheStore : ICacheStore
{
    #region Constants

    /// <summary>
    /// Default time-to-live for cache entries in milliseconds (10 minutes).
    /// </summary>
    private const long DefaultTtlMs = 10 * 60 * 1000;

    /// <summary>
    /// Threshold for triggering probabilistic cleanup during read operations.
    /// When the cache size exceeds this value, there's a chance of triggering cleanup on reads.
    /// </summary>
    private const int DefaultProbabilisticCleanupThreshold = 1000;

    /// <summary>
    /// Probability (1 in N) of triggering cleanup on read when above threshold.
    /// </summary>
    private const int ProbabilisticCleanupChance = 100;

    #endregion

    #region Internal Types

    /// <summary>
    /// Immutable cache entry that stores data with expiration time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a sealed class with readonly fields to ensure immutability.
    /// When an entry needs to be updated, a new instance is created and atomically
    /// replaces the old one in the dictionary.
    /// </para>
    /// <para>
    /// Using <see cref="Environment.TickCount64"/> for expiry tracking is much faster
    /// than <see cref="DateTime.Now"/> (which involves system calls and timezone handling).
    /// </para>
    /// </remarks>
    private sealed class CacheEntry
    {
        /// <summary>
        /// The tick count (from <see cref="Environment.TickCount64"/>) at which this entry expires.
        /// </summary>
        public readonly long ExpiryTicks;

        /// <summary>
        /// The cached data.
        /// </summary>
        public readonly object? Data;

        /// <summary>
        /// Creates a new cache entry with the specified data and TTL.
        /// </summary>
        /// <param name="data">The data to cache.</param>
        /// <param name="currentTicks">The current tick count from <see cref="Environment.TickCount64"/>.</param>
        /// <param name="ttlMs">Time-to-live in milliseconds.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CacheEntry(object? data, long currentTicks, long ttlMs)
        {
            Data = data;
            ExpiryTicks = currentTicks + ttlMs;
        }

        /// <summary>
        /// Creates a renewed entry with extended expiry time.
        /// </summary>
        /// <param name="currentTicks">The current tick count.</param>
        /// <param name="ttlMs">Time-to-live in milliseconds.</param>
        /// <returns>A new entry with the same data but updated expiry.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CacheEntry Renew(long currentTicks, long ttlMs)
            => new(Data, currentTicks, ttlMs);

        /// <summary>
        /// Checks if this entry has expired.
        /// </summary>
        /// <param name="currentTicks">The current tick count.</param>
        /// <returns>True if the entry has expired; otherwise, false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsExpired(long currentTicks)
            => currentTicks >= ExpiryTicks;
    }

    /// <summary>
    /// Value-type key for cache lookups, avoiding heap allocations for the key structure itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Using a <c>readonly struct</c> instead of <see cref="Tuple{T1,T2,T3}"/> provides:
    /// </para>
    /// <list type="bullet">
    ///   <item>No heap allocation for the key structure itself</item>
    ///   <item>Better cache locality</item>
    ///   <item>Custom, optimized <see cref="GetHashCode"/> implementation</item>
    ///   <item>Implements <see cref="IEquatable{T}"/> for faster equality checks</item>
    /// </list>
    /// <para>
    /// <strong>Type Identity:</strong>
    /// </para>
    /// <para>
    /// The key stores a direct <see cref="Type"/> reference rather than a hash code to ensure
    /// correct identity comparison. Different types may have colliding hash codes, but 
    /// <see cref="ReferenceEquals"/> guarantees correct type identity. This eliminates the risk
    /// of silent cache corruption from hash collisions.
    /// </para>
    /// </remarks>
    private readonly struct CacheKey : IEquatable<CacheKey>
    {
        public readonly Guid EntityId;
        public readonly Type Type;
        public readonly string Subject;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CacheKey(Guid entityId, Type type, string? subject)
        {
            EntityId = entityId;
            Type = type;
            Subject = subject ?? string.Empty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(CacheKey other)
            => EntityId == other.EntityId
               && ReferenceEquals(Type, other.Type)
               && string.Equals(Subject, other.Subject, StringComparison.Ordinal);

        public override bool Equals(object? obj)
            => obj is CacheKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(EntityId, Type, Subject);
    }

    #endregion

    #region Private Fields

    private readonly ILogger<CacheStore> _logger;
    private readonly CacheStoreOptions _options;
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> _cache;
    private readonly KeyedAsyncLock<CacheKey> _keyedLock;
    private readonly long _ttlMs;
    private readonly int _probabilisticCleanupThreshold;
    private volatile bool _enableCache = true;

    /// <summary>
    /// Counter for probabilistic cleanup trigger (thread-safe via Interlocked).
    /// </summary>
    private int _accessCounter;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheStore"/> class with default options.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is <c>null</c>.</exception>
    /// <remarks>
    /// Uses default <see cref="CacheStoreOptions"/> with stampede prevention enabled.
    /// </remarks>
    public CacheStore(ILogger<CacheStore> logger)
        : this(logger, Options.Create(new CacheStoreOptions()))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheStore"/> class with options from DI.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="options">Configuration options wrapper from DI. If <c>null</c>, uses defaults.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is <c>null</c>.</exception>
    public CacheStore(ILogger<CacheStore> logger, IOptions<CacheStoreOptions>? options)
    {
        _logger = logger.AssertNotNull(nameof(logger), "ILogger<CacheStore> reference is null");
        _options = options?.Value ?? new CacheStoreOptions();

        _ttlMs = _options.DefaultTtlMs > 0 ? _options.DefaultTtlMs : DefaultTtlMs;
        _probabilisticCleanupThreshold = _options.ProbabilisticCleanupThreshold > 0
            ? _options.ProbabilisticCleanupThreshold
            : DefaultProbabilisticCleanupThreshold;

        // Use Environment.ProcessorCount for initial concurrency level
        _cache = new ConcurrentDictionary<CacheKey, CacheEntry>(
            concurrencyLevel: Environment.ProcessorCount * 2,
            capacity: _options.InitialCapacity > 0 ? _options.InitialCapacity : 256);

        // Initialize per-key lock for single-flight pattern
        _keyedLock = new KeyedAsyncLock<CacheKey>();

        _logger.LogDebug(
            "CacheStore initialized with PreventCacheStampedeByDefault={StampedeDefault}, TtlMs={TtlMs}, InitialCapacity={Capacity}",
            _options.PreventCacheStampedeByDefault, _ttlMs, _options.InitialCapacity);
    }

    #endregion

    #region ICacheStore Implementation

    /// <inheritdoc/>
    public bool EnableCache
    {
        get => _enableCache;
        set
        {
            if (_enableCache != value)
            {
                _enableCache = value;
                Clear();

                if (_enableCache)
                    _logger.LogInformation("Cache has been activated");
                else
                    _logger.LogInformation("Cache has been deactivated");
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// This method implements a read-through caching pattern with optional cache stampede prevention:
    /// </para>
    /// <list type="number">
    ///   <item>Check if the item exists in cache and is valid (fast path)</item>
    ///   <item>If cache miss and stampede prevention is active:
    ///     <list type="bullet">
    ///       <item>Acquire per-key lock</item>
    ///       <item>Double-check cache (another caller may have populated it)</item>
    ///       <item>If still miss, execute recovery</item>
    ///       <item>Store result and release lock</item>
    ///     </list>
    ///   </item>
    ///   <item>If cache miss without stampede prevention, execute recovery directly</item>
    /// </list>
    /// <para>
    /// <strong>Stampede Prevention Resolution:</strong>
    /// </para>
    /// <para>
    /// The behavior parameter resolves as follows:
    /// <list type="bullet">
    ///   <item><see cref="CacheLoadBehavior.Default"/>: Uses <see cref="CacheStoreOptions.PreventCacheStampedeByDefault"/></item>
    ///   <item><see cref="CacheLoadBehavior.PreventStampede"/>: Always uses single-flight locking</item>
    ///   <item><see cref="CacheLoadBehavior.AllowParallelLoad"/>: Never uses single-flight locking</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Renewal Behavior:</strong>
    /// Entry renewal on cache hit is best-effort. Under high contention, the renewal
    /// may be skipped, causing the entry to expire earlier than expected. This is
    /// acceptable for cache semantics - the item will simply be re-fetched on the next access.
    /// </para>
    /// <para>
    /// <strong>Validation Behavior:</strong>
    /// Validation callbacks receive a <see cref="CacheValidationContext"/> providing timing information.
    /// If synchronous validation is provided, it is executed in the lock-free fast path.
    /// If validation fails or if async validation is required, the method proceeds to
    /// the locking path where stampede prevention can coordinate recovery.
    /// For best performance, prefer synchronous validation when possible.
    /// </para>
    /// </remarks>
    public async ValueTask<T?> LoadItem<T>(
        Guid? entityId,
        string? subject,
        Func<Guid, Task<T>>? cacheMissRecovery,
        Func<T, CacheValidationContext, bool>? syncValidateWithContext = null,
        Func<T, CacheValidationContext, Task<bool>>? asyncValidateWithContext = null,
        CacheLoadBehavior behavior = CacheLoadBehavior.Default) where T : class
    {
        // Fast path: cache disabled or invalid entity ID
        if (!_enableCache)
        {
            return cacheMissRecovery is not null && entityId.HasValue && entityId.Value != Guid.Empty
                ? await cacheMissRecovery(entityId.Value).ConfigureAwait(false)
                : default;
        }

        if (!entityId.HasValue || entityId.Value == Guid.Empty)
            return default;

        var id = entityId.Value;
        var key = new CacheKey(id, typeof(T), subject);
        var currentTicks = Environment.TickCount64;

        // Determine if async validation is required
        bool hasAsyncValidation = asyncValidateWithContext is not null;

        // Fast path: Try to get from cache first with sync validation only
        // Async validation requires going through the locking path for consistency
        if (!hasAsyncValidation)
        {
            var cachedResult = TryGetFromCacheWithContext<T>(key, currentTicks, syncValidateWithContext);

            if (cachedResult.Found)
            {
                TriggerProbabilisticCleanup();
                return cachedResult.Value;
            }
        }

        // Resolve stampede prevention behavior
        bool useSingleFlight = behavior switch
        {
            CacheLoadBehavior.PreventStampede => true,
            CacheLoadBehavior.AllowParallelLoad => false,
            _ => _options.PreventCacheStampedeByDefault
        };

        // Cache miss or async validation required - proceed through locking path
        if (cacheMissRecovery is not null)
        {
            if (useSingleFlight)
            {
                return await LoadItemWithSingleFlightWithContextAsync(
                    id, key, cacheMissRecovery, syncValidateWithContext, asyncValidateWithContext).ConfigureAwait(false);
            }
            else
            {
                return await LoadItemWithoutLockAsync(id, key, cacheMissRecovery).ConfigureAwait(false);
            }
        }

        return default;
    }

    /// <inheritdoc/>
    public Task SetItem<T>(Guid? entityId, string? subject, T item) where T : class
    {
        if (!_enableCache || !entityId.HasValue || entityId.Value == Guid.Empty)
            return Task.CompletedTask;

        var key = new CacheKey(entityId.Value, typeof(T), subject);
        var entry = new CacheEntry(item, Environment.TickCount64, _ttlMs);

        _cache.AddOrUpdate(key, entry, (_, _) => entry);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public bool RemoveItem<T>(Guid? entityId, string? subject)
    {
        if (!entityId.HasValue)
            return false;

        var key = new CacheKey(entityId.Value, typeof(T), subject);
        return _cache.TryRemove(key, out _);
    }

    /// <inheritdoc/>
    public void Clear()
    {
        _cache.Clear();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// This method performs a full scan of the cache and removes all expired entries.
    /// It should be called periodically (e.g., via a reminder) to prevent memory leaks
    /// from entries that are never accessed after expiration.
    /// </para>
    /// <para>
    /// Note: With the lazy expiry cleanup during access operations and probabilistic cleanup,
    /// full cleanup is less critical but still useful for removing entries that are never 
    /// accessed again after expiring.
    /// </para>
    /// </remarks>
    public void CleanupCache()
    {
        if (!_enableCache)
            return;

        var currentTicks = Environment.TickCount64;
        var expiredKeys = new List<CacheKey>();

        foreach (var kvp in _cache)
        {
            if (kvp.Value == null || kvp.Value.IsExpired(currentTicks))
            {
                expiredKeys.Add(kvp.Key);
            }
        }

        var expiredCount = expiredKeys.Count;
        if (expiredCount > 0)
        {
            _logger.LogDebug("Releasing cache from ({Count}) expired objects...", expiredCount);

            foreach (var key in expiredKeys)
            {
                _cache.TryRemove(key, out _);
            }

            var remainingCount = _cache.Count;
            _logger.LogDebug(
                "Finish cache cleanup, ({Count}) non-expired objects are still in the memory",
                remainingCount);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// This method iterates through all cache entries to find items of the specified type
    /// and subject. It also performs opportunistic cleanup of expired entries encountered
    /// during the scan.
    /// </para>
    /// <para>
    /// <strong>Performance Note:</strong> This operation is O(n) where n is the total number
    /// of cache entries. For large caches, consider using a secondary index if this method
    /// is called frequently.
    /// </para>
    /// </remarks>
    public List<T>? GetCachedItems<T>(string? subject) where T : class
    {
        if (!_enableCache)
            return null;

        var result = new List<T>();
        var expiredKeys = new List<CacheKey>();
        var currentTicks = Environment.TickCount64;
        var targetType = typeof(T);
        var normalizedSubject = subject ?? string.Empty;

        foreach (var kvp in _cache)
        {
            if (kvp.Value == null)
            {
                expiredKeys.Add(kvp.Key);
                continue;
            }

            // Check if this entry matches the type and subject using ReferenceEquals for type
            if (ReferenceEquals(kvp.Key.Type, targetType) &&
                string.Equals(kvp.Key.Subject, normalizedSubject, StringComparison.Ordinal))
            {
                if (!kvp.Value.IsExpired(currentTicks) && kvp.Value.Data is T typedData)
                {
                    result.Add(typedData);
                }
                else
                {
                    expiredKeys.Add(kvp.Key);
                }
            }
        }

        // Opportunistic cleanup
        if (expiredKeys.Count > 0)
        {
            _logger.LogInformation("Releasing cache from ({Count}) expired objects...", expiredKeys.Count);

            foreach (var key in expiredKeys)
            {
                _cache.TryRemove(key, out _);
            }

            var remainingCount = _cache.Count;
            _logger.LogInformation(
                "Finish cache cleanup, ({Count}) non-expired objects are still in the memory",
                remainingCount);
        }

        return result;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Result of a cache lookup operation.
    /// </summary>
    private readonly struct CacheLookupResult<T>
    {
        public readonly bool Found;
        public readonly T? Value;

        public CacheLookupResult(bool found, T? value)
        {
            Found = found;
            Value = value;
        }
    }

    /// <summary>
    /// Attempts to get an item from the cache with validation context (without locking).
    /// </summary>
    /// <typeparam name="T">The type of the cached item.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="currentTicks">The current tick count for expiry check.</param>
    /// <param name="syncValidateWithContext">Optional synchronous validation function with context.</param>
    /// <returns>A result indicating whether the item was found and its value.</returns>
    /// <remarks>
    /// This method performs lock-free cache lookup with optional synchronous validation.
    /// If validation is provided and fails, the entry is removed from cache.
    /// Async validation is handled separately in the caller to maintain the fast path.
    /// </remarks>
    private CacheLookupResult<T> TryGetFromCacheWithContext<T>(
        CacheKey key,
        long currentTicks,
        Func<T, CacheValidationContext, bool>? syncValidateWithContext) where T : class
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (!entry.IsExpired(currentTicks))
            {
                var data = entry.Data;

                // Type check - data can be null or must be of type T
                if (data == null || data is T)
                {
                    var typedData = data as T;

                    // Synchronous validation with context if provided
                    if (syncValidateWithContext is not null && typedData is not null)
                    {
                        var context = new CacheValidationContext(entry.ExpiryTicks, currentTicks, _ttlMs);
                        if (!syncValidateWithContext(typedData, context))
                        {
                            // Validation failed - remove from cache
                            _cache.TryRemove(key, out _);
                            return new CacheLookupResult<T>(false, default);
                        }
                    }

                    // Renew the entry (best-effort atomic replacement)
                    var renewedEntry = entry.Renew(currentTicks, _ttlMs);
                    _cache.TryUpdate(key, renewedEntry, entry);

                    return new CacheLookupResult<T>(true, typedData);
                }
                else
                {
                    _logger.LogWarning(
                        "Found cached item (id = '{EntityId}'); however its type '{ActualType}' not as expected '{ExpectedType}'",
                        key.EntityId, data.GetType().Name, typeof(T).Name);
                }
            }

            // Entry is expired or invalid - remove it
            _cache.TryRemove(key, out _);
        }

        return new CacheLookupResult<T>(false, default);
    }

    /// <summary>
    /// Attempts to get an item from cache with async validation that includes context.
    /// </summary>
    /// <typeparam name="T">The type of the cached item.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="currentTicks">The current tick count for expiry check.</param>
    /// <param name="syncValidateWithContext">Optional synchronous validation function with context.</param>
    /// <param name="asyncValidateWithContext">Optional asynchronous validation function with context.</param>
    /// <returns>A result indicating whether the item was found and its value.</returns>
    /// <remarks>
    /// This method is used during the double-check after lock acquisition.
    /// It performs both sync and async validation if provided.
    /// </remarks>
    private async ValueTask<CacheLookupResult<T>> TryGetFromCacheWithContextAsync<T>(
        CacheKey key,
        long currentTicks,
        Func<T, CacheValidationContext, bool>? syncValidateWithContext,
        Func<T, CacheValidationContext, Task<bool>>? asyncValidateWithContext) where T : class
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (!entry.IsExpired(currentTicks))
            {
                var data = entry.Data;

                // Type check - data can be null or must be of type T
                if (data == null || data is T)
                {
                    var typedData = data as T;
                    var context = new CacheValidationContext(entry.ExpiryTicks, currentTicks, _ttlMs);

                    // Synchronous validation with context if provided
                    if (syncValidateWithContext is not null && typedData is not null)
                    {
                        if (!syncValidateWithContext(typedData, context))
                        {
                            // Sync validation failed
                            _cache.TryRemove(key, out _);
                            return new CacheLookupResult<T>(false, default);
                        }
                    }

                    // Async validation with context if provided
                    if (asyncValidateWithContext is not null && typedData is not null)
                    {
                        if (!await asyncValidateWithContext(typedData, context).ConfigureAwait(false))
                        {
                            // Async validation failed
                            _cache.TryRemove(key, out _);
                            return new CacheLookupResult<T>(false, default);
                        }
                    }

                    // All validations passed - renew the entry
                    var renewedEntry = entry.Renew(currentTicks, _ttlMs);
                    _cache.TryUpdate(key, renewedEntry, entry);

                    return new CacheLookupResult<T>(true, typedData);
                }
                else
                {
                    _logger.LogWarning(
                        "Found cached item (id = '{EntityId}'); however its type '{ActualType}' not as expected '{ExpectedType}'",
                        key.EntityId, data.GetType().Name, typeof(T).Name);
                }
            }

            // Entry is expired or invalid - remove it
            _cache.TryRemove(key, out _);
        }

        return new CacheLookupResult<T>(false, default);
    }

    /// <summary>
    /// Loads an item using the single-flight pattern with validation context.
    /// </summary>
    /// <typeparam name="T">The type of the cached item.</typeparam>
    /// <param name="id">The entity ID.</param>
    /// <param name="key">The cache key.</param>
    /// <param name="cacheMissRecovery">The recovery function to execute on cache miss.</param>
    /// <param name="syncValidateWithContext">Optional synchronous validation function with context.</param>
    /// <param name="asyncValidateWithContext">Optional asynchronous validation function with context.</param>
    /// <returns>The cached or recovered item.</returns>
    /// <remarks>
    /// <para>
    /// This method implements the double-check locking pattern:
    /// <list type="number">
    ///   <item>Acquire per-key lock</item>
    ///   <item>Check cache again (another caller may have populated it while waiting)</item>
    ///   <item>If still miss, execute recovery and store result</item>
    ///   <item>Release lock</item>
    /// </list>
    /// </para>
    /// <para>
    /// The double-check is critical for efficiency - without it, all waiting callers
    /// would execute the recovery function even though only the first one needs to.
    /// </para>
    /// </remarks>
    private async Task<T?> LoadItemWithSingleFlightWithContextAsync<T>(
        Guid id,
        CacheKey key,
        Func<Guid, Task<T>> cacheMissRecovery,
        Func<T, CacheValidationContext, bool>? syncValidateWithContext,
        Func<T, CacheValidationContext, Task<bool>>? asyncValidateWithContext) where T : class
    {
        // Acquire per-key lock - only one caller per key proceeds past this point
        using (await _keyedLock.AcquireAsync(key).ConfigureAwait(false))
        {
            var currentTicks = Environment.TickCount64;

            // Double-check: Another caller may have populated the cache while we were waiting
            var cachedResult = await TryGetFromCacheWithContextAsync<T>(
                key, currentTicks, syncValidateWithContext, asyncValidateWithContext).ConfigureAwait(false);
            if (cachedResult.Found)
            {
                TriggerProbabilisticCleanup();
                return cachedResult.Value;
            }

            // Still a cache miss - we're the first caller, execute recovery
            var result = await cacheMissRecovery(id).ConfigureAwait(false);
            var newEntry = new CacheEntry(result, Environment.TickCount64, _ttlMs);
            _cache.AddOrUpdate(key, newEntry, (_, _) => newEntry);

            TriggerProbabilisticCleanup();
            return result;
        }
        // Lock is automatically released here via IDisposable
    }

    /// <summary>
    /// Loads an item without per-key locking (allows parallel recovery).
    /// </summary>
    /// <typeparam name="T">The type of the cached item.</typeparam>
    /// <param name="id">The entity ID.</param>
    /// <param name="key">The cache key.</param>
    /// <param name="cacheMissRecovery">The recovery function to execute.</param>
    /// <returns>The recovered item.</returns>
    /// <remarks>
    /// This method allows multiple concurrent callers to execute the recovery function
    /// for the same key. The last caller's result is stored in cache.
    /// Use when recovery is cheap and idempotent.
    /// </remarks>
    private async Task<T?> LoadItemWithoutLockAsync<T>(
        Guid id,
        CacheKey key,
        Func<Guid, Task<T>> cacheMissRecovery) where T : class
    {
        var result = await cacheMissRecovery(id).ConfigureAwait(false);
        var newEntry = new CacheEntry(result, Environment.TickCount64, _ttlMs);
        _cache.AddOrUpdate(key, newEntry, (_, _) => newEntry);

        TriggerProbabilisticCleanup();
        return result;
    }

    /// <summary>
    /// Triggers probabilistic cleanup to prevent unbounded cache growth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the cache exceeds the configured threshold entries,
    /// this method has a 1-in-N probability of triggering a cleanup on each access.
    /// </para>
    /// <para>
    /// This approach amortizes cleanup cost across many operations while preventing
    /// the cache from growing indefinitely.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TriggerProbabilisticCleanup()
    {
        // Only check periodically to avoid overhead
        var counter = Interlocked.Increment(ref _accessCounter);

        if (counter >= ProbabilisticCleanupChance)
        {
            Interlocked.Exchange(ref _accessCounter, 0);

            if (_cache.Count > _probabilisticCleanupThreshold)
            {
                // Fire and forget cleanup on thread pool to avoid blocking the caller
                ThreadPool.QueueUserWorkItem(_ => CleanupCacheInternal());
            }
        }
    }

    /// <summary>
    /// Internal cleanup that logs at Debug level (used for probabilistic cleanup).
    /// </summary>
    private void CleanupCacheInternal()
    {
        if (!_enableCache)
            return;

        var currentTicks = Environment.TickCount64;
        var cleanedCount = 0;

        foreach (var kvp in _cache)
        {
            if (kvp.Value == null || kvp.Value.IsExpired(currentTicks))
            {
                if (_cache.TryRemove(kvp.Key, out _))
                {
                    cleanedCount++;
                }
            }
        }

        if (cleanedCount > 0)
        {
            _logger.LogDebug(
                "Probabilistic cleanup removed {Count} expired entries, {Remaining} entries remaining",
                cleanedCount, _cache.Count);
        }
    }

    #endregion
}
