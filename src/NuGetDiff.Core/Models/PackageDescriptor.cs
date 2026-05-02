namespace NuGetDiff.Core.Models;

public sealed record PackageDescriptor(string Id, string Version)
{
    public string IdLower => Id.ToLowerInvariant();
}
