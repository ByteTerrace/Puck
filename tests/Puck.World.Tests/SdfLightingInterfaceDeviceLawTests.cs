using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The generated native lighting declarations read every field of two consecutive records on both drivers.
/// One corrupted field per table turns exactly its verdict pixel red while every other field remains green.</summary>
[SupportedOSPlatform("windows10.0.15063")]
public sealed class SdfLightingInterfaceDeviceLawTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeLightingRecordsEchoEveryFieldAndDiscriminateWrongWords(bool directX) {
        using var directory = new TemporaryDirectory(prefix: "puck-lighting-echo-");
        var shader = ShaderInterfaceEcho.InterfaceOf(new ShaderInterface(name: "lighting-records",
            members: [.. SdfWorldPackage.LightTables, .. SdfWorldPackage.SkyTables]));
        var include = ShaderFrameInterface.IncludeFileName(interfaceName: shader.Name);
        var compiler = new ShaderCompiler(cacheDirectory: directory.PathOf("cache"));
        var build = await compiler.CompileAsync(descriptor: new ShaderCompilationRequest(
            name: shader.Name,
            stages: [new ShaderStageSource(ShaderStage.Compute, directory.PathOf("echo.comp.hlsl"), ShaderInterfaceEcho.Generate(shader))],
            generatedIncludes: new Dictionary<string, string> { [directory.PathOf(include)] = ShaderInterfaceHlsl.Generate(shader) }
        ), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(build.IsSuccess, string.Join(Environment.NewLine, build.Diagnostics));

        if (directX) {
            using var device = DirectXTestDevices.Hardware();
            Verify(device.Services, build.Dxil, shader);
        } else {
            using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfLightingInterfaceDeviceLawTests));
            Verify(device.Services, build.Spirv, shader);
        }
    }

    private static void Verify(GpuDeviceServices services, ReadOnlyMemory<byte> bytecode, ShaderInterface shader) {
        var layout = shader.Layout();
        var redPixels = new HashSet<int>();
        var pixel = 0;
        foreach (var group in layout.Groups) {
            foreach (var resource in group.Resources) {
                if (resource.Member.Structure is not { } structure) { continue; }
                redPixels.Add(pixel + structure.Members.Count);
                pixel += ShaderInterfaceEcho.RecordElements * structure.Members.Count;
            }
        }
        Assert.Equal(5, redPixels.Count);
        foreach (var corrupt in new[] { false, true }) {
            var values = Run(services, bytecode, shader, corrupt);
            Assert.Equal(pixel * 4, values.Length);
            for (var index = 0; index < pixel; index++) {
                var red = corrupt && redPixels.Contains(index);
                Assert.Equal(new byte[] { red ? (byte)255 : (byte)0, red ? (byte)0 : (byte)255, 0, 255 },
                    values.AsSpan(index * 4, 4).ToArray());
            }
        }
    }

    private static byte[] Run(GpuDeviceServices services, ReadOnlyMemory<byte> bytecode, ShaderInterface shader, bool corrupt) {
        var layout = shader.Layout();
        var width = ShaderInterfaceEcho.Width(shader);
        var description = new GpuComputePipelineDescription(Bindings: [],
            Layout: layout.PipelineLayout(stages: GpuShaderStage.Compute), Name: shader.Name, PushConstantBinding: null);
        using var module = services.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute, bytecode: bytecode);
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R8G8B8A8Unorm,
            height: 1, width: width, name: default, usage: GpuImageUsage.Storage);
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var pool = services.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(groups: description.Layout!.Groups));
        var buffers = new List<IGpuStorageBuffer>();
        try {
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;
            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.TransitionImageLayout(commandBufferHandle: command, imageHandle: output.ImageHandle,
                oldLayout: GpuImageLayout.Undefined, newLayout: GpuImageLayout.General,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            foreach (var group in layout.Groups) {
                var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[(int)group.Set], name: default, poolHandle: pool);
                foreach (var resource in group.Resources) {
                    if (resource.Member.Structure is { } structure) {
                        var bytes = new byte[structure.SizeBytes * ShaderInterfaceEcho.RecordElements];
                        ShaderInterfaceEcho.WriteRecordSentinels(bytes, resource, group.Set);
                        if (corrupt) { bytes[structure.SizeBytes + structure.Members[0].Offset] ^= 1; }
                        var buffer = services.BufferFactory.CreateHostVisible(data: bytes.AsSpan(), name: default, usage: GpuBufferUsage.Storage);
                        buffers.Add(buffer);
                        services.Bindings.WriteBuffer(descriptorSetHandle: set, binding: resource.Binding,
                            bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes,
                            elementStride: structure.SizeBytes, kind: GpuBindingKind.ReadOnlyBuffer);
                    } else {
                        Assert.Equal(ShaderInterfaceEcho.OutputName, resource.Member.Name);
                        services.Bindings.WriteStorageImage(descriptorSetHandle: set, binding: resource.Binding,
                            arrayElement: 0, imageViewHandle: output.ImageViewHandle);
                    }
                }
                recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command,
                    descriptorSetHandle: set, group: group.Set, pipelineLayoutHandle: pipeline.LayoutHandle);
            }
            recorder.Dispatch(commandBufferHandle: command, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            return readback.Read(bytesPerPixel: 4, format: GpuPixelFormat.R8G8B8A8Unorm, height: 1, width: width,
                sourceImageHandle: output.ImageHandle, sourceLayout: GpuImageLayout.General).ToArray();
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
            foreach (var buffer in buffers) { buffer.Dispose(); }
        }
    }
}
