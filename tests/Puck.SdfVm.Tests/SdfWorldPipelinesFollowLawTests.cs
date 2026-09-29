using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Ready optional pipelines follow with their exact cache/device/bytecode ownership, without a driver build.</summary>
public sealed class SdfWorldPipelinesFollowLawTests {
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void OptionalActivationAndReloadRefuseAChangedCacheOrDevice(bool reload, bool changeDevice) {
        var cache = new GpuPassPipelineCache();
        var device = new FakeGpuDevice();
        using var set = SdfTestPipelines.Build(cache: cache, device: device, kernels: SdfTestPipelines.Kernels());
        using var reflector = SdfTestPipelines.Reflector();
        var otherCache = (changeDevice ? cache : new GpuPassPipelineCache());
        var otherDevice = (changeDevice ? new FakeGpuDevice() : device);
        var before = cache.SharedPipelines;

        var refusal = Assert.Throws<ArgumentException>(testCode: () => {
            if (reload) {
                using var candidate = set.PrepareReload(cache: otherCache, device: otherDevice, kernels: set.Kernels, reflector: reflector);
            } else {
                set.BuildOptional(cache: otherCache, device: otherDevice, kernel: SdfKernel.TemporalResolve,
                    reflector: reflector, cancellationToken: TestContext.Current.CancellationToken);
            }
        });

        Assert.Equal(expected: (changeDevice ? "device" : "cache"), actual: refusal.ParamName);
        Assert.Equal(expected: before, actual: cache.SharedPipelines);
        if (!changeDevice) { Assert.Equal(expected: 0, actual: otherCache.SharedPipelines); }
    }
    [InlineData(SdfKernel.Resolve)]
    [InlineData(SdfKernel.TemporalResolve)]
    [InlineData(SdfKernel.TemporalViews)]
    [InlineData(SdfKernel.TemporalViewsCore)]
    [InlineData(SdfKernel.TemporalViewsFolds)]
    [Theory]
    public void AJoinedOptionalLeaseOutlivesItsSourceWithoutAnyNewBuild(SdfKernel kernel) {
        var cache = new GpuPassPipelineCache();
        var device = new FakeGpuDevice();
        var kernels = SdfTestPipelines.Kernels();
        using var source = SdfTestPipelines.Build(cache: cache, device: device, kernels: kernels);
        using var target = SdfTestPipelines.Build(cache: cache, device: device, kernels: kernels);
        using var reflector = SdfTestPipelines.Reflector();

        source.BuildOptional(cache: cache, device: device, kernel: kernel, reflector: reflector,
            cancellationToken: TestContext.Current.CancellationToken);
        var expected = source.Describe();
        var shared = cache.SharedPipelines;

        Assert.True(condition: cache.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var created));
        target.JoinReadyOptional(source: source);
        Assert.Equal(expected: expected, actual: target.Describe());
        source.Dispose();
        Assert.Equal(expected: shared, actual: cache.SharedPipelines);
        target.BuildOptional(cache: cache, device: device, kernel: kernel, reflector: reflector,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: cache.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var after));
        Assert.Equal(actual: after, expected: created);
        target.Dispose();
        Assert.Equal(expected: 0, actual: cache.SharedPipelines);
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void AReadyOptionalSlotCannotCrossBytecodeCacheOrDeviceIdentity(int difference) {
        var cache = new GpuPassPipelineCache();
        var device = new FakeGpuDevice();
        var kernels = SdfTestPipelines.Kernels();
        using var source = SdfTestPipelines.Build(cache: cache, device: device, kernels: kernels);
        using var reflector = SdfTestPipelines.Reflector();

        source.BuildOptional(cache: cache, device: device, kernel: SdfKernel.TemporalResolve, reflector: reflector,
            cancellationToken: TestContext.Current.CancellationToken);
        var targetCache = ((difference == 1) ? new GpuPassPipelineCache() : cache);
        var targetDevice = ((difference == 2) ? new FakeGpuDevice() : device);
        var targetKernels = ((difference == 0) ? kernels.With(kernel: SdfKernel.TemporalResolve,
            bytecode: SpirvEdits.WithGenerator(generator: 713, module: kernels[SdfKernel.TemporalResolve].Span)) : kernels);
        using var target = SdfTestPipelines.Build(cache: targetCache, device: targetDevice, kernels: targetKernels);
        var before = target.Describe();
        var shared = targetCache.SharedPipelines;

        target.JoinReadyOptional(source: source);
        Assert.Equal(expected: before, actual: target.Describe());
        Assert.Equal(expected: shared, actual: targetCache.SharedPipelines);
        target.Dispose();
        source.Dispose();
        Assert.Equal(expected: 0, actual: targetCache.SharedPipelines);
        Assert.Equal(expected: 0, actual: cache.SharedPipelines);
    }
}
