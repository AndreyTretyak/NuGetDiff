using NuGetDiff.Core.Packages;

namespace NuGetDiff.Web.Services;

/// <summary>
/// Coordinates package access across the online source pipeline (cached + nuget.org)
/// and the in-session uploaded source. The two are kept strictly separate:
/// <see cref="OpenOnlineAsync"/> never returns uploaded bytes, so a URL like
/// <c>/{id}/{version}</c> is reproducible across machines. Uploaded packages are only
/// reached through <see cref="OpenLocalAsync"/> on the <c>/local/{contentHash}</c> routes.
/// </summary>
public sealed class PackageResolver
{
    private readonly IPackageSource _online;
    private readonly UploadedPackageSource _uploads;

    public PackageResolver(IPackageSource online, UploadedPackageSource uploads)
    {
        _online = online;
        _uploads = uploads;
    }

    public UploadedPackageSource Uploads => _uploads;

    public Task<Stream?> OpenOnlineAsync(string id, string version, CancellationToken ct = default)
        => _online.OpenAsync(id, version, ct);

    public Task<Stream?> OpenLocalAsync(string contentHash, CancellationToken ct = default)
        => _uploads.OpenByHashAsync(contentHash, ct);
}
