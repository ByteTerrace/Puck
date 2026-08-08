using Puck.Maths;

namespace Puck.Demo.Overworld;

/// <summary>
/// Every knob that shapes how a <see cref="FieldWalkerBody"/> walks and falls, in one place so the feel can be dialed
/// in — the gravity-arc sibling of <see cref="PlatformerTuning"/>. Unlike the flat-floor platformer, there is no
/// fixed "world up" here (a body standing on a planetoid's far side has <see cref="FieldWalkerBody.Up"/> pointing the
/// opposite way from one standing at the near pole), so every quantity below is expressed relative to the body's OWN
/// tangent plane and normal, not world axes. The defaults are DELIBERATELY DEFENSIBLE, not measured: this wave's
/// proof is "a body can walk a planet's surface and orient correctly," not a shipped feel — a later pass tunes these
/// by verb while tuning the planetoid scenario. Every value is <see cref="FixedQ4816"/>
/// (units/second, units/second², or world units), so the simulation that consumes them is integer-only and
/// bit-identical across machines.
/// </summary>
public sealed record FieldWalkerTuning {
    /// <summary>Top walking speed IN THE TANGENT PLANE (units/second). Feel-tunable; matches
    /// <see cref="PlatformerTuning.RunSpeed"/>'s order of magnitude so a walker feels like a sibling of the flat-floor
    /// avatar, not a different creature.</summary>
    public FixedQ4816 WalkSpeed { get; init; } = FixedQ4816.FromDouble(value: 6d);
    /// <summary>The hold-to-run multiplier on <see cref="WalkSpeed"/>. 1 disables sprinting.</summary>
    public FixedQ4816 SprintMultiplier { get; init; } = FixedQ4816.FromDouble(value: 1.6d);
    /// <summary>Tangential acceleration toward the target speed while grounded (units/second²).</summary>
    public FixedQ4816 GroundAcceleration { get; init; } = FixedQ4816.FromDouble(value: 70d);
    /// <summary>Tangential deceleration to zero with no stick input, grounded (units/second²).</summary>
    public FixedQ4816 GroundDeceleration { get; init; } = FixedQ4816.FromDouble(value: 90d);
    /// <summary>Tangential acceleration toward the target speed while airborne (units/second²) — lower than grounded,
    /// so a falling body only weakly steers.</summary>
    public FixedQ4816 AirAcceleration { get; init; } = FixedQ4816.FromDouble(value: 20d);
    /// <summary>Tangential deceleration while airborne with no input (units/second²).</summary>
    public FixedQ4816 AirDeceleration { get; init; } = FixedQ4816.FromDouble(value: 10d);

    /// <summary>Acceleration along <c>-Up</c> (units/second²) — the field's gradient supplies the DIRECTION, this
    /// tuning value supplies the MAGNITUDE (the field itself carries no notion of gravity's strength, only its
    /// direction — see <see cref="Puck.SdfVm.Queries.IFieldEvaluator"/>'s remarks). Deliberately gentler than
    /// <see cref="PlatformerTuning.FallGravity"/> (46) — this wave's proof scene is planet-SCALE (a body should have
    /// time to see the curvature and correct its footing before slamming into the surface), not a tight platformer
    /// arc.</summary>
    public FixedQ4816 GravityAcceleration { get; init; } = FixedQ4816.FromDouble(value: 18d);
    /// <summary>Terminal fall speed along <c>-Up</c> (units/second).</summary>
    public FixedQ4816 MaxFallSpeed { get; init; } = FixedQ4816.FromDouble(value: 30d);

    /// <summary>The ground-snap acceptance distance (world units): once the field's signed distance at the body's
    /// (post-integration) position falls at or under this AND the body is moving inward (not pulling away), the body
    /// snaps exactly onto the surface and its normal velocity is discarded. Sized well above the evaluator's own
    /// resolution floor (<see cref="Puck.SdfVm.Queries.SdfFieldEvaluator"/>'s documented <c>HitEpsilon</c>, 0.001) so
    /// a single tick's fall speed at typical <c>dt</c> cannot tunnel past it before the NEXT tick's snap check runs,
    /// yet small enough that the body visibly rests ON the surface rather than hovering.</summary>
    public FixedQ4816 GroundSnapEpsilon { get; init; } = FixedQ4816.FromDouble(value: 0.08d);

    /// <summary>The gentle, planet-scale defaults.</summary>
    public static FieldWalkerTuning Default { get; } = new();
}
