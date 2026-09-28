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
    private static CommandRegistry BuildRegistry(HostRow row, WorldEditorSeats seats, WorldSeatAuthorityRouter? routes = null, WorldDeferredVerbEchoes? echoes = null) => new(modules: [
        new WorldEditorCommandModule(
            authority: new FakeConsoleAuthority(instance: row.Instance),
            echoes: (echoes ?? new WorldDeferredVerbEchoes()),
            link: row.Instance.Link,
            seats: seats,
            seatRouter: routes,
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
        Assert.Equal(actual: seats.CurrentOf(slot: 0, world: "boot"), expected: "crate1");

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

    // The world a seat crosses into: its own crate1, somewhere else.
    private static readonly Vector3 AwayCrate = new(x: -3f, y: 0f, z: 2f);

    [Fact]
    public void AfterACrossingANudgeEditsTheWorldTheSeatIsInAndNeverTheWorldItLeft() {
        using var row = Build();
        var seats = new WorldEditorSeats();
        var routes = new WorldSeatAuthorityRouter();
        var registry = BuildRegistry(routes: routes, row: row, seats: seats);
        var awayDocument = (row.Server.Definition with {
            DocumentId = "away",
            PlacementRowsRaw = [(Placement(id: "crate1", row: row) with { Position = AwayCrate })],
        });
        var awayLink = new RecordingLink(definition: awayDocument);
        using var away = EditorEndpoints.Of(definition: awayDocument, identity: "away", link: awayLink, pose: AwayCrate);

        // Select in the boot world: the nudge moves the boot crate and makes it the seat's current placement there.
        Submit(line: $"world.grid pitch {Pitch}", registry: registry);
        Submit(line: "world.nudge crate1 x 1", registry: registry);
        Step(row: row);

        var boot = ((Vector3)Placement(id: "crate1", row: row).Position);

        // Cross: the seat is now presented in the away world.
        _ = routes.Publish(endpoint: away, entity: away.Mirror.Address(index: 0), slot: 0);

        // The selection stayed behind: a bare nudge names no placement in the away world and is refused by name.
        var bare = registry.Submit(line: "world.nudge x 1");

        Assert.True(condition: bare.IsError);
        Assert.Equal(actual: bare.Output, expected: "[world.nudge: seat 1 has no current placement; name one]");

        // A named nudge edits the away world's crate through the away world's link, from the away document's position.
        Submit(line: "world.nudge crate1 x 1", registry: registry);
        Step(row: row);

        var routed = Assert.IsType<WorldMutation.UpsertPlacement>(@object: Assert.IsType<WorldSubmissionPayload.Mutation>(@object: Assert.Single(collection: awayLink.Submitted)).Value);

        Assert.Equal(actual: ((Vector3)routed.Placement.Position), expected: (AwayCrate + new Vector3(x: Pitch, y: 0f, z: 0f)));
        Assert.Equal(actual: seats.CurrentOf(slot: 0, world: "away"), expected: "crate1");
        Assert.Null(@object: seats.CurrentOf(slot: 0, world: "boot"));
        Assert.Equal(actual: ((Vector3)Placement(id: "crate1", row: row).Position), expected: boot);

        // Red leg: a module that edits the console's world whatever the seat's route moves the crate the seat left.
        var unrouted = BuildRegistry(row: row, seats: new WorldEditorSeats());

        Submit(line: $"world.grid pitch {Pitch}", registry: unrouted);
        Submit(line: "world.nudge crate1 x 1", registry: unrouted);
        Step(row: row);
        Assert.NotEqual(actual: ((Vector3)Placement(id: "crate1", row: row).Position), expected: boot);
    }
    [Fact]
    public void EditsFasterThanVerdictsAllLandAndARefusedOneRollsBackByName() {
        using var row = Build();
        var echoes = new WorldDeferredVerbEchoes();
        var lines = new List<string>();
        var routes = new WorldSeatAuthorityRouter();
        var registry = BuildRegistry(echoes: echoes, routes: routes, row: row, seats: new WorldEditorSeats());
        var awayDocument = (row.Server.Definition with {
            DocumentId = "away",
            PlacementRowsRaw = [(Placement(id: "crate1", row: row) with { Position = AwayCrate })],
        });
        var awayLink = new RecordingLink(definition: awayDocument);
        using var away = EditorEndpoints.Of(definition: awayDocument, identity: "away", link: awayLink, pose: AwayCrate);
        var step = new Vector3(x: Pitch, y: 0f, z: 0f);

        echoes.Completed += result => lines.Add(item: result.Output);
        _ = routes.Publish(endpoint: away, entity: away.Mirror.Address(index: 0), slot: 0);
        Submit(line: $"world.grid pitch {Pitch}", registry: registry);

        // Three nudges before any verdict: the first is in flight, the next two queue on top of it, each on the last.
        Submit(line: "world.nudge crate1 x 1", registry: registry);
        Submit(line: "world.nudge crate1 x 1", registry: registry);
        Submit(line: "world.nudge crate1 x 1", registry: registry);
        _ = Assert.Single(collection: awayLink.Submitted);
        Assert.Equal(actual: ((Vector3)awayLink.Placement(index: 0).Position), expected: (AwayCrate + step));

        // The first applies, and the queued edit goes next: the crate lands at base + 3 steps, not base + 1.
        awayLink.Complete(index: 0, result: RecordingLink.Applied);
        Assert.Equal(actual: awayLink.Submitted.Count, expected: 2);
        Assert.Equal(actual: ((Vector3)awayLink.Placement(index: 1).Position), expected: (AwayCrate + (3f * step)));
        awayLink.Complete(index: 1, result: RecordingLink.Applied);
        Assert.Equal(actual: awayLink.Submitted.Count, expected: 2);

        // Two more; the world refuses the one in flight: the one queued on it is dropped, and the crate rolls back to
        // the value the world last confirmed, by name.
        Submit(line: "world.nudge crate1 x 1", registry: registry);
        Submit(line: "world.nudge crate1 x 1", registry: registry);
        Assert.Equal(actual: ((Vector3)awayLink.Placement(index: 2).Position), expected: (AwayCrate + (4f * step)));
        awayLink.Complete(index: 2, result: RecordingLink.Refused);
        Assert.Equal(actual: awayLink.Submitted.Count, expected: 3);
        Assert.Contains(collection: lines, expected: "[world.nudge: world.grant.denied the seat may not mutate placements here]");
        Assert.Contains(collection: lines, expected: "[world.nudge: 'crate1' in 'away' rolled back to -1.5,0,2]");

        // The next edit starts from the confirmed value: the refused step never reappears.
        Submit(line: "world.nudge crate1 x 1", registry: registry);
        Assert.Equal(actual: ((Vector3)awayLink.Placement(index: 3).Position), expected: (AwayCrate + (4f * step)));
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
