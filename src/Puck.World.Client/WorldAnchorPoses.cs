using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>What <see cref="WorldAnchorPoses.TryResolve"/> reads an anchor's pose from this frame: the world's body poses,
/// its stamp pool and packed transforms, and the seat a seat-relative anchor resolves for.</summary>
/// <param name="Source">The body poses: the client's render poses, or a session mirror's interpolated ones.</param>
/// <param name="Stamps">The stamp pool whose last pack drew the world's creations, or <see langword="null"/> for a
/// world presented without one, whose placements resolve at rest.</param>
/// <param name="Transforms">The frame's packed transforms, which a body part reads.</param>
/// <param name="Perception">The per-seat perception anchor a <see cref="WorldAnchor.Seat"/> resolves through, or
/// <see langword="null"/> where no seat is presented.</param>
/// <param name="Speech">The speech clock a <see cref="WorldAnchor.RecentSpeaker"/> resolves through, or
/// <see langword="null"/>.</param>
/// <param name="Slot">The 0-based seat a seat-relative anchor with no number resolves for.</param>
public readonly ref struct WorldAnchorScope(IWorldStampSource Source, WorldCreationInstances? Stamps, ReadOnlySpan<DynamicTransform> Transforms, WorldPerceptionAnchor? Perception = null, WorldSpeechClock? Speech = null, int Slot = 0) {
    /// <summary>Gets the body poses.</summary>
    public IWorldStampSource Source { get; } = Source;
    /// <summary>Gets the stamp pool, or <see langword="null"/>.</summary>
    public WorldCreationInstances? Stamps { get; } = Stamps;
    /// <summary>Gets the frame's packed transforms.</summary>
    public ReadOnlySpan<DynamicTransform> Transforms { get; } = Transforms;
    /// <summary>Gets the per-seat perception anchor, or <see langword="null"/>.</summary>
    public WorldPerceptionAnchor? Perception { get; } = Perception;
    /// <summary>Gets the speech clock, or <see langword="null"/>.</summary>
    public WorldSpeechClock? Speech { get; } = Speech;
    /// <summary>Gets the 0-based seat a seat-relative anchor resolves for.</summary>
    public int Slot { get; } = Slot;
}

/// <summary>
/// The one resolver of a <see cref="WorldAnchor"/> to a pose, which every presentation consumer reads: cameras (the main
/// window's named cameras, filmed views, session projections), lights, speakers and overlay subjects. An anchor
/// resolves to the pose the frame draws the anchored thing at, position and facing both, so a camera, a light and a
/// speaker on one anchor agree within a frame.
/// </summary>
/// <remarks>
/// <para>The rule per kind:</para>
/// <para>An entity rides its drawn root: the pose its stamp's last pack drew (followers and rigid copies included),
/// else its render pose when it draws no stamp. An inactive entity resolves nothing.</para>
/// <para>A body part rides its packed pose (<see cref="WorldEntityPartResolver"/>).</para>
/// <para>A placement rides its live pose whenever the pool holds a registration for it (an animated row, an attached
/// row, an inhabited row's body), composed with the named shape's pose and facing, and an inhabited row the pool has
/// not registered yet rides its live inhabitant's root. An attached row whose carrier is not live this frame, and an
/// inhabited row with no live inhabitant, resolve nothing, as the renderer draws nothing for them. Any other row rides
/// its rest pose (<see cref="TryRestPlacement"/>); a dealt template draws nothing and resolves nothing.</para>
/// <para>A group rides its members' raw centroid with no facing; a consumer that smooths it (the main window's
/// establishing shot) does so over this answer. A seat-relative anchor rides its seat's perceived body, or the recent
/// speaker, exactly as an entity or body-part anchor on that body would. A null anchor is the world origin.</para>
/// <para>Presentation only: nothing here feeds simulation, and every pose is the float image of what the frame
/// shows.</para>
/// </remarks>
public static class WorldAnchorPoses {
    /// <summary>Resolves an anchor's pose this frame.</summary>
    /// <param name="anchor">The anchor; <see langword="null"/> is the world origin.</param>
    /// <param name="scope">What the pose is read from.</param>
    /// <param name="pose">The pose, or default when the anchor resolves nothing.</param>
    /// <returns><see langword="true"/> when the anchored thing is drawn this frame.</returns>
    public static bool TryResolve(WorldAnchor? anchor, in WorldAnchorScope scope, out SdfAnchor pose) {
        switch (anchor) {
            case null:
                pose = new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity);

                return true;
            case WorldAnchor.Entity entity:
                return TryEntity(body: entity.Index, pose: out pose, scope: in scope);
            case WorldAnchor.EntityPart part:
                return TryPart(body: part.Index, partId: part.PartId, pose: out pose, scope: in scope);
            case WorldAnchor.Placement placement:
                return TryPlacement(
                    placementId: placement.PlacementId,
                    pose: out pose,
                    shapeId: placement.ShapeId,
                    source: scope.Source,
                    stamps: scope.Stamps
                );
            case WorldAnchor.Group group when (scope.Source is WorldClient client):
                pose = new SdfAnchor(
                    Orientation: Quaternion.Identity,
                    Position: WorldGroupAnchors.ComputeRaw(client: client, group: group, maxPopulation: client.EntityCapacity).Centroid
                );

                return true;
            case WorldAnchor.Seat or WorldAnchor.RecentSpeaker when ((scope.Perception is { } perception) && (scope.Speech is { } speech)):
                var body = WorldSeatAnchors.BodyOf(anchor: anchor, perception: perception, slot: scope.Slot, speech: speech);

                return ((WorldSeatAnchors.PartOf(anchor: anchor) is { } partId)
                    ? TryPart(body: body, partId: partId, pose: out pose, scope: in scope)
                    : TryEntity(body: body, pose: out pose, scope: in scope));
            default:
                pose = default;

                return false;
        }
    }
    /// <summary>Resolves an entity's drawn root pose: its stamp's last packed root, else its render pose.</summary>
    /// <param name="body">The entity index.</param>
    /// <param name="scope">What the pose is read from.</param>
    /// <param name="pose">The pose, or default.</param>
    /// <returns><see langword="false"/> when the entity is out of range or inactive.</returns>
    public static bool TryEntity(int body, in WorldAnchorScope scope, out SdfAnchor pose) {
        if (!WorldBodiesLimits.IsBodyIndex(index: body) || !scope.Source.IsActive(index: body)) {
            pose = default;

            return false;
        }

        if ((scope.Stamps is { } stamps) && stamps.TryBodyRootPose(bodyIndex: body, client: scope.Source, pose: out pose)) {
            return true;
        }

        pose = new SdfAnchor(Position: scope.Source.Position(index: body), Orientation: scope.Source.Orientation(index: body));

        return true;
    }
    /// <summary>Resolves a placement's pose: its live registration's, else its rest pose.</summary>
    /// <param name="placementId">The placement id.</param>
    /// <param name="shapeId">The creation shape to ride, or <see langword="null"/> for the placement's root.</param>
    /// <param name="source">The body poses an inhabited or attached row rides.</param>
    /// <param name="stamps">The stamp pool, or <see langword="null"/> to resolve every row at rest.</param>
    /// <param name="pose">The pose, or default.</param>
    /// <returns><see langword="false"/> when the placement does not resolve or draws nothing this frame.</returns>
    public static bool TryPlacement(string placementId, int? shapeId, IWorldStampSource source, WorldCreationInstances? stamps, out SdfAnchor pose) {
        ArgumentNullException.ThrowIfNull(argument: source);

        if (stamps is not null) {
            if (stamps.TryLivePlacementPose(client: source, held: out var held, placementId: placementId, pose: out pose, shapeId: shapeId)) { return true; }
            // The pool roots the row on a carrier that is not live this frame: it draws nothing, so it resolves nothing.
            if (held) { return false; }
        } else if (source.TryInhabitantBody(index: out var inhabitant, placementId: placementId)) {
            pose = new SdfAnchor(Position: source.Position(index: inhabitant), Orientation: source.Orientation(index: inhabitant));

            return true;
        }

        // A responding row riding a body that the rigid catalog copies, not the pool, rides its body's presented pose.
        if ((WorldDefinitionRows.FindPlacement(id: placementId, placements: source.Definition.Placements) is { Attach: { } attach } riding) && (riding.Inhabit is null) && (riding.Deal is null)) {
            var (position, orientation) = WorldAttachedPose.Root(attach: attach, source: source);

            pose = AtShape(definition: source.Definition, orientation: orientation, placement: riding, position: position, shapeId: shapeId);
            return true;
        }

        return TryRestPlacement(definition: source.Definition, placementId: placementId, pose: out pose, shapeId: shapeId);
    }
    /// <summary>Resolves a placement's rest pose from the document alone: its composed frame, and the named shape's
    /// rest position and facing under it. A row the pool roots on a body (an attached or inhabited row) and a dealt
    /// template resolve nothing, since no document pose of theirs is drawn.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="placementId">The placement id.</param>
    /// <param name="shapeId">The creation shape, or <see langword="null"/> for the root; one the creation does not
    /// name resolves the root.</param>
    /// <param name="pose">The pose, or default.</param>
    /// <returns><see langword="false"/> when no row has the id, or the row draws no document pose.</returns>
    public static bool TryRestPlacement(WorldDefinition definition, string placementId, int? shapeId, out SdfAnchor pose) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (
            (WorldDefinitionRows.FindPlacement(id: placementId, placements: definition.Placements) is not { } placement) ||
            (placement.Attach is not null) ||
            (placement.Inhabit is not null) ||
            (placement.Deal is not null)
        ) {
            pose = default;

            return false;
        }

        var frame = WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement);

        pose = AtShape(definition: definition, orientation: frame.Rotation, placement: placement, position: frame.Position, shapeId: shapeId);
        return true;
    }
    // A row's pose at its root, or at the named shape's rest position and facing under the root.
    private static SdfAnchor AtShape(WorldDefinition definition, WorldPlacement placement, Vector3 position, Quaternion orientation, int? shapeId) {
        if (
            (shapeId is { } target) &&
            (WorldDefinitionRows.FindCreation(creations: definition.Creations, id: placement.ShownPrototypeId) is { } creation)
        ) {
            foreach (var shape in (creation.EngineDocument.Shapes ?? [])) {
                if (shape.Id == target) {
                    return new SdfAnchor(
                        Orientation: Quaternion.Normalize(value: (orientation * shape.Rotation.Value)),
                        Position: (position + Vector3.Transform(value: (shape.Position.Value * placement.Scale), rotation: orientation))
                    );
                }
            }
        }

        return new SdfAnchor(Position: position, Orientation: orientation);
    }
    private static bool TryPart(int body, string partId, in WorldAnchorScope scope, out SdfAnchor pose) {
        if (WorldBodiesLimits.IsBodyIndex(index: body) && (scope.Source is WorldClient client) && (scope.Stamps is { } stamps)) {
            return WorldEntityPartResolver.TryPackedPose(
                client: client,
                entityIndex: body,
                partId: partId,
                pose: out pose,
                stamps: stamps,
                transforms: scope.Transforms
            );
        }

        pose = default;

        return false;
    }
}
