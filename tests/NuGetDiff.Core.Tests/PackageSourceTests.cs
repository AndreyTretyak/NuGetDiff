using NuGet.Versioning;
using NuGetDiff.Core.Packages;
using NuGetDiff.Core.Util;
using Xunit;

namespace NuGetDiff.Core.Tests;

public class PackageSourceTests
{
    [Fact]
    public void NuGetOrg_url_normalizes_version()
    {
        var src = new NuGetOrgPackageSource(new HttpClient());
        var url = src.BuildPackageUrl("Newtonsoft.Json", "13.0.01");
        // 13.0.01 normalizes to 13.0.1
        Assert.Equal("https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.1/newtonsoft.json.13.0.1.nupkg", url.ToString());
    }

    [Fact]
    public void NuGetOrg_url_strips_build_metadata()
    {
        var src = new NuGetOrgPackageSource(new HttpClient());
        var url = src.BuildPackageUrl("Foo", "1.2.3+build.5");
        // SemVer build metadata is stripped from the normalized version
        var verNorm = NuGetVersion.Parse("1.2.3+build.5").ToNormalizedString().ToLowerInvariant();
        Assert.Contains($"/{verNorm}/", url.ToString());
        Assert.DoesNotContain("+", url.ToString());
        Assert.DoesNotContain("%2B", url.ToString());
    }

    [Fact]
    public async Task Uploaded_source_round_trips_by_id_version_and_hash()
    {
        var src = new UploadedPackageSource();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var hash = src.Add("MyPkg", "1.0.0", bytes);

        await using var byIdVer = await src.OpenAsync("MyPkg", "1.0.0");
        Assert.NotNull(byIdVer);
        Assert.Equal(bytes, ToArray(byIdVer!));

        await using var byHash = await src.OpenByHashAsync(hash);
        Assert.NotNull(byHash);
        Assert.Equal(bytes, ToArray(byHash!));
    }

    [Fact]
    public async Task Caching_source_only_calls_inner_once()
    {
        var inner = new CountingSource(new byte[] { 9, 8, 7 });
        var cache = new InMemoryPackageCache();
        var caching = new CachingPackageSource(inner, cache);

        await using (var first = await caching.OpenAsync("X", "1.0.0")) { Assert.NotNull(first); }
        await using (var second = await caching.OpenAsync("X", "1.0.0")) { Assert.NotNull(second); }

        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task Read_all_bytes_does_not_expose_a_public_memory_stream_buffer()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        await using var stream = new MemoryStream(
            bytes,
            0,
            bytes.Length,
            writable: false,
            publiclyVisible: true);

        var result = await HashUtil.ReadAllBytesAsync(stream);

        Assert.NotSame(bytes, result);
        Assert.Equal(bytes, result);
    }

    [Fact]
    public async Task Cached_package_stream_does_not_expose_shared_bytes()
    {
        var caching = new CachingPackageSource(
            new CountingSource(new byte[] { 9, 8, 7 }),
            new InMemoryPackageCache());

        await using var stream = await caching.OpenAsync("X", "1.0.0");

        var memory = Assert.IsAssignableFrom<MemoryStream>(stream);
        Assert.False(memory.TryGetBuffer(out _));
    }

    private static byte[] ToArray(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private sealed class CountingSource : IPackageSource
    {
        private readonly byte[] _bytes;
        public int CallCount { get; private set; }
        public CountingSource(byte[] bytes) { _bytes = bytes; }
        public Task<Stream?> OpenAsync(string id, string version, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult<Stream?>(new MemoryStream(_bytes, writable: false));
        }
    }
}
