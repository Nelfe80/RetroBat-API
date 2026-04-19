using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Hubs;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Events;
using RetroBat.Providers.MameOutputs;
using RetroBat.Providers.EmulationStation;
using RetroBat.Providers.Hi2Txt;
using RetroBat.MediaStore;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using RetroBat.Domain.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Core Services
var eventBus = new SimpleEventBus();
builder.Services.AddSingleton<IEventBus>(eventBus);

var wsManager = new WebSocketConnectionManager();
builder.Services.AddSingleton(wsManager);

builder.Services.AddSingleton<IMediaStore, BasicMediaStore>();
builder.Services.AddSingleton<ApiContext>();

// Providers
builder.Services.AddHostedService<ProviderHostedService>();
builder.Services.AddSingleton<IProvider, MameOutputsProvider>();
builder.Services.AddSingleton<IProvider, EmulationStationWatcherProvider>();
builder.Services.AddSingleton<IProvider, Hi2TxtProvider>();

var app = builder.Build();

// Setup internal event subscriber to broadcast via WebSockets
eventBus.Subscribe<EventEnvelope>(async evt => 
{
    await wsManager.BroadcastAsync(evt);
});

app.UseSwagger();
app.UseSwaggerUI();

app.UseWebSockets();
app.UseRouting();

app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        await wsManager.AddSocketAsync(webSocket);

        // Keep connection open
        var buffer = new byte[1024 * 4];
        var receiveResult = await webSocket.ReceiveAsync(
            new ArraySegment<byte>(buffer), CancellationToken.None);

        while (!receiveResult.CloseStatus.HasValue)
        {
            receiveResult = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer), CancellationToken.None);
        }

        await wsManager.RemoveSocketAsync(webSocket);
        await webSocket.CloseAsync(
            receiveResult.CloseStatus.Value, 
            receiveResult.CloseStatusDescription, 
            CancellationToken.None);
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

app.MapControllers();

app.Run("http://127.0.0.1:12345");

public class ProviderHostedService : IHostedService
{
    private readonly IEnumerable<IProvider> _providers;
    
    public ProviderHostedService(IEnumerable<IProvider> providers)
    {
        _providers = providers;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            await provider.StartAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            await provider.StopAsync(cancellationToken);
        }
    }
}
