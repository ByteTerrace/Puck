using Puck.Hosting;
using Puck.Input.Devices;

namespace Puck.Demo.BindingBar;

/// <summary>One slot as the renderer consumes it — everything family- and binding-resolved on the CPU.</summary>
/// <param name="Glyph">The physical-button badge glyph (already family-resolved).</param>
/// <param name="Icon">The bound action's icon (<see cref="BindingIconId.None"/> = the slot is empty).</param>
/// <param name="Visible">Whether the slot draws at all (the no-modifier page hides unbound slots).</param>
/// <param name="Pressed">Whether the physical button is currently down — the chip's HELD tier-1 state (docs/ui-design-tokens.md section 5).</param>
/// <param name="Alpha">The slot opacity: 1.0 on the active bar, the passive fraction on secondary bars.</param>
/// <param name="Bound">Whether a real action is bound to this physical button. <see langword="false"/> is the
/// chip's DISABLED tier-0 state (a free/unbound button, still shown so the player sees the socket exists); every
/// publisher already computed this honestly (the low-alpha "free button" placeholder path), it just wasn't carried
/// past the icon before. Defaults <see langword="true"/> so existing bound-slot constructions need no edits.</param>
/// <param name="Accent">Whether this slot is the CONTEXT-PRIMARY action — the chip's ACCENT tier-1 state. Only the
/// overworld's dynamically-swapping <c>overworld.context</c> entry sets this today (the one place the demo already
/// tracks a single "the primary thing to do right now" action); other bars have no such concept yet and leave it
/// <see langword="false"/> (an honest absence, not a faked one). Defaults <see langword="false"/>.</param>
internal readonly record struct BindingSlotView(
    BindingGlyphId Glyph,
    BindingIconId Icon,
    bool Visible,
    bool Pressed,
    float Alpha,
    bool Bound = true,
    bool Accent = false
);

/// <summary>One declared modifier as the renderer consumes it (the trigger pips between the clusters).</summary>
/// <param name="Glyph">The modifier's badge glyph.</param>
/// <param name="Held">Whether the active page's chord requires (i.e. the player holds) this modifier.</param>
internal readonly record struct BindingModifierSlotView(
    BindingGlyphId Glyph,
    bool Held
);

/// <summary>The per-frame binding-bar snapshot the overlay renders.</summary>
/// <param name="Family">The active controller family (glyph theming).</param>
/// <param name="ActivePageId">The active page's id (diagnostics / transitions).</param>
/// <param name="BarCount">How many 12-slot bars <paramref name="Slots"/> carries.</param>
/// <param name="Slots">(<paramref name="BarCount"/> × 12) entries in layout-index order.</param>
/// <param name="Modifiers">The declared modifiers, in profile order.</param>
internal readonly record struct BindingBarFrame(
    GamepadType Family,
    string ActivePageId,
    int BarCount,
    ReadOnlyMemory<BindingSlotView> Slots,
    ReadOnlyMemory<BindingModifierSlotView> Modifiers
);

/// <summary>The read seam the overlay consumes; the binding-system adapter is the writer.</summary>
internal interface IBindingBarSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out BindingBarFrame frame);
}

/// <summary>
/// The binding-bar state store: the input/simulation side publishes an immutable frame, the render thread
/// snapshots it. A thin named wrapper over the shared <see cref="PublishBuffer{T}"/> (a whole-reference swap per
/// publish — no locks on the read path, no partially written frames) so DI registration and constructor parameters
/// still name a binding-bar-specific type.
/// </summary>
internal sealed class BindingBarStore : IBindingBarSource {
    private readonly PublishBuffer<BindingBarFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in BindingBarFrame frame) => m_buffer.Publish(frame: frame);

    /// <inheritdoc/>
    public bool TrySnapshot(out BindingBarFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}
