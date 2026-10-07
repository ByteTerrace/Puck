using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The single environment producer for each residency. Its acquired images precede its sky projection and
/// screen reduction, and every lighting consumer reads the same exported dependencies.</summary>
/// <param name="views">The existing view factory that starts each shared residency once per frame.</param>
public sealed class SdfSkyEnvironmentPasses(SdfWorldPasses views) : IRenderGraphPackageFactory, IDisposable {
    private readonly Dictionary<string, Registration> m_instances = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, EnvironmentCounter> m_counters = new(comparer: StringComparer.Ordinal);
    private readonly Lock m_gate = new();

    /// <inheritdoc/>
    public bool OwnsBuffer(string? part) => true;

    /// <inheritdoc/>
    public bool SamplesReads => true;

    /// <inheritdoc/>
    public RenderGraphReadEpoch? ReadEpochOf(string instance, string producer) =>
        ((Resolve(instance: instance)?.Residency is { } residency) ? views.ReadEpochOf(producer: producer, residency: residency) : null);
    /// <summary>Names a residency's one producer while at least one of its views is demanded.</summary>
    /// <param name="name">The generated producer name.</param>
    /// <param name="residency">The existing residency whose environment it projects.</param>
    /// <param name="view">The canonical observed view whose existing screen-source reads it acquires.</param>
    public void Register(string name, SdfWorldResidency residency, int view) {
        ArgumentNullException.ThrowIfNull(residency);
        lock (m_gate) {
            if (m_instances.TryGetValue(key: name, value: out var previous)) {
                if (ReferenceEquals(objA: previous.Residency, objB: residency)) {
                    m_instances[name] = previous with { View = view };
                    views.RegisterEnvironment(name: name, residency: residency, view: view);
                    return;
                }
                views.UnregisterEnvironment(residency: previous.Residency);
                previous.Residency.Release();
            }
            residency.Retain();
            m_instances[name] = new Registration(Residency: residency, View: view);
            views.RegisterEnvironment(name: name, residency: residency, view: view);
        }
    }
    /// <summary>Releases a producer that no view reads.</summary>
    /// <param name="name">The registered producer name.</param>
    public void Unregister(string name) {
        lock (m_gate) {
            m_counters.Remove(key: name);
            if (m_instances.Remove(key: name, value: out var registration)) {
                views.UnregisterEnvironment(residency: registration.Residency);
                registration.Residency.Release();
            }
        }
    }
    /// <inheritdoc/>
    public void Dispose() {
        lock (m_gate) {
            foreach (var registration in m_instances.Values) { views.UnregisterEnvironment(residency: registration.Residency); registration.Residency.Release(); }
            m_instances.Clear();
            m_counters.Clear();
        }
    }
    /// <inheritdoc/>
    public string? RefusalOf(string instance) => Resolve(instance: instance)?.Residency.Refusal;
    /// <inheritdoc/>
    public RenderGraphPackageFragment? FragmentOf(string instance) => SdfSkyEnvironmentGraph.Fragment;
    /// <inheritdoc/>
    public IShaderPipelineStorageCounter? CounterOf(string instance) {
        lock (m_gate) {
            if (!m_instances.ContainsKey(key: instance)) { return null; }
            if (!m_counters.TryGetValue(key: instance, value: out var counter)) {
                counter = new EnvironmentCounter(instance: instance, owner: this);
                m_counters.Add(key: instance, value: counter);
            }
            return counter;
        }
    }
    /// <inheritdoc/>
    public bool IsUnchanged(string instance, long unreadFrames, in FrameContext context) {
        if (Resolve(instance: instance)?.Residency is not { } residency) { return false; }
        views.Begin(residency: residency);
        if (!residency.Prepare(context: context)) { return false; }
        var tables = residency.Tables!;

        tables.PollSkyEnvironment();
        // Image cadence is decided only after the runtime acquires the same reads that recording would sample.
        return (!tables.SkyEnvironmentHasImages && !tables.SkyEnvironmentOwes &&
            !tables.ScreenEmissionHasImages && !tables.ScreenEmissionOwes);
    }
    /// <inheritdoc/>
    public async ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
        var registration = (Resolve(instance: context.Instance) ?? throw new InvalidOperationException(message: $"Environment instance '{context.Instance}' names no residency."));
        var residency = registration.Residency;

        residency.Retain();
        try {
            await residency.WaitReadyAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            return new Built(residency: residency, tables: residency.Tables!, view: registration.View);
        } catch { residency.Release(); throw; }
    }
    /// <inheritdoc/>
    public IGpuBuffer? BorrowedBuffer(RenderGraphPackageRecorderContext context, IDisposable? built, ShaderPipelineResource resource) => context.Part switch {
        SdfSkyEnvironmentGraph.Map => ((Built)built!).Tables.SkyEnvironmentMap,
        SdfSkyEnvironmentGraph.Screens => ((Built)built!).Tables.ScreenEmission,
        _ => ((Built)built!).Tables.SkyEnvironmentCoefficients,
    };
    /// <inheritdoc/>
    public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) {
        try { return new SdfSkyEnvironmentRecorder(built: ((Built)built!), context: context, groups: groups, views: views); } catch { built?.Dispose(); throw; }
    }

    private sealed record Registration(SdfWorldResidency Residency, int View);

    private Registration? Resolve(string instance) { lock (m_gate) { return m_instances.GetValueOrDefault(key: instance); } }

    private sealed class EnvironmentCounter(SdfSkyEnvironmentPasses owner, string instance) : IShaderPipelineStorageCounter {
        private readonly Lock m_gate = new();

        private SdfWorldTables? m_tables;

        private int m_view = -1;

        private long m_revision;

        public long Revision {
            get {
                var registration = owner.Resolve(instance: instance);
                var tables = registration?.Residency.Tables;
                var view = (registration?.View ?? -1);

                lock (m_gate) {
                    if (!ReferenceEquals(objA: tables, objB: m_tables) || (view != m_view)) { m_tables = tables; m_view = view; m_revision++; }
                    return m_revision;
                }
            }
        }

        public ShaderPipelineStorageCounts CountsAt(uint width, uint height) => new(Height: height, Width: width);
    }

    internal sealed class Built(SdfWorldResidency residency, SdfWorldTables tables, int view) : IDisposable {
        public SdfWorldResidency Residency { get; } = residency;
        public SdfWorldTables Tables { get; } = tables;
        public int View { get; } = view;

        public void Dispose() => Residency.Release();
    }
}
