using Puck.Abstractions.Counting;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The eager catalogue reads only the selected backend and shares its resident payload across view leases.</summary>
public sealed class SdfCatalogueMemoryLawTests {
    [InlineData(".spv")]
    [InlineData(".dxil")]
    [Theory]
    public void ALoadNeedsOnlyTheSelectedBackendsFiles(string extension) {
        using var directory = new TemporaryDirectory();
        foreach (var kernel in SdfKernelSet.Kernels) {
            directory.WriteBytes($"{SdfKernelSet.StemOf(kernel)}.comp{extension}", [3, 7, 11]);
        }
        var work = new WorkCounterSet(name: "test.kernel-load", kinds: [SdfKernelSet.Loads, SdfKernelSet.BytecodeBytes]);
        var kernels = SdfKernelSet.Load(bytecodeExtension: extension, directory: directory.RootPath, work: work);
        var expected = SdfKernelSet.Kernels.Count * 3L;

        Assert.Equal(1L, work.Read(SdfKernelSet.Loads));
        Assert.Equal(expected, work.Read(SdfKernelSet.BytecodeBytes));
        Assert.Equal(expected, kernels.ResidentBytecodeBytes);
        Assert.All(SdfKernelSet.Kernels, kernel => Assert.Equal(new byte[] { 3, 7, 11 }, kernels[kernel].ToArray()));
    }

    [InlineData(".spv")]
    [InlineData(".dxil")]
    [Theory]
    public void TwoViewPipelineSetsShareOneBackendCatalogueAndCountItsBytesOnce(string extension) {
        var gpu = new FakeGpuDevice();
        var catalog = SdfTestPipelines.Cache();
        var active = (extension == ".spv" ? SdfWorldPipelineCatalog.VulkanResidentBytes : SdfWorldPipelineCatalog.DirectXResidentBytes);
        var inactive = (extension == ".spv" ? SdfWorldPipelineCatalog.DirectXResidentBytes : SdfWorldPipelineCatalog.VulkanResidentBytes);
        var expected = SdfKernelSet.Kernels.Sum(kernel => new FileInfo(Path.Combine(SdfKernelSet.DefaultDirectory, $"{SdfKernelSet.StemOf(kernel)}.comp{extension}")).Length);

        Assert.Equal(0L, catalog.Work.Read(active));
        Assert.Equal(0L, catalog.Work.Read(inactive));
        var firstKernels = catalog.LoadDeployed(extension);
        using var first = SdfTestPipelines.Build(device: gpu, kernels: firstKernels, cache: catalog.Pipelines);
        var secondKernels = catalog.LoadDeployed(extension);
        using var second = SdfTestPipelines.Build(device: gpu, kernels: secondKernels, cache: catalog.Pipelines);

        Assert.Same(firstKernels, secondKernels);
        Assert.Same(first.Kernels, second.Kernels);
        Assert.Equal(expected, firstKernels.ResidentBytecodeBytes);
        Assert.Equal(expected, catalog.Work.Read(active));
        Assert.Equal(0L, catalog.Work.Read(inactive));
        Assert.Equal(WorkClass.PerBackendDeterministic, active.Class);
        foreach (var kernel in SdfKernelSet.Kernels) {
            // ReadOnlyMemory equality requires the same backing array, offset and length, rather than equal copies.
            Assert.Equal(first.Kernels[kernel], second.Kernels[kernel]);
        }
        second.Dispose(); first.Dispose();
        Assert.Equal(expected, catalog.Work.Read(active));
        Assert.Same(firstKernels, catalog.LoadDeployed(extension));
    }
}
