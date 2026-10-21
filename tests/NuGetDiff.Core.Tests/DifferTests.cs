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
    public void Rename_detection_matches_each_removed_file_at_most_once()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("same");
        using var oldR = MakeReader(("old.txt", bytes));
        using var newR = MakeReader(("new-a.txt", bytes), ("new-b.txt", bytes));

        var diff = new Differ().DiffTree(oldR, newR);

        Assert.Single(diff.Changes, change => change.Kind == FileChangeKind.Renamed);
        Assert.Single(diff.Changes, change => change.Kind == FileChangeKind.Added);
        Assert.DoesNotContain(diff.Changes, change => change.Kind == FileChangeKind.Removed);
    }

    [Fact]
    public void Tree_diff_honors_a_pre_canceled_token()
    {
        using var oldR = MakeReader(("old.txt", System.Text.Encoding.UTF8.GetBytes("old")));
        using var newR = MakeReader(("new.txt", System.Text.Encoding.UTF8.GetBytes("new")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new Differ().DiffTree(oldR, newR, cts.Token));
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
    public void Modified_text_lines_preserve_word_level_changes()
    {
        using var oldR = MakeReader((
            "notes.txt",
            System.Text.Encoding.UTF8.GetBytes("The quick brown fox\n")));
        using var newR = MakeReader((
            "notes.txt",
            System.Text.Encoding.UTF8.GetBytes("The quick red fox\n")));

        var diff = new Differ().DiffFile(oldR, newR, "notes.txt");
        var oldLine = Assert.Single(diff.Text!.Old, line => line.Kind == DiffLineKind.Modified);
        var newLine = Assert.Single(diff.Text.New, line => line.Kind == DiffLineKind.Modified);

        Assert.Contains(oldLine.Segments!, segment =>
            segment.Kind == DiffSegmentKind.Deleted && segment.Text.Contains("brown"));
        Assert.Contains(newLine.Segments!, segment =>
            segment.Kind == DiffSegmentKind.Inserted && segment.Text.Contains("red"));
        Assert.Equal(oldLine.Text, string.Concat(oldLine.Segments!.Select(segment => segment.Text)));
        Assert.Equal(newLine.Text, string.Concat(newLine.Segments!.Select(segment => segment.Text)));
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

    [Fact]
    public void DiffType_diffs_a_single_named_type_across_versions()
    {
        // Same managed assembly on both sides — the per-type diff should still
        // succeed and produce identical text (no inserted/deleted lines).
        var asm = TestPackageBuilder.LoadCoreAssemblyBytes();
        var oldBytes = TestPackageBuilder.Build(
            "Real", "1.0.0",
            new[] { new TestPackageBuilder.FileSpec("lib/net8.0/NuGetDiff.Core.dll", asm) });
        var newBytes = TestPackageBuilder.Build(
            "Real", "1.0.1",
            new[] { new TestPackageBuilder.FileSpec("lib/net8.0/NuGetDiff.Core.dll", asm) });

        using var oldR = new PackageReader(new MemoryStream(oldBytes));
        using var newR = new PackageReader(new MemoryStream(newBytes));

        var diff = new Differ().DiffType(
            oldR, newR,
            "lib/net8.0/NuGetDiff.Core.dll",
            "NuGetDiff.Core.Models.PackageDescriptor");

        Assert.Equal(FileDiffKind.Assembly, diff.Kind);
        Assert.NotNull(diff.Text);
        // Identical content on both sides → no inserted/deleted lines.
        Assert.DoesNotContain(diff.Text!.Old, l => l.Kind == DiffLineKind.Deleted);
        Assert.DoesNotContain(diff.Text!.New, l => l.Kind == DiffLineKind.Inserted);
        Assert.Contains("No C# source differences", diff.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiffType_reports_message_when_type_missing_on_one_side()
    {
        var asm = TestPackageBuilder.LoadCoreAssemblyBytes();
        var oldBytes = TestPackageBuilder.Build(
            "Real", "1.0.0",
            new[] { new TestPackageBuilder.FileSpec("lib/net8.0/NuGetDiff.Core.dll", asm) });
        var newBytes = TestPackageBuilder.Build(
            "Real", "1.0.1",
            new[] { new TestPackageBuilder.FileSpec("lib/net8.0/NuGetDiff.Core.dll", asm) });

        using var oldR = new PackageReader(new MemoryStream(oldBytes));
        using var newR = new PackageReader(new MemoryStream(newBytes));

        var diff = new Differ().DiffType(
            oldR, newR,
            "lib/net8.0/NuGetDiff.Core.dll",
            "Does.Not.Exist.Type");

        Assert.Equal(FileDiffKind.Assembly, diff.Kind);
        // Both sides fail to find the type → text is null and message explains.
        Assert.Null(diff.Text);
        Assert.False(string.IsNullOrEmpty(diff.Message));
    }

    [Fact]
    public void DiffType_uses_both_paths_for_a_renamed_assembly()
    {
        var assembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        using var oldReader = MakeReader(("lib/net8.0/Old.dll", assembly));
        using var newReader = MakeReader(("lib/net8.0/New.dll", assembly));

        var diff = new Differ().DiffType(
            oldReader,
            newReader,
            "lib/net8.0/Old.dll",
            "lib/net8.0/New.dll",
            "NuGetDiff.Core.Models.PackageDescriptor");

        Assert.NotNull(diff.Text);
        Assert.DoesNotContain(diff.Text!.Old, line => line.Kind != DiffLineKind.Equal);
        Assert.DoesNotContain(diff.Text.New, line => line.Kind != DiffLineKind.Equal);
        Assert.Contains("No C# source differences", diff.Message, StringComparison.Ordinal);
    }
}
