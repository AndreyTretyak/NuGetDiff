using System.Collections.Concurrent;
using NuGet.Versioning;
using NuGetDiff.Core.Util;

namespace NuGetDiff.Core.Packages;

/// <summary>
/// In-memory store of user-uploaded .nupkg files, addressable both by (id, version)
/// for normal navigation and by content hash for shareable /local/{hash}/... URLs.
/// </summary>
public sealed class UploadedPackageSource : IPackageSource
{
    private readonly ConcurrentDictionary<(string Id, string Version), byte[]> _byIdVersion = new();
    private readonly ConcurrentDictionary<string, UploadedPackage> _byHash = new(StringComparer.OrdinalIgnoreCase);

    public sealed record UploadedPackage(string Id, string Version, string ContentHash, byte[] Bytes);

    public string Add(string id, string version, byte[] bytes)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("Package id is required.", nameof(id));
        }
        if (string.IsNullOrEmpty(version))
        {
            throw new ArgumentException("Package version is required.", nameof(version));
        }
        var normVer = NuGetVersion.Parse(version).ToNormalizedString().ToLowerInvariant();
        var idLower = id.ToLowerInvariant();
        _byIdVersion[(idLower, normVer)] = bytes;
        var hash = HashUtil.Sha256Hex(bytes);
        _byHash[hash] = new UploadedPackage(id, version, hash, bytes);
        return hash;
    }

    public Task<Stream?> OpenAsync(string id, string version, CancellationToken ct = default)
    {
        var idLower = id.ToLowerInvariant();
        var normVer = NuGetVersion.Parse(version).ToNormalizedString().ToLowerInvariant();
        if (_byIdVersion.TryGetValue((idLower, normVer), out var bytes))
        {
            return Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false));
        }
        return Task.FromResult<Stream?>(null);
    }

    public Task<Stream?> OpenByHashAsync(string contentHash, CancellationToken ct = default)
    {
        if (_byHash.TryGetValue(contentHash, out var pkg))
        {
            return Task.FromResult<Stream?>(new MemoryStream(pkg.Bytes, writable: false));
        }
        return Task.FromResult<Stream?>(null);
    }

    public UploadedPackage? GetByHash(string contentHash)
        => _byHash.TryGetValue(contentHash, out var pkg) ? pkg : null;

    public IReadOnlyCollection<UploadedPackage> All => _byHash.Values.ToArray();
}
