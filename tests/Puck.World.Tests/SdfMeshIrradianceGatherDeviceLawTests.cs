using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>THE LAW: the views pass's probe gather (<c>sdfMeshProbesAt</c>, <c>shade/sdf-mesh-irradiance.hlsli</c>) and
/// the CPU's (<see cref="SdfMeshIrradiance.TryGather(Vector3, Vector3, Span{ValueTuple{int, int, int}}, Span{float})"/>)
/// read the same probes with the same weights. A storeyed hall at the High tier is traced and published on the CPU; the
/// shipped gather then runs on both hardware backends over the published table words and the pass-block parameters
/// <see cref="SdfFrameBlock.WriteMeshIrradiance"/> packs, at receivers on floors, slab undersides, walls, an exhibit, an end
/// wall just past the finest window, the inside of a sealed room no coarser probe sees and surfaces past every window. Each receiver must answer from the same eight slots
/// (the level the CPU chose and its cells), each corner's weight within <see cref="WeightTolerance"/> of the CPU's, and as
/// measured or not alike.</summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed partial class SdfMeshIrradianceGatherDeviceLawTests {
    private const string Kernel = "sdf-mesh-irradiance.comp";
    private const int Rows = 5;
    // Float rounding apart from the CPU's double octahedral encode and the record's FP16 offsets.
    private const float WeightTolerance = 0.002f;

    [Fact]
    public void VulkanGathersTheProbesTheCpuGathers() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfMeshIrradianceGatherDeviceLawTests));

        Verify(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXGathersTheProbesTheCpuGathers() {
        using var device = DirectXTestDevices.Hardware();

        Verify(services: device.Services, extension: ".dxil");
    }

    private readonly record struct Receiver(Vector3 Point, Vector3 Normal);

    [GeneratedRegex(pattern: @"level=(\d) measured=(\d)")]
    private static partial Regex LevelPattern();

    private static void Verify(GpuDeviceServices services, string extension) {
        var volume = new SdfMeshIrradiance();
        var (parameters, words) = Trace(volume: volume, frame: Hall());
        var receivers = Receivers();
        var results = Run(extension: extension, parameters: parameters, receivers: receivers, services: services, words: words);
        var cells = new (int X, int Y, int Z)[8];
        var weights = new float[8];
        var levels = new int[SdfMeshIrradianceProbe.Levels];

        Assert.True(condition: parameters.Window(level: 0).Present && parameters.Middle.Present && parameters.Far.Present);
        for (var level = 1; (level < levels.Length); level++) { levels[level] = (levels[(level - 1)] + parameters.Window(level: (level - 1)).Count); }
        for (var index = 0; (index < receivers.Length); index++) {
            var receiver = receivers[index];

            Assert.True(condition: volume.TryGather(cells: cells, normal: receiver.Normal, point: receiver.Point, weights: weights));

            var match = LevelPattern().Match(input: volume.DescribeProbes(normal: receiver.Normal, point: receiver.Point)[0]);

            Assert.True(condition: match.Success);

            var level = int.Parse(s: match.Groups[1].Value, provider: CultureInfo.InvariantCulture);
            var measured = (match.Groups[2].Value == "1");
            var window = parameters.Window(level: level);
            var label = string.Create(provider: CultureInfo.InvariantCulture, handler: $"receiver {index} at {receiver.Point} facing {receiver.Normal}, level {level}");

            for (var corner = 0; (corner < 8); corner++) {
                var gpuWeight = results[(((corner >> 2) * receivers.Length) + index)][corner & 3];
                var gpuSlot = results[(((2 + (corner >> 2)) * receivers.Length) + index)][corner & 3];
                var cell = cells[corner];
                var slot = ((weights[corner] > 0f) ? (levels[level] + SdfMeshIrradiance.SlotOf(dims: window.Dims, x: cell.X, y: cell.Y, z: cell.Z)) : -1);

                Assert.True(condition: (MathF.Abs(x: (gpuWeight - weights[corner])) <= WeightTolerance),
                    userMessage: $"{label}: corner {corner} weighs {gpuWeight} on the GPU and {weights[corner]} on the CPU");
                if ((weights[corner] > WeightTolerance) || (gpuWeight > WeightTolerance)) {
                    Assert.True(condition: (gpuSlot == slot), userMessage: $"{label}: corner {corner} reads slot {gpuSlot} on the GPU and {slot} on the CPU");
                }
            }
            Assert.True(condition: ((results[((4 * receivers.Length) + index)].X == 1f) == measured), userMessage: $"{label}: measured differs");
        }
    }
    // Receivers over the hall's surfaces, an exhibit, the end wall just past the finest window, and floors past each window.
    private static Receiver[] Receivers() {
        var receivers = new List<Receiver>();

        for (var x = -9.3f; (x < 24f); x += 1.37f) {
            for (var z = -5.2f; (z < 5.5f); z += 2.31f) {
                receivers.Add(item: new Receiver(Normal: Vector3.UnitY, Point: new Vector3(x: x, y: 0f, z: z)));
                receivers.Add(item: new Receiver(Normal: -Vector3.UnitY, Point: new Vector3(x: x, y: 4f, z: z)));
                receivers.Add(item: new Receiver(Normal: Vector3.UnitY, Point: new Vector3(x: x, y: 4.3f, z: z)));
            }
            for (var y = 0.4f; (y < 8.2f); y += 1.13f) {
                if ((y > 3.9f) && (y < 4.4f)) { continue; }
                receivers.Add(item: new Receiver(Normal: Vector3.UnitZ, Point: new Vector3(x: x, y: y, z: -5.7f)));
            }
        }
        for (var y = 0.3f; (y < 8.2f); y += 0.71f) {
            for (var z = -5.4f; (z < 5.5f); z += 1.53f) {
                receivers.Add(item: new Receiver(Normal: -Vector3.UnitX, Point: new Vector3(x: EndWall, y: y, z: z)));
                receivers.Add(item: new Receiver(Normal: Vector3.UnitX, Point: new Vector3(x: -9.7f, y: y, z: z)));
            }
        }
        foreach (var normal in new[] { -Vector3.UnitX, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }) {
            receivers.Add(item: new Receiver(Normal: normal, Point: (new Vector3(x: 10.5f, y: 1f, z: 0f) + (normal * 0.5f))));
        }
        for (var y = 0.2f; (y < 3f); y += 0.7f) {
            receivers.Add(item: new Receiver(Normal: -Vector3.UnitX, Point: new Vector3(x: 63f, y: y, z: 1.65f)));
            receivers.Add(item: new Receiver(Normal: Vector3.UnitX, Point: new Vector3(x: 60.3f, y: y, z: 1.1f)));
        }
        foreach (var x in new[] { 30f, 70f, 140f, 400f, 2000f }) {
            receivers.Add(item: new Receiver(Normal: Vector3.UnitY, Point: new Vector3(x: x, y: 0f, z: 0f)));
        }
        return receivers.ToArray();
    }

    // The end wall's inner face, just past the High tier's finest window about a camera at the origin.
    private const float EndWall = 24.3f;

    private static SdfMesh Box(Vector3 low, Vector3 high) {
        Vector3[] positions = [
            new(x: low.X, y: low.Y, z: low.Z), new(x: high.X, y: low.Y, z: low.Z), new(x: high.X, y: high.Y, z: low.Z), new(x: low.X, y: high.Y, z: low.Z),
            new(x: low.X, y: low.Y, z: high.Z), new(x: high.X, y: low.Y, z: high.Z), new(x: high.X, y: high.Y, z: high.Z), new(x: low.X, y: high.Y, z: high.Z),
        ];
        uint[] indices = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5];

        return new SdfMesh(indices: indices, positions: positions);
    }
    private static SdfMeshDraw Draw(Vector3 low, Vector3 high) => new(Identity: new object(), Material: 0, Mesh: Box(high: high, low: low), ObjectToWorld: Matrix4x4.Identity);
    // A two-storey hall 12 m wide, its storeys 4 m apart over 0.3 m slabs, from x = -10 to the end wall, with an exhibit
    // block on its floor, a sunlit ground outside and a sealed room 3 m on a side 60 m out; the camera stands in the hall
    // at the origin.
    private static SdfFrame Hall() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.8f)));

        builder.Sphere(material: material, radius: 0.1f);

        var lights = new SdfLights();

        lights.Set(index: 0, light: new SdfLight(Color: Vector3.One, Direction: Vector3.Normalize(value: new Vector3(x: 0.3f, y: 1f, z: 0.2f)), Kind: SdfLightKind.Directional, Param: 0f, Shadows: true, Weight: 1000f));
        lights.Set(index: 1, light: new SdfLight(Color: Vector3.One, Direction: new Vector3(x: 5f, y: 3f, z: 0f), Kind: SdfLightKind.Point, Param: 20f, Shadows: true, Weight: SdfPhotometry.PositionalWeight(candela: 2000f)));
        lights.Count = 2;
        lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 0);

        var gains = new SdfIndirectGains(Emission: 0f, Feedback: 1f, Lights: 1f, Screens: 0f, Sky: 1f);

        return new SdfFrame(
            Program: builder.Build(),
            ProgramChanged: true,
            Views: [new SdfViewSnapshot(Camera: CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new Vector3(x: 0f, y: 2f, z: 0f), target: new Vector3(x: 1f, y: 2f, z: 0f), viewportHeight: 4u, viewportWidth: 4u), Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f))],
            Time: 0f
        ) {
            ConvergesLighting = true,
            FarDistance = 120f,
            IndirectBounces = 1,
            IndirectGains = gains,
            IndirectSources = gains.Sources,
            IndirectTier = SdfIndirectTier.High,
            Lights = lights,
            MeshDraws = [
                Draw(low: new Vector3(x: -200f, y: -0.6f, z: -200f), high: new Vector3(x: 2200f, y: -0.3f, z: 200f)),
                Draw(low: new Vector3(x: -10.3f, y: -0.3f, z: -6.3f), high: new Vector3(x: (EndWall + 0.3f), y: 0f, z: 6.3f)),
                Draw(low: new Vector3(x: -10.3f, y: 4f, z: -6.3f), high: new Vector3(x: (EndWall + 0.3f), y: 4.3f, z: 6.3f)),
                Draw(low: new Vector3(x: -10.3f, y: 8.3f, z: -6.3f), high: new Vector3(x: (EndWall + 0.3f), y: 8.6f, z: 6.3f)),
                Draw(low: new Vector3(x: -10.3f, y: 0f, z: -6.3f), high: new Vector3(x: (EndWall + 0.3f), y: 8.3f, z: -6f)),
                Draw(low: new Vector3(x: -10.3f, y: 0f, z: 6f), high: new Vector3(x: (EndWall + 0.3f), y: 8.3f, z: 6.3f)),
                Draw(low: new Vector3(x: -10.3f, y: 0f, z: -6f), high: new Vector3(x: -10f, y: 8.3f, z: 6f)),
                Draw(low: new Vector3(x: EndWall, y: 0f, z: -6f), high: new Vector3(x: (EndWall + 0.3f), y: 8.3f, z: 6f)),
                Draw(low: new Vector3(x: 10f, y: 0f, z: -0.5f), high: new Vector3(x: 11f, y: 2f, z: 0.5f)),
                Draw(low: new Vector3(x: 60f, y: -0.3f, z: 0f), high: new Vector3(x: 63.3f, y: 0f, z: 3.3f)),
                Draw(low: new Vector3(x: 60f, y: 3f, z: 0f), high: new Vector3(x: 63.3f, y: 3.3f, z: 3.3f)),
                Draw(low: new Vector3(x: 60f, y: 0f, z: 0f), high: new Vector3(x: 60.3f, y: 3f, z: 3.3f)),
                Draw(low: new Vector3(x: 63f, y: 0f, z: 0f), high: new Vector3(x: 63.3f, y: 3f, z: 3.3f)),
                Draw(low: new Vector3(x: 60.3f, y: 0f, z: 0f), high: new Vector3(x: 63f, y: 3f, z: 0.3f)),
                Draw(low: new Vector3(x: 60.3f, y: 0f, z: 3f), high: new Vector3(x: 63f, y: 3f, z: 3.3f)),
            ],
        };
    }
    // Stages the volume until it settles, returning the parameters and the table it last published.
    private static (SdfMeshIrradianceParameters Parameters, uint[] Words) Trace(SdfMeshIrradiance volume, SdfFrame frame) {
        var words = Array.Empty<uint>();
        var parameters = default(SdfMeshIrradianceParameters);
        var deadline = DateTime.UtcNow.AddSeconds(value: 300d);

        do {
            parameters = volume.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: memory => words = memory.ToArray());
            Thread.Sleep(millisecondsTimeout: 20);
        } while (volume.Settling && (DateTime.UtcNow < deadline));

        Assert.False(condition: volume.Settling, userMessage: volume.Describe());
        Assert.True(condition: (words.Length > 0), userMessage: volume.Describe());
        return (parameters, words);
    }
    private static Vector4[] Run(GpuDeviceServices services, string extension, SdfMeshIrradianceParameters parameters, Receiver[] receivers, uint[] words) {
        var block = new byte[SdfFrameBlock.SizeBytes];

        SdfFrameBlock.WriteMeshIrradiance(block: block, volume: in parameters);

        var volumeOffset = ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.MeshIrradianceVolume));
        var levelsOffset = ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.MeshIrradianceLevels));
        var packed = new uint[(4 * (1 + SdfMeshIrradianceProbe.Levels))];

        MemoryMarshal.Cast<byte, uint>(span: block.AsSpan(length: 16, start: volumeOffset)).CopyTo(destination: packed);
        MemoryMarshal.Cast<byte, uint>(span: block.AsSpan(length: (16 * SdfMeshIrradianceProbe.Levels), start: levelsOffset)).CopyTo(destination: packed.AsSpan(start: 4));

        var points = new Vector4[(2 * receivers.Length)];

        for (var index = 0; (index < receivers.Length); index++) {
            points[(2 * index)] = new Vector4(value: receivers[index].Point, w: 0f);
            points[((2 * index) + 1)] = new Vector4(value: receivers[index].Normal, w: 0f);
        }

        var width = ((uint)receivers.Length);
        var description = new GpuComputePipelineDescription(Bindings: [], Name: Kernel, PushConstantBinding: null,
            Layout: new GpuPipelineLayoutDescription(groups: [new GpuGroupLayoutDescription(ordinal: 3, bindings: [
                new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ReadOnlyBuffer),
                new GpuGroupBinding(binding: 1, kind: GpuBindingKind.ReadOnlyBuffer),
                new GpuGroupBinding(binding: 2, kind: GpuBindingKind.ReadOnlyBuffer),
                new GpuGroupBinding(binding: 3, kind: GpuBindingKind.StorageImage),
            ])], pushesIndex: false, stages: GpuShaderStage.Compute));
        using var module = services.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (Kernel + extension))));
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var table = services.BufferFactory.CreateHostVisible(name: default, sizeBytes: (((ulong)words.Length) * 4UL), usage: GpuBufferUsage.Storage);
        using var input = services.BufferFactory.CreateHostVisible(name: default, sizeBytes: (((ulong)points.Length) * 16UL), usage: GpuBufferUsage.Storage);
        using var levels = services.BufferFactory.CreateHostVisible(name: default, sizeBytes: (((ulong)packed.Length) * 4UL), usage: GpuBufferUsage.Storage);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R32G32B32A32Float, height: Rows, name: default,
            usage: GpuImageUsage.Storage, width: width);
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var pool = services.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(groups: description.Layout!.Groups));

        try {
            table.Write<uint>(data: words);
            input.Write<Vector4>(data: points);
            levels.Write<uint>(data: packed);
            var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[3], name: default, poolHandle: pool);

            services.Bindings.WriteBuffer(binding: 0, bufferHandle: table.BufferHandle, bufferSize: table.SizeBytes,
                descriptorSetHandle: set, elementStride: 4, kind: GpuBindingKind.ReadOnlyBuffer);
            services.Bindings.WriteBuffer(binding: 1, bufferHandle: input.BufferHandle, bufferSize: input.SizeBytes,
                descriptorSetHandle: set, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
            services.Bindings.WriteBuffer(binding: 2, bufferHandle: levels.BufferHandle, bufferSize: levels.SizeBytes,
                descriptorSetHandle: set, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
            services.Bindings.WriteStorageImage(arrayElement: 0, binding: 3, descriptorSetHandle: set, imageViewHandle: output.ImageViewHandle);
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
