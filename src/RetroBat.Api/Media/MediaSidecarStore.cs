using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroBat.Api.Media;

/// <summary>
/// LOT 5 (§11.4→§11.7) - durable record of which gamelist media bindings APIExpose owns, so a
/// FillMissing allocation can update its own past writes without ever clobbering a user's binding.
///
/// One JSON document per system, at <c>&lt;base&gt;/&lt;systemId&gt;/sidecar.json</c>, kept OUT of
/// <c>roms/</c> (it is bookkeeping, not media). Keyed by normalized rom path; each entry records,
/// per slot, whether APIExpose manages it and the exact value it last wrote. Ownership is only ever
/// asserted when the value we wrote is STILL what the gamelist holds - the caller checks with
/// <see cref="OwnsCurrentValue"/>, and any external edit silently drops ownership. Writes are atomic
/// (temp + replace) and skipped entirely when nothing changed.
/// </summary>
public sealed class MediaSidecarStore
{
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _baseDirectory;
    private readonly ConcurrentDictionary<string, SystemState> _systems = new(StringComparer.OrdinalIgnoreCase);

    public MediaSidecarStore(string baseDirectory)
    {
        _baseDirectory = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
    }

    /// <summary>The value APIExpose last wrote for a slot, or null if it never owned it.</summary>
    public BindingOwnership GetOwnership(string systemId, string romPath, string slot)
    {
        var state = GetSystem(systemId);
        lock (state.Gate)
        {
            if (state.Document.Games.TryGetValue(NormalizeRom(romPath), out var game)
                && game.Bindings.TryGetValue(slot, out var binding)
                && binding.Managed)
            {
                return new BindingOwnership(true, binding.LastValue);
            }
        }

        return BindingOwnership.None;
    }

    /// <summary>True when APIExpose owns the slot AND the gamelist still holds exactly what it wrote.</summary>
    public bool OwnsCurrentValue(string systemId, string romPath, string slot, string? currentValue)
    {
        var ownership = GetOwnership(systemId, romPath, slot);
        return ownership.Managed
            && ownership.LastValue is not null
            && string.Equals(ownership.LastValue, currentValue, StringComparison.Ordinal);
    }

    /// <summary>Record that APIExpose wrote <paramref name="value"/> to a slot. Marks the system dirty
    /// only when this actually changes the stored ownership.</summary>
    public void RecordManaged(string systemId, string romPath, string slot, string value)
    {
        var state = GetSystem(systemId);
        var rom = NormalizeRom(romPath);
        var nowUtc = DateTime.UtcNow.ToString("O");
        lock (state.Gate)
        {
            if (!state.Document.Games.TryGetValue(rom, out var game))
            {
                game = new GameSidecar { RomPath = rom };
                state.Document.Games[rom] = game;
            }

            if (game.Bindings.TryGetValue(slot, out var existing)
                && existing.Managed
                && string.Equals(existing.LastValue, value, StringComparison.Ordinal))
            {
                return; // no real change - do not touch updatedUtc, do not dirty
            }

            game.Bindings[slot] = new BindingRecord { Managed = true, LastValue = value, WrittenUtc = nowUtc };
            game.UpdatedUtc = nowUtc;
            state.Dirty = true;
        }
    }

    /// <summary>Release APIExpose ownership of a slot (the user has taken it over). No-op when we did
    /// not own it, so it never dirties the document needlessly.</summary>
    public void AbandonOwnership(string systemId, string romPath, string slot)
    {
        var state = GetSystem(systemId);
        var rom = NormalizeRom(romPath);
        lock (state.Gate)
        {
            if (state.Document.Games.TryGetValue(rom, out var game)
                && game.Bindings.Remove(slot))
            {
                if (game.Bindings.Count == 0)
                {
                    state.Document.Games.Remove(rom);
                }
                else
                {
                    game.UpdatedUtc = DateTime.UtcNow.ToString("O");
                }

                state.Dirty = true;
            }
        }
    }

    /// <summary>Persist a system's sidecar atomically - but only when something actually changed.</summary>
    public void Save(string systemId)
    {
        var state = GetSystem(systemId);
        lock (state.Gate)
        {
            if (!state.Dirty)
            {
                return;
            }

            state.Document.SchemaVersion = CurrentSchemaVersion;
            state.Document.SystemId = systemId;
            state.Document.GeneratedUtc = DateTime.UtcNow.ToString("O");

            var path = SidecarPath(systemId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(state.Document, SerializerOptions);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }

            state.Dirty = false;
        }
    }

    // ---- internals -------------------------------------------------------

    private SystemState GetSystem(string systemId)
        => _systems.GetOrAdd(systemId, Load);

    private SystemState Load(string systemId)
    {
        var path = SidecarPath(systemId);
        if (File.Exists(path))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<SystemSidecar>(File.ReadAllText(path), SerializerOptions);
                if (doc is not null && doc.SchemaVersion == CurrentSchemaVersion)
                {
                    doc.Games ??= new(StringComparer.OrdinalIgnoreCase);
                    // Re-key defensively so lookups are always normalized even if the file was hand-edited.
                    var normalized = new Dictionary<string, GameSidecar>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (key, value) in doc.Games)
                    {
                        value.Bindings ??= new(StringComparer.OrdinalIgnoreCase);
                        normalized[NormalizeRom(key)] = value;
                    }

                    doc.Games = normalized;
                    return new SystemState(doc);
                }
            }
            catch
            {
                // Corrupt or unreadable sidecar: start clean rather than fail a scrape.
            }
        }

        return new SystemState(new SystemSidecar
        {
            SchemaVersion = CurrentSchemaVersion,
            SystemId = systemId,
            Games = new(StringComparer.OrdinalIgnoreCase)
        });
    }

    private string SidecarPath(string systemId)
        => Path.Combine(_baseDirectory, NormalizeSystem(systemId), "sidecar.json");

    internal static string NormalizeRom(string romPath)
        => (romPath ?? string.Empty).Trim().Replace('\\', '/').TrimStart('.', '/').ToLowerInvariant();

    private static string NormalizeSystem(string systemId)
    {
        var trimmed = (systemId ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '_');
        }

        return string.IsNullOrEmpty(trimmed) ? "_" : trimmed;
    }

    private sealed class SystemState
    {
        public SystemState(SystemSidecar document) => Document = document;

        public object Gate { get; } = new();
        public SystemSidecar Document { get; }
        public bool Dirty { get; set; }
    }

    // ---- serialized shape (§11.5) ---------------------------------------

    internal sealed class SystemSidecar
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("systemId")] public string? SystemId { get; set; }
        [JsonPropertyName("generatedUtc")] public string? GeneratedUtc { get; set; }
        [JsonPropertyName("games")] public Dictionary<string, GameSidecar> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class GameSidecar
    {
        [JsonPropertyName("romPath")] public string? RomPath { get; set; }
        [JsonPropertyName("updatedUtc")] public string? UpdatedUtc { get; set; }
        [JsonPropertyName("bindings")] public Dictionary<string, BindingRecord> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class BindingRecord
    {
        [JsonPropertyName("managed")] public bool Managed { get; set; }
        [JsonPropertyName("lastValue")] public string? LastValue { get; set; }
        [JsonPropertyName("writtenUtc")] public string? WrittenUtc { get; set; }
    }
}

/// <summary>Ownership APIExpose holds over one gamelist slot.</summary>
public readonly record struct BindingOwnership(bool Managed, string? LastValue)
{
    public static readonly BindingOwnership None = new(false, null);
}
