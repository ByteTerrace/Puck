using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The world's one shadow solution (SdfWorldShadows): created once the pass pipelines it draws with are built, drawn by the
// upload ahead of every view's submission, and read by every view through CascadesOf and the layers its pass sets bind.
public sealed partial class SdfWorldTables {
    private SdfWorldShadows? m_shadows;
    // The frame the tables last packed, which the upload draws the shadows of.
    private SdfFrame? m_shadowFrame;
    // The descriptor pools reserved at construction for the shadow entries the world cannot shadow without
    // (SdfWorldShadows.ReservedEntries), until an entry takes one; an entry destroys the pool it took.
    private readonly Stack<nint> m_shadowPools = new();

    // The pools the tables reserve for the shadows (DescriptorPools).
    private static GpuDescriptorPoolSizes[] ShadowPoolSizes {
        get {
            var pools = new GpuDescriptorPoolSizes[SdfWorldShadows.ReservedEntries];

            Array.Fill(array: pools, value: SdfWorldShadows.EntryPoolSizes);

            return pools;
        }
    }

    // Creates the shadows' reserved pools; the constructor calls it, so no later frame takes their descriptor ranges.
    private void ReserveShadowPools(GpuCreationScope scope) {
        for (var at = 0; (at < SdfWorldShadows.ReservedEntries); at++) {
            var pool = m_bindings.CreatePool(name: NameOf(part: "shadow-descriptors", index: at), sizes: SdfWorldShadows.EntryPoolSizes);

            m_shadowPools.Push(item: pool);
            _ = scope.Own(handle: pool, release: m_bindings.DestroyPool);
        }
    }
    // Takes a pool reserved for a shadow entry, or zero once every one is taken.
    internal nint TakeShadowPool() => (m_shadowPools.TryPop(result: out var pool) ? pool : 0);
    // Destroys the reserved pools no entry took.
    private void DestroyShadowPools() {
        while (m_shadowPools.TryPop(result: out var pool)) {
            m_bindings.DestroyPool(poolHandle: pool);
        }
    }

    /// <summary>Gets the world's shadows, or <see langword="null"/> while a pipeline they draw with still builds.</summary>
    internal SdfWorldShadows? Shadows {
        get {
            if ((m_shadows is null) && !m_disposed && (FrameWaiting() is null)) {
                m_shadows = new SdfWorldShadows(gpu: m_gpu, tables: this);
            }
            return m_shadows;
        }
    }
    // The pool the shadows' layers lie in, which their reservation promises in: the mesh regions' (SdfResidencySelector.Pool).
    internal Puck.Abstractions.Memory.PhysicalMemory ShadowPool => m_meshGrant.Selector.Pool;
    /// <summary>Describes the world's shadow entries for <c>world.residency</c>.</summary>
    /// <returns>The description, empty before the shadows exist.</returns>
    public string DescribeShadows() => (m_shadows?.Describe() ?? string.Empty);
    /// <summary>Gets whether the world's shadows stand: every entry a view reads has drawn its layers and owes nothing, so
    /// an upload of a frame that moves nothing draws no shadow; <see langword="true"/> before the shadows exist.</summary>
    public bool ShadowsStand => (m_shadows?.Stands ?? true);

    // Starts a frame of the residency: the shadows decide the next upload's work afresh.
    internal void BeginShadowFrame() => m_shadows?.BeginFrame();
    // An image, framebuffer or buffer the shadows replaced, retired behind every submission that may read it.
    internal void RetireShadowObject(IDisposable replaced) => Retire(replaced: replaced);
    // Records the shadows' work into the upload's command buffer, as its own counted pass.
    private void RecordShadows(nint commandBuffer, int slot) {
        if ((m_shadowFrame is not { } frame) || (Shadows is not { } shadows)) {
            m_work.SkipPass(pass: ShadowsPass);
            return;
        }
        m_work.EnterPass(pass: ShadowsPass);
        m_gpu.Recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, label: "shadows");
        shadows.Record(commandBuffer: commandBuffer, frame: frame, slot: slot);
        m_gpu.Recorder.EndDebugGroup(commandBufferHandle: commandBuffer);
        m_work.LeavePass();
    }
}
