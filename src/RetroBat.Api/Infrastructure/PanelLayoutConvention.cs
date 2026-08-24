namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Where the buttons of a RetroBat cabinet physically sit - the "retrobat_standard"
/// convention every dynpanel declares (`panel.convention`).
///
/// The convention is APIExpose's: dynpanels are built on it, and LedManager's own
/// layout file names its source as "panel_curator LAYOUT_SLOTS / dynpanels convention
/// retrobat_standard". It lives here because a consumer must be able to draw a panel
/// WITHOUT LedManager installed - that is the whole point of the wiring check, and a
/// cabinet with no LED board still deserves to know whether its buttons are wired
/// right.
///
/// The arrangement holds from 2 to 8 buttons without rewiring: each button keeps its
/// RetroBat identity, and growing the panel only ADDS columns - it never moves an
/// existing button. That property is what makes a single convention usable across
/// every cabinet size.
/// </summary>
public static class PanelLayoutConvention
{
    public const string Id = "retrobat_standard";

    /// <summary>Slot → the RetroBat button it answers to.</summary>
    private static readonly (int Slot, string Button)[] Identities =
    {
        (1, "A"), (2, "B"), (3, "X"), (4, "Y"),
        (5, "L1"), (6, "R1"), (7, "L2"), (8, "R2")
    };

    /// <summary>Rows, top to bottom, left to right within each row.</summary>
    private static readonly Dictionary<int, int[][]> RowsByButtonCount = new()
    {
        [2] = new[] { new[] { 1, 2 } },
        [4] = new[] { new[] { 4, 3 }, new[] { 1, 2 } },
        [6] = new[] { new[] { 4, 3, 5 }, new[] { 1, 2, 6 } },
        [8] = new[] { new[] { 4, 3, 5, 7 }, new[] { 1, 2, 6, 8 } }
    };

    /// <summary>The rows a cabinet of this size draws, top to bottom.</summary>
    public static int[][] RowsFor(int buttonsPerPlayer) => RowsByButtonCount[Normalize(buttonsPerPlayer)];

    /// <summary>
    /// The layout for a cabinet carrying <paramref name="buttonsPerPlayer"/> buttons.
    /// An unusual count rounds UP to the next known arrangement (a 5-button panel is
    /// drawn on the 6-button one, its spare place simply unused) rather than falling
    /// back to nothing: a consumer must always have somewhere to put the buttons.
    /// </summary>
    public static object Describe(int buttonsPerPlayer)
    {
        var count = Normalize(buttonsPerPlayer);
        return new
        {
            Convention = Id,
            ButtonCount = count,
            RequestedButtonCount = buttonsPerPlayer,
            Rows = RowsByButtonCount[count],
            Buttons = Identities.Take(count).Select(x => new { x.Slot, RetroBat = x.Button }).ToArray(),
            // SELECT then START, top-left of the panel: the wiring plan drives them on
            // their own pins, so they are not part of the numbered rows.
            SystemButtons = new { Position = "top_left", Order = new[] { "SELECT", "START" } }
        };
    }

    private static int Normalize(int buttonsPerPlayer)
    {
        foreach (var known in RowsByButtonCount.Keys.OrderBy(x => x))
            if (buttonsPerPlayer <= known)
                return known;
        return 8;
    }
}
