using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Formats the captured indirect answer shared by the explanation echo and inspector. A retained immutable
/// answer is shaped once; reading that answer again allocates nothing.</summary>
public sealed class WorldIndirectPickText {
    private SdfIndirectPick? m_pick;
    private WorldIndirectReferenceResult? m_reference;
    private bool m_initialized;
    private string m_text = "indirect unavailable: no fenced pixel";

    /// <summary>Returns the actual fenced receiver, corner, source and cache facts, or their named absence.</summary>
    /// <param name="pick">The immutable answer belonging to the completed pixel, never a later live cache.</param>
    /// <param name="reference">The one explicit evaluation of this exact answer, or null before explanation.</param>
    /// <returns>The shared rows. The inspector applies its existing fixed line reservation to them.</returns>
    public string Read(SdfIndirectPick? pick, WorldIndirectReferenceResult? reference = null) {
        if (m_initialized && ReferenceEquals(m_pick, pick) && ReferenceEquals(m_reference, reference)) { return m_text; }
        m_initialized = true;
        m_pick = pick;
        m_reference = reference;
        m_text = Describe(pick, reference);
        return m_text;
    }

    private static string Describe(SdfIndirectPick? pick, WorldIndirectReferenceResult? reference) {
        if (pick is null) { return "indirect unavailable: no fenced pixel"; }
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"indirect method={pick.Method.ToString().ToLowerInvariant()} status={pick.Status.ToString().ToLowerInvariant()} near={pick.Near.ToString().ToLowerInvariant()} tier={pick.Tier.ToString().ToLowerInvariant()} level={pick.Level} component=0x{pick.ProofMask:x2}\n");
        text.Append(CultureInfo.InvariantCulture,
            $"receiver={Vector(pick.Position)} normal={Vector(pick.Normal)}\nlaunch={Vector(pick.Launched)} clearance={pick.Clearance:0.######}\n");
        text.Append(CultureInfo.InvariantCulture,
            $"read-generation={pick.Generation} read-stamp={pick.Publication} sources=0x{(uint)pick.SourcesEnabled:x2}\n");
        if (pick.Cache is { } cache) {
            text.Append(CultureInfo.InvariantCulture,
                $"cache allocation={cache.Allocation} epoch={cache.Epoch} submission={cache.Submission}\n");
            text.Append(CultureInfo.InvariantCulture,
                $"published-generation={cache.PublishedGeneration} published-stamp={cache.PublishedStamp} published-sweeps={cache.PublishedSweeps}\n");
        } else {
            text.Append("cache unavailable: no captured allocation\n");
        }
        text.Append(DescribeCensus(pick)).Append('\n');
        for (var index = 0; index < pick.Corners.Count; index++) {
            var corner = pick.Corners[index];
            text.Append(CultureInfo.InvariantCulture,
                $"c{index}={corner.Index}/{corner.Classification},w={corner.Weight:0.######},p={corner.Publication}");
            text.Append(((index & 1) == 0) && ((index + 1) < pick.Corners.Count) ? " | " : "\n");
        }
        var sources = pick.Sources;
        text.Append(CultureInfo.InvariantCulture,
            $"apply intensity={pick.Application.Intensity:0.######} tint={Vector(pick.Application.Tint)} contact={pick.Application.Contact:0.######}; sources-before-apply\n");
        if (pick.LightingSource is { } source) {
            var gains = source.Gains;
            text.Append(CultureInfo.InvariantCulture,
                $"source-gains lights={gains.Lights:0.######} emission={gains.Emission:0.######} screens={gains.Screens:0.######} sky={gains.Sky:0.######} feedback={gains.Feedback:0.######}\n");
        }
        text.Append(CultureInfo.InvariantCulture,
            $"sources direct={Vector(sources.Direct)} emission={Vector(sources.Emission)}\nsources sky={Vector(sources.Sky)} screens={Vector(sources.Screens)}\nsources feedback={Vector(sources.Feedback)}\n");
        text.Append(CultureInfo.InvariantCulture,
            $"source-sequence={(pick.LightingSource?.Sequence ?? 0)} source-role={(pick.Near is SdfIndirectNearOutcome.Hit or SdfIndirectNearOutcome.Continuation ? (pick.NearSource is null ? "cache-fallback-not-near-reference" : "current-near-publication") : pick.Method == SdfIndirectMethod.Cache ? "visible-cache-publication" : "cache-fallback-not-alternative-reference")}\n");
        if (pick.Near is SdfIndirectNearOutcome.Hit or SdfIndirectNearOutcome.Continuation) {
            text.Append(CultureInfo.InvariantCulture,
                $"near-direction={Vector(pick.NearDirection)} previous-stamp={pick.NearPreviousPublication} incoming-source={(pick.NearSource?.Sequence ?? 0)}\n");
        }
        if (reference is null) {
            text.Append("cpu-reference unavailable: run world.explain for this fenced pixel\n");
        } else {
            if (reference.Estimate is { } estimate) {
                var rgb = estimate.Irradiance;
                text.Append(CultureInfo.InvariantCulture,
                    $"cpu-reference={rgb.X:0.######},{rgb.Y:0.######},{rgb.Z:0.######} paths={estimate.Paths} unresolved={estimate.Unresolved}\n");
            }
            text.Append(CultureInfo.InvariantCulture,
                $"reference source={reference.SourceSequence} bounces={reference.FeedbackBounces} queries={reference.FieldQueries} casts={reference.Casts}\n");
            if (reference.Refusal is { } refusal) { text.Append("reference unavailable: ").Append(refusal).Append('\n'); }
            text.Append(reference.Difference is { } difference
                ? "gpu-minus-reference=" + Vector(difference) + "\n"
                : "gpu-minus-reference unavailable\n");
        }
        text.Append("fields=prototypes[].palette[].fill,bleed,receive; render.lighting[].bounce\nfields=render.indirect.sources,bounces,apply; render.environment; render.sky.layers; screens[]; placements[].faceSources");
        return text.ToString();
    }

    /// <summary>Formats the exact fenced census, qualifying it with the captured cache identity and epoch.</summary>
    /// <param name="pick">The receiver answer retaining this census and its source.</param>
    /// <returns>The five actual classification counts, or a named unavailable result.</returns>
    public static string DescribeCensus(SdfIndirectPick pick) {
        ArgumentNullException.ThrowIfNull(pick);
        return pick.Census is { } census
            ? string.Create(CultureInfo.InvariantCulture,
                $"census active={census.Active} relocated={census.Relocated} inactive={census.Inactive} dormant={census.Dormant} unpublished={census.Unpublished} epoch={(pick.Cache?.Epoch ?? 0)}")
            : "census unavailable: no fenced probe classifications";
    }

    private static string Vector(Vector3 value) => string.Create(CultureInfo.InvariantCulture,
        $"{value.X:0.######},{value.Y:0.######},{value.Z:0.######}");
}
