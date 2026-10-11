using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EscPos.Printing;

/// <summary>Somewhere to send receipt bytes.</summary>
public interface IReceiptPrinter
{
    Task PrintAsync(byte[] data, CancellationToken cancellationToken = default);
}

/// <summary>
/// Ethernet and Wi-Fi receipt printers. Almost all of them accept raw ESC/POS on TCP port 9100.
/// A new connection is made per receipt, so a printer that was switched off and on keeps working.
/// </summary>
public sealed class NetworkPrinter(string host, int port = 9100, TimeSpan? timeout = null) : IReceiptPrinter
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public string Host { get; } = host;
    public int Port { get; } = port;

    public async Task PrintAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(Host, Port, timeoutCts.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(data, timeoutCts.Token);
            await stream.FlushAsync(timeoutCts.Token);
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PrinterException($"Printer {Host}:{Port} did not respond within {_timeout.TotalSeconds:0} seconds. Is it on and on the same network?");
        }
        catch (SocketException ex)
        {
            throw new PrinterException($"Could not reach printer {Host}:{Port}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Asks the printer for its real-time status (DLE EOT). Returns null if it doesn't answer,
    /// which some printers don't on port 9100.
    /// </summary>
    public async Task<PrinterStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(Host, Port, timeoutCts.Token);
            await using var stream = client.GetStream();

            var replies = new byte[3];
            byte[] requests = [1, 2, 4];
            for (var n = 0; n < requests.Length; n++)
            {
                await stream.WriteAsync(Commands.RequestStatus(requests[n]), timeoutCts.Token);
                var buffer = new byte[1];
                var read = await stream.ReadAsync(buffer, timeoutCts.Token);
                if (read == 0) return null;
                replies[n] = buffer[0];
            }
            return PrinterStatus.Parse(replies[0], replies[1], replies[2]);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>
/// Writes to a device or file path: /dev/usb/lp0 on Linux, a shared printer path, or a .bin file to inspect.
/// </summary>
public sealed class FilePrinter(string path) : IReceiptPrinter
{
    public string Path { get; } = path;

    public async Task PrintAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await stream.WriteAsync(data, cancellationToken);
    }
}

/// <summary>
/// USB and other printers installed in Windows, by their name in "Printers &amp; scanners".
/// Bytes go straight to the spooler as RAW, so the Windows driver doesn't reformat them.
/// Works with the vendor driver or the "Generic / Text Only" driver.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrinter(string printerName) : IReceiptPrinter
{
    public string PrinterName { get; } = printerName;

    public Task PrintAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Send(data), cancellationToken);
    }

    private void Send(byte[] data)
    {
        if (!NativeMethods.OpenPrinter(PrinterName, out var handle, IntPtr.Zero))
        {
            throw new PrinterException($"Printer \"{PrinterName}\" not found (error {Marshal.GetLastWin32Error()}). Check the name in Printers & scanners.");
        }
        try
        {
            var doc = new NativeMethods.DocInfo { DocName = "Receipt", DataType = "RAW" };
            if (NativeMethods.StartDocPrinter(handle, 1, doc) == 0) throw Win32Error("StartDocPrinter");
            try
            {
                if (!NativeMethods.StartPagePrinter(handle)) throw Win32Error("StartPagePrinter");
                if (!NativeMethods.WritePrinter(handle, data, data.Length, out var written) || written != data.Length)
                {
                    throw Win32Error("WritePrinter");
                }
                NativeMethods.EndPagePrinter(handle);
            }
            finally
            {
                NativeMethods.EndDocPrinter(handle);
            }
        }
        finally
        {
            NativeMethods.ClosePrinter(handle);
        }
    }

    private PrinterException Win32Error(string call) =>
        new($"{call} failed for \"{PrinterName}\" (error {Marshal.GetLastWin32Error()})");

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public sealed class DocInfo
        {
            public string? DocName;
            public string? OutputFile;
            public string? DataType;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool ClosePrinter(IntPtr handle);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int StartDocPrinter(IntPtr handle, int level, [In] DocInfo docInfo);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool EndDocPrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool StartPagePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool EndPagePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool WritePrinter(IntPtr handle, byte[] data, int count, out int written);
    }
}

public sealed class PrinterException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Decoded DLE EOT replies.</summary>
public sealed record PrinterStatus(
    bool Online,
    bool CoverOpen,
    bool PaperEnd,
    bool PaperNearEnd,
    bool Error)
{
    /// <summary>True when a receipt can be printed right now.</summary>
    public bool Ready => Online && !CoverOpen && !PaperEnd && !Error;

    /// <param name="printer">Reply to DLE EOT 1.</param>
    /// <param name="offline">Reply to DLE EOT 2.</param>
    /// <param name="paper">Reply to DLE EOT 4.</param>
    public static PrinterStatus Parse(byte printer, byte offline, byte paper) => new(
        Online: (printer & 0x08) == 0,
        CoverOpen: (offline & 0x04) != 0,
        PaperEnd: (offline & 0x20) != 0 || (paper & 0x60) != 0,
        PaperNearEnd: (paper & 0x0C) != 0,
        Error: (offline & 0x40) != 0);
}
