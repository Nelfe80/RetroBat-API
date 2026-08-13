using System.Globalization;
using System.Text;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Draws the control panel as an SVG, so EmulationStation themes can show it.
///
/// Vector on purpose: a theme scales it to whatever slot it has without asking for a
/// rendering size, and it stays sharp on a 4K marquee as on a small card.
///
/// Written twice, to the same relative layout:
///   resources/theme/panels/&lt;system&gt;/&lt;game&gt;.svg   (arcade: one per game)
///   resources/theme/panels/&lt;system&gt;/default.svg   (fallback, any system)
/// and mirrored under the EmulationStation themes folder, which is the only place a
/// theme can reliably read from. The mirror folder starts with a dot so it never
/// mixes with installed themes.
/// </summary>
public static class PanelSvgRenderer
{
    private const double ButtonRadius = 26;
    private const double ButtonGap = 18;
    private const double RowGap = 22;
    private const double Margin = 28;
    private const double StickRadius = 34;

    /// <summary>Neutral ball top: an arcade stick is rarely the colour of a button, and
    /// the buttons are what carry meaning here.</summary>
    private const string StickColor = "#2b2f38";

    /// <summary>A button as it must be drawn: its slot, what it does here, its colour.</summary>
    public sealed record Button(int Slot, string Label, string Function, string Color, bool Used);

    /// <summary>
    /// Renders and writes both copies. Returns the canonical path, or null when nothing
    /// could be written — a theme that finds no file simply shows nothing, which is
    /// better than a half-drawn panel.
    /// </summary>
    public static string? Write(string systemId, string? gameName, int buttonsPerPlayer,
        bool hasStick, IReadOnlyDictionary<int, Button> buttons, ILogger? logger = null)
    {
        try
        {
            var svg = Render(buttonsPerPlayer, hasStick, buttons);
            var relative = Path.Combine(Safe(systemId),
                string.IsNullOrWhiteSpace(gameName) ? "default.svg" : Safe(gameName) + ".svg");

            // same two roots as the panel theme XML (PanelsCatalogService): a theme finds
            // <game>.xml and <game>.svg side by side, in the folder it already reads
            var canonical = Path.Combine(RetroBatPaths.ThemePanelsResourcesRoot, relative);
            WriteFile(canonical, svg);

            // the mirror is best-effort: a cabinet without EmulationStation installed
            // still gets its canonical copy
            try
            {
                WriteFile(Path.Combine(RetroBatPaths.EmulationStationPanelsThemeRoot, relative), svg);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Panel SVG mirror not written for {System}/{Game}", systemId, gameName);
            }

            return canonical;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Unable to render the panel SVG for {System}/{Game}", systemId, gameName);
            return null;
        }
    }

    private static void WriteFile(string path, string svg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, svg, new UTF8Encoding(false));
    }

    /// <summary>
    /// The drawing itself. A button the current game does not use is still drawn — at
    /// 20 % opacity — because the panel must tell the truth about the CABINET: you see
    /// your eight holes, and which of them this game speaks to.
    /// </summary>
    public static string Render(int buttonsPerPlayer, bool hasStick, IReadOnlyDictionary<int, Button> buttons)
    {
        var layout = PanelLayoutConvention.RowsFor(buttonsPerPlayer);
        var columns = layout.Max(row => row.Length);
        var stickWidth = hasStick ? StickRadius * 2 + ButtonGap * 2 : 0;
        var width = Margin * 2 + stickWidth + columns * (ButtonRadius * 2) + (columns - 1) * ButtonGap;
        var height = Margin * 2 + layout.Length * (ButtonRadius * 2) + (layout.Length - 1) * RowGap + 46;

        var svg = new StringBuilder();
        // xlink must be declared here: the artwork's own root carries it, and only its
        // BODY is kept — without this the gradients referenced by xlink:href resolve to
        // nothing and the file fails to parse.
        svg.Append(Inv($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 {width:0.#} {height:0.#}\" "))
           .Append(Inv($"width=\"{width:0.#}\" height=\"{height:0.#}\" role=\"img\">"))
           .Append("<style>.f{font:600 13px 'Segoe UI',sans-serif;fill:#fff;text-anchor:middle}"
                   + ".s{font:600 11px 'Segoe UI',sans-serif;fill:#cfd3dc;text-anchor:middle}</style>");

        var top = Margin + 30; // the system row sits above the buttons

        // SELECT then START, top-left, per the convention: they are wired on their own
        // pins and are not part of the numbered rows.
        svg.Append(Inv($"<text class=\"s\" x=\"{Margin + 22:0.#}\" y=\"{Margin + 12:0.#}\">SELECT</text>"))
           .Append(Inv($"<text class=\"s\" x=\"{Margin + 78:0.#}\" y=\"{Margin + 12:0.#}\">START</text>"));

        // real artwork when it is there, plain shapes otherwise: a cabinet whose theme
        // images were removed still gets a readable panel
        var buttonArt = PanelSvgArtwork.Load("top-button-v2.svg");
        var stickArt = PanelSvgArtwork.Load("top-joystick.svg");

        // One definition per COLOUR, not per button: a panel uses two or three colours,
        // and the artwork weighs 9 KB each time it is copied.
        var colorKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (buttonArt is not null)
        {
            var defs = new StringBuilder();
            foreach (var row in layout)
                foreach (var slot in row)
                {
                    buttons.TryGetValue(slot, out var b);
                    var c = b?.Used == true && !string.IsNullOrWhiteSpace(b.Color) ? WebColor(b.Color) : "#3a3f4b";
                    if (colorKeys.ContainsKey(c)) continue;
                    var key = "c" + colorKeys.Count;
                    colorKeys[c] = key;
                    defs.Append(PanelSvgArtwork.Define(buttonArt, key, c));
                }

            // the stick is neutral: the buttons carry the game's colours, and a red ball
            // on top of them would read as one more function
            if (stickArt is not null) defs.Append(PanelSvgArtwork.Define(stickArt, "stick", StickColor));
            svg.Append("<defs>").Append(defs).Append("</defs>");
        }
        else if (stickArt is not null)
        {
            svg.Append("<defs>").Append(PanelSvgArtwork.Define(stickArt, "stick", StickColor)).Append("</defs>");
        }

        if (hasStick)
        {
            var cx = Margin + StickRadius;
            var cy = top + (layout.Length * ButtonRadius) + ((layout.Length - 1) * RowGap) / 2.0;
            if (stickArt is not null)
            {
                svg.Append(PanelSvgArtwork.Use(stickArt, "stick", cx, cy, StickRadius * 2, 1.0));
            }
            else
            {
                svg.Append(Inv($"<circle cx=\"{cx:0.#}\" cy=\"{cy:0.#}\" r=\"{StickRadius:0.#}\" fill=\"#20232b\" stroke=\"#5b6270\" stroke-width=\"3\"/>"))
                   .Append(Inv($"<circle cx=\"{cx:0.#}\" cy=\"{cy:0.#}\" r=\"9\" fill=\"#5b6270\"/>"));
            }
        }

        for (var r = 0; r < layout.Length; r++)
        {
            var row = layout[r];
            // rows are centred on each other, so a shorter top row sits over the middle
            var rowWidth = row.Length * (ButtonRadius * 2) + (row.Length - 1) * ButtonGap;
            var startX = Margin + stickWidth + ((columns * (ButtonRadius * 2) + (columns - 1) * ButtonGap) - rowWidth) / 2.0;
            var cy = top + ButtonRadius + r * (ButtonRadius * 2 + RowGap);

            for (var c = 0; c < row.Length; c++)
            {
                var slot = row[c];
                var cx = startX + ButtonRadius + c * (ButtonRadius * 2 + ButtonGap);
                buttons.TryGetValue(slot, out var button);
                var used = button?.Used == true;
                var fill = used && !string.IsNullOrWhiteSpace(button!.Color) ? WebColor(button.Color) : "#3a3f4b";

                var opacity = used ? 1.0 : 0.2;
                if (buttonArt is not null)
                {
                    svg.Append(PanelSvgArtwork.Use(buttonArt, colorKeys[fill], cx, cy, ButtonRadius * 2, opacity))
                       .Append(Inv($"<g opacity=\"{opacity:0.##}\">"));
                }
                else
                {
                    svg.Append(Inv($"<g opacity=\"{opacity:0.##}\">"))
                       .Append(Inv($"<circle cx=\"{cx:0.#}\" cy=\"{cy:0.#}\" r=\"{ButtonRadius:0.#}\" fill=\"{fill}\" stroke=\"#0d0f13\" stroke-width=\"2\"/>"));
                }

                svg.Append(Inv($"<text class=\"f\" x=\"{cx:0.#}\" y=\"{cy + 5:0.#}\">{Escape(button?.Label ?? slot.ToString())}</text>"));

                if (used && !string.IsNullOrWhiteSpace(button!.Function))
                {
                    svg.Append(Inv($"<text class=\"s\" x=\"{cx:0.#}\" y=\"{cy + ButtonRadius + 16:0.#}\">{Escape(button.Function)}</text>"));
                }

                svg.Append("</g>");
            }
        }

        return svg.Append("</svg>").ToString();
    }

    /// <summary>Named colours from the dynpanels ("Red", "Blue") become web colours;
    /// anything already usable passes through untouched.</summary>
    private static string WebColor(string color)
    {
        var value = color.Trim();
        if (value.StartsWith('#')) return Escape(value);
        return value.ToLowerInvariant() switch
        {
            "red" => "#d64545", "blue" => "#3d6fd6", "green" => "#3fa650",
            "yellow" => "#e0b038", "white" => "#e8eaed", "black" => "#1a1c22",
            "orange" => "#e08a38", "purple" => "#8a5cd6", "pink" => "#d65c9e",
            "cyan" => "#3fb6c4", "magenta" => "#c43fb6",
            _ => "#3a3f4b"
        };
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string value) => value
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().ToLowerInvariant().Where(c => !invalid.Contains(c)).ToArray());
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }
}
