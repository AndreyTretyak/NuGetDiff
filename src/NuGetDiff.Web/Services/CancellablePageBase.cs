using Microsoft.AspNetCore.Components;

namespace NuGetDiff.Web.Services;

/// <summary>
/// Base for routed page components that need cancellation-aware loads. Each call
/// to <see cref="BeginLoad"/> cancels any prior in-flight load so a quick navigation
/// from one route to another (e.g. <c>/A/1.0</c> → <c>/A/2.0</c>) cannot resolve the
/// stale request after the new one and overwrite page state.
/// </summary>
public abstract class CancellablePageBase : ComponentBase, IDisposable
{
    private CancellationTokenSource? _cts;

    protected CancellationToken BeginLoad()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    public virtual void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}
