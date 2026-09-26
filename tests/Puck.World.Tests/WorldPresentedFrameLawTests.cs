using System.Buffers.Binary;

using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the frame group's presentation time (<see cref="WorldViewGraphHost.PresentedFrame"/>): a pipeline pass's
/// <c>tick</c> is the state mirror's delivered engine tick, written low word then high word by the one frame block
/// writer, and its <c>time</c> is the mirror's presented engine tick at the frame's interpolation fraction, in seconds,
/// so a frame at a given delivered tick and fraction writes the same bytes on every run and an offscreen frame, which
/// pins the fraction to one, presents exactly the delivered tick.
/// </summary>
public sealed class WorldPresentedFrameLawTests {
    // Engine ticks past 2^32, so the high word of the tick is pinned too.
    private const ulong Delivered = 0x0000_0002_0000_0690UL;
    private const ulong Previous = (Delivered - 1680UL);

    private static WorldStateMirror Mirror() {
        var definition = Fixtures.BuildDocument();
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        mirror.Install(
            engineTick: Previous,
            tick: 7UL
        );
        mirror.Refresh(stamp: new WorldStateStamp(
            EngineTick: Delivered,
            Everything: false,
            MovedRows: ReadOnlyMemory<int>.Empty,
            Tick: 8UL
        ));

        return mirror;
    }
    private static byte[] FrameBlock(in ShaderFrameValues values, out ShaderPipelineParameterLayout layout) {
        layout = ShaderPipelineParameterLayout.ForPackage(
            config: null,
            members: [],
            package: "presented"
        );

        var block = new byte[layout.FrameBlockSizeBytes];

        layout.WriteFrame(
            block: block,
            frame: 0UL,
            values: in values
        );

        return block;
    }
    private static uint Offset(ShaderPipelineParameterLayout layout, string member) => layout.Layout.Bindings
        .Single(predicate: static binding => ((binding.Set == 0) && (binding.Members.Count != 0)))
        .Members.Single(predicate: value => string.Equals(
            a: value.Name,
            b: member,
            comparisonType: StringComparison.Ordinal
        )).Offset;

    [Fact]
    public void TheFrameGroupTickIsTheMirrorsDeliveredEngineTick_LowWordThenHighWord() {
        var mirror = Mirror();

        foreach (var fraction in ((float[])[0f, 0.25f, 1f])) {
            var values = WorldViewGraphHost.PresentedFrame(
                fraction: fraction,
                mirror: mirror,
                previousSeconds: 0d
            );
            var block = FrameBlock(
                layout: out var layout,
                values: in values
            );
            var tick = ((int)Offset(
                layout: layout,
                member: ShaderFrameInterface.Tick
            ));

            Assert.Equal(
                actual: values.Tick,
                expected: Delivered
            );
            Assert.Equal(
                actual: BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: tick)),
                expected: 0x0000_0690u
            );
            Assert.Equal(
                actual: BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: (tick + 4))),
                expected: 0x0000_0002u
            );
        }
    }
    [Fact]
    public void TheFrameGroupTimeIsTheMirrorsPresentedEngineTickInSeconds() {
        var mirror = Mirror();
        var previousSeconds = (((double)Previous) / EngineTicks.PerSecond);

        foreach (var (fraction, expected) in (((float Fraction, double Ticks)[])[
            (0f, Previous),
            (0.5f, (Previous + 840UL)),
            (1f, Delivered),
            (2f, Delivered),
        ])) {
            var values = WorldViewGraphHost.PresentedFrame(
                fraction: fraction,
                mirror: mirror,
                previousSeconds: previousSeconds
            );
            var block = FrameBlock(
                layout: out var layout,
                values: in values
            );

            Assert.Equal(
                actual: values.Time,
                expected: (expected / EngineTicks.PerSecond)
            );
            Assert.Equal(
                actual: values.TimeDelta,
                expected: (values.Time - previousSeconds)
            );
            Assert.Equal(
                actual: BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: ((int)Offset(
                    layout: layout,
                    member: ShaderFrameInterface.Time
                )))),
                expected: BitConverter.SingleToUInt32Bits(value: ((float)values.Time))
            );
        }

        // A frame never presents a negative delta, even behind the frame before it.
        Assert.Equal(
            actual: WorldViewGraphHost.PresentedFrame(
                fraction: 0f,
                mirror: mirror,
                previousSeconds: (((double)Delivered) / EngineTicks.PerSecond)
            ).TimeDelta,
            expected: 0d
        );
    }
    [Fact]
    public void AFrameAtOneDeliveredTickAndFractionWritesTheSameBytesOnEveryRun() {
        var first = WorldViewGraphHost.PresentedFrame(
            fraction: 0.375f,
            mirror: Mirror(),
            previousSeconds: 1d
        );
        var second = WorldViewGraphHost.PresentedFrame(
            fraction: 0.375f,
            mirror: Mirror(),
            previousSeconds: 1d
        );

        Assert.Equal(
            actual: FrameBlock(
                layout: out _,
                values: in second
            ),
            expected: FrameBlock(
                layout: out _,
                values: in first
            )
        );
    }
    [Fact]
    public void AnInstallPresentsItsOwnEngineTickAtEveryFraction_AndARestoredTickStartsFromItself() {
        var definition = Fixtures.BuildDocument();
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        mirror.Install(
            engineTick: Delivered,
            tick: 8UL
        );
        Assert.Equal(
            actual: mirror.PresentedEngineTick(fraction: 0f),
            expected: Delivered
        );

        mirror.Refresh(stamp: new WorldStateStamp(
            EngineTick: Previous,
            Everything: true,
            MovedRows: ReadOnlyMemory<int>.Empty,
            Tick: 7UL
        ));
        Assert.Equal(
            actual: (mirror.PreviousEngineTick, mirror.EngineTick),
            expected: (Previous, Previous)
        );
    }
}
