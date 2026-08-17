using NuGetDiff.Core.Packages;
using NuGetDiff.Core.Models;
using NuGetDiff.Web.Services;

namespace NuGetDiff.Web.Tests;

public sealed class PackageComparisonServiceTests
{
    private const string Id = "Test.Package";
    private const string OldVersion = "1.0.0";
    private const string NewVersion = "2.0.0";

    [Fact]
    public async Task Starts_both_package_reads_before_waiting_for_either()
    {
        var source = new CoordinatedPackageSource(Packages());
        var service = CreateService(source);
        var comparisonTask = service.CompareTreeAsync(Id, OldVersion, NewVersion);

        try
        {
            await source.BothStarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            source.Release();
        }

        var result = await comparisonTask;
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Comparison_honors_cancellation_while_packages_are_loading()
    {
        var source = new CoordinatedPackageSource(Packages());
        var service = CreateService(source);
        using var cts = new CancellationTokenSource();
        var comparisonTask = service.CompareTreeAsync(
            Id,
            OldVersion,
            NewVersion,
            ct: cts.Token);
        await source.BothStarted.WaitAsync(TimeSpan.FromSeconds(2));

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await comparisonTask);
        source.Release();
    }

    [Fact]
    public async Task Reuses_cached_tree_and_file_diff_results()
    {
        var source = new ImmediatePackageSource(Packages());
        var service = CreateService(source);

        var first = await service.CompareFileAsync(
            Id,
            OldVersion,
            NewVersion,
            "notes.txt",
            typeName: null);
        var callsAfterFirst = source.OpenCount;
        var second = await service.CompareFileAsync(
            Id,
            OldVersion,
            NewVersion,
            "notes.txt",
            typeName: null);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Same(first.TreeDiff, second.TreeDiff);
        Assert.Same(first.FileDiff, second.FileDiff);
        Assert.Equal(callsAfterFirst, source.OpenCount);
    }

    [Fact]
    public async Task Disposes_a_successful_stream_when_the_other_open_fails()
    {
        var stream = new TrackingMemoryStream(BuildPackage(OldVersion, "old"));
        var service = CreateService(new PartialFailureSource(stream));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.CompareTreeAsync(Id, OldVersion, NewVersion));

        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task Uses_both_paths_when_a_file_was_renamed()
    {
        var source = new ImmediatePackageSource(new Dictionary<(string Id, string Version), byte[]>
        {
            [(Id.ToLowerInvariant(), OldVersion)] = TestPackageBuilder.Build(
                Id,
                OldVersion,
                new[]
                {
                    new TestPackageBuilder.FileSpec(
                        "old.txt",
                        System.Text.Encoding.UTF8.GetBytes("same")),
                }),
            [(Id.ToLowerInvariant(), NewVersion)] = TestPackageBuilder.Build(
                Id,
                NewVersion,
                new[]
                {
                    new TestPackageBuilder.FileSpec(
                        "new.txt",
                        System.Text.Encoding.UTF8.GetBytes("same")),
                }),
        });
        var service = CreateService(source);

        var result = await service.CompareFileAsync(
            Id,
            OldVersion,
            NewVersion,
            "new.txt",
            typeName: null);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(result.FileDiff!.Text!.Old, line => line.Kind != DiffLineKind.Equal);
        Assert.DoesNotContain(result.FileDiff.Text.New, line => line.Kind != DiffLineKind.Equal);

        var oldPathResult = await service.CompareFileAsync(
            Id,
            OldVersion,
            NewVersion,
            "old.txt",
            typeName: null);
        Assert.True(oldPathResult.IsSuccess);
        Assert.DoesNotContain(
            oldPathResult.FileDiff!.Text!.Old,
            line => line.Kind != DiffLineKind.Equal);
        Assert.DoesNotContain(
            oldPathResult.FileDiff.Text.New,
            line => line.Kind != DiffLineKind.Equal);
    }

    [Fact]
    public async Task Classifies_identical_types_in_a_renamed_assembly_as_unchanged()
    {
        var assembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        var source = new ImmediatePackageSource(new Dictionary<(string Id, string Version), byte[]>
        {
            [(Id.ToLowerInvariant(), OldVersion)] = TestPackageBuilder.Build(
                Id,
                OldVersion,
                new[]
                {
                    new TestPackageBuilder.FileSpec("lib/net8.0/Old.dll", assembly),
                }),
            [(Id.ToLowerInvariant(), NewVersion)] = TestPackageBuilder.Build(
                Id,
                NewVersion,
                new[]
                {
                    new TestPackageBuilder.FileSpec("lib/net8.0/New.dll", assembly),
                }),
        });
        var service = CreateService(source);
        var tree = await service.CompareTreeAsync(Id, OldVersion, NewVersion);
        var assemblyChange = Assert.Single(
            tree.Diff!.Changes,
            change => change.Kind == FileChangeKind.Renamed);

        var result = await service.LoadAssemblyTypesAsync(
            Id,
            OldVersion,
            NewVersion,
            assemblyChange);

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.Changes!);
        Assert.All(
            result.Changes!,
            change => Assert.Equal(FileChangeKind.Unchanged, change.Kind));
        Assert.Equal(4, source.OpenCount);
    }

    [Fact]
    public async Task Unrelated_sibling_changes_do_not_modify_byte_identical_types()
    {
        var coreAssembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        var packagingAssembly = TestPackageBuilder.LoadAssemblyBytes(
            typeof(NuGet.Packaging.PackageArchiveReader));
        var source = new ImmediatePackageSource(new Dictionary<(string Id, string Version), byte[]>
        {
            [(Id.ToLowerInvariant(), OldVersion)] = TestPackageBuilder.Build(
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
                }),
            [(Id.ToLowerInvariant(), NewVersion)] = TestPackageBuilder.Build(
                Id,
                NewVersion,
                new[]
                {
                    new TestPackageBuilder.FileSpec(
                        "lib/net8.0/NuGetDiff.Core.dll",
                        coreAssembly),
                }),
        });
        var service = CreateService(source);
        var tree = await service.CompareTreeAsync(Id, OldVersion, NewVersion);
        var coreChange = Assert.Single(
            tree.Diff!.Changes,
            change => change.Path == "lib/net8.0/NuGetDiff.Core.dll");
        Assert.Equal(FileChangeKind.Unchanged, coreChange.Kind);

        var result = await service.LoadAssemblyTypesAsync(
            Id,
            OldVersion,
            NewVersion,
            coreChange);

        Assert.DoesNotContain(
            result.Changes!,
            change => change.Type.ReflectionName == "NuGetDiff.Core.Packages.PackageReader"
                      && change.Kind == FileChangeKind.Modified);
    }

    private static PackageComparisonService CreateService(IPackageSource source)
    {
        var resolver = new PackageResolver(source, new UploadedPackageSource());
        var cache = new PackageAnalysisCache();
        var analysis = new PackageAnalysisService(resolver, cache);
        return new PackageComparisonService(resolver, analysis, cache);
    }

    private static IReadOnlyDictionary<(string Id, string Version), byte[]> Packages()
        => new Dictionary<(string Id, string Version), byte[]>
        {
            [(Id.ToLowerInvariant(), OldVersion)] = BuildPackage(OldVersion, "old"),
            [(Id.ToLowerInvariant(), NewVersion)] = BuildPackage(NewVersion, "new"),
        };

    private static byte[] BuildPackage(string version, string content)
        => TestPackageBuilder.Build(
            Id,
            version,
            new[]
            {
                new TestPackageBuilder.FileSpec(
                    "notes.txt",
                    System.Text.Encoding.UTF8.GetBytes(content)),
            });

    private sealed class ImmediatePackageSource : IPackageSource
    {
        private readonly IReadOnlyDictionary<(string Id, string Version), byte[]> _packages;

        public ImmediatePackageSource(
            IReadOnlyDictionary<(string Id, string Version), byte[]> packages)
        {
            _packages = packages;
        }

        public int OpenCount { get; private set; }

        public Task<Stream?> OpenAsync(
            string id,
            string version,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            OpenCount++;
            return Task.FromResult(Open(_packages, id, version));
        }
    }

    private sealed class CoordinatedPackageSource : IPackageSource
    {
        private readonly IReadOnlyDictionary<(string Id, string Version), byte[]> _packages;
        private readonly TaskCompletionSource _bothStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public CoordinatedPackageSource(
            IReadOnlyDictionary<(string Id, string Version), byte[]> packages)
        {
            _packages = packages;
        }

        public Task BothStarted => _bothStarted.Task;

        public void Release() => _release.TrySetResult();

        public async Task<Stream?> OpenAsync(
            string id,
            string version,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _started) == 2)
            {
                _bothStarted.TrySetResult();
            }
            await _release.Task.WaitAsync(ct);
            return Open(_packages, id, version);
        }
    }

    private sealed class PartialFailureSource : IPackageSource
    {
        private readonly TrackingMemoryStream _stream;

        public PartialFailureSource(TrackingMemoryStream stream)
        {
            _stream = stream;
        }

        public Task<Stream?> OpenAsync(
            string id,
            string version,
            CancellationToken ct = default)
            => version == OldVersion
                ? Task.FromResult<Stream?>(_stream)
                : Task.FromException<Stream?>(new HttpRequestException("Download failed."));
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public TrackingMemoryStream(byte[] bytes)
            : base(bytes, writable: false)
        {
        }

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private static Stream? Open(
        IReadOnlyDictionary<(string Id, string Version), byte[]> packages,
        string id,
        string version)
        => packages.TryGetValue((id.ToLowerInvariant(), version.ToLowerInvariant()), out var bytes)
            ? new MemoryStream(bytes, writable: false)
            : null;
}
