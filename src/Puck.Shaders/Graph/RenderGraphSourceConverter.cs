using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// Converts an image a producer holds outside the render graph's set into an image through the one-pass graph its
/// descriptor names, on a node of its own, so every such image that reaches rendering converts through the shipped
/// conversion kernels. A converter of CPU pixels (<see cref="RenderGraphRuntime.CreateConverter"/>: a camera's or a
/// desktop capture's CPU tier, a capture fill) runs the conversion an uploaded source instance renders through, and each
/// <see cref="TryConvert(in FrameContext, ReadOnlySpan{byte})"/> writes the pixels into the node's region behind the header
/// the descriptor fixed. A converter of an imported image (<see cref="RenderGraphRuntime.CreateImageConverter"/>: a desktop
/// capture of an HDR display copied into shared targets on the GPU) binds the image to the graph's external input for one
/// conversion (<see cref="TryConvert(in FrameContext, ShaderPipelineExternalImage, GpuImageLease)"/>), under the lease that
/// holds it against its producer's next writes and carries the wait its submission makes, so the image never leaves the
/// device. Either records the conversion in one submission; the output is a same-device image in the node's frame-slot
/// ring, sampled by later submissions in queue order as any graph instance's output is. The node builds its graph off the
/// frame thread, so the first conversions wait for it. Every member runs on the thread that produces frames.
/// </summary>
public sealed class RenderGraphSourceConverter : IDisposable {
    // Whether the converter reads an imported image rather than CPU pixels written into its region.
    private readonly bool m_imported;
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
    internal RenderGraphSourceConverter(ShaderPipelineRenderNode node, RenderGraphRuntimeGraph? graph, ImageSourceDescriptor descriptor, string? fault) {
        Fault = fault;
        Format = descriptor.Format;
        Header = new ImageSourceUploadHeader(
            Color: descriptor.Color,
            Format: descriptor.Format,
            Height: descriptor.Height,
            Plane0Offset: 0U,
            Plane0Stride: 0U,
            Plane1Offset: 0U,
            Plane1Stride: 0U,
            Width: descriptor.Width
        );
        m_imported = true;
        m_node = node;
        m_region = new RenderGraphSourceRegion(header: Header);

        if (graph is not null) {
            m_node.Swap(pipeline: graph.Pipeline);
            m_node.Resize(
                height: descriptor.Height,
                width: descriptor.Width
            );
        }
    }

    /// <summary>Gets why no conversion reads the descriptor the converter was made for, or <see langword="null"/> when one
    /// does.</summary>
    public string? Fault { get; }
    /// <summary>Gets the header the descriptor fixed: the extent, format and color encoding of the pixels or the image
    /// a conversion takes. An image converter's header places no plane, since it writes no region.</summary>
    public ImageSourceUploadHeader Header { get; }
    /// <summary>Gets the format of the image an image converter reads (<see cref="RenderGraphRuntime.CreateImageConverter"/>),
    /// or <see langword="null"/> for a converter of CPU pixels.</summary>
    public ImagePixelFormat? Format { get; }
    /// <summary>Gets the image-view handle of the latest conversion's output, or zero before the first and after a device
    /// loss.</summary>
    public nint ImageViewHandle => m_output.ImageViewHandle;
    /// <summary>Gets the latest conversion's output, empty before the first and after a device loss, in
    /// <see cref="OutputLayout"/>.</summary>
    public Surface Output => m_output;
    /// <summary>Gets the latest successful conversion's publication. The node's submission identity survives a
    /// reset, while a held output keeps its original identity until a conversion actually submits.</summary>
    public GpuImagePublication Publication { get; private set; }
    /// <summary>Gets the layout the latest conversion's output rests in between submissions.</summary>
    public GpuImageLayout OutputLayout => m_node.PublishedLayout;
    /// <summary>Gets whether the conversion's graph is building on the thread pool
    /// (<see cref="ShaderPipelineRenderNode.IsBuildingCandidate"/>); a later conversion installs it.</summary>
    public bool IsBuilding => m_node.IsBuildingCandidate;
    /// <summary>Gets the conversion's counted GPU work, one pass per conversion.</summary>
    public IGpuWorkSource Work => m_node;
    /// <summary>Gets the latest conversion's answer, including a refused graph build.</summary>
    public FrameRender Render { get; private set; } = FrameRender.Waiting(reason: "its conversion graph is building");

    /// <inheritdoc/>
    public void Dispose() => m_node.Dispose();
    /// <summary>Releases every device object after the device was lost; the next conversion rebuilds on the recreated
    /// device.</summary>
    public void OnDeviceLost() {
        m_node.OnDeviceLost();
        m_region.OnDeviceLost();
        m_output = default;
        Publication = default;
        Render = FrameRender.Waiting(reason: "its conversion graph is building");
    }
    /// <summary>Converts one image: writes its planes into the region behind the header and records the conversion.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <param name="planes">The pixels, laid out as <see cref="ImageSourceUploadLayout"/> places them after the header:
    /// for a four-channel format, tightly packed rows of <see cref="Header"/>'s extent.</param>
    /// <returns><see langword="true"/> when the conversion was submitted and <see cref="ImageViewHandle"/> names its
    /// output; <see langword="false"/> while the graph builds, or when no conversion reads the descriptor.</returns>
    /// <exception cref="InvalidOperationException">The converter reads an imported image.</exception>
    public bool TryConvert(in FrameContext context, ReadOnlySpan<byte> planes) {
        if (m_imported) {
            throw new InvalidOperationException(message: "An image converter converts an imported image, not CPU pixels.");
        }
        if (Fault is { } fault) {
            Render = FrameRender.Refused(reason: fault);

            return false;
        }
        if (m_region.Bind(node: m_node) is not { } region) {
            Render = Unconverted();

            return false;
        }

        _ = region.Write(
            bytes: planes,
            offset: ImageSourceUploadLayout.HeaderBytes
        );

        var submitted = m_node.FrameCounter;
        var surface = m_node.ProduceFrame(context: in context);

        if (m_node.FrameCounter == submitted) {
            Render = Unconverted();

            return false;
        }

        m_output = surface;
        Publication = new GpuImagePublication(Owner: m_node, Sequence: m_node.SubmissionCount);
        Render = FrameRender.Rendered;

        return true;
    }
    /// <summary>Converts one imported image: binds it to the graph's external input for one frame and records the
    /// conversion. The frame that records holds <paramref name="lease"/> and retires it once that submission has
    /// completed, adding the wait the lease carries to that submission; a conversion that records nothing retires it at
    /// once.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <param name="image">The image, on the converter's device, in the layout its producer leaves it in, which the
    /// conversion hands it back in; of <see cref="Header"/>'s extent.</param>
    /// <param name="lease">The producer's acquisition of the image.</param>
    /// <returns><see langword="true"/> when the conversion was submitted and <see cref="ImageViewHandle"/> names its
    /// output; <see langword="false"/> while the graph builds, or when no conversion reads the descriptor, an image of
    /// another extent is handed over, or the converter reads CPU pixels.</returns>
    public bool TryConvert(in FrameContext context, ShaderPipelineExternalImage image, GpuImageLease lease) {
        if (!m_imported) {
            lease.Retire();
            Render = FrameRender.Refused(reason: "a converter of CPU pixels reads no imported image");

            return false;
        }
        if (Fault is { } fault) {
            lease.Retire();
            Render = FrameRender.Refused(reason: fault);

            return false;
        }
        if ((image.Width != Header.Width) || (image.Height != Header.Height)) {
            lease.Retire();
            Render = FrameRender.Refused(reason: $"an imported {image.Width}x{image.Height} image was handed to a {Header.Width}x{Header.Height} conversion");

            return false;
        }

        m_node.BindImage(
            image: image,
            lease: lease,
            name: RenderGraphPackageCatalog.SourceInput
        );

        var submitted = m_node.FrameCounter;
        var surface = m_node.ProduceFrame(context: in context);

        if (m_node.FrameCounter == submitted) {
            Render = Unconverted();

            return false;
        }

        m_output = surface;
        Publication = new GpuImagePublication(Owner: m_node, Sequence: m_node.SubmissionCount);
        Render = FrameRender.Rendered;

        return true;
    }

    private FrameRender Unconverted() => (((m_node.LastSwapError is { } error) &&
        !m_node.IsBuildingCandidate && !m_node.HasPendingCandidate)
        ? FrameRender.Refused(reason: error.Message)
        : FrameRender.Waiting(reason: "its conversion graph is building"));
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
