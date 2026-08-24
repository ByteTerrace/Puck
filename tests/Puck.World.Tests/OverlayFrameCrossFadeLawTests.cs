using System.Runtime.CompilerServices;
using Puck.Overlays;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the sampled-frame record's cross-fade packing and the writer's degrade-to-single rule when the
/// outgoing source cannot bind.</summary>
public sealed class OverlayFrameCrossFadeLawTests {
    private const int Height = 100;
    private const int Width = 200;

    [Fact]
    public void WriteFramePacksTheOutgoingSlotIntoWord4AndTheMixIntoWord8() {
        var builder = BuildBuilder();

        builder.BeginChannel(channel: OverlayChannel.Hud);
        builder.WriteFrame(
            alpha: 1f,
            fit: OverlayHudFrameFit.Contain,
            h: Height,
            mirror: true,
            mix: 0.25f,
            radius: 0f,
            slot: 3,
            slotB: 5,
            w: Width,
            x: 0f,
            y: 0f
        );

        var record = Record(builder: builder);

        Assert.Equal(expected: (4u | (3u << 4) | (1u << 12) | (1u << 13) | (6u << 16)), actual: record[4]);
        Assert.Equal(expected: BitConverter.SingleToUInt32Bits(value: 0.25f), actual: record[8]);
    }

    [Fact]
    public void WriteFrameWithoutAnOutgoingSlotLeavesBits16UpAndWord8Zero() {
        var builder = BuildBuilder();

        builder.BeginChannel(channel: OverlayChannel.Hud);
        builder.WriteFrame(
            alpha: 1f,
            fit: OverlayHudFrameFit.Cover,
            h: Height,
            mirror: false,
            mix: 0.25f,
            radius: 0f,
            slot: 7,
            slotB: -1,
            w: Width,
            x: 0f,
            y: 0f
        );

        var record = Record(builder: builder);

        Assert.Equal(expected: (4u | (7u << 4)), actual: record[4]);
        Assert.Equal(expected: 0u, actual: record[8]);
    }

    [Fact]
    public void EmitFrameCarriesBothSlotsAndTheMixWhenBothSourcesBind() {
        var builder = BuildBuilder();
        var writer = BuildWriter(
            element: FrameElement(sourceA: 11, sourceB: 12, mix: 0.5f),
            frameSlots: new OverlayFrameSlots(sources: new FixedFrameSources(refuseKey: -1))
        );

        writer.RefreshFrame();
        builder.BeginChannel(channel: OverlayChannel.Hud);
        writer.EmitOver(builder: builder);

        var record = Record(builder: builder);

        Assert.Equal(expected: 1, actual: builder.ElementCount);
        Assert.Equal(expected: (4u | (0u << 4) | (2u << 16)), actual: record[4]);
        Assert.Equal(expected: BitConverter.SingleToUInt32Bits(value: 0.5f), actual: record[8]);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(-1)]
    public void EmitFrameDegradesToTheWinnerAloneWhenTheOutgoingSourceCannotBind(int refuseKey) {
        var builder = BuildBuilder();
        var sources = new FixedFrameSources(refuseKey: refuseKey);
        var frameSlots = new OverlayFrameSlots(sources: sources);

        if (refuseKey < 0) {
            // Fill every slot but the one the winner takes so the outgoing bind trips the capacity refusal.
            for (var key = 100; (key < (100 + (OverlayFrameSlots.SlotCount - 1))); key++) {
                Assert.True(condition: (frameSlots.Bind(key: key) >= 0));
            }
        }

        var writer = BuildWriter(
            element: FrameElement(sourceA: 11, sourceB: 12, mix: 0.5f),
            frameSlots: frameSlots
        );

        writer.RefreshFrame();
        builder.BeginChannel(channel: OverlayChannel.Hud);
        writer.EmitOver(builder: builder);

        var record = Record(builder: builder);
        var winnerSlot = ((refuseKey < 0) ? (uint)(OverlayFrameSlots.SlotCount - 1) : 0u);

        Assert.Equal(expected: 1, actual: builder.ElementCount);
        Assert.Equal(expected: (4u | (winnerSlot << 4)), actual: record[4]);
        Assert.Equal(expected: 0u, actual: record[8]);
    }

    private static OverlayFrameBuilder BuildBuilder() => new(
        glyphs: CreateGlyphs(
            atlasCellWidth: 1,
            atlasCellHeight: 1,
            distanceRange: 1f,
            packedSdf: [0u],
            glyphCount: 1
        ),
        height: Height,
        leases: new OverlayChannelLeases(
            capacity: new OverlayCapacity(
                Seats: 0,
                HudPanels: 1,
                HudElementsPerPanel: 1,
                HudSeatPanelsPerSeat: 0,
                HudElementsPerSeatPanel: 0,
                BindingBarMaxBanks: 0,
                BindingBarMaxSlotsPerBank: 0,
                BindingBarMaxModifiers: 0,
                MarkerMaxChipsPerSeat: 0,
                WheelMaxRings: 0,
                WheelMaxSectorsPerRing: 0
            )
        ),
        theme: OverlayThemeValues.Zero,
        width: Width
    );

    private static HudWriter BuildWriter(OverlayHudElement element, OverlayFrameSlots frameSlots) => new(
        bindings: new NoBindings(),
        frameSlots: frameSlots,
        source: new FixedHudSource(
            frame: new OverlayHudFrame(
                Panels: new[] {
                    new OverlayHudPanel(
                        Id: "fade",
                        Rect: new OverlayHudRect(X: 0f, Y: 0f, Width: 1f, Height: 1f),
                        Band: OverlayHudBand.Over,
                        Style: OverlayPanelStyle.Panel,
                        Elements: new[] { element }
                    ),
                }
            )
        ),
        theme: new OverlayThemeStore()
    );

    private static OverlayHudElement FrameElement(int sourceA, int sourceB, float mix) => new(
        Kind: OverlayHudElementKind.Frame,
        Rect: new OverlayHudRect(X: 0f, Y: 0f, Width: 1f, Height: 1f),
        Role: default,
        Text: null,
        Binding: null,
        FrameSource: sourceA,
        FrameSourceB: sourceB,
        FrameMix: mix
    );

    private static ReadOnlySpan<uint> Record(OverlayFrameBuilder builder) => builder.Scratch.Slice(
        start: (builder.ElementBaseWords + ((builder.ElementCount - 1) * OverlayFrameBuilder.ElementWords)),
        length: OverlayFrameBuilder.ElementWords
    );

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern OverlayGlyphSdfPack CreateGlyphs(int atlasCellWidth, int atlasCellHeight, float distanceRange, uint[] packedSdf, int glyphCount);

    private sealed class FixedFrameSources(int refuseKey) : IOverlayFrameSources {
        public bool TryAcquire(int key, out OverlayFrameLease lease) {
            if (key == refuseKey) {
                lease = default;

                return false;
            }

            lease = new OverlayFrameLease(
                ImageViewHandle: key + 1,
                Release: static _ => { },
                ReleaseToken: key
            );

            return true;
        }
    }

    private sealed class FixedHudSource(OverlayHudFrame frame) : IHudSource {
        public bool TrySnapshot(out OverlayHudFrame frame) {
            frame = this.frame;

            return true;
        }

        private readonly OverlayHudFrame frame = frame;
    }

    private sealed class NoBindings : IHudBindingResolver {
        public bool TryResolve(string binding, out float fraction, out string text) {
            fraction = 0f;
            text = string.Empty;

            return false;
        }
    }
}
