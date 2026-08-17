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

    private static bool HasNativeMethods(
        PEFile pe,
        System.Reflection.Metadata.TypeDefinitionHandle handle)
    {
        var pending = new Stack<System.Reflection.Metadata.TypeDefinitionHandle>();
        pending.Push(handle);
        while (pending.TryPop(out var current))
        {
            var definition = pe.Metadata.GetTypeDefinition(current);
            foreach (var methodHandle in definition.GetMethods())
            {
                var method = pe.Metadata.GetMethodDefinition(methodHandle);
                var implementation = method.ImplAttributes;
                if ((implementation & System.Reflection.MethodImplAttributes.Native) != 0
                    || (implementation & System.Reflection.MethodImplAttributes.Unmanaged) != 0)
                {
                    return true;
                }
            }
            foreach (var nested in definition.GetNestedTypes())
            {
                pending.Push(nested);
            }
        }
        return false;
    }

    private static bool HasDeclarativeSecurity(
        PEFile pe,
        System.Reflection.Metadata.TypeDefinitionHandle handle)
    {
        var pending = new Stack<System.Reflection.Metadata.TypeDefinitionHandle>();
        pending.Push(handle);
        while (pending.TryPop(out var current))
        {
            var definition = pe.Metadata.GetTypeDefinition(current);
            if (definition.GetDeclarativeSecurityAttributes().Count > 0
                || definition.GetMethods().Any(method =>
                    pe.Metadata.GetMethodDefinition(method)
                        .GetDeclarativeSecurityAttributes()
                        .Count > 0))
            {
                return true;
            }
            foreach (var nested in definition.GetNestedTypes())
            {
                pending.Push(nested);
            }
        }
        return false;
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

        var (pe, resolver, _) = loaded.Ok!;
        try
        {
            var handlesByName = pe.Metadata.GetTopLevelTypeDefinitions()
                .ToDictionary(
                    handle => handle.GetFullTypeName(pe.Metadata).ReflectionName,
                    StringComparer.Ordinal);
            var hiddenTypeNames = pe.Metadata.GetTopLevelTypeDefinitions()
                .Where(handle => IsHiddenHelperTypeName(
                    pe.Metadata.GetString(
                        pe.Metadata.GetTypeDefinition(handle).Name))
                    || pe.Metadata.GetString(
                        pe.Metadata.GetTypeDefinition(handle).Name) == "<Module>")
                .SelectMany(handle =>
                {
                    var definition = pe.Metadata.GetTypeDefinition(handle);
                    var simpleName = pe.Metadata.GetString(definition.Name);
                    var fullName = handle.GetFullTypeName(pe.Metadata).ReflectionName;
                    return new[]
                    {
                        simpleName,
                        fullName,
                        DisassemblerHelpers.Escape(simpleName),
                        DisassemblerHelpers.Escape(fullName),
                    };
                })
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var moduleHash = package.HashFile(path);
            var moduleHasExecutableMembers = pe.Metadata.GetTopLevelTypeDefinitions()
                .Where(handle =>
                    pe.Metadata.GetString(
                        pe.Metadata.GetTypeDefinition(handle).Name) == "<Module>")
                .Select(handle => pe.Metadata.GetTypeDefinition(handle))
                .Any(module => module.GetMethods().Any(method =>
                    pe.Metadata.GetMethodDefinition(method).RelativeVirtualAddress != 0));
            var assemblyHasSecurity = pe.Metadata.IsAssembly
                                      && pe.Metadata.GetAssemblyDefinition()
                                          .GetDeclarativeSecurityAttributes()
                                          .Count > 0;
            var results = new List<TypeFingerprint>(names.Length);
            for (var index = 0; index < names.Length; index++)
            {
                if (index > 0)
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
                    using var writer = new StringWriter();
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
                    var fingerprint = NormalizeFingerprint(writer.ToString());
                    fingerprint = AppendAssemblyReferenceIdentities(
                        fingerprint,
                        pe,
                        moduleHash);
                    if (RequiresModuleContext(
                            fingerprint,
                            hiddenTypeNames)
                        || HasDisassemblyDiagnostic(fingerprint)
                        || moduleHasExecutableMembers
                        || assemblyHasSecurity
                        || HasDeclarativeSecurity(pe, handle)
                        || HasNativeMethods(pe, handle))
                    {
                        fingerprint += "\n// Module context: " + moduleHash;
                    }

                    results.Add(new TypeFingerprint(
                        reflectionName,
                        fingerprint,
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

    private static (
        IReadOnlyList<StaticDataHash> Hashes,
        DecompilationError? Error) GetStaticDataHashes(
        PEFile pe,
        CancellationToken ct)
    {
        var entries = new List<StaticDataEntry>();
        DecompilationError? error = null;
        foreach (var handle in pe.Metadata.GetTopLevelTypeDefinitions())
        {
            var definition = pe.Metadata.GetTypeDefinition(handle);
            var declaringType = handle.GetFullTypeName(pe.Metadata).ReflectionName;

            foreach (var fieldHandle in definition.GetFields())
            {
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
        var hashes = new List<StaticDataHash>(entries.Count);
        foreach (var entry in entries)
        {
            var relationships = entries
                .Where(other => other != entry
                    && RangesOverlap(
                        entry.Rva,
                        entry.Bytes.Length,
                        other.Rva,
                        other.Bytes.Length))
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

    private static IReadOnlyDictionary<string, string> GetPrivateImplementationMemberHashes(
        PEFile pe,
        PackageAssemblyResolver resolver,
        IReadOnlyList<StaticDataHash> staticDataHashes,
        string moduleHash,
        CancellationToken ct)
    {
        var members = new List<PrivateMemberContent>();
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

        var byKey = members.ToDictionary(
            member => member.Key,
            member => member,
            StringComparer.Ordinal);
        var qualifiedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var closure = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(member.Key);
            while (pending.TryPop(out var current))
            {
                if (!closure.Add(current))
                {
                    continue;
                }
                foreach (var dependency in members.Where(dependency =>
                             dependency.Key != current
                             && ReferencesQualifiedMember(
                                 byKey[current].Content,
                                 dependency.DeclaringType,
                                 dependency.MemberName)))
                {
                    pending.Push(dependency.Key);
                }
            }
            var closureContent = string.Join(
                "\n",
                closure
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .Select(item => $"// Private member {item}\n{byKey[item].Content}"));
            qualifiedHashes[member.Key] = HashUtil.Sha256Hex(
                System.Text.Encoding.UTF8.GetBytes(closureContent));
        }
        return qualifiedHashes
            .GroupBy(
                pair => byKey[pair.Key].MemberName,
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

    private static IReadOnlyDictionary<string, string> GetHiddenHelperHashes(
        PEFile pe,
        PackageAssemblyResolver resolver,
        IReadOnlyList<StaticDataHash> staticDataHashes,
        IReadOnlyDictionary<string, string> privateImplementationMemberHashes,
        string moduleHash,
        CancellationToken ct)
    {
        var rawByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in pe.Metadata.GetTopLevelTypeDefinitions())
        {
            ct.ThrowIfCancellationRequested();
            var definition = pe.Metadata.GetTypeDefinition(handle);
            var name = pe.Metadata.GetString(definition.Name);
            if (!IsHiddenHelperTypeName(name)
                || IsPrivateImplementationTypeName(name))
            {
                continue;
            }

            using var writer = new StringWriter();
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
            rawByName[name] = NormalizeFingerprint(writer.ToString());
        }
        var baseContent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, rawContent) in rawByName)
        {
            var content = rawContent;
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
            foreach (var (memberName, hash) in privateImplementationMemberHashes
                         .Where(pair => ReferencesMember(content, pair.Key))
                         .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                content += $"\n// Private member {memberName}: {hash}";
            }
            baseContent[name] = content;
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in baseContent.Keys)
        {
            var closure = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(name);
            while (pending.TryPop(out var current))
            {
                if (!closure.Add(current))
                {
                    continue;
                }
                foreach (var dependency in baseContent.Keys.Where(dependency =>
                             dependency != current
                             && baseContent[current].Contains(
                                 dependency,
                                 StringComparison.Ordinal)))
                {
                    pending.Push(dependency);
                }
            }
            var closureContent = string.Join(
                "\n",
                closure
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .Select(item => $"// Hidden helper {item}\n{baseContent[item]}"));
            hashes[name] = HashUtil.Sha256Hex(
                System.Text.Encoding.UTF8.GetBytes(closureContent));
        }
        return hashes;
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

    private static bool HasDisassemblyDiagnostic(string fingerprint)
    {
        var allText = fingerprint.ToLowerInvariant();
        if (allText.Contains("<bad", StringComparison.Ordinal)
            || allText.Contains("<err:", StringComparison.Ordinal)
            || allText.Contains("wrong signature", StringComparison.Ordinal)
            || allText.Contains("bad signature", StringComparison.Ordinal)
            || allText.Contains("invalid local", StringComparison.Ordinal)
            || allText.Contains("invalid typecode", StringComparison.Ordinal)
            || allText.Contains("invalid method", StringComparison.Ordinal)
            || allText.Contains("could not decode", StringComparison.Ordinal)
            || allText.Contains("not enough space", StringComparison.Ordinal)
            || allText.Contains("out of bounds", StringComparison.Ordinal)
            || allText.Contains("unexpected end", StringComparison.Ordinal)
            || allText.Contains("bad image", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var line in fingerprint.Split('\n'))
        {
            for (var index = 0; index <= line.Length - 14; index++)
            {
                if (!line.AsSpan(index, 3).SequenceEqual("/* ")
                    || !line.AsSpan(index + 11, 3).SequenceEqual(" */"))
                {
                    continue;
                }
                var token = line.AsSpan(index + 3, 8);
                if (token.ToString().All(Uri.IsHexDigit))
                {
                    return true;
                }
            }

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("//", StringComparison.Ordinal)
                && !trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("/* ", StringComparison.Ordinal)
                && trimmed.EndsWith(" */", StringComparison.Ordinal))
            {
                var token = trimmed.AsSpan(3, trimmed.Length - 6);
                if (token.Length == 8
                    && token.ToString().All(Uri.IsHexDigit))
                {
                    return true;
                }
            }

            var diagnostic = trimmed.ToLowerInvariant();
            if (diagnostic.Contains("invalid", StringComparison.Ordinal)
                || diagnostic.Contains("could not", StringComparison.Ordinal)
                || diagnostic.Contains("not enough", StringComparison.Ordinal)
                || diagnostic.Contains("out of bounds", StringComparison.Ordinal)
                || diagnostic.Contains("unexpected", StringComparison.Ordinal)
                || diagnostic.Contains("bad ", StringComparison.Ordinal)
                || diagnostic.Contains("<bad", StringComparison.Ordinal)
                || diagnostic.Contains("<err:", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ReferencesMember(string content, string memberName)
        => content.Contains($"::{memberName}", StringComparison.Ordinal)
           || content.Contains($"::'{memberName}'", StringComparison.Ordinal);

    private static bool ReferencesQualifiedMember(
        string content,
        string declaringType,
        string memberName)
        => ReferencesMember(content, memberName)
           && content.Contains(declaringType, StringComparison.Ordinal);

    private static bool ReferencesStaticData(
        string content,
        StaticDataHash staticData)
        => ReferencesQualifiedMember(
            content,
            staticData.DeclaringType,
            staticData.FieldName);

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
