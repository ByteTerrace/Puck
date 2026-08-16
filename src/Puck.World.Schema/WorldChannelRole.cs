namespace Puck.World;

/// <summary>The engine motion role a declared world channel's value drives directly. A compiled
/// <see cref="WorldChannelTable"/> resolves each claimed role to its authored ordinal.</summary>
public enum ChannelRole : byte {
    /// <summary>Motion along facing, +1 forward / -1 back.</summary>
    MoveForward,

    /// <summary>Motion along the body's right, +1 right / -1 left.</summary>
    MoveStrafe,

    /// <summary>Yaw rate about the body's up, +1 left (counter-clockwise) / -1 right.</summary>
    Turn,

    /// <summary>Motion along the body's up, +1 up / -1 down.</summary>
    MoveUp,

    /// <summary>Pitch rate about the body's right, +1 nose-up / -1 nose-down.</summary>
    Pitch,

    /// <summary>Roll rate about the body's forward, +1 / -1.</summary>
    Roll,
}
