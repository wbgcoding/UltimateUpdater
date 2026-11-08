namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Holds the result of a tool lookup, but only a successful one: an installed tool does not vanish while the
/// app runs, whereas a missing one may be installed minutes later and must be found by the next scan without
/// a restart. While nothing is found, every read looks again.
/// </summary>
internal sealed class FoundOnceCache<T>(Func<T> resolve, Func<T, bool> isFound)
{
    private sealed class Holder(T value)
    {
        public T Value { get; } = value;
    }

    private volatile Holder? _found;

    public T Value
    {
        get
        {
            if (_found is { } cached)
                return cached.Value;

            var value = resolve();
            if (isFound(value))
                _found = new Holder(value);

            return value;
        }
    }
}
