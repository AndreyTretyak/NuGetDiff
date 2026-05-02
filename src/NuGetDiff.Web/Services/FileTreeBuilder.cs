using NuGetDiff.Core.Models;

namespace NuGetDiff.Web.Services;

public sealed record FileTreeNode(string Name, string FullPath, bool IsDirectory)
{
    public FileEntry? Entry { get; init; }
    public FileChange? Change { get; init; }
    public List<FileTreeNode> Children { get; } = new();
}

public static class FileTreeBuilder
{
    public static FileTreeNode Build(IEnumerable<FileEntry> files)
    {
        var root = new FileTreeNode(string.Empty, string.Empty, IsDirectory: true);
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            Insert(root, f.Path.Split('/'), 0, f, change: null);
        }
        Sort(root);
        return root;
    }

    public static FileTreeNode BuildFromChanges(IEnumerable<FileChange> changes)
    {
        var root = new FileTreeNode(string.Empty, string.Empty, IsDirectory: true);
        foreach (var c in changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            var entry = c.New ?? c.Old;
            if (entry is null) continue;
            Insert(root, c.Path.Split('/'), 0, entry, c);
        }
        Sort(root);
        return root;
    }

    private static void Insert(FileTreeNode parent, string[] parts, int index, FileEntry entry, FileChange? change)
    {
        if (index == parts.Length - 1)
        {
            parent.Children.Add(new FileTreeNode(parts[index], string.Join('/', parts), IsDirectory: false)
            {
                Entry = entry,
                Change = change,
            });
            return;
        }

        var name = parts[index];
        var fullPath = string.Join('/', parts, 0, index + 1);
        var dir = parent.Children.FirstOrDefault(c => c.IsDirectory && c.Name == name);
        if (dir is null)
        {
            dir = new FileTreeNode(name, fullPath, IsDirectory: true);
            parent.Children.Add(dir);
        }
        Insert(dir, parts, index + 1, entry, change);
    }

    private static void Sort(FileTreeNode node)
    {
        node.Children.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        foreach (var child in node.Children)
        {
            if (child.IsDirectory) Sort(child);
        }
    }
}
