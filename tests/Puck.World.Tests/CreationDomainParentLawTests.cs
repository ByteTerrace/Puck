using System.Numerics;

using Puck.Physics.Motion;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins Door 1: a domain-bearing shape (Symmetry/Repeat/Polar…) carrying a <c>parent</c> rides the
/// parent's chained rigid delta on its own dynamic-transform slot instead of the identity — the pair
/// <see cref="WorldStampPool.PackTransforms"/> (which packs the slot, in placement units) and the emitter (which
/// reads it, scales, then folds, before the shape's own rest pose) — the two part-pose readers' agreement on such a
/// shape, and the canonicalizer's/validator's paired refusals: an own swing/slide, a named <c>frames</c> pose, and
/// a look's <c>partDynamics</c> follower.</summary>
public sealed class CreationDomainParentLawTests {
    private const float Tolerance = 1e-4f;
    private static readonly Vector3 SwingPivot = new(x: 0f, y: 1f, z: 0f);
    // CreationFrame's author-to-engine flip: a half turn about +Y, (x, y, z, w) = (0, 1, 0, 0). An identity-authored
    // shape's engine-frame rest rotation is exactly this, so a slot carrying `root * delta * rest` for the torso and
    // `root * delta` for the fold differ by it on the right.
    private static readonly Quaternion Yaw180 = new(x: 0f, y: 1f, z: 0f, w: 0f);
    // The pool's one registration: root slot 0, shape slots (1 + shape index).
    internal const int RootSlot = 0;
    internal const int TorsoSlot = 1;
    internal const int FoldSlot = 2;
    private const int AnchorSlot = 3;

    private static ShapeDocument Torso(IReadOnlyList<ShapeSwingDocument>? swings) => new(
        Id: 0,
        Name: "torso",
        Type: SdfSolidPrimitive.Capsule,
        Position: Vector3.Zero,
        Rotation: Quaternion.Identity,
        Scale: new Vector3(x: 0.1f, y: 0.5f, z: 0.1f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Swings: swings
    );
    private static ShapeDocument Fold(IReadOnlyList<ShapeDomainOp>? domain, string? parent, IReadOnlyList<ShapeSwingDocument>? swings = null) => new(
        Id: 1,
        Name: "fold",
        Type: SdfSolidPrimitive.Sphere,
        Position: new Vector3(x: 0.4f, y: 0.2f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: (Vector3.One * 0.15f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Domain: domain,
        Parent: parent,
        Swings: swings
    );
    // A second, UNPARENTED domain-bearing shape: its own slot always carries the identity delta (no parent to
    // chain), so it is the follow law's control — the same rig, the same driver, but never moved by the swing.
    private static ShapeDocument Anchor(IReadOnlyList<ShapeDomainOp>? domain) => new(
        Id: 2,
        Name: "anchor",
        Type: SdfSolidPrimitive.Sphere,
        Position: new Vector3(x: -0.3f, y: 0.1f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: (Vector3.One * 0.1f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Domain: domain
    );
    private static CreationDocument Rig(ShapeDocument torso, ShapeDocument fold, ShapeDocument? anchor = null, IReadOnlyList<FrameDocument>? frames = null, IReadOnlyList<CreationPartDocument>? parts = null) => new(
        Schema: CreationDocument.CurrentSchema,
        Name: "rig",
        Palette: null,
        Shapes: ((anchor is { } third) ? [torso, fold, third] : [torso, fold]),
        Frames: frames,
        Parts: parts,
        Drivers: [new CreationDriverDocument(
            Cadence: 1f,
            Name: "sway",
            Signal: CreationDriverDocument.SignalTime,
            When: ["Grounded"]
        )]
    );
    // The rig this file's admission law exercises: an unparented swinging torso and a domain-bearing fold parented
    // to it, with no own facet of its own.
    private static CreationDocument DefaultRig() => Rig(
        fold: Fold(
            domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)],
            parent: "torso"
        ),
        torso: Torso(swings: [new ShapeSwingDocument(Amplitude: 1f, Axis: Vector3.UnitZ, Driver: "sway", Pivot: SwingPivot)])
    );
    // The follow law's rig: DefaultRig plus the unparented control shape, and a part on the fold and the torso so
    // the part-pose readers can be asked about each.
    internal static CreationDocument FollowRig() => Rig(
        anchor: Anchor(domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)]),
        fold: Fold(
            domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)],
            parent: "torso"
        ),
        parts: [new CreationPartDocument(Id: "fold", ShapeId: 1), new CreationPartDocument(Id: "torso", ShapeId: 0)],
        torso: Torso(swings: [new ShapeSwingDocument(Amplitude: 1f, Axis: Vector3.UnitZ, Driver: "sway", Pivot: SwingPivot)])
    );
    private static string Refusal(CreationDocument document) => string.Join(
        separator: "; ",
        values: CreationCanonicalizer.Validate(document: document)
    );
    internal static WorldPrototype Prototype(CreationDocument document) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: "rig"
        );

        return new WorldPrototype(Id: "rig", Document: canonical.Document, HashRaw: canonical.Hash);
    }
    internal static WorldDefinition Definition(WorldPrototype creation, float scale = 1f) => (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
        CreationsRaw = [creation],
        LookRowsRaw = [new WorldLook(Name: "rig", Source: new WorldLookSource.Creation(PrototypeId: creation.Id), Scale: scale, Motion: WorldLookMotion.Default)],
    });
    // The whole-document validator's fixture: the placement-free base document, so the creation list can be
    // replaced without orphaning a placement row.
    private static WorldDefinition ValidatedDefinition(WorldPrototype creation, WorldLookMotion motion) => (Fixtures.BuildDocument() with {
        CreationsRaw = [creation],
        LookRowsRaw = [new WorldLook(Name: "rig", Source: new WorldLookSource.Creation(PrototypeId: creation.Id), Scale: 1f, Motion: motion)],
    });
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
    internal static WorldClient Client(WorldDefinition definition) => new(
        composition: new WorldCompositionState(),
        definition: definition,
        roster: new PlayerRoster(
            definition: definition,
            link: new SilentLink(definition: definition),
            seatBindings: new WorldSeatBindings(definition: definition)
        ),
        seatRouter: new WorldSeatAuthorityRouter()
    );
    // A pool holding one body-rooted registration of the creation at the given look scale.
    internal static WorldStampPool Pool(WorldPrototype creation, float scale = 1f) {
        var pool = new WorldStampPool();

        pool.Reconcile(
            bodyStamps: [new WorldStampPool.BodyStamp(BodyIndex: 0, Creation: creation, Scale: scale, Motion: WorldLookMotion.Default)],
            creations: [creation],
            dynamics: [],
            placements: []
        );

        return pool;
    }
    // One frame of the real pack path: deliver body 0's Grounded pose, resolve the render poses, then pack — the
    // same harness shape CreationEffectorLawTests drives, with a held Grounded fact so the torso's driver-gated
    // swing actually advances.
    internal static void Advance(WorldClient client, WorldStampPool pool, DynamicTransform[] transforms, ulong tick) {
        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Authority: "a",
            Entries: new[] { new EntitySnapshot(
                Active: true,
                BodyColor: Vector3.One,
                CatalogRig: 0,
                Continuity: EntityContinuity.Continuous,
                Facts: BodyFacts.Grounded,
                Generation: 1,
                Index: 0,
                Kit: 0,
                Look: 0,
                Orientation: Quaternion.Identity,
                Position: Vector3.Zero
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
    internal static SdfProgram EmitProgram(WorldPrototype creation, float scale = 1f) {
        var builder = new SdfProgramBuilder();

        Pool(creation: creation, scale: scale).Emit(
            builder: builder,
            definition: Definition(creation: creation, scale: scale),
            maxPlacementScale: scale,
            probeWorstCase: false,
            slotBase: 0
        );

        return builder.Build(buildInstanceGrid: false);
    }
    private static void AssertClose(Vector3 expected, Vector3 actual, string what) => Assert.True(
        condition: (Vector3.Distance(value1: expected, value2: actual) < Tolerance),
        userMessage: $"{what}: expected {expected}, read {actual}."
    );
    private static void AssertSameOrientation(Quaternion expected, Quaternion actual, string what) => Assert.True(
        condition: (MathF.Abs(x: Quaternion.Dot(quaternion1: expected, quaternion2: actual)) > (1f - Tolerance)),
        userMessage: $"{what}: expected {expected}, read {actual}."
    );
    // The fold shape's emitted chain: its own slot, the placement scale, the fold, then its rest translate/rotate —
    // returned as the index of the TransformDynamic that opens it.
    private static int AssertFoldChain(SdfInstruction[] program, float scale, Vector3 engineRestPosition) {
        var transformIndex = Array.FindIndex(
            array: program,
            match: instruction => ((instruction.Op == SdfOp.TransformDynamic) && (((int)instruction.Data0.X) == FoldSlot))
        );

        Assert.True(condition: (transformIndex >= 0), userMessage: "the fold shape never rides its own dynamic-transform slot.");
        Assert.True(condition: ((transformIndex + 4) < program.Length), userMessage: "the fold shape's own slot is not followed by its scale, fold, and rest pose.");
        Assert.Equal(expected: SdfOp.Scale, actual: program[transformIndex + 1].Op);
        AssertClose(
            actual: new Vector3(x: program[transformIndex + 1].Data0.X, y: program[transformIndex + 1].Data0.Y, z: program[transformIndex + 1].Data0.Z),
            expected: new Vector3(value: scale),
            what: "the Scale op after the fold's slot is not the placement scale"
        );
        Assert.Equal(expected: SdfOp.SymmetryPlane, actual: program[transformIndex + 2].Op);
        Assert.Equal(expected: SdfOp.Translate, actual: program[transformIndex + 3].Op);
        AssertClose(
            actual: new Vector3(x: program[transformIndex + 3].Data0.X, y: program[transformIndex + 3].Data0.Y, z: program[transformIndex + 3].Data0.Z),
            expected: engineRestPosition,
            what: "the Translate after the fold is not the shape's (unscaled — the Scale op carries the scale) engine-frame rest position"
        );
        Assert.Equal(expected: SdfOp.Rotate, actual: program[transformIndex + 4].Op);

        return transformIndex;
    }

    /// <summary>A 'symmetry' shape parented to a swinging shape rides EXACTLY the parent's delta frame: the torso's
    /// rest pose is the origin with an identity authored rotation, so its own (composed) slot and the fold's
    /// (delta-only) slot must share a position and differ in orientation by exactly the engine-frame rest rotation of
    /// an identity-authored shape. The same shape with NO parent stays exactly at the identity delta — the two ends of
    /// the contract, read off the SAME pack pass against an unparented control riding the identical domain op and
    /// driver. The emitted program for the fold shape rides its own slot (TransformDynamic), scales, then applies
    /// its domain op (SymmetryPlane) before its unscaled rest pose.</summary>
    [Fact]
    public void DomainShapeParentedToASwingingShapeRidesExactlyTheParentDelta() {
        var creation = Prototype(document: FollowRig());
        var pool = Pool(creation: creation);
        var client = Client(definition: Definition(creation: creation));
        var transforms = new DynamicTransform[WorldStampPool.DynamicSlotCount];

        for (var frame = 0; (frame < 120); frame++) {
            Advance(
                client: client,
                pool: pool,
                tick: ((ulong)frame),
                transforms: transforms
            );
        }

        var torso = transforms[TorsoSlot];
        var fold = transforms[FoldSlot];
        var anchor = transforms[AnchorSlot];

        // The control: an unparented domain shape's own slot carries the identity delta exactly — root position,
        // root orientation, untouched by the torso's swing.
        AssertClose(actual: anchor.Position, expected: Vector3.Zero, what: "an unparented domain shape's slot is not the root position — the identity-delta contract does not hold");
        AssertSameOrientation(actual: anchor.Orientation, expected: Quaternion.Identity, what: "an unparented domain shape's orientation is not the root orientation — the identity-delta contract does not hold");
        // The swing must have produced a real delta, or the equalities below prove nothing.
        Assert.True(
            condition: ((Vector3.Distance(value1: torso.Position, value2: Vector3.Zero) > 0.05f) && (MathF.Abs(x: Quaternion.Dot(quaternion1: torso.Orientation, quaternion2: Yaw180)) < 0.99f)),
            userMessage: $"the parent's swing never produced a non-identity delta over 120 frames (torso at {torso.Position} / {torso.Orientation})."
        );
        // The subject: the fold's slot IS the parent's delta frame — a wrong composition order, a delta from the
        // wrong bone, or an unscaled delta would each break one of these.
        AssertClose(actual: fold.Position, expected: torso.Position, what: "the fold's slot position is not the parent's delta frame");
        AssertSameOrientation(actual: (fold.Orientation * Yaw180), expected: torso.Orientation, what: "the fold's slot orientation is not the parent's delta frame");

        _ = AssertFoldChain(
            engineRestPosition: CreationFrame.ToEngine(document: creation.Document).Shapes![1].Position.Value,
            program: EmitProgram(creation: creation).Instructions.ToArray(),
            scale: 1f
        );
    }

    /// <summary>At a look scale other than one the fold still rides the parent's delta frame exactly — the packed
    /// delta translation is in placement units, like the torso's composed slot — and the emitted chain carries the
    /// scale as its own Scale op ahead of the fold, leaving the rest translate in creation units and the primitive
    /// at the shape's own scale (the static stamper's chain, with the carried frame standing in for the placement
    /// frame). The control: at scale one the same assertions hold with a unit Scale op.</summary>
    [Fact]
    public void DomainShapeFollowsTheParentDeltaInPlacementUnitsAtLookScaleTwo() {
        const float Scale = 2f;
        var creation = Prototype(document: FollowRig());
        var pool = Pool(creation: creation, scale: Scale);
        var client = Client(definition: Definition(creation: creation, scale: Scale));
        var transforms = new DynamicTransform[WorldStampPool.DynamicSlotCount];

        for (var frame = 0; (frame < 120); frame++) {
            Advance(
                client: client,
                pool: pool,
                tick: ((ulong)frame),
                transforms: transforms
            );
        }

        var torso = transforms[TorsoSlot];
        var fold = transforms[FoldSlot];

        Assert.True(
            condition: (Vector3.Distance(value1: torso.Position, value2: Vector3.Zero) > 0.1f),
            userMessage: $"the parent's swing never displaced the torso at scale {Scale} (torso at {torso.Position})."
        );
        AssertClose(actual: fold.Position, expected: torso.Position, what: $"the fold's slot position is not the parent's delta frame at scale {Scale} — the delta was not scaled");
        AssertSameOrientation(actual: (fold.Orientation * Yaw180), expected: torso.Orientation, what: $"the fold's slot orientation is not the parent's delta frame at scale {Scale}");

        var program = EmitProgram(creation: creation, scale: Scale).Instructions.ToArray();
        var engine = CreationFrame.ToEngine(document: creation.Document);
        var transformIndex = AssertFoldChain(
            engineRestPosition: engine.Shapes![1].Position.Value,
            program: program,
            scale: Scale
        );
        // The primitive after the chain is at the shape's OWN scale: the Scale op immediately ahead of its unit
        // sphere carries the authored radius, not radius x 2.
        var shapeIndex = Array.FindIndex(
            array: program,
            startIndex: transformIndex,
            match: instruction => (instruction.Op == SdfOp.ShapeBlend)
        );

        Assert.True(condition: (shapeIndex > transformIndex), userMessage: "the fold's chain never reaches its primitive.");

        var primitiveScaleIndex = Array.FindLastIndex(
            array: program,
            startIndex: shapeIndex,
            match: instruction => (instruction.Op == SdfOp.Scale)
        );

        Assert.True(condition: (primitiveScaleIndex > (transformIndex + 1)), userMessage: "the fold's primitive carries no scale of its own after the chain's placement Scale op.");
        Assert.True(
            condition: (MathF.Abs(x: (program[primitiveScaleIndex].Data0.X - engine.Shapes[1].Scale.X)) < Tolerance),
            userMessage: $"the fold's primitive scale {program[primitiveScaleIndex].Data0.X} is not the shape's own {engine.Shapes[1].Scale.X} — the placement scale was baked twice."
        );
    }

    /// <summary>The two part-pose readers agree on a domain-bearing part: the packed reader composes the shape's
    /// rest pose onto its slot's delta frame, the latch reader applies the delta to the rest pose — the same world
    /// pose either way, at a look scale of two so a missed scale shows. The control: an ordinary part (the torso)
    /// agrees the same way, its slot already being its composed pose.</summary>
    [Fact]
    public void PackedAndAuthoredPartPosesAgreeOnADomainPart() {
        const float Scale = 2f;
        var creation = Prototype(document: FollowRig());
        var pool = Pool(creation: creation, scale: Scale);
        var client = Client(definition: Definition(creation: creation, scale: Scale));
        var transforms = new DynamicTransform[WorldStampPool.DynamicSlotCount];

        for (var frame = 0; (frame < 120); frame++) {
            Advance(
                client: client,
                pool: pool,
                tick: ((ulong)frame),
                transforms: transforms
            );
        }

        foreach (var part in new[] { "fold", "torso" }) {
            Assert.True(condition: pool.TryBodyPartPose(bodyIndex: 0, partId: part, transforms: transforms, pose: out var packed), userMessage: $"part '{part}' resolves no packed pose.");
            Assert.True(condition: pool.TryBodyPartAuthoredPose(bodyIndex: 0, partId: part, client: client, pose: out var authored), userMessage: $"part '{part}' resolves no authored pose.");

            AssertClose(actual: packed.Position, expected: authored.Position, what: $"part '{part}': the packed and authored part poses disagree on position");
            AssertSameOrientation(actual: packed.Orientation, expected: authored.Orientation, what: $"part '{part}': the packed and authored part poses disagree on orientation");
        }

        // The fold's part pose is its REST pose carried by the parent, not the bare delta frame the slot holds.
        Assert.True(condition: pool.TryBodyPartPose(bodyIndex: 0, partId: "fold", transforms: transforms, pose: out var foldPose));
        Assert.True(
            condition: (Vector3.Distance(value1: foldPose.Position, value2: transforms[FoldSlot].Position) > 0.5f),
            userMessage: $"the fold's part pose ({foldPose.Position}) sits at its slot's delta frame ({transforms[FoldSlot].Position}) instead of its carried rest pose."
        );
    }

    /// <summary>A domain-bearing shape still refuses its own swing or slide even once it carries a parent — a fold
    /// rides its parent's frame, never its own swing. The control: the same shape with no own facet, parented, is
    /// admitted.</summary>
    [Fact]
    public void DomainShapeRefusesItsOwnSwingOrSlideEvenWithAParent() {
        Assert.Contains(
            actualString: Refusal(document: Rig(
                fold: Fold(
                    domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)],
                    parent: "torso",
                    swings: [new ShapeSwingDocument(Amplitude: 0.3f, Axis: Vector3.UnitZ, Driver: "sway", Pivot: SwingPivot)]
                ),
                torso: Torso(swings: null)
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "a fold rides its parent's frame, never its own swing"
        );
        Assert.Contains(
            actualString: Refusal(document: Rig(
                fold: Fold(
                    domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)],
                    parent: "torso"
                ) with { Slides = [new ShapeSlideDocument(Amplitude: 0.1f, Axis: Vector3.UnitY, Driver: "sway")] },
                torso: Torso(swings: null)
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "a fold rides its parent's frame, never its own swing"
        );

        // The control: domain + parent with no own facet is admitted.
        Assert.Empty(collection: CreationCanonicalizer.Validate(document: DefaultRig()));
    }

    /// <summary>A domain-bearing shape refuses a named <c>frames</c> transform — its geometry never reads a
    /// captured pose, only the parent's chained delta. The control: naming the domain-free torso instead is
    /// admitted.</summary>
    [Fact]
    public void DomainShapeRefusesANamedFramePose() {
        var foldFramed = Rig(
            fold: Fold(
                domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)],
                parent: "torso"
            ),
            frames: [new FrameDocument(Name: "pose", Transforms: [new FrameTransformDocument(Id: 1, Position: Vector3.Zero, Rotation: Quaternion.Identity, Scale: Vector3.One)])],
            torso: Torso(swings: null)
        );

        Assert.Contains(
            actualString: Refusal(document: foldFramed),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "carries domain operators, so its frame pose is never read."
        );

        // The control: the same frame naming the domain-free torso (id 0) instead is admitted.
        var torsoFramed = Rig(
            fold: Fold(
                domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)],
                parent: "torso"
            ),
            frames: [new FrameDocument(Name: "pose", Transforms: [new FrameTransformDocument(Id: 0, Position: Vector3.Zero, Rotation: Quaternion.Identity, Scale: Vector3.One)])],
            torso: Torso(swings: null)
        );

        Assert.Empty(collection: CreationCanonicalizer.Validate(document: torsoFramed));
    }

    /// <summary>A look's <c>partDynamics</c> refuses, by name, a part whose shape carries domain operators — its slot
    /// holds the parent's delta frame, so a follower has no pose of its own to ease and the pool's part follower
    /// would otherwise be armed and never stepped. The control: the same row on the torso's part is admitted.</summary>
    [Fact]
    public void PartDynamicsRefusesADomainPartWhileAnOrdinaryPartPasses() {
        var creation = Prototype(document: FollowRig());
        var denied = ValidatedDefinition(creation: creation, motion: (WorldLookMotion.Default with {
            PartDynamics = new Dictionary<string, string> { ["fold"] = "chase" },
        }));
        var admitted = ValidatedDefinition(creation: creation, motion: (WorldLookMotion.Default with {
            PartDynamics = new Dictionary<string, string> { ["torso"] = "chase" },
        }));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(
            actualString: deniedReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "motion.partDynamics['fold'] names part 'fold' of creation 'rig', whose shape 1 carries domain operators — a fold rides its parent's frame and has no pose of its own to ease."
        );
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }
}
