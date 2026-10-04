using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the shipped composite (<c>sdf-composite.comp</c>) counts the atmosphere's evaluations in its
/// <c>atmosphere</c> detail row, one <c>gpu.sky.evaluations</c> for each kind whose in-scatter it evaluates at a pixel, and
/// none at a covered pixel of an atmosphere that authors no kind: the atmosphere's off-switch costs nothing. Over a 16x8
/// lit image every pixel of which is covered, its surface twenty units along a camera looking down through the air into
/// water, the composite counts no atmosphere evaluation with every kind off, 128 with the fog alone and 384 with the fog,
/// the haze and the medium, and no sky layer evaluation in any of them. Every binding the pass does not read holds a filler
/// of its kind.
/// </summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfCompositeAtmosphereDeviceLawTests {
    private const uint Width = 16;
    private const uint Height = 8;
    // The pass, its plain detail, then gradient, disc, stars, clouds and atmosphere.
    private const int Rows = 7;
    private const int AtmosphereRow = 6;
    private const float SurfaceDistance = 20f;

    [Fact]
    public void VulkanCountsTheAtmosphereOnlyWhereItsKindsAreAuthored() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfCompositeAtmosphereDeviceLawTests));

        Verify(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXCountsTheAtmosphereOnlyWhereItsKindsAreAuthored() {
        using var device = DirectXTestDevices.Hardware();

        Verify(services: device.Services, extension: ".dxil");
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        var lights = new SdfLights { Count = 1 };

        lights.Set(index: 0, light: new SdfLight(Color: Vector3.One, Direction: new Vector3(x: 0f, y: 0.1f, z: -1f), Kind: SdfLightKind.Directional, Param: 0f, Shadows: false, Weight: 1f));
        var fog = (SdfAtmosphere.None with { FogDensity = 0.05f });
        var every = (fog with { HazeAmount = 0.3f, MediumColor = new Vector3(x: 0f, y: 0.2f, z: 0.3f), MediumExtinction = 0.4f, MediumSurface = 0f });

        Assert.Equal(expected: (Atmosphere: 0L, Layers: 0L), actual: Run(atmosphere: SdfAtmosphere.None, extension: extension, lights: lights, services: services));
        Assert.Equal(expected: (Atmosphere: 128L, Layers: 0L), actual: Run(atmosphere: fog, extension: extension, lights: lights, services: services));
        Assert.Equal(expected: (Atmosphere: 384L, Layers: 0L), actual: Run(atmosphere: every, extension: extension, lights: lights, services: services));
    }
    // Runs the composite once over a wholly covered lit image whose every pixel's surface lies SurfaceDistance along its
    // ray, through the atmosphere, and returns its counted atmosphere evaluations and its sky layers' evaluations.
    private static (long Atmosphere, long Layers) Run(GpuDeviceServices services, string extension, SdfAtmosphere atmosphere, SdfLights lights) {
        var parameters = SdfWorldInterfaces.SkyParameters;
        var block = new byte[parameters.SizeBytes];

        parameters.WriteExtent(block: block, height: Height, width: Width);
        void Word(string member, uint value, int lane = 0) => BinaryPrimitives.WriteUInt32LittleEndian(
            destination: block.AsSpan(start: (((int)parameters.BlockOffsetOf(member: member)) + (lane * 4))), value: value);
        void Float(string member, float value, int lane = 0) => Word(lane: lane, member: member, value: BitConverter.SingleToUInt32Bits(value: value));
        void Vector(string member, Vector3 value) {
            Float(lane: 0, member: member, value: value.X);
            Float(lane: 1, member: member, value: value.Y);
            Float(lane: 2, member: member, value: value.Z);
        }
        // A camera two units above the water's surface, looking down at 45 degrees with a narrow field of view, so every
        // pixel's ray crosses the surface before its own surface twenty units away.
        var forward = Vector3.Normalize(value: new Vector3(x: 0f, y: -1f, z: -1f));
        var up = Vector3.Normalize(value: new Vector3(x: 0f, y: 1f, z: -1f));

        Word(member: SdfWorldPackage.ImageExtent, value: Width);
        Word(lane: 1, member: SdfWorldPackage.ImageExtent, value: Height);
        Word(member: SdfWorldPackage.ResolvedSurface, value: 1u);
        Word(member: ShaderWorkCounters.DetailRow, value: 2u);
        Vector(member: SdfWorldPackage.ViewPosition, value: new Vector3(x: 0f, y: 2f, z: 0f));
        Vector(member: SdfWorldPackage.ViewRight, value: Vector3.UnitX);
        Vector(member: SdfWorldPackage.ViewUp, value: up);
        Vector(member: SdfWorldPackage.ViewForward, value: forward);
        Float(member: SdfWorldPackage.TanHalfFieldOfView, value: 0.1f);
        Float(member: SdfWorldPackage.AspectRatio, value: (Width / ((float)Height)));
        Float(member: SdfWorldPackage.FarDistance, value: SdfFrame.DefaultFarDistance);

        var sky = default(SdfSkyBlock);

        SdfSky.PackAtmosphere(atmosphere: in atmosphere, block: ref sky, farDistance: SdfFrame.DefaultFarDistance, lights: lights);
        var skyBytes = MemoryMarshal.AsBytes(span: new[] { sky }.AsSpan()).ToArray();
        // Every pixel one render sample copied whole: its surface's ray distance under the sample bit.
        var transport = new byte[((Width * Height) * 8)];

        for (var pixel = 0; (pixel < (Width * Height)); pixel++) {
            BinaryPrimitives.WriteUInt32LittleEndian(destination: transport.AsSpan(start: (pixel * 8)), value: SdfSurfaceTransport.SampleWord(distance: SurfaceDistance).Low);
        }
        var lit = new byte[((Width * Height) * 4)];

        for (var pixel = 0; (pixel < (Width * Height)); pixel++) {
            lit[((pixel * 4) + 3)] = 255;
        }
        var layout = parameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute);
        var description = new GpuComputePipelineDescription(Bindings: [], Layout: layout, Name: "sdf-composite-atmosphere-proof", PushConstantBinding: null);
        using var module = services.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(path: Path.Combine(path1: SdfKernelSet.DefaultDirectory, path2: ("sdf-composite.comp" + extension))));
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        var padded = new byte[((((((ulong)block.Length) + IGpuBindings.ConstantBufferAlignment) - 1UL) / IGpuBindings.ConstantBufferAlignment) * IGpuBindings.ConstantBufferAlignment)];

        block.CopyTo(array: padded, index: 0);
        using var constants = services.BufferFactory.CreateHostVisible(data: padded, name: default, usage: GpuBufferUsage.Uniform);
        using var fillerBlock = services.BufferFactory.CreateHostVisible(data: new byte[padded.Length], name: default, usage: GpuBufferUsage.Uniform);
        using var fillerBuffer = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: 4096, usage: GpuBufferUsage.Storage);
        using var skyBuffer = services.BufferFactory.CreateHostVisible(data: skyBytes, name: default, usage: GpuBufferUsage.Storage);
        using var transportBuffer = services.BufferFactory.CreateHostVisible(data: transport, name: default, usage: GpuBufferUsage.Storage);
        using var fillerStorage = services.ImageFactory.Create(format: GpuPixelFormat.R16G16B16A16Float, height: 1, name: default, usage: GpuImageUsage.Storage, width: 1);
        using var output = services.ImageFactory.Create(format: RenderGraphPackageCatalog.WorkingFormat, height: Height, name: default, usage: GpuImageUsage.Storage, width: Width);
        using var counters = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: ((ulong)(Rows * GpuKernelCounters.RowBytes)), usage: GpuBufferUsage.Storage);
        using var counted = services.BufferFactory.CreateReadback(name: default, sizeBytes: ((ulong)(Rows * GpuKernelCounters.RowBytes)));
        using var upload = services.SurfaceTransferFactory.CreateUpload();
        var litView = upload.Upload(pixels: lit, format: GpuPixelFormat.R8G8B8A8Unorm, width: Width, height: Height);
        using var fillerUpload = services.SurfaceTransferFactory.CreateUpload();
        var fillerSampled = fillerUpload.Upload(pixels: new byte[4], format: GpuPixelFormat.R8G8B8A8Unorm, width: 1, height: 1);
        var sampler = services.Bindings.CreateSampler();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var pool = services.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(groups: layout.Groups));

        try {
            (uint Set, uint Binding) At(string member) {
                foreach (var group in parameters.Layout.Groups) {
                    foreach (var resource in group.Resources) {
                        if (resource.Member.Name == member) {
                            return (group.Set, resource.Binding);
                        }
                    }
                }

                throw new InvalidOperationException(message: member);
            }
            var images = new Dictionary<(uint, uint), nint> {
                [At(member: SdfWorldPackage.LitImage)] = litView,
                [At(member: SdfWorldPackage.Output)] = output.ImageViewHandle,
            };
            var buffers = new Dictionary<(uint, uint), IGpuBuffer> {
                [At(member: ShaderWorkCounters.Buffer)] = counters,
                [At(member: SdfKernelInterfaces.Sky)] = skyBuffer,
                [At(member: SdfWorldPackage.TransportRead)] = transportBuffer,
            };
            var sets = new List<(uint Group, nint Set)>();

            foreach (var group in parameters.Layout.Groups) {
                var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[((int)group.Set)], name: default, poolHandle: pool);
                var pass = (group.Group == ShaderInterfaceGroup.Pass);

                sets.Add(item: (group.Set, set));
                foreach (var binding in group.Bindings.Where(predicate: static binding => !binding.Pushed)) {
                    var key = (group.Set, binding.Binding);

                    for (var element = 0u; (element < binding.Count); element++) {
                        switch (binding.Kind) {
                            case GpuBindingKind.ConstantBuffer:
                                var constantBuffer = (pass ? constants : fillerBlock);

                                services.Bindings.WriteConstantBuffer(descriptorSetHandle: set, binding: binding.Binding, arrayElement: element, bufferHandle: constantBuffer.BufferHandle, bufferSize: constantBuffer.SizeBytes);
                                break;
                            case GpuBindingKind.SampledImage:
                                services.Bindings.WriteSampledImage(descriptorSetHandle: set, binding: binding.Binding, arrayElement: element,
                                    imageViewHandle: (images.TryGetValue(key: key, value: out var view) ? view : fillerSampled));
                                break;
                            case GpuBindingKind.StorageImage:
                                services.Bindings.WriteStorageImage(descriptorSetHandle: set, binding: binding.Binding, arrayElement: element,
                                    imageViewHandle: (images.TryGetValue(key: key, value: out var image) ? image : fillerStorage.ImageViewHandle));
                                break;
                            case GpuBindingKind.Sampler:
                                services.Bindings.WriteSampler(descriptorSetHandle: set, binding: binding.Binding, arrayElement: element, samplerHandle: sampler);
                                break;
                            default:
                                var storage = (buffers.TryGetValue(key: key, value: out var bound) ? bound : fillerBuffer);

                                services.Bindings.WriteBuffer(descriptorSetHandle: set, binding: binding.Binding, bufferHandle: storage.BufferHandle,
                                    bufferSize: storage.SizeBytes, kind: binding.Kind, elementStride: binding.ElementStride);
                                break;
                        }
                    }
                }
            }
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            foreach (var image in new[] { output, fillerStorage }) {
                recorder.TransitionImageLayout(commandBufferHandle: command, imageHandle: image.ImageHandle,
                    sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, oldLayout: GpuImageLayout.Undefined,
                    destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader, newLayout: GpuImageLayout.General);
            }
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
            recorder.ClearStorageBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle, sizeBytes: counters.SizeBytes);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer, destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            foreach (var (group, set) in sets) {
                recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: group, pipelineLayoutHandle: pipeline.LayoutHandle);
            }
            recorder.Dispatch(commandBufferHandle: command, groupCountX: ((Width + 7) / 8), groupCountY: ((Height + 7) / 8), groupCountZ: 1);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader, destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
            recorder.CopyBuffer(commandBufferHandle: command, destinationBufferHandle: counted.BufferHandle, sizeBytes: counters.SizeBytes, sourceBufferHandle: counters.BufferHandle);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counted.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer, destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            var words = new byte[(Rows * GpuKernelCounters.RowBytes)];

            counted.Read(destination: words);
            long Count(int row, int kind) => BinaryPrimitives.ReadInt64LittleEndian(source: words.AsSpan(start: ((row * GpuKernelCounters.RowBytes) + ((kind * GpuKernelCounters.CountWords) * sizeof(uint)))));
            var layers = Count(kind: 2, row: 0);

            for (var row = 2; (row < AtmosphereRow); row++) { layers += Count(kind: 2, row: row); }

            return (Atmosphere: Count(kind: 2, row: AtmosphereRow), Layers: layers);
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
            services.Bindings.DestroySampler(samplerHandle: sampler);
        }
    }
}
