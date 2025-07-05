using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using NuGetDiff.Client.Models;
using System.Text;

namespace NuGetDiff.Client.Services;

public class DiffService
{
    private readonly ISideBySideDiffBuilder _diffBuilder;

    public DiffService()
    {
        _diffBuilder = new SideBySideDiffBuilder(new Differ());
    }

    public DiffResult ComparePackages(PackageInfo oldPackage, PackageInfo newPackage)
    {
        var result = new DiffResult
        {
            OldPackage = oldPackage,
            NewPackage = newPackage
        };

        // Get all unique file paths from both packages
        var allFiles = oldPackage.Files.Keys.Union(newPackage.Files.Keys)
            .Union(oldPackage.DecompiledFiles.Keys)
            .Union(newPackage.DecompiledFiles.Keys)
            .Distinct()
            .OrderBy(f => f);

        foreach (var filePath in allFiles)
        {
            var fileDiff = CompareFile(filePath, oldPackage, newPackage);
            result.FileDiffs.Add(fileDiff);
        }

        return result;
    }

    private FileDiff CompareFile(string filePath, PackageInfo oldPackage, PackageInfo newPackage)
    {
        var fileDiff = new FileDiff
        {
            FileName = filePath
        };

        // Check if file exists in decompiled files first, then in regular files
        var oldContent = GetFileContent(filePath, oldPackage);
        var newContent = GetFileContent(filePath, newPackage);

        if (oldContent == null && newContent == null)
        {
            fileDiff.Type = DiffType.Unchanged;
            return fileDiff;
        }
        
        if (oldContent == null)
        {
            fileDiff.Type = DiffType.Added;
            fileDiff.NewContent = newContent;
        }
        else if (newContent == null)
        {
            fileDiff.Type = DiffType.Removed;
            fileDiff.OldContent = oldContent;
        }
        else
        {
            fileDiff.OldContent = oldContent;
            fileDiff.NewContent = newContent;
            
            if (oldContent == newContent)
            {
                fileDiff.Type = DiffType.Unchanged;
            }
            else
            {
                fileDiff.Type = DiffType.Modified;
                fileDiff.Lines = GetDiffLines(oldContent, newContent);
            }
        }

        return fileDiff;
    }

    private string? GetFileContent(string filePath, PackageInfo package)
    {
        // Check decompiled files first
        if (package.DecompiledFiles.TryGetValue(filePath, out var decompiledContent))
        {
            return decompiledContent;
        }

        // Then check regular files
        if (package.Files.TryGetValue(filePath, out var fileBytes))
        {
            // Try to read as text
            try
            {
                return Encoding.UTF8.GetString(fileBytes);
            }
            catch
            {
                // If not text, return a placeholder
                return $"[Binary file - {fileBytes.Length} bytes]";
            }
        }

        return null;
    }

    private List<DiffLine> GetDiffLines(string oldContent, string newContent)
    {
        var diffResult = _diffBuilder.BuildDiffModel(oldContent, newContent);
        var lines = new List<DiffLine>();

        // Process old file lines
        if (diffResult.OldText?.Lines != null)
        {
            foreach (var line in diffResult.OldText.Lines)
            {
                if (line.Type == ChangeType.Unchanged || line.Type == ChangeType.Deleted)
                {
                    lines.Add(new DiffLine
                    {
                        OldLineNumber = line.Position,
                        Text = line.Text,
                        Type = line.Type == ChangeType.Deleted ? DiffLineType.Deleted : DiffLineType.Unchanged
                    });
                }
            }
        }

        // Process new file lines
        if (diffResult.NewText?.Lines != null)
        {
            foreach (var line in diffResult.NewText.Lines)
            {
                if (line.Type == ChangeType.Inserted)
                {
                    lines.Add(new DiffLine
                    {
                        NewLineNumber = line.Position,
                        Text = line.Text,
                        Type = DiffLineType.Inserted
                    });
                }
                else if (line.Type == ChangeType.Unchanged)
                {
                    // Update the unchanged lines with new line numbers
                    var unchangedLine = lines.FirstOrDefault(l => 
                        l.Type == DiffLineType.Unchanged && 
                        l.OldLineNumber == line.Position);
                    
                    if (unchangedLine != null)
                    {
                        unchangedLine.NewLineNumber = line.Position;
                    }
                }
            }
        }

        return lines.OrderBy(l => l.OldLineNumber ?? int.MaxValue)
                    .ThenBy(l => l.NewLineNumber ?? int.MaxValue)
                    .ToList();
    }
}
