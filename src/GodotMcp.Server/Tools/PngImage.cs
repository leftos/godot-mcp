using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>
/// An 8-bit RGB or RGBA image read from or written to a PNG that is not interlaced, as Godot saves screenshots: the pixels
/// row by row from the top-left, three or four bytes each.
/// </summary>
internal sealed class PngImage
{
    private const int HeaderLength = 13;
    private const byte RgbType = 2;
    private const byte RgbaType = 6;
    private const uint MaxEdge = 16384;
    private const ulong MaxPixels = 16384 * 16384;
    private const int InflateBlock = 81920;
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>An image of <paramref name="pixels"/>, row by row, three bytes a pixel, or four with alpha.</summary>
    /// <exception cref="ArgumentException">The pixels do not fill the size exactly.</exception>
    internal PngImage(int width, int height, bool hasAlpha, byte[] pixels)
    {
        Width = width;
        Height = height;
        HasAlpha = hasAlpha;
        Pixels = pixels;
        if ((long)width * height * Channels != pixels.Length)
        {
            throw new ArgumentException(
                $"a {width}x{height} image of {Channels} channels needs {(long)width * height * Channels} bytes, not {pixels.Length}"
            );
        }
    }

    internal int Width { get; }

    internal int Height { get; }

    internal bool HasAlpha { get; }

    /// <summary>The pixels, row by row from the top-left.</summary>
    internal byte[] Pixels { get; }

    internal int Channels => HasAlpha ? 4 : 3;

    /// <summary>Reads a PNG's bytes.</summary>
    /// <param name="png">The file's bytes.</param>
    /// <param name="path">The file, for the refusal.</param>
    /// <exception cref="McpException">The bytes are not an 8-bit RGB or RGBA PNG that is not interlaced, or are damaged.</exception>
    internal static PngImage Decode(byte[] png, string path)
    {
        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw Refuse(path, "not a PNG (its first bytes are not the PNG signature)");
        }

        (byte[] header, byte[] data) = ReadChunks(png, path);
        (int width, int height, bool hasAlpha) = ReadHeader(header, path);
        int channels = hasAlpha ? 4 : 3;
        int stride = width * channels;
        byte[] raw = Inflate(data, (stride + 1) * height, path);
        return new PngImage(width, height, hasAlpha, Unfilter(raw, stride, height, channels, path));
    }

    /// <summary>The image as a PNG of its own colour type: every row unfiltered, the data in one IDAT chunk.</summary>
    internal byte[] Encode()
    {
        int stride = Width * Channels;
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < Height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(Pixels, y * stride, stride);
            }
        }

        byte[] header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)Height);
        header[8] = 8;
        header[9] = HasAlpha ? RgbaType : RgbType;
        using MemoryStream png = new();
        png.Write(Signature);
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    /// <summary>
    /// The <paramref name="width"/> by <paramref name="height"/> pixels from (<paramref name="x"/>, <paramref name="y"/>),
    /// each repeated <paramref name="scale"/> times across and down.
    /// </summary>
    internal PngImage Crop(int x, int y, int width, int height, int scale)
    {
        int stride = Width * Channels;
        int rowLength = width * scale * Channels;
        byte[] pixels = new byte[rowLength * height * scale];
        byte[] row = new byte[rowLength];
        for (int sourceY = 0; sourceY < height; sourceY++)
        {
            int start = ((y + sourceY) * stride) + (x * Channels);
            for (int sourceX = 0; sourceX < width * scale; sourceX++)
            {
                Array.Copy(Pixels, start + (sourceX / scale * Channels), row, sourceX * Channels, Channels);
            }

            for (int copy = 0; copy < scale; copy++)
            {
                row.CopyTo(pixels, ((sourceY * scale) + copy) * rowLength);
            }
        }

        return new PngImage(width * scale, height * scale, HasAlpha, pixels);
    }

    private static McpException Refuse(string path, string what) =>
        new($"crop_screenshot reads only 8-bit RGB or RGBA PNGs that are not interlaced, as Godot saves them; {path} is {what}");

    /// <summary>The IHDR chunk's data and every IDAT chunk's data joined, each chunk's CRC checked, up to IEND.</summary>
    private static (byte[] Header, byte[] Data) ReadChunks(byte[] png, string path)
    {
        using MemoryStream data = new();
        byte[]? header = null;
        int offset = Signature.Length;
        while (true)
        {
            (string type, ReadOnlyMemory<byte> body) = ReadChunk(png, ref offset, path);
            if (header is null && type != "IHDR")
            {
                throw Refuse(path, $"a PNG whose first chunk is {type}, not IHDR");
            }

            switch (type)
            {
                case "IHDR" when header is not null:
                    throw Refuse(path, "a PNG with two IHDR chunks");
                case "IHDR":
                    header = body.ToArray();
                    break;
                case "IDAT":
                    data.Write(body.Span);
                    break;
                case "IEND":
                    return (header!, data.ToArray());
            }
        }
    }

    /// <summary>The chunk at <paramref name="offset"/>, its CRC checked, moving the offset past it.</summary>
    private static (string Type, ReadOnlyMemory<byte> Body) ReadChunk(byte[] png, ref int offset, string path)
    {
        if (png.Length - offset < 12)
        {
            throw Refuse(path, "a PNG cut short before its IEND chunk");
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
        if (length > png.Length - offset - 12)
        {
            throw Refuse(path, "a PNG cut short inside a chunk");
        }

        ReadOnlySpan<byte> typed = png.AsSpan(offset + 4, 4 + (int)length);
        string type = System.Text.Encoding.ASCII.GetString(typed[..4]);
        uint stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + (int)length));
        if (Crc(typed) != stored)
        {
            throw Refuse(path, $"a damaged PNG: its {type} chunk fails its CRC");
        }

        ReadOnlyMemory<byte> body = png.AsMemory(offset + 8, (int)length);
        offset += 12 + (int)length;
        return (type, body);
    }

    /// <summary>The size and colour type IHDR gives, refusing what this codec does not read.</summary>
    private static (int Width, int Height, bool HasAlpha) ReadHeader(byte[] header, string path)
    {
        if (header.Length != HeaderLength)
        {
            throw Refuse(path, $"a PNG whose IHDR chunk holds {header.Length} bytes, not {HeaderLength}");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(header);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        string? what = DescribeUnread(header) ?? DescribeSize(width, height);
        return what is null ? ((int)width, (int)height, header[9] == RgbaType) : throw Refuse(path, what);
    }

    /// <summary>
    /// What makes a size one Godot never saves (Godot 4.7.2 core/io/image.h: MAX_WIDTH, MAX_HEIGHT and MAX_PIXELS), checked
    /// before the size is multiplied; null for a size it saves.
    /// </summary>
    private static string? DescribeSize(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return $"a PNG of {width}x{height}";
        }

        bool tooLarge = width > MaxEdge || height > MaxEdge || (ulong)width * height > MaxPixels;
        return tooLarge ? $"a PNG of {width}x{height}, larger than Godot's {MaxEdge} px edge" : null;
    }

    /// <summary>What makes a header one this codec does not read; null when it reads it.</summary>
    private static string? DescribeUnread(byte[] header)
    {
        byte bitDepth = header[8];
        byte colourType = header[9];
        if (colourType is not (RgbType or RgbaType))
        {
            return $"a PNG of colour type {colourType.ToString(CultureInfo.InvariantCulture)} ({ColourTypeName(colourType)})";
        }

        if (bitDepth != 8)
        {
            return $"a {bitDepth.ToString(CultureInfo.InvariantCulture)}-bit PNG";
        }

        if (header[10] != 0 || header[11] != 0)
        {
            return "a PNG of an unknown compression or filter method";
        }

        return header[12] switch
        {
            0 => null,
            1 => "an interlaced PNG",
            _ => $"a PNG of unknown interlace method {header[12].ToString(CultureInfo.InvariantCulture)}",
        };
    }

    private static string ColourTypeName(byte colourType) =>
        colourType switch
        {
            0 => "greyscale",
            3 => "palette",
            4 => "greyscale with alpha",
            _ => "not a PNG colour type",
        };

    /// <summary>The image data inflated, which must hold exactly <paramref name="length"/> bytes.</summary>
    private static byte[] Inflate(byte[] data, int length, string path)
    {
        try
        {
            using ZLibStream zlib = new(new MemoryStream(data), CompressionMode.Decompress);
            using MemoryStream raw = new();
            byte[] block = new byte[InflateBlock];
            int read;
            while (raw.Length <= length && (read = zlib.Read(block)) > 0)
            {
                raw.Write(block, 0, read);
            }

            return raw.Length == length
                ? raw.ToArray()
                : throw Refuse(path, raw.Length > length ? "a damaged PNG: its image data holds more than its size needs" : TooLittle(raw.Length));
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            throw Refuse(path, $"a damaged PNG: its image data cannot be inflated to its size ({e.Message})");
        }
    }

    private static string TooLittle(long inflated) =>
        $"a damaged PNG: its image data cannot be inflated to its size (it holds {inflated.ToString(CultureInfo.InvariantCulture)} bytes)";

    /// <summary>The pixels of the filtered rows in <paramref name="raw"/>, each row's filter byte undone.</summary>
    private static byte[] Unfilter(byte[] raw, int stride, int height, int bpp, string path)
    {
        byte[] pixels = new byte[stride * height];
        byte[] none = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            byte filter = raw[y * (stride + 1)];
            Span<byte> row = pixels.AsSpan(y * stride, stride);
            raw.AsSpan((y * (stride + 1)) + 1, stride).CopyTo(row);
            ReadOnlySpan<byte> above = y == 0 ? none : pixels.AsSpan((y - 1) * stride, stride);
            if (filter > 4)
            {
                throw Refuse(path, $"a damaged PNG: row {y.ToString(CultureInfo.InvariantCulture)} has filter type {filter}");
            }

            UnfilterRow(filter, row, above, bpp);
        }

        return pixels;
    }

    private static void UnfilterRow(byte filter, Span<byte> row, ReadOnlySpan<byte> above, int bpp)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0;
            int c = i >= bpp ? above[i - bpp] : 0;
            row[i] += filter switch
            {
                1 => (byte)a,
                2 => above[i],
                3 => (byte)((a + above[i]) / 2),
                4 => Paeth(a, above[i], c),
                _ => 0,
            };
        }
    }

    private static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return (byte)a;
        }

        return (byte)(pb <= pc ? b : c);
    }

    private static void WriteChunk(Stream png, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        png.Write(number);
        byte[] typed = [.. type, .. data];
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(typed));
        png.Write(number);
    }

    /// <summary>The CRC-32 a PNG chunk carries over its type and data.</summary>
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in bytes)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            uint c = n;
            for (int bit = 0; bit < 8; bit++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
