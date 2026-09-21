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
                CellValue.Int(value: 1)
            ), new(
                CellName.Parse(candidate: "1"),
                CellValue.Int(value: 2)
            ), new(
                CellName.Parse(candidate: "2"),
                CellValue.Int(value: 2)
            ), new(
                CellName.Parse(candidate: "3"),
                CellValue.Int(value: 1)
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
        Direction: CellName.Parse(candidate: "E"),
        From: "0",
        Pattern: "capture",
        Row: "board",
        Value: 1
    );
    // Admission is not a door of its own: the operation itself is submitted under the guard, and a refused guard
    // composes nothing.
    private static bool Admits(WorldDefinition definition, PhaseGuard guard, WorldPrincipal actor) => WorldArenaTransforms.TryApply(
        actor: actor,
        candidate: out _,
        definition: definition,
        guard: guard,
        instance: "test",
        reason: out _,
        tick: 1,
        transform: Ray()
    );
    private static StatePhase Read(WorldDefinition definition) => WorldDefinitionRows.FindStateRow(
        definition.State,
        "turn"
    )!.Phase!;
    // A mutation submission carries an operation id: the ingress door binds one to the stamped actor and the
    // canonical payload, and refuses an envelope that leaves it empty before anything composes.
    private static WorldMutationOutcome Transform(WorldFixture fixture, long sequence, PhaseGuard? guard = null) {
        WorldSubmissionResult? result = null;

        fixture.Server.Submit(
            new(
                SubmissionEnvelope.LocalConnectionId,
                0,
                sequence,
                sequence,
                WorldPrincipal.Console,
                new WorldSubmissionPayload.Mutation(Value: new WorldMutation.TransformState(
                    WorldPrincipal.Console,
                    Ray(),
                    guard
                )),
                Guid.NewGuid()
            ),
            completed => result = completed
        );
        fixture.Step();

        return Assert.IsType<WorldSubmissionResult.Mutation>(@object: result).Outcome;
    }

    [Fact]
    public void AMatchingGuardAdmitsAndItsSuccessAdvancesTheGenerationByOne() {
        var definition = Document(
            Phase(),
            Board()
        );

        Assert.True(condition: Admits(
            actor: WorldPrincipal.Seat(slot: 0),
            definition: definition,
            guard: new(
                "turn",
                0
            )
        ));
        Assert.False(condition: Admits(
            actor: WorldPrincipal.Seat(slot: 0),
            definition: definition,
            guard: new(
                "turn",
                1
            )
        ));
        Assert.True(
            condition: WorldArenaTransforms.TryApply(
                definition,
                Ray(),
                WorldPrincipal.Seat(slot: 0),
                1,
                "test",
                out var advanced,
                out var reason,
                new PhaseGuard(
                    "turn",
                    0
                )
            ),
            userMessage: reason
        );
        Assert.Equal(
            1L,
            Read(definition: advanced).Sequence
        );
        Assert.False(condition: Admits(
            actor: WorldPrincipal.Seat(slot: 0),
            definition: advanced,
            guard: new(
                "turn",
                0
            )
        ));
        Assert.True(condition: Admits(
            actor: WorldPrincipal.Seat(slot: 0),
            definition: Document(
                Phase(sequence: 1),
                Board()
            ),
            guard: new(
                "turn",
                1
            )
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
        var before = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 0
        );

        var unguarded = Transform(
            fixture: fixture,
            sequence: 1
        );

        Assert.True(condition: unguarded.Refused);
        Assert.Equal(
            0L,
            Read(definition: fixture.Server.Definition).Sequence
        );

        var guarded = Transform(
            fixture: fixture,
            guard: new(
                "turn",
                0
            ),
            sequence: 2
        );

        Assert.True(
            condition: guarded.Applied,
            userMessage: guarded.Detail
        );
        Assert.Equal(
            1L,
            Read(definition: fixture.Server.Definition).Sequence
        );
        Assert.NotEqual(
            before,
            WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 0
            )
        );
    }
    [Fact]
    public void AStaleSequenceRefusesAdmissionRegardlessOfActor() {
        var definition = Document(
            Phase(sequence: 3),
            Board()
        );

        Assert.False(condition: Admits(
            actor: WorldPrincipal.Seat(slot: 0),
            definition: definition,
            guard: new(
                "turn",
                2
            )
        ));
        Assert.False(condition: Admits(
            actor: WorldPrincipal.World,
            definition: definition,
            guard: new(
                "turn",
                2
            )
        ));
        Assert.True(condition: Admits(
            actor: WorldPrincipal.Seat(slot: 0),
            definition: definition,
            guard: new(
                "turn",
                3
            )
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
        var definition = Document(
            Phase(),
            Board()
        );

        Assert.False(condition: Admits(
            actor: WorldPrincipal.Seat(slot: 0),
            definition: definition,
            guard: new(
                Participant: "seat1",
                Row: "turn",
                Sequence: 0
            )
        ));
        Assert.True(condition: Admits(
            actor: WorldPrincipal.World,
            definition: definition,
            guard: new(
                Participant: "seat1",
                Row: "turn",
                Sequence: 0
            )
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
