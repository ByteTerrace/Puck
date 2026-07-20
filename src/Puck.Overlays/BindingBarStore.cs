using Puck.Compositing;
using Puck.Hosting;
using Puck.Input.Devices;

namespace Puck.Overlays;

/// <summary>One binding-bar slot as the renderer consumes it — everything family- and binding-resolved on the CPU.</summary>
/// <param name="Glyph">The physical-button badge glyph (already family-resolved).</param>
/// <param name="Icon">The bound action's icon (<see cref="OverlayIconId.None"/> = no action symbol).</param>
/// <param name="Visible">Whether the slot draws at all.</param>
/// <param name="Pressed">Whether the physical button is currently down — the chip's HELD tier-1 state.</param>
/// <param name="Alpha">The slot opacity: 1.0 on an active bar, a passive fraction otherwise.</param>
/// <param name="Bound">Whether a real action is bound to this physical button; <see langword="false"/> is the chip's
/// DISABLED tier-0 state (a free/unbound button, still shown so the socket reads).</param>
/// <param name="Accent">Whether this slot is the CONTEXT-PRIMARY action — the chip's ACCENT tier-1 state.</param>
public readonly record struct OverlayBindingSlot(
    OverlayGlyphId Glyph,
    OverlayIconId Icon,
    bool Visible,
    bool Pressed,
    float Alpha,
    bool Bound = true,
    bool Accent = false
);

/// <summary>One declared modifier as the renderer consumes it (the trigger pips between the clusters).</summary>
/// <param name="Glyph">The modifier's badge glyph.</param>
/// <param name="Held">Whether the active page's chord requires (i.e. the player holds) this modifier.</param>
public readonly record struct OverlayBindingModifier(
    OverlayGlyphId Glyph,
    bool Held
);

/// <summary>One seat's binding-bar snapshot: the resolved slots of its ACTIVE page plus the normalized frame region
/// its bar is confined to (per-viewport scoping happens here, at the writer layer — the render node stays dumb).</summary>
/// <param name="Viewport">The seat's viewport rect in normalized frame space (its <c>LayoutRegion</c>).</param>
/// <param name="PageId">The active page's id (diagnostics / transitions).</param>
/// <param name="Group">The seat's active page group (diagnostics / transitions).</param>
/// <param name="Label">The active page's display label — drawn beside the modifier pips so holding a trigger chord
/// NAMES the page it turned to; empty draws nothing.</param>
/// <param name="Slots">The twelve layout slots, in <see cref="BindingBarLayout.SlotButtons"/> order.</param>
/// <param name="Modifiers">The declared modifiers, in profile order.</param>
/// <param name="Hints">The active group's command-chord hint lines (e.g. <c>"LT+RT Snapshot"</c>), pre-formatted
/// ASCII — rendered as small text above the modifier pips so a chord-fired act is discoverable.</param>
public readonly record struct OverlayBindingSeat(
    NormalizedRect Viewport,
    string PageId,
    string Group,
    string Label,
    ReadOnlyMemory<OverlayBindingSlot> Slots,
    ReadOnlyMemory<OverlayBindingModifier> Modifiers,
    ReadOnlyMemory<string> Hints
);

/// <summary>The per-frame binding-bar snapshot the unified overlay renders — one entry per joined seat.</summary>
/// <param name="Family">The active controller family (glyph theming; one family per machine today).</param>
/// <param name="Seats">The joined seats, in slot order.</param>
public readonly record struct OverlayBindingBarFrame(
    GamepadType Family,
    ReadOnlyMemory<OverlayBindingSeat> Seats
);

/// <summary>The read seam <see cref="BindingBarWriter"/> consumes; the host's binding feed is the writer.</summary>
public interface IBindingBarSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out OverlayBindingBarFrame frame);
}

/// <summary>
/// The binding-bar state store. A thin named wrapper over the shared <see cref="PublishBuffer{T}"/>. When the feed
/// runs on the SAME thread as the render node (the <c>FeedTick</c> hook), backing arrays may be reused across
/// publishes with zero steady-state allocation; a cross-thread feed must publish freshly allocated snapshots.
/// </summary>
public sealed class BindingBarStore : IBindingBarSource {
    private readonly PublishBuffer<OverlayBindingBarFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in OverlayBindingBarFrame frame) => m_buffer.Publish(frame: frame);

    /// <inheritdoc/>
    public bool TrySnapshot(out OverlayBindingBarFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}
