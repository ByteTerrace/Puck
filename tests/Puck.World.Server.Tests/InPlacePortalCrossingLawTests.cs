using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Motion;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a portal pair whose two faces stand in one world moves its traveller in place. One file-backed
/// row references its own document, so its destination resolves to the row itself; a door laid flat in the floor faces
/// up and its counterpart, a door in the ceiling above it, faces down. A seat dropped over the floor door falls
/// through it, comes out of the ceiling door still falling at the speed it entered with, and falls through the floor
/// door again, keeping its body index throughout, and a tape recorded across the loop replays to the same states. The
/// crossing is continuous motion, not a teleport: the body stays airborne through it, and a presentation mirror sees
/// one carried pose change per crossing, never a snap, whose presented step is the fall's own step rather than the
/// six metres the body moved up. A ceiling door left unturned turns the traveller a half about up on each crossing.
/// Red leg: a floor door whose counterpart is a wall door would turn a fall into a walk, so the crossing is refused
/// by name and the body falls on through the floor.
/// </summary>
public sealed class InPlacePortalCrossingLawTests {
    /// <summary>The door the floor door leads to.</summary>
    public enum Counterpart {
        /// <summary>The ceiling door, turned a half about up so the pair keeps the traveller's heading.</summary>
        Ceiling,
        /// <summary>The ceiling door unturned, so the pair turns the traveller a half about up.</summary>
        TurnedCeiling,
        /// <summary>A wall door, which no fall can cross into.</summary>
        Wall,
        /// <summary>A placement the world does not hold.</summary>
        Missing,
        /// <summary>The ceiling door at half the floor door's size, in a world whose bodies.scaleRow holds each size
        /// from a quarter to one, so each crossing halves the traveller until the third would leave the row.</summary>
        HalfCeiling,
        /// <summary>The half-size ceiling door in a world that authors no bodies.scaleRow.</summary>
        HalfCeilingUnscaled,
        /// <summary>A ceiling door as wide as the floor door and half as deep, which no one scale maps onto it.</summary>
        StretchedCeiling,
    }

    private const float CeilingHeight = 6f;
    private const int CrossingsWanted = 3;
    private const float DoorZ = -10f;
    private const string Face = "glass";
    private const int LoopBound = 600;

    private static readonly WorldChannelTable Channels = WorldChannelTable.Compile(channels: Fixtures.BuildDocument().Channels);

    // A square door creation whose glass is shape 0, turned by `rotation` in engine terms: a quarter turn about +X lays
    // the glass (its +Z face) flat. Converted to the author frame the engine door converts back from.
    private static WorldPrototype Door(string id, float angle, float width = 1.25f, float height = 1.25f) {
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
                    x: width,
                    y: height,
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
    // down, turned a half about up so the pair keeps a traveller's heading or left unturned, or a wall door facing +Z.
    private static WorldDefinition Shaft(string ownPath, Counterpart counterpart) {
        var source = Fixtures.PeerPopulationDocument(networkPlayers: 2);
        var shaft = Shaft(
            counterpart: counterpart,
            ownPath: ownPath,
            source: source
        );

        if (counterpart != Counterpart.HalfCeiling) {
            return shaft;
        }

        // Each body's size, a quarter to one; seat 0 starts at one.
        var scaleRow = new WorldStateRow(
            Name: CellName.Parse(candidate: "scale"),
            Kind: CellKind.Fixed,
            Min: FixedQ4816.FromDouble(value: 0.25).Value,
            Max: FixedQ4816.One.Value,
            Capacity: 8,
            Cells: [new StateCell(
                Key: CellName.Parse(candidate: "0"),
                Value: CellValue.Fixed(rawBits: FixedQ4816.One.Value)
            )]
        );

        return (shaft with {
            PopulationRaw = (shaft.Population with { ScaleRow = "scale" }),
            StateRaw = ((shaft.StateRaw ?? new WorldStateSection()) with {
                World = [.. (shaft.StateRaw?.World ?? []), scaleRow],
            }),
        });
    }
    private static WorldDefinition Shaft(string ownPath, Counterpart counterpart, WorldDefinition source) => (source with {
        CreationsRaw = [
            Door(angle: (-MathF.PI / 2f), id: "floor-door"),
            (counterpart switch {
                Counterpart.HalfCeiling or Counterpart.HalfCeilingUnscaled => Door(angle: (MathF.PI / 2f), height: 0.625f, id: "ceiling-door", width: 0.625f),
                Counterpart.StretchedCeiling => Door(angle: (MathF.PI / 2f), height: 0.625f, id: "ceiling-door"),
                _ => Door(angle: (MathF.PI / 2f), id: "ceiling-door"),
            }),
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
                counterpart: (counterpart switch {
                    Counterpart.Wall => $"wall/{Face}",
                    Counterpart.Missing => $"missing/{Face}",
                    _ => $"ceiling/{Face}",
                }),
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
                yawDegrees: ((counterpart == Counterpart.TurnedCeiling) ? 0f : 180f)
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
        private readonly IDisposable m_mirrorLease;
        private readonly ViewSeats? m_seats;

        // A row admitted as an ordinary instance, or, with `seats`, as the host's boot row with those seats routed to it.
        public Scenario(Counterpart counterpart, ViewSeats? seats = null) {
            m_seats = seats;
            m_files = new TemporaryDirectory(prefix: "puck-in-place-files-");
            m_hostRoot = new TemporaryDirectory(prefix: "puck-in-place-host-");
            m_tapeRoot = new TemporaryDirectory(prefix: "puck-in-place-tape-");

            var path = Path.Combine(
                path1: m_files.RootPath,
                path2: "shaft.world.json"
            );
            var definition = Shaft(
                counterpart: counterpart,
                ownPath: path
            );

            FileBackedRows.Write((path, definition));
            Host = new WorldInstanceHost(
                admitsSpawn: true,
                applicationStopping: CancellationToken.None,
                machineHostFactory: Fixtures.MachineHostFactory,
                machineId: Guid.NewGuid(),
                resolver: new WorldSessionResolver(),
                seats: (((IWorldEmbodiedSeats?)seats) ?? WorldEmbodiedSeats.None),
                stateRoot: new WorldStateRoot(path: m_hostRoot.RootPath)
            );
            (Instance, Server, m_rowState) = FileBackedRows.Build(
                definition: definition,
                name: ((seats is null) ? "shaft" : WorldInstanceHost.BootInstanceName),
                path: path
            );
            if (seats is null) {
                Host.Admit(row: Instance);
            } else {
                Host.AdmitBoot(row: Instance);
            }
            m_narrationLease = Host.AttachNarrationSink(sink: Narration);
            Mirror = new WorldSessionMirror(placeholder: definition);
            m_mirrorLease = Server.AttachSink(sink: Mirror);
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
        // A presentation mirror of the row, fed every snapshot the row delivers.
        public WorldSessionMirror Mirror { get; }
        public RecordingNarrationSink Narration { get; } = new();
        public WorldServer Server { get; }
        public WorldReplayTape Tape { get; }

        public void Dispose() {
            m_mirrorLease.Dispose();
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
        // One master step: the host's transfer drain, then the row's step and its portal scan — the boot row steps and
        // scans as the host's own step drives it.
        public void Step() {
            Host.DrainPendingTransfers();

            if (m_seats is null) {
                Host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            } else {
                Server.Advance(stepTicks: Fixtures.StepTicks);
                Host.ScanBootBoundaryTriggers();
            }
        }
    }

    [InlineData(Counterpart.Ceiling)]
    [InlineData(Counterpart.TurnedCeiling)]
    [Theory]
    public void ABodyFallingThroughAFloorDoorComesOutOfTheCeilingDoorStillFalling_AndLoops(Counterpart counterpart) {
        using var scenario = new Scenario(counterpart: counterpart);
        var name = $"in-place-{Guid.NewGuid():N}";
        var stepSeconds = (1f / Fixtures.DefaultRateHz);

        Assert.True(
            condition: scenario.Tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: refusal
        );
        scenario.DropOverFloor();
        // The pose itself is a snap; the fall it starts is what the mirror must present without one.
        scenario.Step();

        var poseEpoch = scenario.Mirror.PoseEpoch(index: 0);
        var crossings = 0;
        var turn = ((counterpart == Counterpart.TurnedCeiling)
            ? FixedQ4816.Pi
            : FixedQ4816.Zero
        );

        for (var tick = 0; ((tick < LoopBound) && (crossings < CrossingsWanted)); tick++) {
            var body = scenario.Server.Body(index: 0)!;
            var entering = body.CaptureTransferState().VerticalVelocity;
            var heading = body.FixedYaw;

            scenario.Host.DrainPendingTransfers();

            var crossed = (scenario.Count(text: "in place") > crossings);

            if (crossed) {
                crossings++;

                // The same body, at the same index, out of the ceiling door, turned by the pair and falling exactly as
                // fast as it fell in.
                Assert.True(condition: scenario.Server.Population.IsActive(index: 0));
                Assert.Same(
                    actual: scenario.Server.Body(index: 0),
                    expected: body
                );
                // It fired once its origin passed through the floor glass, so it comes out just through the ceiling
                // glass, inside the shaft.
                Assert.True(
                    condition: (body.FixedPosition.Y <= FixedQ4816.FromDouble(value: CeilingHeight)),
                    userMessage: $"crossing {crossings} landed at {body.FixedPosition}, above the ceiling door"
                );
                Assert.True(condition: (entering < FixedQ4816.Zero));
                Assert.Equal(
                    actual: body.CaptureTransferState().VerticalVelocity,
                    expected: entering
                );
                Assert.Equal(
                    actual: FixedQ4816.Abs(value: FixedQ4816.WrapAngle(radians: (body.FixedYaw - heading))),
                    expected: turn
                );
            }

            scenario.Host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);

            // Presented, the crossing is a carried pose change and the fall's own step: no snap, no grounded tick, and
            // a downward step no longer than the body's own speed covers, where a snap would present six metres up.
            var presented = (scenario.Mirror.CurrentPosition(index: 0) - scenario.Mirror.PreviousPosition(index: 0));
            var fall = MathF.Abs(x: ((float)scenario.Server.Body(index: 0)!.CaptureTransferState().VerticalVelocity));

            Assert.Equal(
                actual: scenario.Mirror.PoseEpoch(index: 0),
                expected: poseEpoch
            );
            Assert.Equal(
                actual: scenario.Mirror.Carries.Epoch(index: 0),
                expected: crossings
            );
            Assert.True(condition: scenario.Mirror.Facts(index: 0).HasFlag(flag: BodyFacts.Airborne));
            Assert.False(condition: scenario.Mirror.Facts(index: 0).HasFlag(flag: BodyFacts.Grounded));
            Assert.True(
                condition: ((presented.Y < 0f) && (-presented.Y <= ((fall * stepSeconds) + 1e-3f))),
                userMessage: $"tick {tick}{(crossed ? ", a crossing," : string.Empty)} presented a step of {presented} for a fall at {fall} m/s"
            );
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
    public void ADoorOntoOneHalfItsSizeHalvesTheTravellerEachCrossing_UntilItsSizeWouldLeaveTheRow_AndReplays() {
        using var scenario = new Scenario(counterpart: Counterpart.HalfCeiling);
        var name = $"in-place-scaled-{Guid.NewGuid():N}";
        var half = FixedQ4816.FromDouble(value: 0.5);

        Assert.True(
            condition: scenario.Tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: refusal
        );
        scenario.DropOverFloor();
        scenario.Step();

        var crossings = 0;
        var size = FixedQ4816.One;

        for (var tick = 0; ((tick < LoopBound) && (scenario.Count(text: "outside bodies.scaleRow") == 0)); tick++) {
            var body = scenario.Server.Body(index: 0)!;
            var entering = body.CaptureTransferState().VerticalVelocity;

            scenario.Host.DrainPendingTransfers();

            if (scenario.Count(text: "in place") > crossings) {
                crossings++;
                size *= half;

                // Half the size, falling half as fast, its row cell written with it.
                Assert.Equal(
                    actual: body.Scale,
                    expected: size
                );
                Assert.Equal(
                    actual: body.CaptureTransferState().VerticalVelocity,
                    expected: (entering * half)
                );
                Assert.True(condition: WorldStateReader.TryRead(
                    definition: scenario.Server.Definition,
                    engineTick: 0UL,
                    key: "0",
                    rawValue: out var raw,
                    row: out _,
                    rowName: "scale",
                    text: out _,
                    tick: 0UL
                ));
                Assert.Equal(
                    actual: raw,
                    expected: size.Value
                );
            }

            scenario.Host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }

        // A quarter is the row's floor: the third crossing would make an eighth, so it is refused and the body keeps its
        // size.
        Assert.Equal(
            actual: crossings,
            expected: 2
        );
        Assert.Equal(
            actual: scenario.Server.Body(index: 0)!.Scale,
            expected: FixedQ4816.FromDouble(value: 0.25)
        );

        var stop = scenario.Tape.StopRecording();

        Assert.Null(@object: stop.VerifyFault);
        Assert.True(
            condition: stop.Verdict!.Passing,
            userMessage: stop.Verdict.Describe()
        );
    }
    [InlineData(Counterpart.Wall, "do not map world up onto itself")]
    [InlineData(Counterpart.Missing, "names no placement 'missing'")]
    [InlineData(Counterpart.HalfCeilingUnscaled, "authors no bodies.scaleRow")]
    [InlineData(Counterpart.StretchedCeiling, "no one scale maps one onto the other")]
    [Theory]
    public void AFloorDoorWhoseCounterpartCannotTakeAFallRefusesByName_AndTheBodyFallsOnThroughTheFloor(Counterpart counterpart, string refusal) {
        using var scenario = new Scenario(counterpart: counterpart);

        scenario.DropOverFloor();

        for (var tick = 0; (tick < 60); tick++) {
            scenario.Step();
        }

        Assert.True(condition: (scenario.Count(text: refusal) > 0));
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

        Assert.False(condition: fixture.Server.ExecuteAuthorityOperation(operation: () => fixture.Server.TryApplyInPlaceCrossing(
            crossing: new WorldInPlaceCrossing(
                Destination: frame,
                Slot: -1,
                Source: frame
            ),
            reason: out _
        )));
    }
    [Fact]
    public void ATurningPairTurnsTheViewOfTheSeatRoutedToTheBody() {
        var seats = new ViewSeats();

        using var scenario = new Scenario(
            counterpart: Counterpart.TurnedCeiling,
            seats: seats
        );

        scenario.DropOverFloor();

        for (var tick = 0; ((tick < LoopBound) && (scenario.Count(text: "in place") < CrossingsWanted)); tick++) {
            scenario.Step();
        }

        Assert.Equal(
            actual: seats.Turns,
            expected: Enumerable.Repeat(count: CrossingsWanted, element: (0, FixedQ4816.Pi))
        );
    }
    [Fact]
    public void GroundBehindAFloorDoorHoldsNothingInItsOpening_AndStillHoldsBesideIt() {
        var field = WorldPortalHoleContactField.Wrap(
            definition: Shaft(counterpart: Counterpart.Ceiling, ownPath: "shaft.world.json"),
            inner: new FlatGround()
        );
        var falling = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromInteger(value: -5), Z: FixedQ4816.Zero);

        Assert.IsType<WorldPortalHoleContactField>(@object: field);

        foreach (var sweep in new[] { false, true }) {
            // Through the floor glass, in its opening: the ground under it pushes nothing back.
            var position = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: -0.25), Z: FixedQ4816.FromDouble(value: DoorZ));
            var velocity = falling;
            var opened = Resolve(field: field, position: ref position, sweep: sweep, velocity: ref velocity);

            Assert.False(condition: opened.Grounded);
            Assert.Equal(expected: FixedQ4816.FromDouble(value: -0.25), actual: position.Y);
            Assert.Equal(actual: velocity, expected: falling);

            // Beside the door the same ground stands the body up.
            position = new FixedVector3(X: FixedQ4816.FromInteger(value: 5), Y: FixedQ4816.FromDouble(value: -0.25), Z: FixedQ4816.FromDouble(value: DoorZ));
            velocity = falling;

            Assert.True(condition: Resolve(field: field, position: ref position, sweep: sweep, velocity: ref velocity).Grounded);
            Assert.Equal(expected: FixedQ4816.Zero, actual: position.Y);

            // In the opening, a body that fits it falls through; one too big for it, a box wider than the door or a
            // capsule reaching past its rim, meets the ground round it.
            FixedBodyColliderVolume[][] bodies = [
                [new FixedBodyColliderVolume(Kind: FixedBodyColliderKind.Capsule, Center: FixedVector3.Zero, Endpoint: (FixedVector3.UnitY * FixedQ4816.FromInteger(value: 2)), HalfExtents: FixedVector3.Zero, Rotation: FixedQuaternion.Identity, Radius: FixedQ4816.FromDouble(value: 0.35))],
                [new FixedBodyColliderVolume(Kind: FixedBodyColliderKind.Box, Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero, HalfExtents: new FixedVector3(X: FixedQ4816.FromInteger(value: 2), Y: FixedQ4816.One, Z: FixedQ4816.One), Rotation: FixedQuaternion.Identity, Radius: FixedQ4816.Zero)],
                [new FixedBodyColliderVolume(Kind: FixedBodyColliderKind.Capsule, Center: FixedVector3.Zero, Endpoint: (FixedVector3.UnitX * FixedQ4816.FromInteger(value: 3)), HalfExtents: FixedVector3.Zero, Rotation: FixedQuaternion.Identity, Radius: FixedQ4816.FromDouble(value: 0.35))],
            ];

            for (var body = 0; (body < bodies.Length); body++) {
                position = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: -0.25), Z: FixedQ4816.FromDouble(value: DoorZ));
                velocity = falling;

                Assert.Equal(
                    actual: Resolve(field: field, position: ref position, sweep: sweep, velocity: ref velocity, volumes: bodies[body]).Grounded,
                    expected: (body > 0)
                );
            }
        }

        Assert.True(condition: field.TryUp(position: FixedVector3.Zero, up: out var up));
        Assert.Equal(expected: FixedVector3.UnitY, actual: up);
        Assert.False(condition: field.TryHoldableSurfaceAlongDirection(candidate: out _, direction: -FixedVector3.UnitY, grantedByOverride: out _, maxDistance: FixedQ4816.One, origin: FixedVector3.Zero));
        Assert.False(condition: field.TryNearestSurfaceAlongDirection(assistHalfAngle: FixedQ4816.Zero, candidate: out _, direction: -FixedVector3.UnitY, maxDistance: FixedQ4816.One, origin: FixedVector3.Zero));

        static ContactResolution Resolve(IContactField field, ref FixedVector3 position, ref FixedVector3 velocity, bool sweep, FixedBodyColliderVolume[]? volumes = null) => (sweep
            ? field.ResolveSweep(orientation: FixedQuaternion.Identity, position: ref position, previousPosition: position, up: FixedVector3.UnitY, velocity: ref velocity, volumes: (volumes ?? []))
            : field.Resolve(orientation: FixedQuaternion.Identity, position: ref position, up: FixedVector3.UnitY, velocity: ref velocity, volumes: (volumes ?? [])));
    }

    // Solid ground under y = 0: a foot point below it stands on it, its fall stopped.
    private sealed class FlatGround : IContactField {
        public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) {
            if (position.Y > FixedQ4816.Zero) {
                return new ContactResolution(Grounded: false, ObstructionNormal: FixedVector3.Zero);
            }

            position = new FixedVector3(X: position.X, Y: FixedQ4816.Zero, Z: position.Z);
            velocity = new FixedVector3(X: velocity.X, Y: FixedQ4816.Zero, Z: velocity.Z);

            return new ContactResolution(Grounded: true, ObstructionNormal: FixedVector3.Zero, GroundNormal: FixedVector3.UnitY);
        }
        public bool TryUp(in FixedVector3 position, out FixedVector3 up) {
            up = FixedVector3.UnitY;

            return true;
        }
    }

    // One local seat, occupied and routed wherever the host publishes it, recording each view turn the host asks for.
    public sealed class ViewSeats : IWorldEmbodiedSeats {
        private WorldAuthorityEndpoint? m_endpoint;
        private WorldEntityAddress m_entity;

        public int SeatCount => 1;
        public List<(int Slot, FixedQ4816 Turn)> Turns { get; } = [];

        public void AdvanceSeatViews(float deltaSeconds) { }
        public void ClearAnalog() { }
        public void ClearHeld(int slot) { }
        public void ConfigureLeave(Func<int, Principal, bool> leave) { }
        public void CrossView(int slot, FixedQ4816 yawDelta, WorldSeatYawReference yawReference) => Turns.Add(item: (slot, yawDelta));
        public bool IsOccupied(int slot) => true;
        public bool OccupySeat(int slot, WorldIdentity? profile) => true;
        public void PublishRoute(int slot, WorldAuthorityEndpoint endpoint, in WorldEntityAddress entity) {
            m_endpoint = endpoint;
            m_entity = entity;
        }
        public WorldAuthorityEndpoint? RoutedEndpoint(int slot) => m_endpoint;
        public WorldEntityAddress RoutedEntity(int slot) => m_entity;
        public void SubmitAuthorityIntents(WorldAuthorityEndpoint endpoint, ulong tick) { }
        public bool TryUpdateRoutedEntity(int slot, WorldAuthorityEndpoint expectedEndpoint, in WorldEntityAddress replacement) => false;
        public bool VacateSeat(int slot) => false;
    }
}
