#if WINDOWS
using System.IO.Ports;
using System.Text;

namespace StepperMotorController.Transports;

public sealed class SerialTransport : ITransport
{
    private readonly string _portName;
    private SerialPort? _port;

    public SerialTransport(string portName) => _portName = portName;

    public bool IsConnected => _port?.IsOpen == true;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // If already open, do nothing
        if (_port is { IsOpen: true }) return;

        var sp = new SerialPort(_portName)
        {
            // Explicit 8N1 + no handshake (USB CDC ignores baud, but set it anyway)
            BaudRate = 115200,
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,

            // RRF speaks ASCII G-code and terminates with LF
            Encoding = Encoding.ASCII,
            NewLine = "\n",

            // Short timeouts so ReadLine can poll and we can cancel
            ReadTimeout = 500,
            WriteTimeout = 1000,

            // Many boards stay silent until DTR/RTS are asserted
            DtrEnable = true,
            RtsEnable = true,
        };

        try
        {
            sp.Open();

            // Give USB CDC a moment to settle after open
            await Task.Delay(75, ct);

            // Clear any stale garbage from a previous session
            try { sp.DiscardInBuffer(); } catch { /* ignore */ }
            try { sp.DiscardOutBuffer(); } catch { /* ignore */ }

            // Wake the firmware's line reader (first write after open is sometimes ignored)
            try { sp.Write("\n"); } catch { /* ignore */ }

            // Commit
            _port = sp;
        }
        catch
        {
            // If anything failed, make sure we don't keep a half-open port around
            try { sp.Dispose(); } catch { /* ignore */ }
            throw;
        }
    }
    public async Task WriteLineAsync(string s, CancellationToken ct = default)
    {
        if (_port is null || !_port.IsOpen) return;

        // Explicit LF-terminated ASCII line: [payload]\n
        // Using BaseStream avoids any ambiguity with SerialPort.NewLine + WriteLine
        var payload = System.Text.Encoding.ASCII.GetBytes(s);
        var lf = new byte[] { 0x0A }; // '\n'

        // Small writes reduce chance of partial frame loss on some CDC stacks
        await _port.BaseStream.WriteAsync(payload.AsMemory(0, payload.Length), ct).ConfigureAwait(false);
        await _port.BaseStream.WriteAsync(lf.AsMemory(0, 1), ct).ConfigureAwait(false);

        // Give the USB CDC a breath (very short) to flush to the device
        // (Tiny, but helps some stacks that coalesce the last byte boundary)
        await Task.Yield();
    }


    // Non-nullable, robust against timeouts, supports cancellation.
    public async Task<string?> ReadLineAsync(CancellationToken ct = default)
    {
        if (_port is null || !_port.IsOpen)
            throw new InvalidOperationException("Port not open");

        // Soft deadline using the SerialPort.ReadTimeout for each read attempt
        // We'll accumulate until we see LF (0x0A). CR (0x0D) is ignored.
        var buffer = new byte[256];
        var sb = new System.Text.StringBuilder(128);

        while (true)
        {
            // Honor cancellation
            ct.ThrowIfCancellationRequested();

            try
            {
                // Read what's available up to buffer length
                // Note: BaseStream.ReadAsync respects SerialPort.ReadTimeout by throwing IOException.
                int n = await _port.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                                              .ConfigureAwait(false);

                if (n <= 0)
                    continue; // no data; let loop continue (port may be slow)

                for (int i = 0; i < n; i++)
                {
                    byte b = buffer[i];
                    if (b == 0x0A) // LF terminator
                    {
                        var line = sb.ToString();
                        sb.Clear();
                        return line;
                    }
                    else if (b == 0x0D)
                    {
                        // ignore CR
                        continue;
                    }
                    else
                    {
                        sb.Append((char)b);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException)
            {
                // Map per-read timeout to "no data yet"; keep listening
                continue;
            }
            catch (IOException)
            {
                // Some stacks throw IOException on read timeout instead of TimeoutException
                // Treat it like a soft timeout (no data yet)
                continue;
            }
        }
    }


    public Task DisconnectAsync()
    {
        if (_port is { IsOpen: true })
            _port.Close();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }
}
#endif
