using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfLightingInterfaceDeviceLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task SharedLightResponseSuppliesCloudDirectionRadianceAndAmbientWithoutMaterialResources(bool directX) {
        using var directory = new TemporaryDirectory(prefix: "puck-light-response-");
        var shader = new ShaderInterface(name: "light-response", members: [
            ShaderInterfaceMember.StorageImage(format: GpuPixelFormat.R8G8B8A8Unorm, group: ShaderInterfaceGroup.Pass,
                name: ShaderInterfaceEcho.OutputName, type: ShaderValueType.Float4),
        ]);
        var include = ShaderInterfaceHlsl.FileName(shaderInterface: shader);
        var root = RepositoryPaths.Resolve(relativePath: "src/Puck.SdfVm/Assets/Shaders/Sdf").Replace(newChar: '/', oldChar: '\\');
        var source = $$"""
            #include "{{include}}"
            #include "{{root}}/isa/sdf-isa.hlsli"
            #define SDF_SKY_PASS
            #include "{{root}}/shade/sdf-light.hlsli"
            bool near(float3 actual, float3 expected) {
                return all(isfinite(actual)) && all(abs(actual - expected) < 0.000002);
            }
            [numthreads(16, 1, 1)]
            void CSMain(uint3 id : SV_DispatchThreadID) {
                if (id.x >= 9u) return;
                SdfLight light = (SdfLight)0;
                light.color = float3(0.2, 0.4, 0.8);
                light.weight = 2.0;
                light.position = float3(0.0, 0.6, 0.8);
                SdfShadeSurface surface = (SdfShadeSurface)0;
                surface.normal = float3(0.0, 1.0, 0.0);
                surface.rayDirection = float3(0.0, 0.0, -1.0);
                surface.ambientOcclusion = 0.5;
                surface.keyVisibility = 0.25;
                float3 diffuse = 0.0, weighted = 0.0, toward = 0.0, specular = 0.0, rim = 0.0;
                float attenuation = 1.0;
                bool ambient = false, directed = false;
                if (id.x == 0u || id.x == 6u || id.x == 7u) {
                    light.kind = SDF_LIGHT_DIRECTIONAL;
                    light.key = id.x == 7u;
                    if (id.x == 6u) light.weight = 0.0;
                    float gain = id.x == 6u ? 0.0 : (id.x == 7u ? 0.5 : 1.0);
                    diffuse = float3(0.12, 0.24, 0.48) * gain;
                    weighted = light.color * gain;
                    toward = float3(0.0, 0.6, 0.8);
                    directed = id.x != 6u;
                } else if (id.x == 1u) {
                    light.kind = SDF_LIGHT_HEMISPHERE;
                    light.weight = 0.3;
                    light.param = 0.2;
                    diffuse = float3(0.05, 0.1, 0.2);
                    ambient = true;
                } else if (id.x == 2u) {
                    light.kind = SDF_LIGHT_POINT;
                    light.position = float3(0.0, 3.0, 4.0);
                    light.param = 5.0;
                    diffuse = float3(0.06, 0.12, 0.24);
                    weighted = float3(0.1, 0.2, 0.4);
                    toward = float3(0.0, 0.6, 0.8);
                    directed = true;
                } else if (id.x == 3u) {
                    light.kind = SdfLightScreen;
                    light.position = float3(0.0, 3.0, 4.0);
                    light.facing = float3(0.0, -0.6, -0.8);
                    diffuse = float3(0.03, 0.06, 0.12);
                    weighted = float3(0.05, 0.1, 0.2);
                    toward = float3(0.0, 0.6, 0.8);
                    directed = true;
                } else if (id.x == 4u) {
                    light.kind = SDF_LIGHT_RIM;
                    light.param = 2.0;
                    rim = float3(0.4, 0.8, 1.6);
                } else if (id.x == 5u) {
                    light.kind = SDF_LIGHT_OCCLUDER;
                    light.position = 0.0;
                    light.weight = 0.5;
                    light.param = 1.0;
                    attenuation = 0.5;
                } else {
                    light.kind = SDF_LIGHT_POINT;
                    light.position = float3(0.0, 2.0, 0.0);
                    light.param = 2.0;
                    surface.rayDirection = float3(0.0, -1.0, 0.0);
                    surface.ambientOcclusion = 1.0;
                    surface.material.specular = 0.5;
                    surface.material.roughness = 0.5;
                    toward = float3(0.0, 1.0, 0.0);
                    weighted = diffuse = light.color;
                    // Aligned N,V,L make geometry and N.L one: GGX reduces to f0/(4*pi*alpha2).
                    specular = light.color * (0.5 / (4.0 * SDF_PI * 0.268));
                    directed = true;
                }
                SdfLightResponse response = sdfLightResponse(light, surface);
                bool good = near(response.diffuse, diffuse) && near(response.weightedColor, weighted)
                    && near(response.towardLight, toward) && near(response.specular, specular)
                    && near(response.rim, rim) && near(response.attenuation, attenuation)
                    && response.ambient == ambient && response.directed == directed;
                {{ShaderInterfaceEcho.OutputName}}[uint2(id.x, 0u)] = good ? float4(0, 1, 0, 1) : float4(1, 0, 0, 1);
            }
            """;
        var compiler = new ShaderCompiler(cacheDirectory: directory.PathOf(name: "cache"));
        var build = await compiler.CompileAsync(descriptor: new ShaderCompilationRequest(name: shader.Name,
            stages: [new ShaderStageSource(ShaderStage.Compute, directory.PathOf(name: "probe.comp.hlsl"), source, EntryPoint: "CSMain")],
            generatedIncludes: new Dictionary<string, string> { [directory.PathOf(name: include)] = ShaderInterfaceHlsl.Generate(shaderInterface: shader) }
        ), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: build.IsSuccess, userMessage: string.Join(separator: Environment.NewLine, values: build.Diagnostics));
        byte[] pixels;

        if (directX) {
            using var device = DirectXTestDevices.Hardware();

            pixels = Run(device.Services, build.Dxil, shader, false, 9);
        } else {
            using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfLightingInterfaceDeviceLawTests));

            pixels = Run(device.Services, build.Spirv, shader, false, 9);
        }
        string[] names = ["directional", "hemisphere", "point", "screen", "rim", "occluder", "zero-weight", "key-visibility", "point-specular"];

        for (var index = 0; (index < names.Length); index++) {
            Assert.True(condition: pixels.AsSpan(length: 4, start: (index * 4)).SequenceEqual(other: new byte[] { 0, 255, 0, 255 }),
                userMessage: $"{names[index]}: shared light response differs from its independent expected diffuse, direction, radiance or classification");
        }
    }
}
