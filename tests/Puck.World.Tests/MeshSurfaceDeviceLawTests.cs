using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The device half of the mesh surface a hit pass resolves. The probe kernel (<c>Assets/Shaders/mesh-surface.comp.hlsl</c>)
/// runs the shipped mesh module's <c>sdfMeshSurfaceNormal</c> and <c>sdfMeshMaterial</c> over draws packed by
/// <see cref="SdfMeshRegion.Write"/>, so every case reads the region exactly as the engine's passes do:
/// <list type="bullet">
/// <item><description>a slanted triangle (object normal (1, 1, 0)) drawn under <c>CreateScale(2, 1, 1)</c> resolves a
/// normal proportional to (1, 2, 0): its vertex normals travel through the draw's normal matrix, the inverse transpose,
/// not its position matrix; and its material is the draw's plus the triangle's palette entry;</description></item>
/// <item><description>vertex normals tilted far off their face, seen along a grazing ray from the face's front, would
/// interpolate to a normal leaning away from the camera; the resolved normal faces the camera.</description></item>
/// </list>
/// It runs on the first Vulkan device with a graphics queue, on the first Direct3D 12 hardware adapter and on the
/// software (WARP) renderer.
/// </summary>
[SupportedOSPlatform("windows10.0.15063")]
public sealed class MeshSurfaceDeviceLawTests {
    private const string KernelName = "mesh-surface.comp";
    private const uint Group = 3U;
    private const uint RegionBinding = 0U;
    private const uint CasesBinding = 1U;
    private const uint ResultsBinding = 2U;

    // The slanted triangle on the plane x + y = 1, wound so its face normal is +(1, 1, 0), with that normal at every
    // vertex and palette entry 3 on its one triangle.
    private static readonly SdfMesh Slanted = new(
        indices: new uint[] { 0, 1, 2 },
        normals: new Vector3[] { Vector3.Normalize(value: new(x: 1f, y: 1f, z: 0f)), Vector3.Normalize(value: new(x: 1f, y: 1f, z: 0f)), Vector3.Normalize(value: new(x: 1f, y: 1f, z: 0f)) },
        positions: new Vector3[] { new(x: 1f, y: 0f, z: 0f), new(x: 0f, y: 1f, z: 0f), new(x: 0f, y: 1f, z: 1f) },
        triangleMaterials: new uint[] { 3 }
    );
    // A triangle facing +z whose vertex normals lean almost flat along +x.
    private static readonly SdfMesh Tilted = new(
        indices: new uint[] { 0, 1, 2 },
        normals: new Vector3[] { Vector3.Normalize(value: new(x: 0.99f, y: 0f, z: 0.14f)), Vector3.Normalize(value: new(x: 0.99f, y: 0f, z: 0.14f)), Vector3.Normalize(value: new(x: 0.99f, y: 0f, z: 0.14f)) },
        positions: new Vector3[] { new(x: 0f, y: 0f, z: 0f), new(x: 1f, y: 0f, z: 0f), new(x: 0f, y: 1f, z: 0f) }
    );
    private static readonly SdfMeshDraw[] Draws = [
        new(Material: 5, Mesh: Slanted, ObjectToWorld: Matrix4x4.CreateScale(xScale: 2f, yScale: 1f, zScale: 1f)),
        new(Material: 0, Mesh: Tilted, ObjectToWorld: Matrix4x4.Identity),
    ];
    // The slanted triangle's centroid, (1/3, 2/3, 1/3) in object space, is (2/3, 2/3, 1/3) under the scale; the camera
    // looks at it along -(1, 2, 0). The tilted triangle is seen at its centroid along a grazing ray from its front.
    private static readonly Vector3 GrazingRay = Vector3.Normalize(value: new(x: 0.9f, y: 0f, z: -0.4f));
    private static readonly (Vector3 Point, uint Draw, Vector3 Ray, uint Triangle)[] Cases = [
        (new Vector3(x: (2f / 3f), y: (2f / 3f), z: (1f / 3f)), 0U, -Vector3.Normalize(value: new(x: 1f, y: 2f, z: 0f)), 0U),
        (new Vector3(x: (1f / 3f), y: (1f / 3f), z: 0f), 1U, GrazingRay, 0U),
    ];

    [Fact]
    public void AVulkanDeviceResolvesEveryMeshSurfaceCase() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(MeshSurfaceDeviceLawTests));

        Resolve(
            backend: $"vulkan ({device.Name})",
            kernel: Kernel(extension: ".spv"),
            services: device.Services
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ADirect3D12DeviceResolvesEveryMeshSurfaceCase(bool warp) {
        using (var context = (warp ? DirectXTestDevices.Warp() : DirectXTestDevices.Hardware())) {
            Resolve(
                backend: (warp ? "directx (WARP)" : "directx"),
                kernel: Kernel(extension: ".dxil"),
                services: context.Services
            );
        }
    }

    private static byte[] Kernel(string extension) =>
        File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (KernelName + extension)));
    private static void Resolve(string backend, byte[] kernel, GpuDeviceServices services) {
        var results = Run(
            kernel: kernel,
            services: services
        );
        var slanted = results[0];
        var tilted = results[1];
        var expected = Vector3.Normalize(value: new(x: 1f, y: 2f, z: 0f));

        Assert.True(
            condition: (Vector3.Distance(value1: new Vector3(x: slanted.X, y: slanted.Y, z: slanted.Z), value2: expected) < 1e-4f),
            userMessage: $"{backend}: the slanted triangle under CreateScale(2, 1, 1) resolved ({slanted.X}, {slanted.Y}, {slanted.Z}), not {expected}"
        );
        Assert.Equal(actual: slanted.W, expected: 8f);

        var normal = new Vector3(x: tilted.X, y: tilted.Y, z: tilted.Z);

        Assert.True(
            condition: (Vector3.Dot(vector1: normal, vector2: GrazingRay) < 0f),
            userMessage: $"{backend}: the tilted normals resolved ({normal.X}, {normal.Y}, {normal.Z}), which faces away from the ray"
        );
        Assert.True(
            condition: (MathF.Abs(x: (normal.Length() - 1f)) < 1e-4f),
            userMessage: $"{backend}: the tilted normals resolved a normal of length {normal.Length()}"
        );
    }
    // Packs the draws, dispatches the probe once per case and reads the results row back.
    private static Vector4[] Run(byte[] kernel, GpuDeviceServices services) {
        var meshes = new Dictionary<SdfMesh, SdfMeshRegionMesh>(comparer: ReferenceEqualityComparer.Instance);
        var layout = SdfMeshRegion.Plan(
            draws: Draws,
            meshes: meshes
        );
        var words = new uint[layout.Words];

        SdfMeshRegion.Write(
            destination: words,
            draws: Draws,
            layout: layout,
            meshes: meshes
        );

        var bindings = services.Bindings;
        var description = new GpuComputePipelineDescription(
            Bindings: [],
            Layout: new GpuPipelineLayoutDescription(
                groups: [new GpuGroupLayoutDescription(
                    bindings: [
                        new GpuGroupBinding(binding: RegionBinding, kind: GpuBindingKind.ReadOnlyBuffer),
                        new GpuGroupBinding(binding: CasesBinding, kind: GpuBindingKind.ReadOnlyBuffer),
                        new GpuGroupBinding(binding: ResultsBinding, kind: GpuBindingKind.StorageImage),
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
        using var output = services.ImageFactory.Create(
            format: GpuPixelFormat.R32G32B32A32Float,
            height: 1U,
            name: default,
            usage: GpuImageUsage.Storage,
            width: ((uint)Cases.Length)
        );
        using var commands = services.CommandPoolFactory.Create(name: default);
        using var region = services.BufferFactory.CreateHostVisible(
            name: default,
            sizeBytes: ((ulong)(words.Length * sizeof(uint))),
            usage: GpuBufferUsage.Storage
        );
        using var cases = services.BufferFactory.CreateHostVisible(
            name: default,
            sizeBytes: ((ulong)((Cases.Length * 8) * sizeof(float))),
            usage: GpuBufferUsage.Storage
        );

        region.Write<uint>(data: words);
        cases.Write<float>(data: [.. Cases.SelectMany(selector: static meshCase => ((float[])[meshCase.Point.X, meshCase.Point.Y, meshCase.Point.Z, meshCase.Draw, meshCase.Ray.X, meshCase.Ray.Y, meshCase.Ray.Z, meshCase.Triangle]))]);

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
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            bindings.WriteBuffer(
                binding: RegionBinding,
                bufferHandle: region.BufferHandle,
                bufferSize: region.SizeBytes,
                descriptorSetHandle: set,
                elementStride: sizeof(uint),
                kind: GpuBindingKind.ReadOnlyBuffer
            );
            bindings.WriteBuffer(
                binding: CasesBinding,
                bufferHandle: cases.BufferHandle,
                bufferSize: cases.SizeBytes,
                descriptorSetHandle: set,
                elementStride: (4U * sizeof(float)),
                kind: GpuBindingKind.ReadOnlyBuffer
            );
            bindings.WriteStorageImage(
                arrayElement: 0U,
                binding: ResultsBinding,
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

            for (var slot = 0; (slot < Cases.Length); slot++) {
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
                width: ((uint)Cases.Length)
            );
        } finally {
            bindings.DestroyPool(poolHandle: pool);
        }

        return MemoryMarshal.Cast<byte, Vector4>(span: values.Span).ToArray();
    }
}
