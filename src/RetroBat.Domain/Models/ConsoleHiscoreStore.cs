using System.Text;
using System.Text.RegularExpressions;
using RetroBat.Domain.Paths;

namespace RetroBat.Domain.Models;

/// <summary>
/// Reads the accumulated LOCAL console leaderboard. The RetroArch console hiscore
/// capture appends "#rank name score" lines into the game's theme gameinfos XML
/// (an APIEXPOSE:HISCORE block) on game-end. Those XMLs are named by the game SLUG
/// (e.g. sonic-the-hedgehog.xml), not the rom filename — so we resolve candidate
/// slugs from the game name and rom. Shared so the hiscore extraction serves console
/// scores through /api/v1/hiscores and the WS exactly like arcade (hi2txt).
/// </summary>
public static class ConsoleHiscoreStore
{
    /// <summary>Local console leaderboard for a game, highest score first with ranks
    /// reassigned. Empty when the game has no accumulated local scores yet.</summary>
    public static List<HiscoreEntry> ReadScores(string systemId, string rom, string? gameName = null)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return new List<HiscoreEntry>();
        }

        // The gameinfos XML is named by the game slug, so try candidate identifiers
        // derived from the game name (region-stripped) and the rom.
        var ids = BuildIds(rom, gameName);
        if (ids.Count == 0)
        {
            return new List<HiscoreEntry>();
        }

        foreach (var path in CandidatePaths(systemId, ids))
        {
            if (!File.Exists(path)) continue;

            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }

            var scores = ParseScores(text);
            if (scores.Count > 0)
            {
                return Order(scores);
            }
        }

        return new List<HiscoreEntry>();
    }

    /// <summary>Reattribute a single anonymous entry to a player: rewrites ONLY the
    /// "#rank &lt;from&gt; &lt;score&gt;" line matching this exact score (never other
    /// anonymous entries of other players). Used to claim the current session's runs
    /// when a player identifies. Returns true if a line was rewritten.</summary>
    public static bool RenamePlayer(string systemId, string rom, string? gameName, string from, string to, string score)
    {
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(score))
        {
            return false;
        }

        var ids = BuildIds(rom, gameName);
        var pattern = @"(#\d+\s+)" + Regex.Escape(from) + @"(\s+" + Regex.Escape(score.Trim()) + @"\b)";
        foreach (var path in CandidatePaths(systemId, ids))
        {
            if (!File.Exists(path)) continue;

            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }
            if (!Regex.IsMatch(text, pattern)) continue;

            var updated = Regex.Replace(text, pattern, "$1" + to.Trim().Replace("$", "$$") + "$2");
            try { File.WriteAllText(path, updated); return true; }
            catch { return false; }
        }
        return false;
    }

    private static List<string> BuildIds(string rom, string? gameName)
    {
        var ids = new List<string>();
        void Add(string? candidate)
        {
            var s = Slug(candidate);
            if (s.Length > 0 && !ids.Contains(s, StringComparer.OrdinalIgnoreCase)) ids.Add(s);
        }
        Add(StripRegion(gameName));
        Add(gameName);
        if (!string.IsNullOrWhiteSpace(rom))
        {
            if (!ids.Contains(rom, StringComparer.OrdinalIgnoreCase)) ids.Add(rom);
            Add(StripRegion(rom));
            Add(rom);
        }
        return ids;
    }

    private static IEnumerable<string> CandidatePaths(string systemId, List<string> ids)
    {
        var roots = new[]
        {
            RetroBatPaths.ThemeGameInfosResourcesRoot,
            RetroBatPaths.EmulationStationGameInfosThemeRoot,
            RetroBatPaths.ThemeHiscoreResourcesRoot,
            Path.Combine(RetroBatPaths.EmulationStationThemesRoot, ".hiscore")
        };
        foreach (var root in roots)
            foreach (var id in ids)
                yield return Path.Combine(root, systemId, id + ".xml");
    }

    private static List<HiscoreEntry> ParseScores(string text)
    {
        // Prefer the canonical APIEXPOSE:HISCORE block so we don't mix scores from
        // several theme views; fall back to the whole file.
        var block = Regex.Match(text, @"APIEXPOSE:HISCORE:BEGIN(?<body>.*?)APIEXPOSE:HISCORE:END",
            RegexOptions.Singleline);
        var scan = block.Success ? block.Groups["body"].Value : text;

        var scores = new List<HiscoreEntry>();
        foreach (Match match in Regex.Matches(scan, @"#\d+\s+(?<name>\S+)\s+(?<score>\d+)"))
        {
            var entry = new HiscoreEntry
            {
                Rank = string.Empty,
                Name = match.Groups["name"].Value,
                Score = match.Groups["score"].Value
            };
            if (!scores.Any(existing =>
                    string.Equals(existing.Name, entry.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Score, entry.Score, StringComparison.Ordinal)))
            {
                scores.Add(entry);
            }
        }
        return scores;
    }

    private static List<HiscoreEntry> Order(List<HiscoreEntry> scores)
    {
        var ordered = scores
            .OrderByDescending(s => long.TryParse(s.Score, out var v) ? v : 0)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Rank = (i + 1).ToString();
        }
        return ordered;
    }

    private static string StripRegion(string? name)
        => name == null ? string.Empty : Regex.Replace(name, @"[\(\[].*?[\)\]]", " ");

    private static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
