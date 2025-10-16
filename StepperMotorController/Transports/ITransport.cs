// Transports/ITransport.cs
public interface ITransport : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task WriteLineAsync(string line, CancellationToken ct = default);
    Task<string> ReadLineAsync(CancellationToken ct = default); // non-nullable
    bool IsConnected { get; }
}
