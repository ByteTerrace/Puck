using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldTemporalCaptureLawTests {
    [Fact]
    public void ConvergingPresentationHoldsEveryFrameValueAcrossDifferentHostIntervals() {
        using var state = new TemporaryDirectory(prefix: "puck-temporal-capture-");
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: state,
            world: "tests/Puck.Counters/counters.world.json"
        ).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();

        _ = presenter.CaptureFrame(deltaSeconds: 0.2f, height: 64, interpolationAlpha: 1f, width: 64);
        var request = new FrameCaptureRequest(path: state.PathOf(name: "temporal-frozen.png"), converge: 8);

        presenter.BeginConvergence(request: request);
        var first = presenter.CaptureFrame(deltaSeconds: 0.7f, height: 64, interpolationAlpha: 1f, width: 64);
        var block = Block(frame: first);
        var views = first.Views.ToArray();
        var transforms = first.DynamicTransforms.ToArray();
        var meshes = first.MeshDraws.ToArray();
        var volumes = first.Volumes.ToArray();

        foreach (var delta in ((float[])[0f, 0.003f, 0.9f, 0.001f, 0.3f, 0.03f, 1f])) {
            var next = presenter.CaptureFrame(deltaSeconds: delta, height: 64, interpolationAlpha: 0.37f, width: 64);

            Assert.Equal(expected: block, actual: Block(frame: next));
            Assert.Equal(expected: first.Time, actual: next.Time);
            Assert.Equal(expected: first.Clock, actual: next.Clock);
            Assert.Equal(expected: views, actual: next.Views);
            Assert.Equal(expected: transforms, actual: next.DynamicTransforms);
            Assert.Equal(expected: meshes, actual: next.MeshDraws);
            Assert.Equal(expected: volumes, actual: next.Volumes);
        }
        Assert.True(condition: request.TryFail(error: new OperationCanceledException()));
        var resumed = presenter.CaptureFrame(deltaSeconds: 0.5f, height: 64, interpolationAlpha: 1f, width: 64);

        Assert.True(condition: (resumed.Time > first.Time));
    }

    private static byte[] Block(SdfFrame frame) {
        var environment = new float[Puck.SignedDistance.SdfEnvironment.LaneCount];

        SdfFrameBlock.BakeEnvironment(frame: frame, rows: environment);
        var block = new byte[SdfFrameBlock.SizeBytes];

        SdfFrameBlock.Write(
            block: block,
            tables: new SdfPassValues(ScreenCount: 0, InstanceMaskWordCount: 1, MeshDraws: ((uint)frame.MeshDraws.Count), DebugMode: 0, Environment: environment),
            frame: frame, view: 0, width: 64, height: 64
        );
        return block;
    }
}
