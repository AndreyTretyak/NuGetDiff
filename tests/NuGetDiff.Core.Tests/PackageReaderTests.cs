using NuGetDiff.Core.Models;
using NuGetDiff.Core.Packages;
using Xunit;

namespace NuGetDiff.Core.Tests;

public class PackageReaderTests
{
    [Fact]
    public void Reads_basic_nuspec_metadata()
    {
        var bytes = TestPackageBuilder.Build(
            id: "TestPkg",
            version: "1.2.3",
            files: new[]
            {
                new TestPackageBuilder.FileSpec("readme.md", System.Text.Encoding.UTF8.GetBytes("# hello")),
            },
            dependencyGroups: new[]
            {
                ("net8.0", new[] { ("Newtonsoft.Json", "[13.0.0, )") }.AsEnumerable()),
            });

        using var reader = new PackageReader(new MemoryStream(bytes));
        var contents = reader.Read();

        Assert.Equal("TestPkg", contents.Metadata.Id);
        Assert.Equal("1.2.3", contents.Metadata.Version);
        Assert.Equal("test", contents.Metadata.Authors);
        Assert.Single(contents.Metadata.DependencyGroups);
        Assert.Equal("net8.0", contents.Metadata.DependencyGroups[0].TargetFramework);
        Assert.Equal("Newtonsoft.Json", contents.Metadata.DependencyGroups[0].Dependencies[0].Id);
    }

    [Fact]
    public void Lists_files_and_classifies_kinds()
    {
        var bytes = TestPackageBuilder.Build(
            id: "Cls",
            version: "1.0.0",
            files: new[]
            {
                new TestPackageBuilder.FileSpec("readme.md", System.Text.Encoding.UTF8.GetBytes("# hi")),
                new TestPackageBuilder.FileSpec("LICENSE.txt", System.Text.Encoding.UTF8.GetBytes("MIT")),
                new TestPackageBuilder.FileSpec("lib/net8.0/Cls.dll", new byte[] { 0x4d, 0x5a, 0x90, 0x00 }),
                new TestPackageBuilder.FileSpec("runtimes/win-x64/native/native.dll", new byte[] { 0x4d, 0x5a, 0x90, 0x00 }),
                new TestPackageBuilder.FileSpec("content/data.bin", new byte[] { 0x01, 0x02, 0x03 }),
            });

        using var reader = new PackageReader(new MemoryStream(bytes));
        var files = reader.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(FileKind.Markdown, files["readme.md"].Kind);
        Assert.Equal(FileKind.Text, files["LICENSE.txt"].Kind);
        Assert.Equal(FileKind.Assembly, files["lib/net8.0/Cls.dll"].Kind);
        Assert.Equal(FileKind.Binary, files["runtimes/win-x64/native/native.dll"].Kind);
        Assert.Equal(FileKind.Binary, files["content/data.bin"].Kind);
    }

    [Fact]
    public void Skips_internal_zip_metadata_entries()
    {
        var bytes = TestPackageBuilder.Build(
            id: "Pkg",
            version: "1.0.0",
            files: new[]
            {
                new TestPackageBuilder.FileSpec("_rels/.rels", System.Text.Encoding.UTF8.GetBytes("<rels/>")),
                new TestPackageBuilder.FileSpec("[Content_Types].xml", System.Text.Encoding.UTF8.GetBytes("<types/>")),
                new TestPackageBuilder.FileSpec("readme.md", System.Text.Encoding.UTF8.GetBytes("hi")),
            });

        using var reader = new PackageReader(new MemoryStream(bytes));
        Assert.Contains(reader.Files, f => f.Path == "readme.md");
        Assert.DoesNotContain(reader.Files, f => f.Path.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(reader.Files, f => f.Path.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Open_file_returns_content()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("hello world");
        var bytes = TestPackageBuilder.Build(
            id: "Pkg",
            version: "1.0.0",
            files: new[] { new TestPackageBuilder.FileSpec("readme.md", payload) });

        using var reader = new PackageReader(new MemoryStream(bytes));
        var read = reader.ReadFileBytes("readme.md");
        Assert.Equal(payload, read);
    }

    [Fact]
    public void Finds_files_case_insensitively_and_reports_archive_length()
    {
        var payload = new byte[64 * 1024];
        Array.Fill<byte>(payload, 0x2A);
        var bytes = TestPackageBuilder.Build(
            id: "Pkg",
            version: "1.0.0",
            files: new[] { new TestPackageBuilder.FileSpec("lib/net8.0/Pkg.dll", payload) });

        using var reader = new PackageReader(new MemoryStream(bytes));
        var entry = reader.FindFile("LIB/NET8.0/PKG.DLL");

        Assert.NotNull(entry);
        Assert.Equal(payload.Length, entry.Length);
        Assert.True(reader.ContainsFile("Lib/Net8.0/Pkg.dll"));
    }
}
