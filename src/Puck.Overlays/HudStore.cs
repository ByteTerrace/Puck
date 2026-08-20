using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>Which band an <see cref="OverlayHudPanel"/> draws in relative to the five first-party overlay writers —
/// the presentation-side twin of the document's <c>WorldHudLayer</c> (Puck.Overlays must not reference
/// Puck.World.Schema; the World-side feed maps the document token to this one).</summary>
public enum OverlayHudBand : byte {
    /// <summary>Draws before the base slot.</summary>
    Under,

    /// <summary>Draws after the base slot — always the topmost band.</summary>
    Over,

    /// <summary>Takes the base slot: while at least one live panel declares this band, every such panel draws in the
    /// base slot instead of the five first-party writers.</summary>
    Replace,
}
/// <summary>An <see cref="OverlayHudElement"/>'s rendered kind — the presentation-side twin of the document's
/// <c>WorldHudElementKind</c>.</summary>
public enum OverlayHudElementKind : byte {
    /// <summary>A filled rect.</summary>
    Rect,

    /// <summary>A fixed-cell text run — the authored literal, or (when bound) the live-resolved binding text.</summary>
    Text,

    /// <summary>A fill-bar readout of a bound binding's normalized 0..1 value.</summary>
    Gauge,
}
/// <summary>A normalized rect (origin top-left, Y down) in the same two coordinate spaces
/// <c>Puck.World.WorldHudRect</c> uses: a panel's rect is screen space, an element's rect is its owning panel's local
/// space.</summary>
/// <param name="X">Left, normalized.</param>
/// <param name="Y">Top, normalized.</param>
/// <param name="Width">Width, normalized.</param>
/// <param name="Height">Height, normalized.</param>
public readonly record struct OverlayHudRect(float X, float Y, float Width, float Height);
/// <summary>One run of an <see cref="OverlayHudElement.Template"/> — either a literal run appended verbatim, or a
/// placeholder naming one closed-vocabulary binding token whose live value replaces it. Runs arrive already parsed
/// from the host's HUD feed (<c>Puck.World.Schema</c>'s <c>HudTemplate</c> owns the brace/escape grammar and is the
/// only thing that speaks it), so nothing on the presentation side ever parses a template string.</summary>
/// <param name="IsPlaceholder"><see langword="true"/> for a placeholder run; <see langword="false"/> for literal
/// text.</param>
/// <param name="Text">The literal text, or the placeholder's binding token with its brace delimiters already
/// stripped.</param>
public readonly record struct OverlayHudTemplateSegment(bool IsPlaceholder, string Text);
/// <summary>One HUD element the writer resolves and draws — the presentation-side twin of
/// <c>Puck.World.WorldHudElement</c> (document authoring/validation stays in Puck.World.Schema; this struct carries
/// only what the writer needs to render).</summary>
/// <param name="Kind">The element's rendered kind.</param>
/// <param name="Rect">The element's rect, in its owning panel's local space.</param>
/// <param name="Role">The element's color role.</param>
/// <param name="Text">The authored literal (meaningful for <see cref="OverlayHudElementKind.Text"/> when neither
/// <paramref name="Binding"/> nor <paramref name="Template"/> is set).</param>
/// <param name="Binding">The closed-vocabulary binding token, or <see langword="null"/> for an unbound element.</param>
/// <param name="Template">The parsed runs of a template — the presentation-side twin of
/// <c>Puck.World.WorldHudElement.Template</c>, meaningful only for <see cref="OverlayHudElementKind.Text"/>. Empty
/// for an untemplated element. Takes priority over <paramref name="Binding"/> when both are somehow present (the
/// document validator refuses that combination before it ever reaches a live document, so this is a defensive
/// ordering, never the primary rule).</param>
public readonly record struct OverlayHudElement(
    OverlayHudElementKind Kind,
    OverlayHudRect Rect,
    OverlayColorRole Role,
    string? Text,
    string? Binding,
    ReadOnlyMemory<OverlayHudTemplateSegment> Template = default
);
/// <summary>One HUD panel the writer resolves and draws — the presentation-side twin of
/// <c>Puck.World.WorldHudPanel</c>.</summary>
/// <param name="Id">The panel's stable id (diagnostic only on this side — the document owns identity).</param>
/// <param name="Rect">The panel's viewport rect, in screen space.</param>
/// <param name="Band">Which band the panel draws in.</param>
/// <param name="Style">The panel's chrome recipe.</param>
/// <param name="Elements">The panel's child elements, in authored order.</param>
public readonly record struct OverlayHudPanel(
    string Id,
    OverlayHudRect Rect,
    OverlayHudBand Band,
    OverlayPanelStyle Style,
    ReadOnlyMemory<OverlayHudElement> Elements
);
/// <summary>One player-scope HUD panel: a profile's private single panel plus the local seat viewport it is confined
/// to (screen-normalized, the same convention <c>Puck.Abstractions.Presentation.NormalizedRect</c> uses for a seat's
/// split-screen region) — the presentation-side twin of <c>Puck.World.WorldIdentity.Hud</c>. Only seats that
/// are both joined and have authored a panel appear in <see cref="OverlayHudFrame.SeatPanels"/>; there is no entry
/// for an empty seat or an unauthored identity.</summary>
/// <param name="Viewport">The owning seat's viewport rect, screen-normalized.</param>
/// <param name="Panel">The profile's authored panel (its own <see cref="OverlayHudPanel.Rect"/> is normalized to
/// this viewport's local space, not the whole screen — the same local-space convention an element's rect uses
/// against its owning panel).</param>
public readonly record struct OverlayHudSeatPanel(
    OverlayHudRect Viewport,
    OverlayHudPanel Panel
);
/// <summary>The per-revision HUD structure snapshot <see cref="HudWriter"/> renders — <see cref="Panels"/> reconciled
/// from the delivered world definition only when its revision moves (the <c>WorldFrameSource</c> revision-reconcile
/// pattern); <see cref="SeatPanels"/> recomposed every produced frame (it depends on the roster and per-profile
/// state, which the definition revision does not cover) but from a preallocated per-seat array, so the rebuild is
/// zero-allocation and cheap even though it is unconditional. Live binding values are resolved separately, per
/// frame, by the writer via <see cref="IHudBindingResolver"/>.</summary>
/// <param name="Panels">The authored world-scope panels, in document order.</param>
/// <param name="SeatPanels">The authored player-scope (per-seat) panels — at most one per local seat.</param>
public readonly record struct OverlayHudFrame(
    ReadOnlyMemory<OverlayHudPanel> Panels,
    ReadOnlyMemory<OverlayHudSeatPanel> SeatPanels = default
);
/// <summary>The read seam <see cref="HudWriter"/> consumes; the host's HUD feed is the writer.</summary>
public interface IHudSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out OverlayHudFrame frame);
}
/// <summary>Resolves a closed-vocabulary HUD binding token to its live value, once per produced frame — the seam that
/// keeps binding resolution out of Puck.Overlays (which cannot know what <c>world.tick</c> or a seat position means)
/// while keeping the actual per-frame resolve inside the writer (presentation float; no document round-trip).</summary>
public interface IHudBindingResolver {
    /// <summary>Resolves a binding token.</summary>
    /// <param name="binding">The token (e.g. <c>world.tick</c>).</param>
    /// <param name="fraction">The value normalized to 0..1 (a gauge's fill), when resolved.</param>
    /// <param name="text">The value's formatted text form (a text element's display), when resolved.</param>
    /// <returns><see langword="true"/> when the token resolved (always true for a document-validated token; a
    /// resolver still reports honestly rather than assuming).</returns>
    bool TryResolve(string binding, out float fraction, out string text);
}
/// <summary>
/// The HUD structure store. A thin named wrapper over the shared <see cref="PublishBuffer{T}"/>, published only when
/// the delivered world definition's HUD section actually changes (structure — panels/elements/rects/bindings), not
/// every frame; live binding values are resolved separately by <see cref="HudWriter"/> every produced frame. Same
/// threading contract as every other overlay store.
/// </summary>
public sealed class HudStore : IHudSource {
    private readonly PublishBuffer<OverlayHudFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in OverlayHudFrame frame) => m_buffer.Publish(frame: frame);
    /// <inheritdoc/>
    public bool TrySnapshot(out OverlayHudFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}
