using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.DirectX;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The device half of a desktop capture's zero-copy HDR route. A half-float scRGB image on the render device, holding
/// values below black, through SDR white and well above it, is converted on the device by the image conversion
/// (<see cref="RenderGraphRuntime.CreateImageConverter"/>, the <c>source-scrgb</c> kernel) at a paper white of
/// <see cref="PaperWhite"/> cd/m², and every texel the conversion writes, read back from the device, must be what
/// <see cref="ImageSourceConversion.ToWorking(ImageColorEncoding, double, double, double, double)"/> computes from the texel the image held, within half-float precision,
/// with alpha passed through exactly. On Direct3D 12 the image converted is a simultaneous-access shared target, the kind
/// the platform's capture copies into, resting in <see cref="GpuImageLayout.External"/> as the copy leaves it, with the
/// debug layer on and no <c>[d3d12-debug]</c> line; Vulkan and the software (WARP) renderer convert a device image the
/// fill kernel (<c>Assets/Shaders/scrgb-fill.comp.hlsl</c>) wrote. It skips by name where the host has no such device,
/// and runs alone, because the debug layer removes every device the process already holds.
/// </summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class ImportedImageConversionDeviceLawTests {
    private const uint Extent = 16;
    private const string FillKernel = "scrgb-fill.comp";
    // The fill kernel's one group, set 3, and its bindings, each register at its binding in the group's space.
    private const uint Group = 3U;
    private const uint OutputBinding = 1U;
    private const double PaperWhite = 203.0;
    private const uint ValuesBinding = 0U;

    [Fact]
    public void AnImportedScRgbImageConvertsOnAVulkanDeviceAsTheReferenceDoes() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(ImportedImageConversionDeviceLawTests));

        Convert(
            backend: $"vulkan ({device.Name})",
            device: device,
            export: null,
            hostsOnDirectX: false
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AnImportedScRgbImageConvertsOnADirect3D12DeviceAsTheReferenceDoes(bool warp) {
        var output = new StringWriter();
        var context = (warp ? DirectXTestDevices.Warp() : DirectXTestDevices.Debug(output: output));

        try {
            Convert(
                backend: (warp ? "directx (WARP)" : "directx"),
                device: context,
                export: (warp ? null : new DirectXGpuSurfaceExportFactory(deviceContext: context)),
                hostsOnDirectX: true
            );
            context.DrainDebugMessages();
        } finally {
            context.Dispose();
        }

        Assert.DoesNotContain(
            actualString: output.ToString(),
            expectedSubstring: "[d3d12-debug]"
        );
    }

    // The scRGB values the image holds, texel by texel: red from below black to past seven times SDR white, green to
    // twice it, blue in sixteen steps to four times it, and alpha from zero to nearly one.
    private static float[] Values() {
        var values = new float[checked(((int)((Extent * Extent) * 4U)))];

        for (var y = 0U; (y < Extent); y++) {
            for (var x = 0U; (x < Extent); x++) {
                var texel = ((int)(((y * Extent) + x) * 4U));

                values[texel] = ((((x + 1U) * 0.5f) - 0.75f));
                values[(texel + 1)] = (((y + 1U) * 2f) / Extent);
                values[(texel + 2)] = ((((x * 7U) + (y * 3U)) % 16U) / 4f);
                values[(texel + 3)] = ((x + y) / (2f * Extent));
            }
        }

        return values;
    }
    private static byte[] Kernel(bool hostsOnDirectX) =>
        File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (FillKernel + ShaderBytecode.FileExtension(hostsOnDirectX: hostsOnDirectX))));
    // Writes the values into a half-float image on the device, copies it into the shared target when one is given, and
    // returns the image to convert, in the layout it rests in, with the half floats it holds.
    private static (ShaderPipelineExternalImage Image, Half[] Stored) Fill(GpuDeviceServices services, bool hostsOnDirectX, IGpuImage filled, IGpuExportableImage? shared, IGpuSurfaceReadback readback) {
        var bindings = services.Bindings;
        var recorder = services.Recorder;
        var description = new GpuComputePipelineDescription(
            Bindings: [],
            Layout: new GpuPipelineLayoutDescription(
                groups: [new GpuGroupLayoutDescription(
                    bindings: [
                        new GpuGroupBinding(binding: ValuesBinding, kind: GpuBindingKind.ReadOnlyBuffer),
                        new GpuGroupBinding(binding: OutputBinding, kind: GpuBindingKind.StorageImage),
                    ],
                    ordinal: Group
                )],
                pushesIndex: false,
                stages: GpuShaderStage.Compute
            ),
            Name: FillKernel,
            PushConstantBinding: null
        );
        using var module = services.ShaderModuleFactory.Create(
            bytecode: Kernel(hostsOnDirectX: hostsOnDirectX),
            stage: GpuShaderStage.Compute
        );
        using var pipeline = services.PipelineFactory.Create(
            computeShaderModule: module,
            description: description,
            name: default
        );
        using var commands = services.CommandPoolFactory.Create(name: default);
        using var values = services.BufferFactory.CreateHostVisible(
            data: MemoryMarshal.AsBytes(span: Values().AsSpan()),
            name: default,
            usage: GpuBufferUsage.Storage
        );
        var pool = bindings.CreatePool(
            name: default,
            sizes: GpuDescriptorPoolSizes.ForGroups(groups: description.Layout!.Groups)
        );

        try {
            var set = bindings.AllocateSet(
                descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[((int)Group)],
                name: default,
                poolHandle: pool
            );
            var command = commands.CommandBufferHandle;

            bindings.WriteBuffer(
                binding: ValuesBinding,
                bufferHandle: values.BufferHandle,
                bufferSize: values.SizeBytes,
                descriptorSetHandle: set,
                elementStride: (4U * sizeof(float)),
                kind: GpuBindingKind.ReadOnlyBuffer
            );
            bindings.WriteStorageImage(
                arrayElement: 0U,
                binding: OutputBinding,
                descriptorSetHandle: set,
                imageViewHandle: filled.ImageViewHandle
            );
            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.TransitionImageLayout(
                commandBufferHandle: command,
                destinationAccessMask: GpuAccess.ShaderWrite,
                destinationStageMask: GpuStage.ComputeShader,
                imageHandle: filled.ImageHandle,
                newLayout: GpuImageLayout.General,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuAccess.None,
                sourceStageMask: GpuStage.TopOfPipe
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: command,
                pipelineHandle: pipeline.Handle
            );
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: command,
                descriptorSetHandle: set,
                group: Group,
                pipelineLayoutHandle: pipeline.LayoutHandle
            );
            recorder.Dispatch(
                commandBufferHandle: command,
                groupCountX: (Extent / 8U),
                groupCountY: (Extent / 8U),
                groupCountZ: 1U
            );

            if (shared is not null) {
                // The platform's copy into a shared target: the target leaves the copy in the layout a foreign device
                // shares it in, which is where the conversion finds it.
                recorder.TransitionImageLayout(
                    commandBufferHandle: command,
                    destinationAccessMask: GpuAccess.TransferRead,
                    destinationStageMask: GpuStage.Transfer,
                    imageHandle: filled.ImageHandle,
                    newLayout: GpuImageLayout.TransferSource,
                    oldLayout: GpuImageLayout.General,
                    sourceAccessMask: GpuAccess.ShaderWrite,
                    sourceStageMask: GpuStage.ComputeShader
                );
                recorder.TransitionImageLayout(
                    commandBufferHandle: command,
                    destinationAccessMask: GpuAccess.TransferWrite,
                    destinationStageMask: GpuStage.Transfer,
                    imageHandle: shared.ImageHandle,
                    newLayout: GpuImageLayout.TransferDestination,
                    oldLayout: GpuImageLayout.External,
                    sourceAccessMask: GpuAccess.None,
                    sourceStageMask: GpuStage.TopOfPipe
                );
                recorder.CopyImage(
                    commandBufferHandle: command,
                    destinationImageHandle: shared.ImageHandle,
                    height: Extent,
                    sourceImageHandle: filled.ImageHandle,
                    width: Extent
                );
                recorder.TransitionImageLayout(
                    commandBufferHandle: command,
                    destinationAccessMask: GpuAccess.None,
                    destinationStageMask: GpuStage.TopOfPipe,
                    imageHandle: shared.ImageHandle,
                    newLayout: GpuImageLayout.External,
                    oldLayout: GpuImageLayout.TransferDestination,
                    sourceAccessMask: GpuAccess.TransferWrite,
                    sourceStageMask: GpuStage.Transfer
                );
                recorder.TransitionImageLayout(
                    commandBufferHandle: command,
                    destinationAccessMask: GpuAccess.ShaderWrite,
                    destinationStageMask: GpuStage.ComputeShader,
                    imageHandle: filled.ImageHandle,
                    newLayout: GpuImageLayout.General,
                    oldLayout: GpuImageLayout.TransferSource,
                    sourceAccessMask: GpuAccess.TransferRead,
                    sourceStageMask: GpuStage.Transfer
                );
            }

            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
        } finally {
            bindings.DestroyPool(poolHandle: pool);
        }

        var stored = MemoryMarshal.Cast<byte, Half>(span: readback.Read(
            bytesPerPixel: 8U,
            format: GpuPixelFormat.R16G16B16A16Float,
            height: Extent,
            sourceImageHandle: filled.ImageHandle,
            sourceLayout: GpuImageLayout.General,
            width: Extent
        ).Span).ToArray();
        var image = ((shared is null)
            ? new ShaderPipelineExternalImage(
                Format: filled.Format,
                Height: Extent,
                ImageHandle: filled.ImageHandle,
                ImageViewHandle: filled.ImageViewHandle,
                Layout: GpuImageLayout.General,
                Width: Extent
            )
            : new ShaderPipelineExternalImage(
                Format: shared.Format,
                Height: Extent,
                ImageHandle: shared.ImageHandle,
                ImageViewHandle: shared.ImageViewHandle,
                Layout: GpuImageLayout.External,
                Width: Extent
            ));

        return (image, stored);
    }
    private static void Convert(string backend, IGpuDeviceContext device, DirectXGpuSurfaceExportFactory? export, bool hostsOnDirectX) {
        var services = device.Services;
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var filled = services.ImageFactory.Create(
            format: GpuPixelFormat.R16G16B16A16Float,
            height: Extent,
            name: default,
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
            width: Extent
        );
        using var shared = export?.CreateSimultaneousAccessImage(
            format: GpuPixelFormat.R16G16B16A16Float,
            height: Extent,
            width: Extent
        );

        var (image, stored) = Fill(
            filled: filled,
            hostsOnDirectX: hostsOnDirectX,
            readback: readback,
            services: services,
            shared: shared
        );
        var pipelines = new GpuPassPipelineCache();
        var packages = new RenderGraphPackageRecorders(regionCopy: new GpuRegionCopyPass(
            bytecodeExtension: ShaderBytecode.FileExtension(hostsOnDirectX: hostsOnDirectX),
            pipelines: pipelines
        ));

        SourceConversionPackage.RegisterAll(
            packages: packages,
            paperWhiteNits: PaperWhite
        );
        packages.RegisterSource(
            factory: _ => new IdleUpload(),
            package: (RenderGraphInstance.SourcePackagePrefix + IdleUpload.Producer)
        );
        Assert.True(
            condition: RenderGraphInstanceSet.TryCreate(
                instances: [RenderGraphInstance.Source(name: "idle", producer: IdleUpload.Producer)],
                refusal: out var setRefusal,
                set: out var set
            ),
            userMessage: setRefusal?.Message
        );
        Assert.True(
            condition: RenderGraphRuntime.TryCreate(
                deviceContext: device,
                graphs: new RenderGraphRuntimeGraph?[1],
                hostsOnDirectX: hostsOnDirectX,
                packages: packages,
                pipelines: pipelines,
                refusal: out var refusal,
                root: "idle",
                runtime: out var runtime,
                set: set
            ),
            userMessage: refusal?.Message
        );

        Half[] converted;

        using (runtime) {
            using var converter = runtime.CreateImageConverter(
                descriptor: new ImageSourceDescriptor(
                    Cadence: ImageSourceCadence.Tick,
                    Color: ImageColorEncoding.Of(colorSpace: DisplayColorSpace.ScRgb),
                    Content: ImageContentClass.External,
                    Format: ImagePixelFormat.R16G16B16A16Float,
                    Height: Extent,
                    Producer: "capture",
                    Transport: ImageSourceTransport.Imported,
                    Width: Extent
                ),
                name: "capture:hdr"
            );

            Assert.Null(@object: converter.Fault);
            // The conversion's pipeline builds on the thread pool, so conversions are asked for until one submits.
            TestLiveness.Until(
                reason: () => $"{backend}: the imported image never converted: {converter.Render.Reason}",
                step: () => converter.TryConvert(
                    context: default,
                    image: image,
                    lease: new GpuImageLease(ImageViewHandle: image.ImageViewHandle)
                )
            );
            converted = MemoryMarshal.Cast<byte, Half>(span: readback.Read(
                bytesPerPixel: 8U,
                format: GpuPixelFormat.R16G16B16A16Float,
                height: Extent,
                sourceImageHandle: converter.Output.ImageHandle,
                sourceLayout: converter.OutputLayout,
                width: Extent
            ).Span).ToArray();
        }

        var color = ImageColorEncoding.Of(colorSpace: DisplayColorSpace.ScRgb);
        var failures = new List<string>();

        for (var texel = 0; (texel < (stored.Length / 4)); texel++) {
            var at = (texel * 4);

            var (r, g, b) = ImageSourceConversion.ToWorking(
                b: ((double)stored[(at + 2)]),
                color: color,
                g: ((double)stored[(at + 1)]),
                paperWhiteNits: PaperWhite,
                r: ((double)stored[at])
            );
            double[] expected = [r, g, b, ((double)stored[(at + 3)])];

            for (var channel = 0; (channel < 4); channel++) {
                var actual = ((double)converted[(at + channel)]);
                var tolerance = ((channel == 3)
                    ? 0.0
                    : (0.002 + (0.002 * Math.Abs(value: expected[channel]))));

                if (Math.Abs(value: (actual - expected[channel])) > tolerance) {
                    failures.Add(item: $"{backend}: texel {texel} channel {channel} held {((double)stored[(at + channel)])}, converted to {actual}, expected {expected[channel]}");
                }
            }
        }

        Assert.True(
            condition: (failures.Count == 0),
            userMessage: string.Join(separator: Environment.NewLine, values: failures.Take(count: 16))
        );
    }

    // The runtime's one instance, a source that converts nothing: the image converter runs beside the set on the runtime's
    // device, packages and pipeline cache, as a capture's does.
    private sealed class IdleUpload : IRenderGraphSourceUpload {
        public const string Producer = "idle";

        public ImageSourceDescriptor? Descriptor { get; } = new ImageSourceDescriptor(
            Cadence: ImageSourceCadence.Static,
            Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.Deterministic,
            Format: ImagePixelFormat.B8G8R8A8Unorm,
            Height: 1U,
            Producer: Producer,
            Transport: ImageSourceTransport.Uploaded,
            Width: 1U
        );
        public string? Fault => null;

        public void Dispose() { }
        public FrameRender Write(long tick, GpuRegion region) => FrameRender.Rendered;
    }
}
