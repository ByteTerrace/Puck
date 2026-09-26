using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The device half of the bake sampling check. Each texture of <c>tests/Puck.SignedDistance.Tests/Fixtures/bake-sampling.json</c>
/// (a real bake's BC7 albedo, BC5 normal and BC6H emission, every mip level) is uploaded through the device's one image
/// upload (<see cref="IGpuSurfaceUpload"/>) as an image of its block-compressed format with all its levels, and the probe
/// kernel (<c>Assets/Shaders/bake-sampling.comp.hlsl</c>) samples each probe texel's center at its level through a point
/// sampler. The value read must match what the CPU decoder reads there, under the tolerance the fixture's law states:
/// BC7 within half a code of the expected code over 255, BC5 within one code (a device interpolates BC4 in float), and
/// BC6H exactly the expected half. It runs on the first Vulkan device with a graphics queue, on the first Direct3D 12
/// hardware adapter and on the software (WARP) renderer, and skips by name where the host has none.
/// </summary>
[SupportedOSPlatform("windows10.0.15063")]
public sealed class BakeSamplingDeviceLawTests {
    private const string KernelName = "bake-sampling.comp";
    // The probe kernel's one group and its bindings, each register at its binding in the group's space.
    private const uint Group = 3U;
    private const uint OutputBinding = 2U;
    private const uint ProbesBinding = 3U;
    private const uint SamplerBinding = 1U;
    private const uint SourceBinding = 0U;

    [Fact]
    public void EveryProbeSampledOnAVulkanDeviceIsWhatTheDecoderReads() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(BakeSamplingDeviceLawTests));

        Probe(
            backend: $"vulkan ({device.Name})",
            kernel: Kernel(extension: ".spv"),
            services: device.Services
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void EveryProbeSampledOnADirect3D12DeviceIsWhatTheDecoderReads(bool warp) {
        using (var context = (warp ? DirectXTestDevices.Warp() : DirectXTestDevices.Hardware())) {
            Probe(
                backend: (warp ? "directx (WARP)" : "directx"),
                kernel: Kernel(extension: ".dxil"),
                services: context.Services
            );
        }
    }

    private static byte[] Kernel(string extension) =>
        File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (KernelName + extension)));
    // Uploads each fixture texture with every level, samples its probes, and holds each value read to the fixture.
    private static void Probe(string backend, byte[] kernel, GpuDeviceServices services) {
        var fixture = JsonNode.Parse(json: File.ReadAllText(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Fixtures", path3: "bake-sampling.json")))!;
        var bindings = services.Bindings;
        // One group, set 3: the source, its sampler, the output and the probe table; each dispatch pushes its probe's index.
        var description = new GpuComputePipelineDescription(
            Bindings: [],
            Layout: new GpuPipelineLayoutDescription(
                groups: [new GpuGroupLayoutDescription(
                    bindings: [
                        new GpuGroupBinding(binding: SourceBinding, kind: GpuBindingKind.SampledImage),
                        new GpuGroupBinding(binding: SamplerBinding, kind: GpuBindingKind.Sampler),
                        new GpuGroupBinding(binding: OutputBinding, kind: GpuBindingKind.StorageImage),
                        new GpuGroupBinding(binding: ProbesBinding, kind: GpuBindingKind.ReadOnlyBuffer),
                    ],
                    ordinal: Group
                )],
                pushesIndex: true,
                stages: GpuShaderStage.Compute
            ),
            Name: KernelName,
            PushConstantBinding: null
        );
        using var module = services.ShaderModuleFactory.Create(
            bytecode: kernel,
            stage: GpuShaderStage.Compute
        );
        using var pipeline = services.PipelineFactory.Create(
            computeShaderModule: module,
            description: description,
            name: default
        );
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        var sampler = bindings.CreateSampler(filter: GpuSamplerFilter.Nearest);
        var failures = new List<string>();

        try {
            foreach (var texture in fixture["textures"]!.AsArray()) {
                SampleTexture(
                    backend: backend,
                    bindings: bindings,
                    description: description,
                    failures: failures,
                    pipeline: pipeline,
                    readback: readback,
                    sampler: sampler,
                    services: services,
                    texture: texture!
                );
            }
        } finally {
            bindings.DestroySampler(samplerHandle: sampler);
        }

        Assert.True(
            condition: (failures.Count == 0),
            userMessage: string.Join(separator: Environment.NewLine, values: failures)
        );
    }
    private static void SampleTexture(string backend, GpuDeviceServices services, IGpuBindings bindings, GpuComputePipelineDescription description, IGpuComputePipeline pipeline, IGpuSurfaceReadback readback, nint sampler, JsonNode texture, List<string> failures) {
        // The fixture names its formats in the baker's TextureFormat vocabulary, whose block-compressed members carry
        // the same names as GpuPixelFormat's until the pixel-format fold makes them one.
        var format = Enum.Parse<GpuPixelFormat>(value: texture["format"]!.GetValue<string>());
        var width = texture["width"]!.GetValue<uint>();
        var height = texture["height"]!.GetValue<uint>();
        var levels = texture["levels"]!.AsArray().Select(selector: static level => Convert.FromBase64String(s: level!.GetValue<string>())).ToArray();
        var probes = texture["probes"]!.AsArray();
        var recorder = services.Recorder;
        using var upload = services.SurfaceTransferFactory.CreateUpload();
        var view = upload.Upload(
            format: format,
            height: height,
            levels: ((uint)levels.Length),
            pixels: levels.SelectMany(selector: static level => level).ToArray(),
            width: width
        );
        using var output = services.ImageFactory.Create(
            format: GpuPixelFormat.R32G32B32A32Float,
            height: 1U,
            name: default,
            usage: GpuImageUsage.Storage,
            width: ((uint)probes.Count)
        );
        using var commands = services.CommandPoolFactory.Create(name: default);
        // The probe table: each probe's column, row, level, and the output texel its value lands in.
        using var probeTable = services.BufferFactory.CreateHostVisible(
            name: default,
            sizeBytes: ((ulong)(probes.Count * 4 * sizeof(uint))),
            usage: GpuBufferUsage.Storage
        );

        probeTable.Write<uint>(data: [.. probes.SelectMany(selector: static (probe, slot) => (uint[])[probe!["x"]!.GetValue<uint>(), probe["y"]!.GetValue<uint>(), probe["level"]!.GetValue<uint>(), ((uint)slot)])]);
        var pool = bindings.CreatePool(
            name: default,
            sizes: GpuDescriptorPoolSizes.ForGroups(groups: description.Layout!.Groups)
        );
        ReadOnlyMemory<byte> values;

        try {
            var set = bindings.AllocateSet(
                descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[((int)Group)],
                name: default,
                poolHandle: pool
            );
            var command = commands.CommandBufferHandle;

            bindings.WriteSampledImage(
                arrayElement: 0U,
                binding: SourceBinding,
                descriptorSetHandle: set,
                imageViewHandle: view
            );
            bindings.WriteSampler(
                arrayElement: 0U,
                binding: SamplerBinding,
                descriptorSetHandle: set,
                samplerHandle: sampler
            );
            bindings.WriteBuffer(
                binding: ProbesBinding,
                bufferHandle: probeTable.BufferHandle,
                bufferSize: probeTable.SizeBytes,
                descriptorSetHandle: set,
                elementStride: (4U * sizeof(uint)),
                kind: GpuBindingKind.ReadOnlyBuffer
            );
            bindings.WriteStorageImage(
                arrayElement: 0U,
                binding: OutputBinding,
                descriptorSetHandle: set,
                imageViewHandle: output.ImageViewHandle
            );
            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.TransitionImageLayout(
                commandBufferHandle: command,
                destinationAccessMask: GpuAccess.ShaderWrite,
                destinationStageMask: GpuStage.ComputeShader,
                imageHandle: output.ImageHandle,
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

            for (var slot = 0; (slot < probes.Count); slot++) {
                ReadOnlySpan<uint> index = [((uint)slot)];

                recorder.PushConstants(
                    bindPoint: GpuBindPoint.Compute,
                    commandBufferHandle: command,
                    data: MemoryMarshal.AsBytes(span: index),
                    offset: 0U,
                    pipelineLayoutHandle: pipeline.LayoutHandle,
                    stageFlags: GpuShaderStage.Compute
                );
                recorder.Dispatch(
                    commandBufferHandle: command,
                    groupCountX: 1U,
                    groupCountY: 1U,
                    groupCountZ: 1U
                );
            }

            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            values = readback.Read(
                bytesPerPixel: 16U,
                format: GpuPixelFormat.R32G32B32A32Float,
                height: 1U,
                sourceImageHandle: output.ImageHandle,
                sourceLayout: GpuImageLayout.General,
                width: ((uint)probes.Count)
            );
        } finally {
            bindings.DestroyPool(poolHandle: pool);
        }

        var read = MemoryMarshal.Cast<byte, float>(span: values.Span);

        for (var slot = 0; (slot < probes.Count); slot++) {
            var probe = probes[slot]!;
            var expected = probe["expected"]!.AsArray().Select(selector: static value => value!.GetValue<int>()).ToArray();
            var actual = read.Slice(length: 4, start: (slot * 4)).ToArray();

            if (!Matches(actual: actual, expected: expected, format: format)) {
                failures.Add(item: $"{backend}: {format} level {probe["level"]} texel ({probe["x"]}, {probe["y"]}) read [{string.Join(separator: ", ", values: actual)}], expected [{string.Join(separator: ", ", values: expected)}]");
            }
        }
    }
    // BC7 and BC5 expectations are unorm codes; BC6H's are half bits.
    private static bool Matches(float[] actual, int[] expected, GpuPixelFormat format) {
        for (var channel = 0; (channel < expected.Length); channel++) {
            var matches = format switch {
                GpuPixelFormat.Bc7Unorm => (Math.Abs(value: ((actual[channel] * 255.0) - expected[channel])) <= 0.5),
                GpuPixelFormat.Bc5Unorm => (Math.Abs(value: ((actual[channel] * 255.0) - expected[channel])) <= 1.0),
                GpuPixelFormat.Bc6hUfloat => (BitConverter.HalfToUInt16Bits(value: ((Half)actual[channel])) == expected[channel]),
                _ => throw new InvalidDataException(message: $"The fixture holds a {format} texture, which this law has no tolerance for."),
            };

            if (!matches) {
                return false;
            }
        }

        return true;
    }
}
