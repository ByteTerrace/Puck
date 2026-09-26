using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldViewPaneMappingLawTests {
    private readonly OverlayFrameBuilder m_builder = new(
        glyphs: CreateGlyphs(
            atlasCellHeight: 1,
            atlasCellWidth: 1,
            distanceRange: 1f,
            glyphCount: 1,
            packedSdf: [0u]
        ),
        height: Display,
        leases: new OverlayChannelLeases(capacity: new OverlayCapacity(
            BindingBarMaxBanks: 0,
            BindingBarMaxModifiers: 0,
            BindingBarMaxSlotsPerBank: 0,
            HudElementsPerPanel: 0,
            HudElementsPerSeatPanel: 0,
            HudPanels: 0,
            HudSeatPanelsPerSeat: 0,
            MarkerMaxChipsPerSeat: 0,
            Seats: 1,
            WheelMaxRings: 0,
            WheelMaxSectorsPerRing: 0
        )),
        theme: OverlayThemeValues.Zero,
        width: Display
    );
    private readonly CursorStore m_cursor = new();

    private CursorWriter? m_cursorWriter;

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern OverlayGlyphSdfPack CreateGlyphs(int atlasCellWidth, int atlasCellHeight, float distanceRange, uint[] packedSdf, int glyphCount);
    // The outline a pane's rect owes: four one-pixel edges (the zero theme's hairline, raised to a pixel) inside the rect,
    // top, bottom, left and right, in normalized frame space, derived here in display pixels.
    private static (float X, float Y, float Width, float Height)[] EdgesOf(NormalizedRect pane) {
        const float Edge = 1f;
        var x = (pane.X * Display);
        var y = (pane.Y * Display);
        var width = (pane.Width * Display);
        var height = (pane.Height * Display);

        return [
            ((x / Display), (y / Display), (width / Display), (Edge / Display)),
            ((x / Display), (((y + height) - Edge) / Display), (width / Display), (Edge / Display)),
            ((x / Display), ((y + Edge) / Display), (Edge / Display), ((height - (2f * Edge)) / Display)),
            ((((x + width) - Edge) / Display), ((y + Edge) / Display), (Edge / Display), ((height - (2f * Edge)) / Display)),
        ];
    }
    // The overlay's cursor pass: the cursor writer emits the published cursor frame into its channel.
    private void Emit() {
        m_cursorWriter ??= new CursorWriter(
            source: m_cursor,
            theme: new OverlayThemeStore()
        );
        m_builder.BeginFrame();
        m_builder.BeginChannel(channel: OverlayChannel.Cursor);
        m_cursorWriter.Emit(builder: m_builder);
        m_builder.EndChannel();
    }
    // The cursor feed's half of a frame, after the host's: ask the host's picker which pane the pointer's display point
    // hovers, publish that pane's rect with the cursor frame, and let the overlay's cursor writer emit the frame.
    private SourceMapping? Hover(double x, double y) {
        var pane = m_host.Hover(point: new Vector2(
            x: ((float)x),
            y: ((float)y)
        ));

        Publish(pane: pane);
        Emit();

        return pane;
    }
    // Publishes the cursor frame the feed would: no drawn seat, and the hovered pane's rect, as WorldCursorFeed.Tick
    // derives it from the pane's placement.
    private void Publish(SourceMapping? pane) => m_cursor.Publish(frame: new OverlayCursorFrame(
        HoveredPane: ((pane?.Placement is SourcePlacement.Pane { Region: var region })
            ? region
            : null),
        Seats: ReadOnlyMemory<OverlayCursorSeat>.Empty
    ));
    // The rects the cursor channel drew this frame, in normalized frame space, each asserted to be an accent fill.
    private (float X, float Y, float Width, float Height)[] Outline() {
        var scratch = m_builder.Scratch;
        var rects = new (float X, float Y, float Width, float Height)[m_builder.ElementCount];

        for (var index = 0; (index < rects.Length); index++) {
            var offset = (m_builder.ElementBaseWords + (index * OverlayFrameBuilder.ElementWords));

            Assert.Equal(
                actual: scratch[(offset + 4)],
                expected: 1u | (((uint)OverlayColorRole.Accent) << 4)
            );
            rects[index] = (
                BitConverter.UInt32BitsToSingle(value: scratch[offset]),
                BitConverter.UInt32BitsToSingle(value: scratch[(offset + 1)]),
                BitConverter.UInt32BitsToSingle(value: scratch[(offset + 2)]),
                BitConverter.UInt32BitsToSingle(value: scratch[(offset + 3)])
            );
        }

        return rects;
    }

    // The pane the picker answers for the pointer is the hovered pane, and the cursor writer outlines exactly its rect:
    // over the pane, the pane; over the second view, that view; then, once the views leave, off every pane, none; and a
    // pane the layout stops showing is never hovered where it stood, though its instance still lives.
    [Fact]
    public void ThePaneUnderThePointerIsHoveredAndOutlined() {
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: Left));
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 3f), Region: Right));
        Frame(pane: TopLeft);
        Frame(pane: TopLeft);

        var onPane = Hover(x: 8.5, y: 8.5);

        Assert.Same(
            actual: onPane,
            expected: m_host.Panes[2]
        );
        Assert.Equal(
            actual: (m_host.HoveredPick.Source.Name, m_host.HoveredPick.Hit.PixelX, m_host.HoveredPick.Hit.PixelY),
            expected: (Pane, 8L, 8L)
        );
        Assert.Equal(
            actual: Outline(),
            expected: EdgesOf(pane: TopLeft)
        );

        var onView = Hover(x: 40.5, y: 40.5);

        Assert.Equal(
            actual: (onView?.Source.Name, m_host.HoveredPick.Hit.PixelX, m_host.HoveredPick.Hit.PixelY),
            expected: (WorldRootGraph.ProducerOf(view: 1), 8L, 40L)
        );
        Assert.Equal(
            actual: Outline(),
            expected: EdgesOf(pane: Right)
        );

        // Publishing the next frame's panes forgets the hover until the pointer is asked about again.
        Prepare(pane: TopLeft);
        Assert.Null(@object: m_host.HoveredPane);

        // With the views gone only the pane is published, so the right half is off every pane.
        m_views.Clear();
        Frame(pane: TopLeft);
        Frame(pane: TopLeft);
        Assert.Null(@object: Hover(x: 40.5, y: 40.5));
        Assert.Equal(
            actual: m_host.HoveredPick,
            expected: default(SourcePick)
        );
        Assert.Empty(collection: Outline());
        Assert.Same(
            actual: Hover(x: 8.5, y: 8.5),
            expected: Assert.Single(collection: m_host.Panes)
        );

        // A layout that stops showing the pane publishes nothing for it, so the point it covered hovers nothing.
        Frame(pane: null);
        Frame(pane: null);
        Assert.True(condition: (m_instances.Instances.IndexOf(name: Pane) >= 0));
        Assert.Null(@object: Hover(x: 8.5, y: 8.5));
        Assert.Empty(collection: Outline());
    }
    // A steady frame with the pointer resting on a pane allocates nothing in the host publishing its panes, the picker
    // hovering the pane, or the cursor writer outlining it. The cursor store's publish between them is not measured: a
    // PublishBuffer holder per published frame is the store's existing cost, which the hover adds nothing to.
    [Fact]
    public void ASteadyHoveredFrameAllocatesNothing() {
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: Left));
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 3f), Region: Right));

        for (var frame = 0; (frame < 4); frame++) {
            Frame(pane: TopLeft);
            _ = Hover(x: 8.5, y: 8.5);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        Prepare(pane: TopLeft);

        var pane = m_host.Hover(point: new Vector2(
            x: 8.5f,
            y: 8.5f
        ));
        var hovering = (GC.GetAllocatedBytesForCurrentThread() - before);

        Publish(pane: pane);
        before = GC.GetAllocatedBytesForCurrentThread();
        Emit();

        var outlining = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.Equal(
            actual: (hovering, outlining),
            expected: (0L, 0L)
        );
        Assert.Equal(
            actual: m_host.HoveredPane?.Source.Name,
            expected: Pane
        );
        Assert.Equal(
            actual: m_builder.ElementCount,
            expected: CursorWriter.PaneOutlineElements
        );
    }
}
