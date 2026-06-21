namespace Puck.Demo.MiniAction;

/// <summary>
/// Every knob that shapes how the avatar runs and jumps, in one place so the feel can be dialed in. The defaults target
/// a Hollow Knight–tight character: near-instant acceleration and turn, a fixed run speed, a snappy variable-height
/// jump, asymmetric gravity (you fall faster than you rise), and the two forgiveness windows that make platformers feel
/// fair — coyote time and jump buffering. Units are world units (the room spans roughly ±8) and seconds. The simulation
/// steps at a FIXED tick, so these are frame-rate independent and deterministic.
/// </summary>
public sealed record PlatformerTuning {
    /// <summary>Top horizontal run speed (units/second).</summary>
    public float RunSpeed { get; init; } = 8f;
    /// <summary>Horizontal acceleration toward the target while grounded (units/second²). High = HK-instant.</summary>
    public float GroundAcceleration { get; init; } = 90f;
    /// <summary>Horizontal deceleration to zero when there's no stick input, grounded (units/second²).</summary>
    public float GroundDeceleration { get; init; } = 110f;
    /// <summary>Horizontal acceleration toward the target while airborne (units/second²) — usually less than grounded.</summary>
    public float AirAcceleration { get; init; } = 60f;
    /// <summary>Horizontal deceleration while airborne with no input (units/second²).</summary>
    public float AirDeceleration { get; init; } = 35f;

    /// <summary>Upward velocity imparted at the start of a jump (units/second).</summary>
    public float JumpSpeed { get; init; } = 11f;
    /// <summary>Downward acceleration while rising (units/second²). Lower than <see cref="FallGravity"/> for a floaty top.</summary>
    public float RiseGravity { get; init; } = 28f;
    /// <summary>Downward acceleration while falling (units/second²). Higher than rise gravity for a snappy descent.</summary>
    public float FallGravity { get; init; } = 46f;
    /// <summary>Terminal downward speed (units/second).</summary>
    public float MaxFallSpeed { get; init; } = 40f;
    /// <summary>On early jump release while still rising, upward velocity is multiplied by this — the variable jump
    /// height that lets a tap be a short hop and a hold be a full leap. 1 = no cut, 0 = hard stop.</summary>
    public float JumpCutMultiplier { get; init; } = 0.45f;
    /// <summary>Grace window after walking off a ledge during which a jump still fires (seconds).</summary>
    public float CoyoteTime { get; init; } = 0.09f;
    /// <summary>Window before landing during which a jump press is remembered and fires on touchdown (seconds).</summary>
    public float JumpBufferTime { get; init; } = 0.10f;

    /// <summary>The HK-tight defaults.</summary>
    public static PlatformerTuning Default { get; } = new();
}
