using System.Collections.Concurrent;
using NuGet.Versioning;

namespace NuGetDiff.Core.Packages;

public interface IPackageCache
{
    Task<byte[]?> GetAsync(string id, string version, CancellationToken ct = default);
    Task PutAsync(string id, string version, byte[] bytes, CancellationToken ct = default);
}

public sealed class NullPackageCache : IPackageCache
{
    public Task<byte[]?> GetAsync(string id, string version, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(null);

    public Task PutAsync(string id, string version, byte[] bytes, CancellationToken ct = default)
        => Task.CompletedTask;
}

public sealed class InMemoryPackageCache : IPackageCache
{
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<byte[]?> GetAsync(string id, string version, CancellationToken ct = default)
    {
        return Task.FromResult(_cache.TryGetValue(MakeKey(id, version), out var bytes) ? bytes : null);
    }

    public Task PutAsync(string id, string version, byte[] bytes, CancellationToken ct = default)
    {
        _cache[MakeKey(id, version)] = bytes;
        return Task.CompletedTask;
    }

    private static string MakeKey(string id, string version)
    {
        var verNorm = NuGetVersion.Parse(version).ToNormalizedString().ToLowerInvariant();
        return $"{id.ToLowerInvariant()}|{verNorm}";
    }
}
