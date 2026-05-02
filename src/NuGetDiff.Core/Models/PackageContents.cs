namespace NuGetDiff.Core.Models;

public sealed record PackageContents(
    PackageDescriptor Descriptor,
    NuspecMetadata Metadata,
    IReadOnlyList<FileEntry> Files);
