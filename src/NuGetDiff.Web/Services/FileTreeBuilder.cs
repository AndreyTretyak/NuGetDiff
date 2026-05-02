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

    /// <summary>
    /// True for an assembly leaf that has been expanded with namespace
    /// folders + type leaves underneath. The renderer treats this as a
    /// hybrid: it renders an expandable container and the assembly itself
    /// is no longer a clickable file (the user clicks individual types).
    /// </summary>
    public bool IsAssemblyContainer
        => !IsDirectory && Entry?.Kind == FileKind.Assembly && Children.Count > 0;

    /// <summary>
    /// True when the node should render as expandable (real folder, or an
    /// assembly with type children).
    /// </summary>
    public bool IsExpandable => IsDirectory || IsAssemblyContainer;
}

public static class FileTreeBuilder
{
    public static FileTreeNode Build(IEnumerable<FileEntry> files)
        => Build(files, typesFor: null);

    /// <summary>
    /// Builds a navigation tree from a flat list of package files. When
    /// <paramref name="typesFor"/> is supplied, every assembly file is
    /// expanded into namespace folders + type leaves so users can navigate
    /// directly to a single class instead of seeing the whole module.
    /// </summary>
    public static FileTreeNode Build(
        IEnumerable<FileEntry> files,
        Func<FileEntry, IReadOnlyList<TypeSummary>>? typesFor)
    {
        var root = new FileTreeNode(string.Empty, string.Empty, IsDirectory: true);
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var leaf = Insert(root, f.Path.Split('/'), 0, f, change: null);
            if (typesFor is not null && f.Kind == FileKind.Assembly && leaf is not null)
            {
                IReadOnlyList<TypeSummary>? types = null;
                try { types = typesFor(f); }
                catch { /* caller decided this assembly is opaque — leave as a regular file. */ }
                if (types is { Count: > 0 })
                {
                    foreach (var t in types)
                    {
                        InsertTypeNode(leaf, t, change: null);
                    }
                }
            }
        }
        Sort(root);
        return root;
    }

    public static FileTreeNode BuildFromChanges(IEnumerable<FileChange> changes)
        => BuildFromChanges(changes, typesForOld: null, typesForNew: null);

    /// <summary>
    /// Builds a tree of changed files, optionally expanding modified / added /
    /// removed assemblies into per-type changes underneath.
    /// </summary>
    public static FileTreeNode BuildFromChanges(
        IEnumerable<FileChange> changes,
        Func<FileEntry, IReadOnlyList<TypeSummary>>? typesForOld,
        Func<FileEntry, IReadOnlyList<TypeSummary>>? typesForNew)
    {
        var root = new FileTreeNode(string.Empty, string.Empty, IsDirectory: true);
        foreach (var c in changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            var entry = c.New ?? c.Old;
            if (entry is null) continue;
            var leaf = Insert(root, c.Path.Split('/'), 0, entry, c);

            if (leaf is null || entry.Kind != FileKind.Assembly) continue;
            if (typesForOld is null && typesForNew is null) continue;

            var typeChildren = BuildAssemblyTypeChanges(c, typesForOld, typesForNew);
            foreach (var (summary, change) in typeChildren)
            {
                InsertTypeNode(leaf, summary, change);
            }
        }
        Sort(root);
        return root;
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

    private static IList<(TypeSummary Summary, FileChange? Change)> BuildAssemblyTypeChanges(
        FileChange assemblyChange,
        Func<FileEntry, IReadOnlyList<TypeSummary>>? typesForOld,
        Func<FileEntry, IReadOnlyList<TypeSummary>>? typesForNew)
    {
        var oldTypes = (typesForOld is not null && assemblyChange.Old is not null)
            ? Safe(() => typesForOld(assemblyChange.Old))
            : Array.Empty<TypeSummary>();
        var newTypes = (typesForNew is not null && assemblyChange.New is not null)
            ? Safe(() => typesForNew(assemblyChange.New))
            : Array.Empty<TypeSummary>();

        var oldByName = oldTypes.ToDictionary(t => t.ReflectionName, StringComparer.Ordinal);
        var newByName = newTypes.ToDictionary(t => t.ReflectionName, StringComparer.Ordinal);
        var allNames = new SortedSet<string>(oldByName.Keys.Concat(newByName.Keys), StringComparer.Ordinal);

        var result = new List<(TypeSummary, FileChange?)>(allNames.Count);
        foreach (var name in allNames)
        {
            var hasOld = oldByName.TryGetValue(name, out var ot);
            var hasNew = newByName.TryGetValue(name, out var nt);
            var summary = nt ?? ot!;

            FileChangeKind kind;
            if (hasOld && hasNew)
            {
                kind = assemblyChange.Kind switch
                {
                    FileChangeKind.Added => FileChangeKind.Added,
                    FileChangeKind.Removed => FileChangeKind.Removed,
                    FileChangeKind.Renamed => FileChangeKind.Modified,
                    _ => FileChangeKind.Modified,
                };
            }
            else if (hasNew)
            {
                kind = FileChangeKind.Added;
            }
            else
            {
                kind = FileChangeKind.Removed;
            }

            // Synthesise a FileChange so the existing badge / colour rendering
            // still works for type leaves. Old/New entries are the assembly
            // entries — only Kind is used by the renderer.
            var change = new FileChange(kind, assemblyChange.Old, assemblyChange.New);
            result.Add((summary, change));
        }
        return result;

        static IReadOnlyList<TypeSummary> Safe(Func<IReadOnlyList<TypeSummary>> f)
        {
            try { return f() ?? Array.Empty<TypeSummary>(); }
            catch { return Array.Empty<TypeSummary>(); }
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
