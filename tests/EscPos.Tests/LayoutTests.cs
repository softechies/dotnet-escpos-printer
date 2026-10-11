using Xunit;

namespace EscPos.Tests;

public class LayoutTests
{
    [Fact]
    public void Wrap_breaks_on_spaces_and_splits_long_words()
    {
        Assert.Equal(new[] { "one two", "three" }, TextLayout.Wrap("one two three", 8));
        Assert.Equal(new[] { "abcdefgh", "ij" }, TextLayout.Wrap("abcdefghij", 8));
        Assert.Equal(new[] { "a", "", "b" }, TextLayout.Wrap("a\n\nb", 8));
        Assert.Equal(new[] { "" }, TextLayout.Wrap("", 8));
        Assert.Equal(new[] { "Tel 021  NTN 1" }, TextLayout.Wrap("Tel 021  NTN 1", 20)); // spacing kept when it fits
    }

    [Fact]
    public void Pad_aligns_text()
    {
        Assert.Equal("ab    ", TextLayout.Pad("ab", 6, Align.Left));
        Assert.Equal("    ab", TextLayout.Pad("ab", 6, Align.Right));
        Assert.Equal("  ab  ", TextLayout.Pad("ab", 6, Align.Center));
        Assert.Equal("abcdef", TextLayout.Pad("abcdefgh", 6, Align.Left));
    }

    [Fact]
    public void LeftRight_fills_the_gap()
    {
        Assert.Equal(new[] { "Total           1,250.00" }, TextLayout.LeftRight("Total", "1,250.00", 24));
        Assert.Equal(new[] { "Total ......... 1,250.00" }, TextLayout.LeftRight("Total", "1,250.00", 24, '.'));
    }

    [Fact]
    public void LeftRight_wraps_long_labels_and_keeps_the_value_on_the_last_line()
    {
        var lines = TextLayout.LeftRight("Sales tax at eighteen percent on goods", "225.00", 24);
        Assert.Equal(new[] { "Sales tax at eighteen", "percent on goods  225.00" }, lines);

        // No room on the last line: the value gets its own line, right aligned.
        Assert.Equal(new[] { "Service charge for", "outdoor seating area", "               10,250.00" },
            TextLayout.LeftRight("Service charge for outdoor seating area", "10,250.00", 24));
        Assert.All(lines, l => Assert.True(l.Length <= 24));
    }

    [Fact]
    public void LeftRight_with_no_room_for_filler_dots()
    {
        // 16 + 1 + 7 = 24: exactly one space between, no dots
        Assert.Equal(new[] { "Delivery charges 1500.00" }, TextLayout.LeftRight("Delivery charges", "1500.00", 24, '.'));
    }

    [Fact]
    public void Table_rows_wrap_inside_columns()
    {
        Column[] columns = [new(0), new(3, Align.Right), new(8, Align.Right)];
        var lines = TextLayout.Row(columns, ["Chicken karahi half", "2", "2,400.00"], 32);
        Assert.Equal(new[] { "Chicken karahi half   2 2,400.00" }, lines);

        lines = TextLayout.Row(columns, ["Mutton pulao with raita and salad", "1", "950.00"], 32);
        Assert.Equal(new[] { "Mutton pulao with     1   950.00", "raita and salad" }, lines);
    }

    [Fact]
    public void Table_rejects_columns_wider_than_the_line()
    {
        Assert.Throws<ArgumentException>(() => TextLayout.Row([new(20), new(20)], ["a", "b"], 32));
        Assert.Throws<ArgumentException>(() => TextLayout.Row([new(0), new(0)], ["a", "b"], 32));
    }
}

public class PreviewTests
{
    [Fact]
    public void Renders_a_full_receipt()
    {
        Column[] items = [new(0), new(3, Align.Right), new(9, Align.Right)];
        var bytes = new ReceiptBuilder(32)
            .Align(Align.Center).Bold().Line("CAFE ZEST").Bold(false)
            .Line("Clifton, Karachi")
            .Normal()
            .Separator()
            .Row(items, "Item", "Qty", "Amount")
            .Row(items, "Chai", "2", "300.00")
            .Separator()
            .LeftRight("Total", "300.00")
            .Align(Align.Center)
            .QrCode("9000052011142444901")
            .Barcode("INV-1042")
            .OpenDrawer()
            .Cut()
            .ToArray();

        var expected = string.Join('\n',
            "           CAFE ZEST",
            "        Clifton, Karachi",
            "--------------------------------",
            "Item               Qty    Amount",
            "Chai                 2    300.00",
            "--------------------------------",
            "Total                     300.00",
            "[QR: 9000052011142444901]",
            "[BARCODE Code128: INV-1042]",
            "[DRAWER]",
            "",
            "",
            "",
            "",
            "[PARTIAL CUT]",
            "");
        Assert.Equal(expected, ReceiptPreview.Render(bytes, 32));
    }

    [Fact]
    public void Preview_handles_double_size_centering()
    {
        var bytes = new ReceiptBuilder(32).Align(Align.Center).Size(2, 2).Line("BIG").ToArray();
        Assert.Equal("      BIG\n", ReceiptPreview.Render(bytes, 32));
    }
}
