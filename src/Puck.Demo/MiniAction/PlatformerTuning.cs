using Puck.Maths;

namespace Puck.Demo.MiniAction;

/// <summary>
/// Every knob that shapes how the avatar runs and jumps, in one place so the feel can be dialed in. The defaults target
/// a Hollow Knight–tight character: near-instant acceleration and turn, a fixed run speed, a snappy variable-height
/// jump, asymmetric gravity (you fall faster than you rise), and the two forgiveness windows that make platformers feel
/// fair — coyote time and jump buffering. Units are world units (the room spans roughly ±8) and seconds. Every value is
/// <see cref="FixedQ4816"/>, so the simulation that consumes them is integer-only and bit-identical across machines;
/// the defaults are the exact fixed quantization of the historic float tuning.
/// </summary>
public sealed record PlatformerTuning {
    /// <summary>Top horizontal run speed (units/second).</summary>
    public FixedQ4816 RunSpeed { get; init; } = FixedQ4816.FromDouble(value: 8d);
    /// <summary>Horizontal acceleration toward the target while grounded (units/second²). High = HK-instant.</summary>
    public FixedQ4816 GroundAcceleration { get; init; } = FixedQ4816.FromDouble(value: 90d);
    /// <summary>Horizontal deceleration to zero when there's no stick input, grounded (units/second²).</summary>
    public FixedQ4816 GroundDeceleration { get; init; } = FixedQ4816.FromDouble(value: 110d);
    /// <summary>Horizontal acceleration toward the target while airborne (units/second²) — usually less than grounded.</summary>
    public FixedQ4816 AirAcceleration { get; init; } = FixedQ4816.FromDouble(value: 60d);
    /// <summary>Horizontal deceleration while airborne with no input (units/second²).</summary>
    public FixedQ4816 AirDeceleration { get; init; } = FixedQ4816.FromDouble(value: 35d);

    /// <summary>Upward velocity imparted at the start of a jump (units/second).</summary>
    public FixedQ4816 JumpSpeed { get; init; } = FixedQ4816.FromDouble(value: 11d);
    /// <summary>Downward acceleration while rising (units/second²). Lower than <see cref="FallGravity"/> for a floaty top.</summary>
    public FixedQ4816 RiseGravity { get; init; } = FixedQ4816.FromDouble(value: 28d);
    /// <summary>Downward acceleration while falling (units/second²). Higher than rise gravity for a snappy descent.</summary>
    public FixedQ4816 FallGravity { get; init; } = FixedQ4816.FromDouble(value: 46d);
    /// <summary>Terminal downward speed (units/second).</summary>
    public FixedQ4816 MaxFallSpeed { get; init; } = FixedQ4816.FromDouble(value: 40d);
    /// <summary>On early jump release while still rising, upward velocity is multiplied by this — the variable jump
    /// height that lets a tap be a short hop and a hold be a full leap. 1 = no cut, 0 = hard stop.</summary>
    public FixedQ4816 JumpCutMultiplier { get; init; } = FixedQ4816.FromDouble(value: 0.45d);
    /// <summary>Grace window after walking off a ledge during which a jump still fires (seconds).</summary>
    public FixedQ4816 CoyoteTime { get; init; } = FixedQ4816.FromDouble(value: 0.09d);
    /// <summary>Window before landing during which a jump press is remembered and fires on touchdown (seconds).</summary>
    public FixedQ4816 JumpBufferTime { get; init; } = FixedQ4816.FromDouble(value: 0.10d);

    /// <summary>The HK-tight defaults.</summary>
    public static PlatformerTuning Default { get; } = new();
}
