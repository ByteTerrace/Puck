using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Deterministic-tick laws for <see cref="ShaderPipelineRenderNode"/>: a graph requesting a tick rate reads the engine
/// tick divided by the engine rate over that rate, so one delivered tick writes identical tick bytes at every
/// presentation clock, and a rate that does not divide the engine rate is refused by name before anything is planned;
/// and a capture records the tick of the state its image was rendered from, however many paused frames republish it.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    private const uint RequestedTickRate = 30U;

    // The one-pass board graph at a requested tick rate.
    private static CompiledShaderPipeline Ticking(uint? tickRate) {
        var board = Board();
        var plan = new ShaderPipelineCompiler().Compile(definition: (board.Plan.Definition with { TickRate = tickRate }));

        return new CompiledShaderPipeline(
            plan: plan,
            shaders: board.Shaders
        );
    }

    /// <summary>One delivered engine tick writes identical tick bytes at three presentation clocks: frames presenting
    /// it at 30, 60 and 144 frames a second, each with its own time, delta and frame count, write the same tick words,
    /// the engine tick divided by the engine rate over the requested 30 a second, and that rate; their time words
    /// differ, since presentation time is theirs alone. Every engine tick inside one requested tick's period writes the
    /// same tick.</summary>
    [Fact]
    public void OneTickWritesIdenticalBytesAtThreePresentationClocks() {
        var period = EngineTicks.PerRate(ratePerSecond: RequestedTickRate);
        var engineTick = ((period * 7UL) + (period / 2UL));
        var gpu = new FakePipelineGpu { MemoryProfile = Aperture };
        using var node = Node(gpu: gpu);

        node.Swap(pipeline: Ticking(tickRate: RequestedTickRate));
        _ = node.ProduceUntilInstalled();

        var layout = node.Plan!.Passes[0].Parameters;
        var frameGroup = layout.Layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Frame));
        var tickOffset = ((int)frameGroup.BlockMembers.Single(predicate: static member => (member.Name == ShaderFrameInterface.Tick)).Offset);
        var rateOffset = ((int)frameGroup.BlockMembers.Single(predicate: static member => (member.Name == ShaderFrameInterface.TickRate)).Offset);
        var timeOffset = ((int)frameGroup.BlockMembers.Single(predicate: static member => (member.Name == ShaderFrameInterface.Time)).Offset);
        var ticks = new List<byte[]>();
        var times = new List<float>();

        gpu.Recording = true;

        foreach (var displayRate in ((double[])[30d, 60d, 144d])) {
            // Each clock presents the tick at a fraction of its own frame interval past the tick's time.
            node.Frame = new ShaderFrameValues(
                CameraFov: 0f,
                CameraPosition: Vector3.Zero,
                CameraTarget: Vector3.Zero,
                CameraUp: Vector3.Zero,
                Pointer: Vector2.Zero,
                PointerDown: false,
                PointerPresses: 0,
                Tick: engineTick,
                Time: (EngineTicks.ToSeconds(ticks: engineTick) + (0.5d / displayRate)),
                TimeDelta: (1d / displayRate)
            );

            for (var frame = 0; (frame < ((int)(displayRate / 30d))); frame++) {
                gpu.BoundSets.Clear();
                Produce(node: node);
            }

            var block = gpu.ConstantBlock(
                set: gpu.BoundSets.Last(predicate: static bound => (bound.Group == ((uint)ShaderInterfaceGroup.Frame))).Set,
                sizeBytes: ((int)layout.FrameBlockSizeBytes)
            );

            ticks.Add(item: block[tickOffset..(tickOffset + 8)]);
            times.Add(item: BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: timeOffset)));
            Assert.Equal(
                actual: BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: rateOffset)),
                expected: RequestedTickRate
            );
        }

        Assert.All(
            action: bytes => Assert.Equal(expected: ticks[0], actual: bytes),
            collection: ticks
        );
        Assert.Equal(
            actual: BinaryPrimitives.ReadUInt64LittleEndian(source: ticks[0]),
            expected: 7UL
        );
        Assert.Equal(expected: 3, actual: times.Distinct().Count());

        var written = new byte[layout.FrameBlockSizeBytes];

        foreach (var (presented, expected) in ((ReadOnlySpan<(ulong, ulong)>)[((period * 7UL), 7UL), (((period * 8UL) - 1UL), 7UL), ((period * 8UL), 8UL)])) {
            layout.WriteFrame(
                block: written,
                frame: 0UL,
                tickRate: RequestedTickRate,
                values: (node.Frame with { Tick = presented })
            );
            Assert.Equal(
                actual: BinaryPrimitives.ReadUInt64LittleEndian(source: written.AsSpan(start: tickOffset)),
                expected: expected
            );
        }
    }
    /// <summary>A capture records the tick of the state the image it reads was rendered from: a paused instance
    /// republishing an image it rendered at tick 5 serves a capture armed while the host presents tick 9 with tick 5, not
    /// the request's own source, so the tick verdict holds it to the armed tick 9 and fails; once the node renders
    /// again, its capture records the tick that frame rendered.</summary>
    [Fact]
    public void APausedInstancesCaptureRecordsTheTickItsImageWasRenderedAt() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        using var node = InstalledNode(gpu: gpu);
        var hostTick = 5UL;

        FrameCaptureResult CaptureAt() {
            var request = new FrameCaptureRequest(
                path: Path.Combine(
                    path1: Path.GetTempPath(),
                    path2: $"{Guid.NewGuid():N}.png"
                ),
                tick: () => hostTick
            );

            node.RequestCapture(request: request);
            _ = Produce(node: node);
            Assert.True(condition: request.Completion.IsCompleted);

            var result = request.Completion.Result;

            File.Delete(path: result.Path);

            return result;
        }

        node.Frame = (node.Frame with { StateTick = hostTick });
        _ = Produce(node: node);
        node.Paused = true;
        hostTick = 9UL;
        node.Frame = (node.Frame with { StateTick = hostTick });

        var paused = CaptureAt();

        Assert.Null(@object: paused.Error);
        Assert.Equal(expected: 5UL, actual: paused.Tick);
        Assert.NotEqual(expected: hostTick, actual: paused.Tick);

        node.Paused = false;

        Assert.Equal(expected: 9UL, actual: CaptureAt().Tick);
    }
    /// <summary>A graph requesting a tick rate that does not divide the engine rate exactly, or none a second, is refused
    /// by name, naming the graph and the rate; a dividing rate plans, and a graph requesting none reads the engine
    /// rate.</summary>
    [Fact]
    public void ATickRateThatDoesNotDivideTheEngineRateIsRefusedByName() {
        foreach (var rate in ((uint[])[11U, 0U, 50401U])) {
            var diagnostic = Assert.Single(collection: Assert.Throws<ShaderPipelineCompilationException>(testCode: () => Ticking(tickRate: rate)).Diagnostics);

            Assert.Equal(expected: "SHADERPIPE_TICK_RATE", actual: diagnostic.Code);
            Assert.Equal(
                actual: diagnostic.Message,
                expected: $"Graph 'board' requests a tick rate of {rate} a second, which does not divide the engine's {EngineTicks.PerSecond} ticks a second exactly."
            );
        }

        Assert.Equal(expected: RequestedTickRate, actual: Ticking(tickRate: RequestedTickRate).Plan.TickRate);
        Assert.Equal(expected: ShaderFrameInterface.EngineTickRate, actual: Ticking(tickRate: null).Plan.TickRate);
    }
}
