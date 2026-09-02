using System.Drawing;

namespace RetroBat.Api.Replay.Overlay;

/// <summary>
/// Table d'AFFICHAGE des réactions (emoji + mot + couleur de famille). Le moteur n'émet que
/// famille + niveau ; ici on résout le rendu. Base FR (les 6 langues = table à part ensuite).
/// Un emoji + un mot DISTINCTS par niveau (l'intensité est une progression d'émotions).
/// </summary>
public static class ReplayReactionText
{
    // famille -> 3 niveaux (emoji, mot). Index 0..2 = niveaux 1..3.
    private static readonly IReadOnlyDictionary<string, (string Emoji, string Word)[]> Fr =
        new Dictionary<string, (string, string)[]>(StringComparer.Ordinal)
        {
            ["hype"] = new[] { ("🔥", "Hype"), ("⚡", "Combo !"), ("🚀", "ON FIRE") },
            ["wow"] = new[] { ("😮", "Nice!"), ("🤯", "Incroyable !"), ("👑", "GODLIKE") },
            ["respect"] = new[] { ("👏", "GG"), ("😎", "Stylish"), ("🫡", "Respect") },
            ["laugh"] = new[] { ("😄", "LOL"), ("😂", "MDR"), ("🤣", "KO de rire") },
            ["tension"] = new[] { ("😬", "Ouh…"), ("😰", "Danger !"), ("😱", "NOOOON !") },
            ["ouch"] = new[] { ("😖", "Aïe"), ("💥", "Punition"), ("💀", "FATAL") },
            ["love"] = new[] { ("❤️", "Love"), ("🥰", "Fan !"), ("🥹", "Masterpiece") },
            ["rage"] = new[] { ("🧂", "Salé"), ("😤", "Rage"), ("🤬", "RAGEQUIT") },
            ["celebrate"] = new[] { ("🎉", "PARTY"), ("🎊", "JACKPOT"), ("🏆", "VICTORY!") },
        };

    private static readonly IReadOnlyDictionary<string, Color> FamilyColor =
        new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["hype"] = ColorTranslator.FromHtml("#FF6B35"),
            ["wow"] = ColorTranslator.FromHtml("#B98CFF"),
            ["respect"] = ColorTranslator.FromHtml("#5EA0FF"),
            ["laugh"] = ColorTranslator.FromHtml("#FFD23F"),
            ["tension"] = ColorTranslator.FromHtml("#FF5C7A"),
            ["ouch"] = ColorTranslator.FromHtml("#8AB4E8"),
            ["love"] = ColorTranslator.FromHtml("#FF4D6D"),
            ["rage"] = ColorTranslator.FromHtml("#FF4040"),
            ["celebrate"] = ColorTranslator.FromHtml("#F5B940"),
        };

    /// <summary>Emoji + mot pour une famille/niveau (langue = FR pour l'instant).</summary>
    public static (string Emoji, string Word) Resolve(string family, int level, string lang = "fr")
    {
        if (Fr.TryGetValue(family, out var levels))
        {
            var i = Math.Clamp(level - 1, 0, levels.Length - 1);
            return levels[i];
        }
        return ("✨", family);
    }

    public static Color ColorOf(string family) =>
        FamilyColor.TryGetValue(family, out var c) ? c : ColorTranslator.FromHtml("#5EA0FF");
}
