using System.Net;
using NuGet.Versioning;

namespace NuGetDiff.Core.Packages;

/// <summary>
/// Fetches packages from a NuGet V3 flat-container endpoint, defaulting to nuget.org.
/// The flat-container URL requires the version to be <em>normalized and lowercased</em>;
/// just lowercasing is not enough (e.g. "1.02.3+build" must become "1.2.3").
/// </summary>
public sealed class NuGetOrgPackageSource : IPackageSource
{
    private static readonly Uri DefaultBaseAddress = new("https://api.nuget.org/v3-flatcontainer/");

    private readonly HttpClient _http;
    private readonly Uri _baseAddress;

    public NuGetOrgPackageSource(HttpClient http, Uri? baseAddress = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseAddress = baseAddress ?? DefaultBaseAddress;
        if (!_baseAddress.AbsoluteUri.EndsWith('/'))
        {
            _baseAddress = new Uri(_baseAddress.AbsoluteUri + "/");
        }
    }

    public async Task<Stream?> OpenAsync(string id, string version, CancellationToken ct = default)
    {
        var url = BuildPackageUrl(id, version);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return new MemoryStream(bytes, writable: false);
    }

    public Uri BuildPackageUrl(string id, string version)
    {
        var idLower = id.ToLowerInvariant();
        var verNorm = NuGetVersion.Parse(version).ToNormalizedString().ToLowerInvariant();
        return new Uri(_baseAddress, $"{Uri.EscapeDataString(idLower)}/{Uri.EscapeDataString(verNorm)}/{Uri.EscapeDataString(idLower)}.{Uri.EscapeDataString(verNorm)}.nupkg");
    }
}
