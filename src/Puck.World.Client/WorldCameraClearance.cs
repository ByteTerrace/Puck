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
    /// <summary>No length of the rig's own boom keeps the eye far enough out (past the minimum boom, and past the body
    /// under cutaway), so the boom rises over the obstruction.</summary>
    Risen,
    /// <summary>No rise up to the ceiling clears both under cutaway, so the boom rises to the ceiling, the eye stands
    /// just outside the body there, and the near plane cuts away the geometry between them
    /// (<see cref="WorldCameraClearance.Near"/>).</summary>
    Crowded,
}
/// <summary>
/// <para>Places one seat's chase eye as a spring arm by the camera program's authored clearance
/// (<see cref="SdfCameraClearance"/>), migrating its target toward the authored close target as it compresses. Every
/// number is the program's. Without cutaway the eye keeps the room: it stays short of every wall and level, past the
/// minimum boom where a rise allows it. Under cutaway the eye keeps outside the subject: out of rendered static
/// geometry, out of the framed body (its collider and the shapes it is drawn with), with a clear sightline to the
/// target. Either may fade the subject in this seat's view as the eye approaches. Obstruction first shortens the boom
/// along its own direction. When no length of that boom keeps the eye far enough out, as with the body backed into a
/// wall or a corner, the boom rises toward the seat's up, at most the authored ceiling above the rig's own elevation,
/// until one does. The boom's heading never changes, so the frame the player steers in stays the frame the player
/// sees.</para>
/// <para>The arm holds its state across frames as a fraction of the authored boom and a rise above the rig's own
/// elevation, never as a length, and both ease toward each frame's answer on the rig's own boom dynamics: a pull-in
/// and a rise are smoothed rather than cut, and the arm eases back out to the authored boom once the obstruction
/// clears. Two limits hold on the eased value every frame: the eye's centre never comes within the authored surface
/// skin (<see cref="SdfCameraClearance.SurfaceSkin"/>) of geometry along the boom, except where cutaway's body
/// clearance wins; under cutaway the eye never comes within the authored eye radius
/// (<see cref="SdfCameraClearance.EyeRadius"/>) of the body. Distances scale with the live body.
/// Presentation-only: nothing here feeds movement.</para>
/// </summary>
public sealed class WorldCameraClearance {
    // A crowded boom's rise is scanned in this many even steps up to the ceiling, then bisected this many times between
    // the last blocked step and the first clear one, so the rise follows the obstruction by a fine angle rather than
    // jumping a whole step.
    private const int LiftRefinements = 4;
    private const int LiftSteps = 6;
    // A boom shorter than this has no direction to clear along.
    private const float MinimumBoom = 1e-4f;
    // The highest elevation, in radians, a rise lifts the boom to: short of straight up, where the boom has no heading
    // and the view's up flips.
    private const float MaximumElevation = 1.5f;
    // The full-size margin, in metres, a clearance sweep is cast wider than the eye it was asked for, so its answer stands
    // while the boom drifts by less (ClearanceQuery.Covers): the eye's effective radius moves by at most twice this.
    private const float ReuseMargin = (1f / 256f);
    // The clearance questions one frame asks, each with its own held answer: the rest probe, the goal's first fit, each
    // rise step and its refinements, the held eye, the cutaway face, and the first-person room.
    private const int RestQuestion = 0;
    private const int GoalQuestion = 1;
    private const int LiftQuestion = 2;
    private const int RefineQuestion = (LiftQuestion + LiftSteps);
    private const int HeldQuestion = (RefineQuestion + LiftRefinements);
    private const int FaceQuestion = (HeldQuestion + 1);
    private const int FirstPersonQuestion = (FaceQuestion + 1);
    private const int Questions = (FirstPersonQuestion + 1);

    private readonly WorldFieldAnswer<ClearanceQuery, float>[] m_answers = new WorldFieldAnswer<ClearanceQuery, float>[Questions];

    private SdfCameraDynamics m_cachedDynamics;
    private SecondOrderResponse m_cachedResponse;
    // X is the eased fraction of the authored boom, Y the eased rise in radians; Z is unused.
    private SecondOrderFollower3 m_arm;
    private SecondOrderFollower3 m_targetShift;
    private bool m_hasCachedResponse;

    /// <summary>Gets the clearance the latest frame resolved against, or <see langword="null"/> when its program
    /// authors none and the eye passed through.</summary>
    public SdfCameraClearance? Parameters { get; private set; }
    /// <summary>Gets the body's opacity in this seat's own view, from hidden to solid.</summary>
    public float BodyOpacity { get; private set; } = 1f;

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
    /// the near plane never cuts into the body.</summary>
    public float Near { get; private set; }
    /// <summary>Gets the rise, in radians, the latest frame lifted the boom above the rig's own elevation.</summary>
    public float Rise { get; private set; }
    /// <summary>Gets the target the latest frame looked at, in world units.</summary>
    public Vector3 Target { get; private set; }

    /// <summary>Measures room behind an authored eye independently of the currently placed camera, through this arm's
    /// own held answer for that question.</summary>
    /// <param name="field">The static geometry, or <see langword="null"/> for none, which leaves the whole length.</param>
    /// <param name="head">The eye the room is measured from, in world units.</param>
    /// <param name="backward">The unit direction the room is measured along.</param>
    /// <param name="length">The longest room reported, in world units.</param>
    /// <param name="scale">The body's live scale, applied to <paramref name="eyeRadius"/> and
    /// <paramref name="retractionSkin"/>.</param>
    /// <param name="eyeRadius">The full-size radius of the sphere swept, in metres.</param>
    /// <param name="retractionSkin">The full-size distance, in metres, short of a hit the room is reported.</param>
    /// <returns>The room, in world units, at most <paramref name="length"/>.</returns>
    public float AvailableRoom(SdfFieldEvaluator? field, Vector3 head, Vector3 backward, float length, float scale, float eyeRadius, float retractionSkin) =>
        Clear(answer: ref m_answers[FirstPersonQuestion], field: field, targetObstructed: false, target: head, direction: backward, length: length,
            radius: FixedQ4816.FromDouble(value: (eyeRadius * scale)), scale: scale, retractionSkin: retractionSkin, obstructedSkin: 0f);

    /// <summary>Publishes the seat's first-person transition through the same view-local fade and read-back.</summary>
    public void ApplyView(WorldCameraViewTransition transition, Vector3 head, Vector3 forward, float hideRadius) {
        var eye = Eye;
        var target = Target;
        var opacity = BodyOpacity;
        transition.Apply(head, forward, hideRadius, ref eye, ref target, ref opacity);
        Eye = eye;
        Target = target;
        BodyOpacity = opacity;
        Length *= (1f - transition.Blend);
        Rise *= (1f - transition.Blend);
        Near *= (1f - transition.Blend);
    }

    // How far along the boom the eye can travel from the target before geometry, swept by a sphere of radius, stops it or
    // its sightline, up to the boom's whole length (all of it with no field): the held answer where it covers the question,
    // otherwise a sweep.
    private static float Clear(ref WorldFieldAnswer<ClearanceQuery, float> answer, SdfFieldEvaluator? field, bool targetObstructed, Vector3 target, Vector3 direction, float length, FixedQ4816 radius, float scale, float retractionSkin, float obstructedSkin) {
        if (field is null) {
            WorldFieldQueryWork.Clearance.Skip();

            return length;
        }

        return answer.Ask(asked: new ClearanceQuery(Direction: direction, Field: field, Length: length, Radius: radius.Value, Scale: scale, Target: target, TargetObstructed: targetObstructed,
            RetractionSkin: retractionSkin, ObstructedSkin: obstructedSkin,
            // A target inside geometry answers through an overlap test a wider sphere would change, so it keeps exact inputs.
            Margin: ((targetObstructed || !float.IsFinite(f: scale) || (scale <= 0f)) ? 0f : (ReuseMargin * scale))));
    }

    // One clearance sweep: a sphere of Radius (raw Q48.16) cast Margin wider, from Target along Direction. Every point of the
    // swept path moves by at most |dTarget| + Length * |dDirection| between two questions, and a signed distance changes no
    // faster than the point it is read at, so a path clear of geometry by Radius + Margin stays clear by Radius after any
    // drift within Margin, and one blocked within Radius + 2 * Margin stays blocked within Radius + Margin. The held room
    // therefore lies between the rooms a sweep of the asked question finds at Radius and at Radius + 2 * Margin, as a fresh
    // sweep's does: the eye never comes nearer geometry than its asked radius, and stands off at most 2 * Margin more.
    private readonly record struct ClearanceQuery(SdfFieldEvaluator Field, bool TargetObstructed, Vector3 Target, Vector3 Direction, float Length, long Radius, float Scale,
        float RetractionSkin, float ObstructedSkin, float Margin) : IWorldFieldQuery<ClearanceQuery, float> {
        public static WorldFieldQueryFamily Family => WorldFieldQueryWork.Clearance;

        public bool Covers(in ClearanceQuery asked) {
            if (
                (TargetObstructed != asked.TargetObstructed) ||
                (Radius != asked.Radius) ||
                !Same(left: Length, right: asked.Length) ||
                !Same(left: Scale, right: asked.Scale) ||
                !Same(left: RetractionSkin, right: asked.RetractionSkin) ||
                !Same(left: ObstructedSkin, right: asked.ObstructedSkin) ||
                !Same(left: Margin, right: asked.Margin)
            ) {
                return false;
            }
            if (Same(left: Target, right: asked.Target) && Same(left: Direction, right: asked.Direction)) {
                return true;
            }

            var held = Direction.Length();
            var now = asked.Direction.Length();

            if (!(Margin > 0f) || !(held > 0f) || !(now > 0f)) {
                return false;
            }

            var drift = (Vector3.Distance(value1: Target, value2: asked.Target) + (Length * Vector3.Distance(value1: (Direction / held), value2: (asked.Direction / now))));

            // Both sweeps start from a target and direction rounded to the field's Q48.16 grid, which the float drift does not
            // see: a few grid steps along the origin and along the boom's length are charged against the margin.
            return ((drift + ((4f + (8f * Length)) / 65536f)) <= Margin);
        }
        public float March() => WorldCameraClearance.March(field: Field, targetObstructed: TargetObstructed, target: Target, direction: Direction, length: Length,
            radius: (FixedQ4816.FromRawBits(value: Radius) + FixedQ4816.FromDouble(value: Margin)), scale: Scale, retractionSkin: RetractionSkin, obstructedSkin: ObstructedSkin);

        private static bool Same(float left, float right) => (BitConverter.SingleToInt32Bits(value: left) == BitConverter.SingleToInt32Bits(value: right));
        private static bool Same(Vector3 left, Vector3 right) => (Same(left: left.X, right: right.X) && Same(left: left.Y, right: right.Y) && Same(left: left.Z, right: right.Z));
    }
    // A target inside geometry is inside a shell around the pivot, such as a portal frame a body walks through; the sweep
    // then starts obstructedSkin out along the boom, past that shell.
    private static float March(SdfFieldEvaluator field, bool targetObstructed, Vector3 target, Vector3 direction, float length, FixedQ4816 radius, float scale, float retractionSkin, float obstructedSkin) {
        var fixedLength = FixedQ4816.FromDouble(value: length);
        var fixedScale = FixedQ4816.FromDouble(value: scale);
        var skin = (targetObstructed
            ? (FixedQ4816.FromDouble(value: obstructedSkin) * fixedScale)
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
            y: (hit.Distance - (FixedQ4816.FromDouble(value: retractionSkin) * fixedScale))
        ))));
    }
    private static Vector3 Lifted(Vector3 level, Vector3 up, float elevation) =>
        ((level * MathF.Cos(x: elevation)) + (up * MathF.Sin(x: elevation)));

    // One frame's clearance question, asked of the geometry and the body along any direction of the boom.
    private readonly ref struct Probe {
        private readonly bool m_cutaway;
        private readonly Span<WorldFieldAnswer<ClearanceQuery, float>> m_answers;
        private readonly SdfFieldEvaluator? m_field;
        private readonly float m_minimumBoom;
        private readonly float m_obstructedSkin;
        private readonly float m_retractionSkin;
        private readonly WorldChaseBody m_subject;
        private readonly Vector3 m_target;
        private readonly bool m_targetObstructed;

        public Probe(Span<WorldFieldAnswer<ClearanceQuery, float>> answers, SdfFieldEvaluator? field, bool targetObstructed, Vector3 target, in WorldChaseBody subject, float length, float scale, in SdfCameraClearance clearance) {
            EyeRadius = (clearance.EyeRadius * scale);
            m_answers = answers;
            m_cutaway = clearance.Cutaway;
            m_field = field;
            m_minimumBoom = (clearance.MinimumBoom * scale);
            m_obstructedSkin = clearance.ObstructedTargetSkin;
            m_retractionSkin = clearance.RetractionSkin;
            m_subject = subject;
            m_target = target;
            m_targetObstructed = targetObstructed;
            Length = length;
            Scale = scale;
        }

        // The eye's radius at the body's live scale, in world units.
        public float EyeRadius { get; }
        public float Length { get; }
        public float Scale { get; }

        // How far out the eye must stand: past the minimum boom, and past the body under cutaway.
        public float KeepOut(Vector3 direction) => (m_cutaway
            ? MathF.Max(x: m_minimumBoom, y: m_subject.Exit(direction: direction, origin: m_target, radius: EyeRadius))
            : m_minimumBoom);
        // How far out geometry lets an eye of the given radius stand, through the held answer of one of the frame's questions.
        public float Room(Vector3 direction, FixedQ4816 radius, int question) => Clear(
            answer: ref m_answers[question],
            direction: direction,
            field: m_field,
            length: Length,
            obstructedSkin: m_obstructedSkin,
            radius: radius,
            retractionSkin: m_retractionSkin,
            target: m_target,
            targetObstructed: m_targetObstructed,
            scale: Scale
        );
    }

    // Whether the eye fits on one direction, past the body and short of geometry, and the room geometry leaves there.
    private static bool Fits(in Probe probe, Vector3 direction, int question, out float room) {
        room = probe.Room(
            direction: direction,
            question: question,
            radius: FixedQ4816.FromDouble(value: probe.EyeRadius)
        );

        return (room >= probe.KeepOut(direction: direction));
    }
    // Where the eye belongs this frame before easing: the lowest rise at which it fits, and how far out it stands there.
    private static (WorldCameraClearanceMode Mode, float Rise, float Length) Goal(in Probe probe, Vector3 direction, Vector3 level, Vector3 up, float elevation, float maxRise, bool crowds) {
        var fits = Fits(
            direction: direction,
            probe: in probe,
            question: GoalQuestion,
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
                question: (LiftQuestion + (step - 1)),
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
                        question: (RefineQuestion + refinement),
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

        // Nothing fits. A cutaway goes up and over at the ceiling, just outside the body, and the near plane cuts away
        // whatever stands between: looking down over the body frames it where a level boom this short would not. A fade
        // cuts nothing away, so it stays level, as far out as the room and its minimum boom allow.
        return (crowds
            ? (WorldCameraClearanceMode.Crowded, maxRise, 0f)
            : (WorldCameraClearanceMode.Shortened, 0f, Math.Min(val1: probe.Length, val2: Math.Max(val1: room, val2: probe.KeepOut(direction: direction)))));
    }

    /// <summary>Drops the arm's eased state, so the next <see cref="Resolve"/> places the eye at that frame's answer
    /// at once: the cut a caller wants when the seat stops framing its body or the framing jumps.</summary>
    public void Reset() {
        m_arm.Reseed();
        m_targetShift.Reseed();
        Mode = WorldCameraClearanceMode.None;
        BodyOpacity = 1f;
    }
    /// <summary>Records a frame whose program authors no clearance: the eye and target pass through, the arm drops
    /// its eased state, and the read-back reports no clearance.</summary>
    /// <param name="desiredEye">The eye the rig resolved, in world units.</param>
    /// <param name="target">The point the eye looks at, in world units.</param>
    public void Pass(Vector3 desiredEye, Vector3 target) {
        Reset();
        Parameters = null;
        Eye = desiredEye;
        Target = target;
        Length = Vector3.Distance(value1: desiredEye, value2: target);
        AuthoredLength = Length;
        Near = 0f;
        Rise = 0f;
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
    /// <param name="clearance">The camera program's resolved clearance, full-size.</param>
    /// <param name="scale">The body's live scale, applied to every authored distance and the close target.</param>
    public Vector3 Resolve(SdfFieldEvaluator? field, Vector3 desiredEye, Vector3 target, in WorldChaseBody subject, Vector3 up, in SdfCameraDynamics dynamics, float deltaSeconds,
        in SdfCameraClearance clearance, float scale = 1f) {
        var offset = (desiredEye - target);
        var length = offset.Length();

        Target = target;
        AuthoredLength = length;
        Parameters = clearance;
        BodyOpacity = 1f;

        if (!(length > MinimumBoom)) {
            Reset();
            Eye = desiredEye;
            Length = length;
            Near = 0f;
            Rise = 0f;

            return desiredEye;
        }

        var direction = (offset / length);
        var cutaway = clearance.Cutaway;
        // Measure compression at the authored pivot, independently of the shifted target. Feeding the migrated
        // target back into this measurement would pump the framing under lintels and overhead stair slabs.
        scale = ((float.IsFinite(f: scale) && (scale > 0f)) ? scale : 1f);
        var closeOffset = (clearance.CloseTarget * scale);
        var restProbe = new Probe(answers: m_answers, clearance: in clearance, field: field, length: length, scale: scale, subject: in subject, target: target, targetObstructed: false);
        var eyeRadius = restProbe.EyeRadius;
        var fraction = ((closeOffset == Vector3.Zero) ? 1f : (restProbe.Room(direction: direction, question: RestQuestion, radius: FixedQ4816.FromDouble(value: eyeRadius)) / length));
        var (startFraction, fullFraction) = (clearance.CloseTargetStartFraction, clearance.CloseTargetFullFraction);
        var shiftWeight = ((startFraction > fullFraction)
            ? Math.Clamp(((startFraction - fraction) / (startFraction - fullFraction)), 0f, 1f)
            : 0f);

        shiftWeight = ((shiftWeight * shiftWeight) * (3f - (2f * shiftWeight)));
        var shift = Ease(dynamics: in dynamics, deltaSeconds: deltaSeconds, goal: new Vector3(x: shiftWeight, y: 0f, z: 0f), follower: ref m_targetShift);
        var displacement = (closeOffset * Math.Clamp(max: 1f, min: 0f, value: shift.X));

        target += displacement;
        desiredEye += displacement;
        Target = target;
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
        // The authored ceiling, less what a boom already straight up, or at the highest elevation, has no room for.
        var maxRise = clearance.MaxRise;

        maxRise = ((levelLength > MinimumBoom)
            ? Math.Clamp(
                max: MathF.Max(x: 0f, y: (MaximumElevation - elevation)),
                min: 0f,
                value: (float.IsFinite(f: maxRise) ? maxRise : 0f)
            )
            : 0f
        );

        level = ((levelLength > MinimumBoom)
            ? (level / levelLength)
            : Vector3.Zero
        );

        var probe = new Probe(
            answers: m_answers,
            scale: scale,
            field: field,
            length: length,
            subject: in subject,
            target: target,
            clearance: in clearance,
            targetObstructed: ((clearance.ObstructedTargetSkin > 0f) && (field is not null) && field.Overlap(
                center: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: target)),
                radius: FixedQ4816.FromDouble(value: eyeRadius)
            ))
        );
        var (mode, goalRise, goalLength) = Goal(crowds: cutaway, direction: direction, elevation: elevation, level: level, maxRise: maxRise, probe: in probe, up: upAxis);
        var eased = Ease(
            follower: ref m_arm,
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
        // In cutaway the body outranks geometry; fade allows the eye into the body's shell while preserving the room.
        var keepOut = (cutaway ? probe.KeepOut(direction: presented) : 0f);
        var held = MathF.Min(
            x: (Math.Clamp(
                max: 1f,
                min: 0f,
                value: eased.X
            ) * length),
            y: probe.Room(
                direction: presented,
                question: HeldQuestion,
                radius: FixedQ4816.FromDouble(value: (clearance.SurfaceSkin * scale))
            )
        );
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
        var face = ((!cutaway) ? length : probe.Room(
            direction: presented,
            question: FaceQuestion,
            radius: FixedQ4816.Zero
        ));

        var retraction = (clearance.RetractionSkin * scale);

        Near = ((cutaway && (face < length) && ((face + retraction) < placed))
            ? Math.Clamp(
                max: MathF.Max(
                    x: 0f,
                    y: (subject.NearestDepth(
                        eye: (target + (presented * placed)),
                        forward: -presented
                    ) - (clearance.NearMargin * scale))
                ),
                min: 0f,
                value: (placed - (face + retraction))
            )
            : 0f
        );
        Mode = mode;
        Rise = rise;
        Length = placed;
        if (clearance.Fades) {
            var opacity = Math.Clamp((((placed / scale) - clearance.FadeEnd) / (clearance.FadeStart - clearance.FadeEnd)), 0f, 1f);

            BodyOpacity = ((opacity * opacity) * (3f - (2f * opacity)));
        }
        Eye = (((rise == 0f) && (placed == length))
            ? desiredEye
            : (target + (presented * placed))
        );
        if (placed <= MinimumBoom) {
            // A room may force the eye onto the pivot. Keep a defined sightline along the player's requested look.
            Target = (Eye - (presented * MinimumBoom));
        }

        return Eye;
    }

    // Steps the arm toward this frame's goal on the rig's dynamics, or lands on it where there is no ease or no history.
    private Vector3 Ease(in SdfCameraDynamics dynamics, float deltaSeconds, Vector3 goal, ref SecondOrderFollower3 follower) {
        if (!dynamics.IsLive) {
            follower.Seed(target: goal);
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

        return follower.Step(
            deltaSeconds: deltaSeconds,
            response: in m_cachedResponse,
            target: goal
        );
    }

    /// <summary>Returns the read-back of the latest frame: <c>eye=(x,y,z) target=(x,y,z) boom=l/a rise=d near=n
    /// fade=f clearance=m</c>, the boom in world units placed of authored, the rise in degrees, the near plane in
    /// world units, and fade from zero (solid) to one (hidden); then, when the program authors a clearance, its
    /// parameters as the frame resolved them, full-size: <c>cutaway= eyeRadius= surfaceSkin= maxRise= minimumBoom=
    /// fadeStart= fadeEnd= closeTarget=(x,y,z) closeTargetStart= closeTargetFull= nearMargin= retractionSkin=
    /// obstructedTargetSkin=</c>, with the rise ceiling in degrees.</summary>
    /// <returns>The read-back text.</returns>
    public string Describe() {
        var placed = string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"eye=({Eye.X:0.###},{Eye.Y:0.###},{Eye.Z:0.###}) target=({Target.X:0.###},{Target.Y:0.###},{Target.Z:0.###}) boom={Length:0.###}/{AuthoredLength:0.###} rise={float.RadiansToDegrees(radians: Rise):0.#} near={Near:0.###} fade={(1f - BodyOpacity):0.###} clearance={Mode.ToString().ToLowerInvariant()}"
        );

        if (Parameters is not { } p) {
            return placed;
        }

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{placed} cutaway={(p.Cutaway ? "true" : "false")} eyeRadius={p.EyeRadius:0.###} surfaceSkin={p.SurfaceSkin:0.###} maxRise={float.RadiansToDegrees(radians: p.MaxRise):0.#} minimumBoom={p.MinimumBoom:0.###} fadeStart={p.FadeStart:0.###} fadeEnd={p.FadeEnd:0.###} closeTarget=({p.CloseTarget.X:0.###},{p.CloseTarget.Y:0.###},{p.CloseTarget.Z:0.###}) closeTargetStart={p.CloseTargetStartFraction:0.###} closeTargetFull={p.CloseTargetFullFraction:0.###} nearMargin={p.NearMargin:0.###} retractionSkin={p.RetractionSkin:0.###} obstructedTargetSkin={p.ObstructedTargetSkin:0.###}"
        );
    }
}
