using System.Buffers.Binary;
using StbImageSharp;

namespace ColoringApp.Api.Integrations;

internal static class ColoringPagePng
{
    public const int Width = 1024;
    public const int Height = 1536;
    private const int ByteLimit = 16 * 1024 * 1024;
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static void Verify(byte[] png)
    {
        if (png.Length is < 45 or > ByteLimit || !png.AsSpan(0, 8).SequenceEqual(Signature) ||
            !png.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)) != 13 ||
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) != Width ||
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) != Height ||
            !HasCompleteStaticImage(png))
        {
            throw new InvalidDataException();
        }

        var decoded = ImageResult.FromMemory(png);
        if (decoded.Width != Width || decoded.Height != Height)
        {
            throw new InvalidDataException();
        }
    }

    // The decoder stops at the first image. Require complete chunks and one static PNG.
    private static bool HasCompleteStaticImage(ReadOnlySpan<byte> png)
    {
        var offset = Signature.Length;
        var hasImageData = false;
        while (png.Length - offset >= 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            if (length > png.Length - offset - 12)
            {
                return false;
            }

            var chunk = png.Slice(offset + 4, (int)length + 4);
            offset += (int)length + 12;
            if (!HasValidChecksum(chunk, BinaryPrimitives.ReadUInt32BigEndian(png[(offset - 4)..])))
            {
                return false;
            }

            var type = chunk[..4];
            if (type.SequenceEqual("acTL"u8))
            {
                return false;
            }
            if (type.SequenceEqual("IDAT"u8))
            {
                hasImageData = true;
            }
            if (type.SequenceEqual("IEND"u8))
            {
                return hasImageData && length == 0 && offset == png.Length;
            }
        }
        return false;
    }

    private static bool HasValidChecksum(ReadOnlySpan<byte> chunk, uint expected)
    {
        var checksum = uint.MaxValue;
        foreach (var value in chunk)
        {
            checksum ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                checksum = (checksum & 1) == 0 ? checksum >> 1 : (checksum >> 1) ^ 0xedb88320u;
            }
        }
        return ~checksum == expected;
    }
}
