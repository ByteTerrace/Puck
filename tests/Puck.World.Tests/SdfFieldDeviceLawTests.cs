using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The device half of the instruction set. The probe kernel (<c>Assets/Shaders/sdf-field.comp.hlsl</c>) runs the shipped
/// interpreter's <c>map</c> over programs packed by <see cref="SdfProgram"/> and bound where the world kernels bind them,
/// at a lattice of points, and each program's answers are held to a reference the host computes:
/// <list type="bullet">
/// <item><description>every <see cref="SdfEncodingProbe"/> call whose program <see cref="SdfFieldEvaluator"/> accepts,
/// against that evaluator;</description></item>
/// <item><description>every op the evaluator refuses that has an equivalent at one point, against the evaluator over the
/// equivalent program at that point: a plane rotation or a polar repeat as the <see cref="SdfOp.Rotate"/> taking the
/// point where the op takes it, a shear, a Gaussian push or a domain warp as the <see cref="SdfOp.Translate"/> that does,
/// and a displacement as the <see cref="SdfOp.Dilate"/> of the relief it adds;</description></item>
/// <item><description>and, as the red leg, programs whose words carry two lanes exchanged, which must miss their
/// reference somewhere, so the tolerance is tight enough to see a lane read from the wrong place.</description></item>
/// </list>
/// A distance matches within <see cref="Tolerance"/> once the program's step scale, which the kernel applies to what
/// <c>map</c> returns and the evaluator does not, is divided out; a material matches exactly. It runs on the first Vulkan
/// device with a graphics queue and on the first Direct3D 12 hardware adapter. The software (WARP) renderer is left out:
/// it does not finish one program's lattice through the full interpreter in minutes.
/// </summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed partial class SdfFieldDeviceLawTests {
    // The largest distance the device and the evaluator may disagree by: the evaluator computes in Q48.16 fixed point,
    // the device in single precision, and the trigonometry and exponentials of the warps differ by a few ulps between
    // them and between the backends. The programs that match miss by under a tenth of it; every red leg misses by at
    // least twenty times it.
    private const float Tolerance = 1e-3f;
    private const string KernelName = "sdf-field.comp";
    private const uint WorldGroup = 1U;
    private const uint PassGroup = 3U;
    private const uint WordsBinding = 0U;
    private const uint CasesBinding = 60U;
    private const uint ResultsBinding = 61U;
    // The results image's width, the kernel's FieldResultsWidth.
    private const uint ResultsWidth = 256U;
    private const uint GroupWidth = 64U;

    // The lattice every program is evaluated at: sixteenths, so each point is exact in both single precision and Q48.16,
    // and off every axis and diagonal plane a fold's seam lies on.
    private static readonly float[] Coordinates = [-1.1875f, -0.5625f, 0.0625f, 0.6875f, 1.3125f];
    private static readonly Vector3[] Points = [.. Coordinates.SelectMany(selector: static x => Coordinates.SelectMany(selector: y => Coordinates.Select(selector: z => new Vector3(x: x, y: y, z: z))))];

    [Fact]
    public void AVulkanDeviceEvaluatesEveryProgramAsItsReferenceDoes() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        Verify(
            backend: $"vulkan ({device.Name})",
            kernel: Kernel(extension: ".spv"),
            services: device.Services
        );
    }
    [Fact]
    public void ADirect3D12DeviceEvaluatesEveryProgramAsItsReferenceDoes() {
        using (var context = DirectXTestDevices.Hardware()) {
            Verify(
                backend: "directx",
                kernel: Kernel(extension: ".dxil"),
                services: context.Services
            );
        }
    }

    private static byte[] Kernel(string extension) =>
        File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (KernelName + extension)));
    private static void Verify(string backend, byte[] kernel, GpuDeviceServices services) {
        var legs = Legs();
        var results = Run(
            kernel: kernel,
            legs: legs,
            services: services
        );
        var failures = new List<string>();

        for (var index = 0; (index < legs.Count); index++) {
            var leg = legs[index];
            var worst = 0f;
            var worstPoint = 0;
            var materialMisses = 0;

            for (var point = 0; (point < Points.Length); point++) {
                var result = results[((index * Points.Length) + point)];
                var miss = MathF.Abs(x: ((result.X / result.Z) - leg.Distances[point]));

                if (!(miss <= worst)) {
                    worst = miss;
                    worstPoint = point;
                }
                if (BitConverter.SingleToInt32Bits(value: result.Y) != leg.Materials[point]) {
                    materialMisses++;
                }
            }

            if (leg.Swapped) {
                if (worst <= Tolerance) {
                    failures.Add(item: $"{leg.Name}: the words with two lanes exchanged stayed within {Tolerance} of the reference everywhere (worst {worst})");
                }
            } else if (!(worst <= Tolerance) || (materialMisses != 0)) {
                failures.Add(item: $"{leg.Name}: worst distance miss {worst} at {Points[worstPoint]} (reference {leg.Distances[worstPoint]}), {materialMisses} material miss(es)");
            }
        }

        Assert.True(
            condition: (failures.Count == 0),
            userMessage: $"{backend}: {failures.Count} of {legs.Count} program(s) disagree with their reference:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: failures)}"
        );
    }
    // Packs every leg's cases, binds each leg's words at sdfWords, dispatches the probe once per leg and reads the
    // results back, one per case in leg order.
    private static Vector4[] Run(byte[] kernel, GpuDeviceServices services, IReadOnlyList<SdfFieldLeg> legs, Vector4[]? transforms = null) {
        var caseCount = checked((legs.Count * Points.Length));

        if ((Points.Length > 0xFFFF) || (caseCount > 0xFFFF)) {
            throw new InvalidOperationException(message: "The pushed index packs a leg's first case and count in 16 bits each.");
        }

        var height = ((((uint)caseCount) + (ResultsWidth - 1U)) / ResultsWidth);
        var worldGroup = new GpuGroupLayoutDescription(
            bindings: ((transforms is null)
                ? [new GpuGroupBinding(binding: WordsBinding, kind: GpuBindingKind.ReadOnlyBuffer)]
                : [new GpuGroupBinding(binding: WordsBinding, kind: GpuBindingKind.ReadOnlyBuffer), new GpuGroupBinding(binding: 1, kind: GpuBindingKind.ReadOnlyBuffer)]),
            ordinal: WorldGroup
        );
        var passGroup = new GpuGroupLayoutDescription(
            bindings: [
                .. ((transforms is null) ? Array.Empty<GpuGroupBinding>() : [new GpuGroupBinding(binding: 9, kind: GpuBindingKind.ReadWriteBuffer)]),
                new GpuGroupBinding(binding: CasesBinding, kind: GpuBindingKind.ReadOnlyBuffer),
                new GpuGroupBinding(binding: ResultsBinding, kind: GpuBindingKind.StorageImage),
            ],
            ordinal: PassGroup
        );
        var description = new GpuComputePipelineDescription(
            Bindings: [],
            Layout: new GpuPipelineLayoutDescription(
                groups: [worldGroup, passGroup],
                pushesIndex: true,
                stages: GpuShaderStage.Compute
            ),
            Name: KernelName,
            PushConstantBinding: null
        );
        var caseWords = new uint[(caseCount * 4)];

        for (var index = 0; (index < caseCount); index++) {
            var point = Points[(index % Points.Length)];

            caseWords[(index * 4)] = BitConverter.SingleToUInt32Bits(value: point.X);
            caseWords[((index * 4) + 1)] = BitConverter.SingleToUInt32Bits(value: point.Y);
            caseWords[((index * 4) + 2)] = BitConverter.SingleToUInt32Bits(value: point.Z);
            caseWords[((index * 4) + 3)] = ((uint)index);
        }

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
        using var output = services.ImageFactory.Create(
            format: GpuPixelFormat.R32G32B32A32Float,
            height: height,
            name: default,
            usage: GpuImageUsage.Storage,
            width: ResultsWidth
        );
        using var commands = services.CommandPoolFactory.Create(name: default);
        using var cases = services.BufferFactory.CreateHostVisible(
            name: default,
            sizeBytes: ((ulong)(caseWords.Length * sizeof(uint))),
            usage: GpuBufferUsage.Storage
        );
        using var dynamicTable = ((transforms is null) ? null : services.BufferFactory.CreateHostVisible(
            name: default, sizeBytes: (((ulong)transforms.Length) * 16), usage: GpuBufferUsage.Storage));
        using var visibility = ((transforms is null) ? null : services.BufferFactory.CreateDeviceLocal(
            name: default, sizeBytes: (((ulong)caseCount) * 64), usage: GpuBufferUsage.Storage));

        dynamicTable?.Write<Vector4>(data: transforms);
        var programs = new List<IGpuStorageBuffer>(capacity: legs.Count);

        cases.Write<uint>(data: caseWords);

        var bindings = services.Bindings;
        var pool = bindings.CreatePool(
            name: default,
            sizes: Enumerable.Repeat(
                count: legs.Count,
                element: GpuDescriptorPoolSizes.ForGroups(groups: [worldGroup])
            ).Aggregate(
                func: static (sum, sizes) => (sum + sizes),
                seed: GpuDescriptorPoolSizes.ForGroups(groups: [passGroup])
            )
        );
        ReadOnlyMemory<byte> values;

        try {
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;
            var passSet = bindings.AllocateSet(
                descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[((int)PassGroup)],
                name: default,
                poolHandle: pool
            );

            if (visibility is not null) {
                bindings.WriteBuffer(binding: 9, bufferHandle: visibility.BufferHandle, bufferSize: visibility.SizeBytes,
                    descriptorSetHandle: passSet, elementStride: 4, kind: GpuBindingKind.ReadWriteBuffer);
            }
            bindings.WriteBuffer(
                binding: CasesBinding,
                bufferHandle: cases.BufferHandle,
                bufferSize: cases.SizeBytes,
                descriptorSetHandle: passSet,
                elementStride: (4U * sizeof(uint)),
                kind: GpuBindingKind.ReadOnlyBuffer
            );
            bindings.WriteStorageImage(
                arrayElement: 0U,
                binding: ResultsBinding,
                descriptorSetHandle: passSet,
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
                descriptorSetHandle: passSet,
                group: PassGroup,
                pipelineLayoutHandle: pipeline.LayoutHandle
            );

            for (var index = 0; (index < legs.Count); index++) {
                var words = legs[index].Words;
                var program = services.BufferFactory.CreateHostVisible(
                    name: default,
                    sizeBytes: ((ulong)(words.Length * sizeof(uint))),
                    usage: GpuBufferUsage.Storage
                );

                programs.Add(item: program);
                program.Write<uint>(data: words);

                var worldSet = bindings.AllocateSet(
                    descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[((int)WorldGroup)],
                    name: default,
                    poolHandle: pool
                );

                if (dynamicTable is not null) {
                    bindings.WriteBuffer(binding: 1, bufferHandle: dynamicTable.BufferHandle, bufferSize: dynamicTable.SizeBytes,
                        descriptorSetHandle: worldSet, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
                }
                bindings.WriteBuffer(
                    binding: WordsBinding,
                    bufferHandle: program.BufferHandle,
                    bufferSize: program.SizeBytes,
                    descriptorSetHandle: worldSet,
                    elementStride: (4U * sizeof(uint)),
                    kind: GpuBindingKind.ReadOnlyBuffer
                );
                recorder.BindDescriptorSet(
                    bindPoint: GpuBindPoint.Compute,
                    commandBufferHandle: command,
                    descriptorSetHandle: worldSet,
                    group: WorldGroup,
                    pipelineLayoutHandle: pipeline.LayoutHandle
                );

                ReadOnlySpan<uint> pushed = [(((uint)(index * Points.Length)) | (((uint)Points.Length) << 16))];

                recorder.PushConstants(
                    bindPoint: GpuBindPoint.Compute,
                    commandBufferHandle: command,
                    data: MemoryMarshal.AsBytes(span: pushed),
                    offset: 0U,
                    pipelineLayoutHandle: pipeline.LayoutHandle,
                    stageFlags: GpuShaderStage.Compute
                );
                recorder.Dispatch(
                    commandBufferHandle: command,
                    groupCountX: ((((uint)Points.Length) + (GroupWidth - 1U)) / GroupWidth),
                    groupCountY: 1U,
                    groupCountZ: 1U
                );
            }

            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            values = readback.Read(
                bytesPerPixel: 16U,
                format: GpuPixelFormat.R32G32B32A32Float,
                height: height,
                sourceImageHandle: output.ImageHandle,
                sourceLayout: GpuImageLayout.General,
                width: ResultsWidth
            );
        } finally {
            bindings.DestroyPool(poolHandle: pool);

            foreach (var program in programs) {
                program.Dispose();
            }
        }

        return MemoryMarshal.Cast<byte, Vector4>(span: values.Span)[..caseCount].ToArray();
    }
    // The evaluator's distance and material at every point of the lattice.
    private static (float[] Distances, int[] Materials) Reference(SdfFieldEvaluator evaluator) {
        var distances = new float[Points.Length];
        var materials = new int[Points.Length];

        for (var point = 0; (point < Points.Length); point++) {
            (distances[point], materials[point]) = Reference(
                evaluator: evaluator,
                point: Points[point]
            );
        }

        return (distances, materials);
    }
    private static (float Distance, int Material) Reference(SdfFieldEvaluator evaluator, Vector3 point) {
        Assert.True(condition: evaluator.TryDistance(
            distance: out var distance,
            material: out var material,
            position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))
        ));

        return (((float)((double)distance)), material);
    }

    // One program the device evaluates at every point of the lattice, and its reference's distance and material there;
    // a swapped leg's words carry two lanes exchanged, and must miss the reference somewhere.
    private sealed record SdfFieldLeg(string Name, uint[] Words, float[] Distances, int[] Materials, bool Swapped = false);
}
