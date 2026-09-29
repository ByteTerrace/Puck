using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Isolated sky kinds read native generated records and expose their exact hash work on both backends.
/// The common stack's admission, transforms and blend order have their own laws.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
public sealed partial class SdfSkyKindDeviceLawTests {
    private sealed record KindCase(string Name, SdfSkyStarsData Stars, SdfSkyCloudsData Clouds,
        Vector4 DirectionQuality, Vector4 Mode, Vector4 Expected, uint Hashes, SdfSkyGradientData Gradient = default, SdfSkyNoiseData Noise = default, SdfSkyPatternData Pattern = default, SdfSkyAuroraData Aurora = default, SdfSkyPanoramaData Panorama = default, uint Samples = 0, float RelativeTolerance = 0);

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task KindsUseNativeParametersAndCountOnlyTheirReducedWork(bool directX) {
        using var directory = new TemporaryDirectory(prefix: "puck-sky-kinds-");

        var (shader, build) = await CompileProbe(directory: directory);
        using var debug = new StringWriter();

        if (directX) {
            using (var device = DirectXTestDevices.Debug(output: debug)) { Verify(device.Services, build.Dxil, shader); }
            Assert.DoesNotContain("[d3d12-debug]", debug.ToString(), StringComparison.Ordinal);
        } else {
            using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfSkyKindDeviceLawTests));

            Verify(device.Services, build.Spirv, shader);
        }
    }

    private static async Task<(ShaderInterface Shader, CompiledShader Build)> CompileProbe(TemporaryDirectory directory) {
        var shader = new ShaderInterface(name: "sky-kinds-probe", members: [
            ShaderInterfaceMember.ReadOnlyBuffer(name: "stars", group: ShaderInterfaceGroup.World,
                structure: ShaderInterfaceStructure.From<SdfSkyStarsData>()),
            ShaderInterfaceMember.ReadOnlyBuffer(name: "clouds", group: ShaderInterfaceGroup.World,
                structure: ShaderInterfaceStructure.From<SdfSkyCloudsData>()),
            ShaderInterfaceMember.ReadOnlyBuffer(name: "noises", group: ShaderInterfaceGroup.World,
                structure: ShaderInterfaceStructure.From<SdfSkyNoiseData>()),
            ShaderInterfaceMember.ReadOnlyBuffer(name: "patterns", group: ShaderInterfaceGroup.World,
                structure: ShaderInterfaceStructure.From<SdfSkyPatternData>()),
            ShaderInterfaceMember.ReadOnlyBuffer(name: "auroras", group: ShaderInterfaceGroup.World,
                structure: ShaderInterfaceStructure.From<SdfSkyAuroraData>()),
            ShaderInterfaceMember.ReadOnlyBuffer(name: "panoramas", group: ShaderInterfaceGroup.World,
                structure: ShaderInterfaceStructure.From<SdfSkyPanoramaData>()),
            ShaderInterfaceMember.SampledImage(name: "panoramaSource", group: ShaderInterfaceGroup.Pass, type: ShaderValueType.Float4),
            ShaderInterfaceMember.ReadOnlyBuffer(name: "gradients", group: ShaderInterfaceGroup.World,
                structure: ShaderInterfaceStructure.From<SdfSkyGradientData>()),
            ShaderInterfaceMember.ReadOnlyBuffer(name: "stops", group: ShaderInterfaceGroup.World,
                structure: ShaderInterfaceStructure.From<SdfSkyStopData>()),
            ShaderInterfaceMember.ReadOnlyBuffer(element: ShaderValueType.Float4, group: ShaderInterfaceGroup.Pass, name: "directions"),
            ShaderInterfaceMember.ReadOnlyBuffer(element: ShaderValueType.Float4, group: ShaderInterfaceGroup.Pass, name: "modes"),
            ShaderInterfaceMember.StorageImage(format: GpuPixelFormat.R32G32B32A32Float, group: ShaderInterfaceGroup.Pass, name: "results",
                type: ShaderValueType.Float4),
        ]);
        var include = ShaderInterfaceHlsl.FileName(shaderInterface: shader);
        var root = RepositoryPaths.Resolve(relativePath: "src/Puck.SdfVm/Assets/Shaders/Sdf").Replace(newChar: '/', oldChar: '\\');
        var source = $$"""
            #include "{{include}}"
            #include "{{root}}/isa/sdf-isa.hlsli"
            #include "{{root}}/sky/kinds/stars.hlsli"
            #include "{{root}}/sky/kinds/clouds.hlsli"
            #include "{{root}}/sky/kinds/gradient.hlsli"
            #include "{{root}}/sky/kinds/noise.hlsli"
            #include "{{root}}/sky/kinds/pattern.hlsli"
            #include "{{root}}/sky/kinds/aurora.hlsli"
            #include "{{root}}/sky/kinds/panorama.hlsli"
            [numthreads(1, 1, 1)]
            void CSMain(uint3 id : SV_DispatchThreadID) {
                uint index = id.x;
                float4 input = directions[index];
                float4 mode = modes[index];
                uint hashes = 0u;
                uint samples = 0u;
                float4 color = 0.0;
                if (mode.x == 0.0) {
                    color = sdfSkyStars(input.xyz, stars[index], (uint)input.w, hashes);
                } else if (mode.x == 2.0) {
                    color = sdfSkyGradient(input.xyz, gradients[index], stops, hashes);
                } else if (mode.x == 3.0) {
                    color = sdfSkyNoise(input.xyz, noises[index], (uint)input.w, hashes);
                } else if (mode.x == 4.0) {
                    color = sdfSkyPattern(input.xyz, patterns[index], (uint)input.w, hashes);
                } else if (mode.x == 6.0) {
                    color = sdfSkyAurora(input.xyz, auroras[index], (uint)input.w, hashes);
                } else if (mode.x == 7.0) {
                    color = sdfSkyPanorama(input.xyz, panoramas[index], panoramaSource, samples);
                } else if (mode.x == 8.0) {
                    color = float4(sdfSkyCloudDomeDistance(input.y, clouds[index].DomeRadius), 0.0, 0.0, 1.0);
                } else {
                    SdfSkyCloudSample sample;
                    SdfSkyCloudsData parameters = clouds[index];
                    bool prepared;
                    if (mode.x == 5.0) {
                        sample = (SdfSkyCloudSample)0;
                        sample.localDirection = input.xyz;
                        sample.normal = float3(0.0, 1.0, 0.0);
                        sample.cosAngle = 1.0;
                        sample.thickness = 0.25;
                        sample.alpha = 0.5;
                        prepared = true;
                    } else {
                        prepared = sdfPrepareSkyClouds(input.xyz, parameters, (uint)input.w, sample, hashes);
                    }
                    if (prepared) {
                        float3 direct = 0.0;
                        float3 toward = mode.z == 1.0 ? float3(-1e-5, 1.0, -1e-5) : (mode.z == 2.0 ? float3(0.0, -1.0, 0.0) : float3(0.0, 1.0, 0.0));
                        [loop] for (uint light = 0u; light < (uint)mode.y; light++) {
                            direct += sdfSkyCloudLight(sample, parameters, (uint)input.w,
                                float3(0.5, 0.75, 1.0), toward, float3(1.0, 0.5, 0.25), hashes);
                        }
                        color = mode.x == 9.0 ? float4(sample.normal, sample.alpha)
                            : sdfSkyClouds(sample, parameters, float3(0.25, 0.5, 0.75), direct);
                    }
                }
                results[uint2(index, 0u)] = color;
                results[uint2(index, 1u)] = float4((float)hashes, (float)samples, 0.0, 0.0);
            }
            """;
        var compiler = new ShaderCompiler(cacheDirectory: directory.PathOf(name: "cache"));
        var build = await compiler.CompileAsync(descriptor: new ShaderCompilationRequest(name: shader.Name,
            stages: [new ShaderStageSource(ShaderStage.Compute, directory.PathOf(name: "probe.comp.hlsl"), source, EntryPoint: "CSMain")],
            generatedIncludes: new Dictionary<string, string> {
                [directory.PathOf(name: include)] = ShaderInterfaceHlsl.Generate(shaderInterface: shader),
            }), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: build.IsSuccess, userMessage: string.Join(separator: Environment.NewLine, values: build.Diagnostics));
        return (shader, build);
    }
    private static void Verify(GpuDeviceServices services, ReadOnlyMemory<byte> bytecode, ShaderInterface shader) {
        var cases = Cases();
        var values = Run(bytecode: bytecode, cases: cases, services: services, shader: shader);
        var failures = new List<string>();

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];

            for (var component = 0; (component < 4); component++) {
                var actual = values[index][component];
                var tolerance = (0.00002f + (item.RelativeTolerance * MathF.Abs(x: item.Expected[component])));

                if (!float.IsFinite(f: actual) || (MathF.Abs(x: (actual - item.Expected[component])) > tolerance)) {
                    failures.Add(item: $"{item.Name}: component {component} {actual}, expected {item.Expected[component]}");
                }
            }
            if (values[(index + cases.Length)].Y != item.Samples) {
                failures.Add(item: $"{item.Name}: {values[(index + cases.Length)].Y} samples, expected {item.Samples}");
            }
            if (values[(index + cases.Length)].X != item.Hashes) {
                failures.Add(item: $"{item.Name}: {values[(index + cases.Length)].X} hashes, expected {item.Hashes}");
            }
        }
        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures));
    }
    private static Vector4[] Run(GpuDeviceServices services, ReadOnlyMemory<byte> bytecode, ShaderInterface shader, KindCase[] cases) {
        var layout = shader.Layout();
        var description = new GpuComputePipelineDescription(Bindings: [],
            Layout: layout.PipelineLayout(stages: GpuShaderStage.Compute), Name: shader.Name, PushConstantBinding: null);
        using var module = services.ShaderModuleFactory.Create(bytecode: bytecode, stage: GpuShaderStage.Compute);
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        using var output = services.ImageFactory.Create(format: GpuPixelFormat.R32G32B32A32Float,
            height: 2, name: default, usage: GpuImageUsage.Storage, width: ((uint)cases.Length));
        using var upload = services.SurfaceTransferFactory.CreateUpload();
        var sourceView = upload.Upload(pixels: PanoramaPixels(), format: GpuPixelFormat.R8G8B8A8Unorm, width: 4, height: 2);
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var pool = services.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(description.Layout!.Groups));
        var buffers = new List<IGpuStorageBuffer>();
        var records = new Dictionary<string, byte[]> {
            ["auroras"] = MemoryMarshal.AsBytes(span: cases.Select(selector: item => item.Aurora).ToArray().AsSpan()).ToArray(),
            ["panoramas"] = MemoryMarshal.AsBytes(span: cases.Select(selector: item => item.Panorama).ToArray().AsSpan()).ToArray(),
            ["noises"] = MemoryMarshal.AsBytes(span: cases.Select(selector: item => item.Noise).ToArray().AsSpan()).ToArray(),
            ["patterns"] = MemoryMarshal.AsBytes(span: cases.Select(selector: item => item.Pattern).ToArray().AsSpan()).ToArray(),
            ["gradients"] = MemoryMarshal.AsBytes(span: cases.Select(selector: item => item.Gradient).ToArray().AsSpan()).ToArray(),
            ["stops"] = MemoryMarshal.AsBytes(span: GradientStops().AsSpan()).ToArray(),
            ["stars"] = MemoryMarshal.AsBytes(span: cases.Select(selector: item => item.Stars).ToArray().AsSpan()).ToArray(),
            ["clouds"] = MemoryMarshal.AsBytes(span: cases.Select(selector: item => item.Clouds).ToArray().AsSpan()).ToArray(),
            ["directions"] = MemoryMarshal.AsBytes(span: cases.Select(selector: item => item.DirectionQuality).ToArray().AsSpan()).ToArray(),
            ["modes"] = MemoryMarshal.AsBytes(span: cases.Select(selector: item => item.Mode).ToArray().AsSpan()).ToArray(),
        };

        try {
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.TransitionImageLayout(commandBufferHandle: command, imageHandle: output.ImageHandle,
                oldLayout: GpuImageLayout.Undefined, newLayout: GpuImageLayout.General,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            foreach (var group in layout.Groups) {
                var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[((int)group.Set)], name: default, poolHandle: pool);

                foreach (var resource in group.Resources) {
                    if (records.TryGetValue(key: resource.Member.Name, value: out var bytes)) {
                        var buffer = services.BufferFactory.CreateHostVisible(data: bytes.AsSpan(), name: default, usage: GpuBufferUsage.Storage);

                        buffers.Add(item: buffer);
                        services.Bindings.WriteBuffer(descriptorSetHandle: set, binding: resource.Binding,
                            bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes,
                            elementStride: (resource.Member.Structure?.SizeBytes ?? 16u), kind: GpuBindingKind.ReadOnlyBuffer);
                    } else if (resource.Member.Name == "panoramaSource") {
                        services.Bindings.WriteSampledImage(descriptorSetHandle: set, binding: resource.Binding,
                            arrayElement: 0, imageViewHandle: sourceView);
                    } else {
                        services.Bindings.WriteStorageImage(descriptorSetHandle: set, binding: resource.Binding,
                            arrayElement: 0, imageViewHandle: output.ImageViewHandle);
                    }
                }
                recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command,
                    descriptorSetHandle: set, group: group.Set, pipelineLayoutHandle: pipeline.LayoutHandle);
            }
            recorder.Dispatch(commandBufferHandle: command, groupCountX: ((uint)cases.Length), groupCountY: 1, groupCountZ: 1);
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            var result = readback.Read(bytesPerPixel: 16, format: GpuPixelFormat.R32G32B32A32Float, height: 2,
                width: ((uint)cases.Length), sourceImageHandle: output.ImageHandle, sourceLayout: GpuImageLayout.General);

            return MemoryMarshal.Cast<byte, Vector4>(span: result.Span).ToArray();
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
            foreach (var buffer in buffers) { buffer.Dispose(); }
        }
    }
}
