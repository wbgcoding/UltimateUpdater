using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// The few facts one npm packument read needs, as raw values straight from the document. Turning them
/// into an enrichment (date parsing, URL safety) is <see cref="PackageMetadataParsers"/>' job.
/// </summary>
/// <param name="PublishedText">The string at <c>time[version]</c>, or null.</param>
/// <param name="UnpackedSize">A positive integer at <c>versions[version].dist.unpackedSize</c>, or null.</param>
/// <param name="Tarball">The string at <c>versions[version].dist.tarball</c>, not yet checked for safety.</param>
/// <param name="Homepage">The string at the top-level <c>homepage</c>.</param>
/// <param name="RepositoryUrl">The string at the top-level <c>repository.url</c>.</param>
/// <param name="PeakBufferBytes">The largest read buffer the streaming read ever held (0 for an in-memory scan).</param>
internal sealed record NpmPackumentFacts(
    string? PublishedText,
    long? UnpackedSize,
    string? Tarball,
    string? Homepage,
    string? RepositoryUrl,
    int PeakBufferBytes = 0);

/// <summary>
/// Forward-only reader for an npm packument. A popular package's document runs to tens of megabytes, and
/// parsing it into a DOM (plus the string it came in) cost several times its size per row, with several
/// big packages enriched in parallel. This walks the bytes once with <see cref="Utf8JsonReader"/>, keeps
/// only the handful of fields the enrichment uses and drops everything else as it goes, so memory follows
/// the largest single JSON token, never the document. The whole document is still tokenised to the end, so
/// a malformed or truncated body fails exactly as a full parse would (null).
/// </summary>
internal sealed class NpmPackumentReader
{
    private const int InitialBufferBytes = 64 * 1024;

    /// <summary>
    /// One JSON token (in practice a readme string) has to fit the buffer whole. Anything larger than this is
    /// treated as hostile and refused, which keeps even the worst case far below the old full-document cost.
    /// </summary>
    internal const int MaxTokenBytes = 16 * 1024 * 1024;

    private enum RootKey { None, Time, Versions, Homepage, Repository }

    private enum Slot { None, Time, Homepage, RepositoryUrl, UnpackedSize, Tarball }

    private static ReadOnlySpan<byte> TimeName => "time"u8;
    private static ReadOnlySpan<byte> VersionsName => "versions"u8;
    private static ReadOnlySpan<byte> HomepageName => "homepage"u8;
    private static ReadOnlySpan<byte> RepositoryName => "repository"u8;
    private static ReadOnlySpan<byte> UrlName => "url"u8;
    private static ReadOnlySpan<byte> DistName => "dist"u8;
    private static ReadOnlySpan<byte> UnpackedSizeName => "unpackedSize"u8;
    private static ReadOnlySpan<byte> TarballName => "tarball"u8;

    private readonly byte[] _version;
    private JsonReaderState _state;
    private bool _sawToken;

    private RootKey _root;
    private bool _inTargetVersion;
    private bool _inDist;
    private Slot _slot;

    private string? _published;
    private long? _unpackedSize;
    private string? _tarball;
    private string? _homepage;
    private string? _repositoryUrl;

    private NpmPackumentReader(string version) => _version = Encoding.UTF8.GetBytes(version);

    /// <summary>
    /// Reads a packument from <paramref name="stream"/> in chunks through one reusable buffer. Returns null
    /// when the body is not valid JSON, when it is longer than <paramref name="maxBytes"/>
    /// (<paramref name="onExceeded"/> fires then), or when a single token is implausibly large. The read
    /// honours <paramref name="cancellationToken"/> on every chunk.
    /// </summary>
    internal static async Task<NpmPackumentFacts?> ReadAsync(
        Stream stream, string version, long maxBytes, CancellationToken cancellationToken, Action? onExceeded = null)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var reader = new NpmPackumentReader(version);
        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferBytes);
        var peak = buffer.Length;
        var filled = 0;
        long total = 0;

        try
        {
            while (true)
            {
                // Fill the buffer before scanning so a long token is not re-scanned after every small read.
                var ended = false;
                while (filled < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken);
                    if (read == 0)
                    {
                        ended = true;
                        break;
                    }

                    total += read;
                    if (total > maxBytes)
                    {
                        onExceeded?.Invoke();
                        return null;
                    }

                    filled += read;
                }

                int consumed;
                try
                {
                    consumed = reader.Feed(buffer.AsSpan(0, filled), ended);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                {
                    return null;
                }

                if (ended)
                    return reader.Result(peak);

                if (consumed == 0 && filled == buffer.Length)
                {
                    // One token is larger than the buffer: grow, within the token ceiling.
                    if (buffer.Length >= MaxTokenBytes)
                        return null;

                    var larger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, MaxTokenBytes));
                    Buffer.BlockCopy(buffer, 0, larger, 0, filled);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                    peak = Math.Max(peak, buffer.Length);
                    continue;
                }

                if (consumed > 0 && consumed < filled)
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed);
                filled -= consumed;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Scans a complete in-memory document; null when it is not valid JSON.</summary>
    internal static NpmPackumentFacts? Scan(ReadOnlySpan<byte> json, string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var reader = new NpmPackumentReader(version);
        try
        {
            reader.Feed(json, isFinalBlock: true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }

        return reader.Result(0);
    }

    private NpmPackumentFacts? Result(int peak) =>
        _sawToken ? new NpmPackumentFacts(_published, _unpackedSize, _tarball, _homepage, _repositoryUrl, peak) : null;

    /// <summary>Runs the tokens that are complete in <paramref name="data"/>; returns the bytes consumed.</summary>
    private int Feed(ReadOnlySpan<byte> data, bool isFinalBlock)
    {
        var reader = new Utf8JsonReader(data, isFinalBlock, _state);
        while (reader.Read())
        {
            _sawToken = true;
            Handle(ref reader);
        }

        _state = reader.CurrentState;
        return (int)reader.BytesConsumed;
    }

    private void Handle(ref Utf8JsonReader reader)
    {
        var depth = reader.CurrentDepth;

        if (reader.TokenType == JsonTokenType.PropertyName)
        {
            _slot = Slot.None;
            switch (depth)
            {
                case 1:
                    _inTargetVersion = false;
                    _inDist = false;
                    _root = reader.ValueTextEquals(TimeName) ? RootKey.Time
                        : reader.ValueTextEquals(VersionsName) ? RootKey.Versions
                        : reader.ValueTextEquals(HomepageName) ? RootKey.Homepage
                        : reader.ValueTextEquals(RepositoryName) ? RootKey.Repository
                        : RootKey.None;
                    if (_root == RootKey.Homepage)
                        _slot = Slot.Homepage;
                    break;
                case 2:
                    _inDist = false;
                    _inTargetVersion = false;
                    if (_root == RootKey.Time && reader.ValueTextEquals(_version))
                        _slot = Slot.Time;
                    else if (_root == RootKey.Versions && reader.ValueTextEquals(_version))
                        _inTargetVersion = true;
                    else if (_root == RootKey.Repository && reader.ValueTextEquals(UrlName))
                        _slot = Slot.RepositoryUrl;
                    break;
                case 3:
                    _inDist = _inTargetVersion && reader.ValueTextEquals(DistName);
                    break;
                case 4:
                    if (_inDist && reader.ValueTextEquals(UnpackedSizeName))
                        _slot = Slot.UnpackedSize;
                    else if (_inDist && reader.ValueTextEquals(TarballName))
                        _slot = Slot.Tarball;
                    break;
            }

            return;
        }

        if (_slot == Slot.None)
            return;

        // The first value token after a wanted key: take it when it has the expected type, otherwise the
        // field counts as absent (a registry response is hostile input, a string field may arrive as anything).
        var slot = _slot;
        _slot = Slot.None;
        var isString = reader.TokenType == JsonTokenType.String;
        switch (slot)
        {
            case Slot.Time:
                _published = isString ? reader.GetString() : null;
                break;
            case Slot.Homepage:
                _homepage = isString ? reader.GetString() : null;
                break;
            case Slot.RepositoryUrl:
                _repositoryUrl = isString ? reader.GetString() : null;
                break;
            case Slot.Tarball:
                _tarball = isString ? reader.GetString() : null;
                break;
            case Slot.UnpackedSize:
                _unpackedSize = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var bytes) && bytes > 0
                    ? bytes
                    : null;
                break;
        }
    }
}
