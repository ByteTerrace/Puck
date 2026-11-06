using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Both hardware backends execute the production glass accumulation (<c>frame/sdf-glass.hlsli</c>) over the
/// same faces in many orders.
/// <para>Raster order is unspecified, so the sums a pixel's faces leave must not depend on it: every order of one
/// case's faces leaves identical words, but for the second record's front entries, which hold the same set of front
/// faces (each case has at most three) in whatever order they claimed them, and its nearest back face is the nearest
/// back face before the opaque hit. The decoded optical depth of each channel is the exact Beer-Lambert integral,
/// the sum over each solid's interval before the opaque hit of its material's absorption times the interval's length,
/// to within the encoding's quantization; it carries no error proportional to the opaque hit's distance, so glass seen
/// against the far sky reads as it does against a near wall.</para></summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfGlassSumsDeviceLawTests {
    private const string Kernel = "sdf-glass-sums.comp";
    private const int Orders = 24;
    private const int Words = (int)SdfWorldPackage.GlassSumWords;
    private const int Rows = (1 + ((Words + 1) / 2));
    // The second record's words, frame/sdf-glass.hlsli's SdfGlassFrontCountWord on.
    private const int FrontCountWord = 8;
    private const int FrontEntryWord = 9;
    private const int FrontEntries = 3;
    private const int NearestBackWord = 15;

    // A face: its ray distance, the opaque hit's distance, whether it faces the camera, and its absorption's index.
    private readonly record struct Face(float T, float Opaque, bool Front, int Absorption);
    private sealed record GlassCase(string Name, Vector3[] Absorptions, Face[] Faces, Vector3 Expected, float Thickness);

    [Fact]
    public void VulkanAccumulatesGlassIndependentlyOfRasterOrder() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfGlassSumsDeviceLawTests));

        Verify(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXAccumulatesGlassIndependentlyOfRasterOrder() {
        using var device = DirectXTestDevices.Hardware();

        Verify(services: device.Services, extension: ".dxil");
    }

    // The debug world's materials: the devices' orange acrylic (9 mm deep), the reservoir water and the pavilion's
    // window glass, through the glass contract's own absorption.
    private static readonly Vector3 Acrylic = new SdfGlass(Depth: 0.009f, Ior: 1.49f).Absorption(tint: new Vector3(x: 1f, y: (79f / 255f), z: (16f / 255f)));
    private static readonly Vector3 Water = new SdfGlass(Depth: 2f, Ior: 1.333f).Absorption(tint: new Vector3(x: 0.3f, y: 0.6f, z: 0.65f));
    private static readonly Vector3 Window = new SdfGlass(Depth: 8f, Ior: 1.5f).Absorption(tint: new Vector3(x: (233f / 255f), y: (245f / 255f), z: (244f / 255f)));

    private static GlassCase Solids(string name, float opaque, params (float Front, float Back, Vector3 Absorption)[] solids) {
        var absorptions = solids.Select(selector: solid => solid.Absorption).Distinct().ToArray();
        var faces = new List<Face>();
        var expected = Vector3.Zero;
        var thickness = 0f;

        foreach (var (front, back, absorption) in solids) {
            var index = Array.IndexOf(array: absorptions, value: absorption);

            // A face at or behind the opaque hit is never accumulated (the raster's own test).
            faces.Add(item: new Face(T: front, Opaque: opaque, Front: true, Absorption: index));
            if (back < opaque) { faces.Add(item: new Face(T: back, Opaque: opaque, Front: false, Absorption: index)); }
            var length = (Math.Min(val1: back, val2: opaque) - front);

            expected += (absorption * length);
            thickness += length;
        }
        return new GlassCase(Absorptions: absorptions, Expected: expected, Faces: faces.ToArray(), Name: name, Thickness: thickness);
    }
    private static GlassCase[] Cases() => [
        // Two panes and a body of water against the sky at the debug world's far distance.
        Solids("panes against the sky", 6000f, (2f, 2.009f, Acrylic), (3f, 3.012f, Window), (40f, 42.5f, Water)),
        // The same against a near wall.
        Solids("panes against a wall", 50f, (2f, 2.009f, Acrylic), (3f, 3.012f, Window), (40f, 42.5f, Water)),
        // A window into water whose floor stands inside the water: the floor clips the water's interval.
        Solids("window into water", 61.25f, (60f, 60.012f, Window), (61f, 63.5f, Water)),
        // Two acrylic panes far from the camera and the sky.
        Solids("far panes", 6000f, (900f, 900.009f, Acrylic), (902f, 902.009f, Acrylic)),
    ];
    private static void Verify(GpuDeviceServices services, string extension) {
        foreach (var glassCase in Cases()) {
            var orders = OrdersOf(count: glassCase.Faces.Length, seed: glassCase.Name.Length);
            var results = Run(extension: extension, glassCase: glassCase, orders: orders, services: services);
            var words = Enumerable.Range(start: 0, count: Orders).Select(selector: order => WordsOf(results: results, order: order)).ToArray();

            var fronts = glassCase.Faces.Select(selector: static (face, index) => (face, index)).Where(predicate: static pair => pair.face.Front)
                .Select(selector: static pair => (((uint)pair.index + 1u), ((3u * (uint)pair.index) + 1u))).Order().ToArray();
            var backs = glassCase.Faces.Where(predicate: static face => !face.Front).Select(selector: static face => face.T).ToArray();

            for (var order = 0; (order < Orders); order++) {
                var held = words[order];
                var entries = Enumerable.Range(start: 0, count: Math.Min(val1: fronts.Length, val2: FrontEntries))
                    .Select(selector: entry => (held[(FrontEntryWord + (2 * entry))], held[(FrontEntryWord + (2 * entry) + 1)])).Order().ToArray();

                Assert.True(condition: (words[0].AsSpan(start: 0, length: FrontEntryWord).SequenceEqual(other: held.AsSpan(start: 0, length: FrontEntryWord)) &&
                    (words[0][NearestBackWord] == held[NearestBackWord])),
                    userMessage: $"{glassCase.Name}: order {order} left [{string.Join(separator: ' ', values: held)}], order 0 [{string.Join(separator: ' ', values: words[0])}].");
                Assert.Equal(expected: (uint)fronts.Length, actual: held[FrontCountWord]);
                Assert.Equal(expected: fronts, actual: entries);
                Assert.Equal(expected: ((backs.Length == 0) ? 0u : ~BitConverter.SingleToUInt32Bits(value: backs.Min())), actual: held[NearestBackWord]);
            }
            var decoded = results[0];
            // Each face's distance to the opaque hit is quantized to 2^-15 m and each absorption to 2^-16 per metre.
            var tolerance = glassCase.Faces.Aggregate(seed: new Vector3(value: 1e-6f), func: (sum, face) =>
                sum + (glassCase.Absorptions[face.Absorption] * (1f / 32768f)) + new Vector3(value: ((face.Opaque - face.T) / 65536f)));

            for (var channel = 0; (channel < 3); channel++) {
                Assert.True(condition: (MathF.Abs(x: (decoded[channel] - glassCase.Expected[channel])) <= tolerance[channel]),
                    userMessage: $"{glassCase.Name}: channel {channel} decoded {decoded[channel]}, expected {glassCase.Expected[channel]} within {tolerance[channel]}.");
            }
            Assert.InRange(actual: decoded.W, low: (glassCase.Thickness - (glassCase.Faces.Length / 4096f)), high: (glassCase.Thickness + (glassCase.Faces.Length / 4096f)));
        }
    }
    // The first order is the faces as listed; the rest are seeded shuffles.
    private static uint[] OrdersOf(int count, int seed) {
        var random = new Random(Seed: seed);
        var orders = new uint[(Orders * count)];

        for (var order = 0; (order < Orders); order++) {
            var indices = Enumerable.Range(start: 0, count: count).Select(selector: index => ((uint)index)).ToArray();

            if (order > 0) { random.Shuffle(values: indices); }
            indices.CopyTo(array: orders, index: (order * count));
        }
        return orders;
    }
    private static uint[] WordsOf(Vector4[] results, int order) {
        var words = new uint[Words];

        for (var texel = 0; (texel < (Rows - 1)); texel++) {
            var halves = results[(((1 + texel) * Orders) + order)];

            words[(2 * texel)] = (((uint)halves.X) | (((uint)halves.Y) << 16));
            if (((2 * texel) + 1) < Words) { words[((2 * texel) + 1)] = (((uint)halves.Z) | (((uint)halves.W) << 16)); }
        }
        return words;
    }
    private static Vector4[] Run(GpuDeviceServices services, string extension, GlassCase glassCase, uint[] orders) {
        var faces = glassCase.Faces.Select(selector: face => new Vector4(x: face.T, y: face.Opaque, z: (face.Front ? 1f : 0f), w: face.Absorption)).ToArray();
        var absorptions = glassCase.Absorptions.Select(selector: absorption => new Vector4(value: absorption, w: 0f)).ToArray();
        var description = new GpuComputePipelineDescription(Bindings: [], Name: Kernel, PushConstantBinding: null,
            Layout: new GpuPipelineLayoutDescription(groups: [new GpuGroupLayoutDescription(ordinal: 3, bindings: [
                new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ReadOnlyBuffer),
                new GpuGroupBinding(binding: 1, kind: GpuBindingKind.ReadOnlyBuffer),
                new GpuGroupBinding(binding: 2, kind: GpuBindingKind.ReadOnlyBuffer),
                new GpuGroupBinding(binding: 3, kind: GpuBindingKind.ReadWriteBuffer),
                new GpuGroupBinding(binding: 4, kind: GpuBindingKind.StorageImage),
            ])], pushesIndex: false, stages: GpuShaderStage.Compute));
        using var module = services.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (Kernel + extension))));
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var faceBuffer = services.BufferFactory.CreateHostVisible(name: default, sizeBytes: ((ulong)faces.Length * 16UL), usage: GpuBufferUsage.Storage);
        using var absorptionBuffer = services.BufferFactory.CreateHostVisible(name: default, sizeBytes: ((ulong)absorptions.Length * 16UL), usage: GpuBufferUsage.Storage);
        using var orderBuffer = services.BufferFactory.CreateHostVisible(name: default, sizeBytes: ((ulong)orders.Length * 4UL), usage: GpuBufferUsage.Storage);
        using var sums = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: (Orders * (ulong)Words * 4UL), usage: GpuBufferUsage.Storage);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R32G32B32A32Float, height: Rows, name: default,
            usage: GpuImageUsage.Storage, width: Orders);
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var pool = services.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(groups: description.Layout!.Groups));

        try {
            faceBuffer.Write<Vector4>(data: faces);
            absorptionBuffer.Write<Vector4>(data: absorptions);
            orderBuffer.Write<uint>(data: orders);
            var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[3], name: default, poolHandle: pool);

            services.Bindings.WriteBuffer(binding: 0, bufferHandle: faceBuffer.BufferHandle, bufferSize: faceBuffer.SizeBytes,
                descriptorSetHandle: set, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
            services.Bindings.WriteBuffer(binding: 1, bufferHandle: absorptionBuffer.BufferHandle, bufferSize: absorptionBuffer.SizeBytes,
                descriptorSetHandle: set, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
            services.Bindings.WriteBuffer(binding: 2, bufferHandle: orderBuffer.BufferHandle, bufferSize: orderBuffer.SizeBytes,
                descriptorSetHandle: set, elementStride: 4, kind: GpuBindingKind.ReadOnlyBuffer);
            services.Bindings.WriteBuffer(binding: 3, bufferHandle: sums.BufferHandle, bufferSize: sums.SizeBytes,
                descriptorSetHandle: set, elementStride: 4, kind: GpuBindingKind.ReadWriteBuffer);
            services.Bindings.WriteStorageImage(arrayElement: 0, binding: 4, descriptorSetHandle: set, imageViewHandle: output.ImageViewHandle);
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.ClearStorageBuffer(commandBufferHandle: command, bufferHandle: sums.BufferHandle, sizeBytes: sums.SizeBytes);
            recorder.TransitionImageLayout(commandBufferHandle: command, imageHandle: output.ImageHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, oldLayout: GpuImageLayout.Undefined,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader, newLayout: GpuImageLayout.General);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: 3, pipelineLayoutHandle: pipeline.LayoutHandle);
            recorder.Dispatch(commandBufferHandle: command, groupCountX: Orders, groupCountY: 1, groupCountZ: 1);
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            var result = readback.Read(bytesPerPixel: 16, format: GpuPixelFormat.R32G32B32A32Float, height: Rows, width: Orders,
                sourceImageHandle: output.ImageHandle, sourceLayout: GpuImageLayout.General);

            return MemoryMarshal.Cast<byte, Vector4>(span: result.Span).ToArray();
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
        }
    }
}
