namespace NuGetDiff.Core.Models;

public sealed record DecompilationResult(
    string CSharp,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> TypeFullNames,
    string? AssemblyName,
    string? TargetFramework);

public sealed record DecompilationError(string Message, string? Detail = null);
