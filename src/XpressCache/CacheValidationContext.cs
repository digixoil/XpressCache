namespace XpressCache;

/// <summary>
/// Provides context information for cache entry validation, including timing data for time-based validation logic.
/// </summary>
/// <remarks>
/// <para>
/// This context is passed to validation callbacks when validating cached items, allowing custom
/// validation logic to make decisions based on:
/// </para>
/// <list type="bullet">
///   <item>
///     <term>Entry Age</term>
///     <description>How long the entry has been in cache (via <see cref="Age"/>)</description>
///   </item>
///   <item>
///     <term>Time Until Expiry</term>
///     <description>How much time remains before the entry expires (via <see cref="TimeToExpiry"/>)</description>
///   </item>
///   <item>
///     <term>Expiry Progress</term>
///     <description>What percentage of the TTL has elapsed (via <see cref="ExpiryProgress"/>)</description>
///   </item>
/// </list>
/// <para>
/// <strong>Time Values:</strong>
/// </para>
/// <para>
/// All time values are based on <see cref="Environment.TickCount64"/> which provides millisecond precision
/// without the overhead of <see cref="DateTime"/> operations. This is efficient but means the values
/// represent elapsed time since system start, not wall-clock time.
/// </para>
/// <para>
/// <strong>Usage Examples:</strong>
/// </para>
/// <code>
/// // Invalidate entries that are more than 50% through their TTL
/// syncValidate: (item, ctx) => ctx.ExpiryProgress &lt; 0.5
/// 
/// // Invalidate entries older than 1 minute regardless of TTL
/// syncValidate: (item, ctx) => ctx.Age.TotalMinutes &lt; 1
/// 
/// // Refresh entries with less than 30 seconds remaining
/// asyncValidate: async (item, ctx) => 
/// {
///     if (ctx.TimeToExpiry.TotalSeconds &lt; 30)
///         return false; // Force refresh
///     return await ValidateItemAsync(item);
/// }
/// </code>
/// </remarks>
/// <seealso cref="ICacheStore.LoadItem{T}"/>
/// <seealso cref="CacheStore"/>
public readonly struct CacheValidationContext
{
    /// <summary>
    /// The tick count (from <see cref="Environment.TickCount64"/>) at which the cache entry will expire.
    /// </summary>
    /// <remarks>
    /// This value represents the absolute expiry time in the tick count space.
    /// Compare with <see cref="CurrentTicks"/> to determine time remaining.
    /// </remarks>
    public long ExpiryTicks { get; }

    /// <summary>
    /// The current tick count (from <see cref="Environment.TickCount64"/>) at the time of validation.
    /// </summary>
    /// <remarks>
    /// This value can be compared with <see cref="ExpiryTicks"/> to calculate time remaining,
    /// or used as a reference point for other time-based calculations.
    /// </remarks>
    public long CurrentTicks { get; }

    /// <summary>
    /// The configured time-to-live for cache entries in milliseconds.
    /// </summary>
    /// <remarks>
    /// This is the configured TTL from <see cref="CacheStoreOptions.DefaultTtlMs"/>,
    /// representing the total lifetime a cache entry is allowed before expiration.
    /// </remarks>
    public long TtlMs { get; }

    /// <summary>
    /// Gets the time remaining until the entry expires.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="TimeSpan.Zero"/> if the entry has already expired.
    /// </remarks>
    public TimeSpan TimeToExpiry
    {
        get
        {
            var remaining = ExpiryTicks - CurrentTicks;
            return remaining > 0 ? TimeSpan.FromMilliseconds(remaining) : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Gets how long the entry has been in cache (approximate).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is calculated as <c>TTL - TimeToExpiry</c>, which gives an approximation
    /// of the entry's age. Note that if the entry's expiry has been renewed by
    /// previous accesses (sliding expiration), this represents time since the last renewal,
    /// not time since initial creation.
    /// </para>
    /// <para>
    /// Returns <see cref="TimeSpan.Zero"/> if the entry was just created or renewed.
    /// </para>
    /// </remarks>
    public TimeSpan Age
    {
        get
        {
            var elapsed = TtlMs - (ExpiryTicks - CurrentTicks);
            return elapsed > 0 ? TimeSpan.FromMilliseconds(elapsed) : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Gets the progress toward expiration as a value between 0.0 and 1.0.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>0.0</c> - Entry was just created/renewed</item>
    ///   <item><c>0.5</c> - Entry is halfway to expiration</item>
    ///   <item><c>1.0</c> - Entry has expired (or is about to)</item>
    /// </list>
    /// <para>
    /// Use this to implement progressive staleness policies, such as refreshing
    /// entries proactively when they reach a certain percentage of their TTL.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Refresh when 75% of TTL has elapsed
    /// syncValidate: (item, ctx) => ctx.ExpiryProgress &lt; 0.75
    /// </code>
    /// </example>
    public double ExpiryProgress
    {
        get
        {
            if (TtlMs <= 0) return 1.0;
            var elapsed = TtlMs - (ExpiryTicks - CurrentTicks);
            var progress = (double)elapsed / TtlMs;
            return Math.Clamp(progress, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheValidationContext"/> struct.
    /// </summary>
    /// <param name="expiryTicks">The tick count at which the entry expires.</param>
    /// <param name="currentTicks">The current tick count.</param>
    /// <param name="ttlMs">The configured TTL in milliseconds.</param>
    public CacheValidationContext(long expiryTicks, long currentTicks, long ttlMs)
    {
        ExpiryTicks = expiryTicks;
        CurrentTicks = currentTicks;
        TtlMs = ttlMs;
    }

    /// <summary>
    /// Returns a string representation of the validation context for debugging.
    /// </summary>
    public override string ToString()
        => $"CacheValidationContext {{ Age={Age}, TimeToExpiry={TimeToExpiry}, ExpiryProgress={ExpiryProgress:P1} }}";
}
