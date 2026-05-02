using System;
using System.IO;

namespace NuGetDiff.Web.Services;

/// <summary>
/// Maps a file path / extension to a Prism.js language token used for syntax
/// highlighting. Returns <c>null</c> when no language is known so callers can
/// skip the highlighting markup entirely.
/// </summary>
public static class LanguageDetector
{
    public static string? FromPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" or ".csx" => "csharp",
            // Decompiled assemblies are surfaced as C#.
            ".dll" or ".exe" or ".winmd" => "csharp",
            ".json" => "json",
            ".xml" or ".nuspec" or ".csproj" or ".vbproj" or ".fsproj"
                or ".props" or ".targets" or ".config" or ".resx" or ".xaml"
                or ".html" or ".htm" or ".svg" => "markup",
            ".yml" or ".yaml" => "yaml",
            ".sh" or ".bash" => "bash",
            ".ps1" or ".psm1" or ".psd1" => "powershell",
            ".js" or ".cjs" or ".mjs" => "javascript",
            ".ts" or ".tsx" => "typescript",
            ".css" => "css",
            _ => null,
        };
    }

    /// <summary>
    /// The language to use when rendering a decompiled-assembly diff or view.
    /// Decompilation always produces C#.
    /// </summary>
    public const string AssemblyLanguage = "csharp";
}
