using Puck.SignedDistance;
using Puck.Hosting;

namespace Puck.SdfVm;

/// <summary>The actual CPU source held by one finite indirect solve. Mutable lighting and pose tables are copied on capture;
/// a later live light or transform cannot relabel a result rendered from this snapshot.</summary>
public sealed class SdfIndirectLightingSnapshot {
    private readonly SdfFrame m_frame;

    internal SdfIndirectLightingSnapshot(SdfFrame frame, SdfLightGeometry geometry, ulong sequence, GpuImagePublication environment) {
        m_frame = frame;
        Geometry = geometry;
        Sequence = sequence;
        Environment = environment;
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
    /// <summary>Gets the exact geometry revisions at capture.</summary>
    public SdfLightGeometry Geometry { get; }
    /// <summary>Gets the capture sequence within this cache's source owner.</summary>
    public ulong Sequence { get; }
    /// <summary>Gets the exact submitted environment whose map and coefficients were copied together. Unknown means
    /// the solve has no sky source; it never names a later live projection or an earlier completed buffer.</summary>
    public GpuImagePublication Environment { get; }

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
