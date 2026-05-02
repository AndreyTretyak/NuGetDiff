using ICSharpCode.Decompiler.Metadata;
using NuGetDiff.Core.Models;
using NuGetDiff.Core.Packages;

namespace NuGetDiff.Core.Decompilation;

/// <summary>
/// In-memory <see cref="IAssemblyResolver"/> that resolves references against the
/// assemblies inside a single NuGet package. Does not touch the filesystem; safe for WASM.
/// <para>
/// We intentionally do NOT use <c>UniversalAssemblyResolver</c> — it walks the local
/// filesystem and would fail under Blazor WebAssembly. Missing references are tolerated:
/// the decompiler degrades gracefully and the warnings list captures unresolved names.
/// </para>
/// </summary>
public sealed class PackageAssemblyResolver : IAssemblyResolver, IDisposable
{
    private readonly PackageReader _reader;
    private readonly string _baseFolder;
    private readonly Dictionary<string, PEFile> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PEFile> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _unresolved = new();

    public IReadOnlyList<string> UnresolvedReferences => _unresolved;

    public PackageAssemblyResolver(PackageReader reader, string baseFolder)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _baseFolder = (baseFolder ?? string.Empty).TrimEnd('/', '\\');
    }

    public MetadataFile? Resolve(IAssemblyReference reference)
    {
        if (TryLoadInFolder(_baseFolder, reference.Name, out var pe))
        {
            return pe;
        }

        // Search anywhere else in the package: lib/<tfm>/<name>.dll, ref/<tfm>/<name>.dll, etc.
        foreach (var entry in _reader.Files)
        {
            if (entry.Kind != FileKind.Assembly)
            {
                continue;
            }

            var fileName = System.IO.Path.GetFileNameWithoutExtension(entry.Path);
            if (string.Equals(fileName, reference.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (TryLoadByPath(entry.Path, out pe))
                {
                    return pe;
                }
            }
        }

        if (!_unresolved.Contains(reference.Name, StringComparer.OrdinalIgnoreCase))
        {
            _unresolved.Add(reference.Name);
        }
        return null;
    }

    public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) => null;

    public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult<MetadataFile?>(Resolve(reference));

    public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult<MetadataFile?>(null);

    private bool TryLoadInFolder(string folder, string name, out PEFile? file)
    {
        if (string.IsNullOrEmpty(folder))
        {
            file = null;
            return false;
        }

        var candidate = $"{folder}/{name}.dll";
        return TryLoadByPath(candidate, out file);
    }

    private bool TryLoadByPath(string path, out PEFile? file)
    {
        if (_byPath.TryGetValue(path, out var cached))
        {
            file = cached;
            return true;
        }

        if (!_reader.ContainsFile(path))
        {
            file = null;
            return false;
        }

        try
        {
            var bytes = _reader.ReadFileBytes(path);
            var ms = new MemoryStream(bytes, writable: false);
            // PEFile takes ownership of the stream; it disposes it when itself is disposed.
            var pe = new PEFile(path, ms);
            _byPath[path] = pe;
            _byName[System.IO.Path.GetFileNameWithoutExtension(path)] = pe;
            file = pe;
            return true;
        }
        catch
        {
            file = null;
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var pe in _byPath.Values)
        {
            try { pe.Dispose(); }
            catch { /* defensive: never throw from Dispose */ }
        }
        _byPath.Clear();
        _byName.Clear();
    }
}
