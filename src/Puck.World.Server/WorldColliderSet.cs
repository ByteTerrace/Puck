using Puck.World.Authoring;
using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The analytic <see cref="IContactField"/> — the document-derived, fixed-point, allocation-free-at-steady-state
/// contact provider. It compiles convex colliders from solid screens and every primitive copy in a solid creation
/// placement, so grounding has one owner and the walkable slope means one
/// thing across the whole world.
/// </summary>
/// <remarks>
/// <para>A body contributes up to <see cref="WorldCollider.MaxVolumes"/> sphere, capsule, or oriented-box volumes.
/// Resolution depenetrates each from every collider, killing the velocity component driving into each resolved
/// surface. A support push whose normal's <c>+Y</c> alignment clears the compiled
/// <see cref="FixedWorldCollision.GroundedThreshold"/> grounds the body. Lateral edge approach instead reports an
/// obstruction and the full clearance a step-up needs; vertical landing retains the rounded surface normal.</para>
/// <para>Single-pass-per-iteration relaxation (up to <c>MaxIterations</c>): two adjacent solid boxes can push a body
/// back and forth within one tick. Accepted at authoring scale; the fix (push-order by penetration depth) is cheap and
/// additive. Boxes retain the drawn fillet through <see cref="FixedStaticCollider.RoundedBox"/>; chamfered Boxes
/// and Superellipsoids use their exact convex support maps, and a round capsule is the rounded box around its segment. Spheres and plain circular cylinders
/// use their closest-point geometry; rounded/chamfered circular rims use contained cores with computed error bounds.
/// A creation whose shapes carve, smooth or otherwise blend each other, or that carries relief, collides as its composed
/// field (<see cref="CollidesAsField"/>); any other finite primitive uses its oriented bounding box
/// (<see cref="FixedStaticCollider.Box"/>). A
/// solid screen still uses its world-axis bounding box, exact only for an axis-aligned slab. A sweep
/// visits only the colliders a 4 m grid over world X and Z places near its move, and rejects any whose padded world
/// bounds miss the body's before its exact query; neither cull changes the result.</para>
/// <para>A placement carrying both a solid and an attach facet compiles no static collider here — its origin/yaw are
/// not the row's authored transform, they are a live body's resolved pose. <see cref="RefreshAttached"/> recomputes
/// those colliders once per tick, before any body advances, so every body's <see cref="Resolve"/> call this tick
/// depenetrates against the same snapshot rather than repeating the recompute per body.</para>
/// </remarks>
public sealed partial class WorldColliderSet : IContactField {
    private List<FixedStaticCollider> m_attachedColliders = [];
    // The attached row each collider in m_attachedColliders came from, parallel to it.
    private List<int> m_attachedColliderRows = [];
    // The refresh before the latest one: the colliders a body that was grounded last tick stood on, and their rows.
    private List<FixedStaticCollider> m_previousAttachedColliders = [];
    private List<int> m_previousAttachedColliderRows = [];

    // Each attached row's resolved origin at the latest refresh and at the one before it, and whether the row resolved
    // at each: the difference is the displacement a body standing on the row rides (TryRideDisplacement).
    private FixedVector3[] m_attachedOrigins;
    private FixedVector3[] m_previousAttachedOrigins;
    private FixedQuaternion[] m_attachedRotations;
    private FixedQuaternion[] m_previousAttachedRotations;
    private bool[] m_attachedResolved;
    private bool[] m_previousAttachedResolved;

    // Every ATTACHED solid row (Attach + Solid both set — refused only under the FIELD provider, see the validator):
    // its geometry cannot join m_colliders above because its origin/yaw are not the row's static authored transform,
    // they are a live body's pose. RefreshAttached recomputes m_attachedColliders from these once per tick. Attached
    // Hold queries retain each moving row's authored grip policy beside the sampled colliders.
    private sealed record MovingSolidRow(WorldPlacement Placement, WorldPrototype Creation, FixedVector3 Origin,
        FixedQuaternion Rotation, FixedVector3? ReflectionNormal) {
        public StateBinding? SizeBinding { get; } = StateBinding.Parse(token: Creation.Document.SizeBy);
        public CellName SizeKey { get; } = CellName.Parse(candidate: ((StateBinding.Parse(token: Creation.Document.SizeBy)?.Key is StateBinding.BodyKey)
            ? IndexKeyCache.Get(index: (Placement.Attach!.BodyIndex ?? 0)) : (StateBinding.Parse(token: Creation.Document.SizeBy)?.Key ?? StateRow.SlotKey.Value)));

        public StateCatalog? SizeCatalog;
        public StateHandle SizeHandle;
    }

    private readonly IReadOnlyList<MovingSolidRow> m_attachedRows;

    private FixedQ4816[] m_attachedScales;
    private FixedQ4816[] m_previousAttachedScales;

    private readonly WorldSolidScale[] m_sampledScales;
    private readonly int[] m_scaleSlots;
    private readonly FixedStaticCollider[] m_colliders;
    // m_colliders with their bounds and grid, compiled once: what every solve runs over, and the static geometry
    // navigation plans against (StaticField).
    private readonly FixedStaticContactField m_static;

    // m_attachedColliders with each one's world bounds, published from it at the end of every refresh or restore that
    // rebuilds it, which are the only writers between solves.
    private readonly FixedStaticColliderSet m_attachedSet = new();
    private readonly FixedStaticColliderSet m_rideSet = new();
    private readonly List<FixedStaticCollider> m_rideColliders = [];
    private int m_rideExcludedRow = -1;

    private FixedStaticColliderSet QueryAttachedSet => ((m_rideExcludedRow < 0) ? m_attachedSet : m_rideSet);

    private readonly FixedStaticColliderSet m_bornSupportSet = new();
    private static readonly FixedStaticColliderSet EmptyColliders = new(colliders: []);

    // During a ride only the support being displaced is omitted: its new pose may already enclose the start.
    // Every other solid, portal and neighbouring authority still participates through the body's normal sweep door.
    internal RideSweep ExcludeRideSupport(int row) {
        if (m_rideExcludedRow >= 0) { throw new InvalidOperationException(message: "A ride sweep is already active."); }
        m_rideColliders.Clear();
        for (var index = 0; (index < m_attachedColliders.Count); index++) {
            if (m_attachedColliderRows[index] != row) { m_rideColliders.Add(item: m_attachedColliders[index]); }
        }
        m_rideSet.Refresh(colliders: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_rideColliders));
        m_rideExcludedRow = row;
        return new RideSweep(owner: this);
    }

    internal readonly ref struct RideSweep(WorldColliderSet owner) {
        public void Dispose() => owner.m_rideExcludedRow = -1;
    }

    public IReadOnlyList<WorldDiagnosticCollider> DiagnosticColliders { get; private set; } = [];

    // The holdable subset of m_colliders, filtered at build time rather than the full array gated after the fact:
    // filtering first is the difference between "the nearest holdable surface along the probe" and "the nearest
    // surface along the probe, if it is holdable", and the latter lets an ordinary floor shadow every holdable wall
    // beside it. Parallel to m_holdableGrantedByOverride.
    private readonly FixedStaticCollider[] m_holdableColliders;
    // Whether a per-placement WorldPlacementGrip override (rather than the world's own DefaultGrip) is what made
    // the collider at the same index in m_holdableColliders holdable — the read-back's "which policy layer granted
    // it" fact.
    private readonly bool[] m_holdableGrantedByOverride;
    private readonly bool m_defaultGrip;

    private readonly List<FixedStaticCollider> m_movingHoldable = [];
    private readonly List<bool> m_movingGripOverrides = [];

    internal bool MovingHoldable(int row, out bool grantedByOverride) {
        var grip = m_attachedRows[row].Placement.Grip;

        grantedByOverride = (grip is not null);
        return (grip?.Holdable ?? m_defaultGrip);
    }

    private readonly FixedStaticContactSolver m_solver;

    // A running fold of the last-refreshed m_attachedColliders, so RefreshAttached can tell whether the carrier
    // actually moved without keeping a second snapshot array. AttachedRevision only advances when the fold changes —
    // never on every call — so a world with no moving attached row (the common case) never wakes a sleeping body.
    private ulong m_attachedHash;
    // The box every attached row that moved since the last TakeAttachedChange swept through: its colliders before and
    // after each move. A sleeping body outside it cannot meet any collider that moved, so a moving carrier wakes only
    // what it nears.
    private WorldColliderBox m_attachedMoves;

    private WorldColliderSet(FixedStaticCollider[] colliders, FixedStaticCollider[] holdableColliders, bool[] holdableGrantedByOverride, FixedWorldCollision tuning, IReadOnlyList<MovingSolidRow> attachedRows, bool movingOnly, bool defaultGrip, FixedStaticContactField? previousStatic = null) {
        m_defaultGrip = defaultGrip;
        m_colliders = colliders;
        m_holdableColliders = holdableColliders;
        m_holdableGrantedByOverride = holdableGrantedByOverride;
        m_attachedRows = attachedRows;
        var placements = attachedRows.Select(selector: row => row.Placement.Id.ToString()).Distinct(comparer: StringComparer.Ordinal).ToArray();

        m_sampledScales = placements.Select(selector: id => new WorldSolidScale(Placement: id, Scale: FixedQ4816.Zero)).ToArray();
        m_scaleSlots = attachedRows.Select(selector: row => Array.IndexOf(array: placements, value: row.Placement.Id.ToString())).ToArray();
        m_attachedFields = new FixedStaticCollider?[attachedRows.Count];
        m_fieldContactRows = new bool[attachedRows.Count];
        m_scaledAttachedFields = new FixedStaticCollider?[attachedRows.Count];
        m_scaledAttachedSizes = new FixedQ4816[attachedRows.Count];
        m_liveFields = new FixedStaticCollider?[attachedRows.Count];
        for (var row = 0; (row < attachedRows.Count); row++) {
            var (placement, creation, _, rotation, reflection) = attachedRows[row];
            m_fieldContactRows[row] = (movingOnly || CollidesAsField(document: creation.EngineDocument));
            m_attachedFields[row] = ReliefCollider(creation.EngineDocument,
                new FixedCreationStampTransform(FixedVector3.Zero, FixedQuaternion.Identity, FixedQ4816.FromDouble(value: placement.Scale), ((reflection is { } normal) ? rotation.RotateInverse(vector: normal) : null)),
                placement.Solid!.Margin, partitioned: false);
        }
        m_attachedVisitor = RecordAttachedCopy;
        m_attachedScales = new FixedQ4816[attachedRows.Count];
        m_previousAttachedScales = new FixedQ4816[attachedRows.Count];
        m_attachedOrigins = new FixedVector3[attachedRows.Count];
        m_previousAttachedOrigins = new FixedVector3[attachedRows.Count];
        m_attachedRotations = new FixedQuaternion[attachedRows.Count];
        m_previousAttachedRotations = new FixedQuaternion[attachedRows.Count];
        m_attachedResolved = new bool[attachedRows.Count];
        m_previousAttachedResolved = new bool[attachedRows.Count];
        m_solver = new FixedStaticContactSolver(
            ContactSkin: tuning.ContactSkin,
            GroundedThreshold: tuning.GroundedThreshold,
            MaxIterations: tuning.MaxIterations
        );
        m_static = (((previousStatic is not null) && (previousStatic.Solver == m_solver))
            ? previousStatic
            : new FixedStaticContactField(colliders: colliders, solver: m_solver));
        ContactReach = tuning.ContactReach;
    }

    /// <summary>Gets the static colliders alone as a contact field: what a body collides with here apart from the
    /// attached solids that move each tick. It never changes and may be read from any thread, so navigation bakes
    /// against it off the authority's tick.</summary>
    public FixedStaticContactField StaticField => m_static;

    /// <summary>Gets a counter that advances whenever <see cref="RefreshAttached"/> finds an attached solid row at a
    /// different resolved pose than the snapshot it replaces — never on a call that leaves every collider unchanged.
    /// A sleeping body compares this against the value it last observed to decide whether a moving carrier woke it.</summary>
    public ulong AttachedRevision { get; private set; }

    /// <summary>Gets how far past a body's collider surface a collider of this set still acts on the body, in metres:
    /// the tuning's <see cref="FixedWorldCollision.ContactReach"/>, the reach of a carrier's support
    /// (<see cref="TryRideDisplacement"/>).</summary>
    internal FixedQ4816 ContactReach { get; }

    /// <summary>Gets the number of solid boxes in the set.</summary>
    public int BoxCount { get; private init; }
    /// <summary>Gets the number of solid circular cylinders in the set.</summary>
    public int CylinderCount { get; private init; }
    /// <summary>Gets the number of placement-derived boxes in the set.</summary>
    public int PlacementBoxCount { get; private init; }
    /// <summary>Gets the number of placement-derived circular cylinders in the set.</summary>
    public int PlacementCylinderCount { get; private init; }
    /// <summary>Gets the number of placement-derived half-spaces in the set.</summary>
    public int PlacementPlaneCount { get; private init; }
    /// <summary>Gets the number of placement-derived spheres in the set.</summary>
    public int PlacementSphereCount { get; private init; }
    /// <summary>Gets the number of solid half-spaces in the set.</summary>
    public int PlaneCount { get; private init; }
    /// <summary>Gets the total collider count.</summary>
    public int SolidCount => ((((SphereCount + BoxCount) + CylinderCount) + PlaneCount) + FieldCount);
    /// <summary>Gets the number of field colliders in the set: one per stamped copy of a creation that collides as its composed field (<see cref="CollidesAsField"/>).</summary>
    public int FieldCount { get; private init; }
    /// <summary>Gets the number of solid spheres in the set.</summary>
    public int SphereCount { get; private init; }

    /// <summary>Measures the analytic collider vocabulary without materializing the collider array.</summary>
    /// <param name="definition">The live world definition.</param>
    /// <returns>The screen and placement contribution.</returns>
    internal static WorldContactCensus Measure(WorldDefinition definition) {
        var worldSeed = (definition.Generation?.WorldSeed ?? 0UL);
        var spheres = 0L;
        var boxes = 0L;
        var planes = 0L;
        var placementSpheres = 0L;
        var placementBoxes = 0L;
        var placementCylinders = 0L;
        var placementPlanes = 0L;
        var placementFields = 0L;
        var unsupported = 0L;

        foreach (var screen in definition.Screens) {
            if (screen.Solid is not null) {
                boxes++;
            }
        }

        foreach (var placement in definition.Placements) {
            if (
                (placement.Solid is null) ||
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.ShownPrototypeId
            ) is not { } creation)
            ) {
                continue;
            }

            var copies = WorldPlacementStamp.Resolve(definition, placement, worldSeed: worldSeed).Count;

            // A creation that collides as its composed field compiles one field collider per copy that keeps a contact shape.
            if (CollidesAsField(document: creation.EngineDocument)) {
                if (CreationStampEmitter.EmitsContact(document: creation.EngineDocument)) {
                    placementFields = BinaryIntegerFunctions.AddSaturating(left: placementFields, right: copies);
                }

                continue;
            }

            foreach (var shape in (creation.Document.Shapes ?? [])) {
                if (!CreationStampEmitter.EmitsContact(shape: shape)) {
                    continue;
                }

                // A shape carrying domain ops compiles one collider PER EXPANDED COPY, exactly as Build emits them —
                // counting the authored shape once would under-report the census the placement budget is read against.
                var shapeCopies = (ShapeDomainOps.TryExpand(
                    domain: shape.Domain,
                    subject: shape.Position,
                    frames: out var frames,
                    refusal: out _
                )
                    ? CreationStampLattice.MultiplySaturated(
                        ceiling: long.MaxValue,
                        left: copies,
                        right: frames.Length
                    )
                    : copies
                );

                if (shape.Type == SdfSolidPrimitive.Plane) {
                    placementPlanes = BinaryIntegerFunctions.AddSaturating(
                        left: placementPlanes,
                        right: shapeCopies
                    );
                } else if (
                    (shape.Type == SdfSolidPrimitive.Sphere) &&
                    CreationStampEmitter.IsIsotropicallyScaled(shape: shape)
                ) {
                    placementSpheres = BinaryIntegerFunctions.AddSaturating(
                        left: placementSpheres,
                        right: shapeCopies
                    );
                } else if ((shape.Type == SdfSolidPrimitive.Cylinder) &&
                    (MathF.Abs(x: shape.Scale.X) == MathF.Abs(x: shape.Scale.Z))) {
                    placementCylinders = BinaryIntegerFunctions.AddSaturating(left: placementCylinders, right: shapeCopies);
                } else {
                    placementBoxes = BinaryIntegerFunctions.AddSaturating(
                        left: placementBoxes,
                        right: shapeCopies
                    );
                }
            }
        }

        spheres = BinaryIntegerFunctions.AddSaturating(
            left: spheres,
            right: placementSpheres
        );
        boxes = BinaryIntegerFunctions.AddSaturating(
            left: boxes,
            right: placementBoxes
        );
        planes = BinaryIntegerFunctions.AddSaturating(
            left: planes,
            right: placementPlanes
        );

        return new WorldContactCensus(
            BoxCount: boxes,
            CylinderCount: placementCylinders,
            PlacementBoxCount: placementBoxes,
            PlacementCylinderCount: placementCylinders,
            PlacementPlaneCount: placementPlanes,
            PlacementSphereCount: placementSpheres,
            PlaneCount: planes,
            SphereCount: spheres,
            UnsupportedPlacementCount: unsupported
        ) { PlacementFieldCount = placementFields };
    }

    private static void FoldCollider(in FixedStaticCollider collider, ref Fnv1aHash hash) {
        hash.Add(value: ((byte)collider.Kind));
        hash.Add(value: collider.Center.X.Value);
        hash.Add(value: collider.Center.Y.Value);
        hash.Add(value: collider.Center.Z.Value);
        hash.Add(value: collider.Extent.X.Value);
        hash.Add(value: collider.Extent.Y.Value);
        hash.Add(value: collider.Extent.Z.Value);
        hash.Add(value: collider.Radius.Value);
        hash.Add(value: collider.Chamfer.Value);
        hash.Add(value: collider.Exponent.Value);
        hash.Add(value: collider.Rotation.X.Value);
        hash.Add(value: collider.Rotation.Y.Value);
        hash.Add(value: collider.Rotation.Z.Value);
        hash.Add(value: collider.Rotation.W.Value);
    }

    // Boxes retain their authored rounding (sharp without one), and circular cylinders and ellipsoids their surface; other
    // finite primitives retain a bound.
    private static readonly FixedQ4816 SqrtTwo = FixedQ4816.Sqrt(value: FixedQ4816.FromInteger(value: 2));
    private static readonly FixedQ4816 BevelFactor = (FixedQ4816.FromInteger(value: 2) - SqrtTwo);
    // A capsule's half segment per unit of its scale.y: the segment is 0.5·scale.y long.
    private static readonly FixedQ4816 CapsuleHalfSegment = FixedQ4816.FromDouble(value: 0.25);

    // The one collider a solid row compiles for a creation's primitive copy, static and attached rows alike: a plane a
    // half-space, an exponent-2 ellipsoid-family shape scaled alike on every axis a sphere, anything else its finite
    // collider; each grown by the margin.
    private static FixedStaticCollider CopyCollider(in FixedCreationStampPrimitiveCopy copy, FixedQ4816 margin) {
        if (ShapePointOps.DeformsContact(shape: copy.Shape) && (copy.Shape.Type != SdfSolidPrimitive.Plane)) {
            return FixedStaticCollider.Box(center: copy.Center, halfExtents: (copy.LocalHalfExtents + new FixedVector3(X: margin, Y: margin, Z: margin)), rotation: copy.Rotation);
        }
        if (copy.Shape.Type == SdfSolidPrimitive.Plane) {
            var normal = copy.PlaneNormal;

            return FixedStaticCollider.HalfSpace(point: (copy.Center + (normal * margin)), normal: normal);
        }
        if (IsSphereCopy(copy: copy)) {
            var sphereBounds = SdfSolidGeometry.GetLocalBounds(type: copy.Shape.Type);

            return FixedStaticCollider.Sphere(center: copy.Center, radius: ((FixedQ4816.FromDouble(value: sphereBounds.HalfExtents.X) * copy.UniformScale) + margin));
        }

        return PlacementFinite(copy: copy, margin: margin);
    }
    // An exponent-2 member of the ellipsoid family scaled alike on every axis: its field is a sphere's distance.
    private static bool IsSphereCopy(in FixedCreationStampPrimitiveCopy copy) =>
        ((copy.UniformScale > FixedQ4816.Zero) && (EllipsoidExponent(copy: in copy) == SdfProgramBuilder.MinSuperellipsoidExponent));
    // The exponent a Sphere, Ellipsoid or Superellipsoid emits with (a sphere and an ellipsoid emit the exponent-2 shape),
    // or null for any other primitive.
    private static float? EllipsoidExponent(in FixedCreationStampPrimitiveCopy copy) => copy.Shape.Type switch {
        SdfSolidPrimitive.Sphere or SdfSolidPrimitive.Ellipsoid => SdfProgramBuilder.MinSuperellipsoidExponent,
        SdfSolidPrimitive.Superellipsoid => ShapeDocument.EffectiveExponent(exponent: copy.Shape.Exponent),
        _ => null,
    };
    // Whether a copy's contact field is its spheroid's true distance: the one predicate that chooses the revolved ellipse
    // the field emits (SdfSolidGeometry.IsSpheroid), so the copy's exact collider (ExactCollider) and its reach (CopyReach)
    // follow the field. A deformed copy collides as its composed field.
    private static bool IsSpheroidCopy(in FixedCreationStampPrimitiveCopy copy) =>
        ((EllipsoidExponent(copy: in copy) is { } exponent) && !ShapePointOps.DeformsContact(shape: copy.Shape) &&
        SdfSolidGeometry.IsSpheroid(exponent: exponent, scale: copy.Shape.Scale, type: copy.Shape.Type));
    // A compiled collider with the authored source the diagnostics name it by.
    private static WorldDiagnosticCollider DiagnosticOf(in FixedStaticCollider collider, in FixedCreationStampPrimitiveCopy copy, WorldPlacement placement, WorldPrototype creation) =>
        new(collider, placement.Id.ToString(), creation.Id.ToString(),
            (copy.Shape.Name?.ToString() ?? copy.Shape.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)),
            $"{copy.Shape.Type}/{copy.Shape.Blend}", placement.Solid!.Margin, !creation.EngineDocument.IsGlassShape(shape: copy.Shape)) {
            ConservativeInset = ((collider.Kind == FixedStaticColliderKind.Cylinder) ? (double)CylinderInset(copy: copy).Length : 0),
        };
    private static FixedStaticCollider PlacementFinite(in FixedCreationStampPrimitiveCopy copy, FixedQ4816 margin) {
        if (copy.Shape.Type == SdfSolidPrimitive.Box) {
            var stampScale = copy.StampScale;
            // The drawn box insets its rounding by at most its smallest half-extent (SdfSolidGeometry's Box arms).
            var smallest = FixedQ4816.Min(x: copy.LocalHalfExtents.X, y: FixedQ4816.Min(x: copy.LocalHalfExtents.Y, y: copy.LocalHalfExtents.Z));
            var radius = FixedQ4816.Min(x: (FixedQ4816.FromDouble(value: (copy.Shape.Rounding ?? 0f)) * stampScale), y: smallest);
            var chamfer = (FixedQ4816.FromDouble(value: (copy.Shape.Chamfer ?? 0f)) * stampScale);
            var inner = FixedVector3.Max(left: FixedVector3.Zero, right: (copy.LocalHalfExtents - new FixedVector3(X: radius, Y: radius, Z: radius)));

            if (chamfer > FixedQ4816.Zero) {
                // Match SdfProgramBuilder.ChamferedRectangle's erosion, including the cap/profile corner.
                var insetChamfer = FixedQ4816.Max(x: FixedQ4816.Zero, y: (chamfer - (radius * BevelFactor)));

                return FixedStaticCollider.ChamferedBox(copy.Center, copy.Rotation, inner, insetChamfer, (radius + margin));
            }
            return FixedStaticCollider.RoundedBox(copy.Center, copy.Rotation, inner, (radius + margin));
        }
        if ((copy.Shape.Type == SdfSolidPrimitive.Capsule) && (MathF.Abs(x: copy.Shape.Scale.X) == MathF.Abs(x: copy.Shape.Scale.Z))) {
            // A capsule is a rounded box whose inner box is its segment, which runs 0.5·scale.y from the shape's position
            // along its own Y (SdfSolidGeometry's unit law): scale.y stretches the segment, never the radius. The copy's
            // centre is its box's (SdfSolidGeometry.LocalBox), the segment's midpoint, and its X half-extent the radius.
            var segmentScale = FixedQ4816.Max(x: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: copy.Shape.Scale.Y)),
                y: FixedQ4816.FromDouble(value: SdfSolidGeometry.MinimumScale));
            var halfSegment = ((segmentScale * copy.StampScale) * CapsuleHalfSegment);

            return FixedStaticCollider.RoundedBox(
                copy.Center,
                copy.Rotation,
                new FixedVector3(X: FixedQ4816.Zero, Y: halfSegment, Z: FixedQ4816.Zero),
                (copy.LocalHalfExtents.X + margin)
            );
        }
        if (EllipsoidExponent(copy: in copy) is { } exponent) {
            // Its radii swept by the margin. At exponent two the collider measures the ellipsoid's true distance, which is
            // a spheroid's field (IsSpheroidCopy) and a triaxial ellipsoid's surface, whose gauge field dilates farther
            // along its long axes under a margin; a higher exponent's distance is its gauge.
            return FixedStaticCollider.Superellipsoid(copy.Center, copy.Rotation, copy.LocalHalfExtents, FixedQ4816.FromDouble(value: exponent)) with { Radius = margin };
        }
        if ((copy.Shape.Type == SdfSolidPrimitive.Cylinder) && (copy.LocalHalfExtents.X == copy.LocalHalfExtents.Z)) {
            var inset = CylinderInset(copy: copy);

            return FixedStaticCollider.Cylinder(center: copy.Center, radius: ((copy.LocalHalfExtents.X - inset.X) + margin),
                halfHeight: ((copy.LocalHalfExtents.Y - inset.Y) + margin), rotation: copy.Rotation);
        }
        return FixedStaticCollider.Box(
            center: copy.Center,
            halfExtents: (copy.LocalHalfExtents + new FixedVector3(
                X: margin,
                Y: margin,
                Z: margin
            )),
            rotation: copy.Rotation
        );
    }
    // A rim without an exact cylinder contact uses its contained core. The maximum missing distance follows
    // from the two radial/height contractions and the authored enclosing cylinder, not a shape-name allowance.
    private static FixedVector2 CylinderInset(in FixedCreationStampPrimitiveCopy copy) {
        var scale = copy.StampScale;
        var chamfer = (FixedQ4816.FromDouble(value: (copy.Shape.Chamfer ?? 0f)) * scale);
        var rounding = (FixedQ4816.FromDouble(value: (copy.Shape.Rounding ?? 0f)) * scale);

        return ((chamfer > FixedQ4816.Zero) ? new FixedVector2(X: chamfer, Y: FixedQ4816.Zero) : new FixedVector2(X: rounding, Y: rounding));
    }
    // The axis-aligned bounding box of a screen slab's oriented frame: the geometry center sits one HalfDepth behind the
    // front-face Origin along the face normal, and each world-axis half-extent is the |projection| of the three oriented
    // axes. Exact for the axis-aligned screens the built-in world ships; conservative (bounding) for a rotated slab.
    // The authored Right/Up are used as the box axes directly, which is sound only because they are orthogonal: the
    // validator refuses a skewed frame (WorldDefinitionValidator's screen basis rule), so this triple and the
    // orthonormal rotation the client stamps the slab with are the same frame. Relaxing that rule would make this
    // collider and the rendered slab different solids.
    private static (FixedVector3 Center, FixedVector3 HalfExtents) ScreenBox(WorldScreen screen) {
        var normal = FixedVector3.Cross(
            left: FixedVector3.FromVector3(value: screen.Right),
            right: FixedVector3.FromVector3(value: screen.Up)
        ).Normalize();
        var right = FixedVector3.FromVector3(value: screen.Right).Normalize();
        var up = FixedVector3.FromVector3(value: screen.Up).Normalize();
        var halfWidth = FixedQ4816.FromDouble(value: screen.HalfWidth);
        var halfHeight = FixedQ4816.FromDouble(value: screen.HalfHeight);
        var halfDepth = FixedQ4816.FromDouble(value: screen.HalfDepth);
        var center = (FixedVector3.FromVector3(value: screen.Origin) - (normal * halfDepth));
        var half = new FixedVector3(
            X: (((FixedQ4816.Abs(value: right.X) * halfWidth) + (FixedQ4816.Abs(value: up.X) * halfHeight)) + (FixedQ4816.Abs(value: normal.X) * halfDepth)),
            Y: (((FixedQ4816.Abs(value: right.Y) * halfWidth) + (FixedQ4816.Abs(value: up.Y) * halfHeight)) + (FixedQ4816.Abs(value: normal.Y) * halfDepth)),
            Z: (((FixedQ4816.Abs(value: right.Z) * halfWidth) + (FixedQ4816.Abs(value: up.Z) * halfHeight)) + (FixedQ4816.Abs(value: normal.Z) * halfDepth))
        );

        return (Center: center, HalfExtents: half);
    }

    /// <summary>Builds the analytic contact field from a definition.</summary>
    /// <param name="definition">The world definition supplying the collision tuning and solid rows.</param>
    /// <param name="previous">A set built from an earlier definition, whose colliders for each row that stands as it
    /// was (the same row, showing the same creation at the same resolved frame) are taken rather than compiled again, or
    /// <see langword="null"/> to compile every row.</param>
    /// <returns>The analytic field.</returns>
    /// <param name="movingOnly">Builds only moving roots as composed fields for the global field provider.</param>
    /// <param name="previousDefinition">The document the previous set was compiled or carried over against.</param>
    public static WorldColliderSet Build(WorldDefinition definition, WorldColliderSet? previous = null, bool movingOnly = false, WorldDefinition? previousDefinition = null) {
        var collision = definition.Collision;
        var worldSeed = (definition.Generation?.WorldSeed ?? 0UL);

        var tuning = FixedWorldCollision.Compile(collision: collision);
        var colliders = new List<FixedStaticCollider>();
        var diagnosticColliders = new List<WorldDiagnosticCollider>();
        var holdableColliders = new List<FixedStaticCollider>();
        var holdableGrantedByOverride = new List<bool>();
        var worldDefaultGrip = definition.Collision.DefaultHold;
        var attachedRows = new List<MovingSolidRow>();
        var placementsCompiled = new Dictionary<WorldPlacement, PlacementColliders>(comparer: ReferenceEqualityComparer.Instance);
        var boxes = 0;
        var placementSpheres = 0;
        var placementBoxes = 0;
        var placementCylinders = 0;
        var placementPlanes = 0;
        var fields = 0;
        var holdingMove = previousDefinition is not null && !definition.HoldingsFrom(previousDefinition).IsEmpty;

        foreach (var screen in definition.Screens) {
            if (movingOnly || (screen.Solid is not { } solid)) {
                continue;
            }

            var rowMargin = FixedQ4816.FromDouble(value: solid.Margin);

            var (center, halfExtents) = ScreenBox(screen: screen);
            var screenCollider = FixedStaticCollider.AxisAlignedBox(
                center: center,
                halfExtents: (halfExtents + new FixedVector3(
                    X: rowMargin,
                    Y: rowMargin,
                    Z: rowMargin
                ))
            );

            colliders.Add(item: screenCollider);
            diagnosticColliders.Add(item: new WorldDiagnosticCollider(screenCollider, $"screen:{screen.Index}", "screen", "slab", "box", solid.Margin));
            // A screen carries no grip facet — its collider always follows the world default, never an override.
            if (worldDefaultGrip) {
                holdableColliders.Add(item: screenCollider);
                holdableGrantedByOverride.Add(item: false);
            }
            boxes++;
        }

        for (var row = 0; row < definition.Placements.Count; row++) {
            var placement = definition.Placements[row];
            // A dealt template collides with nothing itself; its solid facet is what its dealt children carry.
            if (
                (placement.Solid is null) ||
                (placement.Deal is not null) ||
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.ShownPrototypeId
            ) is not { } creation)
            ) {
                continue;
            }

            // An ATTACHED solid row's authored Position/YawDegrees are inert (see WorldPlacement's own doc) — its
            // geometry rides a live body instead, recomputed every tick by RefreshAttached rather than compiled here
            // once. Collected separately; never folded into the static m_colliders array.
            if ((placement.Attach is not null) || (creation.Document.SizeBy is not null)) {
                if (placement.Attach is not null) {
                    attachedRows.Add(item: new MovingSolidRow(placement, creation, default, FixedQuaternion.Identity, null));
                } else {
                    var movingStamp = WorldPlacementStamp.Resolve(definition, placement, worldSeed: worldSeed);

                    foreach (var instance in movingStamp.Instances) {
                        attachedRows.Add(item: new MovingSolidRow(placement, creation, instance.Origin, movingStamp.FixedRotation, instance.ReflectionNormal));
                    }
                }
                continue;
            }

            if (movingOnly) { continue; }
            var resolvedFrame = WorldDefinitionRows.ResolvedFrame(
                definition: definition,
                placement: placement
            );

            var stamp = WorldPlacementStamp.Resolve(definition, placement, worldSeed: worldSeed);

            var prior = holdingMove ? previousDefinition!.Placements[row] : placement;
            var compiled = previous?.m_placements.GetValueOrDefault(prior);
            if (compiled is not null && !ReferenceEquals(compiled.Creation, creation)) {
                compiled = compiled.Shows?.GetValueOrDefault(creation);
            }
            if (
                (compiled is null) ||
                !ReferenceEquals(objA: compiled.Stamp, objB: stamp) ||
                (compiled.Frame != resolvedFrame) ||
                (compiled.WorldSeed != worldSeed) ||
                (compiled.DefaultGrip != worldDefaultGrip)
            ) {
                WorldBootWork.Count(kind: WorldBootWork.ColliderPlacementsCompiled);
                compiled = CompilePlacement(
                    creation: creation,
                    defaultGrip: worldDefaultGrip,
                    frame: resolvedFrame,
                    placement: placement,
                    stamp: stamp,
                    worldSeed: worldSeed
                );

                if (placement.Respond is { Count: > 0 } responses) {
                    compiled = CompileShows(
                        creations: definition.Creations,
                        placement: placement,
                        responses: responses,
                        shown: compiled
                    );
                }
            } else {
                WorldBootWork.Count(kind: WorldBootWork.ColliderPlacementsReused);
            }

            placementsCompiled[placement] = compiled;
            colliders.AddRange(collection: compiled.Colliders);
            diagnosticColliders.AddRange(collection: compiled.Diagnostics);
            if (compiled.Holdable) {
                holdableColliders.AddRange(collection: compiled.Colliders);
                for (var index = 0; (index < compiled.Colliders.Length); index++) {
                    holdableGrantedByOverride.Add(item: compiled.HoldableIsOverride);
                }
            }
            boxes += compiled.Boxes;
            placementSpheres += compiled.Spheres;
            placementBoxes += compiled.Boxes;
            placementCylinders += compiled.Cylinders;
            placementPlanes += compiled.Planes;
            fields += compiled.Fields;
        }

        var reuseStatic = (((previous is not null) &&
            (colliders.Count == previous.m_colliders.Length) &&
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: colliders).SequenceEqual(other: previous.m_colliders))
                ? previous.m_static
                : null);

        return new WorldColliderSet(
            holdableColliders: holdableColliders.ToArray(),
            holdableGrantedByOverride: holdableGrantedByOverride.ToArray(),
            colliders: colliders.ToArray(),
            tuning: tuning,
            attachedRows: attachedRows, movingOnly: movingOnly, defaultGrip: worldDefaultGrip,
            previousStatic: reuseStatic
        ) {
            DiagnosticColliders = diagnosticColliders.ToArray(),
            BoxCount = boxes,
            CylinderCount = placementCylinders,
            PlacementBoxCount = placementBoxes,
            PlacementCylinderCount = placementCylinders,
            PlacementPlaneCount = placementPlanes,
            PlacementSphereCount = placementSpheres,
            PlaneCount = placementPlanes,
            SphereCount = placementSpheres,
            FieldCount = fields,
            m_placements = placementsCompiled,
        };
    }

    // One static solid row's colliders, compiled from the creation it shows at its resolved frame, with the inputs they
    // were compiled from. A responding row also holds the colliders of every creation it can show, compiled with it at
    // the same frame (Shows, keyed by creation and holding this entry too), so a response change compiles nothing.
    private sealed record PlacementColliders(WorldPrototype Creation, CompiledPlacementFrame Frame, ulong WorldSeed, bool DefaultGrip,
        CompiledPlacementStamp Stamp,
        FixedStaticCollider[] Colliders, WorldDiagnosticCollider[] Diagnostics, bool Holdable, bool HoldableIsOverride, int Spheres,
        int Boxes, int Cylinders, int Planes, IReadOnlyDictionary<WorldPrototype, PlacementColliders>? Shows = null, int Fields = 0);

    // Compiles a responding row's colliders for its authored creation and every response entry's, beside the one it
    // shows, and returns the shown entry holding them all.
    private static PlacementColliders CompileShows(IReadOnlyList<WorldPrototype> creations, WorldPlacement placement, IReadOnlyList<WorldPlacementResponse> responses, PlacementColliders shown) {
        var shows = new Dictionary<WorldPrototype, PlacementColliders>(comparer: ReferenceEqualityComparer.Instance);

        for (var index = -1; (index < responses.Count); index++) {
            var id = ((index < 0) ? placement.PrototypeId : responses[index]?.PrototypeId);

            if (
                (id is null) ||
                (WorldDefinitionRows.FindCreation(creations: creations, id: id) is not { } creation) ||
                shows.ContainsKey(key: creation)
            ) {
                continue;
            }

            shows[creation] = (ReferenceEquals(objA: creation, objB: shown.Creation)
                ? shown
                : CompilePlacement(
                    creation: creation,
                    defaultGrip: shown.DefaultGrip,
                    frame: shown.Frame,
                    placement: placement,
                    stamp: shown.Stamp,
                    worldSeed: shown.WorldSeed
                )
            );
        }

        foreach (var creation in shows.Keys.ToArray()) {
            shows[creation] = (shows[creation] with { Shows = shows });
        }

        return (shows.TryGetValue(key: shown.Creation, value: out var entry)
            ? entry
            : (shown with { Shows = shows }));
    }

    // The static solid rows this set compiled, by row, so a set built from a later definition takes the rows that stand.
    private Dictionary<WorldPlacement, PlacementColliders> m_placements = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>Takes a response change into this set in place when it moves no collider: each changed row now shows
    /// another creation, and when that creation compiles, at the row's frame, to exactly the colliders and grip the
    /// row had, the set keeps every collider and only names the new creation in its diagnostics.</summary>
    /// <param name="previous">The definition the set was last built or carried over.</param>
    /// <param name="definition">The definition the response change installed: the same rows, with the changed ones
    /// showing other creations.</param>
    /// <param name="rows">The indices of the rows whose shown creation the change moved.</param>
    /// <returns><see langword="true"/> when the set now stands for <paramref name="definition"/>; <see langword="false"/>
    /// when a changed row's colliders differ (or it is attached), so the set must be built again, and nothing was
    /// changed.</returns>
    internal bool TryCarryAcrossHolding(WorldDefinition previous, WorldDefinition definition, IReadOnlyList<int> rows) {
        List<(WorldPlacement Before, WorldPlacement After, PlacementColliders Compiled)>? carried = null;

        foreach (var row in rows) {
            var before = previous.Placements[row];
            var after = definition.Placements[row];

            // A dealt template and a row with no solid facet compile nothing here.
            if ((after.Solid is null) || (after.Deal is not null)) {
                continue;
            }
            if (
                (after.Attach is not null) ||
                !m_placements.TryGetValue(key: before, value: out var compiled) ||
                (WorldDefinitionRows.FindCreation(creations: definition.Creations, id: after.ShownPrototypeId) is not { } creation) ||
                (creation.Document.SizeBy is not null)
            ) {
                return false;
            }

            var next = (ReferenceEquals(objA: creation, objB: compiled.Creation)
                ? compiled
                : ((compiled.Shows?.GetValueOrDefault(key: creation)) ?? CompilePlacement(
                    creation: creation,
                    defaultGrip: compiled.DefaultGrip,
                    frame: compiled.Frame,
                    placement: after,
                    stamp: compiled.Stamp,
                    worldSeed: compiled.WorldSeed
                ))
            );

            if (
                (next.Holdable != compiled.Holdable) ||
                (next.HoldableIsOverride != compiled.HoldableIsOverride) ||
                !next.Colliders.AsSpan().SequenceEqual(other: compiled.Colliders)
            ) {
                return false;
            }

            (carried ??= []).Add(item: (before, after, next));
        }

        if (carried is null) {
            return true;
        }

        var diagnostics = DiagnosticColliders.ToArray();

        foreach (var (before, after, next) in carried) {
            var compiled = m_placements[before];

            _ = m_placements.Remove(key: before);
            m_placements[after] = next;

            if (compiled.Diagnostics.Length == 0) {
                continue;
            }

            var start = Array.FindIndex(array: diagnostics, match: diagnostic => ReferenceEquals(objA: diagnostic, objB: compiled.Diagnostics[0]));

            if (start >= 0) {
                next.Diagnostics.CopyTo(array: diagnostics, index: start);
            }
        }

        DiagnosticColliders = diagnostics;

        return true;
    }

    // A changed contact source can move roots of rows whose own holding did not change.
    internal static bool MovesProjectedSurfaces(WorldDefinition definition, IReadOnlyList<int> rows) {
        foreach (var projected in definition.Placements) {
            if (projected.Surface is not { } surface) { continue; }
            foreach (var row in rows) {
                if (surface.Targets.Contains(definition.Placements[row].Id, StringComparer.Ordinal)) { return true; }
            }
        }
        return false;
    }

    private static PlacementColliders CompilePlacement(WorldPlacement placement, WorldPrototype creation, CompiledPlacementFrame frame, ulong worldSeed, bool defaultGrip, CompiledPlacementStamp stamp) {
        var solid = placement.Solid!;
        var margin = FixedQ4816.FromDouble(value: solid.Margin);
        var colliders = new List<FixedStaticCollider>();
        var diagnostics = new List<WorldDiagnosticCollider>();
        var spheres = 0;
        var boxes = 0;
        var cylinders = 0;
        var planes = 0;
        var fields = 0;
        var composed = CollidesAsField(document: creation.EngineDocument);
        var rotation = stamp.FixedRotation;
        var fieldsByFrame = (composed ? new Dictionary<FixedCreationStampTransform, Puck.SignedDistance.Queries.SdfFieldEvaluator>() : null);

        stamp.ForEachFixed(visitor: instance => {
            var transform = new FixedCreationStampTransform(
                Origin: instance.Origin,
                Rotation: rotation,
                Scale: FixedQ4816.FromDouble(value: placement.Scale),
                ReflectionNormal: instance.ReflectionNormal
            );

            if (composed) {
                if (ReliefCollider(document: creation.EngineDocument, margin: solid.Margin, shared: fieldsByFrame, transform: transform) is not { } relief) {
                    return;
                }

                colliders.Add(item: relief);
                diagnostics.Add(item: new WorldDiagnosticCollider(relief, placement.Id.ToString(), creation.Id.ToString(), "relief",
                    "Field/relief", solid.Margin));
                fields++;
                return;
            }

            CreationStampEmitter.VisitFixedPrimitiveCopies(
            document: creation.EngineDocument,
            transform: transform,
            visitor: copy => {
                var placementCollider = CopyCollider(copy: copy, margin: margin);

                if (copy.Shape.Type == SdfSolidPrimitive.Plane) {
                    planes++;
                } else if (placementCollider.Kind == FixedStaticColliderKind.Sphere) {
                    spheres++;
                } else if (placementCollider.Kind == FixedStaticColliderKind.Cylinder) {
                    cylinders++;
                } else {
                    boxes++;
                }
                colliders.Add(item: placementCollider);
                diagnostics.Add(item: DiagnosticOf(collider: placementCollider, copy: copy, creation: creation, placement: placement));
            }
        );
        }
        );

        // Every collider a distribution/mirror expands this ONE row into shares its ONE grip decision — see
        // WorldPlacementGrip's own remarks.
        return new PlacementColliders(
            Creation: creation,
            Frame: frame,
            Stamp: stamp,
            WorldSeed: worldSeed,
            DefaultGrip: defaultGrip,
            Colliders: colliders.ToArray(),
            Diagnostics: diagnostics.ToArray(),
            Holdable: (placement.Grip?.Holdable ?? defaultGrip),
            HoldableIsOverride: (placement.Grip is not null),
            Spheres: spheres,
            Boxes: boxes,
            Cylinders: cylinders,
            Planes: planes,
            Fields: fields
        );
    }

    /// <summary>Recomputes every ATTACHED solid row's colliders from the live population's CURRENT body poses — the
    /// dynamic counterpart to the static loop <see cref="Build"/> already compiled once for every stationary solid
    /// row. Called exactly once per tick (<see cref="WorldPopulation.AdvanceSimulated"/>, before any body advances),
    /// so every body's <see cref="Resolve"/> this tick sees the SAME snapshot — an attached solid's carrier is read
    /// as of the END of the PREVIOUS tick, the same one-tick relationship a pushed body already has to every other
    /// body's pose. Reuses <see cref="m_attachedColliders"/>'s backing storage (<see cref="List{T}.Clear"/> keeps the
    /// capacity). A size binding is sampled from the same authority tick; its local field is reused while size holds.</summary>
    /// <param name="population">The live population supplying each attached row's target body pose — the SAME
    /// resolve <see cref="WorldPlacementAttachment.TryResolve"/> answers for <c>world.attachments</c>.</param>
    /// <param name="tick">The authority simulation tick being sampled.</param>
    /// <param name="engineTick">The engine-time boundary of that tick.</param>
    /// <param name="definition">The authority's current state projection, including any replaced arena.</param>
    /// <exception cref="ArgumentNullException"><paramref name="population"/> is <see langword="null"/>.</exception>
    public void RefreshAttached(WorldPopulation population, WorldDefinition definition, ulong tick, ulong engineTick) {
        ArgumentNullException.ThrowIfNull(argument: population);

        if (m_attachedRows.Count == 0) {
            return;
        }

        (m_previousAttachedColliders, m_attachedColliders) = (m_attachedColliders, m_previousAttachedColliders);
        (m_previousAttachedColliderRows, m_attachedColliderRows) = (m_attachedColliderRows, m_previousAttachedColliderRows);
        (m_previousAttachedScales, m_attachedScales) = (m_attachedScales, m_previousAttachedScales);
        (m_previousAttachedOrigins, m_attachedOrigins) = (m_attachedOrigins, m_previousAttachedOrigins);
        (m_previousAttachedRotations, m_attachedRotations) = (m_attachedRotations, m_previousAttachedRotations);
        (m_previousAttachedResolved, m_attachedResolved) = (m_attachedResolved, m_previousAttachedResolved);
        m_attachedColliders.Clear();
        m_attachedColliderRows.Clear();
        m_movingHoldable.Clear(); m_movingGripOverrides.Clear();
        Array.Clear(array: m_attachedResolved);
        Array.Clear(array: m_liveFields);
        RefreshAttachedRows(definition: definition, engineTick: engineTick, population: population, tick: tick);
        FoldAttachedMoves(colliders: m_previousAttachedColliders, rows: m_previousAttachedColliderRows);
        FoldAttachedMoves(colliders: m_attachedColliders, rows: m_attachedColliderRows);
        m_attachedSet.Refresh(colliders: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_attachedColliders));
    }

    /// <summary>Takes the box that every attached row moved since the previous take swept through: the bounds of its
    /// colliders before and after each move, empty when no row moved.</summary>
    /// <returns>The box; a body whose bounds miss it cannot meet any collider that moved.</returns>
    internal WorldColliderBox TakeAttachedChange() {
        var moves = m_attachedMoves;

        m_attachedMoves = default;
        return moves;
    }
    /// <summary>Grows a box over the colliders this set compiled for the given rows of the definition it stands for:
    /// a static row's colliders, and an attached row's at its latest refresh.</summary>
    /// <param name="definition">The definition this set was built or carried over.</param>
    /// <param name="rows">The indices of the placement rows to bound.</param>
    /// <param name="box">The box to grow.</param>
    internal void FoldRows(WorldDefinition definition, IReadOnlyList<int> rows, ref WorldColliderBox box) {
        foreach (var row in rows) {
            var placement = definition.Placements[row];

            if (m_placements.TryGetValue(key: placement, value: out var compiled)) {
                foreach (ref readonly var collider in compiled.Colliders.AsSpan()) { box.Fold(collider: in collider); }
            }
            for (var attached = 0; (attached < m_attachedRows.Count); attached++) {
                if (!ReferenceEquals(objA: m_attachedRows[attached].Placement, objB: placement)) { continue; }
                if (!m_attachedResolved[attached]) { box.Fold(box: FixedAabb.Everywhere); }
                for (var index = 0; (index < m_attachedColliders.Count); index++) {
                    if (m_attachedColliderRows[index] == attached) { box.Fold(collider: m_attachedColliders[index]); }
                }
            }
        }
    }

    // A row moved when it resolves at one refresh and not the other, or at another origin or rotation at both.
    private bool AttachedRowMoved(int row) => ((m_attachedResolved[row] != m_previousAttachedResolved[row]) ||
        (m_attachedResolved[row] && ((m_attachedOrigins[row] != m_previousAttachedOrigins[row]) || (m_attachedRotations[row] != m_previousAttachedRotations[row]) || (m_attachedScales[row] != m_previousAttachedScales[row]))));
    private void FoldAttachedMoves(List<FixedStaticCollider> colliders, List<int> rows) {
        for (var index = 0; (index < colliders.Count); index++) {
            if (AttachedRowMoved(row: rows[index])) { m_attachedMoves.Fold(collider: colliders[index]); }
        }
    }

    // This single-threaded refresh reuses its visitor and working fold for every attached row.
    private readonly Action<FixedCreationStampPrimitiveCopy> m_attachedVisitor;

    private Fnv1aHash m_refreshHash;
    private FixedQ4816 m_refreshMargin;
    private int m_refreshRow;

    // Immutable local composed fields; refresh only carries their fixed-point frame, never recompiles relief.
    private readonly FixedStaticCollider?[] m_attachedFields;
    private readonly bool[] m_fieldContactRows;
    private readonly FixedStaticCollider?[] m_scaledAttachedFields;
    private readonly FixedQ4816[] m_scaledAttachedSizes;

    private void RecordAttachedCopy(FixedCreationStampPrimitiveCopy copy) {
        RecordAttachedCollider(attached: CopyCollider(copy: copy, margin: m_refreshMargin));
    }
    private void RecordAttachedCollider(FixedStaticCollider attached) {
        if (attached.Field is not null) { m_liveFields[m_refreshRow] = attached; }
        m_attachedColliders.Add(item: attached);
        m_attachedColliderRows.Add(item: m_refreshRow);
        if (MovingHoldable(grantedByOverride: out var grantedByOverride, row: m_refreshRow)) {
            m_movingHoldable.Add(item: attached); m_movingGripOverrides.Add(item: grantedByOverride);
        }
        FoldCollider(collider: in attached, hash: ref m_refreshHash);
    }

    /// <summary>Returns every attached solid row its carrier resolved at the latest refresh, at the pose that refresh
    /// sampled (the one every solve this tick runs against), with the colliders compiled there and their authored
    /// sources: what a diagnostic compares with the authored solid at the same pose. A row whose carrier is inactive
    /// collides with nothing and is absent.</summary>
    /// <returns>The posed rows, in attached-row order.</returns>
    public IReadOnlyList<WorldAttachedDiagnostic> DiagnosticAttached() {
        var rows = new List<WorldAttachedDiagnostic>();

        for (var row = 0; (row < m_attachedRows.Count); row++) {
            if (!m_attachedResolved[row] || (m_attachedScales[row] <= FixedQ4816.Zero)) { continue; }

            var (placement, creation, _, _, reflection) = m_attachedRows[row];
            var margin = (FixedQ4816.FromDouble(value: placement.Solid!.Margin) * m_attachedScales[row]);
            var colliders = new List<WorldDiagnosticCollider>();
            var pose = new WorldPosedSolid(Creation: creation, Origin: m_attachedOrigins[row], Placement: placement, Rotation: m_attachedRotations[row], Scale: (FixedQ4816.FromDouble(value: placement.Scale) * m_attachedScales[row]), ReflectionNormal: reflection);

            if (m_fieldContactRows[row]) {
                if (PosedAttachedField(row, pose.Origin, pose.Rotation, m_attachedScales[row]) is { } field) {
                    // Diagnostic witnesses are world-space geometry. The live field's program stays in its local
                    // frame, but its immutable compile-pose bases cannot be used at a moved or scaled pose.
                    var bases = new List<FixedStaticCollider>();

                    CreationStampEmitter.VisitFixedPrimitiveCopies(document: creation.EngineDocument, transform: pose.Transform,
                        visitor: copy => bases.Add(item: CopyCollider(copy: copy, margin: margin)));
                    var sampled = field.Field!;

                    field = field with {
                        Field = new FixedColliderField(sampled.Field, sampled.GradientStep, sampled.Bounded) {
                            Bases = bases,
                            Bounds = sampled.Bounds,
                        },
                    };
                    colliders.Add(item: new WorldDiagnosticCollider(field, placement.Id.ToString(), creation.Id.ToString(), "relief", "Field/relief", placement.Solid!.Margin));
                }
            } else {
                CreationStampEmitter.VisitFixedPrimitiveCopies(document: creation.EngineDocument, transform: pose.Transform,
                    visitor: copy => colliders.Add(item: DiagnosticOf(collider: CopyCollider(copy: copy, margin: margin), copy: copy, creation: creation, placement: placement)));
            }
            rows.Add(item: new WorldAttachedDiagnostic(Colliders: colliders, Pose: pose));
        }

        return rows;
    }

    private void RefreshAttachedRows(WorldPopulation population, WorldDefinition definition, ulong tick, ulong engineTick) {
        m_refreshHash = Fnv1aHash.Create();
        var arena = WorldStateReader.Arena(definition: definition);
        var time = new ArenaTime(tick, engineTick, definition.SimulationRateHz, definition.Dynamics);

        for (var row = 0; (row < m_attachedRows.Count); row++) {
            var moving = m_attachedRows[row];
            var origin = moving.Origin;
            var rotation = moving.Rotation;

            if (moving.Placement.Attach is { } attach) {
                if (!WorldPlacementAttachment.TryResolve(attach: attach, population: population, position: out origin, reason: out _, yawRadians: out var yaw)) { continue; }
                rotation = FixedQuaternion.FromAxisAngle(angle: yaw, axis: FixedVector3.UnitY);
            }
            var scale = FixedQ4816.One;

            if (moving.SizeBinding is { } binding) {
                if (!ReferenceEquals(objA: moving.SizeCatalog, objB: arena.Catalog)) {
                    if (!arena.Catalog.TryResolve(StateLane.Document, binding.Row, out moving.SizeHandle)) {
                        throw new InvalidOperationException(message: $"Solid size binding '{binding.Row}' no longer resolves.");
                    }
                    moving.SizeCatalog = arena.Catalog;
                }
                scale = ((arena.Keys.TryResolve(moving.SizeKey, out var key) && arena.TryReadLive(moving.SizeHandle.Ordinal, key, time, out var value))
                    ? WorldCreationSize.Of(value: value) : FixedQ4816.Zero);
            }

            RestoreAttachedRow(origin: origin, rotation: rotation, row: row, scale: scale);
            m_refreshHash.Add(value: scale.Value);
        }
        if (m_refreshHash.Value != m_attachedHash) {
            m_attachedHash = m_refreshHash.Value;
            AttachedRevision++;
        }
    }

    /// <summary>Publishes the authority-sampled root sizes in storage borrowed until the next snapshot, like its body entries.</summary>
    public WorldSolidScale[] SampledScales() {
        for (var row = 0; (row < m_attachedRows.Count); row++) {
            var slot = m_scaleSlots[row];

            m_sampledScales[slot] = m_sampledScales[slot] with { Scale = (m_attachedResolved[row] ? m_attachedScales[row] : FixedQ4816.Zero) };
        }
        return m_sampledScales;
    }
    /// <summary>Finds a walkable contact against the previous attached geometry and returns its carrier's
    /// transform displacement. The same narrowphase and slope limit as ordinary contact decide support; a yawed box is
    /// therefore just as rideable as an axis-aligned one. Ties retain placement and volume order.</summary>
    /// <param name="position">The body's previous foot point.</param>
    /// <param name="orientation">The body's orientation.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <param name="volumes">The body's scaled collider volumes.</param>
    /// <param name="displacement">The foot point's displacement under the carrier's translation and yaw.</param>
    /// <param name="supportRow">The moving row whose material surface supplies the displacement.</param>
    /// <returns>Whether a moving support was found.</returns>
    public bool TryRideDisplacement(FixedVector3 position, in FixedQuaternion orientation, in FixedVector3 up,
        ReadOnlySpan<FixedBodyColliderVolume> volumes, out FixedVector3 displacement, out int supportRow) {
        displacement = FixedVector3.Zero;
        supportRow = -1;
        var found = false;
        var bestDepth = FixedQ4816.Zero;
        var skin = ContactReach;

        for (var index = 0; (index < m_previousAttachedColliders.Count); index++) {
            var collider = m_previousAttachedColliders[index];
            var row = m_previousAttachedColliderRows[index];

            if (!m_attachedResolved[row] || !m_previousAttachedResolved[row] || (m_attachedScales[row] <= FixedQ4816.Zero) || (m_previousAttachedScales[row] <= FixedQ4816.Zero)) {
                continue;
            }
            foreach (ref readonly var volume in volumes) {
                if (!collider.TryGetPush(orientation: in orientation, position: position, push: out var push, skin: skin,
                    uncertain: out _, volume: in volume) ||
                    (FixedVector3.Dot(left: push.Normal, right: up) < m_solver.GroundedThreshold) ||
                    (found && (push.Penetration <= bestDepth))) {
                    continue;
                }
                var carriedPoint = position;

                if (m_attachedScales[row] != m_previousAttachedScales[row]) {
                    var probe = (position + FixedRigidWitness.Anchor(volume, FixedVector3.Zero, orientation, -push.Normal));

                    if (!FixedSurfaceQuery.TryNearest([collider], [], probe, (skin + push.Penetration), out var surface)) { continue; }
                    // Only the material point on the support scales. Body standoff, skin and overhang do not.
                    carriedPoint = surface.Point;
                }
                found = true;
                supportRow = row;
                bestDepth = push.Penetration;
                var offset = (carriedPoint - m_previousAttachedOrigins[row]);
                var sameRotation = (m_attachedRotations[row] == m_previousAttachedRotations[row]);
                var local = (sameRotation ? offset : m_previousAttachedRotations[row].RotateInverse(vector: offset));

                local = new FixedVector3(
                    X: FusedArithmetic.MultiplyDivide(local.X, m_attachedScales[row], m_previousAttachedScales[row]),
                    Y: FusedArithmetic.MultiplyDivide(local.Y, m_attachedScales[row], m_previousAttachedScales[row]),
                    Z: FusedArithmetic.MultiplyDivide(local.Z, m_attachedScales[row], m_previousAttachedScales[row]));
                displacement = ((m_attachedOrigins[row] + (sameRotation ? local : m_attachedRotations[row].Rotate(vector: local))) - carriedPoint);
            }
        }
        if (!found) {
            // A newly born solid has no previous surface. Descend onto its new surface from above its bounds.
            for (var index = 0; (index < m_attachedColliders.Count); index++) {
                var row = m_attachedColliderRows[index];

                if (!m_previousAttachedResolved[row] || (m_previousAttachedScales[row] > FixedQ4816.Zero)) { continue; }
                var collider = m_attachedColliders[index];

                if (!collider.WorldBox().Intersects(other: FixedAabb.FromCenter(center: position, extent: FixedVector3.Zero).Grow(margin: skin))) { continue; }
                var box = collider.WorldBox();

                if (box.IsEverywhere) { continue; }
                var corner = new FixedVector3(X: ((up.X >= FixedQ4816.Zero) ? box.Maximum.X : box.Minimum.X),
                    Y: ((up.Y >= FixedQ4816.Zero) ? box.Maximum.Y : box.Minimum.Y), Z: ((up.Z >= FixedQ4816.Zero) ? box.Maximum.Z : box.Minimum.Z));
                var bottom = FixedQ4816.MaxValue;

                foreach (ref readonly var volume in volumes) {
                    bottom = FixedQ4816.Min(x: bottom, y: FixedColliderBounds.MinimumProjection(normal: up, orientation: orientation, position: position, volume: volume));
                }
                var lift = (FixedQ4816.Max(x: FixedQ4816.Zero, y: (FixedVector3.Dot(left: corner, right: up) - bottom)) + skin);
                var above = (position + (up * lift));

                m_bornSupportSet.Refresh(colliders: [collider]);
                if (m_solver.TrySupportDistance(m_bornSupportSet, EmptyColliders, above, orientation, volumes, up, lift, out var descent)) {
                    displacement = (up * (lift - descent));
                    supportRow = row;
                    found = true;
                    break;
                }
            }
        }
        return (found && (displacement != FixedVector3.Zero));
    }

    // Every solve runs the static field's own query with the attached solids as its per-tick colliders: RefreshAttached
    // recomputes them once per tick, never here, since a solve runs once per body.

    /// <inheritdoc/>
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        m_static.Resolve(dynamicColliders: QueryAttachedSet, orientation: in orientation, position: ref position, up: in up, velocity: ref velocity, volumes: volumes);
    /// <inheritdoc/>
    public int CollectContacts(in FixedVector3 position, in FixedQuaternion orientation, in FixedBodyColliderVolume volume, FixedQ4816 margin,
        Span<FixedSurfaceContact> destination) =>
        m_static.CollectContacts(destination: destination, dynamicColliders: QueryAttachedSet, margin: margin, orientation: in orientation, position: in position, volume: in volume);
    /// <inheritdoc/>
    public ContactSupportWitness? SupportWitness(ReadOnlySpan<FixedSurfaceContact> contacts, in FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, in FixedVector3 up) =>
        m_static.SupportWitness(contacts: contacts, dynamicColliders: QueryAttachedSet, orientation: in orientation, position: in position, up: in up, volume: in volume);
    /// <inheritdoc/>
    public ContactResolution ResolveSweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity,
        in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        m_static.ResolveSweep(dynamicColliders: QueryAttachedSet, orientation: in orientation, position: ref position, previousPosition: in previousPosition,
            up: in up, velocity: ref velocity, volumes: volumes);
    /// <inheritdoc/>
    public bool TrySupportDistance(in FixedVector3 position, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes,
        in FixedVector3 up, FixedQ4816 reach, out FixedQ4816 descent) =>
        m_static.TrySupportDistance(descent: out descent, dynamicColliders: QueryAttachedSet, orientation: in orientation, position: in position,
            reach: reach, up: in up, volumes: volumes);
    /// <inheritdoc/>
    // Searches the static and sampled moving holdable subsets, with a zero
    // assist cone: a hold aims at the surface it is already touching, not at a target it is trying to acquire.
    public bool TryHoldableSurfaceAlongDirection(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, out FixedSurfaceAttachCandidate candidate, out bool grantedByOverride) {
        if (!FixedSurfaceQuery.TryNearestDirected(
            assistHalfAngle: FixedQ4816.Zero,
            candidate: out candidate,
            colliders: m_holdableColliders,
            direction: in direction,
            dynamicColliders: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_movingHoldable),
            maxDistance: maxDistance,
            origin: in origin
        )) {
            candidate = default;
            grantedByOverride = false;

            return false;
        }

        grantedByOverride = ((candidate.Source == FixedSurfaceColliderSource.Static)
            ? m_holdableGrantedByOverride[candidate.ColliderIndex] : m_movingGripOverrides[candidate.ColliderIndex]);

        return true;
    }
    /// <inheritdoc/>
    public bool TryNearestSurfaceAlongDirection(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, FixedQ4816 assistHalfAngle, out FixedSurfaceAttachCandidate candidate) => FixedSurfaceQuery.TryNearestDirected(
        assistHalfAngle: assistHalfAngle,
        candidate: out candidate,
        colliders: m_colliders,
        direction: in direction,
        dynamicColliders: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_attachedColliders),
        maxDistance: maxDistance,
        origin: in origin
    );
    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) {
        _ = position;
        up = FixedVector3.UnitY;

        return true;
    }
}


