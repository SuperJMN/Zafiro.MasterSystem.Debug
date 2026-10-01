using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Sms.Debug.Core;

public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static byte[] EncodeRgb24(IReadOnlyList<uint> pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        }

        if (pixels.Count != width * height)
        {
            throw new ArgumentException("Pixel count must equal width times height.", nameof(pixels));
        }

        using var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR", CreateIhdr(width, height));
        WriteChunk(png, "IDAT", Compress(CreateImageData(pixels, width, height)));
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] CreateIhdr(int width, int height)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4, 4), height);
        data[8] = 8;
        data[9] = 2;
        return data;
    }

    private static byte[] CreateImageData(IReadOnlyList<uint> pixels, int width, int height)
    {
        var rowLength = width * 3 + 1;
        var data = new byte[rowLength * height];
        var offset = 0;

        for (var y = 0; y < height; y++)
        {
            data[offset++] = 0;
            for (var x = 0; x < width; x++)
            {
                var pixel = pixels[y * width + x];
                data[offset++] = (byte)((pixel >> 16) & 0xFF);
                data[offset++] = (byte)((pixel >> 8) & 0xFF);
                data[offset++] = (byte)(pixel & 0xFF);
            }
        }

        return data;
    }

    private static byte[] Compress(byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, CalculateCrc(typeBytes, data));
        stream.Write(crc);
    }

    private static uint CalculateCrc(byte[] typeBytes, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, typeBytes);
        crc = UpdateCrc(crc, data);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint UpdateCrc(uint crc, byte[] bytes)
    {
        foreach (var value in bytes)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
