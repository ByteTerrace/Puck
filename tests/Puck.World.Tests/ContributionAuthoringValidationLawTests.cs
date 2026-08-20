using System.Numerics;
using System.Text;
using Puck.Assets.Documents;
using Puck.Forge.Authoring;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: every way of authoring a contribution facet wrong is refused BY NAME, and the one well-formed spelling of
/// the same slot validates. A misspelled tenure never silently defaults, a negative or oversized grace never
/// silently clamps, and a presence slot naming an adjacency row the world does not declare never silently watches
/// nothing.
/// <para>Each arm is a denial paired with a control differing in exactly one authored field.</para>
/// </summary>
public sealed class ContributionAuthoringValidationLawTests {
    private const string LinkName = "north";
    private const string SlotCreation = "plinth";
    private const string SlotId = "plaza-slot";

    private static void AssertValidates(WorldDefinition definition) {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    private static void AssertRefusedNaming(WorldDefinition definition, string needle) {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: needle
        );
    }
    private static WorldCreation Creation(string id) {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: id,
            Palette: null,
            Shapes: [
                new ShapeDocument(
                    Id: 0,
                    Name: null,
                    Type: SdfSolidPrimitive.Sphere,
                    Position: Vector3.Zero,
                    Rotation: Quaternion.Identity,
                    Scale: new Vector3(value: 1f),
                    Material: 0,
                    Blend: SdfBlendOp.Union,
                    Smooth: 0f,
                    Group: 0
                ),
            ],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: id
        );

        return new WorldCreation(
            Id: id,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    private static WorldDefinition With(WorldPlacementContribution contribution) {
        var document = Fixtures.BuildDocument();

        return (document with {
            CreationsRaw = [Creation(id: SlotCreation)],
            PlacementsRaw = [
                new WorldPlacement(
                    Id: SlotId,
                    CreationId: SlotCreation,
                    Position: new DocumentVector3(value: Vector3.Zero),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Contribution: contribution
                ),
            ],
            References = [
                new WorldReference(
                    Name: WorldSafeName.Parse(candidate: "peer"),
                    Document: "peer.world.json",
                    Owner: null,
                    World: null
                ),
            ],
            Destinations = [
                new WorldDestination(
                    Name: WorldSafeName.Parse(candidate: "peer"),
                    Reference: "peer",
                    Durability: WorldDestinationDurability.Persisted,
                    Scope: WorldDestinationScope.Global
                ),
            ],
            Adjacencies = [
                new WorldAdjacency(
                    Name: WorldSafeName.Parse(candidate: LinkName),
                    Destination: "peer",
                    Counterpart: "south",
                    Boundary: new WorldAdjacencyBoundary(
                        Center: new DocumentVector3(value: new Vector3(x: 0f, y: 0f, z: -12f)),
                        OutwardYawDegrees: 0f,
                        OutwardPitchDegrees: 0f,
                        Width: 24f,
                        Height: 16f
                    ),
                    LivenessGraceSeconds: 1f
                ),
            ],
        });
    }
    private static WorldPlacementContribution WellFormed() => new(
        Tenure: WorldContributionTenure.Presence,
        SlotCreationId: SlotCreation,
        Link: WorldSafeName.Parse(candidate: LinkName),
        GraceSeconds: 30f
    );

    /// <summary>DENIAL: a presence slot naming an adjacency row the world does not declare. CONTROL: the same slot
    /// naming the row it does.</summary>
    [Fact]
    public void PresenceLinkMustNameADeclaredAdjacency() {
        AssertRefusedNaming(
            definition: With(contribution: (WellFormed() with { Link = WorldSafeName.Parse(candidate: "elsewhere") })),
            needle: "contribution.link 'elsewhere' names no adjacencies row"
        );
        AssertRefusedNaming(
            definition: With(contribution: (WellFormed() with { Link = null })),
            needle: "contribution.link is required for tenure 'Presence'"
        );
        AssertValidates(definition: With(contribution: WellFormed()));
    }
    /// <summary>DENIAL: a grace outside its declared band, on both ends. CONTROL: the boundary values themselves
    /// validate.</summary>
    [Fact]
    public void GraceSecondsMustSitInsideItsBand() {
        AssertRefusedNaming(
            definition: With(contribution: (WellFormed() with { GraceSeconds = -1f })),
            needle: "contribution.graceSeconds -1 is outside"
        );
        AssertRefusedNaming(
            definition: With(contribution: (WellFormed() with { GraceSeconds = (WorldContributionCapacity.MaxGraceSeconds + 1f) })),
            needle: "contribution.graceSeconds"
        );
        AssertValidates(definition: With(contribution: (WellFormed() with { GraceSeconds = 0f })));
        AssertValidates(definition: With(contribution: (WellFormed() with { GraceSeconds = WorldContributionCapacity.MaxGraceSeconds })));
    }
    /// <summary>DENIAL: an endowed slot carrying the presence-only fields. CONTROL: the same tenure with neither.</summary>
    [Fact]
    public void EndowedTenureRefusesTheLinkAndGraceFields() {
        AssertRefusedNaming(
            definition: With(contribution: new WorldPlacementContribution(
                Tenure: WorldContributionTenure.Endowed,
                SlotCreationId: SlotCreation,
                Link: WorldSafeName.Parse(candidate: LinkName)
            )),
            needle: "is refused for tenure 'Endowed' — an endowed piece watches no link"
        );
        AssertRefusedNaming(
            definition: With(contribution: new WorldPlacementContribution(
                Tenure: WorldContributionTenure.Endowed,
                SlotCreationId: SlotCreation,
                GraceSeconds: 5f
            )),
            needle: "is refused for tenure 'Endowed' — an endowed piece runs no grace"
        );
        AssertValidates(definition: With(contribution: new WorldPlacementContribution(
                Tenure: WorldContributionTenure.Endowed,
                SlotCreationId: SlotCreation
            )));
    }
    /// <summary>DENIAL: a slotCreationId naming no creation row. CONTROL: the declared one.</summary>
    [Fact]
    public void SlotCreationIdMustResolve() {
        AssertRefusedNaming(
            definition: With(contribution: (WellFormed() with { SlotCreationId = "no-such-creation" })),
            needle: "contribution.slotCreationId 'no-such-creation' names no creation row"
        );
        AssertValidates(definition: With(contribution: WellFormed()));
    }
    /// <summary>DENIAL: an unfilled slot carrying a deadline, and a filled slot still showing its slotCreationId.
    /// CONTROL: the coherent unfilled spelling.</summary>
    [Fact]
    public void StampedHalfMustCohereWithTheFillState() {
        AssertRefusedNaming(
            definition: With(contribution: (WellFormed() with { RetractDeadlineTick = 99L })),
            needle: "stands on an unfilled slot"
        );
        AssertRefusedNaming(
            definition: With(contribution: (WellFormed() with { Contributor = Puck.World.Protocol.WorldPrincipal.Seat(slot: 1) })),
            needle: "its creationId still reads slotCreationId"
        );
        AssertValidates(definition: With(contribution: WellFormed()));
    }
    /// <summary>DENIAL: a misspelled tenure token is a hard PARSE failure, never a silent default to the first enum
    /// member. CONTROL: the correctly spelled token round-trips.</summary>
    [Fact]
    public void MisspelledTenureRefusesAtParse() {
        var bytes = WorldDefinitionSerialization.Serialize(definition: With(contribution: WellFormed()));
        var text = Encoding.UTF8.GetString(bytes: bytes);

        Assert.Contains(
            actualString: text,
            expectedSubstring: "\"tenure\": \"Presence\""
        );

        var sabotaged = Encoding.UTF8.GetBytes(s: text.Replace(
            newValue: "\"tenure\": \"Presencee\"",
            oldValue: "\"tenure\": \"Presence\"",
            comparisonType: StringComparison.Ordinal
        ));

        _ = Assert.ThrowsAny<Exception>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: sabotaged));

        // CONTROL: the untouched bytes parse and validate.
        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);

        Assert.Equal(
            actual: WorldDefinitionRows.FindPlacement(
            id: SlotId,
            placements: parsed.Placements
        )!.Contribution!.Tenure,
            expected: WorldContributionTenure.Presence
        );
    }
}
