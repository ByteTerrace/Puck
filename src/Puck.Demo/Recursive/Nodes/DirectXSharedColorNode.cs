using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Puck.Hosting;

namespace Puck.Recursive.Nodes;

/// <summary>
/// A Direct3D 12 node that clears a <em>shared</em> GPU texture to an animated color each frame and hands it up
/// as a zero-copy shared-handle <see cref="Surface"/> — no readback, no upload. A Vulkan host on the same
/// adapter imports the handle and samples the very same GPU memory. This is the zero-copy counterpart to
/// <see cref="DirectXColorNode"/>, proving the cross-backend transport without a CPU round-trip.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class DirectXSharedColorNode : IRenderNode {
    private const uint OutputExtent = 256;
    private const SurfaceFormat OutputFormat = SurfaceFormat.R8G8B8A8Unorm;

    private readonly NodeDescriptor m_descriptor;
    private bool m_disposed;
    private DirectXSharedTexture? m_texture;

    public DirectXSharedColorNode() {
        m_descriptor = new NodeDescriptor(
            Name: "directx-shared",
            SurfaceId: SurfaceId.New()
        );
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc />
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        if (!context.Host.TryResolveCapability<IDirectXDeviceContext>(capability: out var deviceContext)) {
            return default;
        }

        var texture = (m_texture ??= new DirectXSharedTexture(
            deviceContext: deviceContext,
            height: OutputExtent,
            width: OutputExtent
        ));
        var time = (float)context.RenderSeconds;

        texture.RenderClear(
            alpha: 1f,
            blue: (0.5f + (0.5f * MathF.Sin(x: ((time * 0.5f) + 4.188f)))),
            green: (0.5f + (0.5f * MathF.Sin(x: ((time * 0.5f) + 2.094f)))),
            red: (0.5f + (0.5f * MathF.Sin(x: (time * 0.5f))))
        );

        return new Surface(
            Format: OutputFormat,
            Height: texture.Height,
            ImageViewHandle: 0,
            SharedHandle: texture.SharedHandle,
            Width: texture.Width
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_texture?.Dispose();
        m_texture = null;
    }
}
