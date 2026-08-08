using System.Numerics;

namespace Puck.Demo.Creator;

/// <summary>One shape's pose inside a timeline frame (matched back to the shape by id on apply — a pose whose shape
/// was deleted is skipped harmlessly).</summary>
/// <param name="Id">The shape id the pose belongs to.</param>
/// <param name="Position">The pose position.</param>
/// <param name="Rotation">The pose orientation.</param>
/// <param name="Scale">The pose scale.</param>
public readonly record struct CreatorFramePose(int Id, Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>One timeline frame: a named FULL snapshot of every shape's transform. The deliberate minimal animation
/// model — no per-shape keys, no interpolation (hold-style playback reads correctly for brick-target art); the bake
/// consumes the frame set as its animation frames.</summary>
/// <param name="Name">The frame's name (auto-named <c>f1</c>, <c>f2</c>… on record).</param>
/// <param name="Poses">Every shape's pose at record time.</param>
public sealed record CreatorFrameState(string Name, IReadOnlyList<CreatorFramePose> Poses);
