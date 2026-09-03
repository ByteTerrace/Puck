using System.Numerics;
using Puck.World.Authoring;
using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.World.Server;

/// <summary>
/// The SDF-backed <see cref="IContactField"/> — the second provider behind the same seam the analytic
/// <see cref="WorldColliderSet"/> answers. It compiles solid screens as axis-aligned boxes and solid placements as
/// their emitted creation primitives into one <see cref="SdfProgram"/>, reads it through a fixed-point
/// <see cref="SdfFieldEvaluator"/>, and hands both to <see cref="FixedFieldContactSolver"/>, so the contact surface a
/// body solves against is the rendered geometry — smooth-union blends are solid where they are drawn.
/// </summary>
/// <remarks>
/// <para>This type owns the document half: which rows are solid, how they compile, and the read-backs
/// <c>world.collision.status</c> reports. Contact resolution itself belongs to the solver.</para>
/// <para>A solid screen's contact box is axis-aligned because the renderer only ever <c>Translate</c>s a screen slab —
/// a screen's right/up is a UV frame only, never a geometry rotation (see <see cref="SdfProgramBuilder"/>'s
/// <c>ScreenSlab</c> overload doc). Orienting a screen volume for real is a two-surface arc — render and contact must
/// both rotate together — and neither does today.</para>
/// <para>The field's ambient "up" is world <c>+Y</c> unless the world authors
/// <see cref="WorldContactRequirement.GradientDerivedUp"/>, which derives it from the field gradient instead (a
/// planetoid, an inverted ceiling, or the inside of a sphere are all walkable). Contact resolution receives the
/// body's already-resolved ambient up separately, so authored gravity may still define another walkability axis.</para>
/// <para>Immutable and per-revision: it holds no per-body state, so one instance is shared by reference across all 128
/// bodies and installing a rebuild is a single reference swap on <see cref="WorldServer"/>. The wrapped
/// <see cref="SdfFieldEvaluator"/> holds only a managed <c>CompiledInstruction[]</c>, so a replaced instance needs no
/// disposal.</para>
/// <para>The "which op can be solid" ceiling is <see cref="SdfFieldEvaluator"/>'s warp-free excluded-op set:
/// <see cref="TryBuild"/> forwards the constructor's <see cref="ArgumentException"/> message verbatim as its reject
/// reason, so <see cref="WorldServer"/> turns an unsupported solid into a loud apply-time rejection instead of a
/// constructor throw at install time.</para>
/// </remarks>
public sealed class WorldSolidField : IContactField {
    private static readonly FixedVector3 UnitY = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );

    private readonly IFieldEvaluator m_contactField;
    private readonly SdfFieldEvaluator m_evaluator;
    private readonly FixedFieldContactSolver m_solver;
    // The hold policy per compiled material id — TryBuild adds exactly one material per solid screen and per solid
    // placement, so a probe's reported material id IS the row that composed it. A material id outside these (the
    // field lattice's own terrain, which no placement row owns) falls back to the world's collision.defaultHold.
    // How far back along a grip ray the surface gradient is sampled. A hit point sits ON the isosurface, where the
    // sign of the field is exactly what is in question; one contact-skin-scale step back into open space gives the
    // gradient a side to face. Small enough that no authored surface curves meaningfully across it.
    private static readonly FixedQ4816 GradientBackoff = FixedQ4816.FromDouble(value: 0.02);

    private readonly bool[] m_holdableMaterials;
    private readonly bool[] m_holdableGrantedByOverride;
    private readonly bool m_defaultGrip;

    private WorldSolidField(SdfFieldEvaluator evaluator, IFieldEvaluator contactField, int instructionCount, long placementShapeCount, WorldContactCensus census, FixedWorldCollision tuning, bool[] holdableMaterials, bool[] holdableGrantedByOverride, bool defaultGrip) {
        m_evaluator = evaluator;
        m_contactField = contactField;
        m_holdableMaterials = holdableMaterials;
        m_holdableGrantedByOverride = holdableGrantedByOverride;
        m_defaultGrip = defaultGrip;
        InstructionCount = instructionCount;
        PlacementShapeCount = placementShapeCount;
        Census = census;
        m_solver = new FixedFieldContactSolver(
            contactSkin: tuning.ContactSkin,
            field: contactField,
            gradientProbe: tuning.GradientProbe,
            gradientUp: tuning.GradientUp,
            groundedThreshold: tuning.GroundedThreshold,
            maxIterations: tuning.MaxIterations,
            query: evaluator
        );
    }

    /// <summary>Gets the analytic collider census measured from the same definition, so the read-back is comparable
    /// whichever provider the world selected.</summary>
    public WorldContactCensus Census { get; }
    /// <summary>Gets the field evaluator the <c>world.collision.probe</c> verb reads distance/material/gradient from, so the
    /// surface the simulation itself solves against is directly observable.</summary>
    public IFieldEvaluator Evaluator => m_evaluator;
    /// <summary>Gets the deterministic gameplay-query view over the same compiled solid program.</summary>
    public IWorldQuery Query => m_evaluator;
    /// <summary>Gets a value indicating whether this field's collision tuning authors <see cref="WorldContactRequirement.GradientDerivedUp"/>.</summary>
    public bool GradientUp => m_solver.GradientUp;
    /// <summary>Gets the compiled program's instruction count — the <c>world.collision.status</c> read-back (a rough size of
    /// the solid field the solver walks).</summary>
    public int InstructionCount { get; }
    /// <summary>Gets the placement primitive-shape emissions in the compiled field.</summary>
    public long PlacementShapeCount { get; }

    /// <summary>Queries the wrapped deterministic SDF evaluator for an unobstructed segment.</summary>
    /// <param name="from">The segment start.</param>
    /// <param name="to">The segment end.</param>
    /// <returns><see langword="true"/> when nothing solid lies between the two points.</returns>
    public bool LineOfSight(in FixedVector3 from, in FixedVector3 to) =>
        m_solver.LineOfSight(
            from: in from,
            to: in to
        );
    /// <summary>Reads the compiled field at a point — the <c>world.collision.probe</c> verb's observation.</summary>
    /// <param name="position">The world-space point to read.</param>
    /// <param name="distance">The signed nearest-surface distance, when the field answered.</param>
    /// <param name="material">The nearest surface's material id, when the field answered.</param>
    /// <param name="gradient">The unit-length field gradient, or zero where none exists.</param>
    /// <returns><see langword="true"/> when the field answered.</returns>
    public bool Probe(in FixedVector3 position, out FixedQ4816 distance, out int material, out FixedVector3 gradient) =>
        m_solver.Probe(
            distance: out distance,
            gradient: out gradient,
            material: out material,
            position: in position
        );
    /// <inheritdoc/>
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        m_solver.Resolve(
            orientation: in orientation,
            position: ref position,
            up: in up,
            velocity: ref velocity,
            volumes: volumes
        );
    /// <inheritdoc/>
    public ContactResolution ResolveSweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity,
        in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        m_solver.ResolveSweep(
            orientation: in orientation,
            position: ref position,
            previousPosition: in previousPosition,
            up: in up,
            velocity: ref velocity,
            volumes: volumes
        );
    /// <summary>Builds the SDF contact field from a definition without installing it, or reports the offending op by name.</summary>
    /// <param name="definition">The world definition supplying the collision tuning and solid rows.</param>
    /// <param name="built">The built field on success; <see langword="null"/> on failure.</param>
    /// <param name="reason">The forwarded <see cref="SdfFieldEvaluator"/> reject reason when a solid names an op the
    /// warp-free evaluator cannot interpret; empty on success.</param>
    /// <param name="lattice">The field lattice whose height columns union with the solids for contact, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the field compiled, <see langword="false"/> with a named reason otherwise.</returns>
    public static bool TryBuild(WorldDefinition definition, out WorldSolidField? built, out string reason, WorldFieldLattice? lattice = null) {
        built = null;
        reason = string.Empty;

        var tuning = FixedWorldCollision.Compile(collision: definition.Collision);
        var worldSeed = (definition.Generation?.WorldSeed ?? 0UL);
        var builder = new SdfProgramBuilder();
        var placementShapeCount = 0L;

        var defaultGrip = definition.Collision.DefaultHold;
        var holdableMaterials = new List<bool>();
        var holdableGrantedByOverride = new List<bool>();

        void RecordGrip(int material, bool holdable, bool grantedByOverride) {
            while (holdableMaterials.Count <= material) {
                holdableMaterials.Add(item: defaultGrip);
                holdableGrantedByOverride.Add(item: false);
            }

            holdableMaterials[material] = holdable;
            holdableGrantedByOverride[material] = grantedByOverride;
        }

        foreach (var screen in definition.Screens) {
            if (screen.Solid is not { } solid) {
                continue;
            }

            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            // A screen carries no grip facet of its own, so only the world-level policy can admit it.
            RecordGrip(
                holdable: defaultGrip,
                grantedByOverride: false,
                material: material
            );
            // The same center derivation the frame source and picker bake: the geometry box sits one HalfDepth behind the
            // lit face along the face normal.
            var normal = Vector3.Normalize(value: Vector3.Cross(
                vector1: screen.Right,
                vector2: screen.Up
            ));
            var center = (screen.Origin - (normal * screen.HalfDepth));

            _ = builder
                .Translate(offset: center)
                .Box(
                halfExtents: new Vector3(
                    x: (screen.HalfWidth + solid.Margin),
                    y: (screen.HalfHeight + solid.Margin),
                    z: (screen.HalfDepth + solid.Margin)
                ),
                round: screen.Round,
                material: material
            )
                .ResetPoint();
        }

        foreach (var placement in definition.Placements) {
            if (
                (placement.Solid is not { } solid) ||
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.PrototypeId
            ) is not { } creation)
            ) {
                continue;
            }

            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            RecordGrip(
                holdable: (placement.Grip?.Holdable ?? defaultGrip),
                grantedByOverride: (placement.Grip is not null),
                material: material
            );
            // The one transform conversion boundary: the program is encoded single-precision, but every placement
            // transform reaching it is derived in fixed point first (yaw via integer SinCos, origins via the fixed
            // lattice, reflected frames via fixed quaternion composition) and rounded exactly to float, so every
            // machine encodes bit-identical constants — the evaluator itself stays fixed point throughout.
            var fixedRotation = FixedQuaternion.FromAxisAngle(
                axis: UnitY,
                angle: FixedQ4816.FromDouble(value: (placement.YawDegrees * (Math.PI / 180.0)))
            );

            // A creation whose parts carve each other is one candidate against the solid field, never a carve of it.
            // The field scope is one deep, and a nonzero contact margin already spends it per shape (dilation before
            // the authored blend), so a margined solid emits unscoped.
            var scoped = (
                (solid.Margin == 0f) &&
                (creation.Document.Shapes is { Count: > 0 }) &&
                CreationStampEmitter.ComposesInternally(document: creation.EngineDocument)
            );

            CreationStampLattice.ForEachFixedInstance(
                origin: FixedVector3.FromVector3(value: placement.Position),
                rotation: fixedRotation,
                pattern: WorldPlacementStamp.PatternFor(placement: placement),
                sampledOffsets: WorldPlacementStamp.SampledFixedOffsetsFor(placement: placement, worldSeed: worldSeed),
                mirror: WorldPlacementStamp.MirrorFor(placement: placement),
                visitor: instance => {
                    if (scoped) {
                        _ = builder.PushField(compose: SdfBlendOp.Union);
                    }

                    CreationStampEmitter.EmitFixed(
                        builder: builder,
                        document: creation.EngineDocument,
                        transform: new FixedCreationStampTransform(
                            Origin: instance.Origin,
                            Rotation: fixedRotation,
                            Scale: FixedQ4816.FromDouble(value: placement.Scale),
                            ReflectionNormal: instance.ReflectionNormal
                        ),
                        materialFor: _ => material,
                        contactMargin: solid.Margin
                    );

                    if (scoped) {
                        _ = builder.PopField();
                    }

                    placementShapeCount += (creation.Document.Shapes?.Count ?? 0);
                }
            );
        }

        var program = builder.Build(buildInstanceGrid: false);
        SdfFieldEvaluator evaluator;

        try {
            evaluator = new SdfFieldEvaluator(program: program);
        } catch (ArgumentException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }

        // A field lattice's height columns union with the authored solids for contact; sweeps and line of sight
        // still march the authored program alone.
        built = new WorldSolidField(
            evaluator: evaluator,
            contactField: ((lattice is null)
                ? evaluator
                : new WorldUnionField(
                    a: evaluator,
                    b: new WorldFieldLatticeSolid(lattice: lattice)
                )),
            instructionCount: program.Instructions.Count,
            placementShapeCount: placementShapeCount,
            census: WorldColliderSet.Measure(definition: definition),
            holdableGrantedByOverride: [.. holdableGrantedByOverride],
            holdableMaterials: [.. holdableMaterials],
            defaultGrip: defaultGrip,
            tuning: tuning
        );

        return true;
    }    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) =>
        m_solver.TryUp(
            position: in position,
            up: out up
        );
    /// <summary>Re-wraps this field's already-compiled program with fresh solver scalars, reusing the wrapped
    /// <see cref="SdfFieldEvaluator"/> (safe to share by reference — it holds only an immutable instruction array). A
    /// <c>SetCollision</c> edit touches only the collision tuning row, never the geometry the program bakes (screens and
    /// placements), so a slope/skin/probe/iteration tweak reuses the program instead of
    /// recompiling it. The result is a distinct instance (per-revision immutability) so the install-time reference swap
    /// still bumps the revision.</summary>
    /// <param name="tuning">The recompiled collision tuning to adopt.</param>
    /// <returns>A new field over the same evaluator with the new scalars.</returns>
    public WorldSolidField WithTuning(FixedWorldCollision tuning) =>
        new(
            evaluator: m_evaluator,
            contactField: m_contactField,
            instructionCount: InstructionCount,
            placementShapeCount: PlacementShapeCount,
            census: Census,
            holdableGrantedByOverride: m_holdableGrantedByOverride,
            holdableMaterials: m_holdableMaterials,
            defaultGrip: m_defaultGrip,
            tuning: tuning
        );
    /// <inheritdoc/>
    /// <remarks>The aim-assist cone is not honoured: a field has no candidate LIST to score bearings over, only the
    /// one surface its own march reaches. A caller wanting assisted aim against a field has to widen its own sweep.</remarks>
    public bool TryNearestSurfaceAlongDirection(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, FixedQ4816 assistHalfAngle, out FixedSurfaceAttachCandidate candidate) {
        _ = assistHalfAngle;

        return TryCast(
            candidate: out candidate,
            direction: in direction,
            maxDistance: maxDistance,
            origin: in origin
        );
    }
    // The one directed march both grip and anchor queries read: the first surface along the ray, with the surface
    // orientation read from the field gradient one step back into open space (a march reports WHERE, never which
    // way -- RayHit.Normal is documented zero).
    private bool TryCast(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, out FixedSurfaceAttachCandidate candidate) {
        candidate = default;

        if (!m_evaluator.Raycast(
            dir: direction,
            hit: out var hit,
            maxDist: maxDistance,
            origin: FixedPosition.FromLocal(local: origin)
        )) {
            return false;
        }

        var point = hit.Point.Local;

        if (!m_evaluator.TryFieldGradient(
            gradient: out var gradient,
            position: FixedPosition.FromLocal(local: (point - (direction.Normalize() * GradientBackoff)))
        )) {
            return false;
        }
        if (gradient == FixedVector3.Zero) {
            return false;
        }

        candidate = new FixedSurfaceAttachCandidate(
            Point: point,
            Normal: gradient,
            Distance: hit.Distance,
            Source: FixedSurfaceColliderSource.Static,
            ColliderIndex: hit.Material
        );

        return true;
    }
    /// <inheritdoc/>
    /// <remarks>A ray, not a nearest-point search: the field's own deterministic march reports the FIRST surface
    /// along the direction, which is what makes a grip immune to the nearer geometry beside it (the floor under a
    /// wall's foot, the underside of the ledge above). A hit inside geometry cannot arise from a grip that never
    /// lets itself embed, so no inside-out case is invented here.</remarks>
    public bool TryHoldableSurfaceAlongDirection(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, out FixedSurfaceAttachCandidate candidate, out bool grantedByOverride) {
        candidate = default;
        grantedByOverride = false;

        if (!TryCast(
            candidate: out candidate,
            direction: in direction,
            maxDistance: maxDistance,
            origin: in origin
        )) {
            return false;
        }
        if (!Holdable(
            grantedByOverride: out grantedByOverride,
            material: candidate.ColliderIndex
        )) {
            candidate = default;
            grantedByOverride = false;

            return false;
        }

        return true;
    }
    // The compiled material id's hold verdict — one material per solid screen and per solid placement, so an id
    // outside the recorded range is the field lattice's own terrain and falls back to the world-level policy.
    private bool Holdable(int material, out bool grantedByOverride) {
        grantedByOverride = ((material >= 0) && (material < m_holdableGrantedByOverride.Length) && m_holdableGrantedByOverride[material]);

        return (((material >= 0) && (material < m_holdableMaterials.Length))
            ? m_holdableMaterials[material]
            : m_defaultGrip
        );
    }
}
