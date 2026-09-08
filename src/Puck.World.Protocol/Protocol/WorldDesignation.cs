using Puck.Maths;

namespace Puck.World.Protocol;

/// <summary>A target-bearing write into one target register owned by a body: exactly one of a concrete body
/// (<paramref name="Subject"/>) or a world-space point (<paramref name="Point"/>).</summary>
/// <param name="EntityIndex">The body whose register is written.</param>
/// <param name="Register">The authored target-register name.</param>
/// <param name="Subject">The proposed body subject; ignored when <paramref name="Point"/> is set.</param>
/// <param name="Point">The proposed world-space point, or <see langword="null"/> for a body designation.</param>
public readonly record struct WorldDesignation(int EntityIndex, string Register, GrantSubject Subject, FixedVector3? Point = null);
