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
        var loaded = LoadAssembly(package, path);
        if (!loaded.IsOk) return loaded.Err!;

        var (pe, resolver, _) = loaded.Ok!;
        try
        {
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
        finally
        {
            pe.Dispose();
            resolver.Dispose();
        }
    }

    /// <summary>
    /// Decompiles a single named type (and its nested types) from the assembly at
    /// <paramref name="path"/>. The <paramref name="reflectionName"/> is the same string
    /// returned by <see cref="ListTypes"/> (e.g. <c>Newtonsoft.Json.JsonConvert</c> or
    /// <c>Newtonsoft.Json.JsonConverter`1</c>).
    /// </summary>
    public OneOf<DecompilationResult, DecompilationError> DecompileTypeFromPackage(
        PackageReader package, string path, string reflectionName)
    {
        if (string.IsNullOrEmpty(reflectionName))
        {
            return new DecompilationError("No type name was supplied.");
        }

        var loaded = LoadAssembly(package, path);
        if (!loaded.IsOk) return loaded.Err!;

        var (pe, resolver, _) = loaded.Ok!;
        try
        {
            FullTypeName fullName;
            try
            {
                fullName = new FullTypeName(reflectionName);
            }
            catch (Exception ex)
            {
                return new DecompilationError($"Invalid type name '{reflectionName}'.", ex.Message);
            }

            var decompiler = new CSharpDecompiler(pe, resolver, _settings);

            // Verify the type is actually defined in this module before asking ILSpy
            // to decompile it — otherwise we get a confusing inner-exception.
            var found = false;
            foreach (var handle in pe.Metadata.GetTopLevelTypeDefinitions())
            {
                if (string.Equals(
                        handle.GetFullTypeName(pe.Metadata).ReflectionName,
                        reflectionName,
                        StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return new DecompilationError($"Type '{reflectionName}' was not found in {path}.");
            }

            var csharp = decompiler.DecompileTypeAsString(fullName);

            var warnings = resolver.UnresolvedReferences
                .Select(name => $"Unresolved reference: {name}")
                .ToList();

            return new DecompilationResult(
                csharp,
                warnings,
                new[] { reflectionName },
                SafeAssemblyName(pe),
                ExtractTfm(path));
        }
        catch (Exception ex)
        {
            return new DecompilationError($"Decompilation of '{reflectionName}' failed.", ex.Message);
        }
        finally
        {
            pe.Dispose();
            resolver.Dispose();
        }
    }

    /// <summary>
    /// Lists the top-level types in the assembly at <paramref name="path"/> without
    /// decompiling. Compiler-generated types (those whose name starts with <c>&lt;</c>,
    /// such as <c>&lt;Module&gt;</c> or <c>&lt;PrivateImplementationDetails&gt;</c>) are
    /// filtered out so the navigation tree shows only meaningful entries.
    /// </summary>
    public OneOf<TypeSummary[], DecompilationError> ListTypes(PackageReader package, string path)
    {
        var loaded = LoadAssembly(package, path);
        if (!loaded.IsOk) return loaded.Err!;

        var (pe, resolver, _) = loaded.Ok!;
        try
        {
            var result = new List<TypeSummary>();
            foreach (var handle in pe.Metadata.GetTopLevelTypeDefinitions())
            {
                TypeSummary? summary;
                try
                {
                    summary = TryBuildSummary(handle, pe.Metadata);
                }
                catch
                {
                    summary = null;
                }
                if (summary is not null) result.Add(summary);
            }
            result.Sort(static (a, b) =>
            {
                var ns = StringComparer.Ordinal.Compare(a.Namespace, b.Namespace);
                return ns != 0 ? ns : StringComparer.Ordinal.Compare(a.ReflectionName, b.ReflectionName);
            });
            return result.ToArray();
        }
        catch (Exception ex)
        {
            return new DecompilationError("Failed to list types.", ex.Message);
        }
        finally
        {
            pe.Dispose();
            resolver.Dispose();
        }
    }

    private static TypeSummary? TryBuildSummary(
        System.Reflection.Metadata.TypeDefinitionHandle handle,
        System.Reflection.Metadata.MetadataReader metadata)
    {
        var fullName = handle.GetFullTypeName(metadata);
        var topLevel = fullName.TopLevelTypeName;
        var rawName = topLevel.Name;
        // Skip compiler-generated / unspeakable types.
        if (string.IsNullOrEmpty(rawName)) return null;
        if (rawName[0] == '<') return null;

        var reflectionName = fullName.ReflectionName;
        var arity = topLevel.TypeParameterCount;
        var displayName = arity > 0
            ? $"{rawName}<{new string(',', arity - 1)}>"
            : rawName;

        return new TypeSummary(
            ReflectionName: reflectionName,
            Namespace: topLevel.Namespace ?? string.Empty,
            DisplayName: displayName);
    }

    private OneOf<(PEFile pe, PackageAssemblyResolver resolver, byte[] bytes), DecompilationError> LoadAssembly(
        PackageReader package, string path)
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
            var pe = new PEFile(path, stream);
            var resolver = new PackageAssemblyResolver(package, GetFolder(path));
            return (pe, resolver, bytes);
        }
        catch (Exception ex)
        {
            return new DecompilationError("Failed to open assembly.", ex.Message);
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
