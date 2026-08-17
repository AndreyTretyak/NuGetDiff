using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Output;
using ICSharpCode.Decompiler.TypeSystem;
using NuGetDiff.Core.Diffing;
using NuGetDiff.Core.Models;
using NuGetDiff.Core.Packages;
using NuGetDiff.Core.Util;
using System.Text;
using System.Text.RegularExpressions;

namespace NuGetDiff.Core.Decompilation;

/// <summary>
/// Wraps ILSpy's <see cref="CSharpDecompiler"/> with package-aware assembly resolution.
/// All input is read via streams; no filesystem access is performed, which is what makes
/// this safe to run inside Blazor WebAssembly.
/// </summary>
public sealed partial class Decompiler
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

    private sealed record GeneratedSymbol(
        string Token,
        string Prefix,
        int MajorOrdinal,
        int MinorOrdinal);

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
        PackageReader package,
        string path,
        string reflectionName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
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

            var decompiler = new CSharpDecompiler(pe, resolver, _settings)
            {
                CancellationToken = ct,
            };

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

    public IReadOnlyList<TypeDecompilation> DecompileTypesFromPackage(
        PackageReader package,
        string path,
        IEnumerable<string> reflectionNames,
        CancellationToken ct = default)
    {
        var names = reflectionNames
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0)
        {
            return Array.Empty<TypeDecompilation>();
        }

        var loaded = LoadAssembly(package, path);
        if (!loaded.IsOk)
        {
            return names
                .Select(name => new TypeDecompilation(name, null, loaded.Err))
                .ToArray();
        }

        var (pe, resolver, _) = loaded.Ok!;
        try
        {
            ct.ThrowIfCancellationRequested();
            var handlesByName = pe.Metadata.GetTopLevelTypeDefinitions()
                .ToDictionary(
                    handle => handle.GetFullTypeName(pe.Metadata).ReflectionName,
                    StringComparer.Ordinal);
            var decompiler = new CSharpDecompiler(pe, resolver, _settings)
            {
                CancellationToken = ct,
            };
            var results = new Dictionary<string, TypeDecompilation>(StringComparer.Ordinal);
            var requested = new List<(string Name, System.Reflection.Metadata.TypeDefinitionHandle Handle)>();
            foreach (var reflectionName in names)
            {
                ct.ThrowIfCancellationRequested();
                if (!handlesByName.TryGetValue(reflectionName, out var handle))
                {
                    results[reflectionName] = new TypeDecompilation(
                        reflectionName,
                        null,
                        new DecompilationError($"Type '{reflectionName}' was not found in {path}."));
                }
                else
                {
                    requested.Add((reflectionName, handle));
                }
            }
            if (requested.Count > 0)
            {
                var syntaxTree = decompiler.DecompileTypes(requested.Select(item => item.Handle));
                var declarations = TopLevelTypeDeclarations(syntaxTree)
                    .ToDictionary(
                        item => item.ReflectionName,
                        item => item.Declaration,
                        StringComparer.Ordinal);
                var warnings = resolver.UnresolvedReferences
                    .Select(name => $"Unresolved reference: {name}")
                    .ToList();
                foreach (var item in requested)
                {
                    if (!declarations.TryGetValue(item.Name, out var declaration))
                    {
                        results[item.Name] = new TypeDecompilation(
                            item.Name,
                            null,
                            new DecompilationError(
                                $"Decompilation of '{item.Name}' did not produce a type declaration."));
                        continue;
                    }

                    results[item.Name] = new TypeDecompilation(
                        item.Name,
                        new DecompilationResult(
                            declaration.ToString(),
                            warnings,
                            new[] { item.Name },
                            SafeAssemblyName(pe),
                            ExtractTfm(path)),
                        null);
                }
            }

            return names.Select(name => results[name]).ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return names
                .Select(name => new TypeDecompilation(
                    name,
                    null,
                    new DecompilationError("Failed to compare assembly types.", ex.Message)))
                .ToArray();
        }
        finally
        {
            pe.Dispose();
            resolver.Dispose();
        }
    }

    public async Task<IReadOnlyList<TypeDecompilation>> DecompileTypesFromPackageAsync(
        PackageReader package,
        string path,
        IEnumerable<string> reflectionNames,
        CancellationToken ct = default)
    {
        var names = reflectionNames
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0)
        {
            return Array.Empty<TypeDecompilation>();
        }

        var loaded = LoadAssembly(package, path);
        if (!loaded.IsOk)
        {
            return names
                .Select(name => new TypeDecompilation(name, null, loaded.Err))
                .ToArray();
        }

        var (pe, resolver, _) = loaded.Ok!;
        try
        {
            var available = pe.Metadata.GetTopLevelTypeDefinitions()
                .Select(handle => handle.GetFullTypeName(pe.Metadata).ReflectionName)
                .ToHashSet(StringComparer.Ordinal);
            var decompiler = new CSharpDecompiler(pe, resolver, _settings)
            {
                CancellationToken = ct,
            };
            var results = new List<TypeDecompilation>(names.Length);
            for (var index = 0; index < names.Length; index++)
            {
                if (index > 0)
                {
                    await Task.Yield();
                }
                ct.ThrowIfCancellationRequested();
                var reflectionName = names[index];
                if (!available.Contains(reflectionName))
                {
                    results.Add(new TypeDecompilation(
                        reflectionName,
                        null,
                        new DecompilationError($"Type '{reflectionName}' was not found in {path}.")));
                    continue;
                }

                try
                {
                    var csharp = decompiler.DecompileTypeAsString(
                        new FullTypeName(reflectionName));
                    var warnings = resolver.UnresolvedReferences
                        .Select(name => $"Unresolved reference: {name}")
                        .ToList();
                    results.Add(new TypeDecompilation(
                        reflectionName,
                        new DecompilationResult(
                            csharp,
                            warnings,
                            new[] { reflectionName },
                            SafeAssemblyName(pe),
                            ExtractTfm(path)),
                        null));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    results.Add(new TypeDecompilation(
                        reflectionName,
                        null,
                        new DecompilationError(
                            $"Decompilation of '{reflectionName}' failed.",
                            ex.Message)));
                }
            }
            return results;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return names
                .Select(name => new TypeDecompilation(
                    name,
                    null,
                    new DecompilationError(
                        "Failed to compare assembly types.",
                        ex.Message)))
                .ToArray();
        }
        finally
        {
            pe.Dispose();
            resolver.Dispose();
        }
    }

    private static IEnumerable<(string ReflectionName, EntityDeclaration Declaration)>
        TopLevelTypeDeclarations(SyntaxTree syntaxTree)
    {
        foreach (var member in syntaxTree.Members)
        {
            if (TryGetTypeDeclaration(member, out var declaration))
            {
                yield return declaration;
            }
            else if (member is NamespaceDeclaration @namespace)
            {
                foreach (var namespaceMember in @namespace.Members)
                {
                    if (TryGetTypeDeclaration(namespaceMember, out declaration))
                    {
                        yield return declaration;
                    }
                }
            }
        }

        static bool TryGetTypeDeclaration(
            AstNode node,
            out (string ReflectionName, EntityDeclaration Declaration) declaration)
        {
            if (node is EntityDeclaration entity
                && node is TypeDeclaration or DelegateDeclaration
                && entity.GetSymbol() is ITypeDefinition type)
            {
                declaration = (type.FullTypeName.ReflectionName, entity);
                return true;
            }

            declaration = default;
            return false;
        }
    }

    public async Task<IReadOnlyList<TypeFingerprint>> FingerprintTypesFromPackageAsync(
        PackageReader package,
        string path,
        IEnumerable<string> reflectionNames,
        CancellationToken ct = default)
    {
        var names = reflectionNames
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0)
        {
            return Array.Empty<TypeFingerprint>();
        }

        var loaded = LoadAssembly(package, path);
        if (!loaded.IsOk)
        {
            return names
                .Select(name => new TypeFingerprint(name, null, loaded.Err))
                .ToArray();
        }

        var (pe, resolver, bytes) = loaded.Ok!;
        try
        {
            var handlesByName = pe.Metadata.GetTopLevelTypeDefinitions()
                .ToDictionary(
                    handle => handle.GetFullTypeName(pe.Metadata).ReflectionName,
                    StringComparer.Ordinal);
            var results = new List<TypeFingerprint>(names.Length);
            var moduleHash = HashUtil.Sha256Hex(bytes);
            var (staticDataHashes, staticDataError) =
                await GetStaticDataHashesAsync(pe, ct);
            var privateImplementationMemberHashes =
                await GetPrivateImplementationMemberHashesAsync(
                    pe,
                    resolver,
                    staticDataHashes,
                    moduleHash,
                    ct);
            var hiddenHelpers = await BuildHiddenHelperFingerprintsAsync(
                pe,
                resolver,
                staticDataHashes,
                privateImplementationMemberHashes,
                moduleHash,
                ct);
            for (var index = 0; index < names.Length; index++)
            {
                if (index > 0 && index % 8 == 0)
                {
                    await Task.Yield();
                }
                ct.ThrowIfCancellationRequested();
                var reflectionName = names[index];
                if (!handlesByName.TryGetValue(reflectionName, out var handle))
                {
                    results.Add(new TypeFingerprint(
                        reflectionName,
                        null,
                        new DecompilationError($"Type '{reflectionName}' was not found in {path}.")));
                    continue;
                }

                try
                {
                    var output = new StringBuilder();
                    using var writer = new StringWriter(output)
                    {
                        NewLine = "\n",
                    };
                    var disassembler = new ReflectionDisassembler(
                        new PlainTextOutput(writer),
                        ct)
                    {
                        AssemblyResolver = resolver,
                        DetectControlStructure = false,
                        ExpandMemberDefinitions = true,
                        ShowMetadataTokens = false,
                        ShowRawRVAOffsetAndBytes = false,
                    };
                    disassembler.DisassembleType(pe, handle);
                    var fingerprint = NormalizeFingerprint(output.ToString());
                    fingerprint = AppendAssemblyReferenceIdentities(
                        fingerprint,
                        pe,
                        moduleHash);
                    foreach (var staticData in staticDataHashes
                                 .Where(item => ReferencesStaticData(fingerprint, item))
                                 .OrderBy(item => item.DeclaringType, StringComparer.Ordinal)
                                 .ThenBy(item => item.FieldName, StringComparer.Ordinal))
                    {
                        fingerprint += $"\n// Static data {staticData.DeclaringType}::{staticData.FieldName}: {staticData.Hash}";
                    }
                    foreach (var (memberName, hash) in privateImplementationMemberHashes
                                 .Where(pair => ReferencesMember(fingerprint, pair.Key))
                                 .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        fingerprint += $"\n// Private member {memberName}: {hash}";
                    }
                    if (staticDataError is not null
                        && RequiresModuleContext(
                            fingerprint,
                            new[] { "<PrivateImplementationDetails>" }))
                    {
                        fingerprint += "\n// Static data inspection fallback: " + moduleHash;
                    }
                    var referencedHelpers = hiddenHelpers
                        .Where(helper => ReferencesType(
                            fingerprint,
                            helper.ReflectionName,
                            helper.SimpleName))
                        .OrderBy(helper => helper.SimpleName, StringComparer.Ordinal)
                        .ThenBy(helper => helper.ReflectionName, StringComparer.Ordinal);
                    foreach (var helper in referencedHelpers)
                    {
                        fingerprint += $"\n// Hidden helper {helper.ReflectionName}: {helper.ContentHash}";
                    }
                    fingerprint = CanonicalizeFingerprint(fingerprint);

                    results.Add(new TypeFingerprint(
                        reflectionName,
                        HashUtil.Sha256Hex(fingerprint.AsSpan()),
                        null));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    results.Add(new TypeFingerprint(
                        reflectionName,
                        null,
                        new DecompilationError(
                            $"Failed to inspect '{reflectionName}'.",
                            ex.Message)));
                }
            }
            return results;
        }
        finally
        {
            pe.Dispose();
            resolver.Dispose();
        }
    }

    private static async Task<IReadOnlyList<HiddenHelperFingerprint>>
        BuildHiddenHelperFingerprintsAsync(
        PEFile pe,
        PackageAssemblyResolver resolver,
        IReadOnlyList<StaticDataHash> staticDataHashes,
        IReadOnlyDictionary<string, string> privateImplementationMemberHashes,
        string moduleHash,
        CancellationToken ct)
    {
        var helpers = new List<HiddenHelperContent>();
        var output = new StringBuilder();
        using var writer = new StringWriter(output)
        {
            NewLine = "\n",
        };
        var disassembler = new ReflectionDisassembler(
            new PlainTextOutput(writer),
            ct)
        {
            AssemblyResolver = resolver,
            DetectControlStructure = false,
            ExpandMemberDefinitions = true,
            ShowMetadataTokens = false,
            ShowRawRVAOffsetAndBytes = false,
        };

        var handles = pe.Metadata.GetTopLevelTypeDefinitions().ToArray();
        for (var index = 0; index < handles.Length; index++)
        {
            if (index > 0 && index % 8 == 0)
            {
                await Task.Yield();
            }
            ct.ThrowIfCancellationRequested();
            var handle = handles[index];
            var definition = pe.Metadata.GetTypeDefinition(handle);
            var simpleName = pe.Metadata.GetString(definition.Name);
            if (!IsHiddenHelperTypeName(simpleName)
                || IsPrivateImplementationTypeName(simpleName))
            {
                continue;
            }

            output.Clear();
            disassembler.DisassembleType(pe, handle);
            var rawContent = NormalizeFingerprint(output.ToString());
            rawContent = AppendAssemblyReferenceIdentities(
                rawContent,
                pe,
                moduleHash);
            foreach (var staticData in staticDataHashes
                         .Where(item => ReferencesStaticData(rawContent, item))
                         .OrderBy(item => item.DeclaringType, StringComparer.Ordinal)
                         .ThenBy(item => item.FieldName, StringComparer.Ordinal))
            {
                rawContent += $"\n// Static data {staticData.DeclaringType}::{staticData.FieldName}: {staticData.Hash}";
            }
            foreach (var (memberName, hash) in privateImplementationMemberHashes
                         .Where(pair => ReferencesMember(rawContent, pair.Key))
                         .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                rawContent += $"\n// Private member {memberName}: {hash}";
            }
            var canonicalContent = CanonicalizeFingerprint(rawContent);
            helpers.Add(new HiddenHelperContent(
                simpleName,
                handle.GetFullTypeName(pe.Metadata).ReflectionName,
                rawContent,
                HashUtil.Sha256Hex(canonicalContent.AsSpan())));
        }

        var dependencies = new IReadOnlyList<int>[helpers.Count];
        for (var index = 0; index < helpers.Count; index++)
        {
            if (index > 0 && index % 8 == 0)
            {
                await Task.Yield();
            }
            ct.ThrowIfCancellationRequested();
            var current = helpers[index];
            var referenced = new List<int>();
            for (var candidateIndex = 0; candidateIndex < helpers.Count; candidateIndex++)
            {
                if (candidateIndex == index)
                {
                    continue;
                }
                var candidate = helpers[candidateIndex];
                if (ReferencesType(
                        current.RawContent,
                        candidate.ReflectionName,
                        candidate.SimpleName))
                {
                    referenced.Add(candidateIndex);
                }
            }
            dependencies[index] = referenced;
        }

        var result = new List<HiddenHelperFingerprint>(helpers.Count);
        for (var index = 0; index < helpers.Count; index++)
        {
            if (index > 0 && index % 8 == 0)
            {
                await Task.Yield();
            }
            ct.ThrowIfCancellationRequested();
            var reachable = new HashSet<int>();
            var pending = new Stack<int>();
            pending.Push(index);
            while (pending.TryPop(out var current))
            {
                ct.ThrowIfCancellationRequested();
                if (!reachable.Add(current))
                {
                    continue;
                }
                foreach (var dependency in dependencies[current])
                {
                    pending.Push(dependency);
                }
            }

            var closure = string.Join(
                '\n',
                reachable
                    .Select(item => helpers[item].ContentHash)
                    .OrderBy(hash => hash, StringComparer.Ordinal));
            var helper = helpers[index];
            result.Add(new HiddenHelperFingerprint(
                helper.SimpleName,
                helper.ReflectionName,
                HashUtil.Sha256Hex(closure.AsSpan())));
        }
        return result;
    }

    internal static string CanonicalizeFingerprint(string content)
    {
        var surface = new StringBuilder(content.Length);
        var nestedBlocks = new List<string>();
        var helperAnnotations = new List<string>();
        StringBuilder? nestedBlock = null;
        var inNestedSection = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!inNestedSection
                && line.StartsWith("// Hidden helper ", StringComparison.Ordinal))
            {
                helperAnnotations.Add(line);
                continue;
            }
            if (!inNestedSection && line == "\t// Nested Types")
            {
                inNestedSection = true;
                continue;
            }

            if (inNestedSection)
            {
                var sectionEnded = line.Length > 0
                    && (line[0] != '\t'
                        || (line.StartsWith("\t// ", StringComparison.Ordinal)
                            && !line.StartsWith("\t// end ", StringComparison.Ordinal)));
                if (sectionEnded)
                {
                    AddNestedBlock();
                    inNestedSection = false;
                    surface.Append(line).Append('\n');
                    continue;
                }

                if (line.StartsWith("\t.class nested ", StringComparison.Ordinal))
                {
                    AddNestedBlock();
                    nestedBlock = new StringBuilder();
                }
                nestedBlock?.Append(line).Append('\n');
                continue;
            }

            surface.Append(line).Append('\n');
        }
        AddNestedBlock();

        var symbols = new GeneratedSymbolMap();
        var canonical = new StringBuilder(content.Length);
        canonical.Append(
            NormalizeGeneratedSymbols(
                surface.ToString(),
                symbols,
                "surface"));
        var blockGroups = nestedBlocks
            .Select(block => new
            {
                Content = block,
                SortKey = GeneratedSymbolSortKey(block, symbols),
            })
            .GroupBy(block => block.SortKey, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in blockGroups)
        {
            var scope = HashUtil.Sha256Hex(group.Key.AsSpan())[..12];
            var nextSymbolId = 0;
            foreach (var symbol in group
                         .SelectMany(block => GeneratedSymbols(block.Content))
                         .Where(symbol => !symbols.Replacements.ContainsKey(symbol.Token))
                         .Distinct()
                         .OrderBy(symbol => symbol.Prefix, StringComparer.Ordinal)
                         .ThenBy(symbol => symbol.MajorOrdinal)
                         .ThenBy(symbol => symbol.MinorOrdinal)
                         .ThenBy(symbol => symbol.Token, StringComparer.Ordinal))
            {
                symbols.Replacements.Add(
                    symbol.Token,
                    symbol.Prefix + "#" + scope + "_" + nextSymbolId++);
            }

            foreach (var block in group
                         .Select(block => NormalizeGeneratedSymbols(
                             block.Content,
                             symbols,
                             scope))
                         .OrderBy(block => block, StringComparer.Ordinal))
            {
                canonical.Append("\n// Nested type\n").Append(block);
            }
        }
        var annotationGroups = helperAnnotations
            .Select(annotation => new
            {
                Content = annotation,
                SortKey = GeneratedSymbolSortKey(annotation, symbols),
            })
            .GroupBy(annotation => annotation.SortKey, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in annotationGroups)
        {
            var scope = HashUtil.Sha256Hex(group.Key.AsSpan())[..12];
            var nextSymbolId = 0;
            foreach (var symbol in group
                         .SelectMany(annotation => GeneratedSymbols(annotation.Content))
                         .Where(symbol => !symbols.Replacements.ContainsKey(symbol.Token))
                         .Distinct()
                         .OrderBy(symbol => symbol.Prefix, StringComparer.Ordinal)
                         .ThenBy(symbol => symbol.MajorOrdinal)
                         .ThenBy(symbol => symbol.MinorOrdinal)
                         .ThenBy(symbol => symbol.Token, StringComparer.Ordinal))
            {
                symbols.Replacements.Add(
                    symbol.Token,
                    symbol.Prefix + "#" + scope + "_" + nextSymbolId++);
            }

            foreach (var annotation in group
                         .Select(annotation => NormalizeGeneratedSymbols(
                             annotation.Content,
                             symbols,
                             scope))
                         .OrderBy(annotation => annotation, StringComparer.Ordinal))
            {
                canonical.Append('\n').Append(annotation);
            }
        }
        return canonical.ToString();

        void AddNestedBlock()
        {
            if (nestedBlock is null)
            {
                return;
            }
            nestedBlocks.Add(nestedBlock.ToString().TrimEnd('\n'));
            nestedBlock = null;
        }
    }

    private static string NormalizeGeneratedSymbols(
        string content,
        GeneratedSymbolMap symbols,
        string scope)
    {
        var nextSymbolId = 0;
        return GeneratedOrdinalRegex().Replace(
            content,
            match =>
            {
                var prefix = match.Groups["prefix"];
                if (!prefix.Success)
                {
                    return match.Value;
                }
                if (!symbols.Replacements.TryGetValue(match.Value, out var replacement))
                {
                    replacement = prefix.Value
                                  + "#"
                                  + scope
                                  + "_"
                                  + nextSymbolId++;
                    symbols.Replacements.Add(match.Value, replacement);
                }
                return replacement;
            });
    }

    private static string GeneratedSymbolSortKey(
        string content,
        GeneratedSymbolMap knownSymbols)
        => GeneratedOrdinalRegex().Replace(
            content,
            match =>
            {
                var prefix = match.Groups["prefix"];
                if (!prefix.Success)
                {
                    return match.Value;
                }
                return knownSymbols.Replacements.TryGetValue(match.Value, out var replacement)
                    ? replacement
                    : prefix.Value + "#?";
            });

    private static IEnumerable<GeneratedSymbol> GeneratedSymbols(
        string content)
    {
        foreach (Match match in GeneratedOrdinalRegex().Matches(content))
        {
            var prefix = match.Groups["prefix"];
            if (prefix.Success)
            {
                var ordinalParts = match.Groups["ordinal"].Value.Split('_', 2);
                yield return new GeneratedSymbol(
                    match.Value,
                    prefix.Value,
                    int.Parse(ordinalParts[0], System.Globalization.CultureInfo.InvariantCulture),
                    ordinalParts.Length == 1
                        ? -1
                        : int.Parse(
                            ordinalParts[1],
                            System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    private sealed class GeneratedSymbolMap
    {
        public Dictionary<string, string> Replacements { get; } =
            new(StringComparer.Ordinal);
    }

    [GeneratedRegex(
        "\"(?:\\\\.|[^\"\\\\])*\"|string\\('(?:\\\\.|[^'\\\\])*'\\)|(?<prefix><>c__DisplayClass)(?<ordinal>\\d+(?:_\\d+)?)|(?<prefix><>f__AnonymousType)(?<ordinal>\\d+)|(?<prefix><[^>\\r\\n]+>b__)(?<ordinal>\\d+(?:_\\d+)?)|(?<prefix><[^>\\r\\n]+>d__)(?<ordinal>\\d+)|(?<prefix><>9__)(?<ordinal>\\d+(?:_\\d+)?)|(?<prefix><>8__locals|<>[A-Za-z][A-Za-z0-9]*__)(?<ordinal>\\d+)|(?<prefix><[^>\\r\\n]+>g__[^|\\r\\n']+\\|)(?<ordinal>\\d+_\\d+)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex GeneratedOrdinalRegex();

    private static string NormalizeFingerprint(string content)
    {
        var lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith(
                "// Method begins at RVA ",
                StringComparison.Ordinal))
            .Select(NormalizeRvaLine);
        return string.Join('\n', lines);

        static string NormalizeRvaLine(string line)
        {
            var trimmed = line.TrimStart();
            var isRvaSyntax = trimmed.StartsWith(".data ", StringComparison.Ordinal)
                              || (trimmed.StartsWith(".field ", StringComparison.Ordinal)
                                  && trimmed.Contains(" at ", StringComparison.Ordinal));
            if (!isRvaSyntax)
            {
                return line;
            }

            var searchStart = trimmed.StartsWith(".field ", StringComparison.Ordinal)
                ? line.LastIndexOf(" at ", StringComparison.Ordinal) + 4
                : line.IndexOf(".data ", StringComparison.Ordinal) + 6;
            if (searchStart < 4)
            {
                return line;
            }
            if (trimmed.StartsWith(".field ", StringComparison.Ordinal)
                && IsInsideQuotedString(line, searchStart - 4))
            {
                return line;
            }

            for (var index = searchStart; index <= line.Length - 10; index++)
            {
                if (line[index] is not ('I' or 'D' or 'T')
                    || line[index + 1] != '_')
                {
                    continue;
                }
                if (!line.AsSpan(index + 2, 8).ToString().All(Uri.IsHexDigit))
                {
                    continue;
                }
                return line[..(index + 2)] + "RVA" + line[(index + 10)..];
            }
            return line;

            static bool IsInsideQuotedString(string text, int position)
            {
                var quote = '\0';
                for (var index = 0; index < position; index++)
                {
                    var character = text[index];
                    if (character is not ('"' or '\'')
                        || (index > 0 && text[index - 1] == '\\'))
                    {
                        continue;
                    }
                    if (quote == '\0')
                    {
                        quote = character;
                    }
                    else if (quote == character)
                    {
                        quote = '\0';
                    }
                }
                return quote != '\0';
            }
        }
    }

    private static async Task<(
        IReadOnlyList<StaticDataHash> Hashes,
        DecompilationError? Error)> GetStaticDataHashesAsync(
        PEFile pe,
        CancellationToken ct)
    {
        var entries = new List<StaticDataEntry>();
        DecompilationError? error = null;
        var inspectedFields = 0;
        foreach (var handle in pe.Metadata.GetTopLevelTypeDefinitions())
        {
            var definition = pe.Metadata.GetTypeDefinition(handle);
            var declaringType = handle.GetFullTypeName(pe.Metadata).ReflectionName;

            foreach (var fieldHandle in definition.GetFields())
            {
                if (++inspectedFields % 128 == 0)
                {
                    await Task.Yield();
                }
                ct.ThrowIfCancellationRequested();
                var field = pe.Metadata.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & System.Reflection.FieldAttributes.HasFieldRVA) == 0)
                {
                    continue;
                }

                System.Reflection.Metadata.BlobReader data;
                try
                {
                    data = field.GetInitialValue(pe, null);
                }
                catch (BadImageFormatException ex)
                {
                    error = new DecompilationError(
                        "Failed to inspect static assembly data.",
                        ex.Message);
                    continue;
                }
                if (data.Length > 0)
                {
                    var rva = field.GetRelativeVirtualAddress();
                    var sectionIndex = pe.GetContainingSectionIndex(rva);
                    var storage = sectionIndex < 0
                        ? "invalid"
                        : pe.SectionHeaders[sectionIndex].Name;
                    entries.Add(new StaticDataEntry(
                        declaringType,
                        pe.Metadata.GetString(field.Name),
                        rva,
                        storage,
                        data.ReadBytes(data.Length)));
                }
            }
        }

        var overlaps = entries.ToDictionary(
            entry => entry,
            _ => new List<StaticDataEntry>());
        var active = new List<StaticDataEntry>();
        var orderedEntries = entries
            .OrderBy(entry => entry.Rva)
            .ThenBy(entry => entry.Bytes.Length)
            .ThenBy(entry => entry.DeclaringType, StringComparer.Ordinal)
            .ThenBy(entry => entry.FieldName, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < orderedEntries.Length; index++)
        {
            if (index > 0 && index % 128 == 0)
            {
                await Task.Yield();
            }
            ct.ThrowIfCancellationRequested();
            var current = orderedEntries[index];
            active.RemoveAll(other =>
                (long)other.Rva + other.Bytes.Length <= current.Rva);
            foreach (var other in active)
            {
                ct.ThrowIfCancellationRequested();
                if (!RangesOverlap(
                        current.Rva,
                        current.Bytes.Length,
                        other.Rva,
                        other.Bytes.Length))
                {
                    continue;
                }
                overlaps[current].Add(other);
                overlaps[other].Add(current);
            }
            active.Add(current);
        }

        var hashes = new List<StaticDataHash>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            if (index > 0 && index % 128 == 0)
            {
                await Task.Yield();
            }
            ct.ThrowIfCancellationRequested();
            var entry = entries[index];
            var relationships = overlaps[entry]
                .OrderBy(other => other.DeclaringType, StringComparer.Ordinal)
                .ThenBy(other => other.FieldName, StringComparer.Ordinal)
                .Select(other =>
                    $"{other.DeclaringType}::{other.FieldName}:{other.Rva - entry.Rva}:{other.Bytes.Length}");
            var material = Convert.ToHexString(entry.Bytes)
                           + "|storage:"
                           + entry.Storage
                           + "|overlaps:"
                           + string.Join(',', relationships);
            hashes.Add(new StaticDataHash(
                entry.DeclaringType,
                entry.FieldName,
                HashUtil.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(material))));
        }
        return (hashes, error);

        static bool RangesOverlap(
            int firstStart,
            int firstLength,
            int secondStart,
            int secondLength)
            => (long)firstStart < (long)secondStart + secondLength
               && (long)secondStart < (long)firstStart + firstLength;
    }

    private static async Task<IReadOnlyDictionary<string, string>>
        GetPrivateImplementationMemberHashesAsync(
        PEFile pe,
        PackageAssemblyResolver resolver,
        IReadOnlyList<StaticDataHash> staticDataHashes,
        string moduleHash,
        CancellationToken ct)
    {
        var members = new List<PrivateMemberContent>();
        var inspectedMembers = 0;
        foreach (var handle in pe.Metadata.GetTopLevelTypeDefinitions())
        {
            var definition = pe.Metadata.GetTypeDefinition(handle);
            var name = pe.Metadata.GetString(definition.Name);
            if (!IsPrivateImplementationTypeName(name))
            {
                continue;
            }

            var typeMembers = new List<PrivateMemberContent>();
            foreach (var method in definition.GetMethods())
            {
                if (++inspectedMembers % 64 == 0)
                {
                    await Task.Yield();
                }
                ct.ThrowIfCancellationRequested();
                using var writer = new StringWriter();
                var disassembler = CreateDisassembler(writer);
                disassembler.DisassembleMethod(pe, method);
                typeMembers.Add(CreateMember(
                    name,
                    pe.Metadata.GetString(pe.Metadata.GetMethodDefinition(method).Name),
                    writer.ToString()));
            }
            foreach (var fieldHandle in definition.GetFields())
            {
                if (++inspectedMembers % 64 == 0)
                {
                    await Task.Yield();
                }
                ct.ThrowIfCancellationRequested();
                var field = pe.Metadata.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & System.Reflection.FieldAttributes.HasFieldRVA) == 0)
                {
                    using var writer = new StringWriter();
                    var disassembler = CreateDisassembler(writer);
                    disassembler.DisassembleField(pe, fieldHandle);
                    typeMembers.Add(CreateMember(
                        name,
                        pe.Metadata.GetString(field.Name),
                        writer.ToString()));
                }
            }
            var typeInitializer = string.Join(
                "\n",
                typeMembers
                    .Where(member => member.MemberName == ".cctor")
                    .Select(member => member.Content));
            foreach (var member in typeMembers)
            {
                members.Add(member.MemberName == ".cctor" || string.IsNullOrEmpty(typeInitializer)
                    ? member
                    : member with
                    {
                        Content = member.Content
                                  + "\n// Declaring type initializer\n"
                                  + typeInitializer,
                    });
            }
        }

        var membersByKey = members.ToDictionary(
            member => member.Key,
            member => member,
            StringComparer.Ordinal);
        var dependencies = new IReadOnlyList<int>[members.Count];
        for (var index = 0; index < members.Count; index++)
        {
            if (index > 0 && index % 32 == 0)
            {
                await Task.Yield();
            }
            ct.ThrowIfCancellationRequested();
            var referenced = new List<int>();
            for (var candidateIndex = 0; candidateIndex < members.Count; candidateIndex++)
            {
                if (candidateIndex == index)
                {
                    continue;
                }
                var candidate = members[candidateIndex];
                if (ReferencesQualifiedMember(
                        members[index].Content,
                        candidate.DeclaringType,
                        candidate.MemberName))
                {
                    referenced.Add(candidateIndex);
                }
            }
            dependencies[index] = referenced;
        }

        var qualifiedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < members.Count; index++)
        {
            if (index > 0 && index % 32 == 0)
            {
                await Task.Yield();
            }
            ct.ThrowIfCancellationRequested();
            var closure = new HashSet<int>();
            var pending = new Stack<int>();
            pending.Push(index);
            while (pending.TryPop(out var current))
            {
                ct.ThrowIfCancellationRequested();
                if (!closure.Add(current))
                {
                    continue;
                }
                foreach (var dependency in dependencies[current])
                {
                    pending.Push(dependency);
                }
            }
            var closureContent = string.Join(
                "\n",
                closure
                    .Select(item => members[item])
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"// Private member {item.Key}\n{item.Content}"));
            qualifiedHashes[members[index].Key] = HashUtil.Sha256Hex(
                System.Text.Encoding.UTF8.GetBytes(closureContent));
        }
        return qualifiedHashes
            .GroupBy(
                pair => membersByKey[pair.Key].MemberName,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => HashUtil.Sha256Hex(
                    System.Text.Encoding.UTF8.GetBytes(
                        string.Join(
                            "\n",
                            group
                                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                .Select(pair => pair.Value)))),
                StringComparer.Ordinal);

        ReflectionDisassembler CreateDisassembler(StringWriter writer)
            => new(
                new PlainTextOutput(writer),
                ct)
            {
                AssemblyResolver = resolver,
                DetectControlStructure = false,
                ExpandMemberDefinitions = true,
                ShowMetadataTokens = false,
                ShowRawRVAOffsetAndBytes = false,
            };

        PrivateMemberContent CreateMember(
            string declaringType,
            string memberName,
            string rawContent)
        {
            var content = NormalizeFingerprint(rawContent);
            content = AppendAssemblyReferenceIdentities(
                content,
                pe,
                moduleHash);
            foreach (var staticData in staticDataHashes
                         .Where(item => ReferencesStaticData(content, item))
                         .OrderBy(item => item.DeclaringType, StringComparer.Ordinal)
                         .ThenBy(item => item.FieldName, StringComparer.Ordinal))
            {
                content += $"\n// Static data {staticData.DeclaringType}::{staticData.FieldName}: {staticData.Hash}";
            }
            return new PrivateMemberContent(
                $"{declaringType}::{memberName}",
                declaringType,
                memberName,
                content);
        }
    }

    private static string AppendAssemblyReferenceIdentities(
        string content,
        PEFile pe,
        string ambiguousReferenceFallback)
    {
        foreach (var group in pe.AssemblyReferences
                     .GroupBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
                     .Where(group =>
                         content.Contains(
                             $"[{DisassemblerHelpers.Escape(group.Key)}]",
                             StringComparison.OrdinalIgnoreCase))
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var reference in group.OrderBy(
                         reference => reference.FullName,
                         StringComparer.Ordinal))
            {
                content += "\n// Assembly reference: "
                           + reference.FullName
                           + $"|winrt:{reference.IsWindowsRuntime}"
                           + $"|retargetable:{reference.IsRetargetable}";
            }
            if (group.Skip(1).Any())
            {
                content += "\n// Ambiguous assembly reference context: "
                           + ambiguousReferenceFallback;
            }
        }
        return content;
    }

    private static bool RequiresModuleContext(
        string fingerprint,
        IReadOnlyList<string> hiddenTypeNames)
    {
        if (hiddenTypeNames.Any(name =>
                fingerprint.Contains(name, StringComparison.Ordinal)))
        {
            return true;
        }

        return fingerprint
            .Split('\n')
            .Select(line => line.TrimStart())
            .Any(line => line.StartsWith(".data ", StringComparison.Ordinal)
                || (line.StartsWith(".field ", StringComparison.Ordinal)
                    && line.Contains(" at ", StringComparison.Ordinal)));
    }

    private static bool ReferencesMember(string content, string memberName)
    {
        if (ContainsOutsideProtectedLiteral(content, $"::'{memberName}'"))
        {
            return true;
        }

        var marker = "::" + memberName;
        var start = 0;
        while (start < content.Length)
        {
            var index = content.IndexOf(marker, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var end = index + marker.Length;
            if ((end == content.Length || !IsIdentifierCharacter(content[end]))
                && !IsInsideProtectedLiteral(content, index))
            {
                return true;
            }
            start = index + 1;
        }
        return false;
    }

    private static bool ContainsOutsideProtectedLiteral(string content, string value)
    {
        var start = 0;
        while (start < content.Length)
        {
            var index = content.IndexOf(value, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }
            if (!IsInsideProtectedLiteral(content, index))
            {
                return true;
            }
            start = index + 1;
        }
        return false;
    }

    private static bool ReferencesQualifiedMember(
        string content,
        string declaringType,
        string memberName)
        => ReferencesMember(content, memberName)
           && ContainsExactIdentifier(content, declaringType);

    private static bool ReferencesStaticData(
        string content,
        StaticDataHash staticData)
        => ReferencesQualifiedMember(
            content,
            staticData.DeclaringType,
            staticData.FieldName);

    private static bool ReferencesType(
        string content,
        string reflectionName,
        string simpleName)
        => ContainsExactIdentifier(content, reflectionName)
           || ContainsExactIdentifier(content, simpleName);

    private static bool ContainsExactIdentifier(string content, string identifier)
    {
        var start = 0;
        while (start < content.Length)
        {
            var index = content.IndexOf(identifier, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var end = index + identifier.Length;
            var hasLeftBoundary = index == 0 || !IsIdentifierCharacter(content[index - 1]);
            var hasRightBoundary = end == content.Length || !IsIdentifierCharacter(content[end]);
            if (hasLeftBoundary
                && hasRightBoundary
                && !IsInsideProtectedLiteral(content, index))
            {
                return true;
            }
            start = index + 1;
        }
        return false;

    }

    private static bool IsIdentifierCharacter(char value)
        => char.IsLetterOrDigit(value)
           || value is '_' or '.' or '/' or '`' or '<' or '>' or '$';

    private static bool IsInsideProtectedLiteral(string content, int position)
    {
        var lineStart = content.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        var inDoubleQuotedString = false;
        var inSecurityString = false;
        var escaped = false;
        for (var index = lineStart; index < position; index++)
        {
            var value = content[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (value == '\\' && (inDoubleQuotedString || inSecurityString))
            {
                escaped = true;
                continue;
            }
            if (inDoubleQuotedString)
            {
                if (value == '"')
                {
                    inDoubleQuotedString = false;
                }
                continue;
            }
            if (inSecurityString)
            {
                if (value == '\'')
                {
                    inSecurityString = false;
                }
                continue;
            }
            if (value == '"')
            {
                inDoubleQuotedString = true;
            }
            else if (content.AsSpan(index).StartsWith(
                         "string('",
                         StringComparison.Ordinal))
            {
                inSecurityString = true;
                index += "string('".Length - 1;
            }
        }
        return inDoubleQuotedString || inSecurityString;
    }

    private static bool IsPrivateImplementationTypeName(string name)
        => name.Contains("<PrivateImplementationDetails>", StringComparison.Ordinal);

    private static bool IsHiddenHelperTypeName(string name)
        => name != "<Module>" && name.Contains('<');

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
        if (rawName.Contains('<')) return null;

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

    private sealed record StaticDataEntry(
        string DeclaringType,
        string FieldName,
        int Rva,
        string Storage,
        byte[] Bytes);

    private sealed record StaticDataHash(
        string DeclaringType,
        string FieldName,
        string Hash);

    private sealed record PrivateMemberContent(
        string Key,
        string DeclaringType,
        string MemberName,
        string Content);

    private sealed record HiddenHelperFingerprint(
        string SimpleName,
        string ReflectionName,
        string ContentHash);

    private sealed record HiddenHelperContent(
        string SimpleName,
        string ReflectionName,
        string RawContent,
        string ContentHash);

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
