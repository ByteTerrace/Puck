using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.Testing;
using Windows.Win32.Graphics.Direct3D12;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Creates the gate spike's plan-built layouts on the default adapter with the Direct3D 12 debug layer on: the
/// film grain, pixelate and arrays root signatures (<see cref="DirectXRootSignatures.CreateLayout"/>), and a film grain
/// pass-group set whose sampler table comes from the device's sampler heap, with no <c>[d3d12-debug]</c> line. It creates
/// no pipeline state, so it says nothing of a root signature's agreement with a shader. It runs alone, beside the
/// liveness tests, because turning the debug layer on removes every device the process already holds; it skips when the
/// host has no device or no debug layer.</summary>
[Collection(name: nameof(ConsoleErrorCollection))]
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGroupedLayoutDebugLayerTests {
    [Fact]
    public void The_spike_layouts_create_on_the_default_adapter_with_no_debug_layer_message() {
        var output = new StringWriter();
        var context = DirectXTestDevices.Debug(output: output);
        var filmGrain = GpuGroupLayoutTables.FilmGrain(pushesIndex: true);

        try {
            var bindings = context.Services.Bindings;

            foreach (var description in ((GpuPipelineLayoutDescription[])[filmGrain, GpuGroupLayoutTables.Pixelate(pushesIndex: true), GpuGroupLayoutTables.Arrays()])) {
                using var layout = DirectXRootSignatures.CreateLayout(
                    description: description,
                    device: ((ID3D12Device*)context.Device.Handle)
                );

                Assert.NotEqual(
                    actual: layout.RootSignatureHandle,
                    expected: 0
                );

                if (ReferenceEquals(objA: description, objB: filmGrain)) {
                    var pool = bindings.CreatePool(sizes: GpuDescriptorPoolSizes.ForGroups(groups: filmGrain.Groups));

                    _ = bindings.AllocateSet(
                        descriptorSetLayoutHandle: layout.GroupHandles[3],
                        poolHandle: pool
                    );
                    bindings.DestroyPool(poolHandle: pool);
                }
            }

            context.DrainDebugMessages();
        } finally {
            context.Dispose();
        }

        Assert.DoesNotContain(
            actualString: output.ToString(),
            expectedSubstring: "[d3d12-debug]"
        );
    }
}
