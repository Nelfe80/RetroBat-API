namespace RetroBat.Api.Infrastructure;

/// <summary>
/// A once-built, in-memory index of the SYSTEM logo and fanart shipped by the
/// EmulationStation themes — the active theme first, then <c>es-theme-carbon</c> as a
/// guaranteed fallback. It replaces a per-system-selected glob over the themes (dozens of
/// <c>Directory.Exists</c> / <c>Directory.EnumerateFiles</c> calls against a fixed, shipped
/// set of files) with an O(1) dictionary lookup, built lazily on first use and cached for
/// the process.
///
/// Carbon's confirmed layout is <c>art/logos/&lt;name&gt;.(svg|png)</c> and
/// <c>art/background/&lt;name&gt;.jpg</c>, named by the ES system id. A small alias table
/// bridges the few RetroBat system ids that differ from carbon's file names; logos prefer
/// the <c>-w</c> (white) variant; collections (auto-verticalarcade, auto-favorites,
/// lightgun, custom-collections…) carry a UI-language suffix (<c>-fr</c>, <c>-de</c>…) with
/// the base name as the English fallback. Fanart has neither white nor language variants.
/// </summary>
internal sealed class ThemeSystemArtIndex
{
    /// <summary>RetroBat system id → carbon file base name, for the few that differ.</summary>
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["jaguar"] = "atarijaguar",
            ["jaguarcd"] = "atarijaguarcd",
            ["lynx"] = "atarilynx",
            ["wswan"] = "wonderswan",
            ["wswanc"] = "wonderswancolor",
            ["gamecube"] = "gc",
            ["oricatmos"] = "oric",
            ["sg1000"] = "sg-1000",
            ["gw"] = "gameandwatch",
            ["dos"] = "exodos",
        };

    private readonly IReadOnlyList<ThemeArt> _themes; // priority order: active theme first, carbon last

    private ThemeSystemArtIndex(IReadOnlyList<ThemeArt> themes) => _themes = themes;

    /// <summary>Builds an index over the given theme roots, in priority order (each root is a
    /// <c>themes/&lt;name&gt;</c> directory — the active theme, then es-theme-carbon). The
    /// caller (the projection) provides the roots, so this type never reads es_settings.</summary>
    internal static ThemeSystemArtIndex Build(IEnumerable<string> themeRootsInPriorityOrder)
    {
        var themes = new List<ThemeArt>();
        foreach (var themeRoot in themeRootsInPriorityOrder)
        {
            themes.Add(new ThemeArt(
                IndexDirectory(Path.Combine(themeRoot, "art", "logos")),
                IndexDirectory(Path.Combine(themeRoot, "art", "background"))));
        }

        return new ThemeSystemArtIndex(themes);
    }

    /// <summary>Logo path for a system/collection, honouring the alias table, the <c>-w</c>
    /// white preference and the collection language suffix. Null when no theme carries one.</summary>
    public string? ResolveLogoPath(string? systemId, string? frontendSystemId, string? language)
    {
        foreach (var theme in _themes)
        {
            foreach (var name in Names(systemId, frontendSystemId))
            {
                foreach (var key in LogoKeys(name, language))
                {
                    if (theme.Logos.TryGetValue(key, out var path))
                    {
                        return path;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Fanart (background) path for a system — no white variant, no language.</summary>
    public string? ResolveFanartPath(string? systemId, string? frontendSystemId)
    {
        foreach (var theme in _themes)
        {
            foreach (var name in Names(systemId, frontendSystemId))
            {
                if (theme.Fanart.TryGetValue(name, out var path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> Names(string? systemId, string? frontendSystemId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in new[] { frontendSystemId, systemId })
        {
            var id = (raw ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                continue;
            }

            var name = Aliases.TryGetValue(id, out var alias) ? alias : id;
            if (seen.Add(name))
            {
                yield return name;
            }
        }
    }

    private static IEnumerable<string> LogoKeys(string name, string? language)
    {
        var lang = (language ?? string.Empty).Trim();
        if (lang.Length > 0)
        {
            yield return name + "-" + lang; // localized collection (auto-verticalarcade-fr)
        }

        yield return name + "-w"; // white system variant (mame-w)
        yield return name;        // base
    }

    private static IReadOnlyDictionary<string, string> IndexDirectory(string directory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
        {
            return map;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                // First file wins for a given stem; the consumer converts svg to png anyway,
                // so a png/svg pair collapses to whichever the directory yields first.
                if (!string.IsNullOrEmpty(stem) && !map.ContainsKey(stem))
                {
                    map[stem] = path;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable theme directory: behave as absent.
        }

        return map;
    }

    private sealed record ThemeArt(
        IReadOnlyDictionary<string, string> Logos,
        IReadOnlyDictionary<string, string> Fanart);
}
