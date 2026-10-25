using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.SdfVm;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The portal doors of a world whose counterparts stand in the same world, and the clip a body straddling one is drawn
/// with (<see cref="SdfSlotClip"/>): the body clipped to the front of the glass it straddles, and its copy, moved
/// through the pair's rigid motion, clipped to the front of the counterpart's glass, where the part of the body past
/// the glass shows. Presentation only, in float.
/// </summary>
/// <remarks>A door's destination stands in the same world when the reference it names is the world's own document:
/// the reference's file name without extension is the document's <see cref="WorldDefinition.DocumentId"/>, and its
/// counterpart names a face of this document.</remarks>
public static class WorldPortalStraddle {
    private static readonly ConditionalWeakTable<WorldDefinition, Door[]> PerDefinition = new();

    /// <summary>One door of a same-world pair.</summary>
    /// <param name="Glass">The door's glass.</param>
    /// <param name="Counterpart">The counterpart's glass.</param>
    /// <param name="Carry">The motion and scale the pair moves a traveller through.</param>
    public readonly record struct Door(WorldFaceGeometry Glass, WorldFaceGeometry Counterpart, WorldPoseCarry Carry);

    /// <summary>Returns the doors of a world whose counterparts stand in the same world, derived once per definition.</summary>
    /// <param name="definition">The world's definition.</param>
    /// <returns>The doors, in document order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<Door> Doors(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return PerDefinition.GetValue(
            createValueCallback: Derive,
            key: definition
        );
    }
    /// <summary>Returns the clip a body straddling one of a world's same-world doors is drawn with, or
    /// <see langword="null"/> when it straddles none: its bounding sphere crosses a door's glass plane within the
    /// door's opening, widened by the sphere's radius. Of several, the door whose glass plane lies nearest the center
    /// wins, so a conservative radius that reaches both doors of a shaft still clips at the one the body stands
    /// in.</summary>
    /// <param name="doors">The world's same-world doors (<see cref="Doors"/>).</param>
    /// <param name="center">The body's bounding sphere's center.</param>
    /// <param name="radius">Its radius.</param>
    /// <param name="firstSlot">The body's first dynamic-transform slot, absolute.</param>
    /// <param name="slotCount">Its slot count.</param>
    /// <returns>The clip, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="doors"/> is <see langword="null"/>.</exception>
    public static SdfSlotClip? Clip(IReadOnlyList<Door> doors, Vector3 center, float radius, int firstSlot, int slotCount) =>
        Clip(doors: doors, firstSlot: firstSlot, slotCount: slotCount, spheres: [new Vector4(value: center, w: radius)]);
    /// <summary>Returns the clip a body whose geometry several spheres hold is drawn with, or <see langword="null"/>
    /// when it straddles no door: some sphere crosses a door's glass plane within the door's opening, widened by that
    /// sphere's radius. A body whose parts all stand clear of every glass, however near its whole, is drawn unclipped.
    /// Of several doors, the one whose glass plane lies nearest a crossing sphere's center wins.</summary>
    /// <param name="doors">The world's same-world doors (<see cref="Doors"/>).</param>
    /// <param name="spheres">The spheres holding the body's geometry: each center in <c>XYZ</c> and its radius in
    /// <c>W</c>, in metres.</param>
    /// <param name="firstSlot">The body's first dynamic-transform slot, absolute.</param>
    /// <param name="slotCount">Its slot count.</param>
    /// <returns>The clip, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="doors"/> is <see langword="null"/>.</exception>
    public static SdfSlotClip? Clip(IReadOnlyList<Door> doors, ReadOnlySpan<Vector4> spheres, int firstSlot, int slotCount) {
        ArgumentNullException.ThrowIfNull(argument: doors);

        var nearest = -1;
        var nearestDistance = float.PositiveInfinity;

        for (var index = 0; (index < doors.Count); index++) {
            var glass = doors[index].Glass;

            foreach (var sphere in spheres) {
                var radius = sphere.W;
                var offset = (new Vector3(x: sphere.X, y: sphere.Y, z: sphere.Z) - glass.Origin);
                var distance = MathF.Abs(x: Vector3.Dot(vector1: offset, vector2: glass.Normal));

                if (
                    (distance <= radius) &&
                    (distance < nearestDistance) &&
                    (MathF.Abs(x: Vector3.Dot(vector1: offset, vector2: glass.Right)) <= (glass.HalfWidth + radius)) &&
                    (MathF.Abs(x: Vector3.Dot(vector1: offset, vector2: glass.Up)) <= (glass.HalfHeight + radius))
                ) {
                    nearest = index;
                    nearestDistance = distance;
                }
            }
        }

        if (nearest < 0) {
            return null;
        }

        var door = doors[nearest];

        return new SdfSlotClip(
            CopyPlane: PlaneOf(glass: door.Counterpart),
            CopyRotation: door.Carry.Rotation,
            CopyScale: door.Carry.Scale,
            CopyTranslation: door.Carry.Translation,
            FirstSlot: firstSlot,
            Plane: PlaneOf(glass: door.Glass),
            SlotCount: slotCount
        );
    }
    /// <summary>Carries a camera boom that passes through one of a world's same-world doors: when the segment from the
    /// framed body out to the eye crosses a door's glass from its front to its back within the opening, the eye stands
    /// past the counterpart's glass, so the eye and the look target move through the pair's rigid motion and the
    /// camera looks back through the counterpart at its subject. The first door the boom crosses in document order
    /// wins.</summary>
    /// <param name="doors">The world's same-world doors (<see cref="Doors"/>).</param>
    /// <param name="anchor">The framed body's position, in metres, where the boom starts.</param>
    /// <param name="target">The look target, in metres; carried in place when the boom crosses a door.</param>
    /// <param name="eye">The eye, in metres; carried in place when the boom crosses a door.</param>
    /// <returns><see langword="true"/> when the boom crossed a door and the eye and target were carried.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="doors"/> is <see langword="null"/>.</exception>
    public static bool CarryBoom(IReadOnlyList<Door> doors, Vector3 anchor, ref Vector3 target, ref Vector3 eye) {
        ArgumentNullException.ThrowIfNull(argument: doors);

        foreach (var door in doors) {
            var glass = door.Glass;
            var front = Vector3.Dot(vector1: (anchor - glass.Origin), vector2: glass.Normal);
            var back = Vector3.Dot(vector1: (eye - glass.Origin), vector2: glass.Normal);

            if (!((front > 0f) && (back < 0f))) {
                continue;
            }

            var offset = ((anchor + ((eye - anchor) * (front / (front - back)))) - glass.Origin);

            if (
                (MathF.Abs(x: Vector3.Dot(vector1: offset, vector2: glass.Right)) <= glass.HalfWidth) &&
                (MathF.Abs(x: Vector3.Dot(vector1: offset, vector2: glass.Up)) <= glass.HalfHeight)
            ) {
                target = door.Carry.Point(point: target);
                eye = door.Carry.Point(point: eye);

                return true;
            }
        }

        return false;
    }

    // The plane a glass keeps the front of: its outward normal and the offset that puts the glass at zero.
    private static Vector4 PlaneOf(WorldFaceGeometry glass) => new(
        value: glass.Normal,
        w: -Vector3.Dot(vector1: glass.Normal, vector2: glass.Origin)
    );
    private static Door[] Derive(WorldDefinition definition) {
        var catalog = WorldFaceCatalog.For(definition: definition);
        var doors = new List<Door>();

        foreach (var placement in definition.Placements) {
            foreach (var face in (placement?.FaceSources ?? [])) {
                if (
                    (face?.Portal is not { Arrival: WorldPortalArrival.Mapped } portal) ||
                    !SameWorld(definition: definition, destination: portal.Destination) ||
                    !catalog.TryFind(placementId: placement!.Id, faceName: face.Face, out var row) ||
                    !WorldPortalCounterpart.TryResolve(counterpart: portal.Counterpart, definition: definition, face: out var counterpartFace, placement: out var counterpartPlacement, reason: out _) ||
                    !catalog.TryFind(placementId: counterpartPlacement!.Id, faceName: counterpartFace!.Face, out var counterpartRow)
                ) {
                    continue;
                }

                var source = row.Frame;
                var destination = counterpartRow.Frame;

                doors.Add(item: new Door(
                    Carry: WorldPoseCarry.FromFrames(destination: in destination, source: in source),
                    Counterpart: WorldFaceGeometry.FromFrame(frame: destination),
                    Glass: WorldFaceGeometry.FromFrame(frame: source)
                ));
            }
        }

        return [.. doors];
    }
    private static bool SameWorld(WorldDefinition definition, string destination) =>
        ((definition.DocumentId is { Length: > 0 } own) &&
        (WorldDefinitionRows.FindDestination(destinations: definition.Destinations, name: destination) is { } row) &&
        (WorldDefinitionRows.FindReference(references: definition.References, name: row.Reference)?.Document is { } document) &&
        string.Equals(
            a: Path.GetFileNameWithoutExtension(path: document),
            b: own,
            comparisonType: StringComparison.Ordinal
        ));
}
