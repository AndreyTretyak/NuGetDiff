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
}
