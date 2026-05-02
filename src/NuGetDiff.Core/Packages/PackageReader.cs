using NuGet.Packaging;
using NuGet.Versioning;
using NuGetDiff.Core.Diffing;
using NuGetDiff.Core.Models;

namespace NuGetDiff.Core.Packages;

public sealed class PackageReader : IDisposable
{
    private readonly PackageArchiveReader _reader;
    private NuspecMetadata? _metadata;
    private IReadOnlyList<FileEntry>? _files;

    public PackageReader(Stream nupkg, bool leaveOpen = false)
    {
        // IMPORTANT: Always use the Stream constructor. The (string filePath) ctor is
        // not WASM-safe (it calls into File.* APIs).
        _reader = new PackageArchiveReader(nupkg, leaveOpen);
    }

    public NuspecMetadata Metadata => _metadata ??= ReadMetadata();

    public IReadOnlyList<FileEntry> Files => _files ??= ReadFiles();

    public PackageContents Read()
    {
        var meta = Metadata;
        return new PackageContents(
            new PackageDescriptor(meta.Id, meta.Version),
            meta,
            Files);
    }

    public Stream OpenFile(string path) => _reader.GetStream(path);

    public byte[] ReadFileBytes(string path)
    {
        using var s = _reader.GetStream(path);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public bool ContainsFile(string path) => Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    private NuspecMetadata ReadMetadata()
    {
        var nuspec = _reader.NuspecReader;
        var raw = nuspec
            .GetMetadata()
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

        var depGroups = nuspec.GetDependencyGroups()
            .Select(g => new DependencyGroup(
                g.TargetFramework.GetShortFolderName(),
                g.Packages.Select(p => new PackageDependency(
                    p.Id,
                    p.VersionRange?.OriginalString ?? p.VersionRange?.ToShortString() ?? string.Empty)).ToList()))
            .ToList();

        var tfms = depGroups.Select(g => g.TargetFramework)
            .Concat(InferTfmsFromFiles())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? licenseExpression = null;
        string? licenseFile = null;
        var licenseMeta = TryGetLicenseMetadata(nuspec);
        if (licenseMeta is not null)
        {
            if (licenseMeta.Type == LicenseType.Expression && licenseMeta.LicenseExpression is not null)
            {
                licenseExpression = licenseMeta.LicenseExpression.ToString();
            }
            else if (licenseMeta.Type == LicenseType.File)
            {
                licenseFile = licenseMeta.License;
            }
        }
        licenseExpression ??= raw.GetValueOrDefault("license");

        return new NuspecMetadata(
            Id: nuspec.GetId(),
            Version: nuspec.GetVersion().ToNormalizedString(),
            Title: raw.GetValueOrDefault("title"),
            Description: nuspec.GetDescription(),
            Summary: raw.GetValueOrDefault("summary"),
            Authors: nuspec.GetAuthors(),
            LicenseExpression: licenseExpression,
            LicenseFile: licenseFile,
            ProjectUrl: nuspec.GetProjectUrl(),
            RepositoryUrl: TryGetRepositoryUrl(nuspec),
            IconUrl: nuspec.GetIconUrl(),
            Tags: raw.GetValueOrDefault("tags"),
            TargetFrameworks: tfms,
            DependencyGroups: depGroups);
    }

    private static LicenseMetadata? TryGetLicenseMetadata(NuspecReader nuspec)
    {
        try { return nuspec.GetLicenseMetadata(); }
        catch { return null; }
    }

    private static string? TryGetRepositoryUrl(NuspecReader nuspec)
    {
        try { return nuspec.GetRepositoryMetadata()?.Url; }
        catch { return null; }
    }

    private IEnumerable<string> InferTfmsFromFiles()
    {
        foreach (var path in _reader.GetFiles())
        {
            var parts = path.Split('/');
            if (parts.Length >= 3 &&
                (string.Equals(parts[0], "lib", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(parts[0], "ref", StringComparison.OrdinalIgnoreCase)))
            {
                yield return parts[1];
            }
        }
    }

    private IReadOnlyList<FileEntry> ReadFiles()
    {
        var entries = new List<FileEntry>();
        foreach (var path in _reader.GetFiles())
        {
            if (path.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("package/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long length;
            try
            {
                using var s = _reader.GetStream(path);
                length = TryGetLength(s);
            }
            catch
            {
                length = -1;
            }

            entries.Add(new FileEntry(path, length, FileClassifier.ClassifyByPath(path)));
        }

        return entries
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static long TryGetLength(Stream s)
    {
        try { return s.Length; }
        catch
        {
            byte[] buf = new byte[8192];
            long total = 0;
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
            }
            return total;
        }
    }

    public void Dispose() => _reader.Dispose();
}
