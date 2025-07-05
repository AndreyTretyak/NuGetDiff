using NuGetDiff.Client.Models;

using System.IO.Compression;
using System.Text.Json;

namespace NuGetDiff.Client.Services;

public class NuGetService
{
    private readonly HttpClient _httpClient;

    public NuGetService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<PackageInfo> DownloadPackageAsync(string packageName, string version, string source)
    {
        var packageInfo = new PackageInfo
        {
            Name = packageName,
            Version = version,
            Source = source
        };

        try
        {
            // Get the service index
            var serviceIndex = await GetServiceIndexAsync(source);
            var packageBaseUrl = GetServiceUrl(serviceIndex, "PackageBaseAddress/3.0.0");

            if (string.IsNullOrEmpty(packageBaseUrl))
            {
                throw new Exception($"Could not find PackageBaseAddress service in {source}");
            }

            // Construct the package URL
            var packageUrl = $"{packageBaseUrl}{packageName.ToLowerInvariant()}/{version.ToLowerInvariant()}/{packageName.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg";

            // Download the package
            var packageBytes = await _httpClient.GetByteArrayAsync(packageUrl);

            // Extract files from the package
            using var stream = new MemoryStream(packageBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                if (entry.Length > 0 && !entry.FullName.EndsWith("/"))
                {
                    using var entryStream = entry.Open();
                    using var ms = new MemoryStream();
                    await entryStream.CopyToAsync(ms);
                    packageInfo.Files[entry.FullName] = ms.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to download package {packageName} {version} from {source}: {ex.Message}", ex);
        }

        return packageInfo;
    }

    private static Uri GetPackageUri(string source, string package, string version)
    {
        var indexSuffix = "index.json";
        var trimedSource = source.EndsWith(indexSuffix) ? source[..^indexSuffix.Length] : source;
        return new Uri($"{trimedSource}package/{package}/${version}");
    }

    private async Task<JsonDocument> GetServiceIndexAsync(string source)
    {
        var response = await _httpClient.GetStringAsync(source);
        return JsonDocument.Parse(response);
    }

    private string? GetServiceUrl(JsonDocument serviceIndex, string type)
    {
        var resources = serviceIndex.RootElement.GetProperty("resources");

        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.TryGetProperty("@type", out var typeProperty) &&
                typeProperty.GetString() == type)
            {
                if (resource.TryGetProperty("@id", out var idProperty))
                {
                    return idProperty.GetString();
                }
            }
        }

        return null;
    }
}
