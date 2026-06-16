using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Puck.DirectX.Messages;
using Puck.Hosting;

namespace Puck.Recursive.Nodes;

/// <summary>
/// A Direct3D 12 host node: it drives one child, uploads the child's surface into an SRV texture, composites it
/// as an inset over a framed background through the textured pipeline, reads the result back to host memory,
/// and hands it up as a <em>CPU-pixel</em> <see cref="Surface"/>. It is the Direct3D 12 analog of
/// <c>SdfEngineNode</c> — a host that composites a child "without knowing what produced it" — which lets a
/// DirectX engine sit anywhere in the recursive tree (hosting a Vulkan child, or hosted by a Vulkan parent).
/// The child resolves whichever backend device it needs from the inherited host capability seam.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class DirectXEngineNode : IRenderNode {
    private const uint OutputExtent = 256;
    private const SurfaceFormat OutputFormat = SurfaceFormat.R8G8B8A8Unorm;
    private const uint VertexCount = 6;
    private const uint VertexStrideBytes = (sizeof(float) * 4);

    private static readonly byte[] InsetQuadVertexData = CreateInsetQuadVertexData();
    private readonly IRenderNode m_child;
    private readonly NodeDescriptor m_descriptor;
    private readonly IDirectXPipelineFactory m_pipelineFactory;
    private readonly IDirectXShaderCompilerApi m_shaderCompiler;
    private readonly IDirectXVertexBufferFactory m_vertexBufferFactory;
    private bool m_disposed;
    private DirectXPipeline? m_pipeline;
    private byte[]? m_pixels;
    private DirectXOffscreenRenderer? m_renderer;
    private DirectXSurfaceUpload? m_surfaceUpload;
    private DirectXVertexBuffer? m_vertexBuffer;

    public DirectXEngineNode(
        IRenderNode child,
        IDirectXShaderCompilerApi shaderCompiler,
        IDirectXPipelineFactory pipelineFactory,
        IDirectXVertexBufferFactory vertexBufferFactory,
        string? name = null
    ) {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(pipelineFactory);
        ArgumentNullException.ThrowIfNull(shaderCompiler);
        ArgumentNullException.ThrowIfNull(vertexBufferFactory);

        m_child = child;
        m_descriptor = new NodeDescriptor(
            Name: (name ?? "directx-engine"),
            SurfaceId: SurfaceId.New()
        );
        m_pipelineFactory = pipelineFactory;
        m_shaderCompiler = shaderCompiler;
        m_vertexBufferFactory = vertexBufferFactory;
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

        // The child inherits the same capability seam, so it resolves whichever backend device it was built
        // against — a Vulkan child finds the Vulkan device, a DirectX child the DirectX device.
        var childSurface = m_child.ProduceFrame(context: in context);

        if (
            childSurface.IsEmpty ||
            !childSurface.IsCpuPixels
        ) {
            return default;
        }

        var renderer = (m_renderer ??= new DirectXOffscreenRenderer(
            deviceContext: deviceContext,
            height: OutputExtent,
            width: OutputExtent
        ));
        var pipeline = (m_pipeline ??= CreatePipeline(deviceContext: deviceContext));
        var vertexBuffer = (m_vertexBuffer ??= m_vertexBufferFactory.Create(
            deviceContext: deviceContext,
            strideBytes: VertexStrideBytes,
            vertexData: InsetQuadVertexData
        ));
        var surfaceUpload = (m_surfaceUpload ??= new DirectXSurfaceUpload(deviceContext: deviceContext));
        var pixels = (m_pixels ??= new byte[renderer.PixelByteLength]);

        surfaceUpload.Upload(
            format: DirectXTextured.ToPixelFormat(format: childSurface.Format),
            height: childSurface.Height,
            pixels: childSurface.Pixels.Span,
            width: childSurface.Width
        );
        renderer.RenderInto(
            clearAlpha: 1f,
            clearBlue: 0.18f,
            clearGreen: 0.06f,
            clearRed: 0.10f,
            destination: pixels,
            pipeline: pipeline,
            texture: surfaceUpload,
            vertexBuffer: vertexBuffer,
            vertexCount: VertexCount
        );

        return new Surface(
            Format: OutputFormat,
            Height: renderer.Height,
            ImageViewHandle: 0,
            Pixels: pixels,
            Width: renderer.Width
        );
    }

    private DirectXPipeline CreatePipeline(IDirectXDeviceContext deviceContext) {
        // The PSO copies the bytecode in, so the blobs are freed once the pipeline is built.
        using var vertexShader = m_shaderCompiler.Compile(request: new DirectXShaderCompileRequest(
            EntryPoint: "VSMain",
            HlslSource: DirectXTextured.ShaderSource,
            SourceName: "directx-engine.hlsl",
            Target: "vs_5_0"
        ));
        using var pixelShader = m_shaderCompiler.Compile(request: new DirectXShaderCompileRequest(
            EntryPoint: "PSMain",
            HlslSource: DirectXTextured.ShaderSource,
            SourceName: "directx-engine.hlsl",
            Target: "ps_5_0"
        ));

        return m_pipelineFactory.CreateTextured(
            deviceContext: deviceContext,
            pixelShader: pixelShader,
            vertexShader: vertexShader
        );
    }
    private static byte[] CreateInsetQuadVertexData() {
        // A centered quad (two triangles) covering [-0.8, 0.8] in clip space with [0, 1] texture coordinates,
        // so the framed clear shows as a border around the composited child.
        float[] vertices = [
            -0.8f, -0.8f, 0f, 1f,
            -0.8f, 0.8f, 0f, 0f,
            0.8f, 0.8f, 1f, 0f,
            -0.8f, -0.8f, 0f, 1f,
            0.8f, 0.8f, 1f, 0f,
            0.8f, -0.8f, 1f, 1f,
        ];
        var data = new byte[(vertices.Length * sizeof(float))];

        Buffer.BlockCopy(
            count: data.Length,
            dst: data,
            dstOffset: 0,
            src: vertices,
            srcOffset: 0
        );

        return data;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_child.Dispose();
        m_surfaceUpload?.Dispose();
        m_surfaceUpload = null;
        m_vertexBuffer?.Dispose();
        m_vertexBuffer = null;
        m_pipeline?.Dispose();
        m_pipeline = null;
        m_renderer?.Dispose();
        m_renderer = null;
    }
}
