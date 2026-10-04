using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>Decides when a residency's environment needs rendering. Candidates are compared to the last rendered
/// coefficients, so skipped changes accumulate. A camera-only edit never projects the sky. Image-backed candidates
/// follow exact acquired publication changes instead: the CPU reference does not sample device panoramas.</summary>
public sealed class SdfSkyEnvironmentRefresh {
    private readonly SdfSkyLayer[] m_candidateLayers = new SdfSkyLayer[SdfSky.MaxLayers];
    private readonly Vector3[] m_map = new Vector3[SdfSkyEnvironment.Texels];
    private readonly Vector3[] m_candidate = new Vector3[SdfSkyEnvironment.CoefficientCount];
    private readonly Vector3[] m_rendered = new Vector3[SdfSkyEnvironment.CoefficientCount];

    private SdfSkyBlock m_candidateBlock;
    private bool m_hasCandidate;
    private bool m_hasRendered;
    private bool m_imageCandidate;
    private bool m_imageChanged;

    /// <summary>Gets whether the last decision projected a candidate.</summary>
    public bool Projected { get; private set; }
    /// <summary>Gets whether the last decision skipped a changed sky below one display code.</summary>
    public bool Skipped { get; private set; }

    /// <summary>Projects a new lighting-visible candidate and decides whether it crosses a display code.</summary>
    /// <param name="block">The packed sky block.</param>
    /// <param name="layers">The packed layer table.</param>
    /// <param name="physical">Whether a physical lighting consumer needs the map independently of artistic gains.</param>
    /// <param name="imageChanged">Whether an image-backed layer's acquired publication or sample mapping changed.</param>
    /// <returns>Whether the environment pass must render.</returns>
    public bool Owes(in SdfSkyBlock block, ReadOnlySpan<SdfSkyLayer> layers, bool physical = false, bool imageChanged = false) {
        Projected = false;
        Skipped = false;
        if (!(physical || (block.Ambient > 0f) || (block.Reflection > 0f) ||
            ((block.FogExtinction > 0f) && ((block.AirFlags & SdfAir.FogColorAuthored) == 0u)) ||
            (block.HazeExtinction > 0f))) {
            return false;
        }
        if (!m_hasCandidate || !SdfSkyEnvironment.SameMap(block: block, layers: layers, otherBlock: m_candidateBlock, otherLayers: m_candidateLayers)) {
            var images = HasImages(block: block, layers: layers);

            if (images != m_imageCandidate) { m_hasRendered = false; }
            m_imageCandidate = images;
            if (images) {
                m_imageChanged = true;
            } else {
                SdfSkyEnvironment.Render(block: block, layers: layers, map: m_map);
                SdfSkyEnvironment.Project(coefficients: m_candidate, map: m_map);
                Projected = true;
            }
            m_candidateBlock = block;
            layers.CopyTo(destination: m_candidateLayers);
            m_hasCandidate = true;
        }
        if (m_imageCandidate) { return (!m_hasRendered || m_imageChanged || imageChanged); }
        if (!m_hasRendered) {
            return true;
        }
        var changed = (SdfSkyEnvironment.IrradianceDifference(coefficients: m_candidate, other: m_rendered) >= SdfSkyEnvironment.DisplayCode);

        Skipped = (Projected && !changed);
        return changed;
    }
    /// <summary>Records that the last candidate's map and coefficients have been submitted for rendering.</summary>
    public void Rendered() {
        if (!m_imageCandidate) { m_candidate.CopyTo(array: m_rendered, index: 0); }
        m_imageChanged = false;
        m_hasRendered = true;
    }
    /// <summary>Invalidates the held projection and rendered sky after a kernel reload.</summary>
    public void Forget() {
        m_hasCandidate = false;
        m_hasRendered = false;
        m_imageChanged = false;
        m_imageCandidate = false;
    }
    /// <summary>Returns whether a lighting-visible panorama needs actual image samples. Such a map cannot use the CPU
    /// reference's irradiance threshold; its source publication and mapping are exact refresh inputs.</summary>
    /// <param name="block">The packed sky block.</param>
    /// <param name="layers">The packed layer table.</param>
    /// <returns>Whether a panorama contributes to the lighting map.</returns>
    public static bool HasImages(in SdfSkyBlock block, ReadOnlySpan<SdfSkyLayer> layers) {
        var count = Math.Min((int)block.LayerCount, layers.Length);

        for (var index = 0; index < count; index++) {
            if (layers[index].Kind == SdfSkyLayerKind.Panorama && layers[index].Opacity > 0f && SdfSkyEnvironment.IsLit(in layers[index])) { return true; }
        }
        return false;
    }
}
