using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The indirect package's sole producer for each residency, shared by all of its view buffer edges.</summary>
public sealed class SdfIndirectPasses(SdfWorldPasses views) : IRenderGraphPackageFactory, IDisposable {
    private readonly Dictionary<string, SdfWorldResidency> m_instances = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, CacheCounter> m_counters = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<SdfIndirectTier, RenderGraphPackageFragment> m_fragments = [];
    private readonly Lock m_gate = new();

    /// <inheritdoc/>
    public bool OwnsBuffers => true;

    /// <summary>Names the residency's one producer while views demand its cache.</summary>
    public void Register(string name, SdfWorldResidency residency) {
        ArgumentNullException.ThrowIfNull(residency);
        lock (m_gate) {
            if (m_instances.TryGetValue(key: name, value: out var previous)) {
                if (ReferenceEquals(objA: previous, objB: residency)) { return; }
                previous.Release();
            }
            residency.Retain();
            m_instances[name] = residency;
        }
    }
    /// <summary>Releases an instance no enabled view reads.</summary>
    public void Unregister(string name) {
        lock (m_gate) {
            m_counters.Remove(key: name);
            if (m_instances.Remove(key: name, value: out var residency)) { residency.Release(); }
        }
    }
    /// <inheritdoc/>
    public void Dispose() {
        lock (m_gate) { foreach (var residency in m_instances.Values) { residency.Release(); } m_instances.Clear(); m_counters.Clear(); }
    }
    /// <inheritdoc/>
    /// <remarks>A live far-distance edit replaces the borrowed cache without changing its tier or byte length.
    /// Its identity therefore moves the storage revision, so the graph rebuilds its producer before publishing the new buffer.</remarks>
    public IShaderPipelineStorageCounter? CounterOf(string instance) {
        lock (m_gate) {
            if (!m_instances.ContainsKey(key: instance)) { return null; }
            if (!m_counters.TryGetValue(key: instance, value: out var counter)) {
                counter = new CacheCounter(owner: this, instance: instance);
                m_counters.Add(key: instance, value: counter);
            }
            return counter;
        }
    }
    /// <inheritdoc/>
    public string? RefusalOf(string instance) => Resolve(instance: instance)?.Refusal;
    /// <inheritdoc/>
    public RenderGraphPackageFragment? FragmentOf(string instance) {
        var tier = (Resolve(instance: instance)?.IndirectTier ?? SdfIndirectTier.Medium);

        if (tier == SdfIndirectTier.Off) { tier = SdfIndirectTier.Medium; }
        lock (m_gate) {
            if (!m_fragments.TryGetValue(key: tier, value: out var fragment)) {
                fragment = SdfWorldPackage.IndirectFragment(bytes: new SdfIndirectLayout(tier: tier).ByteLength);
                m_fragments.Add(key: tier, value: fragment);
            }
            return fragment;
        }
    }
    /// <inheritdoc/>
    public bool IsUnchanged(string instance, long unreadFrames, in FrameContext context) {
        var residency = Resolve(instance: instance);

        if ((residency is null) || !residency.Prepare(context: context)) { return false; }
        residency.Tables!.PlanIndirect(frame: residency.Frame!);
        return (residency.Tables.Indirect is not { NeedsPublish: true });
    }
    /// <inheritdoc/>
    public async ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
        var residency = (Resolve(instance: context.Instance) ?? throw new InvalidOperationException(message: $"Indirect instance '{context.Instance}' names no residency."));

        residency.Retain();
        SdfIndirectCache? cache = null;

        try {
            await residency.WaitReadyAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            cache = (residency.Tables!.RetainIndirect() ?? throw new InvalidOperationException(message: $"Indirect instance '{context.Instance}' has no enabled cache."));
            await residency.Tables.Pipelines.BuildIndirectAsync(context.Pipelines, context.Device, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            return new Built(cache: cache, residency: residency);
        } catch { cache?.Dispose(); residency.Release(); throw; }
    }
    /// <inheritdoc/>
    public IGpuBuffer? BorrowedBuffer(RenderGraphPackageRecorderContext context, IDisposable? built, ShaderPipelineResource resource) =>
        ((Built)built!).Cache.Buffer;
    /// <inheritdoc/>
    public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) {
        try { return new SdfIndirectRecorder(built: ((Built)built!), context: context, groups: groups, views: views); } catch { built?.Dispose(); throw; }
    }

    private SdfWorldResidency? Resolve(string instance) { lock (m_gate) { return m_instances.GetValueOrDefault(key: instance); } }

    // Byte-identical layouts can name different allocations. The node's existing storage revision owns that rebuild,
    // including a residency whose tables are recreated, while each installed recorder retains its previous cache.
    private sealed class CacheCounter(SdfIndirectPasses owner, string instance) : IShaderPipelineStorageCounter {
        private readonly Lock m_gate = new();
        private SdfIndirectCache? m_cache;
        private long m_revision;

        public long Revision {
            get {
                var cache = owner.Resolve(instance: instance)?.Tables?.Indirect;

                lock (m_gate) {
                    if (!ReferenceEquals(objA: cache, objB: m_cache)) {
                        m_cache = cache;
                        m_revision++;
                    }
                    return m_revision;
                }
            }
        }
        public ShaderPipelineStorageCounts CountsAt(uint width, uint height) => new(Width: width, Height: height);
    }

    internal sealed class Built(SdfWorldResidency residency, SdfIndirectCache cache) : IDisposable {
        public SdfWorldResidency Residency { get; } = residency;
        public SdfIndirectCache Cache { get; } = cache;

        public void Dispose() { Cache.Dispose(); Residency.Release(); }
    }
}
