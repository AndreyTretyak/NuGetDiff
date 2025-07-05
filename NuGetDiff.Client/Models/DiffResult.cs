namespace NuGetDiff.Client.Models;

public class DiffResult
{
    public List<FileDiff> FileDiffs { get; set; } = new();
    public PackageInfo OldPackage { get; set; } = new();
    public PackageInfo NewPackage { get; set; } = new();
}

public class FileDiff
{
    public string FileName { get; set; } = string.Empty;
    public DiffType Type { get; set; }
    public string? OldContent { get; set; }
    public string? NewContent { get; set; }
    public List<DiffLine> Lines { get; set; } = new();
}

public enum DiffType
{
    Added,
    Removed,
    Modified,
    Unchanged
}

public class DiffLine
{
    public int? OldLineNumber { get; set; }
    public int? NewLineNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    public DiffLineType Type { get; set; }
}

public enum DiffLineType
{
    Unchanged,
    Inserted,
    Deleted
}
