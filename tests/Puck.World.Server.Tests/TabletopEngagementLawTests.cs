using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// THE LAW: a tabletop engages like a device. The context button within a board's engage radius sits the seat down on
/// a <c>placement:&lt;id&gt;</c> application that captures it, and the same button stands it up; a seat beyond the
/// radius engages nothing. While seated, a world rule reads the seat at the board in fixed point — whether it sits
/// there, the cell its pointer ray meets on the playing surface, and the side of the board it stands on — and the
/// validator refuses an engage block no probe or camera could use.
/// </summary>
public sealed class TabletopEngagementLawTests {
    private const float CellSize = 2f;
    private const string Interact = "interact";
    private const string OccupancyRowName = "cells";
    private const string PointedRowName = "pointed";
    private const string SeatedRowName = "seated";
    private const string SideRowName = "side";
    private const string TableId = "table";
    private const string TopologyName = "strip";
    private const int Width = 4;

    private static readonly FixedQ4816 Full = FixedQ4816.FromInteger(value: 1);

    // A 4 by 1 strip of 2 m cells from the origin (its footprint x 0..8, z 0..2, centred on (4, 0, 1)), anchored by a
    // table engaged on the context channel within 3 m, and three rules copying the seat's reads into slots.
    private static WorldDefinition Document(WorldTabletopEngage? engage = null, bool engageable = true, int seat = 1) {
        var creation = CreationFixtures.Sphere(id: "table", scale: 0.2f);
        var definition = Fixtures.BuildDocument();

        return definition with {
            ChannelsRaw = [.. definition.Channels, new WorldChannel(
                Composition: true,
                Name: Interact,
                Shape: ChannelShape.Binary
            )],
            CreationsRaw = [creation],
            PlacementsRaw = new WorldPlacementsSection(
                Policy: Fixtures.StandardAuthoring,
                Rows: [new WorldPlacement(
                    Id: TableId,
                    PrototypeId: creation.Id,
                    Position: Vector3.Zero,
                    YawDegrees: 0f,
                    Scale: 1f,
                    Board: new WorldPlacementBoard(
                        Topology: TopologyName,
                        Occupancy: OccupancyRowName,
                        Engage: (!engageable ? null : engage ?? new WorldTabletopEngage(
                            Channel: Interact,
                            Radius: 3d
                        ))
                    )
                )]
            ),
            StateRaw = new WorldStateSection(
                World: [
                    new WorldStateRow(
                        Name: CellName.Parse(candidate: OccupancyRowName),
                        Kind: CellKind.Int,
                        Domain: new StateDomain.CellsOf(Topology: TopologyName)
                    ),
                    Slot(name: SeatedRowName),
                    Slot(name: PointedRowName),
                    Slot(name: SideRowName),
                ],
                Lattices: [new LatticeTopology.Grid(
                    Name: TopologyName,
                    Origin: Vector3.Zero,
                    CellSize: CellSize,
                    Width: Width,
                    Depth: 1
                )]
            ),
            Rules = [
                Copy(facet: "engaged", row: SeatedRowName, seat: seat),
                Copy(facet: "pointer", row: PointedRowName, seat: seat),
                Copy(facet: "side", row: SideRowName, seat: seat),
            ],
        };
    }
    private static WorldRule Copy(string facet, string row, int seat) => new(
        Name: CellName.Parse(candidate: $"copy-{facet}"),
        Effects: [new ActionEffect.SetState(
            State: row,
            Expression: ExpressionProgram.Parse(text: $"$board:{facet}:{OccupancyRowName}:{seat}")
        )]
    );
    private static WorldStateRow Slot(string name) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Cells: [new StateCell(
            Key: StateRow.SlotKey,
            Value: CellValue.Int(value: -9L)
        )]
    );
    private static long Read(WorldFixture fixture, string row) {
        Assert.True(condition: WorldStateReader.TryRead(
            definition: fixture.Server.Definition,
            engineTick: fixture.Server.CompletedEngineTicks,
            key: WorldStateRow.SlotKey.Value,
            rawValue: out var value,
            row: out _,
            rowName: row,
            text: out _,
            tick: (fixture.Server.NextInputTick - 1UL)
        ));

        return value!.Value;
    }
    private static WorldFixture Seated(Vector3 position, out int interactOrdinal) {
        var fixture = Fixtures.FreshServer(definition: Document());

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: Principal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));
        fixture.Server.Body(index: 0)!.Pose(
            x: position.X,
            y: position.Y,
            z: position.Z,
            yawRadians: 0f,
            pitchRadians: 0f,
            rollRadians: 0f
        );
        interactOrdinal = fixture.Server.Definition.Channels
            .Select(selector: static (channel, index) => (channel.Name, index))
            .Single(predicate: static entry => (entry.Name == Interact)).index;

        return fixture;
    }
    private static void Step(WorldFixture fixture, int interactOrdinal, FixedQ4816 value, SourceRay? ray = null) {
        fixture.Server.Body(index: 0)!.SubmitIntent(intent: default(PlayerIntent).WithChannel(
            ordinal: interactOrdinal,
            value: value
        ) with {
            SourceRay = ray,
        });
        fixture.Step();
    }

    [Fact]
    public void TheContextButtonSitsTheSeatAtTheTableAndTheRulesReadItThereUntilTheButtonStandsItUp() {
        using var fixture = Seated(interactOrdinal: out var interact, position: new Vector3(x: 4f, y: 0f, z: -1f));
        var seat = Principal.Seat(slot: 0);
        var table = GrantSubject.Placement(id: TableId);

        Step(fixture: fixture, interactOrdinal: interact, value: FixedQ4816.Zero);
        Assert.Equal(expected: 0L, actual: Read(fixture: fixture, row: SeatedRowName));

        // The press sits the seat down: captured on the table, its own body no longer applied.
        Step(fixture: fixture, interactOrdinal: interact, value: Full);
        Assert.Equal(
            expected: table,
            actual: Assert.Single(collection: fixture.Server.Grants.Applications(principal: seat)).Target
        );
        Assert.True(condition: fixture.Server.Body(index: 0)!.Engaged);

        // Seated on the -Z side, pointing straight down at x = 5: cell 2 of the strip.
        Step(
            fixture: fixture,
            interactOrdinal: interact,
            ray: new SourceRay(
                Direction: new FixedVector3(X: FixedQ4816.Zero, Y: -Full, Z: FixedQ4816.Zero),
                Origin: FixedVector3.FromVector3(value: new Vector3(x: 5f, y: 4f, z: 1f))
            ),
            value: FixedQ4816.Zero
        );
        Assert.Equal(expected: 1L, actual: Read(fixture: fixture, row: SeatedRowName));
        Assert.Equal(expected: 2L, actual: Read(fixture: fixture, row: PointedRowName));
        Assert.Equal(expected: WorldTabletopFrame.SideNegativeZ, actual: Read(fixture: fixture, row: SideRowName));

        // The next press stands it up, back to the own-body default.
        Step(fixture: fixture, interactOrdinal: interact, value: Full);
        Assert.Equal(
            expected: ControlApplication.OwnBody(bodyIndex: 0),
            actual: Assert.Single(collection: fixture.Server.Grants.Applications(principal: seat))
        );
        Step(fixture: fixture, interactOrdinal: interact, value: FixedQ4816.Zero);
        Assert.Equal(expected: 0L, actual: Read(fixture: fixture, row: SeatedRowName));
        Assert.Equal(expected: -1L, actual: Read(fixture: fixture, row: PointedRowName));
    }

    [Fact]
    public void ASeatBeyondTheEngageRadiusSitsAtNothing() {
        using var fixture = Seated(interactOrdinal: out var interact, position: new Vector3(x: 4f, y: 0f, z: -3f));

        Step(fixture: fixture, interactOrdinal: interact, value: FixedQ4816.Zero);
        Step(fixture: fixture, interactOrdinal: interact, value: Full);

        Assert.Equal(
            expected: ControlApplication.OwnBody(bodyIndex: 0),
            actual: Assert.Single(collection: fixture.Server.Grants.Applications(principal: Principal.Seat(slot: 0)))
        );
        Assert.False(condition: fixture.Server.Body(index: 0)!.Engaged);

        // The policy body.engage applies names each refusal: the radius, a body that is not live, and a placement
        // carrying no engageable tabletop.
        Assert.False(condition: fixture.Server.CheckEngagePolicy(entityIndex: 0, reason: out var far, target: GrantSubject.Placement(id: TableId)));
        Assert.Contains(expectedSubstring: "outside tabletop 'table''s engage radius (3)", actualString: far);
        Assert.False(condition: fixture.Server.CheckEngagePolicy(entityIndex: 99, reason: out var gone, target: GrantSubject.Placement(id: TableId)));
        Assert.Equal(expected: "body 99 is not live", actual: gone);
        Assert.False(condition: fixture.Server.CheckEngagePolicy(entityIndex: 0, reason: out var none, target: GrantSubject.Placement(id: "chair")));
        Assert.Equal(expected: "placement 'chair' carries no engageable tabletop", actual: none);
    }

    [Fact]
    public void ASeatWithoutControlOverTheTableSitsAtNothingUntilItIsGrantedTheTable() {
        using var fixture = Seated(interactOrdinal: out var interact, position: new Vector3(x: 4f, y: 0f, z: -1f));
        var seat = Principal.Seat(slot: 0);

        fixture.Server.Revoke(
            actor: Principal.Console,
            grant: new WorldGrant(
                Capability: WorldCapability.Control,
                Exclusive: false,
                Grantee: seat,
                Subject: GrantSubject.All
            )
        );
        Step(fixture: fixture, interactOrdinal: interact, value: FixedQ4816.Zero);
        Step(fixture: fixture, interactOrdinal: interact, value: Full);
        Assert.False(condition: fixture.Server.Body(index: 0)!.Engaged);

        // Control over the one placement is a legitimate grant, and it is all the table asks for.
        fixture.Server.Grant(
            actor: Principal.Console,
            grant: new WorldGrant(
                Capability: WorldCapability.Control,
                Exclusive: false,
                Grantee: seat,
                Subject: GrantSubject.Placement(id: TableId)
            )
        );
        Step(fixture: fixture, interactOrdinal: interact, value: FixedQ4816.Zero);
        Step(fixture: fixture, interactOrdinal: interact, value: Full);
        Assert.True(condition: fixture.Server.Body(index: 0)!.Engaged);
    }

    [Fact]
    public void TheFrameNamesTheSideAPositionStandsOnAndTheCellARayMeetsOnThePlayingSurface() {
        var topology = WorldTopologyCompilation.Find(
            definition: Document(),
            name: TopologyName
        )!;
        var surface = FixedQ4816.FromDouble(value: 0.5d);
        var down = new FixedVector3(X: FixedQ4816.Zero, Y: -Full, Z: FixedQ4816.Zero);

        Assert.Equal(expected: new FixedVector3(X: FixedQ4816.FromInteger(value: 4L), Y: FixedQ4816.Zero, Z: Full), actual: WorldTabletopFrame.Centre(topology: topology));
        Assert.Equal(expected: WorldTabletopFrame.SidePositiveX, actual: WorldTabletopFrame.SideOf(position: FixedVector3.FromVector3(value: new Vector3(x: 12f, y: 0f, z: 2f)), topology: topology));
        Assert.Equal(expected: WorldTabletopFrame.SideNegativeX, actual: WorldTabletopFrame.SideOf(position: FixedVector3.FromVector3(value: new Vector3(x: -4f, y: 0f, z: 2f)), topology: topology));
        Assert.Equal(expected: WorldTabletopFrame.SidePositiveZ, actual: WorldTabletopFrame.SideOf(position: FixedVector3.FromVector3(value: new Vector3(x: 4f, y: 0f, z: 3f)), topology: topology));

        // A slanted ray meets the surface, not the topology's origin plane: from (1, 2.5, 1) along (1, -1, 0) it lands
        // at x = 3 on the surface 0.5 up (cell 1), where the origin plane would have been x = 3.5.
        Assert.True(condition: WorldTabletopFrame.TryPointerCell(
            cell: out var cell,
            ray: new SourceRay(
                Direction: new FixedVector3(X: Full, Y: -Full, Z: FixedQ4816.Zero),
                Origin: FixedVector3.FromVector3(value: new Vector3(x: 1f, y: 2.5f, z: 1f))
            ),
            surface: surface,
            topology: topology
        ));
        Assert.Equal(expected: 1, actual: cell);

        // Rising, starting below the surface, or landing off the strip names no cell.
        Assert.False(condition: WorldTabletopFrame.TryPointerCell(cell: out _, ray: new SourceRay(Direction: new FixedVector3(X: FixedQ4816.Zero, Y: Full, Z: FixedQ4816.Zero), Origin: FixedVector3.FromVector3(value: new Vector3(x: 1f, y: 2f, z: 1f))), surface: surface, topology: topology));
        Assert.False(condition: WorldTabletopFrame.TryPointerCell(cell: out _, ray: new SourceRay(Direction: down, Origin: FixedVector3.FromVector3(value: new Vector3(x: 1f, y: 0.25f, z: 1f))), surface: surface, topology: topology));
        Assert.False(condition: WorldTabletopFrame.TryPointerCell(cell: out var off, ray: new SourceRay(Direction: down, Origin: FixedVector3.FromVector3(value: new Vector3(x: 9f, y: 2f, z: 1f))), surface: surface, topology: topology));
        Assert.Equal(expected: -1, actual: off);
    }

    [Fact]
    public void AnEngageBlockNoProbeOrCameraCouldUseIsRefusedByName() {
        Assert.True(condition: WorldDefinitionValidator.TryValidate(
            definition: Document(),
            neighbours: null,
            reason: out var cleanReason
        ), userMessage: cleanReason);
        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: Document(engage: new WorldTabletopEngage(
                Channel: "wave",
                Margin: -1d,
                Pitch: 0d,
                Radius: 0d,
                Surface: double.NaN
            )),
            neighbours: null,
            reason: out var reason
        ));

        foreach (var expected in new[] {
            "board.engage.channel 'wave' names no declared channel",
            "board.engage.radius must be a finite positive distance",
            "board.engage.pitch must be in (0, 90] degrees",
            "board.engage.margin must be a finite non-negative distance",
            "board.engage.surface must be a finite non-negative height",
        }) {
            Assert.Contains(expectedSubstring: expected, actualString: reason);
        }

        // A seat read needs an engageable tabletop under the row and a seat the document seats.
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: Document(engageable: false), neighbours: null, reason: out var unseatable));
        Assert.Contains(expectedSubstring: "whose topology 'strip' no placement's board facet anchors with an engage block", actualString: unseatable);
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: Document(seat: 9), neighbours: null, reason: out var unseated));
        Assert.Contains(expectedSubstring: "does not spell '$board:engaged|pointer|side:<row>:<seat>'", actualString: unseated);
    }
}
