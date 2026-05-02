using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;

namespace NuGetDiff.Web.Services;

/// <summary>
/// Reads version listings from the NuGet V3 registration index. Used to populate
/// the <c>/{id}</c> page. The endpoint serves CORS-permissive responses so the call
/// can run directly from the browser.
/// </summary>
public sealed class NuGetRegistrationClient
{
    private static readonly Uri DefaultBase = new("https://api.nuget.org/v3/registration5-gz-semver2/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly Uri _baseAddress;

    public NuGetRegistrationClient(HttpClient http, Uri? baseAddress = null)
    {
        _http = http;
        _baseAddress = baseAddress ?? DefaultBase;
    }

    public async Task<IReadOnlyList<VersionInfo>> GetVersionsAsync(string id, CancellationToken ct = default)
    {
        var url = new Uri(_baseAddress, $"{Uri.EscapeDataString(id.ToLowerInvariant())}/index.json");
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Array.Empty<VersionInfo>();
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var index = await JsonSerializer.DeserializeAsync<RegistrationIndex>(stream, JsonOptions, ct).ConfigureAwait(false);
        if (index?.Items is null)
        {
            return Array.Empty<VersionInfo>();
        }

        var versions = new List<VersionInfo>();
        foreach (var page in index.Items)
        {
            await CollectVersionsAsync(page, versions, ct).ConfigureAwait(false);
        }

        // Newest first.
        versions.Sort((a, b) => NuGetVersion.Parse(b.Version).CompareTo(NuGetVersion.Parse(a.Version)));
        return versions;
    }

    private async Task CollectVersionsAsync(RegistrationPage page, List<VersionInfo> sink, CancellationToken ct)
    {
        var leaves = page.Items;
        if (leaves is null && page.Id is not null)
        {
            // Paginated entry — fetch the page contents.
            using var pageResponse = await _http.GetAsync(page.Id, ct).ConfigureAwait(false);
            pageResponse.EnsureSuccessStatusCode();
            await using var pageStream = await pageResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var resolvedPage = await JsonSerializer.DeserializeAsync<RegistrationPage>(pageStream, JsonOptions, ct).ConfigureAwait(false);
            leaves = resolvedPage?.Items;
        }

        if (leaves is null)
        {
            return;
        }

        foreach (var leaf in leaves)
        {
            var entry = leaf.CatalogEntry;
            if (entry?.Version is null)
            {
                continue;
            }
            sink.Add(new VersionInfo(entry.Version, entry.Published, entry.Listed ?? true));
        }
    }

    public sealed record VersionInfo(string Version, DateTimeOffset? Published, bool Listed);

    private sealed class RegistrationIndex
    {
        public List<RegistrationPage>? Items { get; set; }
    }

    private sealed class RegistrationPage
    {
        [JsonPropertyName("@id")]
        public string? Id { get; set; }
        public List<RegistrationLeaf>? Items { get; set; }
    }

    private sealed class RegistrationLeaf
    {
        public CatalogEntry? CatalogEntry { get; set; }
    }

    private sealed class CatalogEntry
    {
        public string? Version { get; set; }
        public DateTimeOffset? Published { get; set; }
        public bool? Listed { get; set; }
    }
}
