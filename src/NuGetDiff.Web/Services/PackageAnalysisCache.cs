using NuGet.Versioning;
using NuGetDiff.Core.Decompilation;
using NuGetDiff.Core.Models;

namespace NuGetDiff.Web.Services;

public readonly record struct PackageIdentity(string Source, string Id, string VersionOrHash)
{
    public static PackageIdentity Online(string id, string version)
        => new(
            "online",
            id.ToLowerInvariant(),
            NuGetVersion.Parse(version).ToNormalizedString().ToLowerInvariant());

    public static PackageIdentity Local(string contentHash)
        => new("local", string.Empty, contentHash.ToLowerInvariant());
}

public readonly record struct PackagePairIdentity(PackageIdentity Old, PackageIdentity New);

public sealed class PackageAnalysisCache
{
    public const long DefaultSizeLimitBytes = 32L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, CacheEntry> _entries =
        new(CacheKeyComparer.Instance);
    private readonly LinkedList<CacheKey> _recency = new();
    private readonly long _sizeLimitBytes;
    private long _currentSizeBytes;

    public PackageAnalysisCache(long sizeLimitBytes = DefaultSizeLimitBytes)
    {
        if (sizeLimitBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeLimitBytes));
        }
        _sizeLimitBytes = sizeLimitBytes;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public long CurrentSizeBytes
    {
        get
        {
            lock (_gate)
            {
                return _currentSizeBytes;
            }
        }
    }

    public bool TryGetPackage(PackageIdentity package, out PackageContents contents)
        => TryGet(new CacheKey(CacheKind.Package, package, default, string.Empty, string.Empty), out contents);

    public void SetPackage(PackageIdentity package, PackageContents contents)
        => Set(
            new CacheKey(CacheKind.Package, package, default, string.Empty, string.Empty),
            contents,
            Estimate(contents));

    public bool TryGetTreeDiff(PackagePairIdentity pair, out TreeDiff diff)
        => TryGet(new CacheKey(CacheKind.TreeDiff, pair.Old, pair.New, string.Empty, string.Empty), out diff);

    public void SetTreeDiff(PackagePairIdentity pair, TreeDiff diff)
        => Set(
            new CacheKey(CacheKind.TreeDiff, pair.Old, pair.New, string.Empty, string.Empty),
            diff,
            Estimate(diff));

    public bool TryGetTypes(
        PackageIdentity package,
        string path,
        out IReadOnlyList<TypeSummary> types)
        => TryGet(
            new CacheKey(CacheKind.Types, package, default, NormalizePath(path), string.Empty),
            out types);

    public void SetTypes(
        PackageIdentity package,
        string path,
        IReadOnlyList<TypeSummary> types)
        => Set(
            new CacheKey(CacheKind.Types, package, default, NormalizePath(path), string.Empty),
            types,
            Estimate(types));

    public bool TryGetTypeChanges(
        PackagePairIdentity pair,
        string oldPath,
        string newPath,
        out IReadOnlyList<TypeChange> changes)
        => TryGet(
            new CacheKey(
                CacheKind.TypeChanges,
                pair.Old,
                pair.New,
                TypeChangePath(oldPath, newPath),
                string.Empty),
            out changes);

    public void SetTypeChanges(
        PackagePairIdentity pair,
        string oldPath,
        string newPath,
        IReadOnlyList<TypeChange> changes)
        => Set(
            new CacheKey(
                CacheKind.TypeChanges,
                pair.Old,
                pair.New,
                TypeChangePath(oldPath, newPath),
                string.Empty),
            changes,
            Estimate(changes));

    public bool TryGetDecompilation(
        PackageIdentity package,
        string path,
        string typeName,
        out DecompilationResult result)
        => TryGet(
            new CacheKey(CacheKind.Decompilation, package, default, NormalizePath(path), typeName),
            out result);

    public void SetDecompilation(
        PackageIdentity package,
        string path,
        string typeName,
        DecompilationResult result)
        => Set(
            new CacheKey(CacheKind.Decompilation, package, default, NormalizePath(path), typeName),
            result,
            Estimate(result));

    public bool TryGetFileDiff(
        PackagePairIdentity pair,
        string path,
        string? typeName,
        out FileDiff diff)
        => TryGet(
            new CacheKey(
                CacheKind.FileDiff,
                pair.Old,
                pair.New,
                NormalizePath(path),
                typeName ?? string.Empty),
            out diff);

    public void SetFileDiff(
        PackagePairIdentity pair,
        string path,
        string? typeName,
        FileDiff diff)
        => Set(
            new CacheKey(
                CacheKind.FileDiff,
                pair.Old,
                pair.New,
                NormalizePath(path),
                typeName ?? string.Empty),
            diff,
            Estimate(diff));

    private bool TryGet<T>(CacheKey key, out T value)
        where T : class
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Value is T typed)
            {
                _recency.Remove(entry.Node);
                _recency.AddFirst(entry.Node);
                value = typed;
                return true;
            }
        }

        value = null!;
        return false;
    }

    private void Set<T>(CacheKey key, T value, long sizeBytes)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        sizeBytes = Math.Max(1, sizeBytes + Estimate(key));

        lock (_gate)
        {
            Remove(key);
            if (sizeBytes > _sizeLimitBytes)
            {
                return;
            }

            while (_currentSizeBytes + sizeBytes > _sizeLimitBytes
                   && _recency.Last is { } leastRecent)
            {
                Remove(leastRecent.Value);
            }

            var node = _recency.AddFirst(key);
            _entries[key] = new CacheEntry(value, sizeBytes, node);
            _currentSizeBytes += sizeBytes;
        }
    }

    private void Remove(CacheKey key)
    {
        if (_entries.Remove(key, out var existing))
        {
            _recency.Remove(existing.Node);
            _currentSizeBytes -= existing.SizeBytes;
        }
    }

    private static string NormalizePath(string path)
        => path;

    private static string TypeChangePath(string oldPath, string newPath)
        => oldPath + '\0' + newPath;

    private static long Estimate(PackageContents contents)
    {
        var metadata = contents.Metadata;
        return 1024L
               + contents.Files.Sum(file => 96L + (file.Path.Length * 2L))
               + Estimate(metadata.Id)
               + Estimate(metadata.Version)
               + Estimate(metadata.Title)
               + Estimate(metadata.Description)
               + Estimate(metadata.Summary)
               + Estimate(metadata.Authors)
               + Estimate(metadata.LicenseExpression)
               + Estimate(metadata.LicenseFile)
               + Estimate(metadata.ProjectUrl)
               + Estimate(metadata.RepositoryUrl)
               + Estimate(metadata.IconUrl)
               + Estimate(metadata.Tags)
               + metadata.TargetFrameworks.Sum(Estimate)
               + metadata.DependencyGroups.Sum(group =>
                   64L
                   + Estimate(group.TargetFramework)
                   + group.Dependencies.Sum(dependency =>
                       64L + Estimate(dependency.Id) + Estimate(dependency.VersionRange)));
    }

    private static long Estimate(TreeDiff diff)
        => 256L + diff.Changes.Sum(change => 128L + (change.Path.Length * 2L));

    private static long Estimate(IReadOnlyList<TypeSummary> types)
        => 128L + types.Sum(type =>
            96L
            + ((type.ReflectionName.Length + type.Namespace.Length + type.DisplayName.Length) * 2L));

    private static long Estimate(IReadOnlyList<TypeChange> changes)
        => 128L + changes.Sum(change =>
            112L
            + ((change.Type.ReflectionName.Length
                + change.Type.Namespace.Length
                + change.Type.DisplayName.Length) * 2L));

    private static long Estimate(DecompilationResult result)
        => 256L
           + (result.CSharp.Length * 2L)
           + result.Warnings.Sum(warning => 32L + (warning.Length * 2L))
           + result.TypeFullNames.Sum(typeName => 32L + (typeName.Length * 2L));

    private static long Estimate(FileDiff diff)
        => 256L
           + Estimate(diff.Text?.Old)
           + Estimate(diff.Text?.New)
           + ((diff.Message?.Length ?? 0) * 2L);

    private static long Estimate(IReadOnlyList<DiffLine>? lines)
        => lines?.Sum(line =>
            48L
            + (line.Text.Length * 2L)
            + (line.Segments?.Sum(segment =>
                48L + (segment.Text.Length * 2L)) ?? 0)) ?? 0;

    private static long Estimate(string? value)
        => value is null ? 0 : 32L + (value.Length * 2L);

    private static long Estimate(CacheKey key)
        => 128L
           + Estimate(key.Package.Source)
           + Estimate(key.Package.Id)
           + Estimate(key.Package.VersionOrHash)
           + Estimate(key.OtherPackage.Source)
           + Estimate(key.OtherPackage.Id)
           + Estimate(key.OtherPackage.VersionOrHash)
           + Estimate(key.Path)
           + Estimate(key.Detail);

    private enum CacheKind
    {
        Package,
        TreeDiff,
        Types,
        TypeChanges,
        Decompilation,
        FileDiff,
    }

    private readonly record struct CacheKey(
        CacheKind Kind,
        PackageIdentity Package,
        PackageIdentity OtherPackage,
        string Path,
        string Detail);

    private sealed class CacheKeyComparer : IEqualityComparer<CacheKey>
    {
        public static CacheKeyComparer Instance { get; } = new();

        public bool Equals(CacheKey x, CacheKey y)
            => x.Kind == y.Kind
               && x.Package == y.Package
               && x.OtherPackage == y.OtherPackage
               && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path)
               && StringComparer.Ordinal.Equals(x.Detail, y.Detail);

        public int GetHashCode(CacheKey key)
        {
            var hash = new HashCode();
            hash.Add(key.Kind);
            hash.Add(key.Package);
            hash.Add(key.OtherPackage);
            hash.Add(key.Path, StringComparer.OrdinalIgnoreCase);
            hash.Add(key.Detail, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }

    private sealed record CacheEntry(
        object Value,
        long SizeBytes,
        LinkedListNode<CacheKey> Node);
}
