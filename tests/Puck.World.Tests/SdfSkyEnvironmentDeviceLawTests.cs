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
/// CONTRACT UNDER TEST: the shipped environment kernels (<c>sdf-sky-environment.comp</c> and
/// <c>sdf-sky-environment-reduce.comp</c>) render the CPU reference's map (<see cref="SdfSkyEnvironment.Render"/>), every
/// texel's channel within one half-float step of it, and reduce it to the reference's projection of the map they wrote
/// (<see cref="SdfSkyEnvironment.Project"/>) within 1e-4 of the first coefficient's largest channel, for the default look's
/// gradient, a four-stop sky with a bright disc and a covering cloud layer, neither of which the map holds, and a tilted
/// sky frame under a second, turned gradient the lighting alone sees, masked to a cone and multiplied over the first,
/// two analytic panels, and every lighting-capable procedural kind. The
/// map counts one sky evaluation a lit layer and two plane texels written per direction, and the reduction nine texels and no
/// evaluation; two runs on one device write the same bytes. Every binding the kernels do not read holds a filler of its
/// kind.
/// </summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfSkyEnvironmentDeviceLawTests {
    private const int Coefficients = SdfSkyEnvironment.CoefficientCount;
    // The first detail row: past the map's and the reduction's pass rows and an unused plain row.
    private const int DetailRow = 3;

    [Fact]
    public void VulkanRendersTheReferencesMapAndCoefficients() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfSkyEnvironmentDeviceLawTests));

        Verify(extension: ".spv", services: device.Services);
    }
    [Fact]
    public void DirectXRendersTheReferencesMapAndCoefficients() {
        using var device = DirectXTestDevices.Hardware();

        Verify(extension: ".dxil", services: device.Services);
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        foreach (var (sky, litLayers) in new[] {
            (new SdfSky(), 1L), (FourStops(), 1L), (Tilted(), 2L), (Panels(), 3L),
            (Lit(new SdfSkyStars { Brightness = 2f, Density = 16f }), 1L),
            (Lit(new SdfSkyClouds { Coverage = .6f }), 1L),
            (Lit(new SdfSkyAurora { Intensity = 1f }), 1L),
            (Lit(new SdfSkyNoise { Coverage = .5f }), 1L),
            (Lit(new SdfSkyPattern()), 1L),
        }) {
            var layers = new SdfSkyLayer[SdfSky.MaxLayers];

            sky.Pack(block: out var block, details: new SdfSkyDetails(), farDistance: 40f, layers: layers, lights: SdfLights.Default());

            var expected = new Vector3[SdfSkyEnvironment.Texels];

            SdfSkyEnvironment.Render(block: in block, layers: layers, map: expected);
            var reflectionLayers = layers.ToArray();

            for (var index = 0; (index < reflectionLayers.Length); index++) {
                if (reflectionLayers[index].Kind == SdfSkyLayerKind.Panel) {
                    reflectionLayers[index].Visibility = SdfSkyVisibility.Camera;
                }
            }
            var expectedReflection = new Vector3[SdfSkyEnvironment.Texels];

            SdfSkyEnvironment.Render(block: in block, layers: reflectionLayers, map: expectedReflection);

            var first = Run(block: block, extension: extension, layers: layers, services: services);
            var second = Run(block: block, extension: extension, layers: layers, services: services);

            Assert.Equal(actual: second.Map, expected: first.Map);
            Assert.Equal(actual: second.Coefficients, expected: first.Coefficients);
            Assert.Equal(actual: (first.MapTexels, first.ReduceTexels), expected: (8192L, 9L));
            // One evaluation a lit layer a texel, but where a mask leaves it out.
            Assert.InRange(actual: first.Evaluations, high: (4096L * litLayers), low: (4096L + ((litLayers - 1L) * 64L)));

            var map = new Vector3[SdfSkyEnvironment.Texels];

            for (var texel = 0; (texel < map.Length); texel++) {
                var low = BinaryPrimitives.ReadUInt32LittleEndian(source: first.Map.AsSpan(start: (texel * SdfSkyEnvironment.TexelBytes)));
                var high = BinaryPrimitives.ReadUInt32LittleEndian(source: first.Map.AsSpan(start: ((texel * SdfSkyEnvironment.TexelBytes) + 4)));

                map[texel] = new Vector3(
                    x: ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(low & 0xFFFFu)))),
                    y: ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(low >> 16)))),
                    z: ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(high & 0xFFFFu))))
                );
                foreach (var (actual, reference) in new[] { (map[texel].X, expected[texel].X), (map[texel].Y, expected[texel].Y), (map[texel].Z, expected[texel].Z) }) {
                    Assert.True(condition: (MathF.Abs(x: (actual - reference)) <= HalfStep(value: reference)), userMessage: $"Texel {texel} holds {map[texel]}; the reference holds {expected[texel]}.");
                }
                var reflectionOffset = ((SdfSkyEnvironment.Texels + texel) * SdfSkyEnvironment.TexelBytes);

                for (var channel = 0; (channel < 3); channel++) {
                    var actual = ((float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(first.Map.AsSpan((reflectionOffset + (channel * 2))))));
                    var reference = expectedReflection[texel][channel];

                    Assert.True((MathF.Abs((actual - reference)) <= HalfStep(reference)), $"Reflection texel {texel}, channel {channel}: {actual}, reference {reference}.");
                }
            }

            var projected = new Vector3[Coefficients];

            SdfSkyEnvironment.Project(coefficients: projected, map: map);

            var bound = (1e-4f * MathF.Max(x: projected[0].X, y: MathF.Max(x: projected[0].Y, y: projected[0].Z)));

            for (var index = 0; (index < Coefficients); index++) {
                var offset = (index * 16);
                var actual = new Vector3(
                    x: BinaryPrimitives.ReadSingleLittleEndian(source: first.Coefficients.AsSpan(start: offset)),
                    y: BinaryPrimitives.ReadSingleLittleEndian(source: first.Coefficients.AsSpan(start: (offset + 4))),
                    z: BinaryPrimitives.ReadSingleLittleEndian(source: first.Coefficients.AsSpan(start: (offset + 8)))
                );
                var difference = Vector3.Abs(value: (actual - projected[index]));

                Assert.True(condition: (MathF.Max(x: difference.X, y: MathF.Max(x: difference.Y, y: difference.Z)) <= bound), userMessage: $"Coefficient {index} is {actual}; the reference projects {projected[index]}.");
            }
        }
    }
    private static SdfSky Lit<T>(T parameters) where T : unmanaged, ISdfSkyKind {
        var sky = new SdfSky();

        sky.ClearLayers();
        sky.Add(parameters, "lit", visibility: SdfSkyVisibility.Lighting);
        return sky;
    }
    private static SdfSky Panels() {
        var sky = new SdfSky();

        sky.Add(new SdfSkyPanel { Direction = Vector3.UnitY, Size = new Vector2(.2f, .6f), Color = new Vector3(1f, .5f, .2f), Intensity = 2f, Blur = .1f }, "key", blend: SdfSkyBlend.Add, visibility: SdfSkyVisibility.Lighting);
        sky.Add(new SdfSkyPanel { Direction = Vector3.UnitZ, Size = new Vector2(.4f, .1f), Color = Vector3.One, Intensity = .5f, Blur = .05f }, "fill", blend: SdfSkyBlend.Add, visibility: SdfSkyVisibility.Lighting);
        return sky;
    }
    // A sky of four stops, a bright disc on the default sun and clouds over most of it.
    private static SdfSky FourStops() {
        var sky = new SdfSky();
        var gradient = new SdfSkyGradient { Count = 4u };

        gradient.SetStop(color: new Vector3(x: 0.05f, y: 0.05f, z: 0.06f), elevation: -1f, index: 0);
        gradient.SetStop(color: new Vector3(x: 0.3f, y: 0.3f, z: 0.32f), elevation: -0.1f, index: 1);
        gradient.SetStop(color: new Vector3(x: 0.9f, y: 0.8f, z: 0.6f), elevation: 0.1f, index: 2);
        gradient.SetStop(color: new Vector3(x: 0.2f, y: 0.4f, z: 0.9f), elevation: 1f, index: 3);
        sky.ClearLayers();
        _ = sky.Add(label: "gradient", parameters: gradient, visibility: SdfSkyVisibility.Both);
        _ = sky.Add(blend: SdfSkyBlend.Add, label: "disc", parameters: new SdfSkyDisc { Intensity = 50f, Light = 0 }, visibility: SdfSkyVisibility.Both);
        _ = sky.Add(label: "clouds", parameters: new SdfSkyClouds { Coverage = 0.6f });

        return sky;
    }
    // A tilted sky frame, and over the default look a second gradient the lighting alone sees, turned and tilted, masked to
    // a soft cone and multiplied over the first.
    private static SdfSky Tilted() {
        var sky = new SdfSky { FrameUp = new Vector3(x: 0.3f, y: 0.9f, z: -0.2f) };
        var tint = new SdfSkyGradient { Count = 2u };

        tint.SetStop(color: new Vector3(x: 1f, y: 0.6f, z: 0.3f), elevation: -0.5f, index: 0);
        tint.SetStop(color: new Vector3(x: 0.4f, y: 0.9f, z: 1f), elevation: 0.5f, index: 1);

        var index = sky.Add(blend: SdfSkyBlend.Multiply, label: "tint", opacity: 0.8f, parameters: tint, visibility: SdfSkyVisibility.Lighting);
        ref var layer = ref sky.LayerAt(index: index);

        layer.Rotation = SdfSkyLayer.RotationOf(tilt: 0.4d, turn: 1.1d);
        layer.Mask = SdfSkyMask.Cone;
        layer.MaskBand = new Vector4(x: 0f, y: 1f, z: 0f, w: MathF.Cos(x: 1.2f));
        layer.MaskSoftness = 0.2f;

        return sky;
    }
    // The spacing of half floats about a value: the step a rounding of a float evaluated in another order may move by.
    private static float HalfStep(float value) {
        var half = ((Half)value);

        return ((float)(BitConverter.UInt16BitsToHalf(value: ((ushort)(BitConverter.HalfToUInt16Bits(value: half) + 1))) - half));
    }
    // Runs the map and the reduction once over a sky and returns the map's and the coefficients' bytes and the counts.
    private static (byte[] Map, byte[] Coefficients, long Evaluations, long MapTexels, long ReduceTexels) Run(GpuDeviceServices services, string extension, SdfSkyBlock block, SdfSkyLayer[] layers) {
        var parameters = SdfWorldInterfaces.EnvironmentParameters;
        var layout = parameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute);
        var blockBytes = new byte[parameters.SizeBytes];

        parameters.WriteExtent(block: blockBytes, height: SdfSkyEnvironment.Size, width: SdfSkyEnvironment.Size);

        var padded = new byte[((((((ulong)blockBytes.Length) + IGpuBindings.ConstantBufferAlignment) - 1UL) / IGpuBindings.ConstantBufferAlignment) * IGpuBindings.ConstantBufferAlignment)];
        var framePadded = new byte[((((((ulong)parameters.FrameBlockSizeBytes) + IGpuBindings.ConstantBufferAlignment) - 1UL) / IGpuBindings.ConstantBufferAlignment) * IGpuBindings.ConstantBufferAlignment)];
        // The map's row and the reduction's; both name the detail rows from DetailRow, where each lit layer counts its
        // evaluations in its own row, and the reduction evaluates none.
        var rows = new[] { 0u, 1u };

        IGpuComputePipeline Pipeline(IGpuShaderModule module, string name) =>
            services.PipelineFactory.Create(computeShaderModule: module, description: new GpuComputePipelineDescription(Bindings: [], Layout: layout, Name: name, PushConstantBinding: null), name: default);
        IGpuShaderModule Module(string stem) =>
            services.ShaderModuleFactory.Create(bytecode: File.ReadAllBytes(path: Path.Combine(path1: SdfKernelSet.DefaultDirectory, path2: (stem + extension))), stage: GpuShaderStage.Compute);

        using var mapModule = Module(stem: "sdf-sky-environment.comp");
        using var reduceModule = Module(stem: "sdf-sky-environment-reduce.comp");
        using var mapPipeline = Pipeline(module: mapModule, name: "sdf-sky-environment-proof");
        using var reducePipeline = Pipeline(module: reduceModule, name: "sdf-sky-environment-reduce-proof");
        using var frame = services.BufferFactory.CreateHostVisible(data: framePadded, name: default, usage: GpuBufferUsage.Uniform);
        using var skyBuffer = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: new[] { block }.AsSpan()), name: default, usage: GpuBufferUsage.Storage);
        using var layerBuffer = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: layers.AsSpan()), name: default, usage: GpuBufferUsage.Storage);
        using var map = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: SdfSkyEnvironment.MapBytes, usage: GpuBufferUsage.Storage);
        using var coefficients = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: SdfSkyEnvironment.CoefficientBytes, usage: GpuBufferUsage.Storage);
        // Two pass rows, an unused row, then one detail row each of the sky's detail rows.
        using var counters = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: ((ulong)((DetailRow + SdfSkyDetails.Capacity) * GpuKernelCounters.RowBytes)), usage: GpuBufferUsage.Storage);
        using var mapRead = services.BufferFactory.CreateReadback(name: default, sizeBytes: SdfSkyEnvironment.MapBytes);
        using var coefficientRead = services.BufferFactory.CreateReadback(name: default, sizeBytes: SdfSkyEnvironment.CoefficientBytes);
        using var counterRead = services.BufferFactory.CreateReadback(name: default, sizeBytes: counters.SizeBytes);
        using var commands = services.CommandPoolFactory.Create(name: default);
        var pool = services.Bindings.CreatePool(name: default, sizes: (GpuDescriptorPoolSizes.ForGroups(groups: layout.Groups) + GpuDescriptorPoolSizes.ForGroups(groups: layout.Groups)));
        var blocks = new List<IGpuStorageBuffer>();

        try {
            uint Binding(string member) => SdfKernelInterfaces.BindingOf(layout: parameters.Layout, member: member);
            var buffers = new Dictionary<uint, IGpuBuffer> {
                [Binding(member: SdfKernelInterfaces.Sky)] = skyBuffer,
                [Binding(member: SdfKernelInterfaces.SkyLayers)] = layerBuffer,
                [Binding(member: SdfKernelInterfaces.SkyEnvironmentWritten)] = map,
                [Binding(member: SdfKernelInterfaces.SkyCoefficientsWritten)] = coefficients,
                [Binding(member: ShaderWorkCounters.Buffer)] = counters,
            };
            // One frame set and one pass set per kernel, each pass block naming its kernel's counter row.
            var sets = new List<(uint Group, nint Set)>[2];

            for (var kernel = 0; (kernel < 2); kernel++) {
                BinaryPrimitives.WriteUInt32LittleEndian(destination: blockBytes.AsSpan(start: ((int)parameters.BlockOffsetOf(member: ShaderWorkCounters.Row))), value: rows[kernel]);
                BinaryPrimitives.WriteUInt32LittleEndian(destination: blockBytes.AsSpan(start: ((int)parameters.BlockOffsetOf(member: ShaderWorkCounters.DetailRow))), value: ((uint)DetailRow));
                blockBytes.CopyTo(array: padded, index: 0);

                var passBlock = services.BufferFactory.CreateHostVisible(data: padded, name: default, usage: GpuBufferUsage.Uniform);

                blocks.Add(item: passBlock);
                sets[kernel] = [];
                foreach (var group in parameters.Layout.Groups) {
                    var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: mapPipeline.GroupLayoutHandles[((int)group.Set)], name: default, poolHandle: pool);
                    var pass = (group.Group == ShaderInterfaceGroup.Pass);

                    sets[kernel].Add(item: (group.Set, set));
                    foreach (var binding in group.Bindings.Where(predicate: static binding => !binding.Pushed)) {
                        if (binding.Kind == GpuBindingKind.ConstantBuffer) {
                            var constants = (pass ? passBlock : frame);

                            services.Bindings.WriteConstantBuffer(arrayElement: 0, binding: binding.Binding, bufferHandle: constants.BufferHandle, bufferSize: constants.SizeBytes, descriptorSetHandle: set);
                        } else {
                            var storage = buffers[binding.Binding];

                            services.Bindings.WriteBuffer(binding: binding.Binding, bufferHandle: storage.BufferHandle, bufferSize: storage.SizeBytes, descriptorSetHandle: set, elementStride: binding.ElementStride, kind: binding.Kind);
                        }
                    }
                }
            }

            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            void Barrier(IGpuBuffer buffer, GpuAccess source, GpuStage sourceStage, GpuAccess destination, GpuStage destinationStage) =>
                recorder.TransitionBuffer(bufferHandle: buffer.BufferHandle, commandBufferHandle: command, destinationAccessMask: destination, destinationStageMask: destinationStage, sourceAccessMask: source, sourceStageMask: sourceStage);
            void Dispatch(IGpuComputePipeline pipeline, List<(uint Group, nint Set)> kernelSets, uint groups) {
                recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
                foreach (var (group, set) in kernelSets) {
                    recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: group, pipelineLayoutHandle: pipeline.LayoutHandle);
                }
                recorder.Dispatch(commandBufferHandle: command, groupCountX: groups, groupCountY: groups, groupCountZ: 1);
            }

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            Barrier(buffer: counters, destination: GpuAccess.TransferWrite, destinationStage: GpuStage.Transfer, source: GpuAccess.None, sourceStage: GpuStage.TopOfPipe);
            recorder.ClearStorageBuffer(bufferHandle: counters.BufferHandle, commandBufferHandle: command, sizeBytes: counters.SizeBytes);
            Barrier(buffer: counters, destination: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStage: GpuStage.ComputeShader, source: GpuAccess.TransferWrite, sourceStage: GpuStage.Transfer);
            Barrier(buffer: map, destination: GpuAccess.ShaderWrite, destinationStage: GpuStage.ComputeShader, source: GpuAccess.None, sourceStage: GpuStage.TopOfPipe);
            Barrier(buffer: coefficients, destination: GpuAccess.ShaderWrite, destinationStage: GpuStage.ComputeShader, source: GpuAccess.None, sourceStage: GpuStage.TopOfPipe);
            Dispatch(groups: (SdfSkyEnvironment.Size / 8), kernelSets: sets[0], pipeline: mapPipeline);
            Barrier(buffer: map, destination: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStage: GpuStage.ComputeShader, source: GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
            Dispatch(groups: 1u, kernelSets: sets[1], pipeline: reducePipeline);
            foreach (var buffer in new IGpuBuffer[] { map, coefficients, counters }) {
                Barrier(buffer: buffer, destination: GpuAccess.TransferRead, destinationStage: GpuStage.Transfer, source: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
            }
            recorder.CopyBuffer(commandBufferHandle: command, destinationBufferHandle: mapRead.BufferHandle, sizeBytes: map.SizeBytes, sourceBufferHandle: map.BufferHandle);
            recorder.CopyBuffer(commandBufferHandle: command, destinationBufferHandle: coefficientRead.BufferHandle, sizeBytes: coefficients.SizeBytes, sourceBufferHandle: coefficients.BufferHandle);
            recorder.CopyBuffer(commandBufferHandle: command, destinationBufferHandle: counterRead.BufferHandle, sizeBytes: counters.SizeBytes, sourceBufferHandle: counters.BufferHandle);
            foreach (var read in new IGpuBuffer[] { mapRead, coefficientRead, counterRead }) {
                Barrier(buffer: read, destination: GpuAccess.HostRead, destinationStage: GpuStage.Host, source: GpuAccess.TransferWrite, sourceStage: GpuStage.Transfer);
            }
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);

            var mapBytes = new byte[SdfSkyEnvironment.MapBytes];
            var coefficientBytes = new byte[SdfSkyEnvironment.CoefficientBytes];
            var counted = new byte[counters.SizeBytes];

            mapRead.Read(destination: mapBytes);
            coefficientRead.Read(destination: coefficientBytes);
            counterRead.Read(destination: counted);

            // Each row holds the kernel kinds as 64-bit counts in GpuWork.KernelKinds order: steps, texels, then sky.
            long Count(int row, int kind) => BinaryPrimitives.ReadInt64LittleEndian(source: counted.AsSpan(start: ((row * GpuKernelCounters.RowBytes) + ((kind * GpuKernelCounters.CountWords) * sizeof(uint)))));

            Assert.Equal(expected: 0L, actual: Count(kind: 2, row: 0));
            Assert.Equal(expected: 0L, actual: Count(kind: 2, row: 1));
            // The unused row between the pass rows and the detail rows counts nothing.
            Assert.Equal(expected: 0L, actual: Count(kind: 1, row: 2));

            var evaluations = 0L;

            for (var row = DetailRow; (row < (DetailRow + SdfSkyDetails.Capacity)); row++) {
                evaluations += Count(kind: 2, row: row);
            }

            return (mapBytes, coefficientBytes, evaluations, Count(kind: 1, row: 0), Count(kind: 1, row: 1));
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
            foreach (var passBlock in blocks) {
                passBlock.Dispose();
            }
        }
    }
}
