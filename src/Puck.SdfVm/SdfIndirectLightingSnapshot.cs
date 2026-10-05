using Puck.SignedDistance;
using Puck.Hosting;

namespace Puck.SdfVm;

/// <summary>The actual source held by one finite indirect solve. Mutable CPU tables and declared GPU environment
/// buffers are copied on capture; later live lighting, images or transforms cannot relabel this result.</summary>
public sealed class SdfIndirectLightingSnapshot {
    private readonly SdfFrame m_frame;

    internal SdfIndirectLightingSnapshot(SdfFrame frame, SdfLightGeometry geometry, ulong sequence, GpuImagePublication environment, GpuImagePublication screens, bool tainted) {
        m_frame = frame;
        Geometry = geometry;
        Sequence = sequence;
        Environment = environment;
        Screens = screens;
        Tainted = tainted;
        var lights = new SdfLight[SdfLights.MaxLights];
        frame.Lights.Pack(lights);
        Lights = Array.AsReadOnly(lights);
    }

    /// <summary>Gets the immutable program whose full material identities and geometry the solve consumes.</summary>
    public SdfProgram Program => m_frame.Program;
    /// <summary>Gets the captured transforms, not the current presentation frame's transforms.</summary>
    public IReadOnlyList<DynamicTransform> DynamicTransforms => m_frame.DynamicTransforms;
    /// <summary>Gets the actual packed light records, including bounce gains.</summary>
    public IReadOnlyList<SdfLight> Lights { get; }
    /// <summary>Gets the active prefix of <see cref="Lights"/>.</summary>
    public int LightCount => m_frame.Lights.Count;
    /// <summary>Gets the immutable gains applied at the source origins and reflected hops of this solve.</summary>
    public SdfIndirectGains Gains => m_frame.IndirectGains;
    /// <summary>Gets the authored feedback depth request. The publication's actual sweeps also obey its tier cap.</summary>
    public int? Bounces => m_frame.IndirectBounces;
    /// <summary>Gets the exact geometry revisions at capture.</summary>
    public SdfLightGeometry Geometry { get; }
    /// <summary>Gets the capture sequence within this cache's source owner.</summary>
    public ulong Sequence { get; }
    /// <summary>Gets the exact submitted environment whose map and coefficients were copied together. Unknown means
    /// the solve has no sky source; it never names a later live projection or an earlier completed buffer.</summary>
    public GpuImagePublication Environment { get; }
    /// <summary>Gets the actual acquired-image reduction held by this solve. It never denotes a CPU host color or a
    /// newer live image; unknown means the screen source category is disabled.</summary>
    public GpuImagePublication Screens { get; }
    /// <summary>Gets whether the immutable source includes unfilled external pixels. Feedback derived from this
    /// source keeps that taint until a complete solve from capture-filled inputs replaces it.</summary>
    public bool Tainted { get; }

    /// <summary>Returns a private frame for CPU reference evaluation, with independent mutable light and sky tables.
    /// Read-only program and transform data are shared with this immutable capture.</summary>
    /// <returns>The source frame that produced this solve; no later live frame is consulted.</returns>
    public SdfFrame CopyFrame() {
        var lights = new SdfLights();
        lights.CopyFrom(m_frame.Lights);
        var sky = new SdfSky();
        sky.CopyFrom(m_frame.Sky);
        return m_frame with { Lights = lights, Sky = sky };
    }
}
