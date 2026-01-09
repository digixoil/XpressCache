namespace XpressCache;

/// <summary>
/// Specifies the cache loading behavior for controlling cache stampede prevention on a per-call basis.
/// </summary>
/// <remarks>
/// <para>
/// This enum allows callers to override the <see cref="CacheStoreOptions.PreventCacheStampedeByDefault"/> 
/// setting for individual <see cref="ICacheStore.LoadItem{T}"/> calls.
/// </para>
/// <para>
/// <strong>Usage Guidance:</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <term><see cref="Default"/></term>
///     <description>
///       Most callers should use this value. The behavior is controlled by 
///       <see cref="CacheStoreOptions.PreventCacheStampedeByDefault"/>.
///     </description>
///   </item>
///   <item>
///     <term><see cref="PreventStampede"/></term>
///     <description>
///       Use when the recovery function is expensive (database calls, API calls, complex computations)
///       and you want to ensure only one concurrent caller executes recovery for a given key.
///     </description>
///   </item>
///   <item>
///     <term><see cref="AllowParallelLoad"/></term>
///     <description>
///       Use when the recovery function is cheap, idempotent, and concurrent execution
///       is acceptable. This avoids lock acquisition overhead.
///     </description>
///   </item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Use store-wide default behavior (recommended for most cases)
/// var item = await cacheStore.LoadItem(id, "subject", RecoveryAsync);
/// 
/// // Explicitly prevent stampede for expensive operations
/// var item = await cacheStore.LoadItem(id, "subject", ExpensiveRecoveryAsync, 
///     behavior: CacheLoadBehavior.PreventStampede);
/// 
/// // Allow parallel loads for cheap, idempotent operations
/// var item = await cacheStore.LoadItem(id, "subject", CheapRecoveryAsync, 
///     behavior: CacheLoadBehavior.AllowParallelLoad);
/// </code>
/// </example>
/// <seealso cref="CacheStoreOptions"/>
/// <seealso cref="ICacheStore.LoadItem{T}"/>
public enum CacheLoadBehavior
{
    /// <summary>
    /// Uses the store-wide default behavior configured in <see cref="CacheStoreOptions.PreventCacheStampedeByDefault"/>.
    /// </summary>
    /// <remarks>
    /// This is the recommended value for most callers. It allows centralized control over
    /// stampede prevention behavior.
    /// </remarks>
    Default = 0,

    /// <summary>
    /// Forces per-key single-flight locking regardless of the store-wide default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When this value is specified:
    /// <list type="bullet">
    ///   <item>Only one concurrent caller executes the cache-miss recovery function per key</item>
    ///   <item>Other callers for the same key wait for the first caller to complete</item>
    ///   <item>Waiting callers receive the cached result without re-executing recovery</item>
    /// </list>
    /// </para>
    /// <para>
    /// Use this for expensive recovery operations such as:
    /// <list type="bullet">
    ///   <item>Database queries</item>
    ///   <item>External API calls</item>
    ///   <item>Complex computations</item>
    ///   <item>Operations with rate limits</item>
    /// </list>
    /// </para>
    /// </remarks>
    PreventStampede = 1,

    /// <summary>
    /// Explicitly allows parallel cache-miss recovery executions regardless of the store-wide default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When this value is specified:
    /// <list type="bullet">
    ///   <item>Multiple concurrent callers may execute the recovery function simultaneously</item>
    ///   <item>No per-key locking overhead is incurred</item>
    ///   <item>The last caller's result is stored in cache (eventual consistency)</item>
    /// </list>
    /// </para>
    /// <para>
    /// Use this only when:
    /// <list type="bullet">
    ///   <item>The recovery function is cheap (in-memory operations)</item>
    ///   <item>The recovery function is idempotent (produces same result for same input)</item>
    ///   <item>Duplicate work is acceptable</item>
    ///   <item>Lock acquisition overhead is a concern</item>
    /// </list>
    /// </para>
    /// </remarks>
    AllowParallelLoad = 2
}
