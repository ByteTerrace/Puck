using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using System.Runtime.InteropServices;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    private readonly HashSet<string> m_observedViews = new(comparer: StringComparer.Ordinal);
    private readonly List<ScreenClosure> m_screenClosures = [];
    private ScreenEdge[] m_screenEdges = [];
    private readonly List<ScreenEdge> m_screenCandidates = [];

    private long m_screenEpoch;
    private bool m_restartClosures;

    /// <summary>Retains the current graph's observed world views for finite screen-lighting closure. Only actual
    /// emitting or lighting-visible panorama reads between different residencies connect worlds; unrelated and
    /// same-world cameras add no edge.</summary>
    /// <param name="instances">The current graph's world image instances, including its demanded nested views.</param>
    public void ObserveViews(IEnumerable<string> instances) {
        m_observedViews.Clear();
        m_observedViews.UnionWith(other: instances);
    }

    internal long ClosureEpochOf(SdfWorldResidency residency) => (ClosureOf(residency: residency)?.Epoch ?? 0);

    /// <inheritdoc/>
    public RenderGraphReadEpoch? ReadEpochOf(string instance, string producer) =>
        ((Refresh(instance: instance).View is { LightView: false } view) ? ReadEpochOf(view.Residency, producer) : null);

    internal RenderGraphReadEpoch? ReadEpochOf(SdfWorldResidency residency, string producer) {
        // Own-world cameras are excluded from lighting feedback. A cold self-read must not wait on the image whose
        // first render it would prevent; its existing visual feedback edge keeps its ordinary previous-frame policy.
        if (m_entries.TryGetValue(key: producer, value: out var entry) && (entry.View is { } own) && ReferenceEquals(objA: own.Residency, objB: residency)) { return null; }
        return ClosureOf(residency: residency)?.ReadEpochOf(producer: producer);
    }
    internal bool HoldsScreenClosureImage(string instance) {
        foreach (var closure in m_screenClosures) { if (closure.HoldsImage(instance: instance)) { return true; } }
        return false;
    }

    private ScreenClosure? ClosureOf(SdfWorldResidency residency) {
        foreach (var closure in m_screenClosures) { if (closure.Contains(residency: residency)) { return closure; } }
        return null;
    }
    private void RestartClosures() {
        m_restartClosures = true;
        foreach (var closure in m_screenClosures) { closure.Release(); }
    }
    private void AdvanceClosures() {
        if ((m_observedViews.Count == 0) && (m_screenClosures.Count == 0)) { return; }
        var edges = m_screenCandidates;

        edges.Clear();
        foreach (var (residency, registration) in m_environmentNames) {
            if (residency.IsReleased || (residency.ScreenSources is not { } sources)) { continue; }
            foreach (var screen in sources.Screens) {
                var emits = sources.Emits(screen: screen);
                var panorama = ((residency.Tables is { SkyEnvironmentDemand: true } tables) && tables.SkyEnvironmentUsesScreen(screen: screen));

                if ((!emits && !panorama) || (sources.ReadOf(screen: screen, view: registration.View) is not { } producer) ||
                    !m_observedViews.Contains(item: producer) || (Refresh(instance: producer).View is not { LightView: false } view) ||
                    ReferenceEquals(objA: view.Residency, objB: residency)) { continue; }
                edges.Add(item: new(residency, screen, view.Residency, producer, emits, panorama));
            }
        }
        edges.Sort(comparison: static (left, right) => {
            var byWorld = StringComparer.Ordinal.Compare(x: left.Consumer.Name, y: right.Consumer.Name);

            if (byWorld != 0) { return byWorld; }
            var byScreen = left.Screen.CompareTo(value: right.Screen);

            return ((byScreen != 0) ? byScreen : StringComparer.Ordinal.Compare(x: left.Instance, y: right.Instance));
        });
        if (!CollectionsMarshal.AsSpan(list: edges).SequenceEqual(other: m_screenEdges)) {
            var current = edges.ToArray();

            foreach (var closure in m_screenClosures) { closure.Release(); }
            m_screenClosures.Clear();
            m_screenEdges = current;
            var remaining = new HashSet<SdfWorldResidency>(collection: current.SelectMany(selector: edge => new[] { edge.Consumer, edge.Producer }),
                comparer: ReferenceEqualityComparer.Instance);

            while (remaining.Count > 0) {
                var members = new HashSet<SdfWorldResidency>(comparer: ReferenceEqualityComparer.Instance) { remaining.First() };
                var changed = true;

                while (changed) {
                    changed = false;
                    foreach (var edge in current) {
                        if (!members.Contains(item: edge.Consumer) && !members.Contains(item: edge.Producer)) { continue; }
                        changed |= members.Add(item: edge.Consumer);
                        changed |= members.Add(item: edge.Producer);
                    }
                }
                remaining.ExceptWith(other: members);
                m_screenClosures.Add(item: new(this, members.OrderBy(member => member.Name, StringComparer.Ordinal).ToArray(),
                    current.Where(predicate: edge => members.Contains(item: edge.Consumer)).ToArray()));
            }
            m_restartClosures = true;
        }
        foreach (var closure in m_screenClosures) { closure.Advance(restart: m_restartClosures); }
        m_restartClosures = false;
    }
    private void ResetClosureHistory(ScreenClosure closure) {
        foreach (var entry in m_entries.Values) {
            if ((entry.View is not { LightView: false } view) || !closure.Contains(residency: view.Residency)) { continue; }
            entry.Temporal.Reset();
            entry.TemporalFrame = -1;
            entry.HistoryTainted = false;
            entry.SampleRevision++;
        }
    }

    private readonly record struct ScreenEdge(SdfWorldResidency Consumer, int Screen, SdfWorldResidency Producer,
        string Instance, bool Emits, bool Panorama);
    // The graph owns independent image copies for the component's epoch. Its derived iteration boundary is each
    // residency's existing reduced screen and environment tables, rewritten after the preceding solve's images finish.
    private sealed class ScreenClosure {
        private readonly SdfWorldPasses m_owner;
        private readonly ScreenEdge[] m_edges;
        private readonly Dictionary<SdfWorldResidency, SdfViewLightingSource> m_expected = new(comparer: ReferenceEqualityComparer.Instance);

        private int m_round;
        private bool m_reducing;
        private bool m_imagesReady;
        private bool m_started;
        private RenderGraphReadEpoch? m_reads;

        public ScreenClosure(SdfWorldPasses owner, SdfWorldResidency[] residencies, ScreenEdge[] edges) {
            m_owner = owner;
            m_edges = edges;
            Members = residencies.Select(selector: residency => new SdfScreenClosureMember(residency,
                edges.Where(predicate: edge => (edge.Emits && ReferenceEquals(objA: edge.Consumer, objB: residency))).Aggregate(0u, (mask, edge) => mask | (1u << edge.Screen)),
                edges.Where(predicate: edge => (edge.Panorama && ReferenceEquals(objA: edge.Consumer, objB: residency))).Aggregate(0u, (mask, edge) => mask | (1u << edge.Screen)),
                (screen, publication) => Accept(consumer: residency, publication: publication, screen: screen))).ToArray();
        }

        public bool Complete { get; private set; }
        public long Epoch { get; private set; }
        public SdfScreenClosureMember[] Members { get; }

        public bool Contains(SdfWorldResidency residency) => Members.Any(predicate: member => ReferenceEquals(objA: member.Residency, objB: residency));
        public RenderGraphReadEpoch? ReadEpochOf(string producer) => (((m_reads is { IsActive: true }) &&
            !m_edges.Any(predicate: edge => (edge.Instance == producer))) ? m_reads : null);
        public bool HoldsImage(string instance) => (!Complete && m_reducing &&
            m_owner.m_entries.TryGetValue(key: instance, value: out var entry) && (entry.View is { } view) &&
            m_expected.TryGetValue(key: view.Residency, value: out var expected) && entry.ImagePublication.IsKnown && (entry.ImageLighting == expected));

        private bool CanStart => !Members.Any(predicate: member => (member.Residency.IndirectFrozen && (member.Residency.IndirectTier != SdfIndirectTier.Off)));

        public void Release() {
            m_reads?.Dispose();
            foreach (var member in Members) {
                member.Residency.ReleaseScreenClosure();
                if (member.Tables is { } tables) { tables.ScreenClosure = null; }
            }
            m_started = false;
            Complete = false;
        }
        public void Advance(bool restart) {
            if (Members.Any(predicate: member => (!member.Residency.IsReady || (member.Residency.Frame is null) || (member.Residency.Tables is null)))) { return; }
            if (restart || !m_started || (m_reads is { IsActive: true, IsValid: false }) || Members.Any(predicate: member => !ReferenceEquals(objA: member.Tables, objB: member.Residency.Tables))) {
                if (!CanStart) { return; }
                Start();
            }
            if (Complete) {
                // Captures retain their frozen source. A tainted independent source becoming filled may still
                // restart it; otherwise an ordinary live change begins the next finite epoch after this one ends.
                var capturing = (m_owner.m_convergence is { Request.Completion.IsCompleted: false });

                if (!capturing) { m_reads?.Dispose(); }
                if (CanStart && Members.Any(predicate: member => ((member.Changed || (m_reads?.Changed == true)) &&
                    (!capturing || SourceTainted(member.Tables, member.Residency.Frame))))) { Start(); }
                return;
            }
            if (!m_reducing) {
                if (Members.Any(predicate: member => (!member.InitialReductionComplete ||
                    ((member.Residency.IndirectTier != SdfIndirectTier.Off) && !member.Residency.IsIndirectReady)))) { return; }
                Epoch = ++m_owner.m_screenEpoch;
                m_expected.Clear();
                foreach (var member in Members) {
                    var cache = member.Tables!.Indirect;

                    m_expected.Add(key: member.Residency, value: new(cache?.PublishedLightingSource, (cache?.PublishedStamp ?? 0u), Epoch,
                        (member.Tables.SubmittedSkyEnvironment?.Publication ?? default), (member.Tables.SubmittedScreenEmission?.Publication ?? default)));
                    member.HoldsLighting = true;
                }
                m_owner.ResetClosureHistory(closure: this);
                m_reducing = true;
                m_imagesReady = false;
                return;
            }
            if (!m_imagesReady) {
                if (m_edges.Any(predicate: edge => !HoldsImage(instance: edge.Instance))) { return; }
                if (m_round == 2) {
                    // The final reduction is an input, not a completed lighting answer. Keep the epoch frozen until
                    // its final solve has rendered every required image and those exact submissions have completed.
                    if (m_edges.Any(predicate: edge => (m_owner.m_entries[edge.Instance].ImageLightingFence is not { IsSignaled: true }))) { return; }
                    Complete = true;
                    foreach (var member in Members) { member.Residency.ReleaseScreenClosure(); }
                    return;
                }
                foreach (var member in Members) { member.Reduce(); }
                m_imagesReady = true;
                return;
            }
            if (Members.Any(predicate: member => !member.ReductionComplete)) { return; }
            m_round++;
            Epoch = ++m_owner.m_screenEpoch;
            foreach (var member in Members) {
                member.HoldsLighting = false;
                member.Residency.ResetScreenClosureLighting();
            }
            m_owner.ResetClosureHistory(closure: this);
            m_reducing = false;
        }

        private void Start() {
            m_reads?.Dispose();
            m_reads = new RenderGraphReadEpoch();
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
            m_owner.ResetClosureHistory(closure: this);
        }
        private bool Accept(SdfWorldResidency consumer, int screen, GpuImagePublication publication) {
            foreach (var edge in m_edges) {
                if ((edge.Screen == screen) && ReferenceEquals(objA: edge.Consumer, objB: consumer)) {
                    return (m_expected.TryGetValue(key: edge.Producer, value: out var expected) &&
                        m_owner.ImageHasLighting(instance: edge.Instance, publication: publication, source: expected));
                }
            }
            return false;
        }
    }
}

// One small policy record attached to the existing reduction, not a second source cache. The GPU buffers and
// submitted publications remain tables-owned. Independent records are written once, derived records twice.
internal sealed class SdfScreenClosureMember(SdfWorldResidency residency, uint derivedMask, uint derivedSkyMask,
    Func<int, GpuImagePublication, bool> accepts) {
    private long m_reductionBefore;
    private long m_projectionBefore;

    private SdfViewSnapshot[] m_views = [];

    private (ulong Program, ulong Geometry, ulong Lighting, ulong Shadow, ulong Visual) m_scene;

    public SdfWorldResidency Residency { get; } = residency;

    public SdfWorldTables? Tables { get; private set; }

    public uint DerivedMask { get; } = derivedMask;
    public uint DerivedSkyMask { get; } = derivedSkyMask;

    public bool Changed => ((Tables is { } tables) && (tables.ScreenClosureInputsChanged || tables.SkyClosureInputsChanged ||
        (tables.ClosureSceneSignature != m_scene) || CamerasChanged() ||
        ((tables.Indirect?.Lighting is { } lighting) && !lighting.MatchesScene(frame: Residency.Frame!))));
    public bool HoldsLighting { get; set; }
    public bool InitialReductionComplete => ((Tables is { } tables) && tables.CompletedScreenEmission.IsKnown &&
        (!tables.SkyEnvironmentDemand || tables.CompletedSkyEnvironment.IsKnown));
    public bool ProjectsSky { get; private set; }
    public bool ReductionComplete =>
        (((DerivedMask == 0) || ((Tables?.SubmittedScreenEmission is { } submitted) &&
            (submitted.Sequence > m_reductionBefore) && (Tables.CompletedScreenEmission == submitted.Publication))) &&
        ((DerivedSkyMask == 0) || ((Tables?.SubmittedSkyEnvironment is { } projected) &&
            (projected.Sequence > m_projectionBefore) && (Tables.CompletedSkyEnvironment == projected.Publication))));
    public uint WriteMask { get; private set; }
    public bool ZeroDerived { get; private set; }

    public bool Accepts(int screen, GpuImagePublication publication) => accepts(screen, publication);

    private bool CamerasChanged() {
        var views = Residency.Frame!.Views;

        if (m_views.Length != views.Count) { return true; }
        for (var index = 0; (index < m_views.Length); index++) {
            var held = m_views[index];
            var current = views[index];

            if ((held.Camera != current.Camera) || (held.CutRevision != current.CutRevision) || (held.Quality != current.Quality) ||
                (held.Region != current.Region) || (held.RenderScale != current.RenderScale) || (held.ResolvedRenderScale != current.ResolvedRenderScale)) { return true; }
        }
        return false;
    }

    public void Start() {
        if ((Tables is { } previous) && !ReferenceEquals(objA: previous, objB: Residency.Tables)) { previous.ScreenClosure = null; }
        Tables = Residency.Tables!;
        Tables.ScreenClosure = this;
        m_views = Residency.Frame!.Views.ToArray();
        m_scene = Tables.ClosureSceneSignature;
        WriteMask = uint.MaxValue;
        ZeroDerived = true;
        ProjectsSky = true;
        HoldsLighting = false;
        Tables.RestartScreenClosureSources();
    }
    public void Reduce() {
        m_reductionBefore = (Tables!.SubmittedScreenEmission?.Sequence ?? 0);
        m_projectionBefore = (Tables.SubmittedSkyEnvironment?.Sequence ?? 0);
        WriteMask = DerivedMask;
        ProjectsSky = (DerivedSkyMask != 0);
        ZeroDerived = false;
        HoldsLighting = true;
    }
    public void Published() => WriteMask = 0;
    public void Projected() => ProjectsSky = false;
}
