using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Assets.Textures;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Baking;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The device half of a textured mesh hit. Two real bakes are packed into one set of mesh atlases
/// (<see cref="SdfMeshAtlas.Pack"/>), so at least one of them sits away from the atlases' origin, and drawn with their
/// texture coordinates moved into the atlases (<see cref="SdfMeshRegion.Write"/>). The probe kernel
/// (<c>Assets/Shaders/mesh-textures.comp.hlsl</c>) runs the shipped atlas module at a vertex of a triangle of each: a
/// vertex's texture coordinate sits on the center of a texel of its quad's tile, so at the finest level, which a
/// footprint far below one texel selects, bilinear filtering returns exactly that texel. Each read equals the CPU
/// decoder's texel at the bake's own coordinate: the albedo (BC7, decoded from sRGB) and the emission (BC6H), each within
/// the device's decode of a code; the octahedral normal (BC5) within a code of the pair; the occlusion (BC4) within a
/// code; and the material, the draw's plus the texel's palette entry, exactly.
/// It runs on the first Vulkan device with a graphics queue, on the first Direct3D 12 hardware adapter and on the
/// software (WARP) renderer.
/// </summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class MeshTextureDeviceLawTests {
    private const string KernelName = "mesh-textures.comp";
    private const uint Group = 3U;
    private const uint RegionBinding = 0U;
    private const uint CasesBinding = 1U;
    private const uint ResultsBinding = 2U;
    private const uint AtlasBinding = 3U;
    private const uint SamplersBinding = 8U;
    private const int TexelsPerCase = 3;

    private static readonly SdfMaterial[] Materials = [
        new(Albedo: new Vector3(x: 0.8f, y: 0.2f, z: 0.1f)),
        new(Albedo: new Vector3(x: 0.1f, y: 0.5f, z: 0.9f), Emissive: 3f),
    ];

    [Fact]
    public void AVulkanDeviceReadsEveryTexelTheBakeStored() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(MeshTextureDeviceLawTests));

        Resolve(
            backend: $"vulkan ({device.Name})",
            kernel: Kernel(extension: ".spv"),
            services: device.Services
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ADirect3D12DeviceReadsEveryTexelTheBakeStored(bool warp) {
        using (var context = (warp ? DirectXTestDevices.Warp() : DirectXTestDevices.Hardware())) {
            Resolve(
                backend: (warp ? "directx (WARP)" : "directx"),
                kernel: Kernel(extension: ".dxil"),
                services: context.Services
            );
        }
    }

    private static SdfBake Bake(Action<SdfProgramBuilder> emit) {
        var builder = new SdfProgramBuilder();

        foreach (var material in Materials) {
            _ = builder.AddMaterial(material: material);
        }

        emit(obj: builder);

        return SdfBaker.Bake(
            cancellationToken: TestContext.Current.CancellationToken,
            center: Vector3.Zero,
            materials: Materials,
            program: builder.Build(buildInstanceGrid: false),
            reach: 0.8f,
            tier: SdfBakeTier.For(quality: SdfBakeQuality.Preview)
        );
    }
    private static SdfMesh MeshOf(SdfBake bake) => new(
        indices: bake.Mesh.Indices,
        normals: bake.Mesh.Vertices.Select(selector: static vertex => vertex.Normal).ToArray(),
        positions: bake.Mesh.Vertices.Select(selector: static vertex => vertex.Position).ToArray(),
        textures: new SdfMeshTextures(textures: bake.Textures),
        uvs: bake.Mesh.Vertices.Select(selector: static vertex => vertex.Uv).ToArray()
    );
    private static byte[] Kernel(string extension) =>
        File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (KernelName + extension)));
    private static float SrgbToLinear(byte code) {
        var encoded = (code / 255.0);

        return ((float)((encoded <= 0.04045) ? (encoded / 12.92) : Math.Pow(x: ((encoded + 0.055) / 1.055), y: 2.4)));
    }
    private static void Resolve(string backend, byte[] kernel, GpuDeviceServices services) {
        var spheres = Bake(emit: static builder => {
            _ = builder.ResetPoint().Sphere(material: 0, radius: 0.6f);
            _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0.45f, z: 0f)).Sphere(material: 1, radius: 0.3f);
        });
        var box = Bake(emit: static builder => {
            _ = builder.ResetPoint().Box(halfExtents: new Vector3(x: 0.5f, y: 0.3f, z: 0.4f), material: 1, round: 0.05f);
        });
        SdfBake[] bakes = [spheres, box];
        var meshes = bakes.Select(selector: MeshOf).ToArray();
        SdfMeshDraw[] draws = [
            new(Identity: "first", Material: 2, Mesh: meshes[0], ObjectToWorld: Matrix4x4.Identity),
            new(Identity: "second", Material: 7, Mesh: meshes[1], ObjectToWorld: Matrix4x4.CreateTranslation(xPosition: 5f, yPosition: 0f, zPosition: 0f)),
        ];
        var atlas = SdfMeshAtlas.Pack(textures: meshes.Select(selector: static mesh => mesh.Textures!));

        Assert.Contains(
            collection: meshes,
            filter: mesh => (atlas.Placement(textures: mesh.Textures!) is { Z: > 0f } or { W: > 0f })
        );

        // Each case: a draw, a triangle and one of its corners, seen at that corner.
        var cases = new List<(int Draw, int Triangle, int Corner)>();

        for (var draw = 0; (draw < draws.Length); draw++) {
            var triangles = (draws[draw].Mesh.Indices.Length / 3);

            foreach (var triangle in ((int[])[0, (triangles / 2), (triangles - 1)])) {
                for (var corner = 0; (corner < 3); corner++) {
                    cases.Add(item: (draw, triangle, corner));
                }
            }
        }

        var results = Run(
            atlas: atlas,
            cases: cases,
            draws: draws,
            kernel: kernel,
            services: services
        );

        for (var index = 0; (index < cases.Count); index++) {
            var (draw, triangle, corner) = cases[index];
            var bake = bakes[draw];
            var vertex = bake.Mesh.Vertices[bake.Mesh.Indices[((3 * triangle) + corner)]];
            var textures = draws[draw].Mesh.Textures!;
            var x = ((int)MathF.Floor(x: (vertex.Uv.X * textures.Width)));
            var y = ((int)MathF.Floor(x: (vertex.Uv.Y * textures.Height)));
            var at = ((y * textures.Width) + x);
            var albedo = textures.Textures[0].Decode(level: 0);
            var normal = textures.Textures[1].Decode(level: 0);
            var occlusion = textures.Textures[2].Decode(level: 0);
            var material = textures.Textures[3].Decode(level: 0);
            var emission = textures.Textures[4].Decode(level: 0);
            var label = $"{backend}: draw {draw} triangle {triangle} corner {corner} (texel {x}, {y})";
            var shaded = results[(TexelsPerCase * index)];
            var surface = results[((TexelsPerCase * index) + 1)];
            var light = results[((TexelsPerCase * index) + 2)];
            var expectedAlbedo = new Vector3(x: SrgbToLinear(code: albedo[(at * 4)]), y: SrgbToLinear(code: albedo[((at * 4) + 1)]), z: SrgbToLinear(code: albedo[((at * 4) + 2)]));

            var (normalX, normalY, normalZ) = OctahedralNormal.Decode(u: normal[(at * 2)], v: normal[((at * 2) + 1)]);
            var expectedEmission = new Vector3(
                x: ((float)BitConverter.UInt16BitsToHalf(value: BinaryPrimitives.ReadUInt16LittleEndian(source: emission.AsSpan(start: (at * 8))))),
                y: ((float)BitConverter.UInt16BitsToHalf(value: BinaryPrimitives.ReadUInt16LittleEndian(source: emission.AsSpan(start: ((at * 8) + 2))))),
                z: ((float)BitConverter.UInt16BitsToHalf(value: BinaryPrimitives.ReadUInt16LittleEndian(source: emission.AsSpan(start: ((at * 8) + 4)))))
            );

            Assert.True(condition: (light.W == 0f), userMessage: $"{label}: read level {light.W}, not the finest");
            Assert.True(condition: (Vector3.Distance(value1: new Vector3(x: shaded.X, y: shaded.Y, z: shaded.Z), value2: expectedAlbedo) < 0.01f), userMessage: $"{label}: albedo ({shaded.X}, {shaded.Y}, {shaded.Z}), not {expectedAlbedo}");
            Assert.True(condition: (shaded.W == (draws[draw].Material + material[at])), userMessage: $"{label}: material {shaded.W}, not {draws[draw].Material} + {material[at]}");
            Assert.True(condition: (Vector3.Dot(vector1: Vector3.Normalize(value: new Vector3(x: surface.X, y: surface.Y, z: surface.Z)), vector2: new Vector3(x: ((float)normalX), y: ((float)normalY), z: ((float)normalZ))) > 0.999f), userMessage: $"{label}: normal ({surface.X}, {surface.Y}, {surface.Z}), not ({normalX}, {normalY}, {normalZ})");
            Assert.True(condition: (MathF.Abs(x: (surface.W - (occlusion[at] / 255f))) <= (1.5f / 255f)), userMessage: $"{label}: occlusion {surface.W}, not {(occlusion[at] / 255f)}");
            Assert.True(condition: (Vector3.Distance(value1: new Vector3(x: light.X, y: light.Y, z: light.Z), value2: expectedEmission) <= (0.002f * (1f + expectedEmission.Length()))), userMessage: $"{label}: emission ({light.X}, {light.Y}, {light.Z}), not {expectedEmission}");
        }
    }
    // Packs the draws against the atlases, uploads the atlases, dispatches the probe once per case and reads the results
    // row back.
    private static Vector4[] Run(SdfMeshAtlas atlas, IReadOnlyList<(int Draw, int Triangle, int Corner)> cases, SdfMeshDraw[] draws, byte[] kernel, GpuDeviceServices services) {
        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>(comparer: ReferenceEqualityComparer.Instance);
        var layout = SdfMeshRegion.Plan(
            draws: draws,
            meshes: placements
        );
        var words = new uint[layout.Words];

        SdfMeshRegion.Write(
            atlas: atlas,
            destination: words,
            draws: draws,
            layout: layout,
            meshes: placements
        );

        var caseValues = new List<float>();

        foreach (var (draw, triangle, corner) in cases) {
            var mesh = draws[draw].Mesh;
            var point = Vector3.Transform(position: mesh.Positions.Span[((int)mesh.Indices.Span[((3 * triangle) + corner)])], matrix: draws[draw].ObjectToWorld);

            caseValues.AddRange(collection: [point.X, point.Y, point.Z, draw, 1e-6f, triangle, 0f, 0f]);
        }

        var bindings = services.Bindings;
        var description = new GpuComputePipelineDescription(
            Bindings: [],
            Layout: new GpuPipelineLayoutDescription(
                groups: [new GpuGroupLayoutDescription(
                    bindings: [
                        new GpuGroupBinding(binding: RegionBinding, kind: GpuBindingKind.ReadOnlyBuffer),
                        new GpuGroupBinding(binding: CasesBinding, kind: GpuBindingKind.ReadOnlyBuffer),
                        new GpuGroupBinding(binding: ResultsBinding, kind: GpuBindingKind.StorageImage),
                        .. Enumerable.Range(start: 0, count: SdfMeshTextures.Usages.Count).Select(selector: static usage => new GpuGroupBinding(binding: (AtlasBinding + ((uint)usage)), kind: GpuBindingKind.SampledImage)),
                        new GpuGroupBinding(binding: SamplersBinding, count: 2U, kind: GpuBindingKind.Sampler),
                    ],
                    ordinal: Group
                )],
                pushesIndex: true,
                stages: GpuShaderStage.Compute
            ),
            Name: KernelName,
            PushConstantBinding: null
        );
        var width = ((uint)(cases.Count * TexelsPerCase));
        var uploads = new List<IGpuSurfaceUpload>();
        var samplers = new List<nint>();

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
            width: width
        );
        using var commands = services.CommandPoolFactory.Create(name: default);
        using var region = services.BufferFactory.CreateHostVisible(
            name: default,
            sizeBytes: ((ulong)(words.Length * sizeof(uint))),
            usage: GpuBufferUsage.Storage
        );
        using var caseBuffer = services.BufferFactory.CreateHostVisible(
            name: default,
            sizeBytes: ((ulong)(caseValues.Count * sizeof(float))),
            usage: GpuBufferUsage.Storage
        );

        region.Write<uint>(data: words);
        caseBuffer.Write<float>(data: [.. caseValues]);

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

            for (var usage = 0; (usage < SdfMeshTextures.Usages.Count); usage++) {
                var upload = services.SurfaceTransferFactory.CreateUpload();

                uploads.Add(item: upload);
                bindings.WriteSampledImage(
                    arrayElement: 0U,
                    binding: (AtlasBinding + ((uint)usage)),
                    descriptorSetHandle: set,
                    imageViewHandle: upload.Upload(
                        format: SdfMeshTextures.FormatOf(usage: SdfMeshTextures.Usages[usage]),
                        height: ((uint)atlas.Height),
                        levels: ((uint)atlas.Levels),
                        pixels: atlas.Chains[usage],
                        width: ((uint)atlas.Width)
                    )
                );
            }
            foreach (var filter in ((GpuSamplerFilter[])[GpuSamplerFilter.Nearest, GpuSamplerFilter.Linear])) {
                var sampler = bindings.CreateSampler(filter: filter);

                samplers.Add(item: sampler);
                bindings.WriteSampler(
                    arrayElement: ((uint)filter),
                    binding: SamplersBinding,
                    descriptorSetHandle: set,
                    samplerHandle: sampler
                );
            }

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
                bufferHandle: caseBuffer.BufferHandle,
                bufferSize: caseBuffer.SizeBytes,
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

            for (var slot = 0; (slot < cases.Count); slot++) {
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
                width: width
            );
        } finally {
            bindings.DestroyPool(poolHandle: pool);

            foreach (var sampler in samplers) {
                bindings.DestroySampler(samplerHandle: sampler);
            }
            foreach (var upload in uploads) {
                upload.Dispose();
            }
        }

        return MemoryMarshal.Cast<byte, Vector4>(span: values.Span).ToArray();
    }
}
