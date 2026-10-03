using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Both hardware backends execute the production K-word functions for every byte in every stable slot.</summary>
[SupportedOSPlatform("windows10.0.15063")]
public sealed class SdfShadowVisibilityDeviceLawTests {
    private const string Kernel = "sdf-shadow-visibility.comp";

    private readonly record struct VisibilityCase(Vector4 Visibility, uint Word);

    [Fact]
    public void VulkanPreservesFourIndependentEightBitShadowVisibilities() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfShadowVisibilityDeviceLawTests));

        Verify(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXPreservesFourIndependentEightBitShadowVisibilities() {
        using var device = DirectXTestDevices.Hardware();

        Verify(services: device.Services, extension: ".dxil");
    }

    private static VisibilityCase[] Cases() {
        var cases = new List<VisibilityCase>();

        for (var slot = 0; (slot < 4); slot++) {
            for (var code = 0; (code < 256); code++) {
                byte[] codes = [17, 61, 139, 233];

                codes[slot] = ((byte)code);
                var word = ((uint)codes[0]) | (((uint)codes[1]) << 8) | (((uint)codes[2]) << 16) | (((uint)codes[3]) << 24);
                var visibility = new Vector4(x: (codes[0] / 255f), y: (codes[1] / 255f), z: (codes[2] / 255f), w: (codes[3] / 255f));

                cases.Add(item: new VisibilityCase(Visibility: visibility, Word: word));
            }
        }
        cases.Add(item: new VisibilityCase(Visibility: new Vector4(w: 2f, x: -2f, y: 0f, z: 1f), Word: 0xFFFF0000u));
        cases.Add(item: new VisibilityCase(Visibility: new Vector4(w: (254.5f / 255f), x: (0.5f / 255f), y: (63.5f / 255f), z: (127.5f / 255f)), Word: 0xFF804001u));
        for (var code = 0; (code < 255); code++) {
            var visibility = new Vector4(w: 1f, x: ((code + 0.25f) / 255f), y: ((code + 0.5f) / 255f), z: ((code + 0.75f) / 255f));
            var word = ((uint)code) | (((uint)(code + 1)) << 8) | (((uint)(code + 1)) << 16) | 0xFF000000u;

            cases.Add(item: new VisibilityCase(Visibility: visibility, Word: word));
        }
        return cases.ToArray();
    }
    private static void Verify(GpuDeviceServices services, string extension) {
        var cases = Cases();
        var results = Run(cases: cases, extension: extension, services: services);

        for (var index = 0; (index < cases.Length); index++) {
            var word = cases[index].Word;
            var packed = results[index];

            Assert.Equal(actual: packed.X, expected: ((float)(word & 0xFFFFu)));
            Assert.Equal(actual: packed.Y, expected: ((float)(word >> 16)));
            Assert.Equal(actual: packed.Z, expected: packed.X);
            Assert.Equal(actual: packed.W, expected: packed.Y);
            var decoded = results[(cases.Length + index)];

            for (var slot = 0; (slot < 4); slot++) {
                var code = (word >> (slot * 8)) & 255u;
                var actual = decoded[slot];

                Assert.InRange(actual: actual, high: 1f, low: 0f);
                Assert.Equal(expected: code, actual: ((uint)MathF.Round(x: (actual * 255f))));
                Assert.InRange(actual: MathF.Abs(x: (actual - (code / 255f))), low: 0f, high: 0.00000012f);
            }
        }
    }
    private static Vector4[] Run(GpuDeviceServices services, string extension, VisibilityCase[] cases) {
        var values = cases.Select(selector: item => item.Visibility).ToArray();
        var words = cases.Select(selector: item => item.Word).ToArray();
        var width = ((uint)cases.Length);
        var description = new GpuComputePipelineDescription(Bindings: [], Name: Kernel, PushConstantBinding: null,
            Layout: new GpuPipelineLayoutDescription(groups: [new GpuGroupLayoutDescription(ordinal: 3, bindings: [
                new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ReadOnlyBuffer),
                new GpuGroupBinding(binding: 1, kind: GpuBindingKind.ReadOnlyBuffer),
                new GpuGroupBinding(binding: 2, kind: GpuBindingKind.StorageImage),
            ])], pushesIndex: false, stages: GpuShaderStage.Compute));
        using var module = services.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (Kernel + extension))));
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var input = services.BufferFactory.CreateHostVisible(name: default, sizeBytes: (width * 16UL), usage: GpuBufferUsage.Storage);
        using var expected = services.BufferFactory.CreateHostVisible(name: default, sizeBytes: (width * 4UL), usage: GpuBufferUsage.Storage);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R32G32B32A32Float, height: 2, name: default,
            usage: GpuImageUsage.Storage, width: width);
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var pool = services.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(groups: description.Layout!.Groups));

        try {
            input.Write<Vector4>(data: values);
            expected.Write<uint>(data: words);
            var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[3], name: default, poolHandle: pool);

            services.Bindings.WriteBuffer(binding: 0, bufferHandle: input.BufferHandle, bufferSize: input.SizeBytes,
                descriptorSetHandle: set, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
            services.Bindings.WriteBuffer(binding: 1, bufferHandle: expected.BufferHandle, bufferSize: expected.SizeBytes,
                descriptorSetHandle: set, elementStride: 4, kind: GpuBindingKind.ReadOnlyBuffer);
            services.Bindings.WriteStorageImage(arrayElement: 0, binding: 2, descriptorSetHandle: set, imageViewHandle: output.ImageViewHandle);
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.TransitionImageLayout(commandBufferHandle: command, imageHandle: output.ImageHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, oldLayout: GpuImageLayout.Undefined,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader, newLayout: GpuImageLayout.General);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: 3, pipelineLayoutHandle: pipeline.LayoutHandle);
            recorder.Dispatch(commandBufferHandle: command, groupCountX: width, groupCountY: 1, groupCountZ: 1);
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            var result = readback.Read(bytesPerPixel: 16, format: GpuPixelFormat.R32G32B32A32Float, height: 2, width: width,
                sourceImageHandle: output.ImageHandle, sourceLayout: GpuImageLayout.General);

            return MemoryMarshal.Cast<byte, Vector4>(span: result.Span).ToArray();
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
        }
    }
}
