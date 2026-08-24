using RetroBat.Domain.Models;

namespace RetroBat.Api.Media;

/// <summary>
/// LOT 2 - the shared media QUALIFICATION, lifted out of RomsMediaCanonicalMigrationHostedService
/// so a file can be typed WITHOUT moving it or writing a gamelist. It owns the filename-suffix
/// table and the folder conventions that used to live inside the migration; the migration now
/// delegates here, so there is ONE source of truth for "what kind is this file?".
///
/// Order of certainty (§7.2): an explicit gamelist tag or provider type wins over any of this;
/// what this service covers is the file-based tail - filename convention, then folder convention.
/// A generic gamelist slot (&lt;marquee&gt;…&lt;/marquee&gt;) is NEVER enough on its own to make a
/// Kind: the Kind only comes from qualifying the file the slot points at.
/// </summary>
public sealed class MediaQualificationService
{
    // Suffix -> Kind, in PRIORITY order: the most specific suffix wins, so "-video-normalized" is
    // matched before "-video", "-screenmarqueesmall" before "-screenmarquee" before "-marquee",
    // and every specific box face before "-box". Moved verbatim from the migration; the ORDER is
    // the "suffix precedence" rule and must not be sorted.
    private static readonly (string Suffix, string Kind)[] SuffixKinds =
    [
        ("-video-normalized", MediaKinds.VideoNormalized),
        ("-screenmarqueesmall", MediaKinds.ScreenMarqueeSmall),
        ("-screenmarquee", MediaKinds.ScreenMarquee),
        ("-wheelcarbon", MediaKinds.WheelCarbon),
        ("-wheelsteel", MediaKinds.WheelSteel),
        ("-boxtexture", MediaKinds.BoxTexture),
        ("-steamgrid", MediaKinds.SteamGrid),
        ("-mixrbv1", MediaKinds.MixRbv1),
        ("-mixrbv2", MediaKinds.MixRbv2),
        ("-thumbnail", MediaKinds.Thumbnail),
        ("-screenshot", MediaKinds.Thumbnail),
        ("-titleshot", MediaKinds.Image),
        ("-boxside", MediaKinds.BoxSide),
        ("-figurine", MediaKinds.Figurine),
        ("-cartridge", MediaKinds.Cartridge),
        ("-support2d", MediaKinds.Cartridge),
        ("-supporttexture", MediaKinds.Label),
        ("-support-texture", MediaKinds.Label),
        ("-label", MediaKinds.Label),
        ("-themehb", MediaKinds.ThemeHb),
        ("-marquee", MediaKinds.Marquee),
        ("-boxback", MediaKinds.BoxBack),
        ("-boxfront", MediaKinds.BoxFront),
        ("-box2d", MediaKinds.BoxFront),
        ("-box3d", MediaKinds.Box3d),
        ("-box", MediaKinds.BoxFront),
        ("-fanart", MediaKinds.Fanart),
        ("-bezel", MediaKinds.Bezel),
        ("-image", MediaKinds.Image),
        ("-thumb", MediaKinds.Thumbnail),
        ("-logo", MediaKinds.Logo),
        ("-wheel", MediaKinds.Wheel),
        ("-flyer", MediaKinds.Flyer),
        ("-manual", MediaKinds.Manual),
        ("-magazine", MediaKinds.Magazine),
        ("-video", MediaKinds.Video),
        ("-map", MediaKinds.Map),
        ("-mix", MediaKinds.MixRbv2)
    ];

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".webm"];

    /// <summary>
    /// Type <paramref name="fileStem"/> sitting in a legacy media folder
    /// (<paramref name="legacyFolder"/> = images / videos / manuals / themehb / themes), and
    /// return the base name it projects to. Filename suffix first (filename-convention), then the
    /// folder's default (folder-convention). False when nothing is determinable, or when a suffix
    /// matched a kind the folder does not allow (e.g. a "-video" file under images/). The result is
    /// identical to the migration's former private logic; <paramref name="qualification"/> reports
    /// which source decided it.
    /// </summary>
    public bool TryQualify(
        string legacyFolder,
        string fileStem,
        string extension,
        out string projectionBaseName,
        out string kind,
        out string qualification)
    {
        projectionBaseName = string.Empty;
        kind = string.Empty;
        qualification = string.Empty;

        foreach (var (suffix, candidateKind) in SuffixKinds)
        {
            if (!fileStem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsKindAllowedForFolder(candidateKind, legacyFolder))
            {
                return false;
            }

            projectionBaseName = fileStem[..^suffix.Length];
            kind = candidateKind;
            qualification = MediaQualifications.FilenameConvention;
            return !string.IsNullOrWhiteSpace(projectionBaseName);
        }

        if (legacyFolder.Equals("themehb", StringComparison.OrdinalIgnoreCase) ||
            legacyFolder.Equals("themes", StringComparison.OrdinalIgnoreCase))
        {
            projectionBaseName = fileStem;
            kind = MediaKinds.ThemeHb;
            qualification = MediaQualifications.FolderConvention;
            return !string.IsNullOrWhiteSpace(projectionBaseName);
        }

        if (TryResolveDefaultKind(legacyFolder, extension, out kind))
        {
            projectionBaseName = fileStem;
            qualification = MediaQualifications.FolderConvention;
            return !string.IsNullOrWhiteSpace(projectionBaseName);
        }

        return false;
    }

    private static bool TryResolveDefaultKind(string legacyFolder, string extension, out string kind)
    {
        kind = string.Empty;
        var normalizedFolder = legacyFolder.ToLowerInvariant();
        if (normalizedFolder is "videos" && VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            kind = MediaKinds.Video;
            return true;
        }

        if (normalizedFolder is "manuals" && extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            kind = MediaKinds.Manual;
            return true;
        }

        if (normalizedFolder is "images" && ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            kind = MediaKinds.Thumbnail;
            return true;
        }

        return false;
    }

    private static bool IsKindAllowedForFolder(string kind, string legacyFolder)
    {
        return legacyFolder.ToLowerInvariant() switch
        {
            "images" => kind is not MediaKinds.Video and not MediaKinds.VideoNormalized and not MediaKinds.Manual and not MediaKinds.ThemeHb,
            "videos" => kind is MediaKinds.Video or MediaKinds.VideoNormalized,
            "manuals" => kind is MediaKinds.Manual,
            "themehb" or "themes" => kind is MediaKinds.ThemeHb,
            _ => false
        };
    }

    // Durable gamelist tags whose NAME already names a kind (§7.2 source 1: explicit-gamelist).
    // The generic slots image / marquee / thumbnail are deliberately ABSENT (§7.3): they never
    // imply a kind on their own - the kind comes from qualifying the file they point at.
    private static readonly IReadOnlyDictionary<string, string> GamelistTagKinds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fanart"] = MediaKinds.Fanart,
            ["video"] = MediaKinds.Video,
            ["manual"] = MediaKinds.Manual,
            ["magazine"] = MediaKinds.Magazine,
            ["map"] = MediaKinds.Map,
            ["bezel"] = MediaKinds.Bezel,
            ["cartridge"] = MediaKinds.Cartridge,
            ["boxart"] = MediaKinds.BoxFront,
            ["box"] = MediaKinds.BoxFront,
            ["titleshot"] = MediaKinds.Image,
            ["mix"] = MediaKinds.MixRbv2
        };

    /// <summary>The KIND a DURABLE gamelist tag names on its own (explicit-gamelist). False for the
    /// generic slots image / marquee / thumbnail, which never imply a kind (§7.3).</summary>
    public bool TryQualifyByGamelistTag(string tag, out string kind)
    {
        if (!string.IsNullOrWhiteSpace(tag) && GamelistTagKinds.TryGetValue(tag, out var found))
        {
            kind = found;
            return true;
        }

        kind = string.Empty;
        return false;
    }

    /// <summary>The KIND a file name suffix implies, IGNORING the folder - for a gamelist medium
    /// that can live in any folder (downloaded_images, media, …) where the tag is the authority and
    /// the file name is only a secondary signal. Shares the same suffix table (and its precedence)
    /// as <see cref="TryQualify"/>. False when no known suffix matches.</summary>
    public bool TryQualifyByFilename(string fileStem, out string kind)
    {
        foreach (var (suffix, candidateKind) in SuffixKinds)
        {
            if (fileStem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidateKind;
                return true;
            }
        }

        kind = string.Empty;
        return false;
    }
}
