using NuGetDiff.Core.Packages;

namespace NuGetDiff.Web.Tests;

internal sealed class TestPackageSource : IPackageSource
{
    private readonly Dictionary<(string Id, string Version), byte[]> _packages = new();

    public int OpenCount { get; private set; }

    public void Add(string id, string version, byte[] bytes)
        => _packages[(id.ToLowerInvariant(), version.ToLowerInvariant())] = bytes;

    public Task<Stream?> OpenAsync(
        string id,
        string version,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        OpenCount++;
        var key = (id.ToLowerInvariant(), version.ToLowerInvariant());
        var stream = _packages.TryGetValue(key, out var bytes)
            ? new MemoryStream(bytes, writable: false)
            : null;
        return Task.FromResult<Stream?>(stream);
    }
}
