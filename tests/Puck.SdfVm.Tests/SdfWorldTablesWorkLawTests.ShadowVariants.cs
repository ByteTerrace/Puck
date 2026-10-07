using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesWorkLawTests {
    // Sky projection and image reduction add three base pipelines; the four F=1 variants remain demand-only.
    [Fact]
    public async Task ReloadedInactiveFadeVariantsStayUncreatedUntilDemand() {
        using var rig = new Rig();
        using var reflector = SdfTestPipelines.Reflector();
        var initial = rig.Pipelines.Kernels;
        var changed = initial.With(kernel: SdfKernel.ViewsFade1,
            bytecode: SpirvEdits.WithGenerator(generator: 932, module: initial[SdfKernel.ViewsFade1].Span));
        using var reload = rig.Pipelines.PrepareReload(cache: rig.Cache, device: rig.Gpu, kernels: changed, reflector: reflector);

        await reload.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected: 0, actual: reload.ChangedPipelines);
        Assert.Equal(expected: 0, actual: rig.Engine.InstallReload(reload: reload));
        Assert.Equal(expected: 16, actual: rig.Cache.SharedPipelines);
        rig.Pipelines.RequestShadowFadeVariants(cache: rig.Cache, device: rig.Gpu, variants: SdfShadowFadeVariants.One);
        await rig.Pipelines.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: rig.Pipelines.IsBuilt(kernel: SdfKernel.ViewsFade1));
        Assert.Equal(expected: 20, actual: rig.Cache.SharedPipelines);
        Assert.Equal(expected: changed[SdfKernel.ViewsFade1].ToArray(), actual: rig.Pipelines.Kernels[SdfKernel.ViewsFade1].ToArray());
    }
    [Fact]
    public async Task FirstDemandDuringReloadRefusesStaleReplacementThenRetriesOnlyActiveVariants() {
        using var rig = new Rig();
        using var reflector = SdfTestPipelines.Reflector();
        var initial = rig.Pipelines.Kernels;
        var changed = initial.With(kernel: SdfKernel.ViewsFade1,
            bytecode: SpirvEdits.WithGenerator(generator: 933, module: initial[SdfKernel.ViewsFade1].Span));

        using (var reload = rig.Pipelines.PrepareReload(cache: rig.Cache, device: rig.Gpu, kernels: changed, reflector: reflector)) {
            await reload.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
            rig.Pipelines.RequestShadowFadeVariants(cache: rig.Cache, device: rig.Gpu, variants: SdfShadowFadeVariants.One);
            await rig.Pipelines.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
            var refusal = Assert.Throws<InvalidOperationException>(testCode: () => rig.Engine.InstallReload(reload: reload));

            Assert.Contains(expectedSubstring: "before a changed shadow fade variant was requested", actualString: refusal.Message);
            Assert.Same(expected: initial, actual: rig.Pipelines.Kernels);
        }
        Assert.Equal(expected: 1, actual: rig.Reload(kernels: changed));
        Assert.Equal(expected: 20, actual: rig.Cache.SharedPipelines);
    }
}
