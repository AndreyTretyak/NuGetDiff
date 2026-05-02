using NuGetDiff.Core.Diffing;
using NuGetDiff.Core.Models;
using Xunit;

namespace NuGetDiff.Core.Tests;

public class FileClassifierTests
{
    [Theory]
    [InlineData("readme.md", FileKind.Markdown)]
    [InlineData("docs/notes.markdown", FileKind.Markdown)]
    [InlineData("LICENSE.txt", FileKind.Text)]
    [InlineData("config.json", FileKind.Text)]
    [InlineData("build.props", FileKind.Text)]
    [InlineData("lib/net8.0/Foo.dll", FileKind.Assembly)]
    [InlineData("lib/net8.0/Foo.exe", FileKind.Assembly)]
    [InlineData("runtimes/win-x64/native/native.dll", FileKind.Binary)]
    [InlineData("runtimes/linux-x64/native/libfoo.so.dll", FileKind.Binary)]
    [InlineData("content/image.png", FileKind.Binary)]
    public void ClassifyByPath(string path, FileKind expected)
    {
        Assert.Equal(expected, FileClassifier.ClassifyByPath(path));
    }

    [Fact]
    public void IsManagedAssembly_returns_true_for_real_dll()
    {
        var bytes = TestPackageBuilder.LoadCoreAssemblyBytes();
        using var ms = new MemoryStream(bytes);
        Assert.True(FileClassifier.IsManagedAssembly(ms));
    }

    [Fact]
    public void IsManagedAssembly_returns_false_for_non_pe()
    {
        using var ms = new MemoryStream(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 });
        Assert.False(FileClassifier.IsManagedAssembly(ms));
    }
}
