using System.Numerics;

using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: the editor's placement verbs, run through a real command registry over a live row, edit placements
/// through the row door. <c>world.nudge</c> moves a placement by exactly one grid pitch per step, landing the moved
/// axis on the lattice only while snapping is on, and <c>world.undo</c>'s journal takes the moves back;
/// <c>world.turn</c> turns it by exactly one angle step; <c>world.place</c> with no view to aim from is refused by name.
/// Each claim pairs its passing case with a red leg that fails it. <see cref="WorldEditorBuildModeLawTests"/> holds the
/// surface-resting place, which needs the presentation's static field.
/// </summary>
public sealed class WorldEditorPlacementLawTests {
    private sealed class FakeConsoleAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }

    private const float Pitch = 0.5f;

    private static WorldPrototype Crate { get; } = CreationFixtures.UnitSphere(id: "crate");

    // One crate, standing a tenth off the half-unit lattice on x.
    private static HostRow Build() {
        var document = Fixtures.BuildDocument();

        return HostRow.Build(
            definition: (document with {
                CreationsRaw = [Crate],
                PlacementsRaw = (document.PlacementsRaw! with {
                    Rows = [new WorldPlacement(
                        Id: "crate1",
                        Position: new Vector3(x: 1.1f, y: 3f, z: -1f),
                        PrototypeId: Crate.Id,
                        Scale: 1f,
                        YawDegrees: 0f
                    )],
                }),
            }),
            name: "boot"
        );
    }
    private static CommandRegistry BuildRegistry(HostRow row, WorldEditorSeats seats) => new(modules: [
        new WorldEditorCommandModule(
            authority: new FakeConsoleAuthority(instance: row.Instance),
            echoes: new WorldDeferredVerbEchoes(),
            link: row.Instance.Link,
            seats: seats,
            stepGuard: new WorldRowStepWindowGuard()
        ),
    ]);
    private static WorldPlacement Placement(HostRow row, string id) => WorldDefinitionRows.FindPlacement(
        id: id,
        placements: row.Server.Definition.Placements
    )!;
    private static void Step(HostRow row) => row.Server.Advance(stepTicks: Fixtures.StepTicks);
    private static void Submit(CommandRegistry registry, string line) {
        var result = registry.Submit(line: line);

        Assert.False(condition: result.IsError, userMessage: result.Output);
    }

    [Fact]
    public void ANudgeMovesExactlyOnePitchAndUndoTakesItBack() {
        using var row = Build();
        var seats = new WorldEditorSeats();
        var registry = BuildRegistry(row: row, seats: seats);
        var before = ((Vector3)Placement(id: "crate1", row: row).Position);

        Submit(line: $"world.grid pitch {Pitch}", registry: registry);
        Submit(line: "world.nudge crate1 x 1", registry: registry);
        Step(row: row);

        Assert.Equal(actual: ((Vector3)Placement(id: "crate1", row: row).Position), expected: (before + new Vector3(x: Pitch, y: 0f, z: 0f)));
        Assert.Equal(actual: seats.CurrentOf(slot: 0), expected: "crate1");

        // The seat's current placement is the default target, and a step count moves that many pitches.
        Submit(line: "world.nudge z -2", registry: registry);
        Step(row: row);
        Assert.Equal(actual: ((Vector3)Placement(id: "crate1", row: row).Position), expected: (before + new Vector3(x: Pitch, y: 0f, z: (-2f * Pitch))));

        row.Instance.Link.SubmitUndo(count: 2, principal: Principal.Console);
        Step(row: row);
        Assert.Equal(actual: ((Vector3)Placement(id: "crate1", row: row).Position), expected: before);
    }
    [Fact]
    public void ANudgeLandsOnTheLatticeOnlyWhileSnappingIsOn() {
        using var row = Build();
        var registry = BuildRegistry(row: row, seats: new WorldEditorSeats());

        // The crate stands a tenth off the lattice. Red leg: a free nudge keeps the offset.
        Submit(line: $"world.grid pitch {Pitch}", registry: registry);
        Submit(line: "world.nudge crate1 x 1", registry: registry);
        Step(row: row);
        Assert.Equal(actual: Placement(id: "crate1", row: row).Position.X, expected: (1.1f + Pitch));

        // A snapped nudge lands the moved axis on the lattice and leaves the others where they stand.
        Submit(line: "world.snap on", registry: registry);
        Submit(line: "world.nudge crate1 x 1", registry: registry);
        Step(row: row);
        Assert.Equal(actual: ((Vector3)Placement(id: "crate1", row: row).Position), expected: new Vector3(x: 2f, y: 3f, z: -1f));
    }
    [Fact]
    public void ATurnTurnsByExactlyOneAngleStep() {
        using var row = Build();
        var registry = BuildRegistry(row: row, seats: new WorldEditorSeats());

        Submit(line: "world.snap angle 45", registry: registry);
        Submit(line: "world.turn crate1 1", registry: registry);
        Step(row: row);
        Assert.Equal(actual: Placement(id: "crate1", row: row).YawDegrees, expected: 45f);

        Submit(line: "world.turn crate1 -3", registry: registry);
        Step(row: row);
        Assert.Equal(actual: Placement(id: "crate1", row: row).YawDegrees, expected: -90f);
    }
    [Fact]
    public void APlaceWithNoViewToAimFromIsRefusedByName() {
        using var row = Build();
        var registry = BuildRegistry(row: row, seats: new WorldEditorSeats());
        var result = registry.Submit(line: "world.place crate");

        Assert.True(condition: result.IsError);
        Assert.Equal(actual: result.Output, expected: "[world.place: seat 1 presents no view to aim from]");
    }
}
