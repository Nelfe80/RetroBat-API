using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Providers.MameOutputs;

public class MameOutputsProvider : IProvider
{
    private readonly IEventBus _eventBus;
    private readonly ILogger<MameOutputsProvider>? _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public MameOutputsProvider(IEventBus eventBus, ILogger<MameOutputsProvider>? logger = null)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, 8000);
        
        try
        {
            _listener.Start();
            _logger?.LogInformation("MameOutputsProvider started on port 8000");
            _acceptTask = AcceptClientsAsync(_cts.Token);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start TCP listener on port 8000");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts != null)
        {
            _cts.Cancel();
        }

        _listener?.Stop();

        if (_acceptTask != null)
        {
            try
            {
                await Task.WhenAny(_acceptTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            }
            catch
            {
                // Ignore exceptions during shutdown
            }
        }
        
        _logger?.LogInformation("MameOutputsProvider stopped");
    }

    public bool IsHealthy() => _listener?.Server.IsBound == true;

    private async Task AcceptClientsAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(token);
                _ = HandleClientAsync(client, token); // Fire and forget handler
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error accepting client");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            var endPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            _logger?.LogInformation($"MAME client connected from {endPoint}");

            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (!token.IsCancellationRequested && client.Connected)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (line == null) break;

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    ProcessLine(line);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, $"Error handling MAME client {endPoint}");
            }
            
            _logger?.LogInformation($"MAME client disconnected: {endPoint}");
        }
    }

    private void ProcessLine(string line)
    {
        // typical format: "genout5=1"
        var parts = line.Split('=', 2);
        if (parts.Length == 2 && int.TryParse(parts[1], out var val))
        {
            var key = parts[0].Trim();
            
            var payload = new MameOutputEvent
            {
                Source = "mame.network",
                Port = 8000,
                Signals = new List<MameSignal>
                {
                    new MameSignal { Key = key, Value = val, Ts = DateTime.UtcNow }
                }
            };
            
            var evt = new EventEnvelope
            {
                Type = "mame.output.changed",
                Payload = payload
            };
            
            _eventBus.PublishAsync(evt);
        }
    }
}
