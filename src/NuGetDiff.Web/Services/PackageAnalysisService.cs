using NuGetDiff.Core.Decompilation;
using NuGetDiff.Core.Models;
using NuGetDiff.Core.Packages;

namespace NuGetDiff.Web.Services;

public sealed record PackageLoadResult(
    PackageIdentity Identity,
    PackageContents? Contents,
    string? Error)
{
    public bool IsSuccess => Contents is not null;
}

public sealed record AssemblyTypesLoadResult(
    IReadOnlyList<TypeSummary>? Types,
    DecompilationError? Error)
{
    public bool IsSuccess => Types is not null;
}

public sealed record DecompilationLoadResult(
    DecompilationResult? Result,
    DecompilationError? Error)
{
    public bool IsSuccess => Result is not null;
}

public sealed class PackageAnalysisService
{
    private readonly PackageResolver _resolver;
    private readonly PackageAnalysisCache _cache;

    public PackageAnalysisService(PackageResolver resolver, PackageAnalysisCache cache)
    {
        _resolver = resolver;
        _cache = cache;
    }

    public Task<PackageLoadResult> LoadOnlineAsync(
        string id,
        string version,
        CancellationToken ct = default)
    {
        var identity = PackageIdentity.Online(id, version);
        return LoadPackageAsync(
            identity,
            token => _resolver.OpenOnlineAsync(id, version, token),
            $"Could not find {id} {version} on nuget.org.",
            ct);
    }

    public Task<PackageLoadResult> LoadLocalAsync(
        string contentHash,
        CancellationToken ct = default)
    {
        var identity = PackageIdentity.Local(contentHash);
        return LoadPackageAsync(
            identity,
            token => _resolver.OpenLocalAsync(contentHash, token),
            "No uploaded package matches this hash in the current session.",
            ct);
    }

    public Task<AssemblyTypesLoadResult> LoadOnlineTypesAsync(
        string id,
        string version,
        string path,
        CancellationToken ct = default)
    {
        var identity = PackageIdentity.Online(id, version);
        return LoadTypesAsync(
            identity,
            path,
            token => _resolver.OpenOnlineAsync(id, version, token),
            ct);
    }

    public Task<AssemblyTypesLoadResult> LoadLocalTypesAsync(
        string contentHash,
        string path,
        CancellationToken ct = default)
    {
        var identity = PackageIdentity.Local(contentHash);
        return LoadTypesAsync(
            identity,
            path,
            token => _resolver.OpenLocalAsync(contentHash, token),
            ct);
    }

    public Task<DecompilationLoadResult> DecompileOnlineTypeAsync(
        string id,
        string version,
        string path,
        string typeName,
        CancellationToken ct = default)
    {
        var identity = PackageIdentity.Online(id, version);
        return DecompileTypeAsync(
            identity,
            path,
            typeName,
            token => _resolver.OpenOnlineAsync(id, version, token),
            ct);
    }

    public Task<DecompilationLoadResult> DecompileLocalTypeAsync(
        string contentHash,
        string path,
        string typeName,
        CancellationToken ct = default)
    {
        var identity = PackageIdentity.Local(contentHash);
        return DecompileTypeAsync(
            identity,
            path,
            typeName,
            token => _resolver.OpenLocalAsync(contentHash, token),
            ct);
    }

    private async Task<PackageLoadResult> LoadPackageAsync(
        PackageIdentity identity,
        Func<CancellationToken, Task<Stream?>> open,
        string missingMessage,
        CancellationToken ct)
    {
        if (_cache.TryGetPackage(identity, out var cached))
        {
            return new PackageLoadResult(identity, cached, null);
        }

        await using var stream = await open(ct).ConfigureAwait(false);
        if (stream is null)
        {
            return new PackageLoadResult(identity, null, missingMessage);
        }

        using var reader = new PackageReader(stream);
        var contents = reader.Read();
        ct.ThrowIfCancellationRequested();
        _cache.SetPackage(identity, contents);
        return new PackageLoadResult(identity, contents, null);
    }

    private async Task<AssemblyTypesLoadResult> LoadTypesAsync(
        PackageIdentity identity,
        string path,
        Func<CancellationToken, Task<Stream?>> open,
        CancellationToken ct)
    {
        if (_cache.TryGetTypes(identity, path, out var cached))
        {
            return new AssemblyTypesLoadResult(cached, null);
        }

        await using var stream = await open(ct).ConfigureAwait(false);
        if (stream is null)
        {
            return new AssemblyTypesLoadResult(
                null,
                new DecompilationError("Package is no longer available."));
        }

        using var reader = new PackageReader(stream);
        ct.ThrowIfCancellationRequested();
        var result = new Decompiler().ListTypes(reader, path);
        if (!result.IsOk)
        {
            return new AssemblyTypesLoadResult(null, result.Err);
        }

        var types = result.Ok!;
        _cache.SetTypes(identity, path, types);
        return new AssemblyTypesLoadResult(types, null);
    }

    private async Task<DecompilationLoadResult> DecompileTypeAsync(
        PackageIdentity identity,
        string path,
        string typeName,
        Func<CancellationToken, Task<Stream?>> open,
        CancellationToken ct)
    {
        if (_cache.TryGetDecompilation(identity, path, typeName, out var cached))
        {
            return new DecompilationLoadResult(cached, null);
        }

        await using var stream = await open(ct).ConfigureAwait(false);
        if (stream is null)
        {
            return new DecompilationLoadResult(
                null,
                new DecompilationError("Package is no longer available."));
        }

        using var reader = new PackageReader(stream);
        ct.ThrowIfCancellationRequested();
        var result = new Decompiler().DecompileTypeFromPackage(
            reader,
            path,
            typeName,
            ct);
        if (!result.IsOk)
        {
            return new DecompilationLoadResult(null, result.Err);
        }

        _cache.SetDecompilation(identity, path, typeName, result.Ok!);
        return new DecompilationLoadResult(result.Ok, null);
    }
}
