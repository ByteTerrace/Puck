using System.Globalization;
using System.Numerics;
using Puck.Maths;
using Puck.SdfVm.Views;
using Puck.SignedDistance.Queries;

namespace Puck.World.Client;

/// <summary>What the chase eye's clearance did on its latest frame.</summary>
public enum WorldCameraClearanceMode {
    /// <summary>Nothing was resolved yet, or the boom has no length.</summary>
    None,
    /// <summary>The boom stands at its whole length on the rig's own elevation.</summary>
    Open,
    /// <summary>Geometry shortens the boom along the rig's own elevation.</summary>
    Shortened,
    /// <summary>No length of the rig's own boom clears both the geometry and the body, so the boom rises over her.</summary>
    Risen,
    /// <summary>No rise up to <see cref="WorldCameraClearance.LiftCeiling"/> clears both, so the boom rises to the
    /// ceiling, the eye stands just outside the body there, and the near plane cuts away the geometry between them
    /// (<see cref="WorldCameraClearance.Near"/>).</summary>
    Crowded,
}

/// <summary>
/// <para>Places one seat's chase eye where it sees its subject from outside, as a spring arm: out of rendered static
/// geometry, out of the framed body (her collider and the shapes she is drawn with), with a clear sightline to the
/// target. Obstruction first shortens the boom along its own direction. When no length of that boom clears both the
/// geometry and the body, as with the body backed into a wall or a corner, the boom rises toward the seat's up, at most
/// to <see cref="LiftCeiling"/>, until one does. The boom's heading never changes, so the frame the player steers in
/// stays the frame the player sees.</para>
/// <para>The arm holds its state across frames as a fraction of the authored boom and a rise above the rig's own
/// elevation, never as a length, and both ease toward each frame's answer on the rig's own boom dynamics: a pull-in
/// and a rise are smoothed rather than cut, and the arm eases back out to the authored boom once the obstruction
/// clears. Two limits hold on the eased value every frame: the eye's centre never comes within
/// <see cref="SurfaceSkin"/> of geometry along the boom, and the eye never comes within <see cref="EyeRadius"/> of the
/// body. Presentation-only: nothing here feeds movement.</para>
/// </summary>
public sealed class WorldCameraClearance {
    /// <summary>The radius, in world units, of the sphere the eye keeps clear of geometry and of the framed body.</summary>
    public const float EyeRadius = 0.3f;
    /// <summary>The steepest elevation a crowded boom rises to, in radians above the seat's level plane: steep enough
    /// to look down past her head, short of an overhead view that turns her upside down on screen.</summary>
    public const float LiftCeiling = 1.1f;
    /// <summary>The nearest, in world units, the eased eye's centre comes to geometry along the boom: a sudden
    /// obstruction cuts the eye in to here at once, and the ease carries it the rest of the way.</summary>
    public const float SurfaceSkin = 0.1f;

    // A crowded boom's rise is scanned in this many even steps up to the ceiling, then bisected this many times between
    // the last blocked step and the first clear one, so the rise follows the obstruction by a fine angle rather than
    // jumping a whole step.
    private const int LiftRefinements = 4;
    private const int LiftSteps = 6;
    // A boom shorter than this has no direction to clear along.
    private const float MinimumBoom = 1e-4f;
    // How far short of the body's nearest surface a cutaway's near plane stops.
    private const float NearMargin = 0.05f;
    // RetractionSkin as a length: how far short of a hit Clear reports the room.
    private const float RetractionSkinLength = 0.05f;

    // A target inside geometry is inside a shell around the pivot, such as a portal frame a body walks through; the sweep
    // starts this far out along the boom, past that shell.
    private static readonly FixedQ4816 ObstructedTargetSkin = FixedQ4816.FromDouble(value: 1.1);
    private static readonly FixedQ4816 RetractionSkin = FixedQ4816.FromDouble(value: RetractionSkinLength);

    private SdfCameraDynamics m_cachedDynamics;
    private SecondOrderResponse m_cachedResponse;
    // X is the eased fraction of the authored boom, Y the eased rise in radians; Z is unused.
    private SecondOrderFollower3 m_arm;
    private bool m_hasCachedResponse;

    /// <summary>Gets the authored boom length, in world units, the latest frame resolved against.</summary>
    public float AuthoredLength { get; private set; }
    /// <summary>Gets the eye the latest frame placed, in world units.</summary>
    public Vector3 Eye { get; private set; }
    /// <summary>Gets the boom length, in world units, the latest frame placed the eye at.</summary>
    public float Length { get; private set; }
    /// <summary>Gets what the latest frame's clearance answer was before easing.</summary>
    public WorldCameraClearanceMode Mode { get; private set; }
    /// <summary>Gets the near-plane distance, in world units along the sightline, the latest frame's image begins at:
    /// zero when the eye sees the target with nothing between; otherwise far enough to cut away the geometry between an
    /// eye the body put beyond it and the target's side of it, and never as far as the body's own nearest surface, so
    /// the near plane never cuts into her.</summary>
    public float Near { get; private set; }
    /// <summary>Gets the rise, in radians, the latest frame lifted the boom above the rig's own elevation.</summary>
    public float Rise { get; private set; }
    /// <summary>Gets the target the latest frame looked at, in world units.</summary>
    public Vector3 Target { get; private set; }

    // How far along the boom the eye can travel from the target before geometry, swept by a sphere of radius, stops it or
    // its sightline, up to the boom's whole length.
    private static float Clear(SdfFieldEvaluator field, bool targetObstructed, Vector3 target, Vector3 direction, float length, FixedQ4816 radius) {
        var fixedLength = FixedQ4816.FromDouble(value: length);
        var skin = (targetObstructed
            ? ObstructedTargetSkin
            : FixedQ4816.Zero
        );

        if (skin >= fixedLength) {
            return length;
        }

        var fixedDirection = FixedVector3.FromVector3(value: direction).Normalize();
        var origin = (FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: target)) + (fixedDirection * skin));

        if (
            targetObstructed &&
            field.Overlap(
                center: origin,
                radius: radius
            )
        ) {
            // The shell reaches past the skin, so this ray has no clearance answer; the boom stands as authored.
            return length;
        }

        if (!field.SphereCast(
            dir: fixedDirection,
            hit: out var hit,
            maxDist: (fixedLength - skin),
            origin: origin,
            radius: radius
        )) {
            return length;
        }

        return ((float)((double)(skin + FixedQ4816.Max(
            x: FixedQ4816.Zero,
            y: (hit.Distance - RetractionSkin)
        ))));
    }
    private static Vector3 Lifted(Vector3 level, Vector3 up, float elevation) =>
        ((level * MathF.Cos(x: elevation)) + (up * MathF.Sin(x: elevation)));

    // One frame's clearance question, asked of the geometry and the body along any direction of the boom.
    private readonly ref struct Probe {
        private readonly SdfFieldEvaluator? m_field;
        private readonly WorldChaseBody m_subject;
        private readonly Vector3 m_target;
        private readonly bool m_targetObstructed;

        public Probe(SdfFieldEvaluator? field, bool targetObstructed, Vector3 target, in WorldChaseBody subject, float length) {
            m_field = field;
            m_subject = subject;
            m_target = target;
            m_targetObstructed = targetObstructed;
            Length = length;
        }

        public float Length { get; }

        // How far out the eye must stand to clear the body.
        public float KeepOut(Vector3 direction) => m_subject.Exit(
            direction: direction,
            origin: m_target,
            radius: EyeRadius
        );
        // How far out geometry lets an eye of the given radius stand.
        public float Room(Vector3 direction, FixedQ4816 radius) => ((m_field is null)
            ? Length
            : Clear(
                direction: direction,
                field: m_field,
                length: Length,
                radius: radius,
                target: m_target,
                targetObstructed: m_targetObstructed
            )
        );
    }

    // Whether the eye fits on one direction, past the body and short of geometry, and the room geometry leaves there.
    private static bool Fits(in Probe probe, Vector3 direction, out float room) {
        room = probe.Room(
            direction: direction,
            radius: FixedQ4816.FromDouble(value: EyeRadius)
        );

        return (room >= probe.KeepOut(direction: direction));
    }
    // Where the eye belongs this frame before easing: the lowest rise at which it fits, and how far out it stands there.
    private static (WorldCameraClearanceMode Mode, float Rise, float Length) Goal(in Probe probe, Vector3 direction, Vector3 level, Vector3 up, float elevation, float maxRise) {
        var fits = Fits(
            direction: direction,
            probe: in probe,
            room: out var room
        );

        // A whole boom shorter than the body is no obstruction to rise over: the body pushes the eye out along it.
        if (room >= probe.Length) {
            return (WorldCameraClearanceMode.Open, 0f, room);
        }

        if (fits) {
            return (WorldCameraClearanceMode.Shortened, 0f, room);
        }

        var below = 0f;

        for (var step = 1; (step <= LiftSteps); step++) {
            var candidate = ((maxRise * step) / LiftSteps);

            if (Fits(
                direction: Lifted(
                    elevation: (elevation + candidate),
                    level: level,
                    up: up
                ),
                probe: in probe,
                room: out var lifted
            )) {
                for (var refinement = 0; (refinement < LiftRefinements); refinement++) {
                    var middle = ((below + candidate) * 0.5f);

                    if (Fits(
                        direction: Lifted(
                            elevation: (elevation + middle),
                            level: level,
                            up: up
                        ),
                        probe: in probe,
                        room: out var refined
                    )) {
                        candidate = middle;
                        lifted = refined;
                    } else {
                        below = middle;
                    }
                }

                return (WorldCameraClearanceMode.Risen, candidate, lifted);
            }

            below = candidate;
        }

        // Nothing fits, so the eye goes up and over at the ceiling, just outside her, and the near plane cuts away
        // whatever stands between: looking down over her head frames her where a level boom this short frames her
        // waist.
        return (WorldCameraClearanceMode.Crowded, maxRise, 0f);
    }

    /// <summary>Drops the arm's eased state, so the next <see cref="Resolve"/> places the eye at that frame's answer
    /// at once: the cut a caller wants when the seat stops framing its body or the framing jumps.</summary>
    public void Reset() {
        m_arm.Reseed();
        Mode = WorldCameraClearanceMode.None;
    }
    /// <summary>Returns where the chase eye stands this frame, and eases the arm toward it.</summary>
    /// <param name="field">The static geometry the eye keeps out of, or <see langword="null"/> for none, which keeps
    /// the eye out of the framed body alone.</param>
    /// <param name="desiredEye">The eye the rig resolved, in world units.</param>
    /// <param name="target">The point the eye looks at, in world units; the boom runs from here to
    /// <paramref name="desiredEye"/>.</param>
    /// <param name="subject">The body the eye frames and keeps out of.</param>
    /// <param name="up">The seat's up axis, which a crowded boom rises toward; zero counts as world up.</param>
    /// <param name="dynamics">The rig's boom dynamics, which the arm eases on; <see cref="SdfCameraDynamics.None"/>
    /// places the eye at each frame's answer with no ease.</param>
    /// <param name="deltaSeconds">The frame step, in seconds.</param>
    /// <returns><paramref name="desiredEye"/> itself when nothing obstructs it, the eye is outside the body and the arm
    /// rests at the authored boom; otherwise the eased, shortened or risen eye.</returns>
    public Vector3 Resolve(SdfFieldEvaluator? field, Vector3 desiredEye, Vector3 target, in WorldChaseBody subject, Vector3 up, in SdfCameraDynamics dynamics, float deltaSeconds) {
        var offset = (desiredEye - target);
        var length = offset.Length();

        Target = target;
        AuthoredLength = length;

        if (!(length > MinimumBoom)) {
            Reset();
            Eye = desiredEye;
            Length = length;
            Near = 0f;
            Rise = 0f;

            return desiredEye;
        }

        var direction = (offset / length);
        var upAxis = ((up.LengthSquared() > 0f)
            ? Vector3.Normalize(value: up)
            : Vector3.UnitY
        );
        var lift = Vector3.Dot(
            vector1: direction,
            vector2: upAxis
        );
        var level = (direction - (upAxis * lift));
        var levelLength = level.Length();
        var elevation = MathF.Asin(x: Math.Clamp(
            max: 1f,
            min: -1f,
            value: lift
        ));
        // A boom already straight up or past the ceiling has no rise left to take.
        var maxRise = ((levelLength > MinimumBoom)
            ? MathF.Max(
                x: 0f,
                y: (LiftCeiling - elevation)
            )
            : 0f
        );

        level = ((levelLength > MinimumBoom)
            ? (level / levelLength)
            : Vector3.Zero
        );

        var probe = new Probe(
            field: field,
            length: length,
            subject: in subject,
            target: target,
            targetObstructed: ((field is not null) && field.Overlap(
                center: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: target)),
                radius: FixedQ4816.FromDouble(value: EyeRadius)
            ))
        );
        var (mode, goalRise, goalLength) = Goal(
            direction: direction,
            elevation: elevation,
            level: level,
            maxRise: maxRise,
            probe: in probe,
            up: upAxis
        );
        var eased = Ease(
            deltaSeconds: deltaSeconds,
            dynamics: in dynamics,
            goal: new Vector3(
                x: (goalLength / length),
                y: goalRise,
                z: 0f
            )
        );
        var rise = Math.Clamp(
            max: maxRise,
            min: 0f,
            value: eased.Y
        );
        var presented = ((rise > 0f)
            ? Lifted(
                elevation: (elevation + rise),
                level: level,
                up: upAxis
            )
            : direction
        );
        var keepOut = probe.KeepOut(direction: presented);
        var held = MathF.Min(
            x: (Math.Clamp(
                max: 1f,
                min: 0f,
                value: eased.X
            ) * length),
            y: probe.Room(
                direction: presented,
                radius: FixedQ4816.FromDouble(value: SurfaceSkin)
            )
        );
        // The body outranks the geometry: an eye that cannot stand clear of both stands outside her.
        var placed = MathF.Max(
            x: held,
            y: keepOut
        );

        if ((placed / length) != eased.X) {
            // A limit moved the eye, so the arm eases on from where the eye is rather than from where it was going.
            m_arm.Value.X = (placed / length);
            m_arm.Velocity.X = 0f;
        }

        // Geometry between the eye and the target's side of it (an eye the body put beyond a wall) is cut away: the image
        // begins at the face nearest the target, and the near plane stops short of every part of the body in view.
        var face = probe.Room(
            direction: presented,
            radius: FixedQ4816.Zero
        );

        Near = (((face < length) && ((face + RetractionSkinLength) < placed))
            ? Math.Clamp(
                max: MathF.Max(
                    x: 0f,
                    y: (subject.NearestDepth(
                        eye: (target + (presented * placed)),
                        forward: -presented
                    ) - NearMargin)
                ),
                min: 0f,
                value: (placed - (face + RetractionSkinLength))
            )
            : 0f
        );
        Mode = mode;
        Rise = rise;
        Length = placed;
        Eye = (((rise == 0f) && (placed == length))
            ? desiredEye
            : (target + (presented * placed))
        );

        return Eye;
    }
    // Steps the arm toward this frame's goal on the rig's dynamics, or lands on it where there is no ease or no history.
    private Vector3 Ease(in SdfCameraDynamics dynamics, float deltaSeconds, Vector3 goal) {
        if (!dynamics.IsLive) {
            m_arm.Seed(target: goal);
            m_hasCachedResponse = false;

            return goal;
        }

        if (
            !m_hasCachedResponse ||
            (m_cachedDynamics != dynamics)
        ) {
            m_cachedResponse = SecondOrderResponse.Create(
                dampingRatio: dynamics.Damping,
                frequencyHz: dynamics.Frequency,
                initialResponse: dynamics.Response
            );
            m_cachedDynamics = dynamics;
            m_hasCachedResponse = true;
        }

        return m_arm.Step(
            deltaSeconds: deltaSeconds,
            response: in m_cachedResponse,
            target: goal
        );
    }
    /// <summary>Returns the read-back of the latest frame: <c>eye=(x,y,z) target=(x,y,z) boom=l/a rise=d near=n
    /// clearance=m</c>, the boom in world units placed of authored, the rise in degrees and the near plane in world
    /// units.</summary>
    /// <returns>The read-back text.</returns>
    public string Describe() => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"eye=({Eye.X:0.###},{Eye.Y:0.###},{Eye.Z:0.###}) target=({Target.X:0.###},{Target.Y:0.###},{Target.Z:0.###}) boom={Length:0.###}/{AuthoredLength:0.###} rise={float.RadiansToDegrees(radians: Rise):0.#} near={Near:0.###} clearance={Mode.ToString().ToLowerInvariant()}"
    );
}
