using Microsoft.Maui.Controls;
using StepperMotorController.Transports;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace StepperMotorController;

public partial class MainPage : ContentPage
{
    private ITransport? _transport;
    private CancellationTokenSource? _readerCts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<string> _log = new();

    // Loop state
    private CancellationTokenSource? _loopCts;
    private bool _loopRunning;
    private int _loopFeed = 3000; // mm/min
    private Task? _loopTask;
    private double _loopDistance = 20.0; // mm

    private readonly ConcurrentQueue<string> _rxQueue = new();
    private readonly SemaphoreSlim _rxSignal = new(0);  // signaled when a new line arrives

    public MainPage()
    {
        InitializeComponent();

#if WINDOWS
        foreach (var p in System.IO.Ports.SerialPort.GetPortNames().OrderBy(x => x))
            PortPicker.Items.Add(p);
#endif
        LogView.ItemsSource = _log;
        UpdateSpeedLabel();
    }

    // ---------------- Connection ----------------

    private void RefreshPorts_Clicked(object sender, EventArgs e)
    {
        PortPicker.Items.Clear();

        foreach (var p in System.IO.Ports.SerialPort.GetPortNames().OrderBy(x => x))
            PortPicker.Items.Add(p);

        Log($"> Ports refreshed ({PortPicker.Items.Count} found)");
    }
    private async void ConnectBtn_Clicked(object sender, EventArgs e)
    {
        if (_transport == null)
        {
            var port = PortPicker.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(port))
            {
                Log("> Select a COM port first.");
                return;
            }

#if WINDOWS
            _transport = new SerialTransport(port);
#else
            Log("> Windows serial only in this build.");
            return;
#endif
            try
            {
                await _transport.ConnectAsync();
                Log($"> Connected {port}");
                ConnectBtn.Text = "Disconnect";

                _readerCts = new CancellationTokenSource();
                _ = Task.Run(() => ReaderLoop(_readerCts.Token));
            }
            catch (Exception ex)
            {
                Log($"! Connect failed: {ex.Message}");
                _transport = null;
            }
        }
        else
        {
            await StopLoopInternalAsync(); // ensure loop is stopped before closing port
            await DisconnectAsync();
        }
    }

    private async Task DisconnectAsync()
    {
        try
        {
            _readerCts?.Cancel();

            // Block new sends and wait for any in-flight writer before closing transport
            await _sendLock.WaitAsync();
            try
            {
                if (_transport != null)
                {
                    if (_transport.IsConnected)
                        await _transport.DisconnectAsync();

                    await _transport.DisposeAsync();
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch { /* ignore during shutdown */ }
        finally
        {
            _transport = null;
            ConnectBtn.Text = "Connect";
            Log("> Disconnected");
        }
    }

    // ---------------- TESTING FUNCTIONS ---------------
    private void StartReaderIfNeeded()
    {
        if (_readerCts == null || _readerCts.IsCancellationRequested)
        {
            _readerCts = new CancellationTokenSource();
            _ = Task.Run(() => ReaderLoop(_readerCts.Token));
            Log("> Reader started");
        }
    }
    private async Task<string?> WaitForNextLineAsync(int timeoutMs, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            // Wait until ReaderLoop signals a line arrived
            await _rxSignal.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        // Drain exactly one line (there may be more queued; we only consume one)
        if (_rxQueue.TryDequeue(out var line))
            return line;

        return null;
    }
    // Replace your ListenTest_Clicked with this more aggressive diagnostic version
    private async void ListenTest_Clicked(object? sender, EventArgs e)
    {
        if (_transport == null || !_transport.IsConnected)
        {
            Log("! Not connected");
            return;
        }

        if (sender is Button b) b.IsEnabled = false;

        try
        {
            Log("> === DIAGNOSTIC TEST START ===");

            // Test 1: Wait for startup banner (boards usually send something on connect)
            Log("> Test 1: Listening for 3 seconds for any startup messages...");
            var startTime = DateTime.UtcNow;
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var line = await _transport.ReadLineAsync(cts.Token);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Log($"< STARTUP: {line}");
                    }
                }
                catch (TimeoutException) { /* expected */ }
                catch (OperationCanceledException) { break; }
            }

            Log($"> Waited {(DateTime.UtcNow - startTime).TotalSeconds:F1}s for startup");

            // Test 2: Try different line endings
            Log("> Test 2: Sending M115 with different line endings...");

            // LF only (what you're using now)
            await SendRawBytes(new byte[] { 0x4D, 0x31, 0x31, 0x35, 0x0A }, "M115 with LF");
            await Task.Delay(500);
            await CheckForResponse(1000);

            // CRLF
            await SendRawBytes(new byte[] { 0x4D, 0x31, 0x31, 0x35, 0x0D, 0x0A }, "M115 with CRLF");
            await Task.Delay(500);
            await CheckForResponse(1000);

            // CR only (unlikely but worth trying)
            await SendRawBytes(new byte[] { 0x4D, 0x31, 0x31, 0x35, 0x0D }, "M115 with CR");
            await Task.Delay(500);
            await CheckForResponse(1000);

            // Test 3: Try a simple echo command
            Log("> Test 3: Sending empty line (should get 'ok' or error)...");
            await SendRawBytes(new byte[] { 0x0A }, "Empty LF");
            await Task.Delay(500);
            await CheckForResponse(1000);

            // Test 4: Raw buffer inspection
            Log("> Test 4: Reading raw bytes for 2 seconds...");
            await InspectRawBytes(2000);

            Log("> === DIAGNOSTIC TEST COMPLETE ===");
        }
        catch (Exception ex)
        {
            Log($"! Diagnostic error: {ex.Message}");
        }
        finally
        {
            if (sender is Button b2) b2.IsEnabled = true;
        }
    }

    private async Task SendRawBytes(byte[] bytes, string description)
    {
        if (_transport == null || !_transport.IsConnected) return;

        var hex = BitConverter.ToString(bytes).Replace("-", " ");
        Log($"> Sending {description}: [{hex}]");

        var port = (_transport as SerialTransport);
        if (port != null)
        {
            // Use reflection to access the private _port field
            var portField = port.GetType().GetField("_port",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var serialPort = portField?.GetValue(port) as System.IO.Ports.SerialPort;

            if (serialPort != null && serialPort.IsOpen)
            {
                await serialPort.BaseStream.WriteAsync(bytes, 0, bytes.Length);
                await serialPort.BaseStream.FlushAsync();
            }
        }
    }

    private async Task CheckForResponse(int timeoutMs)
    {
        if (_transport == null || !_transport.IsConnected) return;

        var cts = new CancellationTokenSource(timeoutMs);
        var responses = 0;

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await _transport.ReadLineAsync(cts.Token);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    Log($"< RESPONSE: {line}");
                    responses++;
                }
            }
        }
        catch (TimeoutException) { /* expected */ }
        catch (OperationCanceledException) { /* expected */ }

        if (responses == 0)
        {
            Log("  ! No response received");
        }
        else
        {
            Log($"  ✓ Received {responses} response(s)");
        }
    }

    private async Task InspectRawBytes(int durationMs)
    {
        if (_transport == null || !_transport.IsConnected) return;

        var port = (_transport as SerialTransport);
        if (port == null) return;

        var portField = port.GetType().GetField("_port",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var serialPort = portField?.GetValue(port) as System.IO.Ports.SerialPort;

        if (serialPort == null || !serialPort.IsOpen) return;

        var buffer = new byte[256];
        var endTime = DateTime.UtcNow.AddMilliseconds(durationMs);
        var totalBytes = 0;

        while (DateTime.UtcNow < endTime)
        {
            try
            {
                if (serialPort.BytesToRead > 0)
                {
                    var count = await serialPort.BaseStream.ReadAsync(buffer, 0,
                        Math.Min(buffer.Length, serialPort.BytesToRead));

                    if (count > 0)
                    {
                        totalBytes += count;
                        var hex = BitConverter.ToString(buffer, 0, count).Replace("-", " ");
                        var ascii = System.Text.Encoding.ASCII.GetString(buffer, 0, count)
                            .Replace("\r", "\\r")
                            .Replace("\n", "\\n");
                        Log($"< RAW [{count} bytes]: {hex}");
                        Log($"  ASCII: {ascii}");
                    }
                }
                await Task.Delay(50);
            }
            catch (Exception ex)
            {
                Log($"! Raw read error: {ex.Message}");
                break;
            }
        }

        if (totalBytes == 0)
        {
            Log("  ! No bytes received at all - board might not be responding");
        }
        else
        {
            Log($"  Total: {totalBytes} bytes received");
        }
    }



    private async Task ReaderLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _transport?.IsConnected == true)
            {
                try
                {
                    var line = await _transport!.ReadLineAsync(ct);
                    var trimmed = line?.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        MainThread.BeginInvokeOnMainThread(() => Log($"< {trimmed}"));
                        _rxQueue.Enqueue(trimmed);
                        _rxSignal.Release();
                    }
                }
                catch (TimeoutException)
                {
                    // benign: no data yet — keep listening
                    continue;
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (ObjectDisposedException) { /* normal */ }
        catch (Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(() => Log($"! Reader error: {ex.Message}"));
        }
    }



    // ---------------- Basic commands ----------------

    private async Task SendAsync(string cmd, CancellationToken ct = default)
    {
        if (_transport == null || !_transport.IsConnected) return;
        if (ct.IsCancellationRequested) return;

        bool entered = false;
        try
        {
            await _sendLock.WaitAsync(ct);   // may throw OCE
            entered = true;

            if (_transport == null || !_transport.IsConnected || ct.IsCancellationRequested)
                return;

            Log($"> {cmd}");
            await _transport.WriteLineAsync(cmd, ct);
        }
        catch (OperationCanceledException) { /* normal on stop */ }
        catch (ObjectDisposedException) { /* port disposed during shutdown */ }
        catch (System.IO.IOException) { /* port closed / I/O aborted; normal if stopping */ }
        catch (Exception ex)
        {
            Log($"! Send failed: {ex.Message}");
        }
        finally
        {
            if (entered) _sendLock.Release();
        }
    }


    private async void Enable_Clicked(object sender, EventArgs e)
    {
        await SendAsync("M17");
    }

    private async void Disable_Clicked(object sender, EventArgs e)
    {
        await SendAsync("M18");
    }

    private async void JogXPos_Clicked(object sender, EventArgs e)
    {
        await SendAsync($"G1 X10 F{FeedEntry.Text?.Trim()}");
    }

    private async void JogXNeg_Clicked(object sender, EventArgs e)
    {
        await SendAsync($"G1 X-10 F{FeedEntry.Text?.Trim()}");
    }

    private async void Send_Clicked(object sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(CmdEntry.Text))
            await SendAsync(CmdEntry.Text!);
    }

    // ---------------- Loop controls ----------------

    private static int EstimateMoveMs(double distMm, int feedMmPerMin, int bufferMs = 50)
    {
        if (feedMmPerMin <= 0) return 200 + bufferMs;
        var ms = (int)Math.Ceiling((Math.Abs(distMm) / feedMmPerMin) * 60_000.0);
        return Math.Max(50, ms + bufferMs);
    }

    private async void StartLoop_Clicked(object sender, EventArgs e)
    {
        if (_loopRunning) { Log("> Loop already running"); return; }
        if (_transport == null || !_transport.IsConnected) { Log("! Not connected"); return; }

        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loopRunning = true;
        StartLoopBtn.IsEnabled = false;
        StopLoopBtn.IsEnabled = true;

        try
        {
            // Ensure motors are enabled and moves are allowed even if not homed
            await SendAsync("M17", token);         // enable steppers
            await SendAsync("M564 S0", token);     // allow unhomed moves
            await SendAsync("G91", token);         // relative mode
            await Task.Delay(50, token);           // tiny setup pause

            // Estimate timings and host-pace (no G4)
            int moveMs = EstimateMoveMs(_loopDistance, _loopFeed);
            int dwellMs = moveMs;                  // keep your old “feel”; adjust if needed
            int paceMs = Math.Max(25, moveMs + dwellMs + 10);

            // IMPORTANT: do NOT pass token to Task.Run; the delegate handles cancellation
            _loopTask = Task.Run(async () =>
            {
                try
                {
                    // Precompute a conservative host pace so we don't outrun the board.
                    int moveMs = EstimateMoveMs(_loopDistance, _loopFeed);
                    int dwellMs = moveMs;                 // same feel as before; tweak as you like
                    int paceMs = Math.Max(25, moveMs + dwellMs + 10);

                    while (!token.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        await SendAsync($"G1 X{_loopDistance} F{_loopFeed}", token);
                        await Task.Delay(15, token);           // tiny inter-command gap
                        await SendAsync($"G4 P{dwellMs}", token);
                        await Task.Delay(paceMs, token);       // host pacing

                        token.ThrowIfCancellationRequested();
                        await SendAsync($"G1 X{-_loopDistance} F{_loopFeed}", token);
                        await Task.Delay(15, token);
                        await SendAsync($"G4 P{dwellMs}", token);
                        await Task.Delay(paceMs, token);
                    }
                }
                catch (OperationCanceledException) { /* normal on stop */ }
                catch (Exception ex)
                {
                    MainThread.BeginInvokeOnMainThread(() => Log($"! Loop error: {ex.Message}"));
                }
            });

        }
        catch (Exception ex)
        {
            Log($"! Start loop failed: {ex.Message}");
            await StopLoopInternalAsync();
        }
    }




    private async Task StopLoopInternalAsync()
    {
        if (!_loopRunning) return;

        // 1) signal stop
        _loopCts?.Cancel();

        // 2) wait the worker to finish
        if (_loopTask != null)
        {
            try { await _loopTask; } catch { /* ignore */ }
        }

        // 3) optional: restore absolute IF still connected
        if (_transport?.IsConnected == true)
        {
            try { await SendAsync("G90"); } catch { /* ignore */ }
        }

        // 4) reset UI/state
        _loopTask = null;
        _loopCts?.Dispose();
        _loopCts = null;
        _loopRunning = false;
        StartLoopBtn.IsEnabled = true;
        StopLoopBtn.IsEnabled = false;
        Log("> Loop stopped");
    }


    private async void StopLoop_Clicked(object sender, EventArgs e)
        => await StopLoopInternalAsync();



    private void SpeedUp_Clicked(object sender, EventArgs e)
    {
        _loopFeed = Math.Min(_loopFeed + 500, 60000); // clamp to a sane max
        UpdateSpeedLabel();
    }

    private void SpeedDown_Clicked(object sender, EventArgs e)
    {
        _loopFeed = Math.Max(_loopFeed - 500, 100); // clamp to a sane min
        UpdateSpeedLabel();
    }

    private void UpdateSpeedLabel()
        => SpeedLabel.Text = $"{_loopFeed} mm/min";

    // ---------------- Utils ----------------

    private void Log(string msg)
    {
        _log.Add(msg);
        if (_log.Count > 500) _log.RemoveAt(0);
        LogView.ItemsSource = null;   // refresh binding
        LogView.ItemsSource = _log;
    }
}
