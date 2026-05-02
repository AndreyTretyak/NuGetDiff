using Microsoft.JSInterop;
using NuGet.Versioning;
using NuGetDiff.Core.Packages;

namespace NuGetDiff.Web.Services;

/// <summary>
/// IndexedDB-backed implementation of <see cref="IPackageCache"/>. Survives browser reloads
/// and tab close, so the second visit to a package is fully offline. Falls back to a
/// per-tab in-memory cache if the JS interop is unavailable (e.g. during prerendering).
/// </summary>
public sealed class IndexedDbPackageCache : IPackageCache
{
    private const string ModulePath = "./js/packageCache.js";

    private readonly IJSRuntime _js;
    private readonly Dictionary<string, byte[]> _memory = new(StringComparer.OrdinalIgnoreCase);
    private IJSObjectReference? _module;
    private bool _moduleFailed;

    public IndexedDbPackageCache(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<byte[]?> GetAsync(string id, string version, CancellationToken ct = default)
    {
        var key = MakeKey(id, version);
        if (_memory.TryGetValue(key, out var bytes))
        {
            return bytes;
        }

        var module = await TryGetModuleAsync(ct).ConfigureAwait(false);
        if (module is null)
        {
            return null;
        }

        try
        {
            var fromDb = await module.InvokeAsync<byte[]?>("getPackage", ct, key).ConfigureAwait(false);
            if (fromDb is { Length: > 0 })
            {
                _memory[key] = fromDb;
                return fromDb;
            }
        }
        catch (Exception)
        {
            // IndexedDB can be unavailable (private mode, permissions). Treat as cache miss.
        }
        return null;
    }

    public async Task PutAsync(string id, string version, byte[] bytes, CancellationToken ct = default)
    {
        var key = MakeKey(id, version);
        _memory[key] = bytes;

        var module = await TryGetModuleAsync(ct).ConfigureAwait(false);
        if (module is null)
        {
            return;
        }

        try
        {
            await module.InvokeVoidAsync("putPackage", ct, key, bytes).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Silent failure — memory cache still holds the bytes for this session.
        }
    }

    private async Task<IJSObjectReference?> TryGetModuleAsync(CancellationToken ct)
    {
        if (_module is not null)
        {
            return _module;
        }
        if (_moduleFailed)
        {
            return null;
        }
        try
        {
            _module = await _js.InvokeAsync<IJSObjectReference>("import", ct, ModulePath).ConfigureAwait(false);
            return _module;
        }
        catch
        {
            _moduleFailed = true;
            return null;
        }
    }

    private static string MakeKey(string id, string version)
    {
        var verNorm = NuGetVersion.Parse(version).ToNormalizedString().ToLowerInvariant();
        return $"{id.ToLowerInvariant()}|{verNorm}";
    }
}
