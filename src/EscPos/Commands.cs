namespace EscPos;

public enum Align : byte
{
    Left = 0,
    Center = 1,
    Right = 2
}

public enum Underline : byte
{
    None = 0,
    Thin = 1,
    Thick = 2
}

public enum PrinterFont : byte
{
    /// <summary>Standard font, 12×24 dots on most printers.</summary>
    A = 0,
    /// <summary>Smaller font, 9×17 dots. Fits more characters per line.</summary>
    B = 1
}

public enum BarcodeType : byte
{
    UpcA = 65,
    UpcE = 66,
    Ean13 = 67,
    Ean8 = 68,
    Code39 = 69,
    Itf = 70,
    Codabar = 71,
    Code93 = 72,
    Code128 = 73
}

public enum BarcodeText : byte
{
    None = 0,
    Above = 1,
    Below = 2,
    Both = 3
}

public enum QrErrorCorrection : byte
{
    /// <summary>Recovers about 7% damage.</summary>
    L = 48,
    /// <summary>Recovers about 15% damage. Good default for receipts.</summary>
    M = 49,
    /// <summary>Recovers about 25% damage.</summary>
    Q = 50,
    /// <summary>Recovers about 30% damage.</summary>
    H = 51
}

/// <summary>Drawer kick-out connector pin. Most cash drawers use pin 2.</summary>
public enum DrawerPin : byte
{
    Pin2 = 0,
    Pin5 = 1
}

/// <summary>
/// Character tables (ESC t n). Numbers follow the Epson table; most compatible printers use the same.
/// </summary>
public enum CodePage : byte
{
    /// <summary>PC437, USA and standard Europe. Printer default.</summary>
    PC437 = 0,
    PC850 = 2,
    PC858 = 19,
    /// <summary>Windows-1252, Western European with curly quotes and the euro sign.</summary>
    WPC1252 = 16
}

/// <summary>Raw ESC/POS byte sequences.</summary>
public static class Commands
{
    public const byte ESC = 0x1B;
    public const byte GS = 0x1D;
    public const byte DLE = 0x10;
    public const byte EOT = 0x04;
    public const byte LF = 0x0A;

    public static readonly byte[] Initialize = [ESC, (byte)'@'];

    public static byte[] SetAlign(Align align) => [ESC, (byte)'a', (byte)align];
    public static byte[] SetBold(bool on) => [ESC, (byte)'E', on ? (byte)1 : (byte)0];
    public static byte[] SetUnderline(Underline mode) => [ESC, (byte)'-', (byte)mode];
    public static byte[] SetFont(PrinterFont font) => [ESC, (byte)'M', (byte)font];
    public static byte[] SetInverted(bool on) => [GS, (byte)'B', on ? (byte)1 : (byte)0];
    public static byte[] SetCodePage(CodePage page) => [ESC, (byte)'t', (byte)page];

    /// <summary>GS ! n: width and height multipliers from 1 to 8.</summary>
    public static byte[] SetSize(int width, int height)
    {
        if (width is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(width), "1 to 8");
        if (height is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(height), "1 to 8");
        return [GS, (byte)'!', (byte)(((width - 1) << 4) | (height - 1))];
    }

    public static byte[] FeedLines(int lines) => [ESC, (byte)'d', (byte)Math.Clamp(lines, 0, 255)];

    /// <summary>GS V 0 (full) or GS V 1 (partial, leaves a small tab).</summary>
    public static byte[] Cut(bool partial) => [GS, (byte)'V', partial ? (byte)1 : (byte)0];

    /// <summary>ESC p m t1 t2: pulse the drawer pin. 25 × 2 ms on, 250 × 2 ms off.</summary>
    public static byte[] OpenDrawer(DrawerPin pin) => [ESC, (byte)'p', (byte)pin, 25, 250];

    /// <summary>DLE EOT n: real-time status request. 1 printer, 2 offline cause, 3 error cause, 4 paper sensor.</summary>
    public static byte[] RequestStatus(byte n) => [DLE, EOT, n];
}
