using System.Runtime.CompilerServices;
using Puck.Overlays;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A toast is chrome: a world with no theme composes none of it, and a themed one draws its chip.</summary>
public sealed class OverlayToastThemeLawTests {
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern OverlayGlyphSdfPack CreateGlyphs(int atlasCellWidth, int atlasCellHeight, float distanceRange, uint[] packedSdf, int glyphCount);

    private static OverlayFrameBuilder Compose(OverlayThemeValues theme) {
        var toasts = new OverlayToastStore();
        var composer = new OverlayFrameComposer(
            sources: new UnifiedOverlaySources(Console: null, BindingBar: null, Toast: toasts, FeedTick: null),
            capacity: WorldOverlayCapacity.FromSchema(),
            glyphs: CreateGlyphs(atlasCellHeight: 1, atlasCellWidth: 1, distanceRange: 1, glyphCount: 1, packedSdf: [0]),
            frameSources: new EmptyFrames(), width: 1280, height: 900, theme: theme);

        toasts.Publish(isError: false, message: "[world.debug-view: ao]");
        _ = composer.Compose(renderTicks: 0);

        return composer.Builder;
    }

    [Fact]
    public void UnthemedWorldDrawsNoToast() {
        var builder = Compose(theme: OverlayThemeValues.Zero);

        Assert.Equal(expected: 0, actual: builder.PanelCount);
        Assert.Equal(expected: 0, actual: builder.ElementCount);
        Assert.Equal(expected: 0, actual: builder.TextWordCount);
    }

    [Fact]
    public void ThemedWorldDrawsTheToastChip() {
        var zero = OverlayThemeValues.Zero;
        var builder = Compose(theme: (zero with {
            Space = (zero.Space with { HeightChip = 28f, HeightBadge = 16f, Space2 = 8f, Space3 = 12f, Space8 = 32f }),
            Type = (zero.Type with { MonoSize = 13f, MicroSize = 10f }),
        }));

        Assert.Equal(expected: 1, actual: builder.PanelCount);
        Assert.True(condition: (builder.ElementCount > 0));
        Assert.True(condition: (builder.TextWordCount > 0));
    }

    private sealed class EmptyFrames : IOverlayFrameSources {
        public bool TryAcquire(int key, out Puck.Hosting.GpuImageLease lease) { lease = default; return false; }
    }
}
