namespace NuGetDiff.Core.Models;

public enum FileKind
{
    Text,
    Markdown,
    Assembly,
    Binary,
    Unknown,
}

public sealed record FileEntry(string Path, long Length, FileKind Kind);
