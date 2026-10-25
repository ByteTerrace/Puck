using System.Runtime.CompilerServices;
using Puck.Overlays;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A toast is chrome its style sizes: a style with no height composes none of it, and the engine's default
/// theme, which a world that authors none draws, draws its chip.</summary>
public sealed class OverlayToastThemeLawTests {
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern OverlayGlyphSdfPack CreateGlyphs(int atlasCellWidth, int atlasCellHeight, float distanceRange, float advance, uint[] words, int glyphCount);
    private static OverlayFrameBuilder Compose(OverlayThemeValues theme) {
        var toasts = new OverlayToastStore();
        var composer = new OverlayFrameComposer(
            sources: new UnifiedOverlaySources(Console: null, BindingBar: null, Toast: toasts, FeedTick: null),
            capacity: WorldOverlayCapacity.FromSchema(),
            glyphs: CreateGlyphs(advance: 1f, atlasCellHeight: 1, atlasCellWidth: 1, distanceRange: 1, glyphCount: 1, words: [0u, 0u, 0x3F800000u]),
            frameSources: new EmptyFrames(), width: 1280, height: 900, theme: theme);

        toasts.Publish(isError: false, message: "[world.debug-view: ao]");
        _ = composer.Compose(renderTicks: 0);

        return composer.Builder;
    }

    [Fact]
    public void AToastStyleWithNoHeightDrawsNoToast() {
        var builder = Compose(theme: OverlayThemeValues.Zero);

        Assert.Equal(expected: 0, actual: builder.PanelCount);
        Assert.Equal(expected: 0, actual: builder.ElementCount);
        Assert.Equal(expected: 0, actual: builder.TextWordCount);
    }
    [Fact]
    public void TheDefaultThemeDrawsTheToastChip() {
        var definition = Fixtures.BuildDocument();
        var builder = Compose(theme: new WorldThemeResolve(domains: new WorldValueDomainGuard()).Resolve(
            definition: definition,
            mirror: new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition)),
            revision: 1
        ));

        Assert.Equal(expected: 1, actual: builder.PanelCount);
        Assert.True(condition: (builder.ElementCount > 0));
        Assert.True(condition: (builder.TextWordCount > 0));
    }

    private sealed class EmptyFrames : IOverlayFrameSources {
        public bool TryAcquire(int key, out Puck.Hosting.GpuImageLease lease) { lease = default; return false; }
    }
}
