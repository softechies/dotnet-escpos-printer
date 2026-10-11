using System.Text;

namespace EscPos;

/// <summary>A column in <see cref="ReceiptBuilder.Row"/>.</summary>
/// <param name="Width">Characters. Use 0 for "the rest of the line" (one column at most).</param>
/// <param name="Align">Alignment of the text inside the column.</param>
public sealed record Column(int Width, Align Align = Align.Left);

/// <summary>Fixed width text helpers. Receipts are monospaced, so layout is just counting characters.</summary>
public static class TextLayout
{
    /// <summary>Breaks text into lines of at most <paramref name="width"/> characters, on spaces where possible.</summary>
    public static List<string> Wrap(string text, int width)
    {
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        var lines = new List<string>();

        foreach (var paragraph in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            // Lines that fit are kept as written, including deliberate double spaces.
            if (paragraph.Length <= width)
            {
                lines.Add(paragraph.TrimEnd());
                continue;
            }

            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var remaining = word;
                // Words longer than a line are split hard.
                while (remaining.Length > width)
                {
                    if (line.Length > 0)
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                    }
                    lines.Add(remaining[..width]);
                    remaining = remaining[width..];
                }

                if (line.Length > 0 && line.Length + 1 + remaining.Length > width)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(remaining);
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    public static string Pad(string text, int width, Align align)
    {
        text ??= "";
        if (text.Length >= width) return text[..width];
        return align switch
        {
            Align.Right => text.PadLeft(width),
            Align.Center => text.PadLeft((width + text.Length) / 2).PadRight(width),
            _ => text.PadRight(width)
        };
    }

    /// <summary>"Subtotal          1,250.00": left text, right text, filler between.</summary>
    public static List<string> LeftRight(string left, string right, int width, char filler = ' ')
    {
        left ??= "";
        right ??= "";
        if (right.Length >= width) return [.. Wrap(left, width), right[..width]];

        // The right text goes on the last line of the label, or on its own line when it doesn't fit.
        var leftLines = Wrap(left, width);
        var last = leftLines[^1];
        var gap = width - last.Length - right.Length;
        if (gap < 1)
        {
            leftLines.Add(Pad(right, width, Align.Right));
            return leftLines;
        }
        leftLines[^1] = filler == ' ' || gap < 3
            ? last + new string(' ', gap) + right
            : last + " " + new string(filler, gap - 2) + " " + right;
        return leftLines;
    }

    /// <summary>
    /// Lays out one row of a table. Each cell wraps inside its column and the row grows to the tallest cell.
    /// Columns are separated by one space.
    /// </summary>
    public static List<string> Row(IReadOnlyList<Column> columns, IReadOnlyList<string> cells, int width)
    {
        if (columns.Count != cells.Count) throw new ArgumentException("One cell per column");
        var widths = ResolveWidths(columns, width);

        var wrapped = cells.Select((cell, i) => Wrap(cell ?? "", widths[i])).ToList();
        var height = wrapped.Max(c => c.Count);
        var lines = new List<string>(height);

        for (var line = 0; line < height; line++)
        {
            var parts = new string[columns.Count];
            for (var c = 0; c < columns.Count; c++)
            {
                var text = line < wrapped[c].Count ? wrapped[c][line] : "";
                parts[c] = Pad(text, widths[c], columns[c].Align);
            }
            lines.Add(string.Join(' ', parts).TrimEnd());
        }
        return lines;
    }

    internal static int[] ResolveWidths(IReadOnlyList<Column> columns, int width)
    {
        var fixedTotal = columns.Sum(c => c.Width) + (columns.Count - 1);
        var flexible = columns.Count(c => c.Width == 0);
        if (flexible > 1) throw new ArgumentException("Only one column can have width 0");
        if (fixedTotal > width || (flexible == 1 && fixedTotal >= width))
        {
            throw new ArgumentException($"Columns need {fixedTotal + flexible} characters but the line has {width}");
        }
        return columns.Select(c => c.Width == 0 ? width - fixedTotal : c.Width).ToArray();
    }
}
