using NuGetDiff.Core.Diffing;
using NuGetDiff.Core.Models;
using NuGetDiff.Core.Packages;
using Xunit;

namespace NuGetDiff.Core.Tests;

public class DifferTests
{
    private static readonly byte[] DummyAssembly = new byte[] { 0x4d, 0x5a, 0x90, 0x00, 0xAA };

    private static PackageReader MakeReader(params (string Path, byte[] Bytes)[] files)
    {
        var bytes = TestPackageBuilder.Build(
            "Pkg",
            "1.0.0",
            files.Select(f => new TestPackageBuilder.FileSpec(f.Path, f.Bytes)));
        return new PackageReader(new MemoryStream(bytes));
    }

    [Fact]
    public void Identical_packages_produce_only_unchanged_entries()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("same");
        using var oldR = MakeReader(("readme.md", content), ("LICENSE", content));
        using var newR = MakeReader(("readme.md", content), ("LICENSE", content));

        var diff = new Differ().DiffTree(oldR, newR);
        Assert.All(diff.Changes, c => Assert.Equal(FileChangeKind.Unchanged, c.Kind));
        // 2 user files + the .nuspec
        Assert.Equal(3, diff.Changes.Count);
    }

    [Fact]
    public void Detects_added_removed_and_modified()
    {
        using var oldR = MakeReader(
            ("removed.txt", System.Text.Encoding.UTF8.GetBytes("gone")),
            ("changed.txt", System.Text.Encoding.UTF8.GetBytes("v1")));
        using var newR = MakeReader(
            ("added.txt", System.Text.Encoding.UTF8.GetBytes("new")),
            ("changed.txt", System.Text.Encoding.UTF8.GetBytes("v2")));

        var diff = new Differ().DiffTree(oldR, newR);
        Assert.Contains(diff.Changes, c => c.Kind == FileChangeKind.Added && c.Path == "added.txt");
        Assert.Contains(diff.Changes, c => c.Kind == FileChangeKind.Removed && c.Path == "removed.txt");
        Assert.Contains(diff.Changes, c => c.Kind == FileChangeKind.Modified && c.Path == "changed.txt");
    }

    [Fact]
    public void Detects_renames_via_content_hash_match()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("identical-payload");
        using var oldR = MakeReader(("old/path.txt", bytes));
        using var newR = MakeReader(("new/path.txt", bytes));

        var diff = new Differ().DiffTree(oldR, newR);
        var rename = Assert.Single(diff.Changes, c => c.Kind == FileChangeKind.Renamed);
        Assert.Equal("old/path.txt", rename.Old!.Path);
        Assert.Equal("new/path.txt", rename.New!.Path);
        Assert.DoesNotContain(diff.Changes, c => c.Kind == FileChangeKind.Added);
        Assert.DoesNotContain(diff.Changes, c => c.Kind == FileChangeKind.Removed);
    }

    [Fact]
    public void Text_file_diff_reports_inserted_lines()
    {
        using var oldR = MakeReader(("notes.txt", System.Text.Encoding.UTF8.GetBytes("line1\nline2\n")));
        using var newR = MakeReader(("notes.txt", System.Text.Encoding.UTF8.GetBytes("line1\nline2\nline3\n")));

        var diff = new Differ().DiffFile(oldR, newR, "notes.txt");
        Assert.Equal(FileDiffKind.Text, diff.Kind);
        Assert.NotNull(diff.Text);
        Assert.Contains(diff.Text!.New, l => l.Kind == DiffLineKind.Inserted && l.Text.Contains("line3"));
    }

    [Fact]
    public void Binary_diff_reports_only_size_and_hash()
    {
        using var oldR = MakeReader(("data.bin", new byte[] { 1, 2, 3 }));
        using var newR = MakeReader(("data.bin", new byte[] { 1, 2, 3, 4 }));

        var diff = new Differ().DiffFile(oldR, newR, "data.bin");
        Assert.Equal(FileDiffKind.Binary, diff.Kind);
        Assert.Null(diff.Text);
        Assert.NotEqual(diff.OldHash, diff.NewHash);
        Assert.Equal(3, diff.OldLength);
        Assert.Equal(4, diff.NewLength);
    }

    [Fact]
    public void Native_dll_under_runtimes_native_is_classified_binary_not_assembly()
    {
        using var oldR = MakeReader(("runtimes/win-x64/native/native.dll", new byte[] { 0x4d, 0x5a, 0x00 }));
        using var newR = MakeReader(("runtimes/win-x64/native/native.dll", new byte[] { 0x4d, 0x5a, 0x01 }));

        var diff = new Differ().DiffFile(oldR, newR, "runtimes/win-x64/native/native.dll");
        Assert.Equal(FileDiffKind.Binary, diff.Kind);
    }
}
