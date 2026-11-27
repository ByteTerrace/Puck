using System.Numerics;

using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Where a live field slice lies and how it draws: the plane <see cref="Axis"/> = <see cref="Offset"/>, the
/// square of side <c>2·<see cref="Half"/></c> about <see cref="Centre"/> on it in <see cref="SdfFieldSlice"/>'s
/// <c>(u, v)</c> coordinates, its samples a side, its band spacing, the panel it draws and its opacity.</summary>
/// <param name="Axis">The axis the plane is normal to.</param>
/// <param name="Offset">The plane's coordinate along <paramref name="Axis"/>, in metres.</param>
/// <param name="Centre">The square's centre in the plane's <c>(u, v)</c> coordinates, in metres.</param>
/// <param name="Half">The square's half side, in metres; positive and finite.</param>
/// <param name="Resolution">The samples a side; within <see cref="SdfFieldSlice.MinResolution"/> and
/// <see cref="SdfFieldSlice.MaxResolution"/>.</param>
/// <param name="Bands">The distance between iso-distance bands, in metres; positive and finite.</param>
/// <param name="Mode">The panel or panels the plane draws.</param>
/// <param name="Opacity">How much of the view the plane covers where it lies in front of every surface, in (0, 1].</param>
public sealed record SdfFieldSlicePlane(SdfAxis Axis, float Offset, Vector2 Centre, float Half, int Resolution, float Bands, SdfFieldSliceMode Mode, float Opacity) {
    /// <summary>Gets the square's centre in world metres.</summary>
    public Vector3 CentrePoint => Axis switch {
        SdfAxis.X => new Vector3(x: Offset, y: Centre.Y, z: Centre.X),
        SdfAxis.Y => new Vector3(x: Centre.X, y: Offset, z: Centre.Y),
        _ => new Vector3(x: Centre.X, y: Centre.Y, z: Offset),
    };
    /// <summary>Gets the world direction of the plane's <c>u</c> coordinate, the image's right
    /// (<see cref="SdfFieldSlice.PlanePoint"/>).</summary>
    public Vector3 AxisU => ((Axis == SdfAxis.X) ? Vector3.UnitZ : Vector3.UnitX);
    /// <summary>Gets the world direction of the plane's <c>v</c> coordinate, the image's top
    /// (<see cref="SdfFieldSlice.PlanePoint"/>).</summary>
    public Vector3 AxisV => ((Axis == SdfAxis.Y) ? Vector3.UnitZ : Vector3.UnitY);
    /// <summary>Gets the plane with what changes its samples alone: the opacity, which the view applies, left at zero.</summary>
    internal SdfFieldSlicePlane Sampling => (this with { Opacity = 0f });
}

/// <summary>What a pass block carries of the live field slice it draws (<c>fieldSlice*</c>): all zero for none.</summary>
/// <param name="Origin">The square's centre in world metres, and the opacity, zero drawing nothing.</param>
/// <param name="AxisU">The unit world direction of the plane's <c>u</c> coordinate, and the square's half side in
/// metres.</param>
/// <param name="AxisV">The unit world direction of the plane's <c>v</c> coordinate, and the samples a side.</param>
public readonly record struct SdfFieldSliceValues(Vector4 Origin, Vector4 AxisU, Vector4 AxisV);

/// <summary>
/// A residency's live slicing plane: the field cut on a plane the author places, sampled by the mesh bake's field sampler
/// (<see cref="SdfMeshPathBake.Probe"/>, the one field walk) at a bounded resolution, coloured on the CPU by
/// <see cref="SdfFieldSlice.RenderPlane"/> (the colour mapping <c>world.slice</c>'s PNG draws with), and drawn translucent
/// over each view by the composite from the tables' slice words (<see cref="SdfWorldTables.StageFieldSlice"/>).
/// <para>It samples once for each plane and program: a plane that stands over a program that stands costs the bake and the
/// tables nothing, and with no plane set nothing is sampled, uploaded or drawn. While a moved plane or a new program
/// samples, the plane keeps showing its last image where that image was sampled.</para>
/// <para>Threads: <see cref="Plane"/> may be set from any thread; <see cref="Serve"/> runs in the residency's preparation,
/// which one thread at a time runs; the slice's completion runs on whichever thread answers its last lattice and publishes
/// its image through one volatile write, which <see cref="Shown"/> reads.</para>
/// </summary>
public sealed class SdfLiveFieldSlice {
    private SdfFieldSlicePlane? m_plane;
    private Image? m_image;
    private SdfFieldSlice? m_completed;
    // The preparation's own: the slice in flight, the plane and program it samples, and the plane and program the last
    // completed slice sampled.
    private SdfFieldSlice? m_sampling;
    private SdfFieldSlicePlane? m_samplingPlane;
    private SdfProgram? m_samplingProgram;
    private SdfFieldSlicePlane? m_sampledPlane;
    private SdfProgram? m_sampledProgram;

    /// <summary>A completed slice's colours and the plane they were sampled on.</summary>
    /// <param name="Plane">The plane the colours were sampled on, its opacity zero.</param>
    /// <param name="Words">The colours, one word a sample, rows from the top (<see cref="SdfFieldSlice.RenderPlane"/>).</param>
    public sealed record Image(SdfFieldSlicePlane Plane, uint[] Words);

    /// <summary>Gets or sets the plane the slice is cut on, or <see langword="null"/> for none, which draws nothing and
    /// samples nothing.</summary>
    public SdfFieldSlicePlane? Plane {
        get => Volatile.Read(location: ref m_plane);
        set => Volatile.Write(location: ref m_plane, value: value);
    }
    /// <summary>Gets the image the views draw and the plane that sets its opacity, or <see langword="null"/> while no plane
    /// is set or none has completed.</summary>
    public (Image Image, float Opacity)? Shown => ((Plane is { } plane) && (Volatile.Read(location: ref m_image) is { } image))
        ? (image, plane.Opacity)
        : null;
    /// <summary>Gets how many slices the plane has queued on the bake.</summary>
    public long Samplings { get; private set; }

    /// <summary>Queues a slice on the bake when the plane or the program moved since the last completed slice and none is
    /// in flight. A bake already sampling another slice (<c>world.slice</c>'s) leaves the queue to a later call.</summary>
    /// <param name="bake">The residency's bake, whose sampler answers the slice.</param>
    /// <param name="program">The program the residency's frame draws.</param>
    public void Serve(SdfMeshPathBake bake, SdfProgram program) {
        ArgumentNullException.ThrowIfNull(bake);
        ArgumentNullException.ThrowIfNull(program);

        if ((m_sampling is not null) && ReferenceEquals(objA: Volatile.Read(location: ref m_completed), objB: m_sampling)) {
            m_sampledPlane = m_samplingPlane;
            m_sampledProgram = m_samplingProgram;
            m_sampling = null;
        }
        if ((m_sampling is not null) || (Plane is not { } plane)) { return; }

        var sampling = plane.Sampling;

        if ((sampling == m_sampledPlane) && ReferenceEquals(objA: program, objB: m_sampledProgram)) { return; }

        var slice = new SdfFieldSlice(
            axis: sampling.Axis,
            bands: sampling.Bands,
            centre: sampling.Centre,
            completed: answered => Complete(answered: answered, plane: sampling),
            half: sampling.Half,
            offset: sampling.Offset,
            resolution: sampling.Resolution
        );

        if (!bake.Probe(slice: slice)) { return; }
        m_sampling = slice;
        m_samplingPlane = sampling;
        m_samplingProgram = program;
        Samplings++;
    }

    // Colours an answered slice and publishes it, on the thread that answered its last lattice.
    private void Complete(SdfFieldSlice answered, SdfFieldSlicePlane plane) {
        var words = new uint[(plane.Resolution * plane.Resolution)];

        answered.RenderPlane(mode: plane.Mode, words: words);
        Volatile.Write(location: ref m_image, value: new Image(Plane: plane, Words: words));
        Volatile.Write(location: ref m_completed, value: answered);
    }
}
