using System.Globalization;
using EscPos;
using EscPos.Printing;

// Usage:
//   receipt-demo                         show the receipt as text
//   receipt-demo --ip 192.168.1.50       print on a network printer (port 9100)
//   receipt-demo --windows "POS-80"      print on a printer installed in Windows
//   receipt-demo --file receipt.bin      write the raw bytes to a file or device (/dev/usb/lp0)
//   receipt-demo --status --ip 192.168.1.50
//   add --width 32 for 58 mm paper (default 48 for 80 mm)

var options = ParseArgs(args);
var width = options.TryGetValue("width", out var w) ? int.Parse(w) : 48;

if (options.ContainsKey("status"))
{
    if (!options.TryGetValue("ip", out var statusIp)) return Fail("--status needs --ip");
    var status = await new NetworkPrinter(statusIp).GetStatusAsync();
    Console.WriteLine(status is null ? "Printer did not answer the status request." : status.ToString());
    return status?.Ready == true ? 0 : 1;
}

var receipt = BuildSampleReceipt(width);

if (options.TryGetValue("ip", out var ip))
{
    await new NetworkPrinter(ip).PrintAsync(receipt);
    Console.WriteLine($"Sent {receipt.Length} bytes to {ip}:9100");
}
else if (options.TryGetValue("windows", out var printerName))
{
    if (!OperatingSystem.IsWindows()) return Fail("--windows only works on Windows");
    await new WindowsPrinter(printerName).PrintAsync(receipt);
    Console.WriteLine($"Sent {receipt.Length} bytes to \"{printerName}\"");
}
else if (options.TryGetValue("file", out var path))
{
    await new FilePrinter(path).PrintAsync(receipt);
    Console.WriteLine($"Wrote {receipt.Length} bytes to {path}");
}
else
{
    Console.WriteLine(ReceiptPreview.Render(receipt, width));
}
return 0;

static byte[] BuildSampleReceipt(int width)
{
    var pk = CultureInfo.GetCultureInfo("en-US");
    string Money(decimal value) => value.ToString("#,0.00", pk);

    var lines = new (string Name, int Qty, decimal Price)[]
    {
        ("Chicken karahi (half)", 1, 1450m),
        ("Garlic naan", 4, 90m),
        ("Mint raita", 2, 120m),
        ("Mineral water 1.5L", 1, 150m),
    };
    var subtotal = lines.Sum(l => l.Qty * l.Price);
    var tax = Math.Round(subtotal * 0.16m, 2); // provincial sales tax on services, check your rate
    var total = subtotal + tax;

    Column[] columns = [new(0), new(4, Align.Right), new(10, Align.Right)];

    return new ReceiptBuilder(width)
        .Align(Align.Center)
        .Bold().Size(2, 2).Line("KARACHI GRILL").Normal()
        .Align(Align.Center)
        .Line("Shop 4, Block 5, Clifton, Karachi")
        .Line("Tel 021-35123456  NTN 1234567-8")
        .Normal()
        .Separator('=')
        .LeftRight("Invoice", "INV-1042")
        .LeftRight("Date", "10-Oct-2026 22:30")
        .LeftRight("Table / Waiter", "7 / Imran")
        .Separator()
        .Bold().Row(columns, "Item", "Qty", "Amount").Bold(false)
        .Separator()
        .Rows(columns, lines.Select(l => new[] { l.Name, l.Qty.ToString(), Money(l.Qty * l.Price) }))
        .Separator()
        .LeftRight("Subtotal", Money(subtotal))
        .LeftRight("Sales tax 16%", Money(tax))
        .Bold().Size(1, 2).LeftRight("TOTAL (Rs)", Money(total)).Size(1, 1).Bold(false)
        .LeftRight("Paid by", "Cash")
        .Separator()
        .Align(Align.Center)
        .Line("FBR Invoice # 9000052011142444901")
        .QrCode("9000052011142444901", moduleSize: 5)
        .Barcode("INV-1042", height: 50)
        .Line("Thank you! Please visit again.")
        .Cut()
        .ToArray();
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var key = args[i][2..];
        result[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
    }
    return result;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
