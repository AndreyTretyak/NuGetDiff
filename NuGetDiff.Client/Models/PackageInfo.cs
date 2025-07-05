namespace NuGetDiff.Client.Models;

public class PackageInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Source { get; set; } = "https://api.nuget.org/v3/index.json";
    public Dictionary<string, byte[]> Files { get; set; } = new();
    public Dictionary<string, string> DecompiledFiles { get; set; } = new();
}

public class PackageComparisonRequest
{
    public string Name { get; set; } = string.Empty;
    public string? NewName { get; set; }
    public string Version { get; set; } = string.Empty;
    public string NewVersion { get; set; } = string.Empty;
    public string Source { get; set; } = "https://api.nuget.org/v3/index.json";
    public string? NewSource { get; set; }

    public static PackageComparisonRequest FromQueryString(string queryString)
    {
        var query = System.Web.HttpUtility.ParseQueryString(queryString);
        return new PackageComparisonRequest
        {
            Name = query["name"] ?? string.Empty,
            NewName = query["newName"],
            Version = query["version"] ?? string.Empty,
            NewVersion = query["newVersion"] ?? string.Empty,
            Source = query["source"] ?? "https://api.nuget.org/v3/index.json",
            NewSource = query["newSource"]
        };
    }
}
