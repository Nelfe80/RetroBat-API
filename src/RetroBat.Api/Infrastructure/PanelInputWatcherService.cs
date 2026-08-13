using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Publishes cabinet button presses on /ws/panel, already resolved to a panel SLOT.
///
/// This is what turns a drawn panel into a WIRING CHECK: press the bottom-left button,
/// see the bottom-left button light up on the marquee. If another one lights up, the
/// wiring is wrong and it shows in a second — no config file to read, no guessing.
///
/// A consumer receives a slot and a function, never a raw button index. The translation
/// chain is the cabinet's own, end to end:
///
///   press → SDL2 (RetroArch's own) → RetroPad identity → CabinetButtons → slot
///
/// and CabinetButtons is exactly what the LedManager wizard measured on this cabinet,
/// so the slot published here is the slot the LEDs light and the panel draws.
/// </summary>
public sealed class PanelInputWatcherService : IHostedService, IDisposable
{
    private const int PollIntervalMs = 25; // ~40 Hz: a press is never missed, the cost is nil

    private readonly IEventBus _eventBus;
    private readonly ILogger<PanelInputWatcherService>? _logger;
    private CabinetInputReader? _reader;
    private long _ticks;
    private const int RescanEveryTicks = 200; // ~5 s at 25 ms
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PanelInputWatcherService(IEventBus eventBus, ILogger<PanelInputWatcherService>? logger = null)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _reader = new CabinetInputReader();
            var (ok, message) = _reader.Initialize(RetroBatPaths.RetroBatRoot);
            if (!ok)
            {
                // no pad, no SDL, no cabinet: the rest of APIExpose does not care
                _logger?.LogInformation("Panel input watcher not started: {Message}", message);
                _reader.Dispose();
                _reader = null;
                return Task.CompletedTask;
            }

            // Initialize only loads the mapping database; the joysticks still have to be
            // OPENED, or Snapshot walks an empty list and no press is ever seen.
            var mapped = _reader.OpenControllers();
            _logger?.LogInformation("Panel input watcher started: {Message}, {Mapped} mapped device(s)",
                message, mapped);
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => WatchAsync(_cts.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Panel input watcher could not start.");
        }

        return Task.CompletedTask;
    }

    private async Task WatchAsync(CancellationToken token)
    {
        // (device, identity) of everything currently down: the diff between two polls is
        // what becomes a press and a release
        var held = new HashSet<(int Device, string Identity)>();

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);

                // a pad plugged in after startup, or an emulator that took the device and
                // gave it back: rescan now and then rather than stay deaf until restart
                if (++_ticks % RescanEveryTicks == 0) _reader!.OpenControllers();

                var now = _reader!.Snapshot()
                    .Select(p => (Device: p.DeviceIndex, p.Identity))
                    .ToHashSet();

                foreach (var down in now.Where(x => !held.Contains(x)))
                {
                    Publish("panel.input.pressed", down.Device, down.Identity);
                }

                foreach (var up in held.Where(x => !now.Contains(x)).ToList())
                {
                    Publish("panel.input.released", up.Device, up.Identity);
                }

                held = now;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Panel input poll failed.");
                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private void Publish(string type, int device, string identity)
    {
        // the device index is the player: pad 0 drives panel 1
        var player = device + 1;
        var slot = ResolveSlot(player, identity);
        var system = SystemInput(identity);

        _eventBus.PublishAsync(new EventEnvelope
        {
            Type = type,
            Payload = new
            {
                Player = player,
                Slot = slot,
                // START and COIN are wired on their own pins, outside the numbered
                // slots. Reporting them as "no slot" left a consumer unable to tell an
                // unwired button from one that simply is not part of the eight — which
                // is precisely the question a wiring check is asking.
                System = system,
                Identity = identity,
                Device = device
            }
        });
    }

    /// <summary>
    /// The slot this identity reaches on THIS cabinet. The per-player map wins when it
    /// exists — a two-panel cabinet can be wired differently on each side — and the
    /// shared map answers otherwise. Null when the identity reaches no slot: a face
    /// button of a pad that is not part of the panel is not a panel event.
    /// </summary>
    /// <summary>The system input an identity stands for, or null when it is a panel
    /// button.</summary>
    private static string? SystemInput(string identity) => identity.ToLowerInvariant() switch
    {
        "start" => "START",
        "select" => "SELECT",
        "l3" => "L3",
        "r3" => "R3",
        _ => null
    };

    private int? ResolveSlot(int player, string identity)
    {
        // the cabinet's own map, read where the wizard wrote it. Player 1's map is the
        // fallback: a second panel wired like the first has no entry of its own, and
        // answering nothing there would make its buttons silent.
        var map = CabinetCartographyStore.Read(player);
        if (map.Count == 0 && player != 1) map = CabinetCartographyStore.Read(1);

        foreach (var (slot, wired) in map)
        {
            if (wired.Equals(identity, StringComparison.OrdinalIgnoreCase) && int.TryParse(slot, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _reader?.Dispose();
    }
}
