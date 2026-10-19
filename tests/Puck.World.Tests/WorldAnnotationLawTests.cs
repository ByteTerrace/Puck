using System.Numerics;
using Puck.Commands;
using Puck.Hosting;
using Puck.Input;
using Puck.Overlays;
using Puck.Text;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: annotate mode keeps what a person circles and nothing else sees the pointer while it does. The verb keeps,
/// lists, removes and refuses outlines; in annotate mode a left drag keeps the ellipse fitting its box, a click a dot
/// ring and a right click clears, every pointer button and relative motion is consumed, and a release is consumed
/// exactly when its press was; the overlay packs each outline as a stroked ellipse record.
/// </summary>
public sealed class WorldAnnotationLawTests {
    private static OverlayGlyphSdfPack Glyphs() {
        var glyphs = OverlayGlyphSdfPack.TryCreate(monoFont: new FontAtlas(
            kind: FontAtlasKind.Sdf, imagePath: "annotation.png", size: 1f, distanceRange: 1f, width: 1, height: 1,
            metrics: default,
            glyphs: [.. "0123456789".Select(selector: static digit => new FontAtlasGlyph(unicode: digit, advance: 1f, planeBounds: null,
                atlasBounds: new FontAtlasBounds(Bottom: 1f, Left: 0f, Right: 1f, Top: 0f)))],
            kerningPairs: [], imageData: new FontAtlasImageData(height: 1, rgbaPixels: [255, 255, 255, 255], width: 1)
        ));

        Assert.NotNull(@object: glyphs);

        return glyphs;
    }
    private static OverlayFrameBuilder Builder() => new(glyphs: Glyphs(), height: 200, width: 400, theme: OverlayThemeValues.Zero,
        leases: new OverlayChannelLeases(capacity: WorldOverlayCapacity.FromSchema()));
    private static int Draw(OverlayFrameBuilder builder, IAnnotationSource source) {
        builder.BeginFrame();
        builder.BeginChannel(channel: OverlayChannel.Annotation);
        new AnnotationWriter(source: source, theme: new OverlayThemeStore()).Emit(builder: builder);
        builder.EndChannel();

        return builder.ElementCount;
    }
    private static WindowInputEvent Button(int index, CommandPhase phase) => new(Kind: WindowInputKind.PointerButton, ButtonIndex: index, Phase: phase);
    private static WindowInputEvent Position(float x, float y) => new(Kind: WindowInputKind.PointerPosition, Vector: new Vector2(x: x, y: y));

    [Fact]
    public void TheVerbKeepsListsRemovesAndRefusesOutlines() {
        var annotations = new WorldAnnotations();
        var registry = new CommandRegistry(modules: [new WorldAnnotationCommandModule(annotations: annotations)]);

        Assert.Equal(expected: "[view.annotate: off circles=0/16]", actual: registry.Submit(line: "view.annotate").Output);
        Assert.Equal(expected: "[view.annotate: on circles=0]", actual: registry.Submit(line: "view.annotate on").Output);
        Assert.Equal(expected: "[view.annotate: 1 center=0.500,0.500 radius=0.100,0.150]", actual: registry.Submit(line: "view.annotate add 0.5 0.5 0.1 0.15").Output);
        Assert.Equal(expected: "[view.annotate: 2 center=0.250,0.300 radius=0.050,0.050]", actual: registry.Submit(line: "view.annotate add 0.25 0.3 0.05").Output);

        // A center off the window, a short form and an unparsable radius each refuse and keep nothing.
        Assert.True(condition: registry.Submit(line: "view.annotate add 2 0 0.1").IsError);
        Assert.True(condition: registry.Submit(line: "view.annotate add 0.5").IsError);
        Assert.True(condition: registry.Submit(line: "view.annotate add 0.5 0.5 0.1 x").IsError);
        Assert.True(condition: registry.Submit(line: "view.annotate bogus").IsError);
        Assert.Equal(
            expected: string.Join(separator: Environment.NewLine, values: [
                "[view.annotate: on circles=2/16]",
                "[view.annotate: 1 center=0.500,0.500 radius=0.100,0.150]",
                "[view.annotate: 2 center=0.250,0.300 radius=0.050,0.050]",
            ]),
            actual: registry.Submit(line: "view.annotate list").Output
        );
        Assert.Equal(expected: "[view.annotate: removed 2]", actual: registry.Submit(line: "view.annotate undo").Output);
        Assert.Equal(expected: "[view.annotate: off circles=1]", actual: registry.Submit(line: "view.annotate off").Output);
        Assert.Equal(expected: "[view.annotate: on circles=1]", actual: registry.Submit(line: "view.annotate toggle").Output);
        Assert.Equal(expected: "[view.annotate: cleared 1]", actual: registry.Submit(line: "view.annotate clear").Output);
        Assert.True(condition: registry.Submit(line: "view.annotate undo").IsError);

        for (var index = 0; (index < AnnotationWriter.MaxCircles); index++) {
            Assert.False(condition: registry.Submit(line: "view.annotate add 0.5 0.5 0.1").IsError);
        }

        Assert.True(condition: registry.Submit(line: "view.annotate add 0.5 0.5 0.1").IsError);
        Assert.Equal(expected: AnnotationWriter.MaxCircles, actual: annotations.Count);
    }
    [Fact]
    public void AnnotateModeKeepsTheDragAndWithholdsThePointer() {
        var annotations = new WorldAnnotations();
        var viewports = new WorldSeatViewports();
        var input = new WorldAnnotationInput(annotations: annotations, viewports: viewports);
        var filters = new WorldWindowInputFilters(input);

        // Off, nothing is consumed; a release whose press passed on passes on too.
        Assert.False(condition: filters.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Started)));
        Assert.False(condition: filters.Intercept(inputEvent: new WindowInputEvent(Kind: WindowInputKind.PointerMove, Vector: Vector2.One)));
        annotations.SetActive(active: true);
        Assert.False(condition: filters.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Completed)));

        // Before the window publishes its extent a drag has nothing to normalize against and keeps nothing.
        Assert.False(condition: input.Intercept(inputEvent: Position(x: 10f, y: 10f)));
        Assert.True(condition: input.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Started)));
        Assert.True(condition: input.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Completed)));
        Assert.Equal(expected: 0, actual: annotations.Count);

        viewports.PublishClientExtent(height: 200, width: 400);
        Assert.False(condition: input.Intercept(inputEvent: Position(x: 100f, y: 50f)));
        Assert.True(condition: input.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Started)));
        Assert.False(condition: input.Intercept(inputEvent: Position(x: 300f, y: 150f)));
        Assert.True(condition: input.Intercept(inputEvent: new WindowInputEvent(Kind: WindowInputKind.PointerMove, Vector: Vector2.One)));
        Assert.True(condition: annotations.TrySnapshot(frame: out var dragging));
        Assert.Equal(expected: new OverlayAnnotationCircle(CenterX: 0.5f, CenterY: 0.5f, RadiusX: 0.25f, RadiusY: 0.25f), actual: dragging.Drag);
        Assert.True(condition: input.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Completed)));

        // A click keeps a dot ring around the click.
        Assert.False(condition: input.Intercept(inputEvent: Position(x: 40f, y: 40f)));
        Assert.True(condition: input.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Started)));
        Assert.True(condition: input.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Completed)));
        Assert.Equal(
            expected: [
                new OverlayAnnotationCircle(CenterX: 0.5f, CenterY: 0.5f, RadiusX: 0.25f, RadiusY: 0.25f),
                new OverlayAnnotationCircle(CenterX: 0.1f, CenterY: 0.2f, RadiusX: (18f / 400f), RadiusY: (18f / 200f)),
            ],
            actual: annotations.List()
        );

        // A drag begun in annotate mode ends with its own release after the mode is left.
        Assert.True(condition: input.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Started)));
        annotations.SetActive(active: false);
        Assert.True(condition: input.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Completed)));
        Assert.Equal(expected: 3, actual: annotations.Count);
        annotations.SetActive(active: true);

        // Focus loss releases everything, so no release follows; wheel and keys pass on.
        Assert.True(condition: input.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Started)));
        Assert.False(condition: input.Intercept(inputEvent: WindowInputEvent.FocusLost()));
        Assert.False(condition: input.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Completed)));
        Assert.False(condition: input.Intercept(inputEvent: new WindowInputEvent(Kind: WindowInputKind.PointerWheel, Vector: Vector2.One)));

        // A press with the pointer outside the window and a button past the tracked range are consumed, and draw nothing.
        Assert.False(condition: input.Intercept(inputEvent: WindowInputEvent.PointerLeft()));
        Assert.True(condition: input.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Started)));
        Assert.True(condition: input.Intercept(inputEvent: Button(index: 0, phase: CommandPhase.Completed)));
        Assert.True(condition: input.Intercept(inputEvent: Button(index: 40, phase: CommandPhase.Started)));
        Assert.False(condition: input.Intercept(inputEvent: Button(index: 40, phase: CommandPhase.Completed)));
        Assert.Equal(expected: 3, actual: annotations.Count);

        // A right click clears.
        Assert.True(condition: filters.Intercept(inputEvent: Button(index: 1, phase: CommandPhase.Started)));
        Assert.True(condition: filters.Intercept(inputEvent: Button(index: 1, phase: CommandPhase.Completed)));
        Assert.Equal(expected: 0, actual: annotations.Count);
    }
    [Fact]
    public void TheOverlayPacksEachOutlineAsAStrokedEllipse() {
        var annotations = new WorldAnnotations();
        var builder = Builder();

        Assert.Equal(expected: 0, actual: Draw(builder: builder, source: annotations));

        for (var index = 0; (index < 10); index++) {
            Assert.True(condition: annotations.TryAdd(circle: new OverlayAnnotationCircle(CenterX: 0.5f, CenterY: 0.5f, RadiusX: 0.25f, RadiusY: 0.1f), index: out _));
        }

        annotations.SetDrag(drag: new OverlayAnnotationCircle(CenterX: 0.2f, CenterY: 0.2f, RadiusX: 0.05f, RadiusY: 0.05f));
        annotations.SetActive(active: true);

        // Ten outlines of a halo, a stroke and a label, the drag's halo and stroke, and the banner's plate and text.
        Assert.Equal(expected: ((10 * 3) + 2 + 2), actual: Draw(builder: builder, source: annotations));

        var record = builder.Scratch.Slice(start: builder.ElementBaseWords, length: OverlayFrameBuilder.ElementWords);

        Assert.Equal(expected: (3u | (((uint)OverlayColorRole.Custom) << 4)), actual: record[4]);
        Assert.Equal(expected: 100f, actual: BitConverter.UInt32BitsToSingle(value: record[2]));
        Assert.Equal(expected: 20f, actual: BitConverter.UInt32BitsToSingle(value: record[3]));
        Assert.Equal(expected: 2.5f, actual: BitConverter.UInt32BitsToSingle(value: record[10]));

        // A source past the writer's ceiling has its excess refused by name, not drawn.
        Assert.Equal(expected: (AnnotationWriter.MaxCircles * 3), actual: Draw(builder: builder, source: new Overfull()));
        Assert.Equal(expected: 3, actual: builder.Refused(channel: OverlayChannel.Annotation).Elements);

        // An ellipse past the channel's reservation is dropped and attributed to it.
        builder.BeginFrame();
        builder.BeginChannel(channel: OverlayChannel.Annotation);

        for (var index = 0; (index <= AnnotationWriter.MaxElements); index++) {
            builder.WriteEllipse(alpha: 1f, centerX: 1f, centerY: 1f, color: default, halfWidth: 1f, radiusX: 1f, radiusY: 1f);
        }

        builder.EndChannel();
        Assert.Equal(expected: 1, actual: builder.Dropped(channel: OverlayChannel.Annotation).Elements);

        var composer = new OverlayFrameComposer(
            sources: new UnifiedOverlaySources(Console: null, BindingBar: null, Toast: null, FeedTick: null, Annotations: annotations),
            capacity: WorldOverlayCapacity.FromSchema(), glyphs: Glyphs(), frameSources: new NoFrames(), width: 400, height: 200,
            theme: OverlayThemeValues.Zero);

        Assert.True(condition: composer.Compose(renderTicks: 0));
        Assert.Equal(expected: ((10 * 3) + 2 + 2), actual: composer.Builder.Written(channel: OverlayChannel.Annotation).Elements);
    }

    private sealed class Overfull : IAnnotationSource {
        private readonly OverlayAnnotationCircle[] m_circles = [.. Enumerable.Repeat(count: (AnnotationWriter.MaxCircles + 1), element: new OverlayAnnotationCircle(CenterX: 0.5f, CenterY: 0.5f, RadiusX: 0.1f, RadiusY: 0.1f))];

        public bool TrySnapshot(out OverlayAnnotationFrame frame) {
            frame = new OverlayAnnotationFrame(Active: false, Circles: m_circles, Drag: null);

            return true;
        }
    }
    private sealed class NoFrames : IOverlayFrameSources {
        public bool TryAcquire(int key, out GpuImageLease lease) {
            lease = default;

            return false;
        }
    }
}
