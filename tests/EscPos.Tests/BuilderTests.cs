using EscPos.Imaging;
using Xunit;

namespace EscPos.Tests;

public class BuilderTests
{
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    /// <summary>Bytes the builder wrote after the initial ESC @.</summary>
    private static byte[] Body(ReceiptBuilder builder) => builder.ToArray()[2..];

    [Fact]
    public void Starts_with_initialize_and_code_page()
    {
        Assert.Equal("1B40", Hex(new ReceiptBuilder().ToArray()));
        Assert.Equal("1B401B7410", Hex(new ReceiptBuilder(48, CodePage.WPC1252).ToArray()));
    }

    [Fact]
    public void Formatting_commands()
    {
        var bytes = Body(new ReceiptBuilder()
            .Align(Align.Center)
            .Bold()
            .Underline(Underline.Thick)
            .Size(2, 3)
            .Inverted()
            .Font(PrinterFont.B));

        Assert.Equal("1B6101" + "1B4501" + "1B2D02" + "1D2112" + "1D4201" + "1B4D01", Hex(bytes));
    }

    [Fact]
    public void Line_wraps_to_the_current_width()
    {
        var text = ReceiptPreview.Render(new ReceiptBuilder(32).Line("Chicken tikka with garlic naan and mint raita").ToArray(), 32);
        Assert.Equal("Chicken tikka with garlic naan\nand mint raita\n", text);
    }

    [Fact]
    public void Double_width_halves_the_line()
    {
        var builder = new ReceiptBuilder(32).Size(2, 2);
        Assert.Equal(16, builder.CurrentWidth);
        builder.Font(PrinterFont.B);
        Assert.Equal(21, builder.CurrentWidth);
    }

    [Fact]
    public void Replaces_typographic_characters_and_unknown_ones()
    {
        var builder = new ReceiptBuilder();
        Assert.Equal("'Rs' - ok...", System.Text.Encoding.ASCII.GetString(builder.Encode("‘₨’ – ok…")));
        Assert.Equal("?", System.Text.Encoding.ASCII.GetString(builder.Encode("ش")));
    }

    [Fact]
    public void Code_page_1252_keeps_accents()
    {
        var bytes = new ReceiptBuilder(48, CodePage.WPC1252).Encode("Café");
        Assert.Equal("436166E9", Hex(bytes));
    }

    [Fact]
    public void Code128_barcode_bytes()
    {
        var bytes = Body(new ReceiptBuilder().Barcode("INV-1042", height: 60, moduleWidth: 3, text: BarcodeText.Below));
        // GS h 60, GS w 3, GS H 2, GS k 73 10 "{BINV-1042", LF
        Assert.Equal("1D683C" + "1D7703" + "1D4802" + "1D6B490A" + "7B42494E562D31303432" + "0A", Hex(bytes));
    }

    [Fact]
    public void Barcode_validation()
    {
        Assert.Throws<ArgumentException>(() => new ReceiptBuilder().Barcode("12345", BarcodeType.Ean13));
        Assert.Throws<ArgumentException>(() => new ReceiptBuilder().Barcode("abc", BarcodeType.Code39));
        Assert.Throws<ArgumentException>(() => new ReceiptBuilder().Barcode("tab\there"));
        new ReceiptBuilder().Barcode("590123412345", BarcodeType.Ean13); // 12 digits, printer adds the check digit
    }

    [Fact]
    public void QR_code_bytes()
    {
        var bytes = Body(new ReceiptBuilder().QrCode("9000052011142444901", moduleSize: 5, errorCorrection: QrErrorCorrection.Q));
        var expected =
            "1D286B0400314132" + "00" +            // model 2
            "1D286B03003143" + "05" +              // module size 5
            "1D286B03003145" + "32" +              // error correction Q
            "1D286B" + "1600" + "315030" +         // store 19 + 3 bytes
            Hex(System.Text.Encoding.ASCII.GetBytes("9000052011142444901")) +
            "1D286B0300315130" +                   // print
            "0A";
        Assert.Equal(expected, Hex(bytes));
    }

    [Fact]
    public void Long_QR_data_uses_two_length_bytes()
    {
        var data = new string('A', 300);
        var bytes = Body(new ReceiptBuilder().QrCode(data));
        // store length 303 = 0x012F → pL 2F pH 01
        Assert.Contains("1D286B2F01315030", Hex(bytes));
    }

    [Fact]
    public void Cut_feed_and_drawer()
    {
        Assert.Equal("1B6404" + "1D5601", Hex(Body(new ReceiptBuilder().Cut())));
        Assert.Equal("1D5600", Hex(Body(new ReceiptBuilder().Cut(partial: false, feedLines: 0))));
        Assert.Equal("1B700019FA", Hex(Body(new ReceiptBuilder().OpenDrawer())));
        Assert.Equal("1B700119FA", Hex(Body(new ReceiptBuilder().OpenDrawer(DrawerPin.Pin5))));
    }

    [Fact]
    public void Raster_image_packs_bits_msb_first()
    {
        var image = new MonoImage(10, 2);
        image[0, 0] = true;
        image[9, 0] = true;
        image[1, 1] = true;

        var bytes = image.ToRasterCommands();
        // GS v 0 0, xL=2 xH=0, yL=2 yH=0, then rows: 10000000 01000000 / 01000000 00000000
        Assert.Equal("1D763000" + "0200" + "0200" + "8040" + "4000", Hex(bytes));
    }

    [Fact]
    public void Tall_images_are_sent_in_bands()
    {
        var bytes = new MonoImage(8, 300).ToRasterCommands(bandHeight: 128);
        // 3 bands of 128 + 128 + 44 rows, each with an 8 byte header and 1 byte per row
        Assert.Equal(3 * 8 + 300, bytes.Length);
        Assert.Equal(0x2C, bytes[8 + 128 + 8 + 128 + 6]); // last band height 44
    }

    [Fact]
    public void Wide_images_are_scaled_to_the_paper()
    {
        var builder = new ReceiptBuilder().Image(new MonoImage(1200, 300), maxWidthDots: 576);
        Assert.Contains("[IMAGE 576x144]", ReceiptPreview.Render(builder.ToArray()));
    }

    [Fact]
    public void Size_limits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReceiptBuilder().Size(9, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReceiptBuilder().QrCode("x", moduleSize: 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReceiptBuilder(10));
    }
}
