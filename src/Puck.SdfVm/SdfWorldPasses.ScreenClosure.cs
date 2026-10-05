using Puck.Hosting;
using Puck.SignedDistance;
using System.Runtime.InteropServices;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    private readonly HashSet<string> m_observedViews = new(StringComparer.Ordinal);
    private readonly List<ScreenClosure> m_screenClosures = [];
    private ScreenEdge[] m_screenEdges = [];
    private readonly List<ScreenEdge> m_screenCandidates = [];
    private long m_screenEpoch;
    private bool m_restartClosures;

    /// <summary>Retains the current graph's observed world views for finite screen-lighting closure. Only actual
    /// emitting reads between different residencies connect worlds; unrelated and same-world cameras add no edge.</summary>
    /// <param name="instances">The current graph's world image instances, including its demanded nested views.</param>
    public void ObserveViews(IEnumerable<string> instances) {
        m_observedViews.Clear();
        m_observedViews.UnionWith(instances);
    }

    internal long ClosureEpochOf(SdfWorldResidency residency) => ClosureOf(residency)?.Epoch ?? 0;
    internal bool HoldsScreenClosureImage(string instance) {
        foreach (var closure in m_screenClosures) { if (closure.HoldsImage(instance)) { return true; } }
        return false;
    }
    private ScreenClosure? ClosureOf(SdfWorldResidency residency) {
        foreach (var closure in m_screenClosures) { if (closure.Contains(residency)) { return closure; } }
        return null;
    }

    private void RestartClosures() {
        m_restartClosures = true;
        foreach (var closure in m_screenClosures) { closure.Release(); }
    }

    private void AdvanceClosures() {
        if (m_observedViews.Count == 0 && m_screenClosures.Count == 0) { return; }
        var edges = m_screenCandidates;
        edges.Clear();
        foreach (var (residency, registration) in m_environmentNames) {
            if (residency.IsReleased || residency.ScreenSources is not { } sources) { continue; }
            foreach (var screen in sources.Screens) {
                if (!sources.Emits(screen) || sources.ReadOf(registration.View, screen) is not { } producer ||
                    !m_observedViews.Contains(producer) || Refresh(producer).View is not { LightView: false } view ||
                    ReferenceEquals(view.Residency, residency)) { continue; }
                edges.Add(new(residency, screen, view.Residency, producer));
            }
        }
        edges.Sort(static (left, right) => {
            var byWorld = StringComparer.Ordinal.Compare(left.Consumer.Name, right.Consumer.Name);
            if (byWorld != 0) { return byWorld; }
            var byScreen = left.Screen.CompareTo(right.Screen);
            return byScreen != 0 ? byScreen : StringComparer.Ordinal.Compare(left.Instance, right.Instance);
        });
        if (!CollectionsMarshal.AsSpan(edges).SequenceEqual(m_screenEdges)) {
            var current = edges.ToArray();
            foreach (var closure in m_screenClosures) { closure.Release(); }
            m_screenClosures.Clear();
            m_screenEdges = current;
            var remaining = new HashSet<SdfWorldResidency>(current.SelectMany(edge => new[] { edge.Consumer, edge.Producer }),
                ReferenceEqualityComparer.Instance);
            while (remaining.Count > 0) {
                var members = new HashSet<SdfWorldResidency>(ReferenceEqualityComparer.Instance) { remaining.First() };
                var changed = true;
                while (changed) {
                    changed = false;
                    foreach (var edge in current) {
                        if (!members.Contains(edge.Consumer) && !members.Contains(edge.Producer)) { continue; }
                        changed |= members.Add(edge.Consumer);
                        changed |= members.Add(edge.Producer);
                    }
                }
                remaining.ExceptWith(members);
                m_screenClosures.Add(new(this, members.OrderBy(member => member.Name, StringComparer.Ordinal).ToArray(),
                    current.Where(edge => members.Contains(edge.Consumer)).ToArray()));
            }
            m_restartClosures = true;
        }
        foreach (var closure in m_screenClosures) { closure.Advance(m_restartClosures); }
        m_restartClosures = false;
    }

    private void ResetClosureHistory(ScreenClosure closure) {
        foreach (var entry in m_entries.Values) {
            if (entry.View is not { LightView: false } view || !closure.Contains(view.Residency)) { continue; }
            entry.Temporal.Reset();
            entry.TemporalFrame = -1;
            entry.HistoryTainted = false;
            entry.SampleRevision++;
        }
    }

    private readonly record struct ScreenEdge(SdfWorldResidency Consumer, int Screen, SdfWorldResidency Producer, string Instance);

    // A component owns no images or GPU storage. Its immutable iteration boundary is each residency's existing
    // reduced screen table; those records are rewritten only after all consumers have finished the preceding solve.
    private sealed class ScreenClosure {
        private readonly SdfWorldPasses m_owner;
        private readonly ScreenEdge[] m_edges;
        private readonly Dictionary<SdfWorldResidency, SdfViewLightingSource> m_expected = new(ReferenceEqualityComparer.Instance);
        private int m_round;
        private bool m_reducing;
        private bool m_imagesReady;
        private bool m_started;
        public ScreenClosure(SdfWorldPasses owner, SdfWorldResidency[] residencies, ScreenEdge[] edges) {
            m_owner = owner;
            m_edges = edges;
            Members = residencies.Select(residency => new SdfScreenClosureMember(residency,
                edges.Where(edge => ReferenceEquals(edge.Consumer, residency)).Aggregate(0u, (mask, edge) => mask | (1u << edge.Screen)),
                (screen, publication) => Accept(residency, screen, publication))).ToArray();
        }
        public SdfScreenClosureMember[] Members { get; }
        public long Epoch { get; private set; }
        public bool Complete { get; private set; }
        public bool Contains(SdfWorldResidency residency) => Members.Any(member => ReferenceEquals(member.Residency, residency));
        public bool HoldsImage(string instance) => !Complete && m_reducing &&
            m_owner.m_entries.TryGetValue(instance, out var entry) && entry.View is { } view &&
            m_expected.TryGetValue(view.Residency, out var expected) && entry.ImagePublication.IsKnown && entry.ImageLighting == expected;
        private bool CanStart => !Members.Any(member => member.Residency.IndirectFrozen && member.Residency.IndirectTier != SdfIndirectTier.Off);

        public void Release() {
            foreach (var member in Members) {
                member.Residency.ReleaseScreenClosure();
                if (member.Tables is { } tables) { tables.ScreenClosure = null; }
            }
            m_started = false;
            Complete = false;
        }

        public void Advance(bool restart) {
            if (Members.Any(member => !member.Residency.IsReady || member.Residency.Frame is null || member.Residency.Tables is null)) { return; }
            if (restart || !m_started || Members.Any(member => !ReferenceEquals(member.Tables, member.Residency.Tables))) {
                if (!CanStart) { return; }
                Start();
            }
            if (Complete) {
                // Captures retain their frozen source. A tainted independent source becoming filled may still
                // restart it; otherwise an ordinary live change begins the next finite epoch after this one ends.
                var capturing = m_owner.m_convergence is { Request.Completion.IsCompleted: false };
                if (CanStart && Members.Any(member => member.Changed && (!capturing || SourceTainted(member.Tables, member.Residency.Frame)))) { Start(); }
                return;
            }
            if (!m_reducing) {
                if (Members.Any(member => !member.InitialReductionComplete ||
                    (member.Residency.IndirectTier != SdfIndirectTier.Off && !member.Residency.IsIndirectReady))) { return; }
                Epoch = ++m_owner.m_screenEpoch;
                m_expected.Clear();
                foreach (var member in Members) {
                    var cache = member.Tables!.Indirect;
                    m_expected.Add(member.Residency, new(cache?.PublishedLightingSource, cache?.PublishedStamp ?? 0u, Epoch,
                        member.Tables.SubmittedSkyEnvironment?.Publication ?? default, member.Tables.SubmittedScreenEmission?.Publication ?? default));
                    member.HoldsLighting = true;
                }
                m_owner.ResetClosureHistory(this);
                m_reducing = true;
                m_imagesReady = false;
                return;
            }
            if (!m_imagesReady) {
                if (m_edges.Any(edge => !HoldsImage(edge.Instance))) { return; }
                if (m_round == 2) {
                    // The final reduction is an input, not a completed lighting answer. Keep the epoch frozen until
                    // its final solve has rendered every required image and those exact submissions have completed.
                    if (m_edges.Any(edge => m_owner.m_entries[edge.Instance].ImageLightingFence is not { IsSignaled: true })) { return; }
                    Complete = true;
                    foreach (var member in Members) { member.Residency.ReleaseScreenClosure(); }
                    return;
                }
                foreach (var member in Members) { member.Reduce(); }
                m_imagesReady = true;
                return;
            }
            if (Members.Any(member => !member.ReductionComplete)) { return; }
            m_round++;
            Epoch = ++m_owner.m_screenEpoch;
            foreach (var member in Members) {
                member.HoldsLighting = false;
                member.Residency.ResetScreenClosureLighting();
            }
            m_owner.ResetClosureHistory(this);
            m_reducing = false;
        }

        private void Start() {
            Epoch = ++m_owner.m_screenEpoch;
            m_round = 0;
            m_reducing = false;
            Complete = false;
            m_started = true;
            foreach (var member in Members) {
                member.Residency.FreezeScreenClosure();
                member.Start();
                member.Residency.ResetScreenClosureLighting();
            }
            m_owner.ResetClosureHistory(this);
        }

        private bool Accept(SdfWorldResidency consumer, int screen, GpuImagePublication publication) {
            foreach (var edge in m_edges) {
                if (edge.Screen == screen && ReferenceEquals(edge.Consumer, consumer)) {
                    return m_expected.TryGetValue(edge.Producer, out var expected) &&
                        m_owner.ImageHasLighting(edge.Instance, publication, expected);
                }
            }
            return false;
        }
    }
}

// One small policy record attached to the existing reduction, not a second source cache. The GPU buffers and
// submitted publications remain tables-owned. Independent records are written once, derived records twice.
internal sealed class SdfScreenClosureMember(SdfWorldResidency residency, uint derivedMask, Func<int, GpuImagePublication, bool> accepts) {
    private long m_reductionBefore;
    private SdfViewSnapshot[] m_views = [];
    private (ulong Program, ulong Geometry, ulong Lighting, ulong Shadow, ulong Visual) m_scene;
    public SdfWorldResidency Residency { get; } = residency;
    public SdfWorldTables? Tables { get; private set; }
    public uint DerivedMask { get; } = derivedMask;
    public uint WriteMask { get; private set; }
    public bool ZeroDerived { get; private set; }
    public bool HoldsLighting { get; set; }
    public bool InitialReductionComplete => Tables?.CompletedScreenEmission.IsKnown == true;
    public bool ReductionComplete => DerivedMask == 0 || (Tables?.SubmittedScreenEmission is { } submitted &&
        submitted.Sequence > m_reductionBefore && Tables.CompletedScreenEmission == submitted.Publication);
    public bool Changed => Tables is { } tables && (tables.ScreenClosureInputsChanged || tables.SkyClosureInputsChanged ||
        tables.ClosureSceneSignature != m_scene || CamerasChanged() ||
        (tables.Indirect?.Lighting is { } lighting && !lighting.MatchesScene(Residency.Frame!)));
    public bool Accepts(int screen, GpuImagePublication publication) => accepts(screen, publication);

    private bool CamerasChanged() {
        var views = Residency.Frame!.Views;
        if (m_views.Length != views.Count) { return true; }
        for (var index = 0; index < m_views.Length; index++) {
            var held = m_views[index];
            var current = views[index];
            if (held.Camera != current.Camera || held.CutRevision != current.CutRevision || held.Quality != current.Quality ||
                held.Region != current.Region || held.RenderScale != current.RenderScale || held.ResolvedRenderScale != current.ResolvedRenderScale) { return true; }
        }
        return false;
    }

    public void Start() {
        if (Tables is { } previous && !ReferenceEquals(previous, Residency.Tables)) { previous.ScreenClosure = null; }
        Tables = Residency.Tables!;
        Tables.ScreenClosure = this;
        m_views = Residency.Frame!.Views.ToArray();
        m_scene = Tables.ClosureSceneSignature;
        WriteMask = uint.MaxValue;
        ZeroDerived = true;
        HoldsLighting = false;
        Tables.RestartScreenClosureSources();
    }
    public void Reduce() {
        m_reductionBefore = Tables!.SubmittedScreenEmission?.Sequence ?? 0;
        WriteMask = DerivedMask;
        ZeroDerived = false;
        HoldsLighting = true;
    }
    public void Published() => WriteMask = 0;
}
