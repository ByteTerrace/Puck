using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Shadow handoff resources follow configured capacity, one shadow kernel and one kernel per views variant
/// serve every capacity, and the handoff controls share one native layout with HLSL.</summary>
public sealed class SdfShadowResourceLawTests {
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void ZeroFadeCapacityAllocatesNoIncomingImageWhileEveryInterfaceDeclaresIt(bool reconstructs, bool temporal) {
        var fragment = SdfWorldPackage.FragmentFor(fadeCapacity: 0, reconstructs: reconstructs, temporal: temporal);

        Assert.DoesNotContain(collection: fragment.Resources, filter: resource => (resource.Name == SdfWorldPackage.IncomingVisibility));
        // The members stay declared so a pass binds the tables' fillers there (SdfWorldTables.IncomingStorageFiller).
        Assert.Equal(expected: 2, actual: SdfWorldInterfaces.World.Members.Count(predicate: IsIncoming));
        Assert.All(collection: fragment.Passes, action: pass => {
            Assert.DoesNotContain(collection: pass.Inputs, filter: input => (input.Name == SdfWorldPackage.IncomingVisibility));
            Assert.DoesNotContain(collection: pass.Outputs, filter: output => (output.Name == SdfWorldPackage.IncomingVisibility));
        });
    }
    [Fact]
    public void EveryNonzeroCapacityPlansOneTwoChannelImageWrittenBeforeItIsRead() {
        foreach (var (reconstructs, temporal) in new[] { (false, false), (true, false), (false, true) }) {
            var fragment = SdfWorldPackage.FragmentFor(fadeCapacity: 1, reconstructs: reconstructs, temporal: temporal);
            var image = Assert.Single(collection: fragment.Resources, predicate: resource => (resource.Name == SdfWorldPackage.IncomingVisibility));

            Assert.True(condition: image.Retained);
            Assert.False(condition: image.Transient);
            Assert.Equal(expected: GpuPixelFormat.R8G8Unorm.ToString(), actual: image.Format);
            // Both capacities plan the same graph, so a policy moving between them rebuilds nothing.
            Assert.Same(expected: fragment, actual: SdfWorldPackage.FragmentFor(fadeCapacity: 2, reconstructs: reconstructs, temporal: temporal));
            var shadow = fragment.Passes.Single(predicate: pass => (pass.Name == SdfWorldPackage.Parts.Shadow));
            var views = fragment.Passes.Single(predicate: pass => (pass.Name == SdfWorldPackage.Parts.Views));

            Assert.Equal(expected: SdfWorldPackage.IncomingVisibility, actual: shadow.Outputs[^1].Name);
            Assert.Equal(expected: RenderGraphPortAccess.ComputeWrite, actual: shadow.OutputAccesses[^1]);
            Assert.Equal(expected: SdfWorldPackage.IncomingVisibility, actual: views.Inputs[^1].Name);
            Assert.Equal(expected: RenderGraphPortAccess.ComputeRead, actual: views.InputAccesses[^1]);
            // The passes read the one world interface, whose incoming members serve every capacity.
            Assert.Null(@object: shadow.Members);
            Assert.Null(@object: views.Members);
        }
        Assert.All(collection: SdfWorldInterfaces.World.Members.Where(predicate: IsIncoming), action: member => Assert.Equal(expected: ShaderValueType.Float2, actual: member.Type));
    }
    [Fact]
    public void TheIncomingStorageDeclarationUsesTheTwoChannelNormalizedByteFormat() {
        var include = ShaderInterfaceHlsl.Generate(shaderInterface: SdfWorldInterfaces.World);

        Assert.Contains(actualString: include, expectedSubstring: "[[vk::image_format(\"rg8\")]] RWTexture2D<float2> incomingVisibilityRW");
        Assert.Contains(actualString: include, expectedSubstring: "Texture2D<float2> incomingVisibility ");
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
    public void OnlyTheShadowAndViewsBytecodeReadsTheIncomingImageAndHandoffTable() {
        var kernels = SdfKernelSet.Load(bytecodeExtension: ".spv");

        // The one shadow kernel writes the image and each views variant reads it, at every fade capacity.
        foreach (var (kernel, member) in new[] {
            (SdfKernel.Shadow, SdfWorldPackage.IncomingVisibilityWritten), (SdfKernel.Views, SdfWorldPackage.IncomingVisibility),
            (SdfKernel.ViewsCore, SdfWorldPackage.IncomingVisibility), (SdfKernel.ViewsFolds, SdfWorldPackage.IncomingVisibility),
        }) {
            var bindings = SpirvInterfaceReader.Read(module: kernels[kernel].Span);

            Assert.Contains(collection: bindings, filter: binding => binding.Name.EndsWith(comparisonType: StringComparison.Ordinal, value: member));
            Assert.Contains(collection: bindings, filter: binding => binding.Name.Contains(comparisonType: StringComparison.Ordinal, value: SdfKernelInterfaces.ShadowHandoffs));
        }
        // The passes before them compile no fade slot.
        foreach (var kernel in new[] { SdfKernel.Primary, SdfKernel.Surface, SdfKernel.Ambient }) {
            var bindings = SpirvInterfaceReader.Read(module: kernels[kernel].Span);

            Assert.DoesNotContain(collection: bindings, filter: binding => binding.Name.Contains(comparisonType: StringComparison.Ordinal, value: SdfWorldPackage.IncomingVisibility));
        }
    }

    private static bool IsIncoming(ShaderInterfaceMember member) => (member.Name is SdfWorldPackage.IncomingVisibility or SdfWorldPackage.IncomingVisibilityWritten);
}
