using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;

namespace Puck.World.Client;

public sealed partial class WorldCreationInstances {
    /// <summary>Returns whether any live body carries <paramref name="screen"/> this frame, without computing its
    /// carried screen pose or touching the carrier's cached screen table.</summary>
    /// <param name="screen">The screen row.</param>
    /// <param name="client">The client whose active body flags are checked.</param>
    /// <returns><see langword="true"/> when a live body currently holds the carry pose for <paramref name="screen"/>.</returns>
    public bool IsCarriedScreen(WorldScreen screen, IWorldStampSource client) {
        ArgumentNullException.ThrowIfNull(argument: screen);
        ArgumentNullException.ThrowIfNull(argument: client);

        return ((screen.Carry is { } carry) && TryFindCarrier(carry: carry, client: client, live: out _, shapeSlot: out _));
    }
    /// <summary>Resolves where a carried screen's picture is this frame (<see cref="WorldScreen.Carry"/>): on the front
    /// face of its anchor's box (the first live body wearing the carry's look whose pose frame holding now is the carry's
    /// pose, or the first listed attached row riding a live body whose shown creation has the box), read off the latch
    /// the last <see cref="PackTransforms"/> left, so the picture rides the box the frame draws.</summary>
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

        if ((screen.Carry is not { } carry) || !TryFindCarrier(carry: carry, client: client, live: out var live, shapeSlot: out var slot)) {
            return false;
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

        carried = CarriedFrame(
            half: half,
            live: live,
            origin: (pose.Position + (normal * half.Z)),
            right: Vector3.Transform(value: -Vector3.UnitX, rotation: pose.Orientation),
            screen: screen,
            up: Vector3.Transform(value: Vector3.UnitY, rotation: pose.Orientation)
        );

        return true;
    }
    // The registration whose box shows a carried screen's picture this frame, and the box's shape slot: for a look carry,
    // the first live body wearing the look whose latched pose frame is the carry's; for a placement carry, the first
    // listed attached row whose carrier is live and whose shown creation has the box.
    private bool TryFindCarrier(WorldScreenCarry carry, IWorldStampSource client, out Registration live, out int shapeSlot) {
        if (carry.Placements is { Count: > 0 } placements) {
            foreach (var placementId in placements) {
                if (
                    m_rowSlots.TryGetValue(key: placementId, value: out var poolIndex) &&
                    (m_pool[poolIndex] is { Row.Attach: { } attach } candidate) &&
                    WorldAttachedPose.IsRiding(attach: attach, source: client) &&
                    (carry.ShapeSlotIn(shapes: candidate.Creation.EngineDocument.Shapes) is { } slot)
                ) {
                    live = candidate;
                    shapeSlot = slot;

                    return true;
                }
            }
        } else {
            foreach (var candidate in m_pool.AsSpan(0, m_poolCount)) {
                if (
                    (candidate?.BodyIndex is not { } body) ||
                    !Shows(live: candidate) ||
                    !client.IsActive(index: body) ||
                    !string.Equals(a: candidate.Look?.Name.Value, b: carry.Look, comparisonType: StringComparison.Ordinal) ||
                    (candidate.PoseFrame == 0) ||
                    (candidate.PoseFrame != FrameNumber(document: candidate.Creation.Document, name: carry.Pose)) ||
                    (carry.ShapeSlotIn(shapes: candidate.Creation.EngineDocument.Shapes) is not { } slot)
                ) {
                    continue;
                }

                live = candidate;
                shapeSlot = slot;

                return true;
            }
        }

        live = null!;
        shapeSlot = -1;

        return false;
    }
    // The screen row at a carried frame. The presenter, the scene emitter and the surface providers each ask for the same
    // frame, so the row built for it is kept on the carrier and handed back while the frame it was built for repeats.
    private static WorldScreen CarriedFrame(Registration live, WorldScreen screen, Vector3 origin, Vector3 right, Vector3 up, Vector3 half) {
        var built = (live.CarriedScreens ??= new Dictionary<WorldScreen, WorldScreen>(comparer: ReferenceEqualityComparer.Instance));

        if (
            built.TryGetValue(key: screen, value: out var held) &&
            (held.Origin == origin) && (held.Right == right) && (held.Up == up) &&
            (held.HalfWidth == half.X) && (held.HalfHeight == half.Y) && (held.HalfDepth == half.Z)
        ) {
            return held;
        }

        // A screen row edited in the document is a new instance, so the old key never repeats; the few a carrier holds
        // are dropped together rather than tracked.
        if (built.Count >= MaxCarriedScreens) {
            built.Clear();
        }

        return (built[screen] = (screen with {
            Origin = origin,
            Right = right,
            Up = up,
            HalfWidth = half.X,
            HalfHeight = half.Y,
            HalfDepth = half.Z,
            Round = 0f,
        }));
    }
    private const int MaxCarriedScreens = 4;
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
    /// <returns>One provider per screen index the client's definition declares (<see cref="WorldPlacementPolicy.ScreenSpan"/>),
    /// keyed by screen index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>> ScreenSurfaceTransforms(IWorldStampSource client) {
        ArgumentNullException.ThrowIfNull(argument: client);

        var span = WorldPlacementPolicy.ScreenSpan(definition: client.Definition);
        var providers = new Dictionary<int, Func<SdfScreenSurfaceTransform?>>(capacity: span);

        for (var index = 0; (index < span); index++) {
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
    private static int FrameNumber(CreationDocument document, string? name) {
        var frames = (document.Frames ?? []);

        for (var frame = 0; (frame < frames.Count); frame++) {
            if (string.Equals(a: frames[frame]?.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return (frame + 1);
            }
        }

        return 0;
    }
    // The screen material each shape of a registration shows instead of its palette's, by shape slot: a carried screen's
    // box is drawn in that screen's material (SdfProgramBuilder.ScreenMaterialId + 1 + its index), so the picture the
    // frame poses on it (TryCarriedScreen) shades there. A body's look carries the screens whose carry names the look, an
    // attached row the screens whose carry lists the row. Null when the registration carries no screen.
    private static int[]? CarriedScreenMaterials(Registration live, IReadOnlyList<WorldScreen>? screens) {
        if ((screens is null) || ((live.Look is null) && (live.Row?.Attach is null))) {
            return null;
        }

        int[]? materials = null;

        foreach (var screen in screens) {
            if (
                (screen.Carry is not { } carry) ||
                !Carries(carry: carry, live: live) ||
                (carry.ShapeSlotIn(shapes: live.Creation.EngineDocument.Shapes) is not { } slot)
            ) {
                continue;
            }

            materials ??= Enumerable.Repeat(count: (live.Creation.EngineDocument.Shapes?.Count ?? 0), element: -1).ToArray();
            materials[slot] = ((SdfProgramBuilder.ScreenMaterialId + 1) + screen.Index);
        }

        return materials;
    }
    // Whether a carry's anchor is this registration: its look, or one of the carry's listed rows.
    private static bool Carries(WorldScreenCarry carry, Registration live) => ((carry.Placements is { Count: > 0 } placements)
        ? ((live.Row is { Attach: not null } row) && placements.Contains(value: row.Id, comparer: StringComparer.Ordinal))
        : ((live.Look is { } look) && string.Equals(a: carry.Look, b: look.Name.Value, comparisonType: StringComparison.Ordinal)));
}
