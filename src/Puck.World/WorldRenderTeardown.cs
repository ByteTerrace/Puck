using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.World;

/// <summary>
/// A pass-through render root that ties GPU-resource-holding singletons to the render chain's teardown: the window
/// loop disposes its root node (then the presenter) while the device is still alive, but the DI container disposes
/// singletons in reverse CREATION order — which puts early-created GPU holders (the screen binder's camera feeds and
/// jumbotron view engines) AFTER the device context's own teardown, where a queue drain throws (D3D12) or a
/// vkDestroy* faults natively (Vulkan). Disposing them here runs them at the window loop's safe point; the
/// container's later second call is an idempotent no-op.
/// </summary>
internal sealed class WorldRenderTeardown : IRenderNode {
    private readonly IDisposable[] m_alsoDispose;
    private readonly IRenderNode m_inner;

    /// <summary>Initializes a new instance of the <see cref="WorldRenderTeardown"/> class.</summary>
    /// <param name="inner">The actual render root.</param>
    /// <param name="alsoDispose">GPU-holding services to dispose right after the chain, oldest last.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldRenderTeardown(IRenderNode inner, params IDisposable[] alsoDispose) {
        ArgumentNullException.ThrowIfNull(argument: alsoDispose);
        ArgumentNullException.ThrowIfNull(argument: inner);

        m_alsoDispose = alsoDispose;
        m_inner = inner;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_inner.Descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) => m_inner.ProduceFrame(context: in context);

    /// <inheritdoc/>
    public void OnDeviceLost() => m_inner.OnDeviceLost();

    /// <inheritdoc/>
    public void Dispose() {
        m_inner.Dispose();

        foreach (var disposable in m_alsoDispose) {
            disposable.Dispose();
        }
    }
}
