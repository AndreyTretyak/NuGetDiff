namespace NuGetDiff.Core.Models;

public sealed record DecompilationResult(
    string CSharp,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> TypeFullNames,
    string? AssemblyName,
    string? TargetFramework);

public sealed record DecompilationError(string Message, string? Detail = null);

/// <summary>
/// Lightweight description of a top-level type in an assembly, used for
/// building the navigation tree without paying the cost of decompiling.
/// <see cref="ReflectionName"/> is the canonical key (round-trips through
/// ILSpy's <c>FullTypeName</c>); <see cref="Namespace"/> drives folder
/// grouping; <see cref="DisplayName"/> is human-friendly (generic arity
/// is rendered as <c>Foo&lt;,&gt;</c>).
/// </summary>
public sealed record TypeSummary(
    string ReflectionName,
    string Namespace,
    string DisplayName);

