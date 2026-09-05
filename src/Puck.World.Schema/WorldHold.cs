using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>What a hold spends while it is held: a body-lane state slot and the rate it drains at. The hold releases
/// the tick the slot can no longer pay; refilling it, or trading for it, is a world's own rules' business.</summary>
/// <param name="State">The declared <c>state.body</c> slot name.</param>
/// <param name="RatePerSecond">The positive rate the slot drains at, per second.</param>
public sealed record WorldHoldSpend(
    string State,
    float RatePerSecond
);
/// <summary>What the medium does to a body standing in it — the authored half of a <see cref="BodyHoldBond.Medium"/>
/// hold. Required for that bond and refused on every other. <see cref="SettleRate"/> is the one law that turns the
/// equilibrium error into a target velocity; the governing shaping row's own along/dynamics facet then rate-limits
/// the body's actual velocity toward that target — clamped by <see cref="WorldHold.Envelope"/> — the same way it
/// rate-limits every other channel.</summary>
/// <param name="IdleDrift">The idle vertical drift velocity below the equilibrium band, signed (u/s).</param>
/// <param name="EquilibriumOffset">The equilibrium line's depth below the medium surface, and the band's
/// half-width (u).</param>
/// <param name="SettleRate">The proportional gain (1/s) the equilibrium error scales by to reach a target velocity
/// inside the band or while recovering a breach above the surface.</param>
public sealed record WorldHoldMedium(
    float IdleDrift,
    float EquilibriumOffset,
    float SettleRate
);
/// <summary>The vertical arc a <see cref="BodyHoldKind.Gravity"/> or <see cref="BodyHoldKind.Lift"/> row falls
/// under, carried per row so a kit's ground, wall, and air rows may each fall differently. Required on those two
/// kinds; refused on
/// <see cref="BodyHoldKind.Pull"/> (gravity is suspended while a pull holds) and on a <see cref="BodyHoldBond.Medium"/>
/// row (a medium displaces by its own law).</summary>
/// <param name="Rise">The downward acceleration while rising (u/s²) — the floaty top of the arc.</param>
/// <param name="Fall">The downward acceleration while falling (u/s²) — the snappy descent (heavier than the rise).
/// The world's own solved gravity field, where one is authored, overrides the MAGNITUDE but keeps this row's
/// rise-to-fall ratio as the arc's asymmetry.</param>
public sealed record WorldHoldGravity(
    float Rise,
    float Fall
);
/// <summary>The vertical-channel envelope a hold's vertical law is bounded by — the same field family a
/// <see cref="BodyHoldKind.Gravity"/>/<see cref="BodyHoldKind.Lift"/> row's terminal fall speed and a
/// <see cref="BodyHoldBond.Medium"/> row's terminal rise/sink speeds both read, so a document-wide speed ceiling
/// walks one field rather than three. Required for a Medium bond (both directions) and for a Gravity/Lift hold
/// short of full lift (sink only — full lift decays its channel rather than clamping it); refused otherwise.</summary>
/// <param name="RiseSpeed">The terminal upward speed (u/s). Required for a Medium bond; refused (the arc never
/// clamps a rise) for Gravity/Lift.</param>
/// <param name="SinkSpeed">The terminal downward speed (u/s) — a Gravity/Lift row's own terminal fall speed, or a
/// Medium row's terminal sink speed.</param>
public sealed record WorldHoldEnvelope(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? RiseSpeed = null,
    float SinkSpeed = 0f
);
/// <summary>
/// One authored hold row of a kit's ordered <see cref="WorldMotion.Holds"/> list — what may
/// hold this body, in preference order. <c>ResolveHold</c> keeps the hold it has while its surface is still there
/// and the same face, and otherwise takes the first row the world offers; <c>ApplyHold</c> applies
/// <paramref name="Hold"/>.
/// </summary>
/// <param name="Name">The row's name, unique within the list — the <c>body.hold</c> read-back token.</param>
/// <param name="Bond">Whether this row needs a surface (<see cref="BodyHoldBond.Surface"/>) or holds the body where
/// it is (<see cref="BodyHoldBond.Free"/>).</param>
/// <param name="Hold">What holds the body once the row is taken.</param>
/// <param name="Cone">The inclusive angle band, in degrees, between an admitted surface normal and gravity-up:
/// <c>[0, 60]</c> is floors, <c>[60, 120]</c> walls, <c>[0, 180]</c> everything. Required for
/// <see cref="BodyHoldBond.Surface"/>, refused for <see cref="BodyHoldBond.Free"/>.</param>
/// <param name="Pull">The inward pull, world units per second, under <see cref="BodyHoldKind.Pull"/>. Applied as a
/// positional standoff, so it closes a gap the surface opens without ever pushing the body through it.</param>
/// <param name="Lift">The fraction of gravity cancelled under <see cref="BodyHoldKind.Lift"/>: <c>1</c> hovers,
/// <c>0.5</c> halves the fall.</param>
/// <param name="Speed">The travel speed along the hold's tangent plane, world units per second, or
/// <see langword="null"/> to ride the kit's own resolved move speed.</param>
/// <param name="Reach">How far a surface row's probes search, world units. Required positive for
/// <see cref="BodyHoldBond.Surface"/>.</param>
/// <param name="UpLean">How far the body's up axis blends from gravity-up toward the surface normal, in
/// <c>[0, 1]</c>.</param>
/// <param name="Forward">Where the hold's frame takes its forward direction when the surface leaves it free.</param>
/// <param name="OnDrive">Whether driving into an admitted face takes this row with no channel press.</param>
/// <param name="DriveAlignment">The least alignment, in <c>[0, 1]</c>, between the commanded direction and the
/// face's inward normal before <paramref name="OnDrive"/> takes it. Inert without it.</param>
/// <param name="Release">The declared channel name whose held read drops this row, or <see langword="null"/> for a
/// row no channel can drop.</param>
/// <param name="Spend">What the row spends while held, or <see langword="null"/> for a row that spends
/// nothing.</param>
/// <param name="Medium">The medium's own displacement law. Required for <see cref="BodyHoldBond.Medium"/> and
/// refused on every other bond.</param>
/// <param name="Gravity">The vertical arc this row falls under. Required for <see cref="BodyHoldKind.Gravity"/> and
/// <see cref="BodyHoldKind.Lift"/>; refused for <see cref="BodyHoldKind.Pull"/>, <see cref="BodyHoldKind.None"/>,
/// and every <see cref="BodyHoldBond.Medium"/> row.</param>
/// <param name="Envelope">The vertical-channel envelope <see cref="Gravity"/>'s terminal fall speed and
/// <see cref="Medium"/>'s terminal rise/sink speeds share. Required for a Medium bond and for a Gravity/Lift hold
/// short of full lift; refused otherwise.</param>
/// <param name="Thrust">The fraction of the kit's resolved move speed the <c>MoveUp</c> role commands vertically
/// while this row holds, in every bond — <c>0</c> (the default) is no vertical thrust at all, <c>1</c> is fully
/// isotropic. A non-<see cref="BodyHoldBond.Medium"/> row commanding thrust takes the vertical channel outright for
/// the tick, clearing the ballistic carry; a medium row folds it into the medium's own displacement before that
/// law's convergence runs.</param>
public sealed record WorldHold(
    string Name,
    BodyHoldBond Bond,
    BodyHoldKind Hold,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector2? Cone = null,
    float Pull = 0f,
    float Lift = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Speed = null,
    float Reach = 0f,
    float UpLean = 0f,
    BodyHoldForward Forward = BodyHoldForward.Heading,
    bool OnDrive = false,
    float DriveAlignment = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Release = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldHoldSpend? Spend = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldHoldMedium? Medium = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldHoldGravity? Gravity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldHoldEnvelope? Envelope = null,
    float Thrust = 0f
);
/// <summary>The one-time compilation of an authored hold list into the fixed-point form simulation reads. Channel
/// names resolve through the world's compiled channel table here, the same resolved-outside/consumed-as-ordinal
/// seam <see cref="FixedSpeed.HeldOrdinal"/> uses.</summary>
public static class WorldHoldFactory {
    private const double DegreesToRadians = (Math.PI / 180.0);

    /// <summary>Compiles a medium's displacement law, or the inert zeroed law for a row that carries none.</summary>
    /// <param name="medium">The authored law, or <see langword="null"/>.</param>
    /// <returns>The compiled law.</returns>
    public static FixedBodyMedium CompileMedium(WorldHoldMedium? medium) => ((medium is { } law)
        ? new FixedBodyMedium(
            EquilibriumOffset: FixedQ4816.FromDouble(value: law.EquilibriumOffset),
            IdleDrift: FixedQ4816.FromDouble(value: law.IdleDrift),
            SettleRate: FixedQ4816.FromDouble(value: law.SettleRate)
        )
        : default
    );
    /// <summary>Compiles a row's vertical arc, or the inert zeroed arc for a row that carries none.</summary>
    /// <param name="gravity">The authored arc, or <see langword="null"/>.</param>
    /// <returns>The compiled arc.</returns>
    public static FixedBodyHoldGravity CompileGravity(WorldHoldGravity? gravity) => ((gravity is { } arc)
        ? new FixedBodyHoldGravity(
            Fall: FixedQ4816.FromDouble(value: arc.Fall),
            Rise: FixedQ4816.FromDouble(value: arc.Rise)
        )
        : default
    );
    /// <summary>Compiles a row's vertical-channel envelope, or the inert zeroed envelope for a row with no vertical
    /// law. An unauthored rise speed compiles to <see cref="FixedQ4816.MaxValue"/> — the sentinel a gravity/lift
    /// row's own arc, which never clamps a rise, reads as "uncapped".</summary>
    /// <param name="envelope">The authored bound, or <see langword="null"/>.</param>
    /// <returns>The compiled bound.</returns>
    public static FixedVerticalEnvelope CompileEnvelope(WorldHoldEnvelope? envelope) => ((envelope is { } bound)
        ? new FixedVerticalEnvelope(
            RiseSpeed: ((bound.RiseSpeed is { } rise)
                ? FixedQ4816.FromDouble(value: rise)
                : FixedQ4816.MaxValue
            ),
            SinkSpeed: FixedQ4816.FromDouble(value: bound.SinkSpeed)
        )
        : default
    );
    /// <summary>Compiles one authored hold list against a world's channel table.</summary>
    /// <param name="holds">The authored rows in preference order, or <see langword="null"/> for a kit authoring
    /// none.</param>
    /// <param name="channels">The world's compiled channel table.</param>
    /// <returns>The compiled rows, empty for a kit authoring none.</returns>
    public static FixedBodyHold[] Compile(IReadOnlyList<WorldHold>? holds, WorldChannelTable channels) {
        if (holds is not { Count: > 0 }) {
            return [];
        }

        var compiled = new FixedBodyHold[holds.Count];

        for (var index = 0; (index < holds.Count); index++) {
            var hold = holds[index];
            var cone = ((hold.Cone is { } authored)
                ? authored.Value
                : default
            );
            var releaseOrdinal = (((hold.Release is { Length: > 0 } release) && channels.TryGetOrdinal(
                name: release,
                ordinal: out var ordinal
            ))
                ? ordinal
                : -1
            );

            compiled[index] = new FixedBodyHold(
                Bond: hold.Bond,
                ConeAdmitsAbove: (cone.Y >= 90f),
                ConeAdmitsBelow: (cone.X <= 90f),
                ConeCosFar: FixedQ4816.FromDouble(value: Math.Cos(d: (((double)cone.Y) * DegreesToRadians))),
                ConeCosNear: FixedQ4816.FromDouble(value: Math.Cos(d: (((double)cone.X) * DegreesToRadians))),
                DriveAlignment: FixedQ4816.FromDouble(value: hold.DriveAlignment),
                Envelope: CompileEnvelope(envelope: hold.Envelope),
                Forward: hold.Forward,
                Gravity: CompileGravity(gravity: hold.Gravity),
                Pull: FixedQ4816.FromDouble(value: hold.Pull),
                Kind: hold.Hold,
                Lift: FixedQ4816.FromDouble(value: hold.Lift),
                Name: hold.Name,
                OnDrive: hold.OnDrive,
                Reach: FixedQ4816.FromDouble(value: hold.Reach),
                ReleaseOrdinal: releaseOrdinal,
                ReleaseThreshold: ((releaseOrdinal >= 0)
                    ? channels.Threshold(ordinal: releaseOrdinal)
                    : FixedQ4816.Zero
                ),
                Medium: CompileMedium(medium: hold.Medium),
                Speed: FixedQ4816.FromDouble(value: (hold.Speed ?? 0f)),
                SpendPerSecond: FixedQ4816.FromDouble(value: (hold.Spend?.RatePerSecond ?? 0f)),
                SpendState: hold.Spend?.State,
                Thrust: FixedQ4816.FromDouble(value: hold.Thrust),
                UpLean: FixedQ4816.FromDouble(value: hold.UpLean)
            );
        }

        return compiled;
    }
    /// <summary>Gets the fastest vertical speed any hold row in a kit's hold list can reach — the greater of its
    /// own <see cref="WorldHoldEnvelope.RiseSpeed"/> and <see cref="WorldHoldEnvelope.SinkSpeed"/>, across every
    /// row, or zero for a kit authoring no envelope at all. What a document-wide speed ceiling reads.</summary>
    /// <param name="holds">The authored rows, or <see langword="null"/>.</param>
    /// <returns>The fastest authored envelope speed.</returns>
    public static float MaxEnvelopeSpeed(IReadOnlyList<WorldHold>? holds) {
        var fastest = 0f;

        foreach (var hold in (holds ?? [])) {
            if (hold?.Envelope is not { } envelope) {
                continue;
            }

            fastest = Math.Max(val1: fastest, val2: envelope.SinkSpeed);

            if (envelope.RiseSpeed is { } rise) {
                fastest = Math.Max(val1: fastest, val2: rise);
            }
        }

        return fastest;
    }
    /// <summary>Gets the steepest fall acceleration any <see cref="BodyHoldKind.Gravity"/> or
    /// <see cref="BodyHoldKind.Lift"/> row in a kit's hold list authors, or zero for a kit authoring none of
    /// either.</summary>
    /// <param name="holds">The authored rows, or <see langword="null"/>.</param>
    /// <returns>The steepest authored fall acceleration.</returns>
    public static float MaxFallAcceleration(IReadOnlyList<WorldHold>? holds) {
        var fall = 0f;

        foreach (var hold in (holds ?? [])) {
            if (hold?.Gravity is { } gravity) {
                fall = Math.Max(val1: fall, val2: gravity.Fall);
            }
        }

        return fall;
    }
}
