using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Events;

namespace RetroBat.Api.Hubs;

public class WebSocketConnectionManager
{
    private readonly List<WebSocket> _sockets = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task AddSocketAsync(WebSocket socket)
    {
        await _lock.WaitAsync();
        try
        {
            _sockets.Add(socket);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveSocketAsync(WebSocket socket)
    {
        await _lock.WaitAsync();
        try
        {
            _sockets.Remove(socket);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task BroadcastAsync<T>(T message)
    {
        var json = JsonSerializer.Serialize(message);
        var buffer = Encoding.UTF8.GetBytes(json);

        await _lock.WaitAsync();
        try
        {
            var disconnected = new List<WebSocket>();
            foreach (var socket in _sockets)
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(
                        new ArraySegment<byte>(buffer, 0, buffer.Length),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
                else
                {
                    disconnected.Add(socket);
                }
            }

            foreach (var socket in disconnected)
            {
                _sockets.Remove(socket);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
