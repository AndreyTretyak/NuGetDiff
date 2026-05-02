using System.IO.Compression;
using System.Text;

namespace NuGetDiff.Core.Tests;

/// <summary>
/// Builds in-memory .nupkg byte arrays for tests. .nupkg is just a zip with a .nuspec
/// at the root, so we hand-roll one rather than pulling in PackageBuilder.
/// </summary>
internal static class TestPackageBuilder
{
    public sealed record FileSpec(string Path, byte[] Bytes);

    public static byte[] Build(
        string id,
        string version,
        IEnumerable<FileSpec> files,
        string? description = null,
        string? authors = null,
        IEnumerable<(string Tfm, IEnumerable<(string Id, string VersionRange)> Deps)>? dependencyGroups = null)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nuspec = BuildNuspec(id, version, description ?? $"{id} test fixture", authors ?? "test", dependencyGroups);
            WriteEntry(archive, $"{id}.nuspec", Encoding.UTF8.GetBytes(nuspec));

            foreach (var f in files)
            {
                WriteEntry(archive, f.Path, f.Bytes);
            }
        }
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] data)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var s = entry.Open();
        s.Write(data, 0, data.Length);
    }

    private static string BuildNuspec(
        string id,
        string version,
        string description,
        string authors,
        IEnumerable<(string Tfm, IEnumerable<(string Id, string VersionRange)> Deps)>? dependencyGroups)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">");
        sb.AppendLine("  <metadata>");
        sb.AppendLine($"    <id>{Escape(id)}</id>");
        sb.AppendLine($"    <version>{Escape(version)}</version>");
        sb.AppendLine($"    <description>{Escape(description)}</description>");
        sb.AppendLine($"    <authors>{Escape(authors)}</authors>");

        if (dependencyGroups is not null)
        {
            sb.AppendLine("    <dependencies>");
            foreach (var g in dependencyGroups)
            {
                sb.AppendLine($"      <group targetFramework=\"{Escape(g.Tfm)}\">");
                foreach (var d in g.Deps)
                {
                    sb.AppendLine($"        <dependency id=\"{Escape(d.Id)}\" version=\"{Escape(d.VersionRange)}\" />");
                }
                sb.AppendLine("      </group>");
            }
            sb.AppendLine("    </dependencies>");
        }

        sb.AppendLine("  </metadata>");
        sb.AppendLine("</package>");
        return sb.ToString();
    }

    private static string Escape(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    /// <summary>
    /// Returns the bytes of the loaded NuGetDiff.Core.dll on disk so tests can use
    /// a real managed assembly without checking binaries into git.
    /// </summary>
    public static byte[] LoadCoreAssemblyBytes()
    {
        var location = typeof(NuGetDiff.Core.Models.PackageDescriptor).Assembly.Location;
        if (string.IsNullOrEmpty(location))
        {
            throw new InvalidOperationException("Could not locate NuGetDiff.Core.dll — Assembly.Location is empty.");
        }
        return File.ReadAllBytes(location);
    }
}
