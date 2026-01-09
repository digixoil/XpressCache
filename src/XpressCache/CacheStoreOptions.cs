namespace XpressCache;

/// <summary>
/// Configuration options for the <see cref="CacheStore"/> service.
/// </summary>
/// <remarks>
/// <para>
/// These options control the behavior of the cache store, including cache stampede prevention
/// and expiration settings.
/// </para>
/// <para>
/// <strong>Cache Stampede Prevention:</strong>
/// </para>
/// <para>
/// When <see cref="PreventCacheStampedeByDefault"/> is enabled (default), the cache uses 
/// per-key single-flight locking to prevent multiple concurrent callers from simultaneously 
/// executing the same cache-miss recovery function. This solves the classic "thundering herd" 
/// problem where:
/// <list type="bullet">
///   <item>Multiple concurrent callers check cache ? all miss</item>
///   <item>All callers invoke the expensive recovery function</item>
///   <item>All callers compute the same data</item>
///   <item>Wasted work and potential resource contention</item>
/// </list>
/// </para>
/// <para>
/// With single-flight locking enabled:
/// <list type="bullet">
///   <item>First caller acquires the per-key lock and executes recovery</item>
///   <item>Concurrent callers wait for the lock</item>
///   <item>After lock is released, waiting callers find the cached result</item>
///   <item>No redundant work is performed</item>
/// </list>
/// </para>
/// </remarks>
/// <seealso cref="CacheLoadBehavior"/>
/// <seealso cref="ICacheStore"/>
public sealed class CacheStoreOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether cache stampede prevention is enabled by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <c>true</c> (default), the <see cref="ICacheStore.LoadItem{T}"/> method uses per-key 
    /// single-flight locking to prevent multiple concurrent cache-miss recovery executions for 
    /// the same cache key.
    /// </para>
    /// <para>
    /// This can be overridden on a per-call basis using the <see cref="CacheLoadBehavior"/> parameter
    /// in <see cref="ICacheStore.LoadItem{T}"/>.
    /// </para>
    /// <para>
    /// Set to <c>false</c> to disable stampede prevention by default. Individual calls can still
    /// opt-in using <see cref="CacheLoadBehavior.PreventStampede"/>.
    /// </para>
    /// </remarks>
    /// <value>
    /// <c>true</c> to enable per-key single-flight locking by default; otherwise, <c>false</c>.
    /// Default is <c>true</c>.
    /// </value>
    public bool PreventCacheStampedeByDefault { get; init; } = true;

    /// <summary>
    /// Gets or sets the default time-to-live for cache entries in milliseconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entries automatically expire after this duration. Accessing an entry renews its TTL
    /// (sliding expiration).
    /// </para>
    /// </remarks>
    /// <value>
    /// The default TTL in milliseconds. Default is 600000 (10 minutes).
    /// </value>
    public long DefaultTtlMs { get; init; } = 10 * 60 * 1000;

    /// <summary>
    /// Gets or sets the initial capacity of the cache dictionary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A higher initial capacity reduces rehashing operations but consumes more memory upfront.
    /// Set this based on expected cache size.
    /// </para>
    /// </remarks>
    /// <value>
    /// The initial capacity. Default is 256.
    /// </value>
    public int InitialCapacity { get; init; } = 256;

    /// <summary>
    /// Gets or sets the threshold for triggering probabilistic cleanup during read operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the cache size exceeds this threshold, there's a probability of triggering
    /// cleanup on each read operation to prevent unbounded growth.
    /// </para>
    /// </remarks>
    /// <value>
    /// The cleanup threshold. Default is 1000.
    /// </value>
    public int ProbabilisticCleanupThreshold { get; init; } = 1000;
}
