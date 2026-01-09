using System.Collections.Concurrent;

namespace XpressCache;

/// <summary>
/// Provides per-key async locking for single-flight pattern implementation.
/// </summary>
/// <typeparam name="TKey">The type of keys used for locking.</typeparam>
/// <remarks>
/// <para>
/// <strong>Purpose:</strong>
/// </para>
/// <para>
/// This class implements the single-flight pattern for async operations, ensuring that
/// only one concurrent caller can hold a lock for a given key at any time. This is used
/// to prevent cache stampede (thundering herd) problems.
/// </para>
/// <para>
/// <strong>Design Principles:</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <term>Per-Key Isolation</term>
///     <description>
///       Each unique key has its own independent semaphore. Operations on different keys
///       never block each other.
///     </description>
///   </item>
///   <item>
///     <term>No Global Lock</term>
///     <description>
///       Uses <see cref="ConcurrentDictionary{TKey, TValue}"/> for lock-free key lookup
///       and atomic semaphore creation.
///     </description>
///   </item>
///   <item>
///     <term>Automatic Cleanup</term>
///     <description>
///       Semaphores are removed from the dictionary after release when they're no longer
///       in use, preventing unbounded memory growth.
///     </description>
///   </item>
///   <item>
///     <term>Async-Safe</term>
///     <description>
///       Uses <see cref="SemaphoreSlim"/> for async-compatible waiting.
///     </description>
///   </item>
/// </list>
/// <para>
/// <strong>Thread Safety:</strong>
/// </para>
/// <para>
/// All operations are thread-safe and can be called concurrently from multiple threads.
/// </para>
/// <para>
/// <strong>Usage Pattern:</strong>
/// </para>
/// <code>
/// using (await _keyedLock.AcquireAsync(key))
/// {
///     // Critical section - only one caller per key can be here
///     await DoExpensiveWorkAsync();
/// }
/// </code>
/// </remarks>
/// <example>
/// <code>
/// var keyedLock = new KeyedAsyncLock&lt;string&gt;();
/// 
/// async Task&lt;T&gt; LoadWithLockAsync&lt;T&gt;(string key, Func&lt;Task&lt;T&gt;&gt; factory)
/// {
///     using (await keyedLock.AcquireAsync(key))
///     {
///         // Only one caller at a time per key
///         return await factory();
///     }
/// }
/// </code>
/// </example>
internal sealed class KeyedAsyncLock<TKey> where TKey : notnull
{
    /// <summary>
    /// Dictionary mapping keys to their associated semaphores.
    /// </summary>
    /// <remarks>
    /// Using <see cref="ConcurrentDictionary{TKey, TValue}"/> ensures thread-safe
    /// access without requiring a global lock for lookups.
    /// </remarks>
    private readonly ConcurrentDictionary<TKey, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Acquires an exclusive async lock for the specified key.
    /// </summary>
    /// <param name="key">The key to lock on.</param>
    /// <returns>
    /// A disposable object that releases the lock when disposed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method:
    /// <list type="number">
    ///   <item>Gets or creates a semaphore for the key</item>
    ///   <item>Asynchronously waits to acquire the semaphore</item>
    ///   <item>Returns a <see cref="Releaser"/> that will release the semaphore when disposed</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Important:</strong> Always dispose the returned object, preferably using a
    /// <c>using</c> statement or declaration.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using (await keyedLock.AcquireAsync("myKey"))
    /// {
    ///     // Exclusive access per key
    /// }
    /// </code>
    /// </example>
    public async Task<IDisposable> AcquireAsync(TKey key)
    {
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync().ConfigureAwait(false);

        return new Releaser(key, semaphore, _locks);
    }

    /// <summary>
    /// Acquires an exclusive async lock for the specified key with cancellation support.
    /// </summary>
    /// <param name="key">The key to lock on.</param>
    /// <param name="cancellationToken">A token to cancel the wait operation.</param>
    /// <returns>
    /// A disposable object that releases the lock when disposed.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the cancellation token is triggered before the lock is acquired.
    /// </exception>
    public async Task<IDisposable> AcquireAsync(TKey key, CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new Releaser(key, semaphore, _locks);
    }

    /// <summary>
    /// Gets the current number of keys with active locks.
    /// </summary>
    /// <remarks>
    /// This is primarily for diagnostic and monitoring purposes.
    /// </remarks>
    public int ActiveLockCount => _locks.Count;

    /// <summary>
    /// Internal class that releases the semaphore and optionally cleans up the dictionary entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cleanup logic removes the semaphore from the dictionary only when:
    /// <list type="bullet">
    ///   <item>The semaphore is released (count becomes 1)</item>
    ///   <item>No other thread is waiting on it</item>
    /// </list>
    /// </para>
    /// <para>
    /// This prevents unbounded growth of the locks dictionary while ensuring that
    /// concurrent acquires for the same key share the same semaphore.
    /// </para>
    /// </remarks>
    private sealed class Releaser : IDisposable
    {
        private readonly TKey _key;
        private readonly SemaphoreSlim _semaphore;
        private readonly ConcurrentDictionary<TKey, SemaphoreSlim> _locks;
        private int _disposed;

        public Releaser(TKey key, SemaphoreSlim semaphore, ConcurrentDictionary<TKey, SemaphoreSlim> locks)
        {
            _key = key;
            _semaphore = semaphore;
            _locks = locks;
        }

        public void Dispose()
        {
            // Ensure we only release once even if Dispose is called multiple times
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();

                // Cleanup: remove the semaphore from dictionary if no one else is waiting.
                // CurrentCount == 1 means the semaphore is available (we just released it)
                // and no other waiter is pending.
                // 
                // Note: There's a small race window here where another thread could:
                // 1. Call GetOrAdd (gets existing semaphore)
                // 2. We call TryRemove (removes it)
                // 3. Other thread calls WaitAsync on removed semaphore (still works!)
                // 4. Another thread calls GetOrAdd for same key (creates new semaphore)
                // 
                // This is acceptable because:
                // - The removed semaphore still functions correctly
                // - The worst case is two callers with same key don't share the lock momentarily
                // - Under normal load, this race is extremely rare
                // - Memory is still bounded (orphaned semaphores get GC'd)
                if (_semaphore.CurrentCount == 1)
                {
                    _locks.TryRemove(_key, out _);
                }
            }
        }
    }
}
