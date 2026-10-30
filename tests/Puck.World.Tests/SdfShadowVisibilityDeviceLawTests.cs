using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Both hardware backends execute the production K-word functions for every code in every stable slot.
/// <para>Every expectation follows from the K word's contract alone (<c>frame/sdf-shadow-visibility.hlsli</c>): of a
/// slot's eight-bit codes the top one, <see cref="Unqueried"/>, marks a slot the surface pass did not query, and a queried
/// visibility is saturated and rounded half up to the nearest of the codes zero through <see cref="Scale"/>, one below it.
/// So a visibility of <c>c / Scale</c> packs to code <c>c</c>, and any negative visibility (the surface pass writes -1
/// for a side turned from the light) packs to <see cref="Unqueried"/>; a full visibility packs to <see cref="Scale"/>,
/// never to the unqueried code. Decoding gives <c>c / Scale</c> for a queried code and full visibility for the
/// unqueried one, whose queried mask alone is zero; repacking a decoded word therefore turns each unqueried slot into a
/// fully visible queried one.</para></summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfShadowVisibilityDeviceLawTests {
    private const string Kernel = "sdf-shadow-visibility.comp";
    private const uint Unqueried = SdfVisibility.ShadowMask;
    private const uint Scale = (Unqueried - 1u);
    private const int Rows = 3;

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

    private static uint Word(uint x, uint y, uint z, uint w) => (x | (y << 8) | (z << 16) | (w << 24));
    private static uint CodeAt(uint word, int slot) => ((word >> (slot * 8)) & 255u);
    // The visibility the contract packs to a code: c / Scale for a queried code, the surface pass's -1 for the unqueried one.
    private static float VisibilityOf(uint code) => ((code == Unqueried) ? -1f : (code / ((float)Scale)));
    private static VisibilityCase[] Cases() {
        var cases = new List<VisibilityCase>();

        // Every code of each slot, the queried ones and the unqueried one, beside fixed codes in the other three.
        for (var slot = 0; (slot < 4); slot++) {
            for (var code = 0u; (code <= Unqueried); code++) {
                uint[] codes = [17u, 61u, 139u, 233u];

                codes[slot] = code;
                var visibility = new Vector4(x: VisibilityOf(code: codes[0]), y: VisibilityOf(code: codes[1]),
                    z: VisibilityOf(code: codes[2]), w: VisibilityOf(code: codes[3]));

                cases.Add(item: new VisibilityCase(Visibility: visibility, Word: Word(x: codes[0], y: codes[1], z: codes[2], w: codes[3])));
            }
        }
        // Any negative visibility is unqueried; one and above saturate to the fully visible queried code.
        cases.Add(item: new VisibilityCase(Visibility: new Vector4(w: 2f, x: -2f, y: 0f, z: 1f), Word: Word(x: Unqueried, y: 0u, z: Scale, w: Scale)));
        // A negative zero is not below zero: a fully shadowed queried slot. Exact half codes round up.
        cases.Add(item: new VisibilityCase(Visibility: new Vector4(w: ((Scale - 0.5f) / Scale), x: -0f, y: -1f, z: (0.5f / Scale)),
            Word: Word(x: 0u, y: Unqueried, z: 1u, w: Scale)));
        for (var code = 0u; (code < Scale); code++) {
            var visibility = new Vector4(w: 1f, x: ((code + 0.25f) / Scale), y: ((code + 0.5f) / Scale), z: ((code + 0.75f) / Scale));

            cases.Add(item: new VisibilityCase(Visibility: visibility, Word: Word(x: code, y: (code + 1u), z: (code + 1u), w: Scale)));
        }
        return cases.ToArray();
    }
    private static void Verify(GpuDeviceServices services, string extension) {
        var cases = Cases();
        var results = Run(cases: cases, extension: extension, services: services);

        for (var index = 0; (index < cases.Length); index++) {
            var word = cases[index].Word;
            var packed = results[index];
            var decoded = results[(cases.Length + index)];
            var queried = results[((2 * cases.Length) + index)];
            var repacked = 0u;

            for (var slot = 0; (slot < 4); slot++) {
                var code = CodeAt(word: word, slot: slot);
                var actual = decoded[slot];

                repacked |= (((code == Unqueried) ? Scale : code) << (slot * 8));
                Assert.Equal(expected: ((code == Unqueried) ? 0f : 1f), actual: queried[slot]);
                if (code == Unqueried) {
                    Assert.Equal(expected: 1f, actual: actual);
                } else {
                    Assert.InRange(actual: actual, high: 1f, low: 0f);
                    Assert.Equal(expected: code, actual: ((uint)MathF.Round(x: (actual * Scale))));
                    Assert.InRange(actual: MathF.Abs(x: (actual - (code / ((float)Scale)))), low: 0f, high: 0.00000012f);
                }
            }
            Assert.Equal(actual: packed.X, expected: ((float)(word & 0xFFFFu)));
            Assert.Equal(actual: packed.Y, expected: ((float)(word >> 16)));
            Assert.Equal(actual: packed.Z, expected: ((float)(repacked & 0xFFFFu)));
            Assert.Equal(actual: packed.W, expected: ((float)(repacked >> 16)));
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
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R32G32B32A32Float, height: Rows, name: default,
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
            var result = readback.Read(bytesPerPixel: 16, format: GpuPixelFormat.R32G32B32A32Float, height: Rows, width: width,
                sourceImageHandle: output.ImageHandle, sourceLayout: GpuImageLayout.General);

            return MemoryMarshal.Cast<byte, Vector4>(span: result.Span).ToArray();
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
        }
    }
}
