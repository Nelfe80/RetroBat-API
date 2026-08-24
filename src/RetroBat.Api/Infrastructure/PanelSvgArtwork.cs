using System.Globalization;
using System.Text.RegularExpressions;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// The drawn button and joystick artwork of resources/theme/images, prepared for being
/// stamped several times into one panel SVG.
///
/// Two things make a naive copy-paste fail, and both are silent:
///
/// 1. **Ids and CSS class names are document-global.** The artwork carries
///    `id="linear-gradient-2"` and `.cls-1 { fill: url(#linear-gradient-2) }`. Eight
///    buttons in one file means eight `linear-gradient-2` and eight `.cls-1`: every
///    instance would resolve to whichever came last, and the panel would come out
///    with one shading for all. Each instance therefore gets its own suffix.
///
/// 2. **The artwork is red.** Its shading is a ramp of reds, not a flat fill, so a
///    plain colour swap would flatten it. The hue is transferred instead, leaving
///    saturation and lightness alone - the button keeps its highlight, its body and
///    its shadow, in the colour the panel asks for. Greys and whites (the plastic rim,
///    the specular) have almost no saturation and are left untouched.
/// </summary>
public static class PanelSvgArtwork
{
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    private static readonly Regex IdPattern = new("id=\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex HexPattern = new("#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})\\b", RegexOptions.Compiled);
    private static readonly Regex ViewBoxPattern = new("viewBox=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>An artwork ready to be placed: its natural size and its inner markup.</summary>
    public sealed record Piece(double Width, double Height, string Body);

    /// <summary>Loads a piece by file name ("top-button-v2.svg"), or null when absent -
    /// the caller then falls back to drawing a plain circle.</summary>
    public static Piece? Load(string fileName)
    {
        string? raw;
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(fileName, out raw))
            {
                var path = Path.Combine(RetroBatPaths.ThemeResourcesRoot, "images", fileName);
                raw = File.Exists(path) ? File.ReadAllText(path) : null;
                Cache[fileName] = raw;
            }
        }

        if (raw is null) return null;

        var box = ViewBoxPattern.Match(raw);
        if (!box.Success) return null;
        var parts = box.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
        {
            return null;
        }

        // keep everything inside <svg>…</svg>: the defs, the styles and the shapes
        var open = raw.IndexOf('>', raw.IndexOf("<svg", StringComparison.OrdinalIgnoreCase));
        var close = raw.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
        if (open < 0 || close < 0 || close <= open) return null;

        return new Piece(width, height, raw[(open + 1)..close]);
    }

    /// <summary>
    /// The piece as a REUSABLE definition, recoloured once. Inlining the artwork at
    /// every button gave a 77 KB file for eight buttons - times 1591 arcade games, that
    /// is 120 MB of theme folder. A panel uses two or three colours, so the artwork is
    /// defined once per colour and referenced afterwards.
    /// </summary>
    public static string Define(Piece piece, string key, string? color)
    {
        var body = Isolate(piece.Body, key);
        if (color is { Length: > 0 }) body = Recolor(body, color);
        return $"<g id=\"art-{key}\">{body}</g>";
    }

    /// <summary>Places a definition: no artwork is copied, only referenced.</summary>
    public static string Use(Piece piece, string key, double x, double y, double size, double opacity)
    {
        var scale = size / Math.Max(piece.Width, piece.Height);
        var offsetX = x - piece.Width * scale / 2;
        var offsetY = y - piece.Height * scale / 2;
        return string.Create(CultureInfo.InvariantCulture,
            $"<use href=\"#art-{key}\" transform=\"translate({offsetX:0.##} {offsetY:0.##}) scale({scale:0.####})\" opacity=\"{opacity:0.##}\"/>");
    }

    /// <summary>Suffixes every id, every url(#id) and every CSS class so two instances
    /// never share a gradient or a rule.</summary>
    private static string Isolate(string body, string key)
    {
        var ids = IdPattern.Matches(body).Select(m => m.Groups[1].Value).Distinct().ToList();
        foreach (var id in ids)
        {
            body = body.Replace($"id=\"{id}\"", $"id=\"{id}-{key}\"")
                       .Replace($"url(#{id})", $"url(#{id}-{key})")
                       .Replace($"xlink:href=\"#{id}\"", $"xlink:href=\"#{id}-{key}\"")
                       .Replace($"href=\"#{id}\"", $"href=\"#{id}-{key}\"");
        }

        // CSS classes are global too: .cls-1 in two instances is one rule for both.
        var classes = Regex.Matches(body, @"\.(cls-[\w-]+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // the rules first…
        foreach (var css in classes)
        {
            body = Regex.Replace(body, $@"\.{Regex.Escape(css)}\b", $".{css}-{key}");
        }

        // …then the attributes, in ONE pass per class="a b c". Renaming class by class
        // suffixed the same token twice - the next rule matched the "cls-1" it had just
        // written inside "cls-1-c0" - so every element pointed at a rule that did not
        // exist and every fill fell back to black.
        return Regex.Replace(body, "class=\"([^\"]*)\"", match =>
        {
            var tokens = match.Groups[1].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(token => classes.Contains(token) ? $"{token}-{key}" : token);
            return $"class=\"{string.Join(' ', tokens)}\"";
        });
    }

    /// <summary>Transfers the target hue onto every saturated colour, keeping each one's
    /// saturation and lightness - the shading survives, only the colour family moves.</summary>
    private static string Recolor(string body, string color)
    {
        if (!TryParseHex(color, out var r, out var g, out var b)) return body;
        var (targetHue, targetSaturation, targetLightness) = ToHsl(r, g, b);

        // The artwork's own level has to move too. Transferring only the hue kept the
        // RED ramp's lightness, so a button declared White came out dark grey - the
        // right hue, at the wrong level. The ramp is therefore re-centred on the target:
        // each nuance keeps its DISTANCE to the middle, so the highlight, the body and
        // the shadow survive, and the button as a whole lands where its colour says.
        var levels = HexPattern.Matches(body)
            .Select(m => TryParseHex(m.Value, out var lr, out var lg, out var lb) ? ToHsl(lr, lg, lb) : (0, 0, -1))
            .Where(x => x.Item2 >= 0.15 && x.Item3 >= 0)
            .Select(x => x.Item3)
            .ToList();
        var middle = levels.Count > 0 ? levels.Average() : 0.5;

        return HexPattern.Replace(body, match =>
        {
            if (!TryParseHex(match.Value, out var sr, out var sg, out var sb)) return match.Value;
            var (_, saturation, lightness) = ToHsl(sr, sg, sb);
            // near-grey stays grey: the rim, the specular and the shadows are not part
            // of the coloured plastic
            if (saturation < 0.15) return match.Value;

            var level = Math.Clamp(targetLightness + (lightness - middle), 0.04, 0.97);
            // a near-neutral target (a WHITE button) must stay neutral: transferring its
            // hue amplified the faint blue of its hex and turned the plastic bluish
            if (targetSaturation < 0.08) return FromHsl(0, 0, level);
            var blended = Math.Clamp(saturation * 0.35 + targetSaturation * 0.65, 0, 1);
            return FromHsl(targetHue, blended, level);
        });
    }

    private static bool TryParseHex(string value, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var hex = value.TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6) return false;
        return int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
               && int.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
               && int.TryParse(hex[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(int r, int g, int b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var lightness = (max + min) / 2;
        if (Math.Abs(max - min) < 0.0001) return (0, 0, lightness);

        var delta = max - min;
        var saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double hue;
        if (Math.Abs(max - rd) < 0.0001) hue = (gd - bd) / delta + (gd < bd ? 6 : 0);
        else if (Math.Abs(max - gd) < 0.0001) hue = (bd - rd) / delta + 2;
        else hue = (rd - gd) / delta + 4;
        return (hue / 6, saturation, lightness);
    }

    private static string FromHsl(double hue, double saturation, double lightness)
    {
        double r, g, b;
        if (saturation < 0.0001)
        {
            r = g = b = lightness;
        }
        else
        {
            var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
            var p = 2 * lightness - q;
            r = HueToChannel(p, q, hue + 1.0 / 3);
            g = HueToChannel(p, q, hue);
            b = HueToChannel(p, q, hue - 1.0 / 3);
        }

        return $"#{(int)Math.Round(r * 255):x2}{(int)Math.Round(g * 255):x2}{(int)Math.Round(b * 255):x2}";
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
