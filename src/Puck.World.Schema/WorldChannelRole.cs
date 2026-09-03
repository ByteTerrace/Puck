using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>The engine motion role a declared world channel's value drives directly — the continuous signed axes the
/// body motion ops read (discrete verbs are composition channels, not roles; every role channel is bipolar by
/// validator rule). A compiled <see cref="WorldChannelTable"/> resolves each claimed role to its authored ordinal.</summary>
[JsonConverter(typeof(StrictEnumConverter<ChannelRole>))]
public enum ChannelRole : byte {
    /// <summary>Motion along facing, +1 ahead / -1 back.</summary>
    MoveAdvance,

    /// <summary>Motion along the body's right, +1 right / -1 left.</summary>
    MoveStrafe,

    /// <summary>The heading turn rate, +1 left (counter-clockwise about the body's up) / -1 right. The grounded program
    /// integrates it into the heading scalar the attitude derives from; the free program reads it as the yaw rate
    /// beside <see cref="Pitch"/>/<see cref="Roll"/>.</summary>
    Turn,

    /// <summary>Motion along the body's up, +1 up / -1 down.</summary>
    MoveUp,

    /// <summary>Pitch rate about the body's right, +1 nose-up / -1 nose-down.</summary>
    Pitch,

    /// <summary>Roll rate about the body's forward axis, +1 banks left (the body's right rises) / -1 right.</summary>
    Roll,

    /// <summary>The world +X component of a commanded facing direction — with <see cref="FaceY"/> and
    /// <see cref="FaceZ"/> a world-frame vector; nonzero, it is the direction the body faces this tick, ahead of
    /// movement-facing. All zero commands nothing.</summary>
    FaceX,

    /// <summary>The world +Y (up) component of a commanded facing direction. Carried so the commanded facing rides as
    /// a whole vector; no motion op reads it yet — the yaw snap takes its angle from the planar pair alone.</summary>
    FaceY,

    /// <summary>The world +Z component of a commanded facing direction (see <see cref="FaceX"/>).</summary>
    FaceZ,

    /// <summary>The world +X component of a commanded movement direction — with <see cref="MoveY"/> and
    /// <see cref="MoveZ"/> a full world-frame vector. Claimed, it supersedes the
    /// <see cref="MoveAdvance"/>/<see cref="MoveStrafe"/> pair whenever it is nonzero.</summary>
    /// <remarks>The planar pair can only carry a direction in the world's horizontal plane, which is all a body
    /// standing on world +Y ever needs. A body standing on an arbitrary surface — anywhere on a planetoid — needs
    /// the vertical component too: from the side of a sphere, "over the top" is not expressible horizontally at all,
    /// and a body given only the pair is confined to the band the projection leaves it. Carrying the direction whole
    /// also removes the question of which basis the pair is read against, which is not answerable from a scalar yaw
    /// about world up once the body's own up has moved.</remarks>
    MoveX,

    /// <summary>The world +Y component of a commanded movement direction (see <see cref="MoveX"/>).</summary>
    MoveY,

    /// <summary>The world +Z component of a commanded movement direction (see <see cref="MoveX"/>).</summary>
    MoveZ,
}
