using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Publishes cabinet button presses on /ws/panel, already resolved to a panel SLOT.
///
/// This is what turns a drawn panel into a WIRING CHECK: press the bottom-left button,
/// see the bottom-left button light up on the marquee. If another one lights up, the
/// wiring is wrong and it shows in a second - no config file to read, no guessing.
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
    private bool _pollFailed;
    private bool _firstPressLogged;
    private string _lastSignature = "";
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
            _logger?.LogInformation("Panel input watcher started: {Message}, {Mapped} mapped device(s) [{Names}]",
                message, mapped, string.Join(", ", _reader.DeviceNames));
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

                // A pad plugged in after startup, or an emulator that took the device and
                // gave it back. The rescan CLOSES and reopens every joystick, so doing it
                // blindly on a timer meant one unlucky moment - a reopen refused while
                // something else held the device - left the watcher deaf for good, with
                // nothing in the log to say so. It now runs only when the number of
                // joysticks actually changed, and says what it found.
                if (++_ticks % RescanEveryTicks == 0) RescanIfChanged();

                // Boutons cabinet (canal historique) + directions (canal additif dpad/stick).
                // Les deux passent par le même diff pressed/released ; les directions sont
                // taguées System=DPAD par SystemInput et n'ont pas de slot (comme START/SELECT).
                var now = _reader!.Snapshot()
                    .Select(p => (Device: p.DeviceIndex, p.Identity))
                    .Concat(_reader!.SnapshotDirections().Select(p => (Device: p.DeviceIndex, p.Identity)))
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
                // Debug-only was a trap: a poll failing every tick is EXACTLY the state
                // that explains "I press and nothing happens", and it was the one thing
                // the log did not say. Loud once, then quiet - a repeating failure must
                // not drown the file.
                if (!_pollFailed)
                {
                    _pollFailed = true;
                    _logger?.LogWarning(ex, "Panel input poll failed; presses are no longer being read.");
                }

                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reopens the joysticks only when their number changed. Says what it found: a
    /// cabinet whose panel stops answering must be able to show, from the log alone,
    /// whether the device disappeared or the presses did.
    /// </summary>
    private void RescanIfChanged()
    {
        var reader = _reader;
        if (reader is null) return;

        // Signature (compte + instance-ids), pas juste le compte : un unplug+replug garde
        // le même compte mais change l'instance-id → détecté, et on rouvre le device.
        var signature = CabinetInputReader.AttachedSignature();
        if (signature == _lastSignature) return;
        _lastSignature = signature;

        var mapped = reader.OpenControllers();
        _logger?.LogInformation("Panel input devices changed (sig {Sig}): {Mapped} mapped [{Names}]",
            signature, mapped, string.Join(", ", reader.DeviceNames));
    }

    private void Publish(string type, int device, string identity)
    {
        // the device index is the player: pad 0 drives panel 1
        var player = device + 1;
        var slot = ResolveSlot(player, identity);
        var system = SystemInput(identity);

        // One line for the first press of a session: proof the cabinet is being read at
        // all. Without it, "nothing lights up" cannot be told apart from "nothing was
        // pressed" - and the two need opposite fixes.
        if (type.EndsWith(".pressed", StringComparison.Ordinal) && !_firstPressLogged)
        {
            _firstPressLogged = true;
            _logger?.LogInformation("First cabinet press read: player {Player}, identity {Identity}, slot {Slot}, system {System}",
                player, identity, slot?.ToString() ?? "-", system ?? "-");
        }

        _eventBus.PublishAsync(new EventEnvelope
        {
            Type = type,
            Payload = new
            {
                Player = player,
                Slot = slot,
                // START and COIN are wired on their own pins, outside the numbered
                // slots. Reporting them as "no slot" left a consumer unable to tell an
                // unwired button from one that simply is not part of the eight - which
                // is precisely the question a wiring check is asking.
                System = system,
                Identity = identity,
                Device = device
            }
        });
    }

    /// <summary>The system input an identity stands for, or null when it is a panel
    /// button.</summary>
    private static string? SystemInput(string identity) => identity.ToLowerInvariant() switch
    {
        "start" => "START",
        "select" => "SELECT",
        "l3" => "L3",
        "r3" => "R3",
        "up" or "down" or "left" or "right" => "DPAD",
        _ => null
    };

    /// <summary>
    /// The slot this identity reaches on THIS cabinet. The per-player cartography wins
    /// when it exists - a two-panel cabinet can be wired differently on each side - and
    /// the shared map answers otherwise. Null when the identity reaches no slot: a face
    /// button of a pad that is not part of the panel is not a panel event.
    /// </summary>
    private int? ResolveSlot(int player, string identity)
    {
        // THE PER-PLAYER CARTOGRAPHY FIRST: it is what the LedManager wizard measured on
        // THIS panel, through the same reader this service uses, and what the remaps and
        // the MAME cfg are written against - so the marquee lights the very button the
        // game will fire.
        //
        // The global CabinetButtons is a legacy FALLBACK only. The two maps once diverged
        // - the reader used SDL's GameController layer while RetroArch used
        // gamecontrollerdb, so they named x/y and the shoulders/triggers differently, and
        // this resolved through the global on purpose. The reader now parses
        // gamecontrollerdb itself and matches RetroArch, so the per-player map is the
        // single truth; preferring the stale global lit the neighbouring button after a
        // recabling (x/y, L1/L2 and R1/R2 swapped) even though the games stayed correct.
        var map = CabinetCartographyStore.Read(player);
        if (map.Count == 0 && player != 1) map = CabinetCartographyStore.Read(1);
        if (map.Count == 0) map = ReadLegacyCabinetButtons();

        foreach (var (slot, wired) in map)
        {
            if (wired.Equals(identity, StringComparison.OrdinalIgnoreCase) && int.TryParse(slot, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>The cabinet-wide legacy map, kept only as a fallback for a cabinet with
    /// no per-player cartography yet. The per-player map the wizard measured wins - see
    /// <see cref="ResolveSlot"/>.</summary>
    private static IReadOnlyDictionary<string, string> ReadLegacyCabinetButtons()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(RetroBatPaths.PluginRoot, "appsettings.json");
            if (!File.Exists(path)) return result;

            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                ?["ApiExpose"]?["PanelRemapExport"]?["CabinetButtons"] as System.Text.Json.Nodes.JsonObject;
            if (node is null) return result;

            foreach (var entry in node)
            {
                if (entry.Value is System.Text.Json.Nodes.JsonValue value
                    && value.TryGetValue<string>(out var identity))
                {
                    result[entry.Key] = identity;
                }
            }
        }
        catch
        {
            // unreadable settings: no map, the press is simply reported without a slot
        }

        return result;
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
