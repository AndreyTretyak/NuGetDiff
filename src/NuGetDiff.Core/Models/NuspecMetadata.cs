namespace NuGetDiff.Core.Models;

public sealed record PackageDependency(string Id, string VersionRange);

public sealed record DependencyGroup(string TargetFramework, IReadOnlyList<PackageDependency> Dependencies);

public sealed record NuspecMetadata(
    string Id,
    string Version,
    string? Title,
    string? Description,
    string? Summary,
    string? Authors,
    string? LicenseExpression,
    string? LicenseFile,
    string? ProjectUrl,
    string? RepositoryUrl,
    string? IconUrl,
    string? Tags,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<DependencyGroup> DependencyGroups);
