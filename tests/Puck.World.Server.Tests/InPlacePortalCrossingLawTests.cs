using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a portal pair whose two faces stand in one world moves its traveller in place. One file-backed
/// row references its own document, so its destination resolves to the row itself; a door laid flat in the floor faces
/// up and its counterpart, a door in the ceiling above it, faces down. A seat dropped over the floor door falls
/// through it, comes out of the ceiling door still falling at the speed it entered with, and falls through the floor
/// door again, keeping its body index throughout, and a tape recorded across the loop replays to the same states. Red
/// leg: a floor door whose counterpart is a wall door would turn a fall into a walk, so the crossing is refused by name
/// and the body falls on through the floor.
/// </summary>
public sealed class InPlacePortalCrossingLawTests {
    private const float CeilingHeight = 6f;
    private const int CrossingsWanted = 3;
    private const float DoorZ = -10f;
    private const string Face = "glass";
    private const int LoopBound = 600;

    private static readonly WorldChannelTable Channels = WorldChannelTable.Compile(channels: Fixtures.BuildDocument().Channels);

    // A square door creation whose glass is shape 0, turned by `rotation` in engine terms: a quarter turn about +X lays
    // the glass (its +Z face) flat. Converted to the author frame the engine door converts back from.
    private static WorldPrototype Door(string id, float angle) {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: id,
            Palette: null,
            Shapes: [new ShapeDocument(
                Id: 0,
                Name: null,
                Type: SdfSolidPrimitive.Box,
                Position: Vector3.Zero,
                Rotation: Quaternion.CreateFromAxisAngle(
                    angle: angle,
                    axis: Vector3.UnitX
                ),
                Scale: new Vector3(
                    x: 1.25f,
                    y: 1.25f,
                    z: 0.06f
                ),
                Material: 0,
                Blend: SdfBlendOp.Union,
                Smooth: 0f,
                Group: 0
            )],
            Frames: null,
            Behavior: new CreationBehaviorDocument(
                Locomotion: null,
                Faces: [new CreationFaceDocument(
                    DefaultSource: null,
                    Name: Face,
                    ShapeId: 0
                )]
            )
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: CreationFrame.ToAuthor(document: document),
            source: id
        );

        return new WorldPrototype(
            Id: id,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    private static WorldPlacement Placement(string id, string prototype, Vector3 position, float yawDegrees, string? counterpart) => new(
        FaceSources: [new WorldPlacementFace(
            Face: Face,
            Portal: ((counterpart is null)
            ? null
            : new WorldPlacementPortal(
                Arrival: WorldPortalArrival.Mapped,
                Counterpart: counterpart,
                Destination: "self",
                Travel: WorldPortalTravel.Body
            )),
            Source: new WorldScreenSource.None()
        )],
        Id: id,
        Position: position,
        PrototypeId: prototype,
        Scale: 1f,
        YawDegrees: yawDegrees
    );
    // The floor door at the origin of the shaft facing up, and the door its glass leads to: the ceiling door facing
    // down, turned a half about up so the pair keeps a traveller's heading, or a wall door facing +Z.
    private static WorldDefinition Shaft(string ownPath, bool wallCounterpart) => (Fixtures.PeerPopulationDocument(networkPlayers: 2) with {
        CreationsRaw = [
            Door(angle: (-MathF.PI / 2f), id: "floor-door"),
            Door(angle: (MathF.PI / 2f), id: "ceiling-door"),
            Door(angle: 0f, id: "wall-door"),
        ],
        Destinations = [new WorldDestination(
            Durability: WorldDestinationDurability.Persisted,
            Name: SafeName.Parse(candidate: "self"),
            Reference: "self",
            Scope: WorldDestinationScope.Global
        )],
        PlacementRowsRaw = [
            Placement(
                counterpart: (wallCounterpart ? $"wall/{Face}" : $"ceiling/{Face}"),
                id: "floor",
                position: new Vector3(x: 0f, y: 0f, z: DoorZ),
                prototype: "floor-door",
                yawDegrees: 0f
            ),
            Placement(
                counterpart: $"floor/{Face}",
                id: "ceiling",
                position: new Vector3(x: 0f, y: CeilingHeight, z: DoorZ),
                prototype: "ceiling-door",
                yawDegrees: 180f
            ),
            Placement(
                counterpart: null,
                id: "wall",
                position: new Vector3(x: 20f, y: 1.5f, z: DoorZ),
                prototype: "wall-door",
                yawDegrees: 0f
            ),
        ],
        References = [new WorldReference(
            Document: WorldDocumentName.OfDocumentFile(path: ownPath),
            Name: SafeName.Parse(candidate: "self")
        )],
    });

    private sealed class Scenario : IDisposable {
        private readonly TemporaryDirectory m_files;
        private readonly TemporaryDirectory m_hostRoot;
        private readonly TemporaryDirectory m_rowState;
        private readonly TemporaryDirectory m_tapeRoot;
        private readonly IDisposable m_narrationLease;

        public Scenario(bool wallCounterpart) {
            m_files = new TemporaryDirectory(prefix: "puck-in-place-files-");
            m_hostRoot = new TemporaryDirectory(prefix: "puck-in-place-host-");
            m_tapeRoot = new TemporaryDirectory(prefix: "puck-in-place-tape-");

            var path = Path.Combine(
                path1: m_files.RootPath,
                path2: "shaft.world.json"
            );
            var definition = Shaft(
                ownPath: path,
                wallCounterpart: wallCounterpart
            );

            FileBackedRows.Write((path, definition));
            Host = new WorldInstanceHost(
                admitsSpawn: true,
                applicationStopping: CancellationToken.None,
                machineHostFactory: Fixtures.MachineHostFactory,
                machineId: Guid.NewGuid(),
                resolver: new WorldSessionResolver(),
                seats: WorldEmbodiedSeats.None,
                stateRoot: new WorldStateRoot(path: m_hostRoot.RootPath)
            );
            (Instance, Server, m_rowState) = FileBackedRows.Build(
                definition: definition,
                name: "shaft",
                path: path
            );
            Host.Admit(row: Instance);
            m_narrationLease = Host.AttachNarrationSink(sink: Narration);
            Tape = new WorldReplayTape(
                addonHostFactory: static (_, _) => new NullAddonHost(),
                engines: [],
                liveServer: Server,
                machineHostFactory: Fixtures.MachineHostFactory,
                profiles: Server.Profiles,
                stateRoot: new WorldStateRoot(path: m_tapeRoot.RootPath),
                transport: Link
            );
            Instance.Tape = Tape;
            Assert.True(condition: Server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: 0),
                Slot: 0,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
        }

        public WorldInstanceHost Host { get; }
        public WorldInstance Instance { get; }
        public LoopbackTransport Link => ((LoopbackTransport)Instance.Link);
        public RecordingNarrationSink Narration { get; } = new();
        public WorldServer Server { get; }
        public WorldReplayTape Tape { get; }

        public void Dispose() {
            m_narrationLease.Dispose();
            Host.Dispose();
            Instance.Dispose();
            m_rowState.Dispose();
            m_tapeRoot.Dispose();
            m_hostRoot.Dispose();
            m_files.Dispose();
        }
        public int Count(string text) => Narration.Narrations.Count(predicate: line => line.Text.Contains(
            comparisonType: StringComparison.Ordinal,
            value: text
        ));
        // Stands the seat over the floor door, through the loopback so a tape records it.
        public void DropOverFloor() => Link.SubmitCommand(command: new WorldCommand.SnapPose(
            EntityIndex: 0,
            Mode: SnapPoseMode.Pose,
            PitchRadians: 0f,
            Position: new Vector3(x: 0f, y: 3f, z: DoorZ),
            Principal: Principal.Console,
            RollRadians: 0f,
            YawRadians: 0f
        ));
        // One master step: the host's transfer drain, then the row's step.
        public void Step() {
            Host.DrainPendingTransfers();
            Host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }
    }

    [Fact]
    public void ABodyFallingThroughAFloorDoorComesOutOfTheCeilingDoorStillFalling_AndLoops() {
        using var scenario = new Scenario(wallCounterpart: false);
        var name = $"in-place-{Guid.NewGuid():N}";

        Assert.True(
            condition: scenario.Tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: refusal
        );
        scenario.DropOverFloor();

        var crossings = 0;

        for (var tick = 0; ((tick < LoopBound) && (crossings < CrossingsWanted)); tick++) {
            var body = scenario.Server.Body(index: 0)!;
            var entering = body.CaptureTransferState().VerticalVelocity;

            scenario.Host.DrainPendingTransfers();

            if (scenario.Count(text: "in place") > crossings) {
                crossings++;

                // The same body, at the same index, out of the ceiling door and falling exactly as fast as it fell in.
                Assert.True(condition: scenario.Server.Population.IsActive(index: 0));
                Assert.Same(
                    actual: scenario.Server.Body(index: 0),
                    expected: body
                );
                Assert.True(
                    condition: (body.FixedPosition.Y >= FixedQ4816.FromDouble(value: CeilingHeight)),
                    userMessage: $"crossing {crossings} landed at {body.FixedPosition}, below the ceiling door"
                );
                Assert.True(condition: (entering < FixedQ4816.Zero));
                Assert.Equal(
                    actual: body.CaptureTransferState().VerticalVelocity,
                    expected: entering
                );
            }

            scenario.Host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }

        Assert.Equal(
            actual: crossings,
            expected: CrossingsWanted
        );

        var stop = scenario.Tape.StopRecording();

        Assert.Null(@object: stop.VerifyFault);
        Assert.True(
            condition: stop.Verdict!.Passing,
            userMessage: stop.Verdict.Describe()
        );

        using var stream = File.OpenRead(path: scenario.Tape.PathFor(name: name));
        var tape = WorldReplaySnapshot.Read(stream: stream);

        Assert.Equal(
            actual: tape.Ticks.Sum(selector: tick => tick.Authority.Count(predicate: entry => WorldReplayEntryDescriber.Describe(
                channels: Channels,
                entry: entry
            ).Equals(
                comparisonType: StringComparison.Ordinal,
                value: "in-place crossing body:0"
            ))),
            expected: CrossingsWanted
        );
    }
    [Fact]
    public void AFloorDoorLeadingToAWallDoorRefusesByName_AndTheBodyFallsOnThroughTheFloor() {
        using var scenario = new Scenario(wallCounterpart: true);

        scenario.DropOverFloor();

        for (var tick = 0; (tick < 60); tick++) {
            scenario.Step();
        }

        Assert.True(condition: (scenario.Count(text: "do not map world up onto itself") > 0));
        Assert.Equal(
            actual: scenario.Count(text: "in place"),
            expected: 0
        );
        Assert.True(condition: (scenario.Server.Body(index: 0)!.FixedPosition.Y < FixedQ4816.Zero));
    }
    [Fact]
    public void AnIndexHoldingNoBodyCrossesNothing() {
        using var fixture = Fixtures.FreshServer();
        var frame = new WorldFaceFrame(
            HalfDepth: FixedQ4816.One,
            HalfHeight: FixedQ4816.One,
            HalfWidth: FixedQ4816.One,
            Normal: FixedVector3.UnitZ,
            Origin: FixedVector3.Zero,
            Right: FixedVector3.UnitX,
            Up: FixedVector3.UnitY
        );

        Assert.Null(@object: fixture.Server.ExecuteAuthorityOperation(operation: () => fixture.Server.CrossInPlace(
            destination: in frame,
            slot: -1,
            source: in frame
        )));
        Assert.False(condition: fixture.Server.ExecuteAuthorityOperation(operation: () => fixture.Server.ApplyInPlaceCrossing(crossing: new WorldInPlaceCrossing(
            PlanarVelocity: FixedVector3.Zero,
            Position: FixedVector3.Zero,
            Slot: -1,
            TravelTurn: FixedQ4816.Zero,
            VerticalVelocity: FixedQ4816.Zero,
            YawRadians: FixedQ4816.Zero
        ))));
    }
}
