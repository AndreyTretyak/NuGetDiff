using System.Reflection.PortableExecutable;
using NuGetDiff.Core.Models;

namespace NuGetDiff.Core.Diffing;

public static class FileClassifier
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".xml", ".cs", ".vb", ".fs", ".fsi", ".fsx",
        ".html", ".htm", ".css", ".js", ".ts", ".jsx", ".tsx", ".props", ".targets", ".config",
        ".nuspec", ".yml", ".yaml", ".sh", ".ps1", ".cmd", ".bat", ".cake", ".cshtml", ".razor",
        ".csproj", ".vbproj", ".fsproj", ".pubxml", ".editorconfig", ".gitignore", ".gitattributes",
        ".sln", ".slnx", ".dproj", ".props", ".rsp",
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown",
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".winmd",
    };

    public static FileKind ClassifyByPath(string path)
    {
        var ext = System.IO.Path.GetExtension(path);

        if (MarkdownExtensions.Contains(ext))
        {
            return FileKind.Markdown;
        }

        if (TextExtensions.Contains(ext))
        {
            return FileKind.Text;
        }

        if (ExecutableExtensions.Contains(ext))
        {
            // Heuristic: anything under runtimes/<rid>/native/ is a native binary.
            // The decompiler must not be invoked on these. The PE check below is the
            // authoritative gate; this is just an early fast path so we do not even
            // attempt to sniff binaries that we know cannot be managed.
            if (path.Contains("/native/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("\\native\\", StringComparison.OrdinalIgnoreCase))
            {
                return FileKind.Binary;
            }

            return FileKind.Assembly;
        }

        return FileKind.Binary;
    }

    /// <summary>
    /// Returns true only if the stream contains a PE file with a CLR metadata directory
    /// (i.e. a managed assembly). Native PEs return false. The stream is read at position 0
    /// and is left disposed if <paramref name="leaveOpen"/> is false.
    /// </summary>
    public static bool IsManagedAssembly(Stream stream, bool leaveOpen = false)
    {
        try
        {
            var options = leaveOpen ? PEStreamOptions.LeaveOpen : PEStreamOptions.Default;
            using var pe = new PEReader(stream, options);
            return pe.HasMetadata;
        }
        catch
        {
            return false;
        }
    }
}
