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
/// hold, and the one place buoyancy is described. Required for that bond and refused on every other.</summary>
/// <param name="Buoyancy">The idle vertical drift velocity below the bob band, signed (u/s).</param>
/// <param name="MaxRiseSpeed">The terminal ascent speed (u/s).</param>
/// <param name="MaxSinkSpeed">The terminal descent speed (u/s).</param>
/// <param name="SurfaceSettleRate">The proportional settle gain toward the float line (1/s).</param>
/// <param name="FloatDepth">The float line's depth below the medium surface, and the bob band's half-width (u).</param>
public sealed record WorldHoldMedium(
    float Buoyancy,
    float MaxRiseSpeed,
    float MaxSinkSpeed,
    float SurfaceSettleRate,
    float FloatDepth
);
/// <summary>The vertical arc a <see cref="BodyHoldKind.Gravity"/> or <see cref="BodyHoldKind.Lift"/> row falls
/// under — the same asymmetric arc <c>ApplyHold</c> used to read off the kit as a single flat trio, now carried per
/// row so a kit's ground, wall, and air rows may each fall differently. Required on those two kinds; refused on
/// <see cref="BodyHoldKind.Grip"/> (gravity is suspended while a grip holds) and on a <see cref="BodyHoldBond.Medium"/>
/// row (a medium displaces by its own law).</summary>
/// <param name="Rise">The downward acceleration while rising (u/s²) — the floaty top of the arc.</param>
/// <param name="Fall">The downward acceleration while falling (u/s²) — the snappy descent (heavier than the rise).
/// The world's own solved gravity field, where one is authored, overrides the MAGNITUDE but keeps this row's
/// rise-to-fall ratio as the arc's asymmetry.</param>
/// <param name="Terminal">The terminal fall speed the descent is clamped to (u/s).</param>
public sealed record WorldHoldGravity(
    float Rise,
    float Fall,
    float Terminal
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
/// <param name="Grip">The inward pull, world units per second, under <see cref="BodyHoldKind.Grip"/>. Applied as a
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
/// <see cref="BodyHoldKind.Lift"/>; refused for <see cref="BodyHoldKind.Grip"/>, <see cref="BodyHoldKind.None"/>,
/// and every <see cref="BodyHoldBond.Medium"/> row.</param>
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
    float Grip = 0f,
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
    float Thrust = 0f
);
/// <summary>The one-time compilation of an authored hold list into the fixed-point form simulation reads. Channel
/// names resolve through the world's compiled channel table here, the same resolved-outside/consumed-as-ordinal
/// seam <see cref="FixedWorldKit.SprintChannelOrdinal"/> uses.</summary>
public static class WorldHoldFactory {
    private const double DegreesToRadians = (Math.PI / 180.0);

    /// <summary>Compiles a medium's displacement law, or the inert zeroed law for a row that carries none.</summary>
    /// <param name="medium">The authored law, or <see langword="null"/>.</param>
    /// <returns>The compiled law.</returns>
    public static FixedBodyMedium CompileMedium(WorldHoldMedium? medium) => ((medium is { } law)
        ? new FixedBodyMedium(
            Buoyancy: FixedQ4816.FromDouble(value: law.Buoyancy),
            FloatDepth: FixedQ4816.FromDouble(value: law.FloatDepth),
            MaxRiseSpeed: FixedQ4816.FromDouble(value: law.MaxRiseSpeed),
            MaxSinkSpeed: FixedQ4816.FromDouble(value: law.MaxSinkSpeed),
            SurfaceSettleRate: FixedQ4816.FromDouble(value: law.SurfaceSettleRate)
        )
        : default
    );
    /// <summary>Compiles a row's vertical arc, or the inert zeroed arc for a row that carries none.</summary>
    /// <param name="gravity">The authored arc, or <see langword="null"/>.</param>
    /// <returns>The compiled arc.</returns>
    public static FixedBodyHoldGravity CompileGravity(WorldHoldGravity? gravity) => ((gravity is { } arc)
        ? new FixedBodyHoldGravity(
            Fall: FixedQ4816.FromDouble(value: arc.Fall),
            Rise: FixedQ4816.FromDouble(value: arc.Rise),
            Terminal: FixedQ4816.FromDouble(value: arc.Terminal)
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
                Forward: hold.Forward,
                Gravity: CompileGravity(gravity: hold.Gravity),
                Grip: FixedQ4816.FromDouble(value: hold.Grip),
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
    /// <summary>Gets the fastest terminal fall speed any <see cref="BodyHoldKind.Gravity"/> or
    /// <see cref="BodyHoldKind.Lift"/> row in a kit's hold list authors, or zero for a kit authoring none of
    /// either — the per-row generalization of the retired kit-level <c>maxFallSpeed</c> a document-wide speed
    /// ceiling reads.</summary>
    /// <param name="holds">The authored rows, or <see langword="null"/>.</param>
    /// <returns>The fastest authored terminal speed.</returns>
    public static float MaxTerminalFallSpeed(IReadOnlyList<WorldHold>? holds) {
        var terminal = 0f;

        foreach (var hold in (holds ?? [])) {
            if (hold?.Gravity is { } gravity) {
                terminal = Math.Max(val1: terminal, val2: gravity.Terminal);
            }
        }

        return terminal;
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
