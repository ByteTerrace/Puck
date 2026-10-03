using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldTemporalCaptureLawTests {
    [Fact]
    public void AWindowedCapturePinsItsClockAndBodyPoseAndReleasesTheFractionAfterServing() {
        using var state = new TemporaryDirectory(prefix: "puck-window-capture-");
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Windowed,
            stateDirectory: state,
            world: "tests/Puck.Counters/counters.world.json"
        ).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var client = host.Services.GetRequiredService<WorldClient>();
        var position = new Vector3(x: 12f, y: 0f, z: 0f);

        client.DeliverSnapshot(snapshot: Snapshot(position: Vector3.Zero, tick: 0));
        client.DeliverSnapshot(snapshot: Snapshot(position: position, tick: 1));
        client.StateMirror.Install(engineTick: 0, tick: 0);
        client.StateMirror.Refresh(stamp: new WorldStateStamp(EngineTick: 1680, Everything: false, MovedRows: Array.Empty<int>(), Tick: 1));
        var pending = false;

        presenter.CapturePending = () => pending;

        foreach (var owesCapture in new[] { false, true, false }) {
            pending = owesCapture;
            var frame = presenter.CaptureFrame(deltaSeconds: 0f, height: 64, interpolationAlpha: 0.25f, width: 64);

            Assert.Equal(expected: new PresentedTick(Fraction: 0d, Whole: (owesCapture ? 1680UL : 420UL)), actual: frame.Clock);
            Assert.Equal(expected: (position * (owesCapture ? 1f : 0.25f)), actual: client.Position(index: 0));
        }
    }

    private static WorldSnapshot Snapshot(Vector3 position, ulong tick) => new(
        EngineTick: (tick * 1680UL),
        Entries: new[] { new EntitySnapshot(
            Active: true, BodyColor: Vector3.One, CatalogRig: 0, Continuity: EntityContinuity.Continuous,
            Generation: 1, Index: 0, Kit: 0, Look: 0, Orientation: Quaternion.Identity, Position: position) },
        Revision: 0,
        StepTicks: 1680,
        Tick: tick
    );

    [Fact]
    public void ConvergingPresentationHoldsEveryFrameValueAcrossDifferentHostIntervals() {
        using var state = new TemporaryDirectory(prefix: "puck-temporal-capture-");
        var host = state.Own(owner: WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: state,
            world: "tests/Puck.Counters/counters.world.json"
        ).Build());
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

    // The pass block a frame writes, followed by the lights and sky records its tables pack.
    private static byte[] Block(SdfFrame frame) {
        var block = new byte[SdfFrameBlock.SizeBytes];
        var lights = new Puck.SignedDistance.SdfLight[Puck.SignedDistance.SdfLights.MaxLights];
        var layers = new Puck.SignedDistance.SdfSkyLayer[Puck.SignedDistance.SdfSky.MaxLayers];
        var softboxes = new Puck.SignedDistance.SdfSoftbox[Puck.SignedDistance.SdfSky.MaxSoftboxes];

        SdfFrameBlock.Write(
            block: block,
            tables: new SdfPassValues(ScreenCount: 0, InstanceMaskWordCount: 1, MeshDraws: ((uint)frame.MeshDraws.Count), DebugMode: 0),
            frame: frame, view: 0, width: 64, height: 64
        );
        frame.Lights.Pack(records: lights);
        frame.Sky.Pack(block: out var sky, details: new Puck.SignedDistance.SdfSkyDetails(), layers: layers, lights: frame.Lights, softboxes: softboxes);

        return [
            .. block,
            .. System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: lights.AsSpan()),
            .. System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: new ReadOnlySpan<Puck.SignedDistance.SdfSkyBlock>(reference: in sky)),
            .. System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: layers.AsSpan()),
            .. System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: softboxes.AsSpan()),
        ];
    }
}
