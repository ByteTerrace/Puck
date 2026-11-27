using System.Numerics;
using Puck.Physics;
using Puck.SignedDistance;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>
/// The analytic occluders a world's creations cast (<see cref="WorldPrototype.Occlusion"/>), posed each frame and
/// appended to the frame's occluders table: one for each placement showing an occluding creation, riding the placement's
/// anchor (<see cref="WorldAnchor.Placement"/>, as a creation's lights do, <see cref="WorldCreationLights"/>), and one for
/// each live body that renders as an occluding creation (<see cref="WorldBodyStampCensus.TryResolveCreation"/>),
/// riding its drawn root (<see cref="WorldAnchorPoses.TryEntity"/>), so a body that moves moves its occluder through the
/// pose its stamp draws. A placement's is the creation's own solid (<see cref="LocalOf"/>) at the row's scale, and a
/// body's its kit's collider where one solid stands for it (<see cref="BodyLocalOf"/>) at the body's drawn scale. A
/// body's occluder names its stamp's slots (<see cref="WorldCreationInstances.TryBodyTransformSlot(int, out int, out int)"/>),
/// so the body's own surfaces take nothing from it.
/// </summary>
/// <remarks>Presentation only: every pose is the float image of what the frame draws. Past
/// <see cref="SdfOccluders.MaxOccluders"/> the table keeps those whose spheres of reach come nearest the presentation's
/// first view.</remarks>
public sealed class WorldCreationOccluders {
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldPrototype, SdfOccluder?[]> Locals = new();
    private static readonly CreationStampTransform Rest = new(Vector3.Zero, Quaternion.Identity, 1f, null);

    // The definition the placements were gathered from, the placements carrying an occluding creation (each with the
    // anchor it rides and its solid at the row's scale), and whether any creation the definition holds occludes at all.
    private WorldDefinition? m_definition;
    private readonly List<(WorldAnchor Anchor, SdfOccluder Occluder)> m_placed = [];
    private bool m_anyOccludes;
    private readonly List<int> m_bodies = [];
    // The occluders gathered this frame before the table's capacity trims them.
    private readonly List<SdfOccluder> m_candidates = [];

    /// <summary>Gets how many occluders the latest <see cref="AppendTo"/> gathered before the table's capacity, the
    /// authored ones it was handed included.</summary>
    public int Gathered { get; private set; }

    /// <summary>Returns a creation's occluder in its own frame and units: the solid its <see cref="WorldPrototype.Occlusion"/>
    /// fits (<see cref="SdfOccluder.Fit"/>) to the box its shapes fill at rest, every shape but those that carve
    /// (<see cref="SdfBoundAlgebra.Carves"/>) counted by its box (<see cref="CreationStampEmitter.ShapeStampBox"/>), or the
    /// cube about its bounding sphere where it has none, with the occlusion's strengths, reach and softness. Derived
    /// once a row.</summary>
    /// <param name="prototype">The creation.</param>
    /// <returns>The occluder, or <see langword="null"/> for a creation that casts none or has no solid shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="prototype"/> is <see langword="null"/>.</exception>
    public static SdfOccluder? LocalOf(WorldPrototype prototype) {
        ArgumentNullException.ThrowIfNull(argument: prototype);

        return (prototype.Occlusion is null) ? null : Locals.GetValue(key: prototype, derive: static row => [Derive(prototype: row)])[0];
    }
    private static SdfOccluder? Derive(WorldPrototype prototype) {
        var document = prototype.EngineDocument;
        var shapes = (document.Shapes ?? []);
        var low = new Vector3(value: float.PositiveInfinity);
        var high = new Vector3(value: float.NegativeInfinity);

        for (var index = 0; (index < shapes.Count); index++) {
            if (SdfBoundAlgebra.Carves(blend: (shapes[index].Blend ?? SdfBlendOp.Union))) {
                continue;
            }

            var box = CreationStampEmitter.ShapeStampBox(document: document, shapeIndex: index, transform: Rest);

            if (box is not { } corners) {
                var (center, radius) = CreationStampEmitter.ShapeStampBound(document: document, shapeIndex: index, transform: Rest);

                corners = ((center - new Vector3(value: radius)), (center + new Vector3(value: radius)));
            }
            if (!float.IsFinite(f: ((corners.Low.X + corners.Low.Y) + corners.Low.Z)) || !float.IsFinite(f: ((corners.High.X + corners.High.Y) + corners.High.Z))) {
                continue;
            }

            low = Vector3.Min(value1: low, value2: corners.Low);
            high = Vector3.Max(value1: high, value2: corners.High);
        }

        return ((SdfOccluder.Fit(fit: (prototype.Occlusion!.Shape ?? SdfOccluderFit.Auto), high: high, low: low) is { } fitted)
            ? prototype.Occlusion.Apply(fitted: fitted)
            : null);
    }
    /// <summary>Returns the occluder a body wearing a creation casts, in the body's frame at unit scale: the solid of its
    /// kit's collider where that is one sphere, capsule or box (<see cref="SolidOf"/>), which is the volume a body's kit
    /// is authored to fill, else the creation's own (<see cref="LocalOf"/>); with the creation's occlusion's strengths,
    /// reach and softness either way. The collider is preferred because the rest box a creation's shapes fill counts a
    /// shape no box holds (a flared, twisted or swept one) by its cull sphere, which for a character can stand far wider
    /// than the body and reach under the ground it stands on.</summary>
    /// <param name="prototype">The creation the body wears.</param>
    /// <param name="collider">The body's kit's compiled collider (<see cref="IWorldStampSource.Collider"/>), or
    /// <see langword="null"/> for a kit that authors none.</param>
    /// <returns>The occluder, or <see langword="null"/> for a creation that casts none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="prototype"/> is <see langword="null"/>.</exception>
    public static SdfOccluder? BodyLocalOf(WorldPrototype prototype, FixedWorldCollider? collider) {
        ArgumentNullException.ThrowIfNull(argument: prototype);

        if (prototype.Occlusion is not { } occlusion) {
            return null;
        }

        return (((collider is { } kit) && (SolidOf(collider: kit) is { } solid)) ? occlusion.Apply(fitted: solid) : LocalOf(prototype: prototype));
    }
    /// <summary>Returns the analytic solid of a collider that is one sphere, capsule or box volume, in the body's frame at
    /// unit scale, its strengths, reach and softness zero.</summary>
    /// <param name="collider">The compiled collider.</param>
    /// <returns>The solid, or <see langword="null"/> for a collider of several volumes or of an ellipsoid, which no one
    /// sphere, capsule or box stands for.</returns>
    public static SdfOccluder? SolidOf(FixedWorldCollider collider) {
        var volumes = collider.Volumes;

        if (volumes.Length != 1) {
            return null;
        }

        var volume = volumes[0];
        var center = volume.Center.ToVector3();

        switch (volume.Kind) {
            case FixedBodyColliderKind.Sphere:
                return new SdfOccluder { Center = center, Radius = (float)volume.Radius, Shape = SdfOccluderShape.Sphere };
            case FixedBodyColliderKind.Capsule: {
                    // The volume's Center is the core's lower end and its Endpoint the upper.
                    var half = (0.5f * (volume.Endpoint.ToVector3() - center));

                    return new SdfOccluder { AxisX = half, Center = (center + half), Radius = (float)volume.Radius, Shape = SdfOccluderShape.Capsule };
                }
            case FixedBodyColliderKind.Box: {
                    var rotation = volume.Rotation.ToQuaternion();
                    var half = volume.HalfExtents.ToVector3();

                    return new SdfOccluder {
                        AxisX = Vector3.Transform(value: new Vector3(x: half.X, y: 0f, z: 0f), rotation: rotation),
                        AxisY = Vector3.Transform(value: new Vector3(x: 0f, y: half.Y, z: 0f), rotation: rotation),
                        AxisZ = Vector3.Transform(value: new Vector3(x: 0f, y: 0f, z: half.Z), rotation: rotation),
                        Center = center,
                        Shape = SdfOccluderShape.Box,
                    };
                }
            default:
                return null;
        }
    }

    /// <summary>Appends this frame's creation occluders to a table that may already hold the authored ones, then keeps the
    /// nearest <see cref="SdfOccluders.MaxOccluders"/> of them all to <paramref name="eye"/>.</summary>
    /// <param name="occluders">The frame's occluders table, which this call writes.</param>
    /// <param name="scope">What the frame's poses are read from.</param>
    /// <param name="eye">The presentation's first view's eye, which the nearest are kept by.</param>
    /// <exception cref="ArgumentNullException"><paramref name="occluders"/> is <see langword="null"/>.</exception>
    public void AppendTo(SdfOccluders occluders, in WorldAnchorScope scope, Vector3 eye) {
        ArgumentNullException.ThrowIfNull(argument: occluders);

        var definition = scope.Source.Definition;

        if (!ReferenceEquals(objA: definition, objB: m_definition)) {
            Gather(definition: definition);
        }

        Gathered = occluders.Count;
        if (!m_anyOccludes) {
            return;
        }

        m_candidates.Clear();
        foreach (var authored in occluders.Records) { m_candidates.Add(item: authored); }
        foreach (var (anchor, local) in m_placed) {
            if (WorldAnchorPoses.TryResolve(anchor: anchor, pose: out var pose, scope: in scope)) {
                m_candidates.Add(item: local.Placed(orientation: pose.Orientation, position: pose.Position, scale: 1f));
            }
        }

        scope.Source.CopyActiveBodies(bodies: m_bodies);
        foreach (var body in m_bodies) {
            if (!WorldBodyStampCensus.TryResolveCreation(creation: out var creation, definition: definition, index: body, scale: out var scale, source: scope.Source)
                || (BodyLocalOf(collider: scope.Source.Collider(index: body), prototype: creation) is not { } local)
                || !WorldAnchorPoses.TryEntity(body: body, pose: out var pose, scope: in scope)) {
                continue;
            }

            var occluder = local.Placed(orientation: pose.Orientation, position: pose.Position, scale: scale);

            if ((scope.Stamps is { } stamps) && stamps.TryBodyTransformSlot(bodyIndex: body, slotCount: out var count, transformSlot: out var slot)) {
                occluder = (occluder with { OwnSlot = slot, OwnSlots = (uint)Math.Max(val1: count, val2: 0) });
            }

            m_candidates.Add(item: occluder);
        }

        Gathered = m_candidates.Count;
        // Past the table's capacity, the occluders whose spheres of reach come nearest the eye.
        if (m_candidates.Count > SdfOccluders.MaxOccluders) {
            m_candidates.Sort(comparison: (left, right) => (Vector3.Distance(value1: eye, value2: left.Center) - left.Reach).CompareTo(value: (Vector3.Distance(value1: eye, value2: right.Center) - right.Reach)));
        }

        occluders.Clear();
        foreach (var occluder in m_candidates) {
            if (occluders.Count >= SdfOccluders.MaxOccluders) {
                break;
            }

            _ = occluders.TryAdd(occluder: in occluder);
        }
    }
    // The placements carrying an occluding creation: each one frame a creation's light could ride (WorldCreationLights.Carries)
    // that no body inhabits (an inhabitant's occluder is its body's), for each creation it can show.
    private void Gather(WorldDefinition definition) {
        m_definition = definition;
        m_placed.Clear();
        m_anyOccludes = false;

        foreach (var creation in definition.Creations) {
            if (creation?.Occlusion is not null) {
                m_anyOccludes = true;

                break;
            }
        }
        if (!m_anyOccludes) {
            return;
        }

        foreach (var placement in definition.Placements) {
            if ((placement?.Id is not { } id) || (placement.Inhabit is not null) || !WorldCreationLights.Carries(placement: placement)) {
                continue;
            }

            var scale = (placement.HasStaticFrame ? WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement).Scale : placement.Scale);

            foreach (var prototypeId in WorldPlacementResponse.Showable(placement: placement).Distinct(comparer: StringComparer.Ordinal)) {
                if ((WorldDefinitionRows.FindCreation(creations: definition.Creations, id: prototypeId) is { } creation) && (LocalOf(prototype: creation) is { } local)) {
                    m_placed.Add(item: (new WorldAnchor.Placement(PlacementId: id, ShapeId: null, Showing: prototypeId), local.Placed(orientation: Quaternion.Identity, position: Vector3.Zero, scale: scale)));
                }
            }
        }
    }
}
