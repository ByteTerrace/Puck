using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>Decides when a residency's environment needs rendering. Candidates are compared to the last rendered
/// coefficients, so skipped changes accumulate. A camera-only edit never projects the sky.</summary>
public sealed class SdfSkyEnvironmentRefresh {
    private readonly SdfSkyLayer[] m_candidateLayers = new SdfSkyLayer[SdfSky.MaxLayers];
    private readonly Vector3[] m_map = new Vector3[SdfSkyEnvironment.Texels];
    private readonly Vector3[] m_candidate = new Vector3[SdfSkyEnvironment.CoefficientCount];
    private readonly Vector3[] m_rendered = new Vector3[SdfSkyEnvironment.CoefficientCount];
    private SdfSkyBlock m_candidateBlock;
    private bool m_hasCandidate;
    private bool m_hasRendered;

    /// <summary>Gets whether the last decision projected a candidate.</summary>
    public bool Projected { get; private set; }
    /// <summary>Gets whether the last decision skipped a changed sky below one display code.</summary>
    public bool Skipped { get; private set; }

    /// <summary>Projects a new lighting-visible candidate and decides whether it crosses a display code.</summary>
    /// <param name="block">The packed sky block.</param>
    /// <param name="layers">The packed layer table.</param>
    /// <returns>Whether the environment pass must render.</returns>
    public bool Owes(in SdfSkyBlock block, ReadOnlySpan<SdfSkyLayer> layers) {
        Projected = false;
        Skipped = false;
        if (!(block.FogDensity > 0f || block.Ambient > 0f || block.Reflection > 0f)) {
            return false;
        }
        if (!m_hasCandidate || !SdfSkyEnvironment.SameMap(block, layers, m_candidateBlock, m_candidateLayers)) {
            SdfSkyEnvironment.Render(block, layers, m_map);
            SdfSkyEnvironment.Project(m_map, m_candidate);
            m_candidateBlock = block;
            layers.CopyTo(m_candidateLayers);
            m_hasCandidate = true;
            Projected = true;
        }
        if (!m_hasRendered) {
            return true;
        }
        var changed = SdfSkyEnvironment.IrradianceDifference(m_candidate, m_rendered) >= SdfSkyEnvironment.DisplayCode;
        Skipped = Projected && !changed;
        return changed;
    }

    /// <summary>Records that the last candidate's map and coefficients have been submitted for rendering.</summary>
    public void Rendered() {
        m_candidate.CopyTo(m_rendered, 0);
        m_hasRendered = true;
    }

    /// <summary>Invalidates the held projection and rendered sky after a kernel reload.</summary>
    public void Forget() {
        m_hasCandidate = false;
        m_hasRendered = false;
    }
}
