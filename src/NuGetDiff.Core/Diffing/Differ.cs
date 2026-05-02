using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using NuGetDiff.Core.Decompilation;
using NuGetDiff.Core.Models;
using NuGetDiff.Core.Packages;
using NuGetDiff.Core.Util;

namespace NuGetDiff.Core.Diffing;

public sealed class Differ
{
    private readonly Decompiler _decompiler;
    private readonly SideBySideDiffBuilder _diff;

    public Differ(Decompiler? decompiler = null)
    {
        _decompiler = decompiler ?? new Decompiler();
        _diff = new SideBySideDiffBuilder(new DiffPlex.Differ());
    }

    /// <summary>
    /// Diffs the file trees of two packages. Files matched by path are inspected for
    /// identical content via SHA256; files that exist on only one side are matched by
    /// SHA256 across the rest of the tree (rename detection).
    /// </summary>
    public TreeDiff DiffTree(PackageReader oldPkg, PackageReader newPkg)
    {
        var oldByPath = oldPkg.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var newByPath = newPkg.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        var changes = new List<FileChange>();
        var seenInOld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Hashes are computed lazily and only when needed.
        var oldHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var newHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, newEntry) in newByPath)
        {
            if (oldByPath.TryGetValue(path, out var oldEntry))
            {
                seenInOld.Add(path);
                var oh = HashOf(oldPkg, path, oldHashes);
                var nh = HashOf(newPkg, path, newHashes);
                if (string.Equals(oh, nh, StringComparison.Ordinal))
                {
                    changes.Add(new FileChange(FileChangeKind.Unchanged, oldEntry, newEntry));
                }
                else
                {
                    changes.Add(new FileChange(FileChangeKind.Modified, oldEntry, newEntry));
                }
            }
            else
            {
                changes.Add(new FileChange(FileChangeKind.Added, null, newEntry));
            }
        }

        foreach (var (path, oldEntry) in oldByPath)
        {
            if (!seenInOld.Contains(path))
            {
                changes.Add(new FileChange(FileChangeKind.Removed, oldEntry, null));
            }
        }

        // Rename detection: for each Added entry, see if any Removed has the same hash.
        var added = changes.Where(c => c.Kind == FileChangeKind.Added).ToList();
        var removed = changes.Where(c => c.Kind == FileChangeKind.Removed).ToList();
        if (added.Count > 0 && removed.Count > 0)
        {
            // Compute hashes only for the candidate set.
            foreach (var a in added)
            {
                _ = HashOf(newPkg, a.New!.Path, newHashes);
            }
            foreach (var r in removed)
            {
                _ = HashOf(oldPkg, r.Old!.Path, oldHashes);
            }

            var removedByHash = removed.ToLookup(r => oldHashes[r.Old!.Path], StringComparer.Ordinal);
            foreach (var a in added)
            {
                var hash = newHashes[a.New!.Path];
                var match = removedByHash[hash].FirstOrDefault();
                if (match is not null)
                {
                    changes.Remove(a);
                    changes.Remove(match);
                    changes.Add(new FileChange(FileChangeKind.Renamed, match.Old, a.New));
                }
            }
        }

        return new TreeDiff(changes
            .OrderBy(c => SortKey(c.Kind))
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    /// <summary>
    /// Diffs the contents of a single file path across the two packages. Dispatches by
    /// classifier: text → DiffPlex; assembly → decompile both and DiffPlex on the C#;
    /// binary → size + hash comparison only.
    /// </summary>
    public FileDiff DiffFile(PackageReader oldPkg, PackageReader newPkg, string path)
    {
        var oldExists = oldPkg.ContainsFile(path);
        var newExists = newPkg.ContainsFile(path);
        if (!oldExists && !newExists)
        {
            return new FileDiff(FileDiffKind.Unsupported, null, null, null, null, null, "File not found in either version.");
        }

        var oldEntry = oldExists ? oldPkg.Files.First(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) : null;
        var newEntry = newExists ? newPkg.Files.First(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) : null;
        var kind = (newEntry?.Kind ?? oldEntry?.Kind ?? FileKind.Binary);

        var oldBytes = oldExists ? oldPkg.ReadFileBytes(path) : Array.Empty<byte>();
        var newBytes = newExists ? newPkg.ReadFileBytes(path) : Array.Empty<byte>();
        var oldHash = oldExists ? HashUtil.Sha256Hex(oldBytes) : null;
        var newHash = newExists ? HashUtil.Sha256Hex(newBytes) : null;

        if (kind == FileKind.Text || kind == FileKind.Markdown)
        {
            var oldText = oldExists ? DecodeText(oldBytes) : string.Empty;
            var newText = newExists ? DecodeText(newBytes) : string.Empty;
            var sbs = BuildSideBySide(oldText, newText);
            return new FileDiff(FileDiffKind.Text, sbs, oldHash, newHash, oldEntry?.Length, newEntry?.Length, null);
        }

        if (kind == FileKind.Assembly)
        {
            string? oldCs = null;
            string? newCs = null;
            string? message = null;

            if (oldExists)
            {
                var r = _decompiler.DecompileFromPackage(oldPkg, path);
                if (r.IsOk) { oldCs = r.Ok!.CSharp; }
                else { message = "Old: " + r.Err!.Message; }
            }
            if (newExists)
            {
                var r = _decompiler.DecompileFromPackage(newPkg, path);
                if (r.IsOk) { newCs = r.Ok!.CSharp; }
                else { message = ((message is null) ? "New: " : message + " | New: ") + r.Err!.Message; }
            }

            if (oldCs is null && newCs is null)
            {
                return new FileDiff(
                    FileDiffKind.Assembly,
                    null,
                    oldHash, newHash, oldEntry?.Length, newEntry?.Length,
                    message ?? "Could not decompile either side.");
            }

            var sbs = BuildSideBySide(oldCs ?? string.Empty, newCs ?? string.Empty);
            return new FileDiff(FileDiffKind.Assembly, sbs, oldHash, newHash, oldEntry?.Length, newEntry?.Length, message);
        }

        // Binary: just report sizes + hashes; no inline diff.
        return new FileDiff(
            FileDiffKind.Binary,
            null,
            oldHash, newHash,
            oldEntry?.Length, newEntry?.Length,
            string.Equals(oldHash, newHash, StringComparison.Ordinal) ? "Identical." : "Binary differs.");
    }

    /// <summary>
    /// Diffs a single named type within an assembly across two packages. Either side
    /// may be missing (returns the present side as text-only) or fail to decompile (returns
    /// a <see cref="FileDiffKind.Assembly"/> diff with a populated <c>Message</c>).
    /// </summary>
    public FileDiff DiffType(PackageReader oldPkg, PackageReader newPkg, string assemblyPath, string typeReflectionName)
    {
        var oldExists = oldPkg.ContainsFile(assemblyPath);
        var newExists = newPkg.ContainsFile(assemblyPath);
        if (!oldExists && !newExists)
        {
            return new FileDiff(FileDiffKind.Unsupported, null, null, null, null, null,
                $"Assembly '{assemblyPath}' not found in either version.");
        }

        var oldEntry = oldExists ? oldPkg.Files.First(f => string.Equals(f.Path, assemblyPath, StringComparison.OrdinalIgnoreCase)) : null;
        var newEntry = newExists ? newPkg.Files.First(f => string.Equals(f.Path, assemblyPath, StringComparison.OrdinalIgnoreCase)) : null;

        string? oldCs = null;
        string? newCs = null;
        string? message = null;

        if (oldExists)
        {
            var r = _decompiler.DecompileTypeFromPackage(oldPkg, assemblyPath, typeReflectionName);
            if (r.IsOk) { oldCs = r.Ok!.CSharp; }
            else { message = "Old: " + r.Err!.Message; }
        }
        if (newExists)
        {
            var r = _decompiler.DecompileTypeFromPackage(newPkg, assemblyPath, typeReflectionName);
            if (r.IsOk) { newCs = r.Ok!.CSharp; }
            else { message = ((message is null) ? "New: " : message + " | New: ") + r.Err!.Message; }
        }

        if (oldCs is null && newCs is null)
        {
            return new FileDiff(
                FileDiffKind.Assembly, null, null, null, oldEntry?.Length, newEntry?.Length,
                message ?? $"Could not decompile '{typeReflectionName}' on either side.");
        }

        var sbs = BuildSideBySide(oldCs ?? string.Empty, newCs ?? string.Empty);
        return new FileDiff(FileDiffKind.Assembly, sbs, null, null, oldEntry?.Length, newEntry?.Length, message);
    }

    private SideBySideDiff BuildSideBySide(string oldText, string newText)
    {
        var model = _diff.BuildDiffModel(oldText, newText, ignoreWhitespace: false);
        var oldLines = ConvertPane(model.OldText.Lines);
        var newLines = ConvertPane(model.NewText.Lines);
        return new SideBySideDiff(oldLines, newLines);
    }

    private static IReadOnlyList<DiffLine> ConvertPane(IReadOnlyList<DiffPiece> pane)
    {
        var result = new List<DiffLine>(pane.Count);
        foreach (var p in pane)
        {
            var kind = p.Type switch
            {
                ChangeType.Inserted => DiffLineKind.Inserted,
                ChangeType.Deleted => DiffLineKind.Deleted,
                ChangeType.Modified => DiffLineKind.Modified,
                ChangeType.Imaginary => DiffLineKind.Imaginary,
                _ => DiffLineKind.Equal,
            };
            result.Add(new DiffLine(
                OldNumber: p.Position,
                NewNumber: p.Position,
                Kind: kind,
                Text: p.Text ?? string.Empty));
        }
        return result;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        try
        {
            // Strict UTF-8 first, fall back to Latin-1 on failure to avoid corrupting display.
            var enc = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return enc.GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.Encoding.Latin1.GetString(bytes);
        }
    }

    private string HashOf(PackageReader pkg, string path, Dictionary<string, string> cache)
    {
        if (cache.TryGetValue(path, out var h))
        {
            return h;
        }
        var bytes = pkg.ReadFileBytes(path);
        h = HashUtil.Sha256Hex(bytes);
        cache[path] = h;
        return h;
    }

    private static int SortKey(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => 0,
        FileChangeKind.Removed => 1,
        FileChangeKind.Modified => 2,
        FileChangeKind.Renamed => 3,
        FileChangeKind.Unchanged => 4,
        _ => 5,
    };
}
