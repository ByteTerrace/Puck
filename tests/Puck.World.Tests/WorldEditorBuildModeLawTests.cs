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
    private const string World = "tests/Puck.World.Tests/Fixtures/minimal-snake-host.world.json";

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
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World
        ).Build();
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
    public void APlaceThroughTheHostRestsOnTheFloorUnderTheAim() {
        using var host = WorldBootHarness.Compose(
            edit: WithFloor,
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World
        ).Build();
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
