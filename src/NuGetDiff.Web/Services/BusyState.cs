using Microsoft.AspNetCore.Components;

namespace NuGetDiff.Web.Services;

/// <summary>
/// Drives a top-level "I am working on something" overlay for long-running operations
/// (decompiling assemblies, computing diffs). Default Blazor WASM is single-threaded —
/// long synchronous CPU work freezes the UI — so we surface progress via this service
/// after every <c>await Task.Yield()</c> chunk in the workload.
/// </summary>
public sealed class BusyState
{
    private int _depth;
    private string? _message;

    public bool IsBusy => _depth > 0;
    public string? Message => _message;

    public event Action? Changed;

    public IDisposable Begin(string message)
    {
        _depth++;
        _message = message;
        Changed?.Invoke();
        return new Scope(this);
    }

    public void Update(string message)
    {
        _message = message;
        Changed?.Invoke();
    }

    private void End()
    {
        _depth = Math.Max(0, _depth - 1);
        if (_depth == 0)
        {
            _message = null;
        }
        Changed?.Invoke();
    }

    private sealed class Scope : IDisposable
    {
        private readonly BusyState _state;
        private bool _disposed;
        public Scope(BusyState state) { _state = state; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _state.End();
        }
    }
}
