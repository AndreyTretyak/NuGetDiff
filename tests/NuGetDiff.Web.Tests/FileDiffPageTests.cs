using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NuGetDiff.Core.Packages;
using NuGetDiff.Web.Pages;
using NuGetDiff.Web.Services;

namespace NuGetDiff.Web.Tests;

public sealed class FileDiffPageTests : TestContext
{
    private const string Id = "Test.Package";
    private const string OldVersion = "1.0.0";
    private const string NewVersion = "2.0.0";

    [Fact]
    public void Renders_a_text_file_diff_and_clears_busy_state()
    {
        var source = new TestPackageSource();
        source.Add(Id, OldVersion, BuildPackage(OldVersion, "notes.txt", "old line"));
        source.Add(Id, NewVersion, BuildPackage(NewVersion, "notes.txt", "new line"));
        RegisterServices(source);

        var cut = RenderFileDiff("notes.txt");

        var diffView = cut.WaitForElement(".diff-view", TimeSpan.FromSeconds(5));
        Assert.Contains("old line", diffView.TextContent, StringComparison.Ordinal);
        Assert.Contains("new line", diffView.TextContent, StringComparison.Ordinal);
        Assert.False(Services.GetRequiredService<BusyState>().IsBusy);
    }

    [Fact]
    public void Renders_binary_file_metadata()
    {
        var source = new TestPackageSource();
        source.Add(Id, OldVersion, BuildPackage(OldVersion, "data.bin", new byte[] { 1, 2, 3 }));
        source.Add(Id, NewVersion, BuildPackage(NewVersion, "data.bin", new byte[] { 1, 2, 3, 4 }));
        RegisterServices(source);

        var cut = RenderFileDiff("data.bin");

        cut.WaitForElement(".meta-grid", TimeSpan.FromSeconds(5));
        Assert.Contains("<dt>Old size</dt><dd>3 bytes</dd>", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("<dt>New size</dt><dd>4 bytes</dd>", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_a_named_assembly_type_diff()
    {
        var source = new TestPackageSource();
        var assembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        source.Add(Id, OldVersion, BuildPackage(OldVersion, "lib/net8.0/Test.Package.dll", assembly));
        source.Add(Id, NewVersion, BuildPackage(NewVersion, "lib/net8.0/Test.Package.dll", assembly));
        RegisterServices(source);

        var cut = RenderFileDiff(
            "lib/net8.0/Test.Package.dll",
            "NuGetDiff.Core.Models.PackageDescriptor");

        cut.WaitForElement(".diff-view", TimeSpan.FromSeconds(10));
        Assert.Contains("PackageDescriptor", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(
            "No C# source differences",
            cut.Find(".diff-notice").TextContent,
            StringComparison.Ordinal);
        Assert.False(Services.GetRequiredService<BusyState>().IsBusy);
    }

    [Fact]
    public async Task Shows_a_ready_type_diff_while_sidebar_types_are_still_loading()
    {
        var source = new BlockingAfterInitialPairSource();
        source.Add(
            Id,
            OldVersion,
            BuildPackage(
                OldVersion,
                "lib/net8.0/Test.Package.dll",
                TestPackageBuilder.LoadAssemblyBytes(typeof(NuGetDiff.Core.Models.PackageDescriptor))));
        source.Add(
            Id,
            NewVersion,
            BuildPackage(
                NewVersion,
                "lib/net8.0/Test.Package.dll",
                TestPackageBuilder.LoadAssemblyBytes(typeof(NuGetDiff.Web.App))));
        RegisterServices(source);

        var cut = RenderFileDiff(
            "lib/net8.0/Test.Package.dll",
            "NuGetDiff.Core.Models.PackageDescriptor");
        try
        {
            await source.SidebarLoadsStarted.WaitAsync(TimeSpan.FromSeconds(10));

            cut.WaitForElement(".diff-view", TimeSpan.FromSeconds(2));
            Assert.False(Services.GetRequiredService<BusyState>().IsBusy);
        }
        finally
        {
            source.ReleaseSidebarLoads();
        }
    }

    [Fact]
    public void Sidebar_hides_unchanged_files_by_default_and_can_show_them()
    {
        var source = new TestPackageSource();
        source.Add(Id, OldVersion, BuildMixedPackage(OldVersion, "old"));
        source.Add(Id, NewVersion, BuildMixedPackage(NewVersion, "new"));
        RegisterServices(source);

        var cut = RenderFileDiff("notes.txt");
        cut.WaitForElement(".diff-view", TimeSpan.FromSeconds(5));
        var filter = cut.Find("input[aria-label='Hide unchanged files']");

        Assert.True(filter.HasAttribute("checked"));
        Assert.DoesNotContain("same.txt", FileNames(cut));

        filter.Change(false);

        cut.WaitForAssertion(() => Assert.Contains("same.txt", FileNames(cut)));
    }

    [Fact]
    public async Task Filter_toggle_during_type_loading_updates_the_current_tree()
    {
        var source = new BlockingAfterInitialPairSource();
        source.Add(
            Id,
            OldVersion,
            BuildPackage(
                OldVersion,
                "lib/net8.0/Test.Package.dll",
                TestPackageBuilder.LoadAssemblyBytes(typeof(NuGetDiff.Core.Models.PackageDescriptor))));
        source.Add(
            Id,
            NewVersion,
            BuildPackage(
                NewVersion,
                "lib/net8.0/Test.Package.dll",
                TestPackageBuilder.LoadAssemblyBytes(typeof(NuGetDiff.Web.App))));
        RegisterServices(source);
        var cut = RenderFileDiff(
            "lib/net8.0/Test.Package.dll",
            "NuGetDiff.Core.Models.PackageDescriptor");

        await source.SidebarLoadsStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cut.Find("input[aria-label='Hide unchanged files']").Change(false);
        source.ReleaseSidebarLoads();

        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll(".file-link.is-type")),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Old_renamed_type_path_selects_the_canonical_tree_node()
    {
        var source = new TestPackageSource();
        var assembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        source.Add(
            Id,
            OldVersion,
            BuildPackage(OldVersion, "lib/net8.0/Old.dll", assembly));
        source.Add(
            Id,
            NewVersion,
            BuildPackage(NewVersion, "lib/net8.0/New.dll", assembly));
        RegisterServices(source);

        var cut = RenderFileDiff(
            "lib/net8.0/Old.dll",
            "NuGetDiff.Core.Models.PackageDescriptor");
        cut.WaitForElement(".diff-view", TimeSpan.FromSeconds(10));

        cut.Find("input[aria-label='Hide unchanged files']").Change(false);

        cut.WaitForElement(".file-link.is-type.selected", TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Semantically_unchanged_selected_type_is_hidden_only_in_the_sidebar()
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

        var cut = RenderFileDiff(
            "lib/net8.0/NuGetDiff.Core.dll",
            "NuGetDiff.Core.Packages.PackageReader");

        cut.WaitForElement(".diff-view", TimeSpan.FromSeconds(20));
        Assert.DoesNotContain("NuGetDiff.Core.dll", FileNames(cut));

        cut.Find("input[aria-label='Hide unchanged files']").Change(false);

        var selected = cut.WaitForElement(
            ".file-link.is-type.selected",
            TimeSpan.FromSeconds(10));
        Assert.Equal("PackageReader", selected.QuerySelector(".name")?.TextContent);
    }

    private IRenderedComponent<FileDiffPage> RenderFileDiff(
        string path,
        string typeName = "")
    {
        var query = $"?path={Uri.EscapeDataString(path)}";
        if (!string.IsNullOrEmpty(typeName))
        {
            query += $"&type={Uri.EscapeDataString(typeName)}";
        }
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"/{Id}/{OldVersion}/{NewVersion}/file{query}");

        return RenderComponent<FileDiffPage>(parameters => parameters
            .Add(component => component.Id, Id)
            .Add(component => component.V1, OldVersion)
            .Add(component => component.V2, NewVersion));
    }

    private void RegisterServices(IPackageSource source)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<BusyState>();
        Services.AddSingleton(new PackageResolver(source, new UploadedPackageSource()));
        Services.AddSingleton<PackageAnalysisCache>();
        Services.AddSingleton<PackageAnalysisService>();
        Services.AddSingleton<PackageComparisonService>();
    }

    private static byte[] BuildPackage(string version, string path, string content)
        => BuildPackage(version, path, System.Text.Encoding.UTF8.GetBytes(content));

    private static byte[] BuildPackage(string version, string path, byte[] content)
        => TestPackageBuilder.Build(
            Id,
            version,
            new[] { new TestPackageBuilder.FileSpec(path, content) });

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

    private static IReadOnlyList<string> FileNames(IRenderedFragment cut)
        => cut.FindAll(".file-link .name")
            .Select(element => element.TextContent)
            .ToArray();

    private sealed class BlockingAfterInitialPairSource : IPackageSource
    {
        private readonly Dictionary<(string Id, string Version), byte[]> _packages = new();
        private readonly TaskCompletionSource _sidebarLoadsStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSidebarLoads =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _openCount;

        public Task SidebarLoadsStarted => _sidebarLoadsStarted.Task;

        public void Add(string id, string version, byte[] bytes)
            => _packages[(id.ToLowerInvariant(), version.ToLowerInvariant())] = bytes;

        public void ReleaseSidebarLoads() => _releaseSidebarLoads.TrySetResult();

        public async Task<Stream?> OpenAsync(
            string id,
            string version,
            CancellationToken ct = default)
        {
            var openNumber = Interlocked.Increment(ref _openCount);
            if (openNumber > 2)
            {
                if (openNumber == 4)
                {
                    _sidebarLoadsStarted.TrySetResult();
                }
                await _releaseSidebarLoads.Task.WaitAsync(ct);
            }

            return _packages.TryGetValue(
                (id.ToLowerInvariant(), version.ToLowerInvariant()),
                out var bytes)
                ? new MemoryStream(bytes, writable: false)
                : null;
        }
    }
}
