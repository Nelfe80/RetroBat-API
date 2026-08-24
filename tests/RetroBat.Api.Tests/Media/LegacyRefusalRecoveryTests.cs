using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// LOT 1 - the one-shot es_settings recovery that undoes an OLD migration-refusal. This is the
/// risky part: it must re-enable a borne the removed cascade left crippled, WITHOUT ever
/// overriding a config the operator shaped on purpose. These pin that boundary.
/// </summary>
public class LegacyRefusalRecoveryTests
{
    private const string Marker = "global.apiexpose.media_migration.legacy_refusal_recovered";

    // A representative subset of the real DisabledSettingsAfterRefusal, incl. a NON-boolean value.
    private static readonly IReadOnlyDictionary<string, string> Disabled = new Dictionary<string, string>
    {
        ["global.apiexpose.scraping.auto_enabled"] = "0",
        ["global.apiexpose.scraping.queue.enabled"] = "0",
        ["global.apiexpose.romset.pack_installer.on_the_fly.trigger"] = "never",
    };

    private static XElement Root(params (string Name, string Value)[] settings)
    {
        var root = new XElement("config");
        foreach (var (name, value) in settings)
        {
            root.Add(new XElement("string", new XAttribute("name", name), new XAttribute("value", value)));
        }

        return root;
    }

    private static string? Val(XElement root, string name)
        => root.Elements().FirstOrDefault(e => e.Attribute("name")?.Value == name)?.Attribute("value")?.Value;

    [Fact]
    public void FullFingerprint_removesEveryKey_andStampsMarker()
    {
        var root = Root(
            ("global.apiexpose.scraping.auto_enabled", "0"),
            ("global.apiexpose.scraping.queue.enabled", "0"),
            ("global.apiexpose.romset.pack_installer.on_the_fly.trigger", "never"));

        var changed = RomsMediaCanonicalMigrationHostedService.ApplyLegacyRefusalRecovery(
            root, Disabled, Marker, out var recovered);

        Assert.True(changed);
        Assert.Equal(3, recovered);
        // removed => each falls back to its appsettings default (pre-refusal state)
        Assert.Null(Val(root, "global.apiexpose.scraping.auto_enabled"));
        Assert.Null(Val(root, "global.apiexpose.romset.pack_installer.on_the_fly.trigger"));
        Assert.Equal("1", Val(root, Marker));
    }

    [Fact]
    public void PartialFingerprint_touchesNothing_butStampsMarkerOnce()
    {
        // The operator deliberately turned ONE thing off; the rest are their own choices.
        var root = Root(
            ("global.apiexpose.scraping.auto_enabled", "0"),
            ("global.apiexpose.scraping.queue.enabled", "1"),
            ("global.apiexpose.romset.pack_installer.on_the_fly.trigger", "game-start"));

        var changed = RomsMediaCanonicalMigrationHostedService.ApplyLegacyRefusalRecovery(
            root, Disabled, Marker, out var recovered);

        Assert.Equal(0, recovered);                 // nothing recovered
        Assert.Equal("0", Val(root, "global.apiexpose.scraping.auto_enabled")); // left as the operator set it
        Assert.Equal("1", Val(root, "global.apiexpose.scraping.queue.enabled"));
        Assert.Equal("game-start", Val(root, "global.apiexpose.romset.pack_installer.on_the_fly.trigger"));
        Assert.True(changed);                        // only the marker was written
        Assert.Equal("1", Val(root, Marker));        // so it never re-checks this borne
    }

    [Fact]
    public void MarkerAlreadySet_isANoOp()
    {
        var root = Root(
            ("global.apiexpose.scraping.auto_enabled", "0"),
            ("global.apiexpose.scraping.queue.enabled", "0"),
            ("global.apiexpose.romset.pack_installer.on_the_fly.trigger", "never"),
            (Marker, "1"));

        var changed = RomsMediaCanonicalMigrationHostedService.ApplyLegacyRefusalRecovery(
            root, Disabled, Marker, out var recovered);

        Assert.False(changed);
        Assert.Equal(0, recovered);
        Assert.Equal("0", Val(root, "global.apiexpose.scraping.auto_enabled")); // never recovered twice
    }
}
