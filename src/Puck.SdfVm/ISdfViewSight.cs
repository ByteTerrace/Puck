using Puck.Abstractions.Cameras;

namespace Puck.SdfVm;

/// <summary>
/// Completed evidence that a rendered view hides groups of boxes: each group's boxes are tested against the view's mesh
/// pass farthest-depth pyramid by the same test the pass culls its draws with, and a group answers hidden only when every
/// box was, on a completed recording captured for the group's version and the view's camera. Unknown, changed, retired and
/// lapsed observations keep demand. Evidence is presentation-only and describes an earlier completed frame, so a moving
/// occluder carries the readback's delay. Every member is called on the frame thread.
/// </summary>
public interface ISdfViewSight {
    /// <summary>Asks whether a view hides every box of a group, and keeps the group in the view's next captures.</summary>
    /// <param name="instance">The view instance whose mesh pass tests the group.</param>
    /// <param name="key">The group's key, unique within the view.</param>
    /// <param name="version">The group's version: a new version replaces the group's boxes and answers
    /// <see langword="false"/> until a recording captured for it completes.</param>
    /// <param name="camera">The view's current camera; a group is captured only by a recording of that exact camera.</param>
    /// <param name="boxes">The group's boxes, in world space; read only when <paramref name="version"/> or
    /// <paramref name="camera"/> differs from the group's request.</param>
    /// <returns><see langword="true"/> when a completed recording of this version and camera found every box hidden.</returns>
    bool Hidden(string instance, long key, long version, in CameraSnapshot camera, ReadOnlySpan<SdfMeshBox> boxes);
    /// <summary>Returns the frames the view's latest group took from its version's first ask to its first completed
    /// answer.</summary>
    /// <param name="instance">The view instance.</param>
    /// <returns>The frames, or -1 before any group completed.</returns>
    int LatencyFrames(string instance);
}
