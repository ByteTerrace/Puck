using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: build mode is per seat and reaches the frame. In a boot composed as a real one is, a seat that builds with
/// its grid on renders a view carrying exactly the grid its editor state names, while a seat that plays carries none;
/// and <c>world.place</c> through the host's own registry rests a placement on the floor the presentation's static
/// field holds, under the seat's aim, its column snapped to the grid. Every claim has a red leg.
/// </summary>
public sealed class WorldEditorBuildModeLawTests : IDisposable {
    private const float FloorTop = 1.25f;
    private const string World = "tests/Puck.World.Tests/Fixtures/minimal-snake-host.puck";

    private static readonly GridOverlayState SurfaceGrid = (GridOverlayState.Hidden with {
        Flags = GridOverlayFlags.World | GridOverlayFlags.Surface,
        LineWidth = GridOverlayState.DefaultLineWidth,
        PlaneY = 0f,
        WorldPitch = new Vector3(value: WorldEditorGrid.DefaultPitch),
    });

    private readonly TemporaryDirectory m_stateDirectory = new(prefix: "puck-editor-build-");

    private static SdfFrame Capture(WorldFramePresenter presenter) => presenter.CaptureFrame(
        deltaSeconds: 0f,
        height: 36U,
        interpolationAlpha: 0f,
        width: 64U
    );
    // The world with a solid floor slab whose top face stands at FloorTop, and a creation to place.
    private static WorldDefinition WithFloor(WorldDefinition definition) => (definition with {
        CreationsRaw = [
            .. definition.Creations,
            CreationFixtures.UnitSphere(id: "crate"),
            CreationFixtures.Prototype(
                document: CreationFixtures.Document(
                    name: "slab",
                    shapes: [CreationFixtures.Shape(scale: new Vector3(x: 8f, y: 0.25f, z: 8f), type: SdfSolidPrimitive.Box)]
                ),
                id: "slab"
            ),
        ],
        PlacementRowsRaw = [
            .. (definition.PlacementRowsRaw ?? []),
            new WorldPlacement(
                Id: "floor",
                Position: new Vector3(x: 0f, y: (FloorTop - 0.25f), z: 0f),
                PrototypeId: "slab",
                Scale: 1f,
                Solid: new WorldSolid(Margin: 0f),
                YawDegrees: 0f
            ),
        ],
    });
    private static void Submit(CommandRegistry registry, string line) {
        var result = registry.Submit(line: line);

        Assert.False(condition: result.IsError, userMessage: result.Output);
    }

    public void Dispose() => m_stateDirectory.Dispose();
    [Fact]
    public void ABuildingSeatsViewCarriesExactlyItsGridAndAPlayingSeatsNone() {
        var host = m_stateDirectory.Own(owner: WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World
        ).Build());
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var client = host.Services.GetRequiredService<WorldClient>();

        // Resolving the instance host admits the boot row, which routes the world's one seat to it.
        _ = host.Services.GetRequiredService<WorldInstanceHost>();
        Assert.True(condition: client.Roster.IsJoined(slot: 0));

        // Red leg: the grid switched on while the seat plays draws nothing.
        Submit(line: "world.grid on", registry: registry);
        Assert.Equal(actual: Capture(presenter: presenter).Views[0].Grid, expected: GridOverlayState.Hidden);

        Submit(line: "player.build", registry: registry);
        Assert.True(condition: host.Services.GetRequiredService<WorldSeatBindings>().IsBuilding(slot: 0));
        Assert.Equal(actual: Capture(presenter: presenter).Views[0].Grid, expected: SurfaceGrid);

        // A fixed working plane draws the world lattice alone, at its height.
        Submit(line: "world.grid plane 2", registry: registry);
        Assert.Equal(
            actual: Capture(presenter: presenter).Views[0].Grid,
            expected: (SurfaceGrid with { Flags = GridOverlayFlags.World, PlaneY = 2f })
        );

        Submit(line: "world.grid off", registry: registry);
        Assert.Equal(actual: Capture(presenter: presenter).Views[0].Grid, expected: GridOverlayState.Hidden);

        Submit(line: "world.grid on", registry: registry);
        Submit(line: "player.build", registry: registry);
        Assert.Equal(actual: Capture(presenter: presenter).Views[0].Grid, expected: GridOverlayState.Hidden);
    }
    [Fact]
    public void AFollowingGridTakesTheHeightOfThePlatformUnderTheAimWithinAFrame() {
        // The editor-grid fixture with a wide stage whose top, at height 1.5, lies under the middle of the seat's view.
        var host = m_stateDirectory.Own(owner: WorldBootHarness.Compose(
            edit: static definition => (definition with {
                CreationsRaw = [
                    .. definition.Creations,
                    CreationFixtures.Prototype(
                        document: CreationFixtures.Document(
                            name: "stage",
                            shapes: [CreationFixtures.Shape(scale: new Vector3(x: 3f, y: 0.75f, z: 3f), type: SdfSolidPrimitive.Box)]
                        ),
                        id: "stage"
                    ),
                ],
                PlacementRowsRaw = [
                    .. (definition.PlacementRowsRaw ?? []),
                    new WorldPlacement(
                        Id: "stage",
                        Position: new Vector3(x: 0f, y: 0.75f, z: 0f),
                        PrototypeId: "stage",
                        Scale: 1f,
                        Solid: new WorldSolid(Margin: 0f),
                        YawDegrees: 0f
                    ),
                ],
            }),
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: "tests/Puck.World.Canaries/editor-grid/fixture.puck"
        ).Build());
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var pointer = host.Services.GetRequiredService<WorldEditorPointer>();
        var seats = host.Services.GetRequiredService<WorldEditorSeats>();

        _ = host.Services.GetRequiredService<WorldInstanceHost>();

        // Offscreen there is no pointer, so the seat's pointer stands in the middle of its view: the probe casts the
        // aim through the camera the seat's view published this frame.
        seats.PointerProbe = slot => ((pointer.Aim(slot: slot) is { } aim)
            ? pointer.Surface(maxDistance: 100f, ray: aim, slot: slot)
            : null);

        // The first frame builds the static field and publishes the seat's view.
        _ = Capture(presenter: presenter);
        Submit(line: "player.build", registry: registry);
        Submit(line: "world.grid on", registry: registry);
        Submit(line: "world.grid follow", registry: registry);

        // Red leg: with the seat's view published after its grid resolves (the viewports cleared before the grid), the aim
        // of this frame finds no camera, nothing is followed, and the plane keeps its fallback height, 0.
        var grid = Capture(presenter: presenter).Views[0].Grid;

        Assert.Equal(actual: (seats.FollowedHeightOf(slot: 0, world: "boot") ?? float.NaN), expected: 1.5f, tolerance: 0.02);
        Assert.Equal(actual: grid.PlaneY, expected: 1.5f, tolerance: 0.02);
        Assert.Equal(actual: grid.Flags, expected: GridOverlayFlags.World);
    }

    // A world of another document a seat crosses into, with one slab whose top stands at 2 far from where the seat
    // arrives, and nothing where it arrives.
    private static WorldDefinition Elsewhere(WorldDefinition basis) => (basis with {
        CreationsRaw = [
            .. basis.Creations,
            CreationFixtures.Prototype(
                document: CreationFixtures.Document(
                    name: "slab",
                    shapes: [CreationFixtures.Shape(scale: new Vector3(x: 3f, y: 0.25f, z: 3f), type: SdfSolidPrimitive.Box)]
                ),
                id: "slab"
            ),
        ],
        DocumentId = "elsewhere",
        PlacementRowsRaw = [
            new WorldPlacement(
                Id: "slab",
                Position: new Vector3(x: 12f, y: 1.75f, z: 0f),
                PrototypeId: "slab",
                Scale: 1f,
                Solid: new WorldSolid(Margin: 0f),
                YawDegrees: 0f
            ),
        ],
    });

    [Fact]
    public void ACrossedSeatsSurfacesAreTheWorldItCrossedIntoAndItsFirstFrameThereFollowsNothingOfTheWorldItLeft() {
        var host = m_stateDirectory.Own(owner: WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: "tests/Puck.World.Canaries/editor-grid/fixture.puck"
        ).Build());
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var client = host.Services.GetRequiredService<WorldClient>();
        var pointer = host.Services.GetRequiredService<WorldEditorPointer>();
        var routes = host.Services.GetRequiredService<WorldSeatAuthorityRouter>();
        var seats = host.Services.GetRequiredService<WorldEditorSeats>();

        _ = host.Services.GetRequiredService<WorldInstanceHost>();
        Assert.True(condition: client.Roster.IsJoined(slot: 0));

        // Offscreen there is no pointer, so the seat's pointer stands in the middle of its view.
        seats.PointerProbe = slot => ((pointer.Aim(slot: slot) is { } aim)
            ? pointer.Surface(maxDistance: 100f, ray: aim, slot: slot)
            : null);
        _ = Capture(presenter: presenter);
        Submit(line: "player.build", registry: registry);
        Submit(line: "world.grid on", registry: registry);
        Submit(line: "world.grid follow", registry: registry);
        _ = Capture(presenter: presenter);

        // At home the aim meets this world, so a following plane has a height to take; not vacuous below.
        Assert.NotNull(@object: seats.FollowedHeightOf(slot: 0, world: "boot"));

        // The seat crosses, arriving where this world has the surface it just followed and the other world has nothing.
        var elsewhere = Elsewhere(basis: client.Definition);
        using var north = EditorEndpoints.Of(definition: elsewhere, identity: "north", link: new RecordingLink(definition: elsewhere), pose: client.Position(index: 0));

        _ = routes.Publish(endpoint: north, entity: north.Mirror.Address(index: 0), slot: 0);
        _ = Capture(presenter: presenter);

        // The first frame there queries the world it crossed into, whose scene has drawn no view of it yet: nothing is
        // followed, and above all not the height of the world it left. Red leg (planted): with the seat's route resolved
        // after its grid, the frame marches this world's field and records its height for the other world.
        Assert.Null(@object: seats.FollowedHeightOf(slot: 0, world: "north"));

        // Once the crossed world's scene has drawn the seat's view, a surface query resolves against that scene: the
        // slab the other world has at 12,0, beyond this world's floor, answers at its own height, 2.
        Assert.True(condition: presenter.TrySeatScene(index: out _, scene: out var scene, slot: 0));
        _ = scene.FrameSource.CaptureFrame(deltaSeconds: 0f, height: 36U, interpolationAlpha: 0f, width: 64U);

        var down = new WorldEditorRay(Direction: -Vector3.UnitY, Origin: new Vector3(x: 12.3f, y: 10f, z: -0.2f));
        var hit = pointer.Surface(maxDistance: 100f, ray: down, slot: 0);

        Assert.NotNull(@object: hit);
        Assert.Equal(actual: hit.Value.Point.Y, expected: 2f, tolerance: 0.02);
        Assert.True(condition: (hit.Value.Normal.Y > 0.9f));

        // Red leg: a pointer that cannot see the seat's routed scene marches this world's field, which has no slab there.
        var boot = new WorldEditorPointer(client: client, viewports: host.Services.GetRequiredService<WorldSeatViewports>()).Surface(maxDistance: 100f, ray: down, slot: 0);

        Assert.False(condition: ((boot is { } other) && (MathF.Abs(x: (other.Point.Y - 2f)) < 0.02f)));
    }
    [Fact]
    public void APlaceThroughTheHostRestsOnTheFloorUnderTheAim() {
        var host = m_stateDirectory.Own(owner: WorldBootHarness.Compose(
            edit: WithFloor,
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World
        ).Build());
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var server = host.Services.GetRequiredService<WorldServer>();
        var seats = host.Services.GetRequiredService<WorldEditorSeats>();

        // The first frame builds the static field the surface probe marches.
        _ = Capture(presenter: presenter);
        Assert.NotNull(@object: host.Services.GetRequiredService<WorldClient>().StaticField);
        seats.AimProbe = static _ => new WorldEditorRay(Direction: -Vector3.UnitY, Origin: new Vector3(x: 0.3f, y: 6f, z: 0.2f));

        Submit(line: "world.snap on", registry: registry);
        Submit(line: "world.place crate", registry: registry);
        server.Advance(stepTicks: Fixtures.StepTicksAt(rateHz: server.Definition.SimulationRateHz));

        var placed = ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: server.Definition.Placements)!.Position);

        // The column snaps to the grid; the height is the floor's, which lies between two lattice heights.
        Assert.Equal(actual: placed.X, expected: WorldEditorGrid.DefaultPitch);
        Assert.Equal(actual: placed.Z, expected: 0f);
        Assert.Equal(actual: placed.Y, expected: FloorTop, tolerance: 0.02);

        // Red leg: with surface snapping off the height snaps to the lattice too, and leaves the floor.
        Submit(line: "world.snap surface off", registry: registry);
        Submit(line: "world.place crate", registry: registry);
        server.Advance(stepTicks: Fixtures.StepTicksAt(rateHz: server.Definition.SimulationRateHz));

        var unrested = ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate2", placements: server.Definition.Placements)!.Position);

        Assert.Equal(actual: (unrested.Y % WorldEditorGrid.DefaultPitch), expected: 0f);
        Assert.NotEqual(actual: unrested.Y, expected: FloorTop, tolerance: 0.1);
    }
}
