using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Persists the guided-measured cabinet cartography into appsettings.json under
/// ApiExpose:PanelRemapExport:CabinetButtonsByPlayer:{player}. Comment-free JSON
/// (System.Text.Json), pretty-printed, .bak before write. Reading back the
/// previous map lets the caller offer a one-click reverse.
/// </summary>
public static class CabinetCartographyStore
{
    /// <summary>The RetroPad identities a physical button may map to.</summary>
    public static readonly IReadOnlySet<string> ValidIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "b", "a", "x", "y", "l", "r", "l2", "r2", "l3", "r3", "start", "select"
    };

    private static string AppSettingsPath => Path.Combine(RetroBatPaths.PluginRoot, "appsettings.json");

    public static IReadOnlyDictionary<string, string> Read(int player)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(AppSettingsPath))
            {
                return result;
            }

            var root = JsonNode.Parse(File.ReadAllText(AppSettingsPath)) as JsonObject;
            var node = root?["ApiExpose"]?["PanelRemapExport"]?["CabinetButtonsByPlayer"]?[player.ToString()] as JsonObject;
            if (node is null)
            {
                return result;
            }

            foreach (var entry in node)
            {
                if (entry.Value is JsonValue v && v.TryGetValue<string>(out var identity))
                {
                    result[entry.Key] = identity;
                }
            }
        }
        catch
        {
            // unreadable settings: treated as empty (caller keeps defaults)
        }

        return result;
    }

    public static void Write(int player, IReadOnlyDictionary<string, string> map)
    {
        var root = (File.Exists(AppSettingsPath)
                       ? JsonNode.Parse(File.ReadAllText(AppSettingsPath)) as JsonObject
                       : null)
                   ?? new JsonObject();

        var api = GetOrAdd(root, "ApiExpose");
        var export = GetOrAdd(api, "PanelRemapExport");
        var byPlayer = GetOrAdd(export, "CabinetButtonsByPlayer");

        if (map.Count == 0)
        {
            byPlayer.Remove(player.ToString());
        }
        else
        {
            var entry = new JsonObject();
            foreach (var (physical, identity) in map.OrderBy(p => int.TryParse(p.Key, out var n) ? n : 99))
            {
                entry[physical] = identity.Trim().ToLowerInvariant();
            }

            byPlayer[player.ToString()] = entry;
        }

        if (File.Exists(AppSettingsPath))
        {
            File.Copy(AppSettingsPath, AppSettingsPath + ".bak", overwrite: true);
        }

        File.WriteAllText(AppSettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject GetOrAdd(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }
}
