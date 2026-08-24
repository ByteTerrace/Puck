using System.Numerics;

namespace Puck.SdfVm.Views;

/// <summary>
/// A number one <see cref="SdfCameraOp"/> reads: a compiled-in literal, or a per-frame SLOT the host refills before
/// every <see cref="SdfCameraProgramRig.Resolve"/>. The slot arm is what keeps document vocabulary out of this
/// library — a host whose authoring grammar lets an author bind a field to live data resolves that binding itself and
/// writes the plain number here, so the evaluator never learns what a binding is.
/// </summary>
public readonly record struct SdfCameraScalar {
    /// <summary>The <see cref="Slot"/> value of a literal — one that reads no per-frame scalar.</summary>
    public const int NoSlot = -1;

    /// <summary>Gets the compiled-in value, used when <see cref="Slot"/> is <see cref="NoSlot"/> or names no supplied
    /// scalar.</summary>
    public float Literal { get; private init; }
    /// <summary>Gets the per-frame scalar slot this reads, or <see cref="NoSlot"/> for a literal.</summary>
    public int Slot { get; private init; }

    /// <summary>Creates a literal.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The scalar.</returns>
    public static SdfCameraScalar FromLiteral(float value) => new() { Literal = value, Slot = NoSlot };
    /// <summary>Creates a per-frame slot read.</summary>
    /// <param name="slot">The scalar slot index.</param>
    /// <param name="fallback">The value used when the frame supplies no such slot.</param>
    /// <returns>The scalar.</returns>
    public static SdfCameraScalar FromSlot(int slot, float fallback = 0f) => new() { Literal = fallback, Slot = slot };

    /// <summary>Resolves this number against one frame's scalars.</summary>
    /// <param name="scalars">The frame's scalars.</param>
    /// <returns>The slot's value, or <see cref="Literal"/>.</returns>
    public float Resolve(ReadOnlySpan<float> scalars) {
        var slot = Slot;

        return (((slot >= 0) && (slot < scalars.Length))
            ? scalars[slot]
            : Literal
        );
    }
}
/// <summary>The pole-matched second-order response an <see cref="SdfCameraOp.Dynamics"/> op sets for the boom to ease
/// through — the authored triple only. Deriving the propagator from it is <see cref="SecondOrderResponse.Create"/>'s
/// job, done by whichever component actually eases (<see cref="SdfCameraBoomFollower"/>), never here.</summary>
/// <param name="Frequency">The natural frequency, in Hz. Non-positive (zero by default) means <see cref="None"/> —
/// no dynamics authored.</param>
/// <param name="Damping">The damping ratio (dimensionless).</param>
/// <param name="Response">The initial response (dimensionless).</param>
public readonly record struct SdfCameraDynamics(float Frequency, float Damping, float Response) {
    /// <summary>Gets the "no dynamics authored" value — a program with no <see cref="SdfCameraOp.Dynamics"/> op, or a
    /// <see cref="SdfCameraOp.Blend"/> of two programs neither of which authors one.</summary>
    public static SdfCameraDynamics None => default;

    /// <summary>Gets whether this response is live (an authored positive frequency) rather than <see cref="None"/>.</summary>
    public bool IsLive => (Frequency > 0f);
}
/// <summary>
/// One instruction of a compiled camera program — the pose algebra a rig walks in order every frame. Each op is
/// trivial and total; a camera behavior is a LIST of these rather than a closed kind union, so a new framing is data
/// the host compiles rather than a new rig type here. Every op reads only plain numbers and poses (see
/// <see cref="SdfCameraScalar"/> and <see cref="SdfCameraProgramFrame.Subjects"/>): nothing in this vocabulary knows
/// what a document, a binding, or an entity is.
/// </summary>
public abstract record SdfCameraOp {
    private SdfCameraOp() {
    }

    /// <summary>Establishes the CURRENT subject — the pose <see cref="Offset"/>/<see cref="Orbit"/> place the eye
    /// relative to, and <see cref="LookAt"/> aims along the facing of when it names no subject of its own — and
    /// re-seeds the eye at that subject's own position (the first-person start before any placement op runs).</summary>
    /// <param name="SubjectSlot">The frame subject slot, or <see cref="SdfCameraProgram.ReferenceSubject"/> for the
    /// externally supplied reference pose.</param>
    public sealed record Anchor(int SubjectSlot) : SdfCameraOp;
    /// <summary>Places the eye at an offset from the current subject's pose.</summary>
    /// <param name="Value">The offset.</param>
    /// <param name="WorldAxes">Whether <paramref name="Value"/> uses world axes rather than the subject's own.</param>
    /// <param name="Scale">The multiplier applied to <paramref name="Value"/> — a host widens an establishing shot
    /// by writing a per-frame slot here; a plain offset compiles the literal <c>1</c>.</param>
    public sealed record Offset(Vector3 Value, bool WorldAxes, SdfCameraScalar Scale) : SdfCameraOp;
    /// <summary>Sets the aim target.</summary>
    /// <param name="SubjectSlot">The frame subject slot to look at,
    /// <see cref="SdfCameraProgram.ReferenceSubject"/> for the reference pose, or
    /// <see cref="SdfCameraProgram.FacingSubject"/> to look along the current subject's own forward axis from the eye
    /// at <paramref name="FocusDistance"/>.</param>
    /// <param name="TargetOffset">An offset from the resolved subject's pose; unread for
    /// <see cref="SdfCameraProgram.FacingSubject"/>.</param>
    /// <param name="WorldAxes">Whether <paramref name="TargetOffset"/> uses world axes rather than the subject's
    /// own.</param>
    /// <param name="FocusDistance">The finite target distance along the current subject's forward axis, read only for
    /// <see cref="SdfCameraProgram.FacingSubject"/>.</param>
    public sealed record LookAt(int SubjectSlot, Vector3 TargetOffset, bool WorldAxes, SdfCameraScalar FocusDistance) : SdfCameraOp;
    /// <summary>Places the eye by orbiting the current subject's pose.</summary>
    /// <param name="Distance">The orbit distance.</param>
    /// <param name="Yaw">The orbit heading in radians.</param>
    /// <param name="Pitch">The orbit tilt in radians, clamped by the governing <see cref="ClampPitch"/> after the look
    /// sample is added.</param>
    /// <param name="PivotOffset">The world-axis offset from the current subject's origin to the pivot.</param>
    /// <param name="AppliesLook">Whether <see cref="SdfCameraProgramFrame.Look"/> adds to
    /// <paramref name="Yaw"/>/<paramref name="Pitch"/> — an interactive rig sets this; a scripted framing does
    /// not.</param>
    public sealed record Orbit(SdfCameraScalar Distance, SdfCameraScalar Yaw, SdfCameraScalar Pitch, Vector3 PivotOffset, bool AppliesLook) : SdfCameraOp;
    /// <summary>Sets the second-order response reported as <see cref="SdfCameraPose.Dynamics"/>;
    /// <see cref="SdfCameraDynamics.None"/> reports no easing. It never moves the resolved pose — the eased result is
    /// the CALLER's to apply (see <see cref="SdfCameraBoomFollower"/>), because where in a host's pipeline the ease
    /// belongs (before or after that host's own re-framing) is the host's contract, not this vocabulary's.</summary>
    /// <param name="Value">The response — a literal, never a per-frame slot: a dynamics row cannot change without
    /// recompiling its whole holding program, so there is no live-rebind case for a slot to serve.</param>
    public sealed record Dynamics(SdfCameraDynamics Value) : SdfCameraOp;
    /// <summary>Clamps the effective pitch every later <see cref="Orbit"/> op resolves with — its own pitch plus any
    /// applied look sample — to <c>[MinPitch, MaxPitch]</c>.</summary>
    /// <param name="MinPitch">The minimum pitch, radians.</param>
    /// <param name="MaxPitch">The maximum pitch, radians.</param>
    public sealed record ClampPitch(SdfCameraScalar MinPitch, SdfCameraScalar MaxPitch) : SdfCameraOp;
    /// <summary>Sets the rendered vertical field of view, radians.</summary>
    /// <param name="FieldOfViewRadians">The field of view.</param>
    public sealed record Fov(SdfCameraScalar FieldOfViewRadians) : SdfCameraOp;
    /// <summary>Evaluates two other programs of the same <see cref="SdfCameraProgramSet"/> and linearly interpolates
    /// their resolved eye, target, field of view, and dynamics response.</summary>
    /// <param name="ProgramA">The set index resolved at <paramref name="Weight"/> 0.</param>
    /// <param name="ProgramB">The set index resolved at <paramref name="Weight"/> 1.</param>
    /// <param name="Weight">The blend weight, clamped to <c>[0, 1]</c>.</param>
    public sealed record Blend(int ProgramA, int ProgramB, SdfCameraScalar Weight) : SdfCameraOp;
}
/// <summary>One compiled camera program — the ordered op list a rig resolves through every frame.</summary>
/// <param name="Name">The program's name, carried for diagnostics only (a <see cref="SdfCameraOp.Blend"/> selects by
/// INDEX — name resolution belongs to whatever authoring vocabulary compiled this set).</param>
/// <param name="Operations">The ops, in evaluation order.</param>
public sealed record SdfCameraProgram(string Name, IReadOnlyList<SdfCameraOp> Operations) {
    /// <summary>The subject-slot value naming the externally supplied reference pose.</summary>
    public const int ReferenceSubject = -1;
    /// <summary>The <see cref="SdfCameraOp.LookAt.SubjectSlot"/> value naming "along the current subject's own
    /// forward axis" rather than a resolved subject pose.</summary>
    public const int FacingSubject = -2;

    private readonly IReadOnlyList<SdfCameraOp> m_operations = (Operations ?? []);

    /// <summary>Gets the ops, in evaluation order.</summary>
    public IReadOnlyList<SdfCameraOp> Operations {
        get => m_operations;
        init => m_operations = (value ?? []);
    }
}
/// <summary>
/// The programs one rig can reach — its root at index 0, followed by every program a <see cref="SdfCameraOp.Blend"/>
/// can reach transitively. A set is the whole blend namespace: an index outside it resolves nothing, so a compiler
/// that cannot resolve a name simply omits the blend rather than handing this evaluator a dangling reference.
/// </summary>
/// <param name="Programs">The programs; index 0 is the root.</param>
public sealed record SdfCameraProgramSet(IReadOnlyList<SdfCameraProgram> Programs) {
    /// <summary>The deepest blend nesting evaluated — a bound that holds even for a set whose compiler admitted a
    /// cycle, so a malformed set costs a bounded walk rather than a stack overflow.</summary>
    public const int MaxBlendDepth = 8;

    private readonly IReadOnlyList<SdfCameraProgram> m_programs = (Programs ?? []);

    /// <summary>Gets the programs; index 0 is the root.</summary>
    public IReadOnlyList<SdfCameraProgram> Programs {
        get => m_programs;
        init => m_programs = (value ?? []);
    }
}
/// <summary>One frame's live look sample, in radians — the interactive delta a seat's own control adds to an
/// <see cref="SdfCameraOp.Orbit"/> that declares <see cref="SdfCameraOp.Orbit.AppliesLook"/>. Sensitivity, inversion,
/// and any envelope clamp are the host's: what arrives here is already the composed angle.</summary>
/// <param name="Yaw">The yaw delta, radians.</param>
/// <param name="Pitch">The pitch delta, radians.</param>
public readonly record struct SdfCameraLook(float Yaw, float Pitch);
/// <summary>One frame's resolved camera.</summary>
/// <param name="Eye">The eye position.</param>
/// <param name="Target">The look-at target.</param>
/// <param name="FovRadians">The vertical field of view, radians.</param>
/// <param name="Dynamics">The response the program's <see cref="SdfCameraOp.Dynamics"/> reported, or
/// <see cref="SdfCameraDynamics.None"/>.</param>
public readonly record struct SdfCameraPose(Vector3 Eye, Vector3 Target, float FovRadians, SdfCameraDynamics Dynamics);
/// <summary>The per-frame inputs a program evaluates against — everything that varies while the compiled ops do
/// not.</summary>
public readonly ref struct SdfCameraProgramFrame {
    /// <summary>Gets the resolved subject poses, indexed by an op's subject slot.</summary>
    public ReadOnlySpan<SdfAnchor> Subjects { get; init; }
    /// <summary>Gets the resolved scalars, indexed by <see cref="SdfCameraScalar.Slot"/>.</summary>
    public ReadOnlySpan<float> Scalars { get; init; }
    /// <summary>Gets the live look sample.</summary>
    public SdfCameraLook Look { get; init; }
    /// <summary>Gets the presentation clocks.</summary>
    public SdfCameraClock Clock { get; init; }
}
/// <summary>
/// Walks a compiled <see cref="SdfCameraProgramSet"/> and produces one frame's pose. Allocation-free and stateless:
/// the ops are immutable, the frame is spans, and the only recursion is <see cref="SdfCameraOp.Blend"/>, bounded by
/// <see cref="SdfCameraProgramSet.MaxBlendDepth"/>.
/// </summary>
public static class SdfCameraProgramEvaluator {
    /// <summary>The distance ahead the aim target sits when a program authors no <see cref="SdfCameraOp.LookAt"/> —
    /// far enough that the look direction is well conditioned, near enough to stay a plausible focus.</summary>
    public const float DefaultFocusDistance = 6f;
    // The smallest focus distance a facing look-at resolves at: below this the target collapses onto the eye and the
    // look direction has no answer.
    private const float MinimumFocusDistance = 0.01f;

    /// <summary>Resolves one program of a set.</summary>
    /// <param name="programs">The compiled set.</param>
    /// <param name="programIndex">The program to resolve (0 is the root).</param>
    /// <param name="reference">The externally supplied reference pose — what
    /// <see cref="SdfCameraProgram.ReferenceSubject"/> names.</param>
    /// <param name="frame">The frame's resolved inputs.</param>
    /// <returns>The resolved pose.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="programs"/> is <see langword="null"/>.</exception>
    public static SdfCameraPose Evaluate(SdfCameraProgramSet programs, int programIndex, in SdfAnchor reference, in SdfCameraProgramFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: programs);

        return Evaluate(
            depth: 0,
            frame: in frame,
            programIndex: programIndex,
            programs: programs,
            reference: in reference
        );
    }

    private static SdfAnchor ResolveSubject(int slot, in SdfAnchor reference, in SdfCameraProgramFrame frame) {
        var subjects = frame.Subjects;

        return (((slot >= 0) && (slot < subjects.Length))
            ? subjects[slot]
            : reference
        );
    }
    private static Vector3 Place(in SdfAnchor subject, Vector3 value, bool worldAxes, float scale) {
        var scaled = (value * scale);

        return (subject.Position + (worldAxes
            ? scaled
            : Vector3.Transform(
                rotation: subject.Orientation,
                value: scaled
            )
        ));
    }
    private static Vector3 Forward(Quaternion orientation) => Vector3.Transform(
        rotation: orientation,
        value: -Vector3.UnitZ
    );
    // Both live: the blended response itself. One live: that one, unweighted — a blend against a program that
    // authors none has nothing to interpolate toward, so the live side stands rather than fading toward None (which
    // is not a smaller response, only an absent one). Neither live: None.
    private static SdfCameraDynamics BlendDynamics(in SdfCameraDynamics a, in SdfCameraDynamics b, float weight) {
        if (a.IsLive && b.IsLive) {
            return new SdfCameraDynamics(
                Frequency: float.Lerp(amount: weight, value1: a.Frequency, value2: b.Frequency),
                Damping: float.Lerp(amount: weight, value1: a.Damping, value2: b.Damping),
                Response: float.Lerp(amount: weight, value1: a.Response, value2: b.Response)
            );
        }

        return (a.IsLive
            ? a
            : (b.IsLive
                ? b
                : SdfCameraDynamics.None));
    }
    private static SdfCameraPose Evaluate(SdfCameraProgramSet programs, int programIndex, in SdfAnchor reference, in SdfCameraProgramFrame frame, int depth) {
        var table = programs.Programs;

        if (
            (programIndex < 0) ||
            (programIndex >= table.Count) ||
            (table[programIndex] is not { } program)
        ) {
            return new SdfCameraPose(
                Eye: reference.Position,
                FovRadians: OrbitRig.DefaultFieldOfViewRadians,
                Dynamics: SdfCameraDynamics.None,
                Target: (reference.Position + (Forward(orientation: reference.Orientation) * DefaultFocusDistance))
            );
        }

        var scalars = frame.Scalars;
        var subject = reference;
        var eye = subject.Position;
        var target = eye;
        var haveTarget = false;
        var fov = OrbitRig.DefaultFieldOfViewRadians;
        var dynamics = SdfCameraDynamics.None;
        var pitchMin = (-MathF.PI / 2f);
        var pitchMax = (MathF.PI / 2f);
        var operations = program.Operations;

        for (var index = 0; (index < operations.Count); index++) {
            switch (operations[index]) {
                case SdfCameraOp.Anchor anchor:
                    subject = ResolveSubject(
                        frame: in frame,
                        reference: in reference,
                        slot: anchor.SubjectSlot
                    );
                    eye = subject.Position;

                    break;
                case SdfCameraOp.Offset offset:
                    eye = Place(
                        scale: offset.Scale.Resolve(scalars: scalars),
                        subject: in subject,
                        value: offset.Value,
                        worldAxes: offset.WorldAxes
                    );

                    break;
                case SdfCameraOp.Orbit orbit:
                    var look = (orbit.AppliesLook
                        ? frame.Look
                        : default);
                    var pitch = Math.Clamp(
                        max: pitchMax,
                        min: pitchMin,
                        value: (orbit.Pitch.Resolve(scalars: scalars) + look.Pitch)
                    );
                    var pivot = (subject.Position + orbit.PivotOffset);

                    eye = (pivot + OrbitRig.Offset(
                        distance: orbit.Distance.Resolve(scalars: scalars),
                        pitch: pitch,
                        yaw: (orbit.Yaw.Resolve(scalars: scalars) + look.Yaw)
                    ));

                    break;
                case SdfCameraOp.LookAt lookAt:
                    if (lookAt.SubjectSlot == SdfCameraProgram.FacingSubject) {
                        target = (eye + (Forward(orientation: subject.Orientation) * MathF.Max(
                            x: lookAt.FocusDistance.Resolve(scalars: scalars),
                            y: MinimumFocusDistance
                        )));
                    } else {
                        var aim = ResolveSubject(
                            frame: in frame,
                            reference: in reference,
                            slot: lookAt.SubjectSlot
                        );

                        target = Place(
                            scale: 1f,
                            subject: in aim,
                            value: lookAt.TargetOffset,
                            worldAxes: lookAt.WorldAxes
                        );
                    }

                    haveTarget = true;

                    break;
                case SdfCameraOp.Dynamics dynamicsOp:
                    dynamics = dynamicsOp.Value;

                    break;
                case SdfCameraOp.ClampPitch clampPitch:
                    pitchMin = clampPitch.MinPitch.Resolve(scalars: scalars);
                    pitchMax = clampPitch.MaxPitch.Resolve(scalars: scalars);

                    break;
                case SdfCameraOp.Fov fovOp:
                    fov = fovOp.FieldOfViewRadians.Resolve(scalars: scalars);

                    break;
                case SdfCameraOp.Blend blend:
                    if (depth >= SdfCameraProgramSet.MaxBlendDepth) {
                        break;
                    }

                    var resolvedA = Evaluate(
                        depth: (depth + 1),
                        frame: in frame,
                        programIndex: blend.ProgramA,
                        programs: programs,
                        reference: in reference
                    );
                    var resolvedB = Evaluate(
                        depth: (depth + 1),
                        frame: in frame,
                        programIndex: blend.ProgramB,
                        programs: programs,
                        reference: in reference
                    );
                    var weight = Math.Clamp(
                        max: 1f,
                        min: 0f,
                        value: blend.Weight.Resolve(scalars: scalars)
                    );

                    eye = Vector3.Lerp(
                        amount: weight,
                        value1: resolvedA.Eye,
                        value2: resolvedB.Eye
                    );
                    fov = float.Lerp(
                        amount: weight,
                        value1: resolvedA.FovRadians,
                        value2: resolvedB.FovRadians
                    );
                    dynamics = BlendDynamics(
                        a: resolvedA.Dynamics,
                        b: resolvedB.Dynamics,
                        weight: weight
                    );
                    target = Vector3.Lerp(
                        amount: weight,
                        value1: resolvedA.Target,
                        value2: resolvedB.Target
                    );
                    haveTarget = true;

                    break;
            }
        }

        return new SdfCameraPose(
            Eye: eye,
            FovRadians: fov,
            Dynamics: dynamics,
            Target: (haveTarget
                ? target
                : (eye + (Forward(orientation: subject.Orientation) * DefaultFocusDistance)))
        );
    }
}
/// <summary>
/// A compiled camera program wearing the <see cref="ISdfCameraRig"/> seam, so a program frames a
/// <see cref="ViewStack"/> view exactly where an <see cref="OrbitRig"/> or a <see cref="FollowRig"/> would. The rig
/// owns the per-frame buffers the evaluator reads: a host writes <see cref="Subjects"/>, <see cref="Scalars"/>, and
/// <see cref="Look"/> for the coming frame, then resolves.
/// </summary>
public sealed class SdfCameraProgramRig : ISdfCameraRig {
    private readonly SdfCameraProgramSet m_programs;
    private readonly float[] m_scalars;
    private readonly SdfAnchor[] m_subjects;

    /// <summary>Initializes a new instance of the <see cref="SdfCameraProgramRig"/> class.</summary>
    /// <param name="programs">The compiled set; its index 0 is what this rig resolves.</param>
    /// <param name="scalarCount">How many per-frame scalar slots the set's ops read.</param>
    /// <param name="subjectCount">How many per-frame subject slots the set's ops read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="programs"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    public SdfCameraProgramRig(SdfCameraProgramSet programs, int scalarCount, int subjectCount) {
        ArgumentNullException.ThrowIfNull(argument: programs);
        ArgumentOutOfRangeException.ThrowIfNegative(value: scalarCount);
        ArgumentOutOfRangeException.ThrowIfNegative(value: subjectCount);

        m_programs = programs;
        m_scalars = new float[scalarCount];
        m_subjects = new SdfAnchor[subjectCount];
    }

    /// <summary>Gets the live look sample added to every <see cref="SdfCameraOp.Orbit"/> that applies it.</summary>
    public SdfCameraLook Look { get; set; }
    /// <summary>Gets the compiled set this rig resolves.</summary>
    public SdfCameraProgramSet Programs => m_programs;
    /// <summary>Gets the response the last <see cref="Resolve"/> reported (see <see cref="SdfCameraOp.Dynamics"/>).</summary>
    public SdfCameraDynamics Dynamics { get; private set; }
    /// <summary>Gets the per-frame scalar slots, for the host to fill before resolving.</summary>
    public Span<float> Scalars => m_scalars;
    /// <summary>Gets the per-frame subject poses, for the host to fill before resolving.</summary>
    public Span<SdfAnchor> Subjects => m_subjects;

    /// <summary>Resolves this frame's full pose, including the reported dynamics response.</summary>
    /// <param name="anchor">The reference pose.</param>
    /// <param name="clock">The presentation clocks.</param>
    /// <returns>The resolved pose.</returns>
    public SdfCameraPose ResolvePose(in SdfAnchor anchor, in SdfCameraClock clock) {
        var pose = SdfCameraProgramEvaluator.Evaluate(
            frame: new SdfCameraProgramFrame {
                Clock = clock,
                Look = Look,
                Scalars = m_scalars,
                Subjects = m_subjects,
            },
            programIndex: 0,
            programs: m_programs,
            reference: in anchor
        );

        Dynamics = pose.Dynamics;

        return pose;
    }
    /// <inheritdoc/>
    public (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, in SdfCameraClock clock) {
        var pose = ResolvePose(
            anchor: in anchor,
            clock: in clock
        );

        return (pose.Eye, pose.Target, pose.FovRadians);
    }
}
/// <summary>
/// The pole-matched second-order boom ease an <see cref="SdfCameraOp.Dynamics"/> response drives: the eye's offset
/// from the target lags, while the target stays exactly where the program put it.
/// </summary>
/// <remarks>Only the boom eases. Easing absolute eye and target coordinates would give presentation a second,
/// delayed subject trajectory that disagrees with the rendered one.</remarks>
public sealed class SdfCameraBoomFollower {
    private SecondOrderFollower3 m_follower;

    /// <summary>Gets the eased boom — the eye's offset from the target.</summary>
    public Vector3 Boom => m_follower.Value;
    /// <summary>Gets whether the boom currently holds an eased value rather than needing a fresh seed.</summary>
    public bool Seeded => m_follower.Seeded;

    /// <summary>Drops the eased value, so the next <see cref="Apply"/> seeds at that frame's pose — the cut a caller
    /// wants when the framing changes discontinuously.</summary>
    public void Reseed() => m_follower.Reseed();
    /// <summary>Eases <paramref name="eye"/> toward the boom held from earlier frames.</summary>
    /// <param name="dynamics"><see cref="SdfCameraDynamics.None"/> reseeds and passes the pose through untouched,
    /// bit for bit; a live response drives the ease.</param>
    /// <param name="deltaSeconds">The frame step.</param>
    /// <param name="eye">The resolved eye, eased in place.</param>
    /// <param name="target">The resolved target, read but never moved.</param>
    public void Apply(in SdfCameraDynamics dynamics, float deltaSeconds, ref Vector3 eye, ref Vector3 target) {
        if (!dynamics.IsLive) {
            m_follower.Reseed();

            return;
        }

        var response = SecondOrderResponse.Create(
            dampingRatio: dynamics.Damping,
            frequencyHz: dynamics.Frequency,
            initialResponse: dynamics.Response
        );
        var boom = m_follower.Step(
            deltaSeconds: deltaSeconds,
            response: in response,
            target: (eye - target)
        );

        eye = (target + boom);
    }
}
