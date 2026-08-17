using ICSharpCode.Decompiler.Metadata;
using NuGetDiff.Core.Decompilation;
using NuGetDiff.Core.Models;
using NuGetDiff.Core.Packages;
using Xunit;

namespace NuGetDiff.Core.Tests;

public class DecompilerTests
{
    [Fact]
    public void Decompiles_a_real_managed_assembly()
    {
        var realAsm = TestPackageBuilder.LoadCoreAssemblyBytes();
        var bytes = TestPackageBuilder.Build(
            "Real",
            "1.0.0",
            new[] { new TestPackageBuilder.FileSpec("lib/net8.0/NuGetDiff.Core.dll", realAsm) });

        using var reader = new PackageReader(new MemoryStream(bytes));
        var result = new Decompiler().DecompileFromPackage(reader, "lib/net8.0/NuGetDiff.Core.dll");

        Assert.True(result.IsOk, $"expected success, got error: {result.Err?.Message}");
        Assert.NotNull(result.Ok);
        Assert.Contains("NuGetDiff.Core", result.Ok!.CSharp);
        Assert.Contains(result.Ok.TypeFullNames, t => t.Contains("PackageDescriptor"));
        Assert.Equal("net8.0", result.Ok.TargetFramework);
    }

    [Fact]
    public void Returns_error_for_non_managed_pe()
    {
        // A 4-byte file that is not a valid PE.
        var bytes = TestPackageBuilder.Build(
            "Bad",
            "1.0.0",
            new[] { new TestPackageBuilder.FileSpec("lib/net8.0/Bad.dll", new byte[] { 0x00, 0x01, 0x02, 0x03 }) });

        using var reader = new PackageReader(new MemoryStream(bytes));
        var result = new Decompiler().DecompileFromPackage(reader, "lib/net8.0/Bad.dll");

        Assert.False(result.IsOk);
        Assert.NotNull(result.Err);
    }

    [Fact]
    public void Returns_error_for_missing_file()
    {
        var bytes = TestPackageBuilder.Build(
            "Empty",
            "1.0.0",
            Array.Empty<TestPackageBuilder.FileSpec>());

        using var reader = new PackageReader(new MemoryStream(bytes));
        var result = new Decompiler().DecompileFromPackage(reader, "lib/net8.0/Nope.dll");

        Assert.False(result.IsOk);
        Assert.Contains("not found", result.Err!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lists_top_level_types_without_compiler_generated()
    {
        var realAsm = TestPackageBuilder.LoadCoreAssemblyBytes();
        var bytes = TestPackageBuilder.Build(
            "Real",
            "1.0.0",
            new[] { new TestPackageBuilder.FileSpec("lib/net8.0/NuGetDiff.Core.dll", realAsm) });

        using var reader = new PackageReader(new MemoryStream(bytes));
        var result = new Decompiler().ListTypes(reader, "lib/net8.0/NuGetDiff.Core.dll");

        Assert.True(result.IsOk, $"expected success, got error: {result.Err?.Message}");
        var types = result.Ok!;
        Assert.NotEmpty(types);

        // Real types are present.
        Assert.Contains(types, t => t.ReflectionName == "NuGetDiff.Core.Models.PackageDescriptor");

        // No compiler-generated types should slip through.
        Assert.DoesNotContain(types, t => t.DisplayName.StartsWith("<", StringComparison.Ordinal));
        Assert.DoesNotContain(types, t => t.ReflectionName.Contains("<Module>", StringComparison.Ordinal));
    }

    [Fact]
    public void Decompiles_a_single_named_type()
    {
        var realAsm = TestPackageBuilder.LoadCoreAssemblyBytes();
        var bytes = TestPackageBuilder.Build(
            "Real",
            "1.0.0",
            new[] { new TestPackageBuilder.FileSpec("lib/net8.0/NuGetDiff.Core.dll", realAsm) });

        using var reader = new PackageReader(new MemoryStream(bytes));
        var d = new Decompiler();
        var result = d.DecompileTypeFromPackage(
            reader,
            "lib/net8.0/NuGetDiff.Core.dll",
            "NuGetDiff.Core.Models.PackageDescriptor");

        Assert.True(result.IsOk, $"expected success, got error: {result.Err?.Message}");
        var cs = result.Ok!.CSharp;
        Assert.Contains("PackageDescriptor", cs);
        // Per-type decompile should NOT include unrelated types from the assembly.
        Assert.DoesNotContain("class Decompiler", cs);
    }

    [Fact]
    public void Returns_error_for_unknown_type()
    {
        var realAsm = TestPackageBuilder.LoadCoreAssemblyBytes();
        var bytes = TestPackageBuilder.Build(
            "Real",
            "1.0.0",
            new[] { new TestPackageBuilder.FileSpec("lib/net8.0/NuGetDiff.Core.dll", realAsm) });

        using var reader = new PackageReader(new MemoryStream(bytes));
        var result = new Decompiler().DecompileTypeFromPackage(
            reader,
            "lib/net8.0/NuGetDiff.Core.dll",
            "Does.Not.Exist.Type");

        Assert.False(result.IsOk);
        Assert.Contains("not found", result.Err!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Single_type_decompilation_honors_cancellation()
    {
        var realAssembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        var bytes = TestPackageBuilder.Build(
            "Real",
            "1.0.0",
            new[]
            {
                new TestPackageBuilder.FileSpec(
                    "lib/net8.0/NuGetDiff.Core.dll",
                    realAssembly),
            });
        using var reader = new PackageReader(new MemoryStream(bytes));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new Decompiler().DecompileTypeFromPackage(
                reader,
                "lib/net8.0/NuGetDiff.Core.dll",
                "NuGetDiff.Core.Models.PackageDescriptor",
                cts.Token));
    }

    [Fact]
    public void Batch_decompiles_multiple_types_from_one_assembly()
    {
        var realAssembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        var bytes = TestPackageBuilder.Build(
            "Real",
            "1.0.0",
            new[]
            {
                new TestPackageBuilder.FileSpec(
                    "lib/net8.0/NuGetDiff.Core.dll",
                    realAssembly),
            });
        using var reader = new PackageReader(new MemoryStream(bytes));

        var results = new Decompiler().DecompileTypesFromPackage(
            reader,
            "lib/net8.0/NuGetDiff.Core.dll",
            new[]
            {
                "NuGetDiff.Core.Models.PackageDescriptor",
                "NuGetDiff.Core.Models.FileEntry",
            });

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Message));
        Assert.Contains("PackageDescriptor", results[0].Result!.CSharp);
        Assert.Contains("FileEntry", results[1].Result!.CSharp);
    }

    [Fact]
    public void Batch_decompilation_honors_cancellation()
    {
        var realAssembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        var bytes = TestPackageBuilder.Build(
            "Real",
            "1.0.0",
            new[]
            {
                new TestPackageBuilder.FileSpec(
                    "lib/net8.0/NuGetDiff.Core.dll",
                    realAssembly),
            });
        using var reader = new PackageReader(new MemoryStream(bytes));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new Decompiler().DecompileTypesFromPackage(
                reader,
                "lib/net8.0/NuGetDiff.Core.dll",
                new[] { "NuGetDiff.Core.Models.PackageDescriptor" },
                cts.Token));
    }

    [Fact]
    public async Task Batch_decompilation_maps_delegates_and_classes_by_symbol()
    {
        var testAssembly = TestPackageBuilder.LoadAssemblyBytes(typeof(BatchDelegateFixture));
        var bytes = TestPackageBuilder.Build(
            "Fixtures",
            "1.0.0",
            new[]
            {
                new TestPackageBuilder.FileSpec("lib/net8.0/Fixtures.dll", testAssembly),
            });
        using var reader = new PackageReader(new MemoryStream(bytes));

        var results = new Decompiler().DecompileTypesFromPackage(
            reader,
            "lib/net8.0/Fixtures.dll",
            new[]
            {
                "NuGetDiff.Core.Tests.BatchDelegateFixture",
                "NuGetDiff.Core.Tests.BatchClassFixture",
            });

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Message));
        Assert.Contains("delegate", results[0].Result!.CSharp, StringComparison.Ordinal);
        Assert.Contains("BatchClassFixture", results[1].Result!.CSharp, StringComparison.Ordinal);

        var fingerprint = Assert.Single(
            await new Decompiler().FingerprintTypesFromPackageAsync(
                reader,
                "lib/net8.0/Fixtures.dll",
                new[] { "NuGetDiff.Core.Tests.BatchClassFixture" }));
        Assert.True(fingerprint.IsSuccess, fingerprint.Error?.Message);
        Assert.Contains("I_DEADBEEF", fingerprint.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_tries_all_same_named_assembly_candidates()
    {
        var coreAssembly = TestPackageBuilder.LoadCoreAssemblyBytes();
        var packagingAssembly = TestPackageBuilder.LoadAssemblyBytes(
            typeof(NuGet.Packaging.PackageArchiveReader));
        var wrongAssembly = TestPackageBuilder.LoadAssemblyBytes(
            typeof(NuGet.Versioning.NuGetVersion));
        var bytes = TestPackageBuilder.Build(
            "Resolver",
            "1.0.0",
            new[]
            {
                new TestPackageBuilder.FileSpec("lib/net8.0/NuGetDiff.Core.dll", coreAssembly),
                new TestPackageBuilder.FileSpec("a/NuGet.Packaging.dll", wrongAssembly),
                new TestPackageBuilder.FileSpec("z/NuGet.Packaging.dll", packagingAssembly),
            });
        using var reader = new PackageReader(new MemoryStream(bytes));
        using var main = new PEFile(
            "NuGetDiff.Core.dll",
            new MemoryStream(coreAssembly, writable: false));
        var reference = Assert.Single(
            main.AssemblyReferences,
            candidate => candidate.Name == "NuGet.Packaging");
        using var resolver = new PackageAssemblyResolver(reader, "missing");

        var resolved = resolver.Resolve(reference);

        Assert.NotNull(resolved);
        Assert.Equal("NuGet.Packaging", resolved!.Name);

        using var retargetableResolver = new PackageAssemblyResolver(reader, "missing");
        var retargeted = retargetableResolver.Resolve(new RetargetableReference(
            "NuGet.Packaging",
            "NuGet.Packaging, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null, Retargetable=Yes"));
        Assert.NotNull(retargeted);
    }
}

internal sealed class RetargetableReference : IAssemblyReference
{
    public RetargetableReference(string name, string fullName)
    {
        Name = name;
        FullName = fullName;
    }

    public string Name { get; }
    public string FullName { get; }
    public Version? Version => null;
    public string? Culture => null;
    public byte[]? PublicKeyToken => null;
    public bool IsWindowsRuntime => false;
    public bool IsRetargetable => true;
}

public delegate void BatchDelegateFixture();

public sealed class BatchClassFixture
{
    public const string RvaLikeString = "I_DEADBEEF";
}
