using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.Physics.Motion;

/// <summary>What one hold binds a body to.</summary>
[JsonConverter(typeof(StrictEnumConverter<BodyHoldBond>))]
public enum BodyHoldBond : byte {
    /// <summary>A contact-field surface whose normal makes an angle inside the hold's cone with gravity-up: zero is
    /// a floor, ninety a wall, a hundred and eighty a ceiling.</summary>
    Surface,

    /// <summary>No surface at all — the body holds itself where it is.</summary>
    Free,

    /// <summary>The medium the body is standing in — the world's own field lattice column. The medium holds the body
    /// by displacement rather than by contact, so this bond has no cone and no reach: the world either offers a
    /// medium where the body is or it does not.</summary>
    Medium,
}
/// <summary>What holds the body once a hold is taken — the vertical law the hold's own
/// <see cref="BodyMotionOp.ApplyHold"/> applies.</summary>
[JsonConverter(typeof(StrictEnumConverter<BodyHoldKind>))]
public enum BodyHoldKind : byte {
    /// <summary>Nothing holds it: no gravity, no pull, no lift. The body keeps whatever vertical channel it
    /// carried in.</summary>
    None,

    /// <summary>Gravity holds the body against the surface — the walkable case, integrating the row's own
    /// <c>Gravity</c> arc.</summary>
    Gravity,

    /// <summary>A pull toward the surface at the hold's own rate, applied as a positional standoff rather than a
    /// force; gravity is not applied while the hold is held.</summary>
    Pull,

    /// <summary>A fraction of gravity cancelled — one cancels it whole and the body hovers.</summary>
    Lift,
}
/// <summary>Where a hold's frame takes its forward direction when the surface itself leaves it free (a free hold, or
/// a face whose normal is parallel to gravity-up).</summary>
[JsonConverter(typeof(StrictEnumConverter<BodyHoldForward>))]
public enum BodyHoldForward : byte {
    /// <summary>The body's own integrated heading — the frame the ordinary program built.</summary>
    Heading,

    /// <summary>The commanded direction.</summary>
    Intent,

    /// <summary>The direction the body is travelling.</summary>
    Velocity,
}
/// <summary>The compiled displacement law of a <see cref="BodyHoldBond.Medium"/> hold — what the medium does to a
/// body that is in it, independent of what the body itself thrusts. Meaningless, and left zeroed, on every other
/// bond. Convergence toward the equilibrium line is NOT this law's own: the governing shaping row's own along/
/// dynamics facet is the one convergence source, so this carries only the raw signal that facet converges toward,
/// bounded by the hold's own <see cref="FixedBodyHold.Envelope"/>.</summary>
/// <param name="IdleDrift">The medium's idle vertical drift velocity below the equilibrium band, signed (u/s):
/// positive drifts the body up toward equilibrium, negative sinks it, zero holds depth.</param>
/// <param name="EquilibriumOffset">How far below the medium surface the body rests at equilibrium, and the band's
/// half-width around that line (u).</param>
public readonly record struct FixedBodyMedium(
    FixedQ4816 IdleDrift,
    FixedQ4816 EquilibriumOffset
);
/// <summary>The compiled vertical arc a <see cref="BodyHoldKind.Gravity"/> or <see cref="BodyHoldKind.Lift"/> row
/// falls under — zeroed on every other kind and on a <see cref="BodyHoldBond.Medium"/> row. The terminal fall speed
/// this arc is clamped to is not its own: it reads <see cref="FixedBodyHold.Envelope"/>'s sink speed, the same
/// shared vertical-channel bound a medium row's terminal speeds are.</summary>
/// <param name="Rise">The downward acceleration while rising (u/s²).</param>
/// <param name="Fall">The downward acceleration while falling (u/s²).</param>
public readonly record struct FixedBodyHoldGravity(
    FixedQ4816 Rise,
    FixedQ4816 Fall
);
/// <summary>The vertical-channel envelope a hold's vertical law is bounded by — shared by a
/// <see cref="BodyHoldKind.Gravity"/>/<see cref="BodyHoldKind.Lift"/> row's own terminal fall speed and a
/// <see cref="BodyHoldBond.Medium"/> row's terminal rise/sink speeds, so a document-wide speed ceiling reads one
/// field family rather than three.</summary>
/// <param name="RiseSpeed">The terminal upward speed (u/s) — bounds a medium row's rise. A gravity/lift row's own
/// arc never rises against a clamp, so this reads as the compiled "uncapped" sentinel there.</param>
/// <param name="SinkSpeed">The terminal downward speed (u/s) — a gravity/lift row's own terminal fall speed, or a
/// medium row's terminal sink speed.</param>
public readonly record struct FixedVerticalEnvelope(
    FixedQ4816 RiseSpeed,
    FixedQ4816 SinkSpeed
);
/// <summary>
/// One compiled hold: the fixed-point form of an authored hold row, in the ordered list a kit's motion row
/// declares. <see cref="BodyMotionOp.ResolveHold"/> walks the list in order and takes the first hold the world
/// offers; <see cref="BodyMotionOp.ApplyHold"/> applies <see cref="Kind"/>.
/// </summary>
/// <param name="Name">The hold's authored name — the read-back token, never read by the resolve.</param>
/// <param name="Bond">Whether the hold needs a surface.</param>
/// <param name="Kind">The vertical law while the hold is held.</param>
/// <param name="ConeCosNear">The cosine of the cone's least angle — the upper bound on a candidate normal's
/// alignment with gravity-up. Meaningless for a free hold.</param>
/// <param name="ConeCosFar">The cosine of the cone's greatest angle — the lower bound on that same alignment.</param>
/// <param name="ConeAdmitsBelow">Whether the cone reaches at or below a right angle, so a face inside it can sit
/// under the body and a downward probe is worth casting.</param>
/// <param name="ConeAdmitsAbove">Whether the cone reaches at or above a right angle, so a face inside it can sit
/// over the body and an upward probe is worth casting.</param>
/// <param name="Pull">The inward pull rate, world units per second, under <see cref="BodyHoldKind.Pull"/>.</param>
/// <param name="Lift">The fraction of gravity cancelled under <see cref="BodyHoldKind.Lift"/>.</param>
/// <param name="Speed">The travel speed along the hold's tangent plane, or zero to ride the kit's own resolved move
/// speed.</param>
/// <param name="Reach">How far a surface hold's probes search, world units. Meaningless for a free hold.</param>
/// <param name="UpLean">How far the body's up axis blends from gravity-up toward the surface normal, in
/// <c>[0, 1]</c>: zero stays upright on a wall, one lies flat against it.</param>
/// <param name="Forward">Where the frame's forward comes from when the surface leaves it free.</param>
/// <param name="OnDrive">Whether driving into a face inside the cone takes the hold.</param>
/// <param name="DriveAlignment">The least alignment, in <c>[0, 1]</c>, between the commanded direction and the
/// face's inward normal before <see cref="OnDrive"/> takes it.</param>
/// <param name="ReleaseOrdinal">The channel ordinal whose held read drops the hold, or <c>-1</c> for a hold no
/// channel can drop.</param>
/// <param name="ReleaseThreshold">That channel's own binary crossing threshold.</param>
/// <param name="SpendState">The body-lane state slot name the hold drains while held, or <see langword="null"/> for
/// a hold that spends nothing.</param>
/// <param name="SpendPerSecond">The rate that slot drains at, per second.</param>
/// <param name="Medium">The displacement law of a <see cref="BodyHoldBond.Medium"/> bond; zeroed on every
/// other.</param>
/// <param name="Gravity">The vertical arc a <see cref="BodyHoldKind.Gravity"/> or <see cref="BodyHoldKind.Lift"/>
/// row falls under; zeroed on every other kind and on a <see cref="BodyHoldBond.Medium"/> row.</param>
/// <param name="Envelope">The vertical-channel envelope <see cref="Gravity"/>'s terminal fall speed and
/// <see cref="Medium"/>'s terminal rise/sink speeds share; zeroed on a row with no vertical law
/// (<see cref="BodyHoldKind.None"/>, <see cref="BodyHoldKind.Pull"/>, or a full <see cref="BodyHoldKind.Lift"/>,
/// whose channel decays rather than clamps).</param>
/// <param name="Thrust">The fraction of the kit's resolved move speed the <c>MoveUp</c> role commands vertically
/// while this row holds, in every bond; <c>0</c> is no thrust at all.</param>
public readonly record struct FixedBodyHold(
    string Name,
    BodyHoldBond Bond,
    BodyHoldKind Kind,
    FixedQ4816 ConeCosNear,
    FixedQ4816 ConeCosFar,
    bool ConeAdmitsBelow,
    bool ConeAdmitsAbove,
    FixedQ4816 Pull,
    FixedQ4816 Lift,
    FixedQ4816 Speed,
    FixedQ4816 Reach,
    FixedQ4816 UpLean,
    BodyHoldForward Forward,
    bool OnDrive,
    FixedQ4816 DriveAlignment,
    int ReleaseOrdinal,
    FixedQ4816 ReleaseThreshold,
    string? SpendState,
    FixedQ4816 SpendPerSecond,
    FixedBodyMedium Medium,
    FixedBodyHoldGravity Gravity,
    FixedVerticalEnvelope Envelope,
    FixedQ4816 Thrust
) {
    /// <summary>Reports whether a candidate face's alignment with gravity-up falls inside this hold's cone.</summary>
    /// <param name="alignment">The dot product of the candidate's unit normal with gravity-up. Clamped to
    /// <c>[-1, 1]</c> first: it is a cosine, and a fixed-point normalize can land a hair outside that, which would
    /// otherwise refuse a face exactly on a cone bound.</param>
    /// <returns><see langword="true"/> when the face is inside the cone.</returns>
    public bool ConeAdmits(FixedQ4816 alignment) {
        var cosine = ((alignment > FixedQ4816.One)
            ? FixedQ4816.One
            : ((alignment < -FixedQ4816.One)
                ? -FixedQ4816.One
                : alignment
            )
        );

        return ((cosine <= ConeCosNear) && (cosine >= ConeCosFar));
    }
}
