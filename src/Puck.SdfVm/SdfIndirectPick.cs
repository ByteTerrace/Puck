using System.Numerics;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>The actual outcome of the selected pixel's indirect lookup.</summary>
public enum SdfIndirectPickStatus {
    /// <summary>The view has no active indirect cache.</summary>
    Off,
    /// <summary>No complete lighting generation is available.</summary>
    Unpublished,
    /// <summary>The receiver could not reach a lit component through a certified launch and proof.</summary>
    Unresolved,
    /// <summary>The receiver read a published lighting generation.</summary>
    Resolved,
    /// <summary>The shared frame allowance admitted no new proof for this receiver.</summary>
    Deferred,
    /// <summary>The selected pixel did not perform diffuse shading, for example a miss, screen or geometric debug view.</summary>
    NotShaded,
}

/// <summary>One actual corner considered by the selected GPU lookup.</summary>
/// <param name="Index">The physical probe index, or minus one when the corner has no allocation.</param>
/// <param name="Classification">The fenced probe state.</param>
/// <param name="Weight">The normalized weight actually used by the receiver; zero for excluded corners.</param>
/// <param name="Publication">The probe's stamp in the read lighting generation.</param>
public readonly record struct SdfIndirectPickCorner(int Index, IrradianceProbeClass Classification, float Weight, uint Publication);

/// <summary>A fenced census of the snapshot's allocated probes at its exact geometry epoch.</summary>
/// <param name="Active">Active probes.</param>
/// <param name="Relocated">Probes moved within the allowed relocation distance.</param>
/// <param name="Inactive">Classified probes with no certified free-space placement.</param>
/// <param name="Dormant">Classified probes that have no lit surface to trace.</param>
/// <param name="Unpublished">Allocated probes whose state has not been written at this epoch.</param>
public readonly record struct SdfIndirectCensus(int Active, int Relocated, int Inactive, int Dormant, int Unpublished);

/// <summary>The selected receiver's independently accumulated incident linear RGB contributions. Cache reads decode
/// the actual quantized lighting generations; an answered Near ray or alternative reports its replacement sources.
/// These categories are never inferred from the final shaded color.</summary>
/// <param name="Direct">Direct-light contributions at sampled hits.</param>
/// <param name="Feedback">Contributions from preceding complete feedback sweeps.</param>
/// <param name="Emission">Material emission at sampled hits.</param>
/// <param name="Sky">Sky exit contributions.</param>
/// <param name="Screens">Screen and portal contributions.</param>
public readonly record struct SdfIndirectPickSources(Vector3 Direct, Vector3 Feedback, Vector3 Emission, Vector3 Sky, Vector3 Screens);

/// <summary>A selected receiver answer copied under the same fence as its visibility and cache census. The immutable
/// source is the solve that produced the cache fallback generation. An answered Near ray retains that same exact
/// current source and its sampled direction; a later live frame cannot relabel either answer.
/// Alternative output uses its rendered frame and cannot use this cache source as a CPU reference.</summary>
/// <param name="Status">The GPU lookup outcome.</param>
/// <param name="Tier">The tier the selected view used.</param>
/// <param name="Level">The selected lattice level, or minus one without a readable component.</param>
/// <param name="ProofMask">The exact certified corner mask.</param>
/// <param name="Position">The GPU surface point.</param>
/// <param name="Launched">The certified receiver launch.</param>
/// <param name="Clearance">The launch's certified clearance.</param>
/// <param name="Normal">The GPU normal used by the irradiance lookup.</param>
/// <param name="Generation">The read lighting bank.</param>
/// <param name="Publication">The exact per-probe stamp required by the lookup.</param>
/// <param name="Corners">The eight actual corner records in lattice order.</param>
/// <param name="Sources">The five independent incident contributions before material response.</param>
/// <param name="Cache">The CPU inventory captured with the request; null while indirect is off.</param>
/// <param name="Census">The fenced classification census; null without an active cache.</param>
/// <param name="LightingSource">The source that produced the cache publication, not an alternative's current-frame
/// replacement; null before a complete solve.</param>
public sealed record SdfIndirectPick(SdfIndirectPickStatus Status, SdfIndirectTier Tier, int Level, uint ProofMask,
    Vector3 Position, Vector3 Launched, float Clearance, Vector3 Normal, uint Generation, uint Publication,
    IReadOnlyList<SdfIndirectPickCorner> Corners, SdfIndirectPickSources Sources, SdfIndirectCacheSnapshot? Cache,
    SdfIndirectCensus? Census, SdfIndirectLightingSnapshot? LightingSource) {
    /// <summary>Gets the selected presentation algorithm. Corners describe its cache fallback; Sources describes
    /// the actual result after any alternative replacement.</summary>
    public SdfIndirectMethod Method { get; init; }
    /// <summary>Gets the actual bounded Near replacement outcome. Corners still describe the cache fallback;
    /// Sources contains the incoming Near answer only when this outcome is Hit or Continuation.</summary>
    public SdfIndirectNearOutcome Near { get; init; }
    /// <summary>Gets the actual selected Near direction, copied under this pick's fence; zero without an attempt.</summary>
    public Vector3 NearDirection { get; init; }
    /// <summary>Gets the exact same-source predecessor stamp available to Near feedback. It is zero when Near is
    /// not admitted for the view or no preceding bank exists; an unselected pixel does not consume that bank.</summary>
    public uint NearPreviousPublication { get; init; }
    /// <summary>Gets Near's immutable current source only when its captured tier, method, bank and predecessor
    /// identities agree with the fenced record. A later live frame cannot supply or relabel this source.</summary>
    public SdfIndirectLightingSnapshot? NearSource { get; init; }
    /// <summary>Gets the source categories enabled by the rendered receiver, independently of the earlier solve's
    /// source mask. A CPU reference preserves that solve's recursive transport, then masks its final categories
    /// with this captured receiver selection.</summary>
    public SdfIndirectSources SourcesEnabled { get; init; }
    /// <summary>Gets the receiver-only tint, intensity and contact controls copied from the same submitted block.
    /// Sources and the independent reference remain incoming radiance before these controls or material response.</summary>
    public SdfIndirectApplication Application { get; init; } = SdfIndirectApplication.Default;
}
