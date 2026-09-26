using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.Overlays;

/// <summary>
/// The unified overlay's CPU half: every writer, the draw-order table, the frame-slot table and the builder that packs
/// one frame's records, with the overflow narration. <see cref="Compose"/> runs the writers in draw order and reports
/// whether anything is visible; <see cref="WritePassValues"/> and <see cref="UploadFrameRegions"/> hand the packed
/// frame to the GPU half: the pass block values and the storage buffer the fragment shader reads. The <c>overlay</c>
/// package's recorder draws through one, so a surface is a new writer, never a new node or shader.
/// </summary>
public sealed class OverlayFrameComposer {
    // The four first-party writers' draw-order table size: Console..Toast (OverlayChannel 0..3). OverlayChannel.Hud (4)
    // is not in the table: it is the banded pipeline's under/base/over sequence plus the unbanded player-scope seat-panel
    // pass (see Compose), opened as its own channel scope up to four times a frame. OverlayChannel.Cursor (5) and
    // OverlayChannel.Wheel (6) are excluded too: they are the frame's last two channel scopes, drawn over everything and
    // outside the replace-band suppression.
    private const int FirstPartyChannelCount = 4;

    /// <summary>The bytes <see cref="WritePassValues"/> writes: three float4 values.</summary>
    public const int PassValueBytes = ((sizeof(float) * 4) * 3);

    // The glyph outline halo width, in encoded signed-distance units: the SDF contrast band that keeps overlay text
    // legible over any world content, kept clear of the atlas' saturation floor at the overlay's screenPxRange.
    private const float OutlineBand = 0.20f;

    private readonly BindingBarWriter? m_bindingBarWriter;
    private readonly OverlayFrameBuilder m_builder;
    // The draw-order table for the four first-party writers, indexed by (int)OverlayChannel: the enum's declared order
    // is the draw order. A null entry is a source this composer has none of. Toast's renderTicks rides
    // m_currentFrameRenderTicks, set once per Compose.
    private readonly Action<OverlayFrameBuilder>?[] m_channelWriters;
    private readonly ConsolePanelWriter? m_consoleWriter;
    private readonly CursorWriter? m_cursorWriter;
    private readonly OverlayFrameSlots m_frameSlots;
    // The authored world-scope HUD's banded writer, or null when the host wired no Hud/HudBindings source pair.
    private readonly HudWriter? m_hudWriter;
    private readonly MarkerWriter? m_markerWriter;
    private readonly UnifiedOverlaySources m_sources;
    private readonly OverlayThemeStore m_theme;
    private readonly ToastWriter? m_toastWriter;
    private readonly WheelWriter? m_wheelWriter;

    // Per-channel reservation-overflow episode latches: set when a channel starts losing records at its own
    // reservation, cleared the frame it renders clean again, so each episode narrates exactly once.
    private readonly bool[] m_overflowEpisodeOpen = new bool[OverlayChannelLeases.Count];
    // Per-channel own-cap-refusal episode latches, independent of m_overflowEpisodeOpen: a channel can open or close
    // this episode with no reservation overflow ever happening.
    private readonly bool[] m_refusalEpisodeOpen = new bool[OverlayChannelLeases.Count];

    private ulong m_currentFrameRenderTicks;
    // The fixed frame-slot table's independent overflow episode, which can span world- and seat-scope HUD documents.
    private bool m_frameSlotOverflowEpisodeOpen;

    /// <summary>Initializes a new instance of the <see cref="OverlayFrameComposer"/> class.</summary>
    /// <param name="sources">The per-surface read seams and the feed tick.</param>
    /// <param name="capacity">The host's declared counts the lease table is derived from (see
    /// <see cref="OverlayCapacity"/>).</param>
    /// <param name="glyphs">The shared SDF glyph pack.</param>
    /// <param name="frameSources">The host's <see cref="OverlayHudElementKind.Frame"/> content seam.</param>
    /// <param name="width">The render width, in pixels.</param>
    /// <param name="height">The render height, in pixels.</param>
    /// <param name="theme">The theme the writers and the token slab start from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/>, <paramref name="glyphs"/> or
    /// <paramref name="frameSources"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> derives a lease table that
    /// over-subscribes an <see cref="OverlayFrameBuilder"/> backstop (see <see cref="OverlayChannelLeases"/>).</exception>
    public OverlayFrameComposer(UnifiedOverlaySources sources, OverlayCapacity capacity, OverlayGlyphSdfPack glyphs, IOverlayFrameSources frameSources, uint width, uint height, in OverlayThemeValues theme) {
        ArgumentNullException.ThrowIfNull(argument: sources);
        ArgumentNullException.ThrowIfNull(argument: glyphs);
        ArgumentNullException.ThrowIfNull(argument: frameSources);

        m_theme = new OverlayThemeStore();
        m_theme.Publish(theme: in theme);
        m_builder = new OverlayFrameBuilder(
            glyphs: glyphs,
            height: height,
            leases: new OverlayChannelLeases(capacity: capacity),
            theme: in theme,
            width: width
        );
        m_bindingBarWriter = ((sources.BindingBar is { } bindingBar)
            ? new BindingBarWriter(
                source: bindingBar,
                theme: m_theme
            )
            : null
        );
        m_consoleWriter = ((sources.Console is { } console)
            ? new ConsolePanelWriter(
                source: console,
                theme: m_theme
            )
            : null
        );
        m_cursorWriter = ((sources.Cursor is { } cursor)
            ? new CursorWriter(
                source: cursor,
                theme: m_theme
            )
            : null
        );
        m_wheelWriter = ((sources.Wheel is { } wheel)
            ? new WheelWriter(
                source: wheel,
                theme: m_theme
            )
            : null
        );
        m_frameSlots = new OverlayFrameSlots(sources: frameSources);
        m_markerWriter = ((sources.Markers is { } markers)
            ? new MarkerWriter(
                maxChipsPerSeat: capacity.MarkerMaxChipsPerSeat,
                source: markers
            )
            : null
        );
        m_hudWriter = (((sources.Hud is { } hudSource) && (sources.HudBindings is { } hudBindings))
            ? new HudWriter(
                bindings: hudBindings,
                frameSlots: m_frameSlots,
                source: hudSource,
                theme: m_theme
            )
            : null
        );
        m_sources = sources;
        m_toastWriter = ((sources.Toast is { } toast)
            ? new ToastWriter(
                source: toast,
                theme: m_theme
            )
            : null
        );

        // Built once, after every writer above is assigned: OverlayChannel's declared values are the array index, so
        // the enum order is the draw order.
        m_channelWriters = new Action<OverlayFrameBuilder>?[FirstPartyChannelCount];
        m_channelWriters[((int)OverlayChannel.Console)] = ((m_consoleWriter is { } consoleForTable)
            ? (builder => consoleForTable.Emit(builder: builder))
            : null
        );
        m_channelWriters[((int)OverlayChannel.BindingBar)] = ((m_bindingBarWriter is { } bindingBarForTable)
            ? (builder => bindingBarForTable.Emit(builder: builder))
            : null
        );
        m_channelWriters[((int)OverlayChannel.Markers)] = ((m_markerWriter is { } markersForTable)
            ? (builder => markersForTable.Emit(builder: builder))
            : null
        );
        m_channelWriters[((int)OverlayChannel.Toast)] = ((m_toastWriter is { } toastForTable)
            ? (builder => toastForTable.Emit(
                builder: builder,
                renderTicks: m_currentFrameRenderTicks
            ))
            : null
        );
    }

    /// <summary>Gets the builder holding the frame's packed records, whose static prefix (the token slab and the glyph
    /// pack) is the first <see cref="OverlayFrameBuilder.PanelBaseWords"/> words of its scratch.</summary>
    public OverlayFrameBuilder Builder => m_builder;
    /// <summary>Gets the frame-slot table the HUD's <c>Frame</c> elements bind their leases through.</summary>
    public OverlayFrameSlots FrameSlots => m_frameSlots;

    // The resources a channel lost this frame, each as {verb} ({written} of {reserved} written), shared by both
    // narrations so a reservation-overflow "dropped" and an own-cap "refused" read in the same shape.
    private static string Describe(string verb, in OverlayChannelUsage counts, in OverlayChannelUsage written, in OverlayChannelReservation reservation) {
        var parts = new List<string>(capacity: 4);

        if (counts.Elements > 0) {
            parts.Add(item: $"{counts.Elements} elements {verb} ({written.Elements} of {reservation.Elements} written)");
        }

        if (counts.TextWords > 0) {
            parts.Add(item: $"{counts.TextWords} text words {verb} ({written.TextWords} of {reservation.TextWords} written)");
        }

        if (counts.Panels > 0) {
            parts.Add(item: $"{counts.Panels} panels {verb} ({written.Panels} of {reservation.Panels} written)");
        }

        if (counts.Clips > 0) {
            parts.Add(item: $"{counts.Clips} clips {verb} ({written.Clips} of {reservation.Clips} written)");
        }

        return string.Join(
            separator: ", ",
            values: parts
        );
    }
    // A schema-valid world HUD and one or more independently valid seat HUDs can compose to more live sources than the
    // shader-backed frame-slot table holds. Keep that runtime-only aggregate failure loud and episode-latched.
    private void NarrateFrameSlotOverflow() {
        if (!m_frameSlots.CapacityExceeded) {
            m_frameSlotOverflowEpisodeOpen = false;

            return;
        }

        if (m_frameSlotOverflowEpisodeOpen) {
            return;
        }

        m_frameSlotOverflowEpisodeOpen = true;

        Console.Error.WriteLine(value: $"[unified-overlay] more than {OverlayFrameSlots.SlotCount} distinct HUD frame source bindings were requested this frame; the additional element was omitted because every shader-backed frame slot was occupied. Each HUD document is capped at {OverlayFrameSlots.SlotCount}, and a cross-fading element occupies two slots (its incoming and outgoing sources) until the fade completes; reduce the combined world-plus-seat source set.");
    }
    // Loud once per episode, per channel, per cause: a channel exceeding its own reservation
    // (OverlayFrameBuilder.Dropped) and a writer refusing its own excess at a self-declared cap
    // (OverlayFrameBuilder.Refused) are different facts and get different messages.
    private void NarrateOverflow() {
        NarrateFrameSlotOverflow();

        if (!m_builder.HasOverflow) {
            Array.Clear(array: m_overflowEpisodeOpen);
            Array.Clear(array: m_refusalEpisodeOpen);

            return;
        }

        for (var index = 0; (index < OverlayChannelLeases.Count); index++) {
            var channel = ((OverlayChannel)index);
            var reservation = m_builder.ReservationOf(channel: channel);
            var written = m_builder.Written(channel: channel);

            NarrateReservationOverflow(
                channel: channel,
                dropped: m_builder.Dropped(channel: channel),
                index: index,
                reservation: in reservation,
                written: in written
            );
            NarrateOwnCapRefusal(
                channel: channel,
                index: index,
                refused: m_builder.Refused(channel: channel),
                reservation: in reservation,
                written: in written
            );
        }
    }
    // The writer itself refused content before offering it to the builder (NoteRefused), or a WriteText run was
    // truncated by its own caller's maxChars: a pinned limit the writer authored, not a reservation overflow.
    private void NarrateOwnCapRefusal(OverlayChannel channel, int index, in OverlayChannelUsage refused, in OverlayChannelReservation reservation, in OverlayChannelUsage written) {
        if (refused.IsEmpty) {
            m_refusalEpisodeOpen[index] = false;

            return;
        }

        if (m_refusalEpisodeOpen[index]) {
            return;
        }

        m_refusalEpisodeOpen[index] = true;

        Console.Error.WriteLine(value: $"[unified-overlay] channel \"{OverlayChannelLeases.NameOf(channel: channel)}\" refused its own excess at a writer-declared cap (NOT a reservation overflow — its reservation is fine): {Describe(
            counts: refused,
            reservation: reservation,
            verb: "refused",
            written: written
        )}. A deliberate, pinned truncation the writer authored; silent until this channel renders clean and refuses again.");
    }
    // The channel asked the builder for more than OverlayChannelLeases reserved it and the excess clipped: a capacity
    // failure, attributed, never touching another channel.
    private void NarrateReservationOverflow(OverlayChannel channel, int index, in OverlayChannelUsage dropped, in OverlayChannelReservation reservation, in OverlayChannelUsage written) {
        if (dropped.IsEmpty) {
            m_overflowEpisodeOpen[index] = false;

            return;
        }

        if (m_overflowEpisodeOpen[index]) {
            return;
        }

        m_overflowEpisodeOpen[index] = true;

        Console.Error.WriteLine(value: $"[unified-overlay] channel \"{OverlayChannelLeases.NameOf(channel: channel)}\" exceeded its own reservation and clipped: {Describe(
            counts: dropped,
            reservation: reservation,
            verb: "dropped",
            written: written
        )}. No other channel lost capacity; silent until this channel renders clean and overflows again.");
    }

    /// <summary>Packs one frame: freshens the pull-model feeds, starts the builder and the frame-slot table, runs every
    /// writer in draw order and narrates any overflow. The banded order, bottom to top, is the HUD's under band, then the
    /// base (the four first-party writers in <see cref="OverlayChannel"/> order, unless a live authored panel declares
    /// the replace band, whose panels take the base instead), the HUD's over band, the player-scope seat panels, the
    /// radial action menu and the drawn cursor, the last two outside the replace-band suppression.</summary>
    /// <param name="renderTicks">The frame's continuous content clock.</param>
    /// <returns><see langword="true"/> when the frame has anything to draw.</returns>
    public bool Compose(ulong renderTicks) {
        m_sources.FeedTick?.Invoke();
        m_builder.BeginFrame();
        m_frameSlots.BeginFrame();
        m_currentFrameRenderTicks = renderTicks;
        m_hudWriter?.RefreshFrame();

        if (m_hudWriter is { } hudUnder) {
            m_builder.BeginChannel(channel: OverlayChannel.Hud);
            hudUnder.EmitUnder(builder: m_builder);
            m_builder.EndChannel();
        }

        if (m_hudWriter is { HasReplace: true } replacingWriter) {
            m_builder.BeginChannel(channel: OverlayChannel.Hud);
            replacingWriter.EmitReplace(builder: m_builder);
            m_builder.EndChannel();
        } else {
            for (var index = 0; (index < m_channelWriters.Length); index++) {
                if (m_channelWriters[index] is not { } writer) {
                    continue;
                }

                m_builder.BeginChannel(channel: ((OverlayChannel)index));
                writer(m_builder);
                m_builder.EndChannel();
            }
        }

        if (m_hudWriter is { } hudOver) {
            m_builder.BeginChannel(channel: OverlayChannel.Hud);
            hudOver.EmitOver(builder: m_builder);
            m_builder.EndChannel();
        }

        // Player-scope per-seat panels are unbanded and drawn last among the HUD scopes, charged against the same Hud
        // reservation as the three world-scope passes.
        if (m_hudWriter is { } hudSeats) {
            m_builder.BeginChannel(channel: OverlayChannel.Hud);
            hudSeats.EmitSeatPanels(builder: m_builder);
            m_builder.EndChannel();
        }

        if (m_wheelWriter is { } wheelWriter) {
            m_builder.BeginChannel(channel: OverlayChannel.Wheel);
            wheelWriter.Emit(builder: m_builder);
            m_builder.EndChannel();
        }

        if (m_cursorWriter is { } cursorWriter) {
            m_builder.BeginChannel(channel: OverlayChannel.Cursor);
            cursorWriter.Emit(builder: m_builder);
            m_builder.EndChannel();
        }

        NarrateOverflow();

        return m_builder.HasContent;
    }
    /// <summary>Republishes the theme every writer reads and refills the builder's token slab from it; the caller
    /// uploads the slab (the first <see cref="OverlayTokenBlock.WordCount"/> words of the scratch) when its buffer
    /// exists.</summary>
    /// <param name="theme">The newly resolved theme.</param>
    public void UpdateTheme(in OverlayThemeValues theme) {
        m_theme.Publish(theme: in theme);
        m_builder.UpdateTokenBlock(theme: in theme);
    }
    /// <summary>Uploads only what this frame wrote, per region, never the capacity-sized region behind it: the
    /// shader's loops are bounded by the same counts, so a region's untouched tail holds nothing it reads.</summary>
    /// <param name="buffer">The storage buffer the fragment shader reads, which holds the regions at the builder's own
    /// bases.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    public void UploadFrameRegions(IGpuStorageBuffer buffer) {
        ArgumentNullException.ThrowIfNull(argument: buffer);

        Upload(
            baseWords: m_builder.PanelBaseWords,
            buffer: buffer,
            length: (m_builder.PanelCount * OverlayFrameBuilder.PanelWords)
        );
        Upload(
            baseWords: m_builder.ElementBaseWords,
            buffer: buffer,
            length: (m_builder.ElementCount * OverlayFrameBuilder.ElementWords)
        );
        Upload(
            baseWords: m_builder.TextBaseWords,
            buffer: buffer,
            length: m_builder.TextWordCount
        );
        Upload(
            baseWords: m_builder.ClipBaseWords,
            buffer: buffer,
            length: (m_builder.ClipCount * OverlayFrameBuilder.ClipWords)
        );
    }
    /// <summary>Writes the pass block's three per-frame values (<see cref="RenderGraphPackageCatalog.OverlayMembers"/>):
    /// <c>counts</c>, <c>sdf</c> and <c>misc</c>, the counts, glyph and outline figures and region bases the shader reads.
    /// Every slot's buffer holds the regions at the same bases, so a steady frame writes the same values.</summary>
    /// <param name="values">The destination: the pass block from <c>counts</c>' offset, where the three float4 values lie
    /// one after another, at least <see cref="PassValueBytes"/> long.</param>
    public void WritePassValues(Span<byte> values) {
        var floats = MemoryMarshal.Cast<byte, float>(span: values);

        floats[0] = m_builder.PanelCount;
        floats[1] = m_builder.ElementCount;
        floats[2] = m_builder.Glyphs.AtlasCellWidth;
        floats[3] = m_builder.Glyphs.AtlasCellHeight;
        floats[4] = m_builder.Glyphs.DistanceRange;
        floats[5] = OutlineBand;
        floats[6] = m_builder.PanelBaseWords;
        floats[7] = m_builder.ElementBaseWords;
        floats[8] = m_builder.TextBaseWords;
        // The glyph pack's base word: the atlas sits after the token slab, in the static prefix.
        floats[9] = OverlayTokenBlock.WordCount;
        floats[10] = m_builder.ClipBaseWords;
        floats[11] = m_builder.Glyphs.GlyphCount;
    }

    private void Upload(IGpuStorageBuffer buffer, int baseWords, int length) {
        if (length <= 0) {
            return;
        }

        buffer.Write<uint>(
            data: m_builder.Scratch.Slice(
                length: length,
                start: baseWords
            ),
            destinationOffsetBytes: ((ulong)(baseWords * sizeof(uint)))
        );
    }
}
