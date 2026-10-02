using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The shipped primary march (<c>sdfTracePrimaryField</c>) reaches the tested wallpaper copies across their
/// symmetry-LOD switch on Vulkan, Direct3D 12 hardware and WARP. Past the camera-centered switch sphere, the fold drops its
/// mirrors, so a copy can stand where the mirrored lattice put none: a step sized by the field on one side would jump
/// straight through it. The march reads the switch gap through <c>sdfMapStepBound</c>. Wallpaper has no
/// fixed evaluator, so the cases have an analytic oracle: a P2 lattice of four-unit cells whose prototype is a sphere of
/// radius 0.25 at the cell's x = 1. An odd cell turns its copy half way inside the switch and keeps it upright past it,
/// so cell -1's copy stands at x = -5 inside the switch and at x = -3 past it. A mirror alone (PM) cannot show the
/// hazard: dropping a mirror only removes copies, so nothing stands past the switch that the mirrored lattice lacks.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
public sealed class SdfMarchLodDeviceLawTests {
    private const string Kernel = "sdf-march-lod.comp";
    private const float FarDistance = 100;
    private const float Footprint = (1f / 1024);

    // A ray from the origin, the LOD switch's distance, and the depth range a hit must land in (null for a miss).
    private sealed record MarchCase(string Name, Vector3 Direction, float LodDistance, (float Near, float Far)? Hit, SdfProgram? Field = null);

    private static MarchCase[] Cases() => [
        // Inside the switch at 2.9, cell -1's turned copy stands at -5; past it the upright copy spans 2.75 to 3.25, so
        // the surface the camera sees along -x begins at the switch. At 2.25, inside the switch, the field reads the
        // turned copy 2.5 away, and a step that long lands at 4.75, past the upright one.
        new("a copy past the switch is not stepped over", -Vector3.UnitX, 2.9f, (2.89f, 2.95f)),
        new("the even cell's copy inside the switch", Vector3.UnitX, 4, (0.74f, 0.76f)),
        new("the upright copy with the ray past the switch", -Vector3.UnitX, 0.5f, (2.74f, 2.76f)),
        new("nothing beside the lattice's plane", Vector3.UnitY, 2.9f, null),
        new("a Lipschitz clamp does not trap the ray at the switch", Vector3.UnitY, 3, (4.68f, 4.75f), ClampedProgram()),
    ];

    [Fact]
    public void VulkanStopsEveryStepAtTheLodSwitch() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfMarchLodDeviceLawTests));

        Verify(extension: ".spv", services: device.Services);
    }
    [Fact]
    public void DirectXStopsEveryStepAtTheLodSwitch() {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) {
            Verify(extension: ".dxil", services: device.Services);
        }
        Assert.DoesNotContain(comparisonType: StringComparison.Ordinal, expectedSubstring: "[d3d12-debug]", actualString: output.ToString());
    }
    [Fact]
    public void DirectXWarpStopsEveryStepAtTheLodSwitch() {
        using var device = DirectXTestDevices.Warp();

        Verify(extension: ".dxil", services: device.Services);
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        var cases = Cases();
        var results = Run(cases: cases, extension: extension, services: services);
        var failures = new List<string>();

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];
            var result = results[index];
            var found = (result.Y == 1);

            if (item.Hit is { } hit) {
                if (!found || !(result.X >= hit.Near) || !(result.X <= hit.Far)) {
                    failures.Add(item: $"{item.Name}: found {found} at {result.X} after {result.Z} steps, expected a hit in [{hit.Near}, {hit.Far}]");
                }
            } else if (found) {
                failures.Add(item: $"{item.Name}: found a surface at {result.X}, expected none");
            }
        }
        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures));
    }
    private static Vector4[] Run(GpuDeviceServices services, string extension, MarchCase[] cases) {
        var world = new GpuGroupLayoutDescription(ordinal: 1, bindings: [new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ReadOnlyBuffer)]);
        var pass = new GpuGroupLayoutDescription(ordinal: 3, bindings: [
            new GpuGroupBinding(binding: 60, kind: GpuBindingKind.ReadOnlyBuffer),
            new GpuGroupBinding(binding: 61, kind: GpuBindingKind.StorageImage),
        ]);
        var description = new GpuComputePipelineDescription(Bindings: [], Layout: new GpuPipelineLayoutDescription(groups: [world, pass], pushesIndex: true, stages: GpuShaderStage.Compute),
            Name: Kernel, PushConstantBinding: null);
        using var module = services.ShaderModuleFactory.Create(
            bytecode: File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (Kernel + extension))),
            stage: GpuShaderStage.Compute
        );
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R32G32B32A32Float, height: 1, name: default, usage: GpuImageUsage.Storage, width: ((uint)cases.Length));
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var rows = new Vector4[(cases.Length * 2)];

        for (var index = 0; (index < cases.Length); index++) {
            rows[(index * 2)] = new Vector4(value: Vector3.Zero, w: FarDistance);
            rows[((index * 2) + 1)] = new Vector4(value: cases[index].Direction, w: Footprint);
        }
        using var inputs = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: rows.AsSpan()), name: default, usage: GpuBufferUsage.Storage);
        var buffers = new List<IGpuStorageBuffer>();
        var pool = services.Bindings.CreatePool(
            name: default,
            sizes: Enumerable.Repeat(element: GpuDescriptorPoolSizes.ForGroups(groups: [world]), count: cases.Length)
                .Aggregate(seed: GpuDescriptorPoolSizes.ForGroups(groups: [pass]), func: static (sum, sizes) => (sum + sizes))
        );

        try {
            var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[3], name: default, poolHandle: pool);

            services.Bindings.WriteBuffer(binding: 60, bufferHandle: inputs.BufferHandle, bufferSize: inputs.SizeBytes, descriptorSetHandle: set, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
            services.Bindings.WriteStorageImage(arrayElement: 0, binding: 61, descriptorSetHandle: set, imageViewHandle: output.ImageViewHandle);
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.TransitionImageLayout(commandBufferHandle: command, destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader,
                imageHandle: output.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuAccess.None,
                sourceStageMask: GpuStage.TopOfPipe);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: 3, pipelineLayoutHandle: pipeline.LayoutHandle);

            for (var index = 0; (index < cases.Length); index++) {
                var program = (cases[index].Field ?? Program(lodDistance: cases[index].LodDistance));
                var buffer = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: program.Words), name: default, usage: GpuBufferUsage.Storage);

                buffers.Add(item: buffer);
                var worldSet = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[1], name: default, poolHandle: pool);

                services.Bindings.WriteBuffer(binding: 0, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes, descriptorSetHandle: worldSet, elementStride: 16, kind: GpuBindingKind.ReadOnlyBuffer);
                recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: worldSet, group: 1, pipelineLayoutHandle: pipeline.LayoutHandle);
                ReadOnlySpan<uint> pushed = [((uint)index)];

                recorder.PushConstants(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, data: MemoryMarshal.AsBytes(span: pushed), offset: 0,
                    pipelineLayoutHandle: pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
                recorder.Dispatch(commandBufferHandle: command, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
            }
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            var result = readback.Read(bytesPerPixel: 16, format: GpuPixelFormat.R32G32B32A32Float, height: 1, sourceImageHandle: output.ImageHandle,
                sourceLayout: GpuImageLayout.General, width: ((uint)cases.Length));

            return MemoryMarshal.Cast<byte, Vector4>(span: result.Span).ToArray();
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
            foreach (var buffer in buffers) {
                buffer.Dispose();
            }
        }
    }
    // The relief vanishes on the Y ray because its X/Z sine factors are zero, but its global Lipschitz factor is 20.
    // The ray stays in cell zero, so either LOD branch has the same sphere with its front surface at 4.75.
    private static SdfProgram ClampedProgram() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.WallpaperFold(cell: new Vector2(value: 4), group: SdfWallpaperGroup.P2, limit: new Vector2(value: 2), lodDistance: 3)
            .Translate(offset: new Vector3(x: 0, y: 5, z: 0)).Sphere(material: material, radius: 0.25f)
            .Displace(frequency: Vector3.One, amplitude: 19);

        var program = builder.Build();

        Assert.Equal(expected: 0.05f, actual: program.StepScale);

        return program;
    }
    // A P2 lattice of four-unit cells in the XZ plane, two cells either way, whose prototype is a sphere at the cell's
    // x = 1: cell 0 holds it at x = 1, and cell -1 at x = -5 inside the switch and x = -3 past it.
    private static SdfProgram Program(float lodDistance) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.WallpaperFold(cell: new Vector2(value: 4), group: SdfWallpaperGroup.P2, limit: new Vector2(value: 2), lodDistance: lodDistance)
            .Translate(offset: new Vector3(x: 1, y: 0, z: 0)).Sphere(material: material, radius: 0.25f);

        return builder.Build();
    }
}
