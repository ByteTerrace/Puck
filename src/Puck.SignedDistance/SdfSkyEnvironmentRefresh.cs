using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>Decides when a residency's environment needs rendering. Candidates are compared to the last rendered
/// coefficients, so skipped changes accumulate. A camera-only edit never projects the sky. A sky that drifts with the
/// same lit layers (a turning day) projects a new candidate at most once every <see cref="DriftFrames"/> frames, and any
/// other change projects at once. Image-backed candidates follow exact acquired publication changes instead: the CPU
/// reference does not sample device panoramas.</summary>
public sealed class SdfSkyEnvironmentRefresh {
    /// <summary>The frames (<see cref="Advance"/>) a drifting sky's candidate stands before the drift is projected: the
    /// lighting a sky gives moves by less than a display code over a few frames of a day's turn, which the projection
    /// would only find again.</summary>
    public const int DriftFrames = 8;
    private readonly SdfSkyLayer[] m_candidateLayers = new SdfSkyLayer[SdfSky.MaxLayers];
    private readonly Vector3[] m_map = new Vector3[SdfSkyEnvironment.Texels];
    private readonly Vector3[] m_candidate = new Vector3[SdfSkyEnvironment.CoefficientCount];
    private readonly Vector3[] m_rendered = new Vector3[SdfSkyEnvironment.CoefficientCount];

    private SdfSkyBlock m_candidateBlock;
    private bool m_hasCandidate;
    private bool m_hasRendered;
    private bool m_imageCandidate;
    private bool m_imageChanged;
    private int m_sinceProjection;

    /// <summary>Gets whether the last decision projected a candidate.</summary>
    public bool Projected { get; private set; }
    /// <summary>Gets whether the last decision skipped a changed sky below one display code.</summary>
    public bool Skipped { get; private set; }
    /// <summary>Gets whether the last decision held a drifting sky's standing candidate (<see cref="DriftFrames"/>).</summary>
    public bool Held { get; private set; }

    /// <summary>Counts one frame of the candidate's age, once a frame before the frame's first decision.</summary>
    public void Advance() => m_sinceProjection++;

    /// <summary>Projects a new lighting-visible candidate and decides whether it crosses a display code.</summary>
    /// <param name="block">The packed sky block.</param>
    /// <param name="layers">The packed layer table.</param>
    /// <param name="physical">Whether a physical lighting consumer needs the map independently of artistic gains.</param>
    /// <param name="imageChanged">Whether an image-backed layer's acquired publication or sample mapping changed.</param>
    /// <returns>Whether the environment pass must render.</returns>
    public bool Owes(in SdfSkyBlock block, ReadOnlySpan<SdfSkyLayer> layers, bool physical = false, bool imageChanged = false) {
        Projected = false;
        Skipped = false;
        Held = false;
        if (!(physical || (block.Ambient > 0f) || (block.Reflection > 0f) ||
            ((block.FogExtinction > 0f) && ((block.AirFlags & SdfAir.FogColorAuthored) == 0u)) ||
            (block.HazeExtinction > 0f))) {
            return false;
        }
        if (!m_hasCandidate || !SdfSkyEnvironment.SameMap(block: block, layers: layers, otherBlock: m_candidateBlock, otherLayers: m_candidateLayers)) {
            var images = HasImages(block: block, layers: layers);

            // A drift of a projected sky whose lit layers keep their kinds is judged by the standing candidate for its
            // frames; the latest sky is projected once they pass, so no drift is lost, only gathered.
            Held = (m_hasCandidate && !images && !m_imageCandidate && (m_sinceProjection < DriftFrames) &&
                SdfSkyEnvironment.SameLayerKinds(block: block, layers: layers, otherBlock: m_candidateBlock, otherLayers: m_candidateLayers));
            if (!Held) {
                if (images != m_imageCandidate) { m_hasRendered = false; }
                m_imageCandidate = images;
                if (images) {
                    m_imageChanged = true;
                } else {
                    SdfSkyEnvironment.Render(block: block, layers: layers, map: m_map);
                    SdfSkyEnvironment.Project(coefficients: m_candidate, map: m_map);
                    Projected = true;
                    m_sinceProjection = 0;
                }
                m_candidateBlock = block;
                layers.CopyTo(destination: m_candidateLayers);
                m_hasCandidate = true;
            }
        }
        if (m_imageCandidate) { return (!m_hasRendered || m_imageChanged || imageChanged); }
        if (!m_hasRendered) {
            return true;
        }
        // A display code of the sky's own brightness: the irradiance the rendered sky gives an upward surface shown as
        // display white, or the absolute code for a sky dimmer than that unit.
        var white = Math.Max(val1: 1d, val2: MathF.Max(x: Brightest(irradiance: SdfSkyEnvironment.Irradiance(coefficients: m_rendered, normal: Vector3.UnitY)), y: Brightest(irradiance: SdfSkyEnvironment.Irradiance(coefficients: m_rendered, normal: -Vector3.UnitY))));
        var changed = (SdfSkyEnvironment.IrradianceDifference(coefficients: m_candidate, other: m_rendered) >= (SdfSkyEnvironment.DisplayCode * white));

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
        var count = Math.Min(val1: ((int)block.LayerCount), val2: layers.Length);

        for (var index = 0; (index < count); index++) {
            if ((layers[index].Kind == SdfSkyLayerKind.Panorama) && (layers[index].Opacity > 0f) && SdfSkyEnvironment.IsLit(layer: in layers[index])) { return true; }
        }
        return false;
    }

    private static float Brightest(Vector3 irradiance) => MathF.Max(x: irradiance.X, y: MathF.Max(x: irradiance.Y, y: irradiance.Z));
}
