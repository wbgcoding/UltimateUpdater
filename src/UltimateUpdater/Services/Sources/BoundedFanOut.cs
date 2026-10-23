namespace UltimateUpdater.Services.Sources;

/// <summary>
/// One registry lookup per installed package, run together but never all at once. A bare
/// <c>Task.WhenAll</c> over the installed list fires as many simultaneous HTTP requests as the machine
/// happens to have packages. On a well-equipped dev machine that is dozens of connections to one host in
/// the same instant, which is both rude to the registry and the fastest way to be rate-limited into a
/// scan full of holes. The cap is per call, so two sources scanning in parallel still get their own.
/// </summary>
internal static class BoundedFanOut
{
    /// <summary>Enough to hide the latency of a slow lookup, far below what any registry considers abuse.</summary>
    public const int DefaultLimit = 8;

    public static async Task<IReadOnlyList<TResult>> SelectAsync<TSource, TResult>(
        IEnumerable<TSource> items,
        Func<TSource, CancellationToken, Task<TResult>> selector,
        CancellationToken cancellationToken,
        int limit = DefaultLimit)
    {
        using var gate = new SemaphoreSlim(limit);

        return await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await selector(item, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));
    }
}
