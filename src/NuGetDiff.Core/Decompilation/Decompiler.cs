using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using NuGetDiff.Core.Diffing;
using NuGetDiff.Core.Models;
using NuGetDiff.Core.Packages;

namespace NuGetDiff.Core.Decompilation;

/// <summary>
/// Wraps ILSpy's <see cref="CSharpDecompiler"/> with package-aware assembly resolution.
/// All input is read via streams; no filesystem access is performed, which is what makes
/// this safe to run inside Blazor WebAssembly.
/// </summary>
public sealed class Decompiler
{
    private readonly DecompilerSettings _settings;

    public Decompiler(DecompilerSettings? settings = null)
    {
        _settings = settings ?? new DecompilerSettings(LanguageVersion.Latest)
        {
            ShowXmlDocumentation = true,
            UsingDeclarations = true,
            UseExpressionBodyForCalculatedGetterOnlyProperties = true,
            ThrowOnAssemblyResolveErrors = false,
        };
    }

    /// <summary>
    /// Decompiles a single managed assembly contained in the package at <paramref name="path"/>.
    /// Returns either a <see cref="DecompilationResult"/> (success) or a
    /// <see cref="DecompilationError"/> (graceful failure — never throws into the UI).
    /// </summary>
    public OneOf<DecompilationResult, DecompilationError> DecompileFromPackage(PackageReader package, string path)
    {
        if (!package.ContainsFile(path))
        {
            return new DecompilationError($"File '{path}' not found in package.");
        }

        byte[] bytes;
        try
        {
            bytes = package.ReadFileBytes(path);
        }
        catch (Exception ex)
        {
            return new DecompilationError("Failed to read file from package.", ex.Message);
        }

        // Cheap pre-flight: avoid invoking the decompiler on native PEs and other non-managed binaries.
        using (var probe = new MemoryStream(bytes, writable: false))
        {
            if (!FileClassifier.IsManagedAssembly(probe, leaveOpen: false))
            {
                return new DecompilationError(
                    "File is not a managed .NET assembly.",
                    "It may be a native binary, a resource file, or otherwise not contain CLR metadata.");
            }
        }

        try
        {
            var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEFile(path, stream);
            using var resolver = new PackageAssemblyResolver(package, GetFolder(path));

            var decompiler = new CSharpDecompiler(pe, resolver, _settings);
            var csharp = decompiler.DecompileWholeModuleAsString();

            var typeNames = new List<string>();
            foreach (var type in pe.Metadata.GetTopLevelTypeDefinitions())
            {
                try
                {
                    typeNames.Add(type.GetFullTypeName(pe.Metadata).ReflectionName);
                }
                catch
                {
                    // Skip types we cannot read; keeps the rest usable.
                }
            }
            typeNames.Sort(StringComparer.Ordinal);

            var warnings = resolver.UnresolvedReferences
                .Select(name => $"Unresolved reference: {name}")
                .ToList();

            var assemblyName = SafeAssemblyName(pe);
            var tfm = ExtractTfm(path);

            return new DecompilationResult(csharp, warnings, typeNames, assemblyName, tfm);
        }
        catch (Exception ex)
        {
            return new DecompilationError("Decompilation failed.", ex.Message);
        }
    }

    private static string GetFolder(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash > 0 ? path[..lastSlash] : string.Empty;
    }

    private static string? ExtractTfm(string path)
    {
        var parts = path.Split('/');
        if (parts.Length >= 3
            && (string.Equals(parts[0], "lib", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parts[0], "ref", StringComparison.OrdinalIgnoreCase)))
        {
            return parts[1];
        }
        return null;
    }

    private static string? SafeAssemblyName(PEFile pe)
    {
        try
        {
            var def = pe.Metadata.GetAssemblyDefinition();
            return pe.Metadata.GetString(def.Name);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Tiny ad-hoc result union for "result or error".</summary>
public readonly struct OneOf<TOk, TErr>
{
    public TOk? Ok { get; }
    public TErr? Err { get; }
    public bool IsOk { get; }

    private OneOf(TOk? ok, TErr? err, bool isOk)
    {
        Ok = ok;
        Err = err;
        IsOk = isOk;
    }

    public static implicit operator OneOf<TOk, TErr>(TOk ok) => new(ok, default, true);
    public static implicit operator OneOf<TOk, TErr>(TErr err) => new(default, err, false);

    public T Match<T>(Func<TOk, T> ok, Func<TErr, T> err) => IsOk ? ok(Ok!) : err(Err!);
}
