using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SdfVm;

/// <summary>Which side of a form band's front a slot run is drawn on (<see cref="SdfFormBand.Side"/>).</summary>
public enum SdfFormBandSide : byte {
    /// <summary>The run dissolves: it is drawn only ahead of the front, where the front has not yet passed, and its motes
    /// leave the surface where the front crosses it.</summary>
    Ahead = 0,
    /// <summary>The run materializes: it is drawn only where the front has passed, and its motes arrive at the surface
    /// just as the front reaches them.</summary>
    Swept = 1,
    /// <summary>The run stays whole: the band cuts nothing, its edge glows on both sides of the front, and its motes leave
    /// the surface where the front crosses it (a force-field shell flaring, a net's strands parting).</summary>
    Whole = 2,
}

/// <summary>
/// A form band: a front that sweeps over one run of dynamic-transform slots (a body's form, or one of its pieces) and
/// cuts its draws where it has not yet built them or has already dissolved them, with an emissive edge on the side that
/// stays and motes rising off or settling onto the real surface the front crosses. Presentation only: a producer hands
/// one a frame (<see cref="SdfFrameContent.FormBands"/>), in world space at the frame's pose, and the renderer reads it
/// from the frame's sampled table (<see cref="SdfFrameSampled"/>, <c>frame/sdf-form-band.hlsli</c>).
/// <para>
/// The front lies <see cref="Low"/> + (<see cref="High"/> − <see cref="Low"/>) · min(<see cref="Elapsed"/> /
/// <see cref="Seconds"/>, 1) along <see cref="Axis"/> from <see cref="Origin"/>, roughened at each surface point by
/// <see cref="Roughness"/> times the lattice noise at that point; the kernels derive it from these fields with one
/// formula (<c>sdfFormBandFront</c>), so the cut, the edge and every mote's birth agree to the bit. A mote is generated,
/// never stored: its surface point, its birth and its path are closed forms of <see cref="Seed"/>, its index and the
/// elapsed time (<c>passes/sdf-motes.comp.hlsl</c>).
/// </para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct SdfFormBand {
    /// <summary>The most bands one frame carries; a producer's bands past it are dropped, nearest first kept by the
    /// producer's order.</summary>
    public const int MaxBands = SdfFrameSampled.MaxBands;
    /// <summary>The most motes one frame's bands generate together; a band whose motes pass it generates only those
    /// that fit.</summary>
    public const int MaxMotes = SdfFrameSampled.MaxMotes;

    /// <summary>Gets the run's first dynamic-transform slot.</summary>
    public int FirstSlot { get; init; }
    /// <summary>Gets the run's slot count; a band of no slots cuts nothing.</summary>
    public int SlotCount { get; init; }
    /// <summary>Gets which side of the front the run is drawn on.</summary>
    public SdfFormBandSide Side { get; init; }
    /// <summary>Gets whether the run's slots move apart (a form shed or assembled piece by piece), so a draw that blends
    /// several of them, a skinned seam, is not drawn while the band stands.</summary>
    public bool Parted { get; init; }
    /// <summary>Gets the seed every hash of the band (its edge's noise, its motes) is keyed on.</summary>
    public uint Seed { get; init; }
    /// <summary>Gets the world point the front's distance is measured from.</summary>
    public Vector3 Origin { get; init; }
    /// <summary>Gets the unit world direction the front sweeps along.</summary>
    public Vector3 Axis { get; init; }
    /// <summary>Gets where the front starts, metres along <see cref="Axis"/> from <see cref="Origin"/>.</summary>
    public float Low { get; init; }
    /// <summary>Gets where the front ends, metres along <see cref="Axis"/> from <see cref="Origin"/>; at least
    /// <see cref="Low"/>.</summary>
    public float High { get; init; }
    /// <summary>Gets the seconds since the front started.</summary>
    public float Elapsed { get; init; }
    /// <summary>Gets the seconds the front takes from <see cref="Low"/> to <see cref="High"/>; positive.</summary>
    public float Seconds { get; init; }
    /// <summary>Gets the edge's emitted light, linear scene colour in nits; black draws no edge.</summary>
    public Vector3 EdgeLight { get; init; }
    /// <summary>Gets how far behind the front the edge glows, metres.</summary>
    public float EdgeWidth { get; init; }
    /// <summary>Gets the most the front is moved along <see cref="Axis"/> at a point by the lattice noise there, metres;
    /// zero is a flat front.</summary>
    public float Roughness { get; init; }
    /// <summary>Gets the lattice noise's frequency, cycles a metre.</summary>
    public float NoiseScale { get; init; }
    /// <summary>Gets how many motes the band generates; zero for none.</summary>
    public int MoteCount { get; init; }
    /// <summary>Gets each mote's emitted light, linear scene colour in nits, at the brightest point of its life.</summary>
    public Vector3 MoteLight { get; init; }
    /// <summary>Gets a mote's diameter, metres.</summary>
    public float MoteSize { get; init; }
    /// <summary>Gets a mote's life, seconds: from leaving the surface until it fades, or from appearing until it settles.</summary>
    public float MoteSeconds { get; init; }
    /// <summary>Gets how fast a mote drifts off its surface point, metres a second.</summary>
    public float MoteSpeed { get; init; }
    /// <summary>Gets the acceleration a mote takes along world up, metres a second squared: positive rises, negative
    /// falls.</summary>
    public float MoteRise { get; init; }
    /// <summary>Gets how fast a mote's drift turns about <see cref="Axis"/>, radians a second.</summary>
    public float MoteSwirl { get; init; }

    /// <summary>Gets the displacement whose draws the band sweeps instead of a run of slots (a net's sheet, whose static
    /// draws ride no frame slot), or <see langword="null"/> for a band over <see cref="FirstSlot"/> and
    /// <see cref="SlotCount"/>. A displacement the frame's program does not hold leaves the band out.</summary>
    public Puck.SignedDistance.SdfDisplacement? Displacement { get; init; }
    /// <summary>Gets whether the front is a sphere about <see cref="Origin"/> growing from <see cref="Low"/> to
    /// <see cref="High"/> metres across (a hole opening, a shell flaring), rather than a plane sweeping along
    /// <see cref="Axis"/>; <see cref="Axis"/> then only leans the motes' drift.</summary>
    public bool Radial { get; init; }

    /// <summary>Returns whether a band cuts anything: it covers a slot or a displacement and its sweep has length and
    /// time.</summary>
    public bool IsLive => (((SlotCount > 0) || (Displacement is not null)) && (Seconds > 0f) && (High >= Low) && float.IsFinite(f: Origin.LengthSquared()) && (Axis.LengthSquared() > 0f));
}
