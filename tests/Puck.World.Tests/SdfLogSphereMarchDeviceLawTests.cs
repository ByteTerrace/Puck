using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The shipped fine marches cross a log-sphere fold's shell walls soundly on Vulkan and Direct3D 12 hardware,
/// and the kernels fold wallpaper lattices as <see cref="SdfWallpaperFold"/> states. A log-sphere fold holds the
/// prototype at a different scale in each shell, so a step sized by one shell's field would jump a copy that only the
/// next shell holds; the primary march (<c>sdfTracePrimaryField</c>) and the soft shadow (<c>softShadowVisibilityMarch</c>)
/// step through <c>sdfMarchAdvance</c>, which lands a step that reaches a wall just past it, within the march's own
/// acceptance distance, and samples the next shell there. Each march case samples within a few thousandths of a wall,
/// where a step floored at a thousandth of the radius, or a soft shadow's minimum stride, jumps the copy. The field
/// cases evaluate <c>map()</c> under every wallpaper group a program accepts and hold it to the CPU fold.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
public sealed class SdfLogSphereMarchDeviceLawTests {
    private const string Kernel = "sdf-march-log-sphere.comp";
    private const float Footprint = (1f / 1024);
    // The shadow's penumbra sharpness: a key light's 1 / penumbra slope, the slope of a light with none authored.
    private const float ShadowSharpness = 9;
    // The wallpaper field cases' prototype: a sphere of this radius off its cell's center.
    private const float WallpaperRadius = 0.2f;

    private static readonly Vector3 WallpaperOffset = new(x: 0.31f, y: 0.05f, z: 0.12f);

    // The probe's modes, as its kernel numbers them.
    private enum MarchMode {
        Primary = 0,
        Shadow = 1,
        Field = 2,
    }
    // A march along one ray, or a field read at one point: its origin and unit direction, the program, the far distance,
    // the pixel footprint and the march's own input (the primary march's start or the shadow's reach), and the range the
    // result must land in: the primary hit's depth (null for a miss), the shadow's visibility, or the field's value.
    private sealed record MarchCase(string Name, MarchMode Mode, Vector3 Origin, Vector3 Direction, SdfProgram Field, (float Low, float High)? Expected,
        float Far = 30, float Footprint = Footprint, float Input = 0);

    private static MarchCase[] Cases() => [
        // A log-sphere fold of ratio two about the origin, whose prototype is a sphere of radius 0.0003125 at x = 1.41406:
        // shell 4's copy, scaled by 16, spans 22.620 to 22.630, and the shell ends at 2^4.5 = 22.6274. A march from
        // 0.0016 past that wall reads shell 5's copy 16 away; a step floored at 1e-3 of the radius (0.016 after the
        // fold's step scale) lands at 22.613, past the 0.0075 of the copy that lies in shell 4.
        new("a thin copy just inside a log-sphere shell is hit", MarchMode.Primary, new Vector3(x: 22.629f, y: 0, z: 0), -Vector3.UnitX, DrosteProgram(), (0.0015f, 0.003f)),
        // The shadow's first sample sits 0.0011 past the same wall, reading shell 5's copy; a 0.02 minimum stride lands
        // at 22.6085, past the copy, and the ray then reads open space.
        new("a soft shadow's minimum stride does not jump a shell wall", MarchMode.Shadow, new Vector3(x: 22.6685f, y: 0, z: 0), -Vector3.UnitX, DrosteProgram(), (0f, 0.01f),
            Input: 1),
        .. WallpaperCases(),
    ];
    // Eight points over three cells each way under every wallpaper group a program accepts, each held to the CPU fold's
    // distance to the prototype.
    private static IEnumerable<MarchCase> WallpaperCases() {
        foreach (var group in Enum.GetValues<SdfWallpaperGroup>().Where(predicate: SdfWallpaperFold.IsContinuous)) {
            var cell = ((group == SdfWallpaperGroup.Pmm) ? new Vector2(x: 1.7f, y: 1.3f) : new Vector2(value: 1.7f));
            var limit = new Vector2(value: ((group >= SdfWallpaperGroup.P3) ? SdfWallpaperFold.UnboundedLimit : 3f));
            var program = WallpaperProgram(cell: cell, group: group, limit: limit);

            for (var index = 1; (index <= 8); index++) {
                var point = new Vector3(
                    x: ((((index * 0.7548777f) % 1f) * 8f) - 4f),
                    y: 0.1f,
                    z: ((((index * 0.5698403f) % 1f) * 8f) - 4f)
                );
                var folded = SdfWallpaperFold.Fold(cell: cell, cellIndex: out _, group: group, limit: limit, point: new Vector2(x: point.X, y: point.Z));
                var expected = (Vector3.Distance(value1: new Vector3(x: folded.X, y: point.Y, z: folded.Y), value2: WallpaperOffset) - WallpaperRadius);

                yield return new MarchCase($"{group} folds {point} as the CPU fold does", MarchMode.Field, point, Vector3.UnitX, program, ((expected - 1.0e-4f), (expected + 1.0e-4f)));
            }
        }
    }

    [Fact]
    public void VulkanMarchesAcrossFoldWalls() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfLogSphereMarchDeviceLawTests));

        Verify(extension: ".spv", services: device.Services);
    }
    [Fact]
    public void DirectXMarchesAcrossFoldWalls() {
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
        var rows = new Vector4[(cases.Length * 3)];

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];

            rows[(index * 3)] = new Vector4(value: item.Origin, w: item.Far);
            rows[((index * 3) + 1)] = new Vector4(value: item.Direction, w: item.Footprint);
            rows[((index * 3) + 2)] = new Vector4(x: ((float)item.Mode), y: item.Input, z: ShadowSharpness, w: 0);
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
    // Shells of ratio two about the origin, each holding the sphere of radius 0.0003125 at x = 1.41406 scaled by its shell.
    private static SdfProgram DrosteProgram() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.LogSphere(shellRatio: 2)
            .Translate(offset: new Vector3(x: 1.41406f, y: 0, z: 0)).Sphere(material: material, radius: 0.0003125f);

        return builder.Build();
    }
    // A wallpaper lattice in the XZ plane whose prototype is a sphere off its cell's center; its step scale is one, so
    // map() returns the folded distance itself.
    private static SdfProgram WallpaperProgram(SdfWallpaperGroup group, Vector2 cell, Vector2 limit) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.WallpaperFold(cell: cell, group: group, limit: limit)
            .Translate(offset: WallpaperOffset).Sphere(material: material, radius: WallpaperRadius);

        var program = builder.Build();

        Assert.Equal(expected: 1f, actual: program.StepScale);

        return program;
    }
}
