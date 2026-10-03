using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Shadow handoff resources follow configured capacity and share one native control layout with HLSL.</summary>
public sealed class SdfShadowResourceLawTests {
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void ZeroFadeCapacityHasNoIncomingImageOrBinding(bool reconstructs, bool temporal) {
        var fragment = SdfWorldPackage.FragmentFor(fadeCapacity: 0, reconstructs: reconstructs, temporal: temporal);

        Assert.DoesNotContain(collection: fragment.Resources, filter: resource => (resource.Name == SdfWorldPackage.IncomingVisibility));
        Assert.DoesNotContain(collection: SdfWorldInterfaces.World.Members, filter: IsIncoming);
        Assert.All(collection: fragment.Passes, action: pass => {
            Assert.DoesNotContain(collection: pass.Inputs, filter: input => (input.Name == SdfWorldPackage.IncomingVisibility));
            Assert.DoesNotContain(collection: pass.Outputs, filter: output => (output.Name == SdfWorldPackage.IncomingVisibility));
        });
    }
    [InlineData(1, GpuPixelFormat.R8Unorm, ShaderValueType.Float)]
    [InlineData(2, GpuPixelFormat.R8G8Unorm, ShaderValueType.Float2)]
    [Theory]
    public void PolicyReservesExactlyItsIncomingChannelsAndOrdersTheirWriterBeforeReader(int capacity, GpuPixelFormat format, ShaderValueType type) {
        foreach (var (reconstructs, temporal) in new[] { (false, false), (true, false), (false, true) }) {
            var fragment = SdfWorldPackage.FragmentFor(fadeCapacity: capacity, reconstructs: reconstructs, temporal: temporal);
            var image = Assert.Single(collection: fragment.Resources, predicate: resource => (resource.Name == SdfWorldPackage.IncomingVisibility));

            Assert.True(condition: image.Retained);
            Assert.False(condition: image.Transient);
            Assert.Equal(expected: format.ToString(), actual: image.Format);
            Assert.Same(expected: fragment, actual: SdfWorldPackage.FragmentFor(fadeCapacity: capacity, reconstructs: reconstructs, temporal: temporal));
            var shadow = fragment.Passes.Single(predicate: pass => (pass.Name == SdfWorldPackage.Parts.Shadow));
            var views = fragment.Passes.Single(predicate: pass => (pass.Name == SdfWorldPackage.Parts.Views));

            Assert.Equal(expected: SdfWorldPackage.IncomingVisibility, actual: shadow.Outputs[^1].Name);
            Assert.Equal(expected: RenderGraphPortAccess.ComputeWrite, actual: shadow.OutputAccesses[^1]);
            Assert.Equal(expected: SdfWorldPackage.IncomingVisibility, actual: views.Inputs[^1].Name);
            Assert.Equal(expected: RenderGraphPortAccess.ComputeRead, actual: views.InputAccesses[^1]);
            Assert.All(collection: shadow.Members!.Where(predicate: IsIncoming), action: member => Assert.Equal(expected: type, actual: member.Type));
            Assert.Equal(expected: 2, actual: shadow.Members!.Count(predicate: IsIncoming));
        }
    }
    [Fact]
    public void IncomingStorageDeclarationsUseTheConfiguredNormalizedByteFormat() {
        foreach (var (capacity, format, type) in new[] { (1, "r8", "float"), (2, "rg8", "float2") }) {
            var include = ShaderInterfaceHlsl.Generate(shaderInterface: SdfWorldInterfaces.WorldFadeParameters[capacity].Interface);

            Assert.Contains(actualString: include, expectedSubstring: $"[[vk::image_format(\"{format}\")]] RWTexture2D<{type}> incomingVisibilityRW");
        }
    }
    [Fact]
    public void FadeInterfacesKeepEveryCommonBlockOffsetAndBinding() {
        foreach (var parameters in SdfWorldInterfaces.WorldFadeParameters) {
            foreach (var value in SdfWorldPackage.Values) {
                Assert.Equal(expected: SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: value.Name), actual: parameters.BlockOffsetOf(member: value.Name));
            }
            foreach (var group in SdfWorldInterfaces.WorldLayout.Groups) {
                foreach (var resource in group.Resources) {
                    Assert.Equal(expected: resource.Binding, actual: SdfKernelInterfaces.BindingOf(layout: parameters.Layout, member: resource.Member.Name));
                }
            }
        }
    }
    [Fact]
    public void HandoffControlsAreFourNativeWordsInTheGeneratedWorldTable() {
        var member = SdfKernelInterfaces.LightAndSkyTables.Single(predicate: member => (member.Name == SdfKernelInterfaces.ShadowHandoffs));
        var structure = Assert.IsType<ShaderInterfaceStructure>(@object: member.Structure);

        Assert.Equal(expected: ShaderInterfaceGroup.World, actual: member.Group);
        Assert.Equal(expected: 16u, actual: structure.SizeBytes);
        Assert.Equal(expected: ["Outgoing", "Incoming", "Slot", "Weight"], actual: structure.Members.Select(selector: field => field.Name));
        Assert.Equal(expected: [0u, 4u, 8u, 12u], actual: structure.Members.Select(selector: field => field.Offset));
        Assert.Equal(expected: [ShaderValueType.Int, ShaderValueType.Int, ShaderValueType.Int, ShaderValueType.Float], actual: structure.Members.Select(selector: field => field.Type));
        var control = new SdfShadowHandoff(Incoming: 7, Outgoing: 3, Slot: 2, Weight: 0.375f);
        Span<byte> bytes = stackalloc byte[16];

        MemoryMarshal.Write(destination: bytes, value: in control);
        Assert.Equal(expected: 3, actual: BinaryPrimitives.ReadInt32LittleEndian(source: bytes));
        Assert.Equal(expected: 7, actual: BinaryPrimitives.ReadInt32LittleEndian(source: bytes[4..]));
        Assert.Equal(expected: 2, actual: BinaryPrimitives.ReadInt32LittleEndian(source: bytes[8..]));
        Assert.Equal(expected: 0.375f, actual: BinaryPrimitives.ReadSingleLittleEndian(source: bytes[12..]));
        var include = ShaderInterfaceHlsl.Generate(shaderInterface: SdfWorldInterfaces.World);

        Assert.Contains(actualString: include, expectedSubstring: "struct SdfShadowHandoff");
        Assert.Contains(actualString: include, expectedSubstring: "StructuredBuffer<SdfShadowHandoff>");
    }
    [Fact]
    public void ZeroFadeBytecodeDoesNotReadAnIncomingImageOrHandoffTable() {
        var kernels = SdfKernelSet.Load(bytecodeExtension: ".spv");

        foreach (var kernel in new[] { SdfKernel.Shadow, SdfKernel.Views, SdfKernel.ViewsCore, SdfKernel.ViewsFolds }) {
            var bindings = SpirvInterfaceReader.Read(module: kernels[kernel].Span);

            Assert.DoesNotContain(collection: bindings, filter: binding => binding.Name.Contains(comparisonType: StringComparison.Ordinal, value: SdfWorldPackage.IncomingVisibility));
            Assert.DoesNotContain(collection: bindings, filter: binding => binding.Name.Contains(comparisonType: StringComparison.Ordinal, value: SdfKernelInterfaces.ShadowHandoffs));
        }
    }

    private static bool IsIncoming(ShaderInterfaceMember member) => (member.Name is SdfWorldPackage.IncomingVisibility or SdfWorldPackage.IncomingVisibilityWritten);
}
