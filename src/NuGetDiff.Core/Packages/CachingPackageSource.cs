using NuGetDiff.Core.Util;

namespace NuGetDiff.Core.Packages;

/// <summary>
/// Decorates an inner <see cref="IPackageSource"/> with a read-through cache. The cache stores
/// the entire .nupkg byte stream so future loads are completely network-free.
/// </summary>
public sealed class CachingPackageSource : IPackageSource
{
    private readonly IPackageSource _inner;
    private readonly IPackageCache _cache;

    public CachingPackageSource(IPackageSource inner, IPackageCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<Stream?> OpenAsync(string id, string version, CancellationToken ct = default)
    {
        var cached = await _cache.GetAsync(id, version, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return OpenBytes(cached);
        }

        await using var inner = await _inner.OpenAsync(id, version, ct).ConfigureAwait(false);
        if (inner is null)
        {
            return null;
        }

        var bytes = inner is PackageBytesStream packageStream
            ? packageStream.Bytes
            : await HashUtil.ReadAllBytesAsync(inner, ct).ConfigureAwait(false);
        await _cache.PutAsync(id, version, bytes, ct).ConfigureAwait(false);
        return OpenBytes(bytes);
    }

    private static MemoryStream OpenBytes(byte[] bytes)
        => new PackageBytesStream(bytes);
}
