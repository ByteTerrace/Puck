using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Answers where a seat aims, for the editor: the ray through the seat's pointer when it is over the seat's view,
/// otherwise through the middle of the view, cast through the camera the view last presented
/// (<see cref="WorldSeatViewports"/>, <see cref="SourceRay.Through"/>); and the surface under the pointer, that ray
/// marched through the client's static field (<see cref="WorldClient.StaticField"/>) out to the world's far distance.
/// A host with no pointer (offscreen) aims every seat through the middle of its view. Presentation only.</summary>
/// <param name="viewports">The per-seat views the presentation publishes.</param>
/// <param name="client">The client whose static field the ray marches.</param>
/// <param name="pointer">The pointer store, or <see langword="null"/> for a host without one.</param>
public sealed class WorldEditorPointer(WorldSeatViewports viewports, WorldClient client, WorldPointer? pointer = null) {
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

    /// <summary>Returns the ray a seat aims along: through its pointer when the pointer is over its view, otherwise
    /// through the middle of its view.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <returns>The ray, or <see langword="null"/> when the seat presents no view.</returns>
    public WorldEditorRay? Aim(int slot) => Through(
        pointerOnly: false,
        slot: slot
    );
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
    /// <summary>Returns the surface under a seat's pointer, or <see langword="null"/> for none.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <returns>The hit, its normal the field's gradient there, or zero where the field gives none.</returns>
    public WorldEditorPointerHit? Probe(int slot) => ((Through(pointerOnly: true, slot: slot) is { } ray)
        ? Surface(
            maxDistance: WorldRenderFarDistance.Resolve(defaults: client.Definition.Render),
            ray: ray
        )
        : null);
    /// <summary>Returns where a ray first meets the client's static field, or <see langword="null"/> when it meets
    /// nothing within the distance or the client has no static field.</summary>
    /// <param name="ray">The ray, in world space.</param>
    /// <param name="maxDistance">The farthest the ray reaches, in world units.</param>
    /// <returns>The hit, its normal the field's gradient there, or zero where the field gives none.</returns>
    public WorldEditorPointerHit? Surface(WorldEditorRay ray, float maxDistance) => ((client.StaticField is { } field)
        ? Cast(
            direction: ray.Direction,
            field: field,
            maxDistance: maxDistance,
            origin: ray.Origin
        )
        : null);
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
