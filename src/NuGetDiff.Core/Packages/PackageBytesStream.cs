namespace NuGetDiff.Core.Packages;

internal sealed class PackageBytesStream : MemoryStream
{
    public PackageBytesStream(byte[] bytes)
        : base(bytes, writable: false)
    {
        Bytes = bytes;
    }

    public byte[] Bytes { get; }
}
