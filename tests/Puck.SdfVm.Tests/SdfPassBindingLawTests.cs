using Puck.Maths;
using Puck.Shaders;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The counted cost of an <c>sdf.world</c> pass's constant data, held to the generated interface: every pass block is
/// the world interface's pass block, which holds the view and frame values and no light or sky table, and each kernel
/// the build deploys binds the lights table and the sky's block and tables only when its pass reads them (the lights
/// views, the sky block and layers their evaluating passes, the coefficients
/// views alone, and the environment map its radiance readers), as its SPIR-V reflects.
/// </summary>
public sealed class SdfPassBindingLawTests {
    private static readonly SdfKernel[] ViewsKernels = [SdfKernel.Views];
    // The kernels each light and sky table is bound by, and no other.
    // The glass shading pass lights its highlights from the lights table, reflects the environment map and reads the
    // atmosphere's lanes of the sky block.
    private static readonly (string Table, SdfKernel[] Readers)[] Readers = [
        (SdfKernelInterfaces.Lights, [SdfKernel.Glass, .. ViewsKernels]),
        (SdfKernelInterfaces.Sky, [SdfKernel.Sky, SdfKernel.Composite, SdfKernel.Resolve, SdfKernel.SkyEnvironment, SdfKernel.Glass, .. ViewsKernels]),
        (SdfKernelInterfaces.SkyLayers, [SdfKernel.Sky, SdfKernel.Composite, SdfKernel.SkyEnvironment, .. ViewsKernels]),
        (SdfKernelInterfaces.SkyCoefficients, ViewsKernels),
        (SdfKernelInterfaces.SkyEnvironment, [SdfKernel.Composite, SdfKernel.SkyEnvironmentReduce, SdfKernel.Glass, .. ViewsKernels]),
    ];

    private static ShaderInterfaceGroupLayout Group(ShaderInterfaceLayout layout, ShaderInterfaceGroup group) =>
        layout.Groups.Single(predicate: candidate => (candidate.Group == group));
    // The 16-aligned byte length of a pass block holding the extent and the package's pass-block values in ordinal name
    // order, with each member aligned by the interface layout rule.
    private static uint PassBlockBytes(IReadOnlyList<ShaderInterfaceMember> values) {
        const uint RowBytes = 16;
        var cursor = ShaderValueType.Uint2.SizeBytes();

        foreach (var member in values.OrderBy(comparer: StringComparer.Ordinal, keySelector: static member => member.Name)) {
            var type = member.Type!.Value;
            var length = (member.Length ?? 0u);
            var alignment = ((length != 0u)
                ? RowBytes
                : type.ComponentCount() switch {
                    1 => 4u,
                    2 => 8u,
                    _ => RowBytes,
                });
            var offset = cursor.AlignUp(alignment: alignment);

            cursor = (offset + ((length != 0u)
                ? (length * RowBytes)
                : type.SizeBytes()));
        }

        return cursor.AlignUp(alignment: RowBytes);
    }

    [Fact]
    public void ThePassBlockIsTheGeneratedBlockAndCarriesNoLightOrSkyTable() {
        var pass = Group(group: ShaderInterfaceGroup.Pass, layout: SdfWorldInterfaces.WorldLayout);
        var passBlockBytes = PassBlockBytes(values: SdfWorldPackage.Values);
        var tables = SdfKernelInterfaces.LightAndSkyTables.Select(selector: static member => member.Name).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Equal(expected: passBlockBytes, actual: pass.BlockSizeBytes);
        Assert.Equal(expected: ((int)passBlockBytes), actual: SdfFrameBlock.SizeBytes);
        Assert.Equal(
            actual: pass.BlockMembers
                .Where(predicate: static member => !member.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "_pad"))
                .Select(selector: static member => member.Name),
            expected: [ShaderFrameInterface.Extent, .. SdfWorldPackage.Values.Select(selector: static member => member.Name).Order(comparer: StringComparer.Ordinal)]
        );
        Assert.DoesNotContain(collection: pass.BlockMembers, filter: member => tables.Contains(item: member.Name));
        // The tables are World-group records, written once a frame into regions, never a pass's constant data.
        Assert.All(
            action: table => Assert.Contains(collection: Group(group: ShaderInterfaceGroup.World, layout: SdfWorldInterfaces.WorldLayout).Resources, filter: resource => (resource.Member.Name == table)),
            collection: tables
        );
    }
    // The bake's mask and sampler read a pass block of their own, holding the batch they sample and nothing of the view,
    // and every pass group member the bake declares is one it reads: a value or resource another pass gains is no member
    // of it, so the bake kernels' declarations, whose bytecode keys the stored meshes, stay as they were.
    [Fact]
    public void TheBakePassBlockHoldsTheBatchAlone() {
        var pass = Group(group: ShaderInterfaceGroup.Pass, layout: SdfWorldInterfaces.MeshBakeLayout);
        var bakeBlockBytes = PassBlockBytes(values: SdfWorldPackage.BakeValues);

        Assert.Equal(expected: bakeBlockBytes, actual: pass.BlockSizeBytes);
        Assert.Equal(expected: ((int)bakeBlockBytes), actual: SdfFrameBlock.BakeSizeBytes);
        Assert.Equal(
            actual: pass.BlockMembers.Select(selector: static member => member.Name),
            expected: [ShaderFrameInterface.Extent, SdfWorldPackage.InstanceMaskWordCount, SdfWorldPackage.MeshBakeCount, SdfWorldPackage.MeshBakeLattices, ShaderWorkCounters.Row, ShaderWorkCounters.DetailRow]
        );
        Assert.Equal(
            actual: pass.Resources.Select(selector: static resource => resource.Member.Name),
            expected: [SdfWorldPackage.InstanceMasks, SdfWorldPackage.InstanceMasksWritten, SdfWorldPackage.MeshBakeSamplesWritten, ShaderWorkCounters.Buffer]
        );
        // The world interface holds none of the bake's own values or resources.
        Assert.DoesNotContain(
            collection: Group(group: ShaderInterfaceGroup.Pass, layout: SdfWorldInterfaces.WorldLayout).BlockMembers,
            filter: static member => (member.Name is SdfWorldPackage.InstanceMaskWordCount or SdfWorldPackage.MeshBakeCount or SdfWorldPackage.MeshBakeLattices)
        );
    }
    // The bake's parts bind the residency's World set too, so their World group is the world interface's; the field's
    // tables lead it, so a table added after them moves no binding the bake kernels read.
    [Fact]
    public void TheBakePartsLayOutTheWorldGroupTheyBindWithTheFieldsTablesFirst() {
        var world = Group(group: ShaderInterfaceGroup.World, layout: SdfWorldInterfaces.WorldLayout);

        Assert.Equal(
            actual: Group(group: ShaderInterfaceGroup.World, layout: SdfWorldInterfaces.MeshBakeLayout).Bindings,
            expected: world.Bindings
        );
        Assert.Equal(
            actual: world.Resources.Take(count: SdfWorldPackage.FieldTables.Count).Select(selector: static resource => resource.Member.Name),
            expected: SdfWorldPackage.FieldTables.Select(selector: static member => member.Name)
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

        foreach (var kernel in SdfKernelSet.Kernels.Where(predicate: static kernel => (kernel != SdfKernel.BrickBake))) {
            var reflected = SpirvInterfaceReader.Read(module: kernels[kernel].Span);

            // The kernel's bindings are its interface's, stamp included.
            Assert.Null(@object: SdfKernelSet.InterfaceMismatch(kernel: kernel, reflected: reflected));

            foreach (var (table, readers) in Readers) {
                var binds = SdfKernelSet.LayoutOf(kernel: kernel).Groups.Any(predicate: group =>
                    group.Resources.Any(predicate: resource => ((resource.Member.Name == table) &&
                        reflected.Any(predicate: candidate => ((candidate.Set == group.Set) && (candidate.Binding == resource.Binding))))));

                Assert.True(condition: (binds == readers.Contains(value: kernel)), userMessage: $"{kernel} {(binds ? "binds" : "does not bind")} {table}");
            }
        }
    }
}
