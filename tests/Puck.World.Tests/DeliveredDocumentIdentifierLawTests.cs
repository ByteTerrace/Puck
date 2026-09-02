using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using System.Text;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// A <c>state.&lt;row&gt;[.&lt;key&gt;]</c> document identifier is answered at every door that turns bytes into a
/// live document, so a definition delivered across an authority boundary reads exactly like a file-loaded one. A
/// projection carries no <c>state</c> section, so its egress is flattened to literals rather than shipping a
/// reference the receiver has nothing to answer.
/// </summary>
public sealed class DeliveredDocumentIdentifierLawTests {
    private const string GroupKey = "actionGroup";
    private const string GroupReference = "state.bindingGroups.actionGroup";
    private const string GroupRow = "bindingGroups";

    // The reference arm cannot be code-built: DocumentIdentifier's reference constructor is the JSON converter's,
    // reached by the `state.` prefix. Authoring the reference text as a literal and round-tripping the canonical
    // bytes is how a test reaches the same object a hand-authored world produces.
    private static WorldDefinition Delivered(WorldDefinition live, WorldDisclosureTier tier) {
        Assert.True(condition: WorldFederationCodec.TryDecodeDocument(
            body: WorldFederationCodec.EncodeDocument(
                authority: "127.0.0.1:5000",
                definition: live,
                revision: 3,
                tier: tier
            ),
            definition: out var delivered,
            tier: out var decodedTier,
            failure: out var failure
        ), $"{tier} delivery refused: {failure.Detail}");
        Assert.Equal(actual: decodedTier, expected: tier);
        Assert.NotNull(@object: delivered);

        return delivered!;
    }
    private static string DeliveredGroup(WorldDefinition live, WorldDisclosureTier tier) {
        // Compose is what a seat's recompose runs on arrival, and its row key reads the identifier — the exact read
        // an unresolved delivery faults on.
        var composed = WorldBindingComposer.Compose(Delivered(live: live, tier: tier).BindingOverlays[0].Document);

        return composed.Chords[0].Group.Value;
    }
    private static WorldDefinition Live(string groupName) {
        var authored = (Fixtures.BuildDocument() with {
            BindingOverlaysRaw = [
                new WorldBindingOverlay(
                    Id: "delivered-identifier-law",
                    Document: new BindingProfileDocument(
                        Version: BindingProfileDocument.CurrentVersion,
                        Modifiers: [],
                        Chords: [
                            new BindingChordDefinition(
                                Group: GroupReference,
                                Page: new BindingPageDefinition(Id: "base", Entries: [])
                            ),
                        ]
                    )
                ),
            ],
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                    Name: WorldCellName.Parse(candidate: GroupRow),
                    Kind: CellKind.Text,
                    Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: GroupKey), Text: groupName)]
                ),
            ]),
        });
        var live = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: authored));

        Assert.Equal(expected: GroupReference, actual: live.BindingOverlays[0].Document.Chords[0].Group.Reference);
        Assert.Equal(expected: groupName, actual: live.BindingOverlays[0].Document.Chords[0].Group.Value);

        return live;
    }

    /// <summary>The composing authority keeps its authored reference after an egress: flattening runs on the copy
    /// that leaves, never on the live document canonical write-back preserves.</summary>
    [Fact]
    public void ComposingAnEgressLeavesTheLiveDocumentsReferenceAttached() {
        var live = Live(groupName: "actionGroup");

        _ = Delivered(live: live, tier: WorldDisclosureTier.Presentation);

        Assert.Equal(expected: GroupReference, actual: live.BindingOverlays[0].Document.Chords[0].Group.Reference);
    }
    /// <summary>Every delivery tier hands back the cell's value, and the control's one different cell value proves the
    /// read follows the reference rather than matching a literal.</summary>
    [Theory]
    [InlineData(WorldDisclosureTier.Presentation)]
    [InlineData(WorldDisclosureTier.Replica)]
    public void DeliveredDefinitionResolvesItsIdentifier_ControlOneValueDifferent(WorldDisclosureTier tier) {
        Assert.Equal(expected: "actionGroup", actual: DeliveredGroup(live: Live(groupName: "actionGroup"), tier: tier));
        Assert.Equal(expected: "renamedActionGroup", actual: DeliveredGroup(live: Live(groupName: "renamedActionGroup"), tier: tier));
    }
    /// <summary>A projection leaves with literals: nothing on the wire names a state cell the receiver has no section
    /// for.</summary>
    [Fact]
    public void PresentationEgressCarriesNoStateReference() {
        var projection = WorldProjection.Compose(
            definition: Live(groupName: "actionGroup"),
            tier: WorldDisclosureTier.Presentation,
            authority: "127.0.0.1:5000",
            revision: 3
        );

        Assert.NotNull(@object: projection);

        var json = Encoding.UTF8.GetString(bytes: WorldProjection.Serialize(projection: projection!));

        Assert.DoesNotContain(actualString: json, comparisonType: StringComparison.Ordinal, expectedSubstring: GroupReference);
        Assert.Contains(actualString: json, comparisonType: StringComparison.Ordinal, expectedSubstring: "\"actionGroup\"");
    }
    /// <summary>A peer that still names a state cell is refused by name at the decode door, never accepted as a
    /// document that faults at the first read of the value.</summary>
    [Fact]
    public void PeerProjectionNamingAStateCell_Refuses_ControlLiteralClean() {
        var projection = WorldProjection.Compose(
            definition: Live(groupName: "actionGroup"),
            tier: WorldDisclosureTier.Presentation,
            authority: "127.0.0.1:5000",
            revision: 3
        );
        var literal = WorldProjection.Serialize(projection: projection!);

        static byte[] Leaf(byte[] payload) {
            var body = new byte[(payload.Length + 1)];

            body[0] = ((byte)WorldDisclosureTier.Presentation);
            payload.CopyTo(array: body, index: 1);

            return body;
        }

        var referencing = Encoding.UTF8.GetBytes(s: Encoding.UTF8.GetString(bytes: literal).Replace(
            comparisonType: StringComparison.Ordinal,
            newValue: $"\"{GroupReference}\"",
            oldValue: "\"actionGroup\""
        ));

        Laws.RefusalWithControl(
            lawId: "projection.state-reference-undeliverable",
            deniedOutcome: () => WorldFederationCodec.TryDecodeDocument(body: Leaf(payload: referencing), definition: out _, tier: out _, failure: out _),
            controlOutcome: () => WorldFederationCodec.TryDecodeDocument(body: Leaf(payload: literal), definition: out _, tier: out _, failure: out _)
        );
    }
}
