using System.Numerics;
using System.Text;

using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the effector primitive: the two-bone closed form, the cyclic-coordinate-descent sweep, the surface
/// probe, the contact latch, the author-frame conversion, the canonicalizer's refusals, and the claim the whole family
/// rests on — that it reaches the dynamic transform buffer and nothing else.</summary>
public sealed class CreationEffectorLawTests {
    private const float Tolerance = 1e-4f;

    // The bone lengths every geometry law measures against: a 0.5 upper and a 0.4 lower, so the reachable annulus is
    // [0.1, 0.9] from the root and both the fold and the extension bounds are far from each other.
    private static readonly Vector3 Hip = new(x: 0f, y: 1f, z: 0f);
    private static readonly Vector3 Knee = new(x: 0f, y: 0.5f, z: 0f);
    private static readonly Vector3 Ankle = new(x: 0f, y: 0.1f, z: 0.1f);

    private static void AssertNear(Vector3 expected, Vector3 actual, string what) => Assert.True(
        condition: (Vector3.Distance(
            value1: expected,
            value2: actual
        ) < Tolerance),
        userMessage: $"{what}: expected {expected}, got {actual}"
    );
    private static string Refusal(CreationDocument document) => string.Join(
        separator: " | ",
        values: CreationCanonicalizer.Validate(document: document).Select(selector: error => $"{error.Path}: {error.Message}")
    );

    /// <summary>The two-bone closed form puts the tip exactly on a reachable target and keeps both bone lengths.
    /// The control is a target past full extension, which lands the tip at the root plus the summed bone lengths
    /// along the root-to-target direction and no further.</summary>
    [Fact]
    public void TwoBoneSolveReachesAReachableTargetAndClampsBeyondIt() {
        var target = new Vector3(x: 0.3f, y: 0.4f, z: 0.2f);

        WorldEffectorSolver.SolveTwoBone(
            mid: Knee,
            root: Hip,
            solvedMid: out var solvedMid,
            solvedTip: out var solvedTip,
            target: target,
            tip: Ankle
        );
        AssertNear(
            actual: solvedTip,
            expected: target,
            what: "a reachable target was not reached"
        );
        // The bones are rigid: the solve may only rotate them.
        Assert.True(
            condition: (MathF.Abs(x: (Vector3.Distance(value1: Hip, value2: solvedMid) - Vector3.Distance(value1: Hip, value2: Knee))) < Tolerance),
            userMessage: "the upper bone changed length"
        );
        Assert.True(
            condition: (MathF.Abs(x: (Vector3.Distance(value1: solvedMid, value2: solvedTip) - Vector3.Distance(value1: Knee, value2: Ankle))) < Tolerance),
            userMessage: "the lower bone changed length"
        );

        // The control: a target four metres out cannot be reached, so the limb extends straight at it instead of
        // stretching — the assertion above is the solve reaching, not the tip being copied from the target.
        var far = new Vector3(x: 4f, y: 1f, z: 0f);
        var span = (Vector3.Distance(value1: Hip, value2: Knee) + Vector3.Distance(value1: Knee, value2: Ankle));

        WorldEffectorSolver.SolveTwoBone(
            mid: Knee,
            root: Hip,
            solvedMid: out var farMid,
            solvedTip: out var farTip,
            target: far,
            tip: Ankle
        );
        AssertNear(
            actual: farTip,
            expected: (Hip + (Vector3.Normalize(value: (far - Hip)) * span)),
            what: "an out-of-reach target did not clamp to full extension"
        );
        Assert.True(
            condition: (Vector3.Distance(value1: farMid, value2: farTip) > 0f),
            userMessage: "the clamped solve collapsed the lower bone"
        );
    }
    /// <summary>The bend stays on the side of the root-to-target line the driver-posed limb was already bent to. The
    /// control is the mirrored rest pose: the same target then bends the other way, so the plane comes from the
    /// authored pose rather than from a fixed axis.</summary>
    [Fact]
    public void TheBendStaysInTheDriverPosedPlane() {
        var target = new Vector3(x: 0f, y: 0.2f, z: 0f);
        // The rest pose bends the knee toward +Z (the ankle sits forward of the hip-to-ankle line).
        var forwardKnee = new Vector3(x: 0f, y: 0.55f, z: 0.15f);
        var behindKnee = new Vector3(x: 0f, y: 0.55f, z: -0.15f);

        WorldEffectorSolver.SolveTwoBone(
            mid: forwardKnee,
            root: Hip,
            solvedMid: out var forwardSolved,
            solvedTip: out _,
            target: target,
            tip: Ankle
        );
        WorldEffectorSolver.SolveTwoBone(
            mid: behindKnee,
            root: Hip,
            solvedMid: out var behindSolved,
            solvedTip: out _,
            target: target,
            tip: new Vector3(x: 0f, y: 0.1f, z: -0.1f)
        );

        Assert.True(condition: (forwardSolved.Z > 0f), userMessage: $"a knee bent forward at rest solved backward; z={forwardSolved.Z}");
        Assert.True(condition: (behindSolved.Z < 0f), userMessage: $"a knee bent backward at rest solved forward; z={behindSolved.Z}");
    }
    /// <summary>A four-bone chain converges onto a reachable target by cyclic coordinate descent. The control is a
    /// target past the chain's summed length, which the sweep approaches to that length and no closer.</summary>
    [Fact]
    public void CyclicDescentConvergesOnAFourBoneChain() {
        var target = new Vector3(x: 0.7f, y: 0.4f, z: 0.35f);

        Assert.True(
            condition: (Solve(target: target, tip: out var tip) < Tolerance),
            userMessage: $"a reachable target was not converged onto; tip={tip}"
        );

        // The control: four 0.25-metre bones span 1 metre, so a target three metres out stays two metres away
        // however many sweeps run — the convergence above is the solve working, not the tip tracking the target.
        var far = new Vector3(x: 3f, y: 0f, z: 0f);
        var residual = Solve(
            target: far,
            tip: out var farTip
        );

        Assert.True(
            condition: (MathF.Abs(x: (residual - (Vector3.Distance(value1: Vector3.Zero, value2: far) - 1f))) < 1e-2f),
            userMessage: $"an out-of-reach target did not settle at full extension; residual={residual}, tip={farTip}"
        );

        static float Solve(Vector3 target, out Vector3 tip) {
            // A straight chain up +Y: joints at 0, 0.25, 0.5, 0.75 with the tip at 1.
            Span<Vector3> joints = [Vector3.Zero, new Vector3(x: 0f, y: 0.25f, z: 0f), new Vector3(x: 0f, y: 0.5f, z: 0f), new Vector3(x: 0f, y: 0.75f, z: 0f)];
            Span<Quaternion> parents = [Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, Quaternion.Identity];
            Span<Quaternion> corrections = [Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, Quaternion.Identity];

            tip = new Vector3(x: 0f, y: 1f, z: 0f);

            WorldEffectorSolver.Solve(
                corrections: corrections,
                iterations: CreationEffectorDocument.Iterations,
                parentRotations: parents,
                posedJoints: joints,
                posedTip: ref tip,
                target: target
            );

            for (var bone = 0; (bone < 4); bone++) {
                // Every bone keeps its 0.25-metre length: the sweep only rotates.
                var end = ((bone == 3)
                    ? tip
                    : joints[bone + 1]
                );

                Assert.True(
                    condition: (MathF.Abs(x: (Vector3.Distance(value1: joints[bone], value2: end) - 0.25f)) < Tolerance),
                    userMessage: $"bone {bone} changed length"
                );
            }

            return Vector3.Distance(
                value1: target,
                value2: tip
            );
        }
    }
    /// <summary>A surface probe marches the shared query field and reports the hit lifted off its own normal by the
    /// standoff. The controls: a probe pointed away from the box misses, and one whose reach falls short of it
    /// misses — so the hit is the geometry rather than the probe always answering.</summary>
    [Fact]
    public void ASurfaceProbeLandsAtStandoffOffTheHitNormal() {
        var field = new SdfFieldEvaluator(program: BoxProgram());

        Assert.True(condition: WorldEffectorSolver.TryProbeSurface(
            field: field,
            origin: new Vector3(x: 0f, y: 0.4f, z: 0.1f),
            reach: 1f,
            rootRotation: Quaternion.Identity,
            standoff: 0.03f,
            target: out var target,
            towards: -Vector3.UnitY
        ), userMessage: "the probe missed a box directly below it");
        AssertNear(
            actual: target,
            expected: new Vector3(x: 0f, y: 0.03f, z: 0.1f),
            what: "the probe did not land at the standoff off the box's top face"
        );

        // The direction is body-relative: the same authored probe on a body rolled a half turn about +Z points UP,
        // and there is no geometry above the box.
        Assert.False(condition: WorldEffectorSolver.TryProbeSurface(
            field: field,
            origin: new Vector3(x: 0f, y: 0.4f, z: 0.1f),
            reach: 1f,
            rootRotation: Quaternion.CreateFromAxisAngle(
                angle: MathF.PI,
                axis: Vector3.UnitZ
            ),
            standoff: 0.03f,
            target: out _,
            towards: -Vector3.UnitY
        ), userMessage: "a probe pointed away from the box still hit it");
        // The reach bounds the march: 0.1 metres does not span the 0.4-metre gap.
        Assert.False(condition: WorldEffectorSolver.TryProbeSurface(
            field: field,
            origin: new Vector3(x: 0f, y: 0.4f, z: 0.1f),
            reach: 0.1f,
            rootRotation: Quaternion.Identity,
            standoff: 0.03f,
            target: out _,
            towards: -Vector3.UnitY
        ), userMessage: "a probe shorter than the gap still reached the box");
    }
    /// <summary>A surface effector on a body-rooted stamp brings the tip to the probed standoff through the whole
    /// pack path. The control is the identical rig with the effector stripped, whose tip stays where the drivers
    /// posed it.</summary>
    [Fact]
    public void ASurfaceEffectorBringsTheTipToTheProbedStandoff() {
        var solved = Pack(
            effectors: [Leg(plant: null)],
            frames: 120,
            step: Vector3.Zero
        );
        var inert = Pack(
            effectors: null,
            frames: 120,
            step: Vector3.Zero
        );

        // The rig's boot rests 0.02 BELOW the box's top face; the probe lifts it to the 0.03 standoff above it.
        Assert.True(condition: (MathF.Abs(x: (solved.Y - 0.03f)) < 1e-3f), userMessage: $"the tip did not settle at the standoff; y={solved.Y}");
        Assert.True(condition: (inert.Y < -0.01f), userMessage: $"the control rig's tip moved without an effector; y={inert.Y}");
    }
    /// <summary>A plant window latches the tip's world target when it opens, so the tip holds its world point while
    /// the body travels through the window. The control is the identical effector without <c>plant</c>, whose tip
    /// travels with the body.</summary>
    [Fact]
    public void PlantingHoldsTheTipsWorldPointWhileTheBodyTravels() {
        // A `time` driver at cadence 1 with the window covering the first half turn: the phase advances 1/60 of a
        // radian a frame, so a 20-frame walk stays inside one window.
        var planted = Walk(effectors: [Leg(plant: new CreationPlantDocument(
            Driver: "clock",
            Window: new Vector2(x: 0f, y: MathF.PI)
        ))]);
        var free = Walk(effectors: [Leg(plant: null)]);

        Assert.True(
            condition: (planted.Travel > 0.1f),
            userMessage: $"the body did not travel far enough to discriminate; travel={planted.Travel}"
        );
        // The composition is a chain of float rotations over a moving root, so the pin is a tenth of a millimetre
        // rather than bytes.
        Assert.True(
            condition: (planted.Drift < 1e-4f),
            userMessage: $"a planted tip slid while the body travelled; drift={planted.Drift} over {planted.Travel} of travel"
        );
        Assert.True(
            condition: (free.Drift > (0.5f * free.Travel)),
            userMessage: $"the unplanted control did not follow the body; drift={free.Drift} over {free.Travel} of travel"
        );

        static (float Drift, float Travel) Walk(IReadOnlyList<CreationEffectorDocument>? effectors) {
            var pool = new WorldStampPool();
            var creation = Prototype(effectors: effectors);
            var client = Client(definition: Definition(creation: creation));
            var transforms = new DynamicTransform[WorldStampPool.DynamicSlotCount];

            pool.Reconcile(
                bodyStamps: [new WorldStampPool.BodyStamp(BodyIndex: 0, Creation: creation, Scale: 1f, Motion: WorldLookMotion.Default)],
                creations: [creation],
                dynamics: [],
                placements: []
            );

            // Settle the effector weight at rest first, so the walk below measures the latch and not the ease-in.
            for (var frame = 0; (frame < 120); frame++) {
                Advance(
                    client: client,
                    pool: pool,
                    position: Vector3.Zero,
                    tick: ((ulong)frame),
                    transforms: transforms
                );
            }

            var start = transforms[TipSlot].Position;
            var travelled = Vector3.Zero;

            for (var frame = 0; (frame < 20); frame++) {
                travelled += new Vector3(x: 0f, y: 0f, z: 0.01f);

                Advance(
                    client: client,
                    pool: pool,
                    position: travelled,
                    tick: ((ulong)(120 + frame)),
                    transforms: transforms
                );
            }

            return (Vector3.Distance(
                value1: start,
                value2: transforms[TipSlot].Position
            ), travelled.Length());
        }
    }
    /// <summary>An effector's probe direction and body offset take the author frame's half turn, and so does a
    /// bone's authored <c>joint</c>; the reach, standoff, weight, and plant window do not. The round trip back to the
    /// author frame is the identity.</summary>
    [Fact]
    public void AuthoredDirectionsAndJointsTakeTheAuthorFrameHalfTurn() {
        var authored = Rig(effectors: [Leg(plant: null) with {
            Target = new CreationEffectorTargetDocument(
                Direction: new Vector3(x: 0.3f, y: -1f, z: 0.4f),
                Kind: CreationEffectorTargetDocument.KindBody,
                Index: 3,
                Offset: new Vector3(x: 0.2f, y: 0.5f, z: 0.7f)
            ),
        }]);
        var engine = CreationFrame.ToEngine(document: authored);
        var target = engine.Effectors![0].Target;

        // The control: an identity conversion would leave these equal to the authored values, and both flipped
        // components are non-zero on purpose so it cannot pass by accident.
        AssertNear(
            actual: target.Direction!.Value,
            expected: new Vector3(x: -0.3f, y: -1f, z: -0.4f),
            what: "the probe direction did not take the half turn"
        );
        AssertNear(
            actual: target.Offset!.Value,
            expected: new Vector3(x: -0.2f, y: 0.5f, z: -0.7f),
            what: "the body offset did not take the half turn"
        );
        Assert.Equal(expected: 3, actual: target.Index);
        AssertNear(
            actual: engine.Shapes![2].Joint!.Value,
            expected: new Vector3(x: -0.05f, y: 0.1f, z: -0.02f),
            what: "an authored joint did not take the half turn"
        );
        // A half turn is its own inverse, so the round trip restores the authored document.
        AssertNear(
            actual: CreationFrame.ToAuthor(document: engine).Effectors![0].Target.Direction!.Value,
            expected: new Vector3(x: 0.3f, y: -1f, z: 0.4f),
            what: "the round trip was not the identity"
        );
    }
    /// <summary>Effectors are presentation-only: a creation carrying them emits the same SDF program words as one
    /// with them stripped, even though the two documents hash differently.</summary>
    [Fact]
    public void EffectorsChangeNeitherTheRenderProgramNorTheHashOfAnUnEffectedRig() {
        var solved = Canonical(effectors: [Leg(plant: new CreationPlantDocument(
            Driver: "clock",
            Window: new Vector2(x: 0f, y: MathF.PI)
        ))]);
        var inert = Canonical(effectors: null);

        // The control: the two documents really are different content, so an assertion that they agree downstream is
        // not comparing a document with itself.
        Assert.NotEqual(expected: inert.Hash, actual: solved.Hash);
        Assert.Equal(expected: EmitWords(creation: inert), actual: EmitWords(creation: solved));
        // A creation authored without the member keeps its canonical bytes free of it.
        Assert.DoesNotContain(
            actualString: Encoding.UTF8.GetString(bytes: inert.Bytes),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "\"effectors\""
        );
        Assert.Contains(
            actualString: Encoding.UTF8.GetString(bytes: solved.Bytes),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "\"effectors\""
        );

        static CanonicalDocument<CreationDocument> Canonical(IReadOnlyList<CreationEffectorDocument>? effectors) => CreationCanonicalizer.Canonicalize(
            document: Rig(effectors: effectors),
            source: "rig"
        );
    }
    /// <summary>Every refusal the effector family owes, each against the control that differs only in the refused
    /// member.</summary>
    [Fact]
    public void TheCanonicalizerRefusesEachMalformedEffectorByName() {
        Assert.Empty(collection: CreationCanonicalizer.Validate(document: Rig(effectors: [Leg(plant: null)])));

        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with { Chain = ["thigh"] }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"is fewer than the {CreationEffectorDocument.MinChainBones} a chain needs"
        );
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with { Chain = [.. Enumerable.Repeat(count: (CreationEffectorDocument.MaxChainBones + 1), element: "thigh")] }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"exceeds the {CreationEffectorDocument.MaxChainBones}-bone chain"
        );
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with { Chain = ["thigh", "phantom"] }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "names no shape 'phantom'."
        );
        // A chain whose second bone hangs off the creation root rather than off the first is not one limb.
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with { Chain = ["thigh", "loose"] }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "does not descend from 'thigh' through parent"
        );
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with { Tip = "loose" }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "does not descend from the chain's last bone 'shin'"
        );
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with { Chain = ["thigh", "thigh"] }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "duplicate bone 'thigh'."
        );
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with { Target = new CreationEffectorTargetDocument(Kind: "vibes") }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "kind 'vibes' is not recognized"
        );
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with {
                Target = new CreationEffectorTargetDocument(
                    Direction: Vector3.Zero,
                    Kind: CreationEffectorTargetDocument.KindSurface,
                    Reach: 0.5f
                ),
            }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "direction is zero, which names no direction."
        );
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with {
                Target = new CreationEffectorTargetDocument(
                    Direction: -Vector3.UnitY,
                    Kind: CreationEffectorTargetDocument.KindSurface,
                    Reach: 0f
                ),
            }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"reach 0 is outside (0, {CreationEffectorTargetDocument.MaxReach}]"
        );
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with {
                Target = new CreationEffectorTargetDocument(
                    Direction: -Vector3.UnitY,
                    Kind: CreationEffectorTargetDocument.KindSurface,
                    Reach: 0.5f,
                    Standoff: 4f
                ),
            }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"standoff 4 is outside [0, {CreationEffectorTargetDocument.MaxStandoff}]"
        );
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with { Target = new CreationEffectorTargetDocument(Kind: CreationEffectorTargetDocument.KindState, Reference: "boots") }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is not a 'state.<row>[.<key>]' state reference"
        );
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: new CreationPlantDocument(Driver: "phantom", Window: new Vector2(x: 0f, y: 1f)))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "names no declared driver 'phantom'."
        );
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: new CreationPlantDocument(Driver: "clock", Window: new Vector2(x: 0f, y: 7f)))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "radians; a driver's phase is wrapped"
        );
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with { Weight = 2f }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "weight 2 is outside [0, 1]."
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(effectors: [Leg(plant: null), Leg(plant: null)])),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "duplicate effector name 'footLeft'."
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(effectors: [.. Enumerable.Range(start: 0, count: (CreationDocument.MaxEffectors + 1)).Select(selector: index => Leg(plant: null) with { Name = $"e{index}" })])),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"exceeds the {CreationDocument.MaxEffectors}-effector list."
        );
        // The gate rides the driver vocabulary, so its refusals are the same ones.
        Assert.Contains(
            actualString: Refuse(effector: Leg(plant: null) with { When = ["moving", "still"] }),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "are negations, so the gate can never hold."
        );

        static string Refuse(CreationEffectorDocument effector) => Refusal(document: Rig(effectors: [effector]));
    }

    // The one two-bone leg every pipeline law drives: a hip-knee-boot chain whose boot probes for whatever is below
    // the body within half a metre and stands 0.03 off it.
    private static CreationEffectorDocument Leg(CreationPlantDocument? plant) => new(
        Chain: ["thigh", "shin"],
        Name: "footLeft",
        Plant: plant,
        Target: new CreationEffectorTargetDocument(
            Direction: -Vector3.UnitY,
            Kind: CreationEffectorTargetDocument.KindSurface,
            Reach: 0.5f,
            Standoff: 0.03f
        ),
        Tip: "boot",
        When: [CreationDriverDocument.WhenAlways]
    );
    // The tip's dynamic-transform slot: the pool's first registration's root slot plus the boot's shape index.
    private static int TipSlot => (1 + 2);

    private static ShapeDocument Part(int id, string name, Vector3 position, string? parent, Vector3? pivot, Vector3? joint = null) => new(
        Id: id,
        Name: name,
        Type: SdfSolidPrimitive.Capsule,
        Position: position,
        Rotation: Quaternion.Identity,
        Scale: new Vector3(x: 0.05f, y: 0.2f, z: 0.05f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Joint: ((joint is { } authored)
            ? new DocumentVector3(value: authored)
            : null),
        Parent: parent,
        Swings: ((pivot is { } hinge)
            ? [new ShapeSwingDocument(
                Amplitude: 0f,
                Axis: Vector3.UnitX,
                Driver: "clock",
                Pivot: hinge,
                Wave: CreationWave.Constant
            )]
            : null)
    );
    // Author frame == engine frame for this rig's own points: every X and Z is zero, so the half turn is the identity
    // on them and a law's expected numbers read the same in both frames. The boot carries an authored `joint` with
    // non-zero X and Z purely so the frame-conversion law has something to flip.
    private static CreationDocument Rig(IReadOnlyList<CreationEffectorDocument>? effectors) => new(
        Schema: CreationDocument.CurrentSchema,
        Name: "rig",
        Palette: null,
        Shapes: [
            Part(id: 0, name: "thigh", parent: null, pivot: new Vector3(x: 0f, y: 0.5f, z: 0f), position: new Vector3(x: 0f, y: 0.37f, z: 0.03f)),
            Part(id: 1, name: "shin", parent: "thigh", pivot: new Vector3(x: 0f, y: 0.24f, z: 0.06f), position: new Vector3(x: 0f, y: 0.11f, z: 0.04f)),
            Part(id: 2, name: "boot", parent: "shin", pivot: null, position: new Vector3(x: 0f, y: -0.02f, z: 0.02f), joint: new Vector3(x: 0.05f, y: 0.1f, z: 0.02f)),
            Part(id: 3, name: "loose", parent: null, pivot: null, position: new Vector3(x: 0f, y: 0.5f, z: 0f)),
        ],
        Frames: null,
        Drivers: [new CreationDriverDocument(
            Cadence: 1f,
            Name: "clock",
            Signal: CreationDriverDocument.SignalTime,
            When: [CreationDriverDocument.WhenAlways]
        )],
        Effectors: effectors
    );
    private static WorldPrototype Prototype(CreationDocument document) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: "rig"
        );

        return new WorldPrototype(Id: "rig", Document: canonical.Document, HashRaw: canonical.Hash);
    }
    private static WorldDefinition Definition(WorldPrototype creation) => (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
        CreationsRaw = [creation],
        LookRowsRaw = [new WorldLook(Name: "rig", Source: new WorldLookSource.Creation(PrototypeId: creation.Id), Scale: 1f, Motion: WorldLookMotion.Default)],
    });
    private static WorldPrototype Prototype(IReadOnlyList<CreationEffectorDocument>? effectors) => Prototype(document: Rig(effectors: effectors));
    // A 4 x 1 x 4 box whose top face is the y = 0 plane — the one surface every probe law reads.
    private static SdfProgram BoxProgram() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.5f, y: 0.5f, z: 0.5f)));

        _ = builder.Translate(offset: new Vector3(x: 0f, y: -0.5f, z: 0f));
        builder.Box(
            halfExtents: new Vector3(x: 2f, y: 0.5f, z: 2f),
            material: material,
            round: 0f
        );

        return builder.Build(buildInstanceGrid: false);
    }
    // The narrowest link a PlayerRoster can be built over: it answers the one construction-time query and drops
    // everything else, so no server has to run for a pack-path law.
    private sealed class SilentLink(WorldDefinition definition) : IServerLink {
        public void Query(WorldQuery query, Action<QueryAnswer> completion) {
            if (query is WorldQuery.PopulationChannels) {
                completion(obj: new QueryAnswer(
                    Payload: WorldChannelTable.Compile(channels: definition.Channels),
                    Text: string.Empty
                ));
            }
        }
        public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal) => 0L;
        public void SubmitIntent(in IntentSubmission submission) {
        }
        public void SubmitSession(SessionRequest request, Action<SessionReply> completion) {
        }
    }
    private static WorldClient Client(WorldDefinition definition) {
        var client = new WorldClient(
            composition: new WorldCompositionState(),
            definition: definition,
            roster: new PlayerRoster(
                definition: definition,
                link: new SilentLink(definition: definition),
                seatBindings: new WorldSeatBindings(definition: definition)
            ),
            seatRouter: new WorldSeatAuthorityRouter()
        );

        client.PublishStaticField(field: new SdfFieldEvaluator(program: BoxProgram()));

        return client;
    }
    // One frame of the real pack path: deliver body 0's pose, resolve the render poses, then pack.
    private static void Advance(WorldClient client, WorldStampPool pool, DynamicTransform[] transforms, Vector3 position, ulong tick) {
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
                Orientation: Quaternion.Identity,
                Position: position
            ) },
            Revision: 0,
            StepTicks: 1UL,
            Tick: tick
        ));
        client.UpdateRenderPoses(alpha: 1f);
        pool.Tick(deltaSeconds: (1f / 60f));
        pool.PackTransforms(
            client: client,
            parkPosition: new Vector3(x: 0f, y: -1000f, z: 0f),
            slotBase: 0,
            transforms: transforms
        );
    }
    // Runs the pack path for a run of frames and returns the tip's final world position.
    private static Vector3 Pack(IReadOnlyList<CreationEffectorDocument>? effectors, int frames, Vector3 step) {
        var pool = new WorldStampPool();
        var creation = Prototype(effectors: effectors);
        var client = Client(definition: Definition(creation: creation));
        var transforms = new DynamicTransform[WorldStampPool.DynamicSlotCount];
        var position = Vector3.Zero;

        pool.Reconcile(
            bodyStamps: [new WorldStampPool.BodyStamp(BodyIndex: 0, Creation: creation, Scale: 1f, Motion: WorldLookMotion.Default)],
            creations: [creation],
            dynamics: [],
            placements: []
        );

        for (var frame = 0; (frame < frames); frame++) {
            Advance(
                client: client,
                pool: pool,
                position: position,
                tick: ((ulong)frame),
                transforms: transforms
            );
            position += step;
        }

        return transforms[TipSlot].Position;
    }
    // The dynamic emission path a body-stamped creation actually renders through — the one place an effector could
    // leak into geometry if the pack path were not the only reader.
    private static uint[] EmitWords(CanonicalDocument<CreationDocument> creation) {
        var prototype = new WorldPrototype(Id: "rig", Document: creation.Document, HashRaw: creation.Hash);
        var pool = new WorldStampPool();

        pool.Reconcile(
            bodyStamps: [new WorldStampPool.BodyStamp(BodyIndex: 0, Creation: prototype, Scale: 1f, Motion: WorldLookMotion.Default)],
            creations: [prototype],
            dynamics: [],
            placements: []
        );

        var builder = new SdfProgramBuilder();

        pool.Emit(
            builder: builder,
            definition: Definition(creation: prototype),
            maxPlacementScale: 1f,
            probeWorstCase: false,
            slotBase: 0
        );

        return builder.Build(buildInstanceGrid: false).Words.ToArray();
    }
}
