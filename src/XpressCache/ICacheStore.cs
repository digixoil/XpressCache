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
///       Use <see cref="LoadItem{T}"/> 
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
///       <see cref="CacheLoadBehavior"/> parameter in <see cref="LoadItem{T}"/>
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
/// 
/// <para>
/// <strong>Custom Validation with Timing Context:</strong>
/// </para>
/// <para>
/// Validation callbacks receive a <see cref="CacheValidationContext"/> that
/// provides timing information including:
/// <list type="bullet">
///   <item><term>ExpiryTicks</term><description>When the entry is scheduled to expire</description></item>
///   <item><term>CurrentTicks</term><description>The current time reference</description></item>
///   <item><term>TimeToExpiry</term><description>Remaining time until expiration</description></item>
///   <item><term>Age</term><description>Approximate age of the cache entry</description></item>
///   <item><term>ExpiryProgress</term><description>Progress toward expiration (0.0 to 1.0)</description></item>
/// </list>
/// This enables sophisticated time-based validation logic such as proactive refresh
/// when entries are near expiration.
/// </para>
/// </remarks>
/// <seealso cref="CacheStoreOptions"/>
/// <seealso cref="CacheLoadBehavior"/>
/// <seealso cref="CacheValidationContext"/>
public interface ICacheStore
{
    /// <summary>
    /// Gets or sets a value indicating whether the cache is enabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When set to <c>false</c>, all cache operations become no-ops:
    /// <list type="bullet">
    ///   <item><see cref="LoadItem{T}"/> will always invoke the cache-miss recovery function</item>
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
    /// Loads an item from the cache with validation context, or retrieves it using the recovery function if not cached.
    /// </summary>
    /// <typeparam name="T">The type of the cached item. Must be a reference type.</typeparam>
    /// <param name="entityId">
    /// The unique identifier of the entity. If <c>null</c> or <see cref="Guid.Empty"/>,

    /// returns <c>default</c> without executing recovery.
    /// </param>
    /// <param name="subject">
    /// An optional subject for additional categorization. <c>null</c> is normalized to empty string.
    /// Items with the same entity ID but different subjects are cached separately.
    /// </param>
    /// <param name="cacheMissRecovery">
    /// A function to retrieve the item if not found in cache.
    /// Can be <c>null</c> if only checking cache without recovery.
    /// </param>
    /// <param name="syncValidateWithContext">
    /// An optional synchronous validation function that receives both the cached item and a
    /// <see cref="CacheValidationContext"/> with timing information. Returns <c>false</c> to
    /// invalidate the cached item and trigger recovery.
    /// This validator executes in the lock-free fast path for optimal performance.
    /// Prefer this over <paramref name="asyncValidateWithContext"/> when validation doesn't require async operations.
    /// </param>
    /// <param name="asyncValidateWithContext">
    /// An optional asynchronous validation function that receives both the cached item and a
    /// <see cref="CacheValidationContext"/> with timing information.
    /// Use only when validation requires async operations (e.g., database or API checks).
    /// When both validators are provided, <paramref name="syncValidateWithContext"/> runs first.
    /// </param>
    /// <param name="behavior">
    /// Specifies the cache loading behavior for stampede prevention.
    /// <see cref="CacheLoadBehavior.Default"/> uses the store-wide setting from 
    /// <see cref="CacheStoreOptions.PreventCacheStampedeByDefault"/>.
    /// </param>
    /// <returns>
    /// The cached item, the recovered item, or <c>default</c> if:
    /// <list type="bullet">
    ///   <item><paramref name="entityId"/> is <c>null</c> or <see cref="Guid.Empty"/></item>
    ///   <item>Item not found and no <paramref name="cacheMissRecovery"/> provided</item>
    ///   <item>Cache is disabled (<see cref="EnableCache"/> is <c>false</c>)</item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method implements a read-through caching pattern with optional custom validation
    /// and cache stampede prevention.
    /// </para>
    /// <para>
    /// <strong>Validation Context:</strong>
    /// </para>
    /// <para>
    /// The validation context provides timing information enabling sophisticated
    /// time-based validation logic such as:
    /// </para>
    /// <list type="bullet">
    ///   <item>Proactive refresh when entries are near expiration</item>
    ///   <item>Invalidating entries based on age rather than just TTL</item>
    ///   <item>Implementing custom staleness policies</item>
    /// </list>
    /// <para>
    /// <strong>Example - Proactive refresh at 75% TTL:</strong>
    /// </para>
    /// <code>
    /// var item = await cache.LoadItem&lt;User&gt;(
    ///     userId, "users", LoadUserAsync,
    ///     syncValidateWithContext: (user, ctx) => ctx.ExpiryProgress &lt; 0.75
    /// );
    /// </code>
    /// <para>
    /// <strong>Validation Priority:</strong>
    /// </para>
    /// <para>
    /// If both <paramref name="syncValidateWithContext"/> and <paramref name="asyncValidateWithContext"/> are provided,
    /// the synchronous validator is checked first. If it passes, the async validator is then checked.
    /// For best performance, use only synchronous validation when possible.
    /// </para>
    /// <para>
    /// <strong>Stampede Prevention Behavior:</strong>
    /// </para>
    /// <para>
    /// When stampede prevention is active (determined by <paramref name="behavior"/> and 
    /// <see cref="CacheStoreOptions.PreventCacheStampedeByDefault"/>), this method uses the
    /// double-check locking pattern:
    /// <list type="number">
    ///   <item>Check cache (fast path, no lock)</item>
    ///   <item>If miss, acquire per-key lock</item>
    ///   <item>Check cache again (another caller may have populated it)</item>
    ///   <item>If still miss, execute recovery and store result</item>
    ///   <item>Release lock</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>ValueTask Return:</strong>
    /// </para>
    /// <para>
    /// Returns <see cref="ValueTask{T}"/> to avoid <see cref="Task"/> allocation on synchronous cache hits,
    /// improving performance for the common case where items are found in cache.
    /// </para>
    /// </remarks>
    /// <seealso cref="CacheLoadBehavior"/>
    /// <seealso cref="CacheStoreOptions.PreventCacheStampedeByDefault"/>
    /// <seealso cref="CacheValidationContext"/>
    ValueTask<T?> LoadItem<T>(
        Guid? entityId,
        string? subject,
        Func<Guid, Task<T>>? cacheMissRecovery,
        Func<T, CacheValidationContext, bool>? syncValidateWithContext = null,
        Func<T, CacheValidationContext, Task<bool>>? asyncValidateWithContext = null,
        CacheLoadBehavior behavior = CacheLoadBehavior.Default) where T : class;

    /// <summary>
    /// Stores an item in the cache.
    /// </summary>
    /// <typeparam name="T">The type of the item to cache. Must be a reference type.</typeparam>
    /// <param name="entityId">
    /// The unique identifier of the entity. If <c>null</c> or <see cref="Guid.Empty"/>,

    /// the operation is a no-op.
    /// </param>
    /// <param name="subject">
    /// An optional subject for additional categorization. <c>null</c> is normalized to empty string.
    /// </param>
    /// <param name="item">The item to cache. Can be <c>null</c>.</param>
    /// <returns>A completed task.</returns>
    /// <remarks>
    /// <para>
    /// If an item with the same key (entity ID + type + subject) already exists, it is replaced.
    /// </para>
    /// <para>
    /// This operation is a no-op if <see cref="EnableCache"/> is <c>false</c>.
    /// </para>
    /// </remarks>
    Task SetItem<T>(Guid? entityId, string? subject, T item) where T : class;

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
    /// Note: The implementation also performs lazy expiry cleanup during access operations
    /// and probabilistic cleanup when the cache exceeds the configured threshold,
    /// so calling this method frequently is not strictly necessary.
    /// </para>
    /// </remarks>
    void CleanupCache();

    /// <summary>
    /// Removes a specific item from the cache.
    /// </summary>
    /// <typeparam name="T">The type of the item to remove.</typeparam>
    /// <param name="entityId">
    /// The unique identifier of the entity. If <c>null</c>, returns <c>false</c>.
    /// </param>
    /// <param name="subject">
    /// The subject that was used when storing the item. <c>null</c> is normalized to empty string.
    /// </param>
    /// <returns><c>true</c> if the item was found and removed; otherwise, <c>false</c>.</returns>
    bool RemoveItem<T>(Guid? entityId, string? subject);

    /// <summary>
    /// Gets all cached items of a specific type and subject.
    /// </summary>
    /// <typeparam name="T">The type of items to retrieve. Must be a reference type.</typeparam>
    /// <param name="subject">
    /// The subject to filter by. <c>null</c> is normalized to empty string.
    /// </param>
    /// <returns>
    /// A list of all matching cached items, or <c>null</c> if the cache is disabled.
    /// The list may be empty if no items match.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Performance Warning:</strong> This operation is O(n) where n is the total number 
    /// of cache entries. For large caches with frequent calls to this method, consider 
    /// alternative data structures with secondary indices.
    /// </para>
    /// <para>
    /// Expired entries encountered during the scan are opportunistically removed.
    /// </para>
    /// </remarks>
    List<T>? GetCachedItems<T>(string? subject) where T : class;
}