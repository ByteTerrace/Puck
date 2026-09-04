using System.Numerics;
using System.Text.Json;
using System.Text;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the creation-look animation primitive: the author↔engine conversion of a swing's pivot/axis and a
/// slide's axis, the composition math, the driver phase/weight advance, the canonicalizer's refusals, and the claim
/// the whole facet family rests on — that it reaches the dynamic transform buffer and nothing else.</summary>
public sealed class CreationAnimationLawTests {
    private const float Tolerance = 1e-4f;

    private static readonly WorldEntityAddress FirstBody = new(Authority: "a", Index: 0, Generation: 1);
    private static readonly FixedQ4816 SurfaceTolerance = FixedQ4816.FromDouble(value: 0.01);

    private static CreationDriverDocument Stride(string signal = CreationDriverDocument.SignalPlanarTravel, float cadence = 8f, params string[] when) => new(
        Cadence: cadence,
        Name: "stride",
        Signal: signal,
        When: ((when is { Length: > 0 }) ? when : ["Grounded"])
    );
    private static ShapeDocument Limb(IReadOnlyList<ShapeSwingDocument>? swings = null, IReadOnlyList<ShapeSlideDocument>? slides = null, IReadOnlyList<ShapeDomainOp>? domain = null) => new(
        Id: 1,
        Name: "limb",
        Type: SdfSolidPrimitive.Capsule,
        Position: new Vector3(x: 0.5f, y: 0.9f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: new Vector3(x: 0.1f, y: 0.5f, z: 0.1f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Domain: domain,
        Swings: swings,
        Slides: slides
    );
    private static CreationDocument Rig(ShapeDocument shape, IReadOnlyList<CreationDriverDocument>? drivers) => new(
        Schema: CreationDocument.CurrentSchema,
        Name: "rig",
        Palette: null,
        Shapes: [shape],
        Frames: null,
        Drivers: drivers
    );
    private static string Refusal(CreationDocument document) => string.Join(
        separator: "; ",
        values: CreationCanonicalizer.Validate(document: document)
    );

    /// <summary>A swing authored about author +X at a pivot off both flipped axes converts to engine axis
    /// (−1, 0, 0) at pivot (−x, y, −z), and a slide's axis takes the same half turn — the direction-valued members
    /// cross <see cref="CreationFrame"/> exactly once, with the pivot treated as a position.</summary>
    [Fact]
    public void AuthoredSwingPivotAndAxisTakeTheAuthorFrameHalfTurn() {
        var pivot = new Vector3(x: 0.3f, y: 1.25f, z: 0.2f);
        var document = Rig(
            drivers: [Stride()],
            shape: Limb(
                slides: [new ShapeSlideDocument(Amplitude: 0.1f, Axis: new Vector3(x: 0f, y: 0f, z: 1f), Driver: "stride")],
                swings: [new ShapeSwingDocument(Amplitude: 0.6f, Axis: Vector3.UnitX, Driver: "stride", Pivot: pivot)]
            )
        );
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "rig");
        var engine = CreationFrame.ToEngine(document: canonical.Document).Shapes![0];

        Assert.Equal(expected: new Vector3(x: -1f, y: 0f, z: 0f), actual: engine.Swings![0].Axis.Value);
        Assert.Equal(expected: new Vector3(x: -pivot.X, y: pivot.Y, z: -pivot.Z), actual: engine.Swings[0].Pivot.Value);
        Assert.Equal(expected: new Vector3(x: 0f, y: 0f, z: -1f), actual: engine.Slides![0].Axis.Value);
        // The control: an identity conversion would leave these equal to the authored values, and both flipped
        // components are non-zero on purpose so it cannot pass by accident.
        Assert.NotEqual(expected: pivot, actual: engine.Swings[0].Pivot.Value);
        Assert.NotEqual(expected: Vector3.UnitX, actual: engine.Swings[0].Axis.Value);
        // Amplitude, phase, and waveform ride a proper rotation unchanged.
        Assert.Equal(expected: 0.6f, actual: engine.Swings[0].Amplitude.Value);
        Assert.Equal(expected: CreationWave.Sine, actual: engine.Swings[0].Wave);
        // ToAuthor is the same half turn, so the round trip is the identity.
        Assert.Equal(expected: pivot, actual: CreationFrame.ToAuthor(document: CreationFrame.ToEngine(document: canonical.Document)).Shapes![0].Swings![0].Pivot.Value);
    }
    /// <summary>A shape one unit below its pivot, swung +90° about +Z, lands one unit to the pivot's +X — the
    /// right-handed sign convention read literally, with the shape's own orientation carrying the same turn.</summary>
    [Fact]
    public void SwingTurnsTheShapeAboutItsPivotRightHanded() {
        var pivot = new Vector3(x: 0f, y: 1.25f, z: 0f);
        var swing = new ShapeSwingDocument(Amplitude: 1f, Axis: Vector3.UnitZ, Driver: "stride", Pivot: pivot);
        var position = (pivot + new Vector3(x: 0f, y: -1f, z: 0f));
        var rotation = Quaternion.Identity;

        swing.Compose(
            angle: (MathF.PI / 2f),
            position: ref position,
            rotation: ref rotation
        );

        Assert.True((Vector3.Distance(value1: position, value2: (pivot + Vector3.UnitX)) < Tolerance),
            userMessage: $"a +90° swing about +Z did not land at pivot + (1,0,0); landed at {position}");
        Assert.True((Vector3.Distance(
            value1: Vector3.Transform(rotation: rotation, value: Vector3.UnitX),
            value2: Vector3.UnitY
        ) < Tolerance), userMessage: "the composed orientation did not carry the same turn as the position");

        // The controls: the opposite axis lands on the opposite side, and a zero angle moves nothing — either would
        // be indistinguishable from the assertion above if Compose ignored its axis or its angle.
        var mirrored = (pivot + new Vector3(x: 0f, y: -1f, z: 0f));
        var mirroredRotation = Quaternion.Identity;

        (swing with { Axis = -Vector3.UnitZ }).Compose(
            angle: (MathF.PI / 2f),
            position: ref mirrored,
            rotation: ref mirroredRotation
        );

        Assert.True((Vector3.Distance(value1: mirrored, value2: (pivot - Vector3.UnitX)) < Tolerance),
            userMessage: $"a +90° swing about −Z did not land at pivot + (-1,0,0); landed at {mirrored}");

        var still = (pivot + new Vector3(x: 0f, y: -1f, z: 0f));
        var stillRotation = Quaternion.Identity;

        swing.Compose(
            angle: 0f,
            position: ref still,
            rotation: ref stillRotation
        );

        Assert.Equal(expected: (pivot + new Vector3(x: 0f, y: -1f, z: 0f)), actual: still);
    }
    /// <summary>A slide displaces along its own axis by amplitude × waveform × weight and leaves the orientation
    /// alone.</summary>
    [Fact]
    public void SlideDisplacesAlongItsAxisAndLeavesRotationAlone() {
        var slide = new ShapeSlideDocument(Amplitude: 0.25f, Axis: Vector3.UnitY, Driver: "bob");
        var position = new Vector3(x: 1f, y: 2f, z: 3f);

        slide.Compose(
            offset: (slide.Amplitude * CreationWave.Evaluate(argument: (MathF.PI / 2f), wave: slide.Wave)),
            position: ref position
        );

        Assert.True((Vector3.Distance(value1: position, value2: new Vector3(x: 1f, y: 2.25f, z: 3f)) < Tolerance),
            userMessage: $"the slide did not displace by amplitude × sin(π/2) along +Y; landed at {position}");
        // The control: half a period later the same slide displaces the other way, so a sign-blind implementation
        // cannot satisfy both.
        var trough = new Vector3(x: 1f, y: 2f, z: 3f);

        slide.Compose(
            offset: (slide.Amplitude * CreationWave.Evaluate(argument: ((3f * MathF.PI) / 2f), wave: slide.Wave)),
            position: ref trough
        );

        Assert.True((Vector3.Distance(value1: trough, value2: new Vector3(x: 1f, y: 1.75f, z: 3f)) < Tolerance),
            userMessage: $"the slide's trough did not mirror its crest; landed at {trough}");
    }
    /// <summary>The <c>linear</c> waveform is the identity on its argument and <c>sine</c> is not — the wheel/rotor
    /// door.</summary>
    [Fact]
    public void LinearWaveIsTheIdentityAndSineIsNot() {
        Assert.Equal(expected: 12.5f, actual: CreationWave.Evaluate(argument: 12.5f, wave: CreationWave.Linear));
        Assert.Equal(expected: MathF.Sin(x: 12.5f), actual: CreationWave.Evaluate(argument: 12.5f, wave: CreationWave.Sine));
        Assert.Equal(expected: MathF.Sin(x: 12.5f), actual: CreationWave.Evaluate(argument: 12.5f, wave: null));
        Assert.NotEqual(expected: 12.5f, actual: CreationWave.Evaluate(argument: 12.5f, wave: null));
    }
    /// <summary>A <c>planarTravel</c> driver gated on <c>Grounded</c> charges horizontal travel only, charges
    /// nothing while the gate is off, and its weight eases in and back out — the walker's stride, and the reason a
    /// limb returns to rest instead of freezing mid-stride.</summary>
    [Fact]
    public void PlanarTravelDriverChargesOnlyHorizontalTravelUnderItsGate() {
        var drivers = new[] { Stride() };
        var phases = new float[CreationDocument.MaxDrivers];
        var weights = new float[CreationDocument.MaxDrivers];
        var lastPosition = Vector3.Zero;
        var lastOrientation = Quaternion.Identity;
        var seeded = false;
        var easedSpeed = 0f;
        var address = FirstBody;

        void Step(Vector3 position, BodyFacts facts) => WorldGaitDrivers.Advance(
            address: FirstBody,
            deltaSeconds: (1f / 60f),
            drivers: drivers,
            facts: facts,
            easedSpeed: ref easedSpeed,
            lastAddress: ref address,
            lastOrientation: ref lastOrientation,
            lastPosition: ref lastPosition,
            orientation: Quaternion.Identity,
            phases: phases,
            position: position,
            seeded: ref seeded,
            weights: weights
        );

        Step(facts: BodyFacts.Grounded, position: Vector3.Zero); // the seeding frame charges nothing

        Assert.Equal(expected: 0f, actual: phases[0]);
        Assert.Equal(expected: 0f, actual: weights[0]);

        Step(facts: BodyFacts.Grounded, position: new Vector3(x: 0.1f, y: 0f, z: 0f));

        var afterPlanar = phases[0];

        Assert.True((MathF.Abs(x: (afterPlanar - (0.1f * 8f))) < Tolerance),
            userMessage: $"0.1 m of planar travel at cadence 8 did not advance the phase by 0.8; phase={afterPlanar}");
        Assert.True((weights[0] > 0f), userMessage: "the weight did not ease in while the gate held");

        // The control on the SIGNAL: a purely vertical step charges planarTravel nothing, so a driver reading total
        // travel by mistake would fail here.
        Step(facts: BodyFacts.Grounded, position: new Vector3(x: 0.1f, y: 0.5f, z: 0f));

        Assert.True((MathF.Abs(x: (phases[0] - afterPlanar)) < Tolerance),
            userMessage: $"a vertical-only step charged planarTravel; phase moved {phases[0] - afterPlanar}");

        // The control on the gate: once the gate stops holding the weight eases back to rest, and a driver at rest
        // charges nothing — a limb finishes its stride as it fades, then stops.
        var gatedWeight = weights[0];

        Step(facts: BodyFacts.Airborne, position: new Vector3(x: 0.1f, y: 0.5f, z: 0f));

        Assert.True((weights[0] < gatedWeight), userMessage: "the weight did not ease back out once the gate stopped holding");

        for (var frame = 0; (frame < 120); frame++) {
            Step(facts: BodyFacts.Airborne, position: new Vector3(x: 0.1f, y: 0.5f, z: 0f));
        }

        Assert.Equal(expected: 0f, actual: weights[0]);

        var atRest = phases[0];

        Step(facts: BodyFacts.Airborne, position: new Vector3(x: 0.3f, y: 0.5f, z: 0f));

        Assert.Equal(expected: atRest, actual: phases[0]);
        // ...and the same step with the gate restored does charge it, so the stillness above is the gate rather
        // than a phase that has stopped moving for any reason at all.
        Step(facts: BodyFacts.Grounded, position: new Vector3(x: 0.4f, y: 0.5f, z: 0f));

        Assert.True((phases[0] > atRest), userMessage: "restoring the gate did not resume the phase");
    }
    /// <summary>A driver gated on <c>moving</c> eases to weight 0 once the body stops, so its limbs return to rest
    /// with no simulation fact involved; the identical driver gated only on <c>Grounded</c> holds full weight
    /// through the same stop — that difference is the whole of the client-derived predicate.</summary>
    [Fact]
    public void AMovingGateReleasesWhenTheBodyStopsWhileAFactOnlyGateDoesNot() {
        var moving = new[] { Stride(when: ["Grounded", CreationDriverDocument.TokenMoving]) };
        var factOnly = new[] { Stride(when: ["Grounded"]) };
        var movingPhases = new float[CreationDocument.MaxDrivers];
        var movingWeights = new float[CreationDocument.MaxDrivers];
        var movingPosition = Vector3.Zero;
        var movingOrientation = Quaternion.Identity;
        var movingSeeded = false;
        var movingSpeed = 0f;
        var movingAddress = FirstBody;
        var factPhases = new float[CreationDocument.MaxDrivers];
        var factWeights = new float[CreationDocument.MaxDrivers];
        var factPosition = Vector3.Zero;
        var factOrientation = Quaternion.Identity;
        var factSeeded = false;
        var factSpeed = 0f;
        var factAddress = FirstBody;

        // Both gates see the identical pose stream: 40 frames walking at 4 m/s, then 40 standing still.
        void Step(Vector3 position) {
            WorldGaitDrivers.Advance(
                address: FirstBody,
                deltaSeconds: (1f / 60f),
                drivers: moving,
                facts: BodyFacts.Grounded,
                easedSpeed: ref movingSpeed,
                lastAddress: ref movingAddress,
                lastOrientation: ref movingOrientation,
                lastPosition: ref movingPosition,
                orientation: Quaternion.Identity,
                phases: movingPhases,
                position: position,
                seeded: ref movingSeeded,
                weights: movingWeights
            );
            WorldGaitDrivers.Advance(
                address: FirstBody,
                deltaSeconds: (1f / 60f),
                drivers: factOnly,
                facts: BodyFacts.Grounded,
                easedSpeed: ref factSpeed,
                lastAddress: ref factAddress,
                lastOrientation: ref factOrientation,
                lastPosition: ref factPosition,
                orientation: Quaternion.Identity,
                phases: factPhases,
                position: position,
                seeded: ref factSeeded,
                weights: factWeights
            );
        }

        var travelled = Vector3.Zero;

        for (var frame = 0; (frame < 40); frame++) {
            travelled += new Vector3(x: (4f / 60f), y: 0f, z: 0f);
            Step(position: travelled);
        }

        Assert.True((movingWeights[0] > 0.9f), userMessage: $"the moving-gated driver did not reach full weight while walking; weight={movingWeights[0]}");
        Assert.True((factWeights[0] > 0.9f), userMessage: $"the fact-gated control did not reach full weight while walking; weight={factWeights[0]}");

        var walkingPhase = movingPhases[0];

        for (var frame = 0; (frame < 120); frame++) {
            Step(position: travelled);
        }

        Assert.Equal(expected: 0f, actual: movingWeights[0]);
        // The control: the same stop leaves a driver gated only on the sim fact at full weight, so the release
        // above is the moving predicate and not the body simply having stopped travelling.
        Assert.True((factWeights[0] > 0.9f), userMessage: $"the fact-gated control released on a stop; weight={factWeights[0]}");
        // A released driver stops charging its phase too, so the pose it returns from does not drift.
        Assert.Equal(expected: walkingPhase, actual: movingPhases[0]);

        // ...and walking again re-arms it, so the release is a gate and not a latch.
        for (var frame = 0; (frame < 40); frame++) {
            travelled += new Vector3(x: (4f / 60f), y: 0f, z: 0f);
            Step(position: travelled);
        }

        Assert.True((movingWeights[0] > 0.9f), userMessage: $"the moving-gated driver did not re-arm when the body walked again; weight={movingWeights[0]}");
    }
    /// <summary>A gate whose tokens contradict — <c>moving</c> with <c>still</c> — is refused, and a gate mixing
    /// <c>always</c> with a real condition is too; the single-token spelling still parses as a one-token gate.</summary>
    [Fact]
    public void GateShapeRefusalsAndTheSingleTokenSpelling() {
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride(when: [CreationDriverDocument.TokenMoving, CreationDriverDocument.TokenStill])],
                shape: Limb()
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "are negations, so the gate can never hold."
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride(when: [CreationDriverDocument.WhenAlways, CreationDriverDocument.TokenMoving])],
                shape: Limb()
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "cannot join a conjunction"
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride(when: ["Grounded", "Grounded"])],
                shape: Limb()
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "duplicate gate token 'Grounded'."
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride(when: ["a", "b", "c", "d", "e"])],
                shape: Limb()
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"exceeds the {CreationDriverDocument.MaxGateTokens}-token gate."
        );
        // The control: the two-token gate the walker actually authors validates clean.
        Assert.Empty(collection: CreationCanonicalizer.Validate(document: Rig(
            drivers: [Stride(when: ["Grounded", CreationDriverDocument.TokenMoving])],
            shape: Limb()
        )));

        // The single-token spelling: a bare string parses as a one-token gate and canonicalizes to the array form,
        // which is what the wire carries.
        var parsed = JsonSerializer.Deserialize<CreationDriverDocument>(
            json: """{ "name": "stride", "signal": "planarTravel", "cadence": 8, "when": "Grounded" }""",
            options: DocumentJsonOptions.Shared
        );

        Assert.Equal(expected: ["Grounded"], actual: parsed!.When);

        var canonical = CreationCanonicalizer.Canonicalize(
            document: Rig(
                drivers: [parsed],
                shape: Limb()
            ),
            source: "rig"
        );

        Assert.Contains(actualString: Encoding.UTF8.GetString(bytes: canonical.Bytes), comparisonType: StringComparison.Ordinal, expectedSubstring: "\"when\": [");
        // The control: an authored gate that is neither a string nor an array of strings is refused at parse.
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<CreationDriverDocument>(
            json: """{ "name": "stride", "signal": "planarTravel", "cadence": 8, "when": 7 }""",
            options: DocumentJsonOptions.Shared
        ));
    }
    /// <summary>An <c>always</c> gate holds under every fact set, including one that names no fact at all.</summary>
    [Fact]
    public void AlwaysGateHoldsRegardlessOfFacts() {
        var drivers = new[] { Stride(cadence: 1f, signal: CreationDriverDocument.SignalTime, when: [CreationDriverDocument.WhenAlways]) };
        var phases = new float[CreationDocument.MaxDrivers];
        var weights = new float[CreationDocument.MaxDrivers];
        var lastPosition = Vector3.Zero;
        var lastOrientation = Quaternion.Identity;
        var seeded = false;
        var easedSpeed = 0f;
        var address = FirstBody;

        for (var frame = 0; (frame < 20); frame++) {
            WorldGaitDrivers.Advance(
                address: FirstBody,
                deltaSeconds: 0.05f,
                drivers: drivers,
                facts: BodyFacts.None,
                easedSpeed: ref easedSpeed,
                lastAddress: ref address,
                lastOrientation: ref lastOrientation,
                lastPosition: ref lastPosition,
                orientation: Quaternion.Identity,
                phases: phases,
                position: Vector3.Zero,
                seeded: ref seeded,
                weights: weights
            );
        }

        Assert.True((weights[0] > 0.9f), userMessage: $"an ungated driver did not ease to full weight; weight={weights[0]}");
        Assert.True((phases[0] > 0f), userMessage: "a time-driven ungated driver did not advance while the body stood still");
        // The control: the identical driver gated on a fact the body does not carry stays at rest.
        var gatedPhases = new float[CreationDocument.MaxDrivers];
        var gatedWeights = new float[CreationDocument.MaxDrivers];
        var gatedPosition = Vector3.Zero;
        var gatedOrientation = Quaternion.Identity;
        var gatedSeeded = false;
        var gatedSpeed = 0f;
        var gatedAddress = FirstBody;
        var gated = new[] { Stride(cadence: 1f, signal: CreationDriverDocument.SignalTime, when: [nameof(BodyFacts.Submerged)]) };

        for (var frame = 0; (frame < 20); frame++) {
            WorldGaitDrivers.Advance(
                address: FirstBody,
                deltaSeconds: 0.05f,
                drivers: gated,
                facts: BodyFacts.None,
                easedSpeed: ref gatedSpeed,
                lastAddress: ref gatedAddress,
                lastOrientation: ref gatedOrientation,
                lastPosition: ref gatedPosition,
                orientation: Quaternion.Identity,
                phases: gatedPhases,
                position: Vector3.Zero,
                seeded: ref gatedSeeded,
                weights: gatedWeights
            );
        }

        Assert.Equal(expected: 0f, actual: gatedPhases[0]);
        Assert.Equal(expected: 0f, actual: gatedWeights[0]);
    }
    /// <summary>One frame charges at most <see cref="WorldGaitDrivers.MaxTravelPerFrame"/> of travel, so a teleport
    /// cannot spin a limb through dozens of cycles.</summary>
    [Fact]
    public void OneFrameChargesAtMostTheTravelCap() {
        var drivers = new[] { Stride(cadence: 1f, signal: CreationDriverDocument.SignalTravel, when: [CreationDriverDocument.WhenAlways]) };
        var phases = new float[CreationDocument.MaxDrivers];
        var weights = new float[CreationDocument.MaxDrivers];
        var lastPosition = Vector3.Zero;
        var lastOrientation = Quaternion.Identity;
        var seeded = false;
        var easedSpeed = 0f;
        var address = FirstBody;

        void Step(Vector3 position) => WorldGaitDrivers.Advance(
            address: FirstBody,
            deltaSeconds: (1f / 60f),
            drivers: drivers,
            facts: BodyFacts.Grounded,
            easedSpeed: ref easedSpeed,
            lastAddress: ref address,
            lastOrientation: ref lastOrientation,
            lastPosition: ref lastPosition,
            orientation: Quaternion.Identity,
            phases: phases,
            position: position,
            seeded: ref seeded,
            weights: weights
        );

        Step(position: Vector3.Zero);
        Step(position: new Vector3(x: 0.01f, y: 0f, z: 0f)); // ease the weight above zero so the phase may advance

        var before = phases[0];

        Step(position: new Vector3(x: 500f, y: 0f, z: 0f));

        Assert.True(((phases[0] - before) <= (WorldGaitDrivers.MaxTravelPerFrame + Tolerance)),
            userMessage: $"a 500 m jump charged {phases[0] - before} rad at cadence 1, past the {WorldGaitDrivers.MaxTravelPerFrame} cap");
        // The control: a sub-cap step charges its full distance, so the clamp is not simply pinning every frame.
        var capped = phases[0];

        Step(position: new Vector3(x: 500.05f, y: 0f, z: 0f));

        Assert.True((MathF.Abs(x: ((phases[0] - capped) - 0.05f)) < Tolerance),
            userMessage: $"a 0.05 m step did not charge 0.05 rad at cadence 1; charged {phases[0] - capped}");
    }
    /// <summary>An instantaneous signal SETS its phase rather than accumulating it: two identical frames leave the
    /// same phase, and the sign of <c>turnRate</c> follows the sign of the yaw change.</summary>
    [Fact]
    public void InstantaneousSignalsTrackRatherThanAccumulate() {
        var drivers = new[] {
            Stride(cadence: 1f, signal: CreationDriverDocument.SignalVerticalSpeed, when: [CreationDriverDocument.WhenAlways]),
            new CreationDriverDocument(Cadence: 1f, Name: "turn", Signal: CreationDriverDocument.SignalTurnRate, When: [CreationDriverDocument.WhenAlways]),
        };
        var phases = new float[CreationDocument.MaxDrivers];
        var weights = new float[CreationDocument.MaxDrivers];
        var lastPosition = Vector3.Zero;
        var lastOrientation = Quaternion.Identity;
        var seeded = false;
        var easedSpeed = 0f;
        var address = FirstBody;
        var height = 0f;
        var yaw = 0f;

        void Step(float rise, float turn) {
            height += rise;
            yaw += turn;

            WorldGaitDrivers.Advance(
                address: FirstBody,
                deltaSeconds: 0.5f,
                drivers: drivers,
                facts: BodyFacts.Grounded,
                easedSpeed: ref easedSpeed,
                lastAddress: ref address,
                lastOrientation: ref lastOrientation,
                lastPosition: ref lastPosition,
                orientation: Quaternion.CreateFromAxisAngle(angle: yaw, axis: Vector3.UnitY),
                phases: phases,
                position: new Vector3(x: 0f, y: height, z: 0f),
                seeded: ref seeded,
                weights: weights
            );
        }

        Step(rise: 0f, turn: 0f);
        Step(rise: 1f, turn: 0.25f);

        Assert.True((MathF.Abs(x: (phases[0] - 2f)) < Tolerance), userMessage: $"1 m of rise over 0.5 s did not read as 2 m/s; phase={phases[0]}");
        Assert.True((MathF.Abs(x: (phases[1] - 0.5f)) < Tolerance), userMessage: $"0.25 rad of yaw over 0.5 s did not read as 0.5 rad/s; phase={phases[1]}");

        Step(rise: 1f, turn: 0.25f);

        // The control against accumulation: an identical second frame leaves the phases where they were.
        Assert.True((MathF.Abs(x: (phases[0] - 2f)) < Tolerance), userMessage: $"verticalSpeed accumulated instead of tracking; phase={phases[0]}");
        Assert.True((MathF.Abs(x: (phases[1] - 0.5f)) < Tolerance), userMessage: $"turnRate accumulated instead of tracking; phase={phases[1]}");

        // The control on sign: reversing both signals reverses both phases.
        Step(rise: -1f, turn: -0.25f);

        Assert.True((MathF.Abs(x: (phases[0] + 2f)) < Tolerance), userMessage: $"a descent did not read as a negative vertical speed; phase={phases[0]}");
        Assert.True((MathF.Abs(x: (phases[1] + 0.5f)) < Tolerance), userMessage: $"a reversed turn did not read as a negative turn rate; phase={phases[1]}");
    }
    /// <summary>A body slot reused by a different inhabitant reseeds phase and weight, so the new occupant never
    /// inherits the previous one's stride.</summary>
    [Fact]
    public void AnAddressChangeReseedsPhaseAndWeight() {
        var drivers = new[] { Stride(when: [CreationDriverDocument.WhenAlways]) };
        var phases = new float[CreationDocument.MaxDrivers];
        var weights = new float[CreationDocument.MaxDrivers];
        var lastPosition = Vector3.Zero;
        var lastOrientation = Quaternion.Identity;
        var seeded = false;
        var easedSpeed = 0f;
        var address = FirstBody;

        void Step(WorldEntityAddress at, Vector3 position) => WorldGaitDrivers.Advance(
            address: at,
            deltaSeconds: (1f / 60f),
            drivers: drivers,
            facts: BodyFacts.Grounded,
            easedSpeed: ref easedSpeed,
            lastAddress: ref address,
            lastOrientation: ref lastOrientation,
            lastPosition: ref lastPosition,
            orientation: Quaternion.Identity,
            phases: phases,
            position: position,
            seeded: ref seeded,
            weights: weights
        );

        Step(at: FirstBody, position: Vector3.Zero);

        for (var frame = 1; (frame < 12); frame++) {
            Step(at: FirstBody, position: new Vector3(x: (0.05f * frame), y: 0f, z: 0f));
        }

        Assert.True((phases[0] > 0f), userMessage: "the control never built a phase to reseed");
        Assert.True((weights[0] > 0f), userMessage: "the control never built a weight to reseed");

        Step(at: (FirstBody with { Generation = 2 }), position: new Vector3(x: 1f, y: 0f, z: 0f));

        Assert.Equal(expected: 0f, actual: phases[0]);
        Assert.Equal(expected: 0f, actual: weights[0]);
    }
    /// <summary>A facet naming a driver the creation does not declare composes nothing — the runtime half of the
    /// canonicalizer's refusal, so a hand-built document cannot animate off a phantom driver.</summary>
    [Fact]
    public void AFacetNamingNoDriverComposesNothing() {
        var shape = Limb(swings: [new ShapeSwingDocument(Amplitude: 1f, Axis: Vector3.UnitZ, Driver: "absent", Pivot: Vector3.Zero)]);
        var phases = new float[CreationDocument.MaxDrivers];
        var weights = new float[CreationDocument.MaxDrivers];

        phases[0] = (MathF.PI / 2f);
        weights[0] = 1f;

        var position = shape.Position.Value;
        var rotation = shape.Rotation.Value;

        WorldGaitDrivers.Compose(
            drivers: [Stride()],
            phases: phases,
            position: ref position,
            rotation: ref rotation,
            shape: shape,
            weights: weights
        );

        Assert.Equal(expected: shape.Position.Value, actual: position);
        // The control: the same shape naming the declared driver does move under the same phase and weight.
        var named = (shape with { Swings = [new ShapeSwingDocument(Amplitude: 1f, Axis: Vector3.UnitZ, Driver: "stride", Pivot: Vector3.Zero)] });
        var namedPosition = named.Position.Value;
        var namedRotation = named.Rotation.Value;

        WorldGaitDrivers.Compose(
            drivers: [Stride()],
            phases: phases,
            position: ref namedPosition,
            rotation: ref namedRotation,
            shape: named,
            weights: weights
        );

        Assert.NotEqual(expected: named.Position.Value, actual: namedPosition);
    }
    /// <summary>The facets are presentation-only: a creation carrying drivers, swings, and slides emits the SAME
    /// SDF program words and the SAME compiled solid field as one with the facets stripped, even though the two
    /// documents hash differently — so nothing an artist animates can move the field, the colliders, or anything
    /// derived from them.</summary>
    [Fact]
    public void AnimatedFacetsChangeNeitherTheRenderProgramNorTheSolidField() {
        var animated = Rig(
            drivers: [Stride()],
            shape: Limb(
                slides: [new ShapeSlideDocument(Amplitude: 0.2f, Axis: Vector3.UnitY, Driver: "stride")],
                swings: [new ShapeSwingDocument(Amplitude: 0.6f, Axis: Vector3.UnitX, Driver: "stride", Pivot: new Vector3(x: 0.5f, y: 1.25f, z: 0f))]
            )
        );
        var inert = Rig(
            drivers: null,
            shape: Limb()
        );
        var animatedCanonical = CreationCanonicalizer.Canonicalize(document: animated, source: "rig");
        var inertCanonical = CreationCanonicalizer.Canonicalize(document: inert, source: "rig");

        // The control: the two documents really are different content, so an assertion that they agree downstream
        // is not comparing a document with itself.
        Assert.NotEqual(expected: inertCanonical.Hash, actual: animatedCanonical.Hash);

        var animatedCreation = new WorldPrototype(Id: "rig", Document: animatedCanonical.Document, HashRaw: animatedCanonical.Hash);
        var inertCreation = new WorldPrototype(Id: "rig", Document: inertCanonical.Document, HashRaw: inertCanonical.Hash);

        Assert.Equal(expected: EmitBodyStampWords(creation: inertCreation), actual: EmitBodyStampWords(creation: animatedCreation));

        // The limb's engine-frame centre is the author frame's half turn of (0.5, 0.9, 0); its capsule radius is 0.1,
        // so its −X surface sits here.
        var probe = new FixedVector3(X: FixedQ4816.FromDouble(value: -0.6), Y: FixedQ4816.FromDouble(value: 0.9), Z: FixedQ4816.Zero);

        Assert.True(condition: BuildField(creation: animatedCreation).Probe(distance: out var animatedDistance, gradient: out _, material: out _, position: in probe));
        Assert.True(condition: BuildField(creation: inertCreation).Probe(distance: out var inertDistance, gradient: out _, material: out _, position: in probe));
        Assert.Equal(expected: inertDistance, actual: animatedDistance);
        Assert.True((FixedQ4816.Abs(value: animatedDistance) < SurfaceTolerance),
            userMessage: $"the probe missed the limb's surface, so the comparison proves nothing; distance={((double)animatedDistance):0.####}");
    }
    /// <summary>A creation carrying no animation facets keeps its canonical bytes free of every one of the three
    /// members, so an unanimated creation's hash is undisturbed by the family's existence.</summary>
    [Fact]
    public void AnUnanimatedCreationCarriesNoAnimationMembers() {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: Rig(
                drivers: null,
                shape: Limb()
            ),
            source: "rig"
        );
        var json = Encoding.UTF8.GetString(bytes: canonical.Bytes);

        Assert.DoesNotContain(actualString: json, comparisonType: StringComparison.Ordinal, expectedSubstring: "\"drivers\"");
        Assert.DoesNotContain(actualString: json, comparisonType: StringComparison.Ordinal, expectedSubstring: "\"swings\"");
        Assert.DoesNotContain(actualString: json, comparisonType: StringComparison.Ordinal, expectedSubstring: "\"slides\"");
        Assert.Null(@object: canonical.Document.Drivers);
        Assert.Null(@object: canonical.Document.Shapes![0].Swings);
        Assert.Null(@object: canonical.Document.Shapes[0].Slides);
        // The control: an animated creation does write them, so the absence above is the facet being absent rather
        // than the members never being written at all.
        var animated = CreationCanonicalizer.Canonicalize(
            document: Rig(
                drivers: [Stride()],
                shape: Limb(swings: [new ShapeSwingDocument(Amplitude: 0.6f, Axis: Vector3.UnitX, Driver: "stride", Pivot: Vector3.Zero)])
            ),
            source: "rig"
        );

        Assert.Contains(actualString: Encoding.UTF8.GetString(bytes: animated.Bytes), comparisonType: StringComparison.Ordinal, expectedSubstring: "\"drivers\"");
        Assert.Contains(actualString: Encoding.UTF8.GetString(bytes: animated.Bytes), comparisonType: StringComparison.Ordinal, expectedSubstring: "\"swings\"");
    }
    /// <summary>Every refusal the facet family owes, each against the control that differs only in the refused
    /// member.</summary>
    [Fact]
    public void TheCanonicalizerRefusesEachMalformedFacetByName() {
        var control = Rig(
            drivers: [Stride()],
            shape: Limb(swings: [new ShapeSwingDocument(Amplitude: 0.6f, Axis: Vector3.UnitX, Driver: "stride", Pivot: Vector3.Zero)])
        );

        Assert.Empty(collection: CreationCanonicalizer.Validate(document: control));

        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride()],
                shape: Limb(swings: [new ShapeSwingDocument(Amplitude: 0.6f, Axis: Vector3.UnitX, Driver: "phantom", Pivot: Vector3.Zero)])
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "names no declared driver 'phantom'."
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride(signal: "vibes")],
                shape: Limb(swings: [new ShapeSwingDocument(Amplitude: 0.6f, Axis: Vector3.UnitX, Driver: "stride", Pivot: Vector3.Zero)])
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "signal 'vibes' is not recognized"
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride()],
                shape: Limb(swings: [new ShapeSwingDocument(Amplitude: 0.6f, Axis: Vector3.UnitX, Driver: "stride", Pivot: Vector3.Zero, Wave: "square")])
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "wave 'square' is not recognized"
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride()],
                shape: Limb(swings: [new ShapeSwingDocument(Amplitude: 0.6f, Axis: Vector3.UnitX, Driver: "stride", Pivot: Vector3.Zero, Wave: CreationWave.CurvePrefix)])
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "wave 'curve:' is not recognized"
        );
        // A named curve passes the portable document; the world validator is what checks the row exists.
        Assert.Empty(collection: CreationCanonicalizer.Validate(document: Rig(
            drivers: [Stride()],
            shape: Limb(swings: [new ShapeSwingDocument(Amplitude: 0.6f, Axis: Vector3.UnitX, Driver: "stride", Pivot: Vector3.Zero, Wave: $"{CreationWave.CurvePrefix}sway")])
        )));
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride()],
                shape: Limb(swings: [new ShapeSwingDocument(Amplitude: 0.6f, Axis: Vector3.Zero, Driver: "stride", Pivot: Vector3.Zero)])
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "axis is zero, which names no direction."
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride()],
                shape: Limb(swings: [new ShapeSwingDocument(Amplitude: 40f, Axis: Vector3.UnitX, Driver: "stride", Pivot: Vector3.Zero)])
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"amplitude 40 is outside ±{ShapeSwingDocument.MaxAmplitude} radians."
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride()],
                shape: Limb(slides: [new ShapeSlideDocument(Amplitude: 40f, Axis: Vector3.UnitY, Driver: "stride")])
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"amplitude 40 is outside ±{ShapeSlideDocument.MaxAmplitude} creation units."
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [.. Enumerable.Range(start: 0, count: (CreationDocument.MaxDrivers + 1)).Select(selector: index => Stride() with { Name = $"d{index}" })],
                shape: Limb()
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"exceeds the {CreationDocument.MaxDrivers}-driver list."
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride(), Stride()],
                shape: Limb()
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "duplicate driver name 'stride'."
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride()],
                shape: Limb(swings: [.. Enumerable.Repeat(
                    count: (ShapeDocument.MaxSwings + 1),
                    element: new ShapeSwingDocument(Amplitude: 0.6f, Axis: Vector3.UnitX, Driver: "stride", Pivot: Vector3.Zero)
                )])
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"exceeds the {ShapeDocument.MaxSwings}-swing list."
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride()],
                shape: Limb(slides: [.. Enumerable.Repeat(
                    count: (ShapeDocument.MaxSlides + 1),
                    element: new ShapeSlideDocument(Amplitude: 0.2f, Axis: Vector3.UnitY, Driver: "stride")
                )])
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"exceeds the {ShapeDocument.MaxSlides}-slide list."
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                drivers: [Stride()],
                shape: Limb(
                    domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)],
                    swings: [new ShapeSwingDocument(Amplitude: 0.6f, Axis: Vector3.UnitX, Driver: "stride", Pivot: Vector3.Zero)]
                )
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "rides the placement root's transform"
        );
    }
    /// <summary>Every <see cref="ActionFact"/> the simulation publishes has a <see cref="BodyFacts"/> bit of the
    /// same name, and every bit is a single power of two — the gate a driver's <c>when</c> token resolves to cannot
    /// name a fact the fact set has no room for.</summary>
    [Fact]
    public void EveryActionFactHasASingleBodyFactsBit() {
        foreach (var fact in BodyFactVocabulary.Publishable) {
            Assert.True(condition: BodyFactVocabulary.TryResolve(
                gate: out var gate,
                name: fact.ToString()
            ), userMessage: $"ActionFact.{fact} has no BodyFacts bit of the same name.");
            Assert.True(condition: uint.IsPow2(value: ((uint)gate)), userMessage: $"BodyFacts.{fact} is not a single bit.");
        }

        Assert.True(condition: BodyFactVocabulary.TryResolve(gate: out var holdingUnwalkable, name: nameof(BodyFacts.HoldingUnwalkable)));
        Assert.Equal(expected: BodyFacts.HoldingUnwalkable, actual: holdingUnwalkable);
        // The controls: the ungated token resolves to no bit, and an unrecognized token resolves to nothing at all.
        Assert.True(condition: BodyFactVocabulary.TryResolve(gate: out var always, name: BodyFactVocabulary.Always));
        Assert.Equal(expected: BodyFacts.None, actual: always);
        Assert.False(condition: BodyFactVocabulary.TryResolve(gate: out _, name: "Swimming"));
        Assert.False(condition: BodyFactVocabulary.TryResolve(gate: out _, name: "Grounded, Airborne"));
    }
    private static WorldSolidField BuildField(WorldPrototype creation) {
        var definition = Fixtures.BuildGradientUpDocument(gradientUp: false) with {
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(Id: "rig", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
        };

        Assert.True(condition: WorldSolidField.TryBuild(definition: definition, built: out var field, reason: out var reason), userMessage: reason);

        return field!;
    }
    // The dynamic emission path a body-stamped creation actually renders through — the one place an animation facet
    // could leak into geometry if PackTransforms were not the only reader.
    private static uint[] EmitBodyStampWords(WorldPrototype creation) {
        var definition = Fixtures.BuildGradientUpDocument(gradientUp: false) with {
            CreationsRaw = [creation],
            LookRowsRaw = [new WorldLook(Name: "rig", Source: new WorldLookSource.Creation(PrototypeId: creation.Id), Scale: 1f, Motion: WorldLookMotion.Default)],
        };
        var pool = new WorldStampPool();

        pool.Reconcile(
            placements: [],
            creations: [creation],
            dynamics: [],
            bodyStamps: [new WorldStampPool.BodyStamp(BodyIndex: 0, Creation: creation, Scale: 1f, Motion: WorldLookMotion.Default)]
        );

        var builder = new SdfProgramBuilder();

        pool.Emit(
            builder: builder,
            definition: definition,
            probeWorstCase: false,
            maxPlacementScale: 1f,
            slotBase: 0
        );

        return builder.Build(buildInstanceGrid: false).Words.ToArray();
    }
}
