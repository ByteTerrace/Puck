using Puck.Shaders;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The counted cost of an <c>sdf.world</c> pass's constant data, held to the generated interface: every pass block is
/// the world interface's pass block, which holds the view and frame values and no light or sky table, and each kernel
/// the build deploys binds the lights table and the sky's block and tables only when its pass reads them (the lights
/// the shadow and views passes, the sky block and its stops the sky and views passes, the softboxes views alone), as its
/// SPIR-V reflects.
/// </summary>
public sealed class SdfPassBindingLawTests {
    // The pass block's bytes: the extent and the world values, the view, the levers, the light count and shadow light, and
    // the curvature shading, 16-aligned.
    private const uint PassBlockBytes = 496;

    private static readonly SdfKernel[] ViewsKernels = [SdfKernel.Views, SdfKernel.ViewsCore, SdfKernel.ViewsFolds];
    // The kernels each light and sky table is bound by, and no other.
    private static readonly (string Table, SdfKernel[] Readers)[] Readers = [
        (SdfKernelInterfaces.Lights, [SdfKernel.Shadow, .. ViewsKernels]),
        (SdfKernelInterfaces.Sky, [SdfKernel.Sky, SdfKernel.Composite, .. ViewsKernels]),
        (SdfKernelInterfaces.SkyStops, [SdfKernel.Sky, SdfKernel.Composite]),
        (SdfKernelInterfaces.Softboxes, ViewsKernels),
    ];

    private static ShaderInterfaceGroupLayout Group(ShaderInterfaceLayout layout, ShaderInterfaceGroup group) =>
        layout.Groups.Single(predicate: candidate => (candidate.Group == group));

    [Fact]
    public void ThePassBlockIsTheGeneratedBlockAndCarriesNoLightOrSkyTable() {
        var pass = Group(group: ShaderInterfaceGroup.Pass, layout: SdfWorldInterfaces.WorldLayout);
        var tables = SdfKernelInterfaces.LightAndSkyTables.Select(selector: static member => member.Name).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Equal(expected: PassBlockBytes, actual: pass.BlockSizeBytes);
        Assert.Equal(expected: ((int)PassBlockBytes), actual: SdfFrameBlock.SizeBytes);
        Assert.DoesNotContain(collection: pass.BlockMembers, filter: member => tables.Contains(item: member.Name));
        // The tables are World-group records, written once a frame into regions, never a pass's constant data.
        Assert.All(
            action: table => Assert.Contains(collection: Group(group: ShaderInterfaceGroup.World, layout: SdfWorldInterfaces.WorldLayout).Resources, filter: resource => (resource.Member.Name == table)),
            collection: tables
        );
    }
    // Every pass of a view binds the residency's one World set, the resolve, sky and composite passes among them, so the World group its
    // pipeline is created from is the world interface's, binding for binding; a group missing a table is a set layout
    // the bound set does not match.
    [Fact]
    public void TheResolvePassLaysOutTheWorldGroupItBinds() =>
        Assert.Equal(
            actual: Group(group: ShaderInterfaceGroup.World, layout: SdfWorldInterfaces.ResolveParameters.Layout).Bindings,
            expected: Group(group: ShaderInterfaceGroup.World, layout: SdfWorldInterfaces.WorldLayout).Bindings
        );
    [Fact]
    public void TheSkyAndCompositePassesLayOutTheWorldGroupTheyBind() =>
        Assert.Equal(
            actual: Group(group: ShaderInterfaceGroup.World, layout: SdfWorldInterfaces.SkyParameters.Layout).Bindings,
            expected: Group(group: ShaderInterfaceGroup.World, layout: SdfWorldInterfaces.WorldLayout).Bindings
        );
    [Fact]
    public void EachDeployedKernelBindsALightOrSkyTableOnlyWhenItsPassReadsIt() {
        var kernels = SdfKernelSet.Load(bytecodeExtension: ".spv");
        var world = Group(group: ShaderInterfaceGroup.World, layout: SdfWorldInterfaces.WorldLayout);

        foreach (var kernel in SdfKernelSet.Kernels.Where(predicate: static kernel => (kernel != SdfKernel.BrickBake))) {
            var reflected = SpirvInterfaceReader.Read(module: kernels[kernel].Span);

            // The kernel's bindings are its interface's, stamp included.
            Assert.Null(@object: SdfKernelSet.InterfaceMismatch(kernel: kernel, reflected: reflected));

            foreach (var (table, readers) in Readers) {
                var binding = SdfKernelInterfaces.BindingOf(layout: SdfWorldInterfaces.WorldLayout, member: table);
                var binds = reflected.Any(predicate: candidate => ((candidate.Set == world.Set) && (candidate.Binding == binding)));

                Assert.True(condition: (binds == readers.Contains(value: kernel)), userMessage: $"{kernel} {(binds ? "binds" : "does not bind")} {table}");
            }
        }
    }
}
