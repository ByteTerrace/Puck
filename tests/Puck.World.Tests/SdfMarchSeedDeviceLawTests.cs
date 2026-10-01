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

/// <summary>The shared seed proposal and empty-ball admission run on both hardware backends. Hand-derived binary
/// intervals, analytic planes and the independent fixed field evaluator supply the answers; no history lookup or
/// production marcher is installed by these laws.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
public sealed class SdfMarchSeedDeviceLawTests {
    private const string Kernel = "sdf-march-seed.comp";
    private const float Candidate = 7.75f;
    private const float Midpoint = 4.875f;
    private const float Radius = 2.99609375f;

    private static readonly Vector3 Interval = new(x: Candidate, y: Midpoint, z: Radius);

    private sealed record SeedCase(string Name, Vector4 Input, bool Prepared, bool Admitted, Vector3 Expected,
        SdfProgram? Program = null, float FoldBound = float.PositiveInfinity, Vector2? FieldProbe = null);

    private static SeedCase[] Cases() {
        var cases = new List<SeedCase> {
            new("strict equality", new(w: Radius, x: 2, y: 8, z: (1f / 64)), true, false, Interval),
            new("one float below", new(2, 8, (1f / 64), MathF.BitDecrement(x: Radius)), true, false, Interval),
            new("one float above", new(2, 8, (1f / 64), MathF.BitIncrement(x: Radius)), true, true, Interval),
            new("epsilon backoff", new(w: 0.251f, x: 0, y: 0.5f, z: 0), true, true, new(x: 0.498f, y: 0.249f, z: 0.25f)),
            new("negative clearance", new(w: -100, x: 2, y: 8, z: (1f / 64)), true, false, Interval),
            new("zero clearance", new(w: 0, x: 2, y: 8, z: (1f / 64)), true, false, Interval),
            new("NaN clearance", new(w: float.NaN, x: 2, y: 8, z: (1f / 64)), true, false, Interval),
            new("infinite clearance", new(w: float.PositiveInfinity, x: 2, y: 8, z: (1f / 64)), true, false, Interval),
            new("negative infinite clearance", new(w: float.NegativeInfinity, x: 2, y: 8, z: (1f / 64)), true, false, Interval),
        };

        foreach (var (name, input) in new (string, Vector4)[] {
            ("reversed", new(w: 100, x: 3, y: 2, z: 0)), ("equal", new(w: 100, x: 2, y: 2, z: 0)),
            ("backoff consumes progress", new(w: 100, x: 7.8f, y: 8, z: (1f / 64))),
            ("negative start", new(w: 100, x: -1, y: 8, z: 0)), ("negative projection", new(w: 100, x: 0, y: -1, z: 0)),
            ("negative footprint", new(w: 100, x: 2, y: 8, z: -1)), ("NaN start", new(w: 100, x: float.NaN, y: 8, z: 0)),
            ("infinite start", new(w: 100, x: float.PositiveInfinity, y: 8, z: 0)),
            ("NaN projection", new(w: 100, x: 2, y: float.NaN, z: 0)),
            ("infinite projection", new(w: 100, x: 2, y: float.PositiveInfinity, z: 0)),
            ("NaN footprint", new(w: 100, x: 2, y: 8, z: float.NaN)),
            ("infinite footprint", new(w: 100, x: 2, y: 8, z: float.PositiveInfinity)),
            ("overflowed backoff", new(w: 100, x: 0, y: float.MaxValue, z: 1)),
        }) { cases.Add(item: new(name, input, false, false, Vector3.Zero)); }
        cases.Add(item: new("large finite interval", new(w: 1e38f, x: 2e38f, y: 3e38f, z: 0), true, true,
            new(x: 3e38f, y: 2.5e38f, z: 5e37f)));
        void Field(string name, SdfProgram program, bool admitted, float bound = float.PositiveInfinity) =>
            cases.Add(item: new(name, new(w: 0, x: 2, y: 8, z: (1f / 64)), true, admitted, Interval, program, bound));
        Field("stationary front plane", Plane(8), true);
        Field("fold-safe clearance", Plane(8), false, 2.5f);
        Field("near tiny occluder", Sphere(center: 2.03125f, radius: (1f / 64)), false);
        Field("midpoint tiny occluder", Sphere(center: Midpoint, radius: (1f / 64)), false);
        Field("far tiny occluder", Sphere(center: 7.71875f, radius: (1f / 64)), false);
        Field("endpoint contact", Sphere(center: (Candidate + 0.125f), radius: 0.125f), false);
        Field("endpoint accepted band", Sphere(center: ((Candidate + 0.125f) + 0.0625f), radius: 0.125f), false);
        Field("empty segment", Sphere(center: ((Candidate + 0.125f) + 0.25f), radius: 0.125f), true);
        Field("interior is not absolute clearance", Sphere(center: Midpoint, radius: 100), false);
        Field("chamfer overestimate", Plane(7.25f, chamfer: true), false);
        Field("already conservative scale", Plane(8, chamfer: true), true);
        cases.Add(item: new("rounded midpoint endpoint", new(w: 0, x: 65536f, y: 65536.0078125f, z: 0), true, false,
            new(x: 65536.0078125f, y: 65536f, z: 0.0088125f), Sphere(center: 65536.0078125f, radius: (1f / 1024))));
        return [.. cases];
    }

    [Fact]
    public void FixedOverlapIndependentlyClassifiesEveryFieldCase() {
        foreach (var item in Cases()) {
            if (item.Program is not { } program) { continue; }
            var evaluator = new SdfFieldEvaluator(program: program);
            var clear = !evaluator.Overlap(center: Point(x: item.Expected.Y), radius: Outward(value: item.Expected.Z));

            Assert.Equal(item.Admitted, (clear && (item.FoldBound > item.Expected.Z)));
            if (item.Name == "chamfer overestimate") {
                Assert.True(condition: evaluator.TryDistance(Point(x: Midpoint), out var raw, out _));
                Assert.True(condition: (((double)raw) > Radius));
                Assert.True(condition: evaluator.Overlap(center: Point(x: Candidate), radius: FixedQ4816.Zero));
            }
            if (item.Admitted) {
                for (var index = 0; (index <= 128); index++) {
                    var depth = (item.Input.X + (((item.Expected.X - ((double)item.Input.X)) * index) / 128d));
                    var margin = Outward(value: Math.Max(val1: 0.001d, val2: (depth * item.Input.Z)));

                    Assert.False(condition: evaluator.Overlap(center: Point(x: depth), radius: margin));
                }
            }
        }
    }
    [Fact]
    public void VulkanExecutesTheConservativeSeedContract() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfMarchSeedDeviceLawTests));

        Verify(device.Services, ".spv");
    }
    [Fact]
    public void DirectXExecutesTheConservativeSeedContract() {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) { Verify(device.Services, ".dxil"); }
        Assert.DoesNotContain("[d3d12-debug]", output.ToString(), StringComparison.Ordinal);
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        var cases = Cases().Concat(second: WallpaperCases()).ToArray();
        var results = Run(cases: cases, extension: extension, services: services);
        var failures = new List<string>();

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];
            var actual = results[index];
            var flags = ((item.Prepared ? 1 : 0) + (item.Admitted ? 2 : 0));

            if (actual.W != flags) { failures.Add(item: $"{item.Name}: flags {actual.W}, expected {flags}; clearance {results[(index + cases.Length)].X}"); }
            if (item.FieldProbe is { } probe) {
                var field = results[(index + cases.Length)].Y;

                if (!float.IsFinite(f: field) || (MathF.Abs(x: (field - probe.Y)) > 0.000001f)) {
                    failures.Add(item: $"{item.Name}: field at {probe.X} is {field}, expected {probe.Y}");
                }
            }
            for (var component = 0; (component < 3); component++) {
                var expected = item.Expected[component];
                var tolerance = MathF.Max(x: 0.000001f, y: (MathF.Abs(x: expected) * 0.000001f));

                if (!float.IsFinite(f: actual[component]) || (MathF.Abs(x: (actual[component] - expected)) > tolerance)) {
                    failures.Add(item: $"{item.Name}: component {component} is {actual[component]}, expected {expected}");
                }
            }
        }
        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures));
    }
    private static Vector4[] Run(GpuDeviceServices services, string extension, SeedCase[] cases) {
        var world = new GpuGroupLayoutDescription(ordinal: 1,
            bindings: [new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ReadOnlyBuffer)]);
        var pass = new GpuGroupLayoutDescription(ordinal: 3, bindings: [
            new GpuGroupBinding(binding: 60, kind: GpuBindingKind.ReadOnlyBuffer),
            new GpuGroupBinding(binding: 61, kind: GpuBindingKind.StorageImage)]);
        var description = new GpuComputePipelineDescription(Bindings: [], Name: Kernel, PushConstantBinding: null,
            Layout: new GpuPipelineLayoutDescription(groups: [world, pass], pushesIndex: true, stages: GpuShaderStage.Compute));
        using var module = services.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (Kernel + extension))));
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R32G32B32A32Float, height: 2, width: ((uint)cases.Length),
            usage: GpuImageUsage.Storage, name: default);
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var rows = new Vector4[(cases.Length * 2)];

        for (var index = 0; (index < cases.Length); index++) {
            rows[(index * 2)] = cases[index].Input;
            rows[((index * 2) + 1)] = new(((cases[index].Program is null) ? 0 : 1), cases[index].FoldBound,
                (cases[index].FieldProbe?.X ?? 0), (cases[index].FieldProbe.HasValue ? 1 : 0));
        }
        using var inputs = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: rows.AsSpan()), name: default, usage: GpuBufferUsage.Storage);
        var buffers = new List<IGpuStorageBuffer>();
        var pool = services.Bindings.CreatePool(name: default,
            sizes: Enumerable.Repeat(GpuDescriptorPoolSizes.ForGroups([world]), cases.Length)
                .Aggregate(GpuDescriptorPoolSizes.ForGroups([pass]), (sum, sizes) => (sum + sizes)));

        try {
            var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[3], name: default, poolHandle: pool);

            services.Bindings.WriteBuffer(binding: 60, bufferHandle: inputs.BufferHandle, bufferSize: inputs.SizeBytes,
                descriptorSetHandle: set, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
            services.Bindings.WriteStorageImage(arrayElement: 0, binding: 61, descriptorSetHandle: set, imageViewHandle: output.ImageViewHandle);
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.TransitionImageLayout(commandBufferHandle: command, imageHandle: output.ImageHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, oldLayout: GpuImageLayout.Undefined,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader, newLayout: GpuImageLayout.General);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: 3, pipelineLayoutHandle: pipeline.LayoutHandle);
            var fallback = Plane(8);

            for (var index = 0; (index < cases.Length); index++) {
                var program = (cases[index].Program ?? fallback);
                var buffer = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: program.Words), name: default, usage: GpuBufferUsage.Storage);

                buffers.Add(item: buffer);
                var worldSet = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[1], name: default, poolHandle: pool);

                services.Bindings.WriteBuffer(binding: 0, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes,
                    descriptorSetHandle: worldSet, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
                recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: worldSet, group: 1, pipelineLayoutHandle: pipeline.LayoutHandle);
                ReadOnlySpan<uint> pushed = [((uint)index)];

                recorder.PushConstants(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, data: MemoryMarshal.AsBytes(span: pushed),
                    offset: 0, pipelineLayoutHandle: pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
                recorder.Dispatch(commandBufferHandle: command, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
            }
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            var result = readback.Read(bytesPerPixel: 16, format: GpuPixelFormat.R32G32B32A32Float, height: 2, width: ((uint)cases.Length),
                sourceImageHandle: output.ImageHandle, sourceLayout: GpuImageLayout.General);

            return MemoryMarshal.Cast<byte, Vector4>(span: result.Span).ToArray();
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
            foreach (var buffer in buffers) { buffer.Dispose(); }
        }
    }
    private static FixedQ4816 Outward(double value) {
        var rounded = FixedQ4816.FromDouble(value: value);

        return ((((double)rounded) < value) ? (rounded + FixedQ4816.Epsilon) : rounded);
    }
    private static FixedPosition Point(double x) => FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromDouble(value: x), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));
    // Wallpaper has no fixed evaluator. These cases have an analytic oracle: the PM mirror creates a sphere at
    // x = 3 before the LOD switch, but the half-turn puts the upright copy at x = -3 after it. With LOD at 4,
    // the midpoint 4.875 sees the plane's clearance 3.125 > Radius, yet the segment crosses the sphere at 2.75.
    // Its distance to the switch, 0.875, must refuse the seed. LOD at 1 puts the entire interval beyond the switch;
    // the nearest surface is then the plane at 8 and seeding must remain available (the positive control).
    private static SeedCase[] WallpaperCases() {
        SdfProgram Program(float lod) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.Rotate(rotation: new Quaternion(w: 0, x: 0, y: 1, z: 0))
                .WallpaperFold(group: SdfWallpaperGroup.Pm, cell: new Vector2(value: 32), limit: Vector2.Zero, lodDistance: lod)
                .Translate(offset: new Vector3(x: 3, y: 0, z: 0)).Sphere(radius: 0.25f, material: material)
                .ResetPoint().Plane(normal: -Vector3.UnitX, offset: 8, material: material);
            return builder.Build();
        }

        return [
            new("wallpaper LOD crossing", new(w: 0, x: 2, y: 8, z: (1f / 64)), true, false, Interval, Program(lod: 4), FieldProbe: new(x: 3, y: -0.25f)),
            new("wallpaper LOD outside the ball", new(w: 0, x: 2, y: 8, z: (1f / 64)), true, true, Interval, Program(lod: 1), FieldProbe: new(x: 3, y: 5)),
        ];
    }
    private static SdfProgram Plane(float offset, bool chamfer = false) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(-Vector3.UnitX, offset, material);
        if (chamfer) { _ = builder.ResetPoint().Plane(-Vector3.UnitX, offset, material, blend: SdfBlendOp.ChamferIntersection, smooth: 0.25f); }
        return builder.Build();
    }
    private static SdfProgram Sphere(float center, float radius) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Translate(offset: new Vector3(x: center, y: 0, z: 0)).Sphere(radius, material);
        return builder.Build();
    }
}
