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
    /// <summary>The world +X component of a commanded facing direction — with <see cref="FaceY"/> and
    /// <see cref="FaceZ"/> a world-frame vector; nonzero, it is the direction the body faces this tick, ahead of
    /// movement-facing. All zero commands nothing.</summary>
    FaceX,
    /// <summary>The world +Y (up) component of a commanded facing direction. Carried for attitude-bearing bodies; the
    /// yaw snap reads only the planar pair, so a grounded body ignores it.</summary>
    FaceY,
    /// <summary>The world +Z component of a commanded facing direction (see <see cref="FaceX"/>).</summary>
    FaceZ,
}
