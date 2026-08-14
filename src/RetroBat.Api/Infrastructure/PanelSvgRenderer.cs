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

    /// <summary>How far below the row midline the front view's base sits — resting
    /// exactly on the midline read a touch high.</summary>
    private const double StickBaseDrop = 14;

    /// <summary>Ball top when the panel says nothing about it.</summary>
    private const string DefaultStickColor = "#2b2f38";

    /// <summary>A button as it must be drawn: its slot, what it does here, its colour.</summary>
    public sealed record Button(int Slot, string Label, string Function, string Color, bool Used);

    /// <summary>Where a button ENDED UP in the drawing, in viewBox units.</summary>
    public sealed record Placement(int Slot, double Cx, double Cy, double R);

    /// <summary>
    /// One rendered view: the markup, its size, and where each button landed.
    ///
    /// The placements travel with the drawing on purpose. A consumer that lights a
    /// button — the marquee panel — must put its light exactly where the artwork put
    /// the button; recomputing that geometry on its side would be the same maths in two
    /// places, and the day one of them moves a row, the lights land next to the buttons.
    /// </summary>
    public sealed record RenderedPanel(string Svg, double Width, double Height, IReadOnlyList<Placement> Buttons);

    /// <summary>Both written views, with the geometry a consumer needs to light them.</summary>
    public sealed record WrittenPanels(string? TopPath, RenderedPanel? Top, string? FrontPath, RenderedPanel? Front);

    /// <summary>
    /// Renders and writes both copies. Returns the canonical path, or null when nothing
    /// could be written — a theme that finds no file simply shows nothing, which is
    /// better than a half-drawn panel.
    /// </summary>
    public static WrittenPanels Write(string systemId, string? gameName, int buttonsPerPlayer,
        bool hasStick, IReadOnlyDictionary<int, Button> buttons, string? stickColor = null, ILogger? logger = null)
    {
        try
        {
            var stem = string.IsNullOrWhiteSpace(gameName) ? "default" : Safe(gameName);

            // the 3D front view uses the SAME coordinates: same panel, seen from the
            // front instead of from above, so a theme can pick whichever fits its slot
            var front = Render(buttonsPerPlayer, hasStick, buttons, stickColor, "iso-button.svg", "iso-joystick.svg",
                // the artwork's perspective matches the buttons' now, so the stick is
                // sized for balance rather than to compensate for it
                stickHeightRatio: 0.45);
            var frontPath = WritePair(systemId, stem + "-3d", front.Svg, logger);

            var top = Render(buttonsPerPlayer, hasStick, buttons, stickColor);
            var relative = Path.Combine(Safe(systemId), stem + ".svg");

            // same two roots as the panel theme XML (PanelsCatalogService): a theme finds
            // <game>.xml and <game>.svg side by side, in the folder it already reads
            var canonical = Path.Combine(RetroBatPaths.ThemePanelsResourcesRoot, relative);
            WriteFile(canonical, top.Svg);

            // the mirror is best-effort: a cabinet without EmulationStation installed
            // still gets its canonical copy
            try
            {
                WriteFile(Path.Combine(RetroBatPaths.EmulationStationPanelsThemeRoot, relative), top.Svg);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Panel SVG mirror not written for {System}/{Game}", systemId, gameName);
            }

            return new WrittenPanels(canonical, top, frontPath, front);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Unable to render the panel SVG for {System}/{Game}", systemId, gameName);
            return new WrittenPanels(null, null, null, null);
        }
    }

    /// <summary>Writes one variant to both roots, best effort on the mirror. Returns the
    /// canonical path, or null when even that could not be written.</summary>
    private static string? WritePair(string systemId, string stem, string svg, ILogger? logger)
    {
        try
        {
            var relative = Path.Combine(Safe(systemId), stem + ".svg");
            var canonical = Path.Combine(RetroBatPaths.ThemePanelsResourcesRoot, relative);
            WriteFile(canonical, svg);
            try
            {
                WriteFile(Path.Combine(RetroBatPaths.EmulationStationPanelsThemeRoot, relative), svg);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Panel SVG mirror not written for {System}/{Stem}", systemId, stem);
            }

            return canonical;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Panel SVG variant not written for {System}/{Stem}", systemId, stem);
            return null;
        }
    }

    /// <summary>
    /// Written aside, then MOVED into place. Writing straight to the file leaves it
    /// half-written for a few milliseconds, and this file is read live: a consumer that
    /// looks while a game is launching — exactly when the panel is rewritten — gets a
    /// truncated SVG and shows nothing. The move is atomic, so a reader sees either the
    /// old drawing or the new one, never half of one.
    /// </summary>
    private static void WriteFile(string path, string svg)
    {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);

        var staging = Path.Combine(folder, $".{Path.GetFileName(path)}.{Environment.CurrentManagedThreadId}.tmp");
        try
        {
            File.WriteAllText(staging, svg, new UTF8Encoding(false));
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
            {
                try { File.Delete(staging); } catch { /* a leftover temp file harms nothing */ }
            }
        }
    }

    /// <summary>
    /// The drawing itself. A button the current game does not use is still drawn — at
    /// 20 % opacity — because the panel must tell the truth about the CABINET: you see
    /// your eight holes, and which of them this game speaks to.
    /// </summary>
    public static RenderedPanel Render(int buttonsPerPlayer, bool hasStick, IReadOnlyDictionary<int, Button> buttons,
        string? stickColor = null, string buttonFile = "top-button-v2.svg", string stickFile = "top-joystick.svg",
        double stickHeightRatio = 0)
    {
        var placements = new List<Placement>();
        var layout = PanelLayoutConvention.RowsFor(buttonsPerPlayer);
        var columns = layout.Max(row => row.Length);
        var height = Margin * 2 + layout.Length * (ButtonRadius * 2) + (layout.Length - 1) * RowGap + 46;
        var top = Margin + 30; // the rows start below the top margin

        // A stick seen from the FRONT is a tall object: sized like the round top view it
        // came out tiny next to the buttons. Given a height ratio, it is measured against
        // the panel instead, and the room reserved for it follows its real proportions —
        // otherwise it would spill over the first column.
        var stickArtPiece = hasStick ? PanelSvgArtwork.Load(stickFile) : null;
        var stickHeight = stickHeightRatio > 0 ? height * stickHeightRatio : StickRadius * 2;
        var stickDrawnWidth = stickArtPiece is not null && stickHeightRatio > 0
            ? stickHeight * stickArtPiece.Width / stickArtPiece.Height
            : StickRadius * 2;
        var stickWidth = hasStick ? stickDrawnWidth + ButtonGap * 2 : 0;
        var width = Margin * 2 + stickWidth + columns * (ButtonRadius * 2) + (columns - 1) * ButtonGap;

        // A stick seen from the front stands ON the panel: its BASE belongs between the
        // two rows of buttons, not its middle. Raising it that far pushes its ball above
        // the frame — the drawing gains the room it needs, and it must be claimed HERE,
        // before the viewBox is written, or everything below simply falls outside it.
        var rowsMiddle = top + layout.Length * ButtonRadius + (layout.Length - 1) * RowGap / 2.0;
        if (hasStick && stickHeightRatio > 0)
        {
            var headroom = Margin - (rowsMiddle + StickBaseDrop - stickHeight);
            if (headroom > 0)
            {
                top += headroom;
                rowsMiddle += headroom;
                height += headroom;
            }
        }

        var svg = new StringBuilder();
        // xlink must be declared here: the artwork's own root carries it, and only its
        // BODY is kept — without this the gradients referenced by xlink:href resolve to
        // nothing and the file fails to parse.
        svg.Append(Inv($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 {width:0.#} {height:0.#}\" "))
           .Append(Inv($"width=\"{width:0.#}\" height=\"{height:0.#}\" role=\"img\">"))
           .Append("<style>.f{font:600 13px 'Segoe UI',sans-serif;fill:#fff;text-anchor:middle}"
                   + ".s{font:600 11px 'Segoe UI',sans-serif;fill:#cfd3dc;text-anchor:middle}</style>");


        // real artwork when it is there, plain shapes otherwise: a cabinet whose theme
        // images were removed still gets a readable panel
        var buttonArt = PanelSvgArtwork.Load(buttonFile);
        var stickArt = stickArtPiece;

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
            if (stickArt is not null) defs.Append(PanelSvgArtwork.Define(stickArt, "stick", string.IsNullOrWhiteSpace(stickColor) ? DefaultStickColor : WebColor(stickColor)));
            svg.Append("<defs>").Append(defs).Append("</defs>");
        }
        else if (stickArt is not null)
        {
            svg.Append("<defs>").Append(PanelSvgArtwork.Define(stickArt, "stick", string.IsNullOrWhiteSpace(stickColor) ? DefaultStickColor : WebColor(stickColor))).Append("</defs>");
        }

        if (hasStick)
        {
            var cx = Margin + stickDrawnWidth / 2;
            // base on the row midline for the front view, plain centre for the top view
            var cy = stickHeightRatio > 0 ? rowsMiddle + StickBaseDrop - stickHeight / 2 : rowsMiddle;
            if (stickArt is not null)
            {
                svg.Append(PanelSvgArtwork.Use(stickArt, "stick", cx, cy, stickHeight, 1.0));
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
                placements.Add(new Placement(slot, cx, cy, ButtonRadius));
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

                // no number on the cap: it read as the GAME's button number when the game
                // used it, and as the physical slot when it did not — two different
                // meanings in one drawing. The function underneath says what it does.

                if (used && !string.IsNullOrWhiteSpace(button!.Function))
                {
                    svg.Append(Inv($"<text class=\"s\" x=\"{cx:0.#}\" y=\"{cy + ButtonRadius + 16:0.#}\">{Escape(button.Function)}</text>"));
                }

                svg.Append("</g>");
            }
        }

        return new RenderedPanel(svg.Append("</svg>").ToString(), width, height, placements);
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
            "yellow" => "#e0b038", "white" => "#e9e9e9", "black" => "#1a1c22",
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
