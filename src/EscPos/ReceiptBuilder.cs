using System.Text;
using EscPos.Imaging;

namespace EscPos;

/// <summary>
/// Builds the bytes for one receipt. Every method returns the builder, so a receipt reads top to bottom:
/// <code>
/// var bytes = new ReceiptBuilder(48)
///     .Align(Align.Center).Bold().Size(2, 2).Line("CAFE ZEST").Normal()
///     .Line("Shop 4, Clifton, Karachi")
///     .Separator()
///     .LeftRight("Total", "1,250.00")
///     .Cut()
///     .ToArray();
/// </code>
/// </summary>
public sealed class ReceiptBuilder
{
    static ReceiptBuilder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly Dictionary<char, string> Replacements = new()
    {
        ['‘'] = "'", ['’'] = "'", ['“'] = "\"", ['”'] = "\"",
        ['–'] = "-", ['—'] = "-", ['…'] = "...", ['•'] = "*",
        [' '] = " ", ['₨'] = "Rs", ['×'] = "x",
    };

    private readonly MemoryStream _buffer = new();
    private readonly Encoding _encoding;
    private int _widthMultiplier = 1;
    private PrinterFont _font = PrinterFont.A;

    /// <param name="charsPerLine">
    /// Characters per line in font A at normal size: 48 for 80 mm paper, 32 for 58 mm. Some 80 mm printers use 42.
    /// </param>
    /// <param name="codePage">Character table for non-ASCII text. Unsupported characters print as "?".</param>
    public ReceiptBuilder(int charsPerLine = 48, CodePage codePage = CodePage.PC437)
    {
        if (charsPerLine < 16) throw new ArgumentOutOfRangeException(nameof(charsPerLine), "At least 16");
        CharsPerLine = charsPerLine;
        CodePage = codePage;
        _encoding = Encoding.GetEncoding(DotNetCodePage(codePage), new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);

        Write(Commands.Initialize);
        if (codePage != CodePage.PC437) Write(Commands.SetCodePage(codePage));
    }

    public int CharsPerLine { get; }

    public CodePage CodePage { get; }

    /// <summary>Characters that fit on a line with the current font and width.</summary>
    public int CurrentWidth => (_font == PrinterFont.B ? FontBChars : CharsPerLine) / _widthMultiplier;

    /// <summary>Font B is 9 dots wide instead of 12, so a third more characters fit.</summary>
    private int FontBChars => CharsPerLine * 4 / 3;

    // Formatting

    public ReceiptBuilder Align(Align align) => Write(Commands.SetAlign(align));

    public ReceiptBuilder Bold(bool on = true) => Write(Commands.SetBold(on));

    public ReceiptBuilder Underline(Underline mode = EscPos.Underline.Thin) => Write(Commands.SetUnderline(mode));

    public ReceiptBuilder Inverted(bool on = true) => Write(Commands.SetInverted(on));

    public ReceiptBuilder Font(PrinterFont font)
    {
        _font = font;
        return Write(Commands.SetFont(font));
    }

    /// <summary>Character size multipliers, 1 to 8. (2, 2) is the usual "big" header.</summary>
    public ReceiptBuilder Size(int width, int height)
    {
        _widthMultiplier = width;
        return Write(Commands.SetSize(width, height));
    }

    /// <summary>Back to left aligned, normal size, font A, no bold, underline or inversion.</summary>
    public ReceiptBuilder Normal()
    {
        _widthMultiplier = 1;
        _font = PrinterFont.A;
        return Write(Commands.SetAlign(EscPos.Align.Left))
            .Write(Commands.SetSize(1, 1))
            .Write(Commands.SetFont(PrinterFont.A))
            .Write(Commands.SetBold(false))
            .Write(Commands.SetUnderline(EscPos.Underline.None))
            .Write(Commands.SetInverted(false));
    }

    // Text

    /// <summary>Text without a line break. Long text is not wrapped; prefer <see cref="Line"/>.</summary>
    public ReceiptBuilder Text(string text) => Write(Encode(text));

    /// <summary>Text wrapped to the line width, ending with a line break. Empty prints a blank line.</summary>
    public ReceiptBuilder Line(string text = "")
    {
        foreach (var line in TextLayout.Wrap(text, CurrentWidth))
        {
            Write(Encode(line)).Write([Commands.LF]);
        }
        return this;
    }

    public ReceiptBuilder Lines(IEnumerable<string> lines)
    {
        foreach (var line in lines) Line(line);
        return this;
    }

    /// <summary>"Total ............ 1,250.00". Left text wraps if needed; right text stays on the last line.</summary>
    public ReceiptBuilder LeftRight(string left, string right, char filler = ' ')
    {
        foreach (var line in TextLayout.LeftRight(left, right, CurrentWidth, filler))
        {
            Write(Encode(line)).Write([Commands.LF]);
        }
        return this;
    }

    /// <summary>A full width line of one character.</summary>
    public ReceiptBuilder Separator(char ch = '-') => Line(new string(ch, CurrentWidth));

    /// <summary>One table row. Cells wrap within their column.</summary>
    public ReceiptBuilder Row(IReadOnlyList<Column> columns, params string[] cells)
    {
        foreach (var line in TextLayout.Row(columns, cells, CurrentWidth))
        {
            Write(Encode(line)).Write([Commands.LF]);
        }
        return this;
    }

    /// <summary>Several table rows with the same columns.</summary>
    public ReceiptBuilder Rows(IReadOnlyList<Column> columns, IEnumerable<string[]> rows)
    {
        foreach (var cells in rows) Row(columns, cells);
        return this;
    }

    public ReceiptBuilder Feed(int lines = 1) => Write(Commands.FeedLines(lines));

    // Codes and images

    /// <summary>1D barcode. Code128 takes any ASCII text; EAN-13 takes 12 or 13 digits.</summary>
    public ReceiptBuilder Barcode(string data, BarcodeType type = BarcodeType.Code128, int height = 80, int moduleWidth = 2, BarcodeText text = BarcodeText.Below)
    {
        ArgumentException.ThrowIfNullOrEmpty(data);
        if (height is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(height));
        if (moduleWidth is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(moduleWidth), "1 to 6");

        var payload = type switch
        {
            BarcodeType.Code128 => Code128Payload(data),
            BarcodeType.Ean13 when !IsDigits(data, 12, 13) => throw new ArgumentException("EAN-13 needs 12 or 13 digits", nameof(data)),
            BarcodeType.Ean8 when !IsDigits(data, 7, 8) => throw new ArgumentException("EAN-8 needs 7 or 8 digits", nameof(data)),
            BarcodeType.UpcA when !IsDigits(data, 11, 12) => throw new ArgumentException("UPC-A needs 11 or 12 digits", nameof(data)),
            BarcodeType.Code39 when data.Any(c => !"0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ -.$/+%".Contains(c)) =>
                throw new ArgumentException("Code 39 allows A-Z, 0-9, space and - . $ / + %", nameof(data)),
            _ => Encoding.ASCII.GetBytes(data)
        };
        if (payload.Length > 255) throw new ArgumentException("Barcode data is too long", nameof(data));

        Write([Commands.GS, (byte)'h', (byte)height]);
        Write([Commands.GS, (byte)'w', (byte)moduleWidth]);
        Write([Commands.GS, (byte)'H', (byte)text]);
        Write([Commands.GS, (byte)'k', (byte)type, (byte)payload.Length]);
        Write(payload);
        return Write([Commands.LF]);
    }

    /// <summary>
    /// QR code printed by the printer itself (GS ( k, model 2). Module size 1 to 16 dots;
    /// 6 gives about 2.5 cm for a short text on a 203 dpi printer.
    /// </summary>
    public ReceiptBuilder QrCode(string data, int moduleSize = 6, QrErrorCorrection errorCorrection = QrErrorCorrection.M)
    {
        ArgumentException.ThrowIfNullOrEmpty(data);
        if (moduleSize is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(moduleSize), "1 to 16");

        var bytes = Encoding.UTF8.GetBytes(data);
        if (bytes.Length > 7089) throw new ArgumentException("QR data is too long", nameof(data));
        var storeLength = bytes.Length + 3;

        Write([Commands.GS, (byte)'(', (byte)'k', 4, 0, 49, 65, 50, 0]);                  // model 2
        Write([Commands.GS, (byte)'(', (byte)'k', 3, 0, 49, 67, (byte)moduleSize]);          // module size
        Write([Commands.GS, (byte)'(', (byte)'k', 3, 0, 49, 69, (byte)errorCorrection]);     // error correction
        Write([Commands.GS, (byte)'(', (byte)'k', (byte)(storeLength & 0xFF), (byte)(storeLength >> 8), 49, 80, 48]);
        Write(bytes);                                                                        // store data
        Write([Commands.GS, (byte)'(', (byte)'k', 3, 0, 49, 81, 48]);                       // print
        return Write([Commands.LF]);
    }

    /// <summary>
    /// Prints a black and white image. Wider images are scaled down to <paramref name="maxWidthDots"/>
    /// (576 dots for 80 mm paper, 384 for 58 mm at 203 dpi).
    /// </summary>
    public ReceiptBuilder Image(MonoImage image, int maxWidthDots = 576)
    {
        var fitted = image.Width > maxWidthDots ? image.ResizeToWidth(maxWidthDots) : image;
        return Write(fitted.ToRasterCommands());
    }

    // Hardware

    /// <summary>Feeds the paper past the cutter, then cuts. Partial cut leaves a small tab.</summary>
    public ReceiptBuilder Cut(bool partial = true, int feedLines = 4)
    {
        if (feedLines > 0) Write(Commands.FeedLines(feedLines));
        return Write(Commands.Cut(partial));
    }

    public ReceiptBuilder OpenDrawer(DrawerPin pin = DrawerPin.Pin2) => Write(Commands.OpenDrawer(pin));

    /// <summary>Anything this builder doesn't cover: vendor commands, logos stored in the printer, beepers.</summary>
    public ReceiptBuilder Raw(ReadOnlySpan<byte> bytes) => Write(bytes);

    public byte[] ToArray() => _buffer.ToArray();

    // Internals

    private ReceiptBuilder Write(ReadOnlySpan<byte> bytes)
    {
        _buffer.Write(bytes);
        return this;
    }

    internal byte[] Encode(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (Replacements.TryGetValue(ch, out var replacement)) sb.Append(replacement);
            else if (ch is '\r' or '\n' or '\t') sb.Append(' ');
            else if (!char.IsControl(ch)) sb.Append(ch);
        }
        return _encoding.GetBytes(sb.ToString());
    }

    internal static int DotNetCodePage(CodePage page) => page switch
    {
        CodePage.PC437 => 437,
        CodePage.PC850 => 850,
        CodePage.PC858 => 858,
        CodePage.WPC1252 => 1252,
        _ => throw new ArgumentOutOfRangeException(nameof(page))
    };

    /// <summary>Code set B: "{B" prefix, then the data.</summary>
    private static byte[] Code128Payload(string data)
    {
        if (data.Any(c => c < 32 || c > 126)) throw new ArgumentException("Code 128 data must be printable ASCII", nameof(data));
        return Encoding.ASCII.GetBytes("{B" + data.Replace("{", "{{"));
    }

    private static bool IsDigits(string data, int min, int max) =>
        data.Length >= min && data.Length <= max && data.All(char.IsAsciiDigit);
}
