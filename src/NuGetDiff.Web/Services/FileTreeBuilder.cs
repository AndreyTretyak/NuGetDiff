using NuGetDiff.Core.Models;

namespace NuGetDiff.Web.Services;

public sealed record FileTreeNode(string Name, string FullPath, bool IsDirectory)
{
    public FileEntry? Entry { get; init; }
    public FileChange? Change { get; init; }

    /// <summary>
    /// When set, this node represents a single type inside the assembly at
    /// <see cref="FullPath"/>. The value is ILSpy's reflection-name form
    /// (e.g. <c>Newtonsoft.Json.JsonConvert</c> or
    /// <c>Newtonsoft.Json.JsonConverter`1</c>) and round-trips through
    /// the <c>?type=</c> query parameter on the file pages.
    /// </summary>
    public string? TypeFullName { get; init; }

    public List<FileTreeNode> Children { get; } = new();

    public AssemblyTypeLoadState TypeLoadState { get; set; }

    public string? TypeLoadError { get; set; }

    public bool IsExpanded { get; set; }

    public bool IsAssembly => !IsDirectory && Entry?.Kind == FileKind.Assembly;

    /// <summary>
    /// True for an assembly leaf that has been expanded with namespace
    /// folders + type leaves underneath. The renderer treats this as a
    /// hybrid: it renders an expandable container and the assembly itself
    /// is no longer a clickable file (the user clicks individual types).
    /// </summary>
    public bool IsAssemblyContainer
        => IsAssembly;

    /// <summary>
    /// True when the node should render as expandable (real folder, or an
    /// assembly with type children).
    /// </summary>
    public bool IsExpandable => IsDirectory || IsAssemblyContainer;
}

public enum AssemblyTypeLoadState
{
    NotLoaded,
    Loading,
    Analyzing,
    Loaded,
    Failed,
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

    public static void PopulateAssemblyTypes(
        FileTreeNode assemblyNode,
        IReadOnlyList<TypeSummary> types)
    {
        EnsureAssembly(assemblyNode);
        assemblyNode.Children.Clear();
        foreach (var type in types)
        {
            InsertTypeNode(assemblyNode, type, change: null);
        }
        Sort(assemblyNode);
        assemblyNode.TypeLoadError = null;
        assemblyNode.TypeLoadState = AssemblyTypeLoadState.Loaded;
    }

    public static void PopulateAssemblyTypeChanges(
        FileTreeNode assemblyNode,
        IReadOnlyList<TypeChange> typeChanges,
        bool hideUnchanged)
    {
        EnsureAssembly(assemblyNode);
        if (assemblyNode.Change is null)
        {
            throw new ArgumentException(
                "An assembly change is required to populate comparison types.",
                nameof(assemblyNode));
        }

        assemblyNode.Children.Clear();
        foreach (var typeChange in typeChanges)
        {
            if (hideUnchanged && typeChange.Kind == FileChangeKind.Unchanged)
            {
                continue;
            }

            var change = new FileChange(
                typeChange.Kind,
                assemblyNode.Change.Old,
                assemblyNode.Change.New);
            InsertTypeNode(assemblyNode, typeChange.Type, change);
        }
        Sort(assemblyNode);
        assemblyNode.TypeLoadError = null;
        assemblyNode.TypeLoadState = AssemblyTypeLoadState.Loaded;
    }

    public static FileTreeNode? FindFileNode(FileTreeNode root, string path)
    {
        if (!root.IsDirectory
            && root.TypeFullName is null
            && string.Equals(root.FullPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var match = FindFileNode(child, path);
            if (match is not null)
            {
                return match;
            }
        }
        return null;
    }

    private static FileTreeNode? Insert(FileTreeNode parent, string[] parts, int index, FileEntry entry, FileChange? change)
    {
        if (index == parts.Length - 1)
        {
            var leaf = new FileTreeNode(parts[index], string.Join('/', parts), IsDirectory: false)
            {
                Entry = entry,
                Change = change,
            };
            parent.Children.Add(leaf);
            return leaf;
        }

        var name = parts[index];
        var fullPath = string.Join('/', parts, 0, index + 1);
        var dir = parent.Children.FirstOrDefault(c => c.IsDirectory && c.Name == name && c.TypeFullName is null);
        if (dir is null)
        {
            dir = new FileTreeNode(name, fullPath, IsDirectory: true);
            parent.Children.Add(dir);
        }
        return Insert(dir, parts, index + 1, entry, change);
    }

    private static void InsertTypeNode(FileTreeNode assemblyNode, TypeSummary type, FileChange? change)
    {
        // Group the type under nested namespace folders, e.g.
        // Newtonsoft.Json.Serialization.JsonContractResolver →
        //   <assembly>/Newtonsoft/Json/Serialization/JsonContractResolver
        var parent = assemblyNode;
        if (!string.IsNullOrEmpty(type.Namespace))
        {
            foreach (var segment in type.Namespace.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = parent.Children.FirstOrDefault(c =>
                    c.IsDirectory && c.TypeFullName is null && c.Name == segment && c.Entry is null);
                if (dir is null)
                {
                    dir = new FileTreeNode(segment, parent.FullPath + "/" + segment, IsDirectory: true);
                    parent.Children.Add(dir);
                }
                parent = dir;
            }
        }

        parent.Children.Add(new FileTreeNode(type.DisplayName, assemblyNode.FullPath, IsDirectory: false)
        {
            TypeFullName = type.ReflectionName,
            Change = change,
        });
    }

    private static void EnsureAssembly(FileTreeNode node)
    {
        if (!node.IsAssembly)
        {
            throw new ArgumentException("The node is not a managed assembly.", nameof(node));
        }
    }

    private static void Sort(FileTreeNode node)
    {
        node.Children.Sort((a, b) =>
        {
            // Folders before files; within files, type-only entries last so the
            // assembly's own children sit underneath in a predictable order.
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        foreach (var child in node.Children)
        {
            if (child.IsDirectory || child.IsAssemblyContainer) Sort(child);
        }
    }
}
