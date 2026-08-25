using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Proves <see cref="WorldMarketSection.EffectiveFormats"/> — a computed convenience re-deriving
/// <see cref="WorldMarketSection.Formats"/> — never round-trips into the document contract: the wire carries
/// <c>formats</c> alone, never a second <c>effectiveFormats</c> field the compose arms and the <c>world.market</c>
/// read-back would have to agree with by convention rather than by construction. Pairs the negative (the field is
/// absent from the serialized document) with the positive control (the computed property still answers correctly
/// at runtime, both authored and unauthored, so the fix removed a wire leak, not the behavior).</summary>
public sealed class MarketSchemaLeakLawTests {
    [Fact]
    public void SerializedDocument_NeverCarriesEffectiveFormats() {
        var bytes = WorldDefinitionSerialization.Serialize(definition: MarketFixtures.BuildDocument());
        var json = Encoding.UTF8.GetString(bytes: bytes);

        Assert.DoesNotContain(actualString: json, comparisonType: StringComparison.OrdinalIgnoreCase, expectedSubstring: "effectiveFormats");
        // The authored field itself must still be there — this is a leak fix, not a data-loss regression.
        Assert.Contains(actualString: json, comparisonType: StringComparison.Ordinal, expectedSubstring: "\"formats\"");
    }
    [Fact]
    public void RetiredAdmissionTiersMember_RefusesByName() {
        var control = WorldDefinitionSerialization.Serialize(definition: MarketFixtures.BuildDocument());
        var node = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: control))!.AsObject();

        Assert.DoesNotContain(
            expectedSubstring: "admissionTiers",
            actualString: Encoding.UTF8.GetString(bytes: control),
            comparisonType: StringComparison.Ordinal
        );
        _ = WorldDefinitionSerialization.Deserialize(utf8Json: control);

        node["market"]!["admissionTiers"] = new JsonArray(new JsonObject {
            ["name"] = "open",
            ["requireAttestation"] = false,
        });

        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(
            utf8Json: Encoding.UTF8.GetBytes(s: node.ToJsonString())
        ));

        Assert.IsType<JsonException>(@object: exception.InnerException);
        Assert.Contains(
            expectedSubstring: "admissionTiers",
            actualString: exception.InnerException!.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void EffectiveFormats_StillComputesCorrectly_AuthoredAndUnauthored() {
        var authored = MarketFixtures.BuildDocument().Market!;

        Assert.Equal(expected: [WorldMarketFormat.English, WorldMarketFormat.Buyout], actual: authored.EffectiveFormats);

        var unauthored = (authored with { Formats = null });

        Assert.Equal(expected: [WorldMarketFormat.English, WorldMarketFormat.Buyout], actual: unauthored.EffectiveFormats);

        var englishOnly = (authored with { Formats = [WorldMarketFormat.English] });

        Assert.Equal(expected: [WorldMarketFormat.English], actual: englishOnly.EffectiveFormats);

        // Round-tripping the authored document through the wire preserves Formats, and EffectiveFormats still
        // derives correctly from the deserialized copy — the computed property survives the round trip even though
        // it never rode the wire itself.
        var bytes = WorldDefinitionSerialization.Serialize(definition: MarketFixtures.BuildDocument());
        var roundTripped = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);

        Assert.Equal(expected: [WorldMarketFormat.English, WorldMarketFormat.Buyout], actual: roundTripped.Market!.EffectiveFormats);
    }
}
