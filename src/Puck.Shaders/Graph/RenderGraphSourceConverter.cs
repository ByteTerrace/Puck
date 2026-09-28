using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// Converts CPU pixels a producer holds outside the render graph's set — a camera's or a desktop capture's CPU tier, a
/// capture fill — into an image through the one-pass graph the pixels' descriptor names: the conversion an uploaded source
/// instance renders through (<see cref="RenderGraphRuntime.CreateConverter"/>), on a node of its own, so every CPU image
/// that reaches rendering converts through the shipped conversion kernels. Each <see cref="TryConvert"/> writes the pixels
/// into the node's region behind the header the descriptor fixed and records the conversion in one submission; the
/// output is a same-device image in the node's frame-slot ring, sampled by later submissions in queue order as any graph
/// instance's output is. The node builds its graph off the frame thread, so the first conversions wait for it. Every member
/// runs on the thread that produces frames.
/// </summary>
public sealed class RenderGraphSourceConverter : IDisposable {
    private readonly ShaderPipelineRenderNode m_node;
    private readonly RenderGraphSourceRegion m_region;

    private Surface m_output;

    internal RenderGraphSourceConverter(ShaderPipelineRenderNode node, RenderGraphRuntimeGraph? graph, ImageSourceUploadHeader header, string? fault) {
        Fault = fault;
        Header = header;
        m_node = node;
        m_region = new RenderGraphSourceRegion(header: header);

        if (graph is not null) {
            m_node.Swap(pipeline: graph.Pipeline);
            m_node.Resize(
                height: header.Height,
                width: header.Width
            );
        }
    }

    /// <summary>Gets why no conversion reads the descriptor the converter was made for, or <see langword="null"/> when one
    /// does.</summary>
    public string? Fault { get; }
    /// <summary>Gets the header the descriptor fixed: the extent, format and color encoding of the pixels
    /// <see cref="TryConvert"/> takes.</summary>
    public ImageSourceUploadHeader Header { get; }
    /// <summary>Gets the image-view handle of the latest conversion's output, or zero before the first and after a device
    /// loss.</summary>
    public nint ImageViewHandle => m_output.ImageViewHandle;
    /// <summary>Gets the conversion's counted GPU work, one pass per conversion.</summary>
    public IGpuWorkSource Work => m_node;

    /// <inheritdoc/>
    public void Dispose() => m_node.Dispose();
    /// <summary>Releases every device object after the device was lost; the next conversion rebuilds on the recreated
    /// device.</summary>
    public void OnDeviceLost() {
        m_node.OnDeviceLost();
        m_region.OnDeviceLost();
        m_output = default;
    }
    /// <summary>Converts one image: writes its planes into the region behind the header and records the conversion.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <param name="planes">The pixels, laid out as <see cref="ImageSourceUploadLayout"/> places them after the header:
    /// for a four-channel format, tightly packed rows of <see cref="Header"/>'s extent.</param>
    /// <returns><see langword="true"/> when the conversion was submitted and <see cref="ImageViewHandle"/> names its
    /// output; <see langword="false"/> while the graph builds, or when no conversion reads the descriptor.</returns>
    public bool TryConvert(in FrameContext context, ReadOnlySpan<byte> planes) {
        if (
            (Fault is not null) ||
            (m_region.Bind(node: m_node) is not { } region)
        ) {
            return false;
        }

        _ = region.Write(
            bytes: planes,
            offset: ImageSourceUploadLayout.HeaderBytes
        );

        var submitted = m_node.FrameCounter;
        var surface = m_node.ProduceFrame(context: in context);

        if (m_node.FrameCounter == submitted) {
            return false;
        }

        m_output = surface;

        return true;
    }
}

/// <summary>A source conversion's region: the node's host buffer port, bound once the node's graph installs, whose header
/// the descriptor fixed. The node owns the region; a device loss forgets it, and the next write binds a new one.</summary>
/// <param name="header">The header the region leads with.</param>
internal sealed class RenderGraphSourceRegion(ImageSourceUploadHeader header) {
    private GpuRegion? m_region;

    /// <summary>Returns the region, having the node bind a new one first when it has none: never while the node cannot
    /// bind one yet, as a staged region cannot until its graph installs with the device's region-copy pipeline.</summary>
    /// <param name="node">The node that runs the conversion.</param>
    /// <returns>The region, or <see langword="null"/> while the node binds none.</returns>
    public GpuRegion? Bind(ShaderPipelineRenderNode node) {
        if (m_region is not null) {
            return m_region;
        }
        if (node.BindRegion(name: RenderGraphPackageCatalog.SourceRegion) is not { } region) {
            return null;
        }

        Span<byte> head = stackalloc byte[ImageSourceUploadLayout.HeaderBytes];

        ImageSourceUploadLayout.Write(
            header: in header,
            region: head
        );
        _ = region.Write(
            bytes: head,
            offset: 0
        );
        m_region = region;

        return region;
    }
    /// <summary>Forgets the region the node lost with its device objects.</summary>
    public void OnDeviceLost() => m_region = null;
}
