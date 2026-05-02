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
}
