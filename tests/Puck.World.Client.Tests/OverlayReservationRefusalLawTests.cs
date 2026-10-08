using System.Runtime.CompilerServices;
using Puck.Hosting;
using Puck.Overlays;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>Exercises the composer's real reservation refusal, packed text and named episode narration together.</summary>
[Collection(ConsoleRedirectionCollection.Name)]
public sealed class OverlayReservationRefusalLawTests {
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern OverlayGlyphSdfPack CreateGlyphs(int atlasCellWidth, int atlasCellHeight, float distanceRange, uint[] packedSdf, int glyphCount);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_channelWriters")]
    private static extern ref Action<OverlayFrameBuilder>?[] Writers(OverlayFrameComposer composer);

    [Fact]
    public void OversizedWriterRunIsRefusedWholeAndComposerNamesItsOwnerOncePerEpisode() {
        var composer = new OverlayFrameComposer(
            sources: new UnifiedOverlaySources(Console: null, BindingBar: null, Toast: null, FeedTick: null),
            capacity: WorldOverlayCapacity.FromSchema(),
            glyphs: CreateGlyphs(atlasCellHeight: 1, atlasCellWidth: 1, distanceRange: 1, glyphCount: 1, packedSdf: [0]),
            frameSources: new EmptyFrames(), width: 800, height: 600, theme: OverlayThemeValues.Zero);
        var reserved = composer.Builder.ReservationOf(channel: OverlayChannel.Console).TextWords;
        var admitted = new string(c: 'a', count: (reserved - 2));
        var overflow = true;
        // Deliberately adversarial console writer: three chars do not fit the two remaining words, although the
        // shared backing has spare capacity. The following one-char run must still fit after whole-run refusal.
        Writers(composer: composer)[((int)OverlayChannel.Console)] = builder => {
            Write(builder: builder, text: admitted);
            if (overflow) { Write(builder: builder, text: "xyz"); }
            Write(builder: builder, text: "q");
        };
        var original = Console.Error;
        using var captured = new StringWriter();

        try {
            Console.SetError(newError: captured);
            Assert.True(condition: composer.Compose(renderTicks: 0));
            Assert.Equal(expected: (reserved - 1), actual: composer.Builder.TextWordCount);
            Assert.Equal(expected: 2, actual: composer.Builder.ElementCount);
            var packed = composer.Builder.Scratch.Slice(start: composer.Builder.TextBaseWords, length: composer.Builder.TextWordCount);

            Assert.DoesNotContain(expected: ((uint)OverlayGlyphSdfPack.GlyphIndex(codePoint: 'x')), collection: packed.ToArray());
            Assert.Equal(expected: ((uint)OverlayGlyphSdfPack.GlyphIndex(codePoint: 'q')), actual: packed[^1]);
            var narration = captured.ToString();

            Assert.Contains(actualString: narration, expectedSubstring: "channel \"console\" exceeded its own reservation and refused whole records");
            Assert.Contains(actualString: narration, expectedSubstring: "3 text words dropped");
            composer.Compose(renderTicks: 1);
            Assert.Equal(expected: narration, actual: captured.ToString());
            overflow = false;
            composer.Compose(renderTicks: 2);
            overflow = true;
            composer.Compose(renderTicks: 3);
            Assert.Equal(expected: (narration + narration), actual: captured.ToString());
        } finally { Console.SetError(newError: original); }
    }

    private static void Write(OverlayFrameBuilder builder, string text) => builder.WriteText(
        x: 0, y: 0, text: text, cellHeight: 12, role: OverlayColorRole.TextPrimary, alpha: 1);

    private sealed class EmptyFrames : IOverlayFrameSources {
        public bool TryAcquire(int key, out GpuImageLease lease) { lease = default; return false; }
    }
}
