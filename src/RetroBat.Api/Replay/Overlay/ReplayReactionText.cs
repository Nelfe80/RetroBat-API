using System.Drawing;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Models;

namespace RetroBat.Api.Replay.Overlay;

/// <summary>Marqueur « majoritaire » d'un cluster de réactions sur la timeline (scrubber).</summary>
public readonly record struct ReactionMarker(long Frame, string Family, int Level, string Name);

/// <summary>
/// Table d'AFFICHAGE des réactions (emoji + mot + couleur de famille). Le moteur n'émet que
/// famille + niveau ; ici on résout le rendu. Un emoji + un mot DISTINCTS par niveau (l'intensité
/// est une progression d'émotions).
///
/// L'EMOJI est universel (même planche pour tous) ; seul le MOT est localisé, dans les mêmes six
/// langues que le reste de la borne (en, fr, es, ja, zh, ko - cf. <see cref="CabinetAnnounceText"/>).
/// La langue passée est normalisée puis, clé par clé, on retombe sur l'anglais si un mot manque
/// (un niveau ajouté au français sans traduction s'affiche en anglais plutôt que de disparaître).
/// </summary>
public static class ReplayReactionText
{
    // famille -> emoji des 3 niveaux (index 0..2 = niveaux 1..3). Universel, non traduit.
    private static readonly IReadOnlyDictionary<string, string[]> Emojis =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["hype"] = new[] { "🔥", "⚡", "🚀" },
            ["wow"] = new[] { "😮", "🤯", "👑" },
            ["respect"] = new[] { "👏", "😎", "🫡" },
            ["laugh"] = new[] { "😄", "😂", "🤣" },
            ["tension"] = new[] { "😬", "😰", "😱" },
            ["ouch"] = new[] { "😖", "💥", "💀" },
            ["love"] = new[] { "❤️", "🥰", "🥹" },
            ["rage"] = new[] { "🧂", "😤", "🤬" },
            ["celebrate"] = new[] { "🎉", "🎊", "🏆" },
        };

    // langue -> (famille -> mot des 3 niveaux). Les termes de jeu universels (GG, LOL, Combo,
    // Hype, RAGEQUIT…) restent tels quels là où c'est l'usage ; le reste est traduit.
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> Words =
        new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.Ordinal)
        {
            ["fr"] = Fam(
                ("hype", "Hype", "Combo !", "ON FIRE"),
                ("wow", "Nice !", "Incroyable !", "GODLIKE"),
                ("respect", "GG", "Stylé", "Respect"),
                ("laugh", "LOL", "MDR", "KO de rire"),
                ("tension", "Ouh…", "Danger !", "NOOOON !"),
                ("ouch", "Aïe", "Punition", "FATAL"),
                ("love", "Coup de cœur", "Fan !", "Chef-d'œuvre"),
                ("rage", "Salé", "Rage", "RAGEQUIT"),
                ("celebrate", "FÊTE", "JACKPOT", "VICTOIRE !")),
            ["en"] = Fam(
                ("hype", "Hype", "Combo!", "ON FIRE"),
                ("wow", "Nice!", "Insane!", "GODLIKE"),
                ("respect", "GG", "Stylish", "Respect"),
                ("laugh", "LOL", "ROFL", "Dying"),
                ("tension", "Uh-oh…", "Danger!", "NOOO!"),
                ("ouch", "Ouch", "Punished", "FATAL"),
                ("love", "Love", "Fan!", "Masterpiece"),
                ("rage", "Salty", "Rage", "RAGEQUIT"),
                ("celebrate", "PARTY", "JACKPOT", "VICTORY!")),
            ["es"] = Fam(
                ("hype", "Hype", "¡Combo!", "EN LLAMAS"),
                ("wow", "¡Qué bueno!", "¡Increíble!", "DIVINO"),
                ("respect", "GG", "Con estilo", "Respeto"),
                ("laugh", "LOL", "JAJAJA", "Muerto de risa"),
                ("tension", "Uy…", "¡Peligro!", "¡NOOO!"),
                ("ouch", "¡Ay!", "Castigo", "FATAL"),
                ("love", "Me encanta", "¡Fan!", "Obra maestra"),
                ("rage", "Salado", "Rabia", "RAGEQUIT"),
                ("celebrate", "¡FIESTA!", "¡PREMIO!", "¡VICTORIA!")),
            ["ja"] = Fam(
                ("hype", "ハイプ", "コンボ！", "大炎上"),
                ("wow", "いいね！", "ヤバい！", "神プレイ"),
                ("respect", "GG", "シブい", "リスペクト"),
                ("laugh", "www", "大爆笑", "腹筋崩壊"),
                ("tension", "おっと…", "危ない！", "ダメーッ！"),
                ("ouch", "いてっ", "お仕置き", "即死"),
                ("love", "好き", "ファン！", "傑作"),
                ("rage", "しょっぱい", "激おこ", "ブチギレ"),
                ("celebrate", "パーティ", "大当たり", "勝利！")),
            ["zh"] = Fam(
                ("hype", "燃", "连击！", "火力全开"),
                ("wow", "漂亮！", "太强了！", "封神"),
                ("respect", "GG", "有型", "尊敬"),
                ("laugh", "哈哈", "笑死", "笑到断气"),
                ("tension", "哎呀…", "危险！", "不要啊！"),
                ("ouch", "疼", "惩罚", "秒杀"),
                ("love", "爱了", "粉了！", "神作"),
                ("rage", "酸", "暴怒", "掀桌"),
                ("celebrate", "派对", "大奖", "胜利！")),
            ["ko"] = Fam(
                ("hype", "하이프", "콤보!", "활활"),
                ("wow", "좋아!", "대박!", "신급"),
                ("respect", "GG", "스타일", "리스펙트"),
                ("laugh", "ㅋㅋ", "빵터짐", "웃겨 죽음"),
                ("tension", "어이쿠…", "위험!", "안돼애!"),
                ("ouch", "아야", "응징", "즉사"),
                ("love", "사랑", "팬!", "명작"),
                ("rage", "짜다", "분노", "킹받네"),
                ("celebrate", "파티", "잭팟", "승리!")),
        };

    // Petit constructeur : liste de (famille, n1, n2, n3) -> dictionnaire famille -> 3 mots.
    private static IReadOnlyDictionary<string, string[]> Fam(params (string Family, string L1, string L2, string L3)[] rows)
    {
        var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var r in rows) { d[r.Family] = new[] { r.L1, r.L2, r.L3 }; }
        return d;
    }

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

    /// <summary>Emoji (universel) + mot (dans la langue résolue, repli anglais) pour une famille/niveau.</summary>
    public static (string Emoji, string Word) Resolve(string family, int level, string lang = "en")
    {
        var i = Math.Clamp(level - 1, 0, 2);
        var emoji = Emojis.TryGetValue(family, out var em) ? em[Math.Clamp(i, 0, em.Length - 1)] : "✨";

        var code = CabinetAnnounceText.Normalize(lang);
        if (code.Length == 0) { code = "en"; }
        var word = WordFor(code, family, i) ?? WordFor("en", family, i) ?? family;
        return (emoji, word);
    }

    private static string? WordFor(string lang, string family, int i) =>
        Words.TryGetValue(lang, out var fams) && fams.TryGetValue(family, out var levels)
            ? levels[Math.Clamp(i, 0, levels.Length - 1)]
            : null;

    public static Color ColorOf(string family) =>
        FamilyColor.TryGetValue(family, out var c) ? c : ColorTranslator.FromHtml("#5EA0FF");

    /// <summary>
    /// Regroupe les réactions en marqueurs « majoritaires » le long du replay (≤ maxMarkers) : par
    /// bac, la FAMILLE la plus fréquente, représentée par son DERNIER react (ts max) → on privilégie
    /// les derniers reacts de la majorité. Le NOM de l'auteur n'est pas encore capté (placeholder).
    /// </summary>
    public static IReadOnlyList<ReactionMarker> Clusterize(IReadOnlyList<ReplayReaction> reactions, long end, int maxMarkers)
    {
        var markers = new List<ReactionMarker>();
        if (reactions.Count == 0 || end <= 0 || maxMarkers <= 0) return markers;

        var bins = new Dictionary<int, List<ReplayReaction>>();
        foreach (var r in reactions)
        {
            var b = (int)Math.Clamp(r.Frame / (double)end * maxMarkers, 0, maxMarkers - 1);
            (bins.TryGetValue(b, out var list) ? list : bins[b] = new List<ReplayReaction>()).Add(r);
        }

        foreach (var (_, list) in bins)
        {
            // famille majoritaire du bac
            var family = list.GroupBy(r => r.Reaction).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(r => r.TsMs)).First().Key;
            // dernier react de cette famille (ts max) = le représentant
            var rep = list.Where(r => r.Reaction == family).OrderByDescending(r => r.TsMs).First();
            markers.Add(new ReactionMarker(rep.Frame, family, rep.Level, rep.Author ?? "Joueur"));
        }

        markers.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        return markers;
    }
}
