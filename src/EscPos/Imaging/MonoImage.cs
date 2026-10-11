namespace EscPos.Imaging;

/// <summary>
/// A black and white image ready for the printer. Thermal printers only print black dots, so
/// colour and grey images are converted with a brightness threshold.
/// </summary>
public sealed class MonoImage
{
    private readonly bool[] _pixels; // true = black dot

    public MonoImage(int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "Image must be at least 1×1");
        Width = width;
        Height = height;
        _pixels = new bool[width * height];
    }

    public int Width { get; }
    public int Height { get; }

    public bool this[int x, int y]
    {
        get => _pixels[y * Width + x];
        set => _pixels[y * Width + x] = value;
    }

    /// <summary>
    /// Builds an image from 32 bit pixels in RGBA order (as most image libraries expose them).
    /// Transparent pixels are treated as white.
    /// </summary>
    public static MonoImage FromRgba(ReadOnlySpan<byte> rgba, int width, int height, int threshold = 128)
    {
        if (rgba.Length < width * height * 4) throw new ArgumentException("Not enough pixel data for the size given");
        var image = new MonoImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                image[x, y] = rgba[i + 3] >= 128 && Luminance(rgba[i], rgba[i + 1], rgba[i + 2]) < threshold;
            }
        }
        return image;
    }

    /// <summary>
    /// Loads an uncompressed Windows BMP (1, 24 or 32 bits per pixel), the format any image editor
    /// can save. Good enough for logos without pulling in an imaging library.
    /// </summary>
    public static MonoImage FromBmp(Stream stream, int threshold = 128)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (reader.ReadUInt16() != 0x4D42) throw new InvalidDataException("Not a BMP file");
        reader.ReadUInt32(); // file size
        reader.ReadUInt32(); // reserved
        var dataOffset = reader.ReadUInt32();

        var headerSize = reader.ReadUInt32();
        if (headerSize < 40) throw new InvalidDataException("Old OS/2 BMP headers are not supported");
        var width = reader.ReadInt32();
        var rawHeight = reader.ReadInt32();
        reader.ReadUInt16(); // planes
        var bits = reader.ReadUInt16();
        var compression = reader.ReadUInt32();
        if (compression is not (0 or 3)) throw new InvalidDataException("Compressed BMP files are not supported");
        if (bits is not (1 or 24 or 32)) throw new InvalidDataException($"{bits} bit BMP files are not supported; save as 24 bit");

        var bottomUp = rawHeight > 0;
        var height = Math.Abs(rawHeight);

        // 1 bit images have a two entry palette right after the header.
        var paletteDark = new bool[2];
        if (bits == 1)
        {
            stream.Position = 14 + headerSize;
            for (var i = 0; i < 2; i++)
            {
                var b = reader.ReadByte();
                var g = reader.ReadByte();
                var r = reader.ReadByte();
                reader.ReadByte();
                paletteDark[i] = Luminance(r, g, b) < threshold;
            }
        }

        var rowSize = ((bits * width + 31) / 32) * 4;
        var row = new byte[rowSize];
        var image = new MonoImage(width, height);
        stream.Position = dataOffset;

        for (var fileRow = 0; fileRow < height; fileRow++)
        {
            stream.ReadExactly(row);
            var y = bottomUp ? height - 1 - fileRow : fileRow;
            for (var x = 0; x < width; x++)
            {
                image[x, y] = bits switch
                {
                    1 => paletteDark[(row[x / 8] >> (7 - x % 8)) & 1],
                    24 => Luminance(row[x * 3 + 2], row[x * 3 + 1], row[x * 3]) < threshold,
                    // Plain 32 bit BMPs often leave alpha at 0, so alpha only counts when the file declares channels.
                    _ => (compression == 0 || row[x * 4 + 3] >= 128) && Luminance(row[x * 4 + 2], row[x * 4 + 1], row[x * 4]) < threshold,
                };
            }
        }
        return image;
    }

    public static MonoImage FromBmp(string path, int threshold = 128)
    {
        using var file = File.OpenRead(path);
        return FromBmp(file, threshold);
    }

    /// <summary>Scales to a new width (nearest neighbour), keeping the aspect ratio. Use it to fit the paper.</summary>
    public MonoImage ResizeToWidth(int width)
    {
        if (width == Width) return this;
        var height = Math.Max(1, (int)Math.Round(Height * (double)width / Width));
        var result = new MonoImage(width, height);
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Min(Height - 1, y * Height / height);
            for (var x = 0; x < width; x++)
            {
                result[x, y] = this[Math.Min(Width - 1, x * Width / width), sy];
            }
        }
        return result;
    }

    /// <summary>
    /// Packs the image into GS v 0 raster bands: 8 pixels per byte, most significant bit first.
    /// Large images are split into bands because many printers have a small receive buffer.
    /// </summary>
    public byte[] ToRasterCommands(int bandHeight = 128)
    {
        var bytesPerRow = (Width + 7) / 8;
        using var output = new MemoryStream();

        for (var top = 0; top < Height; top += bandHeight)
        {
            var rows = Math.Min(bandHeight, Height - top);
            output.Write([Commands.GS, (byte)'v', (byte)'0', 0,
                (byte)(bytesPerRow & 0xFF), (byte)(bytesPerRow >> 8),
                (byte)(rows & 0xFF), (byte)(rows >> 8)]);

            for (var y = top; y < top + rows; y++)
            {
                for (var bx = 0; bx < bytesPerRow; bx++)
                {
                    byte b = 0;
                    for (var bit = 0; bit < 8; bit++)
                    {
                        var x = bx * 8 + bit;
                        if (x < Width && this[x, y]) b |= (byte)(0x80 >> bit);
                    }
                    output.WriteByte(b);
                }
            }
        }
        return output.ToArray();
    }

    private static int Luminance(byte r, byte g, byte b) => (r * 299 + g * 587 + b * 114) / 1000;
}
