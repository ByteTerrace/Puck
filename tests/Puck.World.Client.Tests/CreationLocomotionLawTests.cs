using System.Numerics;

using Puck.Physics.Motion;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>Pins the directional-locomotion vocabulary a creation rig authors: the travel-direction and planar
/// acceleration signals, a driver read through a <c>dynamics</c> row, and a resting plant that steps rather than
/// slides — each presentation-only, read by the stamp pool's pack path.</summary>
public sealed class CreationLocomotionLawTests {
    private const float Frame = (1f / 60f);
    private static readonly WorldEntityAddress Body = new(
        Authority: "a",
        Generation: 1,
        Index: 0
    );

    private static void Advance(WorldClient client, WorldStampPool pool, DynamicTransform[] transforms, Vector3 position, float yaw, ulong tick) {
        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Authority: "a",
            Entries: new[] { new EntitySnapshot(
                Active: true,
                BodyColor: Vector3.One,
                CatalogRig: 0,
                Continuity: EntityContinuity.Continuous,
                Generation: 1,
                Index: 0,
                Kit: 0,
                Look: 0,
                Orientation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: yaw),
                Position: position
            ) },
            Revision: 0,
            StepTicks: 1UL,
            Tick: tick
        ));
        client.UpdateRenderPoses(alpha: 1f);
        pool.Tick(deltaSeconds: Frame);

        var moved = new SdfMovedTransforms();

        moved.Begin(
            everything: false,
            tableRows: transforms.Length
        );
        pool.PackTransforms(
            client: client,
            moved: moved,
            parkPosition: new Vector3(x: 0f, y: -1000f, z: 0f),
            slotBase: 0,
            transforms: transforms
        );
    }
    // A 4 x 1 x 4 box whose top face is the y = 0 plane, centred on the origin.
    private static SdfProgram FloorProgram() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.5f)));

        _ = builder.Translate(offset: new Vector3(x: 0f, y: -0.5f, z: 0f));
        builder.Box(
            halfExtents: new Vector3(x: 2f, y: 0.5f, z: 2f),
            material: material,
            round: 0f
        );

        return builder.Build();
    }
    private static ShapeDocument Bone(int id, string name, float side, float y, float z, string? parent, Vector3? pivot) => new(
        Id: id,
        Name: name,
        Type: SdfSolidPrimitive.Capsule,
        Position: new Vector3(x: side, y: y, z: z),
        Rotation: Quaternion.Identity,
        Scale: new Vector3(x: 0.05f, y: 0.2f, z: 0.05f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Parent: parent,
        Swings: ((pivot is { } hinge)
            ? [new ShapeSwingDocument(Amplitude: 0f, Axis: Vector3.UnitX, Driver: "stride", Pivot: hinge, Wave: CreationWave.Constant)]
            : null)
    );
    private static CreationEffectorDocument Foot(string name, string side, float from, float to, float? align = null) => new(
        Align: align,
        Chain: [$"thigh-{side}", $"shin-{side}"],
        Name: name,
        Plant: new CreationPlantDocument(
            Driver: "stride",
            Window: new Vector2(x: from, y: to),
            SwingWeight: 0f,
            Replant: new CreationReplantDocument(Distance: 0.05f, Seconds: 0.1f, Height: 0.05f)
        ),
        Target: new CreationEffectorTargetDocument(
            Direction: -Vector3.UnitY,
            Kind: CreationEffectorTargetDocument.KindSurface,
            Reach: 0.5f,
            Standoff: 0.03f
        ),
        Tip: $"boot-{side}",
        When: [CreationDriverDocument.WhenAlways]
    );
    // Two two-bone legs 0.4 apart, each foot planted on the stride driver with a resting replant; `toe` is a constant
    // stride swing on each boot, radians, and `align` each foot's align.
    private static CreationDocument Legs(float? align = null, float toe = 0f) {
        var shapes = new List<ShapeDocument>();
        var id = 0;

        foreach (var (side, x) in new[] { ("left", 0.2f), ("right", -0.2f) }) {
            shapes.Add(item: Bone(id: id++, name: $"thigh-{side}", parent: null, pivot: new Vector3(x: x, y: 0.5f, z: 0f), side: x, y: 0.37f, z: 0.03f));
            shapes.Add(item: Bone(id: id++, name: $"shin-{side}", parent: $"thigh-{side}", pivot: new Vector3(x: x, y: 0.24f, z: 0.06f), side: x, y: 0.11f, z: 0.04f));
            shapes.Add(item: (Bone(id: id++, name: $"boot-{side}", parent: $"shin-{side}", pivot: null, side: x, y: 0.05f, z: 0.02f) with {
                Swings = [new ShapeSwingDocument(Amplitude: toe, Axis: Vector3.UnitX, Driver: "stride", Pivot: new Vector3(x: x, y: 0.05f, z: 0.02f), Wave: CreationWave.Constant)],
            }));
        }

        return new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "rig",
            Palette: null,
            Shapes: shapes,
            Frames: null,
            Drivers: [new CreationDriverDocument(
                Cadence: 6f,
                Name: "stride",
                Signal: CreationDriverDocument.SignalPlanarTravel,
                When: [CreationDriverDocument.TokenMoving]
            )],
            Effectors: [
                Foot(align: align, name: "foot-left", side: "left", from: 0f, to: 3.14f),
                Foot(align: align, name: "foot-right", side: "right", from: 3.14f, to: 6.28f),
            ]
        );
    }
    private static (WorldClient Client, WorldStampPool Pool, DynamicTransform[] Transforms) Stage(float? align = null, float toe = 0f) {
        var creation = CreationFixtures.Prototype(document: Legs(align: align, toe: toe), id: "rig");
        var definition = (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
            CreationsRaw = [creation],
            LookRowsRaw = [new WorldLook(
                Name: "rig",
                Source: new WorldLookSource.Creation(PrototypeId: creation.Id),
                Scale: 1f,
                Motion: WorldLookMotion.Default
            )],
        });
        var client = ClientFixtures.Client(definition: definition);
        var pool = new WorldStampPool();

        client.PublishStaticField(field: new SdfFieldEvaluator(program: FloorProgram()));
        pool.Reconcile(
            bodyStamps: [new WorldStampPool.BodyStamp(BodyIndex: 0, Creation: creation, Scale: 1f, Look: WorldLook.Implicit)],
            creations: [creation],
            dynamics: [],
            placements: []
        );

        return (client, pool, new DynamicTransform[WorldStampPool.DynamicSlotCount]);
    }
    private static WorldRigState Rig(WorldStampPool pool) {
        Assert.True(condition: pool.TryBodyRig(bodyIndex: 0, state: out var state));

        return state!;
    }
    private static float Phase(IReadOnlyList<CreationDriverDocument> drivers, Func<int, (Vector3 Position, float Yaw)> pose, int frames, WorldGaitKinematics? kinematics = null) {
        var phases = new float[CreationDocument.MaxDrivers];
        var weights = new float[CreationDocument.MaxDrivers];
        var lastPosition = Vector3.Zero;
        var lastOrientation = Quaternion.Identity;
        var seeded = false;
        var address = Body;
        var speed = 0f;

        for (var frame = 0; (frame < frames); frame++) {
            var (position, yaw) = pose(frame);

            WorldGaitDrivers.Advance(
                address: Body,
                deltaSeconds: Frame,
                drivers: drivers,
                easedSpeed: ref speed,
                facts: BodyFacts.Grounded,
                kinematics: kinematics,
                lastAddress: ref address,
                lastOrientation: ref lastOrientation,
                lastPosition: ref lastPosition,
                orientation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: yaw),
                phases: phases,
                position: position,
                seeded: ref seeded,
                weights: weights
            );
        }

        return phases[0];
    }
    private static CreationDriverDocument Driver(string signal, float cadence = 1f, string? dynamics = null) => new(
        Cadence: cadence,
        Dynamics: dynamics,
        Name: "probe",
        Signal: signal,
        When: [CreationDriverDocument.WhenAlways]
    );

    /// <summary>The travel direction reads 0 running forward, +π/2 strafing left, π backpedalling and −π/2 strafing
    /// right, whatever way the body faces, and holds its last value once the body stops.</summary>
    [Fact]
    public void TheTravelDirectionIsMeasuredFromTheFacingAndHoldsAtRest() {
        foreach (var yaw in new[] { 0f, 1.1f, -2.3f }) {
            var forward = new Vector3(x: -MathF.Sin(x: yaw), y: 0f, z: -MathF.Cos(x: yaw));
            var left = new Vector3(x: -MathF.Cos(x: yaw), y: 0f, z: MathF.Sin(x: yaw));

            foreach (var (direction, expected) in new[] { (forward, 0f), (left, (MathF.PI / 2f)), (-forward, MathF.PI), (-left, (-MathF.PI / 2f)) }) {
                var kinematics = new WorldGaitKinematics();

                _ = Phase(
                    drivers: [Driver(signal: CreationDriverDocument.SignalTravelDirection)],
                    frames: 90,
                    kinematics: kinematics,
                    pose: frame => (((direction * 0.05f) * frame), yaw)
                );

                Assert.Equal(expected: MathF.Abs(x: expected), actual: MathF.Abs(x: kinematics.TravelDirection), precision: 3);
                if (expected != MathF.PI) {
                    Assert.Equal(expected: expected, actual: kinematics.TravelDirection, precision: 3);
                }
            }
        }

        var held = new WorldGaitKinematics();
        var left90 = new Vector3(x: -1f, y: 0f, z: 0f);
        var phase = Phase(
            drivers: [Driver(signal: CreationDriverDocument.SignalTravelDirection, cadence: 2f)],
            frames: 200,
            kinematics: held,
            pose: frame => (((frame < 60) ? (left90 * (0.05f * frame)) : (left90 * 3f)), 0f)
        );

        Assert.Equal(expected: MathF.PI, actual: phase, precision: 3);
        // Without the kinematics the signal reads zero.
        Assert.Equal(expected: 0f, actual: Phase(drivers: [Driver(signal: CreationDriverDocument.SignalTravelDirection)], frames: 30, pose: frame => ((left90 * (0.05f * frame)), 0f)));
    }
    /// <summary>A run curving left accelerates toward the body's left, a start accelerates forward, and a stop brakes
    /// backward.</summary>
    [Fact]
    public void ThePlanarAccelerationReadsAcrossAndAlongTheFacing() {
        var kinematics = new WorldGaitKinematics();
        var position = Vector3.Zero;
        var yaw = 0f;
        var lateral = Phase(
            drivers: [Driver(signal: CreationDriverDocument.SignalLateralAcceleration)],
            frames: 120,
            kinematics: kinematics,
            pose: frame => {
                yaw = (frame * (2f * Frame));
                position += (new Vector3(x: -MathF.Sin(x: yaw), y: 0f, z: -MathF.Cos(x: yaw)) * (4f * Frame));

                return (position, yaw);
            }
        );

        Assert.InRange(actual: lateral, low: 6f, high: 10f);

        var start = Phase(
            drivers: [Driver(signal: CreationDriverDocument.SignalForwardAcceleration)],
            frames: 4,
            kinematics: new WorldGaitKinematics(),
            pose: frame => (new Vector3(x: 0f, y: 0f, z: -(0.08f * frame)), 0f)
        );
        var stop = Phase(
            drivers: [Driver(signal: CreationDriverDocument.SignalForwardAcceleration)],
            frames: 64,
            kinematics: new WorldGaitKinematics(),
            pose: frame => (new Vector3(x: 0f, y: 0f, z: -(0.08f * Math.Min(val1: frame, val2: 60))), 0f)
        );

        Assert.True(condition: (start > 1f), userMessage: $"start {start}");
        Assert.True(condition: (stop < -1f), userMessage: $"stop {stop}");
    }
    /// <summary>A driver naming a <c>dynamics</c> row trails its signal and settles onto it; the same driver without
    /// one tracks the signal exactly. An angle followed across its ±π seam eases the short way round.</summary>
    [Fact]
    public void ADynamicsRowTrailsTheSignalAndSettles() {
        DynamicsRow[] rows = [new DynamicsRow(Name: "turn", Frequency: 2f, Damping: 0.6f, Response: 0f)];
        IReadOnlyList<CreationDriverDocument> followed = [Driver(dynamics: "turn", signal: CreationDriverDocument.SignalTurnRate)];
        var kinematics = new WorldGaitKinematics();

        kinematics.Resolve(drivers: followed, rows: rows);
        kinematics.Resolve(drivers: followed, rows: rows);

        (Vector3, float) Turning(int frame) => (Vector3.Zero, (Math.Max(val1: 0, val2: (frame - 10)) * Frame));

        var early = Phase(drivers: followed, frames: 14, kinematics: kinematics, pose: Turning);
        var exact = Phase(drivers: [Driver(signal: CreationDriverDocument.SignalTurnRate)], frames: 14, kinematics: new WorldGaitKinematics(), pose: Turning);

        Assert.Equal(expected: 1f, actual: exact, precision: 3);
        Assert.True(condition: (early < 0.5f), userMessage: $"early {early}");

        var settled = new WorldGaitKinematics();

        settled.Resolve(drivers: followed, rows: rows);
        Assert.Equal(expected: 1f, actual: Phase(drivers: followed, frames: 240, kinematics: settled, pose: Turning), precision: 2);

        var seam = new WorldGaitKinematics();

        seam.Resolve(drivers: followed, rows: rows);
        _ = seam.Follow(deltaSeconds: Frame, index: 0, period: (2f * MathF.PI), target: 3.1f);
        Assert.Equal(expected: 3.1f, actual: seam.Follow(deltaSeconds: 0f, index: 0, period: (2f * MathF.PI), target: -3.1f));

        var wrapped = seam.Follow(deltaSeconds: Frame, index: 0, period: (2f * MathF.PI), target: -3.1f);

        Assert.InRange(actual: wrapped, low: 3.09f, high: 3.2f);
        Assert.Equal(expected: (4f * MathF.PI), actual: WorldGaitKinematics.Period(cadence: -2f, signal: CreationDriverDocument.SignalTravelDirection), precision: 4);
        Assert.Equal(expected: 0f, actual: WorldGaitKinematics.Period(cadence: 2f, signal: CreationDriverDocument.SignalTurnRate));
        // An unresolved row, and a frame with no time, leave the eased motion where it was.
        seam.Resolve(drivers: [Driver(dynamics: "absent", signal: CreationDriverDocument.SignalTurnRate)], rows: rows);
        Assert.Equal(expected: 0.25f, actual: seam.Follow(deltaSeconds: Frame, index: 0, period: 0f, target: 0.25f));
        seam.Observe(blend: 1f, deltaSeconds: 0f, travel: Vector3.UnitX, yaw: 0f);
        Assert.Equal(expected: 0f, actual: seam.ForwardAcceleration);
    }
    /// <summary>A state-cell signal passes through the same follower: an absent cell reads zero.</summary>
    [Fact]
    public void AStateSignalWithoutACellReadsZeroThroughTheFollower() {
        var kinematics = new WorldGaitKinematics();

        Assert.Equal(expected: 0f, actual: Phase(drivers: [Driver(signal: "state.lean")], frames: 3, kinematics: kinematics, pose: frame => (Vector3.Zero, 0f)));
    }
    /// <summary>The canonicalizer refuses a follower on an integrating signal, an empty follower name and a replant
    /// outside its ranges; the world validator refuses a follower naming no dynamics row, with the declared row as the
    /// control.</summary>
    [Fact]
    public void TheValidatorsRefuseMalformedLocomotionByName() {
        string Refusal(CreationDocument document) => string.Join(
            separator: " | ",
            values: CreationCanonicalizer.Validate(document: document).Select(selector: error => $"{error.Path}: {error.Message}")
        );

        var integrating = (Legs() with { Drivers = [Driver(dynamics: "turn", signal: CreationDriverDocument.SignalPlanarTravel) with { Name = "stride" }] });
        var empty = (Legs() with { Drivers = [Driver(dynamics: " ", signal: CreationDriverDocument.SignalTurnRate) with { Name = "stride" }] });
        var replant = (Legs() with {
            Effectors = [Foot(align: 1.5f, name: "foot-left", side: "left", from: 0f, to: 3f) with {
                Plant = new CreationPlantDocument(
                    Driver: "stride",
                    Window: new Vector2(x: 0f, y: 3f),
                    Replant: new CreationReplantDocument(Distance: 0f, Seconds: 3f, Height: -1f)
                ),
            }],
        });

        Assert.Contains(expectedSubstring: "dynamics cannot follow the signal 'planarTravel'", actualString: Refusal(document: integrating));
        Assert.Contains(expectedSubstring: "dynamics is empty", actualString: Refusal(document: empty));
        Assert.Contains(expectedSubstring: "replant.distance", actualString: Refusal(document: replant));
        Assert.Contains(expectedSubstring: "replant.seconds", actualString: Refusal(document: replant));
        Assert.Contains(expectedSubstring: "replant.height", actualString: Refusal(document: replant));
        Assert.Contains(expectedSubstring: "effectors[0].align: align 1.5 is outside [0, 1].", actualString: Refusal(document: replant));
        Assert.Equal(expected: string.Empty, actual: Refusal(document: Legs(align: 1f)));
        Assert.Equal(expected: string.Empty, actual: Refusal(document: Legs()));

        var followed = (Legs() with {
            Drivers = [.. Legs().Drivers!, Driver(dynamics: "turn", signal: CreationDriverDocument.SignalTurnRate)],
        });
        var world = CreationFixtures.RigWorld(creation: followed);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: world, reason: out var reason));
        Assert.Contains(expectedSubstring: "dynamics 'turn' names no declared dynamics row", actualString: reason);
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: (world with { DynamicsRaw = [.. world.Dynamics, new DynamicsRow(Name: "turn", Frequency: 2f, Damping: 0.6f, Response: 0f)] }),
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    /// <summary>Turning in place, each resting foot holds its world point until the stance under it drifts past the
    /// replant distance, then steps there along a raised arc; the two feet never step at once, and a resumed gait
    /// takes a foot back mid-step without snapping it to the point it left.</summary>
    [Fact]
    public void RestingFeetStepOneAtATimeInsteadOfSliding() {
        var (client, pool, transforms) = Stage();
        var tick = 0UL;

        for (var frame = 0; (frame < 10); frame++) {
            Advance(client: client, pool: pool, position: Vector3.Zero, tick: tick++, transforms: transforms, yaw: 0f);
        }

        var rest = Rig(pool: pool);

        Assert.All(collection: rest.Effectors, action: effector => Assert.Equal(expected: "yes", actual: effector.Contact));

        var stepped = new bool[2];
        var lifted = false;
        Vector3?[] held = [null, null];

        for (var frame = 1; (frame <= 90); frame++) {
            Advance(client: client, pool: pool, position: Vector3.Zero, tick: tick++, transforms: transforms, yaw: (frame * 0.02f));

            var state = Rig(pool: pool);

            Assert.False(condition: (state.Effectors[0].Stepping && state.Effectors[1].Stepping), userMessage: $"frame {frame}: both feet stepping");

            for (var foot = 0; (foot < 2); foot++) {
                var effector = state.Effectors[foot];

                if (effector.Stepping) {
                    stepped[foot] = true;
                    lifted |= (effector.Target!.Value.Y > 0.04f);
                    held[foot] = null;
                } else if (held[foot] is { } latched) {
                    Assert.Equal(expected: latched, actual: effector.Target!.Value);
                } else {
                    held[foot] = effector.Target;
                }
            }
        }

        Assert.True(condition: (stepped[0] && stepped[1]), userMessage: "each foot steps");
        Assert.True(condition: lifted, userMessage: "a step rises off the floor");
        Assert.Equal(expected: "stepping", actual: new WorldRigEffector(Name: "f", Weight: 1f, Planted: true, Target: null, Bones: 2, Stepping: true).Contact);
        Assert.Equal(expected: "no", actual: new WorldRigEffector(Name: "f", Weight: 1f, Planted: false, Target: null, Bones: 2, Stepping: false).Contact);

        // Walking off mid-step: the gait's own latch takes over and no foot is left stepping.
        var yaw = 1.8f;

        for (var frame = 0; !Rig(pool: pool).Effectors.Any(predicate: effector => effector.Stepping); frame++) {
            Assert.True(condition: (frame < 120), userMessage: "a further turn starts a step");
            yaw += 0.03f;
            Advance(client: client, pool: pool, position: Vector3.Zero, tick: tick++, transforms: transforms, yaw: yaw);
        }

        var position = Vector3.Zero;

        for (var frame = 0; (frame < 12); frame++) {
            position += new Vector3(x: 0f, y: 0f, z: -0.06f);
            Advance(client: client, pool: pool, position: position, tick: tick++, transforms: transforms, yaw: yaw);
        }

        Assert.All(collection: Rig(pool: pool).Effectors, action: effector => Assert.False(condition: effector.Stepping));
    }
    /// <summary>A planted boot whose chain stretches to reach a target off its rest offset stays level with the floor
    /// when its foot aligns, on the same spot as the unaligned control, which tilts with its shin; once a walk releases
    /// the foot to its swing, the aligned boot carries its own swing exactly as the control does.</summary>
    [Fact]
    public void AnAlignedPlantedTipStaysLevelAndItsSwingIsUntouched() {
        const int Boot = (1 + 2);
        const float Toe = 0.3f;
        var (alignedClient, aligned, alignedTransforms) = Stage(align: 1f, toe: Toe);
        var (plainClient, plain, plainTransforms) = Stage(toe: Toe);
        var position = Vector3.Zero;
        var tick = 0UL;

        static Vector3 Up(DynamicTransform[] transforms) => Vector3.Transform(rotation: transforms[Boot].Orientation, value: Vector3.UnitY);
        void Step() {
            Advance(client: alignedClient, pool: aligned, position: position, tick: tick, transforms: alignedTransforms, yaw: 0f);
            Advance(client: plainClient, pool: plain, position: position, tick: tick, transforms: plainTransforms, yaw: 0f);
            tick++;
        }

        for (var frame = 0; (frame < 90); frame++) {
            Step();
        }

        var standing = Rig(pool: aligned).Effectors[0];

        Assert.Equal(expected: "yes", actual: standing.Contact);
        Assert.Equal(expected: 1f, actual: standing.Align);
        Assert.True(condition: (Vector3.Distance(value1: Vector3.UnitY, value2: standing.Normal) < 1e-4f), userMessage: $"normal {standing.Normal}");
        Assert.True(condition: (Vector3.Distance(value1: Vector3.UnitY, value2: Up(transforms: alignedTransforms)) < 1e-4f), userMessage: $"aligned up {Up(transforms: alignedTransforms)}");
        Assert.True(condition: (Vector3.Distance(value1: Vector3.UnitY, value2: Up(transforms: plainTransforms)) > 0.05f), userMessage: $"control up {Up(transforms: plainTransforms)}");
        Assert.True(
            condition: (Vector3.Distance(value1: alignedTransforms[Boot].Position, value2: plainTransforms[Boot].Position) < 1e-4f),
            userMessage: $"aligned {alignedTransforms[Boot].Position}, control {plainTransforms[Boot].Position}"
        );
        Assert.Null(@object: Rig(pool: plain).Effectors[0].Align);

        for (var frame = 0; ((Rig(pool: aligned).Effectors[0] is { Planted: true } or { Weight: >= 1e-3f })); frame++) {
            Assert.True(condition: (frame < 600), userMessage: "the walk releases the left foot to its swing");
            position += new Vector3(x: 0f, y: 0f, z: -(0.2f * Frame));
            Step();
        }

        Assert.True(condition: (Vector3.Distance(value1: Up(transforms: plainTransforms), value2: Up(transforms: alignedTransforms)) < 1e-3f), userMessage: $"aligned up {Up(transforms: alignedTransforms)}, control {Up(transforms: plainTransforms)}");
        Assert.True(condition: (Vector3.Distance(value1: Vector3.UnitY, value2: Up(transforms: plainTransforms)) > (Toe / 2f)), userMessage: $"the swing tilts the control {Up(transforms: plainTransforms)}");
    }
    /// <summary>A resting foot over no surface holds no latch until one resolves.</summary>
    [Fact]
    public void ARestingFootOverNoSurfaceHoldsNoLatch() {
        var (client, pool, transforms) = Stage();
        var far = new Vector3(x: 10f, y: 0f, z: 0f);

        for (var frame = 0; (frame < 10); frame++) {
            Advance(client: client, pool: pool, position: far, tick: ((ulong)frame), transforms: transforms, yaw: 0f);
        }

        Assert.All(collection: Rig(pool: pool).Effectors, action: effector => Assert.Equal(expected: "no", actual: effector.Contact));
    }
    /// <summary><c>view.reseed</c> restarts a walking body's gait as for a new occupant: its stride phase and eased speed
    /// read zero on the next pack, where a teleport of the same occupant would keep them; a body the pool roots nothing
    /// for restarts nothing.</summary>
    [Fact]
    public void AReseedRestartsTheGaitAsForANewOccupant() {
        var (client, pool, transforms) = Stage();
        var tick = 1UL;

        for (; (tick < 40UL); tick++) {
            Advance(client: client, pool: pool, position: new Vector3(x: 0f, y: 0f, z: -(0.05f * tick)), tick: tick, transforms: transforms, yaw: 0f);
        }

        Assert.True(condition: (Rig(pool: pool).Drivers[0].Phase > 0f));
        Assert.True(condition: (Rig(pool: pool).Speed > 0f));
        Assert.Equal(expected: 0, actual: pool.Reseed(bodyIndex: 3));
        Assert.Equal(expected: 1, actual: pool.Reseed(bodyIndex: 0));

        Advance(client: client, pool: pool, position: new Vector3(x: 0f, y: 0f, z: -(0.05f * tick)), tick: tick, transforms: transforms, yaw: 0f);

        Assert.Equal(expected: 0f, actual: Rig(pool: pool).Drivers[0].Phase);
        Assert.Equal(expected: 0f, actual: Rig(pool: pool).Speed);
    }
}
