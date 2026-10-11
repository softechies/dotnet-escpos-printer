using System.Text;

namespace EscPos;

/// <summary>
/// Turns ESC/POS bytes back into readable text: alignment applied, codes and images shown as
/// [QR ...] style markers. Use it in tests, logs, or to show a receipt on screen without a printer.
/// </summary>
public static class ReceiptPreview
{
    public static string Render(ReadOnlySpan<byte> bytes, int charsPerLine = 48, CodePage codePage = CodePage.PC437)
    {
        var data = bytes.ToArray();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(ReceiptBuilder.DotNetCodePage(codePage));

        var output = new StringBuilder();
        var line = new List<byte>();
        var align = Align.Left;
        var width = 1;
        var fontB = false;

        void FlushLine()
        {
            var text = encoding.GetString(line.ToArray());
            var lineWidth = (fontB ? charsPerLine * 4 / 3 : charsPerLine) / width;
            var padded = align == Align.Left ? text : TextLayout.Pad(text, lineWidth, align);
            output.Append(padded.TrimEnd()).Append('\n');
            line.Clear();
        }

        void Marker(string text)
        {
            if (line.Count > 0) FlushLine();
            output.Append('[').Append(text).Append("]\n");
        }

        var i = 0;
        byte At(int offset) => i + offset < data.Length ? data[i + offset] : (byte)0;

        while (i < data.Length)
        {
            var b = data[i];
            if (b == Commands.LF)
            {
                FlushLine();
                i++;
            }
            else if (b == Commands.ESC)
            {
                switch ((char)At(1))
                {
                    case '@': align = Align.Left; width = 1; fontB = false; i += 2; break;
                    case 'a': align = (Align)At(2); i += 3; break;
                    case 'M': fontB = At(2) == 1; i += 3; break;
                    case 'd': for (var n = 0; n < At(2); n++) FlushLine(); i += 3; break;
                    case 'p': Marker("DRAWER"); i += 5; break;
                    case '2': i += 2; break;
                    default: i += 3; break; // ESC E, ESC -, ESC t, ESC 3 and other one-argument commands
                }
            }
            else if (b == Commands.GS)
            {
                switch ((char)At(1))
                {
                    case '!': width = (At(2) >> 4) + 1; i += 3; break;
                    case 'V':
                        Marker(At(2) is 1 or 49 or 66 ? "PARTIAL CUT" : "CUT");
                        i += At(2) is 65 or 66 ? 4 : 3;
                        break;
                    case 'k':
                    {
                        var type = (BarcodeType)At(2);
                        var length = At(3);
                        var text = Encoding.ASCII.GetString(data, i + 4, length);
                        if (type == BarcodeType.Code128 && text.StartsWith("{B")) text = text[2..].Replace("{{", "{");
                        Marker($"BARCODE {type}: {text}");
                        i += 4 + length;
                        if (At(0) == Commands.LF) i++; // the line feed after a barcode only ends the barcode
                        break;
                    }
                    case '(' when At(2) == 'k':
                    {
                        var length = At(3) | (At(4) << 8);
                        if (At(5) == 49 && At(6) == 80)
                        {
                            Marker("QR: " + Encoding.UTF8.GetString(data, i + 8, length - 3));
                        }
                        var isPrint = At(5) == 49 && At(6) == 81;
                        i += 5 + length;
                        if (isPrint && At(0) == Commands.LF) i++; // the line feed after a QR code only ends the code
                        break;
                    }
                    case 'v' when At(2) == '0':
                    {
                        var bytesPerRow = At(4) | (At(5) << 8);
                        var rows = At(6) | (At(7) << 8);
                        Marker($"IMAGE {bytesPerRow * 8}x{rows}");
                        i += 8 + bytesPerRow * rows;
                        break;
                    }
                    default: i += 3; break; // GS B, GS h, GS w, GS H and other one-argument commands
                }
            }
            else if (b == Commands.DLE)
            {
                i += 3;
            }
            else
            {
                line.Add(b);
                i++;
            }
        }

        if (line.Count > 0) FlushLine();
        return CollapseImageBands(output.ToString());
    }

    /// <summary>Large images are sent in bands; show them as one marker.</summary>
    private static string CollapseImageBands(string text)
    {
        var lines = text.Split('\n').ToList();
        for (var n = lines.Count - 1; n > 0; n--)
        {
            if (lines[n].StartsWith("[IMAGE ") && lines[n - 1].StartsWith("[IMAGE "))
            {
                var a = Size(lines[n - 1]);
                var b = Size(lines[n]);
                if (a.w == b.w)
                {
                    lines[n - 1] = $"[IMAGE {a.w}x{a.h + b.h}]";
                    lines.RemoveAt(n);
                }
            }
        }
        return string.Join('\n', lines);

        static (int w, int h) Size(string marker)
        {
            var parts = marker[7..^1].Split('x');
            return (int.Parse(parts[0]), int.Parse(parts[1]));
        }
    }
}
