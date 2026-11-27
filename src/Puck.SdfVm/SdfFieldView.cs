using System.Numerics;

namespace Puck.SdfVm;

/// <summary>How a field view's slicing plane is placed each frame (<see cref="SdfFieldPlane"/>). The values are the
/// kinds the field pass reads from the pass block's <c>fieldStyle.z</c>.</summary>
public enum SdfFieldPlaneKind {
    /// <summary>No plane.</summary>
    None = 0,
    /// <summary>A fixed plane: the points whose dot product with the plane's unit normal is its offset.</summary>
    Axis = 1,
    /// <summary>A plane facing each view, its offset metres ahead of the eye.</summary>
    Camera = 2,
    /// <summary>A horizontal plane its offset metres above each view's eye.</summary>
    Eye = 3,
    /// <summary>A plane through the surface under the pointer, facing the view or normal to a given axis.</summary>
    Pointer = 4,
}

/// <summary>Which reading a field view's slicing plane shows. The values are the panels the field pass reads from the pass
/// block's <c>fieldStyle.w</c>.</summary>
public enum SdfFieldPanel {
    /// <summary>The signed distance: warm bands outside, cool bands inside, darkening toward the surface, a white surface
    /// line.</summary>
    Distance = 0,
    /// <summary>The gradient's magnitude: white where the field is a distance, blue where it is a bound, red where it
    /// overestimates, a black surface line.</summary>
    Gradient = 1,
    /// <summary>The distance on the view's left half and the gradient on its right.</summary>
    Both = 2,
}

/// <summary>A field view's slicing plane: where it lies, how its bands are spaced, how much of the view it covers and
/// which reading it shows.</summary>
/// <param name="Kind">How the plane is placed each frame; not <see cref="SdfFieldPlaneKind.None"/>.</param>
/// <param name="Normal">The unit normal of an axis plane, or of a pointer plane normal to an axis; zero for a pointer plane
/// facing the view, and unused by the other kinds.</param>
/// <param name="Offset">An axis plane's offset along its normal, a camera plane's distance ahead of the eye or an eye
/// plane's height above it, in metres.</param>
/// <param name="Band">The distance between bands, in metres; positive.</param>
/// <param name="Opacity">How much of the view the plane covers where it lies in front of every surface, in (0, 1].</param>
/// <param name="Panel">The reading the plane shows.</param>
public sealed record SdfFieldPlane(SdfFieldPlaneKind Kind, Vector3 Normal, float Offset, float Band, float Opacity, SdfFieldPanel Panel);

/// <summary>
/// A residency's field views' settings: the slicing plane its views draw (<c>world.slice.live</c>), and the pointer a
/// pointer plane follows. The proximity view is the residency's debug mode (<see cref="DebugViewModes.Proximity"/>). While
/// neither shows, no view runs the field pass and the tables stage no entries (<see cref="SdfWorldResidency.ShowsField"/>).
/// <para>Threads: <see cref="Plane"/> and <see cref="Pointer"/> may be set from any thread; the residency's preparation reads
/// them once a frame.</para>
/// </summary>
public sealed class SdfFieldView {
    private SdfFieldPlane? m_plane;
    private Func<Vector2?>? m_pointer;
    private Vector2? m_lastPointer;

    /// <summary>Gets or sets the plane the views draw, or <see langword="null"/> for none.</summary>
    public SdfFieldPlane? Plane {
        get => Volatile.Read(location: ref m_plane);
        set => Volatile.Write(location: ref m_plane, value: value);
    }
    /// <summary>Gets or sets where the pointer lies in the residency's first view, as a share of its width and height, or
    /// <see langword="null"/> while the pointer is off it; read once a frame by a pointer plane, which keeps the last
    /// position read while the source answers none.</summary>
    public Func<Vector2?>? Pointer {
        get => Volatile.Read(location: ref m_pointer);
        set => Volatile.Write(location: ref m_pointer, value: value);
    }

    /// <summary>Reads the pointer for this frame: the source's answer, or the last one it gave while it gives none.</summary>
    /// <returns>The pointer's position as a share of the view's width and height, or <see langword="null"/> before the
    /// source has answered one.</returns>
    internal Vector2? ReadPointer() {
        if (Pointer?.Invoke() is { } pointer && float.IsFinite(f: pointer.X) && float.IsFinite(f: pointer.Y)) {
            m_lastPointer = pointer;
        }
        return m_lastPointer;
    }
}
