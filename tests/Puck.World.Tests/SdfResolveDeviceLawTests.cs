using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The shipped spatial resolve preserves the analytic step colors and exact nearest-surface words. These
/// inputs isolate the resolver from traversal, while the reduced-world canary exercises its ordinary package recorder.</summary>
[SupportedOSPlatform("windows10.0.15063")]
public sealed partial class SdfResolveDeviceLawTests {
    private const uint RenderWidth = 16;
    private const uint RenderHeight = 48;
    private const uint OutputWidth = 64;
    private const uint OutputHeight = 192;
    private const uint InnerIdentity = (2u << 30) | 13u;
    private const uint OuterIdentity = (1u << 30) | 29u;

    [Fact]
    public void VulkanResolvesColorAndExactNearestSurface() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfResolveDeviceLawTests));

        Verify(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXResolvesColorAndExactNearestSurface() {
        using var device = DirectXTestDevices.Hardware();

        Verify(services: device.Services, extension: ".dxil");
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        foreach (var sharpness in new[] { 0f, 1f }) {
            var (color, surface) = Run(extension: extension, height: OutputHeight, services: services,
                sharpness: sharpness, width: OutputWidth);
            // Hand-derived values at fractions 1/8 and 7/8; the source stores its middle band as codes 64 and 191.
            Check(color: color, value: ((sharpness == 0f) ? 0.125f : 0.083984375f), width: OutputWidth, x: 30, y: 20);
            Check(color: color, value: ((sharpness == 0f) ? 0.875f : 0.916015625f), width: OutputWidth, x: 33, y: 20);
            Check(color: color, value: ((sharpness == 0f) ? 0.3132353f : 0.2928079f), width: OutputWidth, x: 30, y: 90);
            Check(color: color, value: (64f / 255f), width: OutputWidth, x: 29, y: 90);
            Check(color: color, value: ((sharpness == 0f) ? 0.3132353f : 0.2928079f), width: OutputWidth, x: 2, y: 150);
            var pixel = ((34 * OutputWidth) + 30);

            Assert.Equal(expected: BitConverter.SingleToUInt32Bits(value: ((sharpness == 0f) ? 6f : 2f)), actual: surface[(pixel * 2)]);
            Assert.Equal(expected: ((sharpness == 0f) ? InnerIdentity : OuterIdentity), actual: surface[((pixel * 2) + 1)]);
            var background = ((34 * OutputWidth) + 60);

            Assert.Equal(expected: BitConverter.SingleToUInt32Bits(value: 1000f), actual: surface[(background * 2)]);
            Assert.Equal(expected: 0u, actual: surface[((background * 2) + 1)]);
        }
        var (native, nativeSurface) = Run(extension: extension, height: RenderHeight, services: services,
            sharpness: 1f, width: RenderWidth);
        Check(color: native, value: 0f, width: RenderWidth, x: 7, y: 8);
        Check(color: native, value: 1f, width: RenderWidth, x: 8, y: 8);
        Assert.Equal(expected: BitConverter.SingleToUInt32Bits(value: 9f), actual: nativeSurface[(((8 * RenderWidth) + 7) * 2)]);
        Assert.Equal(expected: (1u << 30) | 7u, actual: nativeSurface[((((8 * RenderWidth) + 7) * 2) + 1)]);
    }
    private static void Check(Half[] color, uint width, uint x, uint y, float value, float alpha = 1f) {
        var offset = ((int)(((y * width) + x) * 4));

        for (var channel = 0; (channel < 3); channel++) {
            Assert.InRange(actual: MathF.Abs(x: (((float)color[(offset + channel)]) - value)), low: 0f, high: 0.0005f);
        }
        Assert.Equal(expected: ((Half)alpha), actual: color[(offset + 3)]);
    }
    private static (Half[] Color, uint[] Surface) Run(GpuDeviceServices services, string extension, float sharpness, uint width, uint height, TemporalCase? temporal = null) {
        var parameters = ((temporal is null) ? SdfWorldInterfaces.ResolveParameters : SdfWorldInterfaces.TemporalResolveParameters);
        var block = new byte[parameters.SizeBytes];

        parameters.WriteExtent(block: block, height: height, width: width);
        void Word(string member, uint value, int lane = 0) => BinaryPrimitives.WriteUInt32LittleEndian(
            destination: block.AsSpan(start: (((int)parameters.BlockOffsetOf(member: member)) + (lane * 4))), value: value);
        Word(member: SdfWorldPackage.ImageExtent, value: RenderWidth);
        Word(lane: 1, member: SdfWorldPackage.ImageExtent, value: RenderHeight);
        Word(member: SdfWorldPackage.UpscaleSharpness, value: BitConverter.SingleToUInt32Bits(value: sharpness));
        Word(member: SdfWorldPackage.FarDistance, value: BitConverter.SingleToUInt32Bits(value: 1000f));
        if (temporal is not null) {
            Word(member: SdfWorldPackage.ViewForward, lane: 2, value: BitConverter.SingleToUInt32Bits(value: 1f));
            Word(member: SdfWorldPackage.ViewRight, value: BitConverter.SingleToUInt32Bits(value: 1f));
            Word(member: SdfWorldPackage.ViewUp, lane: 1, value: BitConverter.SingleToUInt32Bits(value: 1f));
            Word(member: SdfWorldPackage.AspectRatio, value: BitConverter.SingleToUInt32Bits(value: 1f));
            Word(member: SdfWorldPackage.TanHalfFieldOfView, value: BitConverter.SingleToUInt32Bits(value: 1f));
            Word(member: SdfWorldPackage.HistoryFrames, value: temporal.Frames);
            Word(member: SdfWorldPackage.DebugMode, value: temporal.Debug);
            Word(member: SdfWorldPackage.Jitter, value: BitConverter.SingleToUInt32Bits(value: temporal.JitterX));
            SdfFrameBlock.WritePreviousView(block: block, valid: true, view: new SdfReprojectionView(
                Camera: new Puck.Abstractions.Cameras.CameraSnapshot(Position: System.Numerics.Vector3.Zero,
                    Right: System.Numerics.Vector3.UnitX, Up: System.Numerics.Vector3.UnitY, Forward: System.Numerics.Vector3.UnitZ,
                    TanHalfFieldOfView: 1f, AspectRatio: 1f) { FrustumOffset = new System.Numerics.Vector2(x: temporal.PreviousLensX, y: 0f) },
                Jitter: default, Width: RenderWidth, Height: RenderHeight));
        }
        var records = new uint[((RenderWidth * RenderHeight) * 16)];

        void Hit(uint x, uint y, float distance, uint identity) {
            var offset = (((y * RenderWidth) + x) * 16);

            records[offset] = BitConverter.SingleToUInt32Bits(value: distance);
            records[(offset + 1)] = ((temporal is null) ? identity : TemporalIdentity);
            records[(offset + 7)] = uint.MaxValue;
        }
        Hit(distance: 2f, identity: OuterIdentity, x: 6, y: 8);
        Hit(distance: 9f, identity: (1u << 30) | 7u, x: 7, y: 8);
        Hit(distance: 6f, identity: InnerIdentity, x: 8, y: 8);
        var source = new byte[((RenderWidth * RenderHeight) * 4)];

        for (var y = 0u; (y < RenderHeight); y++) {
            for (var x = 0u; (x < RenderWidth); x++) {
                var value = ((byte)((y < 16) ? ((x < 8) ? 0 : 255) : ((x < ((y < 32) ? 8 : 1)) ? 64 : 191)));
                var offset = (((y * RenderWidth) + x) * 4);

                source[offset] = value; source[(offset + 1)] = value; source[(offset + 2)] = value; source[(offset + 3)] = ((temporal?.PremultipliedCoverage == true) ? value : (byte)255);
            }
        }
        var layout = parameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute);
        var description = new GpuComputePipelineDescription(Bindings: [], Layout: layout, Name: "sdf-resolve-proof", PushConstantBinding: null);
        using var module = services.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(path: Path.Combine(path1: SdfKernelSet.DefaultDirectory, path2: (((temporal is null) ? "sdf-resolve.comp" : "sdf-temporal-resolve.comp") + extension))));
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var frame = services.BufferFactory.CreateHostVisible(data: new byte[256], name: default, usage: GpuBufferUsage.Uniform);
        using var constants = services.BufferFactory.CreateHostVisible(data: block, name: default, usage: GpuBufferUsage.Uniform);
        using var visibility = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: records.AsSpan()), usage: GpuBufferUsage.Storage, name: default);
        using var bounds = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: new uint[] { 0, 0, 2, 6 }.AsSpan()), usage: GpuBufferUsage.Storage, name: default);
        using var surfaces = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: ((width * height) * 8), usage: GpuBufferUsage.Storage);
        using var counters = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: 16, usage: GpuBufferUsage.Storage);
        using var surfaceReadback = services.BufferFactory.CreateReadback(sizeBytes: surfaces.SizeBytes, name: default);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R16G16B16A16Float, height: height, name: default, usage: GpuImageUsage.Storage, width: width);
        using var upload = services.SurfaceTransferFactory.CreateUpload();
        var sourceView = upload.Upload(pixels: source, format: GpuPixelFormat.R8G8B8A8Unorm, width: RenderWidth, height: RenderHeight);
        using var temporalInputs = ((temporal is null) ? null : new TemporalInputs(height: height, sample: temporal, services: services, width: width));
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var pool = services.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(groups: layout.Groups));

        try {
            var frameSet = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[0], name: default, poolHandle: pool);
            var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[3], name: default, poolHandle: pool);

            services.Bindings.WriteConstantBuffer(descriptorSetHandle: frameSet, binding: 0, arrayElement: 0, bufferHandle: frame.BufferHandle, bufferSize: frame.SizeBytes);
            services.Bindings.WriteConstantBuffer(descriptorSetHandle: set, binding: 0, arrayElement: 0, bufferHandle: constants.BufferHandle, bufferSize: constants.SizeBytes);
            uint Binding(string member) => SdfWorldInterfaces.BindingOf(layout: parameters.Layout, member: member);
            services.Bindings.WriteSampledImage(descriptorSetHandle: set, binding: Binding(member: SdfWorldPackage.CurrentColor), arrayElement: 0, imageViewHandle: sourceView);
            services.Bindings.WriteStorageImage(descriptorSetHandle: set, binding: Binding(member: SdfWorldPackage.Output), arrayElement: 0, imageViewHandle: output.ImageViewHandle);
            void Buffer(string member, IGpuBuffer buffer, GpuBindingKind kind, uint stride) => services.Bindings.WriteBuffer(
                descriptorSetHandle: set, binding: Binding(member: member), bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes, kind: kind, elementStride: stride);
            Buffer(buffer: visibility, kind: GpuBindingKind.ReadOnlyBuffer, member: SdfWorldPackage.VisibilityRecords, stride: 4);
            Buffer(buffer: bounds, kind: GpuBindingKind.ReadOnlyBuffer, member: SdfWorldPackage.CullBounds, stride: 4);
            Buffer(buffer: surfaces, kind: GpuBindingKind.ReadWriteBuffer, member: SdfWorldPackage.ResolvedSurface, stride: 8);
            Buffer(buffer: counters, kind: GpuBindingKind.ReadWriteBuffer, member: ShaderWorkCounters.Buffer, stride: 4);
            var worldSet = (temporalInputs?.Bind(parameters: parameters, passSet: set, pipeline: pipeline, pool: pool) ?? 0);
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.TransitionImageLayout(commandBufferHandle: command, imageHandle: output.ImageHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, oldLayout: GpuImageLayout.Undefined,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader, newLayout: GpuImageLayout.General);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: surfaces.BufferHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
            recorder.ClearStorageBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle, sizeBytes: counters.SizeBytes);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer, destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: frameSet, group: 0, pipelineLayoutHandle: pipeline.LayoutHandle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: 3, pipelineLayoutHandle: pipeline.LayoutHandle);
            if (worldSet != 0) {
                recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command,
                    descriptorSetHandle: worldSet, group: 1, pipelineLayoutHandle: pipeline.LayoutHandle);
            }
            recorder.Dispatch(commandBufferHandle: command, groupCountX: ((width + 7) / 8), groupCountY: ((height + 7) / 8), groupCountZ: 1);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: surfaces.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader, destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
            recorder.CopyBuffer(commandBufferHandle: command, sourceBufferHandle: surfaces.BufferHandle, destinationBufferHandle: surfaceReadback.BufferHandle, sizeBytes: surfaces.SizeBytes);
            recorder.MemoryBarrier(commandBufferHandle: command, destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host,
                sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer);
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            var colors = readback.Read(bytesPerPixel: 8, format: GpuPixelFormat.R16G16B16A16Float, height: height, width: width,
                sourceImageHandle: output.ImageHandle, sourceLayout: GpuImageLayout.General);
            var surfaceBytes = new byte[surfaces.SizeBytes];

            surfaceReadback.Read(destination: surfaceBytes);
            return (MemoryMarshal.Cast<byte, Half>(span: colors.Span).ToArray(), MemoryMarshal.Cast<byte, uint>(span: surfaceBytes.AsSpan()).ToArray());
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
        }
    }
}
