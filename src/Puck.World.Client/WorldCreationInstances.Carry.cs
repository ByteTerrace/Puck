using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;

namespace Puck.World.Client;

public sealed partial class WorldCreationInstances {
    /// <summary>Resolves where a carried screen's picture is this frame (<see cref="WorldScreen.Carry"/>): on the front
    /// face of the carrying look's box, for the first live body wearing that look whose pose frame holding now is the
    /// carry's pose, read off the latch the last <see cref="PackTransforms"/> left, so the picture rides the box the frame
    /// draws.</summary>
    /// <param name="screen">The screen row.</param>
    /// <param name="client">The client whose interpolated body poses root the body-rooted stamps.</param>
    /// <param name="carried">The screen row at the box's face, its frame and half-extents the box's, or
    /// <see langword="null"/> when no body carries it.</param>
    /// <returns><see langword="true"/> when a body carries the screen this frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screen"/> or <paramref name="client"/> is
    /// <see langword="null"/>.</exception>
    public bool TryCarriedScreen(WorldScreen screen, IWorldStampSource client, [NotNullWhen(returnValue: true)] out WorldScreen? carried) {
        ArgumentNullException.ThrowIfNull(argument: screen);
        ArgumentNullException.ThrowIfNull(argument: client);

        carried = null;

        if (screen.Carry is not { } carry) {
            return false;
        }

        foreach (var live in m_pool) {
            if (
                (live?.BodyIndex is not { } body) ||
                !client.IsActive(index: body) ||
                !string.Equals(a: live.Look?.Name.Value, b: carry.Look, comparisonType: StringComparison.Ordinal) ||
                (live.PoseFrame == 0) ||
                (live.PoseFrame != FrameNumber(document: live.Creation.Document, name: carry.Pose)) ||
                (CarriedShapeSlot(document: live.Creation.EngineDocument, carry: carry) is not { } slot)
            ) {
                continue;
            }

            var shapes = live.Creation.EngineDocument.Shapes!;
            var pose = LatchedShapePose(
                client: client,
                live: live,
                shapeSlot: slot,
                shapes: shapes
            );
            var scale = FollowedRootPose(
                client: client,
                live: live
            ).Scale;
            // The box's own axes, which the creation frame's half turn about up leaves as authored: its front face (the
            // side a body holding it looks at) is -Z, so the picture's right as that body looks is -X and its up +Y.
            var half = (((Vector3)shapes[slot].Scale) * scale);
            var normal = Vector3.Transform(value: -Vector3.UnitZ, rotation: pose.Orientation);

            carried = screen with {
                Origin = (pose.Position + (normal * half.Z)),
                Right = Vector3.Transform(value: -Vector3.UnitX, rotation: pose.Orientation),
                Up = Vector3.Transform(value: Vector3.UnitY, rotation: pose.Orientation),
                HalfWidth = half.X,
                HalfHeight = half.Y,
                HalfDepth = half.Z,
                Round = 0f,
            };

            return true;
        }

        return false;
    }
    /// <summary>Resolves a screen row to where its picture is this frame: the row at its carrier's box while a body carries
    /// it (<see cref="TryCarriedScreen"/>), else the row as authored.</summary>
    /// <param name="screen">The screen row.</param>
    /// <param name="client">The client whose interpolated body poses root the body-rooted stamps.</param>
    /// <returns>The screen row at its carried frame, or <paramref name="screen"/> itself when no body carries it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screen"/> or <paramref name="client"/> is
    /// <see langword="null"/>.</exception>
    public WorldScreen Carried(WorldScreen screen, IWorldStampSource client) => (TryCarriedScreen(
        carried: out var held,
        client: client,
        screen: screen
    )
        ? held
        : screen);
    /// <summary>Creates the screen-surface transform providers of every engine screen slot, polled every frame: a carried
    /// screen's (<see cref="WorldScreen.Carry"/>) answers the frame its carrier holds it at, or its authored frame while no
    /// body carries it, so its picture rides the carrier's hands and returns to the device when put down; every other slot
    /// answers <see langword="null"/> and keeps the frame its program declares.</summary>
    /// <param name="client">The client whose definition names the screens and whose interpolated body poses root the
    /// body-rooted stamps.</param>
    /// <returns>One provider per slot below <see cref="SdfProgramBuilder.MaxScreenSurfaces"/>, keyed by screen index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>> ScreenSurfaceTransforms(IWorldStampSource client) {
        ArgumentNullException.ThrowIfNull(argument: client);

        var providers = new Dictionary<int, Func<SdfScreenSurfaceTransform?>>(capacity: SdfProgramBuilder.MaxScreenSurfaces);

        for (var index = 0; (index < SdfProgramBuilder.MaxScreenSurfaces); index++) {
            var slot = index;

            providers[index] = () => SurfaceTransform(
                client: client,
                index: slot
            );
        }

        return providers;
    }

    // The frame a carried screen's surface takes this frame; null for a screen no body can carry.
    private SdfScreenSurfaceTransform? SurfaceTransform(IWorldStampSource client, int index) {
        var screens = client.Definition.Screens;

        for (var row = 0; (row < screens.Count); row++) {
            var screen = screens[row];

            if ((screen.Index != index) || (screen.Carry is null)) {
                continue;
            }

            var frame = Carried(
                client: client,
                screen: screen
            );

            return new SdfScreenSurfaceTransform(
                HalfHeight: frame.HalfHeight,
                HalfWidth: frame.HalfWidth,
                Origin: frame.Origin,
                Right: Vector3.Normalize(value: frame.Right),
                Up: Vector3.Normalize(value: frame.Up)
            );
        }

        return null;
    }
    // The 1-based timeline frame a frame name selects, or 0 for none — the numbering PoseFrame holds.
    private static int FrameNumber(CreationDocument document, string name) {
        var frames = (document.Frames ?? []);

        for (var frame = 0; (frame < frames.Count); frame++) {
            if (string.Equals(a: frames[frame]?.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return (frame + 1);
            }
        }

        return 0;
    }
    // The shape slot of the box a carry names, or null when the creation has none by that name.
    private static int? CarriedShapeSlot(CreationDocument document, WorldScreenCarry carry) {
        var shapes = (document.Shapes ?? []);

        for (var slot = 0; ((slot < shapes.Count)); slot++) {
            if (string.Equals(a: shapes[slot].Name?.Value, b: carry.Shape, comparisonType: StringComparison.Ordinal)) {
                return slot;
            }
        }

        return null;
    }
    // The screen material each shape of a body's look shows instead of its palette's, by shape slot: a carried screen's
    // box is drawn in that screen's material (SdfProgramBuilder.ScreenMaterialId + 1 + its index), so the picture the
    // frame poses on it (TryCarriedScreen) shades there. Null when the look carries no screen.
    private static int[]? CarriedScreenMaterials(Registration live, IReadOnlyList<WorldScreen>? screens) {
        if ((screens is null) || (live.Look is not { } look)) {
            return null;
        }

        int[]? materials = null;

        foreach (var screen in screens) {
            if (
                (screen.Carry is not { } carry) ||
                !string.Equals(a: carry.Look, b: look.Name.Value, comparisonType: StringComparison.Ordinal) ||
                (CarriedShapeSlot(document: live.Creation.EngineDocument, carry: carry) is not { } slot)
            ) {
                continue;
            }

            materials ??= Enumerable.Repeat(count: (live.Creation.EngineDocument.Shapes?.Count ?? 0), element: -1).ToArray();
            materials[slot] = ((SdfProgramBuilder.ScreenMaterialId + 1) + screen.Index);
        }

        return materials;
    }
}
