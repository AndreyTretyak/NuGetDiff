namespace NuGetDiff.Core.Models;

public enum FileChangeKind
{
    Unchanged,
    Added,
    Removed,
    Modified,
    Renamed,
}

public sealed record FileChange(FileChangeKind Kind, FileEntry? Old, FileEntry? New)
{
    public string Path => New?.Path ?? Old?.Path ?? string.Empty;
}

public sealed record TreeDiff(IReadOnlyList<FileChange> Changes);

public enum DiffLineKind
{
    Equal,
    Inserted,
    Deleted,
    Modified,
    Imaginary,
}

public sealed record DiffLine(int? OldNumber, int? NewNumber, DiffLineKind Kind, string Text);

public sealed record SideBySideDiff(IReadOnlyList<DiffLine> Old, IReadOnlyList<DiffLine> New);

public enum FileDiffKind
{
    Text,
    Assembly,
    Binary,
    Unsupported,
}

public sealed record FileDiff(
    FileDiffKind Kind,
    SideBySideDiff? Text,
    string? OldHash,
    string? NewHash,
    long? OldLength,
    long? NewLength,
    string? Message);
