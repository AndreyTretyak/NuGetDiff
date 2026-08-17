using NuGetDiff.Core.Decompilation;
using NuGetDiff.Core.Diffing;
using NuGetDiff.Core.Models;
using NuGetDiff.Core.Packages;

namespace NuGetDiff.Web.Services;

public sealed record TreeComparisonLoadResult(TreeDiff? Diff, string? Error)
{
    public bool IsSuccess => Diff is not null;
}

public sealed record FileComparisonLoadResult(
    TreeDiff? TreeDiff,
    FileDiff? FileDiff,
    string? Error)
{
    public bool IsSuccess => TreeDiff is not null && FileDiff is not null;
}

public sealed record AssemblyTypeComparisonLoadResult(
    IReadOnlyList<TypeChange>? Changes,
    string? Error)
{
    public bool IsSuccess => Changes is not null;
}

public sealed class PackageComparisonService
{
    private readonly PackageResolver _resolver;
    private readonly PackageAnalysisService _analysis;
    private readonly PackageAnalysisCache _cache;

    public PackageComparisonService(
        PackageResolver resolver,
        PackageAnalysisService analysis,
        PackageAnalysisCache cache)
    {
        _resolver = resolver;
        _analysis = analysis;
        _cache = cache;
    }

    public async Task<TreeComparisonLoadResult> CompareTreeAsync(
        string id,
        string oldVersion,
        string newVersion,
        Action<string>? reportProgress = null,
        CancellationToken ct = default)
    {
        var pair = CreatePair(id, oldVersion, newVersion);
        if (_cache.TryGetTreeDiff(pair, out var cached))
        {
            return new TreeComparisonLoadResult(cached, null);
        }

        reportProgress?.Invoke($"Loading {id} {oldVersion} and {newVersion}...");
        var streams = await OpenPairAsync(id, oldVersion, newVersion, ct).ConfigureAwait(false);
        await using var oldStream = streams.Old;
        await using var newStream = streams.New;
        if (oldStream is null)
        {
            return new TreeComparisonLoadResult(null, $"Could not find {id} {oldVersion}.");
        }
        if (newStream is null)
        {
            return new TreeComparisonLoadResult(null, $"Could not find {id} {newVersion}.");
        }

        using var oldReader = new PackageReader(oldStream);
        using var newReader = new PackageReader(newStream);
        reportProgress?.Invoke("Computing tree diff...");
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        var diff = new Differ().DiffTree(oldReader, newReader, ct);
        _cache.SetTreeDiff(pair, diff);
        return new TreeComparisonLoadResult(diff, null);
    }

    public async Task<FileComparisonLoadResult> CompareFileAsync(
        string id,
        string oldVersion,
        string newVersion,
        string path,
        string? typeName,
        Action<string>? reportProgress = null,
        CancellationToken ct = default)
    {
        var pair = CreatePair(id, oldVersion, newVersion);
        var hasTree = _cache.TryGetTreeDiff(pair, out var treeDiff);
        var hasFile = _cache.TryGetFileDiff(pair, path, typeName, out var fileDiff);
        if (hasTree && hasFile)
        {
            return new FileComparisonLoadResult(treeDiff, fileDiff, null);
        }

        reportProgress?.Invoke($"Loading {id} {oldVersion} and {newVersion}...");
        var streams = await OpenPairAsync(id, oldVersion, newVersion, ct).ConfigureAwait(false);
        await using var oldStream = streams.Old;
        await using var newStream = streams.New;
        if (oldStream is null)
        {
            return new FileComparisonLoadResult(null, null, $"Could not find {id} {oldVersion}.");
        }
        if (newStream is null)
        {
            return new FileComparisonLoadResult(null, null, $"Could not find {id} {newVersion}.");
        }

        using var oldReader = new PackageReader(oldStream);
        using var newReader = new PackageReader(newStream);
        var differ = new Differ();

        if (!hasTree)
        {
            reportProgress?.Invoke("Computing tree diff...");
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            treeDiff = differ.DiffTree(oldReader, newReader, ct);
            _cache.SetTreeDiff(pair, treeDiff);
        }

        if (!hasFile)
        {
            reportProgress?.Invoke(
                $"Computing diff for {(string.IsNullOrEmpty(typeName) ? path : typeName)}...");
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            var fileChange = treeDiff!.Changes.FirstOrDefault(change =>
                string.Equals(change.Path, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(change.Old?.Path, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(change.New?.Path, path, StringComparison.OrdinalIgnoreCase));
            var oldPath = fileChange?.Old?.Path ?? path;
            var newPath = fileChange?.New?.Path ?? path;
            if (string.IsNullOrEmpty(typeName))
            {
                fileDiff = differ.DiffFile(oldReader, newReader, oldPath, newPath, ct);
            }
            else if (_cache.TryGetDecompilation(
                         pair.Old,
                         oldPath,
                         typeName,
                         out var oldDecompilation)
                     && _cache.TryGetDecompilation(
                         pair.New,
                         newPath,
                         typeName,
                         out var newDecompilation))
            {
                fileDiff = differ.DiffDecompiledType(
                    typeName,
                    oldDecompilation.CSharp,
                    newDecompilation.CSharp,
                    fileChange?.Old?.Length,
                    fileChange?.New?.Length);
            }
            else
            {
                fileDiff = differ.DiffType(
                    oldReader,
                    newReader,
                    oldPath,
                    newPath,
                    typeName,
                    ct);
            }
            _cache.SetFileDiff(pair, path, typeName, fileDiff);
        }

        return new FileComparisonLoadResult(treeDiff, fileDiff, null);
    }

    public async Task<AssemblyTypeComparisonLoadResult> LoadAssemblyTypesAsync(
        string id,
        string oldVersion,
        string newVersion,
        FileChange change,
        CancellationToken ct = default)
    {
        var pair = CreatePair(id, oldVersion, newVersion);
        var oldPath = change.Old?.Path ?? change.Path;
        var newPath = change.New?.Path ?? change.Path;
        if (_cache.TryGetTypeChanges(pair, oldPath, newPath, out var cached))
        {
            return new AssemblyTypeComparisonLoadResult(cached, null);
        }

        var listed = await LoadAssemblyTypeListAsync(
            id,
            oldVersion,
            newVersion,
            change,
            ct);
        if (!listed.IsSuccess)
        {
            return listed;
        }

        var listedChanges = listed.Changes!;
        var changes = listedChanges
            .Where(typeChange => typeChange.Kind != FileChangeKind.Unchanged)
            .ToList();
        var sharedNames = listedChanges
            .Where(typeChange => typeChange.Kind == FileChangeKind.Unchanged)
            .Select(typeChange => typeChange.Type.ReflectionName)
            .ToArray();
        var summaries = listedChanges
            .ToDictionary(
                typeChange => typeChange.Type.ReflectionName,
                typeChange => typeChange.Type,
                StringComparer.Ordinal);

        if (sharedNames.Length > 0)
        {
            var streams = await OpenPairAsync(id, oldVersion, newVersion, ct).ConfigureAwait(false);
            await using var oldStream = streams.Old;
            await using var newStream = streams.New;
            if (oldStream is null || newStream is null)
            {
                return new AssemblyTypeComparisonLoadResult(
                    null,
                    "A package version is no longer available while comparing types.");
            }

            using var oldReader = new PackageReader(oldStream);
            using var newReader = new PackageReader(newStream);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            var oldFingerprintsTask = new Decompiler()
                .FingerprintTypesFromPackageAsync(oldReader, oldPath, sharedNames, ct);
            var newFingerprintsTask = new Decompiler()
                .FingerprintTypesFromPackageAsync(newReader, newPath, sharedNames, ct);
            await Task.WhenAll(oldFingerprintsTask, newFingerprintsTask);
            var oldFingerprints = (await oldFingerprintsTask)
                .ToDictionary(result => result.ReflectionName, StringComparer.Ordinal);
            var newFingerprints = (await newFingerprintsTask)
                .ToDictionary(result => result.ReflectionName, StringComparer.Ordinal);
            foreach (var name in sharedNames)
            {
                ct.ThrowIfCancellationRequested();
                var oldFingerprint = oldFingerprints[name];
                var newFingerprint = newFingerprints[name];
                var kind = oldFingerprint.IsSuccess
                           && newFingerprint.IsSuccess
                           && string.Equals(
                               oldFingerprint.Content,
                               newFingerprint.Content,
                               StringComparison.Ordinal)
                    ? FileChangeKind.Unchanged
                    : FileChangeKind.Modified;
                changes.Add(new TypeChange(summaries[name], kind));
            }
        }

        var ordered = changes
            .OrderBy(typeChange => typeChange.Type.Namespace, StringComparer.Ordinal)
            .ThenBy(typeChange => typeChange.Type.ReflectionName, StringComparer.Ordinal)
            .ToArray();
        _cache.SetTypeChanges(pair, oldPath, newPath, ordered);
        return new AssemblyTypeComparisonLoadResult(ordered, null);
    }

    public async Task<AssemblyTypeComparisonLoadResult> LoadAssemblyTypeListAsync(
        string id,
        string oldVersion,
        string newVersion,
        FileChange change,
        CancellationToken ct = default)
    {
        var oldPath = change.Old?.Path ?? change.Path;
        var newPath = change.New?.Path ?? change.Path;
        var oldTask = change.Old is null
            ? Task.FromResult(new AssemblyTypesLoadResult(Array.Empty<TypeSummary>(), null))
            : _analysis.LoadOnlineTypesAsync(id, oldVersion, oldPath, ct);
        var newTask = change.New is null
            ? Task.FromResult(new AssemblyTypesLoadResult(Array.Empty<TypeSummary>(), null))
            : _analysis.LoadOnlineTypesAsync(id, newVersion, newPath, ct);

        await Task.WhenAll(oldTask, newTask).ConfigureAwait(false);
        var oldResult = await oldTask.ConfigureAwait(false);
        var newResult = await newTask.ConfigureAwait(false);
        if (!oldResult.IsSuccess)
        {
            return new AssemblyTypeComparisonLoadResult(
                null,
                $"Old assembly: {oldResult.Error!.Message}");
        }
        if (!newResult.IsSuccess)
        {
            return new AssemblyTypeComparisonLoadResult(
                null,
                $"New assembly: {newResult.Error!.Message}");
        }

        var oldByName = oldResult.Types!
            .ToDictionary(type => type.ReflectionName, StringComparer.Ordinal);
        var newByName = newResult.Types!
            .ToDictionary(type => type.ReflectionName, StringComparer.Ordinal);
        var allNames = new SortedSet<string>(
            oldByName.Keys.Concat(newByName.Keys),
            StringComparer.Ordinal);
        var changes = new List<TypeChange>(allNames.Count);
        foreach (var name in allNames)
        {
            var hasOld = oldByName.TryGetValue(name, out var oldType);
            var hasNew = newByName.TryGetValue(name, out var newType);
            var summary = newType ?? oldType!;
            if (!hasOld)
            {
                changes.Add(new TypeChange(summary, FileChangeKind.Added));
            }
            else if (!hasNew)
            {
                changes.Add(new TypeChange(summary, FileChangeKind.Removed));
            }
            else
            {
                changes.Add(new TypeChange(summary, FileChangeKind.Unchanged));
            }
        }

        var ordered = changes
            .OrderBy(typeChange => typeChange.Type.Namespace, StringComparer.Ordinal)
            .ThenBy(typeChange => typeChange.Type.ReflectionName, StringComparer.Ordinal)
            .ToArray();
        return new AssemblyTypeComparisonLoadResult(ordered, null);
    }

    private async Task<(Stream? Old, Stream? New)> OpenPairAsync(
        string id,
        string oldVersion,
        string newVersion,
        CancellationToken ct)
    {
        var oldTask = _resolver.OpenOnlineAsync(id, oldVersion, ct);
        var newTask = _resolver.OpenOnlineAsync(id, newVersion, ct);
        try
        {
            var streams = await Task.WhenAll(oldTask, newTask).ConfigureAwait(false);
            return (streams[0], streams[1]);
        }
        catch
        {
            await DisposeCompletedStreamAsync(oldTask).ConfigureAwait(false);
            await DisposeCompletedStreamAsync(newTask).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask DisposeCompletedStreamAsync(Task<Stream?> task)
    {
        if (task.Status == TaskStatus.RanToCompletion && task.Result is { } stream)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static PackagePairIdentity CreatePair(
        string id,
        string oldVersion,
        string newVersion)
        => new(
            PackageIdentity.Online(id, oldVersion),
            PackageIdentity.Online(id, newVersion));
}
