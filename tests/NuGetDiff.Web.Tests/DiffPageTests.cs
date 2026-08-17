using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NuGetDiff.Core.Packages;
using NuGetDiff.Web.Services;

namespace NuGetDiff.Web.Tests;

public sealed class DiffPageTests : TestContext
{
    private const string Id = "Test.Package";
    private const string OldVersion = "1.0.0";
    private const string NewVersion = "2.0.0";

    [Fact]
    public void Comparison_completes_without_eagerly_rendering_assembly_types()
    {
        var source = new TestPackageSource();
        source.Add(Id, OldVersion, BuildAssemblyPackage(OldVersion, typeof(NuGetDiff.Core.Models.PackageDescriptor)));
        source.Add(Id, NewVersion, BuildAssemblyPackage(NewVersion, typeof(NuGetDiff.Web.App)));
        RegisterServices(source);

        var cut = RenderComparison();

        cut.WaitForElement(".badge-modified", TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("Computing diff", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".file-link.is-type"));
        Assert.Single(cut.FindAll(".file-layout"));
        Assert.Single(cut.FindAll(".file-sidebar"));
        Assert.Single(cut.FindAll(".file-content"));
        Assert.False(Services.GetRequiredService<BusyState>().IsBusy);
    }

    [Fact]
    public void Comparison_surfaces_a_missing_version_and_clears_busy_state()
    {
        var source = new TestPackageSource();
        source.Add(Id, OldVersion, BuildTextPackage(OldVersion, "old"));
        RegisterServices(source);

        var cut = RenderComparison();

        cut.WaitForAssertion(
            () => Assert.Contains($"Could not find {Id} {NewVersion}.", cut.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        Assert.False(Services.GetRequiredService<BusyState>().IsBusy);
    }

    [Fact]
    public void Changed_file_click_navigates_to_the_file_diff()
    {
        var source = new TestPackageSource();
        source.Add(Id, OldVersion, BuildTextPackage(OldVersion, "old"));
        source.Add(Id, NewVersion, BuildTextPackage(NewVersion, "new"));
        RegisterServices(source);

        var cut = RenderComparison();
        var link = cut.WaitForElement(
            ".file-tree-node.is-file > .file-link",
            TimeSpan.FromSeconds(5));
        var navigation = Services.GetRequiredService<NavigationManager>();

        link.Click();

        cut.WaitForAssertion(
            () => Assert.EndsWith(
                $"/{Id}/{OldVersion}/{NewVersion}/file?path=notes.txt",
                navigation.Uri,
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Assembly_name_loads_types_once_without_navigating()
    {
        var source = new TestPackageSource();
        source.Add(Id, OldVersion, BuildAssemblyPackage(OldVersion, typeof(NuGetDiff.Core.Models.PackageDescriptor)));
        source.Add(Id, NewVersion, BuildAssemblyPackage(NewVersion, typeof(NuGetDiff.Web.App)));
        RegisterServices(source);
        var cut = RenderComparison();
        var assemblyName = cut.WaitForElement(
            ".file-tree-node.is-asm .asm-summary",
            TimeSpan.FromSeconds(10));
        var navigation = Services.GetRequiredService<NavigationManager>();
        var comparisonUri = navigation.Uri;

        assemblyName.Click();

        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll(".file-link.is-type")),
            TimeSpan.FromSeconds(10));
        Assert.Equal(comparisonUri, navigation.Uri);
        var callsAfterFirstExpansion = source.OpenCount;

        assemblyName.Click();

        Assert.Equal(callsAfterFirstExpansion, source.OpenCount);
    }

    [Fact]
    public void Assembly_open_button_navigates_to_the_whole_file_diff()
    {
        var source = new TestPackageSource();
        source.Add(Id, OldVersion, BuildAssemblyPackage(OldVersion, typeof(NuGetDiff.Core.Models.PackageDescriptor)));
        source.Add(Id, NewVersion, BuildAssemblyPackage(NewVersion, typeof(NuGetDiff.Web.App)));
        RegisterServices(source);
        var cut = RenderComparison();
        var open = cut.WaitForElement(".assembly-open", TimeSpan.FromSeconds(10));
        var navigation = Services.GetRequiredService<NavigationManager>();

        open.Click();

        cut.WaitForAssertion(() => Assert.Contains(
            "/file?path=lib%2Fnet8.0%2FTest.Package.dll",
            navigation.Uri,
            StringComparison.Ordinal));
    }

    [Fact]
    public void Hide_unchanged_files_filters_identical_types_inside_a_renamed_assembly()
    {
        var source = new TestPackageSource();
        var assembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        source.Add(
            Id,
            OldVersion,
            BuildPackageWithAssembly(
                OldVersion,
                "lib/net8.0/Old.dll",
                assembly));
        source.Add(
            Id,
            NewVersion,
            BuildPackageWithAssembly(
                NewVersion,
                "lib/net8.0/New.dll",
                assembly));
        RegisterServices(source);
        var cut = RenderComparison();
        var assemblyName = cut.WaitForElement(
            ".file-tree-node.is-asm .asm-summary",
            TimeSpan.FromSeconds(10));

        assemblyName.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(
                "No matching types",
                cut.Markup,
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        Assert.Empty(cut.FindAll(".file-link.is-type"));

        cut.Find("input[aria-label='Hide unchanged files']").Change(false);

        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll(".file-link.is-type")),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Unrelated_sibling_changes_do_not_keep_identical_assembly_visible()
    {
        var source = new TestPackageSource();
        var coreAssembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        var packagingAssembly = TestPackageBuilder.LoadAssemblyBytes(
            typeof(NuGet.Packaging.PackageArchiveReader));
        source.Add(
            Id,
            OldVersion,
            TestPackageBuilder.Build(
                Id,
                OldVersion,
                new[]
                {
                    new TestPackageBuilder.FileSpec(
                        "lib/net8.0/NuGetDiff.Core.dll",
                        coreAssembly),
                    new TestPackageBuilder.FileSpec(
                        "lib/net8.0/NuGet.Packaging.dll",
                        packagingAssembly),
                }));
        source.Add(
            Id,
            NewVersion,
            TestPackageBuilder.Build(
                Id,
                NewVersion,
                new[]
                {
                    new TestPackageBuilder.FileSpec(
                        "lib/net8.0/NuGetDiff.Core.dll",
                        coreAssembly),
                }));
        RegisterServices(source);
        var cut = RenderComparison();
        var filter = cut.WaitForElement(
            "input[aria-label='Hide unchanged files']",
            TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("NuGetDiff.Core.dll", FileNames(cut));

        filter.Change(false);
        var coreNode = cut.FindAll(".asm-summary")
            .Single(element => element.TextContent.Contains(
                "NuGetDiff.Core.dll",
                StringComparison.Ordinal));
        coreNode.Click();
        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll(".file-link.is-type")),
            TimeSpan.FromSeconds(10));

        filter.Change(true);

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("NuGetDiff.Core.dll", FileNames(cut)));
    }

    [Fact]
    public async Task Expansion_shows_types_before_exact_filtering_finishes()
    {
        var source = new BlockingExactAnalysisSource();
        var oldAssembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        var newAssembly = ChangePeTimestamp(oldAssembly);
        source.Add(
            Id,
            OldVersion,
            BuildPackageWithAssembly(
                OldVersion,
                "lib/net8.0/Test.Package.dll",
                oldAssembly));
        source.Add(
            Id,
            NewVersion,
            BuildPackageWithAssembly(
                NewVersion,
                "lib/net8.0/Test.Package.dll",
                newAssembly));
        RegisterServices(source);
        var cut = RenderComparison();
        var assemblyName = cut.WaitForElement(
            ".file-tree-node.is-asm .asm-summary",
            TimeSpan.FromSeconds(10));

        assemblyName.Click();
        await source.ExactAnalysisStarted.WaitAsync(TimeSpan.FromSeconds(10));

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll(".file-link.is-type"));
            Assert.Contains(
                "Filtering unchanged types",
                cut.Markup,
                StringComparison.Ordinal);
        });

        cut.Find(".file-tree-node.is-asm .asm-summary").Click();
        source.ReleaseExactAnalysis();
        cut.WaitForAssertion(
            () => Assert.Contains("No matching types", cut.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(20));
        Assert.False(cut.Find(".file-tree-node.is-asm > details").HasAttribute("open"));
    }

    [Fact]
    public void Exact_filter_failure_keeps_provisional_types_and_error_visible()
    {
        var source = new FailingExactAnalysisSource();
        var oldAssembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        var newAssembly = ChangePeTimestamp(oldAssembly);
        source.Add(
            Id,
            OldVersion,
            BuildPackageWithAssembly(
                OldVersion,
                "lib/net8.0/Test.Package.dll",
                oldAssembly));
        source.Add(
            Id,
            NewVersion,
            BuildPackageWithAssembly(
                NewVersion,
                "lib/net8.0/Test.Package.dll",
                newAssembly));
        RegisterServices(source);
        var cut = RenderComparison();

        cut.WaitForElement(
            ".file-tree-node.is-asm .asm-summary",
            TimeSpan.FromSeconds(10)).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll(".file-link.is-type"));
            Assert.Contains(
                "Failed to load types",
                cut.Find(".tree-status.error").TextContent,
                StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Repeated_comparison_uses_the_cached_tree_diff()
    {
        var source = new TestPackageSource();
        source.Add(Id, OldVersion, BuildTextPackage(OldVersion, "old"));
        source.Add(Id, NewVersion, BuildTextPackage(NewVersion, "new"));
        RegisterServices(source);
        var first = RenderComparison();
        first.WaitForElement(".badge-modified", TimeSpan.FromSeconds(5));
        var callsAfterFirstComparison = source.OpenCount;

        var second = RenderComparison();
        second.WaitForElement(".badge-modified", TimeSpan.FromSeconds(5));

        Assert.Equal(callsAfterFirstComparison, source.OpenCount);
    }

    [Fact]
    public void Hides_unchanged_files_by_default_and_can_show_them()
    {
        var source = new TestPackageSource();
        source.Add(Id, OldVersion, BuildMixedPackage(OldVersion, "old"));
        source.Add(Id, NewVersion, BuildMixedPackage(NewVersion, "new"));
        RegisterServices(source);

        var cut = RenderComparison();
        cut.WaitForElement(".file-link", TimeSpan.FromSeconds(5));
        var filter = cut.Find("input[aria-label='Hide unchanged files']");

        Assert.True(filter.HasAttribute("checked"));
        Assert.DoesNotContain("same.txt", FileNames(cut));

        filter.Change(false);

        cut.WaitForAssertion(() => Assert.Contains("same.txt", FileNames(cut)));
    }

    private IRenderedComponent<NuGetDiff.Web.Pages.Diff> RenderComparison()
        => RenderComponent<NuGetDiff.Web.Pages.Diff>(parameters => parameters
            .Add(component => component.Id, Id)
            .Add(component => component.V1, OldVersion)
            .Add(component => component.V2, NewVersion));

    private void RegisterServices(IPackageSource source)
    {
        Services.AddSingleton<BusyState>();
        Services.AddSingleton(new PackageResolver(source, new UploadedPackageSource()));
        Services.AddSingleton<PackageAnalysisCache>();
        Services.AddSingleton<PackageAnalysisService>();
        Services.AddSingleton<PackageComparisonService>();
    }

    private static byte[] BuildTextPackage(string version, string text)
        => TestPackageBuilder.Build(
            Id,
            version,
            new[]
            {
                new TestPackageBuilder.FileSpec(
                    "notes.txt",
                    System.Text.Encoding.UTF8.GetBytes(text)),
            });

    private static byte[] BuildAssemblyPackage(string version, Type markerType)
        => TestPackageBuilder.Build(
            Id,
            version,
            new[]
            {
                new TestPackageBuilder.FileSpec(
                    "lib/net8.0/Test.Package.dll",
                    TestPackageBuilder.LoadAssemblyBytes(markerType)),
            });

    private static byte[] BuildMixedPackage(string version, string changedText)
        => TestPackageBuilder.Build(
            Id,
            version,
            new[]
            {
                new TestPackageBuilder.FileSpec(
                    "notes.txt",
                    System.Text.Encoding.UTF8.GetBytes(changedText)),
                new TestPackageBuilder.FileSpec(
                    "same.txt",
                    System.Text.Encoding.UTF8.GetBytes("same")),
            });

    private static byte[] BuildPackageWithAssembly(
        string version,
        string path,
        byte[] assembly)
        => TestPackageBuilder.Build(
            Id,
            version,
            new[] { new TestPackageBuilder.FileSpec(path, assembly) });

    private static byte[] ChangePeTimestamp(byte[] assembly)
    {
        var changed = (byte[])assembly.Clone();
        var peHeaderOffset = BitConverter.ToInt32(changed, 0x3c);
        changed[peHeaderOffset + 8] ^= 1;
        return changed;
    }

    private static IReadOnlyList<string> FileNames(IRenderedFragment cut)
        => cut.FindAll(".file-link .name")
            .Select(element => element.TextContent)
            .ToArray();

    private sealed class BlockingExactAnalysisSource : IPackageSource
    {
        private readonly Dictionary<(string Id, string Version), byte[]> _packages = new();
        private readonly TaskCompletionSource _exactAnalysisStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseExactAnalysis =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _openCount;

        public Task ExactAnalysisStarted => _exactAnalysisStarted.Task;

        public void Add(string id, string version, byte[] bytes)
            => _packages[(id.ToLowerInvariant(), version.ToLowerInvariant())] = bytes;

        public void ReleaseExactAnalysis() => _releaseExactAnalysis.TrySetResult();

        public async Task<Stream?> OpenAsync(
            string id,
            string version,
            CancellationToken ct = default)
        {
            var openNumber = Interlocked.Increment(ref _openCount);
            if (openNumber > 4)
            {
                if (openNumber == 6)
                {
                    _exactAnalysisStarted.TrySetResult();
                }
                await _releaseExactAnalysis.Task.WaitAsync(ct);
            }

            return _packages.TryGetValue(
                (id.ToLowerInvariant(), version.ToLowerInvariant()),
                out var bytes)
                ? new MemoryStream(bytes, writable: false)
                : null;
        }
    }

    private sealed class FailingExactAnalysisSource : IPackageSource
    {
        private readonly Dictionary<(string Id, string Version), byte[]> _packages = new();
        private int _openCount;

        public void Add(string id, string version, byte[] bytes)
            => _packages[(id.ToLowerInvariant(), version.ToLowerInvariant())] = bytes;

        public Task<Stream?> OpenAsync(
            string id,
            string version,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _openCount) > 4)
            {
                throw new HttpRequestException("Exact analysis failed.");
            }
            return Task.FromResult<Stream?>(
                _packages.TryGetValue(
                    (id.ToLowerInvariant(), version.ToLowerInvariant()),
                    out var bytes)
                    ? new MemoryStream(bytes, writable: false)
                    : null);
        }
    }

}
