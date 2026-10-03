using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Every shipped march crosses a wallpaper fold's symmetry-LOD switch soundly on Vulkan and Direct3D 12
/// hardware: the primary march (<c>sdfTracePrimaryField</c>), the beam's cone (<c>coneMarchTileBounds</c>, whose
/// last phase proves the far bound) and the soft shadow (<c>softShadowVisibilityMarch</c>). Past the camera-centered switch
/// sphere the fold drops its mirrors, so a copy can stand where the mirrored lattice put none: a step sized on one side
/// would jump straight through it. Each march steps through <c>sdfMarchAdvance</c>, which lands a step that reaches the
/// switch just past it, within the march's own acceptance distance, and samples the other side there. Wallpaper has no
/// fixed evaluator, so the cases have an analytic oracle: a P2 lattice whose prototype is a sphere off its cell's
/// center, so an odd cell turns its copy half way inside the switch and keeps it upright past it. A mirror alone (PM)
/// cannot show the hazard: dropping a mirror only removes copies, so nothing stands past the switch that the mirrored
/// lattice lacks. The thin cases each sample within a hundredth of a wall, where a step floored at a fixed fraction of
/// the LOD distance or of a log-sphere fold's radius, or a soft shadow's minimum stride, jumps the copy. A log-sphere
/// fold's shells are the other wall a march crosses: spheres about the fold's center, exactly crossed when the chain
/// before the fold is a similarity.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfMarchLodDeviceLawTests {
    private const string Kernel = "sdf-march-lod.comp";
    private const float FarDistance = 100;
    private const float Footprint = (1f / 1024);
    // The shadow's penumbra sharpness: a key light's 1 / penumbra slope, the slope of a light with none authored.
    private const float ShadowSharpness = 9;

    // A camera inside the wide lattice's cell -1, four units from its turned copy's cell center.
    private static readonly Vector3 InCell = new(x: -12, y: 0, z: 0);

    // The march the probe runs, as its kernel numbers them.
    private enum MarchMode {
        Primary = 0,
        Cone = 1,
        ConeFar = 2,
        Shadow = 3,
    }
    // A march along one ray: its origin and unit direction, the program, the far distance, the pixel footprint, the march's
    // own input (the primary or far-bound march's start, the cone's near distance, or the shadow's reach), the cone's
    // chord and the camera (the LOD origin), and the range the result must land in: the primary hit's depth (null for a
    // miss), the cone's entry, its far bound, or the shadow's visibility.
    private sealed record MarchCase(string Name, MarchMode Mode, Vector3 Origin, Vector3 Direction, SdfProgram Field, (float Low, float High)? Expected,
        float Far = FarDistance, float Footprint = Footprint, float Input = 0, float Chord = 0, Vector3 Camera = default);

    private static MarchCase[] Cases() => [
        // A P2 lattice of sixteen-unit cells whose sphere of radius 0.25 sits at the cell's x = 1, seen from a camera at
        // x = -12 in cell -1, whose copy stands at x = -17 inside the switch and at x = -15 past it. The ray never leaves
        // the cell, so the folded field is exact along it. Inside a switch at 2.9 the field at 2.25 reads the turned copy
        // 2.5 away, and a step that long lands at 4.75, past the upright one, whose surface the camera sees from 2.9 on.
        new("a copy past the switch is not stepped over", MarchMode.Primary, InCell, -Vector3.UnitX, WideLattice(lodDistance: 2.9f), (2.89f, 2.95f), Camera: InCell),
        new("the even cell's copy inside the switch", MarchMode.Primary, Vector3.Zero, Vector3.UnitX, Lattice(lodDistance: 4), (0.74f, 0.76f)),
        new("the upright copy with the ray past the switch", MarchMode.Primary, InCell, -Vector3.UnitX, WideLattice(lodDistance: 0.5f), (2.74f, 2.76f), Camera: InCell),
        new("nothing beside the lattice's plane", MarchMode.Primary, Vector3.Zero, Vector3.UnitY, Lattice(lodDistance: 2.9f), null),
        new("a Lipschitz clamp does not trap the ray at the switch", MarchMode.Primary, Vector3.Zero, Vector3.UnitY, ClampedProgram(), (4.68f, 4.75f)),
        // P2 cells 160 wide with a sphere of radius 0.002 at x = 60: cell -1's upright copy spans 99.998 to 100.002 past
        // a switch at 99.999. A march starting 0.001 inside the switch reads the turned copy 120 away; a step floored at
        // 0.01 lands at 100.008, past the upright copy, which is only 0.004 thick.
        new("a thin copy just past the switch is hit", MarchMode.Primary, Vector3.Zero, -Vector3.UnitX, ThinLattice(), (99.998f, 100.0005f),
            Far: 101, Footprint: 1e-6f, Input: 99.998f),
        // The same thin copy bounds a ray-thin cone's entry at the switch, never past it or nowhere.
        new("a cone does not take the switch gap as clearance", MarchMode.Cone, Vector3.Zero, -Vector3.UnitX, ThinLattice(), (99.99f, 99.9995f),
            Far: 101, Footprint: 1e-6f, Input: 99.998f),
        // From inside the switch, the cone's far bound lies past the thin copy, which the fine march would accept.
        new("a cone's far bound does not claim the copy past the switch", MarchMode.ConeFar, Vector3.Zero, -Vector3.UnitX, ThinLattice(), (100.002f, 101f),
            Far: 101, Footprint: 1e-6f, Input: 99.998f),
        // A log-sphere fold of ratio two about the origin, whose prototype is a sphere of radius 0.0003125 at x = 1.41406:
        // shell 4's copy, scaled by 16, spans 22.620 to 22.630, and the shell ends at 2^4.5 = 22.6274. A march from
        // 0.0016 past that wall reads shell 5's copy 16 away; a step floored at 1e-3 of the radius (0.016 after the
        // fold's step scale) lands at 22.613, past the 0.0075 of the copy that lies in shell 4.
        new("a thin copy just inside a log-sphere shell is hit", MarchMode.Primary, new Vector3(x: 22.629f, y: 0, z: 0), -Vector3.UnitX, DrosteProgram(), (0.0015f, 0.003f),
            Far: 30),
        // P2 four-unit cells with a sphere of radius 0.006 at the cell's x = 1.095: cell -1's upright copy spans 2.899 to
        // 2.911 past a switch at 2.9. The shadow's first sample sits at -2.899, 0.001 inside the switch, reading the turned
        // copy 2.19 away; a 0.02 minimum stride lands at -2.919, past the copy, and the ray then reads open space.
        new("a soft shadow's minimum stride does not jump the switch", MarchMode.Shadow, new Vector3(x: -2.859f, y: 0, z: 0), -Vector3.UnitX, ShadowLattice(), (0f, 0.01f),
            Input: 1),
    ];

    [Fact]
    public void VulkanMarchesAcrossTheLodSwitch() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfMarchLodDeviceLawTests));

        Verify(extension: ".spv", services: device.Services);
    }
    [Fact]
    public void DirectXMarchesAcrossTheLodSwitch() {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) {
            Verify(extension: ".dxil", services: device.Services);
        }
        Assert.DoesNotContain(comparisonType: StringComparison.Ordinal, expectedSubstring: "[d3d12-debug]", actualString: output.ToString());
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        var cases = Cases();
        var results = Run(cases: cases, extension: extension, services: services);
        var failures = new List<string>();

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];
            var result = results[index];

            if (item.Mode == MarchMode.Primary) {
                var found = (result.Y == 1);

                if (item.Expected is { } hit) {
                    if (!found || !(result.X >= hit.Low) || !(result.X <= hit.High)) {
                        failures.Add(item: $"{item.Name}: found {found} at {result.X} after {result.Z} steps, expected a hit in [{hit.Low}, {hit.High}]");
                    }
                } else if (found) {
                    failures.Add(item: $"{item.Name}: found a surface at {result.X}, expected none");
                }
            } else if ((item.Expected is { } range) && (!(result.X >= range.Low) || !(result.X <= range.High))) {
                failures.Add(item: $"{item.Name}: read {result}, expected x in [{range.Low}, {range.High}]");
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
        var rows = new Vector4[(cases.Length * 4)];

        // The probe's four rows per case; the LOD origin is the case's camera, as a view's position is.
        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];

            rows[(index * 4)] = new Vector4(value: item.Origin, w: item.Far);
            rows[((index * 4) + 1)] = new Vector4(value: item.Direction, w: item.Footprint);
            rows[((index * 4) + 2)] = new Vector4(value: item.Camera, w: ((float)item.Mode));
            rows[((index * 4) + 3)] = new Vector4(x: item.Input, y: item.Chord, z: ShadowSharpness, w: 0);
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
                var program = cases[index].Field;
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
    // Shells of ratio two about the origin, each holding the sphere of radius 0.0003125 at x = 1.41406 scaled by its shell.
    private static SdfProgram DrosteProgram() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.LogSphere(shellRatio: 2)
            .Translate(offset: new Vector3(x: 1.41406f, y: 0, z: 0)).Sphere(material: material, radius: 0.0003125f);

        return builder.Build();
    }
    // A P2 lattice of four-unit cells in the XZ plane, two cells either way, whose prototype is a sphere at the cell's
    // x = 1: cell 0 holds it at x = 1, and cell -1 at x = -5 inside the switch and x = -3 past it.
    private static SdfProgram Lattice(float lodDistance) => Lattice(cell: 4, lodDistance: lodDistance, offset: 1, radius: 0.25f);
    // The same prototype in sixteen-unit cells: cell -1 spans x = -24 to -8.
    private static SdfProgram WideLattice(float lodDistance) => Lattice(cell: 16, lodDistance: lodDistance, offset: 1, radius: 0.25f);
    // Cells 160 wide with a sphere of radius 0.002 at the cell's x = 60, switching at 99.999: cell -1's upright copy
    // spans 99.998 to 100.002 along -x, and its turned copy stands at x = -220.
    private static SdfProgram ThinLattice() => Lattice(cell: 160, lodDistance: 99.999f, offset: 60, radius: 0.002f);
    // Four-unit cells with a sphere of radius 0.006 at the cell's x = 1.095, switching at 2.9: cell -1's upright copy
    // spans 2.899 to 2.911 along -x, and its turned copy stands at x = -5.095.
    private static SdfProgram ShadowLattice() => Lattice(cell: 4, lodDistance: 2.9f, offset: 1.095f, radius: 0.006f);
    private static SdfProgram Lattice(float cell, float lodDistance, float offset, float radius) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.WallpaperFold(cell: new Vector2(value: cell), group: SdfWallpaperGroup.P2, limit: new Vector2(value: 2), lodDistance: lodDistance)
            .Translate(offset: new Vector3(x: offset, y: 0, z: 0)).Sphere(material: material, radius: radius);

        return builder.Build();
    }
}
