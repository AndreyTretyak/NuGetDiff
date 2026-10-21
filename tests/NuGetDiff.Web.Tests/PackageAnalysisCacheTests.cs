using NuGetDiff.Core.Models;
using NuGetDiff.Web.Services;

namespace NuGetDiff.Web.Tests;

public sealed class PackageAnalysisCacheTests
{
    [Fact]
    public void Evicts_the_least_recently_used_entry_when_over_budget()
    {
        var cache = new PackageAnalysisCache(sizeLimitBytes: 1500);
        var first = PackageIdentity.Online("Pkg", "1.0.0");
        var second = PackageIdentity.Online("Pkg", "2.0.0");
        var third = PackageIdentity.Online("Pkg", "3.0.0");

        cache.SetTypes(first, "a.dll", Types("First"));
        cache.SetTypes(second, "b.dll", Types("Second"));
        Assert.True(cache.TryGetTypes(first, "a.dll", out _));

        cache.SetTypes(third, "c.dll", Types("Third"));

        Assert.True(cache.TryGetTypes(first, "a.dll", out _));
        Assert.False(cache.TryGetTypes(second, "b.dll", out _));
        Assert.True(cache.TryGetTypes(third, "c.dll", out _));
        Assert.True(cache.CurrentSizeBytes <= 1500);
    }

    [Fact]
    public void Does_not_cache_an_entry_larger_than_the_budget()
    {
        var cache = new PackageAnalysisCache(sizeLimitBytes: 100);
        var package = PackageIdentity.Local("ABC");

        cache.SetTypes(package, "large.dll", Types(new string('X', 100)));

        Assert.False(cache.TryGetTypes(package, "large.dll", out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Keeps_forward_and_backslash_archive_paths_distinct()
    {
        var cache = new PackageAnalysisCache();
        var package = PackageIdentity.Online("Pkg", "1.0.0");
        cache.SetTypes(package, "a/b.dll", Types("Forward"));

        Assert.True(cache.TryGetTypes(package, "a/b.dll", out _));
        Assert.False(cache.TryGetTypes(package, @"a\b.dll", out _));
    }

    [Fact]
    public void Uses_ordinal_ignore_case_for_archive_paths()
    {
        var cache = new PackageAnalysisCache();
        var package = PackageIdentity.Online("Pkg", "1.0.0");
        cache.SetTypes(package, "\u017F.dll", Types("LongS"));

        Assert.True(cache.TryGetTypes(package, "\u017F.DLL", out _));
        Assert.False(cache.TryGetTypes(package, "s.dll", out _));
    }

    [Fact]
    public void Includes_package_metadata_in_the_size_budget()
    {
        var cache = new PackageAnalysisCache(sizeLimitBytes: 2048);
        var package = PackageIdentity.Online("Pkg", "1.0.0");
        var metadata = new NuspecMetadata(
            "Pkg",
            "1.0.0",
            null,
            new string('X', 10_000),
            null,
            "test",
            null,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<string>(),
            Array.Empty<DependencyGroup>());
        var contents = new PackageContents(
            new PackageDescriptor("Pkg", "1.0.0"),
            metadata,
            Array.Empty<FileEntry>());

        cache.SetPackage(package, contents);

        Assert.False(cache.TryGetPackage(package, out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Includes_word_segments_in_the_size_budget()
    {
        var cache = new PackageAnalysisCache(sizeLimitBytes: 2048);
        var pair = new PackagePairIdentity(
            PackageIdentity.Online("Pkg", "1.0.0"),
            PackageIdentity.Online("Pkg", "2.0.0"));
        var segments = Enumerable.Range(0, 100)
            .Select(index => new DiffSegment(
                DiffSegmentKind.Modified,
                new string((char)('a' + (index % 26)), 20)))
            .ToArray();
        var line = new DiffLine(
            1,
            1,
            DiffLineKind.Modified,
            "changed",
            segments);
        var text = new SideBySideDiff(new[] { line }, new[] { line });
        var diff = new FileDiff(
            FileDiffKind.Text,
            text,
            null,
            null,
            null,
            null,
            null);

        cache.SetFileDiff(pair, "notes.txt", null, diff);

        Assert.False(cache.TryGetFileDiff(pair, "notes.txt", null, out _));
    }

    [Fact]
    public void Includes_cache_key_strings_in_the_size_budget()
    {
        var cache = new PackageAnalysisCache(sizeLimitBytes: 1024);
        var package = PackageIdentity.Online("Pkg", "1.0.0");

        cache.SetTypes(package, new string('x', 1000) + ".dll", Types("Example"));

        Assert.Equal(0, cache.Count);
    }

    private static IReadOnlyList<TypeSummary> Types(string name)
        => new[]
        {
            new TypeSummary($"Example.{name}", "Example", name),
        };
}
