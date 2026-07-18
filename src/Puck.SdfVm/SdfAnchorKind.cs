namespace Puck.SdfVm;

/// <summary>
/// What a posed thing (a camera eye, a light, any future anchor-riding primitive) is anchored TO — the engine-side
/// vocabulary a host's own anchor kinds (a creation's shape, a world's placement) map onto. Pure classification: this
/// type carries no resolver of its own (see <see cref="ISdfAnchorSource"/> for that) — it only says which OF a host's
/// id spaces an anchor id is drawn from, so a shared primitive (like the demo's <c>CameraEye</c>) can store one
/// without knowing what a "shape" or a "placement" is.
/// </summary>
public enum SdfAnchorKind {
    /// <summary>No anchor: the thing poses directly in world (or render-relative) space via its own stored fields,
    /// unchanging until an authoring verb moves it — <see cref="Views.FixedRig"/>'s shape.</summary>
    World,

    /// <summary>Anchored to a BODY — a live-animated shape with its own pose stream (a creation's shape, a walking
    /// avatar): the anchored thing rides that body's live frame, so it follows IK/animation/locomotion. Named "Body"
    /// rather than "Shape" so the vocabulary reads for a walking avatar too, not only an authored creation shape.</summary>
    Body,

    /// <summary>Anchored to an INSTANCE — a placed/stamped occurrence of something in a world (a world placement, a
    /// stamped assembly): the anchored thing rides that instance's transform, so a camera on a dragged prop moves
    /// with it.</summary>
    Instance,
}
