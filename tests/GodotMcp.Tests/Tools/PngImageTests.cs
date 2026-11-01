using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>PngImage: the server's own codec for 8-bit RGB and RGBA PNGs that are not interlaced, and what it refuses.</summary>
public sealed class PngImageTests
{
    private const string Refusal = "crop_screenshot reads only 8-bit RGB or RGBA PNGs that are not interlaced";
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnImageTheEncoderWritesReadsBackPixelForPixel(bool hasAlpha)
    {
        int channels = hasAlpha ? 4 : 3;
        byte[] pixels = new byte[5 * 3 * channels];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 37) + 11);
        }

        var read = PngImage.Decode(new PngImage(5, 3, hasAlpha, pixels).Encode(), "shot.png");

        Assert.Equal((5, 3, hasAlpha), (read.Width, read.Height, read.HasAlpha));
        Assert.Equal(pixels, read.Pixels);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APngWithEveryRowFilterInUseDecodesToItsPixels(bool hasAlpha)
    {
        int channels = hasAlpha ? 4 : 3;
        const int Width = 3;
        byte[][] rows = [.. Enumerable.Range(0, 5).Select(y => Row(y, Width * channels))];
        byte[] raw = [.. rows.SelectMany((row, y) => Filter(y, row, y == 0 ? new byte[row.Length] : rows[y - 1], channels))];

        var read = PngImage.Decode(Png(Header(Width, rows.Length, 8, (byte)(hasAlpha ? 6 : 2), 0), raw), "filtered.png");

        Assert.Equal((Width, rows.Length, hasAlpha), (read.Width, read.Height, read.HasAlpha));
        Assert.Equal(rows.SelectMany(row => row), read.Pixels);
    }

    [Theory]
    [InlineData(16, 6, 0)]
    [InlineData(8, 3, 0)]
    [InlineData(8, 6, 1)]
    public void APngGodotDoesNotSaveIsRefused(byte bitDepth, byte colourType, byte interlace)
    {
        byte[] png = Png(Header(2, 2, bitDepth, colourType, interlace), new byte[2 * (1 + 16)]);

        McpException refused = Assert.Throws<McpException>(() => PngImage.Decode(png, "odd.png"));

        Assert.StartsWith(Refusal, refused.Message, StringComparison.Ordinal);
        Assert.Contains("odd.png is ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APngWithABadCrcIsRefused()
    {
        byte[] png = new PngImage(2, 2, true, new byte[16]).Encode();
        png[8 + 8 + 13] ^= 0xFF;

        McpException refused = Assert.Throws<McpException>(() => PngImage.Decode(png, "bent.png"));

        Assert.StartsWith(Refusal, refused.Message, StringComparison.Ordinal);
        Assert.Contains("CRC", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2147483647)]
    [InlineData(23170)]
    public void APngLargerThanGodotsEdgeIsRefusedBeforeAnyPixelIsRead(int edge)
    {
        byte[] png = Chunked(("IHDR", Header(edge, edge, 8, 6, 0)), ("IDAT", Deflate([])), ("IEND", []));

        McpException refused = Assert.Throws<McpException>(() => PngImage.Decode(png, "huge.png"));

        Assert.Equal($"{Refusal}, as Godot saves them; huge.png is a PNG of {edge}x{edge}, larger than Godot's 16384 px edge", refused.Message);
    }

    [Theory]
    [InlineData("signature", "not a PNG")]
    [InlineData("cut short", "a PNG cut short before its IEND chunk")]
    [InlineData("chunk past the end", "a PNG cut short inside a chunk")]
    [InlineData("first chunk", "a PNG whose first chunk is IDAT, not IHDR")]
    [InlineData("IHDR length", "a PNG whose IHDR chunk holds 12 bytes, not 13")]
    [InlineData("two IHDR", "a PNG with two IHDR chunks")]
    [InlineData("interlace method", "a PNG of unknown interlace method 2")]
    [InlineData("filter", "a damaged PNG: row 0 has filter type 5")]
    [InlineData("too little data", "a damaged PNG: its image data cannot be inflated to its size")]
    [InlineData("too much data", "a damaged PNG: its image data holds more than its size needs")]
    [InlineData("no IDAT", "a damaged PNG: its image data cannot be inflated to its size")]
    public void AMalformedPngIsRefusedSayingWhatItIs(string kind, string what)
    {
        McpException refused = Assert.Throws<McpException>(() => PngImage.Decode(Malformed[kind](), "bad.png"));

        Assert.StartsWith($"{Refusal}, as Godot saves them; bad.png is {what}", refused.Message, StringComparison.Ordinal);
    }

    private static readonly byte[] Rgb2x2 = Header(2, 2, 8, 2, 0);

    private static readonly Dictionary<string, Func<byte[]>> Malformed = new()
    {
        ["signature"] = () => Encoding.ASCII.GetBytes("GIF89a, not a PNG at all"),
        ["cut short"] = () => Valid()[..15],
        ["chunk past the end"] = () => WithFirstChunkLength(Valid(), 0x7FFFFFFF),
        ["first chunk"] = () => Chunked(("IDAT", Deflate(new byte[14])), ("IHDR", Rgb2x2), ("IEND", [])),
        ["IHDR length"] = () => Chunked(("IHDR", new byte[12]), ("IEND", [])),
        ["two IHDR"] = () => Chunked(("IHDR", Rgb2x2), ("IHDR", Rgb2x2), ("IDAT", Deflate(new byte[14])), ("IEND", [])),
        ["interlace method"] = () => Png(Header(2, 2, 8, 2, 2), new byte[14]),
        ["filter"] = () => Png(Header(2, 1, 8, 2, 0), [5, 0, 0, 0, 0, 0, 0]),
        ["too little data"] = () => Png(Rgb2x2, new byte[13]),
        ["too much data"] = () => Png(Rgb2x2, new byte[15]),
        ["no IDAT"] = () => Chunked(("IHDR", Rgb2x2), ("IEND", [])),
    };

    /// <summary>Row y's bytes: values that make each filter's prediction differ from the plain byte.</summary>
    private static byte[] Row(int y, int length) => [.. Enumerable.Range(0, length).Select(x => (byte)((x * 53) + (y * 101) + ((x * y) % 7)))];

    /// <summary>Row y filtered with filter type y mod 5 (None, Sub, Up, Average, Paeth), its filter byte first.</summary>
    private static byte[] Filter(int y, byte[] row, byte[] above, int bpp)
    {
        int type = y % 5;
        byte[] filtered = new byte[row.Length + 1];
        filtered[0] = (byte)type;
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0;
            int b = above[i];
            int c = i >= bpp ? above[i - bpp] : 0;
            int predicted = type switch
            {
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => 0,
            };
            filtered[i + 1] = (byte)(row[i] - predicted);
        }

        return filtered;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a
            : pb <= pc ? b
            : c;
    }

    private static byte[] Header(int width, int height, byte bitDepth, byte colourType, byte interlace)
    {
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = bitDepth;
        header[9] = colourType;
        header[12] = interlace;
        return header;
    }

    /// <summary>A PNG of the header and the raw filtered rows, the compressed data split over two IDAT chunks.</summary>
    private static byte[] Png(byte[] header, byte[] raw)
    {
        byte[] data = Deflate(raw);
        int half = data.Length / 2;
        return Chunked(("IHDR", header), ("IDAT", data[..half]), ("IDAT", data[half..]), ("IEND", []));
    }

    private static byte[] Deflate(byte[] raw)
    {
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return compressed.ToArray();
    }

    /// <summary>The signature, then each chunk as given, in order.</summary>
    private static byte[] Chunked(params (string Type, byte[] Data)[] chunks)
    {
        using MemoryStream png = new();
        png.Write(Signature);
        foreach ((string type, byte[] data) in chunks)
        {
            WriteChunk(png, type, data);
        }

        return png.ToArray();
    }

    /// <summary>A valid 2x2 RGB PNG.</summary>
    private static byte[] Valid() => Png(Rgb2x2, new byte[2 * 7]);

    private static byte[] WithFirstChunkLength(byte[] png, uint length)
    {
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(Signature.Length), length);
        return png;
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        png.Write(number);
        png.Write(typeBytes);
        png.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc([.. typeBytes, .. data]));
        png.Write(number);
    }

    /// <summary>CRC-32 (ISO 3309), bit by bit, as a second implementation to the codec's table.</summary>
    private static uint Crc(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}
