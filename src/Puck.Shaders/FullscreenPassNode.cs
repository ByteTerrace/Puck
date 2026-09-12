using System.Buffers.Binary;

using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>Adapts a shipped <see cref="ShaderSetManifest"/> fullscreen effect to the canonical shader pipeline executor.</summary>
/// <remarks>The inner node produces the source surface; the adapter preserves its capture and device-lifetime semantics.</remarks>
public sealed class FullscreenPassNode : IRenderNode, ICaptureRequestTarget {
    private readonly NodeDescriptor m_descriptor;
    private readonly IRenderNode m_inner;
    private readonly ShaderSetManifest m_manifest;
    private readonly uint m_width;
    private readonly uint m_height;
    private readonly bool m_hostsOnDirectX;
    private readonly IFullscreenPassServices m_services;
    private ShaderPipelineRenderNode? m_executor;
    private readonly IGpuComputeServices? m_compute;
    private GpuPixelFormat? m_inputFormat;
    private uint m_inputWidth;
    private uint m_inputHeight;
    private readonly ShaderPushConstantLayout? m_layout;
    private readonly byte[] m_constants;
    private ShaderConfigValues m_config;
    private Dictionary<string, ShaderConfigValue>? m_liveConfig;
    private Dictionary<string, byte[]>? m_liveConfigBytes;
    private bool m_disposed;
    private readonly CaptureRequestSlot m_capture = new();

    public FullscreenPassNode(IRenderNode inner, ShaderSetManifest manifest, ShaderConfigValues config,
        IFullscreenPassServices services, bool hostsOnDirectX, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        if (!manifest.IsGraphics) {
            throw new InvalidDataException($"'{manifest.Name}' is a compute set; a fullscreen pass needs vertex and fragment stages.");
        }

        if (manifest.Bindings.Count != 1 || manifest.Bindings[0].Kind != ShaderSetManifestBindingKind.SampledImage || manifest.Bindings[0].Count != 1) {
            throw new InvalidDataException($"'{manifest.Name}' must declare exactly one sampledImage binding (the inner surface) and nothing else to run as a fullscreen pass.");
        }

        m_descriptor = new NodeDescriptor(manifest.Name, SurfaceId.New());
        m_inner = inner; m_manifest = manifest; m_config = config;
        m_layout = manifest.PushConstantLayout; m_constants = new byte[m_layout?.SizeBytes ?? 0];
        m_width = width; m_height = height; m_hostsOnDirectX = hostsOnDirectX; m_services = services;
        FillStaticConstants();
        if (services.ComputeServices is not null) {
            m_compute = services.ComputeServices;
        }
    }

    /// <summary>Gets the live configuration values applied to the manifest constants.</summary>
    public ShaderConfigValues Config => m_config;
    /// <summary>Gets this adapter node descriptor.</summary>
    public NodeDescriptor Descriptor => m_descriptor;
    public string? PendingCapturePath => m_capture.PendingPath ?? m_executor?.PendingCapturePath ?? (m_inner as ICaptureRequestTarget)?.PendingCapturePath;

    /// <summary>Produces one adapted frame from the inner node.</summary>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        var surface = m_inner.ProduceFrame(context);
        if (surface.IsEmpty || surface.ImageViewHandle == 0) {
            m_capture.Forward(m_inner as ICaptureRequestTarget);
            return surface;
        }
        var format = ToGpuFormat(surface.Format);
        var executor = m_executor;
        if (executor is null || m_inputFormat != format || m_inputWidth != surface.Width || m_inputHeight != surface.Height) {
            executor?.Dispose();
            executor = m_executor = CreateExecutor(m_compute ?? throw new InvalidOperationException("Fullscreen pass services do not provide compute services."), format, surface.Width, surface.Height);
            m_inputFormat = format;
            m_inputWidth = surface.Width;
            m_inputHeight = surface.Height;
        }
        executor.BindImage("input", new ShaderPipelineExternalImage(surface.ImageHandle, surface.ImageViewHandle, surface.Width, surface.Height,
            format, GpuImageLayout.ShaderReadOnly));
        m_capture.Forward(executor);
        return executor.ProduceFrame(context);
    }

    /// <summary>Arms a capture that is forwarded to the current executor when available.</summary>
    public void RequestCapture(FrameCaptureRequest request) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        m_capture.Arm(request, PendingCapturePath);
    }

    /// <summary>Updates a declared floating-point manifest parameter.</summary>
    public bool TrySetConfig(string field, float value) {
        if (m_manifest.Config is not { } schema || !schema.TryGetValue(field, out var declared) ||
            declared.Type != ShaderValueType.Float || !float.IsFinite(value) || !ShaderConfigBinding.InRange(declared, value)) {
            return false;
        }

        if (m_liveConfig is not { } live) {
            live = new Dictionary<string, ShaderConfigValue>(StringComparer.Ordinal);
            foreach (var name in m_config.Names) {
                live[name] = m_config[name];
            }

            m_liveConfig = live; m_liveConfigBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal); m_config = new ShaderConfigValues(live);
        }
        if (!m_liveConfigBytes!.TryGetValue(field, out var bytes)) {
            bytes = new byte[ShaderValueTypes.ComponentBytes]; m_liveConfigBytes[field] = bytes; live[field] = new ShaderConfigValue(ShaderValueType.Float, bytes);
        }
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        if (m_layout is { } layout) {
            foreach (var slot in layout.Slots.Where(slot => slot.Kind == ShaderPushConstantSourceKind.Config && slot.ConfigField == field)) {
                bytes.CopyTo(m_constants.AsSpan((int)slot.Offset));
            }
        }

        return true;
    }

    /// <summary>Forwards device loss to the active executor and inner node.</summary>
    public void OnDeviceLost() { m_executor?.OnDeviceLost(); m_inner.OnDeviceLost(); }

    /// <summary>Releases the active executor and inner node.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_capture.Refuse(new ObjectDisposedException(nameof(FullscreenPassNode)));
        try { m_executor?.Dispose(); } finally { m_inner.Dispose(); }
    }

    private ShaderPipelineRenderNode CreateExecutor(IGpuComputeServices compute, GpuPixelFormat inputFormat, uint inputWidth, uint inputHeight) {
        var input = new ShaderPipelineResource("input", ShaderPipelineResourceKind.Image, inputFormat.ToString(), ShaderPipelineDimensions.Absolute(inputWidth, inputHeight), Initialization: ShaderPipelineInitialization.External);
        var output = new ShaderPipelineResource("output", ShaderPipelineResourceKind.Image, "R8G8B8A8Unorm", ShaderPipelineDimensions.Relative());
        var pass = new ShaderPipelinePass(Name: m_manifest.Name, Source: Path.Combine(m_manifest.Directory, m_manifest.Stages.Fragment! + ".hlsl"),
            Language: ShaderSourceLanguage.Hlsl, EntryPoint: "PSMain", Kind: ShaderPipelinePassKind.Fullscreen,
            Inputs: [new ResourceReference("input", Binding: m_manifest.Bindings[0].VulkanBinding)], Outputs: [new ResourceReference("output")]);
        var definition = new ShaderPipelineDefinition(m_manifest.Name, [input, output], [pass], [(ShaderPipelineOutput)"output"]);
        var plan = ShaderPipelineCompiler.Plan(definition);
       var spirv = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> {
            [ShaderStage.Vertex] = File.ReadAllBytes(m_manifest.BytecodePath(m_manifest.Stages.Vertex!, ".spv")),
            [ShaderStage.Fragment] = File.ReadAllBytes(m_manifest.BytecodePath(m_manifest.Stages.Fragment!, ".spv")),
        };
        var dxil = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> {
            [ShaderStage.Vertex] = File.ReadAllBytes(m_manifest.BytecodePath(m_manifest.Stages.Vertex!, ".dxil")),
            [ShaderStage.Fragment] = File.ReadAllBytes(m_manifest.BytecodePath(m_manifest.Stages.Fragment!, ".dxil")),
        };
        var compiled = new CompiledShader(m_manifest.Name, Path.Combine(m_manifest.Directory, m_manifest.Name + ShaderSetManifest.FileSuffix), "manifest", spirv, dxil, []);
        var candidate = new CompiledShaderPipeline(plan, new Dictionary<string, CompiledShader> { [m_manifest.Name] = compiled });
        var constants = new Dictionary<string, IShaderPipelinePassConstants>(StringComparer.Ordinal) {
            [m_manifest.Name] = new ManifestPassConstants(m_layout, () => m_config, m_constants),
        };
        return new ShaderPipelineRenderNode(candidate, compute, m_services.DeviceContext, m_hostsOnDirectX,
            m_width, m_height, m_services, passConstants: constants, outputLayout: GpuImageLayout.ShaderReadOnly,
            positionVertexPasses: new HashSet<string>(StringComparer.Ordinal) { m_manifest.Name });
    }

    private void FillStaticConstants() {
        if (m_layout is not { } layout) {
            return;
        }

        foreach (var slot in layout.Slots) {
            var destination = m_constants.AsSpan((int)slot.Offset, (int)slot.Type.SizeBytes());
            switch (slot.Kind) {
                case ShaderPushConstantSourceKind.Config: m_config[slot.ConfigField!].Bytes.Span.CopyTo(destination); break;
                case ShaderPushConstantSourceKind.Resolution:
                    if (slot.Type == ShaderValueType.Float2) { BinaryPrimitives.WriteSingleLittleEndian(destination, m_width); BinaryPrimitives.WriteSingleLittleEndian(destination[4..], m_height); } else { BinaryPrimitives.WriteUInt32LittleEndian(destination, m_width); BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], m_height); }
                    break;
            }
        }
    }

    private static GpuPixelFormat ToGpuFormat(SurfaceFormat format) {
        return format switch {
            SurfaceFormat.R8G8B8A8Unorm => GpuPixelFormat.R8G8B8A8Unorm,
            SurfaceFormat.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8Unorm,
            _ => throw new InvalidDataException($"Fullscreen input surface format '{format}' is unsupported."),
        };
    }

    private sealed class ManifestPassConstants(ShaderPushConstantLayout? layout, Func<ShaderConfigValues> config, byte[] constants) : IShaderPipelinePassConstants {
        public uint SizeBytes => (uint)constants.Length;
        public GpuShaderStage Stages => layout?.Stages ?? GpuShaderStage.None;
        public void Write(in FrameContext context, in ShaderFrameInput input, uint passWidth, uint passHeight, ulong frameCounter, Span<byte> destination) {
            constants.AsSpan().CopyTo(destination);
            if (layout is not { } resolved) {
                return;
            }

            foreach (var slot in resolved.Slots) {
                var target = destination[(int)slot.Offset..];
                switch (slot.Kind) {
                    case ShaderPushConstantSourceKind.Tick:
                        var period = slot.QuantizeHzLiteral is { } literal ? EngineTicks.PerRate(literal) : slot.QuantizeHzConfigField is { } field ? EngineTicks.PerRate(config()[field].ComponentBits(0)) : 1;
                        var value = context.ElapsedTicks / period;
                        BinaryPrimitives.WriteUInt32LittleEndian(target, (uint)value);
                        if (slot.Type == ShaderValueType.Uint2) {
                            BinaryPrimitives.WriteUInt32LittleEndian(target[4..], (uint)(value >> 32));
                        }

                        break;
                    case ShaderPushConstantSourceKind.Frame: BinaryPrimitives.WriteUInt32LittleEndian(target, (uint)frameCounter); break;
                }
            }
        }
    }
}
