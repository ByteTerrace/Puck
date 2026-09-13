using System.Buffers.Binary;

using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>Adapts a shipped <see cref="ShaderSetManifest"/> fullscreen effect to the canonical shader pipeline executor.</summary>
/// <remarks>The inner node produces the source surface; the adapter preserves its capture and device-lifetime semantics.</remarks>
public sealed class FullscreenPassNode : IRenderNode, ICaptureRequestTarget {
    private readonly CaptureRequestSlot m_capture = new();
    private readonly IGpuComputeServices? m_compute;
    private readonly byte[] m_constants;
    private readonly NodeDescriptor m_descriptor;
    private readonly uint m_height;
    private readonly bool m_hostsOnDirectX;
    private readonly IRenderNode m_inner;
    private readonly ShaderPushConstantLayout? m_layout;
    private readonly ShaderSetManifest m_manifest;
    private readonly IFullscreenPassServices m_services;
    private readonly uint m_width;

    private ShaderConfigValues m_config;
    private bool m_disposed;
    private ShaderPipelineRenderNode? m_executor;
    private GpuPixelFormat? m_inputFormat;
    private uint m_inputHeight;
    private uint m_inputWidth;
    private Dictionary<string, ShaderConfigValue>? m_liveConfig;
    private Dictionary<string, byte[]>? m_liveConfigBytes;

    public FullscreenPassNode(IRenderNode inner, ShaderSetManifest manifest, ShaderConfigValues config,
        IFullscreenPassServices services, bool hostsOnDirectX, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        if (!manifest.IsGraphics) {
            throw new InvalidDataException(message: $"'{manifest.Name}' is a compute set; a fullscreen pass needs vertex and fragment stages.");
        }

        if (
            (manifest.Bindings.Count != 1) ||
            (manifest.Bindings[0].Kind != ShaderSetManifestBindingKind.SampledImage) ||
            (manifest.Bindings[0].Count != 1)
        ) {
            throw new InvalidDataException(message: $"'{manifest.Name}' must declare exactly one sampledImage binding (the inner surface) and nothing else to run as a fullscreen pass.");
        }

        m_descriptor = new NodeDescriptor(
            Name: manifest.Name,
            SurfaceId: SurfaceId.New()
        );
        m_inner = inner; m_manifest = manifest; m_config = config;
        m_layout = manifest.PushConstantLayout; m_constants = new byte[(m_layout?.SizeBytes ?? 0)];
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
    public string? PendingCapturePath => (m_capture.PendingPath ?? (m_executor?.PendingCapturePath ?? (m_inner as ICaptureRequestTarget)?.PendingCapturePath));

    private ShaderPipelineRenderNode CreateExecutor(IGpuComputeServices compute, GpuPixelFormat inputFormat, uint inputWidth, uint inputHeight) {
        var input = new ShaderPipelineResource(
            "input",
            ShaderPipelineResourceKind.Image,
            inputFormat.ToString(),
            ShaderPipelineDimensions.Absolute(
                height: inputHeight,
                width: inputWidth
            ),
            Initialization: ShaderPipelineInitialization.External
        );
        var output = new ShaderPipelineResource(
            "output",
            ShaderPipelineResourceKind.Image,
            "R8G8B8A8Unorm",
            ShaderPipelineDimensions.Relative()
        );
        var pass = new ShaderPipelinePass(
            Name: m_manifest.Name,
            Source: Path.Combine(
                path1: m_manifest.Directory,
                path2: (m_manifest.Stages.Fragment! + ".hlsl")
            ),
            Language: ShaderSourceLanguage.Hlsl,
            EntryPoint: "PSMain",
            Kind: ShaderPipelinePassKind.Fullscreen,
            Inputs: [new ResourceReference(
                    "input",
                    Binding: m_manifest.Bindings[0].VulkanBinding
                )],
            Outputs: [new ResourceReference("output")]
        );
        var definition = new ShaderPipelineDefinition(
            m_manifest.Name,
            [input, output],
            [pass],
            [((ShaderPipelineOutput)"output")]
        );
        var plan = ShaderPipelineCompiler.Plan(definition: definition);
        var spirv = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> {
            [ShaderStage.Vertex] = File.ReadAllBytes(path: m_manifest.BytecodePath(
            m_manifest.Stages.Vertex!,
            ".spv"
        )),
            [ShaderStage.Fragment] = File.ReadAllBytes(path: m_manifest.BytecodePath(
            m_manifest.Stages.Fragment!,
            ".spv"
        )),
        };
        var dxil = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> {
            [ShaderStage.Vertex] = File.ReadAllBytes(path: m_manifest.BytecodePath(
            m_manifest.Stages.Vertex!,
            ".dxil"
        )),
            [ShaderStage.Fragment] = File.ReadAllBytes(path: m_manifest.BytecodePath(
            m_manifest.Stages.Fragment!,
            ".dxil"
        )),
        };
        var compiled = new CompiledShader(
            m_manifest.Name,
            Path.Combine(
                path1: m_manifest.Directory,
                path2: (m_manifest.Name + ShaderSetManifest.FileSuffix)
            ),
            "manifest",
            spirv,
            dxil,
            []
        );
        var candidate = new CompiledShaderPipeline(
            plan: plan,
            shaders: new Dictionary<string, CompiledShader> { [m_manifest.Name] = compiled }
        );
        var constants = new Dictionary<string, IShaderPipelinePassConstants>(comparer: StringComparer.Ordinal) {
            [m_manifest.Name] = new ManifestPassConstants(
            config: () => m_config,
            constants: m_constants,
            layout: m_layout
        ),
        };

        return new ShaderPipelineRenderNode(
            candidate,
            compute,
            m_services.DeviceContext,
            m_hostsOnDirectX,
            m_width,
            m_height,
            m_services,
            passConstants: constants,
            outputLayout: GpuImageLayout.ShaderReadOnly,
            positionVertexPasses: new HashSet<string>(comparer: StringComparer.Ordinal) { m_manifest.Name }
        );
    }
    private void FillStaticConstants() {
        if (m_layout is not { } layout) {
            return;
        }

        foreach (var slot in layout.Slots) {
            var destination = m_constants.AsSpan(
                ((int)slot.Offset),
                ((int)slot.Type.SizeBytes())
            );

            switch (slot.Kind) {
                case ShaderPushConstantSourceKind.Config: m_config[slot.ConfigField!].Bytes.Span.CopyTo(destination: destination); break;
                case ShaderPushConstantSourceKind.Resolution:
                    if (slot.Type == ShaderValueType.Float2) { BinaryPrimitives.WriteSingleLittleEndian(
                        destination: destination,
                        value: m_width
                    ); BinaryPrimitives.WriteSingleLittleEndian(
                        destination: destination[4..],
                        value: m_height
                    ); } else { BinaryPrimitives.WriteUInt32LittleEndian(
                        destination: destination,
                        value: m_width
                    ); BinaryPrimitives.WriteUInt32LittleEndian(
                        destination: destination[4..],
                        value: m_height
                    ); }
                    break;
            }
        }
    }
    private static GpuPixelFormat ToGpuFormat(SurfaceFormat format) {
        return format switch {
            SurfaceFormat.R8G8B8A8Unorm => GpuPixelFormat.R8G8B8A8Unorm,
            SurfaceFormat.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8Unorm,
            _ => throw new InvalidDataException(message: $"Fullscreen input surface format '{format}' is unsupported."),
        };
    }

    /// <summary>Releases the active executor and inner node.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_capture.Refuse(error: new ObjectDisposedException(objectName: nameof(FullscreenPassNode)));
        try { m_executor?.Dispose(); } finally { m_inner.Dispose(); }
    }
    /// <summary>Forwards device loss to the active executor and inner node.</summary>
    public void OnDeviceLost() { m_executor?.OnDeviceLost(); m_inner.OnDeviceLost(); }
    /// <summary>Produces one adapted frame from the inner node.</summary>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        var surface = m_inner.ProduceFrame(context: context);

        if (
            surface.IsEmpty ||
            (surface.ImageViewHandle == 0)
        ) {
            m_capture.Forward(target: (m_inner as ICaptureRequestTarget));
            return surface;
        }
        var format = ToGpuFormat(format: surface.Format);
        var executor = m_executor;

        if (
            (executor is null) ||
            (m_inputFormat != format) ||
            (m_inputWidth != surface.Width) ||
            (m_inputHeight != surface.Height)
        ) {
            executor?.Dispose();
            executor = m_executor = CreateExecutor(
                (m_compute ?? throw new InvalidOperationException(message: "Fullscreen pass services do not provide compute services.")),
                format,
                surface.Width,
                surface.Height
            );
            m_inputFormat = format;
            m_inputWidth = surface.Width;
            m_inputHeight = surface.Height;
        }
        executor.BindImage(
            "input",
            new ShaderPipelineExternalImage(
                surface.ImageHandle,
                surface.ImageViewHandle,
                surface.Width,
                surface.Height,
                format,
                GpuImageLayout.ShaderReadOnly
            )
        );
        m_capture.Forward(target: executor);
        return executor.ProduceFrame(context: context);
    }
    /// <summary>Arms a capture that is forwarded to the current executor when available.</summary>
    public void RequestCapture(FrameCaptureRequest request) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        m_capture.Arm(
            request,
            PendingCapturePath
        );
    }
    /// <summary>Updates a declared floating-point manifest parameter.</summary>
    public bool TrySetConfig(string field, float value) {
        if (
            (m_manifest.Config is not { } schema) ||
            !schema.TryGetValue(
            key: field,
            value: out var declared
        ) ||
            (declared.Type != ShaderValueType.Float) ||
            !float.IsFinite(f: value) ||
            !ShaderConfigBinding.InRange(
            field: declared,
            value: value
        )
        ) {
            return false;
        }

        if (m_liveConfig is not { } live) {
            live = new Dictionary<string, ShaderConfigValue>(comparer: StringComparer.Ordinal);
            foreach (var name in m_config.Names) {
                live[name] = m_config[name];
            }

            m_liveConfig = live; m_liveConfigBytes = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal); m_config = new ShaderConfigValues(values: live);
        }
        if (!m_liveConfigBytes!.TryGetValue(
            key: field,
            value: out var bytes
        )) {
            bytes = new byte[ShaderValueTypes.ComponentBytes]; m_liveConfigBytes[field] = bytes; live[field] = new ShaderConfigValue(
                Bytes: bytes,
                Type: ShaderValueType.Float
            );
        }
        BinaryPrimitives.WriteSingleLittleEndian(
            destination: bytes,
            value: value
        );
        if (m_layout is { } layout) {
            foreach (var slot in layout.Slots.Where(predicate: slot => ((slot.Kind == ShaderPushConstantSourceKind.Config) && (slot.ConfigField == field)))) {
                bytes.CopyTo(destination: m_constants.AsSpan(start: ((int)slot.Offset)));
            }
        }

        return true;
    }

    private sealed class ManifestPassConstants(ShaderPushConstantLayout? layout, Func<ShaderConfigValues> config, byte[] constants) : IShaderPipelinePassConstants {
        public uint SizeBytes => ((uint)constants.Length);
        public GpuShaderStage Stages => (layout?.Stages ?? GpuShaderStage.None);

        public void Write(in FrameContext context, in ShaderFrameInput input, uint passWidth, uint passHeight, ulong frameCounter, Span<byte> destination) {
            constants.AsSpan().CopyTo(destination: destination);
            if (layout is not { } resolved) {
                return;
            }

            foreach (var slot in resolved.Slots) {
                var target = destination[((int)slot.Offset)..];

                switch (slot.Kind) {
                    case ShaderPushConstantSourceKind.Tick:
                        var period = ((slot.QuantizeHzLiteral is { } literal)
                            ? EngineTicks.PerRate(ratePerSecond: literal)
                            : ((slot.QuantizeHzConfigField is { } field)
                                ? EngineTicks.PerRate(ratePerSecond: config()[field].ComponentBits(index: 0))
                                : 1
                        ));
                        var value = (context.ElapsedTicks / period);
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            destination: target,
                            value: ((uint)value)
                        );
                        if (slot.Type == ShaderValueType.Uint2) {
                            BinaryPrimitives.WriteUInt32LittleEndian(
                                destination: target[4..],
                                value: ((uint)(value >> 32))
                            );
                        }

                        break;
                    case ShaderPushConstantSourceKind.Frame: BinaryPrimitives.WriteUInt32LittleEndian(
                        destination: target,
                        value: ((uint)frameCounter)
                    ); break;
                }
            }
        }
    }
}
