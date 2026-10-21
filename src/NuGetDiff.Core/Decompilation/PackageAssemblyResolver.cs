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
    private readonly Dictionary<string, PEFile> _byReference = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _pathsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _unresolved = new();
    private readonly HashSet<string> _unresolvedReferences = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> UnresolvedReferences => _unresolved;

    public PackageAssemblyResolver(PackageReader reader, string baseFolder)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _baseFolder = (baseFolder ?? string.Empty).TrimEnd('/', '\\');
        foreach (var entry in _reader.Files)
        {
            if (entry.Kind == FileKind.Assembly)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(entry.Path);
                if (!_pathsByName.TryGetValue(name, out var paths))
                {
                    paths = new List<string>();
                    _pathsByName[name] = paths;
                }
                paths.Add(entry.Path);
            }
        }
    }

    public MetadataFile? Resolve(IAssemblyReference reference)
    {
        if (_byReference.TryGetValue(reference.FullName, out var cached))
        {
            return cached;
        }
        if (_unresolvedReferences.Contains(reference.FullName))
        {
            return null;
        }

        if (TryLoadInFolder(_baseFolder, reference, out var pe))
        {
            return pe;
        }

        // Search anywhere else in the package: lib/<tfm>/<name>.dll, ref/<tfm>/<name>.dll, etc.
        if (_pathsByName.TryGetValue(reference.Name, out var paths))
        {
            foreach (var path in paths)
            {
                if (TryLoadByPath(path, reference, out pe))
                {
                    return pe;
                }
            }
        }

        if (!_unresolved.Contains(reference.Name, StringComparer.OrdinalIgnoreCase))
        {
            _unresolved.Add(reference.Name);
        }
        _unresolvedReferences.Add(reference.FullName);
        return null;
    }

    public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) => null;

    public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult<MetadataFile?>(Resolve(reference));

    public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult<MetadataFile?>(null);

    private bool TryLoadInFolder(
        string folder,
        IAssemblyReference reference,
        out PEFile? file)
    {
        if (string.IsNullOrEmpty(folder))
        {
            file = null;
            return false;
        }

        var candidate = $"{folder}/{reference.Name}.dll";
        return TryLoadByPath(candidate, reference, out file);
    }

    private bool TryLoadByPath(
        string path,
        IAssemblyReference reference,
        out PEFile? file)
    {
        if (_byPath.TryGetValue(path, out var cached))
        {
            if (MatchesReference(cached, reference))
            {
                _byReference[reference.FullName] = cached;
                file = cached;
                return true;
            }

            file = null;
            return false;
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
            if (!MatchesReference(pe, reference))
            {
                pe.Dispose();
                file = null;
                return false;
            }
            _byPath[path] = pe;
            _byReference[reference.FullName] = pe;
            file = pe;
            return true;
        }
        catch
        {
            file = null;
            return false;
        }
    }

    private static bool MatchesReference(
        MetadataFile file,
        IAssemblyReference reference)
        => string.Equals(file.Name, reference.Name, StringComparison.OrdinalIgnoreCase)
           && (reference.IsRetargetable
               || string.Equals(
                   file.FullName,
                   reference.FullName,
                   StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        foreach (var pe in _byPath.Values)
        {
            try { pe.Dispose(); }
            catch { /* defensive: never throw from Dispose */ }
        }
        _byPath.Clear();
        _byReference.Clear();
        _unresolvedReferences.Clear();
    }
}
