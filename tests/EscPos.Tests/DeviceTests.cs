using System.Net;
using System.Net.Sockets;
using EscPos.Imaging;
using EscPos.Printing;
using Xunit;

namespace EscPos.Tests;

public class ImageTests
{
    /// <summary>Writes a minimal 24 bit bottom-up BMP in memory.</summary>
    private static MemoryStream Bmp24(int width, int height, Func<int, int, bool> black)
    {
        var rowSize = (width * 3 + 3) / 4 * 4;
        var dataSize = rowSize * height;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)0x4D42); w.Write(54 + dataSize); w.Write(0); w.Write(54);
        w.Write(40); w.Write(width); w.Write(height); w.Write((ushort)1); w.Write((ushort)24);
        w.Write(0); w.Write(dataSize); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = 0; x < width; x++)
            {
                var v = black(x, y) ? (byte)0 : (byte)255;
                w.Write(v); w.Write(v); w.Write(v);
            }
            for (var p = width * 3; p < rowSize; p++) w.Write((byte)0);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Loads_a_24_bit_bmp_with_row_padding()
    {
        // 5 px wide forces 1 byte of padding per row; diagonal line
        using var bmp = Bmp24(5, 3, (x, y) => x == y);
        var image = MonoImage.FromBmp(bmp);

        Assert.Equal(5, image.Width);
        Assert.Equal(3, image.Height);
        Assert.True(image[0, 0]);
        Assert.True(image[1, 1]);
        Assert.True(image[2, 2]);
        Assert.False(image[1, 0]);
        Assert.False(image[4, 2]);
    }

    [Fact]
    public void Converts_rgba_with_transparency_as_white()
    {
        byte[] rgba =
        [
            0, 0, 0, 255,       // black
            250, 250, 250, 255, // white
            0, 0, 0, 0,         // transparent
            90, 90, 90, 255     // dark grey
        ];
        var image = MonoImage.FromRgba(rgba, 4, 1);
        Assert.True(image[0, 0]);
        Assert.False(image[1, 0]);
        Assert.False(image[2, 0]);
        Assert.True(image[3, 0]);
    }

    [Fact]
    public void Rejects_other_files()
    {
        Assert.Throws<InvalidDataException>(() => MonoImage.FromBmp(new MemoryStream("not a bitmap"u8.ToArray())));
    }
}

public class PrinterTests
{
    private sealed class FakeNetworkPrinter : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public readonly TaskCompletionSource<byte[]> Received = new();

        public FakeNetworkPrinter(byte[]? statusReplies = null)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var all = new MemoryStream();
                var buffer = new byte[4096];
                var replyIndex = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    all.Write(buffer, 0, read);
                    // Answer each DLE EOT request with the next canned status byte.
                    for (var i = 0; i + 2 < read; i++)
                    {
                        if (buffer[i] == 0x10 && buffer[i + 1] == 0x04 && statusReplies is not null && replyIndex < statusReplies.Length)
                        {
                            await stream.WriteAsync(new[] { statusReplies[replyIndex++] });
                        }
                    }
                    if (statusReplies is not null && replyIndex == statusReplies.Length) break;
                }
                Received.TrySetResult(all.ToArray());
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public async Task Sends_bytes_to_port_9100_style_printers()
    {
        using var fake = new FakeNetworkPrinter();
        var receipt = new ReceiptBuilder().Line("Hello").Cut().ToArray();

        await new NetworkPrinter("127.0.0.1", fake.Port).PrintAsync(receipt);

        var received = await fake.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Convert.ToHexString(receipt), Convert.ToHexString(received));
    }

    [Fact]
    public async Task Reports_an_unreachable_printer_clearly()
    {
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var unused = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();

        var ex = await Assert.ThrowsAsync<PrinterException>(() => new NetworkPrinter("127.0.0.1", unused, TimeSpan.FromSeconds(15)).PrintAsync([1]));
        Assert.Contains("Could not reach printer", ex.Message);
    }

    [Fact]
    public async Task Reads_real_time_status()
    {
        // printer 0x16: online. offline cause 0x32: stopped for paper end. paper sensor 0x1E: near end.
        using var fake = new FakeNetworkPrinter([0x16, 0x32, 0x1E]);
        var status = await new NetworkPrinter("127.0.0.1", fake.Port, TimeSpan.FromSeconds(3)).GetStatusAsync();

        Assert.NotNull(status);
        Assert.True(status!.Online);
        Assert.True(status.PaperEnd);      // 0x32 has bit 5 set: stopped for paper end
        Assert.True(status.PaperNearEnd);  // 0x1E has bits 2 and 3 set
        Assert.False(status.Ready);
    }

    [Fact]
    public void Parses_status_bits()
    {
        var ok = PrinterStatus.Parse(0x12, 0x12, 0x12);
        Assert.True(ok.Ready);

        var cover = PrinterStatus.Parse(0x1A, 0x16, 0x12);
        Assert.False(cover.Online);
        Assert.True(cover.CoverOpen);
        Assert.False(cover.Ready);

        Assert.True(PrinterStatus.Parse(0x12, 0x52, 0x12).Error);
        Assert.True(PrinterStatus.Parse(0x12, 0x12, 0x72).PaperEnd);
    }

    [Fact]
    public async Task Writes_to_a_file_or_device_path()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin");
        try
        {
            await new FilePrinter(path).PrintAsync([0x1B, 0x40, 0x41]);
            Assert.Equal("1B4041", Convert.ToHexString(await File.ReadAllBytesAsync(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
