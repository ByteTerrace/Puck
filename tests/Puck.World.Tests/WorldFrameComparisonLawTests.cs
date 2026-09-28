using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Assets;
using Puck.Commands;
using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldFrameComparisonLawTests {
    [Fact]
    public void OnlyActiveSeatsAppendOrdinarySourcesAndAnOrderedComparisonRoot() {
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: [new RenderGraphInstance(Name: "live", Refresh: RenderGraphRefresh.EveryFrame, Passes: 1, Reads: [])],
            set: out var initial, refusal: out _));
        var comparison = new WorldFrameComparison();
        var set = initial!;
        IReadOnlyList<RenderGraphRuntimeGraph?> graphs = [null];
        var originalGraphs = graphs;
        var root = "live";

        WorldComparisonGraph.Append(comparison: comparison, graphs: ref graphs, root: ref root, set: ref set);
        Assert.Same(actual: set, expected: initial);
        Assert.Same(actual: graphs, expected: originalGraphs);
        Assert.Equal(actual: root, expected: "live");
        comparison.Hold(slot: 1, frame: Frame(), view: View, tick: 1);
        comparison.Hold(slot: 3, frame: Frame(), view: View, tick: 1);
        comparison.SetMode(slot: 1, mode: WorldCompareMode.Wipe);
        comparison.SetMode(slot: 3, mode: WorldCompareMode.Split);
        WorldComparisonGraph.Append(comparison: comparison, graphs: ref graphs, root: ref root, set: ref set);
        Assert.Equal(expected: WorldComparisonGraph.Root, actual: root);
        Assert.Equal(expected: new[] { "live", WorldComparisonGraph.Source(slot: 1), WorldComparisonGraph.Source(slot: 3), root },
            actual: set.Instances.Select(selector: instance => instance.Name));
        Assert.Equal(expected: new[] { "live", WorldComparisonGraph.Source(slot: 1), WorldComparisonGraph.Source(slot: 3) },
            actual: set.Instances[^1].Reads.Select(selector: read => read.Producer));
        Assert.Equal(expected: new[] { WorldComparisonGraph.Source(slot: 1), WorldComparisonGraph.Source(slot: 3) },
            actual: graphs[^1]!.Pipeline.Plan.Passes.Select(selector: pass => pass.Name));
        Assert.All(collection: set.Instances.Skip(count: 1).Take(count: 2), action: instance => {
            Assert.Equal(expected: WorldFrameComparison.SourcePackage, actual: instance.ExternalPackage);
            var slot = WorldComparisonGraph.SeatOf(source: instance.Name);

            Assert.Equal(expected: comparison.Seat(slot: slot)!.Sequence, actual: instance.Settings!["hold"].GetUInt64());
        });
        Assert.Equal(expected: 3, actual: WorldComparisonGraph.Footprints(comparison: comparison, liveRoot: "live").Count());
        comparison.SetMode(slot: 1, mode: WorldCompareMode.Off);
        comparison.SetMode(slot: 3, mode: WorldCompareMode.Off);
        set = initial!;
        graphs = originalGraphs;
        root = "live";
        WorldComparisonGraph.Append(comparison: comparison, graphs: ref graphs, root: ref root, set: ref set);
        Assert.Same(actual: set, expected: initial);
        Assert.Empty(collection: WorldComparisonGraph.Footprints(comparison: comparison, liveRoot: "live"));
    }

    private static PngImage Frame() => new(RgbaPixels: Enumerable.Range(count: (7 * 3), start: 0)
        .SelectMany(selector: static pixel => new byte[] { ((byte)pixel), 40, 80, 255 }).ToArray(), Width: 7, Height: 3);

    private static WorldSeatView View => new(Present: true, Region: new NormalizedRect(Height: (2f / 3f), Width: 0.6f, X: 0.2f, Y: (1f / 3f)),
        Camera: default, Width: 7, Height: 3);

    [Fact]
    public void AHeldSeatUsesPlacesPixelEdgesAndRetainsItsOwnPixels() {
        var comparison = new WorldFrameComparison();
        var frame = Frame();

        comparison.Hold(slot: 1, frame: frame, view: View, tick: 17);
        var held = Assert.IsType<WorldCompareSnapshot>(@object: comparison.Seat(slot: 1));

        Assert.Equal(expected: (5, 2, 17UL), actual: (held.Image.Width, held.Image.Height, held.Tick));
        Assert.Equal(expected: new byte[] { 8, 9, 10, 11, 12, 15, 16, 17, 18, 19 },
            actual: Enumerable.Range(count: 10, start: 0).Select(selector: index => held.Image.RgbaPixels[(index * 4)]));
        Array.Clear(array: frame.RgbaPixels);
        Assert.Equal(expected: 8, actual: held.Image.RgbaPixels[0]);
        Assert.Null(@object: comparison.Seat(slot: 0));
        using var upload = WorldFrameComparison.Open(snapshot: held);

        Assert.Equal(expected: ImageSourceCadence.Static, actual: upload.Descriptor!.Cadence);
        Assert.Equal(expected: ImageContentClass.Presentation, actual: upload.Descriptor.Content);
        Assert.Equal(expected: ImageSourceConversion.RgbaPass,
            actual: ImageSourceConversion.PassOf(format: upload.Descriptor.Format, color: upload.Descriptor.Color));
    }
    [Fact]
    public void ComparisonModesChangeGraphMembershipOnlyWhenCrossingOff() {
        var comparison = new WorldFrameComparison();

        Assert.Throws<InvalidOperationException>(testCode: () => comparison.SetMode(slot: 0, mode: WorldCompareMode.Split));
        comparison.Hold(slot: 0, frame: Frame(), view: View, tick: 1);
        Assert.False(condition: comparison.Active);
        var revision = comparison.Revision;

        comparison.SetMode(mode: WorldCompareMode.Wipe, slot: 0, wipe: 0.3f);
        Assert.True(condition: comparison.Active);
        Assert.Equal(expected: (revision + 1), actual: comparison.Revision);
        comparison.SetMode(slot: 0, mode: WorldCompareMode.Diff);
        Assert.Equal(expected: (revision + 1), actual: comparison.Revision);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => comparison.SetMode(mode: WorldCompareMode.Wipe, slot: 0, wipe: float.NaN));
        Assert.Equal(expected: 0.3f, actual: comparison.Seat(slot: 0)!.Wipe);
        comparison.SetMode(slot: 0, mode: WorldCompareMode.Off);
        Assert.False(condition: comparison.Active);
        Assert.Equal(expected: (revision + 2), actual: comparison.Revision);
        Assert.NotNull(@object: comparison.Seat(slot: 0));
    }
    [Fact]
    public void ChangedPixelsCountOnlyTheSeatAndRefuseADifferentViewportExtent() {
        var comparison = new WorldFrameComparison();

        comparison.Hold(slot: 0, frame: Frame(), view: View, tick: 1);
        var after = Frame();

        after.RgbaPixels[0] += 9;
        after.RgbaPixels[(8 * 4)] += 1;
        after.RgbaPixels[(9 * 4)] += 2;
        after.RgbaPixels[((10 * 4) + 3)] = 0;
        Assert.Equal(expected: new RgbaFrameDifference(ChangedPixels: 1, MaxDelta: 2),
            actual: comparison.Measure(slot: 0, frame: after, view: View));
        Assert.Throws<InvalidOperationException>(testCode: () => comparison.Measure(slot: 0, frame: after,
            view: View with { Region = new NormalizedRect(Height: 1, Width: 1, X: 0, Y: 0) }));
        Assert.Throws<InvalidOperationException>(testCode: () => comparison.Hold(slot: 0, frame: after,
            view: View with { Present = false }, tick: 2));
        Assert.Equal(expected: 1UL, actual: comparison.Seat(slot: 0)!.Tick);
    }
    [Fact]
    public void AnAuthoredRootExtentUsesTheSeatsNormalizedViewport() {
        var cropped = WorldFrameComparison.Crop(frame: Frame(), view: View with { Width = 700, Height = 300 });

        Assert.Equal(expected: (5, 2), actual: (cropped.Width, cropped.Height));
        Assert.Equal(expected: 8, actual: cropped.RgbaPixels[0]);
    }

    private sealed class WipeBinding : IInputBindings, IPrincipalResolver {
        private static readonly CommandBinding[] Bindings = [new(Command: "world.compare")];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => Bindings;
        public Principal PrincipalOf(int slot) => Principal.Console;
    }

    [Fact]
    public void ABoundWipeUsesTheSharedAxisContractWithoutCapturingOrRebuilding() {
        var comparison = new WorldFrameComparison();

        comparison.Hold(slot: 0, frame: Frame(), view: View, tick: 1);
        using var capture = new WorldCompareCapture(comparison: comparison);
        var registry = new CommandRegistry(modules: [new WorldCompareCommandModule(capture: capture, comparison: comparison)]);
        var binding = new WipeBinding();
        var router = new InputRouter(registry: registry, bindings: binding, principalResolver: binding);

        Assert.True(condition: registry.TryGetMetadata(metadata: out var metadata, name: "world.compare"));
        Assert.Equal(expected: CommandValueKind.Axis1D, actual: metadata.ValueKind);
        void Wipe(float position, ulong tick) {
            router.Capture(signal: new InputSignal(Source: "gamepad.rightTrigger", DeviceId: default,
                Value: CommandValue.Axis(value: position), Phase: CommandPhase.Active));
            registry.ApplySnapshot(snapshot: router.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue));
        }
        Wipe(position: 0.75f, tick: 1);
        Assert.Equal(expected: WorldCompareMode.Wipe, actual: comparison.Seat(slot: 0)!.Mode);
        Assert.Equal(expected: 0.75f, actual: comparison.Seat(slot: 0)!.Wipe);
        var revision = comparison.Revision;

        Wipe(position: 0.25f, tick: 2);
        Assert.Equal(expected: 0.25f, actual: comparison.Seat(slot: 0)!.Wipe);
        Assert.Equal(expected: revision, actual: comparison.Revision);
        Assert.False(condition: capture.IsPending(slot: 0));
        Assert.Contains(expectedSubstring: "wipe=0.25", actualString: registry.Submit(line: "world.compare").Output);
    }

    private sealed class Target : ICaptureRequestTarget {
        public string? PendingCapturePath => Request?.Path;
        public FrameCaptureRequest? Request { get; private set; }

        public void RequestCapture(FrameCaptureRequest request) => Request = request;
    }

    [Fact]
    public void ARefusedComparisonCommandLeavesTheModeAndWipeUnchanged() {
        var comparison = new WorldFrameComparison();

        comparison.Hold(slot: 0, frame: Frame(), view: View, tick: 1);
        var viewports = new WorldSeatViewports();

        viewports.Publish(slot: 0, region: View.Region, camera: default, width: 7, height: 3);
        using var capture = new WorldCompareCapture(comparison: comparison, viewports: viewports);
        var registry = new CommandRegistry(modules: [new WorldCompareCommandModule(capture: capture, comparison: comparison)]);

        Assert.True(condition: registry.Submit(line: "world.compare wipe 0.2").IsError);
        Assert.False(condition: comparison.Active);
        Assert.Equal(expected: 0.5f, actual: comparison.Seat(slot: 0)!.Wipe);
        capture.Attach(target: () => new Target());
        var accepted = registry.Submit(line: "world.compare wipe 0.2");

        Assert.False(condition: accepted.IsError);
        Assert.NotNull(@object: accepted.Settlement);
        Assert.True(condition: registry.Submit(line: "world.compare split").IsError);
        Assert.Equal(expected: WorldCompareMode.Wipe, actual: comparison.Seat(slot: 0)!.Mode);
        Assert.True(condition: registry.Submit(line: "world.compare wipe NaN").IsError);
        Assert.True(condition: registry.Submit(line: "world.compare hold extra").IsError);
        Assert.False(condition: registry.Submit(line: "world.compare off").IsError);
        Assert.False(condition: comparison.Active);
        Assert.Equal(expected: 0.2f, actual: comparison.Seat(slot: 0)!.Wipe);
    }
    [Fact]
    public void CompareCaptureSettlesAfterThePngClosesAndUsesItsPublishedViewport() {
        var comparison = new WorldFrameComparison();
        var viewports = new WorldSeatViewports();

        viewports.Publish(slot: 0, region: View.Region, camera: default, width: 7, height: 3);
        using var capture = new WorldCompareCapture(comparison: comparison, viewports: viewports);
        var target = new Target();
        var reports = new List<CommandResult>();

        capture.Attach(target: () => target);
        capture.RecordPreparedFrame();
        capture.Report = reports.Add;
        var result = capture.Request(hold: true, slot: 0);

        Assert.False(condition: result.Settlement!.IsSettled);
        capture.Poll();
        Assert.Null(@object: comparison.Seat(slot: 0));
        var frame = Frame();

        _ = target.Request!.Write(writer: path => PngEncoder.Write(path: path, rgba: frame.RgbaPixels, width: frame.Width, height: frame.Height), tick: 23);
        capture.Poll();
        Assert.True(condition: result.Settlement.IsSettled);
        Assert.Equal(expected: 23UL, actual: comparison.Seat(slot: 0)!.Tick);
        Assert.Equal(expected: 8, actual: comparison.Seat(slot: 0)!.Image.RgbaPixels[0]);
        Assert.False(condition: File.Exists(path: target.Request.Path));
        Assert.False(condition: Assert.Single(collection: reports).IsError);
        var pending = capture.Request(hold: false, slot: 0);

        capture.Dispose();
        Assert.True(condition: pending.Settlement!.IsSettled);
        Assert.True(condition: reports[^1].IsError);
    }
    [Fact]
    public void APausedComparisonCaptureUsesTheViewportOfItsLastRenderedFrame() {
        var comparison = new WorldFrameComparison();
        var viewports = new WorldSeatViewports();

        viewports.Publish(slot: 0, region: View.Region, camera: default, width: 7, height: 3);
        using var capture = new WorldCompareCapture(comparison: comparison, viewports: viewports);
        var target = new Target();
        var completed = 0UL;

        capture.Attach(completedFrames: () => completed, target: () => target);
        capture.RecordPreparedFrame();
        completed++;
        capture.Poll();
        viewports.Publish(slot: 0, region: new NormalizedRect(Height: 1, Width: 1, X: 0, Y: 0), camera: default, width: 7, height: 3);
        capture.RecordPreparedFrame();
        var result = capture.Request(hold: true, slot: 0);
        var frame = Frame();

        _ = target.Request!.Write(writer: path => PngEncoder.Write(path: path, rgba: frame.RgbaPixels, width: frame.Width, height: frame.Height), tick: 1);
        capture.Poll();
        Assert.True(condition: result.Settlement!.IsSettled);
        Assert.Equal(expected: (5, 2), actual: (comparison.Seat(slot: 0)!.Image.Width, comparison.Seat(slot: 0)!.Image.Height));
    }
}
