using NuGetDiff.Core.Models;
using NuGetDiff.Web.Services;

namespace NuGetDiff.Web.Tests;

public sealed class FileTreeBuilderTests
{
    [Fact]
    public void Type_filter_hides_only_classified_unchanged_types()
    {
        var oldEntry = new FileEntry("lib/net8.0/Pkg.dll", 100, FileKind.Assembly);
        var newEntry = new FileEntry("lib/net8.0/Pkg.dll", 120, FileKind.Assembly);
        var root = FileTreeBuilder.BuildFromChanges(new[]
        {
            new FileChange(FileChangeKind.Modified, oldEntry, newEntry),
        });
        var assembly = Assert.IsType<FileTreeNode>(
            FileTreeBuilder.FindFileNode(root, oldEntry.Path));

        FileTreeBuilder.PopulateAssemblyTypeChanges(
            assembly,
            new[]
            {
                new TypeChange(Type("Example.Unchanged"), FileChangeKind.Unchanged),
                new TypeChange(Type("Example.Modified"), FileChangeKind.Modified),
                new TypeChange(Type("Example.Removed"), FileChangeKind.Removed),
                new TypeChange(Type("Example.Added"), FileChangeKind.Added),
            },
            hideUnchanged: true);

        var types = Descendants(assembly)
            .Where(node => node.TypeFullName is not null)
            .ToDictionary(node => node.TypeFullName!, StringComparer.Ordinal);
        Assert.DoesNotContain("Example.Unchanged", types.Keys);
        Assert.Equal(FileChangeKind.Modified, types["Example.Modified"].Change!.Kind);
        Assert.Equal(FileChangeKind.Added, types["Example.Added"].Change!.Kind);
        Assert.Equal(FileChangeKind.Removed, types["Example.Removed"].Change!.Kind);

        FileTreeBuilder.PopulateAssemblyTypeChanges(
            assembly,
            new[]
            {
                new TypeChange(Type("Example.Unchanged"), FileChangeKind.Unchanged),
            },
            hideUnchanged: false);

        var unchanged = Assert.Single(
            Descendants(assembly),
            node => node.TypeFullName == "Example.Unchanged");
        Assert.Equal(FileChangeKind.Unchanged, unchanged.Change!.Kind);
    }

    private static TypeSummary Type(string reflectionName)
        => new(
            reflectionName,
            reflectionName[..reflectionName.LastIndexOf('.')],
            reflectionName[(reflectionName.LastIndexOf('.') + 1)..]);

    private static IEnumerable<FileTreeNode> Descendants(FileTreeNode node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
