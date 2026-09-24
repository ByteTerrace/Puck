using System.Numerics;
using System.Text;
using Puck.Assets.Documents;
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

    private static WorldPlacementContribution WellFormed() => new(
        Tenure: WorldContributionTenure.Presence,
        SlotCreationId: SlotCreation,
        Link: SafeName.Parse(candidate: LinkName),
        GraceSeconds: 30f
    );
    private static WorldDefinition With(WorldPlacementContribution contribution) {
        var document = Fixtures.BuildDocument();

        return (document with {
            CreationsRaw = [CreationFixtures.UnitSphere(id: SlotCreation)],
            PlacementRowsRaw = [
                new WorldPlacement(
                Id: SlotId,
                PrototypeId: SlotCreation,
                Position: new DocumentVector3(value: Vector3.Zero),
                YawDegrees: 0f,
                Scale: 1f,
                Contribution: contribution
            ),
            ],
            References = [
                new WorldReference(
                Name: SafeName.Parse(candidate: "peer"),
                Document: "peer",
                Owner: null,
                World: null
            ),
            ],
            Destinations = [
                new WorldDestination(
                Name: SafeName.Parse(candidate: "peer"),
                Reference: "peer",
                Durability: WorldDestinationDurability.Persisted,
                Scope: WorldDestinationScope.Global
            ),
            ],
            Adjacencies = [
                new WorldAdjacency(
                Name: SafeName.Parse(candidate: LinkName),
                Destination: "peer",
                Counterpart: "south",
                Boundary: new WorldAdjacencyBoundary(
                    Center: new DocumentVector3(value: new Vector3(
                        x: 0f,
                        y: 0f,
                        z: -12f
                    )),
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

    /// <summary>DENIAL: an endowed slot carrying the presence-only fields. CONTROL: the same tenure with neither.</summary>
    [Fact]
    public void EndowedTenureRefusesTheLinkAndGraceFields() {
        Laws.Refuses(
            locally: true,
            definition: With(contribution: new WorldPlacementContribution(
                Tenure: WorldContributionTenure.Endowed,
                SlotCreationId: SlotCreation,
                Link: SafeName.Parse(candidate: LinkName)
            )),
            needle: "is refused for tenure 'Endowed' — an endowed piece watches no link"
        );
        Laws.Refuses(
            locally: true,
            definition: With(contribution: new WorldPlacementContribution(
                Tenure: WorldContributionTenure.Endowed,
                SlotCreationId: SlotCreation,
                GraceSeconds: 5f
            )),
            needle: "is refused for tenure 'Endowed' — an endowed piece runs no grace"
        );
        Laws.Validates(locally: true, definition: With(contribution: new WorldPlacementContribution(
            Tenure: WorldContributionTenure.Endowed,
            SlotCreationId: SlotCreation
        )));
    }
    /// <summary>DENIAL: a grace outside its declared band, on both ends. CONTROL: the boundary values themselves
    /// validate.</summary>
    [Fact]
    public void GraceSecondsMustSitInsideItsBand() {
        Laws.Refuses(
            definition: With(contribution: (WellFormed() with { GraceSeconds = -1f })),
            locally: true,
            needle: "contribution.graceSeconds -1 must be finite and within"
        );
        Laws.Refuses(
            definition: With(contribution: (WellFormed() with { GraceSeconds = (WorldContributionCapacity.MaxGraceSeconds + 1f) })),
            locally: true,
            needle: "contribution.graceSeconds"
        );
        Laws.Validates(locally: true, definition: With(contribution: (WellFormed() with { GraceSeconds = 0f })));
        Laws.Validates(locally: true, definition: With(contribution: (WellFormed() with { GraceSeconds = WorldContributionCapacity.MaxGraceSeconds })));
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
            comparisonType: StringComparison.Ordinal,
            newValue: "\"tenure\": \"Presencee\"",
            oldValue: "\"tenure\": \"Presence\""
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
    /// <summary>DENIAL: a presence slot naming an adjacency row the world does not declare. CONTROL: the same slot
    /// naming the row it does.</summary>
    [Fact]
    public void PresenceLinkMustNameADeclaredAdjacency() {
        Laws.Refuses(
            definition: With(contribution: (WellFormed() with { Link = SafeName.Parse(candidate: "elsewhere") })),
            locally: true,
            needle: "contribution.link 'elsewhere' names no adjacencies row"
        );
        Laws.Refuses(
            definition: With(contribution: (WellFormed() with { Link = null })),
            locally: true,
            needle: "contribution.link is required for tenure 'Presence'"
        );
        Laws.Validates(locally: true, definition: With(contribution: WellFormed()));
    }
    /// <summary>DENIAL: a slotCreationId naming no creation row. CONTROL: the declared one.</summary>
    [Fact]
    public void SlotCreationIdMustResolve() {
        Laws.Refuses(
            definition: With(contribution: (WellFormed() with { SlotCreationId = "no-such-creation" })),
            locally: true,
            needle: "contribution.slotCreationId 'no-such-creation' names no creation row"
        );
        Laws.Validates(locally: true, definition: With(contribution: WellFormed()));
    }
    /// <summary>DENIAL: an unfilled slot carrying a deadline, and a filled slot still showing its slotCreationId.
    /// CONTROL: the coherent unfilled spelling.</summary>
    [Fact]
    public void StampedHalfMustCohereWithTheFillState() {
        Laws.Refuses(
            definition: With(contribution: (WellFormed() with { RetractDeadlineTick = 99L })),
            locally: true,
            needle: "stands on an unfilled slot"
        );
        Laws.Refuses(
            definition: With(contribution: (WellFormed() with { Contributor = Puck.Commands.Principal.Seat(slot: 1) })),
            locally: true,
            needle: "its prototypeId still reads slotCreationId"
        );
        Laws.Validates(locally: true, definition: With(contribution: WellFormed()));
    }
}
