using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the phase row down to a guarded submission stamp: a guard whose sequence matches admits the
/// mutation, a matching guard's success advances the row's own generation by one, and a stale guard is refused
/// without composing anything.</summary>
public sealed class WorldPhaseOrderLawTests {
    private static WorldStateRow Board() => new(
        CellName.Parse(candidate: "board"),
        CellKind.Int,
        Cells: [new(
                CellName.Parse(candidate: "0"),
                1
            ), new(
                CellName.Parse(candidate: "1"),
                2
            ), new(
                CellName.Parse(candidate: "2"),
                2
            ), new(
                CellName.Parse(candidate: "3"),
                1
            )],
        Domain: new StateDomain.CellsOf("map")
    );
    private static WorldDefinition Document(params WorldStateRow[] rows) => Fixtures.BuildDocument() with {
        StateRaw = new(
        World: rows,
        Lattices: (rows.Any(predicate: row => (row.EffectiveDomain is StateDomain.CellsOf))
        ? [new LatticeTopology.Grid(
                    "map",
                    new(
                        x: 0,
                        y: 0,
                        z: 0
                    ),
                    1,
                    4,
                    4
                )]
        : [])
    ),
        PatternsRaw = [new(
            CellName.Parse(candidate: "capture"),
            CellKind.Int,
            [new(
                    CellName.Parse(candidate: "through"),
                    2,
                    2
                ), new(
                    CellName.Parse(candidate: "until"),
                    1,
                    1
                )],
            new PatternNode.Sequence(Items: [new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "through")), new PatternNode.Symbol(Name: "until")])
        )],
        Rules = [],
    };
    private static WorldStateRow Phase(long sequence = 0) => new(
        CellName.Parse(candidate: "turn"),
        CellKind.Int,
        Phase: new(Sequence: sequence)
    );
    private static StateTransform.SetRay Ray() => new(
        Direction: "E",
        From: "0",
        Pattern: "capture",
        Row: "board",
        Value: 1
    );
    private static StatePhase Read(WorldDefinition definition) => WorldDefinitionRows.FindStateRow(
        definition.State,
        "turn"
    )!.Phase!;

    [Fact]
    public void AMatchingGuardAdmitsAndItsSuccessAdvancesTheGenerationByOne() {
        var definition = Document(
            Phase(),
            Board()
        );

        Assert.True(condition: WorldStateTransforms.CanAct(
            definition,
            new(
                "turn",
                0
            ),
            WorldPrincipal.Seat(slot: 0)
        ));
        Assert.False(condition: WorldStateTransforms.CanAct(
            definition,
            new(
                "turn",
                1
            ),
            WorldPrincipal.Seat(slot: 0)
        ));

        Assert.True(condition: CompiledPatterns.TryCompileAll(
            definition.Patterns,
            out var patterns,
            []
        ));
        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                Ray(),
                WorldPrincipal.Seat(slot: 0),
                1,
                "test",
                out var candidate,
                out var reason,
                patterns
            ),
            userMessage: reason
        );
        var advanced = WorldStateTransforms.Advance(
            definition: candidate,
            row: "turn"
        );

        Assert.Equal(
            1L,
            Read(definition: advanced).Sequence
        );
        Assert.False(condition: WorldStateTransforms.CanAct(
            advanced,
            new(
                "turn",
                0
            ),
            WorldPrincipal.Seat(slot: 0)
        ));
        Assert.True(condition: WorldStateTransforms.CanAct(
            advanced,
            new(
                "turn",
                1
            ),
            WorldPrincipal.Seat(slot: 0)
        ));
    }
    [Fact]
    public void ARowTaggedPhaseOfRefusesATransformWithoutItsGuardAndAMatchingGuardAdvancesTheGenerationLive() {
        var definition = Document(
            Phase(),
            Board() with { PhaseOf = "turn" }
        );
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();
        var before = WorldRuntimeStateHash.HashAuthoritative(
            server: fixture.Server,
            tick: 0
        );

        fixture.Server.Submit(
            new(
                SubmissionEnvelope.LocalConnectionId,
                0,
                1,
                1,
                WorldPrincipal.Console,
                new WorldSubmissionPayload.Mutation(Value: new WorldMutation.TransformState(
                    WorldPrincipal.Console,
                    Ray()
                ))
            ),
            _ => { }
        );
        fixture.Step();
        Assert.Equal(
            0L,
            Read(definition: fixture.Server.Definition).Sequence
        );

        fixture.Server.Submit(
            new(
                SubmissionEnvelope.LocalConnectionId,
                0,
                2,
                2,
                WorldPrincipal.Console,
                new WorldSubmissionPayload.Mutation(Value: new WorldMutation.TransformState(
                    WorldPrincipal.Console,
                    Ray(),
                    new(
                        "turn",
                        0
                    )
                ))
            ),
            _ => { }
        );
        fixture.Step();
        Assert.Equal(
            1L,
            Read(definition: fixture.Server.Definition).Sequence
        );
        Assert.NotEqual(
            before,
            WorldRuntimeStateHash.HashAuthoritative(
                server: fixture.Server,
                tick: 0
            )
        );
    }
    [Fact]
    public void AStaleSequenceRefusesAdmissionRegardlessOfActor() {
        var definition = Document(Phase(sequence: 3));

        Assert.False(condition: WorldStateTransforms.CanAct(
            definition,
            new(
                "turn",
                2
            ),
            WorldPrincipal.Seat(slot: 0)
        ));
        Assert.False(condition: WorldStateTransforms.CanAct(
            definition,
            new(
                "turn",
                2
            ),
            WorldPrincipal.World
        ));
        Assert.True(condition: WorldStateTransforms.CanAct(
            definition,
            new(
                "turn",
                3
            ),
            WorldPrincipal.Seat(slot: 0)
        ));
    }
    [Fact]
    public void EveryPhaseRowRequiresAPlainIntegerRowWithoutCellsOrCapacity() {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Document(Phase()),
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(new WorldStateRow(
                CellName.Parse(candidate: "turn"),
                CellKind.Int,
                Phase: new(),
                Capacity: 4
            )),
            reason: out var capacityReason
        ));
        Assert.Contains(
            actualString: capacityReason,
            expectedSubstring: "without cells/capacity"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(new WorldStateRow(
                CellName.Parse(candidate: "turn"),
                CellKind.Bool,
                Phase: new()
            )),
            reason: out var kindReason
        ));
        Assert.Contains(
            actualString: kindReason,
            expectedSubstring: "without cells/capacity"
        );
    }
    [Fact]
    public void NamingAParticipantIsWorldProgramOnly() {
        var definition = Document(Phase());

        Assert.False(condition: WorldStateTransforms.CanAct(
            definition,
            new(
                Participant: "seat1",
                Row: "turn",
                Sequence: 0
            ),
            WorldPrincipal.Seat(slot: 0)
        ));
        Assert.True(condition: WorldStateTransforms.CanAct(
            definition,
            new(
                Participant: "seat1",
                Row: "turn",
                Sequence: 0
            ),
            WorldPrincipal.World
        ));
    }
    [Fact]
    public void PhaseRowsRoundTripThroughTheStrictWireShape() {
        var definition = Document(Phase(sequence: 4));

        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));
        var phase = WorldDefinitionRows.FindStateRow(
            parsed.State,
            "turn"
        )!.Phase!;

        Assert.Equal(
            4L,
            phase.Sequence
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: parsed,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
}
