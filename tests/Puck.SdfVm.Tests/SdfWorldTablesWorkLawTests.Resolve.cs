using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesWorkLawTests {
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void ResolveActivatesOnceAndJoinsReloadEvenWhenActivationFollowsPreparation(bool activateBeforePreparation) {
        using var rig = new Rig();
        using var reflector = SdfTestPipelines.Reflector();

        Assert.True(condition: rig.Cache.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var nativePipelines));
        Assert.Equal(actual: nativePipelines, expected: 11L);
        var initial = rig.Pipelines.Kernels;
        var bytes = SpirvEdits.WithGenerator(generator: 931, module: initial[SdfKernel.Resolve].Span);
        var changed = initial.With(bytecode: bytes, kernel: SdfKernel.Resolve);

        if (activateBeforePreparation) {
            Activate();
        }
        using (var reload = rig.Pipelines.PrepareReload(cache: rig.Cache, device: rig.Gpu, kernels: changed, reflector: reflector)) {
            reload.Wait(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: 1, actual: reload.ChangedPipelines);
            // A graph build can request the optional pass after the reload has already prepared its replacements.
            if (!activateBeforePreparation) {
                Activate();
            }
            Assert.Equal(expected: 1, actual: rig.Engine.InstallReload(reload: reload));
        }
        Assert.Equal(expected: bytes, actual: rig.Pipelines.Kernels[SdfKernel.Resolve].ToArray());
        Activate();
        Assert.True(condition: rig.Cache.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var created));
        Assert.Equal(actual: created, expected: (nativePipelines + 2));
        Assert.Equal(expected: 12, actual: rig.Cache.SharedPipelines);
        // The replacement stays active on an unchanged request; neither an extra lease nor pipeline is added.
        Assert.Equal(expected: 0, actual: rig.Reload(kernels: changed));
        Activate();
        Assert.True(condition: rig.Cache.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var unchanged));
        Assert.Equal(actual: unchanged, expected: created);

        void Activate() => rig.Pipelines.BuildResolve(cache: rig.Cache, device: rig.Gpu, cancellationToken: TestContext.Current.CancellationToken);
    }
}
