using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace XpressCache;

/// <summary>
/// Interface for a high-performance, thread-safe cache store for caching entities.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Purpose:</strong>
/// </para>
/// <para>
/// This cache store provides fast, in-memory caching of entities identified by:
/// <list type="bullet">
///   <item><term>Entity ID</term><description>A <see cref="Guid"/> uniquely identifying the entity</description></item>
///   <item><term>Type</term><description>The .NET type of the cached item (generic parameter)</description></item>
///   <item><term>Subject</term><description>An optional string for additional categorization</description></item>
/// </list>
/// </para>
/// 
/// <para>
/// <strong>Usage Patterns:</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <term>Read-Through Caching</term>
///     <description>
///       Use <see cref="LoadItem{T}(Guid?, string, Func{Guid, Task{T}}, Func{T, Task{bool}}, CacheLoadBehavior)"/> 
///       with a cache-miss recovery function to automatically populate the cache on first access.
///     </description>
///   </item>
///   <item>
///     <term>Write-Through Caching</term>
///     <description>
///       Use <see cref="SetItem{T}"/> to explicitly store items in the cache.
///     </description>
///   </item>
///   <item>
///     <term>Cache Invalidation</term>
///     <description>
///       Use <see cref="RemoveItem{T}"/> to invalidate specific items, or <see cref="Clear"/>
///       to invalidate all items.
///     </description>
///   </item>
/// </list>
/// 
/// <para>
/// <strong>Thread Safety:</strong>
/// </para>
/// <para>
/// All operations on this interface are thread-safe and can be called concurrently
/// from multiple threads without external synchronization.
/// </para>
/// 
/// <para>
/// <strong>Cache Stampede Prevention:</strong>
/// </para>
/// <para>
/// This cache supports optional per-key single-flight locking to prevent the classic
/// cache stampede (thundering herd) problem. When enabled (default), only one concurrent
/// caller executes the cache-miss recovery function for a given key while other callers
/// wait for the result. This behavior is controlled by:
/// <list type="bullet">
///   <item>
///     <term>Store-wide default</term>
///     <description>
///       <see cref="CacheStoreOptions.PreventCacheStampedeByDefault"/> (default: <c>true</c>)
///     </description>
///   </item>
///   <item>
///     <term>Per-call override</term>
///     <description>
///       <see cref="CacheLoadBehavior"/> parameter in <see cref="LoadItem{T}(Guid?, string, Func{Guid, Task{T}}, Func{T, Task{bool}}, CacheLoadBehavior)"/>
///     </description>
///   </item>
/// </list>
/// </para>
/// 
/// <para>
/// <strong>Expiration:</strong>
/// </para>
/// <para>
/// Cached items have a default time-to-live (TTL) and are automatically expired.
/// Accessing an item renews its TTL, implementing a sliding expiration policy.
/// Renewal is best-effort: under high contention, expiry may not be extended,
/// but correctness is preserved as expired items are simply re-fetched.
/// </para>
/// </remarks>
/// <seealso cref="CacheStoreOptions"/>
/// <seealso cref="CacheLoadBehavior"/>
public interface ICacheStore
{
    /// <summary>
    /// Gets or sets a value indicating whether the cache is enabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When set to <c>false</c>, all cache operations become no-ops:
    /// <list type="bullet">
    ///   <item><see cref="LoadItem{T}(Guid?, string, Func{Guid, Task{T}}, Func{T, Task{bool}}, CacheLoadBehavior)"/> will always invoke the cache-miss recovery function</item>
    ///   <item><see cref="SetItem{T}"/> will do nothing</item>
    ///   <item><see cref="GetCachedItems{T}"/> will return <c>null</c></item>
    /// </list>
    /// </para>
    /// <para>
    /// Changing this property clears all cached items.
    /// </para>
    /// </remarks>
    bool EnableCache { get; set; }

    /// <summary>
    /// Loads an item from the cache, or retrieves it using the recovery function if not cached.
    /// </summary>
    /// <typeparam name="T">The type of the cached item.</typeparam>
    /// <param name="entityId">The unique identifier of the entity.</param>
    /// <param name="subject">An optional subject for additional categorization (null is normalized to empty string).</param>
    /// <param name="cacheMissRecovery">
    /// A function to retrieve the item if not found in cache. 
    /// Can be <c>null</c> if only checking cache without recovery.
    /// </param>
    /// <param name="customValidate">
    /// An optional validation function to verify the cached item is still valid.
    /// If this returns <c>false</c>, the cache-miss recovery function is invoked.
    /// </param>
    /// <param name="behavior">
    /// Specifies the cache loading behavior for stampede prevention.
    /// <see cref="CacheLoadBehavior.Default"/> uses the store-wide setting from 
    /// <see cref="CacheStoreOptions.PreventCacheStampedeByDefault"/>.
    /// </param>
    /// <returns>
    /// The cached item, the recovered item, or <c>default</c> if not found and no recovery function provided.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method implements a read-through caching pattern with optional custom validation
    /// and cache stampede prevention.
    /// </para>
    /// <para>
    /// <strong>Stampede Prevention Behavior:</strong>
    /// </para>
    /// <para>
    /// When stampede prevention is active (determined by <paramref name="behavior"/> and 
    /// <see cref="CacheStoreOptions.PreventCacheStampedeByDefault"/>), this method uses the
    /// double-check locking pattern:
    /// <list type="number">
    ///   <item>Check cache (fast path)</item>
    ///   <item>If miss, acquire per-key lock</item>
    ///   <item>Check cache again (another caller may have populated it)</item>
    ///   <item>If still miss, execute recovery and store result</item>
    ///   <item>Release lock</item>
    /// </list>
    /// </para>
    /// <para>
    /// Returns <see cref="ValueTask{T}"/> to avoid Task allocation on synchronous cache hits,
    /// improving performance for the common case where items are found in cache.
    /// </para>
    /// </remarks>
    /// <seealso cref="CacheLoadBehavior"/>
    /// <seealso cref="CacheStoreOptions.PreventCacheStampedeByDefault"/>
    ValueTask<T> LoadItem<T>(
        Guid? entityId, 
        string subject, 
        Func<Guid, Task<T>> cacheMissRecovery, 
        Func<T, Task<bool>> customValidate = null,
        CacheLoadBehavior behavior = CacheLoadBehavior.Default) where T : class;

    /// <summary>
    /// Stores an item in the cache.
    /// </summary>
    /// <typeparam name="T">The type of the item to cache.</typeparam>
    /// <param name="entityId">The unique identifier of the entity.</param>
    /// <param name="subject">An optional subject for additional categorization (null is normalized to empty string).</param>
    /// <param name="item">The item to cache.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// If an item with the same key already exists, it is replaced with the new item.
    /// </remarks>
    Task SetItem<T>(Guid? entityId, string subject, T item) where T : class;

    /// <summary>
    /// Removes all cached items.
    /// </summary>
    void Clear();

    /// <summary>
    /// Removes expired items from the cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method performs a full scan of the cache and removes all expired entries.
    /// It should be called periodically (e.g., via a timer or reminder) to prevent
    /// memory leaks from entries that are never accessed after expiration.
    /// </para>
    /// <para>
    /// Note: The implementation also performs lazy expiry cleanup during access operations,
    /// so calling this method frequently is not strictly necessary.
    /// </para>
    /// </remarks>
    void CleanupCache();

    /// <summary>
    /// Removes a specific item from the cache.
    /// </summary>
    /// <typeparam name="T">The type of the item to remove.</typeparam>
    /// <param name="entityId">The unique identifier of the entity.</param>
    /// <param name="subject">The subject that was used when storing the item (null is normalized to empty string).</param>
    /// <returns><c>true</c> if the item was found and removed; otherwise, <c>false</c>.</returns>
    bool RemoveItem<T>(Guid? entityId, string subject);

    /// <summary>
    /// Gets all cached items of a specific type and subject.
    /// </summary>
    /// <typeparam name="T">The type of items to retrieve.</typeparam>
    /// <param name="subject">The subject to filter by (null is normalized to empty string).</param>
    /// <returns>
    /// A list of all matching cached items, or <c>null</c> if the cache is disabled.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This operation is O(n) where n is the total number of cache entries.
    /// For large caches with frequent calls to this method, consider alternative
    /// data structures with secondary indices.
    /// </para>
    /// <para>
    /// Expired entries encountered during the scan are opportunistically removed.
    /// </para>
    /// </remarks>
    List<T> GetCachedItems<T>(string subject) where T : class;
}