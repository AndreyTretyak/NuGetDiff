using System.Security.Cryptography;

namespace NuGetDiff.Core.Util;

public static class HashUtil
{
    public static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(Stream stream)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct = default)
    {
        if (stream is MemoryStream ms)
        {
            return ms.ToArray();
        }

        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct).ConfigureAwait(false);
        return copy.ToArray();
    }
}
