using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CfSpeedtest.Shared;

namespace CfSpeedtest.Server.Services;

public class ClientWsHub
{
    private sealed class ConnectionInfo
    {
        public string ClientId { get; init; } = string.Empty;
        public WebSocket Socket { get; init; } = default!;
    }

    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();

    public void SetConnection(string clientId, WebSocket socket)
    {
        _connections.AddOrUpdate(clientId,
            _ => new ConnectionInfo { ClientId = clientId, Socket = socket },
            (_, old) => new ConnectionInfo { ClientId = clientId, Socket = socket });
    }

    public void RemoveConnection(string clientId, WebSocket socket)
    {
        if (_connections.TryGetValue(clientId, out var existing) && existing.Socket == socket)
        {
            _connections.TryRemove(clientId, out _);
        }
    }

    public async Task<bool> SendAsync(string clientId, ClientWsMessage message, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(clientId, out var conn) || conn.Socket.State != WebSocketState.Open)
            return false;

        var json = JsonSerializer.Serialize(message, AppJsonContext.Default.ClientWsMessage);
        var bytes = Encoding.UTF8.GetBytes(json);
        await conn.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        return true;
    }
}
