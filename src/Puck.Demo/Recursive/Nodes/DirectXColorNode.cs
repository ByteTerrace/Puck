using System.Runtime.Versioning;
using Puck.DirectX;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Puck.DirectX.Messages;
using Puck.Hosting;

namespace Puck.Recursive.Nodes;

/// <summary>
/// A Direct3D 12 render node: each frame it uploads an animated checkerboard into an SRV texture and draws a
/// full-screen pass sampling it through a compiled HLSL textured pipeline (root signature with an SRV
/// descriptor table + static sampler), reads the pixels back to host memory, and hands them up as a
/// <em>CPU-pixel</em> <see cref="Surface"/>. It is the cross-backend proof — a node drawn on a DirectX device,
/// composited by a Vulkan host into one viewport slot, with the rendered pixels crossing the device boundary
/// through the readback. The host composites it "without knowing what produced it". It resolves its DirectX
/// device from the host capability seam, the same way a Vulkan node resolves its Vulkan device.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class DirectXColorNode : IRenderNode {
    // A fixed internal resolution, decoupled from the window: the host samples this surface by normalized UV
    // into its slot, so the producer's size is free — small keeps the per-frame readback + upload cheap.
    private const uint OutputExtent = 256;
    private const SurfaceFormat OutputFormat = SurfaceFormat.R8G8B8A8Unorm;
    // The sampled texture: a small checkerboard the static sampler stretches over the full-screen quad.
    private const int TextureCellPixels = 8;
    private const uint TextureExtent = 64;
    private const uint VertexCount = 3;
    private const uint VertexStrideBytes = (sizeof(float) * 4);

    private static readonly byte[] FullscreenVertexData = CreateFullscreenVertexData();
    private readonly NodeDescriptor m_descriptor;
    private readonly IDirectXPipelineFactory m_pipelineFactory;
    private readonly IDirectXShaderCompilerApi m_shaderCompiler;
    private readonly byte[] m_texturePixels = new byte[((TextureExtent * TextureExtent) * 4)];
    private readonly IDirectXVertexBufferFactory m_vertexBufferFactory;
    private bool m_disposed;
    private DirectXPipeline? m_pipeline;
    private byte[]? m_pixels;
    private DirectXOffscreenRenderer? m_renderer;
    private DirectXSurfaceUpload? m_surfaceUpload;
    private DirectXVertexBuffer? m_vertexBuffer;

    public DirectXColorNode(
        IDirectXShaderCompilerApi shaderCompiler,
        IDirectXPipelineFactory pipelineFactory,
        IDirectXVertexBufferFactory vertexBufferFactory
    ) {
        ArgumentNullException.ThrowIfNull(pipelineFactory);
        ArgumentNullException.ThrowIfNull(shaderCompiler);
        ArgumentNullException.ThrowIfNull(vertexBufferFactory);

        m_descriptor = new NodeDescriptor(
            Name: "directx-textured",
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

        // Resolve the shared Direct3D 12 device from the host — the same capability seam a Vulkan node uses to
        // resolve its own device. The producer renders on the host's device rather than creating one.
        if (!context.Host.TryResolveCapability<IDirectXDeviceContext>(capability: out var deviceContext)) {
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
            vertexData: FullscreenVertexData
        ));
        var surfaceUpload = (m_surfaceUpload ??= new DirectXSurfaceUpload(deviceContext: deviceContext));
        var pixels = (m_pixels ??= new byte[renderer.PixelByteLength]);
        var time = (float)context.RenderSeconds;

        FillCheckerboard(time: time);
        surfaceUpload.Upload(
            format: DirectXPixelFormat.R8G8B8A8Unorm,
            height: TextureExtent,
            pixels: m_texturePixels,
            width: TextureExtent
        );
        renderer.RenderInto(
            clearAlpha: 1f,
            clearBlue: 0f,
            clearGreen: 0f,
            clearRed: 0f,
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
            SourceName: "directx-textured.hlsl",
            Target: "vs_5_0"
        ));
        using var pixelShader = m_shaderCompiler.Compile(request: new DirectXShaderCompileRequest(
            EntryPoint: "PSMain",
            HlslSource: DirectXTextured.ShaderSource,
            SourceName: "directx-textured.hlsl",
            Target: "ps_5_0"
        ));

        return m_pipelineFactory.CreateTextured(
            deviceContext: deviceContext,
            pixelShader: pixelShader,
            vertexShader: vertexShader
        );
    }
    private void FillCheckerboard(float time) {
        // Two colors that cycle out of phase, so the sampled texture animates and the cross-device readback is
        // visibly live each frame.
        var first = ((byte)(127f + (127f * MathF.Sin(x: time))));
        var second = ((byte)(127f + (127f * MathF.Sin(x: (time + 2.094f)))));

        for (var y = 0; (y < TextureExtent); y++) {
            for (var x = 0; (x < TextureExtent); x++) {
                var isFirst = (0 == (((x / TextureCellPixels) + (y / TextureCellPixels)) & 1));
                var offset = (((y * (int)TextureExtent) + x) * 4);

                m_texturePixels[(offset + 0)] = (isFirst
                    ? first
                    : (byte)32);
                m_texturePixels[(offset + 1)] = (isFirst
                    ? (byte)32
                    : second);
                m_texturePixels[(offset + 2)] = (isFirst
                    ? second
                    : first);
                m_texturePixels[(offset + 3)] = 255;
            }
        }
    }
    private static byte[] CreateFullscreenVertexData() {
        // A full-screen triangle (clip-space position, texture coordinate) covering the viewport.
        float[] vertices = [
            -1f, -1f, 0f, 1f,
            3f, -1f, 2f, 1f,
            -1f, 3f, 0f, -1f,
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
