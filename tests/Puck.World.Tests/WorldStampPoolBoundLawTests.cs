using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a per-shape dynamic instance the stamp pool emits carries an INFLUENCE bound — a sphere no point of the
/// shape's field influence lies outside of. The tile cull applies that contract per tile cone and the interpreter's
/// per-sample influence skip applies it per sample, so a bound short of the primitive's reach clips geometry. The
/// pool once bounded a shape at 0.9 x max(scale), which covers neither a unit sphere nor a box's corners; the
/// packer's smooth halo hid it at tile granularity. Each shape here is checked against the same reach measure the
/// static stamper's <see cref="CreationStampEmitter.ShapeStampBound"/> takes.
/// </summary>
public sealed class WorldStampPoolBoundLawTests {
    private const string PrototypeId = "bounded";

    private static ShapeDocument Shape(int id, SdfSolidPrimitive type, Vector3 scale, float? dilate = null, float? onion = null, ShapePanelDocument? panel = null) =>
        new(
            Id: id,
            Name: null,
            Type: type,
            Position: new Vector3(
                x: (1.5f * id),
                y: 0f,
                z: 0f
            ),
            Rotation: Quaternion.Identity,
            Scale: scale,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0,
            Dilate: dilate,
            Onion: onion,
            Panel: panel
        );
    private static readonly ShapeDocument[] Shapes = [
        Shape(
            id: 0,
            scale: new Vector3(
                x: 0.2f,
                y: 0.3f,
                z: 0.1f
            ),
            type: SdfSolidPrimitive.Box
        ),
        Shape(
            id: 1,
            scale: new Vector3(value: 0.19f),
            type: SdfSolidPrimitive.Sphere
        ),
        Shape(
            id: 2,
            scale: new Vector3(
                x: 0.052f,
                y: 0.4755f,
                z: 0.052f
            ),
            type: SdfSolidPrimitive.Capsule
        ),
        Shape(
            id: 3,
            scale: new Vector3(
                x: 0.27f,
                y: 0.085f,
                z: 0.27f
            ),
            type: SdfSolidPrimitive.Cylinder
        ),
        Shape(
            dilate: 0.1f,
            id: 4,
            onion: 0.05f,
            scale: new Vector3(value: 0.25f),
            type: SdfSolidPrimitive.Sphere
        ),
        // A raised panel (Depth < 0) stands |Depth| proud of the plate's own face, so the packed instance bound
        // must widen by exactly that much — a recess would need no widening (subtraction only removes).
        Shape(
            id: 5,
            panel: new ShapePanelDocument(Inset: 0.01f, Depth: -0.05f, Material: 1),
            scale: new Vector3(
                x: 0.2f,
                y: 0.15f,
                z: 0.1f
            ),
            type: SdfSolidPrimitive.Box
        ),
    ];

    private static SdfProgram EmitPool(float bodyScale = 1f) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: PrototypeId,
                Palette: null,
                Shapes: Shapes,
                Frames: null,
                Noise: null
            ),
            source: PrototypeId
        );
        var creation = new WorldPrototype(
            Id: PrototypeId,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
        var definition = (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
            CreationsRaw = [creation],
            LookRowsRaw = [
                new WorldLook(
                    Name: "rig",
                    Source: new WorldLookSource.Creation(PrototypeId: PrototypeId),
                    Scale: bodyScale,
                    Motion: WorldLookMotion.Default
                ),
            ],
        });
        var pool = new WorldStampPool();

        pool.Reconcile(
            placements: [],
            creations: [creation],
            dynamics: [],
            bodyStamps: [
                new WorldStampPool.BodyStamp(
                    BodyIndex: 0,
                    Creation: creation,
                    Scale: bodyScale,
                    Motion: WorldLookMotion.Default
                ),
            ]
        );

        var builder = new SdfProgramBuilder();

        pool.Emit(
            builder: builder,
            definition: definition,
            probeWorstCase: false,
            maxPlacementScale: bodyScale,
            slotBase: 0
        );

        return builder.Build(buildInstanceGrid: false);
    }

    /// <summary>A raised panel's depth is a creation-unit value: at a body look scale the raise it adds to the
    /// world-unit instance bound scales with the placement, so the packed radius covers the scaled reach plus the
    /// scaled raise.</summary>
    [Fact]
    public void ARaisedPanelBoundScalesItsRaiseWithTheBodyLook() {
        const float BodyScale = 2f;
        const int PanelledShape = 5;
        var program = EmitPool(bodyScale: BodyScale);
        var active = program.Instances.Where(predicate: instance => instance.Active).ToArray();
        var shape = Shapes[PanelledShape];
        var required = (SdfSolidGeometry.Reach(
            type: shape.Type,
            scale: (shape.Scale * BodyScale),
            lift: SdfLift.Extrude
        ) + (-shape.Panel!.Depth * BodyScale));

        Assert.True(
            condition: (active[PanelledShape].Radius >= required),
            userMessage: $"the raised panel at look scale {BodyScale} packs radius {active[PanelledShape].Radius} below its scaled reach + raise {required}"
        );
        // The control: the unscaled raise alone would leave the bound short.
        Assert.True(condition: (required > (SdfSolidGeometry.Reach(type: shape.Type, scale: (shape.Scale * BodyScale), lift: SdfLift.Extrude) - shape.Panel.Depth)));
    }

    /// <summary>Dilate/Onion are creation-unit values, like a panel's depth: at a body look scale the outward growth
    /// they add to the world-unit instance bound scales with the placement.</summary>
    [Fact]
    public void ADilatedAndOnionedShapeBoundScalesWithTheBodyLook() {
        const float BodyScale = 2f;
        const int DilatedShape = 4;
        var program = EmitPool(bodyScale: BodyScale);
        var active = program.Instances.Where(predicate: instance => instance.Active).ToArray();
        var shape = Shapes[DilatedShape];
        var required = (SdfSolidGeometry.Reach(
            type: shape.Type,
            scale: (shape.Scale * BodyScale),
            lift: SdfLift.Extrude
        ) + ((shape.Dilate!.Value + shape.Onion!.Value) * BodyScale));

        Assert.True(
            condition: (active[DilatedShape].Radius >= required),
            userMessage: $"the dilated/onioned shape at look scale {BodyScale} packs radius {active[DilatedShape].Radius} below its scaled reach + growth {required}"
        );
        // The control: the unscaled growth alone would leave the bound short.
        Assert.True(condition: (required > (SdfSolidGeometry.Reach(type: shape.Type, scale: (shape.Scale * BodyScale), lift: SdfLift.Extrude) + shape.Dilate.Value + shape.Onion.Value)));
    }

    /// <summary>THE LAW: <see cref="SdfSolidGeometry.MaxPanelReach"/> — the probe's worst-case raised-panel term — is
    /// derived from <c>ShapePanelDocument</c>'s own <c>|Depth|</c> validation ceiling (twice a plate's own half-extent
    /// at zero inset), not a hand-picked literal: it covers that ceiling for every primitive a panel can be authored
    /// on, at every axis and (for Prism) either lift.</summary>
    [Theory]
    [InlineData(SdfSolidPrimitive.Sphere, SdfLift.Extrude)]
    [InlineData(SdfSolidPrimitive.Box, SdfLift.Extrude)]
    [InlineData(SdfSolidPrimitive.Torus, SdfLift.Extrude)]
    [InlineData(SdfSolidPrimitive.Cylinder, SdfLift.Extrude)]
    [InlineData(SdfSolidPrimitive.Capsule, SdfLift.Extrude)]
    [InlineData(SdfSolidPrimitive.Ellipsoid, SdfLift.Extrude)]
    [InlineData(SdfSolidPrimitive.RoundCone, SdfLift.Extrude)]
    [InlineData(SdfSolidPrimitive.Cone, SdfLift.Extrude)]
    [InlineData(SdfSolidPrimitive.Prism, SdfLift.Extrude)]
    [InlineData(SdfSolidPrimitive.Prism, SdfLift.Revolve)]
    public void MaxPanelReachCoversEveryPanelableTypesOwnDepthCeiling(SdfSolidPrimitive type, SdfLift lift) {
        var scale = new Vector3(value: 3f);
        var maxPanelReach = SdfSolidGeometry.MaxPanelReach(scale: scale);
        var ownCeiling = (2f * MathF.Max(
            x: SdfSolidGeometry.HalfExtent(type: type, scale: scale, lift: lift, axis: Vector3.UnitX),
            y: MathF.Max(
                x: SdfSolidGeometry.HalfExtent(type: type, scale: scale, lift: lift, axis: Vector3.UnitY),
                y: SdfSolidGeometry.HalfExtent(type: type, scale: scale, lift: lift, axis: Vector3.UnitZ)
            )
        ));

        Assert.True(
            condition: (maxPanelReach >= ownCeiling),
            userMessage: $"MaxPanelReach {maxPanelReach} undercuts {type}/{lift}'s own depth ceiling {ownCeiling}"
        );
    }

    [Fact]
    public void EveryPerShapeInstanceCoversItsPrimitiveReach() {
        var program = EmitPool();
        var active = program.Instances.Where(predicate: instance => instance.Active).ToArray();

        // One live instance per authored shape (the ungrouped pass), in document order, ahead of the parked pool slots.
        Assert.True(
            condition: (active.Length >= Shapes.Length),
            userMessage: $"expected at least {Shapes.Length} live instances, found {active.Length}"
        );

        for (var index = 0; (index < Shapes.Length); index++) {
            var shape = Shapes[index];
            var panelRaise = ((shape.Panel is { Depth: < 0f } panel)
                ? -panel.Depth
                : 0f);
            var required = ((SdfSolidGeometry.Reach(
                type: shape.Type,
                scale: shape.Scale,
                lift: SdfLift.Extrude,
                panelRaise: panelRaise
            ) + (shape.Dilate ?? 0f)) + (shape.Onion ?? 0f));

            Assert.True(
                condition: (active[index].Radius >= required),
                userMessage: $"shape {index} ({shape.Type}) packs radius {active[index].Radius} below its reach {required}"
            );
        }
    }

    /// <summary>A domain-bearing shape parented to a swinging shape packs its instance on ITS OWN slot (the parent's
    /// delta frame) with a radius covering every fold image from that frame's origin — rest offset plus fold
    /// displacement plus primitive reach — so the bound travels with the parent. Checked at every frame of a swing:
    /// each mirror image's own sphere lies inside the packed bound around the slot's live position. The discriminator:
    /// at the swing's peak the same image escapes a static sphere of the creation's whole reach around the ROOT
    /// slot, which is where a root-anchored bound would have clipped it at the tile seams.</summary>
    [Fact]
    public void AParentedDomainShapeBoundRidesItsOwnSlotAndCoversEveryFoldImage() {
        var creation = CreationDomainParentLawTests.Prototype(document: CreationDomainParentLawTests.FollowRig());
        var program = CreationDomainParentLawTests.EmitProgram(creation: creation);
        var instance = program.Instances.Single(predicate: candidate => (candidate.IsDynamic && (candidate.Slot == CreationDomainParentLawTests.FoldSlot)));
        var engine = CreationFrame.ToEngine(document: creation.Document);
        var fold = engine.Shapes![1];
        var solidReach = SdfSolidGeometry.Reach(type: fold.Type, scale: fold.Scale);
        // The symmetry plane is the engine-frame YZ plane, so the two images are the rest position and its X mirror.
        Vector3[] images = [fold.Position.Value, (fold.Position.Value with { X = -fold.Position.Value.X })];
        var creationReach = CreationStampEmitter.RenderReach(document: engine, scale: 1f, fontFor: null);
        var pool = CreationDomainParentLawTests.Pool(creation: creation);
        var client = CreationDomainParentLawTests.Client(definition: CreationDomainParentLawTests.Definition(creation: creation));
        var transforms = new DynamicTransform[WorldStampPool.DynamicSlotCount];
        var escapedRoot = false;

        Assert.Equal(expected: Vector3.Zero, actual: instance.Center);

        for (var frame = 0; (frame < 120); frame++) {
            CreationDomainParentLawTests.Advance(client: client, pool: pool, tick: ((ulong)frame), transforms: transforms);

            var slot = transforms[CreationDomainParentLawTests.FoldSlot];
            var root = transforms[CreationDomainParentLawTests.RootSlot];

            foreach (var image in images) {
                var center = (slot.Position + Vector3.Transform(value: image, rotation: slot.Orientation));
                var fromSlot = Vector3.Distance(value1: center, value2: slot.Position);

                Assert.True(
                    condition: ((fromSlot + solidReach) <= (instance.Radius + 1e-4f)),
                    userMessage: $"frame {frame}: fold image at {center} (sphere {solidReach}) lies {fromSlot} from its slot, outside the packed bound {instance.Radius}."
                );
                escapedRoot |= ((Vector3.Distance(value1: center, value2: root.Position) + solidReach) > creationReach);
            }
        }

        Assert.True(condition: escapedRoot, userMessage: "no swing frame carried a fold image past the creation's static reach around the root, so this law cannot tell a travelling bound from a root-anchored one.");
    }
}
