using System.Numerics;

namespace Puck.World.Client;

/// <summary>One seat's resolved view-camera pose for the listener policy — filled by the frame source from the same
/// rig resolution the seat renders through (the editor rig when the seat edits), so "focus" listens where the active
/// view looks. That rig anchors on the seat's perceived body (<see cref="WorldPerceptionAnchor"/> — the bound body,
/// or the routed body while possessing), so the listener follows a possession anchor swap by construction, together
/// with the camera.</summary>
/// <param name="Joined">Whether the seat is joined this frame.</param>
/// <param name="Eye">The resolved camera eye, world space.</param>
/// <param name="Forward">The resolved camera forward (eye → target), world space.</param>
public readonly record struct WorldSeatCameraPose(bool Joined, Vector3 Eye, Vector3 Forward);
