using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Answers where a seat aims, for the editor: the ray through the seat's pointer when it is over the seat's view,
/// otherwise through the middle of the view, cast through the camera the view last presented
/// (<see cref="WorldSeatViewports"/>, <see cref="SourceRay.Through"/>); and the surface a ray meets in the world the seat
/// is presented in: the scene of the world it crossed into (<see cref="WorldFramePresenter.TrySeatScene"/>,
/// <see cref="WorldRoutedScene.TrySurface(int, SourceRay, out FixedVector3, out Vector3)"/>), else the client's static
/// field (<see cref="WorldClient.StaticField"/>) out to the world's far distance. The aim and the surface the placement
/// verbs read are in the frame of the world the seat edits: a seat presented across an adjacency is carried through the
/// adjacency's isometry (<see cref="WorldAdjacencyPath"/>), the one its crossing and its rendering use, and a seat whose
/// view nothing relates to the world it edits aims at nothing. A host with no pointer (offscreen) aims every seat through
/// the middle of its view. Presentation only.</summary>
/// <param name="viewports">The per-seat views the presentation publishes.</param>
/// <param name="client">The client whose static field the ray marches for a seat presented here.</param>
/// <param name="pointer">The pointer store, or <see langword="null"/> for a host without one.</param>
/// <param name="presenter">Resolves the presenter whose routed scenes answer for a seat presented elsewhere, or
/// <see langword="null"/> for a host that presents every seat here. Resolved when a query runs, since the presenter holds
/// the editor seats this pointer answers for.</param>
/// <param name="continuum">Relates the frame each seat is presented in to the world it edits, or
/// <see langword="null"/> for a host whose seats are always presented in the world they edit.</param>
public sealed class WorldEditorPointer(WorldSeatViewports viewports, WorldClient client, WorldPointer? pointer = null, Func<WorldFramePresenter>? presenter = null, WorldContinuum? continuum = null) {
    private static readonly Vector2 Middle = new(value: 0.5f);

    private WorldEditorRay? Through(int slot, bool pointerOnly) {
        var view = viewports.Seat(slot: slot);

        if (!view.Present) {
            return null;
        }

        var image = Middle;
        var overView = (
            (pointer is { } store) &&
            store.HasPosition(slot: slot) &&
            (viewports.Locate(
                framePosition: out _,
                local: out image,
                position: store.Position(slot: slot),
                view: in view
            ) == WorldSeatPointerPlace.Inside)
        );

        if (!overView) {
            if (pointerOnly) {
                return null;
            }

            image = Middle;
        }

        var ray = SourceRay.Through(
            camera: view.Camera,
            image: new FixedVector2(
                X: FixedQ4816.FromDouble(value: image.X),
                Y: FixedQ4816.FromDouble(value: image.Y)
            )
        );

        return new WorldEditorRay(
            Direction: Vector3.Normalize(value: ray.Direction.ToVector3()),
            Origin: ray.Origin.ToVector3()
        );
    }
    // The surface a ray in the frame the seat is presented in meets there: the scene of the world the seat crossed into,
    // else the client's static field, which carries every adjacency the boot frame draws; nothing for a seat whose frame
    // nothing relates to the world it is routed to.
    private WorldEditorPointerHit? PresentedSurface(int slot, WorldEditorRay ray, float maxDistance) {
        if ((presenter?.Invoke() is { } frames) && frames.TrySeatScene(index: out var view, scene: out var scene, slot: slot)) {
            return (scene.TrySurface(
                normal: out var normal,
                point: out var point,
                ray: new SourceRay(
                    Direction: FixedVector3.FromVector3(value: ray.Direction),
                    Origin: FixedVector3.FromVector3(value: ray.Origin)
                ),
                view: view
            )
                ? new WorldEditorPointerHit(Normal: normal, Point: point.ToVector3())
                : null);
        }

        if ((continuum is { } frameOf) && !frameOf.TryEditingPath(path: out _, slot: slot)) {
            return null;
        }

        return ((client.StaticField is { } field)
            ? Cast(
                direction: ray.Direction,
                field: field,
                maxDistance: maxDistance,
                origin: ray.Origin
            )
            : null);
    }
    // The adjacency path from the frame a seat is presented in to the world it edits, or null when they are one frame;
    // false when nothing relates them.
    private bool TryEditingPath(int slot, out IReadOnlyList<WorldAdjacencyFramePair>? path) {
        path = null;

        return ((continuum is not { } frameOf) || frameOf.TryEditingPath(path: out path, slot: slot));
    }

    /// <summary>Returns the ray a seat aims along, in the frame of the world it edits: through its pointer when the
    /// pointer is over its view, otherwise through the middle of its view, carried across the adjacency the seat is
    /// presented through, if any (<see cref="WorldContinuum.TryEditingPath"/>).</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <returns>The ray, or <see langword="null"/> when the seat presents no view, or nothing relates its view to the
    /// world it edits.</returns>
    public WorldEditorRay? Aim(int slot) => IntoEditingFrame(
        ray: Through(pointerOnly: false, slot: slot),
        slot: slot
    );

    // A ray through the seat's view carried into the frame of the world it edits, or null when there is none or nothing
    // relates the two frames.
    private WorldEditorRay? IntoEditingFrame(int slot, WorldEditorRay? ray) {
        if (
            (ray is not { } presented) ||
            !TryEditingPath(path: out var path, slot: slot)
        ) {
            return null;
        }

        return ((path is null)
            ? presented
            : new WorldEditorRay(
                Direction: Vector3.Normalize(value: WorldAdjacencyPath.MapVectorIntoNeighbour(path: path, value: FixedVector3.FromVector3(value: presented.Direction)).ToVector3()),
                Origin: WorldAdjacencyPath.MapPointIntoNeighbour(path: path, value: FixedVector3.FromVector3(value: presented.Origin)).ToVector3()
            ));
    }

    /// <summary>Gives a set of editor seats this host's aim and pointer probes.</summary>
    /// <param name="seats">The editor seats.</param>
    /// <returns>The same editor seats.</returns>
    public WorldEditorSeats Attach(WorldEditorSeats seats) {
        ArgumentNullException.ThrowIfNull(argument: seats);

        seats.AimProbe = Aim;
        seats.PointerProbe = Probe;
        seats.SurfaceProbe = Surface;

        return seats;
    }
    /// <summary>Returns the surface under a seat's pointer in the frame of the world it edits, where its grid's plane and
    /// lattice are composed, or <see langword="null"/> for none.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <returns>The hit, its normal the field's gradient there, or zero where the field gives none.</returns>
    public WorldEditorPointerHit? Probe(int slot) => ((IntoEditingFrame(ray: Through(pointerOnly: true, slot: slot), slot: slot) is { } ray)
        ? Surface(
            maxDistance: WorldRenderFarDistance.Resolve(defaults: client.Definition.Render),
            ray: ray,
            slot: slot
        )
        : null);
    /// <summary>Returns where a ray in the frame of the world a seat edits first meets that world's solid surfaces, in
    /// that frame, or <see langword="null"/> when it meets nothing within the distance, that world has no queryable
    /// field here, or nothing relates the seat's view to it. A seat presented across an adjacency casts through the boot
    /// frame that draws the destination and carries the hit back through the same isometry.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="ray">The ray, in the edited world's coordinates.</param>
    /// <param name="maxDistance">The farthest the ray reaches, in world units, for a seat presented in the boot frame; a
    /// crossed seat's scene bounds the ray by its own frame's far distance.</param>
    /// <returns>The hit, its normal the field's gradient there, or zero where the field gives none.</returns>
    public WorldEditorPointerHit? Surface(int slot, WorldEditorRay ray, float maxDistance) {
        if (!TryEditingPath(path: out var path, slot: slot)) {
            return null;
        }

        if (path is null) {
            return PresentedSurface(
                maxDistance: maxDistance,
                ray: ray,
                slot: slot
            );
        }

        var presented = PresentedSurface(
            maxDistance: maxDistance,
            ray: new WorldEditorRay(
                Direction: WorldAdjacencyPath.MapVectorIntoSource(path: path, value: FixedVector3.FromVector3(value: ray.Direction)).ToVector3(),
                Origin: WorldAdjacencyPath.MapPointIntoSource(path: path, value: FixedVector3.FromVector3(value: ray.Origin)).ToVector3()
            ),
            slot: slot
        );

        return ((presented is { } hit)
            ? new WorldEditorPointerHit(
                Normal: WorldAdjacencyPath.MapVectorIntoNeighbour(path: path, value: FixedVector3.FromVector3(value: hit.Normal)).ToVector3(),
                Point: WorldAdjacencyPath.MapPointIntoNeighbour(path: path, value: FixedVector3.FromVector3(value: hit.Point)).ToVector3()
            )
            : null);
    }
    /// <summary>Returns where a ray meets a static field, or <see langword="null"/> when it meets nothing within the
    /// distance.</summary>
    /// <param name="field">The field.</param>
    /// <param name="origin">The ray's origin, in world space.</param>
    /// <param name="direction">The ray's direction.</param>
    /// <param name="maxDistance">The farthest the ray reaches, in world units.</param>
    /// <returns>The hit, its normal the field's gradient there, or zero where the field gives none.</returns>
    public static WorldEditorPointerHit? Cast(SdfFieldEvaluator field, Vector3 origin, Vector3 direction, float maxDistance) {
        ArgumentNullException.ThrowIfNull(argument: field);

        if (!field.Raycast(
            dir: FixedVector3.FromVector3(value: direction),
            hit: out var hit,
            maxDist: FixedQ4816.FromDouble(value: maxDistance),
            origin: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: origin))
        )) {
            return null;
        }

        var normal = (field.TryFieldGradient(gradient: out var gradient, position: hit.Point)
            ? Vector3.Normalize(value: gradient.ToVector3())
            : Vector3.Zero);

        return new WorldEditorPointerHit(
            Normal: (float.IsFinite(f: normal.X) ? normal : Vector3.Zero),
            Point: hit.Point.ToRenderRelative(origin: FixedPosition.Zero)
        );
    }
}
