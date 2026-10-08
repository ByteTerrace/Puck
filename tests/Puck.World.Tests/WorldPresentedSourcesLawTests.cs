using Puck.Hosting;
using Puck.SdfVm;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the sources of a world shown through a screen, or a world a seat is presented in, drawn from that
/// world's own: its text screens draw through its own font catalog, resolved beside its own document, and its cameras
/// are views of it that read each other at their previous frame, so two cameras of one world filming each other's
/// screens compose.</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class WorldPresentedSourcesLawTests {
    // A destination whose screen 5 shows two lines of text through its own font catalog, beside its own document.
    private const string Destination = "tests/Puck.World.Canaries/uploaded-sources/fixture.puck";

    // The frame source a session view of a destination renders through, composed as WorldScreenBinder.RegisterSessionView
    // composes it, after one captured frame.
    private static (WorldSessionSceneEmitter Emitter, ISdfFrameSource Source, WorldSessionMirror Mirror) Session(WorldDefinition definition) {
        var mirror = new WorldSessionMirror(placeholder: definition);
        var emitter = new WorldSessionSceneEmitter(
            domains: new WorldValueDomainGuard(),
            effectiveCameraName: null,
            mirror: mirror
        );
        var source = new SdfCompositionFrameSource(
            dresser: emitter,
            emitters: [emitter]
        );

        _ = source.CaptureFrame(
            deltaSeconds: 0f,
            height: WorldViewInstances.DefaultSessionHeight,
            interpolationAlpha: 0f,
            width: WorldViewInstances.DefaultSessionWidth
        );

        return (emitter, source, mirror);
    }
    private static WorldView View(string name, IReadOnlyList<string> reads, IReadOnlyList<string>? previous = null) => new(
        Demand: WorldViewDemand.Screen,
        FilmsWorld: false,
        Height: 0.25,
        Name: name,
        Refresh: RenderGraphRefresh.EveryFrame,
        Width: 0.25
    ) {
        Parent = "session$0",
        PreviousReads = previous,
        Reads = reads,
    };

    // THE LAW: a destination's text screen draws its lines through the destination's own font catalog. A session view of
    // a destination whose screen shows text hands its residency that world's glyph atlas and a decal of the text's cells
    // for the screen, and none for a screen showing no text. The red leg is the same destination delivered with no
    // document directory, as a remote authority delivers it: its fonts resolve beside nothing, so the screen draws no
    // text and the session says why.
    [Fact]
    public void ADestinationsTextScreenDrawsThroughItsOwnFontCatalog() {
        // Delivered as a local instance delivers it: with the directory of the document it was read from.
        var definition = (AuthoredGameFixtures.Load(relativePath: Destination) with {
            DocumentDirectory = WorldDocumentPaths.DirectoryOf(documentPath: Path.Combine(
                path1: AuthoredGameFixtures.Root,
                path2: Destination
            )),
        });

        var (_, source, _) = Session(definition: definition);
        var decals = Assert.IsAssignableFrom<IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>>(@object: source.ScreenDecals);
        var decal = Assert.IsType<SdfScreenDecalFrame>(@object: decals[5]());

        Assert.NotNull(@object: source.GlyphAtlas);
        Assert.Equal(expected: 2, actual: decal.Rows);
        Assert.Null(@object: decals[0]());

        var (remote, remoteSource, _) = Session(definition: (definition with { DocumentDirectory = null }));

        Assert.Null(@object: remoteSource.ScreenDecals![5]());
        Assert.Null(@object: remoteSource.GlyphAtlas);
        Assert.Contains(
            expectedSubstring: "directory",
            actualString: remote.TextFault
        );
    }
    // THE LAW: an unresolved delivery clears text even after a valid catalog has drawn. A fresh emitter with no
    // directory is insufficient to exercise the stale catalog; this same emitter loses its directory or its font pin,
    // draws no text, and recovers when a valid delivery returns.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AFailedFontDeliveryStopsUsingThePreviousCatalog(bool badPin) {
        var definition = (AuthoredGameFixtures.Load(relativePath: Destination) with {
            DocumentDirectory = WorldDocumentPaths.DirectoryOf(documentPath: Path.Combine(
                path1: AuthoredGameFixtures.Root,
                path2: Destination
            )),
        });

        var (emitter, source, mirror) = Session(definition: definition);
        var decals = source.ScreenDecals!;

        Assert.NotNull(@object: source.GlyphAtlas);
        Assert.NotNull(@object: decals[5]());

        var text = definition.Text!;
        var failed = (badPin
            ? definition with {
                Text = text with {
                    Fonts = [.. text.Fonts.Select(selector: static font => font with { Hash = "sha256-64/0000000000000000" })],
                },
            }
            : definition with { DocumentDirectory = null });

        mirror.DeliverDefinition(definition: failed, version: default);

        Assert.Null(@object: source.GlyphAtlas);
        Assert.Null(@object: decals[5]());
        Assert.NotNull(@object: emitter.TextFault);

        mirror.DeliverDefinition(definition: definition, version: default);

        Assert.NotNull(@object: source.GlyphAtlas);
        Assert.NotNull(@object: decals[5]());
        Assert.Null(@object: emitter.TextFault);
    }
    // THE LAW: the cameras of a world shown through a screen read each other at their previous frame. Two cameras of one
    // level, each filming a screen that shows the other, compose with the session that shows them, which reads both
    // within the frame. The red leg reads each camera within the frame, as the session does: the set refuses the cycle.
    [Fact]
    public void TheCamerasOfAPresentedWorldReadEachOtherAtTheirPreviousFrame() {
        string[] cameras = ["session$0$camera$east", "session$0$camera$west"];

        RenderGraphInstanceRefusal? Compose(bool previous) => (RenderGraphInstanceSet.TryCreate(
            instances: WorldViewInstances.Of(views: [
                View(name: "session$0", reads: cameras) with { Parent = null },
                .. cameras.Select(selector: camera => (previous
                    ? View(name: camera, previous: cameras, reads: [])
                    : View(name: camera, reads: cameras))),
            ]).Instances(sources: []),
            refusal: out var refusal,
            set: out _
        )
            ? null
            : refusal);

        Assert.Null(@object: Compose(previous: true));
        Assert.Equal(
            actual: Compose(previous: false)?.Code,
            expected: RenderGraphInstanceRefusalCode.SameFrameCycle
        );
    }
}
