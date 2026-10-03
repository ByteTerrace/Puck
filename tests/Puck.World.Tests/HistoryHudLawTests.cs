using Puck.Hosting;
using Puck.Overlays;
using Puck.Text;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class HistoryHudLawTests {
    [Fact]
    public void AHistoryHudLineAllocatesNothingAsItsCursorMoves() {
        using var harness = new WorldHistoryHarness(seats: 0);
        var definition = harness.Fixture.Server.Definition;
        var client = ClientFixtures.Client(definition: definition);
        var resolver = new WorldHudBindingResolver(
            client: client,
            continuum: new WorldContinuum(client, new WorldSeatAuthorityRouter(), new NoNeighbours()),
            frameRate: new FrameRateMonitor(),
            history: harness.History,
            population: harness.Fixture.Server.Population,
            seatBindings: new WorldSeatBindings(definition: definition)
        );
        var source = new HudStore();
        var rect = new OverlayHudRect(Height: 1f, Width: 1f, X: 0f, Y: 0f);

        source.Publish(frame: new OverlayHudFrame(Panels: new[] {
            new OverlayHudPanel(Id: "history", Rect: rect, Band: OverlayHudBand.Over, Style: OverlayPanelStyle.Panel,
                Elements: new[] {
                    new OverlayHudElement(Kind: OverlayHudElementKind.Text, Rect: rect, Role: OverlayColorRole.TextPrimary,
                        Text: null, Binding: null, Template: new[] {
                            new OverlayHudTemplateSegment(IsPlaceholder: true, Text: "history.cursor"),
                            new OverlayHudTemplateSegment(IsPlaceholder: false, Text: " / "),
                            new OverlayHudTemplateSegment(IsPlaceholder: true, Text: "history.window"),
                        }),
                }),
        }));
        var glyphs = OverlayGlyphSdfPack.TryCreate(monoFont: new FontAtlas(
            kind: FontAtlasKind.Sdf, imagePath: "history-hud.png", size: 1f, distanceRange: 1f, width: 1, height: 1,
            metrics: default,
            glyphs: [new FontAtlasGlyph(unicode: '0', advance: 1f, planeBounds: null,
                atlasBounds: new FontAtlasBounds(Bottom: 1f, Left: 0f, Right: 1f, Top: 0f))],
            kerningPairs: [], imageData: new FontAtlasImageData(height: 1, rgbaPixels: [255, 255, 255, 255], width: 1)
        ));

        Assert.NotNull(@object: glyphs);
        var builder = new OverlayFrameBuilder(glyphs: glyphs, height: 100, width: 400, theme: OverlayThemeValues.Zero,
            leases: new OverlayChannelLeases(capacity: new OverlayCapacity(
                BindingBarMaxBanks: 0, BindingBarMaxModifiers: 0, BindingBarMaxSlotsPerBank: 0,
                HudElementsPerPanel: 1, HudElementsPerSeatPanel: 0, HudPanels: 1, HudSeatPanelsPerSeat: 0,
                MarkerMaxChipsPerSeat: 0, Seats: 0, WheelMaxRings: 0, WheelMaxSectorsPerRing: 0
            )));
        var writer = new HudWriter(source: source, bindings: resolver, theme: new OverlayThemeStore(),
            frameSlots: new OverlayFrameSlots(sources: new NoFrames()));

        void Draw() {
            builder.BeginFrame();
            writer.RefreshFrame();
            builder.BeginChannel(channel: OverlayChannel.Hud);
            writer.EmitOver(builder: builder);
            builder.EndChannel();
        }
        harness.Steps(count: 400);
        for (var warm = 0; (warm < 20); warm++) {
            Draw();
        }
        var allocated = 0L;

        for (var tick = 0; (tick < 30); tick++) {
            harness.StepWithoutInput();
            var before = GC.GetAllocatedBytesForCurrentThread();

            Draw();
            allocated += (GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Assert.True(condition: (builder.ElementCount > 0));
        Assert.Equal(actual: allocated, expected: 0L);
    }

    private sealed class NoFrames : IOverlayFrameSources {
        public bool TryAcquire(int key, out GpuImageLease lease) => throw new InvalidOperationException(message: "the history line contains no sampled frame");
    }
    private sealed class NoNeighbours : IWorldAdjacencySource {
        public void BeginTick(ulong tick) { }
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;
            return false;
        }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => [];
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) {
            departedFrom = default;
            return false;
        }
    }
}
