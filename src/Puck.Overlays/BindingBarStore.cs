using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Input.Devices;

namespace Puck.Overlays;

/// <summary>One binding-bar slot as the renderer consumes it — everything family- and binding-resolved on the CPU.
/// One physical button rendered once PER BANK that carries it in its slot set; <see cref="Category"/>/
/// <see cref="CategoryIndex"/>/<see cref="CategoryCount"/> are the geometry inputs the writer feeds
/// <see cref="BindingBarLayout.Place"/> (the region's pixel aspect is a write-time fact, so the placement itself is
/// resolved there, not here), and <see cref="BankOffset"/> is that slot's owning bank's authored 2D offset (region-
/// height units, y-down), added to the resolved placement so several banks stack around one anchor.</summary>
/// <param name="BadgeGlyph0">The physical-button badge's first (or only) atlas glyph index — already family- and
/// world-icon-table-resolved, 1-based, 0 = no badge (see <see cref="OverlayFrameBuilder.WriteIcon"/>).</param>
/// <param name="BadgeGlyph1">The badge's second atlas glyph index (a 2-character label's second cell), 1-based,
/// 0 = a single-glyph badge.</param>
/// <param name="IconGlyph0">The bound action's first (or only) atlas glyph index, 1-based, 0 = no action symbol.</param>
/// <param name="IconGlyph1">The bound action's second atlas glyph index, 1-based, 0 = a single-glyph icon.</param>
/// <param name="Visible">Whether the slot draws at all.</param>
/// <param name="Pressed">Whether the physical button is currently down — the chip's HELD tier-1 state.</param>
/// <param name="Alpha">The slot opacity: the owning bank's authored alpha (or active-alpha), folded with the bound/
/// unbound dim and the seat's own multi-seat dim.</param>
/// <param name="Category">The slot's placement category.</param>
/// <param name="CategoryIndex">The slot's index within its category (see <see cref="BindingBarLayout.Place"/>).</param>
/// <param name="CategoryCount">The bar's total exotic-category slots (centers the exotics row); meaningless
/// otherwise.</param>
/// <param name="BankOffset">The owning bank's authored 2D offset, region-height units, y-down.</param>
/// <param name="Bound">Whether a real action is bound to this physical button; <see langword="false"/> is the chip's
/// DISABLED tier-0 state (a free/unbound button, still shown so the socket reads) unless the resolved policy hides
/// unbound slots, in which case it is not <see cref="Visible"/> instead.</param>
/// <param name="Accent">Whether this slot is the CONTEXT-PRIMARY action — the chip's ACCENT tier-1 state.</param>
public readonly record struct OverlayBindingSlot(
    ushort BadgeGlyph0,
    ushort BadgeGlyph1,
    ushort IconGlyph0,
    ushort IconGlyph1,
    bool Visible,
    bool Pressed,
    float Alpha,
    BindingSlotCategory Category,
    int CategoryIndex,
    int CategoryCount,
    Vector2 BankOffset,
    bool Bound = true,
    bool Accent = false
);
/// <summary>One declared modifier as the renderer consumes it (the trigger pips between the clusters).</summary>
/// <param name="BadgeGlyph0">The modifier's first (or only) badge atlas glyph index, 1-based, 0 = none.</param>
/// <param name="BadgeGlyph1">The modifier badge's second atlas glyph index, 1-based, 0 = a single-glyph badge.</param>
/// <param name="Held">Whether the active page's chord requires (i.e. the player holds) this modifier.</param>
public readonly record struct OverlayBindingModifier(
    ushort BadgeGlyph0,
    ushort BadgeGlyph1,
    bool Held
);
/// <summary>One seat's binding-bar snapshot: every authored bank's resolved slots (one stacked cluster per bank,
/// each bank's own physical buttons resolved against ITS OWN named page — see <see cref="OverlayBindingSlot"/>) plus
/// the normalized frame region its bar is confined to (per-viewport scoping happens here, at the writer layer — the
/// render node stays dumb).</summary>
/// <param name="Viewport">The seat's viewport rect in normalized frame space (its <c>LayoutRegion</c>).</param>
/// <param name="PageId">The seat's currently active page id (diagnostics / transitions).</param>
/// <param name="Group">The seat's active page group (diagnostics / transitions).</param>
/// <param name="Label">The active page's display label — drawn beside the modifier pips so holding a trigger chord
/// NAMES the page it turned to; empty draws nothing.</param>
/// <param name="Slots">Every rendered slot, one entry per (bank, physical button) pair in the authored slot set —
/// bank-major, slot-set order within a bank.</param>
/// <param name="Modifiers">The declared modifiers, in profile order.</param>
/// <param name="Hints">The active group's command-chord hint lines (e.g. <c>"LT+RT Snapshot"</c>), pre-formatted
/// ASCII — rendered as small text above the modifier pips so a chord-fired act is discoverable.</param>
/// <param name="Layout">The authored layout resolved for this seat.</param>
/// <param name="Visible">Whether this seat's bar currently draws.</param>
public readonly record struct OverlayBindingSeat(
    NormalizedRect Viewport,
    string PageId,
    string Group,
    string Label,
    ReadOnlyMemory<OverlayBindingSlot> Slots,
    ReadOnlyMemory<OverlayBindingModifier> Modifiers,
    ReadOnlyMemory<string> Hints,
    BindingBarLayoutOptions Layout,
    bool Visible
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
