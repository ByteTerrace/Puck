using System.Numerics;

namespace Puck.Demo.MiniAction;

/// <summary>
/// The static collision world for the prototype: a flat floor and an axis-aligned rectangular wall boundary, plus the
/// player avatar's box half-extents (so the body resolves against the surfaces with its own size). Pure data — the
/// visual SDF version of these same surfaces is built once by the frame source.
/// </summary>
public sealed record MiniActionRoom {
    /// <summary>The floor plane height (world Y); the player rests with its lower face on it.</summary>
    public float FloorY { get; init; } = 0f;
    /// <summary>The minimum XZ corner of the inner wall boundary (the player's box stops flush against it).</summary>
    public Vector2 BoundsMin { get; init; } = new(-8f, -8f);
    /// <summary>The maximum XZ corner of the inner wall boundary.</summary>
    public Vector2 BoundsMax { get; init; } = new(8f, 8f);
    /// <summary>The player avatar box half-extents (x = half width, y = half height, z = half depth).</summary>
    public Vector3 PlayerHalfExtents { get; init; } = new(0.35f, 0.5f, 0.35f);

    /// <summary>The default 16×16 walled room with a 0.7×1.0×0.7 player box.</summary>
    public static MiniActionRoom Default { get; } = new();
}
