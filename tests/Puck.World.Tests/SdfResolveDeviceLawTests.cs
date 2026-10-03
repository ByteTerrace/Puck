using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The shipped spatial resolve preserves the analytic step colors, and copies a grid of its output's own
/// extent exactly. These inputs isolate the resolver from traversal: the dispatch box covers the whole grid, every
/// visibility record is a miss, and every binding the spatial path does not read holds a filler of its kind, while the
/// reduced-world canary exercises its ordinary package recorder.</summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfResolveDeviceLawTests {
    private const uint RenderWidth = 16;
    private const uint RenderHeight = 48;
    private const uint OutputWidth = 64;
    private const uint OutputHeight = 192;

    [Fact]
    public void VulkanResolvesTheAnalyticColors() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfResolveDeviceLawTests));

        Verify(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXResolvesTheAnalyticColors() {
        using var device = DirectXTestDevices.Hardware();

        Verify(services: device.Services, extension: ".dxil");
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        foreach (var sharpness in new[] { 0f, 1f }) {
            var color = Run(extension: extension, height: OutputHeight, services: services,
                sharpness: sharpness, width: OutputWidth);
            // Hand-derived values at fractions 1/8 and 7/8; the source stores its middle band as codes 64 and 191.
            Check(color: color, value: ((sharpness == 0f) ? 0.125f : 0.083984375f), width: OutputWidth, x: 30, y: 20);
            Check(color: color, value: ((sharpness == 0f) ? 0.875f : 0.916015625f), width: OutputWidth, x: 33, y: 20);
            Check(color: color, value: ((sharpness == 0f) ? 0.3132353f : 0.2928079f), width: OutputWidth, x: 30, y: 90);
            Check(color: color, value: (64f / 255f), width: OutputWidth, x: 29, y: 90);
            Check(color: color, value: ((sharpness == 0f) ? 0.3132353f : 0.2928079f), width: OutputWidth, x: 2, y: 150);
        }
        var native = Run(extension: extension, height: RenderHeight, services: services,
            sharpness: 1f, width: RenderWidth);

        Check(color: native, value: 0f, width: RenderWidth, x: 7, y: 8);
        Check(color: native, value: 1f, width: RenderWidth, x: 8, y: 8);

        foreach (var poison in new[] { Half.NaN, Half.PositiveInfinity, Half.NegativeInfinity }) {
            var recovered = Run(extension: extension, height: RenderHeight, services: services,
                sharpness: 1f, width: RenderWidth, poisonedHistory: poison);

            Assert.Equal(actual: recovered, expected: native);
        }
        var history = Run(extension: extension, height: RenderHeight, services: services,
            sharpness: 1f, width: RenderWidth, poisonCurrent: true);

        Assert.All(collection: history, action: value => Assert.True(condition: Half.IsFinite(value: value)));
        for (var pixel = 0; (pixel < (history.Length / 4)); pixel++) {
            Assert.Equal(expected: ((Half)0f), actual: history[((pixel * 4) + 3)]);
        }
    }
    private static void Check(Half[] color, uint width, uint x, uint y, float value) {
        var offset = ((int)(((y * width) + x) * 4));

        for (var channel = 0; (channel < 3); channel++) {
            Assert.InRange(actual: MathF.Abs(x: (((float)color[(offset + channel)]) - value)), low: 0f, high: 0.0005f);
        }
        Assert.Equal(expected: ((Half)1f), actual: color[(offset + 3)]);
    }
    private static Half[] Run(GpuDeviceServices services, string extension, float sharpness, uint width, uint height, Half? poisonedHistory = null, bool poisonCurrent = false) {
        var temporal = (poisonedHistory.HasValue || poisonCurrent);
        var parameters = SdfWorldInterfaces.ResolveParameters;
        var block = new byte[parameters.SizeBytes];

        parameters.WriteExtent(block: block, height: height, width: width);
        void Word(string member, uint value, int lane = 0) => BinaryPrimitives.WriteUInt32LittleEndian(
            destination: block.AsSpan(start: (((int)parameters.BlockOffsetOf(member: member)) + (lane * 4))), value: value);
        Word(member: SdfWorldPackage.ImageExtent, value: RenderWidth);
        Word(lane: 1, member: SdfWorldPackage.ImageExtent, value: RenderHeight);
        Word(member: SdfWorldPackage.UpscaleSharpness, value: BitConverter.SingleToUInt32Bits(value: sharpness));
        if (temporal) {
            void Float(string member, float value, int lane = 0) => Word(member: member, value: BitConverter.SingleToUInt32Bits(value: value), lane: lane);

            Word(member: SdfWorldPackage.Temporal, value: 1u);
            Word(member: SdfWorldPackage.HistoryFrames, value: 1u);
            Float(member: SdfWorldPackage.ViewRight, value: 1f);
            Float(lane: 1, member: SdfWorldPackage.ViewUp, value: 1f);
            Float(lane: 2, member: SdfWorldPackage.ViewForward, value: 1f);
            Float(member: SdfWorldPackage.TanHalfFieldOfView, value: 0.5f);
            Float(member: SdfWorldPackage.AspectRatio, value: 1f);
            Float(member: SdfWorldPackage.FarDistance, value: 40f);
            Float(lane: 3, member: SdfWorldPackage.PreviousView, value: 1f);
            Float(lane: 4, member: SdfWorldPackage.PreviousView, value: 1f);
            Float(lane: 7, member: SdfWorldPackage.PreviousView, value: 0.5f);
            Float(lane: 9, member: SdfWorldPackage.PreviousView, value: 1f);
            Float(lane: 11, member: SdfWorldPackage.PreviousView, value: 1f);
            Float(lane: 14, member: SdfWorldPackage.PreviousView, value: 1f);
            Float(lane: 16, member: SdfWorldPackage.PreviousView, value: RenderWidth);
            Float(lane: 17, member: SdfWorldPackage.PreviousView, value: RenderHeight);
        }
        var source = new byte[((RenderWidth * RenderHeight) * 4)];

        for (var y = 0u; (y < RenderHeight); y++) {
            for (var x = 0u; (x < RenderWidth); x++) {
                var value = ((byte)((y < 16) ? ((x < 8) ? 0 : 255) : ((x < ((y < 32) ? 8 : 1)) ? 64 : 191)));
                var offset = (((y * RenderWidth) + x) * 4);

                source[offset] = value; source[(offset + 1)] = value; source[(offset + 2)] = value; source[(offset + 3)] = 255;
            }
        }
        var layout = parameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute);
        var description = new GpuComputePipelineDescription(Bindings: [], Layout: layout, Name: "sdf-resolve-proof", PushConstantBinding: null);
        using var module = services.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(path: Path.Combine(path1: SdfKernelSet.DefaultDirectory, path2: ("sdf-resolve.comp" + extension))));
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        var padded = new byte[((((((ulong)block.Length) + IGpuBindings.ConstantBufferAlignment) - 1UL) / IGpuBindings.ConstantBufferAlignment) * IGpuBindings.ConstantBufferAlignment)];

        block.CopyTo(array: padded, index: 0);
        using var constants = services.BufferFactory.CreateHostVisible(data: padded, name: default, usage: GpuBufferUsage.Uniform);
        using var fillerBlock = services.BufferFactory.CreateHostVisible(data: new byte[padded.Length], name: default, usage: GpuBufferUsage.Uniform);
        using var fillerBuffer = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: Math.Max(val1: 4096UL, val2: ((((ulong)(SdfWorldPackage.HistorySurfaceWords * sizeof(uint))) * width) * height)), usage: GpuBufferUsage.Storage);
        using var fillerStorage = services.ImageFactory.Create(format: GpuPixelFormat.R16G16B16A16Float, height: 1, name: default, usage: GpuImageUsage.Storage, width: 1);
        using var historyOutput = (temporal ? services.ImageFactory.Create(format: GpuPixelFormat.R16G16B16A16Float, height: height, name: default, usage: GpuImageUsage.Storage, width: width) : null);
        using var historySurface = (temporal ? services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: ((((ulong)(SdfWorldPackage.HistorySurfaceWords * sizeof(uint))) * width) * height), usage: GpuBufferUsage.Storage) : null);
        using var counters = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: ((ulong)GpuKernelCounters.RowBytes), usage: GpuBufferUsage.Storage);
        // The dispatch box, in 8x8 groups, over the whole render grid; every record a miss; one transport word an output pixel.
        var box = new byte[16];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: box.AsSpan(start: 8), value: ((RenderWidth + 7) / 8));
        BinaryPrimitives.WriteUInt32LittleEndian(destination: box.AsSpan(start: 12), value: ((RenderHeight + 7) / 8));
        using var cullBounds = services.BufferFactory.CreateHostVisible(data: box, name: default, usage: GpuBufferUsage.Storage);
        using var visibility = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: ((((ulong)RenderWidth) * RenderHeight) * SdfWorldPackage.VisibilityRecordByteLength), usage: GpuBufferUsage.Storage);
        using var transport = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: ((((ulong)width) * height) * sizeof(uint)), usage: GpuBufferUsage.Storage);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R16G16B16A16Float, height: height, name: default, usage: GpuImageUsage.Storage, width: width);
        using var upload = services.SurfaceTransferFactory.CreateUpload();
        var sourceFormat = GpuPixelFormat.R8G8B8A8Unorm;

        if (poisonCurrent) {
            source = MemoryMarshal.AsBytes(span: Enumerable.Repeat(element: Half.PositiveInfinity, count: source.Length).ToArray().AsSpan()).ToArray();
            sourceFormat = GpuPixelFormat.R16G16B16A16Float;
        }
        var sourceView = upload.Upload(pixels: source, format: sourceFormat, width: RenderWidth, height: RenderHeight);
        using var fillerUpload = services.SurfaceTransferFactory.CreateUpload();
        var fillerSampled = fillerUpload.Upload(pixels: new byte[4], format: GpuPixelFormat.R8G8B8A8Unorm, width: 1, height: 1);
        using var historyUpload = services.SurfaceTransferFactory.CreateUpload();
        var historyView = fillerSampled;

        if (temporal) {
            var previous = new Half[((width * height) * 4)];

            for (var pixel = 0; (pixel < (previous.Length / 4)); pixel++) {
                for (var channel = 0; (channel < 3); channel++) { previous[((pixel * 4) + channel)] = (poisonedHistory ?? ((Half)0.5f)); }
                previous[((pixel * 4) + 3)] = ((Half)8f);
            }
            historyView = historyUpload.Upload(pixels: MemoryMarshal.AsBytes(span: previous.AsSpan()).ToArray(), format: GpuPixelFormat.R16G16B16A16Float, width: width, height: height);
        }
        var sampler = services.Bindings.CreateSampler();
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var pool = services.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(groups: layout.Groups));

        try {
            uint Binding(string member) => SdfKernelInterfaces.BindingOf(layout: parameters.Layout, member: member);
            var named = new Dictionary<uint, nint> {
                [Binding(member: SdfWorldPackage.CurrentColor)] = sourceView,
                [Binding(member: SdfWorldPackage.Output)] = output.ImageViewHandle,
                [Binding(member: SdfWorldPackage.HistoryColor)] = historyView,
                [Binding(member: SdfWorldPackage.HistoryColorWritten)] = (historyOutput?.ImageViewHandle ?? fillerStorage.ImageViewHandle),
            };
            var buffers = new Dictionary<uint, IGpuBuffer> {
                [Binding(member: ShaderWorkCounters.Buffer)] = counters,
                [Binding(member: SdfWorldPackage.CullBounds)] = cullBounds,
                [Binding(member: SdfWorldPackage.VisibilityRecords)] = visibility,
                [Binding(member: SdfWorldPackage.TransportWritten)] = transport,
            };
            var sets = new List<(uint Group, nint Set)>();

            foreach (var group in parameters.Layout.Groups) {
                var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[((int)group.Set)], name: default, poolHandle: pool);
                var resolves = (group.Group == ShaderInterfaceGroup.Pass);

                sets.Add(item: (group.Set, set));
                foreach (var binding in group.Bindings.Where(predicate: static binding => !binding.Pushed)) {
                    for (var element = 0u; (element < binding.Count); element++) {
                        switch (binding.Kind) {
                            case GpuBindingKind.ConstantBuffer:
                                var buffer = (resolves ? constants : fillerBlock);

                                services.Bindings.WriteConstantBuffer(descriptorSetHandle: set, binding: binding.Binding, arrayElement: element, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes);
                                break;
                            case GpuBindingKind.SampledImage:
                                services.Bindings.WriteSampledImage(descriptorSetHandle: set, binding: binding.Binding, arrayElement: element,
                                    imageViewHandle: ((resolves && named.TryGetValue(key: binding.Binding, value: out var view)) ? view : fillerSampled));
                                break;
                            case GpuBindingKind.StorageImage:
                                services.Bindings.WriteStorageImage(descriptorSetHandle: set, binding: binding.Binding, arrayElement: element,
                                    imageViewHandle: ((resolves && named.TryGetValue(key: binding.Binding, value: out var image)) ? image : fillerStorage.ImageViewHandle));
                                break;
                            case GpuBindingKind.Sampler:
                                services.Bindings.WriteSampler(descriptorSetHandle: set, binding: binding.Binding, arrayElement: element, samplerHandle: sampler);
                                break;
                            default:
                                var storage = ((resolves && buffers.TryGetValue(key: binding.Binding, value: out var bound)) ? bound : fillerBuffer);

                                if (resolves && (binding.Binding == Binding(member: SdfWorldPackage.HistorySurfaceWritten)) && (historySurface is not null)) {
                                    storage = historySurface;
                                }

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
            if (temporal) {
                recorder.TransitionImageLayout(commandBufferHandle: command, imageHandle: historyOutput!.ImageHandle,
                    sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, oldLayout: GpuImageLayout.Undefined,
                    destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader, newLayout: GpuImageLayout.General);
                recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: historySurface!.BufferHandle,
                    sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
                    destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
                recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: fillerBuffer.BufferHandle,
                    sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
                    destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
                recorder.ClearStorageBuffer(commandBufferHandle: command, bufferHandle: fillerBuffer.BufferHandle, sizeBytes: fillerBuffer.SizeBytes);
                recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: fillerBuffer.BufferHandle,
                    sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                    destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
            }
            recorder.TransitionImageLayout(commandBufferHandle: command, imageHandle: output.ImageHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, oldLayout: GpuImageLayout.Undefined,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader, newLayout: GpuImageLayout.General);
            recorder.TransitionImageLayout(commandBufferHandle: command, imageHandle: fillerStorage.ImageHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, oldLayout: GpuImageLayout.Undefined,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader, newLayout: GpuImageLayout.General);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
            recorder.ClearStorageBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle, sizeBytes: counters.SizeBytes);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer, destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: visibility.BufferHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
            recorder.ClearStorageBuffer(commandBufferHandle: command, bufferHandle: visibility.BufferHandle, sizeBytes: visibility.SizeBytes);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: visibility.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer, destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            foreach (var (group, set) in sets) {
                recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: group, pipelineLayoutHandle: pipeline.LayoutHandle);
            }
            recorder.Dispatch(commandBufferHandle: command, groupCountX: ((width + 7) / 8), groupCountY: ((height + 7) / 8), groupCountZ: 1);
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            var colors = readback.Read(bytesPerPixel: 8, format: GpuPixelFormat.R16G16B16A16Float, height: height, width: width,
                sourceImageHandle: (poisonCurrent ? historyOutput!.ImageHandle : output.ImageHandle), sourceLayout: GpuImageLayout.General);

            return MemoryMarshal.Cast<byte, Half>(span: colors.Span).ToArray();
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
            services.Bindings.DestroySampler(samplerHandle: sampler);
        }
    }
}
