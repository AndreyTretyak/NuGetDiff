namespace NuGetDiff.Core.Packages;

public interface IPackageSource
{
    /// <summary>
    /// Returns a seekable stream with the raw .nupkg bytes, or null if the package
    /// is not available from this source. Callers own the returned stream.
    /// </summary>
    Task<Stream?> OpenAsync(string id, string version, CancellationToken ct = default);
}
